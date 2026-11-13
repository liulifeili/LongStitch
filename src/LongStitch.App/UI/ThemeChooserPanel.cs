using LongStitch.Core.Model;
using LongStitch.Core.ProjectIO;

namespace LongStitch.App.UI;

/// <summary>
/// 设置里的「外观」那一块（第四十八改）：白天／黑夜两枚按钮 + 一枚「跟随系统」勾选按钮。
///
/// ═══ 为什么单独成一个控件而不是直接写在对话框里 ═══
/// 本项目的离屏工装不 Show 窗口，而 <c>Button.PerformClick()</c> 在没 Show 过的 Form 上
/// 会被 CanSelect 那道闸静默吃掉（第四十六改那条"确认按钮按了没反应"的判据就是这么红出来的）。
/// 把这一块做成独立的 <see cref="Panel"/>，判据就能像量 ToolPanel 那样直接 new 一个出来真点，
/// 量到的是"按钮 → 出口 → 配色"整条通路，而不是只量一个处理函数。
///
/// ═══ 三枚按钮的分工 ═══
/// · 白天／黑夜：互斥的两枚按钮，点哪一枚就立刻切到哪一档，同时把「跟随系统」关掉。
///   用 <c>Click</c> 而不是 <c>CheckedChanged</c>：跟随系统档下某一枚可能<b>已经是选中态</b>
///   （系统就是那一档），此时点它 CheckedChanged 不会发，用户看到的就是"点了没反应"。
///   Click 每次真点都发，所以这一下总是有效——它把"跟随"改成"手动钉在这一档"。
/// · 跟随系统：勾上就把决定权交给 Windows；取消勾上则把<b>当前生效的那一档</b>钉成手动选择
///   （不是退回黑夜——用户看着浅色界面取消跟随，结果界面跳回黑夜，那是 surprises）。
///
/// ═══ 不许出现的形状 ═══
/// 禁用按钮。跟随系统开着时两枚按钮照样可按（按了就转为手动），所以这里一枚灰按钮都没有。
/// </summary>
public sealed class ThemeChooserPanel : Panel
{
    private const int Pad = 14;
    private const int Gap = 8;

    private readonly FlowLayoutPanel _body = new()
    {
        FlowDirection = FlowDirection.TopDown,
        WrapContents = false,
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
    };

    private readonly RadioButton _day;
    private readonly RadioButton _night;
    private readonly CheckBox _follow;
    private readonly Label _systemLine;
    private readonly Label _effectiveLine;
    private readonly Label _noteLine;
    private readonly ThemePreviewStrip _preview;

    /// <summary>回填勾选态时压住事件，免得"程序设值"被当成"用户又点了一下"。</summary>
    private bool _syncing;

    /// <summary>这一块按当前 DPI 量出来的应有宽度。对话框拿它定自己的宽度（先量后摆）。</summary>
    public int PreferredWidth { get; }

