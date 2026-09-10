using System.Numerics;

namespace MikuEngine.Core.Models;

/// <summary>
/// PMX 二进制文件解析后的完整模型数据（移植自 babylon-mmd 的 <c>PmxObject</c>）。
/// 纯数据容器，不包含任何渲染/物理逻辑，供 Core 其它模块与渲染层消费。
/// </summary>
public sealed class PmxModel
{
    /// <summary>文件头。</summary>
    public required PmxHeader Header { get; init; }

    /// <summary>顶点数组。</summary>
    public required PmxVertex[] Vertices { get; init; }

    /// <summary>索引数组（三角形面索引）。</summary>
    public required int[] Indices { get; init; }

    /// <summary>纹理路径表（不解析、不归一化，保留原始字符串）。</summary>
    public required string[] Textures { get; init; }

    /// <summary>材质数组。</summary>
    public required PmxMaterial[] Materials { get; init; }

    /// <summary>骨骼数组。</summary>
    public required PmxBone[] Bones { get; init; }

    /// <summary>表情（morph）数组。</summary>
    public required PmxMorph[] Morphs { get; init; }

    /// <summary>显示帧（骨骼/表情分组）数组。</summary>
    public required PmxDisplayFrame[] DisplayFrames { get; init; }

    /// <summary>刚体数组。</summary>
    public required PmxRigidBody[] RigidBodies { get; init; }

    /// <summary>物理约束（关节）数组。</summary>
    public required PmxJoint[] Joints { get; init; }

    /// <summary>软体数组（仅 PMX 2.1）。</summary>
    public required PmxSoftBody[] SoftBodies { get; init; }
}

/// <summary>字符串编码方式（PMX 头字节）。</summary>
public enum PmxEncoding : byte
{
    Utf16Le = 0,
    Utf8 = 1,
    ShiftJis = 2,
}

/// <summary>PMX 文件头。</summary>
public readonly struct PmxHeader
{
    /// <summary>签名，恒为 "PMX"。</summary>
    public required string Signature { get; init; }

    /// <summary>版本号（通常为 2.0 / 2.1）。</summary>
    public required float Version { get; init; }

    /// <summary>字符串编码。</summary>
    public required PmxEncoding Encoding { get; init; }

    /// <summary>顶点附加 Vec4 数量（adjacent UV / 顶点色）。</summary>
    public required int AdditionalVec4Count { get; init; }

    // 以下索引字节宽度（1 = int8/uint8，2 = int16/uint16，4 = int32）
    public required int VertexIndexSize { get; init; }
    public required int TextureIndexSize { get; init; }
    public required int MaterialIndexSize { get; init; }
    public required int BoneIndexSize { get; init; }
    public required int MorphIndexSize { get; init; }
    public required int RigidBodyIndexSize { get; init; }

    public required string ModelName { get; init; }
    public required string EnglishModelName { get; init; }
    public required string Comment { get; init; }
    public required string EnglishComment { get; init; }
}

/// <summary>骨骼权重结算方式。</summary>
public enum PmxBoneWeightType : byte
{
    Bdef1 = 0,
    Bdef2 = 1,
    Bdef4 = 2,
    Sdef = 3,
    Qdef = 4,
}

