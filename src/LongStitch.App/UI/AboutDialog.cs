using System.Linq;

namespace LongStitch.App.UI;

/// <summary>
/// 「关于」对话框（第五十一改新建，替掉原来的系统 MessageBox）。
///
/// ═══ 为什么要换掉 MessageBox ═══
/// 用户图2 报「关于页面，设计和其他的 UI 非常割裂」：原来那枚是 Windows 系统 MessageBox——
/// 浅灰系统底、蓝色圆形 info 图标、系统按钮字体，跟整套深色自绘界面放在一起像贴上去的异物。
/// 这里改成与 <see cref="SettingsDialog"/> 同一套语言：窗体底用 Theme.WindowBackground、
/// 文字用 Theme 字体阶梯、图标用系统自带的 Segoe Fluent 字形（不引资源）、底部一枚自绘圆角按钮。
/// 窗口外框的直角与标题栏仍由 Windows 上色（产品内禁 P/Invoke，改不了 DWM 圆角），
/// 这一点与设置／导出对话框一致，不再是「只有这一面是系统灰」。
///
/// ═══ 布局沿用先量后摆 ═══
/// 宽度取「最宽那行说明不折行」的现量值与下限的较大者；纵向交给 FlowLayoutPanel 叠行，
/// 窗体高度由 <see cref="OnLayout"/> 按内容收口。换肤后说明文字可能折成不同行数，高度重收一次。
/// </summary>
public sealed class AboutDialog : Form
{
    private const int Pad = 20;
    private const int Gap = 10;

    private readonly FlowLayoutPanel _body = new()
    {
        FlowDirection = FlowDirection.TopDown,
        WrapContents = false,
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
    };

    private readonly ChromeButton _close;

    public AboutDialog()
    {
        Text = "关于";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        // 全部宽度都是运行时按字体现量的设备像素，关掉字体自动缩放，免得 ClientSize 被二次缩放
        // 而自绘文字仍按原字号画——那正是设置对话框那一族「右侧裁切」的根因。
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Theme.WindowBackground;
        ForeColor = Theme.TextPrimary;
        Font = Theme.UiFont;

        string[] lines =
        {
            "完全离线运行，不上传任何截图内容",
            "导出按源图像素计算，不会因为预览缩放而变小",
            "PNG 走自研编解码，承诺字节级无损；JPEG 是有损格式，界面会如实标注",
            // 第六十五改：原来这里还有一行「零第三方依赖：整个程序只用 .NET 自带的库」。
            // 第六十四改已定调「可以有依赖」，这句对外承诺与产品方向矛盾，发布前删掉；
            // README 的同款措辞（无任何第三方依赖）也一并撤了。
        };

        // 先量后摆：宽度＝最宽那行不折行的现量值 + 两侧留白。
        // 第五十四改②（用户图2红框）：原来的 460 下限比最宽那行还宽出一大截，右侧是一整块
        // 谁也不用的空白。下限降到只保证「关闭」按钮那行站得开的 320，宽度真正贴住内容。
        // 第五十七改①（用户图1红框「右侧空白、文字全缩在左边」）：再加 4px 余量——
        // 下面正文行改成定宽不折行排版，量宽与排宽之间若差 1–2px（GDI 取整），
        // 整行就会被挤折成两行、右半留白。4px 把这条缝堵死。
        int widest = 0;
        foreach (string line in lines)
            widest = Math.Max(widest, TextRenderer.MeasureText("· " + line, Theme.UiFont).Width);
        widest = Math.Max(widest, TextRenderer.MeasureText(TitleText, Theme.UiFontTitle).Width);
        int clientWidth = Math.Max(320, widest + Pad * 2 + 4);
        // 第五十七改①：内容宽在构造期只认这个算出来的常量。_body 是 AutoSize GrowAndShrink，
        // 每加一枚子控件它就按「已加子控件」收一次宽——构造中途读 _body.Width 读到的是
        // 标题行那一截（现量 296px），正文行就会全缩在左边（第一趟电池当场抓到）。
        int contentWidth = clientWidth - Pad * 2;
        ClientSize = new Size(clientWidth, 300);   // 高度占位，真值由 OnLayout 收口

        _body.Location = new Point(Pad, Pad);
        _body.Width = contentWidth;
        _body.BackColor = Theme.WindowBackground;
        Controls.Add(_body);

        // 标题行：一枚相框山形字形 + 产品名与版本（替掉系统那枚蓝色 info 圆图标）。
        var head = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Theme.WindowBackground,
            Margin = new Padding(0, 0, 0, Gap),
        };
        head.Controls.Add(new GlyphLabel { Glyph = ChromeGlyph.Picture });
        head.Controls.Add(new Label
        {
            Text = TitleText,
            AutoSize = true,
            Font = Theme.UiFontTitle,
            ForeColor = Theme.TextPrimary,
            BackColor = Theme.WindowBackground,
            Margin = new Padding(10, 6, 0, 0),
        });
        _body.Controls.Add(head);

