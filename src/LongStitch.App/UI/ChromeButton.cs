namespace LongStitch.App.UI;

/// <summary>按钮的视觉档位（第五十改）。</summary>
internal enum ChromeButtonKind
{
    /// <summary>主操作：强调蓝实底 + 白字。一屏只该有一枚（导出）。</summary>
    Primary,

    /// <summary>次操作：抬起的卡面 + 正文色 + 发丝边（添加图片、对话框里的确认）。</summary>
    Secondary,

    /// <summary>弱操作：平时只有文字，悬停才浮出一块 wash（取消、恢复自动）。</summary>
    Ghost,
}

/// <summary>
/// 第五十改：自绘按钮。系统 Button 在深色底上无论怎么调 FlatAppearance 都留着
/// 一圈系统灰边与直角，和整套圆角卡面语言对不上；这里把四态（常态／悬停／按下／禁用）
/// 与焦点环全部自己画，颜色在 OnPaint 当下从 <see cref="Chrome"/> 现算，所以换肤零接线。
///
/// 仍然是 <see cref="Button"/> 的派生类：PerformClick、DialogResult、CancelButton 语义、
/// 以及判据里「数 ButtonBase 标题」那一族量法一个字都不用改。
/// </summary>
internal sealed class ChromeButton : Button
{
    private bool _hover;
    private bool _pressed;
    private readonly ToolTip _tip = new();

    /// <summary>
    /// 悬停提示。<see cref="Control"/> 本身没有这个属性（那是 ToolStripItem 的），
    /// 而需求 §6 要求「不熟悉的图标均有悬停提示」，所以这里自带一枚 ToolTip 组件补上，
    /// 写法与 ToolStripItem 一致，调用处不必为两种按钮记两套 API。
    /// </summary>
    internal string ToolTipText
    {
        get => _tip.GetToolTip(this) ?? "";
        set => _tip.SetToolTip(this, value);
    }

    /// <summary>视觉档位。改它只重画，不改行为。</summary>
    internal ChromeButtonKind Kind { get; set; } = ChromeButtonKind.Secondary;

    /// <summary>文字左边那枚图标字形；null 表示纯文字按钮。</summary>
    internal char? Glyph { get; set; }

    internal ChromeButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        FlatAppearance.MouseOverBackColor = Color.Transparent;
        FlatAppearance.MouseDownBackColor = Color.Transparent;
        Height = 38;
        MinimumSize = new Size(72, Theme.MinTouchTarget);
        Font = Theme.UiFontBold;
        Cursor = Cursors.Hand;
        BackColor = Theme.PanelBackground;   // 只放调色板认得的值；自绘时整块盖掉（见 Chrome 类注释的纪律）
        ForeColor = Theme.TextPrimary;
    }

    /// <summary>
    /// 那枚 ToolTip 是自建的组件（不是窗体设计器托管的），必须跟着按钮一起释放，
    /// 否则每关一次对话框就漏一枚隐藏的原生提示窗口。
    /// </summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing) _tip.Dispose();
        base.Dispose(disposing);
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; _pressed = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left) { _pressed = true; Invalidate(); }
        base.OnMouseDown(e);
    }
    protected override void OnMouseUp(MouseEventArgs e) { _pressed = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        g.Clear(Parent?.BackColor ?? Theme.PanelBackground);

        Color fill;
        Color text;
        if (!Enabled)
        {
            fill = Kind == ChromeButtonKind.Ghost ? Color.Transparent : Chrome.Mix(Chrome.Surface, Chrome.Base, 0.5);
            text = Theme.TextDisabled;
        }
        else if (Kind == ChromeButtonKind.Primary)
        {
            fill = _pressed ? Chrome.Mix(Theme.Accent, Color.Black, 0.18)
                 : _hover ? Chrome.Mix(Theme.Accent, Color.White, 0.10)
                 : Theme.Accent;
            text = Color.White;   // 写死的白字压强调蓝：两档都不换（与左栏选中行同一条约定）
        }
        else if (Kind == ChromeButtonKind.Ghost)
        {
            fill = _pressed ? Chrome.Pressed : _hover ? Chrome.Hover : Color.Transparent;
            text = _hover ? Theme.TextPrimary : Theme.TextSecondary;
        }
        else
        {
            fill = _pressed ? Chrome.Pressed : _hover ? Chrome.Hover : Chrome.Surface;
            text = Theme.TextPrimary;
        }

        if (fill.A > 0)
        {
            if (Kind == ChromeButtonKind.Ghost && !Enabled)
            {
                // 禁用的弱按钮不画底，只留灰字
            }
            else if (Kind == ChromeButtonKind.Secondary || Kind == ChromeButtonKind.Primary)
            {
                Chrome.Card(g, rect, fill, Chrome.Radius, bevel: false);
            }
            else
            {
                var smooth = g.SmoothingMode;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using var path = Chrome.RoundRect(rect, Chrome.Radius);
                using var brush = new SolidBrush(fill);
                g.FillPath(brush, path);
                g.SmoothingMode = smooth;
            }
        }

        // 字形 + 文字一起居中：先量总宽，再摆，避免「图标贴左、文字居中」那种错位。
        int glyphWidth = Glyph.HasValue && Chrome.HasIconFont ? 20 : 0;
        Size textSize = Chrome.Measure(g, Text, Font);
        int total = glyphWidth + textSize.Width;
        int x = Math.Max(Chrome.Gap, (Width - total) / 2);
        var textRect = new Rectangle(x + glyphWidth, 0, textSize.Width + 2, Height);
        if (Glyph.HasValue && Chrome.HasIconFont)
            Chrome.Glyph(g, Glyph.Value, new Rectangle(x, 0, glyphWidth - 4, Height), text);
        Chrome.Text(g, Text, textRect, text, Font,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

        if (Focused && Enabled) Chrome.FocusRing(g, rect, Chrome.Radius);
    }
}