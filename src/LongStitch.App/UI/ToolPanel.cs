using LongStitch.Core;
using LongStitch.Core.Cleanup;
using LongStitch.Core.Geometry;
using LongStitch.Core.Imaging;
using LongStitch.Core.Model;
using LongStitch.Core.Layout;
using LongStitch.App.Diagnostics;
using LongStitch.App.Imaging;
using System.Windows.Forms.Layout;

namespace LongStitch.App.UI;

/// <summary>右侧面板当前显示哪套工具。</summary>
public enum ToolKind { Seam, Split, Delete, Crop, Cleanup }

/// <summary>
/// 右侧"当前工具"面板。
///
/// 刻意做成<b>一次只显示一套参数</b>：模拟 UI 需求 §3.3 要求
/// "切换到切分、删段、裁边或清理时，右栏变为对应参数，不让相互无关的参数堆在一屏"。
/// 所以这里在切工具时整块重建控件，而不是把五组控件都藏着掖着。
/// </summary>
public sealed class ToolPanel : UserControl, IStackScrollOffset
{
    private readonly Project _project;
    private readonly FileSourceResolver _resolver;
    private readonly CanvasComposer _composer;
    private ToolKind _tool = ToolKind.Seam;

    /// <summary>面板请求画布做点什么（重新分析、跳转、切分位置等）。</summary>
    public event Action<string>? CanvasCommand;

    /// <summary>拼缝参数被面板改动。</summary>
    public event Action? ParametersChanged;

    /// <summary>
    /// 面板要"说一句话"但不改动参数时走这里（例如长截图里输入的横向被锁掉）。
    /// 主窗口接到后显示在状态栏——不能让用户按下箭头或输入数字后一点反馈都没有。
    /// </summary>
    public event Action<string>? OffsetNotice;

    /// <summary>最近一次面板上的提示文字（供冒烟工装核对"锁横向确实出声了"）。</summary>
    public string? LastNoticeForTest { get; private set; }

    /// <summary>
    /// 把"面板向画布发命令"这一条通路按原样走一遍：主窗口的 <c>OnToolCommand</c> 就挂在这个事件上，
    /// 所以这一格触发的是产品自己的路由，不是照抄一份的假通路。
    ///
    /// 为什么需要它——窗体从没 <c>Show()</c> 过时，面板上的按钮在 WinForms 的 CanSelect 那一关就被挡下，
    /// <c>PerformClick</c> 是<b>空转</b>（方法一次都不进，本仓库实测过并已记进红账）。
    /// 所以"确认拼缝一次退出编辑"这件事拆成两段量：
    /// 真按钮那一下用<b>不挂窗体</b>的面板量（那时按钮可按），主窗口这一段路由用这一格量。
    /// 判据见 <c>canvas/确认拼缝一次退出编辑</c>。
    /// </summary>
    public void RaiseCanvasCommandForTest(string command) => CanvasCommand?.Invoke(command);

    /// <summary>最近一次构建时 X 输入框是否可编辑（长截图应为 false）。</summary>
    public bool SeamXRowEnabledForTest => _xRow?.Enabled ?? false;

    /// <summary>
    /// 最近一次构建时"移动方向"那一项显示的档位文字。
    ///
    /// 第十四轮末次把下拉框撤成了只读一行字（长截图里它恒为「仅上下」，是个禁用的摆设），
    /// 但判据 <c>ui/长截图面板锁横向：X 格禁用、档位显示仅上下、硬填会被拦且回话</c> 要量的
    /// 是<b>面板真的把生效约束显示出来了</b>，不是"这里有一个 ComboBox"。
    /// 所以读数从控件里搬到了这一格：它仍然只在 <see cref="Rebuild"/> 里被写，
    /// 面板不刷新它就停在旧值——判据那一头的形状一点没变。
    /// </summary>
    public string SeamConstraintTextForTest => _constraintText ?? "";

    private string? _shownSeamId;
    private IReadOnlyList<string> _lastReview = Array.Empty<string>();

    /// <summary>
    /// 本轮面板是<b>按哪个修订号</b>摆出来的。<see cref="RefreshDisplayedSeam"/> 用它判断
    /// "屏上这几格读数是不是已经过期"——没有这一格，要么每帧重建整块面板（卡顿），
    /// 要么像第十一轮之前那样一次都不重建（读数骗人）。
    /// </summary>
    private int _builtRevision = int.MinValue;
    private string? _builtSeamId;
    private ToolKind _builtTool;

    /// <summary>补丁高亮层当前是否开着（面板上的按钮文字据此显示）。</summary>
    private bool _showPatches;

    /// <summary>
    /// 当前拼缝面板里那枚受"锁横向"影响的输入格。
    /// 面板一次只显示一套参数、切工具就整块重建，所以这个引用只在本轮构建里有效，
    /// <see cref="Rebuild"/> 开头会清空。留着它只为一件事：让冒烟工装能真的量到
    /// "X 那一格被禁用了"，而不是只看我写的注释。
    /// </summary>
    private NumericUpDown? _xRow;

    /// <summary>本轮构建显示出来的"移动方向"生效档位文字（没建这一页时为 null）。</summary>
    private string? _constraintText;

    /// <summary>本轮构建出来的两个「重叠对比画法」勾选框（面板整块重建，所以引用只在本轮有效）。</summary>
    private CheckBox? _ghostCheck;
    private CheckBox? _diffCheck;

    /// <summary>
    /// 这两个勾选框<b>应该</b>显示成什么。它是跨重建留着的一份状态：
    /// 画布上方那两枚按钮改的是画布的开关，面板下一轮重建若不记得，就会退回写死的默认值，
    /// 于是出现用户报的"按钮说没开、画布上还在画，得点两下才对得上"。
    /// 起点与画布自己的默认一致（重影开、差异关）。
    /// </summary>
    private bool _ghostWanted = true;
    private bool _diffWanted;

    /// <summary>
    /// 重叠透明度滑杆跨重建留的档位（第五十三改）。画布上的 <c>OverlapOpacityPercent</c>
    /// 只有一条写入路（面板发的 <c>opacity:N</c> 命令），面板自己记住上次发出的值就够了；
    /// 不记的话每次 Rebuild 滑杆都会弹回写死的 50，用户刚调出来的档位被悄悄冲掉。
    /// </summary>
    private int _opacityWanted = 50;

    /// <summary>
    /// 最近一次建出的透明度滑杆与数字框的引用（第五十三改，仅供冒烟工装读取）。
    /// Build* 系列每次重建都会 new 出新的实例，工装要在重建后拿到「当前正在屏上那一枚」，
    /// 所以这里跟着重建刷新，而不是只在构造器里存一次。
    /// </summary>
    private ChromeSlider? _opacitySlider;
    private ChromeNumericBox? _lastIntRowForTest;

    /// <summary>
    /// 仅供冒烟工装：透明度滑杆（按 <see cref="Control"/> 出手，ChromeSlider 是 internal 类型）。
    /// 判据 <c>ui/透明度滑杆可获焦点且方向键改值并带百分比读数</c> 用它验「点滑杆之后焦点真落下、
    /// 方向键改得动值」——旧实现缺 Selectable，Focus() 空转，方向键全死。
    /// </summary>
    public Control? OpacitySliderForTest => _opacitySlider;

    /// <summary>仅供冒烟工装：滑杆当前值（百分比读数要与它一字不差）。</summary>
    public int OpacitySliderValueForTest => _opacitySlider?.Value ?? -1;

    /// <summary>仅供冒烟工装：滑杆下限（Home 键应到头）。</summary>
    public int OpacitySliderMinimumForTest => _opacitySlider?.Minimum ?? -1;

