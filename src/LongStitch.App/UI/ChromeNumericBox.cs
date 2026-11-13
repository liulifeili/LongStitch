namespace LongStitch.App.UI;

/// <summary>
/// 第五十一改（用户图黄框）：自绘外观的数字框。系统 NumericUpDown 是 FixedSingle 直角灰边 +
/// 一对系统上下小箭头，深色底上与整套圆角卡面语言对不上；这里把边框与箭头全部自己画——
/// 圆角发丝边 + 右侧两枚自绘小箭头（上半按＝加、下半按＝减），中间的输入区仍是
/// NumericUpDown 自带的编辑框，所以键盘输入、滚轮、上下限、ValueChanged 全部照旧。
///
/// 仍然是 <see cref="NumericUpDown"/> 的派生类：判据里 <c>OfType&lt;NumericUpDown&gt;()</c>
/// 那一族（如 <c>ui/长截图面板锁横向：X 格禁用、档位显示仅上下、硬填会被拦且回话</c>）
/// 量法一个字不用改。
/// 颜色在 OnPaint 当下从 <see cref="Chrome"/> 现算（换肤零接线）；自己的 BackColor
/// 与内部编辑框的 BackColor 只放调色板认得的值。
/// </summary>
internal sealed class ChromeNumericBox : NumericUpDown
{
    private bool _hover;
    private int _hoverHalf;   // 0 = 无；1 = 上半（加）；2 = 下半（减）
    private TextBox? _edit;

    /// <summary>右侧箭头条的宽度；输入区给它让位。判据也用它核对字形落在点击竖带内。</summary>
    internal const int SpinStrip = 20;

    internal ChromeNumericBox()
    {
        BorderStyle = BorderStyle.None;   // 系统直角边撤掉，圆角边自己画
        BackColor = Theme.WindowBackground;
        ForeColor = Theme.TextPrimary;
        Font = Theme.MonoFont;
        TextAlign = HorizontalAlignment.Left;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        // NumericUpDown 内部固定有两个子控件：编辑框（TextBox 派生）与系统上下箭头。
        // 箭头藏掉（外观太旧），编辑框换上调色板底色并与外框同色，看起来才是「一块」。
        foreach (Control child in Controls)
        {
            if (child is TextBox box)
            {
                _edit = box;
                box.BackColor = Theme.WindowBackground;
                box.ForeColor = Theme.TextPrimary;
                box.BorderStyle = BorderStyle.None;
            }
            else
            {
                child.Visible = false;
            }
        }
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }

