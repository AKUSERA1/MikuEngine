using System.Diagnostics;
using System.Runtime.InteropServices;
using MikuEngine.Demo.Controls;
using MikuEngine.Demo.Rendering;
using MikuEngine.Engine;
using MikuEngine.Render.GLES;

namespace MikuEngine.Demo;

/// <summary>
/// 主窗口：MMD 本体那种「倒品字形」布局 ——
/// 顶部菜单 / 左上时间线与消息、右上 3D 视口（右下角叠变换盘）/ 下方通栏操作面板 / 底部状态栏。
///
/// 本类只做「界面与交互 → 场景调用」的组装：渲染与动画逻辑在 <see cref="DemoScene"/>，
/// GL 上下文在 <see cref="GlViewport"/>。启动时不载入任何文件，模型与动画都由用户拖放或菜单打开。
/// </summary>
public partial class MainForm : Form
{
    /// <summary>无消息待处理时每帧的最小间隔（无 vsync 时的兜底限速，避免满核空转）。</summary>
    private const double MinFrameSeconds = 0.004;

    /// <summary>单帧最多推进的时间（长卡顿后不要一次推进几十帧）。</summary>
    private const double MaxFrameSeconds = 0.25;

    /// <summary>WinForms 滚轮一格是 ±120，而 <c>OrbitInputController.OnScroll</c> 按「一格 = ±1」计量。</summary>
    private const float WheelDeltaPerNotch = 120f;

    private readonly DemoScene _scene = new();

    private long _lastTick;
    private double _fps;
    private double _readoutTimer;
    private bool _closing;
    private bool _fatalReported;
    private bool _suppressComboEvents;
    private bool _suppressCheckEvents;
    private string _glVersion = "-";

    public MainForm()
    {
        InitializeComponent();
        DoubleBuffered = true;

        _viewport.ContextReady += OnContextReady;
        _viewport.RenderFailed += OnRenderFailed;

        // 视口的每帧钩子：DrawFrame 在一个已 current 的上下文里回调这里，返回后由控件 SwapBuffers。
        // 少了这一行视口就是全黑（后缓冲从没被画过，SwapBuffers 只是把它原样翻上来）。
        _viewport.Render += OnViewportRender;

        // 相机输入：OrbitInputController 只要「原生事件 → 控制器方法」一行转发，
        // 与 GLFW 版同源（见 Old Demo 的 SetMouseButton/SetCursorPos/SetScrollCallback）。
        _viewport.MouseDown += OnViewportMouseDown;
        _viewport.MouseMove += OnViewportMouseMove;
        _viewport.MouseUp += OnViewportMouseUp;
        _viewport.MouseWheel += OnViewportMouseWheel;

        _transformPad.Dragged += OnTransformDragged;
        _transformPad.ChannelChanged += (_, _) => RefreshReadouts();
        _viewportHost.Resize += (_, _) => PositionTransformPad();

        _scene.Message += (_, text) => AppendLog(text);
        _scene.StateChanged += (_, _) => RefreshReadouts();

        Shown += OnShown;
        FormClosed += OnFormClosed;
        EnableDropRecursive(this);

        RefreshReadouts();
    }

    // ================================================================== 生命周期

    private void OnShown(object? sender, EventArgs e)
    {
        PositionTransformPad();

        // 视口的窗口句柄在窗体 Show 过程中就已建立，ContextReady 可能早于本方法触发 ——
        // 这里补一次（DemoScene.Initialize 自身幂等）。
        if (_viewport.IsReady) OnContextReady(_viewport, EventArgs.Empty);

        AppendLog("拖放 .pmx 载入模型、拖放 .vmd 载入到活跃模型；也可用「文件」菜单打开。");
        _lastTick = Stopwatch.GetTimestamp();
        Application.Idle += OnIdle;
    }

    private void OnFormClosed(object? sender, FormClosedEventArgs e)
    {
        _closing = true;
        Application.Idle -= OnIdle;
        _scene.Dispose();
    }

    private void OnContextReady(object? sender, EventArgs e)
    {
        try
        {
            _scene.Initialize(_viewport);
            _glVersion = _viewport.Gl!.GetStringS(Silk.NET.OpenGL.StringName.Version);
        }
        catch (Exception ex)
        {
            ReportFatal(ex);
            return;
        }
        RefreshReadouts();
    }

