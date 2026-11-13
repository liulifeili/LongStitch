using System.Drawing.Drawing2D;
using LongStitch.Core.Model;

namespace LongStitch.App.UI;

/// <summary>
/// 设置里那条「配色预览」（第四十八改）。
///
/// ═══ 它解决的是哪一个具体问题 ═══
/// 只给两枚写着"白天""黑夜"的按钮，用户点之前根本不知道点了会变成什么样：
/// 界面配色这件事，文字描述永远不如直接看一眼。所以这里把<b>当前生效的那一套</b>
/// 真实画出来——主文字、次文字、强调块、四种拼缝状态、五种底色——
/// 点一下按钮，这条带子立刻跟着翻，用户在按下"关闭"之前就已经看见结果了。
///
/// ═══ 为什么颜色一律在 OnPaint 里现问 Theme ═══
/// 构造期抄下来的颜色是<b>值</b>，换肤之后不会自己变（这正是 ThemeApplicator 要翻译控件树的原因）。
/// 这条带子干脆一个颜色字段都不存：每次绘制都问 <c>Theme.TextPrimary</c> 等属性，
/// 于是换肤后只要 Invalidate 就必然是新配色，不存在"漏翻一处"的可能。
///
/// ═══ 状态样本为什么必须带字 ═══
/// 模拟 UI 需求 §6 与无障碍要求：状态不许只靠颜色表达。所以这里四个样本用的是
/// <see cref="Theme.StatusText"/> 的原话（"✓ 自动匹配""! 需检查""! 手动待确认"）
/// 加上源文件缺失那枚"缺"字，而不是四个色块。判据量的也是这几句话有没有真的画上去。
///
/// ═══ 判据怎么量它 ═══
/// 走 <see cref="RenderForTest"/>：把绘制通路完整跑一遍画进离屏位图，再按
/// <see cref="SurfaceProbeForTest"/> / <see cref="AccentProbeForTest"/> 给的坐标读真实像素。
/// 那两块探针矩形刻意避开文字，取到的是纯色填充，所以断言可以逐字节比，不用容差。
/// </summary>
public sealed class ThemePreviewStrip : Control
{
    private const int Pad = 10;
    private const int RowGap = 6;
    private const int SurfaceGap = 4;

    /// <summary>底部那条底色轨道展示的五种面。顺序与 <see cref="SurfaceProbeForTest"/> 的下标一致。</summary>
    private static readonly string[] SurfaceNames = { "窗口", "面板", "列表", "画布", "棋盘" };

    /// <summary>四个状态样本。颜色在绘制时现问，这里只钉住"画哪四种状态"。</summary>
    private static readonly SeamStatus[] SampleStatuses =
    {
        SeamStatus.AutoPassed,
        SeamStatus.NeedsReview,
        SeamStatus.ManualPending,
    };

    /// <summary>最后一次绘制真的用上的状态色（判据的正向对照：证明绘制通路读的是新配色）。</summary>
    private readonly List<Color> _paintedStatusColors = new();

    /// <summary>最后一次绘制真的写上去的状态文字。</summary>
    private readonly List<string> _paintedStatusTexts = new();