    /// <summary>仅供冒烟工装：滑杆上限（End 键应到头）。</summary>
    public int OpacitySliderMaximumForTest => _opacitySlider?.Maximum ?? -1;

    /// <summary>仅供冒烟工装：向滑杆注入一枚按键（与真实键盘事件同一条 OnKeyDown 通路）。</summary>
    public void OpacitySliderPressKeyForTest(Keys key) => _opacitySlider?.PressKeyForTest(key);

    /// <summary>仅供冒烟工装：滑杆的 IsInputKey 是否认领该键（不认领会被容器吞掉）。</summary>
    public bool OpacitySliderAcceptsKeyForTest(Keys key) => _opacitySlider?.AcceptsKeyForTest(key) ?? false;

    /// <summary>
    /// 仅供冒烟工装：数字框右侧两枚 chevron 的点序（产品绘制用的同一份算式）。
    /// 判据 <c>guard/数字框下箭头点序必须朝下</c> 拿它断言下枚读作 v——点序写反时两枚同形，
    /// 就是用户看到的「下箭头被标注成了上箭头」。
    /// </summary>
    public (Point[] Up, Point[] Down) NumericSpinGlyphsForTest =>
        _lastIntRowForTest?.SpinGlyphsForTest
        ?? throw new InvalidOperationException("面板当前没有数字框（夹具失效）");

    /// <summary>仅供冒烟工装：数字框自绘点击带的宽度（字形与点击区要同一条竖带）。</summary>
    public int NumericSpinStripForTest => ChromeNumericBox.SpinStrip;

    /// <summary>仅供冒烟工装：当前数字框本体（按基类出手，内部类型不进签名）。</summary>
    public NumericUpDown? NumericBoxForTest => _lastIntRowForTest;

    /// <summary>
    /// 回填勾选框时压掉 <c>CheckedChanged</c>。程序设 <see cref="CheckBox.Checked"/> 与用户点它
    /// 走的是同一个事件，不拦就会"外部改状态 → 面板回填 → 面板又发一条命令回主窗口"绕成回路。
    /// 判据见 <c>guard/面板回填不许把画法命令再发回主窗口</c>。
    /// </summary>
    private bool _syncingOverlapChecks;

    /// <summary>面板自己发出的画布命令条数（只有那两个画法勾选框计数，判据用它数回路）。</summary>
    public int OverlapCommandCountForTest { get; private set; }

    /// <summary>供冒烟工装：面板那两个画法勾选框此刻的勾选态；null 表示本轮没显示拼缝那一页。</summary>
    public (bool? Ghost, bool? Difference) OverlapChecksForTest => (_ghostCheck?.Checked, _diffCheck?.Checked);

    /// <summary>
    /// 供冒烟工装：模拟用户点面板上的「重影对比」/「差异标红」勾选框。
    /// <see cref="CheckBox"/> 没有 <c>PerformClick</c>，改 <see cref="CheckBox.Checked"/> 就是
    /// 用户点下去的同一条通路（同样触发 <c>CheckedChanged</c>），与清理面板那格用的是同一个办法。
    /// </summary>
    public void ClickPanelGhostForTest()
    {
        CheckBox box = _ghostCheck!;
        box.Checked = !box.Checked;
    }

    /// <summary>供冒烟工装：同上，点差异那一枚。</summary>
    public void ClickPanelDiffForTest()
    {
        CheckBox box = _diffCheck!;
        box.Checked = !box.Checked;
    }

    /// <summary>
    /// 主窗口把「重叠对比画法」改完之后，用它把面板那两个勾选框同步成同一件事。
    /// 本轮没显示拼缝页时只记状态，重建那一页按这个状态起（见 <see cref="_ghostWanted"/>）。
    /// </summary>
    public void SyncOverlapChecks(bool ghost, bool difference)
    {
        _ghostWanted = ghost;
        _diffWanted = difference;
        if (_ghostCheck is null || _diffCheck is null) return;
        _syncingOverlapChecks = true;
        try
        {
            _ghostCheck.Checked = ghost;
            _diffCheck.Checked = difference;
        }
        finally { _syncingOverlapChecks = false; }
    }

    /// <summary>
    /// 本轮构建显示出来的蓝线行号（带内第几行）。
    /// 原来它是「蓝线」那个数值框里的数；数值框撤了之后改由只读那一行字承担，
    /// 但判据要读的还是同一个数——它证明"画布上拖完，面板按修订号刷新了，读数跟着走"。
    /// </summary>
    private int? _cutRowValue;

    /// <summary>屏上当前显示的蓝线行号（面板没这一格时为 -1）。</summary>
    public int CutRowValueForTest => _cutRowValue ?? -1;

    public ToolPanel(Project project, FileSourceResolver resolver, CanvasComposer composer)
    {
        _project = project;
        _resolver = resolver;
        _composer = composer;
        BackColor = Theme.PanelBackground;
        Padding = new Padding(Chrome.PadPanel, 10, Chrome.PadPanel, 12);
        // ═══ 第五十四改⑥⑦：滚动改道（用户图6白条 / 图7抽搐卡死）═══
        // 旧版 AutoScroll + 原生滚动条有两个治不好的病：黑夜档下原生条是白底旧组件（禁 P/Invoke
        // 藏不掉），且 AutoScrollMinSize 与滚动条显隐构成反馈环——最大化后滚到最底会整块抽搐卡死。
        // 现在改手动滚动：行位置由 StackedDockLayout 按 StackScrollOffset 直接摆，
        // 这枚自绘的「蓝色圆弧粗线条」叠在右留白里，条的显隐不再改变客户区，自激不可能再发生。
        _scrollbar = new ChromeScrollBar();
        _scrollbar.ValueChanged += () => PerformLayout();
        Controls.Add(_scrollbar);
        Rebuild();
    }

    private readonly ChromeScrollBar _scrollbar;

    // ───────────────────────── 手动滚动（第五十四改⑥⑦）─────────────────────────

    /// <summary>当前滚动偏移（布局引擎从这格取「第一行已滚出视口多少像素」）。
    /// 构造期 set_Padding 会早于 _scrollbar 赋值触发布局，那时还没有滚动可言，回 0。</summary>
    public int StackScrollOffset => _scrollbar?.Value ?? 0;

    /// <summary>内容总高：Padding 上下 + Σ(各行 Margin 与行高)。与 StackedDockLayout 的逐行累加同一段账，
    /// 自绘滚动条的最大值由它决定。只数可见行、不数滚动条自己。</summary>
    private int MeasureExtent()
    {
        int stack = 0;
        for (int i = Controls.Count - 1; i >= 0; i--)
        {
            Control child = Controls[i];
            if (!child.Visible || child is ChromeScrollBar) continue;
            stack += child.Margin.Top + child.Height + child.Margin.Bottom;
        }
        return Padding.Top + Padding.Bottom + stack;
    }

    /// <summary>
    /// 自绘滚动条的摆位：整根叠在<b>右留白</b>（Padding.Right = 20px）里，
    /// 内容行的右缘（ClientSize.Width − Padding.Right）永远不会被它压住。
    /// 每次布局末尾按现量重摆；内容总高喂给 Maximum，视口高喂给 LargeChange——
    /// 溢出时滑块出现，装得下时它什么都不画。
    /// </summary>
    private void PlaceScrollbar()
    {
        // 构造期 set_Padding 触发的第一趟布局早于 _scrollbar 赋值，那时还没有可摆的东西。
        if (_scrollbar is null) return;
        _extent = MeasureExtent();
        _scrollbar.SetBounds(Math.Max(0, ClientSize.Width - ChromeScrollBarWidth - 6), 6,
            ChromeScrollBarWidth, Math.Max(0, ClientSize.Height - 12));
        _scrollbar.Maximum = _extent;
        _scrollbar.LargeChange = Math.Max(1, ClientSize.Height);
    }

