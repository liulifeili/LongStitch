namespace LongStitch.App.UI;

/// <summary>
/// 工作台配色与字体。
///
/// 遵循模拟 UI 需求 §6：深色中性底、图片保持原色、蓝色只用于"当前可操作的拼缝"，
/// 并且 <b>✓ / ! 必须同时有图标与文字</b>——状态不能只靠颜色表达（无障碍要求）。
///
/// ═══ 第四十八改：颜色不再是写死的字段 ═══
/// 用户要「白天黑夜切换」与「是否跟随系统」，于是颜色搬进 <see cref="ThemePalette"/> 的两套实例，
/// 这里只留一批<b>属性</b>转发到当前那一套。对调用方来说写法一个字没变（还是 <c>Theme.Accent</c>），
/// 但值可以在运行时换掉。字体与尺寸常量不随主题变，仍是 static readonly。
///
/// ═══ 换肤之后必须做的两件事（都在 Changed 事件上）═══
/// ① 控件树：构造期赋给 BackColor/ForeColor 的那些颜色是<b>抄下来的值</b>，不会自己跟着变，
///    要靠 <see cref="ThemeApplicator"/> 按"旧档 → 新档"翻译一遍；
/// ② 常驻画具：StitchCanvasControl 与 BusyOverlay 里那些 static readonly 的 Pen/SolidBrush
///    也是抄下来的值，但它们各自订阅了 Changed，就地把对象的 Color 改掉
///    （字段仍是 readonly、对象仍是同一枚，所以"画具提静态、不许每帧新建"那条红线一个字没动）。
///
/// ═══ 线程 ═══
/// 换肤与绘制都在 UI 线程上发生（设置对话框、轮询 Timer、窗口激活三条路都是 UI 线程），
/// 所以这里不加锁。真要从别的线程改主题，得先 BeginInvoke 回 UI 线程——这条写在
/// <see cref="UsePalette"/> 的注释里，免得下一个人以为可以随手并发调。
/// </summary>
public static class Theme
{
    private static ThemePalette _current = ThemePalette.Dark;
    private static Dictionary<int, int> _remap = ThemePalette.Dark.BuildRemapTo(ThemePalette.Dark);

    /// <summary>当前生效的那一套配色。</summary>
    public static ThemePalette Current => _current;

    /// <summary>当前是不是黑夜档。判据与界面读数都问这一枚，不要自己去比颜色。</summary>
    public static bool IsDark => _current.IsDark;

    /// <summary>
    /// 配色真的换了一套时广播一次（参数：旧的那套、新的那套）。
    /// <b>同一套重复 UsePalette 不广播</b>——跟随系统那枚轮询每 2 秒问一次注册表，
    /// 要是每次都广播，整棵控件树就会被白翻一遍、画布白重画一帧。
    /// </summary>
    public static event Action<ThemePalette, ThemePalette>? Changed;

    /// <summary>
    /// 换到指定的一套配色。<b>只能在 UI 线程上调用</b>（理由见类注释）。
    /// 换完立刻广播，订阅者各自把抄下来的颜色刷新；这一趟是同步的，
    /// 所以调用返回时屏上该用的新值已经就位，判据不需要泵消息去等。
    /// </summary>
    public static void UsePalette(ThemePalette palette)
    {
        if (ReferenceEquals(_current, palette)) return;
        ThemePalette old = _current;
        _remap = old.BuildRemapTo(palette);
        _current = palette;
        Changed?.Invoke(old, palette);
    }

    /// <summary>
    /// 把一枚颜色从<b>上一套</b>配色翻译到当前这套。认不得的原样返回。
    /// 控件树换肤走的就是这一条：它只认调色板里那几枚，所以用户图片、透明色、
    /// 以及故意写死的白字都不会被误翻。
    /// </summary>
    public static Color Translate(Color color) =>
        _remap.TryGetValue(color.ToArgb(), out int argb) ? Color.FromArgb(argb) : color;

    // ── 底色 ────────────────────────────────────────────────
    public static Color WindowBackground => _current.WindowBackground;
    public static Color PanelBackground => _current.PanelBackground;
    public static Color PanelBorder => _current.PanelBorder;
    public static Color CanvasBackground => _current.CanvasBackground;

    /// <summary>左栏拼缝列表那一块的底色（原来在 MainForm 里写死，现在两档各有一枚）。</summary>
    public static Color ListBackground => _current.ListBackground;

    /// <summary>棋盘格：透明区域必须看得见，否则用户会以为"内容丢了"。</summary>
    public static Color CheckerLight => _current.CheckerLight;
    public static Color CheckerDark => _current.CheckerDark;

