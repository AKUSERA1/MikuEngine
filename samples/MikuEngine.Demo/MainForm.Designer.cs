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
    private System.Windows.Forms.Label _lblModelHint;
    private System.Windows.Forms.Label _lblActiveModel;
    private System.Windows.Forms.ComboBox _comboModels;
    private System.Windows.Forms.Button _btnRemoveModel;
    private System.Windows.Forms.Label _lblModelNote;
    private System.Windows.Forms.CheckBox _chkIk;
    private System.Windows.Forms.GroupBox _groupMotion;
    private System.Windows.Forms.Button _btnLoadMotion;
    private System.Windows.Forms.Label _lblMotionHint;
    private System.Windows.Forms.Button _btnPlay;
    private System.Windows.Forms.Button _btnPrevFrame;
    private System.Windows.Forms.Button _btnNextFrame;
    private System.Windows.Forms.Button _btnFirstFrame;
    private System.Windows.Forms.Label _lblFps;
    private System.Windows.Forms.NumericUpDown _numFps;
    private System.Windows.Forms.Label _lblMotionNote;
    private System.Windows.Forms.GroupBox _groupTransform;
    private System.Windows.Forms.Label _lblTransform;
    private System.Windows.Forms.Button _btnResetTransform;
    private System.Windows.Forms.Button _btnFitCamera;
    private System.Windows.Forms.Label _lblTransformHint;

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
        this.components = new System.ComponentModel.Container();
        this._root = new System.Windows.Forms.TableLayoutPanel();
        this._menu = new System.Windows.Forms.MenuStrip();
        this._menuFile = new System.Windows.Forms.ToolStripMenuItem();
        this._menuOpenModel = new System.Windows.Forms.ToolStripMenuItem();
        this._menuOpenMotion = new System.Windows.Forms.ToolStripMenuItem();
        this._menuFileSeparator = new System.Windows.Forms.ToolStripSeparator();
        this._menuExit = new System.Windows.Forms.ToolStripMenuItem();
        this._menuView = new System.Windows.Forms.ToolStripMenuItem();
        this._menuEdge = new System.Windows.Forms.ToolStripMenuItem();
        this._menuShadowMode = new System.Windows.Forms.ToolStripMenuItem();
        this._shadowModeOff = new System.Windows.Forms.ToolStripMenuItem();
        this._shadowModeSelf = new System.Windows.Forms.ToolStripMenuItem();
        this._shadowModeFloor = new System.Windows.Forms.ToolStripMenuItem();
        this._menuShadowStyle = new System.Windows.Forms.ToolStripMenuItem();
        this._styleStandard = new System.Windows.Forms.ToolStripMenuItem();
        this._styleThreshold = new System.Windows.Forms.ToolStripMenuItem();
        this._styleSoft = new System.Windows.Forms.ToolStripMenuItem();
        this._menuViewSeparator = new System.Windows.Forms.ToolStripSeparator();
        this._menuFitCamera = new System.Windows.Forms.ToolStripMenuItem();
        this._menuPhysics = new System.Windows.Forms.ToolStripMenuItem();
        this._menuPhysicsEnabled = new System.Windows.Forms.ToolStripMenuItem();
        this._menuGround = new System.Windows.Forms.ToolStripMenuItem();
        this._menuAppend = new System.Windows.Forms.ToolStripMenuItem();
        this._menuAction = new System.Windows.Forms.ToolStripMenuItem();
        this._menuPlay = new System.Windows.Forms.ToolStripMenuItem();
        this._menuPrevFrame = new System.Windows.Forms.ToolStripMenuItem();
        this._menuNextFrame = new System.Windows.Forms.ToolStripMenuItem();
        this._menuFirstFrame = new System.Windows.Forms.ToolStripMenuItem();
        this._menuActionSeparator = new System.Windows.Forms.ToolStripSeparator();
        this._menuCameraAnimation = new System.Windows.Forms.ToolStripMenuItem();
        this._menuHelp = new System.Windows.Forms.ToolStripMenuItem();
        this._menuAbout = new System.Windows.Forms.ToolStripMenuItem();
        this._mainTable = new System.Windows.Forms.TableLayoutPanel();
        this._leftColumn = new System.Windows.Forms.TableLayoutPanel();
        this._timeline = new MikuEngine.Demo.Controls.TimelineView();
        this._groupLog = new System.Windows.Forms.GroupBox();
        this._logBox = new System.Windows.Forms.TextBox();
        this._viewportHost = new System.Windows.Forms.Panel();
        this._viewport = new MikuEngine.Demo.Rendering.GlViewport();
        this._transformPad = new MikuEngine.Demo.Controls.TransformPad();
        this._bottom = new System.Windows.Forms.TableLayoutPanel();
        this._groupModel = new System.Windows.Forms.GroupBox();
        this._btnLoadModel = new System.Windows.Forms.Button();
        this._lblModelHint = new System.Windows.Forms.Label();
        this._lblActiveModel = new System.Windows.Forms.Label();
        this._comboModels = new System.Windows.Forms.ComboBox();
        this._btnRemoveModel = new System.Windows.Forms.Button();
        this._lblModelNote = new System.Windows.Forms.Label();
        this._chkIk = new System.Windows.Forms.CheckBox();
        this._groupMotion = new System.Windows.Forms.GroupBox();
        this._btnLoadMotion = new System.Windows.Forms.Button();
        this._lblMotionHint = new System.Windows.Forms.Label();
        this._btnPlay = new System.Windows.Forms.Button();
        this._btnPrevFrame = new System.Windows.Forms.Button();
        this._btnNextFrame = new System.Windows.Forms.Button();
        this._btnFirstFrame = new System.Windows.Forms.Button();
        this._lblFps = new System.Windows.Forms.Label();
        this._numFps = new System.Windows.Forms.NumericUpDown();
        this._lblMotionNote = new System.Windows.Forms.Label();
        this._groupTransform = new System.Windows.Forms.GroupBox();
        this._lblTransform = new System.Windows.Forms.Label();
        this._btnResetTransform = new System.Windows.Forms.Button();
        this._btnFitCamera = new System.Windows.Forms.Button();
        this._lblTransformHint = new System.Windows.Forms.Label();
        this._status = new System.Windows.Forms.StatusStrip();
        this._statusHint = new System.Windows.Forms.ToolStripStatusLabel();
        this._statusSpacer = new System.Windows.Forms.ToolStripStatusLabel();
        this._statusRight = new System.Windows.Forms.ToolStripStatusLabel();
        this._root.SuspendLayout();
        this._menu.SuspendLayout();
        this._mainTable.SuspendLayout();
        this._leftColumn.SuspendLayout();
        this._groupLog.SuspendLayout();
        this._viewportHost.SuspendLayout();
        this._bottom.SuspendLayout();
        this._groupModel.SuspendLayout();
        this._groupMotion.SuspendLayout();
        ((System.ComponentModel.ISupportInitialize)(this._numFps)).BeginInit();
        this._groupTransform.SuspendLayout();
        this._status.SuspendLayout();
        this.SuspendLayout();
        //
        // _menu
        //
        this._menu.Dock = System.Windows.Forms.DockStyle.Fill;
        this._menu.AutoSize = false;
        this._menu.Items.AddRange(new System.Windows.Forms.ToolStripItem[] {
            this._menuFile,
            this._menuView,
            this._menuPhysics,
            this._menuAction,
            this._menuHelp});
        //
        // _menuFile
        //
        this._menuFile.Text = "文件(&F)";
        this._menuFile.DropDownItems.AddRange(new System.Windows.Forms.ToolStripItem[] {
            this._menuOpenModel,
            this._menuOpenMotion,
            this._menuFileSeparator,
            this._menuExit});
        //
        // _menuOpenModel
        //
        this._menuOpenModel.Text = "打开模型…(&O)";
        this._menuOpenModel.ShortcutKeys = ((System.Windows.Forms.Keys)((System.Windows.Forms.Keys.Control | System.Windows.Forms.Keys.O)));
        this._menuOpenModel.Click += new System.EventHandler(this.OnOpenModel);
        //
        // _menuOpenMotion
        //
        this._menuOpenMotion.Text = "打开动画…(&A)";
        this._menuOpenMotion.ShortcutKeys = ((System.Windows.Forms.Keys)((System.Windows.Forms.Keys.Control | System.Windows.Forms.Keys.M)));
        this._menuOpenMotion.Click += new System.EventHandler(this.OnOpenMotion);
        //
        // _menuExit
        //
        this._menuExit.Text = "退出(&X)";
        this._menuExit.Click += new System.EventHandler(this.OnExit);
        //
        // _menuView
        //
        this._menuView.Text = "显示(&V)";
        this._menuView.DropDownItems.AddRange(new System.Windows.Forms.ToolStripItem[] {
            this._menuEdge,
            this._menuShadowMode,
            this._menuShadowStyle,
            this._menuViewSeparator,
            this._menuFitCamera});
        //
        // _menuEdge
        //
        this._menuEdge.Text = "轮廓线(&E)";
        this._menuEdge.CheckOnClick = false;
        this._menuEdge.Click += new System.EventHandler(this.OnToggleEdge);
        //
        // _menuShadowMode
        //
        this._menuShadowMode.Text = "自阴影模式";
        this._menuShadowMode.DropDownItems.AddRange(new System.Windows.Forms.ToolStripItem[] {
            this._shadowModeOff,
            this._shadowModeSelf,
            this._shadowModeFloor});
        //
        // _shadowModeOff
        //
        this._shadowModeOff.Text = "关(&0)";
        this._shadowModeOff.Click += new System.EventHandler(this.OnShadowModeOff);
        //
        // _shadowModeSelf
        //
        this._shadowModeSelf.Text = "自阴影(&1)";
        this._shadowModeSelf.Click += new System.EventHandler(this.OnShadowModeSelf);
        //
        // _shadowModeFloor
        //
        this._shadowModeFloor.Text = "自阴影 + 床影(&2)";
        this._shadowModeFloor.Click += new System.EventHandler(this.OnShadowModeFloor);
        //
        // _menuShadowStyle
        //
        this._menuShadowStyle.Text = "自阴影风格";
        this._menuShadowStyle.DropDownItems.AddRange(new System.Windows.Forms.ToolStripItem[] {
            this._styleStandard,
            this._styleThreshold,
            this._styleSoft});
        //
        // _styleStandard
        //
        this._styleStandard.Text = "标准本影";
        this._styleStandard.Click += new System.EventHandler(this.OnStyleStandard);
        //
        // _styleThreshold
        //
        this._styleThreshold.Text = "硬边本影";
        this._styleThreshold.Click += new System.EventHandler(this.OnStyleThreshold);
        //
        // _styleSoft
        //
        this._styleSoft.Text = "普通阴影（PCF）";
        this._styleSoft.Click += new System.EventHandler(this.OnStyleSoft);
        //
        // _menuFitCamera
        //
        this._menuFitCamera.Text = "适应视图(&F)";
        this._menuFitCamera.Click += new System.EventHandler(this.OnFitCamera);
        //
        // _menuPhysics
        //
        this._menuPhysics.Text = "物理(&P)";
        this._menuPhysics.DropDownItems.AddRange(new System.Windows.Forms.ToolStripItem[] {
            this._menuPhysicsEnabled,
            this._menuGround,
            this._menuAppend});
        //
        // _menuPhysicsEnabled
        //
        this._menuPhysicsEnabled.Text = "物理模拟(&P)";
        this._menuPhysicsEnabled.CheckOnClick = false;
        this._menuPhysicsEnabled.Click += new System.EventHandler(this.OnTogglePhysics);
        //
        // _menuGround
        //
        this._menuGround.Text = "地面碰撞(&G)";
        this._menuGround.CheckOnClick = false;
        this._menuGround.Click += new System.EventHandler(this.OnToggleGround);
        //
        // _menuAppend
        //
        this._menuAppend.Text = "物理后付与(&H)";
        this._menuAppend.CheckOnClick = false;
        this._menuAppend.Click += new System.EventHandler(this.OnToggleAppend);
        //
        // _menuAction
        //
        this._menuAction.Text = "动作(&K)";
        this._menuAction.DropDownItems.AddRange(new System.Windows.Forms.ToolStripItem[] {
            this._menuPlay,
            this._menuPrevFrame,
            this._menuNextFrame,
            this._menuFirstFrame,
            this._menuActionSeparator,
            this._menuCameraAnimation});
        //
        // _menuPlay
        //
        this._menuPlay.Text = "播放 / 暂停(&Space)";
        this._menuPlay.Click += new System.EventHandler(this.OnTogglePlay);
        //
        // _menuPrevFrame
        //
        this._menuPrevFrame.Text = "后退 1 帧";
        this._menuPrevFrame.Click += new System.EventHandler(this.OnPrevFrame);
        //
        // _menuNextFrame
        //
        this._menuNextFrame.Text = "前进 1 帧";
        this._menuNextFrame.Click += new System.EventHandler(this.OnNextFrame);
        //
        // _menuFirstFrame
        //
        this._menuFirstFrame.Text = "回到首帧";
        this._menuFirstFrame.Click += new System.EventHandler(this.OnFirstFrame);
        //
        // _menuCameraAnimation
        //
        this._menuCameraAnimation.Text = "相机动画(&C)";
        this._menuCameraAnimation.CheckOnClick = false;
        this._menuCameraAnimation.Click += new System.EventHandler(this.OnToggleCameraAnimation);
        //
        // _menuHelp
        //
        this._menuHelp.Text = "帮助(&H)";
        this._menuHelp.DropDownItems.AddRange(new System.Windows.Forms.ToolStripItem[] {
            this._menuAbout});
        //
        // _menuAbout
        //
        this._menuAbout.Text = "操作说明(&A)";
        this._menuAbout.Click += new System.EventHandler(this.OnShowHelp);
        //
        // _viewportHost
        //
        this._viewportHost.Dock = System.Windows.Forms.DockStyle.Fill;
        this._viewportHost.Margin = new System.Windows.Forms.Padding(0);
        this._viewportHost.BackColor = System.Drawing.Color.FromArgb(20, 24, 36);
        this._viewportHost.Controls.Add(this._transformPad);
        this._viewportHost.Controls.Add(this._viewport);
        //
        // _viewport
        //
        this._viewport.Dock = System.Windows.Forms.DockStyle.Fill;
        this._viewport.Location = new System.Drawing.Point(0, 0);
        this._viewport.Size = new System.Drawing.Size(832, 544);
        //
        // _transformPad
        //
        this._transformPad.Location = new System.Drawing.Point(642, 430);
        this._transformPad.Size = new System.Drawing.Size(174, 98);
        //
        // _timeline
        //
        this._timeline.Dock = System.Windows.Forms.DockStyle.Fill;
        //
        // _logBox
        //
        this._logBox.Dock = System.Windows.Forms.DockStyle.Fill;
        this._logBox.Multiline = true;
        this._logBox.ReadOnly = true;
        this._logBox.ScrollBars = System.Windows.Forms.ScrollBars.Vertical;
        this._logBox.WordWrap = false;
        this._logBox.Font = new System.Drawing.Font("Consolas", 8.25F);
        this._logBox.BackColor = System.Drawing.Color.FromArgb(30, 32, 40);
        this._logBox.ForeColor = System.Drawing.Color.FromArgb(206, 212, 224);
        //
        // _groupLog
        //
        this._groupLog.Text = "消息";
        this._groupLog.Dock = System.Windows.Forms.DockStyle.Fill;
        this._groupLog.Padding = new System.Windows.Forms.Padding(6);
        this._groupLog.Controls.Add(this._logBox);
        //
        // _leftColumn
        //
        this._leftColumn.Dock = System.Windows.Forms.DockStyle.Fill;
        this._leftColumn.ColumnCount = 1;
        this._leftColumn.RowCount = 2;
        this._leftColumn.Margin = new System.Windows.Forms.Padding(3, 0, 0, 0);
        this._leftColumn.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 100F));
        this._leftColumn.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Absolute, 150F));
        this._leftColumn.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 100F));
        this._leftColumn.Controls.Add(this._timeline, 0, 0);
        this._leftColumn.Controls.Add(this._groupLog, 0, 1);
        //
        // _mainTable
        //
        this._mainTable.Dock = System.Windows.Forms.DockStyle.Fill;
        this._mainTable.ColumnCount = 2;
        this._mainTable.RowCount = 1;
        this._mainTable.Margin = new System.Windows.Forms.Padding(0);
        this._mainTable.Padding = new System.Windows.Forms.Padding(6, 6, 6, 0);
        this._mainTable.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Absolute, 352F));
        this._mainTable.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 100F));
        this._mainTable.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 100F));
        this._mainTable.Controls.Add(this._leftColumn, 0, 0);
        this._mainTable.Controls.Add(this._viewportHost, 1, 0);
        //
        // _groupModel
        //
        this._groupModel.Text = "模型";
        this._groupModel.Dock = System.Windows.Forms.DockStyle.Fill;
        this._groupModel.Controls.Add(this._btnLoadModel);
        this._groupModel.Controls.Add(this._lblModelHint);
        this._groupModel.Controls.Add(this._lblActiveModel);
        this._groupModel.Controls.Add(this._comboModels);
        this._groupModel.Controls.Add(this._btnRemoveModel);
        this._groupModel.Controls.Add(this._lblModelNote);
        this._groupModel.Controls.Add(this._chkIk);
        //
        // _btnLoadModel
        //
        this._btnLoadModel.Text = "载入模型…";
        this._btnLoadModel.Location = new System.Drawing.Point(12, 26);
        this._btnLoadModel.Size = new System.Drawing.Size(116, 30);
        this._btnLoadModel.Click += new System.EventHandler(this.OnOpenModel);
        //
        // _lblModelHint
        //
        this._lblModelHint.Text = "拖放 .pmx 到窗口 = 新增模型";
        this._lblModelHint.Location = new System.Drawing.Point(136, 33);
        this._lblModelHint.Size = new System.Drawing.Size(240, 18);
        this._lblModelHint.ForeColor = System.Drawing.Color.FromArgb(96, 100, 112);
        //
        // _lblActiveModel
        //
        this._lblActiveModel.Text = "活跃模型";
        this._lblActiveModel.Location = new System.Drawing.Point(12, 68);
        this._lblActiveModel.Size = new System.Drawing.Size(100, 18);
        //
        // _comboModels
        //
        this._comboModels.DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
        this._comboModels.Location = new System.Drawing.Point(12, 88);
        this._comboModels.Size = new System.Drawing.Size(240, 25);
        this._comboModels.SelectedIndexChanged += new System.EventHandler(this.OnActiveModelChanged);
        //
        // _btnRemoveModel
        //
        this._btnRemoveModel.Text = "移除";
        this._btnRemoveModel.Location = new System.Drawing.Point(258, 87);
        this._btnRemoveModel.Size = new System.Drawing.Size(72, 28);
        this._btnRemoveModel.Click += new System.EventHandler(this.OnRemoveModel);
        //
        // _lblModelNote
        //
        this._lblModelNote.Text = "活跃模型 = TRS + 动画的目标";
        this._lblModelNote.Location = new System.Drawing.Point(12, 124);
        this._lblModelNote.Size = new System.Drawing.Size(186, 20);
        this._lblModelNote.ForeColor = System.Drawing.Color.FromArgb(96, 100, 112);
        //
        // _chkIk
        //
        this._chkIk.Text = "IK 求解（活跃模型）";
        this._chkIk.Location = new System.Drawing.Point(204, 120);
        this._chkIk.Size = new System.Drawing.Size(172, 24);
        this._chkIk.CheckedChanged += new System.EventHandler(this.OnToggleIk);
        //
        // _groupMotion
        //
        this._groupMotion.Text = "动画";
        this._groupMotion.Dock = System.Windows.Forms.DockStyle.Fill;
        this._groupMotion.Controls.Add(this._btnLoadMotion);
        this._groupMotion.Controls.Add(this._lblMotionHint);
        this._groupMotion.Controls.Add(this._btnPlay);
        this._groupMotion.Controls.Add(this._btnPrevFrame);
        this._groupMotion.Controls.Add(this._btnNextFrame);
        this._groupMotion.Controls.Add(this._btnFirstFrame);
        this._groupMotion.Controls.Add(this._lblFps);
        this._groupMotion.Controls.Add(this._numFps);
        this._groupMotion.Controls.Add(this._lblMotionNote);
        //
        // _btnLoadMotion
        //
        this._btnLoadMotion.Text = "载入动画…";
        this._btnLoadMotion.Location = new System.Drawing.Point(12, 26);
        this._btnLoadMotion.Size = new System.Drawing.Size(116, 30);
        this._btnLoadMotion.Click += new System.EventHandler(this.OnOpenMotion);
        //
        // _lblMotionHint
        //
        this._lblMotionHint.Text = "拖放 .vmd = 载入到活跃模型";
        this._lblMotionHint.Location = new System.Drawing.Point(136, 33);
        this._lblMotionHint.Size = new System.Drawing.Size(230, 18);
        this._lblMotionHint.ForeColor = System.Drawing.Color.FromArgb(96, 100, 112);
        //
        // _btnPlay
        //
        this._btnPlay.Text = "⏸ 暂停";
        this._btnPlay.Location = new System.Drawing.Point(12, 68);
        this._btnPlay.Size = new System.Drawing.Size(88, 30);
        this._btnPlay.Click += new System.EventHandler(this.OnTogglePlay);
        //
        // _btnPrevFrame
        //
        this._btnPrevFrame.Text = "◀ 帧";
        this._btnPrevFrame.Location = new System.Drawing.Point(104, 68);
        this._btnPrevFrame.Size = new System.Drawing.Size(60, 30);
        this._btnPrevFrame.Click += new System.EventHandler(this.OnPrevFrame);
        //
        // _btnNextFrame
        //
        this._btnNextFrame.Text = "帧 ▶";
        this._btnNextFrame.Location = new System.Drawing.Point(168, 68);
        this._btnNextFrame.Size = new System.Drawing.Size(60, 30);
        this._btnNextFrame.Click += new System.EventHandler(this.OnNextFrame);
        //
        // _btnFirstFrame
        //
        this._btnFirstFrame.Text = "⏮ 首帧";
        this._btnFirstFrame.Location = new System.Drawing.Point(232, 68);
        this._btnFirstFrame.Size = new System.Drawing.Size(72, 30);
        this._btnFirstFrame.Click += new System.EventHandler(this.OnFirstFrame);
        //
        // _lblFps
        //
        this._lblFps.Text = "帧率";
        this._lblFps.Location = new System.Drawing.Point(12, 110);
        this._lblFps.Size = new System.Drawing.Size(40, 18);
        //
        // _numFps
        //
        this._numFps.Location = new System.Drawing.Point(52, 107);
        this._numFps.Size = new System.Drawing.Size(64, 24);
        this._numFps.Minimum = new decimal(new int[] { 1, 0, 0, 0 });
        this._numFps.Maximum = new decimal(new int[] { 240, 0, 0, 0 });
        this._numFps.Value = new decimal(new int[] { 30, 0, 0, 0 });
        this._numFps.ValueChanged += new System.EventHandler(this.OnPlaybackFpsChanged);
        //
        // _lblMotionNote
        //
        this._lblMotionNote.Text = "空格 播放/暂停 · ←/→ 步进 · F 回首帧";
        this._lblMotionNote.Location = new System.Drawing.Point(126, 110);
        this._lblMotionNote.Size = new System.Drawing.Size(250, 18);
        this._lblMotionNote.ForeColor = System.Drawing.Color.FromArgb(96, 100, 112);
        //
        // _groupTransform
        //
        this._groupTransform.Text = "模型变换（全局模式）";
        this._groupTransform.Dock = System.Windows.Forms.DockStyle.Fill;
        this._groupTransform.Controls.Add(this._lblTransform);
        this._groupTransform.Controls.Add(this._btnResetTransform);
        this._groupTransform.Controls.Add(this._btnFitCamera);
        this._groupTransform.Controls.Add(this._lblTransformHint);
        //
        // _lblTransform
        //
        this._lblTransform.Text = "（未选中模型）";
        this._lblTransform.Location = new System.Drawing.Point(12, 26);
        this._lblTransform.Size = new System.Drawing.Size(356, 56);
        this._lblTransform.Font = new System.Drawing.Font("Consolas", 9F);
        //
        // _btnResetTransform
        //
        this._btnResetTransform.Text = "重置变换 (R)";
        this._btnResetTransform.Location = new System.Drawing.Point(12, 90);
        this._btnResetTransform.Size = new System.Drawing.Size(120, 28);
        this._btnResetTransform.Click += new System.EventHandler(this.OnResetTransform);
        //
        // _btnFitCamera
        //
        this._btnFitCamera.Text = "适应视图";
        this._btnFitCamera.Location = new System.Drawing.Point(138, 90);
        this._btnFitCamera.Size = new System.Drawing.Size(88, 28);
        this._btnFitCamera.Click += new System.EventHandler(this.OnFitCamera);
        //
        // _lblTransformHint
        //
        this._lblTransformHint.Text = "视口右下角：下排选 TRS，上排按住 X/Y/Z 上下拖动（上=正向）";
        this._lblTransformHint.Location = new System.Drawing.Point(12, 124);
        this._lblTransformHint.Size = new System.Drawing.Size(360, 20);
        this._lblTransformHint.ForeColor = System.Drawing.Color.FromArgb(96, 100, 112);
        //
        // _bottom
        //
        this._bottom.Dock = System.Windows.Forms.DockStyle.Fill;
        this._bottom.ColumnCount = 3;
        this._bottom.RowCount = 1;
        this._bottom.Margin = new System.Windows.Forms.Padding(0);
        this._bottom.Padding = new System.Windows.Forms.Padding(6, 2, 6, 6);
        this._bottom.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 33.34F));
        this._bottom.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 33.33F));
        this._bottom.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 33.33F));
        this._bottom.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 100F));
        this._bottom.Controls.Add(this._groupModel, 0, 0);
        this._bottom.Controls.Add(this._groupMotion, 1, 0);
        this._bottom.Controls.Add(this._groupTransform, 2, 0);
        //
        // _statusHint
        //
        this._statusHint.Text = "就绪";
        //
        // _statusSpacer
        //
        this._statusSpacer.Spring = true;
        //
        // _statusRight
        //
        this._statusRight.Text = "-";
        //
        // _status
        //
        this._status.Dock = System.Windows.Forms.DockStyle.Fill;
        this._status.SizingGrip = false;
        this._status.Items.AddRange(new System.Windows.Forms.ToolStripItem[] {
            this._statusHint,
            this._statusSpacer,
            this._statusRight});
        //
        // _root
        //
        this._root.Dock = System.Windows.Forms.DockStyle.Fill;
        this._root.ColumnCount = 1;
        this._root.RowCount = 4;
        this._root.Margin = new System.Windows.Forms.Padding(0);
        this._root.Padding = new System.Windows.Forms.Padding(0);
        this._root.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 100F));
        this._root.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Absolute, 26F));
        this._root.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 100F));
        this._root.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Absolute, 182F));
        this._root.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Absolute, 28F));
        this._root.Controls.Add(this._menu, 0, 0);
        this._root.Controls.Add(this._mainTable, 0, 1);
        this._root.Controls.Add(this._bottom, 0, 2);
        this._root.Controls.Add(this._status, 0, 3);
        //
        // MainForm
        //
        this.AutoScaleDimensions = new System.Drawing.SizeF(7F, 17F);
        this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
        this.ClientSize = new System.Drawing.Size(1180, 780);
        this.Controls.Add(this._root);
        this.KeyPreview = true;
        this.MinimumSize = new System.Drawing.Size(960, 640);
        this.Text = "MikuEngine Demo";
        this._menu.ResumeLayout(false);
        this._menu.PerformLayout();
        this._groupLog.ResumeLayout(false);
        this._leftColumn.ResumeLayout(false);
        this._viewportHost.ResumeLayout(false);
        this._mainTable.ResumeLayout(false);
        this._groupModel.ResumeLayout(false);
        this._groupMotion.ResumeLayout(false);
        ((System.ComponentModel.ISupportInitialize)(this._numFps)).EndInit();
        this._groupTransform.ResumeLayout(false);
        this._status.ResumeLayout(false);
        this._status.PerformLayout();
        this._bottom.ResumeLayout(false);
        this._root.ResumeLayout(false);
        this.ResumeLayout(false);
    }

    #endregion
}
