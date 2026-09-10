using System.Numerics;

namespace MikuEngine.Core.Models;

/// <summary>
/// 引擎侧的材质渲染分类。PMX 本身不区分，由 <c>SkeletalModelConverter</c> 按
/// <c>PmxMaterial.Diffuse.W</c>（MMD "非透过度"）判定。
/// 参见 docs/shader-design.md §3.2.2 / §5.2.7。
/// </summary>
public enum MaterialRenderType : byte
{
    Opaque = 0,
    Cutout = 1,
    Blended = 2,
}

/// <summary>
/// 一个材质段 = 一次 draw call。
/// </summary>
public sealed class DrawSegment
{
    public int MaterialIndex;
    public string MaterialName = "";

    /// <summary>索引缓冲内的起始位置（以 uint 元素计）。</summary>
    public int IndexStart;

    public int IndexCount;

    public MaterialRenderType Type;

    /// <summary>纹理库 id，-1 表示无。</summary>
    public int DiffuseTextureId = -1;
    public int SphereTextureId = -1;
    public int ToonTextureId = -1;

    public PmxMaterialSphereMode SphereMode;
    public bool EnableTexture;
    public bool EnableSphere;
    public bool EnableToon;
    public bool IsDoubleSided;
    public bool EnableEdge;

    /// <summary>本段顶点在绑定姿势下的几何中心（模型空间），用于 Blended 队列排序。</summary>
    public Vector3 Center;
}

/// <summary>
/// 运行时模型：PmxModel 经 SkeletalModelConverter 转换后的形态，引擎侧直接消费。
/// 纯数据 + 骨骼姿势计算，不含任何 GL / 平台依赖。
/// </summary>
public sealed class SkeletalModel
{
    /// <summary>
    /// 交错顶点缓冲步长（字节）。
    ///
    /// ⚠️ 与 docs 原稿的 48 不同：本仓库的 Model.pmx 有 <b>1099 根骨骼</b>，
    /// 远超 UNSIGNED_BYTE 能表达的 255，因此骨骼索引必须用 UNSIGNED_SHORT（4 × 2 = 8 字节）：
    ///   aPosition vec3   12 @0
    ///   aNormal   vec3   12 @12
    ///   aUv       vec4   16 @24   (xy=UV, z=EdgeScale, w=DeformType)
    ///   aJoints   uvec4   8 @40   (UNSIGNED_SHORT ×4)
    ///   aWeights  vec4    4 @48   (UNSIGNED_BYTE ×4, normalized)
    ///   stride = 52
    /// </summary>
    public const int VertexStride = 52;

    // ── 几何 ───────────────────────────────────────────────────────────
    public byte[] VertexData = Array.Empty<byte>();
    public int VertexCount;
    public uint[] IndexData = Array.Empty<uint>();

    // ── 材质 / 分组 ─────────────────────────────────────────────────────
    public PmxMaterial[] Materials = Array.Empty<PmxMaterial>();
    public DrawSegment[] Segments = Array.Empty<DrawSegment>();

    /// <summary>PMX 纹理表（原始相对路径，未解析成磁盘路径）。</summary>
    public string[] Textures = Array.Empty<string>();

    // ── 骨骼 ───────────────────────────────────────────────────────────
    public int BoneCount;
    public string[] BoneNames = Array.Empty<string>();

    /// <summary>保证"父先于子"的遍历顺序，供 UpdateWorldMatrices 使用。</summary>
    public int[] DeformOrder = Array.Empty<int>();

    public int[] ParentIndices = Array.Empty<int>();

    /// <summary>绑定姿势下的局部平移（= PmxBone.Position，相对父骨）。</summary>
    public Vector3[] LocalPositions = Array.Empty<Vector3>();

    /// <summary>当前局部旋转，初始为单位四元数（即绑定姿势）。</summary>
    public Quaternion[] LocalRotations = Array.Empty<Quaternion>();

    public Matrix4x4[] InverseBind = Array.Empty<Matrix4x4>();
    public Matrix4x4[] WorldMatrices = Array.Empty<Matrix4x4>();

    /// <summary>SkinMatrices[i] = WorldMatrices[i] * InverseBind[i]（行主序，可直接喂 GLSL mat4）。</summary>
    public Matrix4x4[] SkinMatrices = Array.Empty<Matrix4x4>();

    // ── 包围盒（绑定姿势，模型空间） ─────────────────────────────────────
    public Vector3 BoundsMin;
    public Vector3 BoundsMax;

    public Vector3 BoundsCenter => (BoundsMin + BoundsMax) * 0.5f;
    public Vector3 BoundsSize => BoundsMax - BoundsMin;

    /// <summary>按名字找骨骼，找不到返回 -1。</summary>
    public int FindBone(string name)
    {
        for (int i = 0; i < BoneNames.Length; i++)
            if (string.Equals(BoneNames[i], name, StringComparison.Ordinal))
                return i;
        return -1;
    }

    public void SetBoneLocalRotation(int boneIndex, Quaternion rotation)
    {
        if ((uint)boneIndex >= (uint)BoneCount) return;
        LocalRotations[boneIndex] = rotation;
    }

    public void ResetPose()
    {
        for (int i = 0; i < LocalRotations.Length; i++)
            LocalRotations[i] = Quaternion.Identity;
        UpdateWorldMatrices();
    }

    /// <summary>
    /// 由当前局部 T/R 重算世界矩阵与蒙皮矩阵。按 <see cref="DeformOrder"/> 遍历保证父先于子。
    /// </summary>
    public void UpdateWorldMatrices()
    {
        var identity = Matrix4x4.Identity;

        for (int k = 0; k < DeformOrder.Length; k++)
        {
            int i = DeformOrder[k];

            // System.Numerics 是行主序 row-vector 约定：local = R * T
            Matrix4x4 local = Matrix4x4.CreateFromQuaternion(LocalRotations[i]);
            local.Translation = LocalPositions[i];

            int parent = ParentIndices[i];
            WorldMatrices[i] = parent >= 0 ? WorldMatrices[parent] * local : local;
            SkinMatrices[i] = WorldMatrices[i] * InverseBind[i];
        }
    }
}
