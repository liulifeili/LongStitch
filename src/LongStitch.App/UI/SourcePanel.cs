using LongStitch.Core;
using LongStitch.Core.Model;
using LongStitch.App.Imaging;

namespace LongStitch.App.UI;

/// <summary>
/// 左侧素材面板：缩略图列表 + 添加按钮 + 右键排序/删除。
///
/// 需求 §4.1 的"缩略图标注序号、文件名、尺寸"与 §3.1 的"拖动时显示插入线"都在这里。
/// 拖动排序用自绘的插入线而不是依赖系统拖放，行为更可控，也不会在长列表上抖。
/// </summary>
public sealed class SourcePanel : UserControl
{
    private readonly Project _project;
    private readonly FileSourceResolver _resolver;
    private readonly HintListBox _list;
    private readonly ChromeScrollHost _listHost;
    private readonly Button _addButton;
    private readonly Label _header;
    private readonly ContextMenuStrip _menu;

    /// <summary>用户点了"添加图片"。由主窗口负责弹文件对话框并回填。</summary>
    public event Action? AddRequested;

    /// <summary>选中项变化（-1 表示没有）。主窗口据此定位画布。</summary>
    public event Action<int>? IndexSelected;

    private int _dragFromIndex = -1;
    private int _insertIndex = -1;

    /// <summary>
    /// 第五十六改①（用户图1红框）：行卡片与「添加图片」按钮<b>共用</b>的左右缩进。
    /// 旧写法卡片缩 8px、按钮宿主却留 16px——红框量出的正是这 8px 错位。
    /// 现在两边同吃这一枚常量，任何一方改缩进都躲不过另一方的对账。
    /// public：本仓没有 InternalsVisibleTo，电池的对账判据要直接读这枚数。
    /// </summary>
    public const int SideInset = 16;

    /// <summary>第五十六改①工装口：最后一次行自绘写下的卡片矩形（列表客户区坐标，判据据此与按钮外框对账）。</summary>
    public Rectangle LastCardRectForTest => _lastCardRect;

    /// <summary>第五十六改①工装口：「添加图片」按钮外框在面板客户区里的矩形。</summary>
    public Rectangle AddButtonFrameForTest
    {
        get
        {
            Point origin = PointToScreen(Point.Empty);
            Point corner = _addButton.PointToScreen(Point.Empty);
            return new Rectangle(corner.X - origin.X, corner.Y - origin.Y, _addButton.Width, _addButton.Height);
        }
    }

    /// <summary>第五十六改①工装口：同步逼一次行自绘（工装进程没有真屏，Update 会同步泵出 WM_PAINT）。</summary>
    public void ForceRowPaintForTest()
    {
        _list.Invalidate();
        _list.Update();
    }

    private Rectangle _lastCardRect;

