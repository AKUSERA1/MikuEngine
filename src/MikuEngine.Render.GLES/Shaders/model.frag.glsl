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

layout(location = 0) in vec3  vNormal;
layout(location = 1) in vec4  vColor;
layout(location = 2) in vec2  vUv;
layout(location = 3) in vec2  vUvSphere;
layout(location = 4) in float vToonV;

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

    // Toon 查表（PE L554）—— U 用主 UV 的 x（toon 图在 U 方向恒定），V 用 ToonCf
    if (uEnableToon > 0.5)
        col *= texture(uToonTex, vec2(vUv.x, vToonV));

    // Straight alpha，blend 由固定管线 SRC_ALPHA/INV_SRC_ALPHA 完成
    fragColor = col;
}
