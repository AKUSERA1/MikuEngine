using System.Numerics;
using MikuEngine.Core.Models;

namespace MikuEngine.Core.Animation;

/// <summary>
/// 多动画混合器。
///
/// 每帧对时间轴帧号做一次 <see cref="Evaluate"/>：
/// <list type="number">
///   <item>收集活跃层（<see cref="MmdAnimationLayer.IsActive"/>：Weight > 0 且落在活跃区间内）；</item>
///   <item>逐层 <see cref="MmdAnimation.SampleInto"/> 到<b>共享 scratch</b>（所有层复用同一实例）；</item>
///   <item>逐项「覆盖权重和」归一 + 残差混回绑定姿势；</item>
///   <item>可见性：逐活跃层取阶梯布尔，AND 合并写回 <see cref="SkeletalModel.Visible"/>；</item>
///   <item>整体写回局部 T/R 与原始 morph 权重（含未覆盖项）⇒ 帧号纯函数、幂等、seek 等价连续播放。</item>
/// </list>
///
/// 与参考实现的取舍：旋转用 reze 的半球对齐 nlerp（顺序无关）；
/// 残差按<b>覆盖该项的权重和</b>混回绑定（不是全局权重）；可见性<b>不</b>做数值加权
/// （规避 babylon-mmd 的 0.5 半透明 bug），布尔 AND。
///
/// 权重归一用<b>逐项</b>归一 —— 仅当同一骨 / morph 上的覆盖权重和 W(item) > 1
/// 时才除以 W(item)。层间竞争（多个层驱动同一项）时与 babylon / reze 的全局归一严格等价
/// （那正是两家的设计场景：同一条动画的重叠 span）；而 MMD 的「モーション槽 + 表情槽」各层
/// 轨道互不重叠，全局归一会把四层各压到 1/4 强度 —— 与 MMD 的并集行为相悖。
/// </summary>
public sealed class MmdAnimationMixer
{
    private readonly List<MmdAnimationLayer> _layers = [];

    /// <summary>全部层（含当前不活跃的）。顺序即累加顺序。</summary>
    public IReadOnlyList<MmdAnimationLayer> Layers => _layers;

    /// <summary>本帧可见性合并结果（活跃层阶梯布尔的 AND；无活跃层 ⇒ true）。</summary>
    public bool Visible { get; private set; } = true;

    /// <summary>添加一层并返回它（便于链式设置 Offset / Weight 等）。</summary>
    public MmdAnimationLayer AddLayer(MmdAnimationLayer layer)
    {
        _layers.Add(layer);
        return layer;
    }

    /// <summary>
    /// 移除一层，返回是否确实存在并被移除。
    ///
    /// 下一帧 <see cref="Evaluate"/> 时该层的轨道即成为「未覆盖项」自动写回绑定姿势 / morph 0、
    /// 可见性 AND 投票自动少一票 —— 整体写回语义保证无残留，无需任何手动清理。
    /// </summary>
    public bool RemoveLayer(MmdAnimationLayer layer) => _layers.Remove(layer);

    /// <summary>移除全部层（语义同 <see cref="RemoveLayer"/>：下一帧整模型回绑定姿势、恒可见）。</summary>
    public void ClearLayers() => _layers.Clear();

    /// <summary>
    /// 各层活跃区间 <c>[ActiveStart, ActiveEnd]</c> 的并集（时间轴帧）。
    /// 无任何层 / 全部区间倒置时返回 <c>(0, 0)</c>。
    ///
    /// 不按当前 Weight 过滤：权重是运行时可变的，区间是层的静态属性 ——
    /// 播放器用它定时间轴范围（空档由「未激活层不贡献」兜底，见 3.1）。
    /// </summary>
    public (double Start, double End) ActiveRange
    {
        get
        {
            double min = double.MaxValue, max = double.MinValue;
            foreach (var layer in _layers)
            {
                if (layer.TrimEnd < layer.TrimStart) continue;      // 区间倒置 ⇒ 不进并集
                if (layer.ActiveStart < min) min = layer.ActiveStart;
                if (layer.ActiveEnd > max) max = layer.ActiveEnd;
            }

            return max >= min ? (min, max) : (0, 0);
        }
    }

    /// <summary>所有层复用的采样 scratch（层间不需要私有缓冲：采样是帧号的纯函数）。</summary>
    private MmdPoseBuffer? _scratch;

    // 累加器（与模型同尺寸；Evaluate 开头按需重建）
    private Quaternion[]? _rotAcc;
    private Vector3[]? _transAcc;
    private float[]? _morphAcc;
    private float[]? _boneWeightAcc;    // 每骨「覆盖该项的 w_eff 和」（逐项归一 + 残差用）
    private int[]? _boneCoverCount;     // 每骨被几层覆盖（单层满权直通，保证与单动效位级一致）
    private float[]? _morphWeightAcc;   // 每 morph「覆盖该项的 w_eff 和」（同上）

