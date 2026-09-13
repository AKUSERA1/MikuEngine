using System.Numerics;

namespace MikuEngine.Core.Models;

/// <summary>
/// IK link 的 Euler 分解顺序。**不是 PMX 文件里的字段** —— PMX 只存角度范围的 min/max，
/// 顺序由 <see cref="MmdIkChainBuilder.DeriveEulerOrder"/> 按 PmxEditor 的规则推导
/// （PmxLib <c>IKLink.NormalizeEulerAxis</c>：X 范围在 ±90° 内 → ZXY；否则 Y 在 ±90° 内 → XYZ；否则 YZX）。
/// </summary>
public enum MmdIkEulerOrder : byte
{
    Zxy = 0,
    Xyz = 1,
    Yzx = 2,
}

/// <summary>
/// IK link 的固定轴。同样是**推导**出来的（PmxLib <c>NormalizeEulerAxis</c> 同一函数）：
/// 六个角度分量全为 0 → <see cref="Fix"/>；三个轴中只有两个恒为 0 → 单轴可动；否则 <see cref="None"/>。
/// </summary>
public enum MmdIkFixAxis : byte
{
    /// <summary>自由轴（无角度限制，或限制不构成单轴）。</summary>
    None = 0,

    /// <summary>六分量全 0 ⇒ 完全锁死。PmxEditor 的 <c>Transform()</c> 直接跳过该 link（不旋转）。</summary>
    Fix = 1,

    X = 2,
    Y = 3,
    Z = 4,
}

/// <summary>IK 链的一节（受 IK 影响的骨骼）。</summary>
public readonly struct MmdIkLink
{
    /// <summary>链骨索引。</summary>
    public required int BoneIndex { get; init; }

    /// <summary>PMX 是否声明了角度限制。</summary>
    public required bool HasLimitation { get; init; }

    /// <summary>角度限制下界（逐分量已排序为 min，弧度）。</summary>
    public required Vector3 MinAngle { get; init; }

    /// <summary>角度限制上界（逐分量已排序为 max，弧度）。</summary>
    public required Vector3 MaxAngle { get; init; }

    /// <summary>推导出的 Euler 分解顺序。</summary>
    public required MmdIkEulerOrder EulerOrder { get; init; }

    /// <summary>推导出的固定轴。</summary>
    public required MmdIkFixAxis FixAxis { get; init; }
}

/// <summary>
/// 运行时 IK 链。
///
/// <b>角色语义</b>（这一条搞反会直接导致"脚永远到不了地面"）：
/// <list type="bullet">
/// <item><b>目标（冻结）</b>= 带 IK 标志的那根骨（如 <c>左足ＩＫ</c>）。世界坐标在迭代前取一次，全程不刷新。</item>
/// <item><b>被驱动端（活跃）</b>= PMX <c>Ik.Target</c> 字段所指的骨（如 <c>左足首</c>）。每步 link 更新后刷新。</item>
/// </list>
/// PMX 的 <c>Target</c> 字段指的是"被驱动端"，不是目标。参考 PmxEditor <c>IKTransform.Transform()</c>：
/// <c>m_ikPosition</c>（IK 骨）只取一次，<c>m_targetPosition</c> 在每次 <c>CalcBonePosition_Link</c> 刷新。
///
/// <b>链序</b>：<see cref="Links"/> 保持 PMX 文件顺序 = **末端侧 → 根**
/// （reze-engine <c>model.ts</c>：<c>links: IKLink[] // Chain bones from effector to root</c>）。
/// 因此每轮转角上限 <c>LimitAngle * (index + 1)</c> 会让越靠根的骨获得越大的预算
/// （足 IK 的 <c>[ひざ, 足]</c> ⇒ 大腿 2× > 小腿 1×），与解剖直觉一致。
/// </summary>
public sealed class MmdIkChain
{
    /// <summary>带 IK 标志的骨 —— 目标（冻结）。</summary>
    public required int Goal { get; init; }

    /// <summary>PMX <c>Ik.Target</c> 所指的骨 —— 被驱动端（活跃）。</summary>
    public required int Driven { get; init; }

    /// <summary>迭代次数上限（按 PmxEditor 钳到 256）。</summary>
    public required int Iteration { get; init; }

    /// <summary>每轮转角基准（PMX 的 rotationConstraint，弧度）。</summary>
    public required float LimitAngle { get; init; }

    /// <summary>链骨，**顺序 = PMX 文件顺序 = 末端侧 → 根**（不要反转）。</summary>
    public required MmdIkLink[] Links { get; init; }
}
