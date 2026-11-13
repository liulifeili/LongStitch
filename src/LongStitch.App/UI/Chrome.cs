using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace LongStitch.App.UI;

/// <summary>
/// 第五十改：整套界面的「工艺语言」——明暗阶、圆角、发丝线、图标字形、焦点环。
///
/// ═══ 为什么颜色是<b>算出来的</b>而不是调色板里新增的键 ═══
/// 换肤靠 <see cref="ThemePalette.BuildRemapTo"/> 那张「旧档 ARGB → 新档 ARGB」翻译表，
/// 而翻译表只认 <see cref="ThemePalette.Enumerate"/> 交出来的那 19 枚具名色。
/// 往调色板里加键会同时动三件事：两档颜色集合可能撞键（翻第二遍把已翻好的再翻一次）、
/// <c>guard/纯白不许进调色板否则写死的白字会被翻掉</c> 逐枚数的名单变长、
/// 对比度判据要重新挑配对。这一改不想碰那套已经验过的不变量。
///
/// 所以这里的每一枚颜色都是<b>绘制当下</b>从 <see cref="Theme.Current"/> 现算的：
/// 换肤之后下一次 OnPaint 自然就是新档的值，<b>一个翻译表条目都不需要</b>。
/// 代价是一条纪律：<b>这些算出来的颜色只许进 OnPaint，不许赋给控件的 BackColor/ForeColor</b>。
/// 控件的 BackColor 仍然只放调色板里那 19 枚（翻译表认得），自绘时整块盖掉它。
/// 违反这条纪律的症状是「换到黑夜之后某一块停在白天的灰」——因为翻译表认不得算出来的值。
///
/// ═══ 明暗阶的方向在两档里是反的，这是对的 ═══
/// 黑夜档「抬起」＝更亮（面板 38 → 悬停 58）；白天档「抬起」＝更暗一点点的灰影
/// （面板 252 → 悬停 236），因为近白的表面再亮也亮不到哪去，白底上的层级靠阴影与发丝线表达。
/// 统一写成「朝 <see cref="ThemePalette.TextPrimary"/> 混一档」就自动得到这个反向：
/// 黑夜的文字色近白、白天的文字色近黑，混过去正好是各自该走的方向。
///
/// ═══ 蓝色仍然只用于选中与可交互拼缝（需求 §6）═══
/// 这一改没有引入任何新色相。<see cref="AccentWash"/> 是强调蓝混进面板底的一层薄 wash，
/// 只画在「选中行／勾选态」上，正是需求允许蓝出现的那两处；其余层级全是同族中性灰。
/// </summary>
internal static class Chrome
{
    // ── 节奏常量（整数像素，红线一：几何不许出现浮点）─────────────
    /// <summary>控件之间的小缝。</summary>
    internal const int Gap = 8;

    /// <summary>组与组之间的缝。</summary>
    internal const int GapWide = 14;

    /// <summary>面板内容离面板边的距离。</summary>
    internal const int PadPanel = 20;

    /// <summary>卡片／按钮的圆角半径。</summary>
    internal const int Radius = 10;

    /// <summary>小 chip／输入格的圆角半径。</summary>
    internal const int RadiusSmall = 5;

    /// <summary>顶栏高度：菜单、模式分段、项目名、导出共用这一条带（需求 §2 线框就是一行）。</summary>
    internal const int TopBarHeight = 64;

    /// <summary>画布上方那一排的高度。</summary>
    internal const int CanvasBarHeight = 56;

    /// <summary>底部状态栏高度：五枚工具 chip 要站得下 36px（需求 §6 主要点击目标 ≥ 36×36）。</summary>
    internal const int StatusBarHeight = 46;

    /// <summary>
    /// 列表行：素材行与拼缝行各自的高度。
    ///
    /// ═══ 为什么是按字体现量而不是写死的 68 / 40 ═══
    /// 这两行里画的全部是文字（文件名、尺寸、序号、状态、摘要），而字体是磅值——
    /// 在 150% DPI 的屏幕上 <c>Font.Height</c> 会按 1.5 倍长出来（9pt ≈ 24px），
    /// 写死的像素盒装不下整枚字框，下缘必然被裁（用户图1/图2 的「文字下方被挤压」就是它）。
    /// 所以行高改成「画这个行的时候真正用的那几个字体的高度」加呼吸边推出来：
    /// 96 DPI 上算出来与旧常量完全同值（68/40 一带），150% DPI 上自动放大，
    /// 不需要任何 DPI 分支。<see cref="SourceRowHeight"/> 的算术与
    /// SourcePanel.OnDrawItem 的盒子摆放是同一段账的两半（那里从这里取行高）；
    /// <see cref="SeamRowHeight"/> 与 MainForm.OnDrawSeamItem 同理。
    /// </summary>
    internal static int SourceRowHeight
    {
        get
        {
            // 文本块：文件名一行（UiFontSmall）+ 2px 缝 + 尺寸一行（UiFontMicro）
            int textH = SourceRowTitleHeight + 2 + SourceRowSizeHeight;
            // 卡内上下呼吸 10+10；卡外上下各 4（OnDrawItem 的 card = bounds 内缩 8）。
            // 60 是缩略图（52px 高）要求的最低卡高：再矮缩略图就贴边了。
            int cardH = Math.Max(60, textH + 20);
            return cardH + 8;
        }
    }