    public SourcePanel(Project project, FileSourceResolver resolver)
    {
        _project = project;
        _resolver = resolver;

        // 第五十改：换成自绘的次级按钮（圆角卡面 + 「＋」字形）。
        // 外面套一层带 Padding 的宿主面板：圆角卡面直接贴着侧栏边缘会显得被切掉一半，
        // 留 12px 呼吸位才读得出"这是一枚放在面板里的按钮"。宿主不改按钮的归属——
        // 判据 ui/添加图片在界面上只有一枚按钮 走的是整棵控件树，仍然判在 SourcePanel 里。
        _addButton = new ChromeButton
        {
            Text = "添加图片",
            Dock = DockStyle.Fill,
            Kind = ChromeButtonKind.Secondary,
            Glyph = ChromeGlyph.Plus,
            ToolTipText = "从文件里选截图追加到末尾（Ctrl+O）；也可以直接把文件拖进这个列表",
        };
        _addButton.Click += (_, _) => AddRequested?.Invoke();
        var addHost = new Panel
        {
            Dock = DockStyle.Top,
            Height = Theme.UiFontBold.Height + 40,
            // 第五十六改①：左右缩进与行卡片同吃 SourcePanel.SideInset——两条沿必须落在同一条竖线上。
            Padding = new Padding(SideInset, 4, SideInset, 12),
            BackColor = Theme.PanelBackground,
        };
        addHost.Controls.Add(_addButton);

        _list = new HintListBox
        {
            // 第五十五改④：Dock 移交给宿主（ChromeScrollHost），列表在宿主里被加宽到
            // 原生滚动条落界外——这里再 Dock=Fill 会把宽度顶回宿主宽，原生条又露出来。
            BorderStyle = BorderStyle.None,
            DrawMode = DrawMode.OwnerDrawFixed,
            ItemHeight = Chrome.SourceRowHeight,
            IntegralHeight = false,
            BackColor = Theme.PanelBackground,
            ForeColor = Theme.TextPrimary,
            Font = Theme.UiFontSmall,
            AllowDrop = true,
        };
        _list.DrawItem += OnDrawItem;
        _list.SelectedIndexChanged += (_, _) => IndexSelected?.Invoke(_list.SelectedIndex);
        _list.MouseDown += OnMouseDown;
        _list.MouseMove += OnMouseMove;
        _list.MouseUp += OnMouseUp;
        _list.DragEnter += OnDragEnter;
        _list.DragDrop += OnDragDrop;
        _list.PaintEmpty = DrawEmptyHint;

        // 第五十五改④（用户图4）：列表套进自绘滚动条宿主——原生白条被裁到界外，
        // 换成与 ToolPanel 同一根蓝色圆弧 <see cref="ChromeScrollBar"/>。
        // 列表自身不再直接 Dock：宿主接管 Fill，列表在宿主里被加宽到原生条落界外。
        _listHost = new ChromeScrollHost(_list) { Dock = DockStyle.Fill };

        _menu = new ContextMenuStrip { BackColor = Theme.PanelBackground, ForeColor = Theme.TextPrimary };
        _menu.Items.Add("上移", null, (_, _) => MoveSelected(-1));
        _menu.Items.Add("下移", null, (_, _) => MoveSelected(+1));
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("移到最前", null, (_, _) => MoveSelectedTo(0));
        _menu.Items.Add("移到最后", null, (_, _) => MoveSelectedTo(_project.Doc.Segments.Count - 1));
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("重新定位源文件…", null, (_, _) => RelocateSelected());
        _menu.Items.Add("查看源图信息", null, (_, _) => ShowSourceInfo());
        _menu.Items.Add("删除这一段", null, (_, _) => DeleteSelected());
        _list.ContextMenuStrip = _menu;

        _header = new Label
        {
            Text = "素材",
            Dock = DockStyle.Top,
            Height = Theme.UiFontBold.Height + 32,
            Padding = new Padding(20, 0, 16, 0),
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Theme.TextPrimary,
            Font = Theme.UiFontBold,
            BackColor = Theme.PanelBackground,
        };

        Dock = DockStyle.Fill;
        BackColor = Theme.PanelBackground;
        Controls.Add(_listHost);
        Controls.Add(addHost);
        Controls.Add(_header);

        _project.Changed += RefreshItems;
        RefreshItems();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _project.Changed -= RefreshItems;
            // 第五十九改②：销毁时把还挂在行上的缩略图显式 Dispose。
            // 这些是 GDI 位图（非托管资源），靠终结器回收要等下一轮 GC——
            // 面板销毁意味着它们再也不会被画，没有任何理由留到终结器。
            DisposeRowThumbs();
        }
        base.Dispose(disposing);
    }

    /// <summary>已被显式 Dispose 掉的缩略图张数（进程累计，判据与内存快照读它对账）。</summary>
    public long ThumbsDisposedForTest { get; private set; }

    /// <summary>
    /// 把当前列表里每行的缩略图逐张 Dispose 并从行上摘掉。
    /// <see cref="RefreshItems"/> 整列重建前与面板销毁时各调一次——
    /// 之前这两处都直接扔下旧 <see cref="Row"/>，Thumb 那枚 GDI 位图只能等终结器，
    /// 连续刷新（分析、排序、删段）会反复造缩略图，回收压力全落在 GC 上。
    /// </summary>
    private void DisposeRowThumbs()
    {
        // 按下标遍历而不是 foreach：行内要原地换成 Thumb=null 的新 Row（摘掉已 Dispose 的位图，
        // 不给后续绘制留摸到死位图的机会），替换期间条目总数不变，按下标是安全的。
        for (int i = 0; i < _list.Items.Count; i++)
        {
            if (_list.Items[i] is Row { Thumb: not null } row)
            {
                row.Thumb.Dispose();
                ThumbsDisposedForTest++;
                _list.Items[i] = row with { Thumb = null };
            }
        }
    }

    private sealed record Row(
        string SegmentId, string? SourceId, int Index, string Name, string SizeText,
        System.Drawing.Image? Thumb, bool Missing);

    /// <summary>
    /// 只为"空态引导"而存在的 ListBox 子类。
    /// 第五十五改④起改继承 <see cref="ChromeScrollListBox"/>（滚轮拦截与可见行宽都在那里），
    /// 本类只保留空态补画这一件事。
    ///
    /// ═══ 为什么不能挂 Paint 事件 ═══
    /// ListBox 是原生控件（没有 UserPaint 样式），WinForms 的 <see cref="Control.Paint"/>
    /// 在它身上根本不发——第一版把引导挂在 Paint 上，真窗口里那一块仍然是纯灰。
    /// 原生控件的绘制发生在 DefWndProc 里，所以唯一可靠的"画在它上面"的钩子是
    /// WndProc：先让 base 把原生内容画完，再在 WM_PAINT 之后补我们这一笔。
    /// 只在 Items.Count == 0 时补；有内容时一个像素都不碰（不干扰行自绘与插入线）。
    /// </summary>
    private sealed class HintListBox : ChromeScrollListBox
    {
        private const int WmPaint = 0x000F;

        /// <summary>空态时补画的那一笔；由宿主面板提供（它才认识主题与文案）。</summary>
        internal Action<Graphics>? PaintEmpty;

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg != WmPaint || Items.Count != 0 || PaintEmpty is null) return;
            // base 里 DefWndProc 已经 BeginPaint/EndPaint 过，这里另开一枚 DC 补画空区。
            using Graphics g = Graphics.FromHwnd(Handle);
            PaintEmpty(g);
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            // 侧栏宽度变化时引导要重新居中；原生 ListBox 不一定会因 WM_SIZE 重画空区。
            if (Items.Count == 0) Invalidate();
        }
    }

    private void RefreshItems()
    {
        if (InvokeRequired) { BeginInvoke(RefreshItems); return; }

        // 批次期间挂起：逐张导入时每追加一段就整列 Clear 再重填一次缩略图列表，
        // 正是"添加一张图片整个画面就渲染一次"的那一半闪烁（用户第四十四改）。
        // 放闸时欠着的那一次统一补上，与画布那一头同一个批次（MainForm.BeginUiBatch）。
        if (_refreshSuspended)
        {
            _refreshOwed = true;
            RefreshesDeferredForTest++;
            return;
        }

        int keep = _list.SelectedIndex;
        _list.BeginUpdate();
        // 第五十九改②：整列重建前把旧行的缩略图显式 Dispose（GDI 位图不留给终结器）。
        DisposeRowThumbs();
        _list.Items.Clear();
        foreach (Segment seg in _project.Doc.Segments)
        {
            // 关键：缩略图要按<b>源图 Id</b>取，不是片段 Id。
            // 片段 Id 拿去查源图永远查不到，列表就会整列显示"缺"。
            SourceImage? src = _project.Doc.FindSource(seg.SourceId);
            // "缺失"有两种：记录都没有（src is null），或记录在但文件已不在
            // （SourceMissing，打开项目时回填）。只判 null 会漏掉后一种。
            bool missing = src is null || src.SourceMissing;
            int index = _project.Doc.Segments.IndexOf(seg) + 1;
            _list.Items.Add(new Row(
                seg.Id, src?.Id, index,
                src?.FileName ?? "(源文件丢失)",
                missing ? "源文件丢失 · 右键重新定位" : $"{src!.Size.Width}×{src.Size.Height}",
                null, missing));
        }
        if (keep >= 0 && keep < _list.Items.Count) _list.SelectedIndex = keep;
        _list.EndUpdate();
        // 标题上带枚数：侧栏一折叠再展开、或者导入了十几张之后，
        // "现在到底有几段"要能在不数列表的情况下读出来（与拼缝那行统计同一条口径）。
        int missingCount = 0;
        foreach (object item in _list.Items) if (((Row)item).Missing) missingCount++;
        _header.Text = _list.Items.Count == 0
            ? "素材"
            : $"素材  {_list.Items.Count}" + (missingCount > 0 ? $"  ·  {missingCount} 张丢失" : "");
        _header.ForeColor = missingCount > 0 ? Theme.Warning : Theme.TextPrimary;
        // 第五十五改④：条目数一变就把滚动状态读回自绘条（滚动量随之变短/消失）。
        _listHost.SyncFromList();
    }

    private bool _refreshSuspended;
    private bool _refreshOwed;

    /// <summary>批次期间被挂起的重建笔数（进程累计）——判据拿它对账"通知真的发出来了、也被接住了"。</summary>
    public long RefreshesDeferredForTest { get; private set; }

    /// <summary>
    /// 当前列表里挂着缩略图的行数（第五十九改内存快照的 ThumbnailCount 读数）。
    /// 每次现场数一遍而不另记计数器：行的生成/替换/整列重建有三个入口，
    /// 三处各自加减迟早漏一处，现场遍历 96px 行列表的开销可以忽略。
    /// <b>口径警告</b>：它只统计当前列表行上挂着的缩略图——列表清空/重建后，
    /// 还没被回收的旧缩略图已经不在这个数里；"这个数很低"证明不了旧缩略图都释放了。
    /// "释放了没有"由 <see cref="ThumbsDisposedForTest"/> 那一笔对账。
    /// </summary>
    public int ThumbnailCountForTest
    {
        get
        {
            int count = 0;
            foreach (object item in _list.Items)
                if (((Row)item).Thumb is not null) count++;
            return count;
        }
    }

    /// <summary>
    /// 测试出口：走真身 <see cref="OnDrawItem"/> 画一行。缩略图的唯一生成入口就在那里面
    /// （懒生成、生成后挂在 Row 上），判据要拿到真缩略图只能走这条通路，不能绕过它自己 new。
    /// </summary>
    public void DrawItemForTest(DrawItemEventArgs e) => OnDrawItem(this, e);

    // ───── 第五十五改④：滚动条换装的回读口（电池判据从这里量） ─────

    /// <summary>列表的滚动宿主（自绘滚动条在那上面）。</summary>
    public Control ListHostForTest => _listHost;

    /// <summary>内容装不下时自绘滑块才出现。</summary>
    public bool ScrollbarThumbVisibleForTest => _listHost.Bar.ThumbVisible;

    /// <summary>自绘条当前的像素偏移。</summary>
    public int ScrollbarValueForTest => _listHost.Bar.Value;

    /// <summary>自绘条算出的内容总高（条数×行高）与一屏高（诊断钳位用）。</summary>
    public int ScrollbarMaximumForTest => _listHost.Bar.Maximum;
    public int ScrollbarLargeChangeForTest => _listHost.Bar.LargeChange;

    /// <summary>列表当前条目数（核对夹具真的把料装进去了）。</summary>
    public int ListItemsCountForTest => _list.Items.Count;

    /// <summary>列表顶行下标（滚动位置的真实值）。</summary>
    public int ListTopIndexForTest => _list.TopIndex;

    /// <summary>列表行高（把像素偏移换算回行的账本）。</summary>
    public int ListRowHeightForTest => _list.ItemHeight;

    /// <summary>可见行宽：列表必须比宿主宽（原生条被裁在界外的几何前提）。</summary>
    public int ListRowWidthForTest => _list.RowWidth;

    /// <summary>原生滚动条是否真的整体落在宿主界外（列表宽 &gt; 宿主宽）。</summary>
    public bool NativeScrollbarClippedForTest => _list.Width > _listHost.ClientSize.Width;

    /// <summary>注入一格滚轮（delta 是 ±120 的倍数），走的是与真滚轮同一条 WndProc 之外的宿主通路。</summary>
    public void ScrollListWheelForTest(int delta) => _listHost.ScrollByWheel(delta);

    /// <summary>导入／分析批次期间挂起列表重建；放闸时欠着的那一次补上（见 <see cref="RefreshItems"/>）。</summary>
    public void SuspendRefresh(bool suspend)
    {
        _refreshSuspended = suspend;
        if (!suspend && _refreshOwed)
        {
            _refreshOwed = false;
            RefreshItems();
        }
    }

    /// <summary>
    /// 列表空态：一张素材都没有时，这块侧栏原来是一整片纯灰，
    /// 读起来像"面板坏了"而不是"还没开始"。这里补一段居中的引导。
    /// 由 <see cref="HintListBox"/> 在原生绘制之后调用（原生 ListBox 不发 Paint 事件）。
    ///
    /// ═══ 为什么只画屏幕，不往 Items 里塞一枚占位项 ═══
    /// <c>_list.Items.Count</c> 是被当作真值读的：标题上的枚数、外部判据、
    /// 右键菜单的可用性都直接或间接问它。塞一枚"（空）"进去会让计数变成 1，
    /// 于是"没有素材"这个事实在数据层就消失了。这一笔只画屏幕，一个条目都不加，
    /// 空态与真值互不污染。
    ///
    /// 图标字体不在本机时 <see cref="Chrome.Glyph"/> 自己会跳过（不画方框），
    /// 文字仍然在，引导不会整段消失。
    /// </summary>
    private void DrawEmptyHint(Graphics g)
    {
        // 第五十五改④：列表比宿主宽（藏原生条用），居中要按可见行宽算，不然引导整体偏右。
        int wide = _list.RowWidth > 0 ? _list.RowWidth : _list.ClientSize.Width;
        // 放在上三分之一而不是正中：正中的话，导入第一张之后那行卡会盖在引导原来的位置上，
        // 视觉上像"引导被压住了"；靠上一点则读作"这一栏的开头说明"。
        int mid = Math.Max(56, _list.ClientSize.Height / 3);
        var iconBox = new Rectangle(Math.Max(0, (wide - 44) / 2), mid - 44, 44, 44);
        Chrome.Glyph(g, ChromeGlyph.Picture, iconBox, Theme.TextDisabled, Chrome.IconFontLarge);
        // 第五十一改（用户图3）：标题盒原来 22px 高，UiFontBold 的字框在部分 DPI 下更高，
        // 下半截被 VerticalCenter 裁掉（「文字的下半部分受到挤压」）。放宽到 28px，正文随之下移 4px。
        int titleHeight = Theme.UiFontBold.Height + 10;
        Chrome.Text(g, "素材将在这里排列", new Rectangle(0, mid + 8, wide, titleHeight),
            Theme.TextSecondary, Theme.UiFontBold,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        Chrome.Text(g, "拖入截图，开始新的拼接", new Rectangle(8, mid + titleHeight + 16, Math.Max(0, wide - 16), Theme.UiFontSmall.Height + 8),
            Theme.TextSecondary, Theme.UiFontSmall,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    private void OnDrawItem(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= _list.Items.Count) return;
        var row = (Row)_list.Items[e.Index];

        bool selected = (e.State & DrawItemState.Selected) != 0;
        Graphics g = e.Graphics;
        using (var bg = new SolidBrush(Theme.PanelBackground))
            g.FillRectangle(bg, e.Bounds);

        // 第五十五改④：列表被宿主加宽以藏原生滚动条，e.Bounds 的右端有一截在界外。
        // 卡片右沿不能照抄"可见行宽"（那已经把自绘滚动条的占位扣掉了一截），
        // 第五十六改①起直接按宿主客户宽收——宿主 Dock=Fill，宽＝面板宽。
        // 第五十六改①（用户图1红框）：卡片左右沿与「添加图片」按钮外框对成同一条竖线，
        // 左右同吃 SideInset；滚动条画在宿主右缘上方，与卡片右沿之间还留 6px 呼吸位。
        var card = new Rectangle(SideInset, e.Bounds.Y + 4,
            Math.Max(0, _listHost.ClientSize.Width - SideInset * 2), Math.Max(0, e.Bounds.Height - 8));
        _lastCardRect = card;
        Chrome.Card(g, card, selected ? Theme.Accent : Chrome.Raised, Chrome.Radius, bevel: !selected);

        var thumbBox = new Rectangle(card.X + 8, card.Y + (card.Height - 52) / 2, 44, 52);

        // 缩略图只在需要时生成一次并缓存到 Row 上；列表滚动时不重复解码
        System.Drawing.Image? thumb = row.Thumb;
        if (thumb is null && !row.Missing && row.SourceId is not null)
        {
            if (_resolver.TryResolve(row.SourceId, out Core.Imaging.RgbaImage? img, out _) && img is not null)
            {
                thumb = FileSourceResolver.MakeThumbnail(img, 96);
                _list.Items[e.Index] = row with { Thumb = thumb };
            }
        }
        var smooth = g.SmoothingMode;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using (var thumbPath = Chrome.RoundRect(thumbBox, Chrome.RadiusSmall))
        {
            if (thumb is not null)
            {
                // 缩略图按框等比居中：长截图是极窄极高的条，直接拉伸会糊成一根线
                var fit = new Rectangle(thumbBox.X, thumbBox.Y, thumbBox.Width, thumbBox.Height);
                double scale = Math.Min((double)fit.Width / thumb.Width, (double)fit.Height / thumb.Height);
                int w = Math.Max(1, (int)Math.Round(thumb.Width * scale));
                int h = Math.Max(1, (int)Math.Round(thumb.Height * scale));
                var dest = new Rectangle(fit.X + (fit.Width - w) / 2, fit.Y + (fit.Height - h) / 2, w, h);
                using (var backdrop = new SolidBrush(Chrome.Sunken))
                    g.FillPath(backdrop, thumbPath);
                var mode = g.InterpolationMode;
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(thumb, dest);
                g.InterpolationMode = mode;
            }
            else
            {
                using var backdrop = new SolidBrush(Chrome.Sunken);
                g.FillPath(backdrop, thumbPath);
            }
            using var frame = new Pen(selected ? Chrome.Fade(Color.White, 110) : Theme.PanelBorder, 1f);
            g.DrawPath(frame, thumbPath);
        }
        g.SmoothingMode = smooth;
        if (thumb is null)
            Chrome.Text(g, row.Missing ? "缺" : "…", thumbBox,
                row.Missing ? Theme.Danger : Theme.TextDisabled, Theme.UiFontBold,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

        int textX = thumbBox.Right + 10;
        int textWidth = Math.Max(40, card.Right - 10 - textX);
        // 第五十一改回修（用户图2红框）：这两行文本盒原来写死 20/16px——那是 96 DPI 的字高。
        // 150% DPI 下 UiFontSmall 长到 ~24px，盒子装不下，下缘被裁、两行互相叠压。
        // 现在盒高从字体现量推导（<c>Chrome.SourceRowTitleHeight</c> / <c>SourceRowSizeHeight</c>，
        // 与 <c>Chrome.SourceRowHeight</c> 行高是同一段账），两行整块在卡内垂直居中，
        // 与缩略图的居中同一条规则，任何 DPI 下都不挤。
        int titleH = Chrome.SourceRowTitleHeight;
        int sizeH = Chrome.SourceRowSizeHeight;
        int blockTop = card.Y + Math.Max(0, (card.Height - (titleH + 2 + sizeH)) / 2);
        Chrome.Text(g, $"#{row.Index}  {row.Name}",
            new Rectangle(textX, blockTop, textWidth, titleH),
            selected ? Color.White : Theme.TextPrimary, Theme.UiFontSmall,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
            TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

        Chrome.Text(g, row.SizeText,
            new Rectangle(textX, blockTop + titleH + 2, textWidth, sizeH),
            selected ? Color.White : row.Missing ? Theme.Danger : Theme.TextSecondary,
            Theme.UiFontMicro,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
            TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

        // 拖动插入线：告诉用户"松手会插到这里"，比直接换顺序更可预期。
        // 两端各加一枚小圆帽，细线在深色底上才不至于看不见。
        if (_insertIndex >= 0 && _dragFromIndex >= 0)
        {
            int lineY = _insertIndex <= e.Index ? e.Bounds.Top + 1 : e.Bounds.Bottom - 2;
            if (lineY >= 0 && lineY <= _list.Height)
            {
                using var pen = new Pen(Theme.Accent, 2f);
                // 第五十六改①：插入线两端也收进卡片边框里，不再沿旧缩进伸到卡外。
                int lineRight = card.Right - 4;
                g.DrawLine(pen, card.Left, lineY, lineRight, lineY);
                using var cap = new SolidBrush(Theme.Accent);
                var aa = g.SmoothingMode;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.FillEllipse(cap, card.Left - 3, lineY - 3, 7, 7);
                g.FillEllipse(cap, lineRight - 3, lineY - 3, 7, 7);
                g.SmoothingMode = aa;
            }
        }
    }

    // ───────────────────────── 拖动排序 ─────────────────────────

    private void OnMouseDown(object? sender, MouseEventArgs e)
    {
        int index = _list.IndexFromPoint(e.Location);
        if (index < 0) return;
        _list.SelectedIndex = index;
        _dragFromIndex = index;
        _insertIndex = index;
        _list.Invalidate();
    }

    private void OnMouseMove(object? sender, MouseEventArgs e)
    {
        if (_dragFromIndex < 0 || (e.Button & MouseButtons.Left) == 0) return;
        int index = _list.IndexFromPoint(e.Location);
        // 半行阈值必须跟着行高走：写死 31 是上一版 62px 行高的一半，
        // 行高改成 Chrome.SourceRowHeight 之后那个字面量会把插入点判到上一行去。
        int half = Chrome.SourceRowHeight / 2;
        int target = index < 0 ? _list.Items.Count : (e.Location.Y > _list.GetItemRectangle(index).Y + half ? index + 1 : index);
        if (target == _insertIndex) return;
        _insertIndex = Math.Clamp(target, 0, _list.Items.Count);
        _list.Invalidate();
    }

    private void OnMouseUp(object? sender, MouseEventArgs e)
    {
        if (_dragFromIndex < 0) return;
        int from = _dragFromIndex, to = _insertIndex;
        _dragFromIndex = -1;
        _insertIndex = -1;
        _list.Invalidate();

        if (to < 0 || to == from || to == from + 1) return;
        // ListBox 的"插入点"语义转成"目标下标"：往后插要减一。
        // MoveSegment 内部已经在同一条历史里重建了拼缝，
        // 再调 EnsureSeams 会让一次拖动记两条撤销（审查问题 #10）。
        int destination = to > from ? to - 1 : to;
        _project.MoveSegment(from, destination);
    }

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        // 从资源管理器直接拖文件到列表尾部 = 追加导入
        if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true)
            e.Effect = DragDropEffects.Copy;
    }

    private void OnDragDrop(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetData(DataFormats.FileDrop) is string[] files)
            ImportRequested?.Invoke(files);
    }

    /// <summary>从面板上拖进来的文件路径（与主窗口的导入流程共用）。</summary>
    public event Action<string[]>? ImportRequested;

    // ───────────────────────── 右键动作 ─────────────────────────

    private void MoveSelected(int delta)
    {
        int from = _list.SelectedIndex;
        int to = from + delta;
        if (from < 0 || to < 0 || to >= _project.Doc.Segments.Count) return;
        _project.MoveSegment(from, to);   // 拼缝重建已在同一条历史里完成
    }

    private void MoveSelectedTo(int to)
    {
        int from = _list.SelectedIndex;
        if (from < 0) return;
        _project.MoveSegment(from, Math.Clamp(to, 0, _project.Doc.Segments.Count - 1));
    }

    private void DeleteSelected()
    {
        int index = _list.SelectedIndex;
        if (index < 0 || index >= _project.Doc.Segments.Count) return;
        string id = _project.Doc.Segments[index].Id;
        _project.RemoveSegment(id);       // 同上：RemoveSegment 内部已重建拼缝
    }

    /// <summary>
    /// 右键"重新定位源文件…"：只负责问用户要哪个文件、以及尺寸不符时再问一句，
    /// 判定与写入全在 <see cref="RelocateTo"/>（那才是能被电池量到的地方）。
    /// </summary>
    private void RelocateSelected()
    {
        int index = _list.SelectedIndex;
        if (index < 0 || index >= _project.Doc.Segments.Count) return;
        SourceImage? src = _project.Doc.FindSource(_project.Doc.Segments[index].SourceId);
        if (src is null)
        {
            MessageBox.Show(this, "这一段没有源图记录，无法重新定位。", "重新定位",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        using var dialog = new OpenFileDialog
        {
            Title = $"为「{src.FileName}」重新选择源文件",
            Filter = "图片文件|*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.tif;*.tiff|所有文件|*.*",
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        RelocateOutcome outcome = RelocateTo(index, dialog.FileName, out string? message);
        if (outcome == RelocateOutcome.NeedsSizeConfirmation &&
            MessageBox.Show(this, message + "\n\n仍要继续吗？", "尺寸不一致",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
            outcome = RelocateTo(index, dialog.FileName, out message, acceptSizeChange: true);

        if (outcome == RelocateOutcome.Failed)
            MessageBox.Show(this, message ?? "重新定位失败", "重新定位",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    /// <summary>一次重新定位的结果。</summary>
    public enum RelocateOutcome { Applied, Failed, NeedsSizeConfirmation }

    /// <summary>
    /// 把第 <paramref name="segmentIndex"/> 段的源文件指向新路径。
    ///
    /// 公开并带明确返回值，是为了让这条通路能被电池直接量：以前它整个埋在文件对话框里，
    /// "尺寸不符要先问、缺失标记要清掉、缓存要作废"这些只能靠真机点。
    /// 只解码<b>一次</b>：先解出来再决定要不要改，改与不改都以这份解码结果为准，
    /// 避免"弹窗时读到的尺寸"与"真正写进记录的尺寸"是两份。
    /// </summary>
    /// <param name="segmentIndex">片段在 <c>Doc.Segments</c> 里的下标。</param>
    /// <param name="newPath">新选中的文件路径。</param>
    /// <param name="message">失败原因或需要用户确认的那句话；成功时为 null。</param>
    /// <param name="acceptSizeChange">尺寸与记录不同是否已经征得同意。</param>
    public RelocateOutcome RelocateTo(int segmentIndex, string newPath,
        out string? message, bool acceptSizeChange = false)
    {
        message = null;
        if (segmentIndex < 0 || segmentIndex >= _project.Doc.Segments.Count)
        {
            message = "这一段已不存在（可能被删除或重排）";
            return RelocateOutcome.Failed;
        }
        Segment seg = _project.Doc.Segments[segmentIndex];
        SourceImage? src = _project.Doc.FindSource(seg.SourceId);
        if (src is null)
        {
            message = "这一段没有源图记录，无法重新定位";
            return RelocateOutcome.Failed;
        }

        DecodedImage decoded;
        try
        {
            decoded = FileSourceResolver.Decode(newPath);
        }
        catch (Exception ex)
        {
            message = "这个文件读不出来：" + ex.Message;
            return RelocateOutcome.Failed;
        }

        // 尺寸变了要先问：已记录的取用区域可能越界，硬拉过去比报错更糟
        if (!acceptSizeChange && decoded.Image.Size != src.Size)
        {
            message = $"新文件是 {decoded.Image.Size.Width}×{decoded.Image.Size.Height}，" +
                      $"与记录的 {src.Size.Width}×{src.Size.Height} 不同。\n" +
                      "已保存的取用区域可能越界（越界部分按透明处理），拼缝也需要重新分析。";
            return RelocateOutcome.NeedsSizeConfirmation;
        }

        var info = new FileInfo(newPath);
        _project.Mutate("重新定位源文件", _ =>
        {
            src.Path = newPath;
            src.FileName = Path.GetFileName(newPath);
            src.Size = decoded.Image.Size;
            src.ExifOrientation = decoded.ExifOrientation;
            src.FileSizeBytes = info.Length;
            src.FileModifiedUtcMs = info.LastWriteTimeUtc.Ticks / TimeSpan.TicksPerMillisecond;
            src.Format = decoded.Format;
            src.ImportWarnings = decoded.Warnings.ToList();
            // 关键：缺失标记要跟着清掉，否则列表继续显示"源文件丢失"、导出也照旧被硬拦
            src.SourceMissing = false;
        });
        _resolver.Register(src);
        _resolver.Invalidate();
        _list.Invalidate();
        return RelocateOutcome.Applied;
    }

    private void ShowSourceInfo()
    {
        int index = _list.SelectedIndex;
        if (index < 0 || index >= _project.Doc.Segments.Count) return;
        Segment seg = _project.Doc.Segments[index];
        SourceImage? src = _project.FindSource(seg);

        var lines = new List<string>
        {
            $"文件：{src?.Path ?? "(未登记)"}",
            $"原始尺寸：{src?.Size.Width ?? 0}×{src?.Size.Height ?? 0}",
            $"格式：{src?.Format ?? "?"} · EXIF 方向 {src?.ExifOrientation ?? 1}",
            $"取用区域：{seg.SourceRect}（裁边后 {seg.EffectiveRect(_project.Doc.Crop.Enabled ? _project.Doc.Crop.Uniform : Core.Geometry.Insets.Zero)}）",
            $"清理补丁：{seg.Patches.Count(x => x.Enabled)} 个生效 / {seg.Patches.Count} 个",
        };
        if (src?.ImportWarnings.Count > 0)
            lines.Add("导入提示：\n  · " + string.Join("\n  · ", src.ImportWarnings));

        MessageBox.Show(this, string.Join("\n", lines), "源图信息",
            MessageBoxButtons.OK, MessageBoxIcon.Information);
    }
}

/// <summary>给面板用的便捷扩展：从片段找回它的源图记录。</summary>
internal static class PanelExtensions
{
    public static SourceImage? FindSource(this Project project, Segment segment) =>
        project.Doc.FindSource(segment.SourceId);
}
