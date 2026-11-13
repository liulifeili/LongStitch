namespace LongStitch.App.UI;

/// <summary>
/// 一整套配色（第四十八改：白天／黑夜两档）。
///
/// ═══ 为什么要把颜色从 <see cref="Theme"/> 里搬出来 ═══
/// 原来 Theme 的颜色是 <c>static readonly</c> 字段：进程一启动就定型，换主题只能重启。
/// 现在颜色住在两个<b>不可变</b>的调色板实例里，Theme 只是"当前指着哪一套"的那根指针。
/// 换肤 = 换指针 + 广播一次，而不是把二十几个静态字段逐个改写（那种写法迟早漏掉一枚）。
///
/// ═══ 浅色档不是把深色档反相 ═══
/// 反相出来的浅色档会有两个毛病：状态色（橙／黄／绿）在白底上对比度不够，
/// 以及"需检查"与"手动待确认"两枚本来靠明度区分的颜色挤到同一档明度上、再也分不开。
/// 所以浅色档的状态色是重新挑过的：全部压到白底上 ≥ 4.5:1（WCAG AA 正文），
/// 并且两枚琥珀色拉开色相（橙 24° vs 橄榄黄 46°）。判据
/// <c>ui/两套配色的文字与状态色都必须够对比度</c> 量的就是这批比值。
///
/// ═══ 那格判据刻意不量的两对（写在这里，免得下一个人以为漏了）═══
/// ① <see cref="TextDisabled"/>：WCAG 1.4.3 豁免"不可用组件"，把它也压到 4.5 反而会让
///    "能点／不能点"在屏上分不出来。判据只把它的实测比值印成证据。
/// ② 白字压 <see cref="Accent"/>：黑夜档实测 3.65:1，<b>不到 AA 正文</b>；浅色档 5.57:1 达标。
///    这是本轮之前就有的老样子（左栏选中行一直是白字压那枚蓝），本轮没有为了凑绿去改那枚
///    被几百格数着像素的蓝，而是把读数如实印出来、记进 README 的偏差账等人判。
///    判据里那段注释与这一条口径一致：印读数，不当闸。
///
/// ═══ 读数层那三色为什么两档同值 ═══
/// <see cref="OverlayBefore"/>／<see cref="OverlayAfter"/>／<see cref="OverlayAlign"/> 是需求六
/// 逐字钉下来的合同色（绿=图1、黄=图2、红箭头=可拖对齐区），而且它们画在<b>用户截图</b>上，
/// 不是画在界面底色上——换肤根本不改变它们底下压着什么。所以两档同值，既不违约也不影响可读性。
/// </summary>
public sealed class ThemePalette
{
    // ── 底色 ────────────────────────────────────────────────
    public required Color WindowBackground { get; init; }
    public required Color PanelBackground { get; init; }
    public required Color PanelBorder { get; init; }
    public required Color CanvasBackground { get; init; }

    /// <summary>左栏拼缝列表那一块的底色。原来在 MainForm 里写死成 (34,34,36)，换肤必须能翻译它。</summary>
    public required Color ListBackground { get; init; }

    /// <summary>棋盘格两色：透明区域必须看得见，否则用户会以为"内容丢了"。</summary>
    public required Color CheckerLight { get; init; }
    public required Color CheckerDark { get; init; }

    // ── 文字 ────────────────────────────────────────────────
    public required Color TextPrimary { get; init; }
    public required Color TextSecondary { get; init; }
    public required Color TextDisabled { get; init; }

    // ── 强调与状态 ──────────────────────────────────────────
    public required Color Accent { get; init; }
    public required Color AccentSoft { get; init; }
    public required Color Passed { get; init; }
    public required Color Warning { get; init; }
    public required Color ManualPending { get; init; }
    public required Color Danger { get; init; }

    // ── 读数层合同色（两档同值，理由见类注释）─────────────────
    public required Color OverlayBefore { get; init; }
    public required Color OverlayAfter { get; init; }
    public required Color OverlayAlign { get; init; }

