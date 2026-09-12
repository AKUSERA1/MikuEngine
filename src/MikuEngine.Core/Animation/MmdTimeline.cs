using MikuEngine.Core.Models;

namespace MikuEngine.Core.Animation;

/// <summary>
/// 多层时间轴：<see cref="MmdAnimationMixer"/> 的播放控制外壳。
///
/// 职责划分：
/// <list type="bullet">
///   <item>混合（采样 / 加权 / 可见性 AND）—— <see cref="MmdAnimationMixer"/>，本类不碰；</item>
///   <item>层集合管理 —— <see cref="AddLayer"/>（可指定「在第 N 帧导入」）/ <see cref="RemoveLayer"/> /
///         <see cref="ClearLayers"/>，结构变化后自动刷新播放区间（各层活跃区间并集）；</item>
///   <item>时间游标 —— 与 <see cref="MmdAnimationPlayer"/> 同一套语义
///         （连续帧号 / 暂停 / 帧率 / Loop / seek），只是推进结果喂给混合器。</item>
/// </list>
///
/// 采样仍是帧号的纯函数：跳帧 / seek / 暂停 / 改帧率只改游标，不动求值逻辑；
/// 「连续播放到帧 N」≡「直接 seek 到帧 N」。区间外（各层都不活跃）求值即整模型绑定姿势 —— 空白保留。
/// </summary>
public sealed class MmdTimeline
{
    private readonly MmdAnimationMixer _mixer;

    /// <summary>建时间轴。可传入既有混合器（层与状态保留），否则自建。</summary>
    public MmdTimeline(MmdAnimationMixer? mixer = null)
    {
        _mixer = mixer ?? new MmdAnimationMixer();
        RefreshRange();
    }

    /// <summary>被驱动的混合器（可见性 / 层列表 / 求值都从这里读）。</summary>
    public MmdAnimationMixer Mixer => _mixer;

    /// <summary>连续帧号游标，可任意 seek（不取整 ⇒ 30fps 动效在 144Hz 屏上依旧平滑）。</summary>
    public double CurrentFrame;

    /// <summary>每秒播放多少时间轴帧，默认 30（与 <see cref="MmdAnimationPlayer"/> 一致）。</summary>
    public float PlaybackFps = 30f;

    /// <summary>帧号推进模式（真实时间 / 帧锁定），语义同 <see cref="MmdAnimationPlayer.Mode"/>。</summary>
    public MmdPlaybackMode Mode = MmdPlaybackMode.RealTime;

    /// <summary>true = 到时间轴末端后回卷到起点；false = 钳制在末端（默认）。</summary>
    public bool Loop;

    /// <summary>暂停时不推进游标。</summary>
    public bool Paused;

    /// <summary>播放区间起点（各层活跃区间并集；结构变化后自动刷新）。</summary>
    public double StartFrame { get; private set; }

    /// <summary>播放区间终点（并集；结构变化后自动刷新）。</summary>
    public double EndFrame { get; private set; }

    /// <summary>时间轴上的全部层（转发自混合器）。</summary>
    public IReadOnlyList<MmdAnimationLayer> Layers => _mixer.Layers;

    /// <summary>
    /// 添加一层。<paramref name="importAt"/> 非空时把该层放到时间轴帧 <b>importAt</b>（设置
    /// <see cref="MmdAnimationLayer.Offset"/>）——「在第 N 帧导入：该动效的第 0 帧落在时间轴第 N 帧」。
    /// </summary>
    public MmdAnimationLayer AddLayer(MmdAnimationLayer layer, double? importAt = null)
    {
        if (importAt is not null) layer.Offset = importAt.Value;
        _mixer.AddLayer(layer);
        RefreshRange();
        return layer;
    }

    /// <summary>
    /// 移除一层，返回是否确实存在并被移除。
    /// 下一帧 <see cref="Apply"/> 时该层轨道自动回绑定姿势、可见性投票自动解除 —— 无残留。
    /// </summary>
    public bool RemoveLayer(MmdAnimationLayer layer)
    {
        bool removed = _mixer.RemoveLayer(layer);
        if (removed) RefreshRange();
        return removed;
    }

    /// <summary>移除全部层（下一帧整模型回绑定姿势、恒可见）。</summary>
    public void ClearLayers()
    {
        _mixer.ClearLayers();
        RefreshRange();
    }

    /// <summary>推进游标（暂停时无操作）。区间 / 回卷语义与 <see cref="MmdAnimationPlayer.Advance"/> 逐行一致。</summary>
    public void Advance(double deltaSeconds)
    {
        if (Paused) return;

        CurrentFrame += Mode == MmdPlaybackMode.FrameLocked
            ? 1.0
            : deltaSeconds * PlaybackFps;

        if (EndFrame <= StartFrame)
        {
            CurrentFrame = StartFrame;
            return;
        }

        if (CurrentFrame < StartFrame) CurrentFrame = StartFrame;
        if (CurrentFrame < EndFrame) return;

        if (!Loop)
        {
            CurrentFrame = EndFrame;
            return;
        }

        double span = EndFrame - StartFrame;
        CurrentFrame = StartFrame + (CurrentFrame - StartFrame) % span;
    }

    /// <summary>直接定位到某帧（钳制到播放区间）。</summary>
    public void Seek(double frame) => CurrentFrame = System.Math.Clamp(frame, StartFrame, EndFrame);

    /// <summary>相对当前帧步进（钳制到播放区间）。</summary>
    public void Step(double frames) => Seek(CurrentFrame + frames);

    /// <summary>在当前游标处求值并写回模型（等价 <c>Mixer.Evaluate(model, CurrentFrame)</c>）。</summary>
    public void Apply(SkeletalModel model) => _mixer.Evaluate(model, CurrentFrame);

    private void RefreshRange()
    {
        (double start, double end) = _mixer.ActiveRange;
        StartFrame = start;
        EndFrame = end;
    }
}
