using LongStitch.Core;
using LongStitch.Core.Analysis;
using LongStitch.Core.Cleanup;
using LongStitch.Core.Export;
using LongStitch.Core.Geometry;
using LongStitch.Core.Imaging;
using LongStitch.Core.Matching;
using LongStitch.Core.Layout;
using LongStitch.Core.Model;
using LongStitch.Core.ProjectIO;
using LongStitch.App.Imaging;
using LongStitch.App.Diagnostics;

namespace LongStitch.App.UI;

/// <summary>
/// 主窗口。按模拟 UI 需求 §2 的工作台布局摆放：
/// 顶栏（菜单 / 三模式 / 项目名 / 导出）— 左侧素材与拼缝 — 中央画布 — 右侧当前工具 — 底部状态。
///
/// 这一层刻意<b>不做任何像素计算</b>：它只把用户意图翻译成对 <see cref="Project"/> 的调用，
/// 再把 <see cref="Project.Changed"/> 广播给各个面板。
/// 所有会改动像素的东西都在 Core 里，因此也都能被 Checks 离线验。
/// </summary>
public sealed partial class MainForm : Form
{
    private readonly Project _project;
    private readonly FileSourceResolver _resolver = new();
    private readonly EncoderRegistry _encoders = new();
    private readonly CanvasComposer _composer;
    private readonly SeamAnalyzer _analyzer;

    private StitchCanvasControl _canvas = null!;

    /// <summary>导入与分析期间的忙碌遮罩（画布虚化 + 进度条）。见 <see cref="BusyOverlay"/>。</summary>
    private BusyOverlay _overlay = null!;

    /// <summary>
    /// 忙碌闸：遮罩挂着的时候，第二趟导入/分析一律不再进来。
    ///
    /// 为什么需要它：导入那趟循环是<b>同步</b>跑在 UI 线程上的，进度条要靠泵一次消息才画得出来
    /// （见 <see cref="PumpUi"/>）。泵消息就意味着这期间别的输入也会被处理——
    /// 用户再拖一批图片进来就会叠出第二趟，两趟交替写 <c>_project.Doc.Sources</c>，
    /// 那种交错的形状事后根本查不出来。所以这里直接拦掉，并说一句为什么没响应。
    /// </summary>
    private bool _busy;

    /// <summary>
    /// 抓一帧画布当前那一屏，交给遮罩做模糊底。
    /// 走画布自己的 <c>RenderFrameForTest</c>（它就是"把绘制一帧这条通路完整走一遍"那个入口），
    /// 不用 <c>DrawToBitmap</c>：后者靠 WM_PRINT，自绘双缓冲控件在那条路上经常只给一块空白，
    /// 而"虚化"要是糊了一块空白，用户看到的就是"画布消失了"，比不糊还糟。
    /// 画布还没上屏（离屏工装）或尺寸为零时返回 null，遮罩自己会退化成只压暗纱。
    /// </summary>
    private Bitmap? CaptureCanvasFrame()
    {
        StitchCanvasControl? canvas = _canvas;
        Size size = canvas?.ClientSize ?? Size.Empty;
        if (canvas is null || size.Width <= 0 || size.Height <= 0) return null;
        var frame = new Bitmap(size.Width, size.Height);
        try
        {
            canvas.RenderFrameForTest(frame);
            return frame;
        }
        catch
        {
            frame.Dispose();
            return null;
        }
    }

    /// <summary>
    /// 泵一次 UI 消息，让遮罩那一帧真的画到屏幕上。
    /// 只在忙碌闸（<see cref="_busy"/>）挂着的时候调用：闸在，重入就被拦在门外，
    /// 这正是 DoEvents 唯一安全的用法。
    /// </summary>
    private static void PumpUi() => Application.DoEvents();

    /// <summary>测试出口：忙碌遮罩那一层（判据读的是<b>它自己留在档上的</b>那批数，不是另算一遍）。</summary>
    public BusyOverlay BusyOverlayForTest => _overlay;

    /// <summary>测试出口：忙碌闸现在挂着没有。重入那一句拦不拦得住，判据读的就是它。</summary>
    public bool BusyForTest => _busy;

    // ═══ 界面刷新批次（用户第六条：「每导入一张界面就会闪烁一次…等完全导入并分析结束后再渲染整个界面」）═══
    // 病根不在解码、在通知：每追加一张都走一次 Project.Mutate（要进撤销栈，必须走），
    // Mutate 一 raise Changed → RefreshChrome 就把标题、撤销菜单、模式按钮、工具可用性、
    // 拼缝列表（整条 Items.Clear 再重填）、右侧面板、状态栏一次全刷；循环里那一下 PumpUi 又真的
    // 把它们泵到屏幕上 —— 100 张就是 100 次闪烁。
    // 批次期间这些刷新只记一次"欠着"，放闸时统一刷一遍。
    private int _uiBatchDepth;
    private bool _uiBatchQueued;

    /// <summary>仅供判据：<see cref="RefreshChrome"/> 真身执行了几次（一次都不许多）。</summary>
    public int UiRefreshCountForTest { get; private set; }

    /// <summary>
    /// 仅供判据：批次期间被挂起（合并掉）了几次刷新。
    /// 这一枚是"批次真的接住了逐张通知"的正向对照——没有它，"只刷了一次"也可能只是
    /// 因为通知根本没发出来，判据就会把"没发生"当成"没闪烁"。
    /// </summary>
    public int UiRefreshDeferredForTest { get; private set; }

    /// <summary>
    /// 开一个界面刷新批次（可嵌套：导入末尾会串起分析，提前放闸会把半成品那一屏刷出来）。
    /// 批次闸住三条直通的车：主窗口自己的 <see cref="RefreshChrome"/>、画布的重画、
    /// 左侧素材列表的重建——后两条各自订阅 <c>Project.Changed</c>，不闸就是逐张闪烁的另一半
    /// （用户第四十三改报了闪烁、第四十四改复看仍闪，缺的就是这两条）。
    /// </summary>
    private void BeginUiBatch()
    {
        if (_uiBatchDepth == 0)
        {
            _canvas.SuspendRepaints(true);
            _sourcePanel.SuspendRefresh(true);
        }
        _uiBatchDepth++;
    }

    /// <summary>放闸：最外层那一次才真的刷。先放画布与列表（各自补欠着的那一次），再刷 chrome。</summary>
    private void EndUiBatch()
    {
        if (_uiBatchDepth > 0) _uiBatchDepth--;
        if (_uiBatchDepth > 0) return;
        _canvas.SuspendRepaints(false);
        _sourcePanel.SuspendRefresh(false);
        if (!_uiBatchQueued) return;
        _uiBatchQueued = false;
        RefreshChrome();
    }

    private SourcePanel _sourcePanel = null!;
    private ToolPanel _toolPanel = null!;
    private ChromeScrollListBox _seamList = null!;
    private ChromeScrollHost _seamListHost = null!;
    private CheckBox _seamFilter = null!;

    /// <summary>拼缝列表的滚动宿主（第五十五改④，判据从这里量几何裁剪是否成立）。</summary>
    public Control SeamListHostForTest => _seamListHost;

    /// <summary>列表里当前显示的那几条拼缝（行号 → 真实拼缝）。
    /// 筛选后行号与 <c>Doc.Seams</c> 的下标不再相等，所有按行取数都必须走这份映射。</summary>
    private readonly List<Seam> _listedSeams = new();
    private Label _seamSummary = null!;
    private ToolStripStatusLabel _statusInfo = null!;
    private ToolStripStatusLabel _statusMode = null!;
    private ToolStripLabel _zoomLabel = null!;
    private ToolStripStatusLabel _statusSize = null!;
    private ToolStripLabel _statusZoom = null!;
    /// <summary>
    /// 顶栏那枚真正的分段控件（第五十改）。原来是三枚各自带勾选框的 ToolStripButton，
    /// 屏上读作「三个松散的词」而不是「一组互斥档位」；现在是一条凹槽里三格，选中格抬起。
    /// </summary>
    private SegmentedControl _modeSegment = null!;

    /// <summary>顶栏右半的项目名与未保存标记（原来只在窗口标题栏里，标题栏被系统画得很小）。</summary>
    private ToolStripLabel _projectTitle = null!;

    /// <summary>顶栏最右那枚主操作按钮。整条顶栏只有它是实强调蓝，视线第一落点就是「导出」。</summary>
    private ChromeButton _exportButton = null!;

    /// <summary>画布工具条右端的浏览／编辑徽标（需求 §7 验收第一条）。</summary>
    private ModeBadge _modeBadge = null!;

    private ToolStripButton[] _toolButtons = null!;
    private ToolStripButton _compareButton = null!;
    private ToolStripButton _diffButton = null!;
    private ToolStripMenuItem _undoItem = null!;
    private ToolStripMenuItem _redoItem = null!;
    private ToolStripMenuItem _exportItem = null!;
    private Panel _leftPanel = null!;
    private Panel _rightPanel = null!;
    private ToolStripMenuItem _recentMenu = null!;

    /// <summary>一次改动最多记多少条 <c>layout.seam</c>。超出只记一条截断说明。</summary>
    private const int LayoutSeamLogCap = 64;

    /// <summary>已经记过布局拼缝账的那个 Revision。同一版参数不许因为重绘反复记。</summary>
    private int _layoutSeamsLoggedRevision = -1;

    /// <summary>
    /// 两栏的展开宽度（需求 §2：左 230–280、右 280–340）。
    /// 折叠走"宽度归零"而不是 Visible=false：实测停靠宽度不认 invisible 控件的释放，
    /// 藏起来仍占着那 268 px，画布一点没变宽。
    /// 第五十改把字号从 9pt 提到 9.75pt、行高从 22 提到 40，两栏各跟着放宽一档：
    /// 左 252→268（拼缝行要站得下「序号 + 状态 + 原因摘要」三段），右 310→320（参数标签不再挤成两行）。
    /// </summary>
    private const int LeftPanelWidth = 268;
    private const int RightPanelWidth = 320;

    private string? _projectPath;
    private readonly List<string> _startupPaths = new();
    private CancellationTokenSource? _analysisCts;

    /// <summary>
    /// 分析写回闸门（审查第四轮 P2）：取消 CancellationToken **撤不回已经排进 UI 消息队列的
    /// BeginInvoke 回调**，所以写回前必须再确认"我还是最新一轮、项目没被换、参数没被动过"。
    /// 规则本身抽到 <see cref="AnalysisGate"/>，是为了让它能被单独注入、单独判红。
    /// </summary>
    private readonly AnalysisGate _analysisGate = new();

    /// <summary>悬停提示：拼缝把手上显示"点击编辑拼缝 2→3"（需求 §3.2）。</summary>
    private ToolTip? _hoverTip;

    /// <summary>
    /// 「跟随系统」档下的轮询计时器（第四十八改）。
    ///
    /// ═══ 为什么是轮询而不是 SystemEvents.UserPreferenceChanged ═══
    /// 那个事件会起一条常驻的原生消息线程，离屏工装里既不好收也不好判；而本项目产品内禁止 P/Invoke，
    /// 拿不到 WM_SETTINGCHANGE 的可靠通知。注册表读一次是微秒级的事，2 秒一趟的代价可以忽略，
    /// 而且<b>只在跟随档才跑</b>：手动选了白天/黑夜就把 Enabled 关掉，一次都不读。
    /// 用户从 Windows 设置里改完切回本窗口时，OnActivated 还会立刻补读一次，不用等那 2 秒。
    /// </summary>
    private readonly System.Windows.Forms.Timer _themePoll = new() { Interval = 2000 };

