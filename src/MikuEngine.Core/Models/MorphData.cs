using System.Numerics;

namespace MikuEngine.Core.Models;

/// <summary>
/// 顶点 morph 的稀疏数据：只保留<b>受影响的顶点</b>（PMX 源数据本就是稀疏的，
/// 这里只是把「索引数组 + 扁平 xyz」重排成对齐的两组数组，<b>不做任何稠密化</b>）。
///
/// 与 babylon-mmd 的分野：它给每条 morph 复制一份与顶点数等长的稠密 array，
/// 内存 O(顶点数 × morph 数)；本引擎全程稀疏，内存 O(受影响顶点数)。
/// </summary>
public struct VertexMorphSparse
{
    /// <summary>该 morph 在 <see cref="SkeletalModel.MorphWeights"/> 里的下标。</summary>
    public int MorphIndex;

    /// <summary>受影响的顶点索引（已过滤越界项）。</summary>
    public int[] VertexIndices;

    /// <summary>与 <see cref="VertexIndices"/> 等长的模型空间偏移。</summary>
    public Vector3[] Offsets;
}

/// <summary>主 UV（UV 通道 0）morph 的稀疏数据。附加 UV1~4 不在支持范围内。</summary>
public struct UvMorphSparse
{
    public int MorphIndex;
    public int[] VertexIndices;

    /// <summary>PMX 里偏移是 vec4，本引擎只用 xy（z/w 属于附加 UV 语义）。</summary>
    public Vector2[] Offsets;
}

/// <summary>骨 morph 的稀疏数据：对目标骨叠加父空间平移与局部旋转。</summary>
public struct BoneMorphSparse
{
    public int MorphIndex;
    public int[] BoneIndices;
    public Vector3[] Translations;   // 与 BoneIndices 等长
    public Quaternion[] Rotations;   // 与 BoneIndices 等长
}

/// <summary>
/// Group（组）morph 的引用表。求值序见 <see cref="SkeletalModel.GroupOrder"/>。
/// 注意：Flip（PMX 2.1 类型 9）与 Group 文件布局相同，但语义不同，本步<b>不支持</b>，
/// 因此不会出现在本表中（见 docs/2026-09-11-anim-morph-plan.md §0.2）。
/// </summary>
public struct GroupMorphSparse
{
    public int MorphIndex;
    public int[] ChildIndices;   // 被引用的 morph 索引（已过滤越界项）
    public float[] Ratios;       // 与 ChildIndices 等长；PMX 允许负值
}

/// <summary>
/// 材质 morph 混合后的有效材质值（叠加在所有材质 morph 之上）。
/// 供渲染层直接逐字段改 uniform，Core 侧可无 GL 单测。
/// </summary>
public struct MaterialState
{
    public Vector4 Diffuse;        // 含 alpha（MMD 非透过度）
    public Vector3 Specular;
    public float Shininess;
    public Vector3 Ambient;
    public Vector4 EdgeColor;
    public float EdgeSize;

    /// <summary>主纹理色调系数（乘算）；权重为 0 时恒为 <see cref="Vector4.One"/>。</summary>
    public Vector4 TextureColor;

    /// <summary>球面纹理色调系数（乘算）。</summary>
    public Vector4 SphereColor;

    /// <summary>卡通纹理色调系数（乘算）。</summary>
    public Vector4 ToonColor;
}
