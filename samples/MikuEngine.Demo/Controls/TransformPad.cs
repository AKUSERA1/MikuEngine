using MikuEngine.Demo.Rendering;

namespace MikuEngine.Demo.Controls;

/// <summary>
/// 视口右下角的模型变换盘 —— MMD 本体 <b>軸ボタン</b> 的精简版，**只做全局模式**。
///
/// 两行 6 个按钮：
/// <list type="bullet">
///   <item><b>下排 移动 / 旋转 / 缩放</b>：选择要调整的分量（互斥）；</item>
///   <item><b>上排 X / Y / Z</b>：按住任意一个**上下拖动** —— 向上为该轴正方向，向下为负方向。</item>
/// </list>
///
/// 控件只报「分量 + 轴 + 像素位移」，量纲换算（每像素多少单位 / 度 / 倍率）在
/// <see cref="ModelTransform"/> 里，手感参数集中一处。
/// </summary>
public sealed class TransformPad : UserControl
{
    /// <summary>一次拖动位移。</summary>
    public sealed class NudgeEventArgs : EventArgs
    {
        public NudgeEventArgs(TransformChannel channel, TransformAxis axis, int pixels)
        {
            Channel = channel;
            Axis = axis;
            Pixels = pixels;
        }

        public TransformChannel Channel { get; }

        public TransformAxis Axis { get; }

        /// <summary>像素位移，向上为正。</summary>
        public int Pixels { get; }
    }

    private static readonly Color[] AxisColors =
    [
        Color.FromArgb(178, 66, 62),    // X 红
        Color.FromArgb(66, 148, 78),    // Y 绿
        Color.FromArgb(62, 104, 190),   // Z 蓝
    ];

    private static readonly string[] AxisNames = ["X", "Y", "Z"];

    private static readonly (string Text, TransformChannel Channel)[] Channels =
    [
        ("移动", TransformChannel.Translate),
        ("旋转", TransformChannel.Rotate),
        ("缩放", TransformChannel.Scale),
    ];

    private readonly Label _readout = new();
    private readonly Button[] _axisButtons = new Button[3];
    private readonly RadioButton[] _channelButtons = new RadioButton[3];
    private int _draggingAxis = -1;
    private int _lastPointerY;

    /// <summary>按住上排按钮上下拖动。</summary>
    public event EventHandler<NudgeEventArgs>? Dragged;

    /// <summary>下排分量选择发生变化。</summary>
    public event EventHandler? ChannelChanged;

    /// <summary>当前选中的分量。</summary>
    public TransformChannel Channel
    {
        get
        {
            for (int i = 0; i < _channelButtons.Length; i++)
                if (_channelButtons[i].Checked) return Channels[i].Channel;
            return TransformChannel.Translate;
        }
    }

    public TransformPad()
    {
        // 用表布局而不是绝对坐标：跟随系统字体 / DPI 缩放，不会错位
        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 3,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / 3f));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / 3f));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / 3f));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 18f));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 50f));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 50f));

        _readout.Dock = DockStyle.Fill;
        _readout.TextAlign = ContentAlignment.MiddleCenter;
        _readout.Text = "全局 · 移动";
        _readout.Margin = Padding.Empty;
        grid.Controls.Add(_readout, 0, 0);
        grid.SetColumnSpan(_readout, 3);

        for (int i = 0; i < AxisNames.Length; i++)
        {
            int axis = i;
            var button = new Button
            {
                Text = AxisNames[i],
                Dock = DockStyle.Fill,
                Margin = new Padding(1),
                FlatStyle = FlatStyle.Flat,
                BackColor = AxisColors[i],
                ForeColor = Color.White,
                Font = new Font(Font, FontStyle.Bold),
                TabStop = false,
                UseVisualStyleBackColor = false,
            };
            button.MouseDown += (_, e) => BeginDrag(axis, e);
            button.MouseMove += (_, e) => ContinueDrag(axis, e);
            button.MouseUp += (_, _) => EndDrag(axis);
            button.MouseCaptureChanged += (_, _) =>
            {
                if (!button.Capture) EndDrag(axis);
            };
            _axisButtons[i] = button;
            grid.Controls.Add(button, i, 1);
        }

        for (int i = 0; i < Channels.Length; i++)
        {
            int index = i;
            var button = new RadioButton
            {
                Text = Channels[i].Text,
                Dock = DockStyle.Fill,
                Margin = new Padding(1),
                Appearance = Appearance.Button,
                TextAlign = ContentAlignment.MiddleCenter,
                FlatStyle = FlatStyle.Flat,
                TabStop = false,
                Checked = i == 0,
            };
            button.CheckedChanged += (_, _) =>
            {
                if (button.Checked) ChannelChanged?.Invoke(this, EventArgs.Empty);
            };
            _channelButtons[i] = button;
            grid.Controls.Add(button, i, 2);
        }

        BorderStyle = BorderStyle.FixedSingle;
        BackColor = Color.FromArgb(44, 46, 54);
        Size = new Size(174, 98);
        Controls.Add(grid);
    }

    /// <summary>由宿主写入读数（例如「全局 · 移动」）。</summary>
    public void SetReadout(string text)
    {
        if (_readout.Text != text) _readout.Text = text;
    }

    private void BeginDrag(int axis, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        _draggingAxis = axis;
        _lastPointerY = e.Y;
        Cursor = Cursors.SizeNS;
    }

    private void ContinueDrag(int axis, MouseEventArgs e)
    {
        if (_draggingAxis != axis) return;
        int pixels = _lastPointerY - e.Y;      // 向上为正
        if (pixels == 0) return;
        _lastPointerY = e.Y;
        Dragged?.Invoke(this, new NudgeEventArgs(Channel, (TransformAxis)axis, pixels));
    }

    private void EndDrag(int axis)
    {
        if (_draggingAxis != axis) return;
        _draggingAxis = -1;
        Cursor = Cursors.Default;
    }
}
