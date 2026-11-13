namespace LongStitch.App.UI;

/// <summary>
/// 第五十改：会按<b>自己当前宽度</b>重算高度的说明段落。
///
/// 为什么不能用一枚普通 Label：面板是 Dock=Top 叠行的，行高必须在加入容器那一刻定下来，
/// 而 ToolPanel 构造时还没被塞进右栏，ClientSize 是 UserControl 的默认宽度（一百多像素）。
/// 拿那个宽度去量折行，一段一百多字的说明会被量成四五百像素高；等真正停进 320px 的右栏，
/// 文字只占上面一百多像素，下面三百多像素是一片空腔——屏上就是「说明在上、分组标题掉到屏幕底下」。
///
/// 这里把量法挪到 <see cref="OnSizeChanged"/>：停靠布局把宽度定成容器宽之后会触发一次，
/// 那时量出来的折行数才是屏上真正会画出来的行数。改高度又会触发一次，所以必须判等再写，
/// 否则就是无限递归（栈溢出在真机上表现为窗口一打开就闪退）。
/// </summary>
internal sealed class NoteLabel : Label
{
    internal NoteLabel(string text)
    {
        Text = text;
        Dock = DockStyle.Top;
        AutoSize = false;
        Font = Theme.UiFontSmall;
        ForeColor = Theme.TextSecondary;
        BackColor = Theme.PanelBackground;
        // 第五十一改（用户图红框）：候选行（「重叠 1249px …」）就是一枚枚 NoteLabel，
        // 原来上下边距 2/6 挤成一片；放开到 4/8 让每行之间有呼吸
        Margin = new Padding(0, 4, 0, 8);
        Height = 40;
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        int wide = Math.Max(40, Width);
        Size measured = TextRenderer.MeasureText(Text, Font, new Size(wide, int.MaxValue),
            TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
        int wanted = Math.Max(20, measured.Height + 6);
        if (Height != wanted) Height = wanted;
    }
}