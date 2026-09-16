namespace MikuEngine.Demo;

partial class MainForm
{
    private System.ComponentModel.IContainer components = null;

    private System.Windows.Forms.TableLayoutPanel _root;
    private System.Windows.Forms.MenuStrip _menu;
    private System.Windows.Forms.ToolStripMenuItem _menuFile;
    private System.Windows.Forms.ToolStripMenuItem _menuOpenModel;
    private System.Windows.Forms.ToolStripMenuItem _menuOpenMotion;
    private System.Windows.Forms.ToolStripSeparator _menuFileSeparator;
    private System.Windows.Forms.ToolStripMenuItem _menuExit;
    private System.Windows.Forms.ToolStripMenuItem _menuView;
    private System.Windows.Forms.ToolStripMenuItem _menuEdge;
    private System.Windows.Forms.ToolStripMenuItem _menuShadowMode;
    private System.Windows.Forms.ToolStripMenuItem _shadowModeOff;
    private System.Windows.Forms.ToolStripMenuItem _shadowModeSelf;
    private System.Windows.Forms.ToolStripMenuItem _shadowModeFloor;
    private System.Windows.Forms.ToolStripMenuItem _menuShadowStyle;
    private System.Windows.Forms.ToolStripMenuItem _styleStandard;
    private System.Windows.Forms.ToolStripMenuItem _styleThreshold;
    private System.Windows.Forms.ToolStripMenuItem _styleSoft;
    private System.Windows.Forms.ToolStripSeparator _menuViewSeparator;
    private System.Windows.Forms.ToolStripMenuItem _menuFitCamera;
    private System.Windows.Forms.ToolStripMenuItem _menuPhysics;
    private System.Windows.Forms.ToolStripMenuItem _menuPhysicsEnabled;
    private System.Windows.Forms.ToolStripMenuItem _menuGround;
    private System.Windows.Forms.ToolStripMenuItem _menuAppend;
    private System.Windows.Forms.ToolStripMenuItem _menuAction;
    private System.Windows.Forms.ToolStripMenuItem _menuPlay;
    private System.Windows.Forms.ToolStripMenuItem _menuPrevFrame;
    private System.Windows.Forms.ToolStripMenuItem _menuNextFrame;
    private System.Windows.Forms.ToolStripMenuItem _menuFirstFrame;
    private System.Windows.Forms.ToolStripSeparator _menuActionSeparator;
    private System.Windows.Forms.ToolStripMenuItem _menuCameraAnimation;
    private System.Windows.Forms.ToolStripMenuItem _menuHelp;
    private System.Windows.Forms.ToolStripMenuItem _menuAbout;

    private System.Windows.Forms.TableLayoutPanel _mainTable;
    private System.Windows.Forms.TableLayoutPanel _leftColumn;
    private MikuEngine.Demo.Controls.TimelineView _timeline;
    private System.Windows.Forms.GroupBox _groupLog;
    private System.Windows.Forms.TextBox _logBox;
    private System.Windows.Forms.Panel _viewportHost;
    private MikuEngine.Demo.Rendering.GlViewport _viewport;
    private MikuEngine.Demo.Controls.TransformPad _transformPad;

    private System.Windows.Forms.TableLayoutPanel _bottom;
    private System.Windows.Forms.GroupBox _groupModel;
    private System.Windows.Forms.Button _btnLoadModel;
    private System.Windows.Forms.Label _lblActiveModel;
    private System.Windows.Forms.ComboBox _comboModels;
    private System.Windows.Forms.Button _btnRemoveModel;
    private System.Windows.Forms.CheckBox _chkIk;
    private System.Windows.Forms.GroupBox _groupMotion;
    private System.Windows.Forms.Button _btnLoadMotion;
    private System.Windows.Forms.Button _btnPlay;
    private System.Windows.Forms.Button _btnFirstFrame;
    private System.Windows.Forms.Label _lblFps;
    private System.Windows.Forms.NumericUpDown _numFps;
    private System.Windows.Forms.GroupBox _groupTransform;
    private System.Windows.Forms.Label _lblTransform;
    private System.Windows.Forms.Button _btnResetTransform;
    private System.Windows.Forms.Button _btnFitCamera;
    private System.Windows.Forms.GroupBox _groupLight;
    private System.Windows.Forms.TrackBar _trackR;
    private System.Windows.Forms.TrackBar _trackG;
    private System.Windows.Forms.TrackBar _trackB;
    private System.Windows.Forms.TrackBar _trackX;
    private System.Windows.Forms.TrackBar _trackY;
    private System.Windows.Forms.TrackBar _trackZ;
    private System.Windows.Forms.Label _lblLightR;
    private System.Windows.Forms.Label _lblLightG;
    private System.Windows.Forms.Label _lblLightB;
    private System.Windows.Forms.Label _lblLightX;
    private System.Windows.Forms.Label _lblLightY;
    private System.Windows.Forms.Label _lblLightZ;
    private System.Windows.Forms.Label _lblRVal;
    private System.Windows.Forms.Label _lblGVal;
    private System.Windows.Forms.Label _lblBVal;
    private System.Windows.Forms.Label _lblXVal;
    private System.Windows.Forms.Label _lblYVal;
    private System.Windows.Forms.Label _lblZVal;
    private System.Windows.Forms.Button _btnResetLight;

    private System.Windows.Forms.StatusStrip _status;
    private System.Windows.Forms.ToolStripStatusLabel _statusHint;
    private System.Windows.Forms.ToolStripStatusLabel _statusSpacer;
    private System.Windows.Forms.ToolStripStatusLabel _statusRight;

    protected override void Dispose(bool disposing)
    {
        if (disposing && components != null)
        {
            components.Dispose();
        }
        base.Dispose(disposing);
    }

    #region Windows Form Designer generated code

