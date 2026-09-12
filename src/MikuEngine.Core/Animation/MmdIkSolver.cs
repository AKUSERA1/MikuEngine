using System.Numerics;
using MikuEngine.Core.Models;

namespace MikuEngine.Core.Animation;

/// <summary>单条 IK 链的求解结果（诊断用，不参与逻辑）。</summary>
public readonly record struct IkChainSolveResult(bool Solved, int IterationsUsed, float ErrorSquared);

/// <summary>
/// MMD 风格 CCD IK 求解器 —— 逐式移植 PmxEditor 的 <c>IKTransform</c>
/// （<c>reference/PmxEditor/decompiled/PmxEditorLib/PmxEditorLib.decompiled.cs:74675-75010</c>）。
///
/// 移植纪律（见 docs/2026-09-12-mmd-ik-design.md §10 / 附录 D）：
/// <list type="bullet">
/// <item>公式<b>逐行照抄</b>，不做"等价改写"；行号在注释里，便于回查。</item>
/// <item>坐标系与乘序：本引擎 = 左手 Z-forward + <b>行向量</b>（<c>v * M</c>），与 PmxEditor 的
/// SlimDX/D3D9 <b>同族</b> ⇒ 交叉积顺序、四元数乘序、<c>M11..M44</c> 元素索引都可直接沿用。</item>
/// <item>**角色语义**：目标（带 IK 标志的骨）<b>冻结</b>，被驱动端（PMX <c>Ik.Target</c>）<b>每步刷新</b>。
/// 这一条搞反会直接表现为"脚永远到不了地面"。</item>
/// <item>**链序**：<c>Links</c> 保持 PMX 文件顺序 = 末端侧 → 根；<c>LimitAngle * (index + 1)</c>
/// 因此让越靠根的骨获得越大预算（大腿 &gt; 小腿），与解剖直觉一致。</item>
/// <item>无跨帧状态：每次 <see cref="Solve"/> 先整体复位 <c>IkRotations</c>
/// （PmxEditor <c>InitializeAngle()</c>），保证"连续播放到帧 N ≡ 直接 seek 到帧 N"。</item>
/// </list>
///
/// 未实现（记录在案）：PMX 的足 IK 在 MMD 本体走私有解析式；本实现与 PmxEditor 一致，
/// <b>把足 IK 当普通 CCD 链解</b>，不做按骨名特判。MMD 本体的通用 CCD 私有公式
/// （<c>0.5·asin(本轴投影)</c>、FK 播种、膝骨名特判）也未实现 —— 均为后续可选项。
/// </summary>
public static class MmdIkSolver
{
    /// <summary>收敛判据：目标与被驱动端的距离平方（PmxEditor <c>1E-08f</c>）。</summary>
    public const float ConvergenceEpsilonSq = 1e-8f;

    /// <summary>退化轴容差（叉积长度平方）。PmxEditor/babylon/reze 同量级。</summary>
    private const float AxisEpsilonSq = 1e-8f;

    /// <summary>
    /// Euler 分解的奇异值钳位 <c>1.535889</c> rad（±88°）。PmxEditor 与 MMD 本体
    /// （<c>0x530F40/0x530F44</c>）同值 —— "接近垂直时锁轴避免 gimbal 翻转"。
    /// </summary>
    private const float SingularClamp = 1.535889f;

    private static readonly Vector3 UnitX = new(1f, 0f, 0f);
    private static readonly Vector3 UnitY = new(0f, 1f, 0f);
    private static readonly Vector3 UnitZ = new(0f, 0f, 1f);

    /// <summary>
    /// 诊断开关（环境变量 <c>MIKU_IK_NO_LIMIT=1</c>）：忽略全部角度限制与固定轴量化。
    /// 用途是把"轴 / 每轮转角公式"与"Euler 限位路径"两类差异二分定位
    /// （对拍 reze 时同步用 <c>IK_NO_LIMIT=1</c>）。默认关闭。
    /// </summary>
    public static readonly bool IgnoreLimitations =
        System.Environment.GetEnvironmentVariable("MIKU_IK_NO_LIMIT") == "1";

