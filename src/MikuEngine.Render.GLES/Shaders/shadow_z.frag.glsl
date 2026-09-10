#version 310 es
// 光照深度图：输出 gl_FragCoord.z（= Depth.z / Depth.w，PE PS1 的比较对象）
precision highp float;
layout(location = 0) in vec2 vUv;
layout(location = 1) in vec4 vLightPos;
uniform float uEnableTexture;
uniform sampler2D uDiffuseTex;
out vec4 fragColor;
void main()
{
    // PE PS1_Shadow 开头的贴图 alpha 剔除（透明贴图不投影）
    if (uEnableTexture > 0.5 && texture(uDiffuseTex, vUv).a <= 0.0) discard;
    fragColor = vec4(gl_FragCoord.z, 0.0, 0.0, 1.0);
}
