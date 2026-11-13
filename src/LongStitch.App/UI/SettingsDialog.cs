namespace LongStitch.App.UI;

/// <summary>
/// 「设置」对话框（第四十八改新建）。
///
/// ═══ 为什么这个窗口是这一轮才出现的 ═══
/// 用户要「在设置里弄一个白天黑夜切换的以及是否跟随系统的按钮」。在此之前本程序<b>没有任何设置界面</b>，
/// 也没有任何跨启动的偏好存储——所以这一轮不是"往设置里加一项"，而是把设置这一面从零建起来：
/// 外观档位（<see cref="ThemeChooserPanel"/>）+ 落盘（<see cref="Core.ProjectIO.AppPreferences"/>）
/// + 启动时读回（<see cref="ThemeController.Load"/>，只在 Program.cs 调）。
///
/// ═══ 布局沿用第四十七改那一课：先量后摆 ═══
/// 宽度取"外观那一块按当前 DPI 量出来的应有宽度"，纵向交给 FlowLayoutPanel 叠行，
/// 窗体高度由 <see cref="OnLayout"/> 按内容收口。一个写死的坐标都没有，
/// 所以 150% 缩放、换字体、说明文案折行都不会把下面的按钮压住。
/// 判据 <c>ui/设置对话框任何控件不许互相重叠也不许出界</c>。
///
/// ═══ 即时生效，没有"确定/应用" ═══
/// 点白天／黑夜／跟随系统当场就换、当场就写盘，所以这里只留「恢复默认」与「关闭」。
/// 少一枚"确定"就少一种"我明明点了怎么没变"的困惑；关掉窗口不会把选择回滚。
/// </summary>
public sealed class SettingsDialog : Form
{
    private const int Pad = 16;
    private const int Gap = 10;

    private readonly FlowLayoutPanel _body = new()
    {
        FlowDirection = FlowDirection.TopDown,
        WrapContents = false,
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
    };

    private readonly ThemeChooserPanel _chooser = new();
    private readonly Button _reset;
    private readonly Button _close;

    public SettingsDialog()
    {
        Text = "设置";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Theme.WindowBackground;
        ForeColor = Theme.TextPrimary;
        Font = Theme.UiFont;
        // 第五十一改（用户图1）：全部宽度都是运行时按字体现量的「设备像素」（Graphics.MeasureString
        // 在 96 DPI 的 Graphics 上量出来的就是像素数），而 WinForms 默认 AutoScaleMode.Font 会再按
        // 「当前字体高 ÷ 设计字体高」把 ClientSize 二次放大或缩小——量好的 520 被缩成 ~380，
        // 右侧那一列就这样被裁出窗外。关掉自动缩放，量多少就是多少（与 AboutDialog 同一条约定）。
        AutoScaleMode = AutoScaleMode.None;

        // 宽度＝外观那一块现量出来的宽度 + 两侧留白；下限从 420 放宽到 520
        // （第五十四改④：用户嫌设置页「窄而高」，要求稍微宽一点），底部两枚按钮也更站得开。
        int clientWidth = Math.Max(520, _chooser.PreferredWidth + Pad * 2);
        ClientSize = new Size(clientWidth, 420);   // 高度是占位，真值由 OnLayout 收口

        _body.Location = new Point(Pad, Pad);
        _body.Width = clientWidth - Pad * 2;
        _body.BackColor = Theme.WindowBackground;
        Controls.Add(_body);

        _chooser.Margin = new Padding(0, 0, 0, Gap);
        _body.Controls.Add(_chooser);

        // 标题栏与系统自带的打开/保存框由 Windows 上色。改它们要调 DWM 的窗口属性（P/Invoke），
        // 而本项目产品内禁止 P/Invoke，所以这里如实说明，免得用户以为"换肤没换干净"是个 bug。
        _body.Controls.Add(new Label
        {
            Text = "标题栏和系统的「打开／保存」框由 Windows 自己上色，本程序不改它们；画布里的图片永远保持原色。",
            AutoSize = true,
            MaximumSize = new Size(_body.Width, 0),
            Font = Theme.UiFontSmall,
            ForeColor = Theme.TextSecondary,
            BackColor = Theme.WindowBackground,
            Margin = new Padding(0, 0, 0, Gap),
        });

        var footer = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Theme.WindowBackground,
            Margin = new Padding(0),
        };
        _reset = MakeButton("恢复默认（黑夜）");
        _close = MakeButton("关闭");
        _reset.Click += (_, _) => ThemeController.SetMode(Core.Model.ThemePreference.Dark);
        _close.Click += (_, _) => Close();
        footer.Controls.Add(_reset);
        footer.Controls.Add(_close);
        _body.Controls.Add(footer);

        AcceptButton = _close;
        CancelButton = _close;

        // 换肤时这个窗体自己的底色与底部两枚按钮也要翻（它们同样是构造期抄下来的值）。
        Theme.Changed += OnThemeChanged;
    }

    /// <summary>外观那一块。判据读它，也直接真点它上面那三枚按钮。</summary>
    public ThemeChooserPanel ChooserForTest => _chooser;

    /// <summary>「恢复默认（黑夜）」那枚按钮。</summary>
    public Button ResetButtonForTest => _reset;

    /// <summary>「关闭」那枚按钮。</summary>
    public Button CloseButtonForTest => _close;

    private Button MakeButton(string text)
    {
        // 按钮宽度现量：中文十字与两字在 150% 下差出几十像素，写死宽度会把字裁掉。
        int width = Math.Max(96, TextRenderer.MeasureText(text, Theme.UiFont).Width + 28);
        return new Button
        {
            Text = text,
            Width = width,
            Height = Theme.MinButtonHeight,
            AutoSize = false,
            FlatStyle = FlatStyle.Flat,
            Font = Theme.UiFont,
            BackColor = Theme.PanelBackground,
            ForeColor = Theme.TextPrimary,
            Margin = new Padding(0, 0, Gap, 6),
            FlatAppearance = { BorderColor = Theme.PanelBorder, BorderSize = 1 },
        };
    }

    private void OnThemeChanged(ThemePalette old, ThemePalette current)
    {
        ThemeApplicator.Apply(this);
        // 换肤后说明文字可能折成不同的行数，高度重新收一次。
        PerformLayout();
    }

    /// <summary>
    /// 窗体高度跟着内容走：说明行换行、提示行出现/消失，都把窗体撑到刚好装下。
    /// 与 ExportDialog 同一套写法，带变化闸防止 OnLayout 自激。
    /// </summary>
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
}