    /// <summary>素材行第一行（文件名）的文本盒高：字体高加 4px 余量。</summary>
    internal static int SourceRowTitleHeight => Theme.UiFontSmall.Height + 4;

    /// <summary>素材行第二行（尺寸）的文本盒高：字体高加 2px 余量。</summary>
    internal static int SourceRowSizeHeight => Theme.UiFontMicro.Height + 2;

    /// <summary>拼缝行高：序号行 + 摘要行加缝与呼吸推出来（算术与 OnDrawSeamItem 同账）。</summary>
    internal static int SeamRowHeight
    {
        get
        {
            // 卡内：顶 3 + 序号行 + 缝 2 + 摘要行 + 底 4；卡外上下各 3（card = bounds 内缩 6）
            return SeamRowTopHeight + 2 + SeamRowSummaryHeight + 3 + 4 + 6;
        }
    }

    /// <summary>拼缝行第一行（序号 + 状态）的文本盒高。</summary>
    internal static int SeamRowTopHeight => Math.Max(Theme.MonoFont.Height, Theme.UiFontMicro.Height) + 3;

    /// <summary>拼缝行第二行（问题摘要）的文本盒高。</summary>
    internal static int SeamRowSummaryHeight => Theme.UiFontMicro.Height + 2;

    // ── 明暗阶（绘制当下现算，理由见类注释）───────────────────────

    /// <summary>最外壳的底（窗体背景那一档）。</summary>
    internal static Color Base => Theme.WindowBackground;

    /// <summary>面板与条带的底。</summary>
    internal static Color Surface => Theme.PanelBackground;

    /// <summary>
    /// 列表里「一行一枚卡」的卡面：比面板底只高一点点。
    /// 直接用 <see cref="Hover"/> 会太跳（那是鼠标悬停专用的反馈量），
    /// 静止的卡只需要一点点明暗差就能从底上浮出来。
    /// </summary>
    internal static Color Raised => Mix(Theme.PanelBackground, Theme.TextPrimary, Theme.IsDark ? 0.05 : 0.03);

    /// <summary>凹下去的槽：分段控件的轨、缩放读数那一枚 pill。比外壳还暗一档。</summary>
    internal static Color Sunken => Mix(Theme.WindowBackground, Theme.CanvasBackground, 0.45);

    /// <summary>悬停：朝文字色混一档，两档各自往「更看得见」的方向走。</summary>
    internal static Color Hover => Mix(Theme.PanelBackground, Theme.TextPrimary, Theme.IsDark ? 0.10 : 0.07);

    /// <summary>按下：比悬停再深一档，手指按下去要有「陷进去」的读数。</summary>
    internal static Color Pressed => Mix(Theme.PanelBackground, Theme.TextPrimary, Theme.IsDark ? 0.17 : 0.13);

    /// <summary>选中行的薄 wash：强调蓝混进面板底。只用于选中态（需求 §6 蓝的用途）。</summary>
    internal static Color AccentWash => Mix(Theme.PanelBackground, Theme.Accent, Theme.IsDark ? 0.24 : 0.13);

    /// <summary>比 PanelBorder 更轻的发丝线，用于组内分隔（组间仍用 PanelBorder）。</summary>
    internal static Color HairlineSoft => Mix(Theme.PanelBackground, Theme.PanelBorder, 0.55);

    /// <summary>卡片顶缘那一道 1px 高光：让「抬起」在黑夜档读得出来。</summary>
    internal static Color BevelTop => Color.FromArgb(Theme.IsDark ? 26 : 210, 255, 255, 255);

    /// <summary>卡片底缘那一道 1px 投影：白天档的层级主要靠它。</summary>
    internal static Color BevelBottom => Color.FromArgb(Theme.IsDark ? 70 : 30, 0, 0, 0);

    /// <summary>两枚颜色按 t 线性混合（t=0 取 a，t=1 取 b）。整数结果，不引入浮点几何。</summary>
    internal static Color Mix(Color a, Color b, double t)
    {
        int L(int x, int y) => (int)Math.Round(x + (y - x) * t);
        return Color.FromArgb(L(a.R, b.R), L(a.G, b.G), L(a.B, b.B));
    }