    public ThemeChooserPanel()
    {
        // ── 先量后摆（第四十七改那一课）：所有列宽都从"这段字在当前字体下占多少像素"算出来，
        //    不写死 96 DPI 的坐标。150% 缩放下字体渲染宽 1.5 倍，写死的列必然重叠。
        int leadWidth = TextRenderer.MeasureText(LeadText, Theme.UiFont).Width;
        int followNoteWidth = TextRenderer.MeasureText(FollowNoteText, Theme.UiFontSmall).Width;
        int persistWidth = TextRenderer.MeasureText(PersistText, Theme.UiFontSmall).Width;
        int buttonWidth = Math.Max(96, Math.Max(
            TextRenderer.MeasureText("白天", Theme.UiFontBold).Width + 34,
            TextRenderer.MeasureText("黑夜", Theme.UiFontBold).Width + 34));
        int followWidth = TextRenderer.MeasureText("跟随系统深浅色", Theme.UiFont).Width + 30;
        int previewWidth = ThemePreviewStrip.MeasurePreferredWidth();

        // 说明文字允许折行，但折行后的宽度不能超过这一块；取"最宽那行不折"与"预览条"两者的较大值，
        // 再给说明文字一个折行上限（第五十四改④从 460 放宽到 560，用户嫌设置页窄而高），于是长句折两行而不是把窗体撑到半屏宽。
        int contentWidth = Math.Max(Math.Max(leadWidth, followWidth), Math.Max(buttonWidth * 2 + Gap, previewWidth));
        contentWidth = Math.Max(contentWidth, Math.Min(560, Math.Max(followNoteWidth, persistWidth)));
        PreferredWidth = contentWidth + Pad * 2;
        Width = PreferredWidth;

        // 第五十四改④（用户图4黄框）：这一块原来整块铺 PanelBackground，与对话框的
        // WindowBackground 拼出一圈突兀的异色底纹。统一成 WindowBackground，设置页整面同色。
        BackColor = Theme.WindowBackground;
        Font = Theme.UiFont;
        ForeColor = Theme.TextPrimary;

        _body.Location = new Point(Pad, Pad);
        _body.Width = contentWidth;
        _body.BackColor = Theme.WindowBackground;
        Controls.Add(_body);

        _body.Controls.Add(Section("外观"));
        _body.Controls.Add(Note(LeadText, Theme.UiFont, Theme.TextPrimary, contentWidth));

        // ── 白天／黑夜：两枚互斥的"按钮式单选" ──
        var modeRow = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0, 0, 0, Gap),
            BackColor = Theme.WindowBackground,
        };
        _day = MakeModeButton("白天");
        _night = MakeModeButton("黑夜");
        _day.Width = buttonWidth;
        _night.Width = buttonWidth;
        // Click 而不是 CheckedChanged：理由见类注释（已经是选中态时点它也要有效）。
        _day.Click += (_, _) => ThemeController.SetMode(ThemePreference.Light);
        _night.Click += (_, _) => ThemeController.SetMode(ThemePreference.Dark);
        modeRow.Controls.Add(_day);
        modeRow.Controls.Add(_night);
        _body.Controls.Add(modeRow);

        // ── 跟随系统 ──
        // 第五十一改：换自绘 ChromeCheckBox（与面板其他勾选框同一形状语言）
        _follow = new ChromeCheckBox
        {
            Text = "跟随系统深浅色",
            AutoSize = true,
            Font = Theme.UiFont,
            ForeColor = Theme.TextPrimary,
            BackColor = Theme.WindowBackground,
            Margin = new Padding(0, 0, 0, 8),
            MinimumSize = new Size(0, Theme.MinTouchTarget - 10),
        };
        _follow.CheckedChanged += (_, _) =>
        {
            if (_syncing) return;
            // 取消跟随时把当前生效的那一档钉住，而不是跳回默认——用户看着哪一档就该留在哪一档。
            ThemeController.SetMode(_follow.Checked
                ? ThemePreference.FollowSystem
                : (Theme.IsDark ? ThemePreference.Dark : ThemePreference.Light));
        };
        _body.Controls.Add(_follow);
        _body.Controls.Add(Note(FollowNoteText, Theme.UiFontSmall, Theme.TextSecondary, contentWidth));

        _systemLine = Note("", Theme.UiFontSmall, Theme.TextSecondary, contentWidth);
        _effectiveLine = Note("", Theme.UiFont, Theme.TextPrimary, contentWidth);
        _body.Controls.Add(_systemLine);
        _body.Controls.Add(_effectiveLine);

        _preview = new ThemePreviewStrip { Width = contentWidth, Margin = new Padding(0, 4, 0, Gap) };
        _body.Controls.Add(_preview);

        _noteLine = Note("", Theme.UiFontSmall, Theme.Warning, contentWidth);
        _noteLine.Visible = false;
        _body.Controls.Add(_noteLine);
        _body.Controls.Add(Note(PersistText, Theme.UiFontSmall, Theme.TextDisabled, contentWidth));

        // 换肤与换档都要回填：配色变了预览条要重画，档位变了勾选态与那两行读数要跟上。
        Theme.Changed += OnThemeChanged;
        ThemeController.ModeChanged += OnModeChanged;
        SyncFromController();
    }

    private const string LeadText = "界面配色（点一下立刻生效，并记住到下次启动）";
    private const string FollowNoteText = "勾上之后由 Windows 的「个性化 → 颜色 → 应用模式」决定，2 秒内跟上；" +
                                          "直接点白天或黑夜就会关掉跟随。";
    private static string PersistText => "偏好文件：" + AppPreferences.StorePath;

    // ── 判据出口：三枚真控件 + 那两行读数 ──────────────────────
    /// <summary>「白天」那枚按钮（判据直接 PerformClick，量整条通路）。</summary>
    public RadioButton DayButtonForTest => _day;

    /// <summary>「黑夜」那枚按钮。</summary>
    public RadioButton NightButtonForTest => _night;

    /// <summary>「跟随系统深浅色」那枚勾选按钮。</summary>
    public CheckBox FollowCheckForTest => _follow;

    /// <summary>「系统当前：…」那一行的原文。</summary>
    public string SystemLineForTest => _systemLine.Text;

    /// <summary>「现在生效：…」那一行的原文。</summary>
    public string EffectiveLineForTest => _effectiveLine.Text;

    /// <summary>说明行（读不到系统／偏好写不进去时才出现）。null 表示这一行没显示。</summary>
    public string? NoteLineForTest => _noteLine.Visible ? _noteLine.Text : null;

    /// <summary>预览条（判据读它有没有真的按新配色重画）。</summary>
    public ThemePreviewStrip PreviewForTest => _preview;

    private RadioButton MakeModeButton(string text) => new()
    {
        Text = text,
        Appearance = Appearance.Button,
        AutoCheck = true,
        AutoSize = false,
        Height = Theme.MinTouchTarget,
        FlatStyle = FlatStyle.Flat,
        Font = Theme.UiFontBold,
        TextAlign = ContentAlignment.MiddleCenter,
        Margin = new Padding(0, 0, Gap, 0),
        BackColor = Theme.WindowBackground,
        ForeColor = Theme.TextPrimary,
        FlatAppearance = { BorderColor = Theme.PanelBorder, BorderSize = 1 },
    };

    private static Label Section(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Font = Theme.UiFontBold,
        ForeColor = Theme.TextPrimary,
        BackColor = Theme.WindowBackground,
        Margin = new Padding(0, 0, 0, 6),
    };

    /// <summary>
    /// 一行会自动折行的说明文字。<c>MaximumSize</c> 的宽度是量出来的 contentWidth，
    /// 高度给 0 = 不限，于是长句折行、窗体高度跟着内容走（不写死高度，就不会压住下面的按钮）。
    /// </summary>
    private static Label Note(string text, Font font, Color color, int width) => new()
    {
        Text = text,
        AutoSize = true,
        MaximumSize = new Size(width, 0),
        Font = font,
        ForeColor = color,
        BackColor = Theme.WindowBackground,
        Margin = new Padding(0, 2, 0, 8),
    };

    private void OnThemeChanged(ThemePalette old, ThemePalette current)
    {
        // 自己这一块也要翻：按钮底色、说明文字色都是构造期抄下来的值。
        ThemeApplicator.Apply(this);
        SyncFromController();
        _preview.Invalidate();
    }

    private void OnModeChanged() => SyncFromController();

    /// <summary>
    /// 把控制器那一头的状态回填到三枚按钮与两行读数上。
    /// 回填期间压住 CheckedChanged，否则"程序设值"会被当成用户点击、再写一次盘。
    /// </summary>
    private void SyncFromController()
    {
        _syncing = true;
        try
        {
            bool dark = Theme.IsDark;
            _follow.Checked = ThemeController.Mode == ThemePreference.FollowSystem;
            _night.Checked = dark;
            _day.Checked = !dark;
            // 选中那一枚压强调色底 + 白字，没选中的底色比面板深/浅一档：
            // 两档配色下都一眼看得出"现在在哪一档"，不依赖系统给的按钮推入效果。
            PaintModeButton(_day, !dark);
            PaintModeButton(_night, dark);

            _systemLine.Text = ThemeController.Mode == ThemePreference.FollowSystem
                ? SystemTheme.Describe()
                : "系统当前：" + (SystemTheme.TryReadIsLight() switch
                {
                    true => "浅色（白天）",
                    false => "深色（黑夜）",
                    null => "读不到",
                }) + "（未跟随，仅供对照）";
            _effectiveLine.Text = $"现在生效：{ThemeController.EffectiveLabel}" +
                                  (ThemeController.Mode == ThemePreference.FollowSystem ? "（跟随系统）" : "（手动选择）");
            _noteLine.Text = ThemeController.Note ?? "";
            _noteLine.Visible = ThemeController.Note is { Length: > 0 };
        }
        finally
        {
            _syncing = false;
        }
        // 说明行显示/隐藏会改变内容高度，交给 OnLayout 收。
        PerformLayout();
        _preview.Invalidate();
    }

    private static void PaintModeButton(RadioButton button, bool selected)
    {
        button.BackColor = selected ? Theme.Accent : Theme.WindowBackground;
        button.ForeColor = selected ? Color.White : Theme.TextPrimary;
        button.FlatAppearance.BorderColor = selected ? Theme.Accent : Theme.PanelBorder;
        button.Invalidate();
    }

    /// <summary>
    /// 高度跟着内容走：说明行换行变高、说明行出现/消失，都把这一块撑到刚好装下。
    /// 写死高度会在长句折行时把下面的按钮压住——那正是第四十七改用户报的"元素重叠"那一族。
    /// </summary>
    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        int want = _body.Bottom + Pad;
        if (want > 0 && want != Height) Height = want;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Theme.Changed -= OnThemeChanged;
            ThemeController.ModeChanged -= OnModeChanged;
        }
        base.Dispose(disposing);
    }
}