    // ── F3（设计文档附录 F）：angleDot<0 反向钳 ────────────────────────────
    // 目标落在反向半球（toTarget·toIk < 0）时，acos 给出 90°~180° 的巨大单步转角，
    // CCD 会试图一步把腿甩过去（"腿被吸走/翻转"）。MMD 解析式从不提议越界转角，
    // 该钳把反向一步封顶到 LimitAngle（去掉 (linkIndex+1) 的逐级放大）。
    // 来源：修改版 CCDIKSolver.js 独有改动 #7；env: MIKU_IK_NO_REVERSE_CLAMP=1 关闭（A/B 对照）。
    public static readonly bool ReverseClamp =
        System.Environment.GetEnvironmentVariable("MIKU_IK_NO_REVERSE_CLAMP") != "1";




    /// <summary>
    /// 求解全部 IK 链。必须在「动画已写入局部 T/R（含骨 morph）」之后、
    /// 「最后一次 <see cref="SkeletalModel.UpdateWorldMatrices"/>」之前调用。
    ///
    /// 内部先跑一次全量 <c>UpdateWorldMatrices</c> 拿到 FK 世界矩阵并导出链骨基旋转，
    /// 迭代中只在链骨子树上增量重算，结束后由调用方再跑一次全量更新。
    /// </summary>
    public static void Solve(SkeletalModel model, List<IkChainSolveResult>? results = null)
    {
        results?.Clear();

        // 无论开关如何都要保证 IkRotations 干净：渲染路径会无条件读它。
        var ikRotations = model.IkRotations;
        for (int i = 0; i < ikRotations.Length; i++)
            ikRotations[i] = Quaternion.Identity;

        if (!model.IkSolverEnabled || model.IkChains.Length == 0) return;

        // FK 世界矩阵 + 导出 IkLinkBaseRotations（"付与之后、IK 之前"的旋转）。
        model.UpdateWorldMatrices();

        // 注意：不要写成 `results?.Add(SolveChain(...))` —— 空条件运算符在 results 为 null 时
        // **连实参都不求值**，会把整个求解过程静默跳掉（渲染路径不传 results，症状是"IK 完全没效果"）。
        for (int c = 0; c < model.IkChains.Length; c++)
        {
            var result = SolveChain(model, model.IkChains[c], c);
            results?.Add(result);
        }
    }

    /// <summary>
    /// 单条链。<c>PmxEditorLib:74782 Transform()</c> 的结构：
    /// 复位 → 取目标（冻结）→ 收敛预检 → 预推进链 → 迭代 { 逐 link → 收敛判据 }。
    /// </summary>
    private static IkChainSolveResult SolveChain(SkeletalModel model, MmdIkChain chain, int chainIndex)
    {
        var links = chain.Links;
        if (links.Length == 0 || !model.IkEnabled[chainIndex])
            return new IkChainSolveResult(false, 0, 0f);

        // 目标（冻结）：带 IK 标志的骨。**只取一次**（PmxEditor L74789 m_ikPosition）。
        // 该骨通常是链骨的后代 ⇒ 迭代中它的世界矩阵会跟着腿动，但我们必须继续用这一份快照。
        Vector3 goalPos = model.WorldMatrices[chain.Goal].Translation;

        // 预推进整条链（PmxEditor L74796 CalcBonePosition(Link.Length - 1)）：链骨的世界矩阵
        // 可能还停在上一次求解/绑定姿势，必须先刷。
        model.UpdateWorldMatricesSubtree(links[^1].BoneIndex);
        float err = Vector3.DistanceSquared(goalPos, model.WorldMatrices[chain.Driven].Translation);
        if (err < ConvergenceEpsilonSq) return new IkChainSolveResult(true, 0, err);

        int loop = chain.Iteration;
        int half = loop >> 1;          // PMX 的 RotationConstraint 只在【前半段迭代】生效
        int used = 0;

        for (int it = 0; it < loop; it++)
        {
            for (int i = 0; i < links.Length; i++)
            {
                // PmxEditor L74806-74812: `if (!m_fixAxis[j]) IKProc_Link(j, i < num);`
                // 六分量全零 ⇒ 完全锁死 ⇒ 跳过（与 babylon/reze 的 SolveAxis.Fixed → skip 同义）。
                // m_fixAxis 只在有角度限制时才可能置位，故必须与 HasLimitation 同时判断。
                if (Limited(links[i]) && links[i].FixAxis == MmdIkFixAxis.Fix) continue;
                SolveLink(model, chain, goalPos, i, it < half);
            }

            used = it + 1;
            // PmxEditor L74813：收敛判据在【链循环之后】，不在 link 循环内。
            err = Vector3.DistanceSquared(goalPos, model.WorldMatrices[chain.Driven].Translation);
            if (err < ConvergenceEpsilonSq) break;
        }

        return new IkChainSolveResult(true, used, err);
    }


