using System.Numerics;

namespace MikuEngine.Core.Models;

/// <summary>
/// PMX 的 IK 定义 → 运行时 <see cref="MmdIkChain"/>。
///
/// 这里做的是 PmxEditor <c>IKTransform.Initialize</c> 里那一步"从角度范围推导约束"的移植：
/// <c>PmxLink[i].NormalizeAngle()</c> + <c>NormalizeEulerAxis()</c>（<c>PmxLib:3014-3063</c>）。
/// 关键点：**PMX 文件里没有 Euler 顺序字段**（<c>IKLink.FromStreamEx</c> 只读 Bone/IsLimit/Low/High），
/// 顺序与固定轴都是推出来的。babylon-mmd / reze-engine 用同一规则（只是命名为 YXZ/ZYX/XZY）。
/// </summary>
public static class MmdIkChainBuilder
{
    /// <summary>±90°。Euler 顺序与固定轴的判定阈值（PmxEditor 用 ±π/2）。</summary>
    private const float HalfPi = System.MathF.PI * 0.5f;

    /// <summary>
    /// 构建全部 IK 链。返回顺序 = PMX 骨骼顺序（与 PmxEditor 的遍历一致）。
    /// 目标骨或链骨越界的条目会被跳过（坏文件保护）。
    /// </summary>
    public static MmdIkChain[] Build(PmxBone[] bones)
    {
        if (bones.Length == 0) return [];

        var chains = new List<MmdIkChain>();
        for (int i = 0; i < bones.Length; i++)
        {
            if (bones[i].Ik is not { } ik) continue;

            // PMX 的 `Target` 字段是【被驱动端】（如 左足首），带 IK 标志的 i 才是目标（左足ＩＫ）。
            if ((uint)ik.Target >= (uint)bones.Length) continue;

            var links = new List<MmdIkLink>(ik.Links.Length);
            foreach (var l in ik.Links)
            {
                if ((uint)l.BoneIndex >= (uint)bones.Length) continue;

                // 逐分量排序（PmxEditor NormalizeAngle）：PMX 里上下界写反的模型确实存在。
                var low = Vector3.Min(l.MinimumAngle, l.MaximumAngle);
                var high = Vector3.Max(l.MinimumAngle, l.MaximumAngle);

                // 顺序与固定轴**只在 IsLimit 时**才推导 —— PmxEditor 的 `NormalizeEulerAxis()` 调用
                // 就在 `if (PmxLink[i].IsLimit)` 分支内（PmxEditorLib:74730-74739）。
                // 若对无限制的 link 也推导，全零的 (0,0,0) 会得到 FixAxis.Fix ⇒ 被当成"完全锁死"跳过，
                // 整条腿只剩膝能动（实测症状：足 IK 末端误差只降 15%，右腿因膝关节反向被钳成 0 而完全不动）。
                links.Add(new MmdIkLink
                {
                    BoneIndex = l.BoneIndex,
                    HasLimitation = l.HasLimitation,
                    MinAngle = low,
                    MaxAngle = high,
                    EulerOrder = l.HasLimitation ? DeriveEulerOrder(low, high) : MmdIkEulerOrder.Zxy,
                    FixAxis = l.HasLimitation ? DeriveFixAxis(low, high) : MmdIkFixAxis.None,
                });
            }

            if (links.Count == 0) continue;   // PmxEditor: Link.Length == 0 时直接不求解

            chains.Add(new MmdIkChain
            {
                Goal = i,
                Driven = ik.Target,
                Iteration = System.Math.Min(ik.Iteration, 256),   // PmxEditor: Math.Min(iK.LoopCount, 256)
                LimitAngle = ik.RotationConstraint,
                Links = links.ToArray(),
            });
        }
        return chains.ToArray();
    }

    /// <summary>
    /// Euler 分解顺序推导（PmxLib <c>NormalizeEulerAxis</c> L3032-3045）。
    /// 判据是"哪个轴的角度范围落在 ±90° 内"—— 那个轴在分解时是可安全 asin 的轴。
    /// </summary>
    public static MmdIkEulerOrder DeriveEulerOrder(Vector3 low, Vector3 high)
    {
        if (-HalfPi < low.X && high.X < HalfPi) return MmdIkEulerOrder.Zxy;
        if (-HalfPi < low.Y && high.Y < HalfPi) return MmdIkEulerOrder.Xyz;
        return MmdIkEulerOrder.Yzx;
    }

    /// <summary>
    /// 固定轴推导（同一函数 L3046-3063）。
    /// 六个分量全为 0 ⇒ <see cref="MmdIkFixAxis.Fix"/>（完全锁死，求解时跳过该 link）；
    /// 只有两个轴恒为 0 ⇒ 剩下那个轴是唯一可动轴；
    /// 否则 <see cref="MmdIkFixAxis.None"/>（自由轴）。
    /// </summary>
    public static MmdIkFixAxis DeriveFixAxis(Vector3 low, Vector3 high)
    {
        bool xZero = low.X == 0f && high.X == 0f;
        bool yZero = low.Y == 0f && high.Y == 0f;
        bool zZero = low.Z == 0f && high.Z == 0f;

        if (xZero && yZero && zZero) return MmdIkFixAxis.Fix;
        if (yZero && zZero) return MmdIkFixAxis.X;
        if (xZero && zZero) return MmdIkFixAxis.Y;
        if (xZero && yZero) return MmdIkFixAxis.Z;
        return MmdIkFixAxis.None;
    }
}