/// <summary>
/// 顶点骨骼权重。为简单起见用固定 4 槽 + SDEF 参数表示所有 BDEF/SDEF/QDEF 形态。
/// 有效槽位数量由 <see cref="SortOf"/>（Type）决定。
/// </summary>
public readonly struct PmxBoneWeight
{
    /// <summary>权重结算类型。</summary>
    public required PmxBoneWeightType Type { get; init; }

    /// <summary>骨骼索引（最多 4 个，未用槽位为 -1）。</summary>
    public required int Bone0 { get; init; }
    public required int Bone1 { get; init; }
    public required int Bone2 { get; init; }
    public required int Bone3 { get; init; }

    /// <summary>骨骼权重（未用槽位为 0）。</summary>
    public required float Weight0 { get; init; }
    public required float Weight1 { get; init; }
    public required float Weight2 { get; init; }
    public required float Weight3 { get; init; }

    // --- SDEF（球形变形的额外参数） ---
    public required Vector3 SdefC { get; init; }
    public required Vector3 SdefR0 { get; init; }
    public required Vector3 SdefR1 { get; init; }

    /// <summary>有效骨骼数量。</summary>
    public int BoneCount => Type switch
    {
        PmxBoneWeightType.Bdef1 => 1,
        PmxBoneWeightType.Bdef2 => 2,
        PmxBoneWeightType.Sdef => 2,
        PmxBoneWeightType.Bdef4 => 4,
        PmxBoneWeightType.Qdef => 4,
        _ => 0,
    };

    /// <summary>取第 i 个骨骼索引。</summary>
    public int Bone(int i) => i switch { 0 => Bone0, 1 => Bone1, 2 => Bone2, 3 => Bone3, _ => -1 };

    /// <summary>取第 i 个权重。BDEF1 的隐式权重处理见 <see cref="EffectiveWeight(int)"/>。</summary>
    public float Weight(int i) => i switch { 0 => Weight0, 1 => Weight1, 2 => Weight2, 3 => Weight3, _ => 0f };

    /// <summary>
    /// 返回实际参与蒙皮的归一化权重：BDEF2 第二根骨权重 = 1 - w0，SDEF 第一根骨权重 = 1 - w0。
    /// BDEF1 = {1.0}。
    /// </summary>
    public float EffectiveWeight(int i)
    {
        switch (Type)
        {
            case PmxBoneWeightType.Bdef1:
                return i == 0 ? 1f : 0f;
            case PmxBoneWeightType.Bdef2:
            case PmxBoneWeightType.Sdef:
                return i == 0 ? Weight0 : 1f - Weight0;
            default:
                return Weight(i);
        }
    }
}

/// <summary>顶点。位置/法线/UV 用列主序向量类型存储。</summary>
public readonly struct PmxVertex
{
    public required Vector3 Position { get; init; }
    public required Vector3 Normal { get; init; }
    public required Vector2 Uv { get; init; }

    /// <summary>附加 Vec4（UV2~UV4 / 顶点色等），数量由 Header.AdditionalVec4Count 决定。</summary>
    public required Vector4[] AdditionalVec4 { get; init; }

    public required PmxBoneWeight Weight { get; init; }

    /// <summary>轮廓线宽度系数（约 1.0 对应 1 像素）。</summary>
    public required float EdgeScale { get; init; }
}

/// <summary>材质标志位。</summary>
[Flags]
public enum PmxMaterialFlag : byte
{
    None = 0,
    IsDoubleSided = 1,
    EnabledGroundShadow = 2,
    EnabledDrawShadow = 4,
    EnabledReceiveShadow = 8,
    EnabledToonEdge = 16,
    EnabledVertexColor = 32,
    EnabledPointDraw = 64,
    EnabledLineDraw = 128,
}

/// <summary>球体贴图混合方式。</summary>
public enum PmxMaterialSphereMode : byte
{
    Off = 0,
    Multiply = 1,
    Add = 2,
    SubTexture = 3,
}

/// <summary>材质定义。</summary>
public readonly struct PmxMaterial
{
    public required string Name { get; init; }
    public required string EnglishName { get; init; }

    public required Vector4 Diffuse { get; init; }
    public required Vector3 Specular { get; init; }
    public required float Shininess { get; init; }
    public required Vector3 Ambient { get; init; }

    public required PmxMaterialFlag Flag { get; init; }

    public required Vector4 EdgeColor { get; init; }
    public required float EdgeSize { get; init; }

    /// <summary>主纹理索引，-1 表示无。</summary>
    public required int TextureIndex { get; init; }

    /// <summary>球体纹理索引，-1 表示无。</summary>
    public required int SphereTextureIndex { get; init; }
    public required PmxMaterialSphereMode SphereTextureMode { get; init; }

    /// <summary>是否使用共享卡通纹理（toonX.png）。</summary>
    public required bool IsSharedToonTexture { get; init; }

    /// <summary>
    /// 卡通纹理索引。共享时为 0..9（toon01~toon10）；否则为纹理表索引；-1 表示无。
    /// </summary>
    public required int ToonTextureIndex { get; init; }

    public required string Comment { get; init; }

    /// <summary>本材质在一次 draw 中使用的索引数量。</summary>
    public required int IndexCount { get; init; }
}

