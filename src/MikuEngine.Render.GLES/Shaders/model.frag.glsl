#version 310 es

// MikuEngine GLES 3.1 —— PMX 模型主渲染 片元着色器（Phase 0 / 0.5）
//
// 处理顺序严格照抄 PmxEditor PS1（fxd_decoded.txt）：
//   L508  saturate(p_in.Col)
//   L511  col *= tex
//   L517/522/528  Sphere（Mul / Add / SubTex）
//   L531-534  if (col.a <= 0) clip(-1)
//   L554  Toon：tex2D(ToonSampler, float2(UV.x, ToonCf(Normal)))
//
// v1 未实现 Material Morph，因此 offsetTex0/offsetTex1 的 mul/add 恒为默认值，
// 等价于恒等变换（offsetTex1 返回 float4(rgb, col.a)，alpha 透传），此处直接省去。

precision highp float;

uniform sampler2D uDiffuseTex;
uniform sampler2D uSphereTex;
uniform sampler2D uToonTex;

// ⚠️ 开关/模式类 uniform 一律用 float（C# 侧走 glUniform1f）。
//    之前声明成 int 却用 glUniform1f 上传 → GL_INVALID_OPERATION → 恒为 0 → 纹理不采样。
uniform float uEnableTexture;
uniform float uEnableSphere;
uniform float uEnableToon;
uniform float uSphereMode;   // 1 = Multiply, 2 = Add, 3 = SubTexture
uniform float uToonMode;     // 0 = None, 1 = Type1(MMD), 2 = Type2(固有) —— PE L47
uniform float uEnableSelfShadow;  // 0 关 / 1 自阴影 / 2 自阴影+床影
uniform sampler2D uShadowMap;     // 影强度图（屏幕空间）

layout(location = 0) in vec3  vNormal;
layout(location = 1) in vec4  vColor;
layout(location = 2) in vec2  vUv;
layout(location = 3) in vec2  vUvSphere;
layout(location = 4) in float vToonV;
layout(location = 6) in vec4  vCameraClip;

out vec4 fragColor;

void main()
{
    int sphereMode = int(uSphereMode + 0.5);

    vec4 col = clamp(vColor, 0.0, 1.0);

    if (uEnableTexture > 0.5)
        col *= texture(uDiffuseTex, vUv);

    if (uEnableSphere > 0.5)
    {
        if (sphereMode == 1)
            col *= texture(uSphereTex, vUvSphere);
        else if (sphereMode == 2)
            col.rgb += texture(uSphereTex, vUvSphere).rgb;
        else if (sphereMode == 3)
            col *= texture(uSphereTex, vUv);   // SubTex：PE 用 UVA1，v1 退化成主 UV
    }

    // ── Alpha（对齐 PmxEditor，2026-09-10 修订）─────────────────────────
    // PE 的 fxd 只有一个 model technique、单一 pass：
    //   AlphaBlendEnable = True; SrcBlend = SRCALPHA; DestBlend = INVSRCALPHA
    // 且**完全没有** AlphaTestEnable / AlphaFunc / AlphaRef / ZWriteEnable。
    // PS 里唯一的丢弃就是下面这句 —— 阈值是 0，不是 0.5。
    //
    // 之前的 `col.a < 0.5 discard`（Cutout 队列）是自创行为，PE/MMD 没有：
    // 它会把 0.3~0.5 的连续 alpha 像素整个砍掉、把柔和边缘切成硬边，
    // 破坏 MMD 的连续渐变透明（如本模型的「袖透」Diffuse.a=0.8）。
    if (col.a <= 0.0) discard;

    // ── Toon / 自阴影（PE L546-590）────────────────────────────────────
    // PE 的结构是「二选一」：
    //   if (EnableSelfShadow) { ...影合成... }   ← 此时【不做】 col *= toonCol
    //   else                  { col *= toonCol; }
    // 这个分支关系必须照抄，否则开自阴影后亮度会对不上。
    int toonMode = int(uToonMode + 0.5);
    vec4 toonCol = vec4(1.0);
    if (uEnableToon > 0.5)
    {
        if (toonMode == 1)
            toonCol = texture(uToonTex, vec2(vUv.x, vToonV));            // PE L554
        else if (toonMode == 2)
            toonCol = texture(uToonTex, vec2(vUv.x, vToonV * vToonV));   // PE L560: p*p
    }

    if (uEnableSelfShadow > 0.5)
    {
        // 影强度图是【屏幕空间】的（见 shadow.vert.glsl uCameraSpace）：
        // 按片元自己的屏幕坐标取回 —— 等价于 PE PS1 L565 的
        //   uv = getShadowTexPos(p_in.Depth)   // 主渲染里 Depth = 相机裁剪坐标
        //
        // ⚠️ 这里【不能】用光源裁剪坐标算 uv：那是光源屏幕坐标，取回的是"别人的"判定。
        //    （历史上就是这么写的：suv = vLightPos.xy/w * vec2(0.5,-0.5) + 0.5 —— 两个错叠在一起。）
        // ⚠️ 也【不能】照抄 PE 的 float2(0.5,-0.5) 做 Y 翻转：那个负号是 D3D9 的
        //    "渲染目标 v=0 在图像上方"约定；GL 的 FBO 纹理 v=0 就在下方，
        //    写入时 v = ndc.y*0.5+0.5、读取也必须用同一个映射，翻转会上下镜像。
        vec2 suv = vCameraClip.xy / vCameraClip.w * 0.5 + 0.5;
        float cc = texture(uShadowMap, suv).r;                        // 0 受光 ~ 1 全影
        // cf = 亮度(LightColor) * g_selfStrength(0.6)
        float cf = dot(vec3(0.299, 0.587, 0.114), vec3(0.5)) * 0.6;   // LightColor 固定 0.5 灰

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
            vec4 toon = texture(uToonTex, vec2(0.0, 1.0));
            vec4 shadowCol = col * toon;
            float cf2 = cf * cc * 4.0;
            col = col * (1.0 - cf2) + shadowCol * cf2;
        }
    }
    else
    {
        col *= toonCol;                                              // PE L588-590
    }

    // Straight alpha，blend 由固定管线 SRC_ALPHA/INV_SRC_ALPHA 完成
    fragColor = col;
}
