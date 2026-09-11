#version 310 es
// 光照深度图（硬件比较采样器）：深度直接写入 DEPTH_COMPONENT24 深度附件，
// 保留 PE PS1_Shadow 开头的 alpha 剔除：透明贴图不投影（discard 同时阻止深度写入）。
precision highp float;
layout(location = 0) in vec2 vUv;
uniform float uEnableTexture;
uniform sampler2D uDiffuseTex;
void main()
{
    if (uEnableTexture > 0.5 && texture(uDiffuseTex, vUv).a <= 0.0) discard;
}
