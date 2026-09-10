#version 310 es
// 床影顶点着色器（PE VS1_FloorShadow：只有 POSITION）
precision highp float;
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
layout(location = 0) in vec3 aPos;
layout(location = 0) out vec4 vLightPos;
void main()
{
    vec4 p = vec4(aPos, 1.0);
    vLightPos = uFrame.uLightViewProj * p;
    gl_Position = uFrame.uViewProj * p;
}
