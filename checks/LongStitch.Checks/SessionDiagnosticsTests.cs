using System.Runtime.InteropServices;
using System.Text;
using LongStitch.App.Diagnostics;
using LongStitch.App.UI;
using LongStitch.Core.Geometry;

namespace LongStitch.Checks;

/// <summary>
/// 第十四轮需求四：「交互诊断模式」的三个出口与 F8 那十二项读数。
///
/// ═══ 这一组格子守的是哪几句话 ═══
/// 需求四的原话是"如果当前机器无法由 AI 直接确认鼠标手感，请在程序中增加交互诊断模式"，
/// 然后列了十二项读数 + 三个出口（复制诊断信息 / 打开诊断日志目录 / 导出当前交互会话）。
/// 人工手感这一条 AI 确实证不了（见 README 的「未验证」档），但**工具本身能不能被机器核对**是可以的：
/// <list type="number">
///   <item>十二项读数一项都不缺——少一项就等于把验收的活推回给人去猜；</item>
///   <item>读数里没有文件路径、没有像素（隐私红线：日志与读数只许有数字、坐标、Id、状态）；</item>
///   <item>导出的那份文件里<b>连缓冲里最后那几行都在</b>——不先刷盘就导出，
///         最想复盘的那一段（一次拖动的头尾）恰好不在文件里；</item>
///   <item>三个出口真的挂在菜单上（写了 public 方法却没接按钮，是本工程反复犯的那类呆账）。</item>
/// </list>
///
/// 帧耗时那一档（P95 之类）在这里只判"有没有这项读数"，不判数值——数值由 <c>--gui</c>
/// 在真窗口里量（需求五），离线数值当常驻判据会得到"今天绿明天红"的噪声。
/// </summary>
internal static class SessionDiagnosticsTests
{
    /// <summary>需求四列的十二项 → 屏上/导出文本里的那个标签。左列是需求原话，右列是实际字面。</summary>
    private static readonly (string Requirement, string Label)[] TwelveReadouts =
    {
        ("当前命中类型", "命中类型="),
        ("当前 SeamId", "SeamId="),
        ("当前 FixedSide", "固定侧="),
        ("AlignmentOffset", "AlignmentOffset="),
        ("CutY", "CutY="),
        ("OverlapRect", "OverlapRect="),
        ("Viewport.TopLeft", "Viewport.TopLeft="),
        ("当前帧耗时", "帧 最近"),
        ("P95 帧耗时", "P95"),
        ("预览帧数", "预览帧"),
        ("鼠标事件数", "鼠标"),
        ("TileCache 命中率", "命中率"),
    };

    public static void Register()
    {
        Battery.Add("canvas", "交互诊断读数：需求四点名的十二项一个都不缺", () =>
            UiSmokeTests.RunOnStaThread(AllTwelveReadoutsPresent));
        Battery.Add("canvas", "交互诊断读数里需求五点名的 GC 与整块合成两笔都得有", () =>
            UiSmokeTests.RunOnStaThread(GcAndWholeRenderAttributionPresent));
        Battery.Add("canvas", "交互诊断读数里需求五点名的进程调度那一笔必须从操作系统读来", () =>
            UiSmokeTests.RunOnStaThread(SchedulingAttributionComesFromOs));
        Battery.Add("guard", "调度读数若写成固定值则空转攒够十五毫秒再取第二次它也不涨", () =>
            UiSmokeTests.RunOnStaThread(FrozenSchedulingReadingWouldNotGrow));
        Battery.Add("canvas", "交互诊断读数里不许出现文件路径与像素", () =>
            UiSmokeTests.RunOnStaThread(DiagnosticsTextIsPrivacySafe));
        Battery.Add("ui", "导出当前交互会话：导出的文件必须含缓冲里最后几行", () =>
            UiSmokeTests.RunOnStaThread(ExportSessionIncludesBufferedLines));
        Battery.Add("ui", "帮助菜单里三个诊断出口都在且可点", () =>
            UiSmokeTests.RunOnStaThread(ThreeDiagnosticsMenuEntriesExist));
        Battery.Add("guard", "隐私判据的探针：读数混进路径就必须被认出来",
            PathDetectorActuallyDetectsPaths);
        Battery.Add("ui", "内存快照五类事件各记一行且承重字段齐全", () =>
            UiSmokeTests.RunOnStaThread(MemorySnapshotLogsStagesWithRequiredFields));
    }

    // ───────────────────────── ① 十二项读数 ─────────────────────────

    /// <summary>
    /// 十二项逐条对字面。最后一句是夹具自证：把指针真的压在重叠带里，让「当前命中类型」
    /// 读到一个类型而不是默认的「无」——不然"有这一项"可能只是"有个空壳标签"。
    /// </summary>
    private static void AllTwelveReadoutsPresent()
    {
        using HudCostTests.Harness h = HudCostTests.Harness.New();
        h.Canvas.ShowDebugHud = true;
        h.Render();
        // 悬停在重叠带内部（视口原点 (0,300)、1:1，带占画布行 480..600 ⇒ 设备行 180..300）
        h.Canvas.MouseMoveTo(new Point(30, 240));
        h.Render();

        string text = h.Canvas.DescribeDiagnostics();
        var missing = new List<string>();
        foreach ((string req, string label) in TwelveReadouts)
            if (!text.Contains(label, StringComparison.Ordinal)) missing.Add($"{req}（找的字面「{label}」）");
        Battery.Assert(missing.Count == 0,
            $"需求四那十二项里，读数文本缺 {missing.Count} 项：{string.Join("、", missing)}");
        Battery.Assert(h.Canvas.LastHitKindForTest != "无",
            $"夹具自证：指针压在带里时命中类型必须真读到一个类型，实际「{h.Canvas.LastHitKindForTest}」——" +
            "读到「无」说明上面那句「有命中类型这一项」是空壳");
        Battery.Assert(text.Contains($"命中类型={h.Canvas.LastHitKindForTest}", StringComparison.Ordinal),
            "读数里那一项的值必须就是交互层算出来的那一个，不是另拼的一份（屏上与诊断文本不许各说一套）");
        Battery.Evidence($"十二项齐备；命中类型={h.Canvas.LastHitKindForTest}；" +
                         $"读数文本 {text.Split('\n').Length} 行");
    }