    /// <summary>这一套是不是深色档。显式写出来，不靠"与某个实例比引用"去猜。</summary>
    public required bool IsDark { get; init; }

    /// <summary>黑夜：冷灰工作区，强调色与叠层合同色保持独立。</summary>
    public static ThemePalette Dark { get; } = new()
    {
        IsDark = true,
        WindowBackground = Color.FromArgb(19, 22, 29),
        PanelBackground = Color.FromArgb(29, 33, 42),
        PanelBorder = Color.FromArgb(49, 56, 69),
        CanvasBackground = Color.FromArgb(16, 19, 25),
        ListBackground = Color.FromArgb(25, 29, 37),
        CheckerLight = Color.FromArgb(42, 47, 58),
        CheckerDark = Color.FromArgb(32, 37, 47),
        TextPrimary = Color.FromArgb(232, 237, 245),
        TextSecondary = Color.FromArgb(159, 170, 188),
        TextDisabled = Color.FromArgb(102, 114, 134),
        Accent = Color.FromArgb(10, 132, 255),
        AccentSoft = Color.FromArgb(10, 132, 255),
        Passed = Color.FromArgb(48, 209, 88),
        Warning = Color.FromArgb(255, 159, 10),
        ManualPending = Color.FromArgb(255, 205, 90),
        // 原值 (255,69,58) 在面板底上只有 4.43:1，差 AA 正文那一线。这一枚只画左栏那个「缺」字角标，
        // 不进任何导出像素、也没有判据数它的像素，所以按新判据提到 4.73:1（读数层那枚红箭头仍是原值）。
        Danger = Color.FromArgb(255, 83, 72),
        OverlayBefore = Color.FromArgb(48, 209, 88),
        OverlayAfter = Color.FromArgb(255, 205, 90),
        OverlayAlign = Color.FromArgb(255, 69, 58),
    };

    /// <summary>白天。状态色重新挑过，全部满足白底 ≥ 4.5:1。</summary>
    public static ThemePalette Light { get; } = new()
    {
        IsDark = false,
        WindowBackground = Color.FromArgb(240, 243, 248),
        // 刻意<b>不</b>取纯白 (255,255,255)。产品里有八处把 `Color.White` <b>写死</b>当字色
        // （左栏选中行两枚、素材列表选中行两枚、菜单渲染器选中项、外观那枚选中按钮、预览条 chip、
        // 画布把手图章两枚），设计上"两档都压在强调蓝上、永远不换"。翻译表的规矩是"认得才翻"，
        // 一旦纯白成了调色板里的一枚，它就"认得"了：从白天翻回黑夜时那些白字会被当成
        // "旧档面板底"翻成 (38,38,40)——选中行上出现深灰字，正是判据
        // <c>guard/换肤若不翻控件树则底色停在旧档</c> 第一趟红出来的形状。
        // 角色冲突没法在翻译表里表达（同一枚白色，当底色要翻、当字色不能翻），所以在源头消除歧义：
        // 面板底改成差三档的近白，肉眼分不出，白底上的对比度只掉 0.05~0.15（全部仍 ≥ 4.5:1）。
        // 这条不变量由 <c>guard/纯白不许进调色板否则写死的白字会被翻掉</c> 钉住
        // （两档各 19 枚具名色逐个查，再双向查一次翻译结果）。
        PanelBackground = Color.FromArgb(252, 252, 253),
        PanelBorder = Color.FromArgb(215, 222, 233),
        // 画布四周比面板明显暗一档：浅色档里"内容到哪儿为止"全靠这一档差，
        // 否则白底截图铺在白工作台上，用户看不出边界。
        CanvasBackground = Color.FromArgb(227, 232, 240),
        ListBackground = Color.FromArgb(250, 250, 252),
        CheckerLight = Color.FromArgb(247, 247, 250),
        CheckerDark = Color.FromArgb(223, 223, 231),
        // 这一枚刻意<b>不</b>取 (28,28,30)——那正好是黑夜档的 WindowBackground。
        // 翻译表的键是"旧档的颜色"，值若是旧档另一枚颜色，翻第二遍就会把已经翻好的字色再翻一次：
        // 正文 (28,28,30) 会被当成"旧档窗口底"翻成 (243,243,245)，白底上近白字＝整段文字消失。
        // 而"翻第二遍"在产品里真的会发生：设置窗口开着时，主窗口的 ApplyEverywhere 与
        // 对话框自己的 OnThemeChanged 各翻一次同一棵树。所以两档的颜色集合必须互不撞键，
        // 判据 <c>guard/翻译表的目标色不许自己又是旧档的键</c> 把这条不变量钉住。
        // 差两档灰度肉眼分不出，对比度反而更高（面板底上 17.4:1）。
        TextPrimary = Color.FromArgb(26, 26, 28),
        TextSecondary = Color.FromArgb(90, 90, 98),
        TextDisabled = Color.FromArgb(134, 134, 142),
        // 白字压在这枚蓝上要有 5.5:1，所以浅色档的蓝比深色档深一档。
        // 白字压强调蓝的地方有三处：左栏拼缝列表的选中行、设置里选中的那枚白天／黑夜按钮、预览条那枚 chip。
        Accent = Color.FromArgb(0, 102, 204),
        AccentSoft = Color.FromArgb(0, 102, 204),
        Passed = Color.FromArgb(26, 127, 55),
        Warning = Color.FromArgb(178, 80, 0),
        ManualPending = Color.FromArgb(122, 96, 0),
        Danger = Color.FromArgb(198, 40, 40),
        OverlayBefore = Color.FromArgb(48, 209, 88),
        OverlayAfter = Color.FromArgb(255, 205, 90),
        OverlayAlign = Color.FromArgb(255, 69, 58),
    };