    /// <summary>
    /// 单根链骨（<c>PmxEditorLib:74820 IKProc_Link</c>）。
    ///
    /// 每轮转角上限 <c>LimitOnce * (linkNum + 1)</c>；<paramref name="axisLim"/>（前半段迭代）
    /// 只控制两件事：固定轴的量化投影、以及角度回弹是否允许"反射"——
    /// 硬钳位到 [min, max] <b>每轮都做</b>，不受它影响。
    /// </summary>
    private static void SolveLink(SkeletalModel model, MmdIkChain chain, Vector3 goalPos, int linkIndex, bool axisLim)
    {
        var link = chain.Links[linkIndex];
        int bone = link.BoneIndex;

        Vector3 pos = model.WorldMatrices[bone].Translation;

        // 与 PmxEditor 同式：left/right 都从链骨【指向】目标 / 效应器（因此等于 -(目标向)/-(效应器向)）。
        Vector3 toTarget = pos - model.WorldMatrices[chain.Driven].Translation;   // 指向被驱动端（活跃）
        Vector3 toIk = pos - goalPos;                                            // 指向目标（冻结）

        float lenTarget = toTarget.Length();
        float lenIk = toIk.Length();
        if (lenTarget < 1e-6f || lenIk < 1e-6f) return;
        toTarget /= lenTarget;
        toIk /= lenIk;

        Vector3 axis = Vector3.Cross(toTarget, toIk);
        float axisLen2 = axis.LengthSquared();
        if (axisLen2 < AxisEpsilonSq) return;      // 三点共线 ⇒ 这个 link 无需旋转
        axis /= MathF.Sqrt(axisLen2);

        // 父骨的世界旋转（去平移）；转置 = 逆旋转，用于把轴变到父旋转系。
        // PmxEditor L74829-74832 用的正是父骨的累积矩阵（Parent.LocalMatrix）。
        int parent = model.ParentIndices[bone];
        Matrix4x4 parentWorld = parent >= 0 ? model.WorldMatrices[parent] : Matrix4x4.Identity;
        Matrix4x4 parentRot = parentWorld;
        parentRot.M41 = parentRot.M42 = parentRot.M43 = 0f;
        Matrix4x4 invParent = Matrix4x4.Transpose(parentRot);

        bool limited = Limited(link);
        if (limited && axisLim)
        {
            // 固定轴：把自由轴量化成 ±父旋转系基轴（PmxEditor L74833-74866）。
            // 注意行向量约定下 M 的第 k 行就是基向量 e_k 的像，故用 Dot(axis, rowK) 定符号。
            switch (link.FixAxis)
            {
                case MmdIkFixAxis.X:
                {
                    float d = Vector3.Dot(axis, new Vector3(parentWorld.M11, parentWorld.M12, parentWorld.M13));
                    axis = new Vector3(d >= 0f ? 1f : -1f, 0f, 0f);
                    break;
                }
                case MmdIkFixAxis.Y:
                {
                    float d = Vector3.Dot(axis, new Vector3(parentWorld.M21, parentWorld.M22, parentWorld.M23));
                    axis = new Vector3(0f, d >= 0f ? 1f : -1f, 0f);
                    break;
                }
                case MmdIkFixAxis.Z:
                {
                    float d = Vector3.Dot(axis, new Vector3(parentWorld.M31, parentWorld.M32, parentWorld.M33));
                    axis = new Vector3(0f, 0f, d >= 0f ? 1f : -1f);
                    break;
                }
                default:
                    axis = Vector3.Normalize(Vector3.TransformNormal(axis, invParent));
                    break;
            }
        }
        else
        {
            axis = Vector3.Normalize(Vector3.TransformNormal(axis, invParent));
        }

        if (axis.LengthSquared() < AxisEpsilonSq) return;

        // F3 反向钳：目标在反向半球时封顶到 LimitAngle（去掉逐级放大）。
        // 注意必须在 clamp 前取原始 dot 判断半球；正常半球保持 PE 的 (linkIndex+1) 预算不变。
        float angleDot = Vector3.Dot(toTarget, toIk);
        float dot = System.Math.Clamp(angleDot, -1f, 1f);
        float budget = chain.LimitAngle * (linkIndex + 1);
        if (ReverseClamp && angleDot < 0f)
            budget = System.Math.Min(budget, chain.LimitAngle);
        float angle = MathF.Min(MathF.Acos(dot), budget);
        model.IkRotations[bone] *= Quaternion.CreateFromAxisAngle(axis, angle);

        if (limited)
        {
            // PmxEditor L74887-74964：在 `base * IKRotation` 上做欧拉分解 → 硬钳/反射回弹 → 重建。
            Quaternion basis = model.IkLinkBaseRotations[bone];
            Matrix4x4 m = Matrix4x4.CreateFromQuaternion(basis * model.IkRotations[bone]);
            Vector3 euler = ExtractEuler(m, link.EulerOrder);
            LimitAngles(ref euler, link.MinAngle, link.MaxAngle, axisLim);
            model.IkRotations[bone] = Quaternion.Inverse(basis) * Rebuild(euler, link.EulerOrder);
        }

        // PmxEditor L74965 CalcBonePosition_Link(linkNum)：立刻推进，供下个 link / 收敛判据读。
        model.UpdateWorldMatricesSubtree(bone);
    }