    public ThemePreviewStrip()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.PanelBackground;
        Font = Theme.UiFont;
        ForeColor = Theme.TextPrimary;
        Width = MeasurePreferredWidth();
        Height = ComputeHeight();
    }

    // ── 判据出口 ────────────────────────────────────────────

    /// <summary>最后一次绘制用上的状态色，顺序同画面从左到右（末尾那枚是"缺"）。</summary>
    public IReadOnlyList<Color> PaintedStatusColorsForTest => _paintedStatusColors;

    /// <summary>最后一次绘制写上去的状态文字。</summary>
    public IReadOnlyList<string> PaintedStatusTextsForTest => _paintedStatusTexts;

    /// <summary>
    /// 把这条带子完整画一帧到离屏位图。判据用它读真实像素，
    /// 不用 <c>DrawToBitmap</c>——后者靠 WM_PRINT，自绘双缓冲控件在那条路上经常只给一块空白。
    /// </summary>
    public void RenderForTest(Bitmap target)
    {
        using var graphics = Graphics.FromImage(target);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        OnPaint(new PaintEventArgs(graphics, new Rectangle(0, 0, Width, Height)));
    }

    /// <summary>
    /// 第 index 块底色轨道左上角那一小片<b>纯色</b>区域（避开了居中的文字）。
    /// 判据在这里取像素，就能逐字节断言"这块面用的是新配色的那一枚"。
    /// </summary>
    public Rectangle SurfaceProbeForTest(int index)
    {
        Rectangle[] rail = BuildLayout().Surfaces;
        Rectangle cell = rail[Math.Clamp(index, 0, rail.Length - 1)];
        return new Rectangle(cell.Left + 3, cell.Top + 3, 4, 4);
    }

    /// <summary>强调块（"导出"那枚）左上角的纯色探针，同样避开文字。</summary>
    public Rectangle AccentProbeForTest
    {
        get
        {
            Rectangle chip = BuildLayout().AccentChip;
            return new Rectangle(chip.Left + 3, chip.Top + 3, 4, 4);
        }
    }

    /// <summary>整条带子的背景探针（左上角空白处）。</summary>
    public Rectangle BackgroundProbeForTest => new(2, 2, 4, 4);

    /// <summary>
    /// 按当前 DPI 量出来的应有宽度：取"最宽的那一行不折行"所需宽度。
    /// 调用方（ThemeChooserPanel）拿它参与算整块的宽度，所以这里绝不能写死 96 DPI 的数。
    /// </summary>
    public static int MeasurePreferredWidth()
    {
        int swatch = SwatchSize;
        int statusRow = 0;
        foreach (SeamStatus status in SampleStatuses)
            statusRow += swatch + 5 + TextRenderer.MeasureText(Theme.StatusText(status), Theme.UiFont).Width + 14;
        statusRow += swatch + 5 + TextRenderer.MeasureText(MissingText, Theme.UiFont).Width;

        int chipRow = AccentChipWidth() + 14 +
                      TextRenderer.MeasureText(DisabledText, Theme.UiFont).Width;

        int labelWidth = 0;
        foreach (string name in SurfaceNames)
            labelWidth = Math.Max(labelWidth, TextRenderer.MeasureText(name, Theme.UiFontSmall).Width);
        int railRow = SurfaceNames.Length * (labelWidth + 18) + (SurfaceNames.Length - 1) * SurfaceGap;

        int widest = Math.Max(
            Math.Max(TextRenderer.MeasureText(HeaderText, Theme.UiFontSmall).Width,
                     TextRenderer.MeasureText(PrimaryText, Theme.UiFont).Width),
            Math.Max(Math.Max(TextRenderer.MeasureText(SecondaryText, Theme.UiFontSmall).Width, statusRow),
                     Math.Max(chipRow, railRow)));
        return widest + Pad * 2;
    }

    private const string HeaderText = "配色预览（跟着当前档实时变）";
    private const string PrimaryText = "长截图拼合 · 3 条拼缝";
    private const string SecondaryText = "拼缝 2→3 · 偏移 +12 px · 已手动确认";
    private const string DisabledText = "（不可用的文字长这样）";
    private const string MissingText = "缺";
    private const string AccentText = "导出";

    private static int SwatchSize => Math.Max(10, Theme.UiFont.Height - 4);

    private static int AccentChipWidth() =>
        TextRenderer.MeasureText(AccentText, Theme.UiFontBold).Width + 24;

    /// <summary>高度只由字体行高决定（没有折行），所以与宽度无关，可以在构造期一次算准。</summary>
    private static int ComputeHeight()
    {
        int small = Theme.UiFontSmall.Height;
        int normal = Theme.UiFont.Height;
        int chip = normal + 10;
        int rail = small + 14;
        return Pad + small + RowGap + normal + 2 + small + RowGap +
               Math.Max(normal, SwatchSize) + RowGap + chip + RowGap + rail + Pad;
    }

    /// <summary>一次绘制用到的全部几何。颜色不进这里——颜色必须绘制时现问，见类注释。</summary>
    private readonly record struct StripLayout(
        Rectangle Header,
        Rectangle Primary,
        Rectangle Secondary,
        Rectangle StatusRow,
        Rectangle AccentChip,
        Rectangle Disabled,
        Rectangle[] Surfaces);

    private StripLayout BuildLayout()
    {
        int width = Math.Max(Width, MeasurePreferredWidth());
        int inner = width - Pad * 2;
        int small = Theme.UiFontSmall.Height;
        int normal = Theme.UiFont.Height;
        int y = Pad;

        var header = new Rectangle(Pad, y, inner, small);
        y += small + RowGap;
        var primary = new Rectangle(Pad, y, inner, normal);
        y += normal + 2;
        var secondary = new Rectangle(Pad, y, inner, small);
        y += small + RowGap;

        int statusHeight = Math.Max(normal, SwatchSize);
        var statusRow = new Rectangle(Pad, y, inner, statusHeight);
        y += statusHeight + RowGap;

        int chipHeight = normal + 10;
        int chipWidth = Math.Min(AccentChipWidth(), inner);
        var chip = new Rectangle(Pad, y, chipWidth, chipHeight);
        var disabled = new Rectangle(chip.Right + 14, y, Math.Max(0, inner - chipWidth - 14), chipHeight);
        y += chipHeight + RowGap;

        int railHeight = small + 14;
        int cellWidth = (inner - (SurfaceNames.Length - 1) * SurfaceGap) / SurfaceNames.Length;
        var surfaces = new Rectangle[SurfaceNames.Length];
        for (int i = 0; i < surfaces.Length; i++)
            surfaces[i] = new Rectangle(Pad + i * (cellWidth + SurfaceGap), y, cellWidth, railHeight);

        return new StripLayout(header, primary, secondary, statusRow, chip, disabled, surfaces);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Graphics g = e.Graphics;
        StripLayout layout = BuildLayout();

        // 背景现问 Theme：换肤后这一笔就是新档的面板色，探针读到的也就是新值。
        using (var background = new SolidBrush(Theme.PanelBackground))
            g.FillRectangle(background, 0, 0, Width, Height);

        DrawLine(g, HeaderText, Theme.UiFontSmall, layout.Header, Theme.TextSecondary, Theme.PanelBackground);
        DrawLine(g, PrimaryText, Theme.UiFont, layout.Primary, Theme.TextPrimary, Theme.PanelBackground);
        DrawLine(g, SecondaryText, Theme.UiFontSmall, layout.Secondary, Theme.TextSecondary, Theme.PanelBackground);

        // ── 四个状态样本：色块 + 原话文字（不许只有色块，理由见类注释）──
        _paintedStatusColors.Clear();
        _paintedStatusTexts.Clear();
        int swatch = SwatchSize;
        int x = layout.StatusRow.Left;
        foreach (SeamStatus status in SampleStatuses)
            x = DrawStatusSample(g, layout.StatusRow, x, swatch, Theme.StatusColor(status), Theme.StatusText(status));
        DrawStatusSample(g, layout.StatusRow, x, swatch, Theme.Danger, MissingText);

        // ── 强调块：两档配色下都是"这一块可以点"的唯一蓝 ──
        using (var accent = new SolidBrush(Theme.Accent))
            g.FillRectangle(accent, layout.AccentChip);
        DrawLine(g, AccentText, Theme.UiFontBold, layout.AccentChip, Color.White, Theme.Accent, center: true);
        DrawLine(g, DisabledText, Theme.UiFont, layout.Disabled, Theme.TextDisabled, Theme.PanelBackground);

        // ── 底色轨道：五种面各画一块，让用户一眼看出"窗口/面板/列表/画布/棋盘"分别是哪一枚 ──
        Color[] surfaces =
        {
            Theme.WindowBackground, Theme.PanelBackground, Theme.ListBackground,
            Theme.CanvasBackground, Theme.CheckerLight,
        };
        using var border = new Pen(Theme.PanelBorder, 1f);
        for (int i = 0; i < layout.Surfaces.Length; i++)
        {
            Rectangle cell = layout.Surfaces[i];
            using (var fill = new SolidBrush(surfaces[i]))
                g.FillRectangle(fill, cell);
            // 棋盘那一格再补一条深色斜带，否则它与"列表"在浅色档下几乎分不出来。
            if (i == 4)
            {
                using var checker = new SolidBrush(Theme.CheckerDark);
                g.FillRectangle(checker, cell.Left, cell.Bottom - 6, cell.Width, 6);
            }
            g.DrawRectangle(border, cell.Left, cell.Top, cell.Width - 1, cell.Height - 1);
            var label = new Rectangle(cell.Left, cell.Top, cell.Width, cell.Height - (i == 4 ? 6 : 0));
            DrawLine(g, SurfaceNames[i], Theme.UiFontSmall, label, Theme.TextPrimary, surfaces[i], center: true);
        }

        // 外框：这条带子嵌在面板里，没有框就看不出它的边界在哪。
        g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
    }

    /// <summary>画一枚"色块 + 文字"的状态样本，返回下一枚的起始 x。</summary>
    private int DrawStatusSample(Graphics g, Rectangle row, int x, int swatch, Color color, string text)
    {
        _paintedStatusColors.Add(color);
        _paintedStatusTexts.Add(text);

        var box = new Rectangle(x, row.Top + (row.Height - swatch) / 2, swatch, swatch);
        using (var fill = new SolidBrush(color))
            g.FillRectangle(fill, box);
        int textWidth = TextRenderer.MeasureText(text, Theme.UiFont).Width;
        var label = new Rectangle(box.Right + 5, row.Top, textWidth, row.Height);
        DrawLine(g, text, Theme.UiFont, label, color, Theme.PanelBackground);
        return label.Right + 14;
    }

    /// <summary>
    /// 统一走 TextRenderer（GDI）而不是 Graphics.DrawString：控件上的文字全是 GDI 排的，
    /// 两者字距不同，混用会出现"预览里的字比界面上的字宽一截"这种对不上的观感。
    /// 显式传背景色，免得 GDI 拿控件 BackColor 去填底、把色块上的字糊掉。
    /// </summary>
    private static void DrawLine(Graphics g, string text, Font font, Rectangle bounds,
                                 Color color, Color back, bool center = false)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        TextFormatFlags flags = TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding |
                                TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis |
                                (center ? TextFormatFlags.HorizontalCenter : TextFormatFlags.Left);
        TextRenderer.DrawText(g, text, font, bounds, color, back, flags);
    }

    /// <summary>
    /// 高度跟着字体走（DPI 变了字体行高就变）。带变化闸，免得 OnLayout 里改 Height 又触发 OnLayout。
    /// </summary>
    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        int want = ComputeHeight();
        if (want > 0 && want != Height) Height = want;
    }
}