    /// <summary>
    /// 按固定顺序交出这一套里的全部颜色，供"旧档 → 新档"的翻译表配对。
    ///
    /// <b>顺序是有讲究的，不许随便排：</b>深色档里 Passed 与 OverlayBefore 是同一个 ARGB、
    /// ManualPending 与 OverlayAfter 也是同一个 ARGB。翻译表按键 overwrite，所以<b>后写的赢</b>。
    /// 这里把语义色（强调／通过／需检查／待确认／危险）排在合同色之后，
    /// 于是"一枚控件底色是通过绿"会被翻成浅色档的通过绿，而不是被合同色那条抢走翻成亮绿。
    /// 反过来排就会出现"换到白天之后绿勾亮得刺眼"这种查半天的毛病。
    /// </summary>
    public IEnumerable<Color> Enumerate()
    {
        yield return WindowBackground;
        yield return PanelBackground;
        yield return PanelBorder;
        yield return CanvasBackground;
        yield return ListBackground;
        yield return CheckerLight;
        yield return CheckerDark;
        yield return TextPrimary;
        yield return TextSecondary;
        yield return TextDisabled;
        yield return OverlayBefore;
        yield return OverlayAfter;
        yield return OverlayAlign;
        yield return Accent;
        yield return AccentSoft;
        yield return Passed;
        yield return Warning;
        yield return ManualPending;
        yield return Danger;
    }

    /// <summary>
    /// 造一张"本档 ARGB → 目标档 ARGB"的翻译表。
    /// 换肤时遍历控件树用的就是它：只翻译<b>认得的</b>颜色，用户图片、透明色、
    /// 以及那些故意写死的白字（选中行上的白字两档都压在强调蓝上）一律原样留着。
    /// </summary>
    public Dictionary<int, int> BuildRemapTo(ThemePalette target)
    {
        var map = new Dictionary<int, int>();
        foreach ((Color from, Color to) in Enumerate().Zip(target.Enumerate()))
            map[from.ToArgb()] = to.ToArgb();
        return map;
    }
}