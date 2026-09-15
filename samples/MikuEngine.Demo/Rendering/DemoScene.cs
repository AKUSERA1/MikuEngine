using System.Numerics;
using MikuEngine.Core.Animation;
using MikuEngine.Core.Camera;
using MikuEngine.Core.Models;
using MikuEngine.Engine;
using MikuEngine.Physics;
using MikuEngine.Render.GLES;
using Silk.NET.OpenGL;

namespace MikuEngine.Demo.Rendering;

/// <summary>
/// 场景：模型集合 + 相机 + 主时钟 + 每帧渲染管线。完全不涉及窗口与 WinForms（宿主是 <c>MainForm</c>）。
///
/// 主时钟：所有模型共用**一个帧号游标**（MMD 同一全局时间轴的语义），
/// 每个模型把自己的时间轴 <c>Seek</c> 到该游标（各自按自己的区间夹取）。
///
/// 活跃模型 = **当前操作对象**（TRS 作用对象 + 动画载入目标），**不参与阴影决策**：阴影由
/// **全部模型**共同投射，光照视锥按所有模型的包围盒并集拟合，因此切换活跃模型不会让画面上的
/// 影子发生任何变化。
///
/// 多模型：每个模型一套渲染器与时间轴；外部親按「PMX 模型名 / 显示名」自动注册，
/// VMD 带外部親键时即生效（迭代顺序 = 模型加载顺序，**加载顺序即父子序**）。
/// </summary>
public sealed class DemoScene : IDisposable
{
    /// <summary>PE / MMD 默认视野角（度）。</summary>
    private const float DefaultViewAngleDegrees = 25f;

    /// <summary>
    /// 默认朝向：相机放在模型**正面**。α=0 时相机落在 +Z（模型是背对 +Z 的，见 PMX/MMD 朝向），
    /// 屏幕上就是"看背面"；转到 π 才是看正面。
    /// </summary>
    private const float DefaultCameraAlpha = MathF.PI;

    /// <summary>
    /// 旋转灵敏度（取负 = 反向）。引擎控制器的鼠标约定是「相机跟手」（拖右 → 视点右移，
    /// 模型看着往左转），而桌面 3D 视角的直觉是「**模型跟手**」（拖右 → 模型向右转、
    /// 拖下 → 露出顶部）。只反向旋转：平移与缩放本来就是跟手的，保持原符号不动。
    /// </summary>
    private const float OrbitRotationSensitivity = -0.0025f;

    /// <summary>帧级光照默认方向（与 <see cref="FrameUniforms.Default"/> 同源，只取 xyz）。</summary>
    private static readonly Vector3 DefaultLightDirection = ToVector3(FrameUniforms.Default().LightDirection);

    private static Vector3 ToVector3(Vector4 v) => new(v.X, v.Y, v.Z);

    private readonly List<DemoModel> _models = [];
    private readonly MmdExternalParentController _externalParents = new();

    /// <summary>阴影 caster / 光照视锥拟合用的缓存，只在模型集合变化时重建（逐帧遍历会白分配）。</summary>
    private readonly List<GlesModelRenderer> _casters = [];
    private readonly List<SkeletalModel> _skeletons = [];

    private GlesGridRenderer? _grid;
    private GlesShadowRenderer? _shadow;
    private bool _ready;

    public GL Gl { get; private set; } = null!;

    public GlesDevice Device { get; private set; } = null!;

    public OrbitCamera Camera { get; } = new(
        alpha: DefaultCameraAlpha,
        beta: MathF.PI / 2.2f,
        radius: 40f,
        target: Vector3.Zero,
        fov: DefaultViewAngleDegrees * MathF.PI / 180f);

    public OrbitInputController Input { get; private set; } = null!;

    public IReadOnlyList<DemoModel> Models => _models;

    public DemoModel? ActiveModel { get; private set; }

    // ---------------------------------------------------------------- 主时钟

    public double Frame { get; private set; }

    public float PlaybackFps { get; set; } = 30f;

