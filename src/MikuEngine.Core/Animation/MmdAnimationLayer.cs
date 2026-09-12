namespace MikuEngine.Core.Animation;

/// <summary>
/// 时间轴上的一个动效层：把一条已绑定到模型的动效放到时间轴的任意位置，并可裁剪 / 加权 / 淡入淡出 / 循环。
///
/// 坐标映射：
/// <code>
/// local    = timeline − Offset
/// 活跃区间 = [Offset + TrimStart, Offset + TrimEnd]        // 闭区间，与 babylon-mmd isInSpan 一致
/// </code>
///
/// <see cref="Offset"/> = 「动画帧 0 落在时间轴哪一帧」：在第 30 帧导入 ⇒ <c>Offset = 30</c>。
/// <see cref="TrimStart"/>/<see cref="TrimEnd"/> 默认 = 动效内容起止（<see cref="MmdAnimation.StartFrame"/> /
/// <c>EndFrame</c>，即首 / 末键），因此 VMD 自身的前导空帧会如实表现为「这段时间轴区间上该层不贡献」
/// （空白保留、不被首键外推填满）；显式把 <c>TrimStart</c> 设到内容起点之前才等价于「首键外推」——
/// 那是显式选择，不是默认行为。
///
/// 本类只管「时间轴帧 → 本地帧 / 活跃性 / 权重包络 / 可见性」的换算，<b>不做</b>混合与写回
/// （那属于 MmdAnimationMixer）。
/// </summary>
public sealed class MmdAnimationLayer
{
    /// <summary>建层，并把裁剪区间默认设为该动效的内容区间。</summary>
    public MmdAnimationLayer(MmdAnimation animation)
    {
        Animation = animation;
        TrimStart = animation.StartFrame;
        TrimEnd = animation.EndFrame;
    }

    /// <summary>该层驱动的动效（应已 <c>Bind</c> 到目标模型）。</summary>
    public MmdAnimation Animation { get; }

    /// <summary>动画帧 0 落在时间轴哪一帧（「在第 30 帧导入」⇒ 30）。</summary>
    public double Offset;

    /// <summary>裁剪区间起点（动画本地帧）。默认 = <see cref="MmdAnimation.StartFrame"/>（内容起点）。</summary>
    public double TrimStart;

    /// <summary>裁剪区间终点（动画本地帧）。默认 = <see cref="MmdAnimation.EndFrame"/>（内容终点）。</summary>
    public double TrimEnd;

    /// <summary>层权重（≤ 0 ⇒ 该层完全不贡献）。</summary>
    public float Weight = 1f;

    /// <summary>true = 越过活跃区间末端后把本地帧回卷到 <c>[TrimStart, TrimEnd)</c>；false = 区间外不贡献。</summary>
    public bool Loop;

    /// <summary>层内淡入帧数（自活跃区间起点起算；0 = 不淡入）。</summary>
    public double FadeIn;

    /// <summary>层内淡出帧数（自活跃区间终点往前算；0 = 不淡出）。</summary>
    public double FadeOut;

    /// <summary>内容起点（动效首键，动画本地帧）。</summary>
    public double ContentStart => Animation.StartFrame;

    /// <summary>内容终点（动效末键，动画本地帧）。</summary>
    public double ContentEnd => Animation.EndFrame;

    /// <summary>活跃区间起点（时间轴帧，闭）。</summary>
    public double ActiveStart => Offset + TrimStart;

    /// <summary>活跃区间终点（时间轴帧，闭）。</summary>
    public double ActiveEnd => Offset + TrimEnd;

    /// <summary>
    /// 该层在时间轴帧 <paramref name="timelineFrame"/> 是否贡献。
    ///
    /// 规则：
    /// <list type="bullet">
    ///   <item>活跃区间<b>之前</b>一律不贡献 —— 空白保留，即使 <see cref="Loop"/> 也尚未开始；</item>
    ///   <item>活跃区间<b>之内</b>贡献；</item>
    ///   <item>活跃区间<b>之后</b>仅当 <see cref="Loop"/> 回卷时继续贡献。</item>
    /// </list>
    ///
    /// 判定只看「<c>Weight &gt; 0</c> 且落在区间内」，<b>与淡入淡出包络无关</b>：淡入首帧的包络为 0，
    /// 但该层仍算活跃（可见性投票不受 fade 影响 —— 表示枠是布尔量，不参与数值混合）。
    /// </summary>
    public bool IsActive(double timelineFrame)
    {
        if (Weight <= 0f) return false;
        if (TrimEnd < TrimStart) return false;          // 区间倒置 ⇒ 视为不贡献
        if (timelineFrame < ActiveStart) return false;
        if (timelineFrame <= ActiveEnd) return true;
        return Loop;
    }

    /// <summary>
    /// 时间轴帧 → 该动效自己的本地帧。
    ///
    /// 区间之前 / 之内原样映射（是否使用由调用方按 <see cref="IsActive"/> 决定，因此区间前采到的会是
    /// 首键的钳制值）；<see cref="Loop"/> 且越过末端时回卷到 <c>[TrimStart, TrimEnd)</c>。
    /// 退化区间（<c>TrimEnd == TrimStart</c>，例如只有一个键的动效）回卷无意义，直接返回 <see cref="TrimStart"/>。
    /// </summary>
    public double ToLocal(double timelineFrame)
    {
        double local = timelineFrame - Offset;
        if (!Loop || local <= TrimEnd) return local;

        double span = TrimEnd - TrimStart;
        if (span <= 0) return TrimStart;
        return TrimStart + (local - TrimStart) % span;   // local > TrimEnd ≥ TrimStart ⇒ 余数非负
    }

    /// <summary>
    /// 层内淡入淡出包络 ∈ [0, 1]；默认（<see cref="FadeIn"/> = <see cref="FadeOut"/> = 0）恒为 1。
    ///
    /// 当前是线性斜坡；日后若要换类似 babylon-mmd 的 <c>easingFunction</c>，只改这里、接口不变。
    /// 混合器按 §3.4 的 <c>wᵢ_eff = wᵢ · norm · fadeᵢ(timeline)</c> 消费本值。
    /// </summary>
    public float FadeAt(double timelineFrame)
    {
        float fade = 1f;

        if (FadeIn > 0 && timelineFrame < ActiveStart + FadeIn)
            fade = System.Math.Clamp((float)((timelineFrame - ActiveStart) / FadeIn), 0f, 1f);

        if (FadeOut > 0 && timelineFrame > ActiveEnd - FadeOut)
            fade = System.Math.Min(fade, System.Math.Clamp((float)((ActiveEnd - timelineFrame) / FadeOut), 0f, 1f));

        return fade;
    }

    /// <summary>
    /// 该层在<b>本地帧</b> <paramref name="localFrame"/> 处的表示枠（显示 / 非表示）。
    /// 混合器按 <c>IsVisibleAt(ToLocal(timelineFrame))</c> 组合使用，再把各活跃层的结果做 AND。
    /// </summary>
    public bool IsVisibleAt(double localFrame) => Animation.PropertyTrack.SampleVisible(localFrame);
}