    /// <summary>
    /// 在时间轴帧 <paramref name="timelineFrame"/> 处求值并写回模型。
    /// 帧号的纯函数：不依赖也不保留上一帧状态，同一帧重复调用结果一致。
    /// </summary>
    public void Evaluate(SkeletalModel model, double timelineFrame)
    {
        int boneCount = model.LocalRotations.Length;
        int morphCount = model.MorphRawWeights.Length;

        EnsureCapacity(boneCount, morphCount);

        var rotAcc = _rotAcc!;
        var transAcc = _transAcc!;
        var morphAcc = _morphAcc!;
        var boneW = _boneWeightAcc!;
        var boneN = _boneCoverCount!;
        var morphW = _morphWeightAcc!;

        // 累加器的中性元是「零」（不是绑定姿势！）：Identity 填充会把贡献叠成 2 倍；
        // 绑定姿势由写回时的残差项 (1 − W)·Identity 统一补足。
        Array.Fill(rotAcc, default);
        Array.Fill(transAcc, Vector3.Zero);
        Array.Fill(morphAcc, 0f);
        Array.Fill(boneW, 0f);
        Array.Fill(boneN, 0);
        Array.Fill(morphW, 0f);

        // ── 逐层采样 + 累加 ─────────────────────────────────────────
        var scratch = _scratch!;
        bool visible = true;
        int activeCount = 0;

        for (int i = 0; i < _layers.Count; i++)
        {
            var layer = _layers[i];
            if (!layer.IsActive(timelineFrame)) continue;
            activeCount++;

            double local = layer.ToLocal(timelineFrame);
            float wEff = layer.Weight * layer.FadeAt(timelineFrame);

            layer.Animation.SampleInto(local, scratch);

            var coveredBones = scratch.CoveredBones;
            var rotations = scratch.Rotations;
            var translations = scratch.Translations;

            for (int k = 0; k < coveredBones.Count; k++)
            {
                int b = coveredBones[k];

                // 半球对齐（reze nlerp）：与已累计方向 dot < 0 取负，走最短弧。
                // 首项（boneN == 0）没有已累计方向，不翻转。
                var q = rotations[b];
                var acc = rotAcc[b];
                if (boneN[b] > 0 && Quaternion.Dot(q, acc) < 0f)
                    q = -q;

                rotAcc[b] = acc + q * wEff;
                transAcc[b] += wEff * translations[b];
                boneW[b] += wEff;
                boneN[b]++;
            }

            var coveredMorphs = scratch.CoveredMorphs;
            var morphWeights = scratch.MorphWeights;
            for (int k = 0; k < coveredMorphs.Count; k++)
            {
                int mo = coveredMorphs[k];
                morphAcc[mo] += wEff * morphWeights[mo];
                morphW[mo] += wEff;
            }

            // ── 可见性 AND：布尔量不进数值混合，权重 0.5 的隐藏层照样一票否决 ──
            visible &= layer.IsVisibleAt(local);
        }

        Visible = activeCount > 0 ? visible : true;

        // ── 可见性写回（Visible == false ⇒ 渲染三 pass 早退）────────
        model.Visible = Visible;

        // ── 写回（整体写、含未覆盖项 ⇒ 幂等）───────────────────────────
        var modelRotations = model.LocalRotations;
        var modelTranslations = model.LocalTranslations;
        var bindPositions = model.LocalPositions;

        for (int b = 0; b < boneCount; b++)
        {
            float w = boneW[b];
            if (boneN[b] == 1 && w == 1f)
            {
                // 单层满权直通：acc 就是轨道采样值（1.0f * q 精确、且本就归一），
                // 不再过 Normalize ⇒ 与单动效 MmdAnimation.Sample 位级一致（回归基线）。
                modelRotations[b] = rotAcc[b];
            }
            else if (w > 1f)
            {
                // 逐项归一（偏离说明见类注释）：覆盖权重和 > 1 ⇒ 各贡献按 W 摊薄，
                // 归一后的权重和恰为 1，无残差。
                modelRotations[b] = Quaternion.Normalize(rotAcc[b] * (1f / w));
            }
            else
            {
                // 残差混回绑定姿势：绑定局部旋转 = Identity，余额 (1 − W) 加在单位上。
                // 和向量长度 ≥ 1 − W ≥ 0；极端对消（两个半权反向）时长度趋 0，兜底回 Identity。
                var sum = rotAcc[b] + Quaternion.Identity * (1f - w);
                float lenSq = Quaternion.Dot(sum, sum);
                modelRotations[b] = lenSq > 1e-12f
                    ? Quaternion.Normalize(sum)
                    : Quaternion.Identity;
            }

            // 平移：余额贡献 0 = 绑定位移，直接加回绑定位置；W > 1 时与旋转同样摊薄
            modelTranslations[b] = bindPositions[b] + (w > 1f ? transAcc[b] * (1f / w) : transAcc[b]);
        }

        var modelMorphs = model.MorphRawWeights;
        for (int mo = 0; mo < morphCount; mo++)
            modelMorphs[mo] = _morphWeightAcc![mo] > 1f
                ? morphAcc[mo] / _morphWeightAcc[mo]    // 逐项归一（同上）
                : morphAcc[mo];                          // W ≤ 1：余额 0 = 无表情，天然混向绑定
    }

    private void EnsureCapacity(int boneCount, int morphCount)
    {
        if (_rotAcc is not null && _rotAcc.Length == boneCount && _morphAcc!.Length == morphCount)
        {
            if (_scratch is null || _scratch.BoneCount != boneCount || _scratch.MorphCount != morphCount)
                _scratch = new MmdPoseBuffer(boneCount, morphCount);
            return;
        }

        _rotAcc = new Quaternion[boneCount];
        _transAcc = new Vector3[boneCount];
        _morphAcc = new float[morphCount];
        _boneWeightAcc = new float[boneCount];
        _boneCoverCount = new int[boneCount];
        _morphWeightAcc = new float[morphCount];
        _scratch = new MmdPoseBuffer(boneCount, morphCount);
    }
}
