namespace LongStitch.App.UI;

// 分组标题和下缘细线按当前主题绘制，避免换肤时保留派生旧色。
internal sealed class SectionHeader : Control
{
    internal SectionHeader(string text)
    {
        Text = text;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false);
        TabStop = false;
        Dock = DockStyle.Top;
        Height = Theme.UiFontBold.Height + 20;
        Margin = new Padding(0, Chrome.GapWide, 0, Chrome.Gap);
        Font = Theme.UiFontBold;
        BackColor = Theme.PanelBackground;
        ForeColor = Theme.TextPrimary;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.PanelBackground);
        Chrome.Text(g, Text, new Rectangle(0, 0, Width, Height - 8),
            Theme.TextPrimary, Font,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        Chrome.Rule(g, 0, Width - 1, Height - 1, Chrome.HairlineSoft);
    }
}