    /// <summary>给一枚颜色加 alpha（画 wash／遮罩用）。</summary>
    internal static Color Fade(Color c, int alpha) => Color.FromArgb(alpha, c.R, c.G, c.B);

    // ── 形状 ────────────────────────────────────────────────────

    /// <summary>
    /// 圆角矩形路径。半径夹到短边的一半，免得窄 chip 上画出两个交叉的弧。
    /// 调用方负责 Dispose（using var）。
    /// </summary>
    internal static GraphicsPath RoundRect(Rectangle r, int radius)
    {
        int d = Math.Max(0, Math.Min(radius * 2, Math.Min(r.Width, r.Height)));
        var path = new GraphicsPath();
        if (d == 0 || r.Width <= 0 || r.Height <= 0)
        {
            if (r.Width > 0 && r.Height > 0) path.AddRectangle(r);
            return path;
        }
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>
    /// 画一块「抬起的卡」：圆角填色 + 一圈发丝边 + 顶缘高光 + 底缘投影。
    /// 这四笔合起来才是「精致」与「灰块」的区别：单独填色在任何 DPI 下都只是一块灰。
    /// </summary>
    internal static void Card(Graphics g, Rectangle r, Color fill, int radius = Radius, bool bevel = true)
    {
        if (r.Width <= 0 || r.Height <= 0) return;
        var smooth = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var path = RoundRect(r, radius))
        {
            using var brush = new SolidBrush(fill);
            g.FillPath(brush, path);
            using var edge = new Pen(Theme.PanelBorder, 1f);
            g.DrawPath(edge, path);
        }
        g.SmoothingMode = smooth;
        if (!bevel) return;
        // 高光与投影都画在圆角内侧的直边上，避开四个弧角（弧角上画直线会露出直角疤）。
        int inset = radius + 1;
        if (r.Width > inset * 2 + 2)
        {
            using var top = new Pen(BevelTop, 1f);
            g.DrawLine(top, r.X + inset, r.Y + 1, r.Right - inset - 1, r.Y + 1);
            using var bottom = new Pen(BevelBottom, 1f);
            g.DrawLine(bottom, r.X + inset, r.Bottom - 1, r.Right - inset - 1, r.Bottom - 1);
        }
    }

    /// <summary>一条水平发丝线（组间分隔）。整数坐标，1px。</summary>
    internal static void Rule(Graphics g, int x1, int x2, int y, Color color)
    {
        using var pen = new Pen(color, 1f);
        g.DrawLine(pen, x1, y, x2, y);
    }

    /// <summary>键盘焦点环：控件有焦点时画一圈强调色细环（需求 §6 键盘焦点提示）。</summary>
    internal static void FocusRing(Graphics g, Rectangle r, int radius = Radius)
    {
        // 第五十改回修：环画在矩形<b>内侧</b>，不许外扩。原来外扩 2px 把四条直边推出控件
        // 自己的裁剪区，屏上只剩四个角弧探回来——用户截图里分段控件那「四枚蓝角疤」就是这一族
        // （ChromeButton 同病）。内缩 1px 后 1.6px 的笔整条落在控件内，读作一圈完整的环。
        var outer = new Rectangle(r.X + 1, r.Y + 1, Math.Max(0, r.Width - 2), Math.Max(0, r.Height - 2));
        var smooth = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = RoundRect(outer, radius);
        using var pen = new Pen(Fade(Theme.Accent, 190), 1.6f);
        g.DrawPath(pen, path);
        g.SmoothingMode = smooth;
    }

    // ── 图标字形（Segoe Fluent Icons，Windows 自带的现成图标库，零依赖）───

    /// <summary>
    /// 图标字体。Windows 10/11 都带 Segoe Fluent Icons；老机器上它叫 Segoe MDL2 Assets，
    /// 码点同一套。这里按名字探一次，探不到就退回 MDL2，再探不到退回普通字体
    /// （那时字形会变成方框——所以 <see cref="HasIconFont"/> 交给界面决定要不要画字形）。
    /// 需求 §6 说「图标优先采用现成图标库」：这一枚就是系统自带的那一套，不引 NuGet、不下载资源。
    /// </summary>
    internal static readonly Font IconFont = CreateIconFont(10f);

    /// <summary>大一号的图标（空态引导、面板徽标用）。</summary>
    internal static readonly Font IconFontLarge = CreateIconFont(22f);

    /// <summary>本机到底有没有那套图标字体。没有时界面改画纯文字，不许画出一排方框。</summary>
    internal static bool HasIconFont { get; } = IconFont.Name.StartsWith("Segoe", StringComparison.OrdinalIgnoreCase);

