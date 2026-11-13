namespace LongStitch.App.UI;

/// <summary>
/// 画布工具条右端那枚「当前在干什么」徽标（第五十改）。
///
/// ═══ 为什么要有它 ═══
/// 模拟UI需求 §7 的验收第一条是「用户能一眼分辨当前在浏览画布还是编辑拼缝」。
/// 原来这件事只写在<b>最底下</b>的状态栏里：用户眼睛盯着画布中央那条蓝线，
/// 要判断自己在哪个态就得把视线挪到屏幕最下沿，读一行 8pt 灰字。
/// 现在把它搬到画布正上方、与缩放按钮同一条带的右端，并且两态<b>形状也不同</b>：
/// 浏览＝凹下去的灰槽 + 眼睛图标；编辑＝强调色薄 wash + 铅笔图标 + 左侧一道强调色竖条。
/// 颜色之外还有图标与文字，符合需求 §6「状态不能只靠颜色表达」。
///
/// ═══ 它只读不写 ═══
/// 这枚控件不接任何鼠标事件（不可点），也不写任何参数：它只是 <c>UpdateSizeInfo</c>
/// 那一句读数的第二个出口。真正的唯一写入出口仍是画布的 <c>SelectSeam</c>。
///
/// ═══ 换肤 ═══
/// 底色与字色全部在 OnPaint 当下从 <see cref="Chrome"/> 现算（见 Chrome 类注释那条纪律），
/// 所以换肤只需要 Invalidate 一次，不需要翻译表认得任何新颜色。
/// </summary>
internal sealed class ModeBadge : Control
{
    private bool _editing;
    private string _label = "浏览画布";

    public ModeBadge()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        // 不可点：它是一句读数，不是按钮。Tab 也不该停在这里。
        SetStyle(ControlStyles.Selectable, false);
        TabStop = false;
        Height = 30;
        Font = Theme.UiFontBold;
        BackColor = Theme.PanelBackground;
        Theme.Changed += OnThemeChanged;
        // 槽位宽度一次量定、之后不动：量的是<b>最长可能的那句</b>（四位数序号），
        // 而不是当前那句。否则每选中一条拼缝徽标就变宽一次，整条工具条右端跟着重排。
        int widest = TextRenderer.MeasureText("编辑拼缝 999→1000", Font, new Size(int.MaxValue, int.MaxValue),
            TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine).Width;
        // 左内边距 12 + 图标 14 + 缝 8 + 文字 + 右内边距 12
        Width = Math.Max(150, widest + 46);
    }

    /// <summary>
    /// 交进当前状态。<paramref name="editing"/> 为真时画编辑态。
    /// 同一个值重复交进来不重画——这个方法挂在 <c>RefreshChrome</c> 上，
    /// 而 RefreshChrome 每次参数改动都会走一遍。
    /// </summary>
    public void Set(bool editing, string label)
    {
        if (_editing == editing && _label == label) return;
        _editing = editing;
        _label = label;
        Invalidate();
    }

    private void OnThemeChanged(ThemePalette old, ThemePalette current) => Invalidate();

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        // 控件自己的 BackColor 只是「翻译表认得的那一枚」，真正看到的底由下面整块盖掉。
        using (var clear = new SolidBrush(Parent?.BackColor ?? BackColor))
            g.FillRectangle(clear, ClientRectangle);

        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        Color fill = _editing ? Chrome.AccentWash : Chrome.Sunken;
        Color edge = _editing ? Chrome.Fade(Theme.Accent, 150) : Chrome.HairlineSoft;
        Color ink = _editing ? Theme.TextPrimary : Theme.TextSecondary;

        var smooth = g.SmoothingMode;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using (var path = Chrome.RoundRect(rect, Chrome.RadiusSmall))
        {
            using var brush = new SolidBrush(fill);
            g.FillPath(brush, path);
            using var pen = new Pen(edge, 1f);
            g.DrawPath(pen, path);
        }
        g.SmoothingMode = smooth;

        // 编辑态在左缘立一道强调色竖条：即使把界面截成灰度图，两态也分得出来。
        if (_editing)
        {
            using var bar = new SolidBrush(Theme.Accent);
            g.FillRectangle(bar, rect.X + 4, rect.Y + 7, 2, rect.Height - 14);
        }

        Chrome.Glyph(g, _editing ? ChromeGlyph.Pencil : ChromeGlyph.Eye,
            new Rectangle(12, 0, 14, Height), _editing ? Theme.Accent : ink);
        Chrome.Text(g, _label, new Rectangle(34, 0, Width - 46, Height), ink, Font);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) Theme.Changed -= OnThemeChanged;
        base.Dispose(disposing);
    }
}