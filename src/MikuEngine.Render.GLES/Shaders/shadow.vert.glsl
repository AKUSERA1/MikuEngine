#version 310 es
// 自阴影 共用顶点着色器（PE VS1_Shadow）
//
// 两个 pass 共用同一套蒙皮/UV 计算，但【光栅化视角不同】，由 uCameraSpace 切换：
//   · Z pass（uCameraSpace=0）：从【光源】光栅化 —— gl_Position = WVP_Light * p
//     对应 PE ZPlot：把 WorldViewProj 换成 WVP_Light。
//   · 影强度 pass（uCameraSpace=1）：从【相机】光栅化 —— gl_Position = camera WVP * p
//     对应 PE DrawSelfShadow：先 SetEffectMatrix()（只设相机 World/View/WVP），
//     VS1_Shadow 里 Position 用相机 WorldViewProj，只有 Depth 用 WVP_Light。
//
// 为什么必须分成两种：影强度图是【屏幕空间】的遮影强度，
// 每个"相机看得见的片元"要在自己的屏幕位置写下"自己是否被光挡住"的判定。
// 若两段都从光源光栅化，只有离光最近的面写得进去，而它们按定义就是受光的（判定 0），
// 相机看得见但被遮挡的面永远读不到自己的判定 → 投影阴影信息整体丢失。
precision highp float;
precision highp int;

layout(binding = 0) uniform FrameBlock {
    mat4 uViewProj;
    mat4 uView;
    vec4 uCameraPosition;
    vec4 uLightDirection;
    vec4 uLightColor;
    vec4 uAmbientColor;
    vec4 uBaseAmbient;
    mat4 uLightViewProj;
} uFrame;

layout(std430, binding = 1) buffer SkinMatricesBlock { mat4 uSkinMatrices[]; };

uniform float uSkinMatBase;
uniform float uCameraSpace;   // 0 = 从光源光栅化（Z pass）/ 1 = 从相机光栅化（影强度 pass）

layout(location = 0) in vec3  aPosition;
layout(location = 1) in vec3  aNormal;
layout(location = 2) in vec4  aUv;
layout(location = 3) in uvec4 aJoints;
layout(location = 4) in vec4  aWeights;

layout(location = 0) out vec2 vUv;
layout(location = 1) out vec4 vLightPos;

void main()
{
    int base = int(uSkinMatBase + 0.5);
    float w[4] = float[4](aWeights.x, aWeights.y, aWeights.z, aWeights.w);
    uint  j[4] = uint[4](aJoints.x, aJoints.y, aJoints.z, aJoints.w);
    float wsum = w[0] + w[1] + w[2] + w[3];

    vec4 sp = vec4(0.0);
    if (wsum > 1e-5)
    {
        for (int k = 0; k < 4; k++)
            sp += uSkinMatrices[base + int(j[k])] * vec4(aPosition, 1.0) * (w[k] / wsum);
    }
    else
    {
        sp = uSkinMatrices[base] * vec4(aPosition, 1.0);
    }

    vec4 lp = uFrame.uLightViewProj * sp;   // 光源裁剪坐标（深度比较用）
    vLightPos = lp;
    vUv = aUv.xy;

    gl_Position = (uCameraSpace > 0.5) ? (uFrame.uViewProj * sp) : lp;
}
