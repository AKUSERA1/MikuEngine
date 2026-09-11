#version 310 es
// 床影（PE PS1_FloorShadow）：col = LightColor*0.5，alpha = floorStrength * 影判定
// uShadowZMap 是 DEPTH_COMPONENT24 比较采样器（硬件 2×2 双线性比较），
// texture() 直接返回 lit 分数（1 = 受光），不再手工读原始深度。
precision highp float;
precision highp sampler2DShadow;
layout(location = 0) in vec4 vLightPos;
uniform sampler2DShadow uShadowZMap;
uniform vec4 uLightColor;
uniform float uFloorStrength;   // PE g_floorStrength = 0.7
uniform float uShadowTexel;     // 1/影子图边长（阶段 0：单一来源，不再硬编码 1/1024）
out vec4 fragColor;
const float g_shadowMargin = 0.003;
void main()
{
    vec2 uv2 = vLightPos.xy / vLightPos.w * vec2(0.5, 0.5) + 0.5;
    float z  = vLightPos.z / vLightPos.w * 0.5 + 0.5 - g_shadowMargin;   // z_ndc → [0,1]

    // 视锥外淡出：600×600 床影面片大半落在光照视锥外，clamp 边缘的
    // 无关深度会误读成影；淡出带 0.88→0.96 无分支地把它降到 0。
    vec2 lndc = vLightPos.xy / vLightPos.w;
    float fade = (1.0 - smoothstep(0.88, 0.96, abs(lndc.x)))
               * (1.0 - smoothstep(0.88, 0.96, abs(lndc.y)));

    vec4 col = vec4(uLightColor.rgb * 0.5, 0.0);

    // 占位判定（等价旧版 z0 < 1.0 的"此处有几何投影"）：
    // ref=0.999 时比较结果 1 = 背景（Clear 空值 z=1），0 = 有真实深度。背景不画床影。
    float bg = texture(uShadowZMap, vec3(uv2, 0.999));
    if (bg < 0.5)
    {
        // ぼかしあり：周囲 4 点（SwOffset 5/6/9/10）
        vec2 t = vec2(uShadowTexel);
        float sd = 0.0;
        sd += (1.0 - texture(uShadowZMap, vec3(uv2 + vec2(-0.5,  1.5) * t, z))) * 0.25;
        sd += (1.0 - texture(uShadowZMap, vec3(uv2 + vec2( 0.5,  1.5) * t, z))) * 0.25;
        sd += (1.0 - texture(uShadowZMap, vec3(uv2 + vec2(-0.5,  0.5) * t, z))) * 0.25;
        sd += (1.0 - texture(uShadowZMap, vec3(uv2 + vec2( 0.5,  0.5) * t, z))) * 0.25;
        col.a = uFloorStrength * sd * fade;
    }
    if (col.a <= 0.0) discard;   // PE L873-876
    fragColor = col;
}
