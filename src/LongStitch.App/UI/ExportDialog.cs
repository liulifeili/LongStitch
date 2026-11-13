using LongStitch.Core;
using LongStitch.Core.Export;
using LongStitch.Core.Geometry;
using LongStitch.Core.Imaging;
using LongStitch.Core.Layout;
using LongStitch.Core.Model;
using LongStitch.App.Diagnostics;
using System.Linq;

namespace LongStitch.App.UI;

/// <summary>
/// 导出对话框。需求 §7 与模拟 UI 需求 §3.6 的落点：
/// 必须先把"最终像素、格式、预估大小、限制原因、分段方案"摊开给人看，
/// 再动手写文件；降尺寸与分段都是<b>用户主动选</b>，程序绝不替他决定。
/// </summary>
public sealed class ExportDialog : Form
{
    private readonly Project _project;
    private readonly CanvasComposer _composer;
    private readonly EncoderRegistry _encoders;
    private readonly ISourceResolver? _probe;

    // ═══ 第五十四改⑤（用户图5「导出界面和整体 UI 完全不搭」）═══
    // 系统族控件（ComboBox/NumericUpDown/ProgressBar/Button）在黑夜档下全是白底旧组件。
    // 这里整群换成自绘的 Chrome 家族：下拉框/进度条是新控件，数字框复用右栏那枚
    // ChromeNumericBox，按钮复用 ChromeButton，背景色按钮做成色卡（点开取色器）。
    // 数据通路全部不变：SelectedIndex / Value / Enabled / Click 与旧控件同名同义。
    private readonly ChromeComboBox _format = new() { Width = 160 };
    private readonly BackgroundSwatchButton _background = new() { Width = 64, Height = 30 };
    // 四行"摊开给人看"的读数：高度一律交给 AutoSize 按换行现量（第四十七改），
    // 写死 Height 的旧写法在警告文案换行时会压住下一行。宽度上限在 BuildContent 里钉成内容宽。
    private readonly Label _sizeLine = new() { AutoSize = true, Font = Theme.UiFontBold, ForeColor = Theme.TextPrimary };
    private readonly Label _estimateLine = new() { AutoSize = true, Font = Theme.UiFontSmall, ForeColor = Theme.TextSecondary };
    private readonly Label _limitLine = new()
    {
        AutoSize = true, Font = Theme.UiFontSmall, ForeColor = Theme.Warning,
    };
    private readonly TextBox _directory = new() { ReadOnly = true, BackColor = Theme.WindowBackground, ForeColor = Theme.TextPrimary };
    private readonly ChromeNumericBox _maxHeight = new() { Width = 110, Minimum = 0, Maximum = 200000, Increment = 500 };
    private readonly ChromeNumericBox _overlap = new() { Width = 90, Minimum = 0, Maximum = 4000 };
    private readonly ChromeComboBox _downscale = new() { Width = 130 };
    // 第五十七改④（用户图4红框）：进度文字不再写进「开始导出」那枚定宽按钮——
    // 「正在合成像素 20%」比 148px 的按钮宽，字会画出按钮压到进度条上。
    // 改成独立一行：定宽文字格 + 进度条 + 取消，三者各占各的矩形，结构上不可能重叠。
    private readonly Label _progressLabel = new()
    {
        AutoSize = false, TextAlign = ContentAlignment.MiddleLeft,
        ForeColor = Theme.TextSecondary, Font = Theme.UiFontSmall,
    };
    private readonly FlowLayoutPanel _progressRow = new()
    {
        FlowDirection = FlowDirection.LeftToRight, WrapContents = false,
        AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
        Visible = false, Margin = new Padding(0, 0, 0, gap0),
    };
    private const int gap0 = 14;
    private bool _exporting;
    // 第五十六改②（用户图2蓝框）：「透明」勾——勾上就不再能设背景色（PNG 才有透明档可保，
    // JPEG/TIFF 没有透明，这枚勾对它们禁用）。勾的通路与设置页那枚 ChromeCheckBox 同一件。
    private readonly ChromeCheckBox _transparent = new() { Text = "透明", AutoSize = true };
    // 文件名模板与 JPEG 质量：需求 §3.6 要"命名预览"，产品需求 §7 要有损格式的质量可见可调
    private readonly TextBox _template = new() { BackColor = Theme.WindowBackground, ForeColor = Theme.TextPrimary };
    private readonly ChromeNumericBox _quality = new() { Width = 70, Minimum = 30, Maximum = 100, Increment = 1 };
    private readonly Label _qualityHint = new() { AutoSize = true, ForeColor = Theme.TextSecondary, Font = Theme.UiFontSmall };
    private readonly Label _piecesLine = new() { AutoSize = true, Font = Theme.UiFontSmall, ForeColor = Theme.TextSecondary };
    private readonly ChromeButton _export = new() { Text = "开始导出", Kind = ChromeButtonKind.Primary, Width = 148 };
    private readonly ChromeProgressBar _progress = new() { Width = 300, Height = 18, Visible = false };
    private readonly ChromeButton _cancel = new() { Text = "取消", Kind = ChromeButtonKind.Ghost, Width = 88, Visible = false };
    private readonly FlowLayoutPanel _resultLinks = new() { AutoSize = true, Height = 30, WrapContents = true };
    // 第四十七改：整块内容装进这一个自上而下的自动容器——行与行永远不会互相压住，
    // 窗体高度由 OnLayout 按它的实际高度收口（旧版是手写 y 坐标逐行累加，写死的高度一换行就重叠）。
    private readonly FlowLayoutPanel _body = new()
    {
        FlowDirection = FlowDirection.TopDown,
        WrapContents = false,
        AutoSize = true,
        // 长缩都允许：宽度应等于"最宽那一行"的现量值（算错了它会自己长出去，判据会红，好过静默裁掉）；
        // 高度随读数换行／文件链接变多自己长，警告清掉之后也缩得回去。
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
    };

