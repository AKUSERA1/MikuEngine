#version 310 es
// 自阴影 Z pass 顶点着色器（PE VS1_Shadow 的光源部分）
//
// 内联PCF直接采样 Z 图。
// 不学PE的 uCameraSpace 双视角分支 —— Z pass 只剩"从光源光栅化"一种。
//
// aNormal 声明但未使用：本 pass 与主渲染共用同一个 VAO，attribute 槽位必须对齐
// （location 1 被主 VS 的 aNormal 占用）；未消费的输入会被编译器优化掉，无运行成本。
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

// 顶点 morph 偏移（binding 2）—— 影子必须跟着表情走，否则眨眼时影图与脸不同步。
layout(std430, binding = 2) buffer MorphBlock { vec4 uMorphOffsets[]; };

uniform float uSkinMatBase;
uniform float uMorphEnabled;     // 0 = 模型无顶点 morph

layout(location = 0) in vec3  aPosition;
layout(location = 1) in vec3  aNormal;
layout(location = 2) in vec4  aUv;
layout(location = 3) in uvec4 aJoints;
layout(location = 4) in vec4  aWeights;

layout(location = 0) out vec2 vUv;

void main()
{
    int base = int(uSkinMatBase + 0.5);
    float w[4] = float[4](aWeights.x, aWeights.y, aWeights.z, aWeights.w);
    uint  j[4] = uint[4](aJoints.x, aJoints.y, aJoints.z, aJoints.w);
    float wsum = w[0] + w[1] + w[2] + w[3];

    vec3 morphPos = aPosition + (uMorphEnabled > 0.5 ? uMorphOffsets[gl_VertexID].xyz : vec3(0.0));

    vec4 sp = vec4(0.0);
    if (wsum > 1e-5)
    {
        for (int k = 0; k < 4; k++)
            sp += uSkinMatrices[base + int(j[k])] * vec4(morphPos, 1.0) * (w[k] / wsum);
    }
    else
    {
        sp = uSkinMatrices[base] * vec4(morphPos, 1.0);
    }

    vUv = aUv.xy;
    gl_Position = uFrame.uLightViewProj * sp;
}
