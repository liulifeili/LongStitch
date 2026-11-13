using LongStitch.App.UI;
using LongStitch.Core;
using LongStitch.Core.Geometry;
using LongStitch.Core.Imaging;
using LongStitch.Core.Model;
using LongStitch.Core.ProjectIO;

namespace LongStitch.Checks;

/// <summary>
/// 第四十八改（白天／黑夜切换 + 是否跟随系统）的判据与反证。
///
/// ═══ 这组格子守的是什么 ═══
/// 用户要的是「在设置里弄一个白天黑夜切换的以及是否跟随系统的按钮」。这句话落到实现上有四段通路，
/// 每一段都能"看起来做了、其实没接上"，所以各量各的，谁都不许替谁作证：
/// <list type="number">
///   <item>偏好：选了什么 → 写进盘 → 下次启动读回来还是那一档（<see cref="PreferencesRoundTrip"/>）。</item>
///   <item>配色：两套调色板本身能不能读（对比度），以及换过去之后<b>屏上真的变了</b>
///         （<see cref="BothPalettesAreReadable"/>、<see cref="ClickingDayNightRepaintsEverything"/>）。</item>
///   <item>跟随系统：读数三态（浅色／深色／读不到）各走一遍，读不到必须退回黑夜<b>并且出声</b>
///         （<see cref="UnreadableSystemFallsBackToDarkAndSpeaks"/>、<see cref="FollowSystemTracksBothReadings"/>）。</item>
///   <item>设置界面：真按钮真按下去有效、控件不许互相重叠（<see cref="SettingsDialogControlsNeverOverlap"/>）。</item>
/// </list>
///
/// ═══ 为什么每格都要沙箱 ═══
/// <see cref="ThemeController"/> 与 <see cref="Theme"/> 都是进程级静态状态。电池一趟要跑三百格，
/// 若某一格把配色留在白天，后面所有按深色底数像素的判据会一起翻红——那不是产品的错，是工装污染。
/// 所以每格都在 <see cref="InSandbox"/> 里跑：偏好写临时文件（绝不碰用户真的 %APPDATA%）、
/// 系统读数用注入的假探针（绝不碰真注册表）、结束时把档位与探针原样还回去。
///
/// ═══ 为什么按钮那一格不挂在窗体上量 ═══
/// 窗体从没 <c>Show()</c> 过时，子按钮在 WinForms 的 CanSelect 那一关就被挡下，
/// <c>PerformClick</c> 是<b>空转</b>（本仓库实测过，已记进红账）。
/// <see cref="ThemeChooserPanel"/> 之所以单独成一个 Panel 而不是直接写在对话框里，就是为了让判据
/// 能像量 ToolPanel 那样 new 一个出来真点。这一格量的因此是"按钮 → 控制器 → 配色 → 控件树 → 预览条"
/// 整条通路，而不是只调一个处理函数。
/// </summary>
public static class ThemeTests
{
    public static void Register()
    {
        Battery.Add("ui", "两套配色的文字与状态色都必须够对比度", BothPalettesAreReadable);
        Battery.Add("ui", "跟随系统读不到时必须退回黑夜并且出声", UnreadableSystemFallsBackToDarkAndSpeaks);
        Battery.Add("ui", "设置对话框任何控件不许互相重叠也不许出界", SettingsDialogControlsNeverOverlap);
        Battery.Add("ui", "外观三档写盘再读回来一字不差", PreferencesRoundTrip);
        Battery.Add("ui", "点白天黑夜立刻换配色并把控件树翻过来", ClickingDayNightRepaintsEverything);
        Battery.Add("ui", "跟随系统那一档真的跟着读数换两趟", FollowSystemTracksBothReadings);
        Battery.Add("ui", "换肤后画布常驻画具就地改色且不新建", CanvasPaintsRecolorInPlace);

        Battery.Add("guard", "对比度尺子必须能判出一枚不够对比的颜色", ContrastRulerHasTeeth);
        Battery.Add("guard", "取消跟随若退回黑夜就会把浅色界面跳掉", UnfollowingMustNotJumpToDark);
        Battery.Add("guard", "偏好文件写坏了必须出声而不是静默当黑夜", BrokenPreferenceMustSpeak);
        Battery.Add("guard", "换肤若不翻控件树则底色停在旧档", WithoutApplicatorControlsKeepOldColors);
        Battery.Add("guard", "翻译表的目标色不许自己又是旧档的键", RemapTargetsMustNotBeOldKeys);
        Battery.Add("guard", "纯白不许进调色板否则写死的白字会被翻掉", PureWhiteMustStayOutOfPalettes);
        Battery.Add("guard", "系统读数没变时广播一次就会让整棵树白翻", UnchangedReadingMustNotBroadcast);
    }

    // ═════════════════════════ 沙箱与通用尺子 ═════════════════════════

