#version 310 es
// 影 mask pass 顶点着色器：
// 从【相机】光栅化模型（gl_Position 用 uViewProj），片元里按【光源】坐标采样 Z 图
// 得到连续 lit 值 —— mask 是屏幕空间的"自阴影强度场"，供模糊 + 0.5 阈值提取重建光滑边缘。
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

layout(location = 0) in vec3  aPosition;
layout(location = 1) in vec3  aNormal;   // 与主渲染 VAO 对齐，本 pass 不消费
layout(location = 2) in vec4  aUv;
layout(location = 3) in uvec4 aJoints;
layout(location = 4) in vec4  aWeights;

layout(location = 0) out vec4 vLightPos;

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

    vLightPos = uFrame.uLightViewProj * sp;
    gl_Position = uFrame.uViewProj * sp;
}