    private string? _outputDirectory;
    private CancellationTokenSource? _cts;
    private ExportPlan _plan = null!;
    private bool _controlsInitialized;

    public ExportDialog(Project project, CanvasComposer composer, EncoderRegistry encoders,
        ISourceResolver? probe = null)
    {
        _project = project;
        _composer = composer;
        _encoders = encoders;
        _probe = probe;

        Text = "导出";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(560, 470);
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Theme.WindowBackground;
        ForeColor = Theme.TextPrimary;
        Font = Theme.UiFont;
        // 第五十七改②回修（真窗口目验抓到）：BuildContent 里所有宽度都是运行时按字体现量的
        // 「设备像素」（缩小下拉宽＝最长选项文字宽 + 一枚箭头高 + 16），与 AboutDialog／
        // SettingsDialog／ChromeColorDialog 同一条约定。WinForms 默认 AutoScaleMode 会再按
        // 「当前字体高 ÷ 设计字体高」把控件 Bounds 二次缩放——电池进程 DPI  unaware 被系统
        // 虚拟化成 96 DPI（缩放比≈1，量多少就是多少，故电池绿），而产品是 SystemAware 跑在
        // 主屏 150% DPI：收起格文字盒（整宽 − 一枚箭头 − 8）被那趟缩放压窄，「缩小到 1/4」
        // 在真窗口里截成「缩小到 1…」。关掉自动缩放，现量宽度在任何 DPI 都不被二次缩放。
        // 判据 <c>ui/缩小下拉收起格装得下最长选项整串</c> 钉住 AutoScaleMode.None。
        AutoScaleMode = AutoScaleMode.None;

        BuildContent();
        _outputDirectory = project.Doc.Export.OutputDirectory is { Length: > 0 } d && Directory.Exists(d)
            ? d
            : Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        RefreshPlan();
    }

    private void BuildContent()
    {
        // ═══ 第四十七改：导出对话框改成"先量后摆" ═══
        // 旧版把每一列的 x 写死（90／270／380／430……），那是按 96 DPI 下某一种字体宽度估出来的；
        // 真机 150% DPI 时点字号渲染成 1.5 倍像素宽，标签比预留的列宽出一截，尾巴就被右边的控件盖住——
        // 用户报的"导出界面很多元素重叠"正是这一族（截图里几乎每一行都是标签尾被控件压住）。
        // 现在所有横坐标由 TextRenderer.MeasureText <b>现量</b>：标签多宽、控件就从它后面开始；
        // 纵向交给 FlowLayoutPanel 自己叠行，行高由字体与控件高度决定，窗体高度由 OnLayout 按内容收口。
        // DPI、字体、语言换任何一种，布局都跟着现量结果变——重叠在结构上不可能再出现。
        // 判据 <c>ui/导出对话框任何控件不许互相重叠也不许出界</c>。
        // 第五十六改②（用户图2绿框+总体）：行距与页边距从 10/16 放宽到 14/20——
        // 用户原话「总体的【导出】窗口文字太挤，上方的部分需要拉开距离」。
        const int pad = 20, gap = 14, colGap = 24;
        // 自绘数字框统一走右栏同一套字体现量盒高（ToolPanel.AddIntRow 同一段账），
        // 150% DPI 下数字下缘不会出框。
        int boxH = Math.Max(30, Theme.MonoFont.Height + 10);
        _maxHeight.Height = boxH;
        _overlap.Height = boxH;
        _quality.Height = boxH;
        // 第五十七改②（用户图2红框「不…」）：缩小下拉的宽度改成按最长选项现量。
        // 旧值写死 130：96 DPI 勉强装下，150% DPI 下「缩小到 1/4」比 130 宽，
        // 收起格文字盒（整宽 − 一枚箭头 − 8）装不下整串，尾巴被截成「不…」。
        // 这里摆的只是初值（boxH 当箭头宽的代理）；真窗口 150% DPI 复验证明这层
        // 不充分——ComboBox 的真实 Height 是字体/ItemHeight 的内蕴值、设不进去，
        // 高 DPI 下比 boxH 大，文字盒被挤窄仍会截断。终局保证在
        // ChromeComboBox 的宽度自适应（OnHandleCreated/OnVisibleChanged/OnSizeChanged
        // 三挂点，用真实 Height 只增不减地现算）：handle 创建那一刻条目还没进 Items，
        // 真正能加宽的第一枚挂点是 OnVisibleChanged。本处这笔账只负责让窗宽把
        // 适配后的宽度也装得下（required 计数含它）。
        _downscale.Height = boxH;
        int downscaleTextW = 0;
        foreach (string option in new[] { "不缩小", "缩小到 1/2", "缩小到 1/3", "缩小到 1/4" })
            downscaleTextW = Math.Max(downscaleTextW, TextRenderer.MeasureText(option, Font).Width);
        _downscale.Width = downscaleTextW + boxH + 16;

        int W(string text) => TextRenderer.MeasureText(text, Font).Width;
        int col1 = Math.Max(
            Math.Max(W("格式"), W("输出位置")),
            Math.Max(Math.Max(W("分段：每片最大高度（0=不分段）"), W("主动缩小（默认不缩）")), W("文件名模板")));
        int col2 = Math.Max(Math.Max(W("背景色（透明处）"), W("相邻片重叠")), W("JPEG 质量"));
        // 右侧第二列的控件宽度（160/110/222/64/90/70）与下面各行的实际摆件一一同源；
        // 改控件宽先改这里的账（第四十七改立下的规矩）。缩小下拉的宽是上面现量的
        // _downscale.Width（第五十七改②），也进这笔账。
        const int dirBrowseW = 88, templateHostW = 222, swatchW = 64;
        int required = Math.Max(
            Math.Max(pad + col1 + gap + 160 + colGap + col2 + gap + swatchW + pad,
                     pad + col1 + gap + 110 + colGap + col2 + gap + 90 + pad),
            Math.Max(Math.Max(pad + col1 + gap + templateHostW + colGap + col2 + gap + 70 + 6 + W("有损") + pad,
                              pad + col1 + gap + dirBrowseW + gap + pad),
                     pad + col1 + gap + _downscale.Width + pad));
        int clientW = Math.Max(560, required);
        ClientSize = new Size(clientW, 470);   // 宽度按最宽那一行现量；高度由 OnLayout 收口

        _body.Location = new Point(pad, pad);
        _body.Width = clientW - pad * 2;
        Controls.Add(_body);

        FlowLayoutPanel Row()
        {
            var row = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Margin = new Padding(0, 0, 0, gap),
            };
            _body.Controls.Add(row);
            return row;
        }
        // 标签列宽统一＝本列最宽的那枚：控件因此纵向对齐，而不是每行各起一个头。
        // Tag 用来在收尾那一趟把标签高度拉成行高、文字垂直居中（字体要等挂进窗体才生效，先量不到）。
        Label FieldLabel(string text, int width, int leftMargin) => new()
        {
            Text = text,
            AutoSize = false,
            Width = width,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Theme.TextSecondary,
            Margin = new Padding(leftMargin, 0, gap, 0),
            Tag = "field-label",
        };