    /// <summary>
    /// 设计器支持所需的方法 - 不要用代码编辑器修改此方法的内容。
    /// 布局是「倒品字形」：顶部菜单 / 左上时间线 + 消息、右上 3D 视口 / 下方通栏操作面板 / 底部状态栏。
    /// </summary>
    private void InitializeComponent()
    {
        _root = new TableLayoutPanel();
        _menu = new MenuStrip();
        _menuFile = new ToolStripMenuItem();
        _menuOpenModel = new ToolStripMenuItem();
        _menuOpenMotion = new ToolStripMenuItem();
        _menuFileSeparator = new ToolStripSeparator();
        _menuExit = new ToolStripMenuItem();
        _menuView = new ToolStripMenuItem();
        _menuEdge = new ToolStripMenuItem();
        _menuShadowMode = new ToolStripMenuItem();
        _shadowModeOff = new ToolStripMenuItem();
        _shadowModeSelf = new ToolStripMenuItem();
        _shadowModeFloor = new ToolStripMenuItem();
        _menuShadowStyle = new ToolStripMenuItem();
        _styleStandard = new ToolStripMenuItem();
        _styleThreshold = new ToolStripMenuItem();
        _styleSoft = new ToolStripMenuItem();
        _menuViewSeparator = new ToolStripSeparator();
        _menuFitCamera = new ToolStripMenuItem();
        _menuPhysics = new ToolStripMenuItem();
        _menuPhysicsEnabled = new ToolStripMenuItem();
        _menuGround = new ToolStripMenuItem();
        _menuAppend = new ToolStripMenuItem();
        _menuAction = new ToolStripMenuItem();
        _menuPlay = new ToolStripMenuItem();
        _menuPrevFrame = new ToolStripMenuItem();
        _menuNextFrame = new ToolStripMenuItem();
        _menuFirstFrame = new ToolStripMenuItem();
        _menuActionSeparator = new ToolStripSeparator();
        _menuCameraAnimation = new ToolStripMenuItem();
        _menuHelp = new ToolStripMenuItem();
        _menuAbout = new ToolStripMenuItem();
        _mainTable = new TableLayoutPanel();
        _leftColumn = new TableLayoutPanel();
        _timeline = new MikuEngine.Demo.Controls.TimelineView();
        _groupLog = new GroupBox();
        _logBox = new TextBox();
        _viewportHost = new Panel();
        _transformPad = new MikuEngine.Demo.Controls.TransformPad();
        _viewport = new MikuEngine.Demo.Rendering.GlViewport();
        _bottom = new TableLayoutPanel();
        _groupModel = new GroupBox();
        _btnLoadModel = new Button();
        _lblActiveModel = new Label();
        _comboModels = new ComboBox();
        _btnRemoveModel = new Button();
        _chkIk = new CheckBox();
        _groupMotion = new GroupBox();
        _btnLoadMotion = new Button();
        _btnPlay = new Button();
        _btnFirstFrame = new Button();
        _lblFps = new Label();
        _numFps = new NumericUpDown();
        _groupTransform = new GroupBox();
        _lblTransform = new Label();
        _btnResetTransform = new Button();
        _btnFitCamera = new Button();
        _groupLight = new GroupBox();
        _lblLightR = new Label();
        _trackR = new TrackBar();
        _lblRVal = new Label();
        _lblLightG = new Label();
        _trackG = new TrackBar();
        _lblGVal = new Label();
        _lblLightB = new Label();
        _trackB = new TrackBar();
        _lblBVal = new Label();
        _lblLightX = new Label();
        _trackX = new TrackBar();
        _lblXVal = new Label();
        _lblLightY = new Label();
        _trackY = new TrackBar();
        _lblYVal = new Label();
        _lblLightZ = new Label();
        _trackZ = new TrackBar();
        _lblZVal = new Label();
        _btnResetLight = new Button();
        _status = new StatusStrip();
        _statusHint = new ToolStripStatusLabel();
        _statusSpacer = new ToolStripStatusLabel();
        _statusRight = new ToolStripStatusLabel();
        _root.SuspendLayout();
        _menu.SuspendLayout();
        _mainTable.SuspendLayout();
        _leftColumn.SuspendLayout();
        _groupLog.SuspendLayout();
        _viewportHost.SuspendLayout();
        _bottom.SuspendLayout();
        _groupModel.SuspendLayout();
        _groupMotion.SuspendLayout();
        ((System.ComponentModel.ISupportInitialize)_numFps).BeginInit();
        _groupTransform.SuspendLayout();
        _groupLight.SuspendLayout();
        ((System.ComponentModel.ISupportInitialize)_trackR).BeginInit();
        ((System.ComponentModel.ISupportInitialize)_trackG).BeginInit();
        ((System.ComponentModel.ISupportInitialize)_trackB).BeginInit();
        ((System.ComponentModel.ISupportInitialize)_trackX).BeginInit();
        ((System.ComponentModel.ISupportInitialize)_trackY).BeginInit();
        ((System.ComponentModel.ISupportInitialize)_trackZ).BeginInit();
        _status.SuspendLayout();
        SuspendLayout();
        // 
        // _root
        // 
        _root.ColumnCount = 1;
        _root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        _root.Controls.Add(_menu, 0, 0);
        _root.Controls.Add(_mainTable, 0, 1);
        _root.Controls.Add(_bottom, 0, 2);
        _root.Controls.Add(_status, 0, 3);
        _root.Dock = DockStyle.Fill;
        _root.Location = new Point(0, 0);
        _root.Margin = new Padding(0);
        _root.Name = "_root";
        _root.RowCount = 4;
        _root.RowStyles.Add(new RowStyle(SizeType.Absolute, 37F));
        _root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        _root.RowStyles.Add(new RowStyle(SizeType.Absolute, 257F));
        _root.RowStyles.Add(new RowStyle(SizeType.Absolute, 40F));
        _root.Size = new Size(1854, 1101);
        _root.TabIndex = 0;
        // 
        // _menu
        // 
        _menu.AutoSize = false;
        _menu.Dock = DockStyle.Fill;
        _menu.ImageScalingSize = new Size(24, 24);
        _menu.Items.AddRange(new ToolStripItem[] { _menuFile, _menuView, _menuPhysics, _menuAction, _menuHelp });
        _menu.Location = new Point(0, 0);
        _menu.Name = "_menu";
        _menu.Padding = new Padding(14, 4, 0, 4);
        _menu.Size = new Size(1854, 37);
        _menu.TabIndex = 0;
        // 
        // _menuFile
        // 
        _menuFile.DropDownItems.AddRange(new ToolStripItem[] { _menuOpenModel, _menuOpenMotion, _menuFileSeparator, _menuExit });
        _menuFile.Name = "_menuFile";
        _menuFile.Size = new Size(84, 29);
        _menuFile.Text = "文件(&F)";
        // 
        // _menuOpenModel
        // 
        _menuOpenModel.Name = "_menuOpenModel";
        _menuOpenModel.ShortcutKeys = Keys.Control | Keys.O;
        _menuOpenModel.Size = new Size(294, 34);
        _menuOpenModel.Text = "打开模型…(&O)";
        _menuOpenModel.Click += OnOpenModel;
        // 
        // _menuOpenMotion
        // 
        _menuOpenMotion.Name = "_menuOpenMotion";
        _menuOpenMotion.ShortcutKeys = Keys.Control | Keys.M;
        _menuOpenMotion.Size = new Size(294, 34);
        _menuOpenMotion.Text = "打开动画…(&A)";
        _menuOpenMotion.Click += OnOpenMotion;
        // 
        // _menuFileSeparator
        // 
        _menuFileSeparator.Name = "_menuFileSeparator";
        _menuFileSeparator.Size = new Size(291, 6);
        // 
        // _menuExit
        // 
        _menuExit.Name = "_menuExit";
        _menuExit.Size = new Size(294, 34);
        _menuExit.Text = "退出(&X)";
        _menuExit.Click += OnExit;
        // 
        // _menuView
        // 
        _menuView.DropDownItems.AddRange(new ToolStripItem[] { _menuEdge, _menuShadowMode, _menuShadowStyle, _menuViewSeparator, _menuFitCamera });
        _menuView.Name = "_menuView";
        _menuView.Size = new Size(86, 29);
        _menuView.Text = "显示(&V)";
        // 
        // _menuEdge
        // 
        _menuEdge.Name = "_menuEdge";
        _menuEdge.Size = new Size(204, 34);
        _menuEdge.Text = "轮廓线(&E)";
        _menuEdge.Click += OnToggleEdge;
        // 
        // _menuShadowMode
        // 
        _menuShadowMode.DropDownItems.AddRange(new ToolStripItem[] { _shadowModeOff, _shadowModeSelf, _shadowModeFloor });
        _menuShadowMode.Name = "_menuShadowMode";
        _menuShadowMode.Size = new Size(204, 34);
        _menuShadowMode.Text = "自阴影模式";
        // 
        // _shadowModeOff
        // 
        _shadowModeOff.Name = "_shadowModeOff";
        _shadowModeOff.Size = new Size(246, 34);
        _shadowModeOff.Text = "关(&0)";
        _shadowModeOff.Click += OnShadowModeOff;
        // 
        // _shadowModeSelf
        // 
        _shadowModeSelf.Name = "_shadowModeSelf";
        _shadowModeSelf.Size = new Size(246, 34);
        _shadowModeSelf.Text = "自阴影(&1)";
        _shadowModeSelf.Click += OnShadowModeSelf;
        // 
        // _shadowModeFloor
        // 
        _shadowModeFloor.Name = "_shadowModeFloor";
        _shadowModeFloor.Size = new Size(246, 34);
        _shadowModeFloor.Text = "自阴影 + 床影(&2)";
        _shadowModeFloor.Click += OnShadowModeFloor;
        // 
        // _menuShadowStyle
        // 
        _menuShadowStyle.DropDownItems.AddRange(new ToolStripItem[] { _styleStandard, _styleThreshold, _styleSoft });
        _menuShadowStyle.Name = "_menuShadowStyle";
        _menuShadowStyle.Size = new Size(204, 34);
        _menuShadowStyle.Text = "自阴影风格";
        // 
        // _styleStandard
        // 
        _styleStandard.Name = "_styleStandard";
        _styleStandard.Size = new Size(251, 34);
        _styleStandard.Text = "标准本影";
        _styleStandard.Click += OnStyleStandard;
        // 
        // _styleThreshold
        // 
        _styleThreshold.Name = "_styleThreshold";
        _styleThreshold.Size = new Size(251, 34);
        _styleThreshold.Text = "硬边本影";
        _styleThreshold.Click += OnStyleThreshold;
        // 
        // _styleSoft
        // 
        _styleSoft.Name = "_styleSoft";
        _styleSoft.Size = new Size(251, 34);
        _styleSoft.Text = "普通阴影（PCF）";
        _styleSoft.Click += OnStyleSoft;
        // 
        // _menuViewSeparator
        // 
        _menuViewSeparator.Name = "_menuViewSeparator";
        _menuViewSeparator.Size = new Size(201, 6);
        // 
        // _menuFitCamera
        // 
        _menuFitCamera.Name = "_menuFitCamera";
        _menuFitCamera.Size = new Size(204, 34);
        _menuFitCamera.Text = "适应视图(&F)";
        _menuFitCamera.Click += OnFitCamera;
        // 
        // _menuPhysics
        // 
        _menuPhysics.DropDownItems.AddRange(new ToolStripItem[] { _menuPhysicsEnabled, _menuGround, _menuAppend });
        _menuPhysics.Name = "_menuPhysics";
        _menuPhysics.Size = new Size(85, 29);
        _menuPhysics.Text = "物理(&P)";
        // 
        // _menuPhysicsEnabled
        // 
        _menuPhysicsEnabled.Name = "_menuPhysicsEnabled";
        _menuPhysicsEnabled.Size = new Size(226, 34);
        _menuPhysicsEnabled.Text = "物理模拟(&P)";
        _menuPhysicsEnabled.Click += OnTogglePhysics;
        // 
        // _menuGround
        // 
        _menuGround.Name = "_menuGround";
        _menuGround.Size = new Size(226, 34);
        _menuGround.Text = "地面碰撞(&G)";
        _menuGround.Click += OnToggleGround;
        // 
        // _menuAppend
        // 
        _menuAppend.Name = "_menuAppend";
        _menuAppend.Size = new Size(226, 34);
        _menuAppend.Text = "物理后付与(&H)";
        _menuAppend.Click += OnToggleAppend;
        // 
        // _menuAction
        // 
        _menuAction.DropDownItems.AddRange(new ToolStripItem[] { _menuPlay, _menuPrevFrame, _menuNextFrame, _menuFirstFrame, _menuActionSeparator, _menuCameraAnimation });
        _menuAction.Name = "_menuAction";
        _menuAction.Size = new Size(85, 29);
        _menuAction.Text = "动作(&K)";
        // 
        // _menuPlay
        // 
        _menuPlay.Name = "_menuPlay";
        _menuPlay.Size = new Size(263, 34);
        _menuPlay.Text = "播放 / 暂停(&Space)";
        _menuPlay.Click += OnTogglePlay;
        // 
        // _menuPrevFrame
        // 
        _menuPrevFrame.Name = "_menuPrevFrame";
        _menuPrevFrame.Size = new Size(263, 34);
        _menuPrevFrame.Text = "后退 1 帧";
        _menuPrevFrame.Click += OnPrevFrame;
        // 
        // _menuNextFrame
        // 
        _menuNextFrame.Name = "_menuNextFrame";
        _menuNextFrame.Size = new Size(263, 34);
        _menuNextFrame.Text = "前进 1 帧";
        _menuNextFrame.Click += OnNextFrame;
        // 
        // _menuFirstFrame
        // 
        _menuFirstFrame.Name = "_menuFirstFrame";
        _menuFirstFrame.Size = new Size(263, 34);
        _menuFirstFrame.Text = "回到首帧";
        _menuFirstFrame.Click += OnFirstFrame;
        // 
        // _menuActionSeparator
        // 
        _menuActionSeparator.Name = "_menuActionSeparator";
        _menuActionSeparator.Size = new Size(260, 6);
        // 
        // _menuCameraAnimation
        // 
        _menuCameraAnimation.Name = "_menuCameraAnimation";
        _menuCameraAnimation.Size = new Size(263, 34);
        _menuCameraAnimation.Text = "相机动画(&C)";
        _menuCameraAnimation.Click += OnToggleCameraAnimation;
        // 
        // _menuHelp
        // 
        _menuHelp.DropDownItems.AddRange(new ToolStripItem[] { _menuAbout });
        _menuHelp.Name = "_menuHelp";
        _menuHelp.Size = new Size(88, 29);
        _menuHelp.Text = "帮助(&H)";
        // 
        // _menuAbout
        // 
        _menuAbout.Name = "_menuAbout";
        _menuAbout.Size = new Size(207, 34);
        _menuAbout.Text = "操作说明(&A)";
        _menuAbout.Click += OnShowHelp;
        // 
        // _mainTable
        // 
        _mainTable.ColumnCount = 2;
        _mainTable.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 553F));
        _mainTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        _mainTable.Controls.Add(_leftColumn, 0, 0);
        _mainTable.Controls.Add(_viewportHost, 1, 0);
        _mainTable.Dock = DockStyle.Fill;
        _mainTable.Location = new Point(0, 37);
        _mainTable.Margin = new Padding(0);
        _mainTable.Name = "_mainTable";
        _mainTable.Padding = new Padding(9, 8, 9, 0);
        _mainTable.RowCount = 1;
        _mainTable.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        _mainTable.Size = new Size(1854, 767);
        _mainTable.TabIndex = 1;
        // 
        // _leftColumn
        // 
        _leftColumn.ColumnCount = 1;
        _leftColumn.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        _leftColumn.Controls.Add(_timeline, 0, 0);
        _leftColumn.Controls.Add(_groupLog, 0, 1);
        _leftColumn.Dock = DockStyle.Fill;
        _leftColumn.Location = new Point(14, 8);
        _leftColumn.Margin = new Padding(5, 0, 0, 0);
        _leftColumn.Name = "_leftColumn";
        _leftColumn.RowCount = 2;
        _leftColumn.RowStyles.Add(new RowStyle(SizeType.Absolute, 212F));
        _leftColumn.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        _leftColumn.Size = new Size(548, 759);
        _leftColumn.TabIndex = 0;
        // 
        // _timeline
        // 
        _timeline.BackColor = Color.FromArgb(248, 248, 250);
        _timeline.Dock = DockStyle.Fill;
        _timeline.Location = new Point(5, 4);
        _timeline.Margin = new Padding(5, 4, 5, 4);
        _timeline.Name = "_timeline";
        _timeline.Size = new Size(538, 204);
        _timeline.TabIndex = 0;
        // 
        // _groupLog
        // 
        _groupLog.Controls.Add(_logBox);
        _groupLog.Dock = DockStyle.Fill;
        _groupLog.Location = new Point(5, 216);
        _groupLog.Margin = new Padding(5, 4, 5, 4);
        _groupLog.Name = "_groupLog";
        _groupLog.Padding = new Padding(9, 8, 9, 8);
        _groupLog.Size = new Size(538, 539);
        _groupLog.TabIndex = 1;
        _groupLog.TabStop = false;
        _groupLog.Text = "消息";
        // 
        // _logBox
        // 
        _logBox.BackColor = Color.FromArgb(30, 32, 40);
        _logBox.Dock = DockStyle.Fill;
        _logBox.Font = new Font("Consolas", 8.25F);
        _logBox.ForeColor = Color.FromArgb(206, 212, 224);
        _logBox.Location = new Point(9, 31);
        _logBox.Margin = new Padding(5, 4, 5, 4);
        _logBox.Multiline = true;
        _logBox.Name = "_logBox";
        _logBox.ReadOnly = true;
        _logBox.ScrollBars = ScrollBars.Vertical;
        _logBox.Size = new Size(520, 500);
        _logBox.TabIndex = 0;
        _logBox.WordWrap = false;
        // 
        // _viewportHost
        // 
        _viewportHost.BackColor = Color.FromArgb(20, 24, 36);
        _viewportHost.Controls.Add(_transformPad);
        _viewportHost.Controls.Add(_viewport);
        _viewportHost.Dock = DockStyle.Fill;
        _viewportHost.Location = new Point(562, 8);
        _viewportHost.Margin = new Padding(0);
        _viewportHost.Name = "_viewportHost";
        _viewportHost.Size = new Size(1283, 759);
        _viewportHost.TabIndex = 1;
        // 
        // _transformPad
        // 
        _transformPad.BackColor = Color.FromArgb(44, 46, 54);
        _transformPad.BorderStyle = BorderStyle.FixedSingle;
        _transformPad.Location = new Point(1009, 607);
        _transformPad.Margin = new Padding(5, 4, 5, 4);
        _transformPad.Name = "_transformPad";
        _transformPad.Size = new Size(272, 138);
        _transformPad.TabIndex = 0;
        // 
        // _viewport
        // 
        _viewport.BackColor = Color.FromArgb(28, 36, 56);
        _viewport.Dock = DockStyle.Fill;
        _viewport.Location = new Point(0, 0);
        _viewport.Margin = new Padding(5, 4, 5, 4);
        _viewport.Name = "_viewport";
        _viewport.Size = new Size(1283, 759);
        _viewport.TabIndex = 1;
        // 
        // _bottom
        // 
        _bottom.ColumnCount = 4;
        _bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 12.3638344F));
        _bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 12.6906319F));
        _bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 12.5272331F));
        _bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 62.5272331F));
        _bottom.Controls.Add(_groupModel, 0, 0);
        _bottom.Controls.Add(_groupMotion, 1, 0);
        _bottom.Controls.Add(_groupTransform, 2, 0);
        _bottom.Controls.Add(_groupLight, 3, 0);
        _bottom.Dock = DockStyle.Fill;
        _bottom.Location = new Point(0, 804);
        _bottom.Margin = new Padding(0);
        _bottom.Name = "_bottom";
        _bottom.Padding = new Padding(9, 3, 9, 8);
        _bottom.RowCount = 1;
        _bottom.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        _bottom.Size = new Size(1854, 257);
        _bottom.TabIndex = 2;
        // 
        // _groupModel
        // 
        _groupModel.AutoSize = true;
        _groupModel.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _groupModel.Controls.Add(_btnLoadModel);
        _groupModel.Controls.Add(_lblActiveModel);
        _groupModel.Controls.Add(_comboModels);
        _groupModel.Controls.Add(_btnRemoveModel);
        _groupModel.Controls.Add(_chkIk);
        _groupModel.Dock = DockStyle.Fill;
        _groupModel.Location = new Point(14, 7);
        _groupModel.Margin = new Padding(5, 4, 5, 4);
        _groupModel.Name = "_groupModel";
        _groupModel.Padding = new Padding(5, 4, 5, 4);
        _groupModel.Size = new Size(216, 238);
        _groupModel.TabIndex = 0;
        _groupModel.TabStop = false;
        _groupModel.Text = "模型";
        // 
        // _btnLoadModel
        // 
        _btnLoadModel.Location = new Point(17, 37);
        _btnLoadModel.Margin = new Padding(5, 4, 5, 4);
        _btnLoadModel.Name = "_btnLoadModel";
        _btnLoadModel.Size = new Size(182, 42);
        _btnLoadModel.TabIndex = 0;
        _btnLoadModel.Text = "载入模型…";
        _btnLoadModel.Click += OnOpenModel;
        // 
        // _lblActiveModel
        // 
        _lblActiveModel.Location = new Point(17, 82);
        _lblActiveModel.Margin = new Padding(5, 0, 5, 0);
        _lblActiveModel.Name = "_lblActiveModel";
        _lblActiveModel.Size = new Size(182, 25);
        _lblActiveModel.TabIndex = 2;
        _lblActiveModel.Text = "活跃模型";
        // 
        // _comboModels
        // 
        _comboModels.DropDownStyle = ComboBoxStyle.DropDownList;
        _comboModels.Location = new Point(17, 110);
        _comboModels.Margin = new Padding(5, 4, 5, 4);
        _comboModels.Name = "_comboModels";
        _comboModels.Size = new Size(182, 32);
        _comboModels.TabIndex = 3;
        _comboModels.SelectedIndexChanged += OnActiveModelChanged;
        // 
        // _btnRemoveModel
        // 
        _btnRemoveModel.Location = new Point(17, 145);
        _btnRemoveModel.Margin = new Padding(5, 4, 5, 4);
        _btnRemoveModel.Name = "_btnRemoveModel";
        _btnRemoveModel.Size = new Size(182, 34);
        _btnRemoveModel.TabIndex = 4;
        _btnRemoveModel.Text = "移除";
        _btnRemoveModel.Click += OnRemoveModel;
        // 
        // _chkIk
        // 
        _chkIk.Location = new Point(17, 182);
        _chkIk.Margin = new Padding(5, 4, 5, 4);
        _chkIk.Name = "_chkIk";
        _chkIk.Size = new Size(182, 34);
        _chkIk.TabIndex = 6;
        _chkIk.Text = "IK 求解";
        _chkIk.CheckedChanged += OnToggleIk;
        // 
        // _groupMotion
        // 
        _groupMotion.AutoSize = true;
        _groupMotion.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _groupMotion.Controls.Add(_btnLoadMotion);
        _groupMotion.Controls.Add(_btnPlay);
        _groupMotion.Controls.Add(_btnFirstFrame);
        _groupMotion.Controls.Add(_lblFps);
        _groupMotion.Controls.Add(_numFps);
        _groupMotion.Dock = DockStyle.Fill;
        _groupMotion.Location = new Point(240, 7);
        _groupMotion.Margin = new Padding(5, 4, 5, 4);
        _groupMotion.Name = "_groupMotion";
        _groupMotion.Padding = new Padding(5, 4, 5, 4);
        _groupMotion.Size = new Size(222, 238);
        _groupMotion.TabIndex = 1;
        _groupMotion.TabStop = false;
        _groupMotion.Text = "动画";
        // 
        // _btnLoadMotion
        // 
        _btnLoadMotion.Location = new Point(19, 37);
        _btnLoadMotion.Margin = new Padding(5, 4, 5, 4);
        _btnLoadMotion.Name = "_btnLoadMotion";
        _btnLoadMotion.Size = new Size(182, 42);
        _btnLoadMotion.TabIndex = 0;
        _btnLoadMotion.Text = "载入动画…";
        _btnLoadMotion.Click += OnOpenMotion;
        // 
        // _btnPlay
        // 
        _btnPlay.Location = new Point(19, 96);
        _btnPlay.Margin = new Padding(5, 4, 5, 4);
        _btnPlay.Name = "_btnPlay";
        _btnPlay.Size = new Size(86, 42);
        _btnPlay.TabIndex = 2;
        _btnPlay.Text = "⏸ 暂停";
        _btnPlay.Click += OnTogglePlay;
        // 
        // _btnFirstFrame
        // 
        _btnFirstFrame.Location = new Point(115, 96);
        _btnFirstFrame.Margin = new Padding(5, 4, 5, 4);
        _btnFirstFrame.Name = "_btnFirstFrame";
        _btnFirstFrame.Size = new Size(86, 42);
        _btnFirstFrame.TabIndex = 5;
        _btnFirstFrame.Text = "⏮ 首帧";
        _btnFirstFrame.Click += OnFirstFrame;
        // 
        // _lblFps
        // 
        _lblFps.Location = new Point(19, 155);
        _lblFps.Margin = new Padding(5, 0, 5, 0);
        _lblFps.Name = "_lblFps";
        _lblFps.Size = new Size(86, 30);
        _lblFps.TabIndex = 6;
        _lblFps.Text = "推进帧率";
        // 
        // _numFps
        // 
        _numFps.Location = new Point(115, 150);
        _numFps.Margin = new Padding(5, 4, 5, 4);
        _numFps.Maximum = new decimal(new int[] { 240, 0, 0, 0 });
        _numFps.Minimum = new decimal(new int[] { 1, 0, 0, 0 });
        _numFps.Name = "_numFps";
        _numFps.Size = new Size(86, 30);
        _numFps.TabIndex = 7;
        _numFps.Value = new decimal(new int[] { 30, 0, 0, 0 });
        _numFps.ValueChanged += OnPlaybackFpsChanged;
        // 
        // _groupTransform
        // 
        _groupTransform.Controls.Add(_lblTransform);
        _groupTransform.Controls.Add(_btnResetTransform);
        _groupTransform.Controls.Add(_btnFitCamera);
        _groupTransform.Dock = DockStyle.Fill;
        _groupTransform.Location = new Point(472, 7);
        _groupTransform.Margin = new Padding(5, 4, 5, 4);
        _groupTransform.Name = "_groupTransform";
        _groupTransform.Padding = new Padding(5, 4, 5, 4);
        _groupTransform.Size = new Size(219, 238);
        _groupTransform.TabIndex = 2;
        _groupTransform.TabStop = false;
        _groupTransform.Text = "模型变换（全局模式）";
        // 
        // _lblTransform
        // 
        _lblTransform.Font = new Font("Consolas", 9F);
        _lblTransform.Location = new Point(19, 37);
        _lblTransform.Margin = new Padding(5, 0, 5, 0);
        _lblTransform.Name = "_lblTransform";
        _lblTransform.Size = new Size(182, 80);
        _lblTransform.TabIndex = 0;
        _lblTransform.Text = "（未选中模型）";
        // 
        // _btnResetTransform
        // 
        _btnResetTransform.Location = new Point(19, 126);
        _btnResetTransform.Margin = new Padding(5, 4, 5, 4);
        _btnResetTransform.Name = "_btnResetTransform";
        _btnResetTransform.Size = new Size(182, 40);
        _btnResetTransform.TabIndex = 1;
        _btnResetTransform.Text = "重置变换 (R)";
        _btnResetTransform.Click += OnResetTransform;
        // 
        // _btnFitCamera
        // 
        _btnFitCamera.Location = new Point(19, 175);
        _btnFitCamera.Margin = new Padding(5, 4, 5, 4);
        _btnFitCamera.Name = "_btnFitCamera";
        _btnFitCamera.Size = new Size(182, 40);
        _btnFitCamera.TabIndex = 2;
        _btnFitCamera.Text = "适应视图";
        _btnFitCamera.Click += OnFitCamera;
        // 
        // _groupLight
        // 
        _groupLight.Controls.Add(_lblLightR);
        _groupLight.Controls.Add(_trackR);
        _groupLight.Controls.Add(_lblRVal);
        _groupLight.Controls.Add(_lblLightG);
        _groupLight.Controls.Add(_trackG);
        _groupLight.Controls.Add(_lblGVal);
        _groupLight.Controls.Add(_lblLightB);
        _groupLight.Controls.Add(_trackB);
        _groupLight.Controls.Add(_lblBVal);
        _groupLight.Controls.Add(_lblLightX);
        _groupLight.Controls.Add(_trackX);
        _groupLight.Controls.Add(_lblXVal);
        _groupLight.Controls.Add(_lblLightY);
        _groupLight.Controls.Add(_trackY);
        _groupLight.Controls.Add(_lblYVal);
        _groupLight.Controls.Add(_lblLightZ);
        _groupLight.Controls.Add(_trackZ);
        _groupLight.Controls.Add(_lblZVal);
        _groupLight.Controls.Add(_btnResetLight);
        _groupLight.Dock = DockStyle.Fill;
        _groupLight.Location = new Point(701, 7);
        _groupLight.Margin = new Padding(5, 4, 5, 4);
        _groupLight.Name = "_groupLight";
        _groupLight.Padding = new Padding(5, 4, 5, 4);
        _groupLight.Size = new Size(1139, 238);
        _groupLight.TabIndex = 3;
        _groupLight.TabStop = false;
        _groupLight.Text = "光源";
        // 
        // _lblLightR
        // 
        _lblLightR.Location = new Point(9, 34);
        _lblLightR.Margin = new Padding(5, 0, 5, 0);
        _lblLightR.Name = "_lblLightR";
        _lblLightR.Size = new Size(22, 25);
        _lblLightR.TabIndex = 0;
        _lblLightR.Text = "R";
        // 
        // _trackR
        // 
        _trackR.AutoSize = false;
        _trackR.Location = new Point(35, 31);
        _trackR.Margin = new Padding(5, 4, 5, 4);
        _trackR.Maximum = 255;
        _trackR.Name = "_trackR";
        _trackR.Size = new Size(151, 31);
        _trackR.TabIndex = 1;
        _trackR.TickStyle = TickStyle.None;
        _trackR.Value = 128;
        _trackR.ValueChanged += OnLightSliderChanged;
        // 
        // _lblRVal
        // 
        _lblRVal.ForeColor = Color.FromArgb(96, 100, 112);
        _lblRVal.Location = new Point(192, 35);
        _lblRVal.Margin = new Padding(5, 0, 5, 0);
        _lblRVal.Name = "_lblRVal";
        _lblRVal.Size = new Size(47, 23);
        _lblRVal.TabIndex = 2;
        _lblRVal.Text = "128";
        // 
        // _lblLightG
        // 
        _lblLightG.Location = new Point(9, 69);
        _lblLightG.Margin = new Padding(5, 0, 5, 0);
        _lblLightG.Name = "_lblLightG";
        _lblLightG.Size = new Size(22, 25);
        _lblLightG.TabIndex = 3;
        _lblLightG.Text = "G";
        // 
        // _trackG
        // 
        _trackG.AutoSize = false;
        _trackG.Location = new Point(35, 66);
        _trackG.Margin = new Padding(5, 4, 5, 4);
        _trackG.Maximum = 255;
        _trackG.Name = "_trackG";
        _trackG.Size = new Size(151, 31);
        _trackG.TabIndex = 4;
        _trackG.TickStyle = TickStyle.None;
        _trackG.Value = 128;
        _trackG.ValueChanged += OnLightSliderChanged;
        // 
        // _lblGVal
        // 
        _lblGVal.ForeColor = Color.FromArgb(96, 100, 112);
        _lblGVal.Location = new Point(192, 70);
        _lblGVal.Margin = new Padding(5, 0, 5, 0);
        _lblGVal.Name = "_lblGVal";
        _lblGVal.Size = new Size(47, 23);
        _lblGVal.TabIndex = 5;
        _lblGVal.Text = "128";
        // 
        // _lblLightB
        // 
        _lblLightB.Location = new Point(9, 104);
        _lblLightB.Margin = new Padding(5, 0, 5, 0);
        _lblLightB.Name = "_lblLightB";
        _lblLightB.Size = new Size(22, 25);
        _lblLightB.TabIndex = 6;
        _lblLightB.Text = "B";
        // 
        // _trackB
        // 
        _trackB.AutoSize = false;
        _trackB.Location = new Point(35, 101);
        _trackB.Margin = new Padding(5, 4, 5, 4);
        _trackB.Maximum = 255;
        _trackB.Name = "_trackB";
        _trackB.Size = new Size(151, 31);
        _trackB.TabIndex = 7;
        _trackB.TickStyle = TickStyle.None;
        _trackB.Value = 128;
        _trackB.ValueChanged += OnLightSliderChanged;
        // 
        // _lblBVal
        // 
        _lblBVal.ForeColor = Color.FromArgb(96, 100, 112);
        _lblBVal.Location = new Point(192, 105);
        _lblBVal.Margin = new Padding(5, 0, 5, 0);
        _lblBVal.Name = "_lblBVal";
        _lblBVal.Size = new Size(47, 23);
        _lblBVal.TabIndex = 8;
        _lblBVal.Text = "128";
        // 
        // _lblLightX
        // 
        _lblLightX.Location = new Point(10, 139);
        _lblLightX.Margin = new Padding(5, 0, 5, 0);
        _lblLightX.Name = "_lblLightX";
        _lblLightX.Size = new Size(22, 25);
        _lblLightX.TabIndex = 9;
        _lblLightX.Text = "X";
        // 
        // _trackX
        // 
        _trackX.AutoSize = false;
        _trackX.Location = new Point(35, 136);
        _trackX.Margin = new Padding(5, 4, 5, 4);
        _trackX.Maximum = 100;
        _trackX.Minimum = -100;
        _trackX.Name = "_trackX";
        _trackX.Size = new Size(151, 31);
        _trackX.TabIndex = 10;
        _trackX.TickStyle = TickStyle.None;
        _trackX.Value = -50;
        _trackX.ValueChanged += OnLightSliderChanged;
        // 
        // _lblXVal
        // 
        _lblXVal.ForeColor = Color.FromArgb(96, 100, 112);
        _lblXVal.Location = new Point(192, 140);
        _lblXVal.Margin = new Padding(5, 0, 5, 0);
        _lblXVal.Name = "_lblXVal";
        _lblXVal.Size = new Size(60, 23);
        _lblXVal.TabIndex = 11;
        _lblXVal.Text = "-0.50";
        // 
        // _lblLightY
        // 
        _lblLightY.Location = new Point(10, 174);
        _lblLightY.Margin = new Padding(5, 0, 5, 0);
        _lblLightY.Name = "_lblLightY";
        _lblLightY.Size = new Size(22, 25);
        _lblLightY.TabIndex = 12;
        _lblLightY.Text = "Y";
        // 
        // _trackY
        // 
        _trackY.AutoSize = false;
        _trackY.Location = new Point(35, 171);
        _trackY.Margin = new Padding(5, 4, 5, 4);
        _trackY.Maximum = 100;
        _trackY.Minimum = -100;
        _trackY.Name = "_trackY";
        _trackY.Size = new Size(151, 31);
        _trackY.TabIndex = 13;
        _trackY.TickStyle = TickStyle.None;
        _trackY.Value = -100;
        _trackY.ValueChanged += OnLightSliderChanged;
        // 
        // _lblYVal
        // 
        _lblYVal.ForeColor = Color.FromArgb(96, 100, 112);
        _lblYVal.Location = new Point(192, 175);
        _lblYVal.Margin = new Padding(5, 0, 5, 0);
        _lblYVal.Name = "_lblYVal";
        _lblYVal.Size = new Size(60, 23);
        _lblYVal.TabIndex = 14;
        _lblYVal.Text = "-1.00";
        // 
        // _lblLightZ
        // 
        _lblLightZ.Location = new Point(10, 209);
        _lblLightZ.Margin = new Padding(5, 0, 5, 0);
        _lblLightZ.Name = "_lblLightZ";
        _lblLightZ.Size = new Size(22, 25);
        _lblLightZ.TabIndex = 15;
        _lblLightZ.Text = "Z";
        // 
        // _trackZ
        // 
        _trackZ.AutoSize = false;
        _trackZ.Location = new Point(35, 206);
        _trackZ.Margin = new Padding(5, 4, 5, 4);
        _trackZ.Maximum = 100;
        _trackZ.Minimum = -100;
        _trackZ.Name = "_trackZ";
        _trackZ.Size = new Size(151, 31);
        _trackZ.TabIndex = 16;
        _trackZ.TickStyle = TickStyle.None;
        _trackZ.Value = 50;
        _trackZ.ValueChanged += OnLightSliderChanged;
        // 
        // _lblZVal
        // 
        _lblZVal.ForeColor = Color.FromArgb(96, 100, 112);
        _lblZVal.Location = new Point(192, 210);
        _lblZVal.Margin = new Padding(5, 0, 5, 0);
        _lblZVal.Name = "_lblZVal";
        _lblZVal.Size = new Size(60, 23);
        _lblZVal.TabIndex = 17;
        _lblZVal.Text = "0.50";
        // 
        // _btnResetLight
        // 
        _btnResetLight.Location = new Point(263, 34);
        _btnResetLight.Margin = new Padding(5, 4, 5, 4);
        _btnResetLight.Name = "_btnResetLight";
        _btnResetLight.Size = new Size(113, 37);
        _btnResetLight.TabIndex = 18;
        _btnResetLight.Text = "重置";
        _btnResetLight.Click += OnResetLight;
        // 
        // _status
        // 
        _status.Dock = DockStyle.Fill;
        _status.ImageScalingSize = new Size(24, 24);
        _status.Items.AddRange(new ToolStripItem[] { _statusHint, _statusSpacer, _statusRight });
        _status.Location = new Point(0, 1061);
        _status.Name = "_status";
        _status.Padding = new Padding(2, 0, 22, 0);
        _status.Size = new Size(1854, 40);
        _status.SizingGrip = false;
        _status.TabIndex = 3;
        // 
        // _statusHint
        // 
        _statusHint.Name = "_statusHint";
        _statusHint.Size = new Size(46, 33);
        _statusHint.Text = "就绪";
        // 
        // _statusSpacer
        // 
        _statusSpacer.Name = "_statusSpacer";
        _statusSpacer.Size = new Size(1766, 33);
        _statusSpacer.Spring = true;
        // 
        // _statusRight
        // 
        _statusRight.Name = "_statusRight";
        _statusRight.Size = new Size(18, 33);
        _statusRight.Text = "-";
        // 
        // MainForm
        // 
        AutoScaleDimensions = new SizeF(11F, 24F);
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(1854, 1101);
        Controls.Add(_root);
        KeyPreview = true;
        Margin = new Padding(5, 4, 5, 4);
        MinimumSize = new Size(1496, 880);
        Name = "MainForm";
        Text = "MikuEngine Demo";
        _root.ResumeLayout(false);
        _root.PerformLayout();
        _menu.ResumeLayout(false);
        _menu.PerformLayout();
        _mainTable.ResumeLayout(false);
        _leftColumn.ResumeLayout(false);
        _groupLog.ResumeLayout(false);
        _groupLog.PerformLayout();
        _viewportHost.ResumeLayout(false);
        _bottom.ResumeLayout(false);
        _bottom.PerformLayout();
        _groupModel.ResumeLayout(false);
        _groupMotion.ResumeLayout(false);
        ((System.ComponentModel.ISupportInitialize)_numFps).EndInit();
        _groupTransform.ResumeLayout(false);
        _groupLight.ResumeLayout(false);
        ((System.ComponentModel.ISupportInitialize)_trackR).EndInit();
        ((System.ComponentModel.ISupportInitialize)_trackG).EndInit();
        ((System.ComponentModel.ISupportInitialize)_trackB).EndInit();
        ((System.ComponentModel.ISupportInitialize)_trackX).EndInit();
        ((System.ComponentModel.ISupportInitialize)_trackY).EndInit();
        ((System.ComponentModel.ISupportInitialize)_trackZ).EndInit();
        _status.ResumeLayout(false);
        _status.PerformLayout();
        ResumeLayout(false);
    }

    #endregion
}