/// <summary>骨骼标志位。</summary>
[Flags]
public enum PmxBoneFlag : ushort
{
    None = 0,
    UseBoneIndexAsTailPosition = 1,
    IsRotatable = 2,
    IsMovable = 4,
    IsVisible = 8,
    IsControllable = 16,
    IsIkEnabled = 32,
    LocalAppendTransform = 128,
    HasAppendRotate = 256,
    HasAppendMove = 512,
    HasAxisLimit = 1024,
    HasLocalVector = 2048,
    TransformAfterPhysics = 4096,
    IsExternalParentTransformed = 8192,
}

/// <summary>IK 链接（IK 链中的一节）。</summary>
public readonly struct PmxIkLink
{
    /// <summary>受 IK 影响的骨骼索引。</summary>
    public required int BoneIndex { get; init; }

    /// <summary>是否带角度限制。</summary>
    public required bool HasLimitation { get; init; }

    /// <summary>最小角度限制（弧度，YXZ）。无限制时为零向量。</summary>
    public required Vector3 MinimumAngle { get; init; }

    /// <summary>最大角度限制（弧度，YXZ）。无限制时为零向量。</summary>
    public required Vector3 MaximumAngle { get; init; }
}

/// <summary>追加变换（附加变换）配置。</summary>
public readonly struct PmxAppendTransform
{
    public required int ParentIndex { get; init; }
    public required float Ratio { get; init; }
}

/// <summary>局部坐标向量（x 轴与 z 轴向）。</summary>
public readonly struct PmxLocalVector
{
    public required Vector3 X { get; init; }
    public required Vector3 Z { get; init; }
}

/// <summary>IK 求解配置。</summary>
public readonly struct PmxIk
{
    /// <summary>IK 目标骨骼索引。</summary>
    public required int Target { get; init; }

    /// <summary>迭代次数。</summary>
    public required int Iteration { get; init; }

    /// <summary>旋转限制量（弧度）。</summary>
    public required float RotationConstraint { get; init; }

    public required PmxIkLink[] Links { get; init; }
}

/// <summary>骨骼定义。</summary>
public readonly struct PmxBone
{
    public required string Name { get; init; }
    public required string EnglishName { get; init; }
    public required Vector3 Position { get; init; }
    public required int ParentBoneIndex { get; init; }

    /// <summary>变形顺序（决定骨骼矩阵乘法次序）。</summary>
    public required int TransformOrder { get; init; }

    public required PmxBoneFlag Flag { get; init; }

    /// <summary>
    /// 尾部位置：若标志 UseBoneIndexAsTailPosition 置位则为骨骼索引，否则为世界坐标偏移。
    /// 仅用于编辑器可视化，运行时一般用不到。
    /// </summary>
    public required int TailBoneIndex { get; init; }
    public required Vector3 TailPosition { get; init; }
    public required bool IsTailBoneIndex { get; init; }

    /// <summary>追加变换（可选）。</summary>
    public PmxAppendTransform? AppendTransform { get; init; }

    /// <summary>轴向限制（可选）。</summary>
    public Vector3? AxisLimit { get; init; }

    /// <summary>局部坐标向量（可选）。</summary>
    public PmxLocalVector? LocalVector { get; init; }

    /// <summary>外部父骨骼索引（可选）。</summary>
    public int? ExternalParentIndex { get; init; }

    /// <summary>IK 配置（可选）。</summary>
    public PmxIk? Ik { get; init; }
}

