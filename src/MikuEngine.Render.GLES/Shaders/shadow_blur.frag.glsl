#version 310 es
// 影 mask 的可分离高斯模糊（水平/垂直各跑一遍，uStep = 方向 × 半径，UV 单位）
// 9-tap 权重 = [0.2270, 0.1946, 0.1216, 0.0541, 0.0162]（经典 σ≈2 二项系数）。
// 顶点着色器复用 debug_quad.vert（gl_VertexID 生成全屏两三角形）。
precision highp float;

uniform sampler2D uTex;
uniform vec2 uStep;    // 已含方向与半径（例：(radius/mw, 0) 或 (0, radius/mh)）

in vec2 vUv;
out vec4 fragColor;

void main()
{
    float m = texture(uTex, vUv).r * 0.227027;
    m += (texture(uTex, vUv + uStep      ).r + texture(uTex, vUv - uStep      ).r) * 0.1945946;
    m += (texture(uTex, vUv + uStep * 2.0).r + texture(uTex, vUv - uStep * 2.0).r) * 0.1216216;
    m += (texture(uTex, vUv + uStep * 3.0).r + texture(uTex, vUv - uStep * 3.0).r) * 0.054054;
    m += (texture(uTex, vUv + uStep * 4.0).r + texture(uTex, vUv - uStep * 4.0).r) * 0.016216;
    fragColor = vec4(m, 0.0, 0.0, 1.0);
}