        var rowFormat = Row();
        rowFormat.Controls.Add(FieldLabel("格式", col1, 0));
        rowFormat.Controls.Add(_format);
        rowFormat.Controls.Add(FieldLabel("背景色（透明处）", col2, colGap));
        rowFormat.Controls.Add(_background);
        _background.Swatch = _project.Doc.Export.Background.ToGdiColor();
        _background.Click += (_, _) =>
        {
            // 第五十七改③（用户图3）：系统 ColorDialog 是浅灰旧组件，和整套深色自绘
            // 界面割裂。换成同一套 Chrome 语言的自绘取色器：预设色板 + RGB 数字框 + 预览。
            // 数据通路不变：OK 之后照样写 Export.Background 并 RefreshPlan。
            using var picker = new ChromeColorDialog(_project.Doc.Export.Background.ToGdiColor());
            if (picker.ShowDialog(this) == DialogResult.OK)
            {
                _background.Swatch = picker.SelectedColor;
                _background.Invalidate();
                _project.Mutate("导出背景色", d => d.Export.Background = picker.SelectedColor.ToRgba());
                RefreshPlan();
            }
        };
        // 第五十六改②（用户图2蓝框）：色卡旁边挂一枚「透明」勾。旧规则把 PNG 档的色卡整个禁用
        // ——用户量到的「背景颜色无法点开」就是这一条。新规则：勾上透明＝不许设背景色；
        // 勾开（或选 JPEG/TIFF）＝背景色照设。规则集中在 ApplyTransparencyRule 一处。
        _transparent.CheckedChanged += (_, _) =>
        {
            if (!_controlsInitialized) return;
            ApplyTransparencyRule();
            _project.Mutate("导出透明背景", d => d.Export.PngTransparent = _transparent.Checked);
            RefreshPlan();
        };
        _transparent.Margin = new Padding(colGap - Chrome.Gap, 0, 0, 0);
        rowFormat.Controls.Add(_transparent);

        var rowDirectory = Row();
        var browse = new ChromeButton { Text = "选择…", Kind = ChromeButtonKind.Secondary, Width = dirBrowseW };
        browse.Margin = new Padding(gap, 0, 0, 0);
        browse.Click += (_, _) =>
        {
            using var picker = new FolderBrowserDialog { SelectedPath = _outputDirectory ?? "" };
            if (picker.ShowDialog(this) == DialogResult.OK) { _outputDirectory = picker.SelectedPath; RefreshPlan(); }
        };
        rowDirectory.Controls.Add(FieldLabel("输出位置", col1, 0));
        // 路径框装进一圈发丝边宿主（第五十四改⑤；第五十五改②按用户图2再修）：
        // TextBox 自身的系统边框撤掉；宿主底色原来是 Theme.PanelBorder（浅灰），
        // 上下各 5px 的 Padding 带会把整条浅灰露出来——就是用户截图里那圈"难看的浅色底纹"。
        // 现在宿主与框体同底（PanelBackground，调色板色），1px 的 PanelBorder 发丝边
        // 改由 Paint 画出（Chrome 纪律：算出来的颜色只许进 OnPaint，BackColor 只放调色板值）。
        _directory.BorderStyle = BorderStyle.None;
        _directory.BackColor = Theme.PanelBackground;
        var directoryHost = new Panel
        {
            Width = Math.Max(200, _body.Width - col1 - gap - dirBrowseW - gap),
            Height = _directory.Height + 10,
            BackColor = Theme.PanelBackground,
            Padding = new Padding(1, 5, 1, 5),
            Margin = new Padding(0),
        };
        directoryHost.Paint += HairlineBorderPaint;
        _directory.Dock = DockStyle.Fill;
        directoryHost.Controls.Add(_directory);
        rowDirectory.Controls.Add(directoryHost);
        rowDirectory.Controls.Add(browse);

        var rowSegment = Row();
        rowSegment.Controls.Add(FieldLabel("分段：每片最大高度（0=不分段）", col1, 0));
        rowSegment.Controls.Add(_maxHeight);
        rowSegment.Controls.Add(FieldLabel("相邻片重叠", col2, colGap));
        rowSegment.Controls.Add(_overlap);

        var rowDownscale = Row();
        rowDownscale.Controls.Add(FieldLabel("主动缩小（默认不缩）", col1, 0));
        rowDownscale.Controls.Add(_downscale);