    /// <summary>
    /// 欧拉分解（<c>PmxEditorLib:74891-74961</c> 的三档）。
    /// 奇异轴用 <c>asin</c> 后钳到 ±88°；<c>cos == 0</c> 时倒数取 <b>0</b>（不是 1/0）——
    /// PmxEditor 的退化处理是 <c>if (num8 != 0f) num8 = 1f / num8;</c>，照抄。
    /// </summary>
    private static Vector3 ExtractEuler(Matrix4x4 m, MmdIkEulerOrder order)
    {
        Vector3 e = Vector3.Zero;
        switch (order)
        {
            case MmdIkEulerOrder.Zxy:
            {
                e.X = ClampSingular(MathF.Asin(System.Math.Clamp(-m.M32, -1f, 1f)));
                float inv = MathF.Cos(e.X);
                if (inv != 0f) inv = 1f / inv; else inv = 0f;
                e.Y = MathF.Atan2(m.M31 * inv, m.M33 * inv);
                e.Z = MathF.Atan2(m.M12 * inv, m.M22 * inv);
                break;
            }
            case MmdIkEulerOrder.Xyz:
            {
                e.Y = ClampSingular(MathF.Asin(System.Math.Clamp(-m.M13, -1f, 1f)));
                float inv = MathF.Cos(e.Y);
                if (inv != 0f) inv = 1f / inv; else inv = 0f;
                e.X = MathF.Atan2(m.M23 * inv, m.M33 * inv);
                e.Z = MathF.Atan2(m.M12 * inv, m.M11 * inv);
                break;
            }
            default:
            {
                e.Z = ClampSingular(MathF.Asin(System.Math.Clamp(-m.M21, -1f, 1f)));
                float inv = MathF.Cos(e.Z);
                if (inv != 0f) inv = 1f / inv; else inv = 0f;
                e.X = MathF.Atan2(m.M23 * inv, m.M22 * inv);
                e.Y = MathF.Atan2(m.M31 * inv, m.M11 * inv);
                break;
            }
        }
        return e;
    }

