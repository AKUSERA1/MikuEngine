#version 310 es

// MikuEngine GLES 3.1 —— PMX 模型主渲染 顶点着色器（Phase 0 / 0.5）
//
// 光照部分严格复刻 PmxEditor 的 fxd_decoded.txt：
//   PhongColor    L291-307
//   调用点 + 非透过度赋值  L451-463（其中 L462-463: v_out.Color.w = Material_Diffuse.w）
//   vcol 关、light 开时：v_out.Color = PhongColor(phong_in) + BaseAmbient
//   Sphere UV     L437-438
//   ToonCf        L480-485

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
    mat4 uLightViewProj;    // WVP_Light：自阴影 / 床影用
} uFrame;

// ── 蒙皮矩阵 SSBO (binding 1) ───────────────────────────────────────────
// 1024 骨 × 64 B ≈ 65 KB，远超 UBO 的 16 KB 最小保证 —— 这正是选 SSBO 的原因。
layout(std430, binding = 1) buffer SkinMatricesBlock {
    mat4 uSkinMatrices[];
};

// ── 顶点 morph 偏移 SSBO (binding 2) ────────────────────────────────────
// PMX 顶点 morph 的偏移是【模型空间】量，必须在蒙皮之前加到 aPosition 上。
// 索引直接用 gl_VertexID —— glDrawElements 下它等于顶点索引（不是元素序号）。
// 每帧只上传脏区，未受影响顶点的偏移恒为 0。
layout(std430, binding = 2) buffer MorphBlock {
    vec4 uMorphOffsets[];    // xyz = 偏移，w 未用（std430 下 vec4 数组 stride = 16B）
};

// ── UV morph 偏移 SSBO (binding 3) ─────────────────────────────────────
// PMX 的 UV morph 偏移直接加在顶点 UV 上（本引擎上传的是文件原序 UV 且采样已与
// PmxEditor 对齐，因此这里【不做】V 翻转）。只影响主纹理 UV；球贴图 / toon 的
// 坐标由法线与视图推出，不受 UV morph 影响。
layout(std430, binding = 3) buffer MorphUvBlock {
    vec2 uMorphUvs[];        // std430 下 vec2 数组 stride = 8B
};

// ── per-draw uniforms ───────────────────────────────────────────────────
// 全部用 float：C# 侧统一走 glUniform1f。若这里声明成 int 而上传用 1f，
// 则会触发 GL_INVALID_OPERATION 且 uniform 保持默认值 0 —— 纹理就会整体失效。
uniform float uSkinMatBase;      // 当前角色在 SSBO 里的骨骼起点
uniform float uMorphEnabled;     // 0 = 模型无顶点 morph（此时不读 binding 2）
uniform float uMorphUvEnabled;   // 0 = 模型无 UV morph（此时不读 binding 3）
uniform vec4  uMaterialDiffuse;  // rgb = 材质色, a = MMD 非透过度
uniform vec4  uMaterialSpecular;
uniform float uMaterialShininess;
uniform vec4  uMaterialAmbient;
uniform float uNormalOffset;     // 法线偏移偏置（世界单位；demo 按 1.5×世界texel 接线）
uniform mat4  uModelRoot;        // 渲染层根矩阵：MMD 拡大率（+ 无 全ての親 时的 TR 兜底），蒙皮后整体施加
uniform mat3  uModelNormalRoot;  // = Root 线性部分⁻ᵀ（引擎侧算好上传）；非均匀缩放的法线方向修正

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
layout(location = 6) out vec4  vLightPos;     // 光源裁剪坐标：主渲染内联 PCF 采样 Z 图用（阶段 3）

void main()
{
    int base = int(uSkinMatBase + 0.5);

    // 用局部数组而非 vec 动态下标，规避部分驱动对向量动态索引的兼容性问题
    float w[4] = float[4](aWeights.x, aWeights.y, aWeights.z, aWeights.w);
    uint  j[4] = uint[4](aJoints.x, aJoints.y, aJoints.z, aJoints.w);

    float wsum = w[0] + w[1] + w[2] + w[3];

    // 顶点 morph：先加偏移，再蒙皮（PMX 顶点 morph 偏移是模型空间量）。
    // 下面两个分支（正常 / 权重全零退化）都必须用 morphPos，否则脏数据顶点会跳回未变形位置。
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
        // 权重全零（脏数据）：退化成第 0 根骨，避免顶点塌陷到原点
        mat4 m = uSkinMatrices[base];
        sp = m * vec4(morphPos, 1.0);
        sn = mat3(m) * aNormal;
    }

    // 渲染层根变换（MMD 拡大率）：蒙皮之后整体施加 —— 物理/IK/付与运行在 bind 尺度，
    // 缩放只影响视觉。非均匀缩放（压成纸片）不经过蒙皮链，因此不产生剪切。
    // 法线用逆轉置修正（普通矩阵变换法线在非均匀缩放下方向是错的），随后归一化。
    sp = uModelRoot * sp;
    vec3 nWorld = normalize(uModelNormalRoot * sn);
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
    // UV morph：主纹理 UV 加偏移（模型空间无关，纯粹是 UV 空间的位移）
    vUv = aUv.xy + (uMorphUvEnabled > 0.5 ? uMorphUvs[gl_VertexID] : vec2(0.0));

    // Sphere UV（PE L437-438）：D3D9 约定，v = n.y * -0.5 + 0.5
    // 纹理数据按文件行序原样上传，采样结果与 PmxEditor 一致，无需翻转。
    vec3 nView = mat3(uFrame.uView) * nWorld;
    vUvSphere = nView.xy * vec2(0.5, -0.5) + 0.5;

    // ToonCf（PE L480-485）：dot(n, LightDirect) * 0.5 + 0.5
    vToonV = dot(nWorld, normalize(uFrame.uLightDirection.xyz)) * 0.5 + 0.5;

    // 光源裁剪坐标（= uLightViewProj * sp）：内联 PCF 用它算光空间 uv + z 直接采样 Z 图。
    // 阶段 3 起不再有屏幕空间影强度图，也就不需要相机裁剪坐标了。
    // 法线偏移偏置（reze §2 #5）：接收位置沿世界法线推离表面 —— 弧面（脸/裙内）的 acne
    // 由它 + Z pass 斜率偏置共同消化；比较侧的余量自阶段 7 起改为「常数底 + 按面朝向的
    // 斜率缩放」（见 model.frag.glsl），不再是 PE 的全屏常数 0.003。
    vLightPos = uFrame.uLightViewProj * vec4(pw + nWorld * uNormalOffset, 1.0);

    gl_Position = uFrame.uViewProj * sp;
}
