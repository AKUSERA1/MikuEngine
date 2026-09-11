#version 310 es

// MikuEngine GLES 3.1 —— PMX 模型主渲染 片元着色器（Phase 0 / 0.5）
//
// 处理顺序严格复刻 PmxEditor PS1（fxd_decoded.txt）：
//   L508  saturate(p_in.Col)
//   L511  col *= tex
//   L517/522/528  Sphere（Mul / Add / SubTex）
//   L531-534  if (col.a <= 0) clip(-1)
//   L554  Toon：tex2D(ToonSampler, float2(UV.x, ToonCf(Normal)))
//
// 修订：自阴影为【内联 PCF】直接采样光照深度图（不再有屏幕空间 mask），
// 并按 uShadowStyle 走三路注入（见 docs/2026-09-10-shadow-provider-design.md §0b.3）：
//   1 = 标准本影（PE 16-tap，ToonMode 二选一 / 压 YCrCb）
//   2 = 硬边本影（连续场高斯模糊 + smoothstep 阈值提取 + 影色；硬核心 + 平滑边界）
//   3 = 普通阴影（PCF 软影 + 直接遮蔽乘子）
// 三者消费同一张 Z 图（DEPTH_COMPONENT24 比较采样器）、同一组全局参数（强度 / 影色 / 偏置）。
// 方案 B：uShadowZMap 是开了 LINEAR + COMPARE_REF_TO_TEXTURE 的深度纹理，
// 每次 texture() 硬件先对 2×2 texel 各做二值深度比较、再双线性插值结果 ——
// 阈值提取正是靠这个连续场做模糊 + 阈值硬化，最重的采样成本也只有 9 tap。

precision highp float;
precision highp sampler2DShadow;

uniform sampler2D uDiffuseTex;
uniform sampler2D uSphereTex;
uniform sampler2D uToonTex;

// 材质 morph 的色调系数（Multiplication 结果；无 morph 时恒为 (1,1,1,1)）。
// 由 Core 的 MmdMorphEvaluator.ResolveMaterial 逐段算好，这里只做乘法。
uniform vec4 uTextureCoeff;
uniform vec4 uSphereCoeff;
uniform vec4 uToonCoeff;

// 开关/模式类 uniform 一律用 float（C# 侧走 glUniform1f）。
// 之前声明成 int 却用 glUniform1f 上传 → GL_INVALID_OPERATION → 恒为 0 → 纹理不采样。
uniform float uEnableTexture;
uniform float uEnableSphere;
uniform float uEnableToon;
uniform float uSphereMode;   // 1 = Multiply, 2 = Add, 3 = SubTexture
uniform float uToonMode;     // 0 = None, 1 = Type1(MMD), 2 = Type2(固有) —— PE L47
uniform float uEnableSelfShadow;  // 0 关 / >0 开（阶段 1 收影侧旗标按材质决定此值）
uniform sampler2DShadow uShadowZMap;  // 光照深度图（unit 3，比较采样器 —— 方案 B）
uniform float uShadowTexel;       // 1/影子图边长，PCF 核缩放（单一来源，阶段 0）
uniform float uSelfShadowStrength;// 影强度（全局，三模式共享；0 = 全受光，隔离测试用）
uniform float uShadowStyle;       // 1=标准本影 2=硬边本影（阈值提取）3=普通阴影
uniform vec4  uShadowColor;       // 硬边本影的影色 —— 乘性暗度，非替换色
uniform float uShadowBias;        // 深度比较偏置的【常数底】（阶段 7；默认 0.0005）
uniform float uShadowSlopeBias;   // 斜率缩放系数（阶段 7）：乘在「一个 texel 内自身深度变化」上
uniform float uShadowBiasMax;     // 偏置上限（阶段 7；默认 0.003 = 旧的全屏常数，掠射端封顶用）
uniform float uShadowSoftness;    // 硬边本影的核宽 / 普通阴影的 PCF 核宽（>1 更软）
uniform float uShadowEdge;        // 硬边本影的阈值带宽 w（越小越硬，0.3~0.5 为硬边观感）

layout(location = 0) in vec3  vNormal;
layout(location = 1) in vec4  vColor;
layout(location = 2) in vec2  vUv;
layout(location = 3) in vec2  vUvSphere;
layout(location = 4) in float vToonV;
layout(location = 6) in vec4  vLightPos;   // 光源裁剪坐标（内联 PCF 用）

out vec4 fragColor;

// PE 的 SwOffset[16] 用 4×4 交错盘近似（半径约 1.6 texel）
const vec2 SW[16] = vec2[16](
    vec2(-1.5, -0.5), vec2(-0.5, -1.5), vec2( 0.5, -1.5), vec2( 1.5, -0.5),
    vec2( 1.5,  0.5), vec2( 0.5,  1.5), vec2(-0.5,  1.5), vec2(-1.5,  0.5),
    vec2(-0.5, -0.5), vec2( 0.5, -0.5), vec2(-0.5,  0.5), vec2( 0.5,  0.5),
    vec2(-2.5, -0.5), vec2(-0.5, -2.5), vec2( 0.5,  2.5), vec2( 2.5,  0.5)
);