    private static Font CreateIconFont(float size)
    {
        foreach (string name in new[] { "Segoe Fluent Icons", "Segoe MDL2 Assets" })
        {
            using var probe = new Font(name, size);
            // Font 构造探不到名字时不抛，而是静默换成默认字体——所以必须回读 Name 核对。
            if (probe.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return new Font(name, size);
        }
        return new Font(Theme.UiFont.Name, size);
    }

    /// <summary>在指定矩形正中画一枚图标字形。</summary>
    internal static void Glyph(Graphics g, char glyph, Rectangle box, Color color, Font? font = null)
    {
        if (!HasIconFont) return;
        var previous = g.TextRenderingHint;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        using var format = new StringFormat(StringFormat.GenericTypographic)
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            FormatFlags = StringFormatFlags.NoWrap,
        };
        using var brush = new SolidBrush(color);
        g.DrawString(glyph.ToString(), font ?? IconFont, brush, box, format);
        g.TextRenderingHint = previous;
    }

    /// <summary>
    /// 把一枚字形烤成位图，供 ToolStripItem.Image 用（ToolStrip 的项不是自绘控件，
    /// 拿不到「绘制当下」的钩子，只能给一张图）。调用方负责在换肤时换图并 Dispose 旧图。
    /// </summary>
    internal static Bitmap GlyphBitmap(char glyph, int px, Color color)
    {
        var bmp = new Bitmap(px, px);
        using Graphics g = Graphics.FromImage(bmp);
        g.Clear(Color.Transparent);
        using var font = CreateIconFont(px * 0.72f);
        Glyph(g, glyph, new Rectangle(0, 0, px, px), color, font);
        return bmp;
    }

    // ── 文字 ────────────────────────────────────────────────────

    /// <summary>单行文字，垂直居中、左对齐，超出裁掉（列表行与条带上的读数都用它）。</summary>
    internal static void Text(Graphics g, string text, Rectangle box, Color color, Font font,
        TextFormatFlags align = TextFormatFlags.Left | TextFormatFlags.VerticalCenter)
    {
        TextRenderer.DrawText(g, text, font, box, color,
            align | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }

    /// <summary>量一段文字在指定字体下的像素尺寸（先量后摆，导出对话框那一改立下的规矩）。</summary>
    internal static Size Measure(Graphics g, string text, Font font) =>
        TextRenderer.MeasureText(g, text, font, new Size(int.MaxValue, int.MaxValue),
            TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
}

/// <summary>
/// 图标码点表。每一个都在本机的 Segoe Fluent Icons 上<b>渲染成联系表逐枚眼核过</b>
/// （不是凭记忆抄的码点表——抄错的症状是屏上一排方框或语义相反的图标）。
/// 名字写成语义，调用处不出现裸码点。
/// </summary>
internal static class ChromeGlyph
{
    internal const char Plus = '\uE710';       // E710 加号（添加图片 / 放大共用）
    internal const char Minus = '\uE738';       // E738 一道横线（缩小）
    internal const char Save = '\uE74E';        // E74E 软盘（导出＝写进文件）
    internal const char Refresh = '\uE72C';     // E72C 环形箭头（全部重新分析）
    internal const char Settings = '\uE713';    // E713 齿轮
    internal const char Eye = '\uE7B3';         // E7B3 眼睛（浏览态 / 对比叠层）
    internal const char Pencil = '\uE70F';      // E70F 铅笔（编辑拼缝）
    internal const char Trash = '\uE74D';       // E74D 垃圾桶（删段）
    internal const char Crop = '\uE7A8';        // E7A8 裁切角标（裁边）
    internal const char Split = '\uE784';       // E784 双竖线（切分）
    internal const char Eraser = '\uE75C';      // E75C 橡皮（清理）
    internal const char Picture = '\uE7AA';     // E7AA 相框山形（素材）
    internal const char SeamLines = '\uE76F';   // E76F 双横线（拼缝）
    internal const char Compare = '\uE736';     // E736 并排两页（对比）
    internal const char ZoomIn = '\uE71E';      // E71E 放大镜加（差异＝凑近看）
    internal const char Expand = '\uE740';      // E740 对角双箭头（适应窗口）
    internal const char Check = '\uE73E';       // E73E 勾
    internal const char Warn = '\uE7BA';        // E7BA 三角感叹号
    internal const char Sun = '\uE706';         // E706 太阳（白天档）
    internal const char Moon = '\uE708';        // E708 月亮（黑夜档）
    internal const char Monitor = '\uE7F4';     // E7F4 显示器（跟随系统）
    internal const char ChevronDown = '\uE70D'; // E70D 下箭头
}