        var rowTemplate = Row();
        rowTemplate.Controls.Add(FieldLabel("文件名模板", col1, 0));
        // 模板输入框同样装进发丝边宿主（与路径框同一套语言；第五十五改②浅底同修）。
        _template.Font = Theme.MonoFont;   // 等宽字先上，宿主高度才按同一枚字体量
        _template.BorderStyle = BorderStyle.None;
        _template.BackColor = Theme.PanelBackground;
        var templateHost = new Panel
        {
            Width = templateHostW,
            Height = _template.Height + 10,
            BackColor = Theme.PanelBackground,
            Padding = new Padding(1, 5, 1, 5),
            Margin = new Padding(0),
        };
        templateHost.Paint += HairlineBorderPaint;
        _template.Dock = DockStyle.Fill;
        templateHost.Controls.Add(_template);
        rowTemplate.Controls.Add(templateHost);
        rowTemplate.Controls.Add(FieldLabel("JPEG 质量", col2, colGap));
        rowTemplate.Controls.Add(_quality);
        _qualityHint.Margin = new Padding(6, 4, 0, 0);
        rowTemplate.Controls.Add(_qualityHint);
        _template.Font = Theme.MonoFont;

        // 四行"摊开给人看"的读数：宽度钉死成内容宽、高度交给 AutoSize 自己换行长高，
        // 警告文案再长也只是把窗体撑高，不会压到下一行（旧版写死 Height=40 就会压）。
        foreach (Label line in new[] { _sizeLine, _estimateLine, _limitLine, _piecesLine })
        {
            line.AutoSize = true;
            line.MaximumSize = new Size(_body.Width, 0);
            line.Margin = new Padding(0, 0, 0, 8);
            _body.Controls.Add(line);
        }

        var rowAction = Row();
        // 第五十六改②（用户图2绿框）：动作行与上方读数之间多给一口气，与窗底的尾巴收紧——
        // 旧账上方 14px、下方 30px，用户量出「距离上方太近了，距离下方太远」。
        rowAction.Margin = new Padding(0, 12, 0, 0);
        rowAction.Controls.Add(_export);
        _export.Click += (_, _) => StartExport();
        _cancel.Click += (_, _) => _cts?.Cancel();

        // ═══ 第五十七改④（用户图4红框「文字和进度条重叠」）═══
        // 旧版把进度文字写进「开始导出」那枚定宽 148px 的按钮：「正在合成像素 20%」
        // 比按钮宽，字画到按钮外面，正好压在旁边的进度条上。现在进度单独一行：
        // 定宽文字格（按最宽阶段串现量）＋进度条＋取消，三者各占各的矩形，
        // FlowLayoutPanel 横向排布，结构上不可能重叠。这一行平时隐藏，
        // 导出开始才 Visible——窗体高度由 OnLayout 跟着内容收口。
        int progressLabelW = TextRenderer.MeasureText("正在写第 999/999 片 100%", Theme.UiFontSmall).Width + 8;
        _progressLabel.Width = progressLabelW;
        _progressLabel.Height = _cancel.Height;   // 与取消按钮同高，MiddleLeft 天然对齐
        _progressLabel.Margin = new Padding(0);
        _progress.Visible = true;                 // 显隐交给整行 _progressRow，不再各自开关
        _cancel.Visible = true;
        _progress.Width = Math.Max(120, _body.Width - progressLabelW - gap - _cancel.Width - gap);
        _progressRow.Controls.Add(_progressLabel);
        _progressRow.Controls.Add(_progress);
        _progressRow.Controls.Add(_cancel);
        _body.Controls.Add(_progressRow);

        _resultLinks.Width = _body.Width;
        _resultLinks.Margin = new Padding(0);
        _body.Controls.Add(_resultLinks);

        // WinForms 控件默认 Margin 是 (3,3,3,3)：不清零的话每行会比现量结果宽出 6 像素，
        // "最宽那一行"的账就正好差在这一撮上——旧版的重叠也正是这类"没入账的几像素"攒出来的。
        // 两个发丝边宿主与「选择…」不在此列：前者自己 Margin=0，后者预留了 gap 左边距。
        foreach (Control c in new Control[]
                 { _format, _background, _maxHeight, _overlap, _downscale, _quality, _export })
            c.Margin = new Padding(0);

        // 收尾：字体这时才真正生效，把每枚标签的高度拉成它那一行的高度、文字垂直居中。
        // 第五十五改②（用户图2）：同一行里的非标签控件（输入框宿主、下拉框、按钮…）
        // 原来各自顶行首站着——行高被某个高个儿撑起来后，矮个儿就偏上，
        // 用户量出来「两个红框没有水平对齐」。这里按行高逐个垂直居中（保留水平边距）。
        foreach (Control row in _body.Controls)
        {
            if (row is not FlowLayoutPanel strip) continue;
            int rowHeight = strip.PreferredSize.Height;
            foreach (Control child in strip.Controls)
            {
                if (Equals(child.Tag, "field-label")) { child.Height = rowHeight; continue; }
                int top = Math.Max(0, (rowHeight - child.Height) / 2);
                child.Margin = new Padding(child.Margin.Left, top, child.Margin.Right, child.Margin.Bottom);
            }
        }

        _template.Text = _project.Doc.Export.FileNameTemplate;
        _quality.Value = Math.Clamp(_project.Doc.Export.JpegQuality, 30, 100);

