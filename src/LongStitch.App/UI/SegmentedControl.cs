namespace LongStitch.App.UI;

/// <summary>
/// 第五十改：真正的分段控件（长截图／竖拼／横拼）。
///
/// 原来那三枚是 ToolStripButton 加一个 Checked 勾：屏上看起来是三个散落的词，
/// 选中态只有一圈细蓝框，用户看不出「这是一组互斥的档位」。这里画成一条凹轨 +
/// 一枚凸起的滑块：凹轨告诉人「这几个选项属于同一件事」，滑块告诉人「现在在哪一档」。
///
/// 交互契约：单击某段＝切到那档；键盘左右方向键＝在档间移动（需求 §6 键盘焦点提示）；
/// Tab 能进来。焦点环只在<b>用键盘换档</b>时画（鼠标点击不画——否则蓝环常驻，用户报过
/// 「外框有蓝色」；焦点环的几何见 Chrome.FocusRing 的回修注释）。选中变化只发一次
/// <see cref="SelectionChanged"/>，由主窗口决定要不要真的切模式（切模式可能弹「手动拼缝怎么办」的询问框）。
/// </summary>
internal sealed class SegmentedControl : Control
{
    private readonly string[] _labels;
    private int[] _widths = Array.Empty<int>();
    // 第六十改：-1 = 没有任何一档被选中。新建项目时文档模式是 Unset（还没选），
    // 分段必须如实显示"三档都没抬起"，而不是替用户默认抬起第一档。
    private int _selected = -1;
    private int _hover = -1;
    private readonly ToolTip _tip = new();

    /// <summary>
    /// 选中档用的加粗字。第五十二改：原来直接写 <see cref="Theme.UiFontBold"/>，于是控件自己的
    /// <see cref="Control.Font"/> 长高时选中档的字却不跟着长（高 DPI 下两者脱节）。改成「当前 Font 的加粗版」：
    /// 正常使用时 Font==Theme.UiFont，它的加粗版与 Theme.UiFontBold 同族同字号，视觉不变；
    /// 而判把 Font 设高模拟高 DPI 时，选中档的字与控件高度一起长，几何判据才量得到真东西。
    /// </summary>
    private Font _boldFont = new(Theme.UiFont, FontStyle.Bold);

    /// <summary>
    /// 焦点环是否该画。只有「用键盘换档」才置 true（OnKeyDown），鼠标点下立即置 false。
    /// 初值 false：窗口刚打开时控件若自动拿到焦点，也不画环——屏上不该常驻一枚蓝框。
    /// </summary>
    private bool _keyboardFocusVisual;

    /// <summary>
    /// 悬停提示。<see cref="Control"/> 没有这个属性（那是 ToolStripItem 的），
    /// 但这枚控件要装进 ToolStripControlHost，调用处希望与 ToolStripItem 同一套写法。
    /// </summary>
    internal string ToolTipText
    {
        get => _tip.GetToolTip(this) ?? "";
        set => _tip.SetToolTip(this, value);
    }

    /// <summary>用户选了另一档（参数是新下标）。程序设 <see cref="SelectedIndex"/> 不发这个事件。</summary>
    internal event Action<int>? SelectionChanged;

    /// <summary>
    /// 「用户选择」事件累计发过几次。程序走 <see cref="SelectedIndex"/> 回填不许让它涨——
    /// 判据 <c>guard/分段程序回填必须静音且用户选择只发一次</c> 钉的就是这条纪律：
    /// 回填一旦发事件，主窗口那个「切模式可能弹框、取消后要退回」的 handler 会被绕回重入。
    /// </summary>
    internal int SelectionChangedRaiseCount { get; private set; }

    /// <summary>
    /// 用户选择某一档的唯一入口：鼠标、键盘与冒烟工装都走这里，
    /// 保证「改选中 + 发一次事件」这条通路只有一份。
    /// 与 <see cref="SelectedIndex"/> 的区别：那个是程序回填，不发事件、不涨计数。
    /// </summary>
    internal void Pick(int index)
    {
        int clamped = Math.Clamp(index, 0, _labels.Length - 1);
        if (clamped == _selected) return;
        _selected = clamped;
        Invalidate();
        SelectionChangedRaiseCount++;
        SelectionChanged?.Invoke(clamped);
    }

    internal int SelectedIndex
    {
        get => _selected;
        set
        {
            // 第六十改：-1（未选中）也是合法值，与文档的 StitchMode.Unset 一一对应；
            // 其余照旧夹回合法档位区间。
            int clamped = value == -1 ? -1 : Math.Clamp(value, 0, _labels.Length - 1);
            if (clamped == _selected) return;
            _selected = clamped;
            Invalidate();
        }
    }

    /// <summary>当前是否有任何一档被选中（第六十改）。判据用这个而不是猜 SelectedText。</summary>
    internal bool HasSelection => _selected >= 0;

