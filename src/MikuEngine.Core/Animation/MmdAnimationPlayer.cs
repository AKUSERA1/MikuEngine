namespace MikuEngine.Core.Animation;

/// <summary>动画帧的推进模式。</summary>
public enum MmdPlaybackMode
{
    /// <summary>动画锁定真实时间：<c>CurrentFrame += dt * PlaybackFps</c>（默认）。</summary>
    RealTime = 0,

    /// <summary>每渲染帧精确推进 1 动画帧（离线渲染 / 调试 / 帧导出）。</summary>
    FrameLocked = 1,
}

/// <summary>
/// 帧号游标驱动的播放器。
///
/// 显示帧率与动画帧率解耦：渲染循环跑显示器的 vsync，动画只由连续帧号 <see cref="CurrentFrame"/>
/// 表达，两者仅通过游标耦合。因为采样是帧号的纯函数（见 <see cref="MmdAnimation.Sample"/>），
/// 跳帧 / seek / 暂停 / 改帧率都只是改游标或改推进步长，不动采样逻辑。
///
/// 平滑的关键：采样在<b>连续帧号</b>上插值（不 floor 到整数帧），所以 30fps 动效在 144Hz 屏上依旧平滑；
/// 若错误地取整帧号，会出现 30fps 步进抖动。
/// </summary>
public sealed class MmdAnimationPlayer
{
    /// <summary>连续帧号，可任意 seek。</summary>
    public double CurrentFrame;

    /// <summary>每秒播放多少动画帧（类似 Blender 的 FPS），默认 30。</summary>
    public float PlaybackFps = 30f;

    public MmdPlaybackMode Mode = MmdPlaybackMode.RealTime;

    /// <summary>true = 到尾帧后回卷到首帧；false = 钳制在尾帧（默认）。</summary>
    public bool Loop;

    /// <summary>暂停时不推进帧号。</summary>
    public bool Paused;

    public double StartFrame;
    public double EndFrame;

    /// <summary>设定播放区间并把游标置于首帧。</summary>
    public void Configure(double startFrame, double endFrame)
    {
        StartFrame = startFrame;
        EndFrame = endFrame;
        CurrentFrame = startFrame;
    }

    /// <summary>推进游标。暂停时为无操作。</summary>
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
}