    // ───────────────────────── ①b 需求五那两笔归因读数（GC / 整块合成） ─────────────────────────

    /// <summary>
    /// 需求五要求"偶发 80–150ms 时记录 GC 与进程调度信息"、指标清单里点名 <b>GC 次数</b> 与
    /// <b>RenderWhole 次数</b>。这两笔过去只在 <c>--gui</c> 工装里报得出来，而那个进程是 96 DPI 无感知的
    /// ⇒ <b>真窗口 150% 那一档只有面板上几枚毫秒数</b>，没法按"连跑 5 次报 P50/P95/max"的口径交账
    /// （README 的诚实缺口里就是这么记的）。现在诊断出口补了这一行，150% 那一档也能自己交出来。
    ///
    /// ═══ 两头都钉，缺一头这格就是空判据 ═══
    /// ① 字面齐备：gen0/gen1/gen2、撞上回收的帧数、UI 线程累计分配、进程 CPU、RenderWhole；
    /// ② <b>数字接在真计数器上</b>——用"取读数前后各读一次计数器"夹住它（读数本身会分配，
    ///    中间再涨几次是正常的，所以判的是<b>夹住</b>而不是<b>相等</b>），再故意整块合成一次、
    ///    逼一次 gen2 回收，取第二份读数，两笔都必须跟着动。
    ///    这一句反的是本项目反复栽的那一族：<b>恒为 0 的字段与"这趟真的没发生"在屏上长得一模一样</b>。
    /// </summary>
    private static void GcAndWholeRenderAttributionPresent()
    {
        using HudCostTests.Harness h = HudCostTests.Harness.New();
        h.Canvas.ShowDebugHud = true;
        h.Render();

        long wholeBefore = LongStitch.Core.CanvasComposer.WholeRunCountForTest;
        int gen0Before = GC.CollectionCount(0), gen2Before = GC.CollectionCount(2);
        string text = h.Canvas.DescribeDiagnostics();
        long wholeAfter = LongStitch.Core.CanvasComposer.WholeRunCountForTest;
        int gen0After = GC.CollectionCount(0), gen2After = GC.CollectionCount(2);

        string[] needed =
        {
            "GC 与调度：", "gen0 ", "gen1 ", "gen2 ", "撞上回收的", "UI 线程累计分配",
            "进程 CPU 用户", "整块合成 RenderWhole",
        };
        var missing = needed.Where(l => !text.Contains(l, StringComparison.Ordinal)).ToList();
        Battery.Assert(missing.Count == 0,
            $"需求五点名的归因读数缺 {missing.Count} 笔：{string.Join("、", missing)}");

        // 夹具自证：这一趟里 gen0 必须早就非零，否则下面那句"夹住"会被一个写死的 0 满足。
        Battery.Assert(gen0After > 0,
            $"夹具自证：跑完夹具 gen0 计数还是 {gen0After}——那「夹住读数」这一句就恒真了");
        Battery.Assert(ParseAfter(text, "gen0 ") >= gen0Before && ParseAfter(text, "gen0 ") <= gen0After,
            $"读数里那笔 gen0（{ParseAfter(text, "gen0 ")}）必须落在取读数前后各读一次的区间 " +
            $"[{gen0Before}, {gen0After}] 里——写死一个数不会落进来");
        Battery.Assert(ParseAfter(text, "整块合成 RenderWhole ") >= wholeBefore
                       && ParseAfter(text, "整块合成 RenderWhole ") <= wholeAfter,
            $"读数里那笔 RenderWhole（{ParseAfter(text, "整块合成 RenderWhole ")}）同样要被 " +
            $"[{wholeBefore}, {wholeAfter}] 夹住");

        // 正向对照：故意整块合成一次 + 逼一次 gen2 回收，第二份读数必须跟着动。
        new LongStitch.Core.CanvasComposer(h.R).RenderWhole(LongStitch.Core.Layout.CanvasLayout.Build(h.P.Doc));
        GC.Collect(2, GCCollectionMode.Forced);
        string again = h.Canvas.DescribeDiagnostics();
        Battery.AssertEqual(wholeBefore + 1, LongStitch.Core.CanvasComposer.WholeRunCountForTest,
            "夹具自证：故意调一次 RenderWhole，计数器必须恰好 +1（它要不动，上面那句夹住就是白写）");
        Battery.Assert(ParseAfter(again, "整块合成 RenderWhole ") >= wholeBefore + 1,
            "第二份读数里那笔 RenderWhole 必须跟着涨——不涨说明这一笔不是从计数器读的");
        Battery.Assert(ParseAfter(again, "gen2 ") > gen2Before,
            $"逼过一次 gen2 回收之后，第二份读数的 gen2（{ParseAfter(again, "gen2 ")}）必须大于逼之前的 " +
            $"{gen2Before}；不涨说明这一笔是拼出来的常数");
        Battery.Evidence($"归因行齐备：gen0 {ParseAfter(text, "gen0 ")}、gen2 {ParseAfter(text, "gen2 ")} → " +
                         $"逼一次回收后 gen2 {ParseAfter(again, "gen2 ")}；RenderWhole {ParseAfter(text, "整块合成 RenderWhole ")} → " +
                         $"故意合成一次后 {ParseAfter(again, "整块合成 RenderWhole ")}");
    }