    /// <summary>
    /// 第五十三改：悬停在哪一半的箭头上，就把那一半点亮（与 <see cref="ChromeButton"/> 的
    /// hover wash 同一条语言）。鼠标一离开整条箭头区就两半都熄灭。
    /// </summary>
    protected override void OnMouseMove(MouseEventArgs e)
    {
        int half = e.Y < Height / 2 ? 1 : 2;
        if (_hoverHalf != half)
        {
            _hoverHalf = half;
            Invalidate();
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hover = false;
        _hoverHalf = 0;
        Invalidate();
        base.OnMouseLeave(e);
    }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

    /// <summary>
    /// 系统会在尺寸变化时把内部编辑框摆成「几乎整个客户区」，把自画的边框盖掉；
    /// 所以每次布局之后自己再摆一遍：左边留字距，右边给箭头条让位。
    /// </summary>
    protected override void OnResize(EventArgs eventargs)
    {
        base.OnResize(eventargs);
        PositionEdit();
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        PositionEdit();
    }

    private void PositionEdit()
    {
        if (_edit is null) return;
        _edit.Bounds = new Rectangle(8, 3, Math.Max(10, Width - 8 - SpinStrip - 4), Math.Max(10, Height - 6));
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        // 右侧箭头条：上半＝加、下半＝减（UpButton/DownButton 是公开方法，走的就是
        // 系统箭头原来那条通路——上下限、Hexadecimal、Increment 全部照旧生效）
        if (e.Button == MouseButtons.Left && Enabled && e.X >= Width - SpinStrip)
        {
            if (e.Y < Height / 2) UpButton();
            else DownButton();
            return;
        }
        base.OnMouseDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.Clear(BackColor);

        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        var smooth = g.SmoothingMode;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using (var path = Chrome.RoundRect(rect, Math.Min(Chrome.RadiusSmall, rect.Height / 2)))
        using (var pen = new Pen(!Enabled ? Chrome.HairlineSoft : _hover ? Chrome.Hover : Chrome.HairlineSoft, 1f))
            g.DrawPath(pen, path);

        // ── 箭头条（第五十三改，用户图蓝框）────────────────────────
        // 病：原来下半那枚的点序写成了 (cy+8),(cy+4),(cy+8)——顶点反而高于两臂，画出来还是一枚 ^，
        //     屏上「下箭头被标注成了上箭头」。正的 v 必须顶点低于两臂：(cy+4),(cy+8),(cy+4)。
        // 形：两枚 chevron 各自落在自己那半点击区的正中（上半中点 Height/4、下半中点 3·Height/4），
        //     点哪儿亮哪儿（_hoverHalf），中间一道发丝分隔线把两半点区分开——与整套圆角卡面语言对齐。
        int stripLeft = Width - SpinStrip;
        if (_hover && Enabled && _hoverHalf != 0)
        {
            var half = new Rectangle(stripLeft, _hoverHalf == 1 ? 1 : Height / 2,
                SpinStrip - 1, Height / 2 - 1);
            using var wash = new SolidBrush(Chrome.Hover);
            g.FillRectangle(wash, half);
        }
        using (var hairline = new Pen(Chrome.HairlineSoft, 1f))
            g.DrawLine(hairline, stripLeft, Math.Max(2, Chrome.RadiusSmall), stripLeft, Height - 1 - Math.Max(2, Chrome.RadiusSmall));

        Color glyphColor = Enabled ? (_hoverHalf != 0 ? Theme.TextPrimary : Theme.TextSecondary) : Theme.TextDisabled;
        using (var glyph = new Pen(glyphColor, 2f)
               {
                   StartCap = System.Drawing.Drawing2D.LineCap.Round,
                   EndCap = System.Drawing.Drawing2D.LineCap.Round,
                   LineJoin = System.Drawing.Drawing2D.LineJoin.Round,
               })
        {
            int cx = stripLeft + SpinStrip / 2;
            int upY = Height / 4;           // 上半点击区的正中
            int downY = Height * 3 / 4;     // 下半点击区的正中
            g.DrawLines(glyph, UpGlyphPoints(cx, upY));
            g.DrawLines(glyph, DownGlyphPoints(cx, downY));
        }
        g.SmoothingMode = smooth;
    }

    /// <summary>上半那枚 chevron 的三点折线：顶点（中点）高于两臂，读作 ^。</summary>
    internal static Point[] UpGlyphPoints(int cx, int cy) =>
        new[] { new Point(cx - 4, cy + 2), new Point(cx, cy - 2), new Point(cx + 4, cy + 2) };

    /// <summary>
    /// 下半那枚 chevron 的三点折线：顶点（中点）必须<b>低于</b>两臂，读作 v。
    /// 第五十三改之前这里点序写反（顶点高于两臂），屏上「下箭头被标注成了上箭头」。
    /// OnPaint 与判据钩子共用这一份算式，判据见 <c>guard/数字框下箭头点序必须朝下</c>。
    /// </summary>
    internal static Point[] DownGlyphPoints(int cx, int cy) =>
        new[] { new Point(cx - 4, cy - 2), new Point(cx, cy + 2), new Point(cx + 4, cy - 2) };

    /// <summary>供判据：两枚箭头此刻的完整点序（与 OnPaint 用的同一份算式）。</summary>
    internal (Point[] Up, Point[] Down) SpinGlyphsForTest
    {
        get
        {
            int cx = Width - SpinStrip / 2;
            return (UpGlyphPoints(cx, Height / 4), DownGlyphPoints(cx, Height * 3 / 4));
        }
    }
}