        // 第五十七改①：正文行改成「定宽 + 高度现量」，不再 AutoSize+MaximumSize。
        // 旧写法在真机 DPI 下 AutoSize 的折行判定比窗体宽度量出来的更保守，
        // 整段文字提前折成两行、缩在左半，右半就是用户红框里那块空白。
        // 现在每行宽度钉成内容宽，高度按 WordBreak 现量：装得下就一行铺满，
        // 真装不下才折行——折不折由同一把尺子说了算。Tag 供换肤重算与判据回读。
        foreach (string line in lines)
        {
            string text = "· " + line;
            _body.Controls.Add(new Label
            {
                Text = text,
                AutoSize = false,
                Width = contentWidth,
                Height = MeasureLineHeight(text, Theme.UiFont, contentWidth),
                TextAlign = ContentAlignment.TopLeft,
                Font = Theme.UiFont,
                ForeColor = Theme.TextSecondary,
                BackColor = Theme.WindowBackground,
                Margin = new Padding(0, 0, 0, 6),
                Tag = "about-line",
            });
        }

        {
            const string tail = "精度判据与已知缺口见仓库里的 docs/精度红线.md。";
            _body.Controls.Add(new Label
            {
                Text = tail,
                AutoSize = false,
                Width = contentWidth,
                Height = MeasureLineHeight(tail, Theme.UiFontSmall, contentWidth),
                TextAlign = ContentAlignment.TopLeft,
                Font = Theme.UiFontSmall,
                ForeColor = Theme.TextDisabled,
                BackColor = Theme.WindowBackground,
                Margin = new Padding(0, Gap, 0, Gap),
                Tag = "about-line",
            });
        }

        _close = new ChromeButton
        {
            Text = "关闭",
            Kind = ChromeButtonKind.Secondary,
            Width = 96,
            Margin = new Padding(0),
        };
        _close.Click += (_, _) => Close();
        // 第五十四改②（用户图2）：关闭按钮水平居中。footer 定宽成正文宽，按钮左边距取
        // (正文宽 − 按钮宽) / 2——宽度是构造期定死的现量值，不随换肤变化，居中一次即稳。
        var footer = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoSize = false,
            Width = contentWidth,
            Height = _close.Height + 8,
            BackColor = Theme.WindowBackground,
            Margin = new Padding(0),
        };
        footer.Controls.Add(_close);
        _close.Margin = new Padding(Math.Max(0, (footer.Width - _close.Width) / 2), 4, 0, 4);
        _body.Controls.Add(footer);

        AcceptButton = _close;
        CancelButton = _close;
        Theme.Changed += OnThemeChanged;
    }

    private const string TitleText = "LongStitch · 长截图拼合工具 v0.1";

    /// <summary>「关闭」那枚按钮（冒烟工装可直接真点）。</summary>
    public Button CloseButtonForTest => _close;

    /// <summary>第五十七改①工装口：正文行全量（判每行铺满内容宽、高度装得下整段文字）。</summary>
    public IReadOnlyList<Label> BodyLinesForTest =>
        _body.Controls.OfType<Label>().Where(l => Equals(l.Tag, "about-line")).ToArray();

    /// <summary>第五十七改①工装口：内容宽（正文行宽应恰好等于它）。</summary>
    public int ContentWidthForTest => _body.Width;

    /// <summary>
    /// 第五十七改①：一行正文在给定宽度下需要的高度（WordBreak 现量）。
    /// 装得下＝一枚字高；真装不下才按折行数长高。量高与排行用的是同一把尺子。
    /// </summary>
    private static int MeasureLineHeight(string text, Font font, int width) =>
        TextRenderer.MeasureText(text, font, new Size(width, int.MaxValue),
            TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height;

    private void OnThemeChanged(ThemePalette old, ThemePalette current)
    {
        ThemeApplicator.Apply(this);
        // 换肤可能换字体阶梯：正文行是定宽定高的，高度要按新字体重新现量，
        // 否则新字更高时行尾会被裁（第五十一改那一族）。
        foreach (Control c in _body.Controls)
            if (c is Label line && Equals(line.Tag, "about-line"))
                line.Height = MeasureLineHeight(line.Text, line.Font, _body.Width);
        PerformLayout();
    }

    /// <summary>窗体高度跟着内容走（与设置／导出对话框同一套写法，带变化闸防自激）。</summary>
    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        int want = _body.Bottom + Pad;
        if (want > 0 && want != ClientSize.Height)
            ClientSize = new Size(ClientSize.Width, want);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) Theme.Changed -= OnThemeChanged;
        base.Dispose(disposing);
    }

    /// <summary>
    /// 标题行那枚图标：用系统自带字形画在一个固定方盒里，垂直方向与旁边的标题文字对齐。
    /// 字形字体不在本机时 <see cref="Chrome.Glyph"/> 自己跳过（不画方框），标题文字仍在。
    /// </summary>
    private sealed class GlyphLabel : Control
    {
        internal char Glyph { get; set; }

        public GlyphLabel()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Size = new Size(30, 30);
            BackColor = Theme.WindowBackground;
            TabStop = false;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            using var path = Chrome.RoundRect(new Rectangle(0, 0, Width - 1, Height - 1), Chrome.RadiusSmall);
            using var fill = new SolidBrush(Chrome.Sunken);
            e.Graphics.FillPath(fill, path);
            Chrome.Glyph(e.Graphics, Glyph, new Rectangle(0, 0, Width, Height), Theme.TextSecondary, Chrome.IconFont);
        }
    }
}