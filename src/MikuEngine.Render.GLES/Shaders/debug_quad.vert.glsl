#version 310 es
// 调试用全屏四边形（步骤 1：中间 RT 只读预览）
// 不依赖 VBO/属性，只用 gl_VertexID 生成两个三角形（6 顶点 TRIANGLES）。
// uRect = (x0, y0, x1, y1)，NDC 坐标，y = +1 是屏幕上方。
//
// 注意：vUv 的 (0,0) 落在 uRect 的 (x0,y0)，与 GL 纹理/FBO 的 v=0 在下一致，
// 因此预览图【不做上下翻转】，与主渲染的朝向可比。
precision highp float;

uniform vec4 uRect;   // (x0, y0, x1, y1) in NDC

out vec2 vUv;

void main()
{
    vec2 c = vec2(float(gl_VertexID & 1), float((gl_VertexID >> 1) & 1));
    vec2 ndc = mix(uRect.xy, uRect.zw, c);
    vUv = c;
    gl_Position = vec4(ndc, 0.0, 1.0);
}
