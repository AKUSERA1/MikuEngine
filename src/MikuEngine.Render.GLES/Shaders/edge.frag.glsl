#version 310 es

// MikuEngine GLES 3.1 —— PMX 轮廓线（Edge / Outline）片元着色器
//
// PE 的 PS1_Edge（fxd_decoded.txt L606-608）只有一行：
//   float4 PS1_Edge(pIn p_in) : COLOR0 { return p_in.Col; }
// 没有光照、没有纹理、没有 Toon/Sphere，也没有 clip。
//
// 混合：tec_edge 的 pass 没写 AlphaBlendEnable，D3D9 渲染状态是粘滞的，
// 会继承 tec_model 留下的 True / SRCALPHA / INVSRCALPHA。
// 因此 EdgeColor.w < 1 的边缘是半透明的（本模型脸/肌/足 = 0.60，其余多为 0.80）。

precision highp float;

layout(location = 0) in vec4 vColor;

out vec4 fragColor;

void main()
{
    fragColor = vColor;
}
