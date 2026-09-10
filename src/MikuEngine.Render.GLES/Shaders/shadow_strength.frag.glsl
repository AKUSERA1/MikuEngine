#version 310 es
// 影强度图：16 点采样 Z 图，输出 0(受光)~1(全影)。PE PS1_Shadow 的ぼかしあり分支
precision highp float;
layout(location = 0) in vec2 vUv;
layout(location = 1) in vec4 vLightPos;
uniform float uEnableTexture;
uniform sampler2D uDiffuseTex;
uniform sampler2D uShadowZMap;     // 光照深度图
uniform float uShadowTexel;        // 1/影子图边长，用于 SwOffset 缩放
out vec4 fragColor;

// PE 的 float2 SwOffset[16] 在 fxd 里只声明不初始化（值由 C# 注入，反编译未取到）。
// 这里用 4x4 交错盘代替，半径约 1.6 texel。
const vec2 SW[16] = vec2[16](
    vec2(-1.5, -0.5), vec2(-0.5, -1.5), vec2( 0.5, -1.5), vec2( 1.5, -0.5),
    vec2( 1.5,  0.5), vec2( 0.5,  1.5), vec2(-0.5,  1.5), vec2(-1.5,  0.5),
    vec2(-0.5, -0.5), vec2( 0.5, -0.5), vec2(-0.5,  0.5), vec2( 0.5,  0.5),
    vec2(-2.5, -0.5), vec2(-0.5, -2.5), vec2( 0.5,  2.5), vec2( 2.5,  0.5)
);

const float g_shadowMargin = 0.003;   // PE L59

void main()
{
    if (uEnableTexture > 0.5 && texture(uDiffuseTex, vUv).a <= 0.0) discard;

    vec2 uv = vLightPos.xy / vLightPos.w * vec2(0.5, 0.5) + 0.5;
    //   深度区间必须一致：Z 图存的是 gl_FragCoord.z ∈ [0,1]，
    //    而 vLightPos.z/w 是 z_ndc ∈ [-1,1]。不换算的话 z 几乎恒为负，
    //    `s >= z` 恒成立 → 全部判为受光 → 自阴影完全失效（实测踩坑）。
    float z = vLightPos.z / vLightPos.w * 0.5 + 0.5 - g_shadowMargin;

    float sd = 0.0;
    for (int i = 0; i < 16; i++)
    {
        float s = texture(uShadowZMap, uv + SW[i] * uShadowTexel).r;
        sd += (s >= z) ? 0.0 : (1.0 / 16.0);
    }
    fragColor = vec4(sd, sd, sd, 1.0);
}