    /// <summary>供判据：当前选中档的文字（不许拿别的读数冒充选中态）；没有任何选中档时是空串。</summary>
    internal string SelectedText => _selected >= 0 ? _labels[_selected] : "";

    internal SegmentedControl(params string[] labels)
    {
        _labels = labels;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                 ControlStyles.Selectable, true);
        TabStop = true;
        Cursor = Cursors.Hand;
        Font = Theme.UiFont;
        // 第五十改回修：32 → 36。52px 顶栏里原来那枚偏矮，选中卡只有 24px 高，文字贴着上下边。
        // 第五十二改（用户图左上绿框「文字和下划线重叠」）：36 是 96 DPI 的字高账。
        // 150% DPI 下 UiFont 长到 ~24px，28px 的段格里垂直居中的字会一路压到段底那道强调条上。
        // 高度改从字体现量：96 DPI 算出来仍 ≥36（与旧值同），150% 自动长高给字让位。
        SyncHeight();
        BackColor = Theme.PanelBackground;
        Remeasure();
    }

    /// <summary>
    /// 控件高度跟着字体走（第五十二改）。段格 = Height-8，文字区还要再让出下划线那条带，
    /// 所以 Height 至少 = 字高 + 16，才能保证「文字区高度 ≥ 字高」且文字底缘压不到下划线。
    /// 96 DPI 上 UiFont.Height+16 ≤ 36，取 max 后与旧值完全一致；高 DPI 上自动长高。
    /// </summary>
    private void SyncHeight() => Height = Math.Max(36, Font.Height + 16);

    /// <summary>段格内那枚居中滑块（选中底 / 悬停底共用同一枚几何）。</summary>
    private static Rectangle ThumbRect(Rectangle seg) =>
        new(seg.X + 5, seg.Y + 3, Math.Max(0, seg.Width - 10), Math.Max(0, seg.Height - 6));

    /// <summary>
    /// 文字可用的矩形：从选中胶囊（<see cref="ThumbRect"/>）自己推导——上缘贴胶囊顶、
    /// 下缘钉在下划线顶缘之上。第五十三改（用户图红框「文字的最上方有重叠」）：
    /// 原来文字区从段格顶（Y=4）起算，垂直居中的字在 96 DPI 下顶到 Y=6、高 DPI 下 Y=5，
    /// 都越过胶囊顶缘（Y=7）探出头去；第五十二改只钉了底缘，顶缘漏了。现在文字区整块
    /// 收进胶囊内部（判据 <c>guard/分段文字顶缘不出选中胶囊</c> 钉 top ≥ thumb.Y），
    /// 区域高 = 下划线顶 − 胶囊顶 ≥ 字高由 SyncHeight 保证（Height ≥ Font.Height + 16，
    /// 区域 = Height−16 ≥ Font.Height）。
    /// </summary>
    private static Rectangle TextRect(Rectangle seg)
    {
        Rectangle thumb = ThumbRect(seg);
        Rectangle underline = UnderlineRect(thumb);
        return new Rectangle(seg.X, thumb.Y, seg.Width, Math.Max(0, underline.Y - thumb.Y));
    }

    /// <summary>选中档底缘那道强调色细条的几何（与绘制共用同一份算式）。</summary>
    private static Rectangle UnderlineRect(Rectangle thumb) =>
        new(thumb.X + Chrome.RadiusSmall, thumb.Bottom - 2,
            Math.Max(0, thumb.Width - Chrome.RadiusSmall * 2), 2);

    /// <summary>
    /// 供判据：选中档的「文字区」「选中胶囊」「下划线」三枚矩形（产品绘制用的同一份算式，不是照抄的假几何）。
    /// 判据 <c>guard/分段文字区与下划线几何不相交</c> 断言文字底缘 ≤ 下划线顶缘、
    /// 文字区高度装得下当前字体；<c>guard/分段文字顶缘不出选中胶囊</c> 断言文字顶缘 ≥ 胶囊顶缘——
    /// 这两条合起来正是「文字既不探出胶囊顶、也压不到下划线」那个缺陷的两面。
    /// </summary>
    internal (Rectangle Text, Rectangle Thumb, Rectangle Underline) SelectedGeometryForTest
    {
        get
        {
            if (_selected < 0)
                throw new InvalidOperationException("分段控件此刻没有选中档（未选模式状态）——要量几何请先把某一档选中");
            Rectangle seg = SegmentRect(_selected);
            Rectangle thumb = ThumbRect(seg);
            return (TextRect(seg), thumb, UnderlineRect(thumb));
        }
    }

    /// <summary>
    /// 量每段的宽度并缓存。刻意不用 CreateGraphics()——那会在构造期就把句柄建出来，
    /// 离屏工装里句柄建早了会踩到「字体还没生效就量几何」那一族坑（第四十七改记过）。
    /// 拿一枚 1×1 的scratch 位图量，与屏上同一套 TextRenderer 口径，不碰句柄。
    /// </summary>
    private void Remeasure()
    {
        using var scratch = new Bitmap(1, 1);
        using Graphics g = Graphics.FromImage(scratch);
        _widths = new int[_labels.Length];
        int total = Chrome.Gap * 2;
        for (int i = 0; i < _labels.Length; i++)
        {
            _widths[i] = Chrome.Measure(g, _labels[i], Font).Width + Chrome.GapWide * 2;
            total += _widths[i];
        }
        Width = total;
        Invalidate();
    }

    protected override void OnFontChanged(EventArgs e)
    {
        // 加粗版跟着当前 Font 重建（旧的释放掉，避免每次换字体漏一枚 GDI 字柄）
        Font old = _boldFont;
        _boldFont = new Font(Font, FontStyle.Bold);
        old.Dispose();
        SyncHeight();
        Remeasure();
        base.OnFontChanged(e);
    }

    private Rectangle SegmentRect(int index)
    {
        int x = Chrome.Gap;
        for (int i = 0; i < index; i++) x += _widths[i];
        return new Rectangle(x, 4, _widths[index], Height - 8);
    }

    private int HitTest(Point p)
    {
        for (int i = 0; i < _labels.Length; i++)
            if (SegmentRect(i).Contains(p)) return i;
        return -1;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        _keyboardFocusVisual = false;   // 鼠标进来的焦点不画环（蓝环只服务键盘换档）
        Focus();
        int hit = HitTest(e.Location);
        if (hit >= 0) Pick(hit);
        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int hit = HitTest(e.Location);
        if (hit != _hover) { _hover = hit; Invalidate(); }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e) { _hover = -1; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

    protected override bool IsInputKey(Keys keyData) =>
        keyData is Keys.Left or Keys.Right or Keys.Home or Keys.End || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        int next = e.KeyCode switch
        {
            Keys.Left => Math.Max(0, _selected - 1),
            Keys.Right => Math.Min(_labels.Length - 1, _selected + 1),
            Keys.Home => 0,
            Keys.End => _labels.Length - 1,
            _ => _selected,
        };
        if (next != _selected)
        {
            _keyboardFocusVisual = true;   // 真的用键盘换了档，才亮焦点环
            Pick(next);
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.PanelBackground);

        // 凹轨：整条一个圆角槽，比外壳暗一档，读作「陷进去的一组档位」。
        var track = new Rectangle(0, 0, Width - 1, Height - 1);
        var smooth = g.SmoothingMode;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using (var path = Chrome.RoundRect(track, Chrome.Radius))
        {
            using var brush = new SolidBrush(Chrome.Sunken);
            g.FillPath(brush, path);
            using var edge = new Pen(Chrome.HairlineSoft, 1f);
            g.DrawPath(edge, path);
        }

        for (int i = 0; i < _labels.Length; i++)
        {
            Rectangle seg = SegmentRect(i);
            bool selected = i == _selected;
            // 第五十一改（用户图3）：选中底原来整块填满段格（左右顶到格边），读起来「不居中」，
            // 且段宽随字数变化，点过去像「突然放大」。改成段格内缩一枚居中的滑块：左右各收 5px、
            // 上下各收 3px，三档的底都是同一枚居中小 pill，宽度差被内边距吸收，不再贴边跳变。
            Rectangle thumb = ThumbRect(seg);
            if (selected)
            {
                // 滑块：抬起的卡面 + 顶缘高光，和凹轨形成一对明暗差。
                Chrome.Card(g, thumb, Chrome.Hover, Chrome.RadiusSmall);
            }
            else if (i == _hover && Enabled)
            {
                using var path = Chrome.RoundRect(thumb, Chrome.RadiusSmall);
                using var brush = new SolidBrush(Chrome.Fade(Theme.TextPrimary, 14));
                g.FillPath(brush, path);
            }

            Color text = !Enabled ? Theme.TextDisabled
                       : selected ? Theme.TextPrimary
                       : i == _hover ? Theme.TextPrimary
                       : Theme.TextSecondary;
            // 第五十二改：文字只摆在「让出下划线」的那块矩形里（TextRect），不再占满整段格，
            // 字再高也压不到底缘那道强调条。加粗版跟当前 Font 走（见 _boldFont）。
            Chrome.Text(g, _labels[i], TextRect(seg), text, selected ? _boldFont : Font,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

            // 选中档底下一道强调色细条：蓝只出现在「当前选中」上（需求 §6）。跟着滑块宽度走。
            if (selected)
            {
                Rectangle underline = UnderlineRect(thumb);
                using var accent = new SolidBrush(Theme.Accent);
                g.FillRectangle(accent, underline);
            }
        }
        g.SmoothingMode = smooth;
        if (Focused && Enabled && _keyboardFocusVisual) Chrome.FocusRing(g, track, Chrome.Radius);
    }

    /// <summary>自建的 ToolTip 组件跟着控件一起释放（与 <see cref="ChromeButton"/> 同一条约定）。</summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tip.Dispose();
            _boldFont.Dispose();
        }
        base.Dispose(disposing);
    }
}