    private void OnRenderFailed(object? sender, Exception ex)
    {
        if (_fatalReported) return;
        _fatalReported = true;
        AppendLog($"渲染失败：{ex.Message}");
        MessageBox.Show(this, ex.ToString(), "渲染失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    private void ReportFatal(Exception ex)
    {
        if (_fatalReported) return;
        _fatalReported = true;
        AppendLog($"初始化失败：{ex.Message}");
        MessageBox.Show(this, ex.ToString(), "初始化失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    // ================================================================== 渲染循环

    private void OnIdle(object? sender, EventArgs e)
    {
        if (_closing || _fatalReported || !_viewport.IsReady) return;

        // Application.Idle 在消息队列空时反复触发 ⇒ 这里就是经典的 WinForms + OpenGL 循环；
        // 开了 vsync 时 SwapBuffers 会阻塞到刷新，自然限速到 60 fps。
        while (IsMessageQueueEmpty())
        {
            long now = Stopwatch.GetTimestamp();
            double delta = (now - _lastTick) / (double)Stopwatch.Frequency;
            if (delta < MinFrameSeconds)
            {
                Thread.Sleep(1);
                continue;
            }
            _lastTick = now;
            if (delta > MaxFrameSeconds) delta = MaxFrameSeconds;

            _viewport.DrawFrame(delta);
            if (_fatalReported || _closing) return;

            double instant = delta > 0 ? 1.0 / delta : 0;
            _fps = _fps <= 0 ? instant : _fps * 0.9 + instant * 0.1;

            _readoutTimer += delta;
            if (_readoutTimer >= 0.1)
            {
                _readoutTimer = 0;
                RefreshReadouts();
            }
        }
    }

    private static bool IsMessageQueueEmpty() => !PeekMessage(out _, IntPtr.Zero, 0, 0, 0);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        public IntPtr Handle;
        public uint Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int PointX;
        public int PointY;
    }

    [DllImport("user32.dll", EntryPoint = "PeekMessageW")]
    private static extern bool PeekMessage(out NativeMessage message, IntPtr hWnd, uint filterMin, uint filterMax, uint remove);

    // ================================================================== 模型 / 动画载入

    private void OnOpenModel(object? sender, EventArgs e)
    {
        using var dialog = new OpenFileDialog
        {
            Title = "载入 PMX 模型",
            Filter = "PMX 模型 (*.pmx)|*.pmx|所有文件 (*.*)|*.*",
        };
        if (dialog.ShowDialog(this) == DialogResult.OK) LoadModelFile(dialog.FileName);
    }

    private void OnOpenMotion(object? sender, EventArgs e)
    {
        if (_scene.ActiveModel is not { } target)
        {
            MessageBox.Show(this, "先载入一个模型，再给它载入动画。", "没有活跃模型",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var dialog = new OpenFileDialog
        {
            Title = $"载入动画到「{target.DisplayName}」",
            Filter = "VMD 动作 (*.vmd)|*.vmd|所有文件 (*.*)|*.*",
        };
        if (dialog.ShowDialog(this) == DialogResult.OK) LoadMotionFile(dialog.FileName, target);
    }

    private void OnRemoveModel(object? sender, EventArgs e)
    {
        if (_scene.ActiveModel is not { } model) return;
        _scene.RemoveModel(model);
        RefreshModelCombo();
    }

    private void OnActiveModelChanged(object? sender, EventArgs e)
    {
        if (_suppressComboEvents) return;
        if (_comboModels.SelectedItem is DemoModel model) _scene.SetActiveModel(model);
    }

    private void OnToggleIk(object? sender, EventArgs e)
    {
        // RefreshReadouts 会按活跃模型回写勾选状态，回写本身会触发本事件 ⇒ 用抑制标志挡住回环
        if (_suppressCheckEvents) return;
        _scene.SetActiveIkEnabled(_chkIk.Checked);
    }

    private void LoadModelFile(string path)
    {
        if (!RequireRenderer()) return;
        try
        {
            _scene.LoadModel(path);
            RefreshModelCombo();
        }
        catch (Exception ex)
        {
            AppendLog($"模型载入失败（{Path.GetFileName(path)}）：{ex.Message}");
            MessageBox.Show(this, ex.Message, "模型载入失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void LoadMotionFile(string path, DemoModel target)
    {
        if (!RequireRenderer()) return;
        try
        {
            _scene.LoadMotion(path, target);
        }
        catch (Exception ex)
        {
            AppendLog($"动画载入失败（{Path.GetFileName(path)}）：{ex.Message}");
            MessageBox.Show(this, ex.Message, "动画载入失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private bool RequireRenderer()
    {
        if (_viewport.IsReady) return true;
        AppendLog("渲染尚未就绪，无法载入文件。");
        return false;
    }

    private void RefreshModelCombo()
    {
        _suppressComboEvents = true;
        int selected = _scene.ActiveModel is null ? -1 : _scene.Models.ToList().IndexOf(_scene.ActiveModel);
        _comboModels.Items.Clear();
        foreach (var model in _scene.Models) _comboModels.Items.Add(model);
        if (_comboModels.Items.Count > 0) _comboModels.SelectedIndex = Math.Max(0, selected);
        _suppressComboEvents = false;
        RefreshReadouts();
    }

    // ================================================================== 播放 / 变换

    private void OnTogglePlay(object? sender, EventArgs e) => _scene.TogglePlay();

    private void OnPrevFrame(object? sender, EventArgs e) => _scene.StepFrame(-1);

    private void OnNextFrame(object? sender, EventArgs e) => _scene.StepFrame(1);

    private void OnFirstFrame(object? sender, EventArgs e) => _scene.SeekToStart();

    private void OnPlaybackFpsChanged(object? sender, EventArgs e) => _scene.PlaybackFps = (float)_numFps.Value;

    private void OnResetTransform(object? sender, EventArgs e) => _scene.ResetActiveTransform();

    private void OnFitCamera(object? sender, EventArgs e) => _scene.FrameCamera();

    private void OnTransformDragged(object? sender, TransformPad.NudgeEventArgs e)
    {
        _scene.NudgeActiveTransform(e.Channel, e.Axis, e.Pixels);
        RefreshReadouts();
    }

    // ================================================================== 显示 / 物理菜单

    private void OnToggleEdge(object? sender, EventArgs e) => _scene.SetEdgeVisible(!_scene.EdgeVisible);

    private void OnShadowModeOff(object? sender, EventArgs e) => _scene.SetSelfShadowMode(0);

    private void OnShadowModeSelf(object? sender, EventArgs e) => _scene.SetSelfShadowMode(1);

    private void OnShadowModeFloor(object? sender, EventArgs e) => _scene.SetSelfShadowMode(2);

    private void OnStyleStandard(object? sender, EventArgs e) => _scene.SetShadowStyle(SelfShadowStyle.Standard);

    private void OnStyleThreshold(object? sender, EventArgs e) => _scene.SetShadowStyle(SelfShadowStyle.Threshold);

    private void OnStyleSoft(object? sender, EventArgs e) => _scene.SetShadowStyle(SelfShadowStyle.Soft);

    private void OnTogglePhysics(object? sender, EventArgs e) => _scene.SetPhysicsEnabled(!_scene.PhysicsEnabled);

    private void OnToggleGround(object? sender, EventArgs e) => _scene.SetGroundCollisionEnabled(!_scene.GroundCollisionEnabled);

    private void OnToggleAppend(object? sender, EventArgs e) => _scene.SetPostPhysicsAppendEnabled(!_scene.PostPhysicsAppendEnabled);

    private void OnToggleCameraAnimation(object? sender, EventArgs e)
        => _scene.SetCameraAnimationEnabled(!_scene.CameraAnimationEnabled);

    private void OnExit(object? sender, EventArgs e) => Close();

    private void OnShowHelp(object? sender, EventArgs e)
    {
        MessageBox.Show(this,
            """
            载入
              拖放 .pmx 到窗口任意位置 = 新增模型
              拖放 .vmd 到窗口任意位置 = 载入到当前活跃模型
              下拉框切换活跃模型 = 当前操作对象（TRS 作用对象 + 动画载入目标）
                —— 阴影由全部模型共同投射，切换活跃模型不会改变画面上的影子
              「IK 求解」复选框 = 开关活跃模型的 IK（逐模型保存，默认开）

            视口
              左键拖动 = 旋转 | 右键拖动 = 平移 | 滚轮 = 缩放
              默认视角朝模型正面；左键拖动是「模型跟手」：拖右 → 向右转，拖下 → 露出顶部
              视口右下角变换盘：下排选 移动 / 旋转 / 缩放，
                                上排按住 X / Y / Z 上下拖动（向上 = 正向）

            播放
              启动为暂停；没有载入动画时帧号不推进
              空格 = 播放 / 暂停 | ← / → = 步进 1 帧 | F = 回首帧

            显示
              E = 轮廓线 | 0 / 1 / 2 = 自阴影 关 / 自阴影 / 自阴影+床影 | S = 自阴影风格循环
              P = 物理模拟 | G = 地面碰撞 | H = 物理后付与 | R = 重置模型变换
            """,
            "操作说明", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    // ================================================================== 视口输入

    /// <summary>视口每帧：把上下文已 current 的这一帧交给场景绘制。</summary>
    private void OnViewportRender(double deltaSeconds) => _scene.Draw(deltaSeconds);

    private void OnViewportMouseDown(object? sender, MouseEventArgs e)
    {
        // 视口拿到焦点后滚轮消息才会送到它（见 OnMouseWheel 的兜底分支）
        _viewport.Focus();
        _scene.Input.OnPointerDown(0, e.X, e.Y, ToPointerButton(e.Button));
    }

    private void OnViewportMouseMove(object? sender, MouseEventArgs e)
        => _scene.Input.OnPointerMove(0, e.X, e.Y);

    private void OnViewportMouseUp(object? sender, MouseEventArgs e)
        => _scene.Input.OnPointerUp(0);

    private void OnViewportMouseWheel(object? sender, MouseEventArgs e)
        => _scene.Input.OnScroll(e.Delta / WheelDeltaPerNotch);

    /// <summary>
    /// 滚轮兜底：WM_MOUSEWHEEL 只送给焦点窗口，视口没焦点时消息由别的控件冒泡上来，
    /// 这里按「光标是否在视口内」决定转不转给相机。视口自己有焦点时走上面那条，用
    /// <c>_viewport.Focused</c> 互斥，避免同一次滚动被算两次。
    /// </summary>
    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);

        if (_viewport.Focused || IsTypingTarget(ActiveControl)) return;
        if (!_viewport.ClientRectangle.Contains(_viewport.PointToClient(Cursor.Position))) return;

        _scene.Input.OnScroll(e.Delta / WheelDeltaPerNotch);
    }

    private static OrbitInputController.PointerButton ToPointerButton(MouseButtons button) => button switch
    {
        MouseButtons.Left => OrbitInputController.PointerButton.Left,
        MouseButtons.Right => OrbitInputController.PointerButton.Right,
        MouseButtons.Middle => OrbitInputController.PointerButton.Middle,
        _ => OrbitInputController.PointerButton.None,
    };

    private void PositionTransformPad()
    {
        var host = _viewportHost.ClientSize;
        _transformPad.Location = new Point(
            Math.Max(4, host.Width - _transformPad.Width - 16),
            Math.Max(4, host.Height - _transformPad.Height - 16));
        _transformPad.BringToFront();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        // 输入控件获得焦点时把手势键让给它们，避免打字/调数字被快捷键截走
        if (IsTypingTarget(ActiveControl)) return;

        switch (e.KeyCode)
        {
            case Keys.Space: _scene.TogglePlay(); e.Handled = true; break;
            case Keys.Left: _scene.StepFrame(-1); e.Handled = true; break;
            case Keys.Right: _scene.StepFrame(1); e.Handled = true; break;
            case Keys.F: _scene.SeekToStart(); e.Handled = true; break;
            case Keys.R: _scene.ResetActiveTransform(); e.Handled = true; break;
            case Keys.E: _scene.SetEdgeVisible(!_scene.EdgeVisible); e.Handled = true; break;
            case Keys.S: _scene.CycleShadowStyle(); e.Handled = true; break;
            case Keys.D0: _scene.SetSelfShadowMode(0); e.Handled = true; break;
            case Keys.D1: _scene.SetSelfShadowMode(1); e.Handled = true; break;
            case Keys.D2: _scene.SetSelfShadowMode(2); e.Handled = true; break;
            case Keys.P: _scene.SetPhysicsEnabled(!_scene.PhysicsEnabled); e.Handled = true; break;
            case Keys.G: _scene.SetGroundCollisionEnabled(!_scene.GroundCollisionEnabled); e.Handled = true; break;
            case Keys.H: _scene.SetPostPhysicsAppendEnabled(!_scene.PostPhysicsAppendEnabled); e.Handled = true; break;
            case Keys.C: _scene.SetCameraAnimationEnabled(!_scene.CameraAnimationEnabled); e.Handled = true; break;
        }
    }

    private static bool IsTypingTarget(Control? control)
        => control is TextBoxBase or NumericUpDown or ComboBox;

    // ================================================================== 拖放

    private void EnableDropRecursive(Control control)
    {
        control.AllowDrop = true;
        control.DragEnter += OnDragEnter;
        control.DragDrop += OnDragDrop;
        foreach (Control child in control.Controls) EnableDropRecursive(child);
    }

    private static void OnDragEnter(object? sender, System.Windows.Forms.DragEventArgs e)
    {
        if (!e.Data!.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effect = DragDropEffects.None;
            return;
        }
        string[] files = (string[])e.Data.GetData(DataFormats.FileDrop)!;
        e.Effect = files.Length > 0 && files.All(IsSupportedFile)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
    }

    private void OnDragDrop(object? sender, System.Windows.Forms.DragEventArgs e)
    {
        if (!e.Data!.GetDataPresent(DataFormats.FileDrop)) return;
        string[] files = (string[])e.Data.GetData(DataFormats.FileDrop)!;

        foreach (string file in files)
        {
            switch (Path.GetExtension(file).ToLowerInvariant())
            {
                case ".pmx":
                    LoadModelFile(file);
                    break;

                case ".vmd" when _scene.ActiveModel is { } target:
                    LoadMotionFile(file, target);
                    break;

                case ".vmd":
                    AppendLog("还没有任何模型，.vmd 无处可载（先拖一个 .pmx 进来）");
                    break;
            }
        }
    }

    private static bool IsSupportedFile(string path)
        => Path.GetExtension(path).ToLowerInvariant() is ".pmx" or ".vmd";

    // ================================================================== 读数刷新

    private void RefreshReadouts()
    {
        var active = _scene.ActiveModel;

        _timeline.SetState(active, _scene.Frame, _scene.PlaybackFps, _scene.IsPlaying);
        _btnPlay.Text = _scene.IsPlaying ? "⏸ 暂停" : "▶ 播放";
        _lblTransform.Text = _scene.DescribeActiveTransform();

        // IK 开关逐模型保存 ⇒ 跟着活跃模型走
        _suppressCheckEvents = true;
        _chkIk.Checked = active?.IkEnabled ?? false;
        _chkIk.Enabled = active is not null;
        _suppressCheckEvents = false;

        _transformPad.SetReadout($"全局 · {_transformPad.Channel switch
        {
            TransformChannel.Translate => "移动",
            TransformChannel.Rotate => "旋转",
            _ => "缩放",
        }}");

        _menuEdge.Checked = _scene.EdgeVisible;
        _menuPhysicsEnabled.Checked = _scene.PhysicsEnabled;
        _menuGround.Checked = _scene.GroundCollisionEnabled;
        _menuAppend.Checked = _scene.PostPhysicsAppendEnabled;
        _menuCameraAnimation.Checked = _scene.CameraAnimationEnabled;
        _shadowModeOff.Checked = _scene.SelfShadowMode == 0;
        _shadowModeSelf.Checked = _scene.SelfShadowMode == 1;
        _shadowModeFloor.Checked = _scene.SelfShadowMode == 2;
        _styleStandard.Checked = _scene.ShadowStyle == SelfShadowStyle.Standard;
        _styleThreshold.Checked = _scene.ShadowStyle == SelfShadowStyle.Threshold;
        _styleSoft.Checked = _scene.ShadowStyle == SelfShadowStyle.Soft;

        _statusRight.Text = $"{_fps:F0} fps | 模型 {_scene.Models.Count} | OpenGL {_glVersion}";
    }

    private void AppendLog(string message)
    {
        if (_logBox.Lines.Length > 400)
            _logBox.Lines = _logBox.Lines.Skip(200).ToArray();

        _logBox.AppendText($"{DateTime.Now:HH:mm:ss} {message}{Environment.NewLine}");
        _statusHint.Text = message;
    }
}