        _format.SelectedIndexChanged += (_, _) => { if (_controlsInitialized) { ApplyFormatChoice(); RefreshPlan(); } };
        _maxHeight.ValueChanged += (_, _) => { if (_controlsInitialized) RefreshPlan(); };
        _overlap.ValueChanged += (_, _) => { if (_controlsInitialized) RefreshPlan(); };
        _downscale.SelectedIndexChanged += (_, _) => { if (_controlsInitialized) RefreshPlan(); };
        _template.TextChanged += (_, _) => { if (_controlsInitialized) RefreshPlan(); };
        _quality.ValueChanged += (_, _) => { if (_controlsInitialized) RefreshPlan(); };
    }

    /// <summary>
    /// 第五十五改②：发丝边宿主的 1px 外框改由 Paint 画。
    /// 原来靠宿主 BackColor=PanelBorder 露边，但上下各 5px 的 Padding 带也跟着露浅灰；
    /// 现在宿主整体走 PanelBackground，浅色只剩这一圈线。
    /// 第五十六改②（用户图2黄框）：直角改成圆角——用 <see cref="Chrome.RoundRect"/> 走
    /// RadiusSmall，与色卡、数字框、按钮同一族；1px 线在抗锯齿下拐角不毛刺。
    /// </summary>
    private static void HairlineBorderPaint(object? sender, PaintEventArgs e)
    {
        if (sender is not Panel host) return;
        var smooth = e.Graphics.SmoothingMode;
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var pen = new Pen(Theme.PanelBorder, 1f);
        using var path = Chrome.RoundRect(new Rectangle(0, 0, host.Width - 1, host.Height - 1), Chrome.RadiusSmall);
        e.Graphics.DrawPath(pen, path);
        e.Graphics.SmoothingMode = smooth;
    }

    /// <summary>
    /// 窗体高度跟着内容走：读数行换行变高、导出完多出文件链接，都把窗体撑到刚好装下。
    /// 固定写死高度的旧写法在警告文案换行时会把下面的按钮压住——那一族也是"元素重叠"。
    /// </summary>
    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        // 第五十六改②：窗底尾巴 16 → 14（绿框「距离下方太远」的那一半；上面那半在动作行的
        // Margin.Top 里补回）。与行距 gap 同值，上下读起来是同一口呼吸。
        int want = _body.Bottom + 14;
        if (want > 0 && want != ClientSize.Height)
            ClientSize = new Size(ClientSize.Width, want);
    }

    private void ApplyFormatChoice()
    {
        if (_format.SelectedItem is not FormatChoice choice) return;
        _project.Mutate("导出格式", d =>
        {
            d.Export.Format = choice.Format;
            d.Export.MaxSegmentHeight = (int)_maxHeight.Value;
        });
        // WebP 已从导出下拉里移除（第五十五改③），用户能选到的只剩这三种
        ApplyTransparencyRule();
        // 质量只对 JPEG 有实际意义，别的格式下禁用并说明，避免"调了没反应"的错觉
        _quality.Enabled = choice.Format == ExportFormat.Jpeg;
        _qualityHint.Text = choice.Format == ExportFormat.Jpeg ? "有损" : "—";
        _qualityHint.ForeColor = choice.Format == ExportFormat.Jpeg ? Theme.Warning : Theme.TextDisabled;
    }

    /// <summary>
    /// 第五十六改②（用户图2蓝框）：「透明」勾与背景色卡的一处总规则。
    /// 旧规则把 PNG 档的色卡整个禁用——背景色对透明导出确实无用，但用户连"点开看看"都不行，
    /// 报的就是「背景颜色的选择，无法点开」。新规则：
    /// ① 只有 PNG 有透明档可选 → 勾只在 PNG 档可用；
    /// ② 勾上透明 → 背景色禁用（用户原话："一旦选了，就不能设置背景色"）；
    /// ③ PNG 勾开透明 → 背景色可用（导出时把透明像素压到这个色上）；JPEG/TIFF → 背景色照旧可用。
    /// </summary>
    private void ApplyTransparencyRule()
    {
        bool png = (_format.SelectedItem as FormatChoice)?.Format == ExportFormat.Png;
        _transparent.Enabled = png;
        _background.Enabled = !png || !_transparent.Checked;
    }

    private sealed record FormatChoice(ExportFormat Format, string Label, bool Available, string? Reason)
    {
        public override string ToString() => Available ? Label : $"{Label}（不可用）";
    }

    /// <summary>
    /// 格式的屏上显示名（第五十五改③）：大写通名。文件扩展名仍由
    /// <see cref="ExportPlan"/>/模板按小写产出——"显示大写、存储小写"两边各管各的。
    /// </summary>
    private static string DisplayName(ExportFormat format) => format switch
    {
        ExportFormat.Png => "PNG",
        ExportFormat.Jpeg => "JPEG",
        ExportFormat.Tiff => "TIFF",
        _ => format.ToString().ToUpperInvariant(),
    };

    /// <summary>仅供冒烟工装：格式下拉里实际列出的显示名（第五十五改③判据用）。</summary>
    public IReadOnlyList<string> FormatLabelsForTest =>
        _format.Items.Cast<FormatChoice>().Select(c => c.Label).ToArray();

    /// <summary>仅供冒烟工装：当前选中格式的扩展名（证明显示名大写没把存储口径带走）。</summary>
    public string? SelectedExtensionForTest =>
        _plan.Pieces.Count > 0 ? Path.GetExtension(_plan.Pieces[0].FileName) : null;

    /// <summary>
    /// 第五十五改②：行容器全量回读（每个都是 FlowLayoutPanel 条）。
    /// 判据用它逐行核对"标签拉满行高、其余控件垂直居中、输入框宿主不再有浅色底纹"。
    /// </summary>
    public IReadOnlyList<Control> BodyRowsForTest =>
        _body.Controls.OfType<FlowLayoutPanel>().Cast<Control>().ToArray();

    private void RefreshPlan()
    {
        ExportSettings settings = _project.Doc.Export;
        if (!_controlsInitialized)
        {
            _maxHeight.Value = Math.Clamp(settings.MaxSegmentHeight, 0, 200000);
            _overlap.Value = Math.Clamp(settings.SegmentOverlap, 0, 4000);
            foreach (int step in new[] { 1, 2, 3, 4 })
                _downscale.Items.Add(step == 1
                    ? new FormatChoice(ExportFormat.Png, "不缩小", true, null)
                    : new FormatChoice(ExportFormat.Png, $"缩小到 1/{step}", true, null));
            _downscale.SelectedIndex = Math.Clamp(settings.DownscaleDivisor - 1, 0, 3);

            // ═══ 第五十五改③（用户图3）═══
            // ① 显示名大写：PNG/JPEG/TIFF 是格式通名，原 format.ToString() 产出的 "Png/Jpeg/Tiff"
            //    读起来像类名不像格式名。存储与扩展名仍走小写（ExportFormat 枚举与模板的 {ext}
            //    一概不动，导出通路零变化）。
            // ② 删掉 WebP 导出项：编码器探不到 WebP 支持时它永远挂着「（不可用）」——
            //    一个永远选不了的选项只会占地方。用户原话："既然不支持导出就直接删除（但得支持导入）"。
            //    导入侧 FileSourceResolver 的 WebP 解码原样保留；旧项目若记录过 WebP，
            //    下面的选中循环找不到匹配项，就落到 PNG（首选项，无损）。
            foreach (ExportFormat format in new[] { ExportFormat.Png, ExportFormat.Jpeg, ExportFormat.Tiff })
            {
                string? reason = _encoders.Probe(format);
                _format.Items.Add(new FormatChoice(format, DisplayName(format), reason is null, reason));
            }
            bool matched = false;
            for (int i = 0; i < _format.Items.Count; i++)
                if (_format.Items[i] is FormatChoice c && c.Format == settings.Format)
                {
                    _format.SelectedIndex = i;
                    matched = true;
                }
            if (!matched) _format.SelectedIndex = 0;
            // 透明勾先在静音档里回填（_controlsInitialized 还没立起来，CheckedChanged 不动手），
            // 之后一次性套用互斥规则
            _transparent.Checked = settings.PngTransparent;
            _controlsInitialized = true;
            ApplyTransparencyRule();
            _quality.Enabled = settings.Format == ExportFormat.Jpeg;
            _qualityHint.Text = settings.Format == ExportFormat.Jpeg ? "有损" : "—";
            _qualityHint.ForeColor = settings.Format == ExportFormat.Jpeg ? Theme.Warning : Theme.TextDisabled;
        }

        int divisor = ((int)_downscale.SelectedIndex) + 1;
        string template = string.IsNullOrWhiteSpace(_template.Text) ? "stitched_{index}.{ext}" : _template.Text.Trim();
        var snapshot = settings with
        {
            Format = (_format.SelectedItem as FormatChoice)?.Format ?? settings.Format,
            MaxSegmentHeight = (int)_maxHeight.Value,
            SegmentOverlap = (int)_overlap.Value,
            DownscaleDivisor = divisor,
            FileNameTemplate = template,
            JpegQuality = (int)_quality.Value,
            OutputDirectory = _outputDirectory ?? "",
        };

        _plan = ExportPlanner.Build(_project.Layout, snapshot, _encoders, _outputDirectory, _composer, _probe);
        DiagnosticLog.Info("export.plan", $"revision={_project.Revision} format={snapshot.Format} original={_plan.OriginalPixelSize} output={_plan.OutputPixelSize} divisor={divisor} max_height={snapshot.MaxSegmentHeight} overlap={snapshot.SegmentOverlap} pieces={_plan.Pieces.Count} blocked={_plan.IsHardBlock} reason={_plan.BlockingReason}");

        bool lossless = _plan.Lossless;
        _sizeLine.Text = $"最终像素：{_plan.OutputPixelSize.Width} × {_plan.OutputPixelSize.Height} px" +
                         (lossless ? " · 无损" : " · 有损格式");
        _sizeLine.ForeColor = lossless ? Theme.Passed : Theme.Warning;

        _estimateLine.Text = _plan.EstimatedTotalBytes > 0
            ? $"预估大小：约 {ExportPlan.FormatBytes(_plan.EstimatedTotalBytes)}（按真实编码器采样 3 段外推）"
            : "预估大小：计算中或无法估算";

        _limitLine.Text = _plan.Verdict.Reason is null
            ? $"输出尺寸按源图像素计算，不受预览缩放 {_canvasZoomHint()}"
            : "⚠ " + _plan.Verdict.Reason;
        _limitLine.ForeColor = _plan.Verdict.Reason is null ? Theme.TextSecondary : Theme.Warning;

        if (_plan.Pieces.Count == 1)
        {
            _piecesLine.Text = $"将写出 1 个文件：{Path.Combine(_outputDirectory ?? "", _plan.Pieces[0].FileName)}";
        }
        else
        {
            string first = _plan.Pieces[0].FileName, last = _plan.Pieces[^1].FileName;
            _piecesLine.Text = $"将写出 {_plan.Pieces.Count} 个文件（{first} … {last}），" +
                               $"每片宽 {_plan.Pieces[0].Width}px、高 ≤ {_plan.Pieces.Max(x => x.Height)}px";
        }

        bool blocked = _plan.IsHardBlock || _outputDirectory is null;
        // 第五十七改④：守卫改用 _exporting 字段。旧写法读按钮文字是不是「正在」开头，
        // 而按钮文字现在不再承载进度（进度搬去独立行），再拿文字当状态会失准。
        _export.Enabled = !blocked && !_exporting;
        if (!_exporting)
            _export.Text = _plan.IsHardBlock ? "无法导出（见上方提示）" : "开始导出";
        _resultLinks.Controls.Clear();
    }

    private string _canvasZoomHint()
    {
        // 明确写清"预览缩放不影响导出"，这是需求 §1 与验收场景 7 的核心承诺
        return "（当前预览缩放只是显示效果）";
    }

    private async void StartExport()
    {
        if (_outputDirectory is null || _plan.IsHardBlock) return;

        try { Directory.CreateDirectory(_outputDirectory); }
        catch (Exception ex)
        {
            DiagnosticLog.Error("export.directory_failed", ex, $"directory={_outputDirectory}");
            MessageBox.Show(this, "输出目录不可用：" + ex.Message, "导出",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        // 模板与质量先记进项目（可撤销），再以下面这份设置真正写文件——
        // 这样下次打开对话框还能看见用户自己的命名习惯，而且两边读的是同一份值。
        _project.Mutate("导出命名与质量", d =>
        {
            d.Export.FileNameTemplate = string.IsNullOrWhiteSpace(_template.Text)
                ? "stitched_{index}.{ext}" : _template.Text.Trim();
            d.Export.JpegQuality = (int)_quality.Value;
            d.Export.MaxSegmentHeight = (int)_maxHeight.Value;
            d.Export.SegmentOverlap = (int)_overlap.Value;
            d.Export.DownscaleDivisor = _plan.DownscaleDivisor;
            d.Export.PngTransparent = _transparent.Checked;   // 第五十六改②：透明勾随导出一起入账
        });

        var settings = _project.Doc.Export with
        {
            Format = _plan.Format,
            MaxSegmentHeight = (int)_maxHeight.Value,
            SegmentOverlap = (int)_overlap.Value,
            DownscaleDivisor = _plan.DownscaleDivisor,
        };

        // 第五十七改④：进度文字写进独立行的定宽文字格，不再写进按钮。
        _exporting = true;
        _export.Enabled = false;
        _export.Text = "导出中…";
        _progress.Value = 0;
        _progressLabel.Text = "";
        _progressRow.Visible = true;
        _cts = new CancellationTokenSource();
        var progress = new Progress<ExportProgress>(p =>
        {
            _progress.Value = Math.Clamp(p.Percent, 0, 100);
            _progressLabel.Text = $"{p.Stage} {p.Percent}%";
        });

        ExportEngine engine = new(_composer, _encoders);
        ExportOutcome outcome;
        DiagnosticLog.Info("export.begin", $"directory={_outputDirectory} format={settings.Format} size={_plan.OutputPixelSize} pieces={_plan.Pieces.Count} quality={settings.JpegQuality}");
        // ═══ 导出前的裁切账（export.seam.crop）═══
        // 写文件之前把这条链的等式记一遍：Σ各段未裁切长 − Σ两侧裁切 = 画布高。
        // 成品高度不对时，日志里已经有现成的两个数可以对着看，不必重跑一遍去猜是谁吃掉的。
        {
            CanvasLayout exportLayout = _project.Layout;
            var account = exportLayout.MeasureDedup();
            DiagnosticLog.Info("export.seam.crop.summary",
                $"crop={exportLayout.UsesCropPlacement} sumExtents={account.SumExtents} " +
                $"sumOverlaps={account.SumOverlaps} expected={account.Expected} " +
                $"canvasHeight={exportLayout.CanvasSize.Height} outputHeight={_plan.OutputPixelSize.Height} " +
                $"divisor={_plan.DownscaleDivisor} pieces={_plan.Pieces.Count} " +
                $"seams={_project.Doc.Seams.Count} holds={account.Holds} unexplained={account.Unexplained}");
            foreach (var seam in _project.Doc.Seams)
            {
                var geometry = exportLayout.DescribeSeam(seam);
                if (geometry is null) continue;
                DiagnosticLog.Info("export.seam.crop",
                    $"revision={_project.Revision} outputHeight={_plan.OutputPixelSize.Height} {geometry}");
            }
        }        try
        {
            outcome = await Task.Run(() =>
                engine.Run(_project.Layout, _plan, settings, _outputDirectory!, progress, _cts.Token));
        }
        catch (Exception ex)
        {
            DiagnosticLog.Error("export.unexpected_failure", ex, $"directory={_outputDirectory}");
            outcome = ExportOutcome.Failed(ex.Message, Array.Empty<string>());
        }
        DiagnosticLog.Info("export.end", $"success={outcome.Success} cancelled={outcome.Cancelled} files={string.Join(";", outcome.Files)} error={outcome.Error}");
        Diagnostics.MemorySnapshot.Log("export");
        Diagnostics.MemorySnapshot.ReleaseAfterOperation("export");

        _progressRow.Visible = false;
        _exporting = false;
        _export.Enabled = true;
        _export.Text = "开始导出";

        if (outcome.Success)
        {
            _resultLinks.Controls.Add(new Label
            {
                Text = $"已写出 {outcome.Files.Count} 个文件：",
                AutoSize = true,
                ForeColor = Theme.Passed,
                Font = Theme.UiFont,
            });
            foreach (string file in outcome.Files)
            {
                var link = new LinkLabel
                {
                    Text = Path.GetFileName(file),
                    AutoSize = true,
                    LinkColor = Theme.Accent,
                    Font = Theme.UiFont,
                    Margin = new Padding(6, 0, 6, 0),
                };
                string captured = file;
                link.LinkClicked += (_, _) => OpenInExplorer(captured);
                _resultLinks.Controls.Add(link);
            }
            _project.MarkSaved(_project.FilePath);
            _project.Doc.Export.OutputDirectory = _outputDirectory;
            Status($"导出完成：{ExportPlan.FormatBytes(outcome.Files.Sum(f => new FileInfo(f).Length))}");
        }
        else
        {
            MessageBox.Show(this,
                (outcome.Cancelled ? "导出已取消。\n" : "导出失败。\n") + (outcome.Error ?? "") +
                "\n\n未完成的临时文件已删除，原图与项目参数都没有被改动。",
                "导出", MessageBoxButtons.OK,
                outcome.Cancelled ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            RefreshPlan();
        }
    }

    private void OpenInExplorer(string file)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = file,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "打不开文件：" + ex.Message, "导出",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void Status(string text) => _estimateLine.Text = text;

    /// <summary>仅供冒烟工装：当前计划会写出的文件名（命名预览的那句话就来自这份计划）。</summary>
    public IReadOnlyList<string> PlanFileNamesForTest => _plan.Pieces.Select(p => p.FileName).ToArray();

    /// <summary>仅供冒烟工装：模板输入框与质量数值控件的回读值。</summary>
    public string TemplateTextForTest => _template.Text;

    /// <summary>仅供冒烟工装：见 TemplateTextForTest。</summary>
    public int QualityValueForTest => (int)_quality.Value;

    /// <summary>仅供冒烟工装：质量数值框当前是否可用（只有 JPEG 该可用）。</summary>
    public bool QualityEnabledForTest => _quality.Enabled;

    /// <summary>第五十六改②工装口：「透明」勾当前状态（默认 PNG 档应勾上）。</summary>
    public bool TransparentCheckedForTest => _transparent.Checked;

    /// <summary>第五十六改②工装口：「透明」勾当前是否可用（只有 PNG 该可用）。</summary>
    public bool TransparentCheckEnabledForTest => _transparent.Enabled;

    /// <summary>第五十六改②工装口：背景色卡当前是否可用（勾上透明或 PNG+透明时应不可用）。</summary>
    public bool BackgroundEnabledForTest => _background.Enabled;

    /// <summary>仅供冒烟工装：走真勾的 CheckedChanged 处理器改「透明」勾（不绕过任何规则）。</summary>
    public void SetTransparentForTest(bool value) => _transparent.Checked = value;

    /// <summary>
    /// 第五十六改②工装口：格式下拉收起格补画箭头的执行次数。
    /// ChromeComboBox 是 internal 类型，电池没有 InternalsVisibleTo，计数从这一口出。
    /// </summary>
    public int FormatComboArrowPaintCountForTest => _format.ArrowPaintCount;

    /// <summary>Exercise the same value-change handlers as the visible export controls.</summary>
    public (int MaxHeight, int Overlap, int Divisor, int Pieces) SetPreviewOptionsForTest(
        int maxHeight, int overlap, int divisor)
    {
        _maxHeight.Value = maxHeight;
        _overlap.Value = overlap;
        _downscale.SelectedIndex = divisor - 1;
        return ((int)_maxHeight.Value, (int)_overlap.Value,
            _downscale.SelectedIndex + 1, _plan.Pieces.Count);
    }

    /// <summary>第五十七改②工装口：「主动缩小」下拉本体（判收起格装得下最长选项整串）。</summary>
    public Control DownscaleComboForTest => _downscale;

    /// <summary>第五十七改④工装口：进度行的定宽文字格（判它与进度条各占各的矩形）。</summary>
    public Control ProgressLabelForTest => _progressLabel;

    /// <summary>第五十七改④工装口：进度条本体。</summary>
    public Control ProgressBarForTest => _progress;

    /// <summary>
    /// 第五十七改④工装口：按真导出同一条接线点亮进度行（不写文件）。
    /// 判据用它把进度行摆出来，再量文字格／进度条／取消三枚互不重叠。
    /// </summary>
    public void BeginProgressUiForTest(string stage, int percent)
    {
        _exporting = true;
        _export.Enabled = false;
        _export.Text = "导出中…";
        _progress.Value = Math.Clamp(percent, 0, 100);
        _progressLabel.Text = $"{stage} {percent}%";
        _progressRow.Visible = true;
    }

    /// <summary>第五十七改④工装口：按导出结束同一条接线收起进度行。</summary>
    public void EndProgressUiForTest()
    {
        _progressRow.Visible = false;
        _exporting = false;
        _export.Enabled = true;
        _export.Text = "开始导出";
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _cts?.Dispose();
        base.Dispose(disposing);
    }

    /// <summary>
    /// 第五十四改⑤：背景色选择做成一枚「色卡」按钮——卡片面上直接展示当前导出背景色，
    /// 点下打开取色器（第五十七改③起是 <see cref="ChromeColorDialog"/> 自绘深色档，
    /// 不再是系统 ColorDialog）。旧版是一枚无字的系统 Button（黑夜档下就是一块来历不明的灰方块）。
    /// 颜色绘制当下画，卡片外观走 <see cref="Chrome.Card"/> 同一套语言。
    /// </summary>
    private sealed class BackgroundSwatchButton : Control
    {
        private bool _hover;

        /// <summary>当前展示的导出背景色。</summary>
        internal Color Swatch { get; set; } = Color.White;

        internal BackgroundSwatchButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.WindowBackground;   // 只放调色板认得的值；自绘时整块盖掉
            Cursor = Cursors.Hand;
            TabStop = false;
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            var card = new Rectangle(0, 0, Width - 1, Height - 1);
            Chrome.Card(g, card, _hover && Enabled ? Chrome.Hover : Chrome.Raised, Chrome.RadiusSmall, bevel: false);
            // 色块缩进卡面一圈发丝边；禁用时半透明表示「这一格式不需要背景色」。
            var swatch = new Rectangle(4, 4, Math.Max(0, Width - 9), Math.Max(0, Height - 9));
            var smooth = g.SmoothingMode;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (var path = Chrome.RoundRect(swatch, Chrome.RadiusSmall - 2))
            using (var fill = new SolidBrush(Enabled ? Swatch : Chrome.Fade(Swatch, 70)))
                g.FillPath(fill, path);
            using (var edge = new Pen(Enabled ? Theme.PanelBorder : Chrome.HairlineSoft, 1f))
            {
                var edgeRect = new Rectangle(swatch.X, swatch.Y, swatch.Width - 1, swatch.Height - 1);
                using var edgePath = Chrome.RoundRect(edgeRect, Chrome.RadiusSmall - 2);
                g.DrawPath(edge, edgePath);
            }
            g.SmoothingMode = smooth;
        }
    }
}