/// <summary>表情（morph）类别。</summary>
public enum PmxMorphCategory : byte
{
    System = 0,
    Eyebrow = 1,
    Eye = 2,
    Lip = 3,
    Other = 4,
}

/// <summary>表情类型。</summary>
public enum PmxMorphType : byte
{
    Group = 0,
    Vertex = 1,
    Bone = 2,
    Uv = 3,
    AdditionalUv1 = 4,
    AdditionalUv2 = 5,
    AdditionalUv3 = 6,
    AdditionalUv4 = 7,
    Material = 8,
    Flip = 9,
    Impulse = 10,
}

/// <summary>材质表情的操作类型。</summary>
public enum PmxMaterialMorphType : byte
{
    Multiply = 0,
    Add = 1,
}

/// <summary>材质表情单条记录。</summary>
public readonly struct PmxMaterialMorphElement
{
    /// <summary>材质索引，-1 表示全部材质。</summary>
    public required int MaterialIndex { get; init; }
    public required PmxMaterialMorphType Type { get; init; }
    public required Vector4 Diffuse { get; init; }
    public required Vector3 Specular { get; init; }
    public required float Shininess { get; init; }
    public required Vector3 Ambient { get; init; }
    public required Vector4 EdgeColor { get; init; }
    public required float EdgeSize { get; init; }
    public required Vector4 TextureColor { get; init; }
    public required Vector4 SphereTextureColor { get; init; }
    public required Vector4 ToonTextureColor { get; init; }
}

/// <summary>
/// 表情。不同类型使用不同字段；未使用的字段为 null/空数组。
/// 与 babylon-mmd 一致，采用"单类型容器 + 类型判定字段"模型。
/// </summary>
public sealed class PmxMorph
{
    public required string Name { get; init; }
    public required string EnglishName { get; init; }
    public required PmxMorphCategory Category { get; init; }
    public required PmxMorphType Type { get; init; }

    // --- Group / Flip ---
    /// <summary>引用的其它表情索引。</summary>
    public int[]? Indices { get; init; }

    /// <summary>组合比例（Group）/ 翻转倍率（Flip）。</summary>
    public float[]? Ratios { get; init; }

    // --- Vertex ---
    public float[]? Positions { get; init; }

    // --- Bone ---
    public float[]? Rotations { get; init; }

    // --- Uv / AdditionalUv ---
    public float[]? Offsets { get; init; }

    // --- Material ---
    public PmxMaterialMorphElement[]? MaterialElements { get; init; }

    // --- Impulse ---
    /// <summary>是否用局部坐标施加冲量。</summary>
    public bool[]? IsLocals { get; init; }
    public float[]? Velocities { get; init; }
    public float[]? Torques { get; init; }
}

/// <summary>显示帧（骨骼/表情分组）元素类型。</summary>
public enum PmxDisplayFrameElementType : byte
{
    Bone = 0,
    Morph = 1,
}

/// <summary>显示帧。</summary>
public readonly struct PmxDisplayFrame
{
    public required string Name { get; init; }
    public required string EnglishName { get; init; }
    public required bool IsSpecialFrame { get; init; }
    public required PmxDisplayFrameElement[] Elements { get; init; }
}

/// <summary>显示帧里的单个元素。</summary>
public readonly struct PmxDisplayFrameElement
{
    public required PmxDisplayFrameElementType Type { get; init; }
    public required int Index { get; init; }
}

/// <summary>刚体形状。</summary>
public enum PmxRigidBodyShapeType : byte
{
    Sphere = 0,
    Box = 1,
    Capsule = 2,
}

/// <summary>刚体物理模式。</summary>
public enum PmxRigidBodyMode : byte
{
    FollowBone = 0,
    Physics = 1,
    PhysicsWithBone = 2,
}

