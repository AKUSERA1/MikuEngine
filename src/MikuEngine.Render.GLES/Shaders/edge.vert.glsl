#version 310 es

// MikuEngine GLES 3.1 —— PMX 轮廓线（Edge / Outline）顶点着色器
//
// 移植自 PmxEditor 的 VS1_Edge（fxd_decoded.txt L589-609）：
//   WeightVertexOut wv = WeightVertex(v_in);            // 蒙皮（BDEF 分支里已 normalize）
//   float h = ((v_in.UV.z * EdgeValue) * OffsetMul_EdgeSize + OffsetAdd_EdgeSize) * BaseEdgeValue;
//   float d = distance(CameraPosition, wv.Position) * BaseDistanceInv;
//   float cf = sqrt(d);
//   float3 offset = cf * h * wv.Normal;
//   float4 p2 = float4(wv.Position.xyz + offset, 1);
//   v_out.Position = mul(p2, WorldViewProj);
//   v_out.Color = EdgeColor;
//
// 常量（fxd L29-31）：EdgeValue=1.0、BaseEdgeValue=0.015、BaseDistanceInv=0.1
//
// 与 PE 的一处刻意差异：PE 的 EdgeValue 是**模型级**全局值（编辑器 UI「サイズ」，
// PmxModel.EdgeValue），而 PMX 里 EdgeSize 本是**逐材质**的。

precision highp float;
precision highp int;

// 与 model.vert.glsl 相同的 per-frame UBO（binding 0）与蒙皮 SSBO（binding 1）
layout(binding = 0) uniform FrameBlock {
    mat4 uViewProj;
    mat4 uView;
    vec4 uCameraPosition;
    vec4 uLightDirection;
    vec4 uLightColor;
    vec4 uAmbientColor;
    vec4 uBaseAmbient;
} uFrame;

layout(std430, binding = 1) buffer SkinMatricesBlock {
    mat4 uSkinMatrices[];
};

// 顶点 morph 偏移（binding 2）—— 必须与主渲染用同一份偏移，否则轮廓线会与本体错位。
layout(std430, binding = 2) buffer MorphBlock {
    vec4 uMorphOffsets[];
};

uniform float uSkinMatBase;
uniform float uMorphEnabled;       // 0 = 模型无顶点 morph
uniform vec4  uMaterialEdgeColor;
uniform float uMaterialEdgeSize;   // PMX 材质的 EdgeSize

layout(location = 0) in vec3  aPosition;
layout(location = 1) in vec3  aNormal;
layout(location = 2) in vec4  aUv;      // z = 逐顶点 EdgeScale
layout(location = 3) in uvec4 aJoints;
layout(location = 4) in vec4  aWeights;

layout(location = 0) out vec4 vColor;

void main()
{
    int base = int(uSkinMatBase + 0.5);

    float w[4] = float[4](aWeights.x, aWeights.y, aWeights.z, aWeights.w);
    uint  j[4] = uint[4](aJoints.x, aJoints.y, aJoints.z, aJoints.w);
    float wsum = w[0] + w[1] + w[2] + w[3];

    // 顶点 morph：与主 VS 完全一致（模型空间偏移，蒙皮之前）
    vec3 morphPos = aPosition + (uMorphEnabled > 0.5 ? uMorphOffsets[gl_VertexID].xyz : vec3(0.0));

    vec4 sp = vec4(0.0);
    vec3 sn = vec3(0.0);
    if (wsum > 1e-5)
    {
        for (int k = 0; k < 4; k++)
        {
            float wk = w[k] / wsum;
            mat4 m = uSkinMatrices[base + int(j[k])];
            sp += m * vec4(morphPos, 1.0) * wk;
            sn += mat3(m) * aNormal * wk;
        }
    }
    else
    {
        mat4 m = uSkinMatrices[base];
        sp = m * vec4(morphPos, 1.0);
        sn = mat3(m) * aNormal;
    }

    // PE 的 WeightVertex 在 BDEF 各分支里都会 normalize，这里保持一致
    vec3 n = normalize(sn);

    // ── Edge 外推（PE VS1_Edge L595-600）─────────────────────────────
    // OffsetMul/OffsetAdd_EdgeSize 是 Material Morph 的产物，v1 无 morph，恒为 1 / 0
    float h  = ((aUv.z * uMaterialEdgeSize) * 1.0 + 0.0) * 0.015;   // BaseEdgeValue = 0.015
    float d  = distance(uFrame.uCameraPosition.xyz, sp.xyz) * 0.1;  // BaseDistanceInv = 0.1
    float cf = sqrt(d);
    vec3 offset = cf * h * n;

    vec4 p2 = vec4(sp.xyz + offset, 1.0);
    gl_Position = uFrame.uViewProj * p2;

    vColor = uMaterialEdgeColor;   // 含 EdgeColor.w（PE 的 Edge 自身 alpha）
}