// 硬边本影（style 2）用的 3×3 高斯核（σ≈1）：[1 2 1; 2 4 2; 1 2 1] / 16，行主序
const float GW[9] = float[9](
    0.0625, 0.125, 0.0625,
    0.125,  0.25,  0.125,
    0.0625, 0.125, 0.0625
);

void main()
{
    int sphereMode = int(uSphereMode + 0.5);

    vec4 col = clamp(vColor, 0.0, 1.0);

    // 材质 morph 的三个色调系数（默认恒等 (1,1,1,1)，权重为 0 时逐像素等价于无 morph）。
    // PMX 的 MaterialMorph 对每个纹理色调都可做 Multiply / Add，Core 侧 MmdMorphEvaluator
    // 已把「基础值 + 全部活跃材质 morph」混合好，这里只做一次乘法。
    if (uEnableTexture > 0.5)
        col *= texture(uDiffuseTex, vUv) * uTextureCoeff;

    if (uEnableSphere > 0.5)
    {
        if (sphereMode == 1)
            col *= texture(uSphereTex, vUvSphere) * uSphereCoeff;
        else if (sphereMode == 2)
            col.rgb += texture(uSphereTex, vUvSphere).rgb * uSphereCoeff.rgb;
        else if (sphereMode == 3)
            col *= texture(uSphereTex, vUv) * uSphereCoeff;   // SubTex：PE 用 UVA1，v1 退化成主 UV
    }

    // ── Alpha ─────────────────────────────────────────
    // PE 的 PS 里唯一的丢弃就是下面这句，阈值是 0，不是 0.5。
    if (col.a <= 0.0) discard;

    // ── Toon / 自阴影 ────────────────────────────────────
    // PE 的结构是「二选一」：
    //   if (EnableSelfShadow) { ...影合成... }   ← 此时【不做】 col *= toonCol
    //   else                  { col *= toonCol; }
    int toonMode = int(uToonMode + 0.5);
    vec4 toonCol = vec4(1.0);
    if (uEnableToon > 0.5)
    {
        if (toonMode == 1)
            toonCol = texture(uToonTex, vec2(vUv.x, vToonV)) * uToonCoeff;            // PE L554
        else if (toonMode == 2)
            toonCol = texture(uToonTex, vec2(vUv.x, vToonV * vToonV)) * uToonCoeff;   // PE L560: p*p
    }

    if (uEnableSelfShadow > 0.5)
    {
        int style = int(uShadowStyle + 0.5);

        // 深度区间必须一致：Z 图存 gl_FragCoord.z ∈ [0,1]，而 vLightPos.z/w 是 z_ndc ∈ [-1,1]。
        vec2  suv = vLightPos.xy / vLightPos.w * 0.5 + 0.5;
        float lz  = vLightPos.z / vLightPos.w * 0.5 + 0.5;

        // ── 斜率缩放偏置 ───────────────────────────────────────────
        // 原做法是全屏统一的常数偏置（0.003 ≈ 掠射到 82° 才需要的余量）。它不区分面的朝向，
        // 于是「自身几乎正对光、遮挡余量又很小」的区域（典型：背光侧眉）被整片推过遮挡物 ⇒
        // 影消失；迎光侧眉余量大 ⇒ 影留下 —— 形成左右不对称（实测 0.003 时 11.5% vs 2.6%，
        // 0.016 时 2.4% vs 0%）。
        // 这里改成按【面的朝向】给偏置：掠射面在一个影图 texel 内的自身深度变化大 ⇒ 需要更大
        // 余量才压得住 acne；正对面几乎不需要。
        //   · 用「d(lz)/d(屏幕像素) ÷ d(uv)/d(屏幕像素)」估计 ∂lz/∂uv 的方向导数 —— 平面三角形
        //     上两者严格成比例，比值即该面的深度梯度，天然有界（对比解 2×2 雅可比要除以
        //     det，退化三角形会让它爆掉）。
        //   · 再乘一个 texel 的 uv 步长，得到「每 texel 的深度变化」。
        //   · 上限锁在旧的全屏常数 uShadowBiasMax：掠射端不比原行为更差，陡面 acne 不受影响。
        vec2  dUvX = dFdx(suv), dUvY = dFdy(suv);
        float gx   = abs(dFdx(lz)) / max(length(dUvX), 1e-8);
        float gy   = abs(dFdy(lz)) / max(length(dUvY), 1e-8);
        float slope = max(gx, gy) * uShadowTexel;       // 每 texel 的自身深度变化（归一化深度）
        float bias  = min(uShadowBias + uShadowSlopeBias * slope, uShadowBiasMax);
        float z     = lz - bias;

        // 视锥外淡出：紧视锥下模型边缘 ndc≤0.85，淡出带 0.88→0.96 在模型外。
        // clamp 边缘的采样对的是"无关深度"会误读成影（reze 注释原话 visible band at the frustum
        // edge），乘上这个因子淡到受光 —— 无分支、边缘不跳变，PCF 核也永不跨界采样。
        vec2 lndc = vLightPos.xy / vLightPos.w;
        float fade = (1.0 - smoothstep(0.88, 0.96, abs(lndc.x)))
                   * (1.0 - smoothstep(0.88, 0.96, abs(lndc.y)));

        // cf = 亮度(LightColor) * g_selfStrength(0.6)；LightColor 固定 0.5 灰
        float cf = dot(vec3(0.299, 0.587, 0.114), vec3(0.5)) * 0.6;

        if (style == 2)
        {
            // ── 硬边本影：连续场 → 高斯模糊 → smoothstep 阈值硬化 ──
            // 顺序是关键：先对【连续】lit 场做模糊，再用阈值把平坦区压回纯 0/1。
            //   · 模糊的是连续场而非二值结果 ⇒ 亚 texel 信息不丢、0.5 等值线位置一阶保持
            //     （边缘不随曲率漂移；对比"先二值化再平均"）
            //   · |d| > w 的区域被阈值重新压成纯 0/1 ⇒ 影内仍是平坦的硬核心
            //   · 软硬解耦：uShadowSoftness 定半影宽度，uShadowEdge 定边界陡峭度
            //   · 用 smoothstep（单调非线性）而非 unsharp 锐化 ⇒ 不会过冲出亮边晕
            float lit = 0.0;
            for (int iy = -1; iy <= 1; iy++)
            for (int ix = -1; ix <= 1; ix++)
            {
                vec2 off = vec2(float(ix), float(iy)) * uShadowTexel * uShadowSoftness;
                lit += texture(uShadowZMap, vec3(suv + off, z)) * GW[iy * 3 + ix + 4];
            }

            float d = 2.0 * lit - 1.0;                     // >0 受光 / <0 在影（有符号场）
            float shadowAmt = (1.0 - smoothstep(-uShadowEdge, uShadowEdge, d))
                            * uSelfShadowStrength * fade;
            //    必须【乘】而非替换：uShadowColor 的语义是「乘性暗度」（1=不变，0.5=压暗一半），
            //    不是替换色。用 mix(col, color, shadowAmt) 替换会在影内把贴图/球面/toon
            //    整体抹成一块单色（曾实测"阴影完全覆盖材质"）。
            col.rgb *= mix(vec3(1.0), uShadowColor.rgb, shadowAmt);
        }
        else if (style == 3)
        {
            // ── 普通阴影：9-tap @2texel + IGN 旋转（成本技巧 + 抗 banding）──
            // 3×3 步长 2 texel：配合硬件比较采样器的 2×2 滤波，覆盖 ≡ 5×5 @1texel（±2 texel半影），省 7 个 tap。
            // IGN 逐像素旋转核把固定盘的残余梯度条带打散；gl_FragCoord 量化 ⇒ 静止相机 = 静止影。
            float ign = fract(52.9829189 * fract(dot(gl_FragCoord.xy, vec2(0.06711056, 0.00583715))));
            float ang = ign * 6.2831853;
            mat2 rot = mat2(cos(ang), -sin(ang), sin(ang), cos(ang));

            float cc = 0.0;
            for (int iy = -1; iy <= 1; iy++)
            for (int ix = -1; ix <= 1; ix++)
            {
                vec2 off = rot * vec2(float(ix), float(iy)) * 2.0 * uShadowTexel * uShadowSoftness;
                float lit = texture(uShadowZMap, vec3(suv + off, z));
                cc += (1.0 - lit) / 9.0;
            }
            cc *= uSelfShadowStrength * fade;
            col.rgb *= (1.0 - cf * cc);
        }
        else
        {
            // ── 标准本影：16-tap PCF + PE 二选一注入（PE 保真路，不动核）──
            float cc = 0.0;
            for (int i = 0; i < 16; i++)
            {
                float lit = texture(uShadowZMap, vec3(suv + SW[i] * uShadowTexel, z));
                cc += (1.0 - lit) / 16.0;
            }
            cc *= uSelfShadowStrength * fade;

            if (toonMode == 0)
            {
                // PE L569-575：只降亮度（YCrCb 的 Y），等价于 RGB 同比例缩放
                float y0 = dot(col.rgb, vec3(0.299, 0.587, 0.114));
                float k  = (1.0 - cf * cc);
                col.rgb *= (y0 > 1e-5) ? (y0 * k / y0) : 0.0;
            }
            else
            {
                // PE L578-585：与 toon(0,1) 角点色合成，cf2 有 4 倍固定增益
                vec4 toon = texture(uToonTex, vec2(0.0, 1.0)) * uToonCoeff;
                vec4 shadowCol = col * toon;
                float cf2 = cf * cc * 4.0;
                col = col * (1.0 - cf2) + shadowCol * cf2;
            }
        }
    }
    else
    {
        col *= toonCol; // PE L588-590
    }

    // Straight alpha，blend 由固定管线 SRC_ALPHA/INV_SRC_ALPHA 完成
    fragColor = col;
}