    /// <summary>
    /// 在一间"临时偏好文件 + 假系统读数"的沙箱里跑一段，结束后把进程级静态状态原样还回去。
    /// 还回去这件事必须在 <c>finally</c> 里：判据自己抛异常时若不还，后面几百格会跟着一起红，
    /// 而红的原因看起来像产品坏了——那是工装最会骗人的一种失效。
    /// </summary>
    private static void InSandbox(Action<string> body)
    {
        string dir = Path.Combine(Path.GetTempPath(), "ls-theme-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        string prefs = Path.Combine(dir, "preferences.txt");
        Func<bool?> probeBefore = SystemTheme.Probe;
        ThemePreference modeBefore = ThemeController.Mode;
        string? storeBefore = AppPreferences.StoreOverride;
        try
        {
            // 界面那三枚按钮调的是不带路径的 SetMode(mode)——产品运行时就该写用户真的那一份。
            // 判据要真点按钮，所以必须把整进程的存储位置改道到这里，否则跑一趟电池就把
            // 开发者自己 %APPDATA% 里选的档位冲掉（本仓库第一趟就是这么红的：判据量临时文件写没写，
            // 产品却写了真的那一份）。
            AppPreferences.StoreOverride = prefs;
            body(prefs);
        }
        finally
        {
            AppPreferences.StoreOverride = storeBefore;
            SystemTheme.Probe = probeBefore;
            // 写回临时文件而不是用户真的那份：SetMode 的第二个参数就是为这件事留的。
            ThemeController.SetMode(modeBefore, prefs);
            TryDeleteDir(dir);
        }
    }

    private static void TryDeleteDir(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch (IOException) { /* 临时目录删不掉不影响判据结论，下一次系统清理会收 */ }
        catch (UnauthorizedAccessException) { /* 同上 */ }
    }

    /// <summary>WCAG 2.x 的相对亮度。三个通道各自先做 sRGB 逆伽马，再按 0.2126/0.7152/0.0722 加权。</summary>
    private static double Luminance(Color c)
    {
        static double Channel(byte v)
        {
            double s = v / 255.0;
            return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }

    /// <summary>对比度比值 (L亮 + 0.05) / (L暗 + 0.05)。WCAG AA 正文要 ≥ 4.5。</summary>
    private static double Contrast(Color a, Color b)
    {
        double la = Luminance(a), lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    /// <summary>色相（度）。用来量"需检查"与"手动待确认"两枚琥珀色有没有拉开。</summary>
    private static double Hue(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        if (d <= 0) return 0;
        double h = max == r ? (g - b) / d % 6 : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
        return h * 60;
    }

    private static string Rgb(Color c) => $"({c.R},{c.G},{c.B})";

    /// <summary>数一帧里等于某一色的像素有几枚。判据用它证"屏上真的出现了新档的颜色"。</summary>
    private static int CountPixels(Bitmap bmp, Color color)
    {
        int argb = color.ToArgb(), n = 0;
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
                if (bmp.GetPixel(x, y).ToArgb() == argb) n++;
        return n;
    }

    /// <summary>递归找第一枚满足条件的 Label（判据不抄产品文案，只按颜色角色认控件）。</summary>
    private static Label? FindLabel(Control root, Func<Label, bool> match)
    {
        foreach (Control c in root.Controls)
        {
            if (c is Label label && match(label)) return label;
            Label? deeper = FindLabel(c, match);
            if (deeper is not null) return deeper;
        }
        return null;
    }

    /// <summary>数一帧里 ARGB 恰等于某一枚值的像素有几枚（期望值是从内存读回来的，所以按 int 比）。</summary>
    private static int CountArgb(Bitmap bmp, int argb)
    {
        int n = 0;
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
                if (bmp.GetPixel(x, y).ToArgb() == argb) n++;
        return n;
    }

    /// <summary>一帧里出现最多的那枚像素（ARGB 与枚数）。面板底色占绝大多数，所以它就是清底那枚。</summary>
    private static (int Argb, int Count) DominantPixel(Bitmap bmp)
    {
        var tally = new Dictionary<int, int>();
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
            {
                int argb = bmp.GetPixel(x, y).ToArgb();
                tally[argb] = tally.TryGetValue(argb, out int n) ? n + 1 : 1;
            }
        (int Argb, int Count) best = (0, -1);
        foreach (KeyValuePair<int, int> kv in tally)
            if (kv.Value > best.Count) best = (kv.Key, kv.Value);
        return best;
    }

    /// <summary>
    /// 一枚 ARGB 的"亮不亮"（三通道之和）。只用来比<b>方向</b>：浅色档面板底比黑夜档亮，
    /// 那么面板缓冲区读回的清底色也必须更亮。
    /// 为什么不用对比度那把尺：面板缓冲区是预乘 alpha，读回的值已经不是原来那枚颜色，
    /// 拿它算对比度没有意义；但预乘与读回都是单调的，"更亮"这个方向保得住。
    /// </summary>
    private static int Brightness(int argb) => ((argb >> 16) & 255) + ((argb >> 8) & 255) + (argb & 255);

    // ═════════════════════════ 正向判据 ═════════════════════════

    /// <summary>
    /// 两套配色的<b>文字色与状态色</b>压在它们真的会压的那几种底色上，都必须 ≥ 4.5:1（WCAG AA 正文）。
    ///
    /// 量的配对不是随手挑的，是产品里真出现的：正文/次级文字压在窗口底、面板底、左栏列表底上；
    /// 四枚状态色（通过／需检查／手动待确认／危险）压在面板底与列表底上——左栏那个「缺」字角标
    /// 与拼缝行的状态字就是这两处。
    ///
    /// <b>刻意不量的两对，理由写在这里而不是悄悄漏掉：</b>
    /// ① <c>TextDisabled</c>：WCAG 1.4.3 明确豁免"不可用组件"，把它压到 4.5 反而会让"能点/不能点"
    ///    在屏上分不出来。这里只把它的实测比值印成证据，不当判据。
    /// ② 白字压强调蓝（选中行／选中按钮）：黑夜档实测 3.65:1，<b>不到 AA 正文</b>。这是第四十八改
    ///    之前就有的老样子（选中行一直是白字压那枚蓝），本轮没有为了凑绿去改那枚被几百格数着像素的蓝，
    ///    而是把读数如实印出来、记进 README 的偏差账等人判。浅色档同一对是 5.57:1，达标。
    ///
    /// 另量一条"两枚琥珀色分得开"：黑夜档靠明度（比值 1.38），浅色档靠色相（差 20°）。
    /// 判据写成"色相拉开 <b>或</b> 明度拉开"，因为两档用的是不同手段——只钉其中一种会把另一档判红。
    /// </summary>
    private static void BothPalettesAreReadable()
    {
        const double aa = 4.5;
        var failures = new List<string>();
        var notes = new List<string>();

        foreach (ThemePalette p in new[] { ThemePalette.Dark, ThemePalette.Light })
        {
            string tag = p.IsDark ? "黑夜" : "白天";
            Color[] backs = { p.WindowBackground, p.PanelBackground, p.ListBackground };
            string[] backNames = { "窗口底", "面板底", "列表底" };

            foreach ((Color fg, string fgName) in new[] { (p.TextPrimary, "正文"), (p.TextSecondary, "次级文字") })
                for (int i = 0; i < backs.Length; i++)
                {
                    double ratio = Contrast(fg, backs[i]);
                    notes.Add($"{tag} {fgName}{Rgb(fg)} 压 {backNames[i]} = {ratio:F2}");
                    if (ratio < aa) failures.Add($"{tag} {fgName}压{backNames[i]} 只有 {ratio:F2}");
                }

            foreach ((Color fg, string fgName) in new[]
                     { (p.Passed, "通过"), (p.Warning, "需检查"), (p.ManualPending, "手动待确认"), (p.Danger, "危险") })
                foreach (Color back in new[] { p.PanelBackground, p.ListBackground })
                {
                    double ratio = Contrast(fg, back);
                    notes.Add($"{tag} {fgName}{Rgb(fg)} 压 {Rgb(back)} = {ratio:F2}");
                    if (ratio < aa) failures.Add($"{tag} {fgName}压{Rgb(back)} 只有 {ratio:F2}");
                }

            // 两枚琥珀色必须分得开：色相差 ≥ 12° 或相对亮度比 ≥ 1.25，二者有一即可。
            double hueGap = Math.Abs(Hue(p.Warning) - Hue(p.ManualPending));
            double lw = Luminance(p.Warning), lp = Luminance(p.ManualPending);
            double lumRatio = (Math.Max(lw, lp) + 0.05) / (Math.Min(lw, lp) + 0.05);
            Battery.Assert(hueGap >= 12 || lumRatio >= 1.25,
                $"{tag}档「需检查」与「手动待确认」既没拉开色相（差 {hueGap:F1}°）也没拉开明度（比 {lumRatio:F2}）——" +
                "两枚颜色挤到同一档上，用户在左栏分不出哪条缝是哪种状态");
            Battery.Assert(p.Warning.ToArgb() != p.ManualPending.ToArgb(),
                $"{tag}档两枚琥珀色竟然是同一个 ARGB");

            // 豁免与老偏差只印读数，不当判据（理由见方法注释）。
            notes.Add($"{tag} 不可用文字（WCAG 豁免）= {Contrast(p.TextDisabled, p.PanelBackground):F2}");
            notes.Add($"{tag} 白字压强调蓝（选中行，老偏差）= {Contrast(Color.White, p.Accent):F2}");
        }

        // 夹具自证：两套必须真的是两套。若有人把 Light 写成 Dark 的别名，上面所有比值照样绿。
        Battery.Assert(ThemePalette.Dark.WindowBackground.ToArgb() != ThemePalette.Light.WindowBackground.ToArgb(),
            "两套配色的窗口底色相同——「白天」那一档压根没换，上面那些比值是同一套量了两遍");
        Battery.Assert(ThemePalette.Dark.IsDark != ThemePalette.Light.IsDark, "两套的 IsDark 标记必须相反");
        Battery.AssertEqual("", string.Join("；", failures),
            "两套配色的文字与状态色都必须 ≥ 4.5:1（WCAG AA 正文）");

        // 读数层那三枚合同色两档同值：它们画在用户截图上，不是画在界面底色上，换肤不该动它们。
        Battery.AssertEqual(ThemePalette.Dark.OverlayBefore.ToArgb(), ThemePalette.Light.OverlayBefore.ToArgb(), "图1 绿框合同色两档必须同值");
        Battery.AssertEqual(ThemePalette.Dark.OverlayAfter.ToArgb(), ThemePalette.Light.OverlayAfter.ToArgb(), "图2 黄框合同色两档必须同值");
        Battery.AssertEqual(ThemePalette.Dark.OverlayAlign.ToArgb(), ThemePalette.Light.OverlayAlign.ToArgb(), "红箭头合同色两档必须同值");

        foreach (string note in notes) Battery.Evidence(note);
    }

    /// <summary>
    /// 系统读数取不到时（键不在／值类型不对／权限不够／探针自己抛），"跟随系统"必须退回黑夜
    /// <b>并且把这件事说出来</b>。静默退回与"系统本来就是深色"在屏上一模一样，用户只会以为跟随坏了。
    /// 同一趟再量反向对照：读到之后那句说明必须<b>撤掉</b>，否则它会一直挂在界面上吓人。
    /// </summary>
    private static void UnreadableSystemFallsBackToDarkAndSpeaks() => UiSmokeTests.RunOnStaThread(() =>
        InSandbox(prefs =>
        {
            SystemTheme.Probe = () => null;
            ThemeController.SetMode(ThemePreference.FollowSystem, prefs);

            Battery.Assert(Theme.IsDark, "读不到系统读数时必须退回黑夜");
            Battery.Assert(ThemeController.SystemReading is null, "读不到就要如实记成 null，不许装成读到了深色");
            Battery.Assert(ThemeController.Note is { Length: > 0 },
                "读不到系统读数必须留下一句说明——静默退回与「系统本来就是深色」在屏上分不出来");
            Battery.Assert(ThemeController.Note!.Contains("黑夜"),
                $"说明里必须点明退到了哪一档，实际：{ThemeController.Note}");

            // 界面上那一行真的显示出来了（不是只写在控制器字段里）
            using var panel = new ThemeChooserPanel();
            Battery.Assert(panel.NoteLineForTest is not null,
                "设置面板必须把那句说明真的显示出来（NoteLine 此刻是隐藏的）");
            Battery.AssertEqual(ThemeController.Note, panel.NoteLineForTest!, "面板显示的说明必须与控制器那句一字不差");

            // 反向对照①：读到了浅色 → 换到白天，并且把上一轮那句"读不到"撤掉
            SystemTheme.Probe = () => true;
            ThemeController.RefreshSystemReading();
            Battery.Assert(!Theme.IsDark, "读到系统浅色之后必须真的换到白天");
            Battery.Assert(ThemeController.Note is null,
                $"读到之后必须撤掉那句「读不到」，否则它一直挂在界面上吓人；实际还留着：{ThemeController.Note}");
            Battery.Assert(panel.NoteLineForTest is null, "控制器撤了说明，面板那一行也必须跟着隐藏");

            // 反向对照②：探针自己抛异常也算"读不到"，不许把异常甩给 UI
            SystemTheme.Probe = () => throw new InvalidOperationException("工装注入的探针异常");
            ThemeController.SetMode(ThemePreference.Dark, prefs);
            ThemeController.SetMode(ThemePreference.FollowSystem, prefs);
            Battery.Assert(Theme.IsDark, "探针抛异常时同样必须退回黑夜");
            Battery.Assert(ThemeController.Note is { Length: > 0 }, "探针抛异常同样必须出声");
            Battery.AssertEqual("系统当前：读不到（已退回黑夜）", SystemTheme.Describe(),
                "给界面显示的那句话必须如实说「读不到」，不许说成深色或浅色");
            Battery.Evidence("三态各走一遍：null → 黑夜+说明；true → 白天+说明撤掉；探针抛 → 黑夜+说明");
        }));

    /// <summary>
    /// 设置对话框<b>搬到屏外真 Show 一次</b>（见 <see cref="UiSmokeTests.ShowOffscreen"/>：窗体不可见时
    /// 整棵子树的 <c>Visible</c> 一律是 false，按"只量可见叶子"枚举会一枚也枚举不到），
    /// 递归取所有"叶子控件"的屏幕矩形：两两不许有正面积交集、
    /// 且都必须落在窗体客户区内。这与第四十七改重做导出对话框时用的是同一把尺子——
    /// 用户报的「很多元素都重叠」就是标签尾被右边控件盖住，量几何比看截图可靠。
    /// <b>两档各量一遍</b>：宽度虽然由字体现量、与配色无关，但说明行的显示/隐藏会改内容高度，
    /// 只量一档会漏掉"另一档下最后一行被切掉"这种形状。
    /// 夹具自证：叶子数必须 ≥ 10——枚举空了的话"没有重叠"就是空话。
    /// </summary>
    private static void SettingsDialogControlsNeverOverlap() => UiSmokeTests.RunOnStaThread(() =>
        InSandbox(prefs =>
        {
            SystemTheme.Probe = () => false;
            foreach (ThemePreference mode in new[] { ThemePreference.Dark, ThemePreference.Light })
            {
                ThemeController.SetMode(mode, prefs);
                using var dialog = new SettingsDialog();
                // 必须真 Show（搬到屏外、全透明）：窗体不可见时整棵子树的 Visible 一律是 false，
                // 按"只量可见叶子"枚举会一枚也枚举不到——本批第一趟就是这么红成"叶子 0 枚"的。
                UiSmokeTests.ShowOffscreen(dialog);

                var leaves = new List<(string Name, Rectangle Rect)>();
                void Collect(Control root)
                {
                    foreach (Control c in root.Controls)
                    {
                        if (!c.Visible || c.Width <= 0 || c.Height <= 0) continue;
                        if (c.Controls.Count == 0)
                            leaves.Add((c.Text is { Length: > 0 } t ? t : c.GetType().Name,
                                        c.RectangleToScreen(new Rectangle(Point.Empty, c.Size))));
                        else
                            Collect(c);
                    }
                }
                Collect(dialog);
                Battery.Assert(leaves.Count >= 10,
                    $"夹具自证：{ThemeController.ModeLabel}档设置对话框应量到至少 10 枚叶子控件（实际 {leaves.Count}）");

                Rectangle client = dialog.RectangleToScreen(new Rectangle(Point.Empty, dialog.ClientSize));
                var overlaps = new List<string>();
                for (int i = 0; i < leaves.Count; i++)
                    for (int j = i + 1; j < leaves.Count; j++)
                    {
                        Rectangle hit = Rectangle.Intersect(leaves[i].Rect, leaves[j].Rect);
                        if (hit.Width > 0 && hit.Height > 0)
                            overlaps.Add($"「{leaves[i].Name}」×「{leaves[j].Name}」重叠 {hit.Width}×{hit.Height}");
                    }
                Battery.AssertEqual("", string.Join("；", overlaps),
                    $"{ThemeController.ModeLabel}档设置对话框任何两枚控件不许有正面积交集");

                var outside = leaves.Where(l => l.Rect.Left < client.Left || l.Rect.Top < client.Top ||
                                                l.Rect.Right > client.Right || l.Rect.Bottom > client.Bottom)
                                    .Select(l => l.Name).ToList();
                Battery.AssertEqual("", string.Join("；", outside),
                    $"{ThemeController.ModeLabel}档设置对话框任何控件不许画出窗体客户区");
                Battery.Evidence($"{ThemeController.ModeLabel}档：{leaves.Count} 枚叶子两两无交集、全部在客户区内；" +
                                 $"客户区 {dialog.ClientSize.Width}×{dialog.ClientSize.Height}");
                UiSmokeTests.HideOffscreen(dialog);
            }
        }));

    /// <summary>
    /// 三档各写一次盘再读回来，必须还是那一档；文件里那个词也必须是文档承诺的那三个之一。
    /// 另外量"这份文件是给人手改的"这件事：大小写、中文写法、连字符写法都认得。
    /// 夹具自证：三档的 token 必须互不相同——相同的话"写盘再读回来"会串档，而往返判据照样绿。
    /// </summary>
    private static void PreferencesRoundTrip() => InSandbox(prefs =>
    {
        var tokens = new List<string>();
        foreach (ThemePreference mode in new[]
                 { ThemePreference.Dark, ThemePreference.Light, ThemePreference.FollowSystem })
        {
            AppPreferences.Save(new AppPreferences(mode), prefs);
            string text = File.ReadAllText(prefs);
            string token = AppPreferences.Token(mode);
            tokens.Add(token);
            Battery.Assert(text.Contains(AppPreferences.ThemeKey + "=" + token),
                $"写进盘的那一行必须是 {AppPreferences.ThemeKey}={token}，实际文件内容：{text.Trim()}");

            AppPreferences back = AppPreferences.Load(prefs);
            Battery.AssertEqual(mode, back.Theme, $"{token} 写盘再读回来");
            Battery.Assert(AppPreferences.LastLoadNote is null,
                $"正常写正常读不该有任何说明，实际：{AppPreferences.LastLoadNote}");
        }
        Battery.AssertEqual(3, tokens.Distinct().Count(),
            "三档在文件里必须是三个不同的词，否则往返会串档（判据却照样绿）");

        foreach ((string written, ThemePreference expected) in new[]
                 {
                     ("LIGHT", ThemePreference.Light), ("白天", ThemePreference.Light), ("浅色", ThemePreference.Light),
                     ("Follow-System", ThemePreference.FollowSystem), ("跟随系统", ThemePreference.FollowSystem),
                     ("DARK", ThemePreference.Dark), ("黑夜", ThemePreference.Dark),
                 })
        {
            File.WriteAllText(prefs, "# 手改的一行\n " + AppPreferences.ThemeKey + " = " + written + " \n");
            Battery.AssertEqual(expected, AppPreferences.Parse(File.ReadAllText(prefs)),
                $"手改写法「{written}」必须认得（这份文件是给人用记事本改的）");
        }

        // 隐私自证：这份文件里只许有档位名，不许出现路径清单以外的东西，更不许有像素。
        AppPreferences.Save(new AppPreferences(ThemePreference.Light), prefs);
        string final = File.ReadAllText(prefs);
        Battery.Assert(!final.Contains('\0'), "偏好文件必须是纯文本");
        Battery.Assert(final.Split('\n').Count(l => l.Trim().Length > 0 && !l.TrimStart().StartsWith('#')) == 1,
            $"偏好文件里只许有一行有效设置（只有外观档位），实际：{final.Trim()}");
        Battery.Evidence($"三档 token [{string.Join("/", tokens)}] 各写各读一字不差；7 种手改写法全认得");
    });

    /// <summary>
    /// 真按钮真按下去：点「白天」必须立刻换配色、把整棵控件树翻过来、把偏好写进盘，
    /// 预览条也必须真的按新配色重画（不是只把控制器字段改了）。再点「黑夜」原路翻回来。
    ///
    /// 控件树那一头量的是产品自己造出来的 Label（按"底色=面板底、字色=正文"这个角色认，不抄文案），
    /// 因为它走的是 <c>Theme.Changed → ThemeApplicator.Apply</c> 这条真通路；
    /// 若只 new 一枚 Label 再手动调 Apply，量到的就只是翻译函数自己，接线断了也绿。
    /// </summary>
    private static void ClickingDayNightRepaintsEverything() => UiSmokeTests.RunOnStaThread(() =>
        InSandbox(prefs =>
        {
            SystemTheme.Probe = () => false;
            ThemeController.SetMode(ThemePreference.Dark, prefs);
            using var panel = new ThemeChooserPanel();
            panel.PerformLayout();

            // 夹具自证：起点是黑夜，而且这枚按钮此刻真的可按（PerformClick 对灰按钮是空转、不报错）
            Battery.Assert(Theme.IsDark, "夹具自证：起点必须是黑夜档");
            Battery.Assert(panel.DayButtonForTest.Enabled, "夹具自证：「白天」按钮此刻必须可按");
            // 第五十四改④把选色块底纹统一成 WindowBackground（用户图4黄框异色底纹），
            // 探针角色随之搬家：「窗口底+正文色」。判据本体不变——找一枚构造期抄色的 Label，
            // 换肤后它必须翻到新档。
            Label? lead = FindLabel(panel, l => l.BackColor.ToArgb() == ThemePalette.Dark.WindowBackground.ToArgb() &&
                                                l.ForeColor.ToArgb() == ThemePalette.Dark.TextPrimary.ToArgb());
            Battery.Assert(lead is not null,
                "夹具自证：面板里应有一枚「窗口底+正文色」的 Label 可当翻译探针——找不到就是控件树没按配色造");

            panel.DayButtonForTest.PerformClick();
            Battery.AssertEqual(ThemePreference.Light, ThemeController.Mode,
                "点「白天」必须把档位写成白天（没写就是这一下被 CanSelect 吃了，等于没量）");
            Battery.Assert(!Theme.IsDark, "点「白天」之后生效配色必须立刻是白天");
            Battery.AssertEqual(ThemePalette.Light.WindowBackground.ToArgb(), lead!.BackColor.ToArgb(),
                "换肤必须把控件构造期抄下来的底色翻成新档——不翻就会在浅色面板上留一块深色疤");
            Battery.AssertEqual(ThemePalette.Light.TextPrimary.ToArgb(), lead.ForeColor.ToArgb(),
                "换肤必须把控件的字色一起翻过来");
            Battery.Assert(File.Exists(prefs) && File.ReadAllText(prefs).Contains("theme=light"),
                "点一下必须同时写盘（用户要的是「记住到下次启动」）");
            Battery.Assert(panel.DayButtonForTest.Checked && !panel.NightButtonForTest.Checked,
                "两枚按钮必须互斥：白天选中、黑夜不选中");
            AssertPreviewPainted(panel.PreviewForTest, ThemePalette.Light);

            // 翻译必须幂等：再翻一遍什么都不会发生（主窗口与设置面板可能各翻一次）
            Battery.AssertEqual(0, ThemeApplicator.Apply(panel),
                "翻完之后控件身上已是新档颜色，再翻一遍必须改 0 枚——不然重复换肤会把颜色越翻越偏");

            panel.NightButtonForTest.PerformClick();
            Battery.AssertEqual(ThemePreference.Dark, ThemeController.Mode, "点「黑夜」必须翻回去");
            Battery.Assert(Theme.IsDark, "点「黑夜」之后生效配色必须立刻是黑夜");
            Battery.AssertEqual(ThemePalette.Dark.WindowBackground.ToArgb(), lead.BackColor.ToArgb(), "翻回黑夜后底色也必须回来");
            Battery.Assert(panel.NightButtonForTest.Checked && !panel.DayButtonForTest.Checked, "互斥方向反过来同样成立");
            AssertPreviewPainted(panel.PreviewForTest, ThemePalette.Dark);
            Battery.Evidence("白天⇄黑夜各真点一次：档位、生效配色、控件树底色/字色、写盘、预览条像素五头全跟上");
        }));

    /// <summary>
    /// 预览条必须按<b>指定那一套</b>配色真的画出来：背景探针、强调 chip 探针逐像素对，
    /// 五种表面色各至少出现一枚，状态色序列与产品自己的 <c>Theme.StatusColor</c> 同源，
    /// 而且每枚状态色旁边必须有文字（需求 §6：状态不许只靠颜色表达）。
    /// </summary>
    private static void AssertPreviewPainted(ThemePreviewStrip strip, ThemePalette expected)
    {
        Battery.Assert(strip.Width > 0 && strip.Height > 0,
            $"夹具自证：预览条必须有尺寸才画得出东西，实际 {strip.Width}×{strip.Height}");
        using var bmp = new Bitmap(strip.Width, strip.Height);
        strip.RenderForTest(bmp);

        Rectangle bg = strip.BackgroundProbeForTest;
        Battery.AssertEqual(expected.PanelBackground.ToArgb(), bmp.GetPixel(bg.X + 1, bg.Y + 1).ToArgb(),
            "预览条背景探针必须等于这一套的面板底");
        Rectangle chip = strip.AccentProbeForTest;
        Battery.AssertEqual(expected.Accent.ToArgb(), bmp.GetPixel(chip.X + 1, chip.Y + 1).ToArgb(),
            "预览条那枚「导出」chip 必须等于这一套的强调色");

        foreach ((Color color, string name) in new[]
                 {
                     (expected.WindowBackground, "窗口"), (expected.PanelBackground, "面板"),
                     (expected.ListBackground, "列表"), (expected.CanvasBackground, "画布"),
                     (expected.CheckerLight, "棋盘"),
                 })
            Battery.Assert(CountPixels(bmp, color) > 0,
                $"预览条里必须真的画出了这一套的{name}底 {Rgb(color)}——0 枚说明它还在用旧配色");

        Color[] want = { expected.Passed, expected.Warning, expected.ManualPending, expected.Danger };
        IReadOnlyList<Color> got = strip.PaintedStatusColorsForTest;
        Battery.AssertEqual(want.Length, got.Count, "预览条应画出四枚状态样本（三枚拼缝状态 + 一枚「缺」角标）");
        for (int i = 0; i < want.Length; i++)
            Battery.AssertEqual(want[i].ToArgb(), got[i].ToArgb(), $"第 {i + 1} 枚状态样本的颜色");
        IReadOnlyList<string> texts = strip.PaintedStatusTextsForTest;
        Battery.AssertEqual(want.Length, texts.Count, "每枚状态色旁边都必须有文字");
        for (int i = 0; i < 3; i++)
            Battery.AssertEqual(Theme.StatusText(new[] { SeamStatus.AutoPassed, SeamStatus.NeedsReview, SeamStatus.ManualPending }[i]),
                texts[i], "状态文字必须与产品自己的 StatusText 同源（不许预览条另写一套）");
        Battery.Assert(texts.All(t => t.Length > 0), "状态文字不许是空串——只靠颜色表达状态违反需求 §6");
    }

    /// <summary>
    /// "跟随系统"这一档必须真的跟着读数走两趟（深→浅→深），而且<b>档位本身不许被改写</b>：
    /// 跟随时用户选的是"交给 Windows 决定"，系统换一次就把档位改成手动的话，下一次系统再换就不跟了。
    /// </summary>
    private static void FollowSystemTracksBothReadings() => UiSmokeTests.RunOnStaThread(() =>
        InSandbox(prefs =>
        {
            SystemTheme.Probe = () => false;
            ThemeController.SetMode(ThemePreference.FollowSystem, prefs);
            Battery.Assert(Theme.IsDark, "系统深色时跟随档必须生效为黑夜");

            SystemTheme.Probe = () => true;
            ThemeController.RefreshSystemReading();
            Battery.Assert(!Theme.IsDark, "系统改成浅色后必须真的换到白天（这就是「跟随」二字的全部内容）");
            Battery.AssertEqual(ThemePreference.FollowSystem, ThemeController.Mode, "系统换档不许把用户选的「跟随」改成手动");
            Battery.AssertEqual(true, ThemeController.SystemReading, "读数必须如实记成浅色");

            SystemTheme.Probe = () => false;
            ThemeController.RefreshSystemReading();
            Battery.Assert(Theme.IsDark, "系统再改回深色必须再跟着换回黑夜（只跟一趟不算跟随）");
            Battery.AssertEqual(ThemePreference.FollowSystem, ThemeController.Mode, "第二趟同样不许改写档位");

            using var panel = new ThemeChooserPanel();
            Battery.Assert(panel.FollowCheckForTest.Checked, "跟随档下那枚勾选按钮必须是勾上的");
            Battery.Assert(panel.EffectiveLineForTest.Contains("跟随系统"),
                $"「现在生效」那一行必须说清是跟随来的，实际：{panel.EffectiveLineForTest}");
            Battery.Assert(panel.SystemLineForTest.Contains("深色"),
                $"「系统当前」那一行必须如实报读数，实际：{panel.SystemLineForTest}");
            Battery.Assert(ThemeController.Describe().Contains("跟随系统"), "状态栏那句整句也必须点明跟随");
            Battery.Evidence("深→浅→深三趟：生效配色跟着换、档位始终停在跟随、面板两行读数如实");
        }));

    /// <summary>
    /// 画布那些 <c>static readonly</c> 的 Pen/SolidBrush 是类型初始化时抄下来的值，换肤之后
    /// 必须<b>就地把对象的 Color 改掉</b>。这一格用像素证它真的改了：同一份单色夹具、同一个视口，
    /// 换档前后各取一帧，新档的画布底色与缝线强调色必须出现、旧档那两枚必须一枚不剩。
    ///
    /// 同一趟再量一条红线：<c>HudPanelAllocations</c> 不许因为换肤而增加。F8 面板是一张复用位图，
    /// 颜色烤在里面；换肤要做的是"清掉比较基准让它走<b>复用</b>那条重画路"，不是重新分配一张。
    /// 若有人把换肤写成"释放再建"，这一格会红——每帧分配那条红线在换肤这一路同样成立。
    /// 夹具用单色而不是截图样：截图样花花绿绿，"旧档颜色一枚不剩"会被内容里碰巧同色的像素喂红。
    /// </summary>
    private static void CanvasPaintsRecolorInPlace() => UiSmokeTests.RunOnStaThread(() =>
        InSandbox(prefs =>
        {
            SystemTheme.Probe = () => false;
            ThemeController.SetMode(ThemePreference.Dark, prefs);

            RgbaImage a = Fixture.Solid(60, 120, new RgbaColor(201, 31, 31, 255));
            RgbaImage b = Fixture.Solid(60, 120, new RgbaColor(31, 201, 31, 255));
            (Project p, MemoryResolver r, _) = TestProject.Create(StitchMode.LongScroll, a, b);
            using var canvas = new StitchCanvasControl(p, new CanvasComposer(r));
            canvas.Size = new Size(300, 260);
            canvas.Viewport.ZoomAt(1.0, new Point(0, 0));
            canvas.Viewport.MoveTopLeftTo(IntPoint.Zero);
            p.Mutate("布置一条成功缝", d => d.Seams[0].Status = SeamStatus.AutoPassed);
            canvas.ShowDebugHud = true;

            using var bmp = new Bitmap(300, 260);
            canvas.RenderFrameForTest(bmp);
            long allocBefore = canvas.HudPanelAllocations;
            int darkBg = CountPixels(bmp, ThemePalette.Dark.CanvasBackground);
            int darkAccent = CountPixels(bmp, ThemePalette.Dark.Accent);
            Battery.Assert(darkBg > 0,
                $"夹具自证：黑夜档这一帧里必须数得到画布底色（实际 {darkBg} 枚）——0 枚说明视口被内容铺满，后面那句「旧色一枚不剩」就是空话");
            Battery.Assert(darkAccent > 0,
                $"夹具自证：黑夜档这一帧里必须数得到缝线强调色（实际 {darkAccent} 枚）");
            using Bitmap? hudBefore = canvas.HudPanelSnapshotForTest();
            Battery.Assert(hudBefore is not null, "夹具自证：开着 F8 就该有一张面板缓冲区可量");
            // 面板缓冲区是 Format32bppPArgb（<b>预乘</b> alpha），清底色落到内存里不是那枚 ARGB，
            // 而且"预乘 → 读回"这一步<b>有损且与位图大小/来源有关</b>：本批实测同一枚
            // WithAlpha(面板底,215)，在一张 1×1 探针位图上读回 (215,37,37,40)，
            // 在产品那张真面板上读回 (215,36,36,40)——差 1 个色阶。
            // 所以判据<b>不复算</b>这枚值（第一版就是拿 1×1 探针当期望，红在"差 1"上，
            // 红出来的话却像"产品换肤没重画面板"）。改成两档各测一次、只比关系：
            // 变了、旧的一枚不剩、新的占多数、方向与调色板一致（浅色档面板底更亮）。
            (int darkClear, int darkCount) = DominantPixel(hudBefore!);
            int beforePixels = hudBefore!.Width * hudBefore!.Height;
            int scrimAlpha = Color.FromArgb(darkClear).A;
            Battery.Assert(scrimAlpha > 0 && scrimAlpha < 255,
                $"夹具自证：面板清底那枚像素应当是半透明的（读到 alpha={scrimAlpha}）——全不透明或全透明都说明读错了地方");
            Battery.Assert(darkCount * 2 > beforePixels,
                $"夹具自证：清底色必须是面板缓冲区里占多数的那一枚（{darkCount}/{beforePixels}）——" +
                "不占多数就说明量到的不是清底色，下面那几句会失去主语");

            ThemeController.SetMode(ThemePreference.Light, prefs);
            canvas.RenderFrameForTest(bmp);
            Battery.Assert(CountPixels(bmp, ThemePalette.Light.CanvasBackground) > 0,
                "换到白天后画布底色必须真的变成浅色档那枚——常驻画具没就地改色就会停在深色");
            Battery.AssertEqual(0, CountPixels(bmp, ThemePalette.Dark.CanvasBackground),
                "换到白天后旧档画布底色必须一枚不剩（剩了就是某支画具还在用抄下来的旧值）");
            Battery.Assert(CountPixels(bmp, ThemePalette.Light.Accent) > 0,
                "换到白天后缝线必须用浅色档的强调蓝");
            Battery.AssertEqual(0, CountPixels(bmp, ThemePalette.Dark.Accent),
                "换到白天后旧档强调蓝必须一枚不剩");
            Battery.AssertEqual(allocBefore, canvas.HudPanelAllocations,
                "换肤不许让 F8 面板重新分配位图——它该走复用那条重画路（每帧分配红线在换肤这一路同样成立）");

            using Bitmap? hudAfter = canvas.HudPanelSnapshotForTest();
            Battery.Assert(hudAfter is not null, "换肤后面板缓冲区必须还在（复用的是同一张，不是释放掉了）");
            (int lightClear, int lightCount) = DominantPixel(hudAfter!);
            int afterPixels = hudAfter!.Width * hudAfter!.Height;
            Battery.Assert(darkClear != lightClear,
                "换肤后面板缓冲区的清底色必须变——两档读数相同就是没重画（颜色是烤在这张位图里的）");
            Battery.AssertEqual(0, CountArgb(hudAfter, darkClear),
                "面板缓冲区里旧档的清底色必须一枚不剩");
            Battery.Assert(lightCount * 2 > afterPixels,
                $"新档清底色同样必须占多数（{lightCount}/{afterPixels}）");
            Battery.AssertEqual(scrimAlpha, Color.FromArgb(lightClear).A,
                "清底那枚半透明的 alpha 不该随换肤变（变的只该是颜色那三个通道）");
            Battery.Assert(Brightness(lightClear) > Brightness(darkClear),
                $"方向必须与调色板一致：浅色档面板底 {Rgb(ThemePalette.Light.PanelBackground)} 比黑夜档 " +
                $"{Rgb(ThemePalette.Dark.PanelBackground)} 亮，读回的清底色也必须更亮——" +
                "只判「变了」的话，把面板改成更暗也算变，那一半就喂绿了");
            Battery.Evidence($"黑夜→白天：画布底 {darkBg} 枚旧色→0、缝线 {darkAccent} 枚旧蓝→0，新档两色各出现；" +
                             $"面板分配 {allocBefore}→{canvas.HudPanelAllocations}（复用不新建）；" +
                             $"缓冲区清底色 0x{darkClear:X8}（{darkCount} 枚）→ 0x{lightClear:X8}（{lightCount} 枚），" +
                             $"亮度 {Brightness(darkClear)}→{Brightness(lightClear)}，alpha 两趟都是 {scrimAlpha}");
        }));

    // ═════════════════════════ 反证（守卫的守卫）═════════════════════════

    /// <summary>
    /// 上一格那把对比度尺子必须真的有刻度：白压黑要接近 21，浅灰压白必须<b>不到</b> 4.5，
    /// 同一枚浅灰压黑又必须够。三条一起才证明"≥4.5"这条闸不是恒真——
    /// 若有人把 <see cref="Contrast"/> 写成永远返回一个大数，正向判据会绿得很好看。
    /// </summary>
    private static void ContrastRulerHasTeeth()
    {
        double max = Contrast(Color.White, Color.Black);
        Battery.Assert(max > 20.9 && max < 21.1,
            $"白压黑的理论值是 21:1，尺子量出 {max:F3}——偏离就是公式写错了，上面那格的所有比值都不可信");

        Color gray = Color.FromArgb(200, 200, 200);
        double onWhite = Contrast(gray, Color.White);
        double onBlack = Contrast(gray, Color.Black);
        Battery.Assert(onWhite < 4.5,
            $"浅灰 {Rgb(gray)} 压白底只有 {onWhite:F2}，必须被判成不够——尺子若在这里也说够，那它量什么都说够");
        Battery.Assert(onBlack >= 4.5,
            $"同一枚浅灰压黑底有 {onBlack:F2}，必须被判成够——只往一个方向偏的尺子同样是坏尺子");
        Battery.Assert(Math.Abs(Contrast(gray, Color.White) - Contrast(Color.White, gray)) < 1e-9,
            "对比度必须与两枚颜色的先后无关（公式里取的是 max/min，写反了就会随调用顺序漂）");
        Battery.Evidence($"白/黑 {max:F3}；浅灰压白 {onWhite:F2}（不够）vs 压黑 {onBlack:F2}（够）——尺子有刻度");
    }

    /// <summary>
    /// 取消「跟随系统」时该钉住哪一档？产品钉的是<b>当前生效的那一档</b>。
    /// 这一格造出"系统浅色、跟着生效为白天"的现场，再真把勾选取消：必须留在白天。
    /// 同时把旧写法（一律退回黑夜）会得到什么算出来并排比——两者必须不同，
    /// 否则这格判据没有杀伤力（在深色系统上两种写法结果一样，量了等于没量）。
    /// </summary>
    private static void UnfollowingMustNotJumpToDark() => UiSmokeTests.RunOnStaThread(() =>
        InSandbox(prefs =>
        {
            SystemTheme.Probe = () => true;      // 刻意选浅色系统：这是两种写法分得开的那一档
            ThemeController.SetMode(ThemePreference.FollowSystem, prefs);
            Battery.Assert(!Theme.IsDark, "夹具自证：此刻必须生效为白天，否则这格量不出两种写法的差别");

            using var panel = new ThemeChooserPanel();
            Battery.Assert(panel.FollowCheckForTest.Checked, "夹具自证：勾选此刻必须是勾上的");
            panel.FollowCheckForTest.Checked = false;   // 勾选框没有 PerformClick，改 Checked 就是用户点下去那条通路

            Battery.AssertEqual(ThemePreference.Light, ThemeController.Mode,
                "看着白天界面取消跟随，必须钉在白天——跳回黑夜就是用户没要的那一下");
            Battery.Assert(!Theme.IsDark, "取消跟随之后生效配色必须还是白天");
            Battery.Assert(!panel.FollowCheckForTest.Checked, "取消之后勾选必须保持不勾");

            // 反证自证：旧写法在这一现场会得到黑夜，与产品现在的结果必须不同
            ThemePreference oldRule = ThemePreference.Dark;
            Battery.Assert(oldRule != ThemeController.Mode,
                "夹具失效：旧写法与新写法在这一现场结果相同，这格已经量不出差别（请改用浅色系统那档）");
            Battery.Evidence($"浅色系统下取消跟随 ⇒ 钉住 {ThemeController.Mode}（旧写法会钉 {oldRule}，屏上会白跳一档）");
        }));

    /// <summary>
    /// 偏好文件"在但写坏了"与"压根不存在"都会退回黑夜，但<b>只有前者需要解释</b>。
    /// 这一格把三种现场并排量：写坏 → 黑夜 + 出声；不存在 → 黑夜 + 不出声；认得的手改写法 → 那一档 + 不出声。
    /// 三条一起才证明那句说明不是"永远挂着"也不是"永远没有"——它是真的在分辨现场。
    /// </summary>
    private static void BrokenPreferenceMustSpeak() => InSandbox(prefs =>
    {
        File.WriteAllText(prefs, "theme=purple\n");
        AppPreferences broken = AppPreferences.Load(prefs);
        Battery.AssertEqual(ThemePreference.Dark, broken.Theme, "认不得的档位必须退回黑夜（枚举 0 档）");
        Battery.Assert(AppPreferences.LastLoadNote is { Length: > 0 },
            "文件在、内容不对，必须留下一句说明——不然用户下次启动只会以为软件忘了他的选择");

        File.Delete(prefs);
        AppPreferences missing = AppPreferences.Load(prefs);
        Battery.AssertEqual(ThemePreference.Dark, missing.Theme, "首次运行（文件不存在）同样是黑夜");
        Battery.Assert(AppPreferences.LastLoadNote is null,
            $"文件不存在不需要解释，实际却说了：{AppPreferences.LastLoadNote}——说明若永远挂着，用户很快就学会无视它");

        File.WriteAllText(prefs, "theme=白天\n");
        AppPreferences known = AppPreferences.Load(prefs);
        Battery.AssertEqual(ThemePreference.Light, known.Theme, "认得的中文写法必须读成白天");
        Battery.Assert(AppPreferences.LastLoadNote is null,
            $"认得的写法不该出声，实际：{AppPreferences.LastLoadNote}");

        // 写不进去也不许把程序搞崩：本次切换照样生效，只是下次启动记不住。
        string blockedDir = Path.Combine(Path.GetTempPath(), "ls-theme-blocked-" + Guid.NewGuid().ToString("N")[..8]);
        File.WriteAllText(blockedDir, "占位：让下面那个目录创建不出来");
        try
        {
            string impossible = Path.Combine(blockedDir, "sub", "preferences.txt");
            AppPreferences.Save(new AppPreferences(ThemePreference.Light), impossible);
            Battery.Assert(AppPreferences.LastSaveNote is { Length: > 0 },
                "写不进去必须留下一句说明（本次已生效、下次会回到上一档），不许静默失败");
            Battery.Assert(!File.Exists(impossible), "夹具自证：这条路本来就写不进去");
        }
        finally
        {
            try { File.Delete(blockedDir); } catch (IOException) { /* 临时占位文件，删不掉不影响结论 */ }
        }
        Battery.Evidence("写坏→黑夜+出声；不存在→黑夜+不出声；认得→那一档+不出声；写不进去→出声但不崩");
    });

    /// <summary>
    /// 为什么需要 <see cref="ThemeApplicator"/> 这一层？因为 WinForms 的颜色是<b>构造期抄下来的值</b>。
    /// 这一格把那条缺陷现场原样造出来：先按深色造一枚 Label，再换配色但<b>不</b>调翻译——
    /// 它必须还停在深色（这就是"浅色面板上一块深色疤"的成因）。然后调一次翻译，它必须变；
    /// 再调一次必须改 0 枚（幂等）。三段合起来证明翻译层不是可有可无的装饰。
    /// </summary>
    private static void WithoutApplicatorControlsKeepOldColors() => UiSmokeTests.RunOnStaThread(() =>
        InSandbox(prefs =>
        {
            SystemTheme.Probe = () => false;
            ThemeController.SetMode(ThemePreference.Dark, prefs);
            using var label = new Label
            {
                Text = "翻译探针",
                AutoSize = true,
                BackColor = ThemePalette.Dark.PanelBackground,
                ForeColor = ThemePalette.Dark.TextPrimary,
            };

            ThemeController.SetMode(ThemePreference.Light, prefs);
            Battery.AssertEqual(ThemePalette.Dark.PanelBackground.ToArgb(), label.BackColor.ToArgb(),
                "换配色之后、翻译之前，控件身上必须还是旧档那枚值——若它自己跟着变了，翻译层就是多余的，这格也就没了主语");

            int changed = ThemeApplicator.Apply(label);
            Battery.Assert(changed > 0, $"翻译这一趟必须真的改了几枚颜色（实际 {changed}）——0 就是翻译表压根没认得这枚色");
            Battery.AssertEqual(ThemePalette.Light.PanelBackground.ToArgb(), label.BackColor.ToArgb(), "翻译后底色必须是新档");
            Battery.AssertEqual(ThemePalette.Light.TextPrimary.ToArgb(), label.ForeColor.ToArgb(), "翻译后字色必须是新档");
            Battery.AssertEqual(0, ThemeApplicator.Apply(label), "再翻一遍必须改 0 枚（幂等）");

            // 来回一趟必须逐枚回到原点：只量"翻过去对"会漏掉"翻回来错"——
            // 翻译表是单向造的，两个方向各有一张，撞键的形状在两个方向上并不对称。
            ThemeController.SetMode(ThemePreference.Dark, prefs);
            ThemeApplicator.Apply(label);
            Battery.AssertEqual(ThemePalette.Dark.PanelBackground.ToArgb(), label.BackColor.ToArgb(), "翻回黑夜后底色必须回到原值");
            Battery.AssertEqual(ThemePalette.Dark.TextPrimary.ToArgb(), label.ForeColor.ToArgb(), "翻回黑夜后字色必须回到原值");
            Battery.AssertEqual(0, ThemeApplicator.Apply(label), "回程同样必须幂等");

            // 认不得的颜色必须原样留着：用户图片、透明色、以及故意写死的白字都不许被误伤。
            using var untouched = new Label
            {
                Text = "不该被翻的",
                AutoSize = true,
                BackColor = Color.FromArgb(12, 34, 56),
                ForeColor = Color.White,
            };
            ThemeApplicator.Apply(untouched);
            Battery.AssertEqual(Color.FromArgb(12, 34, 56).ToArgb(), untouched.BackColor.ToArgb(),
                "不在调色板里的颜色必须原样留着——翻译若把认不得的也改，用户图片与透明区域会被误伤");
            Battery.AssertEqual(Color.White.ToArgb(), untouched.ForeColor.ToArgb(),
                "故意写死的白字（两档都压在强调蓝上）必须原样留着");
            Battery.Evidence($"不翻译→停在旧档；翻译一次→改 {changed} 枚；再翻→0 枚；回程逐枚复原；认不得的两枚原样不动");
        }));

    /// <summary>
    /// 翻译表的<b>值</b>不许同时又是它自己的<b>键</b>（除非那一格翻过去就是它自己）。
    ///
    /// ═══ 为什么这条不变量值得单独一格 ═══
    /// <see cref="ThemeApplicator"/> 是"旧档 ARGB → 新档 ARGB"的查表翻译，它的幂等性<b>只</b>建立在
    /// 一条前提上：翻完之后控件身上那些颜色都不是旧档的颜色。一旦某枚新档颜色恰好等于某枚旧档颜色，
    /// 翻第二遍就会把已经翻好的那枚再翻一次——本仓库现场撞上一枚：浅色档的正文原本取 (28,28,30)，
    /// 而它正是黑夜档的 WindowBackground，于是 (28,28,30) 在第二遍里被当成"旧档窗口底"翻成
    /// (243,243,245)＝白底上的近白字，整段文字在屏上消失。
    ///
    /// ═══ "翻第二遍"在产品里真的会发生 ═══
    /// 设置窗口开着换肤时，主窗口的 <c>Theme.Changed → ApplyEverywhere</c> 会翻一遍所有开着的窗口
    /// （含设置窗口），紧接着设置窗口自己的订阅又翻一遍同一棵树；外观那一块还有第三遍。
    /// 所以这不是理论缺陷，是每次从设置窗口切换都会走到的路。
    ///
    /// ═══ 为什么直接查表而不是靠控件跑一趟 ═══
    /// 上面那格用一枚 Label 量幂等，量的形状只有"面板底 + 正文色"这一对；调色板一共十九枚，
    /// 任何一对撞键都要能被抓出来，所以这里直接对产品自己造的那张表逐枚核，两个方向各一张。
    /// 尺子先自证（手工造一张撞键表必须被点名）再上产品，否则"无撞键"可能是尺子恒绿。
    /// </summary>
    private static void RemapTargetsMustNotBeOldKeys()
    {
        // 尺子：把"目标色自己又是键、而且那一格再翻一次不是它自己"的格子逐枚点名成 a→b→c 三连。
        static List<string> Collisions(Dictionary<int, int> map)
        {
            var hits = new List<string>();
            foreach (KeyValuePair<int, int> kv in map)
            {
                if (kv.Value == kv.Key) continue;   // 两档同值的合同色：翻过去还是它自己，第二遍不会动它
                if (map.TryGetValue(kv.Value, out int again) && again != kv.Value)
                    hits.Add($"{Rgb(Color.FromArgb(kv.Key))}→{Rgb(Color.FromArgb(kv.Value))}→{Rgb(Color.FromArgb(again))}");
            }
            return hits;
        }

        // 尺子自证①：手工造一张撞键表（形状就是本仓库真撞上那一枚），必须被点名。
        var synthetic = new Dictionary<int, int>
        {
            [Color.FromArgb(28, 28, 30).ToArgb()] = Color.FromArgb(243, 243, 245).ToArgb(),
            [Color.FromArgb(235, 235, 237).ToArgb()] = Color.FromArgb(28, 28, 30).ToArgb(),
        };
        Battery.Assert(Collisions(synthetic).Count > 0,
            "夹具自证：手工撞键表必须被这把尺子点名——点不出来就说明尺子恒绿，下面两档那句「无撞键」全是空话");

        // 尺子自证②：翻过去就是它自己那一格（读数层合同色两档同值）不许被误报成撞键。
        var selfOnly = new Dictionary<int, int>
        {
            [ThemePalette.Dark.OverlayBefore.ToArgb()] = ThemePalette.Light.OverlayBefore.ToArgb(),
        };
        Battery.AssertEqual(0, Collisions(selfOnly).Count,
            "两档同值那一格不是撞键（读数层合同色不许被这条判据误伤，否则它会逼着人把合同色改掉）");

        foreach ((ThemePalette from, ThemePalette to) in new[]
                 { (ThemePalette.Dark, ThemePalette.Light), (ThemePalette.Light, ThemePalette.Dark) })
        {
            string tag = from.IsDark ? "黑夜→白天" : "白天→黑夜";
            Dictionary<int, int> map = from.BuildRemapTo(to);
            Battery.Assert(map.Count > 0, $"夹具自证：{tag} 的翻译表必须非空（实际 {map.Count} 枚）——空表查不出撞键");
            List<string> hits = Collisions(map);
            Battery.AssertEqual("", string.Join("；", hits),
                $"{tag} 的翻译表里有目标色自己又是旧档的键——翻第二遍会把已经翻好的颜色再翻一次" +
                "（设置窗口开着换肤时同一棵树会被翻两三遍，这条路产品里每次都走）");
            Battery.Evidence($"{tag}：{map.Count} 枚翻译，撞键 0 枚（幂等成立的前提）");
        }
    }

    /// <summary>
    /// 纯白<b>不许</b>是任何一档调色板里的颜色。
    ///
    /// ═══ 为什么这条与"撞键"那条是两回事 ═══
    /// 上一格管的是"翻译表的值撞上自己的键"（翻两遍会跑偏）。这一格管的是另一种形状：
    /// 产品里有八处把 <c>Color.White</c> <b>写死</b>当字色，设计上"两档都压在强调蓝上、永远不换"——
    /// 左栏拼缝列表选中行两枚、素材列表选中行两枚、菜单渲染器选中项、外观那枚选中按钮、
    /// 预览条那枚 chip、画布把手图章两枚。翻译表的规矩是"认得才翻、认不得原样留着"，
    /// 所以纯白一旦被收进调色板，它就"认得"了：从白天翻回黑夜时那些白字会被当成
    /// "旧档面板底"翻成深色面板底——选中行上出现深灰字。
    ///
    /// ═══ 为什么不在翻译层加一句"白色跳过" ═══
    /// 因为角色冲突没法在一张 ARGB 表里表达：同一枚纯白，当<b>底色</b>时必须翻
    /// （浅色面板翻回深色面板），当<b>字色</b>时不能翻。要在翻译层分清就得知道每枚控件的角色，
    /// 那正是 <see cref="ThemeApplicator"/> 类注释里否掉的那条路（等于把几百处构造代码再抄一遍）。
    /// 所以在源头消除歧义：浅色档面板底取差三档的近白 (252,252,253)，肉眼分不出，
    /// 白底上的对比度只掉 0.05~0.15（全部仍 ≥ 4.5:1）。
    ///
    /// 本批第一趟就是这么红的：`guard/换肤若不翻控件树则底色停在旧档` 报
    /// "故意写死的白字必须原样留着：期望 -1，实际 -14277080"，那枚 -14277080 正是 0xFF262628＝深色面板底。
    /// </summary>
    private static void PureWhiteMustStayOutOfPalettes() => UiSmokeTests.RunOnStaThread(() =>
        InSandbox(prefs =>
        {
            // 尺子：把一套配色里等于纯白的那几枚按角色点名。
            static List<string> PureWhiteHits(IEnumerable<(string Name, Color Color)> colors) =>
                colors.Where(c => c.Color.ToArgb() == Color.White.ToArgb()).Select(c => c.Name).ToList();

            // 尺子自证：把浅色档面板底改回纯白（本批第一趟红出来的那个形状），必须被点名。
            Battery.AssertEqual(1,
                PureWhiteHits(new[] { ("PanelBackground", Color.FromArgb(255, 255, 255)) }).Count,
                "夹具自证：纯白必须被这把尺子点名——点不出来说明尺子恒绿，下面两档那句「不含纯白」全是空话");

            foreach (ThemePalette p in new[] { ThemePalette.Dark, ThemePalette.Light })
            {
                string tag = p.IsDark ? "黑夜" : "白天";
                List<string> hits = PureWhiteHits(NamedColors(p));
                Battery.AssertEqual("", string.Join("；", hits),
                    $"{tag}档调色板里不许有纯白（命中：{string.Join("；", hits)}）——" +
                    "产品有八处把白字写死当「两档都不换」，纯白进了表那些白字就会被当成底色翻掉");
            }

            // 行为那一头：两个方向各翻一次，白字都必须原样穿过（上面查的是表，这里查的是产品自己的出口）。
            SystemTheme.Probe = () => false;
            ThemeController.SetMode(ThemePreference.Light, prefs);
            Battery.AssertEqual(Color.White.ToArgb(), Theme.Translate(Color.White).ToArgb(),
                "白天档下白字必须原样穿过翻译");
            ThemeController.SetMode(ThemePreference.Dark, prefs);
            Battery.AssertEqual(Color.White.ToArgb(), Theme.Translate(Color.White).ToArgb(),
                "翻回黑夜档时白字同样必须原样穿过——本批第一趟就是这一头红的（白字被翻成深色面板底）");
            Battery.Evidence("两档调色板各 19 枚均不含纯白；两个方向的 Theme.Translate(White) 都原样返回");
        }));

    /// <summary>
    /// 按角色名交出一套配色的全部颜色（顺序与 <see cref="ThemePalette.Enumerate"/> 无关，
    /// 这里要的是"红的时候能说出是哪一枚"，所以逐枚带名字）。
    /// </summary>
    private static IEnumerable<(string Name, Color Color)> NamedColors(ThemePalette p) => new (string, Color)[]
    {
        ("WindowBackground", p.WindowBackground), ("PanelBackground", p.PanelBackground),
        ("PanelBorder", p.PanelBorder), ("CanvasBackground", p.CanvasBackground),
        ("ListBackground", p.ListBackground), ("CheckerLight", p.CheckerLight),
        ("CheckerDark", p.CheckerDark), ("TextPrimary", p.TextPrimary),
        ("TextSecondary", p.TextSecondary), ("TextDisabled", p.TextDisabled),
        ("OverlayBefore", p.OverlayBefore), ("OverlayAfter", p.OverlayAfter),
        ("OverlayAlign", p.OverlayAlign), ("Accent", p.Accent), ("AccentSoft", p.AccentSoft),
        ("Passed", p.Passed), ("Warning", p.Warning), ("ManualPending", p.ManualPending),
        ("Danger", p.Danger),
    };

    /// <summary>
    /// 跟随系统那枚轮询每 2 秒问一次注册表。<b>读数没变就不许广播</b>：
    /// 空转的广播会让整棵控件树白翻一遍、画布白重画一帧，静止看着界面也会持续掉帧。
    /// 这一格先量"同一读数连问两趟 ⇒ 0 次广播"，再量"读数真变了 ⇒ 恰好 1 次"。
    /// 后一半是尺子的自证：若计数器压根收不到事件，前一半那个 0 就是空话。
    /// </summary>
    private static void UnchangedReadingMustNotBroadcast() => UiSmokeTests.RunOnStaThread(() =>
        InSandbox(prefs =>
        {
            int paletteEvents = 0, modeEvents = 0;
            void OnPalette(ThemePalette o, ThemePalette n) => paletteEvents++;
            void OnMode() => modeEvents++;
            Theme.Changed += OnPalette;
            ThemeController.ModeChanged += OnMode;
            try
            {
                SystemTheme.Probe = () => true;
                ThemeController.SetMode(ThemePreference.FollowSystem, prefs);
                paletteEvents = 0;
                modeEvents = 0;

                ThemeController.RefreshSystemReading();
                ThemeController.RefreshSystemReading();
                Battery.AssertEqual(0, paletteEvents,
                    "系统读数没变时连问两趟，配色广播必须是 0 次——否则整棵控件树每 2 秒被白翻一遍");
                Battery.AssertEqual(0, modeEvents,
                    "系统读数没变时档位广播也必须是 0 次（设置面板会跟着白回填一遍）");

                SystemTheme.Probe = () => false;
                ThemeController.RefreshSystemReading();
                Battery.AssertEqual(1, paletteEvents,
                    "读数真变了必须恰好广播一次——这一半是尺子的自证：收不到事件的话上面那个 0 就是空话");
                Battery.AssertEqual(1, modeEvents, "读数真变了档位广播也必须恰好一次");
                Battery.Assert(Theme.IsDark, "广播之外配色也必须真的换过去（只广播不换色同样是坏的）");
                Battery.Evidence("同读数两趟 ⇒ 配色/档位各 0 次；换读数一趟 ⇒ 各 1 次，且生效配色真的换了");
            }
            finally
            {
                Theme.Changed -= OnPalette;
                ThemeController.ModeChanged -= OnMode;
            }
        }));
}