    /// <summary>取标记后面那一串数字（读数是给人看的整排文字，这里只要把那一枚数抠出来对账）。</summary>
    private static long ParseAfter(string text, string marker)
    {
        int at = text.IndexOf(marker, StringComparison.Ordinal);
        Battery.Assert(at >= 0, $"读数里找不到标记「{marker}」——上面那句字面齐备没拦住？");
        int i = at + marker.Length;
        while (i < text.Length && (text[i] < '0' || text[i] > '9')) i++;
        int start = i;
        while (i < text.Length && text[i] >= '0' && text[i] <= '9') i++;
        Battery.Assert(i > start, $"标记「{marker}」后面没读到数字（原文片段「{text.Substring(at, Math.Min(40, text.Length - at))}」）");
        return long.Parse(text.AsSpan(start, i - start));
    }

    // ───────────────────────── ② 隐私 ─────────────────────────

    /// <summary>
    /// 「复制诊断信息」这个按钮的存在理由就是"贴给别人看"，所以它的内容必须是干净的：
    /// 不许有盘符路径、UNC 路径、源图文件名，也不许有任何长得像像素块的 base64。
    /// 判据本身由 <c>guard/隐私判据的探针：读数混进路径就必须被认出来</c> 那一格反证。
    /// </summary>
    private static void DiagnosticsTextIsPrivacySafe()
    {
        using HudCostTests.Harness h = HudCostTests.Harness.New();
        h.Canvas.ShowDebugHud = true;
        h.Render();
        string text = h.Canvas.DescribeDiagnostics();

        Battery.Assert(text.Split('\n').Length >= 10,
            $"夹具自证：读数至少要有 10 行才谈得上「这一份里没混进路径」（实际 {text.Split('\n').Length} 行）——" +
            "空文本能骗过下面所有「不包含」");
        Battery.Assert(!HasPath(text), "诊断读数里出现了文件路径（盘符 / UNC / 扩展名）——这份文本是要被贴给别人的");
        Battery.Assert(!HasPixelBlob(text),
            "诊断读数里出现了疑似像素数据（>40 个连续 base64 字符）——隐私红线：读数与日志只许有数字、坐标、Id、状态");
        Battery.Evidence($"读数 {text.Length} 字符、{text.Split('\n').Length} 行，无路径、无像素块");
    }

    /// <summary>路径的字面特征：盘符、UNC、或带图片扩展名的文件名。</summary>
    private static bool HasPath(string s) =>
        DrivePath.IsMatch(s) || s.Contains("\\\\", StringComparison.Ordinal) ||
        s.Contains(".png", StringComparison.OrdinalIgnoreCase) ||
        s.Contains(".jpg", StringComparison.OrdinalIgnoreCase) ||
        s.Contains("LocalAppData", StringComparison.OrdinalIgnoreCase);

    /// <summary>一长串连续 base64 字符——像素块被塞进读数的典型形状。</summary>
    private static bool HasPixelBlob(string s) => PixelBlob.IsMatch(s);

    private static readonly System.Text.RegularExpressions.Regex DrivePath = new("[A-Za-z]:[\\\\/]");
    private static readonly System.Text.RegularExpressions.Regex PixelBlob = new("[A-Za-z0-9+/]{40,}={0,2}");

    // ───────────────────────── ③ 导出：刷盘顺序 ─────────────────────────