/// <summary>物理刚体。</summary>
public readonly struct PmxRigidBody
{
    public required string Name { get; init; }
    public required string EnglishName { get; init; }

    /// <summary>关联骨骼索引，-1 表示地面。</summary>
    public required int BoneIndex { get; init; }

    public required byte CollisionGroup { get; init; }
    public required ushort CollisionMask { get; init; }

    public required PmxRigidBodyShapeType ShapeType { get; init; }
    public required Vector3 ShapeSize { get; init; }
    public required Vector3 ShapePosition { get; init; }
    public required Vector3 ShapeRotation { get; init; }

    public required float Mass { get; init; }
    public required float LinearDamping { get; init; }
    public required float AngularDamping { get; init; }
    public required float Repulsion { get; init; }
    public required float Friction { get; init; }
    public required PmxRigidBodyMode PhysicsMode { get; init; }
}

/// <summary>物理关节（约束）类型。</summary>
public enum PmxJointType : byte
{
    Spring6dof = 0,
    Sixdof = 1,
    P2p = 2,
    ConeTwist = 3,
    Slider = 4,
    Hinge = 5,
}

/// <summary>物理关节。通常参数用于构建 6DOF 弹簧约束。</summary>
public readonly struct PmxJoint
{
    public required string Name { get; init; }
    public required string EnglishName { get; init; }
    public required PmxJointType Type { get; init; }
    public required int RigidBodyIndexA { get; init; }
    public required int RigidBodyIndexB { get; init; }
    public required Vector3 Position { get; init; }
    public required Vector3 Rotation { get; init; }
    public required Vector3 PositionMin { get; init; }
    public required Vector3 PositionMax { get; init; }
    public required Vector3 RotationMin { get; init; }
    public required Vector3 RotationMax { get; init; }
    public required Vector3 SpringPosition { get; init; }
    public required Vector3 SpringRotation { get; init; }
}

/// <summary>软体类型。</summary>
public enum PmxSoftBodyType : byte
{
    TriMesh = 0,
    Rope = 1,
}

/// <summary>软体锚点。</summary>
public readonly struct PmxSoftBodyAnchor
{
    public required int RigidBodyIndex { get; init; }
    public required int VertexIndex { get; init; }
    public required bool IsNearMode { get; init; }
}

/// <summary>软体力学参数。</summary>
public readonly struct PmxSoftBodyConfig
{
    public required float Vcf { get; init; } // Velocities correction factor (Baumgarte)
    public required float Dp { get; init; }  // Damping coefficient
    public required float Dg { get; init; }  // Drag coefficient
    public required float Lf { get; init; }  // Lift coefficient
    public required float Pr { get; init; }  // Pressure coefficient
    public required float Vc { get; init; }  // Volume conversation coefficient
    public required float Df { get; init; }  // Dynamic friction coefficient
    public required float Mt { get; init; }  // Pose matching coefficient
    public required float Chr { get; init; } // Rigid contacts hardness
    public required float Khr { get; init; } // Kinetic contacts hardness
    public required float Shr { get; init; } // Soft contacts hardness
    public required float Ahr { get; init; } // Anchors hardness
}

/// <summary>软体。仅 PMX 2.1 含此段。</summary>
public readonly struct PmxSoftBody
{
    public required string Name { get; init; }
    public required string EnglishName { get; init; }
    public required PmxSoftBodyType Type { get; init; }
    public required int MaterialIndex { get; init; }
    public required byte CollisionGroup { get; init; }
    public required ushort CollisionMask { get; init; }
    public required byte Flags { get; init; }
    public required int BLinkDistance { get; init; }
    public required int ClusterCount { get; init; }
    public required float TotalMass { get; init; }
    public required float CollisionMargin { get; init; }
    public required int AeroModel { get; init; }
    public required PmxSoftBodyConfig Config { get; init; }
    public required float[] Cluster { get; init; }
    public required int[] Iteration { get; init; }
    public required int[] Material { get; init; }
    public required PmxSoftBodyAnchor[] Anchors { get; init; }
    public required int[] VertexPins { get; init; }
}