    public MainForm()
    {
        _project = new Project();
        _composer = new CanvasComposer(_resolver);
        _analyzer = new SeamAnalyzer(_composer);
        _encoders.Register(new GdiplusJpegEncoder());

        Text = "LongStitch · 长截图拼合工具";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(1024, 700);
        // ═══ 第五十五改⑤（用户图5）：默认宽度 1440 → 1600 ═══
        // 用户报告：1440 宽下底部状态栏那句「还没有可导出的内容」右侧被截断，拉大窗口才看得全。
        // 状态栏的固定槽位（五工具 + 模式 + 尺寸 + 帧读数 + 缩放）本身是按最长文案现量的，
        // 中间那句弹性读数只能吃"剩下的宽度"——默认宽度越紧，它越先被牺牲。
        // 高度不动（900）：2K@150% 的屏逻辑高度只有约 955，再高默认窗就会顶到任务栏。
        // 1600 对 1080p（1920 逻辑宽）与 2K@150%（约 1707 逻辑宽）都放得下。
        Size = new Size(1600, 900);
        BackColor = Theme.WindowBackground;
        ForeColor = Theme.TextPrimary;
        Font = Theme.UiFont;
        AllowDrop = true;
        DragEnter += OnFileDragEnter;
        DragDrop += OnFileDragDrop;
        FormClosing += OnFormClosingCheckSave;

        // WinForms 的停靠是"后加入的占外侧"。所以顺序必须是：
        //   中央(Fill) → 左栏 → 右栏 → 画布工具条 → 状态栏 → 顶栏（菜单栏）
        // 反过来写会让菜单被顶到 y=58（被画布工具条压在下面）。
        // 这条不是猜的：由 Checks 的 <c>ui/主窗口可构造并完成布局</c> 量着 Bounds 定下来。
        //
        // ═══ 第五十改：顶栏与菜单合成一条带 ═══
        // 原来 BuildToolbar 自己 new 一条 ToolStrip 再 Controls.Add，于是屏上出现
        // 「菜单一条 + 模式一条」两条同色灰带（需求 §2 的线框明明只画了一行）。
        // 现在菜单先建好并停靠到最上沿，BuildToolbar 只往<b>那一条 MenuStrip</b> 里塞
        // 分段控件／项目名／导出按钮（ToolStripControlHost），不再新增任何停靠控件。
        // 所以这两句的先后不能换：BuildToolbar 要拿 MainMenuStrip，菜单没建它就无处可塞。
        BuildBody();
        BuildStatusBar();
        BuildMenu();
        BuildToolbar();

        // 第五十九改第一阶段：三件套齐了就把内存快照的读数来源挂上（Dispose 里对应 Detach）。
        Diagnostics.MemorySnapshot.Attach(_resolver, _canvas, _sourcePanel);

        _project.Changed += RefreshChrome;
        RefreshChrome();

        // ═══ 第四十八改：换肤与跟随系统 ═══
        // 两条订阅各管一件事：Theme.Changed 是"配色真的换了一套"（翻控件树、报状态栏），
        // ThemeController.ModeChanged 是"档位变了"（可能配色没变，例如从黑夜切到跟随而系统正好是深色），
        // 后者只用来开/关那枚轮询。分开订阅才不会在配色没变时白翻一遍整棵树。
        Theme.Changed += OnThemeChanged;
        ThemeController.ModeChanged += OnThemeModeChanged;
        _themePoll.Tick += (_, _) => ThemeController.RefreshSystemReading();
        SyncThemePoll();
    }

    /// <summary>跟随档才轮询；手动档一次注册表都不读。</summary>
    private void SyncThemePoll() =>
        _themePoll.Enabled = ThemeController.Mode == Core.Model.ThemePreference.FollowSystem;

    private void OnThemeModeChanged() => SyncThemePoll();

    /// <summary>
    /// 配色换了一套：把整棵控件树按"旧档 → 新档"翻译一遍，再刷 chrome 与状态栏。
    /// 画布与忙碌遮罩各自订阅了同一个事件（它们要改的是常驻画具与烤进位图的底），这里不代劳。
    /// <see cref="ThemeApplicator.ApplyEverywhere"/> 会连开着的设置对话框一起翻，
    /// 所以用户在对话框里点"白天"时，对话框自己与背后的主窗口是同一趟变完的。
    /// </summary>
    private void OnThemeChanged(ThemePalette old, ThemePalette current)
    {
        ThemeApplicator.ApplyEverywhere(this);
        RefreshChrome();
        UpdateSeamList();
        Status(ThemeController.Describe());
    }

