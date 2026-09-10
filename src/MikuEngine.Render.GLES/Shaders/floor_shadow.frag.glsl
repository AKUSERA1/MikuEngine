#version 310 es
// 床影（PE PS1_FloorShadow）：col = LightColor*0.5，alpha = floorStrength * 影判定
precision highp float;
layout(location = 0) in vec4 vLightPos;
uniform sampler2D uShadowZMap;
uniform vec4 uLightColor;
uniform float uFloorStrength;   // PE g_floorStrength = 0.7
out vec4 fragColor;
const float g_shadowMargin = 0.003;
void main()
{
    vec2 uv2 = vLightPos.xy / vLightPos.w * vec2(0.5, 0.5) + 0.5;
    float z0 = texture(uShadowZMap, uv2).r;
    float z  = vLightPos.z / vLightPos.w * 0.5 + 0.5 - g_shadowMargin;   // z_ndc → [0,1]

    vec4 col = vec4(uLightColor.rgb * 0.5, 0.0);
    if (z0 < 1.0)
    {
        // ぼかしあり：周囲 4 点（SwOffset 5/6/9/10）
        vec2 t = vec2(1.0 / 1024.0);
        float sd = 0.0;
        sd += (texture(uShadowZMap, uv2 + vec2(-0.5,  1.5) * t).r >= z) ? 0.0 : 0.25;
        sd += (texture(uShadowZMap, uv2 + vec2( 0.5,  1.5) * t).r >= z) ? 0.0 : 0.25;
        sd += (texture(uShadowZMap, uv2 + vec2(-0.5,  0.5) * t).r >= z) ? 0.0 : 0.25;
        sd += (texture(uShadowZMap, uv2 + vec2( 0.5,  0.5) * t).r >= z) ? 0.0 : 0.25;
        col.a = uFloorStrength * sd;
    }
    if (col.a <= 0.0) discard;   // PE L873-876
    fragColor = col;
}
