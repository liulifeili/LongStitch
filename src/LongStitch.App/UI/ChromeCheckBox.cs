namespace LongStitch.App.UI;

/// <summary>
/// 第五十一改（用户图黄框）：自绘勾选框。系统 CheckBox 在深色底上是方角白框 + 系统灰勾，
/// 与整套圆角卡面语言对不上（与第五十改换掉系统按钮的同一原因）。
///
/// 仍然是 <see cref="CheckBox"/> 的派生类：判据里 <c>OfType&lt;CheckBox&gt;()</c> 那一族量法、
/// 以及 <c>Checked</c>／<c>CheckedChanged</c> 的通路一个字不用改——
/// 面板回填（<c>guard/面板回填不许把画法命令再发回主窗口</c>）走的还是同一枚事件。
///
/// 颜色在 OnPaint 当下从 <see cref="Chrome"/> 现算（换肤零接线，见 Chrome 类注释的纪律）；
/// 自己的 BackColor 只放调色板认得的值。文字与勾选框一起垂直居中——这正是用户在缝列表表头
/// 抱怨「勾选框没有和文字水平对齐」的根因：系统控件的框与文字基线各自排，从来对不齐。
/// </summary>
internal sealed class ChromeCheckBox : CheckBox
{
    private bool _hover;

    /// <summary>
    /// 内容整体靠右摆（缝列表表头那枚「只看需检查」用）：
    /// Dock=Right 的控件宽度固定，内容不靠右就空出一截，看起来像没对齐。
    /// </summary>
    internal bool RightAlign { get; set; }

    internal ChromeCheckBox()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        FlatAppearance.MouseOverBackColor = Color.Transparent;
        FlatAppearance.MouseDownBackColor = Color.Transparent;
        BackColor = Theme.PanelBackground;
        ForeColor = Theme.TextPrimary;
        Font = Theme.UiFont;
        Cursor = Cursors.Hand;
        Height = 28;
        UseVisualStyleBackColor = false;
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnCheckedChanged(EventArgs e) { Invalidate(); base.OnCheckedChanged(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

    /// <summary>
    /// 用户要求 AutoSize（设置页那枚「跟随系统深浅色」）时有正确的高度与宽度：
    /// UserPaint 控件系统的默认量法不知道我们画了多大一块勾选框，这里自己报。
    /// </summary>
    public override Size GetPreferredSize(Size proposedSize)
    {
        int textWidth = TextRenderer.MeasureText(Text, Font,
            new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPrefix).Width;
        return new Size(BoxSize + Chrome.Gap + textWidth + 2, Math.Max(24, Font.Height + 8));
    }

    private const int BoxSize = 16;

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.PanelBackground);

        int textWidth = Chrome.Measure(g, Text, Font).Width;
        int total = BoxSize + Chrome.Gap + textWidth;
        int x = RightAlign ? Math.Max(0, Width - total - 8) : 0;
        int y = (Height - BoxSize) / 2;
        var box = new Rectangle(x, y, BoxSize, BoxSize);

        Color fill;
        Color text;
        if (!Enabled)
        {
            fill = Chrome.Mix(Chrome.Surface, Chrome.Base, 0.5);
            text = Theme.TextDisabled;
        }
        else if (Checked)
        {
            fill = Theme.Accent;   // 与主按钮同一条约定：强调蓝上白字，两档都不换
            text = ForeColor;
        }
        else
        {
            fill = _hover ? Chrome.Hover : Chrome.Surface;
            text = ForeColor;
        }

        var smooth = g.SmoothingMode;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using (var path = Chrome.RoundRect(box, 4))
        {
            using var brush = new SolidBrush(fill);
            g.FillPath(brush, path);
            using var pen = new Pen(Enabled && Checked ? Theme.Accent : Chrome.HairlineSoft, 1f);
            g.DrawPath(pen, path);
        }
        // 勾：一段圆头白折线（与画布上「确认拼缝」那枚 ✓ 同一形状语言）
        if (Checked)
        {
            using var check = new Pen(Enabled ? Color.White : Theme.TextDisabled, 2f)
            {
                StartCap = System.Drawing.Drawing2D.LineCap.Round,
                EndCap = System.Drawing.Drawing2D.LineCap.Round,
                LineJoin = System.Drawing.Drawing2D.LineJoin.Round,
            };
            g.DrawLines(check, new[]
            {
                new Point(box.Left + 3, box.Top + BoxSize / 2),
                new Point(box.Left + BoxSize / 2 - 1, box.Bottom - 4),
                new Point(box.Right - 3, box.Top + 4),
            });
        }
        g.SmoothingMode = smooth;

        Chrome.Text(g, Text, new Rectangle(box.Right + Chrome.Gap, 0,
                Math.Max(0, Width - box.Right - Chrome.Gap), Height),
            text, Font, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
    }
}