    /// <summary>自绘滚动条占的横向宽（滑块最宽 9px + 两侧呼吸）。</summary>
    private const int ChromeScrollBarWidth = 10;

    private int _extent;

    /// <summary>布局收口：行摆完 → 量总高 → 摆滚动条。总高变小把滚动值夹回合法区间时，
    /// <see cref="ChromeScrollBar.ValueChanged"/> 会再触发一次 PerformLayout；
    /// 夹取是幂等的，第二次进来值不再变，自然停住。</summary>
    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        PlaceScrollbar();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        int notches = Math.Max(1, Math.Abs(e.Delta) / 120);
        _scrollbar.Value += (e.Delta > 0 ? -1 : 1) * ChromeScrollBar.WheelStep * notches;
    }

    /// <summary>面板自己有焦点时键盘也能滚（Up/Down 一格、PgUp/PgDn 一屏、Home/End 到头）。</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        int viewport = Math.Max(ChromeScrollBar.WheelStep, ClientSize.Height - 24);
        int step = e.KeyCode switch
        {
            Keys.Up => -ChromeScrollBar.WheelStep,
            Keys.Down => ChromeScrollBar.WheelStep,
            Keys.PageUp => -viewport,
            Keys.PageDown => viewport,
            Keys.Home => int.MinValue,
            Keys.End => int.MaxValue,
            _ => 0,
        };
        if (step == 0) return;
        _scrollbar.Value = step == int.MinValue ? 0 : step == int.MaxValue ? int.MaxValue : _scrollbar.Value + step;
        e.Handled = true;
    }

    /// <summary>焦点落进某一行时把它滚到看得见的地方（键盘 Tab 导航与程序聚焦共用）。</summary>
    private void EnsureVisible(Control? child)
    {
        if (child is null || !child.Visible || child is ChromeScrollBar) return;
        int viewTop = Padding.Top, viewBottom = ClientSize.Height - Padding.Bottom;
        if (child.Top < viewTop)
            _scrollbar.Value -= viewTop - child.Top;
        else if (child.Bottom > viewBottom)
            _scrollbar.Value += child.Bottom - viewBottom;
    }

    private void OnChildGotFocus(object? sender, EventArgs e) => EnsureVisible(sender as Control);

    // ── 供冒烟工装量滚动行为（判据 ui/参数栏手动滚动… 那一族）──────────
    /// <summary>当前滚动偏移。判据用它钉「滚到底后重复布局不许动」。</summary>
    public int ScrollValueForTest => _scrollbar.Value;

    /// <summary>内容总高（与滚动条 Maximum 同值）。</summary>
    public int ContentExtentForTest => _extent;

    /// <summary>滑块此刻是否可见（内容装不下 ⇒ true）。原生 VerticalScroll 那一族已随 AutoScroll 退役。</summary>
    public bool ScrollbarVisibleForTest => _scrollbar.ThumbVisible;

    /// <summary>自绘滚动条本体（按基类出手，内部类型不进签名）。</summary>
    public Control ScrollbarForTest => _scrollbar;

    /// <summary>把滚动值设到指定位置（越界会被夹回，判据用越界值反证夹取存在）。</summary>
    public void ScrollToForTest(int value) => _scrollbar.Value = value;

    /// <summary>显式把一枚控件滚到看得见的地方（旧 ScrollControlIntoView 的手动滚动等价出口）。</summary>
    public void EnsureVisibleForTest(Control child) => EnsureVisible(child);

    private readonly StackedDockLayout _stackedLayout = new();

    /// <summary>
    /// 第五十三改：换上吃 Margin 的竖排停靠引擎。DefaultLayout 对 Dock=Top 不消费 Margin
    /// （判据 <c>ui/拼缝面板相邻控件留有呼吸间距</c> 逐对量出 13 对间隙全是 0px），
    /// 本面板各工厂早就写好的 Margin 一直是死字面量——用户看到的「按钮没有间隙」就是它。
    /// 换引擎后所有 Margin 原样生效，Build* 系列与 Rebuild 的翻转逻辑一行不改。
    /// </summary>
    public override LayoutEngine LayoutEngine => _stackedLayout;

    public ToolKind Tool
    {
        get => _tool;
        set { if (_tool == value) return; _tool = value; Rebuild(); }
    }

    /// <summary>当前要检查的拼缝；主窗口在画布选中拼缝时同步过来。</summary>
    public string? SeamId
    {
        get => _shownSeamId;
        set
        {
            if (_shownSeamId == value && _tool == ToolKind.Seam) return;
            _shownSeamId = value;
            if (_tool == ToolKind.Seam) Rebuild();
        }
    }

    /// <summary>
    /// 项目改动之后，把面板上那几格读数按<b>当前参数</b>重摆一遍（第十一轮的缺陷修复）。
    ///
    /// ═══ 为什么必须有这一条 ═══
    /// <see cref="SeamId"/> 的 setter 在 Id 没变时直接早退（同一个 Id 不该反复整块重建），
    /// 于是画布上拖完一趟切点、参数已经变了，面板却还停在"刚选中那一刻"的那个数。
    /// 现场就是用户报的那句"点击红框后无法上下调整拼缝"：
    /// 面板写着切点 300，实际参数已经是 0，再往上一拖立刻顶到带顶、一句"被夹掉 429 行"，
    /// 看起来就是"这块根本拖不动"。读数不跟着参数走，界面就在骗人。
    ///
    /// 两条护栏：
    /// ① 只在<b>真的过期</b>时重建（按修订号判，见 <see cref="_builtRevision"/>），
    ///    所以主窗口每次改动都调它也不会变成每帧重建整块面板；
    /// ② 焦点还在面板里的控件上时<b>不重建</b>——否则用户刚敲到一半的数字会被顶掉。
    /// </summary>
    public void RefreshDisplayedSeam()
    {
        if (_tool != ToolKind.Seam) return;
        if (_builtRevision == _project.Revision && _builtSeamId == _shownSeamId && _builtTool == _tool) return;
        if (ContainsFocus) return;
        Rebuild();
    }

    private void Rebuild()
    {
        SuspendLayout();
        // 面板整块重建：旧控件马上要 Dispose，留下的引用会让工装读到已销毁控件的状态
        _xRow = null;
        _constraintText = null;
        _cutRowValue = null;
        // 画法那两个勾选框同理要清空：留着旧引用，SyncOverlapChecks 就会去动一个已经 Dispose 的控件
        _ghostCheck = null;
        _diffCheck = null;
        // ControlCollection 不是泛型集合，没有 ToArray；先 OfType 再 ToList 才能安全边遍历边移除。
        // 自绘滚动条（第五十四改⑥⑦）不是一行内容，跨重建存活，不许被 Dispose。
        foreach (Control old in Controls.OfType<Control>().Where(c => !ReferenceEquals(c, _scrollbar)).ToList())
        {
            Controls.Remove(old);
            old.Dispose();
        }

        switch (_tool)
        {
            case ToolKind.Seam: BuildSeamTools(); break;
            case ToolKind.Split: BuildSplitTools(); break;
            case ToolKind.Delete: BuildDeleteTools(); break;
            case ToolKind.Crop: BuildCropTools(); break;
            case ToolKind.Cleanup: BuildCleanupTools(); break;
        }
        // ═══ 阅读顺序翻转（第五十一改回修）═══
        // Dock=Top 的停靠规则是「后加入的占外侧」——MainForm 与 SourcePanel 都是照着这条规则
        // 反着写加入顺序的（见 MainForm seamBox 处的注释）。但本面板的 Build* 系列是按
        // **阅读顺序**加的：AddSection 在前、AddButton 在后，于是整页渲染成了倒序——
        // 「当前工具」标题掉到说明文字下面、「重叠透明度」标签掉到滑杆下面（用户图1右栏可见）。
        // 这里在建成后把子控件下标整体翻过来，Build* 继续保持自然的阅读顺序，
        // 停靠规则也只用 MainForm 里写明的那一条，两处不必再互相提防。
        var built = Controls.OfType<Control>().Where(c => !ReferenceEquals(c, _scrollbar)).Reverse().ToList();
        for (int i = 0; i < built.Count; i++)
            Controls.SetChildIndex(built[i], i);
        // 滚动条永远压在内容之上（z 序最前）；它不参与叠放，摆位在 OnLayout 的收口里做。
        Controls.SetChildIndex(_scrollbar, 0);
        // 焦点落进任何一行时把它滚到看得见的地方（旧 AutoScroll 的 ScrollToControl 由这一条接替）。
        foreach (Control child in built)
            child.GotFocus += OnChildGotFocus;
        // 记下"这一屏读数是按哪一版参数摆的"，RefreshDisplayedSeam 才有东西可判过期
        _builtRevision = _project.Revision;
        _builtSeamId = _shownSeamId;
        _builtTool = _tool;
        ResumeLayout(true);
    }

    // ───────────────────────── 拼缝检查 ─────────────────────────

    private void BuildSeamTools()
    {
        Seam? seam = _shownSeamId is null ? null : _project.Doc.Seams.FirstOrDefault(s => s.Id == _shownSeamId);

        if (seam is null)
        {
            AddSection("检查与调整");
            AddNote("选择左侧拼缝或画布上的接缝，在这里查看结果并微调。");
            AddSection("三个步骤，完成拼接");
            AddLabel("01   导入截图", Theme.UiFontBold);
            AddNote("按阅读顺序添加，自动识别重叠。");
            AddLabel("02   检查接缝", Theme.UiFontBold);
            AddNote("检查提醒项，微调并确认接缝。");
            AddLabel("03   导出长图", Theme.UiFontBold);
            AddNote("点击右上角导出，保留原始像素。");
            return;
        }

        int ordinal = _project.Doc.Seams.IndexOf(seam) + 1;
        int total = _project.Doc.Seams.Count;
        PlacedSegment? after = _project.Layout.Find(seam.AfterId);
        // 生效位移只有一个出处：布局。面板不再自己用 before.Rect.Height 复述紧接量，
        // 否则"屏上显示的 Y"与"画布实际摆的 Y"早晚分叉。
        IntPoint current = _project.Layout.EffectiveOffset(seam);
        bool lockHorizontal = StitchAxes.LocksHorizontal(_project.Doc.Mode);
        SeamGeometry? geometry = _project.Layout.DescribeSeam(seam);
        bool cropPlacement = _project.Layout.UsesCropPlacement;

        // ═══ 第五十二改（用户图右下 1/2/3 三个框）：三组操作置顶，顺序固定 ═══
        // 用户要把「能动手的」放在右栏最上面，不用滚到底才够得着：
        //   第一位 = 动作按钮组（重新分析/恢复自动/取消/确认/跳到）——原来沉在整页最底，
        //            窄窗口下「跳到这条拼缝」直接被裁掉（用户图绿框 1 的下缘）；
        //   第二位 = 重影对比/差异标红/重叠透明度/自动建议候选（用户图黄框 2）；
        //   第三位 = 位移 X/Y（用户图蓝框 3）。
        // 只读的状态与账目沉到下面「状态与账目」一节：它们是"看完再动手"的参考，不该挡住操作。
        // 顺序由本方法的加入顺序决定（Rebuild 末尾统一翻转成阅读顺序），判据见
        // <c>ui/拼缝面板三组置顶顺序为按钮重影位移</c>。

        // ── 第一位：动作按钮 ────────────────────────────────
        AddButton("重新分析这条", () => CanvasCommand?.Invoke($"analyze:{seam.Id}"));
        var reset = AddButton("恢复自动", () =>
        {
            // 必须走 Mutate：进撤销栈、置 IsDirty（Touch 两者都不做，
            // 直接改对象会让"确认后保存"漏掉这次改变、Ctrl+Z 退不回去）
            bool changed = false;
            _project.Mutate("恢复自动", _ => changed = seam.ResetToAuto());
            if (changed) ParametersChanged?.Invoke();
            Rebuild();
        }, ChromeButtonKind.Ghost);
        reset.Enabled = seam.HasAutoSuggestion;
        if (!seam.HasAutoSuggestion)
            AddNote("没有可用的自动建议（重叠太短或多解），只能手动确定。");

        AddButton("取消本次调整", () =>
        {
            _project.Mutate("取消本次调整", _ =>
            {
                seam.ManualOffset = null;
                seam.Status = seam.AutoStatus;
                seam.Cause = SeamCause.None;
            });
            ParametersChanged?.Invoke();
            Rebuild();
        }, ChromeButtonKind.Ghost);
        var confirm = AddButton("确认拼缝", () =>
        {
            _project.Mutate("确认拼缝", _ => seam.Confirm());
            // 第四十六改：确认就是"这条缝我看过了、没问题"——用户把它当一件事，
            // 界面就不该再要他回画布点一次蓝线上的 ✓ 才退出编辑（与 ✓／Esc 同一口径：退档＋取消选中）。
            CanvasCommand?.Invoke("exit-edit");
            ParametersChanged?.Invoke();
            Rebuild();
        }, ChromeButtonKind.Primary);
        confirm.Enabled = !seam.Confirmed || seam.IsManual;

        if (after is not null)
            AddButton("跳到这条拼缝", () => CanvasCommand?.Invoke($"goto:{seam.Id}"));

        // ── 第二位：重叠预览画法 + 自动建议候选 ────────────────
        // 重叠预览的两种画法（需求 §3.4"透明度滑块、差异显示"）：互斥，与画布上方那两个按钮同步。
        // 初始值**不再写死** true/false：面板一重建（换缝、改参数、切工具都会）写死的值就把用户
        // 已经改出来的画法冲回默认，屏上于是出现"按钮说没开、画布还在画"。
        // 现在这两个勾选框按上一次同步的真实状态起，并且由主窗口那条唯一写入出口（SetOverlapView）回填。
        AddSection("重叠预览");
        _ghostCheck = AddCheck("重影对比（半透明叠另一侧）", _ghostWanted,
            on => OnOverlapCheckChanged("overlay", on));
        _diffCheck = AddCheck("差异标红（只画不一致处）", _diffWanted,
            on => OnOverlapCheckChanged("diff", on));

        // 第五十三改（用户图绿框）：① 标签上方多让 8px（原来与「差异标红」贴死，"太挤了"）；
        // ② 标签里带实时百分比读数——滑杆改一次这里跟一次，用户不用再猜 50 到底是多透。
        // ③ 起点不再写死 50：面板会整块重建（换缝、改参数都会），写死的起点会把用户已调出的
        //    透明度悄悄冲回 50——与画法那两个勾选框同一族缺陷，同一份跨重建状态（_opacityWanted）。
        // ④ 滑杆自身补了 Selectable（见 ChromeSlider 构造器注释），点它之后左右方向键可用。
        Label opacityLabel = AddLabel($"重叠透明度　{_opacityWanted}%", Theme.UiFontSmall, topMargin: 8);
        var opacity = new ChromeSlider { Minimum = 0, Maximum = 100, Value = _opacityWanted, Dock = DockStyle.Top, Height = 28, Margin = new Padding(0, 3, 0, 6) };
        _opacitySlider = opacity;
        opacity.ValueChanged += (_, _) =>
        {
            opacityLabel.Text = $"重叠透明度　{opacity.Value}%";
            _opacityWanted = opacity.Value;
            CanvasCommand?.Invoke($"opacity:{opacity.Value}");
        };
        Controls.Add(opacity);

        AddLabel("自动建议的候选", Theme.UiFontSmall);
        if (seam.Candidates.Count == 0)
            AddNote(seam.Analyzed ? "本次分析没有留下候选。" : "尚未分析。点「重新分析这条」试试。");
        else
            foreach (var c in seam.Candidates.Take(4))
                AddNote($"{(c.IsChosen ? "▶ " : "   ")}重叠 {c.OverlapLength}px" +
                        $"  强度 {c.Score:P1}  偏移 {c.Offset.X},{c.Offset.Y}");

        // ── 第三位：位移 X/Y ─────────────────────────────────
        // X/Y 数值：单位永远是源图像素，与预览缩放无关（需求 §5.2）。
        // 回调执行时必须**现取**当前偏移，不能捕获建面板时的 current——
        // 否则先改 X 再改 Y 时，Y 的回调还拿着旧 X，会把刚改的 X 顶回去。
        AddSection("位置微调");
        AddLabel("位移（源图像素）", Theme.UiFontSmall);
        _xRow = AddIntRow("X", current.X, value =>
            ApplyOffset(seam, new IntPoint(value, CurrentOffset(seam).Y)));
        AddIntRow("Y", current.Y, value =>
            ApplyOffset(seam, new IntPoint(CurrentOffset(seam).X, value)));
        if (lockHorizontal)
        {
            // 禁用而不是藏起来：让用户看得见"这一格存在，但本模式不允许"，
            // 切到竖拼/横拼之后它自己会重新可用。
            _xRow.Enabled = false;
            AddNote("长截图模式已锁定横向：X 恒为 0。纵向滚动的连拍不该有左右位移，" +
                    "一旦允许左右挪，重叠区里那份内容就会错开几列、在成品里重复出现两次。");
        }

        // ═══ 「固定侧」与「移动方向」两个下拉撤掉了（第十四轮末次）═══
        // 「固定侧」问的是"拖动时哪一张钉在屏幕上不动"——现在这件事由手势自己回答：
        // 你抓住哪一张，另一张就是钉住的那一张。让用户先在面板里选一遍再去画布上拖，
        // 等于要求他在动手之前先想清楚一个屏上看不见的概念，这正是"过于复杂"的来源。
        // 参数里的 seam.Fixed 并没有消失：滑动手势提交时 Core 会把结论写回去，
        // 竖拼／横拼的位移档仍然读它，F8 读数与日志里的 fixedSide= 也仍然说的是真话。
        // 「移动方向」在长截图里恒为「仅上下」（被 StitchAxes.Enforce 强制），下拉框是禁用的摆设；
        // 竖拼／横拼里它也只有一档有意义。改成只读一行字，判据读的是同一个生效值。
        _constraintText = ConstraintText(StitchAxes.Enforce(_project.Doc.Mode, seam.Constraint));
        AddLabel($"屏上钉住的是：{(seam.Fixed == FixedSide.Before ? "上方那张（你拖的是下方那张）" : "下方那张（你拖的是上方那张）")}" +
                 $" · 移动方向：{_constraintText}", Theme.UiFontSmall, Theme.TextSecondary);
        if (lockHorizontal)
            AddNote("长截图只允许上下移动：X 恒为 0，横向输入会被拦下并出声。" +
                    "纵向滚动的连拍一旦允许左右挪，重叠区里那份内容就会错开几列、在成品里重复出现两次。");

        // ── 沉底：只读的状态与账目（看完再动手的参考，不挡操作）──
        AddSection("状态与账目");
        AddLabel($"第 {ordinal}/{total} 条拼缝", Theme.UiFontBold);
        AddLabel(Theme.StatusText(seam.Status), Theme.UiFontBold, Theme.StatusColor(seam.Status));

        if (!string.IsNullOrWhiteSpace(seam.CauseText))
            AddLabel("原因：" + seam.CauseText, Theme.UiFontSmall, Theme.TextSecondary);

        AddLabel($"重叠 {seam.OverlapLength}px · 强度 {seam.Confidence:P1} · 来源 {(seam.IsManual ? "手动" : "自动")}",
            Theme.UiFontSmall, Theme.TextSecondary);

        // 去重账：把"到底吃掉了几行、总高应该是多少"直接摆在面板上。
        // 用户怀疑"重叠是不是只是被盖住没真去掉"时，这几个数就是回答。
        if (geometry is not null)
            AddLabel($"去重账（主轴像素）：{geometry.BeforeExtent} + {geometry.AfterExtent} − " +
                     $"{geometry.Overlap} = {geometry.PairExtent}" +
                     (geometry.OverlapMatchesReport ? "" : " ！与分析记录的重叠不一致"),
                Theme.UiFontSmall,
                geometry.OverlapMatchesReport ? Theme.TextSecondary : Theme.Warning);

        // ═══ 裁切账（第九轮：长截图才有）═══
        // 用户问"重叠到底去掉没有"，回答必须是三个整数与两个源矩形，不是"看起来对上了"。
        if (cropPlacement && geometry is not null)
        {
            // 第十二轮：面板说的是"公共坐标里的那四个量"，不再是两个凭空的裁切数。
            AddLabel($"公共坐标：Rect1 {geometry.Rect1} · Rect2 {geometry.Rect2} · " +
                     $"重叠带 Rect1∩Rect2 = {geometry.OverlapRect}（{geometry.OverlapStartY}→{geometry.OverlapEndY}）",
                Theme.UiFontSmall, Theme.TextSecondary);
            AddLabel($"蓝线 CutY = 带内第 {geometry.CutPosition} 行（画布第 {geometry.CutYCanvas} 行）· " +
                     $"来源 {CropModeText(geometry.CropMode)} · 对齐位移 ({geometry.AlignmentOffset.X},{geometry.AlignmentOffset.Y})",
                Theme.UiFontSmall, Theme.TextSecondary);
            AddLabel($"蓝线两侧：上方 {geometry.AppliedAfterCrop} 行取自图1 · 下方 {geometry.AppliedBeforeCrop} 行取自图2" +
                     $"（合计=重叠 {geometry.Overlap}）",
                Theme.UiFontSmall, Theme.TextSecondary);
            AddLabel($"真正进像素的源矩形：sourceRect1 {geometry.BeforeSourceRect} / sourceRect2 {geometry.AfterSourceRect}" +
                     (geometry.RectsDisjoint ? " · 两段不再交叠" : " ！两段仍交叠（等于没切）"),
                Theme.UiFontSmall, geometry.RectsDisjoint ? Theme.TextSecondary : Theme.Warning);
            if (!geometry.CropsAppliedAsRequested)
                AddLabel($"！参数写的裁切是 ({geometry.RequestedBeforeCrop},{geometry.RequestedAfterCrop})，" +
                         $"超出可见长度，实际只切到 ({geometry.AppliedBeforeCrop},{geometry.AppliedAfterCrop})" +
                         "——重叠报得比图还高，请重新分析或改小",
                    Theme.UiFontSmall, Theme.Warning);
            if (!geometry.CutInsideBand)
                AddLabel("！蓝线不在重叠带里（被夹回带边）——带变短会把蓝线挤出来，这是正常的，但要说一声",
                    Theme.UiFontSmall, Theme.Warning);
        }

        if (cropPlacement)
        {
            // ═══ 第十四轮末次：这一页不再摆旋钮，只摆读数与一句怎么做 ═══
            // 原来这里有「对齐」「蓝线」两个数值框、「整带取图1／整带取图2／对半切」三个按钮，
            // 外加下面那个「固定侧」下拉——五样东西说的是同一件事的五个侧面，
            // 用户得先理解"重叠 O 与带内行号 Cut 是两个各自独立的量"才敢下手，
            // 而这两个量在屏幕上根本看不出来。
            // 现在画布上只有一档手势：抓住蓝线上方或下方那张图上下滑动，蓝线钉在屏幕原地不动；
            // 精细调整走蓝线上那枚「调整」把手 + ↑/↓ 每次 1 个源像素。
            //
            // 蓝线的行号仍然<b>显示</b>在这里（只读）：判据
            // <c>ui/面板蓝线读数跟着画布拖动的结果走，不停在旧值</c> 读的就是它——
            // 那是"参数真的改了、面板真的按修订号刷新了"这一头的证据，不能跟着旋钮一起删掉。
            _cutRowValue = seam.CutPosition;
            AddNote("怎么调：在画布上抓住蓝线「上方」或「下方」那张图上下拖动——蓝线钉在屏幕原地不动。\n" +
                    "带薄到抓不住时：点蓝线上那枚上下箭头小按钮，再点要挪的那一张图，按 ↑/↓ 每次挪 1 个源像素（Shift ×10）。\n" +
                    "调完点那枚 ✓（或按一次 Esc）就退出编辑这条缝。下面这些数只是账，不用手填。");
            AddLabel($"重叠 {seam.OverlapLength} 行 · 蓝线在带内第 {seam.CutPosition} 行 · " +
                     $"上方 {seam.CutPosition} 行取自图1 · 下方 " +
                     $"{Math.Max(0, seam.OverlapLength - seam.CutPosition)} 行取自图2",
                Theme.UiFontSmall, Theme.TextSecondary);
            AddNote("关掉「重影对比」后，红框里就只剩一份内容；那层叠图与绿/黄框都是调试用的，永不进出品。");
        }
    }

    /// <summary>回调执行时现取的生效位移（含"紧贴"回退值与模式的轴规则），与画布、布局同一套算法。</summary>
    private IntPoint CurrentOffset(Seam seam) => _project.Layout.EffectiveOffset(seam);

    /// <summary>
    /// 数值框改位移。先按模式把不允许的轴折算掉，再决定要不要写参数——
    /// "输入 5 但被锁成 0"这种净效果为 0 的改动绝不能进撤销栈，
    /// 但它必须出声，否则用户会以为自己输入的 6 已经生效。
    /// </summary>
    private void ApplyOffset(Seam seam, IntPoint offset)
    {
        IntPoint allowed = StitchAxes.Enforce(_project.Doc.Mode, offset);
        if (_project.Layout.UsesCropPlacement)
        {
            // 长截图里 Y 框改的不是落位，而是"这条缝去掉多少行"（O = 紧接位移 − Y）。
            // 位移档才把 Y 直接当落位写进 ManualOffset——两种语义不许混用（需求五）。
            int tightY = _project.Layout.TightOffset(seam).Y;
            int wantedOverlap = Math.Max(0, tightY - allowed.Y);
            if (wantedOverlap != seam.OverlapLength)
            {
                _project.Mutate("输入拼缝偏移", _ => seam.ApplyOverlapFromOffset(allowed.Y, tightY));
                ParametersChanged?.Invoke();
            }
            if (offset.X != allowed.X) ReportHorizontalLock(seam, offset, allowed, "数值框");
            Rebuild();   // 无论有没有写进去，都要把框拉回真实生效值
            return;
        }

        if (allowed == CurrentOffset(seam))
        {
            if (offset.X != allowed.X) ReportHorizontalLock(seam, offset, allowed, "数值框");
            Rebuild();      // 把输入框拉回真实生效值，别停在一个"看起来生效了"的数字上
            return;
        }

        // 数值框与方向键走同一条路：改一次记一条历史，Ctrl+Z 能逐步退回
        ManualApply result = default;
        _project.Mutate("输入拼缝偏移", _ => result = seam.ApplyManual(offset, _project.Doc.Mode));
        if (result.HorizontalRejected) ReportHorizontalLock(seam, offset, allowed, "数值框");
        ParametersChanged?.Invoke();
        Rebuild();
    }

    /// <summary>
    /// 移动约束的人话说法（面板那行"移动方向：…"用它）。
    /// 文字与原来那个下拉框的三项<b>逐字相同</b>：判据
    /// <c>ui/长截图面板锁横向：X 格禁用、档位显示仅上下、硬填会被拦且回话</c> 比的就是"仅上下"这三个字，
    /// 换了说法等于把判据的锚点挪走——那不是搬家，那是让它再也找不到东西。
    /// </summary>
    private static string ConstraintText(MoveConstraint constraint) => constraint switch
    {
        MoveConstraint.Both => "双向",
        MoveConstraint.VerticalOnly => "仅上下",
        MoveConstraint.HorizontalOnly => "仅左右",
        _ => constraint.ToString(),
    };

    /// <summary>切点来源的人话说法（面板那行"切点来源 …"用它）。</summary>
    private static string CropModeText(SeamCropMode mode) => mode switch
    {
        SeamCropMode.Auto => "自动",
        SeamCropMode.KeepBefore => "整带留上方",
        SeamCropMode.KeepAfter => "整带留下方",
        SeamCropMode.SplitMiddle => "对半",
        SeamCropMode.Manual => "手动拖动",
        _ => mode.ToString(),
    };

    /// <summary>横向位移被模式锁掉：状态栏说一句 + 日志记一条（只记数字，不记像素）。</summary>
    private void ReportHorizontalLock(Seam seam, IntPoint wanted, IntPoint allowed, string source)
    {
        DiagnosticLog.Info("analysis.longscroll.constraint",
            $"seam={seam.Id} source={source} mode={_project.Doc.Mode} " +
            $"requested=({wanted.X},{wanted.Y}) allowed=({allowed.X},{allowed.Y}) dropped_x={wanted.X - allowed.X}");
        LastNoticeForTest =
            $"长截图模式已锁定左右方向：输入的 X={wanted.X} 按 0 处理（{source}的纵向输入按重叠/切点生效）";
        OffsetNotice?.Invoke(LastNoticeForTest);
    }

    // ───────────────────────── 切分 / 删段 ─────────────────────────

    private void BuildSplitTools()
    {
        AddSection("切分");
        AddNote("把一段从中间分成两段，之后可以单独移动、删除或导出。\n" +
                "切分只把「取哪一块源图」一分为二，不复制也不丢弃任何像素，因此无损且可撤销。");

        AddLabel("切分位置（画布 Y）", Theme.UiFontSmall);
        var y = AddIntRow("Y", 0, _ => { });

        AddButton("在画布上点选位置", () => CanvasCommand?.Invoke("split-pick"));
        AddButton("按输入值切分", () =>
        {
            if (!_project.SplitSegmentAtCanvas((int)y.Value))
                MessageBox.Show(this, "这个位置切不动：不在任何图片内，或紧贴上下边缘。",
                    "切分", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            ParametersChanged?.Invoke();
        });
    }

    private void BuildDeleteTools()
    {
        AddSection("删段");
        AddNote("删除选中的源图或切分后的片段。删除后相邻拼缝会重算，这一步可以撤销。");

        int index = 0;
        foreach (Segment seg in _project.Doc.Segments)
        {
            index++;
            SourceImage? src = _project.Doc.FindSource(seg.SourceId);
            string label = $"#{index} {src?.FileName ?? "(源文件丢失)"}";
            AddButton(label, () =>
            {
                string answer = MessageBox.Show(this,
                    $"确定删除 {label} 吗？\n原图文件不会被删除，只从本次拼合中移除。",
                    "删段", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes
                    ? "yes" : "no";
                if (answer != "yes") return;
                _project.RemoveSegment(seg.Id);   // 内部已在同一条历史里重建拼缝，别再补一次 EnsureSeams
                ParametersChanged?.Invoke();
                Rebuild();
            });
        }
        if (_project.Doc.Segments.Count == 0) AddNote("还没有素材。");
    }

    // ───────────────────────── 裁边 ─────────────────────────

    private void BuildCropTools()
    {
        AddSection("裁边");
        AddNote("默认不改原图，只记录「每段取源图的哪一块」。预览会立刻显示最终宽高。");

        var enabled = AddCheck("对所有图片启用统一裁边", _project.Doc.Crop.Enabled,
            on => _project.Mutate("裁边开关", _ => _project.Doc.Crop.Enabled = on));

        AddLabel("统一边距（左 上 右 下）", Theme.UiFontSmall);
        var left = AddIntRow("左", _project.Doc.Crop.Uniform.Left, v => SetCrop(v, null, null, null));
        var top = AddIntRow("上", _project.Doc.Crop.Uniform.Top, v => SetCrop(null, v, null, null));
        var right = AddIntRow("右", _project.Doc.Crop.Uniform.Right, v => SetCrop(null, null, v, null));
        var bottom = AddLabel2("下", _project.Doc.Crop.Uniform.Bottom, v => SetCrop(null, null, null, v));

        AddLabel($"结果尺寸：{_project.Layout.CanvasSize.Width}×{_project.Layout.CanvasSize.Height}",
            Theme.UiFontSmall, Theme.TextSecondary);

        int index = 0;
        foreach (Segment seg in _project.Doc.Segments)
        {
            index++;
            AddLabel($"#{index} 单张裁边", Theme.UiFontSmall);
            var t = AddIntRow("上", seg.EdgeCrop.Top, v =>
                _project.Mutate("单张裁边", _ => seg.EdgeCrop = seg.EdgeCrop with { Top = Math.Max(0, v) }));
            var b = AddIntRow("下", seg.EdgeCrop.Bottom, v =>
                _project.Mutate("单张裁边", _ => seg.EdgeCrop = seg.EdgeCrop with { Bottom = Math.Max(0, v) }));
            _ = left; _ = right; _ = bottom; _ = enabled; _ = t; _ = b;
        }
    }

    private void SetCrop(int? l, int? t, int? r, int? b)
    {
        Insets cur = _project.Doc.Crop.Uniform;
        var next = new Insets(
            Math.Max(0, l ?? cur.Left), Math.Max(0, t ?? cur.Top),
            Math.Max(0, r ?? cur.Right), Math.Max(0, b ?? cur.Bottom));
        _project.Mutate("统一裁边", _ => _project.Doc.Crop.Uniform = next);
    }

    // ───────────────────────── 清理 ─────────────────────────

    /// <summary>
    /// 清理面板。第十四轮末次把"重复状态栏 / 底部固定栏"那一族下线了（合并、替换、裁剪、留白、
    /// 时间替换与画布手动框选，作废记录见 README「第四十改」），这里只剩滚动条一项自动清理，
    /// 加上"逐张开关 + 前后对比 + 画布高亮"这套仍然成立的约定。
    /// </summary>
    private void BuildCleanupTools()
    {
        AddSection("清理");
        CleanupSettings s = _project.Doc.Cleanup;

        AddNote("清理会真的改动像素，所以每一项都能单独关掉、能看掩膜前后对比、能撤销。" +
                "置信度不足时只标「需检查」，不自动涂抹正文。");

        // ── 滚动条 ──────────────────────────────────────────
        AddCheck("清理滚动条", s.Scrollbar.Enabled,
            on => _project.Mutate("滚动条开关", _ => s.Scrollbar.Enabled = on));
        AddLabel($"最大条宽 {s.Scrollbar.MaxThickness}px · 置信度门限 {s.Scrollbar.MinConfidence:P0}" +
                 "（低于门限只标需检查）", Theme.UiFontSmall, Theme.TextSecondary);

        // ── 应用预览（而不是"提交隐藏修改"）─────────────────
        var apply = AddButton("检测并应用预览", () => RunCleanup(), ChromeButtonKind.Primary);
        apply.Enabled = _project.Doc.Segments.Count > 0;
        AddButton(_showPatches ? "隐藏补丁位置" : "在画布上标出补丁位置", () =>
        {
            _showPatches = !_showPatches;
            CanvasCommand?.Invoke(_showPatches ? "patch-overlay:1" : "patch-overlay:0");
            Rebuild();
        });

        if (_lastReview.Count > 0)
        {
            AddLabel("需检查", Theme.UiFontSmall, Theme.Warning);
            foreach (string line in _lastReview) AddNote("· " + line);
        }

        // ── 逐张补丁开关（这就是"前后对比"：关掉=处理前，打开=处理后）──
        AddLabel("逐张补丁（勾选=生效；取消即恢复该处原像素）", Theme.UiFontSmall);
        ShowPatchRows();

        AddLabel($"当前补丁：{PatchCount()} 个生效（滚动条 {PatchCount(PatchKind.ExtendColumn)}）",
            Theme.UiFontSmall, Theme.TextSecondary);
        AddButton("清除全部自动清理", () =>
        {
            _project.Mutate("清除清理结果", d =>
            {
                foreach (Segment seg in d.Segments)
                    seg.Patches.RemoveAll(p => p.Reason.StartsWith(CleanupPlanner.AutoReasonPrefix, StringComparison.Ordinal));
            });
            Rebuild();
        });

        if (_project.Doc.Segments.Count == 0) AddNote("还没有素材。");
    }

    /// <summary>逐行列出补丁：侧别/宽度等检测结论都在 Reason 里，勾选框就是逐张开关。</summary>
    private void ShowPatchRows()
    {
        int index = 0;
        foreach (Segment seg in _project.Doc.Segments)
        {
            index++;
            foreach (PixelPatch patch in seg.Patches)
            {
                string kind = patch.Kind switch
                {
                    PatchKind.ExtendColumn => "滚动条",
                    _ => "补丁",
                };
                string text = $"#{index} {kind}：{TrimReason(patch.Reason)}（置信度 {patch.Confidence:P0}）";
                PixelPatch capturedPatch = patch;
                AddCheck(text, patch.Enabled, on =>
                {
                    // 走 Mutate：关掉一个补丁是真的改了最终像素，必须进撤销栈、必须置脏
                    _project.Mutate("切换补丁", _ => capturedPatch.Enabled = on);
                    ParametersChanged?.Invoke();
                });
            }
        }
        if (_project.Doc.Segments.Sum(s => s.Patches.Count) == 0)
            AddNote("还没有补丁。点「检测并应用预览」由程序检测。");
    }

    private static string TrimReason(string reason)
    {
        // 自动清理写的前缀只有一枚，列表里只留"人要看的那半句"
        if (reason.StartsWith(CleanupPlanner.AutoReasonPrefix, StringComparison.Ordinal))
            return reason[CleanupPlanner.AutoReasonPrefix.Length..];
        return reason;
    }

    private CleanupReport? RunCleanup()
    {
        try
        {
            CleanupReport report = CleanupPlanner.Apply(_project, _resolver, _composer);
            _lastReview = report.NeedsReview;
            ParametersChanged?.Invoke();
            Rebuild();
            return report;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "清理失败：" + ex.Message + "\n\n原图与已有参数都没有被改动。",
                "清理", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return null;
        }
    }

    private int PatchCount(PatchKind? kind = null)
    {
        int n = 0;
        foreach (Segment seg in _project.Doc.Segments)
            foreach (PixelPatch p in seg.Patches)
                if (p.Enabled && (kind is null || p.Kind == kind)) n++;
        return n;
    }

    // ───────────────────────── 小控件工厂 ─────────────────────────

    private void AddSection(string text) => Controls.Add(new SectionHeader(text));

    /// <summary>
    /// 加一枚只读标签。第五十三改补了 <paramref name="topMargin"/>：标签在停靠布局里原来与
    /// 上一行贴死（DefaultLayout 不吃 Margin，见 <see cref="StackedDockLayout"/>），
    /// 引上一个小节的标签（重叠透明度／自动建议的候选／位移）上方要多让几像素，
    /// 否则用户看到的就是「文字上方没有空隙，太挤了」。
    /// 返回这枚 Label 供调用处做实时读数（如透明度百分比）。
    /// </summary>
    private Label AddLabel(string text, Font font, Color? color = null, int topMargin = 3)
    {
        var label = new Label
        {
            Text = text,
            Dock = DockStyle.Top,
            AutoSize = false,
            Height = font.Height + 8,
            Font = font,
            ForeColor = color ?? Theme.TextPrimary,
            BackColor = Theme.PanelBackground,
            Margin = new Padding(0, topMargin, 0, 3),
        };
        Controls.Add(label);
        return label;
    }

    /// <summary>
    /// 说明段落。高度由 <see cref="NoteLabel"/> 在停靠宽度定下来之后自己量，
    /// 不在这里估——构造那一刻面板还没停进右栏，拿默认宽度量出来的折行数会把段落撑成一片空腔。
    /// </summary>
    private void AddNote(string text) => Controls.Add(new NoteLabel(text));

    /// <summary>
    /// 面板那两个「重叠对比画法」勾选框的唯一出口。两件事必须都在这里做：
    /// ① <b>回填时不许发命令</b>（看 <see cref="_syncingOverlapChecks"/>）——主窗口改一次状态、
    ///    面板回敬一条命令，用户看到的就是"点一下没反应、点第二下才动"；
    /// ② <b>计数</b>——判据 <c>guard/面板回填不许把画法命令再发回主窗口</c> 数的就是这一个数，
    ///    它的正向对照（真点一次必须加一）走的也是这一个出口，所以这把尺不会焊在空转上。
    /// </summary>
    private void OnOverlapCheckChanged(string kind, bool on)
    {
        if (_syncingOverlapChecks) return;
        OverlapCommandCountForTest++;
        CanvasCommand?.Invoke($"{kind}:{(on ? 1 : 0)}");
    }

    private CheckBox AddCheck(string text, bool value, Action<bool> onChange)
    {
        // 第五十一改（用户图黄框）：系统勾选框换成自绘的 ChromeCheckBox——
        // 类型仍是 CheckBox，Checked/CheckedChanged 通路与判据的 OfType 全部照旧
        var box = new ChromeCheckBox
        {
            Text = text,
            Dock = DockStyle.Top,
            Height = Math.Max(30, Theme.UiFont.Height + 12),
            Checked = value,
            ForeColor = Theme.TextPrimary,
            BackColor = Theme.PanelBackground,
            Font = Theme.UiFont,
            Margin = new Padding(0, 3, 0, 3),
        };
        box.CheckedChanged += (_, _) => onChange(box.Checked);
        Controls.Add(box);
        return box;
    }

    private ComboBox AddCombo(string[] items, int selected, Action<int> onChange)
    {
        var combo = new ComboBox
        {
            Dock = DockStyle.Top,
            DropDownStyle = ComboBoxStyle.DropDownList,
            FlatStyle = FlatStyle.Flat,
            Height = Math.Max(30, Theme.UiFont.Height + 12),
            BackColor = Theme.WindowBackground,
            ForeColor = Theme.TextPrimary,
            Font = Theme.UiFont,
            Margin = new Padding(0, 3, 0, 8),
        };
        combo.Items.AddRange(items);
        combo.SelectedIndex = Math.Clamp(selected, 0, items.Length - 1);
        combo.SelectedIndexChanged += (_, _) => onChange(combo.SelectedIndex);
        Controls.Add(combo);
        return combo;
    }

    private NumericUpDown AddIntRow(string label, int value, Action<int> onChange)
    {
        // 第五十一改（用户图红框+黄框）：行高与上下边距放开（原来 34 高、边距 1，
        // 一屏十几行挤成一片）；数字框换成自绘 ChromeNumericBox——类型仍是 NumericUpDown
        // 第五十一改回修（用户图2绿框「还是不对」）：行高与框高原来写死 40/30——96 DPI 的字高。
        // 150% DPI 下 MonoFont 长到 ~24px，30px 的框装不下数字的下缘。现在从字体现量推导：
        // 96 DPI 上算出来与旧值同量级，150% DPI 上自动长高，数字永远整枚落得下。
        int boxH = Math.Max(30, Theme.MonoFont.Height + 10);
        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = boxH + 10,
            FlowDirection = FlowDirection.LeftToRight,
            BackColor = Theme.PanelBackground,
            Margin = new Padding(0, 3, 0, 3),
        };
        panel.Controls.Add(new Label
        {
            Text = label, Width = 34, Height = boxH, AutoSize = false, Font = Theme.UiFont,
            ForeColor = Theme.TextSecondary, BackColor = Theme.PanelBackground,
            TextAlign = ContentAlignment.MiddleLeft,
        });
        var box = new ChromeNumericBox
        {
            Width = 132,
            Height = boxH,
            Minimum = -200000,
            Maximum = 200000,
            Value = Math.Clamp(value, -200000, 200000),
            Font = Theme.MonoFont,
            BackColor = Theme.WindowBackground,
            ForeColor = Theme.TextPrimary,
            // 第五十三改（用户图蓝框「UI 和整体不搭」）：这里原来又写了 BorderStyle.FixedSingle，
            // 把 ChromeNumericBox 构造器里设好的 None 顶掉——系统直角灰边重新盖回来，
            // 自绘的圆角发丝边就被叠成两层。圆角边由控件自己画，这里不许再碰 BorderStyle。
        };
        // 直接键入整数像素后立即与画布同步（需求 §3.4）
        box.ValueChanged += (_, _) => onChange((int)box.Value);
        _lastIntRowForTest = box;
        panel.Controls.Add(box);
        Controls.Add(panel);
        return box;
    }

    /// <summary>与 AddIntRow 同形，只为了在"下"这一行也保持同样的排版。</summary>
    private NumericUpDown AddLabel2(string label, int value, Action<int> onChange) =>
        AddIntRow(label, value, onChange);

    /// <summary>
    /// 面板里的动作按钮。默认次级（抬起的卡面）；一屏的主操作传 <see cref="ChromeButtonKind.Primary"/>，
    /// 「取消／恢复」这类弱操作传 <see cref="ChromeButtonKind.Ghost"/>。
    /// 返回类型仍是 <see cref="Button"/>：判据里 <c>OfType&lt;Button&gt;()</c> 那一族量法一个字不用改。
    /// </summary>
    private Button AddButton(string text, Action onClick, ChromeButtonKind kind = ChromeButtonKind.Secondary)
    {
        var button = new ChromeButton
        {
            Text = text,
            Dock = DockStyle.Top,
            Height = Math.Max(40, Theme.UiFontBold.Height + 20),
            Kind = kind,
            Margin = new Padding(0, 4, 0, 4),
        };
        button.Click += (_, _) => onClick();
        Controls.Add(button);
        return button;
    }
}