    /// <summary>启动即**暂停**：还没载入任何东西就没有"在播"这回事（MMD 载入动效后也不会自己播）。</summary>
    public bool IsPlaying { get; private set; }

    public bool Loop { get; set; } = true;

    // ---------------------------------------------------------------- 显示 / 物理开关

    public bool EdgeVisible { get; private set; } = true;

    public int SelfShadowMode { get; private set; } = 1;

    public SelfShadowStyle ShadowStyle { get; private set; } = SelfShadowStyle.Standard;

    public bool PhysicsEnabled { get; private set; } = true;

    public bool GroundCollisionEnabled { get; private set; } = true;

    public bool PostPhysicsAppendEnabled { get; private set; } = true;

    // ---------------------------------------------------------------- 相机动画（整场景量）

    public MmdCameraTrack? CameraTrack { get; private set; }

    public bool CameraAnimationEnabled { get; set; }

    /// <summary>面向用户的消息（载入结果 / 失败原因），宿主接到日志区。</summary>
    public event EventHandler<string>? Message;

    /// <summary>模型集合、活跃模型、播放状态或变换发生变化（宿主刷新读数）。</summary>
    public event EventHandler? StateChanged;

    // ================================================================== 生命周期

    /// <summary>在 GL 上下文就绪后调用一次。</summary>
    public void Initialize(GlViewport viewport)
    {
        if (_ready) return;
        Gl = viewport.Gl ?? throw new InvalidOperationException("GL 上下文未就绪");
        Device = viewport.Device ?? throw new InvalidOperationException("GL 设备未就绪");

        Input = new OrbitInputController(Camera, enabled: true)
        {
            RotationSensitivity = OrbitRotationSensitivity,
        };
        _grid = new GlesGridRenderer(Device);
        _shadow = new GlesShadowRenderer(Device) { Mode = (GlesShadowRenderer.ShadowMode)SelfShadowMode };
        _ready = true;

        Report($"渲染就绪：{Gl.GetStringS(StringName.Version)} | {Gl.GetStringS(StringName.Renderer)}");
    }

    public void Dispose()
    {
        _externalParents.Clear();
        foreach (var model in _models) model.Dispose();
        _models.Clear();
        if (!_ready) return;
        _shadow?.Dispose();
        _grid?.Dispose();
        Device.Dispose();
        _ready = false;
    }

    private void Report(string message) => Message?.Invoke(this, message);