    /// <summary>
    /// 需求四「导出当前交互会话」。这条格子只判一件事，但它是真会被写错的那件事：
    /// <b>日志是攒一批才落盘的</b>（第十一轮为了去掉每行开一次文件的开销改成这样），
    /// 导出时若不先 Flush，读到的就是半截日志——缺的正好是最近这一次手势。
    /// 所以：先写一条 <c>*.preview</c>（那一族按设计只进缓冲不落盘），
    /// 当场读日志文件确认<b>它不在盘上</b>（证明缓冲是真的），再走产品那个导出方法，
    /// 确认导出的文件里<b>它在</b>（证明导出真的先刷了盘）。两头都量，中间那步才不是空判据。
    /// </summary>
    private static void ExportSessionIncludesBufferedLines()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ls-diag-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(dir);
        string outPath = Path.Combine(dir, "session.txt");
        DiagnosticLog.Start();
        string? logPath = DiagnosticLog.FilePath;
        try
        {
            Battery.Assert(logPath is not null && File.Exists(logPath),
                "夹具自证：这一趟真的要有一份日志文件（否则「导出包含日志」那句是无从判起的）");

            const string marker = "battery.buffered.preview";
            DiagnosticLog.Info(marker, "x=1");           // .preview 族：只进缓冲，按设计不落盘
            // 用产品自己的读取通路取"盘上已有的那部分"（File.ReadAllText 的共享模式会把
            // 还开着的写句柄挡掉——第一次跑这格就是红在那一句上）
            string onDisk = DiagnosticLog.ReadSessionLog();
            Battery.Assert(onDisk.Contains("app.start", StringComparison.Ordinal),
                "夹具自证：会话开头那行已在盘上（缓冲与落盘的分界从这里才量得准）");
            Battery.Assert(!onDisk.Contains(marker, StringComparison.Ordinal),
                "夹具自证：这条 .preview 行此时不该在磁盘上——它已经在的话，" +
                "下面那句「导出里有它」就成了白判（缓冲没生效，判据量不到刷盘这件事）");

            using var form = new MainForm();
            form.CreateControl();
            form.WriteSessionFile(outPath);

            string exported = File.ReadAllText(outPath);
            Battery.Assert(exported.Contains("LongStitch 交互诊断", StringComparison.Ordinal),
                "导出文件的第一段必须是那份读数（与 F8 同源），不是只有日志");
            Battery.Assert(exported.Contains("app.start", StringComparison.Ordinal),
                "导出文件必须带本次会话日志（app.start 那行在会话开头，已刷盘的那部分要在）");
            Battery.Assert(exported.Contains(marker, StringComparison.Ordinal),
                "缓冲里最后那几行必须被刷盘后才读——缺了它就等于「导出的会话文件没有最近一次手势」，" +
                "而这正是人拿着文件来问「刚才那下卡在哪」时最需要的那几行");
            Battery.Assert(exported.Contains("TileCache", StringComparison.Ordinal) ||
                           exported.Contains("命中率", StringComparison.Ordinal),
                "导出的读数里要能认出缓存那一项（需求四点名的十二项之一）");
            Battery.Evidence($"导出 {exported.Length} 字符：盘上原本 {onDisk.Length} 字符的半截日志 + 缓冲里那一条，" +
                             "导出后两者齐备");
        }
        finally
        {
            DiagnosticLog.Close();
            try { if (logPath is not null && File.Exists(logPath)) File.Delete(logPath); } catch { /* 系统回收 */ }
            try { Directory.Delete(dir, true); } catch { /* 系统回收 */ }
        }
    }

    // ───────────────────────── ④ 菜单出口真的挂着 ─────────────────────────

    /// <summary>
    /// 三个诊断出口必须真在「帮助」菜单上、而且当前可点。**标题文字逐字对需求四那份清单**
    /// （复制诊断信息 / 打开诊断日志目录 / 导出当前交互会话）——需求特意写了"目录"而不是
    /// "文件夹"，拿着清单来界面上找入口的人读到的必须是同一个词；哪天有人把它改回"文件夹"，
    /// 这一格就红。
    /// 这里刻意<b>不</b>去 PerformClick：复制会动剪贴板（别的进程占着就失败），
    /// 导出会弹模态框（工装没有对话框可答）——那两条通路由上面 ③ 直接调产品方法量，
    /// 这一格只补"方法写了但没接按钮"这一类呆账。
    /// </summary>
    private static void ThreeDiagnosticsMenuEntriesExist()
    {
        using var form = new MainForm();
        form.CreateControl();
        var menu = form.Controls.OfType<MenuStrip>().FirstOrDefault();
        Battery.Assert(menu is not null, "夹具自证：主窗口要有 MenuStrip");
        var help = menu!.Items.OfType<ToolStripMenuItem>().FirstOrDefault(i => i.Text?.StartsWith("帮助") == true);
        Battery.Assert(help is not null, "菜单里要有「帮助」这一档，三个诊断出口按需求放在这里");
        string[] wanted = ["复制诊断信息", "打开诊断日志目录", "导出当前交互会话"];
        var problems = new List<string>();
        foreach (string caption in wanted)
        {
            ToolStripItem? item = help!.DropDownItems.Cast<ToolStripItem>()
                .FirstOrDefault(i => i.Text?.Contains(caption, StringComparison.Ordinal) == true);
            if (item is null) problems.Add(caption + "（菜单项不存在）");
            else if (!item.Enabled) problems.Add(caption + "（菜单项当前不可点）");
        }
        Battery.Assert(problems.Count == 0,
            $"需求四那三个诊断出口：{string.Join("、", problems)}");
        Battery.Evidence("帮助菜单三项齐备且可点：复制诊断信息 / 打开诊断日志目录 / 导出当前交互会话");
    }

    // ───────────────────────── ⑤ 隐私判据自身的反证 ─────────────────────────

    /// <summary>
    /// 精度红线 §3：给上面那两条「不包含」配一格探针。
    /// 「不包含」这类判据最坏的失效方式是<b>谓词本身恒假</b>——那份读数于是"永远隐私安全"。
    /// 这里不改真实源文件，只在内存里造三份脏文本，要求同一个 <see cref="HasPath"/> 逐条认出来；
    /// 再拿干净读数做正向对照（不许误报），两条都过才算这条判据是活的。
    /// </summary>
    private static void PathDetectorActuallyDetectsPaths()
    {
        string dirty1 = @"帧 P95=12.3ms cwd=C:\Users\someone\Desktop\iflow";
        string dirty2 = "源图=\\\\nas\\share\\chat.png 段数=17";
        string dirty3 = "LocalAppData\\LongStitch\\logs 里第 3 张";
        Battery.Assert(HasPath(dirty1), "探针 1 没认出来：盘符路径是隐私判据最基础的那一种，认不出就是谓词写坏了");
        Battery.Assert(HasPath(dirty2), "探针 2 没认出来：UNC 路径与 .png 文件名都要能认");
        Battery.Assert(HasPath(dirty3), "探针 3 没认出来：日志目录名本身也算路径线索");
        Battery.Assert(HasPixelBlob("data:image/png;base64," + new string('A', 64)),
            "探针 4 没认出来：一长串 base64 就是像素块被塞进读数的形状");
        // 正向对照：真实的读数里有坐标、有百分号、有斜杠分隔的数字，都不该误报
        string clean = "Viewport.TopLeft=(0,35538) zoom=1.000 画布高=1170×7348 px\r\n" +
                       "瓦片 缓存 4 块 命中率 60 % CutY=40/120 比例=0.333";
        Battery.Assert(!HasPath(clean), "干净读数被误判成含路径——那这条隐私判据会把功能判死，等于没人敢开");
        Battery.Assert(!HasPixelBlob(clean), "干净读数被误判成含像素块（数字连排 40 位以上才会算，这里不该命中）");
        Battery.Evidence("四份脏样本全部命中、两份干净对照全部不命中 ⇒ 隐私判据量的确实是「路径与像素」");
    }

    // ───────────────────────── ⑥ 内存快照（第五十九改第一阶段） ─────────────────────────

    /// <summary>
    /// 第五十九改第一阶段的判据：内存快照出口必须<b>五类事件各记一行</b>（import / analyze_all /
    /// scroll / export / project_close），每一行都必须带全承重字段。用户方案点名了十几个读数，
    /// 少任何一个，下一阶段动刀前后就没有对照——所以这里逐字段点名，不数个数了事。
    ///
    /// 两句口径要说清：
    /// ① 这里只判"字段在场、数值可读"，<b>不判数值大小</b>——绝对字节数随机器与负载漂，
    ///   当常驻判据会得到"今天绿明天红"的噪声；数值对照由下一阶段的手工读数承担（改前改后各抄一遍）。
    /// ② 隐私红线顺带钉一条：快照行里不许出现 <c>path=</c>——快照只许有字节数、计数与阶段名。
    /// 最后量 <see cref="MemorySnapshot.Detach"/>：主窗口释放后再记，必须<b>安静地一行不写</b>——
    /// 静态引用不摘就是把整个窗口钉在托管堆上，这正是这一改要修的那类泄漏的反面教材。
    /// </summary>
    private static void MemorySnapshotLogsStagesWithRequiredFields()
    {
        DiagnosticLog.Start();
        string? logPath = DiagnosticLog.FilePath;
        try
        {
            using var form = new MainForm();   // 构造即 Attach（三件套齐了才挂上）
            form.CreateControl();

            foreach (string stage in new[] { "import", "analyze_all", "scroll", "export", "project_close" })
                MemorySnapshot.Log(stage);

            // mem.snapshot 不是 .preview 族：每行当场刷盘，ReadSessionLog 必须立刻读得到。
            string log = DiagnosticLog.ReadSessionLog();
            string[] lines = log.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Where(l => l.Contains(" mem.snapshot ", StringComparison.Ordinal)).ToArray();
            Battery.Assert(lines.Length == 5,
                $"五类事件各记一行：实际 {lines.Length} 行（少了就是接线漏了一个出口，多了就是重复记）");
            foreach (string stage in new[] { "import", "analyze_all", "scroll", "export", "project_close" })
                Battery.Assert(lines.Any(l => l.Contains($"stage={stage}", StringComparison.Ordinal)),
                    $"五类事件里缺 stage={stage} 那一行");

            // 用户方案点名的读数逐个在场。guard/…那格拿本行做变异靶子，这里只认"名字在、值可解析"。
            string[] requiredFields =
            {
                "sourceCacheBytes=", "sourceCacheCount=", "tileCacheBytes=", "tileCacheCount=",
                "tileBitmapCount=", "tileBitmapBytes=", "thumbnailCount=", "managedHeapBytes=",
                "privateBytes=", "workingSetBytes=", "gen0=", "gen1=", "gen2=", "lohBytes=",
                "processHandles=", "decodePeakInFlight=", "matchTemporaryBytesTotal=",
            };
            string sample = lines.First(l => l.Contains("stage=import", StringComparison.Ordinal));
            foreach (string field in requiredFields)
            {
                int at = sample.IndexOf(field, StringComparison.Ordinal);
                Battery.Assert(at >= 0, $"承重字段 {field} 不在快照行里——用户方案点名要的读数少了一个");
                int valueStart = at + field.Length;
                int valueEnd = valueStart;
                while (valueEnd < sample.Length && (char.IsDigit(sample[valueEnd]) || sample[valueEnd] == '-'))
                    valueEnd++;
                Battery.Assert(long.TryParse(sample[valueStart..valueEnd], out long v) &&
                              (v >= 0 || field == "lohBytes"),
                    $"承重字段 {field} 的值解析不出来（快照行必须写成 key=十进制数 的形状）");
            }
            Battery.Assert(sample.Contains("path=", StringComparison.Ordinal) is false,
                "快照行里不许出现 path=（隐私红线：快照只许有字节数、计数与阶段名）");
            Battery.Assert(sample.Contains("sourceCacheCount=0", StringComparison.Ordinal),
                "刚建好的空项目源图缓存必须是 0——连这个都是别的数就说明计数器接错了来源");

            // Detach 之后必须安静：不写一行、也不许炸。
            form.Dispose();
            long before = DiagnosticLog.AppendRequests;
            MemorySnapshot.Log("export");
            Battery.Assert(DiagnosticLog.AppendRequests == before,
                "主窗口释放后再记快照必须一行不写（Detach 没摘干净就是把窗口钉在托管堆上的前兆）");
            Battery.Evidence($"五类事件各 1 行共 {lines.Length} 行、{requiredFields.Length} 个承重字段逐个在场且可解析、" +
                             "快照行无 path=、Detach 后安静");
        }
        finally
        {
            DiagnosticLog.Close();
            try { if (logPath is not null && File.Exists(logPath)) File.Delete(logPath); } catch { /* 系统回收 */ }
        }
    }

    // ───────────────────────── ①c 需求五那笔"进程调度信息"（从推算变成实量） ─────────────────────────    /// <summary>
    /// 需求五原话："如果由于操作系统抢占出现偶发 80–150ms：记录 GC 和<b>进程调度信息</b>"。
    /// GC 那一半上一批已经接进诊断出口了；调度那一半当时只有一句推论——"墙钟减 CPU 时间差出去的就是被抢占的那截"。
    /// 推论没错，但它<b>不是量</b>：换台机器、换个负载，谁也没法说那截到底多长；
    /// 而且那个差里混着后台解码线程的时间，本身就分不清是"谁"在等。
    ///
    /// ═══ 这一平台能给什么，是先探过才写的 ═══
    /// .NET 8 的 <c>ProcessThread</c> 在 Windows 上给 <c>UserProcessorTime</c>、<c>PrivilegedProcessorTime</c>、
    /// <c>TotalProcessorTime</c>、<c>ThreadState</c>、<c>WaitReason</c>；
    /// <b>不给</b> <c>ContextSwitchCount</c>（.NET Framework 的 Windows-only 成员，没移植过来）。
    /// 要它只能装性能计数器包，而本项目零 NuGet 依赖 ⇒ 出口把那一笔明写成"按未量记"，
    /// 下面第一格专门判它不许写成 0（"恒为 0 的字段"与"这趟真的没发生"在屏上长得一模一样）。
    ///
    /// ═══ 两头都钉（与 ①b 同一形状）═══
    /// ① 字面齐备；② 出口打印的那笔"求和"必须落在<b>工装自己逐条线程相加</b>量到的 [前, 后] 区间里——
    ///    写死一个数、或者压根没逐条线程去问操作系统，都不会落进这个区间。
    ///    工装这里是<b>独立的第二份证人</b>（自己走 <c>Process.Threads</c> 求和），不是把产品那行再读一遍。
    ///
    /// 措辞纪律：那一行写的是"最忙那条"，没写"UI 线程"——.NET 不公开托管线程对应的 Win32 线程 id，
    /// 要精确点名就得为诊断读数引一条 kernel32 声明，这个仓库至今零 P/Invoke，为这一笔不值当。
    /// </summary>
    private static void SchedulingAttributionComesFromOs()
    {
        using HudCostTests.Harness h = HudCostTests.Harness.New();
        h.Canvas.ShowDebugHud = true;
        h.Render();

        double ownBefore = OwnSumThreadTotalMs();
        string text = h.Canvas.DescribeDiagnostics();
        double ownAfter = OwnSumThreadTotalMs();

        string[] needed =
        {
            "线程与调度：", "条线程", "在等", "求和 ", "单条 ", "那一刻状态", "上下文切换次数", "按未量记",
        };
        var missing = needed.Where(l => !text.Contains(l, StringComparison.Ordinal)).ToList();
        Battery.Assert(missing.Count == 0,
            $"需求五那笔进程调度读数缺 {missing.Count} 个字段：{string.Join("、", missing)}");
        // 拿不到的那一笔（上下文切换）必须写成"按未量记"：写成 0 就与"这趟真的一次没切换"同形了。
        Battery.Assert(!text.Contains("上下文切换 0", StringComparison.Ordinal)
                       && !text.Contains("切换 0 次", StringComparison.Ordinal),
            "这一平台拿不到上下文切换次数，出口里出现「0 次」就是让一个恒为 0 的字段冒充一次测量");

        double printed = ParseAfter(text, "求和 ");
        Battery.Assert(ownBefore > 0,
            $"夹具自证：工装自己把各条线程的 CPU 加起来是 {ownBefore:F0}ms——" +
            "真跑过消息泵的进程不可能一毫秒都没花；它要是 0，下面那句「落在区间里」就是恒真的白判据");
        Battery.Assert(printed >= ownBefore - 1 && printed <= ownAfter + 1,
            $"出口打印的那笔「求和」（{printed:F0}ms）必须落在工装前后各加一次的区间 " +
            $"[{ownBefore:F0}, {ownAfter:F0}]ms 里——写死的数、或者压根没逐条线程去问操作系统，都不会落进来");
        Battery.Evidence($"线程与调度：出口「求和」{printed:F0}ms，工装独立加到 [{ownBefore:F0}, {ownAfter:F0}]ms，" +
                         $"线程条数 {ParseAfter(text, "线程与调度：")}，「单条」{ParseAfter(text, "单条 ")}ms");
    }

    /// <summary>
    /// 这一格是上一格的反证（精度红线 §3：能抓 bug 的断言必须配一格注缺陷要求它红）。
    /// 注入方式是内存里的：<b>什么都不改</b>，只是在这条线程上空转到攒够 CPU 时间（15ms 就停，上限 400ms）——
    /// 空转消耗的就是这条线程自己的用户态 CPU，所以「全部线程求和」一定涨。
    /// 一个写死的常数、或只在启动时取过一次的缓存，在这一句上必红。
    ///
    /// ═══ 这一格第一次跑红，红出来的不是产品，是判据选错了钉子 ═══
    /// 我原本钉的是「最忙那条」：空转 40ms 之后它一字未动（1188 → 1188），因为<b>空转的这条线程本来就不在最忙</b>。
    /// 那一次红是对的——它拒绝了一条"永远照不出退化"的判据。所以正向对照必须钉在<b>求和</b>那一笔上：
    /// 任何一条线程烧 CPU 它都跟着动。"最忙那条"留给归因用（它才是拖动要看的那条），两笔不许互相顶替。
    /// </summary>
    /// <b>为什么窗口不能钉死一个时间</b>：本批现场红过一次——那 40ms 整机被别的进程占满，
    /// 这条线程 0ms CPU 都没攒到（复跑两遍全绿）⇒ 固定时间窗会把环境偶发写成"产品坏了"，那是假红。
    /// 现在"攒到 15ms 为止、最多等 400ms"，阈值没降；另加一条<b>出口必须跟得上独立证人（差 ≤5ms）</b>——
    /// 只要求最低增幅时，一份"每 100ms 才刷新一次"的缓存值也能蒙过去。
    /// </summary>
    private static void FrozenSchedulingReadingWouldNotGrow()
    {
        using HudCostTests.Harness h = HudCostTests.Harness.New();
        h.Canvas.ShowDebugHud = true;
        h.Render();

        double printedBefore = ParseAfter(h.Canvas.DescribeDiagnostics(), "求和 ");
        double ownBefore = OwnSumThreadTotalMs();
        // ═══ 空转前先把系统计时器分辨率申请到 1ms ═══
        // Windows 记线程 CPU 时间靠时钟中断：当整机没人申请细计时器（分辨率退回 15.6ms）
        // 且机器整体空闲时，busy 线程的 CPU 时间会被严重欠账（本机实测：busy 600ms 只记到
        // 0~62ms，sleep 也不涨；timeBeginPeriod(1) 后 busy 600ms 能记到 515ms）。
        // 也就是说这格的绿红取决于"别的程序有没有占着 1ms 计时器"的抽签，与环境无关的实现
        // 也会被判成环境红。申请 1ms 分辨率是工装侧（Checks 允许 P/Invoke）把抽签钉死；
        // 产品读数与本格在同一进程里，共享同一份记账口径，不构成"判据环境与产品环境不一致"。
        // ═══ 两个杠杆都要拉，缺一不可 ═══
        // 独立探针实测（第六十四改当日）：timeBeginPeriod(1) **加** High 优先级两者并用，
        // busy 600ms 才能记到 515ms；只拉其中一个、或两个都不拉，都可能欠账。
        // 全量电池里也验证过反面：只申请 1ms 分辨率时，guard 单跑绿、全量跑红过两次
        // （109→109、281→281，空转都撞满 411ms 上限，中途补申请也救不回来）
        // ⇒ 单靠分辨率不保证这台机器记账。产品读数与本格在同一进程里共享同一份记账口径，
        // 临时提优先级只影响工装进程自身，跑完 finally 里还原，不构成"判据环境与产品环境不一致"。
        // timeBeginPeriod 成功时返回 0（TIMERR_NOERROR），失败返回 TIMERR_NOCANDO——
        // 它不返回"之前的分辨率"，所以归还时必须原样传 1，不能拿返回值当旧值传。
        int beginResult = timeBeginPeriod(1);
        System.Diagnostics.ProcessPriorityClass savedPriority =
            System.Diagnostics.Process.GetCurrentProcess().PriorityClass;
        System.Diagnostics.Process.GetCurrentProcess().PriorityClass =
            System.Diagnostics.ProcessPriorityClass.High;
        bool reRequested = false;
        try
        {
            SchedulingSpinUntilOwnSumGrows(ownBefore, h, out reRequested,
                out double ownAfter, out double printedAfter, out double spun);
            AssertSchedulingReadingTracksCpu(ownBefore, ownAfter, printedBefore, printedAfter, spun);
            Battery.Evidence($"计时器分辨率申请返回 {beginResult}、进程优先级 {savedPriority}→High" +
                             (reRequested ? "（空转中途补申请过一次）" : "（一次到位）"));
        }
        finally
        {
            System.Diagnostics.Process.GetCurrentProcess().PriorityClass = savedPriority;
            if (beginResult == 0) timeEndPeriod(1);
            if (reRequested) timeEndPeriod(1);
        }
    }

    /// <summary>winmm 计时器分辨率申请/撤销（winmm.dll，Checks 工装允许 P/Invoke）。</summary>
    [DllImport("winmm.dll")]
    private static extern int timeBeginPeriod(uint ms);

    [DllImport("winmm.dll")]
    private static extern int timeEndPeriod(uint ms);

    // ─── 红账环境自报用的四个 kernel32 入口（只在这一格红的时候被调） ───
    [DllImport("kernel32.dll")]
    private static extern bool IsProcessInJob(IntPtr process, IntPtr job, out bool isInJob);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemTimeAdjustment(out uint adjustment, out uint increment, out bool disabled);

    /// <summary>
    /// 空转攒不到 CPU 时间时拼出的环境自报。三个疑点各量一头：
    /// ① 系统此刻<b>真实</b>的计时器中断增量（申请了 1ms 不等于系统真的在 1ms 打点）；
    /// ② 当前线程（就是空转那条）再烧 120ms 看它<b>自己</b>被记了多少——
    ///   它自己是 0 说明整条通路（调度→记账→读数）都没走，它有数而求和不动才是读数层的毛病；
    /// ③ 进程是否被套在某个 Job 里——CPU 限额型 Job 会把分辨率与优先级两个杠杆全部作废，
    ///   且表现恰好是"墙钟照走、CPU 一笔不记"。
    /// </summary>
    private static string DescribeAccountingEnvironment()
    {
        uint tid = GetCurrentThreadId();
        double mineBefore = -1;
        foreach (System.Diagnostics.ProcessThread t in System.Diagnostics.Process.GetCurrentProcess().Threads)
            if (t.Id == tid) { mineBefore = t.TotalProcessorTime.TotalMilliseconds; break; }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 120) { }
        double mineAfter = -1;
        foreach (System.Diagnostics.ProcessThread t in System.Diagnostics.Process.GetCurrentProcess().Threads)
            if (t.Id == tid) { mineAfter = t.TotalProcessorTime.TotalMilliseconds; break; }
        GetSystemTimeAdjustment(out uint _, out uint increment, out bool _);
        IsProcessInJob(GetCurrentProcess(), IntPtr.Zero, out bool inJob);
        using System.Diagnostics.Process proc = System.Diagnostics.Process.GetCurrentProcess();
        return $"; 环境自报：真实中断增量 {increment * 0.0001:F2}ms、进程在 Job 里={inJob}、" +
               $"当前线程自己 120ms 只被记 {mineAfter - mineBefore:F0}ms、优先级 {proc.PriorityClass}、" +
               $"线程数 {proc.Threads.Count}";
    }

    private static void SchedulingSpinUntilOwnSumGrows(double ownBefore, HudCostTests.Harness h,
        out bool reRequested, out double ownAfter, out double printedAfter, out double spun)
    {
        // ═══ 空转多久要自适应，不能钉死一个时间窗 ═══
        // 阈值（15ms）不降，只是把"确实烧到 CPU"这件事的**等待上限**从固定 40ms 改成"最多 400ms，
        // 一到 15ms 就停"。原因不是理论风险：本批就红过一次——那 40ms 整机被别的进程占满，
        // 这条线程 0ms CPU 都没攒到（复跑两遍全绿，红的是环境不是实现）。
        // 固定窗口会把这种偶发写成"产品坏了"，那是假红；上限 400ms 之内还攒不到 15ms，
        // 才是真·这台机器不记线程 CPU 时间，那时判环境、不判产品。
        // ═══ 另：1ms 分辨率不保证撑住，过半没攒到就补拉一次 ═══
        // 全量电池里红过两次（guard 单独跑绿）：申请了 1ms 分辨率，空转 411ms 仍然
        // 一笔 CPU 都没记账，中途补申请也没救回来（那次红的真正缺的是另一个杠杆——
        // 进程优先级，见调用方）。补申请本身无害（幂等引用计数，归还按次数对称，
        // 不会顶掉别人的申请），留着当多一道保险。
        reRequested = false;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        double after = ownBefore;
        do
        {
            after = OwnSumThreadTotalMs();        // 这一下枚举本身就烧 CPU，循环不会空转到天荒地老
            if (!reRequested && sw.ElapsedMilliseconds > 150 && after - ownBefore < 15
                && timeBeginPeriod(1) == 0)
            {
                reRequested = true;
            }
        }
        while (after - ownBefore < 15 && sw.ElapsedMilliseconds < 400);
        ownAfter = after;
        printedAfter = ParseAfter(h.Canvas.DescribeDiagnostics(), "求和 ");
        spun = sw.ElapsedMilliseconds;
    }

    private static void AssertSchedulingReadingTracksCpu(double ownBefore, double ownAfter,
        double printedBefore, double printedAfter, double spun)
    {
        Battery.Assert(ownAfter - ownBefore >= 15,
            $"夹具自证：空转 {spun}ms 之后，工装自己加到的求和只从 {ownBefore:F0} 走到 {ownAfter:F0}ms——" +
            "这台机器要是不记线程自己的 CPU 时间，下面那句「出口也必须涨」就是空判据（这一格改判环境，不改判产品）" +
            DescribeAccountingEnvironment());
        Battery.Assert(printedAfter - printedBefore >= 15,
            $"这条线程已经烧到 {ownAfter - ownBefore:F0}ms 的用户＋内核时间（等了 {spun}ms），" +
            $"出口那行「求和」却从 {printedBefore:F0}ms 只到 {printedAfter:F0}ms：" +
            "说明它是写死的常数或不再更新的缓存，而不是逐条线程问操作系统来的" +
            "（需求五要的那笔「进程调度信息」就成了一句空话）");
        // 第二头：出口那笔必须<b>跟得上</b>独立证人，不只是"涨了 15ms"就算过——
        // 只要求最低增幅时，一个"每过 100ms 才更新一次"的缓存值也能蒙过去。
        Battery.Assert(printedAfter - printedBefore >= ownAfter - ownBefore - 5,
            $"出口那笔只涨了 {printedAfter - printedBefore:F0}ms，而工装独立加到 {ownAfter - ownBefore:F0}ms：" +
            "两者相差超过 5ms 就说明出口不是每次现问操作系统，而是拿了一份会过期的缓存");
        Battery.Evidence($"空转 {spun}ms：出口 {printedBefore:F0}→{printedAfter:F0}ms、" +
                         $"工装独立 {ownBefore:F0}→{ownAfter:F0}ms（差 {ownAfter - printedAfter:+0.0;-0.0}ms）" +
                         "⇒ 这一笔确实跟着线程自己的 CPU 时间动，且跟得上独立证人");
    }

    /// <summary>
    /// 工装自己走一遍 <c>Process.Threads</c> 把所有线程的用户＋内核 CPU 加起来，
    /// 作为产品那一行的<b>独立第二份证人</b>（同一套操作系统数据，不同调用点）。
    /// 中途有线程退出就跳过那一条。
    /// </summary>
    private static double OwnSumThreadTotalMs()
    {
        using System.Diagnostics.Process proc = System.Diagnostics.Process.GetCurrentProcess();
        double sum = 0;
        foreach (System.Diagnostics.ProcessThread t in proc.Threads)
        {
            try { sum += t.TotalProcessorTime.TotalMilliseconds; }
            catch (Exception) { /* 线程刚退出或拿不到：跳过那一条 */ }
        }
        return sum;
    }
}
