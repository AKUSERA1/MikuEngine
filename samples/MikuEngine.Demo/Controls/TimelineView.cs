using MikuEngine.Demo.Rendering;

namespace MikuEngine.Demo.Controls;

/// <summary>
/// 简化时间线：只播报**当前帧号**与**活跃模型的动画轨道数**（外加区间 / 帧率 / 播放态），
/// 不画关键帧分布、不做拖动定位 —— 播放与步进由操作面板的按钮负责。
/// </summary>
public sealed class TimelineView : UserControl
{
    private readonly Label _frameLabel = new();
    private readonly Label _modelLabel = new();
    private readonly Label _trackLabel = new();
    private readonly Label _rangeLabel = new();

    public TimelineView()
    {
        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Margin = Padding.Empty,
            Padding = new Padding(10, 8, 10, 8),
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 22f));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 22f));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 22f));

        // 帧号用系统界面字体放大：直接指定 "Consolas" 之类的西文字体会让汉字走奇怪的兜底字形
        _frameLabel.Dock = DockStyle.Fill;
        _frameLabel.Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 20f, FontStyle.Bold);
        _frameLabel.ForeColor = Color.FromArgb(40, 44, 60);
        _frameLabel.TextAlign = ContentAlignment.MiddleLeft;
        _frameLabel.Text = "当前帧 0";
        grid.Controls.Add(_frameLabel, 0, 0);

        foreach (var label in new[] { _modelLabel, _trackLabel, _rangeLabel })
        {
            label.Dock = DockStyle.Fill;
            label.TextAlign = ContentAlignment.MiddleLeft;
            label.ForeColor = Color.FromArgb(72, 76, 88);
        }
        grid.Controls.Add(_modelLabel, 0, 1);
        grid.Controls.Add(_trackLabel, 0, 2);
        grid.Controls.Add(_rangeLabel, 0, 3);

        BackColor = Color.FromArgb(248, 248, 250);
        Controls.Add(grid);

        SetState(null, 0, 30f, false);
    }

    /// <summary>刷新读数。命名避开 <see cref="Control.Update"/> / <see cref="Control.Refresh"/>。</summary>
    public void SetState(DemoModel? model, double frame, float fps, bool playing)
    {
        var motion = model?.Motion ?? MotionInfo.None;

        _frameLabel.Text = $"当前帧 {frame:F0}";
        _modelLabel.Text = model is null ? "活跃模型：（未载入）" : $"活跃模型：{model.DisplayName}";
        _trackLabel.Text = $"动画轨道数：{motion.TrackCount}（骨 {motion.BoneTracks} / 表情 {motion.MorphTracks}）";
        _rangeLabel.Text = motion.HasMotion
            ? $"区间：{motion.StartFrame:F0}..{motion.EndFrame:F0} 帧 · {fps:F0} fps · {(playing ? "播放中" : "已暂停")}"
            : $"无动画 · {fps:F0} fps · {(playing ? "播放中" : "已暂停")}";
    }
}
