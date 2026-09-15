using MikuEngine.Core.Animation;
using MikuEngine.Core.Models;
using MikuEngine.Render.GLES;

namespace MikuEngine.Demo.Rendering;

/// <summary>
/// 一个已载入的模型：自己的渲染器 + 自己的动画时间轴 + MMD「模型变换」状态。
/// 多模型时每个实例完全自包含（引擎侧本就无全局动画状态），共享的只有相机 / 格网 / 阴影 / 主时钟。
/// </summary>
public sealed class DemoModel : IDisposable
{
    private MmdTimeline? _timeline;

    internal DemoModel(string displayName, string pmxPath, string modelName, GlesModelRenderer renderer)
    {
        DisplayName = displayName;
        PmxPath = pmxPath;
        ModelName = modelName;
        Renderer = renderer;
        Skeleton.IkSolverEnabled = IkEnabled;
    }

    /// <summary>下拉框 / 日志里的显示名（文件名，必要时附 PMX 模型名）。</summary>
    public string DisplayName { get; }

    public string PmxPath { get; }

    /// <summary>PMX 里的模型名；外部親按这个名字匹配（可能为空串）。</summary>
    public string ModelName { get; }

    public GlesModelRenderer Renderer { get; }

    public SkeletalModel Skeleton => Renderer.Model;

    /// <summary>本模型的时间轴；未载入动画时为 null（模型停在绑定姿势）。</summary>
    public MmdTimeline? Timeline => _timeline;

    /// <summary>当前动画的只读摘要（时间线面板显示用）。</summary>
    public MotionInfo Motion { get; private set; } = MotionInfo.None;

    /// <summary>
    /// 本模型的 IK 求解开关（逐模型，默认开）。关掉后求解器只做 FK + 付与，
    /// 模型停在「付与之后、IK 之前」的姿态（脚随动效走，不再被地面吸附）。
    /// </summary>
    public bool IkEnabled { get; private set; } = true;

    internal void SetIkEnabled(bool enabled)
    {
        if (IkEnabled == enabled) return;
        IkEnabled = enabled;
        Skeleton.IkSolverEnabled = enabled;
    }

    /// <summary>把一份已绑定到本模型的动画装进来（替换原有的）。</summary>
    internal void AttachMotion(string vmdPath, MmdAnimation bound)
    {
        _timeline ??= new MmdTimeline { Loop = true };
        _timeline.ClearLayers();
        _timeline.AddLayer(new MmdAnimationLayer(bound));
        Motion = new MotionInfo(
            Path.GetFileName(vmdPath),
            bound.BoneTracks.Length,
            bound.MorphTracks.Length,
            _timeline.StartFrame,
            _timeline.EndFrame);
    }

    public override string ToString() => DisplayName;

    public void Dispose() => Renderer.Dispose();
}

/// <summary>模型当前动画的摘要。未载入动画时 <see cref="HasMotion"/> 为 false。</summary>
public readonly record struct MotionInfo(
    string? FileName,
    int BoneTracks,
    int MorphTracks,
    double StartFrame,
    double EndFrame)
{
    public static MotionInfo None => new(null, 0, 0, 0, 0);

    public bool HasMotion => FileName is not null;

    /// <summary>动画轨道数 = 骨轨道 + 表情轨道。</summary>
    public int TrackCount => BoneTracks + MorphTracks;
}
