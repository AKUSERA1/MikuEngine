#version 310 es
// 调试用贴图预览（步骤 1）
// 把中间 RenderTarget 原样（灰度）画出来，用来判断 pass 到底有没有产出数据。
//   · uChannel：0=R 1=G 2=B（Z 图与影强度图都只用 R）
//   · uGain   ：整体增益（Z 图动态范围小的时候临时放大用，默认 1.0）
precision highp float;

uniform sampler2D uTex;
uniform float uChannel;   // 0=R 1=G 2=B —— 按本仓约定用 float 传
uniform float uGain;

in vec2 vUv;
out vec4 fragColor;

void main()
{
    vec4 t = texture(uTex, vUv);
    float v;
    if (uChannel > 1.5)      v = t.b;
    else if (uChannel > 0.5) v = t.g;
    else                     v = t.r;

    v = clamp(v * uGain, 0.0, 1.0);
    fragColor = vec4(v, v, v, 1.0);
}