    /// <summary>该 link 当前是否受角度限制（含 <see cref="IgnoreLimitations"/> 诊断开关）。</summary>
    private static bool Limited(in MmdIkLink link) => link.HasLimitation && !IgnoreLimitations;

    private static float ClampSingular(float angle)
        => MathF.Abs(angle) > SingularClamp ? (angle < 0f ? -SingularClamp : SingularClamp) : angle;

    /// <summary>欧拉角重建（<c>PmxEditorLib:74913/74936/74959</c> 的三个乘序）。</summary>
    private static Quaternion Rebuild(Vector3 e, MmdIkEulerOrder order) => order switch
    {
        MmdIkEulerOrder.Zxy =>
            Quaternion.CreateFromAxisAngle(UnitZ, e.Z) *
            Quaternion.CreateFromAxisAngle(UnitX, e.X) *
            Quaternion.CreateFromAxisAngle(UnitY, e.Y),
        MmdIkEulerOrder.Xyz =>
            Quaternion.CreateFromAxisAngle(UnitX, e.X) *
            Quaternion.CreateFromAxisAngle(UnitY, e.Y) *
            Quaternion.CreateFromAxisAngle(UnitZ, e.Z),
        _ =>
            Quaternion.CreateFromAxisAngle(UnitY, e.Y) *
            Quaternion.CreateFromAxisAngle(UnitZ, e.Z) *
            Quaternion.CreateFromAxisAngle(UnitX, e.X),
    };

    /// <summary>
    /// 角度限制（<c>PmxEditorLib:74968 LimitAngle</c>）：越界时用 <c>2*bound − angle</c> 反射回弹，
    /// 仅当前半段迭代（<paramref name="axisLim"/>）允许回弹；否则硬钳到边界。
    /// </summary>
    private static void LimitAngles(ref Vector3 e, Vector3 low, Vector3 high, bool axisLim)
    {

        if (e.X < low.X)
        {
            float reflect = 2f * low.X - e.X;
            e.X = (reflect <= high.X && axisLim) ? reflect : low.X;
        }
        else if (e.X > high.X)
        {
            float reflect = 2f * high.X - e.X;
            e.X = (reflect >= low.X && axisLim) ? reflect : high.X;
        }

        if (e.Y < low.Y)
        {
            float reflect = 2f * low.Y - e.Y;
            e.Y = (reflect <= high.Y && axisLim) ? reflect : low.Y;
        }
        else if (e.Y > high.Y)
        {
            float reflect = 2f * high.Y - e.Y;
            e.Y = (reflect >= low.Y && axisLim) ? reflect : high.Y;
        }

        if (e.Z < low.Z)
        {
            float reflect = 2f * low.Z - e.Z;
            e.Z = (reflect <= high.Z && axisLim) ? reflect : low.Z;
        }
        else if (e.Z > high.Z)
        {
            float reflect = 2f * high.Z - e.Z;
            e.Z = (reflect >= low.Z && axisLim) ? reflect : high.Z;
        }
    }
}