    private void RaiseStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);

    // ================================================================== 模型

    /// <summary>载入一个 PMX 并设为活跃模型。失败抛异常，由宿主提示。</summary>
    public DemoModel LoadModel(string pmxPath)
    {
        string full = Path.GetFullPath(pmxPath);
        var renderer = GlesModelRenderer.LoadFromFile(Device, full);
        var pmx = PmxParser.Parse(File.ReadAllBytes(full));

        string stem = Path.GetFileNameWithoutExtension(full);
        string modelName = pmx.Header.ModelName;
        string display = modelName.Length > 0 && !string.Equals(modelName, stem, StringComparison.Ordinal)
            ? $"{stem} — {modelName}"
            : stem;

        var model = new DemoModel(display, full, modelName, renderer);
        ApplyDisplaySettings(model);
        TryAttachPhysics(model, pmx, stem);

        _models.Add(model);
        _externalParents.Register(display, renderer.Model);
        if (modelName.Length > 0) _externalParents.Register(modelName, renderer.Model);

        Report($"[{stem}] 载入完成：{renderer.Model.BoneCount} 骨 / {renderer.Model.Materials.Length} 材质 / " +
               $"顶点 {renderer.Model.VertexCount}");

        RebuildShadowFit();
        SetActiveModel(model);
        if (_models.Count == 1) FrameCamera();
        RaiseStateChanged();
        return model;
    }

    public void RemoveModel(DemoModel model)
    {
        if (!_models.Remove(model)) return;
        _externalParents.Unregister(model.DisplayName);
        if (model.ModelName.Length > 0) _externalParents.Unregister(model.ModelName);
        model.Dispose();
        Report($"[{model.DisplayName}] 已移除");
        RebuildShadowFit();
        SetActiveModel(_models.Count > 0 ? _models[^1] : null);
        RaiseStateChanged();
    }

    /// <summary>
    /// 切换活跃模型：**只影响"当前操作对象"**（TRS 作用对象 / 动画载入目标 / 变换读数），
    /// 不再牵动阴影 —— 阴影由全部模型共同投射，光照视锥按所有模型的并集拟合，见
    /// <see cref="RebuildShadowFit"/>。
    /// </summary>
    public void SetActiveModel(DemoModel? model)
    {
        ActiveModel = model;
        RaiseStateChanged();
    }

    /// <summary>
    /// 阴影接线：光照视锥按**全部**模型的包围盒并集拟合，再把接收侧参数（Z 图 / texel / 法线偏移）
    /// 发给每个模型。
    ///
    /// 只在「模型集合变化」时调用：包围盒取自绑定姿势（转换期算好即静态），而 texel 与法线偏移
    /// 是拟合结果的派生量 ⇒ 拟合一变就得整体重发一遍，否则新模型的偏移量按旧 texel 计算。
    /// </summary>
    private void RebuildShadowFit()
    {
        if (!_ready) return;

        _casters.Clear();
        _skeletons.Clear();
        foreach (var model in _models)
        {
            _casters.Add(model.Renderer);
            _skeletons.Add(model.Skeleton);
        }

        _shadow!.UpdateLight(_skeletons, DefaultLightDirection);
        foreach (var model in _models) WireShadow(model);
    }

    // ================================================================== 逐模型开关

    /// <summary>
    /// 开关**活跃模型**的 IK 求解。逐模型保存：切走再切回来仍是原来的状态。
    /// 关掉后该模型停在「付与之后、IK 之前」的姿态（脚会跟着动效走而不再被地面吸附）。
    /// 读状态直接看 <c>ActiveModel.IkEnabled</c>。
    /// </summary>
    public void SetActiveIkEnabled(bool enabled)
    {
        if (ActiveModel is not { } model) return;
        model.SetIkEnabled(enabled);
        Report($"[{model.DisplayName}] IK 求解：{(enabled ? "开" : "关")}");
        RaiseStateChanged();
    }

    /// <summary>相机按活跃模型的包围盒定位。</summary>
    public void FrameCamera()
    {
        var model = ActiveModel ?? (_models.Count > 0 ? _models[0] : null);
        if (model is null) return;
        var bounds = model.Skeleton;
        Camera.Target = bounds.BoundsCenter;
        Camera.Radius = MathF.Max(bounds.BoundsSize.Y * 2f, bounds.BoundsSize.Length() * 1.1f);
        Camera.Beta = MathF.PI / 2.2f;
        RaiseStateChanged();
    }

    // ================================================================== 动画

    /// <summary>把 VMD 载入到指定模型（替换该模型已有的动画；其它模型不受影响）。</summary>
    public bool LoadMotion(string vmdPath, DemoModel target)
    {
        string full = Path.GetFullPath(vmdPath);
        var animation = MmdAnimation.FromVmd(VmdParser.Parse(File.ReadAllBytes(full)));
        string file = Path.GetFileName(full);

        // 纯相机 VMD：只有相机轨道，不能拿它替换模型的动画层（会把已载入的动作清掉）
        if (animation.BoneTracks.Length == 0 && animation.MorphTracks.Length == 0 && animation.PropertyTrack.IsEmpty)
        {
            if (animation.CameraTrack is { IsEmpty: false } only)
            {
                SetCameraTrack(only, file);
                return true;
            }
            Report($"[{target.DisplayName}] {file} 既没有可匹配的骨/表情轨道，也没有相机关键帧，已忽略");
            return false;
        }

        var bound = animation.Bind(target.Skeleton);
        target.AttachMotion(full, bound);

        _externalParents.Register(target.DisplayName, target.Skeleton, bound);
        if (target.ModelName.Length > 0) _externalParents.Register(target.ModelName, target.Skeleton, bound);

        var motion = target.Motion;
        Report($"[{target.DisplayName}] 动画：{file} | 骨轨道 {motion.BoneTracks} | 表情轨道 {motion.MorphTracks} | " +
               $"区间 {motion.StartFrame:F0}..{motion.EndFrame:F0}");
        if (motion.TrackCount == 0)
            Report($"[{target.DisplayName}] 该 VMD 没有能与本模型匹配的轨道，播放不会有可见效果");

        if (animation.CameraTrack is { IsEmpty: false } camera) SetCameraTrack(camera, file);
        RaiseStateChanged();
        return true;
    }

    private void SetCameraTrack(MmdCameraTrack track, string file)
    {
        CameraTrack = track;
        Report($"相机动画：{file} | {track.Frames.Length} 键 | 帧 {track.Frames[0]}..{track.Frames[^1]}");
        RaiseStateChanged();
    }

    public void SetCameraAnimationEnabled(bool enabled)
    {
        CameraAnimationEnabled = enabled;
        Report($"相机动画：{(enabled ? "开（VMD 独占视图，鼠标轨道失效）" : "关（回到轨道相机）")}");
        RaiseStateChanged();
    }

    // ================================================================== 主时钟

    /// <summary>所有模型动画区间的并集起点（无动画时为 0）。</summary>
    public double TimelineStart
    {
        get
        {
            double start = double.MaxValue;
            foreach (var m in _models)
                if (m.Timeline is not null) start = Math.Min(start, m.Motion.StartFrame);
            return start == double.MaxValue ? 0 : start;
        }
    }

    /// <summary>所有模型动画区间的并集终点（无动画时为 0）。</summary>
    public double TimelineEnd
    {
        get
        {
            double end = double.MinValue;
            foreach (var m in _models)
                if (m.Timeline is not null) end = Math.Max(end, m.Motion.EndFrame);
            return end == double.MinValue ? 0 : end;
        }
    }

    public void Play() => SetPlaying(true);

    public void Pause() => SetPlaying(false);

    public void TogglePlay() => SetPlaying(!IsPlaying);

    private void SetPlaying(bool playing)
    {
        if (IsPlaying == playing) return;
        IsPlaying = playing;
        RaiseStateChanged();
    }

    public void SeekFrame(double frame)
    {
        Frame = Math.Max(TimelineStart, frame);
        RaiseStateChanged();
    }

    public void StepFrame(double frames) => SeekFrame(Frame + frames);

    public void SeekToStart() => SeekFrame(TimelineStart);

    private void Advance(double deltaSeconds)
    {
        if (!IsPlaying) return;

        // 没有任何可播区间（没载入模型 / 模型没有动画）时**不推进游标**：
        // 否则空场景的帧号也会一路涨，"启动就在播"这个假象就是这么来的。
        double start = TimelineStart, end = TimelineEnd;
        if (end <= start) return;

        Frame += deltaSeconds * PlaybackFps;

        if (Loop)
        {
            double span = end - start;
            if (Frame > end) Frame = start + (Frame - start) % span;
        }
        else if (Frame > end)
        {
            Frame = end;
            SetPlaying(false);
        }
    }

    // ================================================================== 模型变换（全局模式）

    /// <summary>按拖动位移调整活跃模型的指定分量。</summary>
    public void NudgeActiveTransform(TransformChannel channel, TransformAxis axis, int pixels)
    {
        if (ActiveModel is not { } model) return;
        ModelTransform.Nudge(model.Skeleton, model.Renderer, channel, axis, pixels);
        RaiseStateChanged();
    }

    public void ResetActiveTransform()
    {
        if (ActiveModel is not { } model) return;
        ModelTransform.Reset(model.Skeleton, model.Renderer);
        Report($"[{model.DisplayName}] 模型变换已重置");
        RaiseStateChanged();
    }

    /// <summary>活跃模型的变换读数（未选中模型时给占位文字）。</summary>
    public string DescribeActiveTransform()
        => ActiveModel is { } model ? ModelTransform.Describe(model.Skeleton, model.Renderer) : "（未选中模型）";

    // ================================================================== 显示 / 物理

    public void SetEdgeVisible(bool visible)
    {
        EdgeVisible = visible;
        foreach (var m in _models) m.Renderer.EdgeVisible = visible;
        Report($"轮廓线：{(visible ? "开" : "关")}");
        RaiseStateChanged();
    }

    public void SetSelfShadowMode(int mode)
    {
        SelfShadowMode = mode;
        _shadow!.Mode = (GlesShadowRenderer.ShadowMode)mode;
        foreach (var m in _models) m.Renderer.SelfShadowMode = mode;
        Report($"自阴影：{(mode == 0 ? "关" : mode == 1 ? "自阴影" : "自阴影 + 床影")}");
        RaiseStateChanged();
    }

    public void SetShadowStyle(SelfShadowStyle style)
    {
        ShadowStyle = style;
        foreach (var m in _models) m.Renderer.ShadowStyle = style;
        Report($"自阴影风格：{style switch
        {
            SelfShadowStyle.Threshold => "硬边本影",
            SelfShadowStyle.Soft => "普通阴影（PCF）",
            _ => "标准本影",
        }}");
        RaiseStateChanged();
    }

    public void CycleShadowStyle()
        => SetShadowStyle(ShadowStyle switch
        {
            SelfShadowStyle.Standard => SelfShadowStyle.Threshold,
            SelfShadowStyle.Threshold => SelfShadowStyle.Soft,
            _ => SelfShadowStyle.Standard,
        });

    public void SetPhysicsEnabled(bool enabled)
    {
        PhysicsEnabled = enabled;
        Report($"物理模拟：{(enabled ? "开" : "关")}");
        RaiseStateChanged();
    }

    public void SetGroundCollisionEnabled(bool enabled)
    {
        GroundCollisionEnabled = enabled;
        Report($"地面碰撞：{(enabled ? "开" : "关")}");
        RaiseStateChanged();
    }

    public void SetPostPhysicsAppendEnabled(bool enabled)
    {
        PostPhysicsAppendEnabled = enabled;
        foreach (var m in _models) m.Renderer.PostPhysicsAppendEnabled = enabled;
        Report($"物理后付与：{(enabled ? "开" : "关")}");
        RaiseStateChanged();
    }

    private void ApplyDisplaySettings(DemoModel model)
    {
        model.Renderer.SelfShadowMode = SelfShadowMode;
        model.Renderer.ShadowStyle = ShadowStyle;
        model.Renderer.EdgeVisible = EdgeVisible;
        model.Renderer.PostPhysicsAppendEnabled = PostPhysicsAppendEnabled;
        WireShadow(model);
    }

    /// <summary>
    /// 接收侧参数：Z 图 + texel 尺寸 + 法线偏移。三个都是光照拟合的派生量
    /// （texel 随拟合出的视锥大小变），所以拟合一变就必须对**所有**模型重发，见 <see cref="RebuildShadowFit"/>。
    /// </summary>
    private void WireShadow(DemoModel model)
    {
        model.Renderer.ShadowZTexture = _shadow!.ZTexture;
        model.Renderer.ShadowTexel = _shadow.Texel;
        model.Renderer.ShadowNormalOffset = _shadow.TexelWorld * 1.5f;
    }

    private void TryAttachPhysics(DemoModel model, PmxModel pmx, string stem)
    {
        try
        {
            var bodies = pmx.RigidBodies.Select(RigidBodyDef.FromPmx).ToArray();
            var joints = pmx.Joints.Select(JointDef.FromPmx).ToArray();
            model.Renderer.Physics = new MMDPhysics(bodies, joints);

            int dynamic = bodies.Count(b => b.Type == RigidbodyType.Dynamic);
            Report($"[{stem}] 物理：刚体 {bodies.Length}（动态 {dynamic}）/ 关节 {joints.Length}");
        }
        catch (Exception ex)
        {
            // 模型可以不带物理，接口不匹配时渲染照常
            Report($"[{stem}] 物理初始化失败（该模型不接物理）：{ex.Message}");
        }
    }

    // ================================================================== 每帧

    /// <summary>渲染一帧。宿主在 GL 上下文 current 的前提下调用（见 <see cref="GlViewport.DrawFrame"/>）。</summary>
    public void Draw(double deltaSeconds)
    {
        if (!_ready) return;

        Advance(deltaSeconds);
        Device.BeginFrame();

        int width = Device.Width, height = Device.Height;
        Camera.Aspect = height > 0 ? (float)width / height : 1f;

        // 相机动画：与时间轴同一游标采样（采样是帧号的纯函数，seek / 暂停 / 回卷自动正确）
        bool driveCamera = CameraTrack is not null && CameraAnimationEnabled;
        Camera.SetVmdDriven(driveCamera);
        if (driveCamera && CameraTrack!.Sample(Frame, out var pose))
            Camera.SetVmdPose(pose.Target, pose.RotationEuler, pose.Distance, pose.Fov);

        Span<float> viewProjection = stackalloc float[16];
        Span<float> view = stackalloc float[16];
        Camera.ComputeViewProj(viewProjection);
        Camera.WriteViewMatrix(view);

        var frame = FrameUniforms.Default();
        frame.ViewProj = FromColumnMajor(viewProjection);
        frame.View = FromColumnMajor(view);
        frame.CameraPosition = new Vector4(Camera.GetEyePosition(), 0f);
        frame.LightViewProj = _shadow!.LightViewProj;

        // 1) 动画采样：主时钟游标 → 各模型自己的时间轴 → 混合求值 → morph 传播
        foreach (var model in _models)
        {
            if (model.Timeline is null) continue;
            model.Timeline.Seek(Frame);
            model.Timeline.Apply(model.Skeleton);
            MmdMorphEvaluator.Evaluate(model.Skeleton);
        }

        // 2) FK + 物理。按加载顺序逐个走（**加载顺序即父子序**）：亲模型世界矩阵定稿后再解析一次
        //    外部親绑定，后面的子模型就能读到本帧的亲骨世界矩阵。
        foreach (var model in _models)
        {
            if (model.Renderer.Physics is not null)
            {
                model.Renderer.Physics.PlaybackFps = PlaybackFps;
                model.Renderer.PhysicsFrame = model.Timeline?.CurrentFrame ?? Frame;
                model.Renderer.PhysicsEnabled = PhysicsEnabled;
                model.Renderer.GroundCollisionEnabled = GroundCollisionEnabled;
            }
            model.Renderer.PrepareFrame(in frame);
            _externalParents.Update(Frame);
        }

        // 3) 阴影 + 绘制：Z pass 由**全部**模型共同投射（与活跃模型无关），共享同一张深度图
        if (_shadow.Enabled && _casters.Count > 0)
            _shadow.RenderShadowMaps(Device, _casters, width, height);

        foreach (var model in _models) model.Renderer.Draw(in frame);

        _grid!.Draw(viewProjection);

        // 床影是贴地半透明 overlay（只混合不写深度），必须最后画，否则被格网地面盖掉
        if (_models.Count > 0 && _shadow.Enabled)
            _shadow.DrawFloor(Device, new Vector3(frame.LightColor.X, frame.LightColor.Y, frame.LightColor.Z));
    }

    /// <summary>
    /// OrbitCamera 输出的是列主序 float[16]；<see cref="Matrix4x4"/> 的字段顺序
    /// （M11,M12,M13,M14,M21…）与 GLSL mat4 的列主序内存在字节上一致，因此**按顺序直抄**，不能转置。
    /// </summary>
    private static Matrix4x4 FromColumnMajor(ReadOnlySpan<float> c) => new(
        c[0], c[1], c[2], c[3],
        c[4], c[5], c[6], c[7],
        c[8], c[9], c[10], c[11],
        c[12], c[13], c[14], c[15]);
}
