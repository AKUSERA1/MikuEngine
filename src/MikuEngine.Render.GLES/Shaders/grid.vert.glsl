#version 310 es

// MikuEngine GLES 3.1 —— 迷雾格网地面 顶点着色器

layout(location = 0) in vec3 aPos;

layout(binding = 0) uniform GridUniform {
    mat4 uViewProj;
    vec4 uFogColor;
    vec4 uFadeParams;
    vec4 uGridParams;
    vec4 uMinorColor;
    vec4 uMajorColor;
    vec4 uAxisXColor;
    vec4 uAxisZColor;
    vec4 uSurface;
} u;

layout(location = 0) out vec3 vWorldPos;

void main() {
    vWorldPos = aPos;
    gl_Position = u.uViewProj * vec4(aPos, 1.0);
}
