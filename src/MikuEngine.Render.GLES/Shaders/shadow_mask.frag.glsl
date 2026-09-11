#version 310 es
// 影 mask pass 片元着色器，连续 lit 场：
// 不做二值化 —— 直接输出硬件 2×2 深度比较的连续 lit 值。
// lit ≈ texel 尺度的带符号距离场（边缘处跨 1 texel 的线性斜坡，两侧饱和），
// 模糊这个连续场再按 0.5 阈值提取 = 水平集平滑：
//   · 比较翻转噪声（平缓受光角的 ±浅波动）被周围主导符号淹没 —— 头发凹槽的主要来源
//   · 零交叉位置一阶保持（不像二值 mask 模糊那样随曲率偏移边缘）
// 对比旧的二值 mask：二值化在模糊前发生 = 亚 texel 信息已丢；这里保留它。
precision highp float;
precision highp sampler2DShadow;

layout(location = 0) in vec4 vLightPos;

uniform sampler2DShadow uShadowZMap;
uniform float uShadowBias;
uniform float uLitOverride;   // 1 = 本段不接收自阴影（PMX bit3=0），输出恒 1

out vec4 fragColor;

void main()
{
    if (uLitOverride > 0.5)
    {
        fragColor = vec4(1.0, 0.0, 0.0, 1.0);
        return;
    }

    // 深度区间与 model.frag 一致：Z 图存 gl_FragCoord.z ∈ [0,1]
    vec2 suv = vLightPos.xy / vLightPos.w * 0.5 + 0.5;
    float z  = vLightPos.z / vLightPos.w * 0.5 + 0.5 - uShadowBias;

    // 视锥外淡出：烘进 mask（fade→1 即变受光），模糊就不会把影漏到视锥外
    vec2 lndc = vLightPos.xy / vLightPos.w;
    float fade = (1.0 - smoothstep(0.88, 0.96, abs(lndc.x)))
               * (1.0 - smoothstep(0.88, 0.96, abs(lndc.y)));

    float lit = texture(uShadowZMap, vec3(suv, z));
    lit = mix(1.0, lit, fade);            // fade=0 ⇒ 受光（视锥外无影信息）

    fragColor = vec4(lit, 0.0, 0.0, 1.0);
}
