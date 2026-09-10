#version 310 es

// MikuEngine GLES 3.1 —— PMX 模型主渲染 顶点着色器（Phase 0 / 0.5）
//
// 光照部分严格照抄 PmxEditor 的 fxd_decoded.txt：
//   PhongColor    L291-307
//   调用点 + 非透过度赋值  L451-463（其中 L462-463: v_out.Color.w = Material_Diffuse.w）
//   vcol 关、light 开时：v_out.Color = PhongColor(phong_in) + BaseAmbient
//   Sphere UV     L437-438
//   ToonCf        L480-485
//
// 与 docs 原稿的差异（审计后修正）：
//   1. aJoints 用 UNSIGNED_SHORT ×4（本模型 1099 骨，UNSIGNED_BYTE 装不下）
//   2. aWeights 声明为 vec4（normalized 属性硬件自动 ÷255），不是 uvec4
//   3. 顶点着色器用 highp —— 世界空间骨骼运算用 mediump 会抖

precision highp float;
precision highp int;

// ── per-frame UBO (binding 0, std140) ──────────────────────────────────
layout(binding = 0) uniform FrameBlock {
    mat4 uViewProj;
    mat4 uView;             // 供 Sphere UV 用（PE: mul(n, (float3x3)View)）
    vec4 uCameraPosition;
    vec4 uLightDirection;   // xyz = 光线传播方向（PE: ln = normalize(-LightDirect)）
    vec4 uLightColor;
    vec4 uAmbientColor;
    vec4 uBaseAmbient;      // (0.07, 0.07, 0.07, 1) —— PE L25
} uFrame;

// ── 蒙皮矩阵 SSBO (binding 1) ───────────────────────────────────────────
// 1099 骨 × 64 B ≈ 70 KB，远超 UBO 的 16 KB 最小保证 —— 这正是选 SSBO 的原因。
layout(std430, binding = 1) buffer SkinMatricesBlock {
    mat4 uSkinMatrices[];
};

// ── per-draw uniforms ───────────────────────────────────────────────────
// ⚠️ 全部用 float：C# 侧统一走 glUniform1f。若这里声明成 int 而上传用 1f，
//    会触发 GL_INVALID_OPERATION 且 uniform 保持默认值 0 —— 纹理就会整体失效。
uniform float uSkinMatBase;      // 当前角色在 SSBO 里的骨骼起点
uniform vec4  uMaterialDiffuse;  // rgb = 材质色, a = MMD 非透过度
uniform vec4  uMaterialSpecular;
uniform float uMaterialShininess;
uniform vec4  uMaterialAmbient;

layout(location = 0) in vec3  aPosition;
layout(location = 1) in vec3  aNormal;
layout(location = 2) in vec4  aUv;      // xy=UV, z=EdgeScale, w=DeformType
layout(location = 3) in uvec4 aJoints;  // UNSIGNED_SHORT ×4
layout(location = 4) in vec4  aWeights; // UNSIGNED_BYTE ×4, normalized → [0,1]

layout(location = 0) out vec3  vNormal;
layout(location = 1) out vec4  vColor;
layout(location = 2) out vec2  vUv;
layout(location = 3) out vec2  vUvSphere;
layout(location = 4) out float vToonV;

void main()
{
    int base = int(uSkinMatBase + 0.5);

    // 用局部数组而非 vec 动态下标，规避部分驱动对向量动态索引的兼容性问题
    float w[4] = float[4](aWeights.x, aWeights.y, aWeights.z, aWeights.w);
    uint  j[4] = uint[4](aJoints.x, aJoints.y, aJoints.z, aJoints.w);

    float wsum = w[0] + w[1] + w[2] + w[3];

    vec4 sp = vec4(0.0);
    vec3 sn = vec3(0.0);

    if (wsum > 1e-5)
    {
        for (int k = 0; k < 4; k++)
        {
            float wk = w[k] / wsum;
            mat4 m = uSkinMatrices[base + int(j[k])];
            sp += m * vec4(aPosition, 1.0) * wk;
            sn += mat3(m) * aNormal * wk;
        }
    }
    else
    {
        // 权重全零（脏数据）：退化成第 0 根骨，避免顶点塌陷到原点
        mat4 m = uSkinMatrices[base];
        sp = m * vec4(aPosition, 1.0);
        sn = mat3(m) * aNormal;
    }

    vec3 nWorld = normalize(sn);
    vec3 pw = sp.xyz;

    // ── PhongColor（PE L291-307）────────────────────────────────────────
    vec3 vn = normalize(uFrame.uCameraPosition.xyz - pw);
    vec3 ln = normalize(-uFrame.uLightDirection.xyz);
    vec3 hn = normalize(vn + ln);

    float ndl = dot(nWorld, ln);
    float ly  = max(ndl, 0.0);
    float sh  = max(uMaterialShininess, 1.0);            // 避免 pow(0, 0)
    float lz  = (ndl < 0.0) ? 0.0 : pow(max(dot(nWorld, hn), 0.0), sh);

    vec4 diff = uMaterialDiffuse * ((0.5 * ly * uFrame.uLightColor) + uMaterialAmbient);
    diff *= uFrame.uAmbientColor * 2.0;                  // PE 的 *2.0 是 MMD 味道的关键，不要省
    vec4 spec = ly * lz * ((uFrame.uLightColor + uMaterialSpecular) * 0.5) * 0.5;

    vColor = clamp(diff + spec + uFrame.uBaseAmbient, 0.0, 1.0);  // PE: PhongColor + BaseAmbient
    vColor.a = uMaterialDiffuse.a;                       // ← PE L462-463：非透过度

    // ── 输出 ────────────────────────────────────────────────────────────
    vNormal = nWorld;
    vUv = aUv.xy;

    // Sphere UV（PE L437-438）：D3D9 约定，v = n.y * -0.5 + 0.5
    // 纹理数据按文件行序原样上传，采样结果与 PmxEditor 一致，无需翻转。
    vec3 nView = mat3(uFrame.uView) * nWorld;
    vUvSphere = nView.xy * vec2(0.5, -0.5) + 0.5;

    // ToonCf（PE L480-485）：dot(n, LightDirect) * 0.5 + 0.5
    vToonV = dot(nWorld, normalize(uFrame.uLightDirection.xyz)) * 0.5 + 0.5;

    gl_Position = uFrame.uViewProj * sp;
}