    /// <summary>
    /// 切回本窗口时立刻补读一次系统深浅色：用户刚在 Windows 设置里改完就切回来，
    /// 不该还要等最多 2 秒才跟上。只在跟随档读，手动档不打扰注册表。
    /// </summary>
    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        if (ThemeController.Mode == Core.Model.ThemePreference.FollowSystem)
            ThemeController.RefreshSystemReading();
    }

    /// <summary>
    /// 主窗口释放时把两条静态订阅与那枚计时器收干净。
    /// 静态事件不收就会把整个窗口（连同它引用的项目与全部位图）钉在托管堆上——
    /// 离屏工装一趟要建几十个窗口，不收就是几十份泄漏。
    /// </summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // 静态快照引用必须在这里摘掉：留着它就是把整个窗口连同全部位图钉在托管堆上
            //（与上面两条静态订阅同一种泄漏形状）。摘之前先记一行 exit 快照。
            Diagnostics.MemorySnapshot.Log("exit");
            Diagnostics.MemorySnapshot.Detach();
            Theme.Changed -= OnThemeChanged;
            ThemeController.ModeChanged -= OnThemeModeChanged;
            _themePoll.Stop();
            _themePoll.Dispose();
        }
        base.Dispose(disposing);
    }

    /// <summary>命令行/资源管理器"打开方式"传进来的路径。</summary>
    public void EnqueueStartupPaths(string[] paths) => _startupPaths.AddRange(paths);

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (_startupPaths.Count > 0)
        {
            string[] paths = _startupPaths.ToArray();
            _startupPaths.Clear();
            ImportPaths(paths);
        }
    }

    // ───────────────────────── 布局搭建 ─────────────────────────

    private void BuildBody()
    {
        // 三栏：用手动 Dock + 固定宽度，避免 SplitContainer 在 DPI 缩放下抖 1 像素
        var center = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.CanvasBackground,
            // 第五十四改①（用户图1红框）：左右原来各让 12px，画布工具条两端与左右侧栏之间
            // 空出两条谁也不属于的缝。归零后工具条整条与侧栏相接；画布背景与这里同色、
            // 内容按缩放居中，贴边不产生任何视觉接缝。底部 12px 是画布的呼吸位，保留。
            Padding = new Padding(0, 0, 0, 12),
        };

        _canvas = new StitchCanvasControl(_project, _composer) { Dock = DockStyle.Fill };
        _canvas.SelectionChanged += OnCanvasSelectionChanged;
        // 窗口拖大拖小、侧栏折叠都改变画布尺寸 ⇒ 窄内容要跟着回到正中（用户第七条「无论窗口多大都得保持居中」）。
        // 挂在这个位置而不是画布自己的 OnResize 里，理由见 FollowContentCentre 的注释。
        _canvas.Resize += (_, _) => _canvas.FollowContentCentre();
        // 第十一轮：这里原来挂着 `_ => UpdateSeamList()`——拖动每来一帧就把整条列表清空重建。
        // 但预览期间<b>一个字节的参数都没写</b>（松手才 Mutate），列表上根本没有新东西可显示，
        // 于是这一行只买到卡顿。参数真变了的时候 _project.Changed → RefreshChrome 已经在刷。
        _canvas.SplitPicked += OnCanvasSplitPicked;
        _canvas.HoverSeamTip += OnHoverSeamTip;
        // 长截图里横向被锁掉时必须出声：按了箭头或往左右拖却"什么都没发生"，
        // 不说一句用户只会以为软件卡了。
        _canvas.HorizontalShiftRejected += dropped => Status(
            $"长截图模式已锁定左右方向：横向 {dropped:+#;-#;0} 像素按 X=0 处理（纵向照常生效）");
        // 切点顶到重叠带边界也要出声：否则用户只会觉得"线拖不动了"，
        // 不知道已经整带用上一张／下一张了（裁切量不能再往下减）。
        _canvas.CutClamped += rejected => Status(
            rejected > 0
                ? $"切点已到重叠带底部：整带都改由上方那张提供（被夹掉 {rejected} 行）"
                : $"切点已到重叠带顶部：整带都改由下方那张提供（被夹掉 {-rejected} 行）");
        center.Controls.Add(_canvas);

        // ═══ 忙碌遮罩：导入与分析期间把画布虚化，中间摆一句说明与一条进度条 ═══
        // 它是画布的<b>兄弟</b>控件、不是画布的一层叠图：画布自己的 OnPaint 完全不知道它存在，
        // 所以瓦片、导出与那些逐字节判据一个都不受影响（精度红线三在这一层同样成立）。
        // BringToFront 是必需的：WinForms 里后 Add 的控件在 z 序里更靠后，不提到前面就被画布盖住。
        _overlay = new BusyOverlay { Dock = DockStyle.Fill };
        center.Controls.Add(_overlay);
        _overlay.BringToFront();

        // 微调模式那句话要报到状态栏：用户点完「调整」之后，下一步该干什么得有人在屏上说。
        _canvas.AdjustModeMessage += message => Status(message ?? "就绪");
        // 方向键按了没反应时必须出声（第四十六改）：不出声用户只能猜是卡了还是键没被认。
        _canvas.NudgeNotice += Status;

        var canvasTools = BuildCanvasToolStrip();
        center.Controls.Add(canvasTools);

        var right = new Panel
        {
            Dock = DockStyle.Right,
            Width = RightPanelWidth,
            // 底色取边框色、左缘留 1px 内边距：那一条 1px 就是右栏与画布之间的发丝分界。
            // 比给面板画边框省事，也不会随 DPI 抖（padding 与停靠都是整数像素）。
            BackColor = Theme.PanelBorder,
            Padding = new Padding(1, 0, 0, 0),
        };
        _rightPanel = right;
        _toolPanel = new ToolPanel(_project, _resolver, _composer)
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.PanelBackground,
        };
        _toolPanel.CanvasCommand += OnToolCommand;
        _toolPanel.ParametersChanged += () => { RefreshChrome(); UpdateSeamList(); };
        _toolPanel.OffsetNotice += Status;
        right.Controls.Add(_toolPanel);
        var contextHeader = new Panel
        {
            Dock = DockStyle.Top, Height = Chrome.CanvasBarHeight,
            Padding = new Padding(20, 12, 20, 8), BackColor = Theme.PanelBackground,
        };
        _modeBadge.Dock = DockStyle.Fill;
        contextHeader.Controls.Add(_modeBadge);
        right.Controls.Add(contextHeader);

        var left = new Panel
        {
            Dock = DockStyle.Left,
            Width = LeftPanelWidth,
            BackColor = Theme.PanelBorder,
            Padding = new Padding(0, 0, 1, 0),   // 右缘那 1px 发丝线，与右栏对称
        };
        _leftPanel = left;
        _sourcePanel = new SourcePanel(_project, _resolver) { Dock = DockStyle.Fill };
        _sourcePanel.AddRequested += () => AddImagesViaDialog();
        _sourcePanel.ImportRequested += files => ImportPaths(files);

        // 这三处原来写死 (34,34,36)：那是黑夜档的列表底色，白天档下它会变成一条突兀的深色带。
        // 现在统一走 Theme.ListBackground（黑夜档的值与原来逐字节相同，所以旧观感一点没动）。
        // 第五十改：表头从 26px 提到 36px、字号提到正文档——原来那行 8pt 灰字是整屏最弱的文字，
        // 而它偏偏是「这一栏是什么」的唯一说明。
        var seamHeaderPanel = new Panel { Dock = DockStyle.Top, Height = Theme.UiFontBold.Height + 24, BackColor = Theme.ListBackground };
        var seamHeader = new Label
        {
            Text = "拼缝", Dock = DockStyle.Left,
            Width = TextRenderer.MeasureText("拼缝", Theme.UiFontBold).Width + 24, ForeColor = Theme.TextPrimary,
            Font = Theme.UiFontBold, Padding = new Padding(20, 0, 0, 0), TextAlign = ContentAlignment.MiddleLeft,
        };
        // 需求 §5 A：`!` 可筛选，但**图片顺序保持不变**——筛的是列表视图，不是文档里的段序
        // 第五十一改（用户图绿框）：换自绘 ChromeCheckBox 并整体靠右——
        // 系统勾选框的框与文字基线各自排，从来不齐；内容也不靠右，看起来像没对齐
        _seamFilter = new ChromeCheckBox
        {
            Text = "只看需检查", Dock = DockStyle.Right,
            Width = TextRenderer.MeasureText("只看需检查", Theme.UiFontSmall).Width + 44, AutoSize = false,
            ForeColor = Theme.TextSecondary, Font = Theme.UiFontSmall,
            RightAlign = true,
        };
        _seamFilter.CheckedChanged += (_, _) => { UpdateSeamList(); };
        seamHeaderPanel.Controls.Add(seamHeader);
        seamHeaderPanel.Controls.Add(_seamFilter);
        _seamList = new ChromeScrollListBox
        {
            BorderStyle = BorderStyle.None,
            BackColor = Theme.ListBackground,
            ForeColor = Theme.TextPrimary,
            Font = Theme.UiFont,
            // ═══ 第五十改：把那一行自绘真的接上 ═══
            // OnDrawSeamItem 一直挂着，但 DrawMode 从没设过——ListBox 默认 DrawMode.Normal
            // 根本不发 DrawItem，于是那段「序号 + 状态 + 原因摘要」的设计稿一行都没画出来，
            // 屏上看到的是系统默认的 ToString()（一条缝的类型名）。设成 OwnerDrawFixed 才算接线。
            DrawMode = DrawMode.OwnerDrawFixed,
            ItemHeight = Chrome.SeamRowHeight,
            // 行高 40 除不尽列表高度时，IntegralHeight=true 会把列表整体缩到整行数，
            // 底下留一条谁也不知道是什么的空带。关掉它，让最后一行被裁一半——滚动条已经说明还有内容。
            IntegralHeight = false,
        };
        _seamList.DrawItem += OnDrawSeamItem;
        _seamList.SelectedIndexChanged += OnSeamListSelected;
        _seamSummary = new Label
        {
            Dock = DockStyle.Bottom, Height = 30, ForeColor = Theme.TextSecondary,
            BackColor = Theme.ListBackground,
            Font = Theme.UiFontMicro, Padding = new Padding(12, 8, 10, 0),
        };

        // 上下分给素材与拼缝两块列表：素材占大头，拼缝列表要始终可见可跳转（需求 §3.2）。
        // 210→268：表头 36 + 摘要 30 + 分界线 1 之后，210 只剩 143px＝三行半，
        // 第四条缝要滚动才看得见，而「拼缝列表始终可用于跳转」正是这一栏存在的理由。
        // 第五十五改④（用户图4）：拼缝列表同样套进自绘滚动条宿主，与左栏素材列表同一根蓝条。
        var seamBox = new Panel { Dock = DockStyle.Bottom, Height = 268, BackColor = Theme.ListBackground };
        _seamListHost = new ChromeScrollHost(_seamList) { Dock = DockStyle.Fill };
        seamBox.Controls.Add(_seamListHost);
        seamBox.Controls.Add(_seamSummary);
        seamBox.Controls.Add(seamHeaderPanel);
        // 素材与拼缝之间的一道发丝线：两块列表底色相同，没有它就读不出「这是两栏」。
        // 最后 Add 的 Top 停靠占外侧，所以它压在表头之上而不是表头之下。
        seamBox.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 1, BackColor = Theme.PanelBorder });

        left.Controls.Add(_sourcePanel);
        left.Controls.Add(seamBox);

        // 后加入的侧栏占外侧，画布工具条只参与中央容器的停靠。
        Controls.Add(center);
        Controls.Add(left);
        Controls.Add(right);
    }

    /// <summary>中央工作区的缩放与对比工具，状态徽标放在右侧参数栏顶部。</summary>
    private ToolStrip BuildCanvasToolStrip()
    {
        var strip = new ToolStrip
        {
            Dock = DockStyle.Top,
            GripStyle = ToolStripGripStyle.Hidden,
            BackColor = Theme.PanelBackground,
            Renderer = new DarkToolStripRenderer(),
            AutoSize = false,
            Height = Chrome.CanvasBarHeight,
            Padding = new Padding(10, 5, 10, 5),
        };

        // 局部函数：这一排所有按钮同一副骨架，只有文字与用途不同。
        ToolStripButton BarButton(string text, string tip, int minWidth = 0)
        {
            int wide = TextRenderer.MeasureText(text, Theme.UiFont, new Size(int.MaxValue, int.MaxValue),
                TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine).Width;
            return new ToolStripButton(text)
            {
                ToolTipText = tip,
                DisplayStyle = ToolStripItemDisplayStyle.Text,
                Font = Theme.UiFont,
                AutoSize = false,
                Size = new Size(Math.Max(minWidth, wide + 22), 34),
                Margin = new Padding(0, 0, 2, 0),
            };
        }
        ToolStripSeparator Group() => new() { Margin = new Padding(6, 0, 6, 0) };

        // 需求 §4：三个快捷缩放操作——适应宽度 / 适应整个结果 / 100%
        ToolStripButton fitWidth = BarButton("适应宽度", "按画布宽度缩放（只改预览，不改拼合）");
        fitWidth.Click += (_, _) => _canvas.Viewport.FitWidth(_project.Layout.CanvasSize);
        strip.Items.Add(fitWidth);
        ToolStripButton fitWhole = BarButton("适应窗口", "把整条结果缩放到刚好看得见（只改预览，不改拼合）");
        fitWhole.Click += (_, _) => _canvas.Viewport.FitWhole(_project.Layout.CanvasSize);
        strip.Items.Add(fitWhole);
        ToolStripButton one = BarButton("100%", "实际像素：一个源图像素占一个屏幕像素", 58);
        // 第五十二改：100% 与 ＋／－ 都走画布的"缩放+居中"入口，放大后窄内容自动回到水平正中
        one.Click += (_, _) => _canvas.ZoomActualSize();
        strip.Items.Add(one);
        strip.Items.Add(Group());

        ToolStripButton minus = BarButton("－", "缩小预览，并让装得下那一轴自动居中", 38);
        minus.Click += (_, _) => _canvas.ZoomStep(1 / 1.25);
        strip.Items.Add(minus);
        // 读数槽位固定宽度：缩放每变一档就重排整条工具条的话，右边那几枚会跟着左右跳。
        _zoomLabel = new ToolStripLabel("—")
        {
            ForeColor = Theme.TextPrimary,
            Font = Theme.MonoFont,
            AutoSize = false,
            Size = new Size(58, 34),
            TextAlign = ContentAlignment.MiddleCenter,
            ToolTipText = "当前预览缩放（右侧 X/Y 偏移始终是源图像素，与这个比例无关）",
        };
        strip.Items.Add(_zoomLabel);
        ToolStripButton plus = BarButton("＋", "放大预览，并让装得下那一轴自动居中", 38);
        plus.Click += (_, _) => _canvas.ZoomStep(1.25);
        strip.Items.Add(plus);
        strip.Items.Add(Group());

        // 「对比」= 半透明重影叠层，「差异」= 只标不一致处。两者显示的是同一块重叠带的
        // 两种画法，必须互斥——之前"对比"点下去只是把差异关掉、自己从不生效，是个假开关。
        ToolStripButton compare = BarButton("对比", "在编辑拼缝时叠一层半透明的另一侧内容，看有没有重影");
        compare.CheckOnClick = true;
        // 画布的默认就是重影层开着，按钮必须跟着显示为已勾选——否则第一下点击会变成"关掉"，
        // 用户看到的是"点了没反应"。
        compare.Checked = true;
        ToolStripButton diff = BarButton("差异", "只把两侧不一致的地方标红，接缝差一像素会立刻看见");
        diff.CheckOnClick = true;
        compare.Click += (_, _) => SetOverlapView(compare.Checked, false);
        diff.Click += (_, _) => SetOverlapView(false, diff.Checked);
        strip.Items.Add(compare);
        strip.Items.Add(diff);
        _compareButton = compare;
        _diffButton = diff;

        // 参数栏顶部的只读徽标：一眼区分浏览与编辑，不承担切换操作。
        // 它不可点、不写参数，只是 UpdateSizeInfo 那句读数的第二个出口（第一个在底部状态栏）。
        _modeBadge = new ModeBadge();

        _canvas.Viewport.Changed += () => UpdateZoomLabel();
        return strip;
    }

    /// <summary>
    /// 「重叠对比画法」的<b>唯一写入出口</b>（用户报的第五条从这里断根）。
    /// 画法只有三种状态：都不开 / 重影 / 差异，两档同时开在这一个出口里是写不出来的。
    /// 之前是三条路各写自己那一半——面板那条路只改画布的差异开关、把「对比」按钮的勾去掉，
    /// 却从不关画布的重影层，于是屏上出现"按钮说没开、画布还在画"，用户只能再点一下把它点对上。
    /// 现在一次写入同时落四处：画布两个开关、上方两枚按钮、面板两个勾选框，
    /// 判据 <c>ui/画法三个出口必须落在同一份状态上</c> 量的就是这个。
    /// </summary>
    private void SetOverlapView(bool ghost, bool difference)
    {
        _canvas.ShowCompareOverlay = ghost;
        _canvas.ShowDifference = difference;
        _compareButton.Checked = ghost;
        _diffButton.Checked = difference;
        // 程序设 ToolStripButton.Checked 不会触发 Click，所以这里不会绕回来重入；
        // 面板那一侧必须显式压住 CheckedChanged（见 ToolPanel.SyncOverlapChecks），因为它设值会发命令。
        _toolPanel.SyncOverlapChecks(ghost, difference);
        _canvas.Invalidate();
    }

    /// <summary>
    /// 面板那条路（<c>overlay:1</c> / <c>diff:0</c> 这类命令）翻译成 <see cref="SetOverlapView"/> 的一次调用：
    /// 开某一档 ⇒ 那一档赢、另一档关掉；关某一档 ⇒ 只撤自己，另一档维持现状。
    /// 翻译只在这一处发生，出口仍然只有一个——判据 <c>guard/面板回填不许把画法命令再发回主窗口</c>
    /// 量的就是"回填这一趟有没有再绕回来"。
    /// </summary>
    private void ApplyOverlapCommand(string kind, bool on)
    {
        if (on) SetOverlapView(kind == "overlay", kind != "overlay");
        else if (kind == "overlay") SetOverlapView(false, _canvas.ShowDifference);
        else SetOverlapView(_canvas.ShowCompareOverlay, false);
    }

    private void BuildMenu()
    {
        var menu = new MenuStrip
        {
            BackColor = Theme.PanelBackground,
            ForeColor = Theme.TextPrimary,
            Renderer = new DarkToolStripRenderer(),
            // ═══ 第五十改：这一条就是整个顶栏 ═══
            // 需求 §2 的线框只画了一行：菜单 + 三模式 + 项目名 + 导出。
            // 原来菜单自己一条（约 24px）、模式与导出另起一条（约 31px），两条同色灰带叠在一起，
            // 屏上读不出层级。现在关掉 AutoSize、把高度钉成 52，模式分段／项目名／导出
            // 由 BuildToolbar 以 ToolStripControlHost 塞进<b>同一条</b> Items 里。
            AutoSize = false,
            Height = Chrome.TopBarHeight,
            Padding = new Padding(8, 0, 10, 0),
            GripStyle = ToolStripGripStyle.Hidden,
        };

        var file = new ToolStripMenuItem("文件(&F)");
        file.DropDownItems.Add("新建项目", null, (_, _) => NewProject());
        file.DropDownItems.Add("打开项目…", null, (_, _) => OpenProject());
        _recentMenu = new ToolStripMenuItem("最近打开");
        _recentMenu.DropDownOpening += (_, _) => RebuildRecentMenu();
        file.DropDownItems.Add(_recentMenu);
        _exportItem = new ToolStripMenuItem("导出…", null, (_, _) => ExportNow()) { ShortcutKeys = Keys.Control | Keys.E };
        file.DropDownItems.Add(new ToolStripMenuItem("保存项目(&S)", null, (_, _) => SaveProject(false))
        { ShortcutKeys = Keys.Control | Keys.S });
        file.DropDownItems.Add("另存为…", null, (_, _) => SaveProject(true));
        file.DropDownItems.Add(new ToolStripSeparator());
        // 打包是**单独一条命令**：默认的项目文件只引用路径，原图不会被复制
        file.DropDownItems.Add("打包项目（含原图）…", null, (_, _) => SavePackage())
            .ToolTipText = "把项目参数和全部原图存进一个 .lstickx 容器（zip），换机器也能打开";
        file.DropDownItems.Add("打开打包项目…", null, (_, _) => OpenPackage());
        file.DropDownItems.Add(new ToolStripSeparator());
        var addItem = new ToolStripMenuItem("添加图片…", null, (_, _) => AddImagesViaDialog())
        { ShortcutKeys = Keys.Control | Keys.O };
        file.DropDownItems.Add(addItem);
        file.DropDownItems.Add(new ToolStripSeparator());
        // 第四十八改：设置入口只有这一枚。同一个动作在界面上出现两次，用户会先点错一个、
        // 再怀疑另一个是不是干别的用的（「添加图片」那一族已经吃过这个教训）。
        file.DropDownItems.Add("设置…", null, (_, _) => OpenSettings())
            .ToolTipText = "外观：白天／黑夜切换，以及是否跟随 Windows 的深浅色设置";
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add("退出", null, (_, _) => Close());

        var edit = new ToolStripMenuItem("编辑(&E)");
        _undoItem = new ToolStripMenuItem("撤销", null, (_, _) => { _project.Undo(); RefreshChrome(); })
        { ShortcutKeys = Keys.Control | Keys.Z };
        _redoItem = new ToolStripMenuItem("重做", null, (_, _) => { _project.Redo(); RefreshChrome(); })
        { ShortcutKeys = Keys.Control | Keys.Y };
        edit.DropDownItems.Add(_undoItem);
        edit.DropDownItems.Add(_redoItem);
        edit.DropDownItems.Add(new ToolStripSeparator());
        edit.DropDownItems.Add("全部重新分析", null, (_, _) => AnalyzeAll());
        edit.DropDownItems.Add("按文件名排序", null, (_, _) => SortBy(0));
        edit.DropDownItems.Add("按拍摄时间排序", null, (_, _) => SortBy(1));

        var view = new ToolStripMenuItem("视图(&V)");
        view.DropDownItems.Add(new ToolStripMenuItem("跳到下一条需检查", null, (_, _) => GotoNextReview())
        { ShortcutKeys = Keys.F3 });
        // ═══ 第十三轮需求六：几何与帧耗时读数层（F8，默认关）═══
        // 打开之后屏上多的是框、线与几行读数，一字节都不写进参数、也不参与取数——
        // 这条等价性由常驻判据 <c>align/关掉调试层不改对齐、蓝线、导出字节与命中区</c> 守着。
        // 颜色照需求六那份约定：绿=图1 有效区域、黄=图2、白半透明=重叠带、蓝线=CutY、
        // 红箭头=可拖对齐区、灰=画布/视口边界，另加紫虚线=当前<b>真的可点</b>的三块命中区。
        var debugHud = new ToolStripMenuItem("几何与帧耗时读数层")
        {
            ShortcutKeys = Keys.F8,
            CheckOnClick = true,
            ToolTipText = "绿=图1、黄=图2、白=重叠带、蓝线=CutY、红箭头=可拖对齐区、灰=画布边界、紫虚线=当前可点的命中区；" +
                          "左上角读数：Rect1/Rect2/OverlapRect/CutY 与比例/AlignmentOffset/视口/瓦片数/帧耗时/FPS/命中率",
        };
        debugHud.Click += (_, _) =>
        {
            _canvas.ShowDebugHud = debugHud.Checked;
            _canvas.Invalidate();
            Status(debugHud.Checked
                ? "调试读数层已打开（只画在屏幕上，不参与取数；再按 F8 关闭）"
                : "调试读数层已关闭");
        };
        view.DropDownItems.Add(debugHud);
        // 窄窗口时侧栏可折叠，不遮挡画布（模拟 UI 需求 §2 的布局建议）
        view.DropDownItems.Add(new ToolStripMenuItem("折叠/展开左侧栏", null,
            (_, _) => Status(ToggleSidePanel(left: true) ? "左侧栏已展开" : "左侧栏已折叠（素材与拼缝列表暂时收起）"))
        { ShortcutKeys = Keys.Control | Keys.D1 });
        view.DropDownItems.Add(new ToolStripMenuItem("折叠/展开右侧栏", null,
            (_, _) => Status(ToggleSidePanel(left: false) ? "右侧栏已展开" : "右侧栏已折叠（当前工具参数暂时收起）"))
        { ShortcutKeys = Keys.Control | Keys.D2 });
        var arrowPan = new ToolStripMenuItem("方向键浏览画布")
        {
            Checked = true,
            ToolTipText = "关掉后，浏览态按 ↑↓←→ 不再平移视口（编辑态的 1 源像素微调始终有效）",
        };
        arrowPan.Click += (_, _) =>
        {
            _canvas.BrowseArrowPanEnabled = arrowPan.Checked;
        };
        view.DropDownItems.Add(arrowPan);
        // Esc 由画布控件自己处理（焦点在文本框时不该退出编辑）。
        // 这里刻意不设 ShortcutKeys：WinForms 要求非字母数字的快捷键必须带修饰键，
        // 直接赋 Keys.Escape 会在构造菜单项时抛 InvalidEnumArgumentException——
        // 也就是说主窗口根本建不起来。这个坑是 UI 冒烟工装抓出来的。
        view.DropDownItems.Add(new ToolStripMenuItem("退出拼缝编辑（Esc）", null,
            (_, _) => _canvas.SelectSeam(null)));

        var help = new ToolStripMenuItem("帮助(&H)");
        help.DropDownItems.Add("快捷键与操作说明", null, (_, _) => ShowHelp());
        // 第十四轮需求四：F8 只能给"正在看屏幕的人"读，这三个出口把同一批读数交出去。
        // 三项都放在帮助菜单里，文字逐字照需求四那份清单写（是「打开诊断日志目录」不是「文件夹」——
        // 拿着需求清单来界面上找入口的人，读到的必须是同一个词）。用户要找诊断入口只会翻这里。
        help.DropDownItems.Add("复制诊断信息", null, (_, _) => CopyDiagnosticsToClipboard());
        help.DropDownItems.Add("打开诊断日志目录", null, (_, _) =>
        {
            try { DiagnosticLog.OpenDirectory(); }
            catch (Exception ex)
            {
                DiagnosticLog.Error("diagnostics.open_failed", ex);
                MessageBox.Show(this, "无法打开日志文件夹：" + ex.Message, "诊断日志",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        });
        help.DropDownItems.Add("导出当前交互会话", null, (_, _) => ExportInteractionSession());
        help.DropDownItems.Add("关于", null, (_, _) => ShowAbout());

        menu.Items.AddRange(new ToolStripItem[] { file, edit, view, help });
        // 四个顶级项的字体钉成同一枚（第四十六改）。用户报【文件】比其余三个大一号：
        // 代码里四枚是同一构造器new出来的，屏上却不一样大——渲染器在视觉样式可用时
        // 会拿系统菜单字体画顶级项（见 DarkToolStripRenderer.OnRenderItemText 的注释），
        // 哪一枚走过 owner-draw 路径哪一枚就"现出原形"。这里把字号也一起钉死：
        // 取与左栏表头同档的 UiFontSmall，整条菜单栏与其余三枚原来的观感一致。
        foreach (ToolStripMenuItem top in new[] { file, edit, view, help }) top.Font = Theme.UiFontSmall;
        // 第五十改：顶栏整条 52px 高。菜单项若按文字自动取高（约 20px）就会被贴在上沿，
        // 下半条空着，文字与右邻的模式分段不在一条视线上。这里把项高也钉成整条带高，
        // 文字交给渲染器按 MiddleCenter 水平垂直居中（回修：原来 MiddleLeft 让【文件】偏在
        // 灰色 pill 的左边，用户报「不在圆角矩形的居中位置」）；宽度先量文字再加左右各 12px。
        // 量之前把助记符的 & 去掉：NoPrefix 只是「不解析」，那个 & 仍会被算进宽度。
        foreach (ToolStripMenuItem top in new[] { file, edit, view, help })
        {
            int wide = TextRenderer.MeasureText((top.Text ?? "").Replace("&", ""), top.Font,
                new Size(int.MaxValue, int.MaxValue),
                TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine).Width;
            top.AutoSize = false;
            top.Size = new Size(wide + 24, Chrome.TopBarHeight);
            top.TextAlign = ContentAlignment.MiddleCenter;
            // 第五十改回修：下拉容器（ToolStripDropDown）的 BackColor 没人赋过，是系统浅色。
            // 每次打开前现赋成当前面板底——渲染器背景/分隔符虽已在绘制当下现算，但容器
            // 上下 padding 那圈不走渲染器，仍读 BackColor，不赋就会漏出「粗白线」。
            // 放在 DropDownOpening（显示之前）而非构造期：换肤后下一次打开拿到的就是新档色。
            top.DropDownOpening += (_, _) => top.DropDown.BackColor = Theme.PanelBackground;
        }
        MainMenuStrip = menu;
        Controls.Add(menu);
    }

    /// <summary>
    /// 底部栏。模拟 UI 需求 §3.3 的最后一行：
    /// 「[拼缝] [切分] [删段] [裁边] [清理]   浏览画布 / 编辑拼缝 2→3   1170 × 7348 px   64%」。
/// 线框里那个 `1170 × 7348 px` 两侧带空格，代码实际打出来的是 `1170×7348 px`（出处 `UpdateSizeInfo`），
/// 第十四轮「人工验收十二步」第 1 步引用的是<b>代码那个字面</b>——照线框去找会找不到那一格。
    /// 五枚工具按钮与状态区放在同一条 StatusStrip 上——它天生支持 Spring 右对齐，
    /// 比再叠一条 Dock=Bottom 的 ToolStrip 更贴近线框，也不会与窗口调整互相抢位置。
    /// </summary>
    private void BuildStatusBar()
    {
        var strip = new StatusStrip
        {
            BackColor = Theme.PanelBackground,
            ForeColor = Theme.TextSecondary,
            Renderer = new DarkToolStripRenderer(),
            SizingGrip = false,
            AutoSize = false,
            Height = Chrome.StatusBarHeight,
            Padding = new Padding(10, 5, 10, 5),
        };

        // 第五十改：槽宽一律"按这一格能出现的最长文字"当场量，不写死像素。
        // 写死会在 150% DPI 下字体变大而槽没变大 → 读数被截；量出来的宽度自带 DPI 口径。
        // 定宽而不是 AutoSize：读数每 500ms 变一次，AutoSize 会让整条右端跟着一跳一跳。
        int Slot(string widest, Font font, int min = 0) =>
            Math.Max(min, TextRenderer.MeasureText(widest, font, new Size(int.MaxValue, int.MaxValue),
                TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine).Width + 16);

        // 五工具入口：文字即用途，悬停有说明（需求 §6 不许出现认不出的图标）。
        // Checked 表示右侧面板当前显示的是哪套参数；点击只切面板，不改任何拼合参数。
        (string Text, ToolKind Tool, string Tip)[] defs =
        {
            ("拼缝", ToolKind.Seam, "检查与调整相邻两张图之间的位移：状态、原因、X/Y、确认或恢复自动"),
            ("切分", ToolKind.Split, "把一段从中间分成两段，之后可单独移动、删除或导出（无损、可撤销）"),
            ("删段", ToolKind.Delete, "从本次拼合中移除某一段；原图文件不会被删除"),
            ("裁边", ToolKind.Crop, "记录「每段取源图的哪一块」，默认不改原图像素"),
            ("清理", ToolKind.Cleanup, "滚动条：可单独开关、逐张开关、可撤销"),
        };
        _toolButtons = new ToolStripButton[defs.Length];
        for (int i = 0; i < defs.Length; i++)
        {
            ToolKind tool = defs[i].Tool;
            var button = new ToolStripButton(defs[i].Text)
            {
                ToolTipText = defs[i].Tip,
                DisplayStyle = ToolStripItemDisplayStyle.Text,
                Font = Theme.UiFontBold,
                AutoSize = false,
                // 高度 36 = Theme.MinTouchTarget：判据 ui/底部五枚工具的点击目标不小于 36 量的就是这两维
                Size = new Size(Slot(defs[i].Text, Theme.UiFontBold, 56), Theme.MinTouchTarget),
                Margin = new Padding(0, 0, 2, 0),
            };
            button.Click += (_, _) => SelectTool(tool);
            _toolButtons[i] = button;
            strip.Items.Add(button);
        }

        strip.Items.Add(new ToolStripSeparator { Margin = new Padding(6, 0, 6, 0) });
        _statusMode = new ToolStripStatusLabel("浏览画布")
        {
            ForeColor = Theme.TextPrimary,
            Font = Theme.UiFontBold,
            AutoSize = false,
            Size = new Size(Slot("编辑拼缝 999→1000", Theme.UiFontBold), Theme.MinTouchTarget),
            TextAlign = ContentAlignment.MiddleLeft,
        };
        _statusInfo = new ToolStripStatusLabel("就绪")
        {
            Spring = true,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = Theme.UiFont,
            ForeColor = Theme.TextSecondary,
        };
        // 需求 §3.3：状态栏右侧持续显示当前模式、画布实际像素和预览缩放比例
        _statusSize = new ToolStripStatusLabel("无内容")
        {
            ForeColor = Theme.TextSecondary,
            Font = Theme.UiFont,
            AutoSize = false,
            Size = new Size(Slot("99999×999999 px", Theme.UiFont), Theme.MinTouchTarget),
            TextAlign = ContentAlignment.MiddleLeft,
        };
        _statusZoom = new ToolStripStatusLabel("—")
        {
            ForeColor = Theme.TextPrimary,
            Font = Theme.MonoFont,
            AutoSize = false,
            Size = new Size(Slot("1000%", Theme.MonoFont), Theme.MinTouchTarget),
            TextAlign = ContentAlignment.MiddleRight,
            ToolTipText = "预览缩放比例（只改预览，不改拼合与导出像素）",
        };
        // ═══ 第十三轮需求五：状态栏持续显示 FPS / 帧耗时 / 缓存命中率 ═══
        // 这三枚读数必须由界面自己长期挂着，而不是只有工装跑起来才看得到——
        // 用户在自己机器上觉得卡，抬头就能看出是"每帧渲染耗时长"还是"缓存没命中在重算"。
        _statusPerf = new ToolStripStatusLabel("帧 —")
        {
            ForeColor = Theme.TextSecondary,
            Font = Theme.MonoFont,
            AutoSize = false,
            Size = new Size(Slot("帧 999.9ms P95 999.9ms 999fps 命中 100%", Theme.MonoFont), Theme.MinTouchTarget),
            TextAlign = ContentAlignment.MiddleLeft,
            ToolTipText = "最近一帧 OnPaint 耗时 / P95 / 最近一秒的 Paint 次数（FPS）/ 瓦片缓存命中率。" +
                          "命中率低 = 每帧都在从源图重算；P95 高 = 拖动会掉帧",
        };
        strip.Items.AddRange(new ToolStripItem[]
            { _statusMode, new ToolStripSeparator { Margin = new Padding(6, 0, 6, 0) },
              _statusInfo, _statusSize, _statusPerf, _statusZoom });
        Controls.Add(strip);

        // 每 500ms 刷新一次。刷新走的是控件自己的计数读数（O(1) 读几个 long），
        // 不订阅 OnPaint——那等于每帧多一次跨线程/跨控件的赋值，测量本身就会变成卡顿源。
        _perfTimer = new System.Windows.Forms.Timer { Interval = 500 };
        _perfTimer.Tick += (_, _) => RefreshPerfLabel();
        _perfTimer.Start();
    }

    private ToolStripStatusLabel _statusPerf = null!;
    private System.Windows.Forms.Timer? _perfTimer;

    /// <summary>把画布自己的帧测量读数抄到状态栏（画布还没建好就什么都不写，不许抛）。</summary>
    private void RefreshPerfLabel()
    {
        if (_statusPerf is null || _canvas is null) return;
        if (_canvas.FrameSampleCount == 0)
        {
            _statusPerf.Text = "帧 —";
            return;
        }
        _statusPerf.Text = $"帧 {_canvas.LastFrameMs:F1}ms P95 {_canvas.FramePercentileMs(0.95):F1}ms " +
                           $"{_canvas.Fps:F0}fps 命中 {_canvas.CacheHitRate:P0}";
    }

    /// <summary>切换右侧面板到指定工具，并同步底部按钮的选中态。</summary>
    private void SelectTool(ToolKind tool)
    {
        _toolPanel.Tool = tool;
        for (int i = 0; i < _toolButtons.Length; i++)
            _toolButtons[i].Checked = (ToolKind)i == tool;
        // 离开清理面板时收起补丁高亮框——那是"看清理动了哪里"的辅助层，不该残留
        if (tool != ToolKind.Cleanup) _canvas.ShowPatchOverlay = false;
    }

    // ───────────────────────── 导入 ─────────────────────────

    private void AddImagesViaDialog()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "选择截图",
            Multiselect = true,
            Filter = "图片文件|*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.tif;*.tiff|所有文件|*.*",
        };
        if (dialog.ShowDialog(this) == DialogResult.OK) ImportPaths(dialog.FileNames);
    }

    private void OnFileDragEnter(object? sender, DragEventArgs e)
    {
        e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true
            ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private void OnFileDragDrop(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetData(DataFormats.FileDrop) is string[] paths) ImportPaths(paths);
    }

    /// <summary>
    /// 导入一批路径：展开文件夹、跳过不支持的格式、逐项报告失败原因，
    /// 成功的照常进入项目（需求 §4.1"对损坏图片逐项提示；有效图片继续导入"）。
    /// </summary>
    public void ImportPaths(IEnumerable<string> paths)
    {
        DiagnosticLog.Info("import.begin", $"revision={_project.Revision} mode={_project.Doc.Mode}");
        var files = new List<string>();
        foreach (string path in paths)
        {
            if (Directory.Exists(path))
                // 文件夹本身没有"用户排好的顺序"，展开时按名字排保证结果确定；
                // 用户逐个选中的文件则严格保持传入顺序（需求 §4.1）。
                files.AddRange(Directory.EnumerateFiles(path, "*.*", SearchOption.AllDirectories)
                    .Where(IsSupportedExtension)
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase));
            else if (File.Exists(path) && IsSupportedExtension(path)) files.Add(path);
            else if (path.EndsWith(ProjectFile.Extension, StringComparison.OrdinalIgnoreCase) && File.Exists(path))
            {
                OpenProjectFrom(path);
                return;
            }
            else if (path.EndsWith(ProjectPackage.Extension, StringComparison.OrdinalIgnoreCase) && File.Exists(path))
            {
                // 双击 .lstickx 或把它拖进窗口 = 打开打包项目
                OpenPackageFrom(path);
                return;
            }
        }

        if (files.Count == 0)
        {
            Status("没有可导入的图片（支持 PNG / JPEG / BMP / TIFF；WebP 需本机装有对应编解码组件）");
            return;
        }

        // 注意：这里**不再**按文件名重排整批。默认顺序 = 导入顺序；
        // 要按名字排请用菜单「编辑 → 按文件名排序」（显式操作，可撤销）。
        var failures = new List<string>();
        int added = 0;

        if (_busy)
        {
            // 忙碌闸：上一趟导入或分析还没跑完。此时泵过消息，重入是真的会发生的（见 _busy 的说明）。
            Status("上一趟还没跑完，这一次先不接——等进度条走完再拖");
            DiagnosticLog.Info("import.rejected", $"reason=busy files={files.Count} revision={_project.Revision}");
            return;
        }

        UseWaitCursor = true;
        _busy = true;
        // 遮罩在这一趟一开始就盖上：解码几十张连拍要几秒，期间画布上是半成品，
        // 用户看得见却动不了，不糊起来只会让人以为卡死了。
        using (Bitmap? frame = CaptureCanvasFrame())
            _overlay.Begin(frame, $"正在导入 {files.Count} 张图片", files.Count);
        try
        {
            BeginUiBatch();   // 逐张通知合并成出口那一次，见 BeginUiBatch 的说明
            int index = 0;
            foreach (string file in files)
            {
                try
                {
                    DecodedImage decoded = FileSourceResolver.Decode(file);
                    var source = new SourceImage
                    {
                        Id = Project.NewId("src"),
                        Path = file,
                        FileName = Path.GetFileName(file),
                        Size = decoded.Image.Size,
                        ExifOrientation = decoded.ExifOrientation,
                        FileSizeBytes = new FileInfo(file).Length,
                        FileModifiedUtcMs = File.GetLastWriteTimeUtc(file).Ticks / TimeSpan.TicksPerMillisecond,
                        Format = decoded.Format,
                        ImportWarnings = decoded.Warnings.ToList(),
                        PngFormat = decoded.Format == "png"
                            ? new Core.Imaging.Png.PngFormatInfo
                            {
                                Width = decoded.Image.Width, Height = decoded.Image.Height,
                                BitDepth = 8, ColorType = Core.Imaging.Png.PngColorType.Rgba,
                            }
                            : null,
                    };
                    _resolver.Register(source);
                    // 第五十九改③：这次解码出来的像素交给 Resolver 缓存（所有权移交），
                    // 不再"取完元数据就扔"。画布首屏/缩略图/分析随后 Resolve 全部命中缓存，
                    // 同一张文件不再解码第二遍——判据 ui/导入 N 张 PNG 全程只解码 N 次（预热缓存与重新解码逐字节同源） 从
                    // FileSourceResolver.DecodeCallsTotalForTest 取数。
                    _resolver.Prime(source.Id, decoded.Image);
                    _project.Doc.Sources.Add(source);
                    _project.AppendSegmentForSource(source);
                    added++;
                    DiagnosticLog.Info("import.file_ok", $"path={file} size={source.Size} format={source.Format} warnings={source.ImportWarnings.Count}");
                }
                catch (Exception ex)
                {
                    failures.Add($"{Path.GetFileName(file)}：{ex.Message}");
                    DiagnosticLog.Error("import.file_failed", ex, $"path={file}");
                }
                // 进度条要真的往前走一格：这一趟是同步循环，不泵一次消息它就一直停在 0
                _overlay.Report(++index, files.Count, $"正在导入 {index}/{files.Count}：{Path.GetFileName(file)}");
                PumpUi();
            }

            // 段与缝都齐了才算"这一批做完"，所以这两条也留在批次里：
            // 放在批次之外的话，导入一趟就是三次刷新（放闸一次 + 这两条各一次）。
            _project.EnsureSeams();
            _project.Touch();
            // 内容和视口都定了，把窄内容摆回正中（用户第七条）。分析会把画布高度改掉，
            // 所以 AnalyzeAll 写回那一下还要再摆一次。
            _canvas.FollowContentCentre();
        }
        finally
        {
            _overlay.End();
            // 放闸必须落在 finally：循环里抛异常时唯一的出口就是这里，
            // 放闸留在正常路径上，深度会永久卡在 1、界面从此再也不刷新。
            EndUiBatch();
            _busy = false;
            UseWaitCursor = false;
        }

        if (failures.Count > 0)
            MessageBox.Show(this,
                $"{added} 张已导入，{failures.Count} 张无法读取：\n\n" +
                string.Join("\n", failures.Take(12)) +
                (failures.Count > 12 ? $"\n… 还有 {failures.Count - 12} 项" : ""),
                "导入结果", MessageBoxButtons.OK, MessageBoxIcon.Warning);

        Status($"导入 {added} 张" + (failures.Count > 0 ? $"，{failures.Count} 张失败" : ""));
        DiagnosticLog.Info("import.end", $"added={added} failed={failures.Count} revision={_project.Revision}");
        Diagnostics.MemorySnapshot.Log("import");
        if (added >= 2 && _project.Doc.Mode == StitchMode.LongScroll) AnalyzeAll();
    }

    private static bool IsSupportedExtension(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp" or ".tif" or ".tiff";
    }

    // ───────────────────────── 分析 ─────────────────────────

    /// <summary>后台跑自动匹配。跑的时候仍可浏览已完成区域（需求 §8）。</summary>
    private async void AnalyzeAll()
    {
        if (_project.Doc.Segments.Count < 2)
        {
            Status("至少需要 2 张图片才能自动匹配拼缝");
            return;
        }

        if (_analysisCts is not null) await _analysisCts.CancelAsync();
        _analysisCts = new CancellationTokenSource();
        int total = _project.Doc.Seams.Count;
        // ═══ 进度条这一头原来是一枚空转 ═══
        // 这个 Progress<> 从第十一轮起就摆在这里，回调写着"正在分析拼缝 N/M…"，
        // 但后台那个循环<b>一次都没 Report 过</b>——状态栏因此永远停在上一句话上，
        // 而读代码的人看见变量在、回调在，会以为进度是报着的。
        // 这与第四十改那颗 PerformClick 空转同族：<b>建了不等于走了</b>。
        // 现在循环里每分析完一条就报一次，遮罩与状态栏读的是同一份数。
        // 报进度的是 IProgress<T> 那个接口（Report 在接口上，Progress<T> 类本身没有公开它）。
        IProgress<(int Done, int Total)> progress = new Progress<(int Done, int Total)>(p =>
        {
            Status($"正在分析拼缝 {p.Done}/{p.Total}…");
            _overlay.Report(p.Done, p.Total);
        });
        var captured = _project.Layout;

        UseWaitCursor = true;
        _busy = true;
        using (Bitmap? frame = CaptureCanvasFrame())
            _overlay.Begin(frame, $"正在分析 {total} 条拼缝", total);

        Project project = _project;
        AnalysisGate.Ticket ticket = _analysisGate.Begin(project);
        CancellationToken token = _analysisCts.Token;
        DiagnosticLog.Info("analysis.all_begin", $"generation={ticket.Generation} revision={ticket.Revision} seams={captured.DocSeams().Count()} mode={project.Doc.Mode}");
        try
        {
            await Task.Run(() =>
            {
                // 分析在后台线程读参数、写参数；Project 不是线程安全的，
                // 所以这里只读 Layout 快照，写回统一回到 UI 线程（见下面的 Invoke）
                var results = new List<(string SeamId, IntPoint Offset, double Confidence,
                    int Overlap, SeamStatus Status, SeamCause Cause, string Text, bool HasSuggestion,
                    List<MatchCandidate> Candidates, int Cut)>();
                // 先把这一趟要分析的缝列出来：进度条那个分母必须与循环真正走的趟数<b>同源</b>，
                // 一边写 captured.DocSeams().Count()、一边另算一个数，两边迟早会对不上。
                List<Seam> seams = captured.DocSeams().ToList();
                int done = 0;
                foreach (Seam seam in seams)
                {
                    token.ThrowIfCancellationRequested();
                    if (TryAnalyzeSnapshot(project, captured, seam, out var row) && row is not null)
                        results.Add(row.Value);
                    // 失败与"没算出结果"的那几条也要计数：进度条报的是"这一趟走到第几条了"，
                    // 不是"成功了几条"——只数成功的话，遇到一串坏图进度条就再也不动了。
                    progress.Report((++done, seams.Count));
                }

                BeginInvoke(() =>
                {
                    // 闸门（审查第四轮 P2）：BeginInvoke 排进队列后才取消/改参数/换项目，
                    // 回调依然会执行——只靠 token 拦不住它。
                    if (!_analysisGate.Allows(ticket, _project))
                    {
                        DiagnosticLog.Info("analysis.all_discarded", $"generation={ticket.Generation} results={results.Count} reason={_analysisGate.RejectReason(ticket, _project)}");
                        Status($"本批 {results.Count} 条分析结果未写回：{_analysisGate.RejectReason(ticket, _project)}");
                        return;
                    }

                    project.Mutate("自动匹配", d =>
                    {
                        foreach (var row in results)
                        {
                            Seam? target = d.Seams.FirstOrDefault(s => s.Id == row.SeamId);
                            if (target is null) continue;
                            // 已有手动偏移的缝只更新自动侧信息，显示状态保持手动（P1）
                            // 第九轮：切点也一起交出去——ApplyAutoResult 是唯一的写回出口，
                            // 它会保住用户手工切过的缝不被覆盖。
                            target.ApplyAutoResult(row.Offset, row.Status, row.Cause, row.Text,
                                row.Confidence, row.Overlap, row.Candidates, row.Cut);
                        }
                    });
                    Status($"分析完成：{results.Count} 条拼缝，" +
                           $"{results.Count(r => r.Status == SeamStatus.AutoPassed)} 条自动通过，" +
                           $"{results.Count(r => r.Status == SeamStatus.NeedsReview)} 条需检查");
                    // 写回会改掉各段的重叠量，画布总高跟着变 —— 内容尺寸变了就得重新摆正（用户第七条）。
                    _canvas.FollowContentCentre();
                    DiagnosticLog.Info("analysis.all_done", $"generation={ticket.Generation} results={results.Count} passed={results.Count(r => r.Status == SeamStatus.AutoPassed)} review={results.Count(r => r.Status == SeamStatus.NeedsReview)} revision={project.Revision}");
                    Diagnostics.MemorySnapshot.Log("analyze_all");
                });
            }, token);
        }
        catch (OperationCanceledException)
        {
            DiagnosticLog.Info("analysis.all_cancelled", $"generation={ticket.Generation}");
            Status("分析已取消");
        }
        catch (Exception ex)
        {
            DiagnosticLog.Error("analysis.all_failed", ex, $"generation={ticket.Generation} revision={ticket.Revision}");
            Status("分析失败：" + ex.Message);
        }
        finally
        {
            // 遮罩只在这里收（一处出口）：Task.Run 一返回就收，写回那一下紧跟在后，
            // 用户看到的是"进度条走完 → 画布恢复"。放进 BeginInvoke 回调里收的话，
            // 句柄还没建起来（离屏工装）那一趟就永远收不掉，画布会被一层糊住的遮罩钉死。
            _overlay.End();
            _busy = false;
            UseWaitCursor = false;
        }
    }

    /// <summary>
    /// 在后台线程对<b>布局快照</b>做一条拼缝的分析。
    /// 只算不改参数，因此不需要锁；写回由调用方在 UI 线程一次性完成。
    /// </summary>
    private bool TryAnalyzeSnapshot(Project project, CanvasLayout snapshot, Seam seam,
        out (string SeamId, IntPoint Offset, double Confidence, int Overlap, SeamStatus Status,
            SeamCause Cause, string Text, bool HasSuggestion, List<MatchCandidate> Candidates,
            int Cut)? result)
    {
        result = null;
        PlacedSegment? before = snapshot.Find(seam.BeforeId);
        PlacedSegment? after = snapshot.Find(seam.AfterId);
        if (before is null || after is null) return false;

        try
        {
            // 参数按模式生成：长截图锁横向。竖拼/横拼照旧允许少量错位。
            MatchOptions options = MatchOptions.For(project.Doc.Mode);
            DiagnosticLog.Info("analysis.seam.begin",
                $"seam={seam.Id} before={seam.BeforeId} after={seam.AfterId} " +
                $"beforeSize={before.Rect.Width}x{before.Rect.Height} afterSize={after.Rect.Width}x{after.Rect.Height} " +
                $"mode={project.Doc.Mode} lock_horizontal={options.LockHorizontal} " +
                $"min_overlap={options.MinOverlap} pass_similarity={options.PassSimilarity:F3}");

            // 匹配的两个输入必须取<b>未裁重叠</b>的完整内容：蓝线切掉的那几行正是两张截图
            // 公共的那一段，切完再匹配就一行公共内容都没有 ⇒ 重分析整批归零（用户第四十四改）。
            // 出处与理由见 CanvasComposer.RenderMatchInput 的注释。
            RgbaImage imageBefore = _composer.RenderMatchInput(before);
            RgbaImage imageAfter = _composer.RenderMatchInput(after);
            MatchResult match = OverlapMatcher.Match(imageBefore, imageAfter, options);
            // detected_x 是"数据想横着挪多少"，offset 是"真正采用的位移"。
            // 两个都要记：只有后者时，一次被锁掉的横向偏移在日志里就完全不存在了。
            DiagnosticLog.Info("analysis.seam.result",
                $"seam={seam.Id} status={match.Status} cause={match.Cause} " +
                $"offset=({match.Offset.X},{match.Offset.Y}) detected_x={match.DetectedHorizontalShift} " +
                $"x_locked={match.HorizontalShiftLocked} overlap={match.OverlapLength} " +
                $"confidence={match.Confidence:F4} uniqueness={match.Uniqueness:F5} " +
                $"candidates={match.Candidates.Count} suggestion={match.HasSuggestion}");
            if (match.HorizontalShiftLocked)
                DiagnosticLog.Info("analysis.longscroll.constraint",
                    $"seam={seam.Id} source=自动匹配 requested_x={match.DetectedHorizontalShift} " +
                    $"allowed_x={match.Offset.X} dropped_x={match.DetectedHorizontalShift - match.Offset.X}");
            result = (seam.Id, match.Offset, match.Confidence, match.OverlapLength,
                match.Status, match.Cause, match.CauseText, match.HasSuggestion, match.Candidates.ToList(),
                match.SeamCutPosition);
            return true;
        }
        catch (Exception ex) when (ex is IOException or FileNotFoundException or KeyNotFoundException)
        {
            DiagnosticLog.Error("analysis.seam_failed", ex, $"seam={seam.Id}");
            BeginInvoke(() => Status($"这条拼缝算不了：{ex.Message}"));
            return false;
        }
    }

    private void GotoNextReview()
    {
        Seam? next = _project.Doc.Seams.FirstOrDefault(s =>
            s.Status == SeamStatus.NeedsReview && s.Id != _canvas.SelectedSeamId)
            ?? _project.Doc.Seams.FirstOrDefault(s => s.Status == SeamStatus.NeedsReview);
        if (next is null) { Status("没有需要检查的拼缝"); return; }
        _canvas.SelectSeam(next.Id, scrollIntoView: true);
        _toolPanel.SeamId = next.Id;
        UpdateSeamList();
    }

    // ───────────────────────── 刷新 ─────────────────────────

    private void RefreshChrome()
    {
        if (InvokeRequired) { BeginInvoke(RefreshChrome); return; }

        // 批次里先挂起：见 BeginUiBatch 的说明。放闸那一下统一刷，屏上只出现一次最终状态。
        if (_uiBatchDepth > 0)
        {
            _uiBatchQueued = true;
            UiRefreshDeferredForTest++;
            return;
        }
        UiRefreshCountForTest++;

        LogLayoutSeams();

        Text = $"LongStitch · 新版工作台 · {_project.Doc.Title}{(_project.IsDirty ? "*" : "")}" +
               (_project.FilePath is null ? "" : $" — {Path.GetFileName(_project.FilePath)}");
        // 第五十改：项目名从"只写在标题栏"改成"顶栏右端也常驻一枚"。
        // 窗口一多、标题栏一被系统截断，屏上就再没有能指认"我在改哪一份"的地方了。
        if (_projectTitle is not null)
        {
            _projectTitle.Text = $"{_project.Doc.Title}{(_project.IsDirty ? " *" : "")}";
            _projectTitle.ToolTipText = _project.FilePath is null
                ? "还没保存到磁盘（Ctrl+S 存成 .lstickx）"
                : _project.FilePath;
        }
        _undoItem.Text = _project.CanUndo ? $"撤销 {_project.NextUndoLabel}" : "撤销";
        _undoItem.Enabled = _project.CanUndo;
        _redoItem.Text = _project.CanRedo ? $"重做 {_project.NextRedoLabel}" : "重做";
        _redoItem.Enabled = _project.CanRedo;

        UpdateModeButtons();
        UpdateToolAvailability();
        UpdateSeamList();
        // 面板那几格读数必须跟着参数走（第十一轮：拖完切点面板还停在旧数=界面在骗人）。
        // 它按修订号判过期，所以"每次改动都调"不会变成"每帧都重建"。
        _toolPanel.RefreshDisplayedSeam();
        UpdateZoomLabel();
        UpdateSizeInfo();
    }

    /// <summary>
    /// 每次参数改动记一版"布局拼缝账"：每条缝两侧的尺寸、生效位移、画布上量到的重叠、
    /// 与分析记录是否一致、公式是否成立。这是排查"去重到底有没有兑现"的第一手材料——
    /// 光看成品图说不清，这几个数一眼就能看出哪条缝在骗人。
    /// 只写数字与 Id，绝不写像素内容或文件路径。
    /// </summary>
    /// <remarks>
    /// 同一个 Revision 只记一次（重绘、切焦点都会走这里）；超过
    /// <see cref="LayoutSeamLogCap"/> 条的项目只记前若干条并写明截断，
    /// 否则几百张连拍一次改动就是几百行，日志本身没法看了。全量请走 <c>--stitch</c> 工装。
    /// </remarks>
    private void LogLayoutSeams()
    {
        CanvasLayout layout = _project.Layout;
        if (layout.SeamCount == 0) return;
        if (_layoutSeamsLoggedRevision == _project.Revision) return;
        _layoutSeamsLoggedRevision = _project.Revision;

        int logged = 0;
        foreach (Seam seam in _project.Doc.Seams)
        {
            if (logged >= LayoutSeamLogCap)
            {
                DiagnosticLog.Info("layout.seam.truncated",
                    $"revision={_project.Revision} logged={logged} seams={_project.Doc.Seams.Count} " +
                    "全量账请用 dotnet run --project checks/LongStitch.Checks -- --stitch <目录>");
                return;
            }
            SeamGeometry? geometry = layout.DescribeSeam(seam);
            if (geometry is null) continue;
            DiagnosticLog.Info("layout.seam", $"revision={_project.Revision} mode={layout.Mode} {geometry}");
            // 第九轮：裁切这本账再单独发一条（字段与上一行同源，不另算一遍）。
            // 分成两个事件名的用处是：查"成品为什么高了/矮了"只 grep layout.seam.crop，
            // 不必在一堆位移字段里找那两个裁切量。
            DiagnosticLog.Info("layout.seam.crop",
                $"revision={_project.Revision} crop={layout.UsesCropPlacement} " +
                $"canvasHeight={layout.CanvasSize.Height} seam={seam.Id} " +
                $"beforeCropBottom={geometry.AppliedBeforeCrop} afterCropTop={geometry.AppliedAfterCrop} " +
                $"overlap={geometry.Overlap} cut={geometry.CutPosition} cropMode={geometry.CropMode} " +
                $"manualCut={geometry.ManualCut} beforeSourceRect={geometry.BeforeSourceRect} " +
                $"afterSourceRect={geometry.AfterSourceRect} rectsDisjoint={geometry.RectsDisjoint} " +
                $"cropsAsRequested={geometry.CropsAppliedAsRequested} cropHolds={geometry.CropInvariantHolds}");
            logged++;
        }
    }

    private void UpdateZoomLabel()
    {
        if (_zoomLabel is null || _canvas is null) return;
        string text = $"{_canvas.Viewport.Zoom * 100:F0}%";
        _zoomLabel.Text = text;
        // 需求 §3.3：缩放比例要在底部状态区持续可见（画布上方按钮只是快捷入口）。
        // 建控件树时布局会先于状态栏触发一次视口变化，所以这里要判空。
        if (_statusZoom is not null) _statusZoom.Text = text;
    }

    private void UpdateSizeInfo()
    {
        // 与 UpdateZoomLabel 同理：状态栏/菜单项可能在控件树建好之前就被布局回调碰到
        if (_statusMode is null || _statusSize is null || _exportItem is null) return;
        CanvasLayout layout = _project.Layout;
        // 模式区按线框写成"浏览画布 / 编辑拼缝 2→3"这种可指认的形态
        int selected = _canvas.SelectedSeamId is null
            ? -1 : _project.Doc.Seams.FindIndex(s => s.Id == _canvas.SelectedSeamId);
        _statusMode.Text = selected < 0
            ? "浏览画布"
            : $"编辑拼缝 {selected + 1}→{selected + 2}";
        _statusMode.ForeColor = selected < 0 ? Theme.TextPrimary : Theme.Accent;
        // 画布工具条右端那枚角标与状态栏是同一份读数的两个出口（需求 §7：一眼分辨浏览还是编辑）。
        // 状态栏在屏幕最底下，视线在画布上时看不见它，所以把这句搬到画布正上方再说一遍。
        _modeBadge?.Set(selected >= 0, _statusMode.Text);
        string extra = layout.IsEmpty ? "无内容" : $"{layout.CanvasSize.Width}×{layout.CanvasSize.Height} px";
        _statusSize.Text = extra;
        _statusSize.ToolTipText = "画布实际像素（源图口径，与预览缩放无关）";
        int missing = _project.Doc.Sources.Count(s => !File.Exists(s.Path));
        if (missing > 0) extra += $" · {missing} 张源文件丢失";
        _exportItem.Text = $"导出…（{extra}）";
        _statusInfo.ToolTipText = extra;
    }

    private void UpdateSeamList()
    {
        if (_seamList is null) return;
        _seamList.BeginUpdate();
        _seamList.Items.Clear();
        _listedSeams.Clear();
        int passed = 0, review = 0;
        bool onlyReview = _seamFilter is not null && _seamFilter.Checked;
        foreach (Seam seam in _project.Doc.Seams)
        {
            bool needsReview = seam.Status is SeamStatus.NeedsReview or SeamStatus.ManualPending;
            if (seam.Status is SeamStatus.AutoPassed or SeamStatus.ManualConfirmed) passed++;
            if (needsReview) review++;
            if (onlyReview && !needsReview) continue;
            _listedSeams.Add(seam);
            _seamList.Items.Add(seam);
        }
        _seamList.EndUpdate();
        // 第五十五改④：条目数变了就把滚动状态读回自绘条（与左栏素材列表同一收口）。
        _seamListHost.SyncFromList();
        // 统计口径始终是**全部**拼缝：筛选只改视图，不能让人以为"通过了 12 条"其实只剩筛出来的几条
        _seamSummary.Text = $"{_project.Doc.Seams.Count} 条拼缝 · {passed} 已通过 · {review} 需检查" +
                            (onlyReview ? $"（已筛出 {_listedSeams.Count} 条）" : "");
    }

    /// <summary>
    /// 第五十改：拼缝行改成"两行式卡片"。
    ///
    /// 旧版是 22px 高的一行三段（序号｜状态｜摘要），在 268px 宽的侧栏里摘要只剩几十像素，
    /// 长一点的原因文字全被省略号吃掉——而"为什么这条缝被标 !"恰恰是用户最该在列表里看见的东西。
    /// 现在一行 40px：上行放「序号 + 状态」，下行整宽放原因摘要；左侧一枚状态圆点，
    /// 颜色与右侧面板、画布上的缝线同源（<see cref="Theme.StatusColor"/>），
    /// 但**绝不只靠颜色**——同一行永远有状态文字（需求 §6）。
    ///
    /// 选中行仍然是"整块强调蓝 + 写死白字"，与左栏素材行同一条约定：
    /// 换肤时白字不参与重映射，否则浅色主题下会翻成浅底浅字。
    /// </summary>
    private void OnDrawSeamItem(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= _seamList.Items.Count) return;
        var seam = (Seam)_seamList.Items[e.Index];
        bool selected = seam.Id == _canvas.SelectedSeamId;
        // 序号取**文档里的真实位置**：筛选或跳转后行号与序号本来就不一致
        int realIndex = _project.Doc.Seams.IndexOf(seam);

        Graphics g = e.Graphics;
        using (var bg = new SolidBrush(_seamList.BackColor))
            g.FillRectangle(bg, e.Bounds);

        // 第五十五改④：列表被宿主加宽以藏原生滚动条，卡片按"可见行宽"收右沿。
        int rowW = _seamList.RowWidth > 0 ? _seamList.RowWidth : e.Bounds.Width;
        Rectangle card = new(e.Bounds.X + 6, e.Bounds.Y + 3,
            Math.Max(0, rowW - 12), Math.Max(0, e.Bounds.Height - 6));
        if (selected)
        {
            Chrome.Card(g, card, Theme.Accent, Chrome.RadiusSmall, bevel: false);
        }
        else
        {
            // 未选中行只在底部留一道发丝线：比每行一个灰框轻，滚起来不花。
            using var rule = new Pen(Chrome.HairlineSoft, 1f);
            g.DrawLine(rule, card.X + 2, e.Bounds.Bottom - 1, card.Right - 2, e.Bounds.Bottom - 1);
        }

        Color statusColor = Theme.StatusColor(seam.Status);
        Color ink = selected ? Color.White : Theme.TextPrimary;
        Color sub = selected ? Color.White : Theme.TextSecondary;

        // 状态圆点：选中时用白描边空心点（蓝底上再放绿点会脏），未选中时实心状态色。
        var dot = new Rectangle(card.X + 10, card.Y + (card.Height - 9) / 2, 9, 9);
        var smooth = g.SmoothingMode;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        if (selected)
        {
            using var ring = new Pen(Color.White, 1.6f);
            g.DrawEllipse(ring, dot);
        }
        else
        {
            using var fill = new SolidBrush(statusColor);
            g.FillEllipse(fill, dot);
        }
        g.SmoothingMode = smooth;

        int textX = dot.Right + 8;
        int textRight = card.Right - 10;
        string index = $"{realIndex + 1}→{realIndex + 2}";
        Size indexSize = Chrome.Measure(g, index, Theme.MonoFont);
        // 第五十一改回修（用户图1/图2红框）：两行文本盒原来写死 17/16px——96 DPI 的字高。
        // 150% DPI 下字体长到 ~24px，盒子装不下，第二行叠在第一行的下缘上（「依旧受到挤压」）。
        // 盒高与行高（<c>Chrome.SeamRowHeight</c>）都从字体现量推导，是同一段账的两半，
        // 任何 DPI 下整枚字框都落得下。
        int topH = Chrome.SeamRowTopHeight;
        int sumH = Chrome.SeamRowSummaryHeight;
        var top = new Rectangle(textX, card.Y + 3, Math.Max(0, textRight - textX), topH);
        Chrome.Text(g, index, new Rectangle(textX, top.Y, indexSize.Width + 2, top.Height),
            ink, Theme.MonoFont, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

        // 状态文字右对齐贴边：序号长度会变（1→2 与 99→100），右对齐才不会把状态推来推去。
        string prefix = Theme.StatusText(seam.Status);
        int statusLeft = textX + indexSize.Width + 10;
        if (statusLeft < textRight)
            Chrome.Text(g, prefix, new Rectangle(statusLeft, top.Y, textRight - statusLeft, top.Height),
                selected ? Color.White : statusColor, Theme.UiFontMicro,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter |
                TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

        // 需求 §3.2：每行还要有"问题摘要"。没有原因文字时退回原因枚举的名字，
        // 这样"为什么被标 ! "在列表里就能看出来，不必逐条点进右侧面板。
        string summary = seam.CauseText is { Length: > 0 } t ? t : seam.Cause.ToString();
        if (summary.Length == 0 || summary == "None")
            summary = seam.Status switch
            {
                SeamStatus.AutoPassed => "自动匹配通过",
                SeamStatus.ManualConfirmed => "已人工确认",
                SeamStatus.Pending => "尚未分析",
                SeamStatus.Analyzing => "正在分析…",
                _ => "—",
            };
        // 第五十一改（用户图5红框）：摘要行原来 UiFontSmall 塞进 15px 盒，字框比盒高、下半截被裁
        // （「文字受到挤压，字号太大」）。降成 UiFontMicro；盒高回修后按字体现量（见上面的 topH/sumH）。
        Chrome.Text(g, summary,
            new Rectangle(textX, top.Bottom + 2, Math.Max(0, textRight - textX), sumH),
            sub, Theme.UiFontMicro,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
            TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }

    private void OnSeamListSelected(object? sender, EventArgs e)
    {
        // 高亮是画布那侧同步过来的，不是用户点的这一行：必须原样退回，
        // 否则这条"点列表=跳过去"的通路会在画布按下的一瞬间被反着走一遍（见下面的守卫）。
        if (_syncingSeamSelection) return;
        int index = _seamList.SelectedIndex;
        if (index < 0 || index >= _listedSeams.Count) return;
        Seam seam = _listedSeams[index];
        _canvas.SelectSeam(seam.Id, scrollIntoView: true);
        _toolPanel.SeamId = seam.Id;
        _toolPanel.Tool = ToolKind.Seam;
    }

    /// <summary>
    /// 正在由画布往列表同步高亮（而不是用户点列表）。
    /// 没有这一面旗，画布按下→选中→列表高亮→又回调 <see cref="OnSeamListSelected"/>
    /// →再 <c>SelectSeam(scrollIntoView: true)</c>：一次手势刚开始就把视口推走，
    /// 顺带整块 <see cref="RefreshChrome"/>。需求四写得很明白：拖切点这一趟不许动视口。
    /// </summary>
    private bool _syncingSeamSelection;

    private void OnCanvasSelectionChanged(string? seamId)
    {
        _toolPanel.SeamId = seamId;
        if (seamId is not null) _toolPanel.Tool = ToolKind.Seam;
        int index = seamId is null ? -1 : _listedSeams.FindIndex(s => s.Id == seamId);
        _syncingSeamSelection = true;
        try
        {
            if (index >= 0 && index < _seamList.Items.Count) _seamList.SelectedIndex = index;
            else if (seamId is null || index < 0) _seamList.SelectedIndex = -1;
        }
        finally { _syncingSeamSelection = false; }
        RefreshChrome();
    }

    private void OnToolCommand(string command)
    {
        string[] parts = command.Split(':', 2);
        switch (parts[0])
        {
            // 面板那两个勾选框与画布上方那两枚按钮说的是同一件事，所以两路都必须走同一个写入出口。
            // 旧写法各写一半（"diff" 只开差异、只把「对比」按钮的勾抹掉，从不去关画布那层重影），
            // 用户看到的"点了自动取消但画布还在显示、得再点一次"就断在这里。见 SetOverlapView 的注释。
            case "diff":
                ApplyOverlapCommand("diff", parts[1] == "1");
                break;
            case "overlay":
                ApplyOverlapCommand("overlay", parts[1] == "1");
                break;
            case "opacity":
                if (int.TryParse(parts[1], out int value)) _canvas.OverlapOpacityPercent = value;
                break;
            case "goto": _canvas.SelectSeam(parts[1], scrollIntoView: true); break;
            // 面板「确认拼缝」＝用户把"看过且没问题"当一件事：确认即退出编辑（与 ✓／Esc 同口径）。
            case "exit-edit": _canvas.TryHandleEscape(); break;
            case "analyze": AnalyzeOneAsync(parts[1]); break;
            case "split-pick": StartSplitPick(); break;
            case "patch-overlay": _canvas.ShowPatchOverlay = parts[1] == "1"; break;
        }
        _canvas.Invalidate();
    }

    /// <summary>画布悬停提示；离开拼缝时立刻收起，避免残留一句"编辑拼缝 3→4"。</summary>
    private void OnHoverSeamTip(string? tip, Point at)
    {
        _hoverTip ??= new ToolTip { AutoPopDelay = 4000, InitialDelay = 250, ReshowDelay = 100 };
        if (tip is null) { _hoverTip.Hide(_canvas); return; }
        _hoverTip.Show(tip, _canvas, at.X + 16, at.Y + 18, 3000);
    }

    /// <summary>
    /// 只重新分析一条拼缝。与全量分析同规则加了三重保护（审查第三轮 #1/#4）：
    ///   ① 计算用的是开始时刻的布局快照；
    ///   ② 写回前发现<b>项目 Revision 变过</b>（用户动过任何东西）→ 整条结果丢弃；
    ///   ③ 即使 Revision 没变但这条缝的手动偏移/确认状态与开始时不一致 → 不覆盖。
    /// 并入 <see cref="_analysisCts"/>，与全量分析共用取消。
    /// </summary>
    private async void AnalyzeOneAsync(string seamId)
    {
        Seam? seam = _project.Doc.Seams.FirstOrDefault(s => s.Id == seamId);
        if (seam is null) { Status("这条拼缝已不存在"); return; }

        CanvasLayout snapshot = _project.Layout;

        if (_analysisCts is not null) await _analysisCts.CancelAsync();
        _analysisCts = new CancellationTokenSource();
        CancellationToken token = _analysisCts.Token;
        // 与全量分析共用同一个闸门：这条单票一取，尚未写回的全量旧轮次立刻作废
        Project project = _project;
        AnalysisGate.Ticket ticket = _analysisGate.Begin(project);
        DiagnosticLog.Info("analysis.one_begin", $"generation={ticket.Generation} revision={ticket.Revision} seam={seamId} manual={seam.IsManual}");
        Status("正在重新分析这条拼缝…");

        try
        {
            (string SeamId, IntPoint Offset, double Confidence, int Overlap, SeamStatus Status,
                SeamCause Cause, string Text, bool HasSuggestion, List<MatchCandidate> Candidates, int Cut)? row =
                await Task.Run(() =>
                    TryAnalyzeSnapshot(_project, snapshot, seam, out var r) && r is not null &&
                    r.Value.SeamId == seamId ? r : null, token);

            if (row is null)
            {
                DiagnosticLog.Info("analysis.one_no_result", $"generation={ticket.Generation} seam={seamId}");
                Status("这条拼缝已无法分析（片段可能被删或重排）");
                return;
            }
            // 闸门与全量分析同规则（审查第四轮 P2）
            if (!_analysisGate.Allows(ticket, _project))
            {
                DiagnosticLog.Info("analysis.one_discarded", $"generation={ticket.Generation} seam={seamId} reason={_analysisGate.RejectReason(ticket, _project)}");
                Status($"本次结果已丢弃，可重试：{_analysisGate.RejectReason(ticket, _project)}");
                return;
            }

            var value = row.Value;
            _project.Mutate("重新分析", d =>
            {
                Seam? t = d.Seams.FirstOrDefault(s => s.Id == value.SeamId);
                if (t is null) return;
                // 手动偏移还在 → 只刷新自动侧信息，显示状态维持手动（审查第四轮 P1）
                t.ApplyAutoResult(value.Offset, value.Status, value.Cause, value.Text,
                    value.Confidence, value.Overlap, value.Candidates, value.Cut);
            });
            Seam after = _project.Doc.Seams.First(s => s.Id == seamId);
            Status(after.IsManual
                ? $"重新分析完成：自动建议已更新；该缝仍按手动偏移，显示状态保持 {Theme.StatusText(after.Status)}"
                : $"重新分析完成：{Theme.StatusText(after.Status)}");
            DiagnosticLog.Info("analysis.one_done", $"generation={ticket.Generation} seam={seamId} status={after.Status} auto_status={after.AutoStatus} manual={after.IsManual} revision={_project.Revision}");
            Diagnostics.MemorySnapshot.Log("analyze_one");
        }
        catch (OperationCanceledException)
        {
            DiagnosticLog.Info("analysis.one_cancelled", $"generation={ticket.Generation} seam={seamId}");
            Status("分析已取消");
        }
        catch (Exception ex)
        {
            DiagnosticLog.Error("analysis.one_failed", ex, $"generation={ticket.Generation} seam={seamId}");
            Status("重新分析失败：" + ex.Message);
        }
        _canvas.Invalidate();
    }

    /// <summary>进入"画布点选切分位置"模式；再点一次按钮或 Esc 取消（审查第三轮 #5）。</summary>
    private void StartSplitPick()
    {
        if (_canvas.SplitPickActive)
        {
            _canvas.CancelSplitPick();
            Status("已取消点选切分");
            return;
        }
        if (_project.Doc.Segments.Count == 0)
        {
            Status("还没有素材可切分");
            return;
        }
        _canvas.EnterSplitPick();
        Status("点选模式：在画布上单击一条横线的位置即可切分；Esc 或再点按钮取消");
    }

    private void OnCanvasSplitPicked(int canvasY)
    {
        // SplitSegmentAtCanvas 内部走 Mutate：Changed 事件会带动画布与面板刷新
        if (_project.SplitSegmentAtCanvas(canvasY))
            Status($"已在画布 Y={canvasY} 处切分（Ctrl+Z 可撤销）");
        else
            Status($"Y={canvasY} 切不动：不在任何图片内，或紧贴上下边缘");
    }

    private void Status(string text)
    {
        if (InvokeRequired) { BeginInvoke(() => Status(text)); return; }
        _statusInfo.Text = text;
        _statusInfo.ToolTipText = text;
    }

    private void SortBy(int mode)
    {
        var ordered = mode == 0
            ? _project.Doc.Segments.OrderBy(s => _project.Doc.FindSource(s.SourceId)?.FileName, StringComparer.OrdinalIgnoreCase).ToList()
            : _project.Doc.Segments.OrderBy(s => _project.Doc.FindSource(s.SourceId)?.FileModifiedUtcMs ?? 0).ToList();

        // 排序 + 拼缝重建必须在同一条历史里（ReorderSegments），
        // 否则一次排序要按两次 Ctrl+Z 才能退干净
        _project.ReorderSegments(ordered);
        Status(mode == 0 ? "已按文件名排序" : "已按拍摄时间排序");
    }

    /// <summary>
    /// 把文档里的模式抄回顶栏那枚分段控件。
    /// 这里走 <see cref="SegmentedControl.SelectedIndex"/> 而不是模拟点击：那个 setter 不发
    /// <c>SelectionChanged</c>，所以"用户在询问框里点了取消"这一支能原样退回旧档而不再切一次。
    /// 判据 <c>ui/分段控件与文档模式两向同源</c> 量的就是这条双向一致。
    /// </summary>
    private void UpdateModeButtons()
    {
        if (_modeSegment is null) return;
        _modeSegment.SelectedIndex = (int)_project.Doc.Mode;
    }

    /// <summary>
    /// 需求 §3.1：图片少于 2 张时"继续添加"，拼缝工具禁用。
    /// 只有 1 张图时"检查拼缝"这件事没有对象，留着按钮只会让人点了找不到反应。
    /// </summary>
    private void UpdateToolAvailability()
    {
        if (_toolButtons is null) return;
        int segments = _project.Doc.Segments.Count;
        _toolButtons[0].Enabled = segments >= 2;                       // 拼缝
        _toolButtons[0].ToolTipText = segments >= 2
            ? "检查与调整相邻两张图之间的位移：状态、原因、X/Y、确认或恢复自动"
            : "至少需要 2 张图片才有拼缝可检查——请先继续添加";
        for (int i = 1; i < _toolButtons.Length; i++)
            _toolButtons[i].Enabled = segments >= 1;                   // 切分/删段/裁边/清理
    }

    /// <summary>
    /// 每次展开都重新读一遍列表：用户可能在资源管理器里把项目挪走或删掉，
    /// 菜单必须反映磁盘上的现状，而不是启动那一刻的快照。
    /// </summary>
    private void RebuildRecentMenu()
    {
        _recentMenu.DropDownItems.Clear();
        var recent = RecentProjects.Load();
        if (recent.Count == 0)
        {
            _recentMenu.DropDownItems.Add(new ToolStripMenuItem("（还没有打开过的项目）") { Enabled = false });
        }
        else
        {
            foreach (string path in recent)
            {
                string captured = path;
                var item = new ToolStripMenuItem(Path.GetFileName(path)) { ToolTipText = path };
                item.Click += (_, _) => OpenPathSmart(captured);
                _recentMenu.DropDownItems.Add(item);
            }
        }
        _recentMenu.DropDownItems.Add(new ToolStripSeparator());
        _recentMenu.DropDownItems.Add("清空最近项目列表", null,
            (_, _) => { RecentProjects.Clear(); RebuildRecentMenu(); Status("已清空最近项目列表"); });
    }

    /// <summary>按扩展名决定走普通项目还是打包项目。</summary>
    private void OpenPathSmart(string path)
    {
        if (path.EndsWith(ProjectPackage.Extension, StringComparison.OrdinalIgnoreCase)) OpenPackageFrom(path);
        else OpenProjectFrom(path);
    }
}

/// <summary>布局上的小便利：从布局反查它对应的拼缝集合。</summary>
internal static class CanvasLayoutExtensions
{
    public static IReadOnlyList<Seam> DocSeams(this CanvasLayout layout) =>
        layout is null ? Array.Empty<Seam>() : _seamsOf(layout);

    private static IReadOnlyList<Seam> _seamsOf(CanvasLayout layout)
    {
        // PlacedSegment 已经带着 IncomingSeam，按顺序取出来即可，
        // 不必再回到 Project 拿一份可能已被改动的列表
        var list = new List<Seam>();
        foreach (PlacedSegment placed in layout.Placed)
            if (placed.IncomingSeam is { } seam) list.Add(seam);
        return list;
    }
}