    // ── 文字 ────────────────────────────────────────────────
    public static Color TextPrimary => _current.TextPrimary;
    public static Color TextSecondary => _current.TextSecondary;
    public static Color TextDisabled => _current.TextDisabled;

    // ── 强调与状态 ──────────────────────────────────────────
    /// <summary>唯一的交互蓝：选中态、可点拼缝。</summary>
    public static Color Accent => _current.Accent;
    public static Color AccentSoft => _current.AccentSoft;

    /// <summary>通过：绿勾。必须配"自动匹配/手动确认"文字。</summary>
    public static Color Passed => _current.Passed;

    /// <summary>需检查：琥珀感叹号。必须配原因文字。</summary>
    public static Color Warning => _current.Warning;

    /// <summary>手动待确认：与"自动通过"视觉上可区分（需求 §8）。</summary>
    public static Color ManualPending => _current.ManualPending;

    public static Color Danger => _current.Danger;

    /// <summary>调试叠层：图1 有效区域（绿框，与需求八那张示意图里的"图1"同色）。两档同值。</summary>
    public static Color OverlayBefore => _current.OverlayBefore;

    /// <summary>调试叠层：图2 有效区域（黄框）。两档同值。</summary>
    public static Color OverlayAfter => _current.OverlayAfter;

    /// <summary>调试叠层：红色箭头（表示"这两张图可以沿上下方向挪来对对齐"）。两档同值。</summary>
    public static Color OverlayAlign => _current.OverlayAlign;

    // ── 字体（不随主题变）────────────────────────────────────
    // 第五十改把整条字体阶梯抬了一档：原来正文 9pt / 标签 8.25pt，在 100% DPI 上只有
    // 12px / 11px，一屏读下来眼睛要凑近屏。现在正文 9.75pt（13px）、标签 9pt（12px）、
    // 微读数 8.25pt（11px）、标题 11.25pt（15px）。层级靠「字号 + 字重 + 颜色」三档一起拉开，
    // 而不是原来那样所有文字挤在同一档灰里。
    // 注意：菜单顶级项被 <c>ui/菜单栏四个顶级项字体必须同一枚</c> 钉成 UiFontSmall，
    // 所以改这一枚会同时改菜单——那是判据想要的（它比的是 Theme.UiFontSmall，不是写死的 8.25）。
    public static readonly Font UiFont = new("Microsoft YaHei UI", 9.75f);
    public static readonly Font UiFontSmall = new("Microsoft YaHei UI", 9f);
    public static readonly Font UiFontBold = new("Microsoft YaHei UI", 9.75f, FontStyle.Bold);
    public static readonly Font MonoFont = new("Consolas", 9f);
    public static readonly Font StatusFont = new("Microsoft YaHei UI", 9f);

    /// <summary>面板标题／对话框标题那一档（第五十改新增）。</summary>
    public static readonly Font UiFontTitle = new("Microsoft YaHei UI", 11.25f, FontStyle.Bold);

    /// <summary>角标、计数 chip、行内元信息那一档（第五十改新增）。</summary>
    public static readonly Font UiFontMicro = new("Microsoft YaHei UI", 8.25f);

    /// <summary>按钮/行高的最小尺寸（需求 §6：主要点击目标至少 36×36）。</summary>
    public const int MinButtonHeight = 32;
    public const int MinTouchTarget = 36;

    /// <summary>状态的可读文字。图标之外必须有这句，不能只靠颜色。</summary>
    public static string StatusText(Core.Model.SeamStatus status) => status switch
    {
        Core.Model.SeamStatus.Pending => "待分析",
        Core.Model.SeamStatus.Analyzing => "分析中",
        Core.Model.SeamStatus.AutoPassed => "✓ 自动匹配",
        Core.Model.SeamStatus.NeedsReview => "! 需检查",
        Core.Model.SeamStatus.ManualPending => "! 手动待确认",
        Core.Model.SeamStatus.ManualConfirmed => "✓ 手动确认",
        _ => "未知",
    };

    /// <summary>状态色（但界面不许只依赖它——同时一定显示 StatusText）。</summary>
    public static Color StatusColor(Core.Model.SeamStatus status) => status switch
    {
        Core.Model.SeamStatus.AutoPassed or Core.Model.SeamStatus.ManualConfirmed => Passed,
        Core.Model.SeamStatus.NeedsReview => Warning,
        Core.Model.SeamStatus.ManualPending => ManualPending,
        _ => TextSecondary,
    };
}