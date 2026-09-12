using System.Numerics;
using MikuEngine.Core.Models;

namespace MikuEngine.Core.Animation;

/// <summary>
/// 一层动效的采样输出缓冲。多动效混合时由<b>所有层复用同一个实例</b>（不需要每层一份私有缓冲），
/// 因为采样是帧号的纯函数、每层采样前都会整体复位。
///
/// 语义约定：
/// <list type="bullet">
///   <item><see cref="Rotations"/> 是<b>局部旋转本体</b>（绑定姿势 = Identity）；</item>
///   <item><see cref="Translations"/> 是<b>相对绑定姿势的父空间偏移</b>（绑定姿势 = 0，不是绝对局部平移 ——
///         写回模型时由调用方加 <c>SkeletalModel.LocalPositions</c>）；</item>
///   <item><see cref="MorphWeights"/> 是 VMD 原始权重（无表情 = 0）。</item>
/// </list>
///
/// 未覆盖的项保持复位值，因此「层只覆盖一部分骨 / morph」这件事靠 <see cref="CoveredBones"/> /
/// <see cref="CoveredMorphs"/> 显式记录 —— 混合器要用它算「覆盖该项的权重和」（残差混回绑定姿势）。
/// </summary>
public sealed class MmdPoseBuffer
{
    /// <summary>每骨的局部旋转（复位为 Identity）。长度 = 模型的骨数。</summary>
    public readonly Quaternion[] Rotations;

    /// <summary>每骨相对绑定姿势的父空间偏移（复位为 0）。长度同 <see cref="Rotations"/>。</summary>
    public readonly Vector3[] Translations;

    /// <summary>每 morph 的原始权重（复位为 0）。长度 = 模型的 morph 数。</summary>
    public readonly float[] MorphWeights;

    /// <summary>本次采样覆盖到的骨索引（同层内不会重复：轨道按骨名聚合，一骨至多一条轨道）。</summary>
    public readonly List<int> CoveredBones = [];

    /// <summary>本次采样覆盖到的 morph 索引（同名 morph 是不同槽位，故同层内也不会重复）。</summary>
    public readonly List<int> CoveredMorphs = [];

    public MmdPoseBuffer(int boneCount, int morphCount)
    {
        Rotations = new Quaternion[boneCount];
        Translations = new Vector3[boneCount];
        MorphWeights = new float[morphCount];
        Reset();
    }

    /// <summary>
    /// 按模型尺寸建缓冲：骨数取 <c>LocalRotations</c> 长度、morph 数取 <c>MorphRawWeights</c> 长度 ——
    /// 正是 <see cref="MmdAnimation.Sample"/> 写回时逐条目覆盖的那两组数组。
    /// （<c>LocalRotations</c> / <c>LocalTranslations</c> / <c>LocalPositions</c> 三者同长，由
    /// <c>SkeletalModelConverter</c> 保证。）
    /// </summary>
    public static MmdPoseBuffer ForModel(SkeletalModel model) =>
        new(model.LocalRotations.Length, model.MorphRawWeights.Length);

    public int BoneCount => Rotations.Length;

    public int MorphCount => MorphWeights.Length;

    /// <summary>是否与给定模型的尺寸一致（供调用方判断是否需要重建缓冲）。</summary>
    public bool Matches(SkeletalModel model) =>
        BoneCount == model.LocalRotations.Length && MorphCount == model.MorphRawWeights.Length;

    /// <summary>整体复位到「绑定姿势 + 无表情」并清空覆盖表。</summary>
    public void Reset()
    {
        Array.Fill(Rotations, Quaternion.Identity);
        Array.Fill(Translations, Vector3.Zero);
        Array.Fill(MorphWeights, 0f);
        CoveredBones.Clear();
        CoveredMorphs.Clear();
    }
}
