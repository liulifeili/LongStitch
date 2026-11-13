using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using LongStitch.App.UI;
using LongStitch.Core;
using LongStitch.Core.Geometry;
using LongStitch.Core.Imaging.Png;
using LongStitch.Core.Layout;
using LongStitch.Core.Model;

namespace LongStitch.Checks;

/// <summary>
/// 第十三轮需求五：<b>真实 WinForms 窗口里的性能测量</b>。
///
/// ═══ 为什么要有这一趟，而不是继续用电池里那格"100 次 MouseMove" ═══
/// 电池那一格量的是"手势状态机 + 预览合并逻辑"，它是<b>逻辑夹具</b>：
/// 控件没有真正显示、没有真的 HWND、没有真的 Windows 消息队列，
/// 于是"界面到底画了多久""消息在队列里排了多久"这两件事它天生量不到。
/// 需求八明确禁止"把 GUI 性能测试改成纯函数测试后宣称 GUI 已流畅"，所以这里：
/// <list type="bullet">
///   <item>真的 <see cref="MainForm"/>、真的 <c>Show()</c>、真的消息泵；</item>
///   <item>鼠标移动用 <c>PostMessage(WM_MOUSEMOVE)</c> 投进那个 HWND，走 Windows 的输入队列，
///         而不是直接调 <c>MouseMoveTo</c>；</item>
///   <item>读数全部来自控件自己在 <c>OnPaint</c> 里记的那一份（Paint 次数、帧耗时、瓦片渲染、
///         整块清空、RenderWhole 次数、队列延迟）。</item>
/// </list>
///
/// ═══ 用法 ═══
/// <code>
///   dotnet run --project checks/LongStitch.Checks -c Release -- --gui
///   dotnet run --project checks/LongStitch.Checks -c Release -- --gui &lt;图片目录&gt; --moves 100 --runs 5 --hud
/// </code>
/// <c>--runs N</c> 是需求五那条"连续运行 5 次"的落点：同一形状连跑 N 趟，逐趟结清、逐趟判阈值，
/// 分位数（P50/P95）在 N 趟合并的帧样本池上算——单趟只有 5–7 帧时 P95 会退化成"倒数第二慢那一帧"。
/// 合并池<b>不是</b>放宽：每趟自己的 P50/P95/最慢照样逐行打印，"单帧 ≤ 50ms"仍按逐趟最严判。
/// HUD 开与关各跑一趟（加不加 <c>--hud</c>），两档读数分开报——需求三不许混为一谈。
/// 默认读 <c>test/</c>（仓库外、已 gitignore 的真截图）。目录不存在或不足 17 张时，
/// 自动在 %TEMP% 造 17 张 1179×2556 的合成图——<b>测量的是同一套绘制通路</b>，
/// 但"真截图上的真实内容"这一条就只有 test/ 存在时才算量到，输出里会明说。
///
/// 退出码：0 = 全部阈值通过；1 = 有阈值没过；2 = 环境跑不起来（此时必须按 NOT-RUN 记账，
/// 不许把离线电池的结果当成"真实 GUI 已验证"）。
/// </summary>
internal static class GuiPerfHarness
{
    internal const uint WmMouseMove = 0x0200;
    private const uint WmLButtonDown = 0x0201;
    private const uint WmLButtonUp = 0x0202;
    private const int MkLButton = 0x0001;

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    /// <summary>画布坐标系里的一点 → 该点的 lParam（低 16 位 X、高 16 位 Y，Win32 的原样约定）。</summary>
    internal static IntPtr MakeLParam(int x, int y) => new((x & 0xFFFF) | ((y & 0xFFFF) << 16));

    public static int Run(string[] args)
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch (IOException) { /* 重定向时可能失败 */ }

        string dir = args.Length > 1 && !args[1].StartsWith("-") ? args[1] : "test";
        int moves = ParseInt(args, "--moves", 100);
        int runs = ParseInt(args, "--runs", 1);
        bool hud = args.Contains("--hud");
        // --no-compare：把重影对比层关掉。开这一档只为一件事——把"调试层到底值多少钱"拆开量：
        // 色块那一层（绿/黄/白/红箭头）由 DrawAlignmentOverlay 画，它**不在读数层的计时段里**，
        // 而对比层默认就是开的，所以"开读数层多花的钱"与"开色块层多花的钱"必须分成三个组合量：
        //   关+开(默认) / 开+开 / 关+关 —— 前两个之差＝读数层自己，后两个之差＝色块层。
        bool compare = !args.Contains("--no-compare");
        // --wheel N / --zoom N：真窗口里的第二、三种手势（滚轮平移 N 格、Ctrl+滚轮缩放 N 档），
        // 在同一个进程、同一个窗口里接着拖动通道之后跑，跑法与阈值见 GuiGestureHarness。
        int wheel = ParseInt(args, "--wheel", 0);
        int zoom = ParseInt(args, "--zoom", 0);
        // --at-zoom N：整趟量哪一档缩放（百分比，默认 35＝沿用已写进文档的那批读数的起点）。
        // 需求五的阈值原文没按缩放档分档，所以这一路只换缩放、不换阈值：100% 与 25% 各量一遍，
        // 红了就照红写（"别的缩放档仍未量"这条欠账就是这么销的，量不到的档仍然留在未量清单里）。
        int atZoom = ParseInt(args, "--at-zoom", 35);
        // --cycles N：混合长会话跑几轮（默认 0＝不跑）。这一档不是"把 --runs 调大"的别名：
        // --runs 是同一个手势反复跑，--cycles 是一轮里把换缝／切档／三种拖动／撤销／存包／开包串起来，
        // 需求三那句"长期帧率稳定性"要的是后者（见 MixedCycles 的注释）。
        int cycles = ParseInt(args, "--cycles", 0);

        int exit = 0;
        // 真窗口要 STA：Main 未必是 STA 线程，这里显式起一条并 join，读数照样在这条线程上收集。
        var thread = new Thread(() => exit = Measure(dir, moves, hud, runs, compare, wheel, zoom, atZoom, cycles))
        {
            IsBackground = false,
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return exit;
    }

    private static int ParseInt(string[] args, string name, int fallback)
    {
        int at = Array.IndexOf(args, name);
        if (at < 0 || at + 1 >= args.Length) return fallback;
        return int.TryParse(args[at + 1], out int v) ? v : fallback;
    }

    /// <summary>
    /// 从产品自己的诊断出口那一行「线程与调度」里取回来的两枚数。
    /// 为什么走文本而不是直接调 <c>ThreadScheduling</c>：那个类是 App 程序集的 internal，
    /// 而这个仓库没有 InternalsVisibleTo——为工装开这个口子不值得。
    /// 而且"读出口原文"本身就是要证的事：需求五那笔调度信息得是<b>人能从出口读到的</b>，
    /// 不是只活在工装进程里的一堆计数器。
    ///
    /// 拿不到的那一笔（上下文切换次数）不在这里补：.NET 8 的 <c>ProcessThread</c> 没有
    /// <c>ContextSwitchCount</c>，要它只能装性能计数器包（本项目零 NuGet 依赖）。
    /// 出口那行已经把这一笔明写成"按未量记"，工装不许把它偷偷变成一个数。
    /// </summary>
    internal struct Scheduling
    {
        public int Threads;
        public double SumTotalMs;       // 全部线程的用户＋内核 CPU 之和（进程累计）
        public double BusyTotalMs;      // 取读数那一刻"累计 CPU 最多的那条线程"的时间
        public bool Ok;

        /// <summary>取不到就返回 Ok=false：宁可报"没读到"，也不许把 0 当成一次测量。</summary>
        public static Scheduling Read(StitchCanvasControl canvas)
        {
            string text = canvas.DescribeDiagnostics();
            var s = new Scheduling();
            int at = text.IndexOf("线程与调度：", StringComparison.Ordinal);
            if (at < 0) return s;
            int end = text.IndexOf('\n', at);
            if (end < 0) end = text.Length;
            string line = text.Substring(at, end - at);
            long threads = AfterInt(line, "线程与调度：");
            double sum = AfterDouble(line, "求和 ");
            double busy = AfterDouble(line, "单条 ");
            if (threads < 0 || sum < 0 || busy < 0) return s;
            s.Threads = (int)threads;
            s.SumTotalMs = sum;
            s.BusyTotalMs = busy;
            s.Ok = true;
            return s;
        }

        /// <summary>
        /// 两次快照之间的<b>增量</b>。<b>线程条数掉了就不给增量</b>：那一笔「全部线程 CPU 之和」是逐条相加的，
        /// 一条线程退出（后台解码干完活）会把它那份累计时间整个带出求和，于是增量能红成负数——
        /// 混合长会话第 10 轮就读到 全线程 −1594ms（那一轮线程从 20 掉到 19）。
        /// 那不是测量，是<b>这一笔量不到</b>：报 −1 并带上原因，不许把负数解释成"这一趟没花 CPU"
        /// （同一族老毛病：恒为 0 的字段与"真的没发生"在屏上长得一模一样）。
        /// </summary>
        public static bool Delta(Scheduling before, Scheduling after,
            out double busyMs, out double sumMs, out string note)
        {
            busyMs = -1; sumMs = -1; note = "";
            if (!before.Ok || !after.Ok) { note = "两端有一端没读到"; return false; }
            if (after.Threads < before.Threads)
            {
                note = $"线程从 {before.Threads} 掉到 {after.Threads}，求和跟着掉，这一笔按未量记";
                return false;
            }
            double busy = after.BusyTotalMs - before.BusyTotalMs, sum = after.SumTotalMs - before.SumTotalMs;
            if (busy < 0 || sum < 0)
            {
                note = $"读出负增量（单条 {busy:F0}／求和 {sum:F0}ms），按未量记";
                return false;
            }
            busyMs = busy; sumMs = sum;
            return true;
        }

        private static long AfterInt(string text, string key)        {
            int at = text.IndexOf(key, StringComparison.Ordinal);
            if (at < 0) return -1;
            int i = at + key.Length;
            while (i < text.Length && char.IsDigit(text[i])) i++;
            return i == at + key.Length ? -1 : long.Parse(text.Substring(at + key.Length, i - at - key.Length));
        }

        private static double AfterDouble(string text, string key)
        {
            int at = text.IndexOf(key, StringComparison.Ordinal);
            if (at < 0) return -1;
            int i = at + key.Length;
            while (i < text.Length && (char.IsDigit(text[i]) || text[i] == '.')) i++;
            return i == at + key.Length ? -1 : double.Parse(text.Substring(at + key.Length, i - at - key.Length));
        }
    }

    /// <summary>
    /// 一趟拖动的全部读数（需求五点名的那十一项一项不少）。
    /// 单独成类是为了"逐趟判阈值"——把 N 趟的数摊平成一个平均数，就把偶发那一帧的 150ms 抹平了，
    /// 而那恰恰是需求五不许做的事。
    /// </summary>
    private sealed class RunReadings
    {
        public int Run;
        public int Moves;
        public long MouseEvents;
        public int MovesDelivered;   // 这一趟真被取走的移动条数（工装自证：没送到的那条不算拖动）
        public long PreviewFrames;
        public long Paints;
        public int Samples;
        public double Avg, P50, P95, Max;
        public double ReleaseFrameMs;
        public long TileRenders;
        public long WholeRuns;
        public long LayoutBuilds;
        public long Clears;
        public long ClearsAfterRelease;
        public long PreviewRecomputes;
        public double PreviewRecomputeMs;
        public long Exports;
        public int GcGen0, GcGen2;
        // 第三十二改：本趟拖动帧的<b>托管堆</b>分配（字节与帧数分开交，按帧归一才是跨批可比的）。
        // 一次 gen0 能吞掉几万字节，"每帧多拷一块缓冲"这种事 GC 次数那行看不见。
        public long AllocBytes, AllocFrames;
        public double QueueDelayMs, AliveLatencyMs;
        public double WallMs, CpuMs;
        public double CacheHit;
        // 本趟"局部丢了几个块、LRU 挤了几个块"（需求五那道"整块清 = 0"管不到的那一半）。
        public long TileDrops, TileTrims;
        // 需求五"记录进程调度信息"那一笔的<b>本趟增量</b>（从产品诊断出口那行读回来的，见 Scheduling）。
        // −1 ＝ 没读到（读不到就明写"没读到"，不许报 0 冒充一次测量）。
        public int SchedThreads = -1;
        public double ThreadCpuMs = -1, SumCpuMs = -1;
        public long HudGeometryRebuilds, HudPanelRebuilds;
        public long HudPanelAllocations;
        public double HudPanelAllocMs, HudPanelReuseMs;
        public double HudGeometryMs, HudTextMs, HudOutlineMs, HudBlitMs;
        public double HudBorderMs, HudHitsMs;
        public int HudHitRectsDrawn;
        // 面板两条重画路各自走了几次（本趟增量）。第十五改加这一笔的理由写在打印那一行。
        public string PanelRoads = "";
        public double OverlayStrokeMs, OverlayFillMs;
        public StitchCanvasControl.HudSplit Hud;
        public double[] DragFrames = Array.Empty<double>();
        public double[] HudOfFrames = Array.Empty<double>();
        public string SamplesText = "";
        public string SlowestStages = "";
        // 第五改：叠层那一笔往下的六列拆账（与上面那句同源，都是最慢那帧的，不是最后一帧的）。
        public string SlowestOverlay = "";
        // 第十四改：读数层那一笔往下的拆账，同样取<b>最慢那帧</b>的那一份（关着读数层时产品交回空串）。
        public string SlowestHud = "";
        // ═══ 本趟"这一趟到底动了什么"（第十四轮 #95 补的读数）═══
        // 起因：撤销档那三枚<b>前提</b>判据在一批 5 趟的跑法里红过（重叠 9→9、视口不变、画布高不变），
        // 而当时没有任何数能分开这两种解释：① 拖动按增量累计、下冲撞上限后回来少一截（棘轮，属产品）；
        // ② 起点是按第一趟的带算好后固定的，几趟之后那个像素点命中的已经不是同一条缝（属工装序列）。
        // 所以每一趟各记一次：按下命中的是谁、这一趟起止重叠/蓝线/视口各是多少。
        // <b>只加读数，不加判据</b>——判据是那一族原本就有的，量不到才是要补的东西。
        public string HitAtPress = "";
        public string SeamTracked = "";
        public int OverlapBefore, OverlapAfter, CutBefore, CutAfter;
        public IntPoint ViewportBefore, ViewportAfter;
        public string DragDeviceDelta = "";
        // 第二十改：本趟产品自己记的那条 trace 走到哪个阶段（PendingSeamClick / AlignSeam / MoveSeam）。
        // 判"这一批帧到底是不是拖动途中帧"读的是它＋上面那条送达数，<b>不是</b>末点的净参数改动。
        public string TraceStage = "";
        public bool GestureLanded;
    }

    /// <summary>
    /// 真把鼠标消息投进那个 HWND 跑一趟，结清读数。
    /// 形状：来回拖 <paramref name="moves"/> 次、最大偏移 10 设备像素（≈29 源图行），
    /// 带从 120 行变成 91↔149 行、重叠始终 &gt; 0——这才是"用户调对齐"的真实形状。
    /// 一开始这里写的是"一路往下拖 100 像素"，结果重叠被拖成 0、整张画布重排，
    /// 量到的是"重新排版"而不是"拖动预览"，读数会误导（69ms 那一版就是这种形状）。
    /// </summary>
    private static RunReadings DragOnce(StitchCanvasControl canvas, Project project,
        Point start, int moves, int run)
    {
        var r = new RunReadings { Run = run, Moves = moves };
        canvas.ResetFrameStats();
        long previewBefore = canvas.PreviewFramesForTest;
        long tilesBefore = canvas.TileRendersForTest;
        long clearsBefore = canvas.TileFullInvalidationsForTest;
        // 需求五那道门只管得到"整块清了几次"，管不到"一次丢十几块"。拖动途中 4~6% 的命中率是后者造的，
        // 而"作废范围宽"与"缓存装不下被 LRU 挤掉"两种原因的修法相反，所以这两笔必须各记各的。
        long dropsBefore = canvas.TileRegionDropsForTest, trimsBefore = canvas.TileTrimEvictionsForTest;
        long wholeBefore = LongStitch.Core.CanvasComposer.WholeRunCountForTest;
        long exportsBefore = LongStitch.Core.Export.ExportEngine.RunCountForTest;
        long layoutBefore = CanvasLayout.BuildCountForTest;
        // 调度读数要<b>先于</b> GC 基线取：诊断出口自己会分配，放在后面就会把这一笔算进"这一趟回收了几次"。
        Scheduling schedBefore = Scheduling.Read(canvas);
        int gen0Before = GC.CollectionCount(0), gen2Before = GC.CollectionCount(2);
        // 分配读数是进程累计（ResetFrameStats 不清它），所以和 GC 那两行一样<b>先记基线、结束作差</b>。
        long allocBefore = canvas.FrameAllocBytesTotalForTest;
        long allocFramesBefore = canvas.FrameAllocSamplesForTest;
        // 读数层拆账取**本趟增量**：那几项是进程累计，连着 5 趟只会一路涨，
        // 上一批就出现过"子项的累计数比父项这一趟的合计还大"的读法混乱（描边累计 40.6ms vs 本趟 HUD 整段 11.2ms）。
        // 起点先记下，结束时作差——每一趟的拆账只说这一趟的事。
        (double Geo, double Text, double Outline, double Blit, double Border, double Hits, int HitRects,
         double Clear, double Draw, long Wise, long Whole, long LinesPainted, long Rebuilds, long Allocs) hudBefore =
            (canvas.HudGeometryRebuildMs, canvas.HudTextBuildMs, canvas.HudOutlineMs, canvas.HudBlitMs,
             canvas.HudBorderOutlineMs, canvas.HudHitsOutlineMs, canvas.HudHitRectsDrawn,
             StitchCanvasControl.HudPanelClearMs, StitchCanvasControl.HudPanelDrawTextMs,
             canvas.HudPanelLineWiseRepaints, canvas.HudPanelWholeRepaints, canvas.HudPanelLinesRepainted,
             canvas.HudPanelRebuilds, canvas.HudPanelAllocations);
        IntPtr hwnd = canvas.Handle;
        // ═══ 按下之前先问一次命中（第十四轮 #95 补的读数）═══
        // 起点是<b>照第一趟的带</b>算好后固定的，几趟之后那个像素点命中的还是不是同一条缝，
        // 光看起点坐标判不出来——上一批"撤销前提红（重叠 9→9）"就是因为缺这一笔数，
        // 两种解释（拖动棘轮／命中的已经不是那条缝）分不开，只能写成现象。
        // 现在每趟各记一次：按下命中谁、这一趟起止的重叠/蓝线/视口各是多少。
        // <b>只加读数，一条判据都没加、也没动</b>。
        StitchCanvasControl.SeamHit? hitAtPress = canvas.HitTestSeamEditRegion(start);
        string? trackedId = hitAtPress?.SeamId;
        r.HitAtPress = hitAtPress is null
            ? "没命中任何编辑区"
            : $"{hitAtPress.Value.SeamId}·{hitAtPress.Value.Kind}·固定{hitAtPress.Value.Side}";
        r.SeamTracked = trackedId ?? "";
        Seam? seamBefore = FindSeamById(project, trackedId);
        if (seamBefore is not null)
        {
            r.OverlapBefore = seamBefore.OverlapLength;
            r.CutBefore = seamBefore.CutPosition;
        }
        r.ViewportBefore = canvas.Viewport.TopLeft;
        Process proc = Process.GetCurrentProcess();
        TimeSpan cpuBefore = proc.TotalProcessorTime;
        long wallBefore = Environment.TickCount64;

        // ═══ 按下之前先把"起点站"送一条移动（本批量出来的第二个假象）═══
        // 真人的光标在按下那一刻就停在按下点上，所以产品的第一次位移必然是 0；
        // 工装是 PostMessage，物理光标其实停在别处，Windows 在设 capture 时会补发一条
        // <b>物理光标位置</b>的 WM_MOUSEMOVE——上一批读到"第 1 趟重叠 120→1071、第 2~5 趟一字不动"
        // 就是这个形状（1071−120＝951 源行 ≈ 333 设备像素，正是起点到屏上某处的距离）。
        // 那条位移是<b>工装的假象</b>，真人做不出来；按下之前先把起点显式送到屏上，
        // 基准才是"光标已经在起点"。
        long seenStart = canvas.MouseMoveCount;
        PostMessage(hwnd, WmMouseMove, IntPtr.Zero, MakeLParam(start.X, start.Y));
        PumpUntil(() => canvas.MouseMoveCount > seenStart, 200);

        PostMessage(hwnd, WmLButtonDown, (IntPtr)MkLButton, MakeLParam(start.X, start.Y));
        Pump(10);
        int half = Math.Max(1, moves / 2);
        int delivered = 0;
        for (int i = 1; i <= moves; i++)
        {
            int step = i <= half ? i : moves - i + 1;        // 先下去再回来，末点等于起点
            long seenOne = canvas.MouseMoveCount;
            PostMessage(hwnd, WmMouseMove, (IntPtr)MkLButton,
                MakeLParam(start.X, start.Y + step * 10 / half));
            // ═══ 每投一条都等到它真被取走（本批现场量出来的那条工装缺陷）═══
            // Windows 会把队列里还没取的 WM_MOUSEMOVE <b>合并</b>，只留最后一条。上一版每 20 条才泵几下，
            // 于是这一百条里真送到的常常就是"退回按下点"那一条——产品自己的 trace 当场读成
            // "设备位移 (0,0)·对齐没变"，那种趟数量到的<b>不是途中帧</b>，是"按下＋松手"两帧。
            // 现在一条一条送，并记"送到几条"当工装自证的门（没有这道门，性能判据可以被一趟
            // 压根没动的鼠标消息喂绿——与需求五那句"不要把偶发环境抢占误判成代码稳定流畅"同一族）。
            PumpUntil(() => canvas.MouseMoveCount > seenOne, 60);
            if (canvas.MouseMoveCount > seenOne) delivered++;
        }
        r.MovesDelivered = delivered;
        long postedDone = Environment.TickCount64;
        // 队列延迟 = 最后一条移动被处理的那一刻 − 全部投完的那一刻
        PumpUntil(() => canvas.MouseMoveCount >= moves, 8000);
        r.QueueDelayMs = Math.Max(0, canvas.LastInputTicks - postedDone);

        // ═══ 拖动阶段的读数先结清，再松手 ═══
        // "拖动期间整块清缓存 = 0"这句说的是<b>预览那几帧</b>（需求十/五的原话）。
        // 松手收尾时 EndPreview 会把修订号推进一次，那一次全清是设计里的（"一次手势只清一遍"），
        // 混进拖动阶段一起数，等于让设计行为替实现背债；单列在下面，看得见也不隐藏。
        r.MouseEvents = canvas.MouseMoveCount;
        r.PreviewFrames = canvas.PreviewFramesForTest - previewBefore;
        r.TileRenders = canvas.TileRendersForTest - tilesBefore;
        r.Clears = canvas.TileFullInvalidationsForTest - clearsBefore;
        r.TileDrops = canvas.TileRegionDropsForTest - dropsBefore;
        r.TileTrims = canvas.TileTrimEvictionsForTest - trimsBefore;
        r.WholeRuns = LongStitch.Core.CanvasComposer.WholeRunCountForTest - wholeBefore;
        r.LayoutBuilds = CanvasLayout.BuildCountForTest - layoutBefore;
        r.Avg = canvas.FrameAvgMs;
        r.P50 = canvas.FramePercentileMs(0.5);
        r.P95 = canvas.FramePercentileMs(0.95);
        r.Max = canvas.MaxFrameMs;
        r.Paints = canvas.PaintCount;
        r.Samples = canvas.FrameSampleCount;
        r.SamplesText = canvas.FrameSamplesForTest;
        r.DragFrames = canvas.FrameSamplesMsForTest();
        r.HudOfFrames = canvas.FrameHudSamplesMsForTest();
        r.SlowestStages = canvas.SlowestStageBreakdown;
        r.SlowestOverlay = canvas.SlowestOverlaySplit;
        r.SlowestHud = canvas.SlowestHudSplit;
        r.CacheHit = canvas.CacheHitRate;
        r.PreviewRecomputes = canvas.PreviewRecomputeCount;
        r.PreviewRecomputeMs = canvas.PreviewRecomputeMs;
        r.HudGeometryRebuilds = canvas.HudGeometryRebuilds;
        r.HudPanelRebuilds = canvas.HudPanelRebuilds;
        r.HudPanelAllocations = canvas.HudPanelAllocations;
        r.HudPanelAllocMs = canvas.HudPanelAllocRebuildMs;
        r.HudPanelReuseMs = canvas.HudPanelReuseRebuildMs;
        r.HudGeometryMs = canvas.HudGeometryRebuildMs - hudBefore.Geo;
        r.HudTextMs = canvas.HudTextBuildMs - hudBefore.Text;
        r.HudOutlineMs = canvas.HudOutlineMs - hudBefore.Outline;
        r.HudBlitMs = canvas.HudBlitMs - hudBefore.Blit;
        r.HudBorderMs = canvas.HudBorderOutlineMs - hudBefore.Border;
        r.HudHitsMs = canvas.HudHitsOutlineMs - hudBefore.Hits;
        r.HudHitRectsDrawn = canvas.HudHitRectsDrawn - hudBefore.HitRects;
        // ═══ 第十五改：面板这一趟到底走的哪条路 ═══
        // 第十四改的拆账里"面板重画"那一路拆成清底＋排字两笔；第十五改新开了行级重画这条路，
        // 它<b>绕过了</b> PaintHudPanelInto。若新路上的两笔计时没接（本批第一版就是这样），
        // 那"复用 7.9ms、其中清底 0.0／排字 0.0"读起来就像钱凭空消失了，而 5 趟的 P95 变好变坏
        // 都没法归因到这次改动上。所以这一行报<b>本趟</b>的两路次数、每次重排几行、
        // 以及行级那个算术闸（行距×行数＋6 是不是等于面板高）在真窗口的 DPI 下成不成立。
        long wiseRun = canvas.HudPanelLineWiseRepaints - hudBefore.Wise;
        long wholeRun = canvas.HudPanelWholeRepaints - hudBefore.Whole;
        long paintedRun = canvas.HudPanelLinesRepainted - hudBefore.LinesPainted;
        long reuseRun = (canvas.HudPanelRebuilds - hudBefore.Rebuilds)
                      - (canvas.HudPanelAllocations - hudBefore.Allocs);
        // 第二十一改：算术闸那一句<b>由产品自己交出</b>（<c>canvas/诊断文本要带出行级那道算术闸的两头读数</c>）。
        // 理由不是"少写一行格式化"，而是<b>这个进程没声明 DPI 感知</b>：工装自己按 ForTest 属性算，
        // 算出来的行距永远是 96 DPI 那一档的那个数，读起来像"真窗口的闸成立"，其实量的是错的那一档。
        // 现在这一句里带的 DPI、行距、行数、面板高全是<b>产品进程</b>在自己 DPI 下量的，
        // 而真窗口这一趟本来就跑在产品进程里，所以这句在 150% 屏上也就不需要另开一条测量通路了。
        r.PanelRoads = $"行级 {wiseRun} 次（共重排 {paintedRun} 行，每次 {(wiseRun == 0 ? 0 : (double)paintedRun / wiseRun):F1} 行）、" +
                       $"整块 {wholeRun} 次、复用 {reuseRun} 次／分配 {canvas.HudPanelAllocations - hudBefore.Allocs} 次；" +
                       canvas.DescribeHudPanelGate() + "；" +
                       $"本趟清底 {StitchCanvasControl.HudPanelClearMs - hudBefore.Clear:F1}ms／排字 {StitchCanvasControl.HudPanelDrawTextMs - hudBefore.Draw:F1}ms";
        r.OverlayStrokeMs = canvas.OverlayStrokeMs;
        r.OverlayFillMs = canvas.OverlayFillMs;
        r.Hud = canvas.HudSplitStats;
        r.GcGen0 = GC.CollectionCount(0) - gen0Before;
        r.GcGen2 = GC.CollectionCount(2) - gen2Before;
        // 结算窗口与上面 GC 两行<b>同一批帧</b>（都在拖动段末尾、松手之前），
        // 这样"gen0 几次 / 每帧多少字节"才是同一批帧的两个属性，能对着读。
        r.AllocBytes = canvas.FrameAllocBytesTotalForTest - allocBefore;
        r.AllocFrames = canvas.FrameAllocSamplesForTest - allocFramesBefore;

        double maxBeforeRelease = canvas.MaxFrameMs;
        PostMessage(hwnd, WmLButtonUp, IntPtr.Zero, MakeLParam(start.X, start.Y));
        Pump(40);
        r.ClearsAfterRelease = canvas.TileFullInvalidationsForTest - clearsBefore - r.Clears;
        // 松手那一帧是"设计里的那一次全量重算"（EndPreview 推进修订号）——它与拖动帧分开记账，
        // 既不混进"拖动期不许整块清"那条判据，也不藏起来。
        r.ReleaseFrameMs = Math.Max(0, canvas.MaxFrameMs - maxBeforeRelease);

        // ── 活着的证明：还能不能再收一条消息 ──
        long beforeAlive = canvas.MouseMoveCount;
        long alivePosted = Environment.TickCount64;
        PostMessage(hwnd, WmMouseMove, IntPtr.Zero, MakeLParam(start.X, start.Y));
        PumpUntil(() => canvas.MouseMoveCount > beforeAlive, 3000);
        r.AliveLatencyMs = Environment.TickCount64 - alivePosted;

        // 末点读数：<b>按同一个 id 现查</b>——松手收尾会走 Project.Mutate 换文档实例，
        // 揣着按下时那个旧引用读出来的是"上一世"的那条缝（撤销档里同一件事写过一次注释）。
        r.ViewportAfter = canvas.Viewport.TopLeft;
        // 产品自己记的那笔手势设备位移（按下点为原点）——真窗口里这一笔如果读成 (0,333) 之类，
        // 说明"跳"来自消息通路送进来的某个远点，而不是实现把来回拖出了净改动（进程内同形状量到的是 0）。
        r.DragDeviceDelta = canvas.LastSeamEditTraceForTest is { } t
            ? $"设备位移 ({t.DeviceDelta.X},{t.DeviceDelta.Y})·对齐变了 {t.AlignmentChanged}"
            : "没有 trace";
        // ═══ 第二十改：本趟"拖没拖起来"改读产品自己记的手势阶段 ═══
        // 上一版拿末点的<b>净参数改动</b>当这件事的证据，而这批的形状是<b>来回拖、末点等于起点</b>
        // （故意这么设计，免得 5 趟把文档一路拖走），产品对"拖回原位"按设计<b>不留历史</b>
        // （<c>align/对齐拖动整趟只提交一条撤销，拖回原位不留空记录</c> 与
        // <c>align/来回拖同一趟（净位移 0）在小缩放档与固定后一段下也一字不改</c> 钉的就是这件事），
        // 于是净改动天生是 0——拿它当门，判的是"设计对不对"，不是"量没量到"。
        // 现在读的是产品自己的手势阶段：trace 挂在本趟命中的那条缝上、且已越过拖动阈值（不是 Pending）。
        // <b>红-capable 的对照在电池里</b>：按下点落在带外 ⇒ 没有 trace（<c>canvas/选着拼缝点空白处拖动只平移画布，参数一字不改</c>）。
        r.TraceStage = canvas.LastSeamEditTraceForTest is { } tr
            ? $"{tr.SeamId}·{tr.DragMode}" : "没有 trace";
        r.GestureLanded = canvas.LastSeamEditTraceForTest is { } tt
            && tt.SeamId == trackedId && tt.DragMode != "PendingSeamClick";
        Seam? seamAfter = FindSeamById(project, trackedId);
        if (seamAfter is not null)
        {
            r.OverlapAfter = seamAfter.OverlapLength;
            r.CutAfter = seamAfter.CutPosition;
        }

        r.Exports = LongStitch.Core.Export.ExportEngine.RunCountForTest - exportsBefore;
        r.WallMs = Environment.TickCount64 - wallBefore;
        // 进程 CPU 时间是<b>所有线程</b>合计（后台解码、终结器、GC 都算进去），所以它会大于墙钟——
        // 真正说明"UI 线程画了多久"的是上面那批帧样本之和，两个数一起看才分得清
        // "这里在算"与"这趟里有别的工作在并行/被系统切走"（需求五要把偶发抢占与真慢分开记）。
        r.CpuMs = (proc.TotalProcessorTime - cpuBefore).TotalMilliseconds;
        // 调度增量取在<b>最后</b>（GC 增量已经结清了）：这一笔读数自己的分配就不会冒充"这一趟的回收次数"。
        Scheduling schedAfter = Scheduling.Read(canvas);
        r.SchedThreads = schedAfter.Ok ? schedAfter.Threads : -1;
        // 增量交给 Scheduling.Delta：线程条数掉了就不给增量（−1 ＝ 这一笔量不到，不等于"这趟没花 CPU"）。
        if (Scheduling.Delta(schedBefore, schedAfter, out double busyMs, out double sumMs, out _))
        {
            r.ThreadCpuMs = busyMs;
            r.SumCpuMs = sumMs;
        }
        return r;
    }

    /// <summary>逐趟打印读数——需求五那十一项一项不落，另加 HUD 分档与调度信息。</summary>
    /// <summary>
    /// 按 id 在<b>当下这份</b>文档里找那条缝。撤销与每次提交都会换掉 <c>ProjectDocument</c> 里的实例，
    /// 揣着旧引用读出来的是"上一世"的那条缝（撤销档里为了同一件事已经写过一次注释）。
    /// 找不到就返回 null——调用方（#95 那两笔读数）于是报 0→0，这是"这一趟没动到它"的样子，
    /// 不是"读数为 0 所以没发生"：起点命中那句话会一起印出来，两种解释分得开。
    /// </summary>
    private static Seam? FindSeamById(Project project, string? id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        foreach (Seam s in project.Doc.Seams)
        {
            if (s.Id == id) return s;
        }
        return null;
    }

    private static void PrintRun(RunReadings r)
    {
        Console.WriteLine();
        Console.WriteLine($"─── 第 {r.Run} 趟读数（全部来自控件自己在 OnPaint 里记的那一份）───");
        Console.WriteLine($"本趟动了谁      = 命中 {r.HitAtPress} · 重叠 {r.OverlapBefore}→{r.OverlapAfter} · " +
                          $"蓝线 {r.CutBefore}→{r.CutAfter} · 视口 {r.ViewportBefore}→{r.ViewportAfter} · " +
                          $"阶段 {r.TraceStage} · {r.DragDeviceDelta}");
        Console.WriteLine($"输入事件数      = {r.MouseEvents}（其中拖动 {r.Moves} 次，逐条送达 {r.MovesDelivered} 条）" +
                          $"· 队列延迟 {r.QueueDelayMs:F0}ms");
        Console.WriteLine($"预览帧数        = {r.PreviewFrames} · Paint 次数 = {r.Paints} · 帧样本 = {r.Samples}");
        Console.WriteLine($"帧耗时 平均/P50/P95/最大 = {r.Avg:F1} / {r.P50:F1} / {r.P95:F1} / {r.Max:F1} ms");
        Console.WriteLine($"逐帧样本（毫秒，*=这一帧里发生过回收；打印时已过松手那一帧，所以可能比 {r.Samples} 个多）= {r.SamplesText}");
        Console.WriteLine($"RenderWhole     = {r.WholeRuns} 次 · 完整 CanvasLayout.Build = {r.LayoutBuilds} 次" +
                          $"（拖动预览每帧一次，与下面「单帧预览重算」同数）· RenderTile = {r.TileRenders}");
        Console.WriteLine($"整块清缓存      = 拖动阶段 {r.Clears} 次；松手收尾 {r.ClearsAfterRelease} 次" +
                          "（收尾那一次是 EndPreview 推进修订号，设计里「一次手势只清一遍」）");
        Console.WriteLine($"单帧预览重算    = {r.PreviewRecomputes} 次 共 {r.PreviewRecomputeMs:F1}ms，" +
                          $"均 {r.PreviewRecomputeMs / Math.Max(1, r.PreviewRecomputes):F2}ms（这段在鼠标消息里、不在 OnPaint 里）");
        Console.WriteLine($"GC 次数         = gen0 {r.GcGen0}、gen2 {r.GcGen2} · 导出 {r.Exports} 次 · 命中率 {r.CacheHit:P0}");
        // 每帧托管堆分配：取数窗口是<b>产品自己的 OnPaint</b>（`FrameAllocBytesTotalForTest` 在那里面结算），
        // 不是工装算的账——工装若在帧外取数，会把 `Graphics.FromImage`／`new PaintEventArgs` 这些自己的开销记到产品头上。
        // 只数托管堆：瓦片位图那 1MB 在非托管堆上，这把尺看不见，归贴图预算那两枚门管。
        Console.WriteLine($"每帧托管堆分配  = {r.AllocBytes / Math.Max(1, r.AllocFrames):F0}B／帧" +
                          $"（本趟 {r.AllocFrames} 帧共 {r.AllocBytes / 1048576.0:F2} MiB，与上面 GC 那行同批帧）");
        // ═══ 命中率低到底是谁造成的：这一行把两种相反的形状分开 ═══
        // 上面那个"命中率"只说结果。需求五那道门量的是"整块清 = 0"，可 0 次整块清并不等于"缓存留住了"：
        // 对齐手势每帧把<b>带顶往下</b>整段局部作废（`DropRegion`），块数可以等于视口里全部块数——
        // 读起来与"缓存太小被 LRU 挤光"一模一样，改法却完全相反（前者要收窄作废范围／换键，后者要加预算）。
        Console.WriteLine($"瓦片留住的账    = 局部丢 {r.TileDrops} 块、LRU 挤 {r.TileTrims} 块、真渲染 {r.TileRenders} 块" +
                          $"（{r.Paints} 帧：平均每帧丢 {(r.Paints == 0 ? 0 : (double)r.TileDrops / r.Paints):F1} 块" +
                          $"、每帧重算 {(r.Paints == 0 ? 0 : (double)r.TileRenders / r.Paints):F1} 块）");
        Console.WriteLine($"墙钟 {r.WallMs:F0}ms / 进程 CPU（所有线程合计）{r.CpuMs:F0}ms / " +
                          $"UI 线程花在画上的 {r.DragFrames.Sum():F0}ms（拖动帧合计）");
        // 需求五"记录 GC 与<b>进程调度</b>信息"——这一行是实量的调度，不是"墙钟减 CPU"的推论。
        // 数字全部从产品自己的诊断出口那一行「线程与调度」读回来（工装不另造一套计数器）。
        Console.WriteLine(r.SumCpuMs >= 0
            ? $"线程与调度      = {r.SchedThreads} 条线程 · 本趟全部线程 CPU 求和增加 {r.SumCpuMs:F0}ms、"
              + $"最忙那条单条增加 {r.ThreadCpuMs:F0}ms · 本趟墙钟 {r.WallMs:F0}ms"
              + " ⇒ 墙钟比「最忙那条」大出去的那截是「等」（等消息、等锁、等被调度回来）"
            : "线程与调度      = 没读到（这台机器拿不到线程快照）——这一笔按未量记账，不写成 0");
        Console.WriteLine("                    两笔各管各的：求和那笔跟着任何一条线程烧 CPU 而动（反证格钉的是它），"
                          + "「最忙那条」才是拖动归因——它在两次取读数之间可能换成另一条线程，所以只作读数、不进任何阈值");
        Console.WriteLine($"最慢那帧叠层拆账= {r.SlowestOverlay}");
        // 第十四改那一行：它答的是"开读数层那档最慢的一帧里，读数层自己的 33.5ms 是哪种钱"。
        // 关着读数层时产品交回空串——那时这一行写"量不到"，不写一串 0（写 0 会被读成"量到了 0"）。
        Console.WriteLine($"最慢那帧读数层拆账= {(r.SlowestHud.Length == 0 ? "量不到（这一趟读数层是关着的）" : r.SlowestHud)}");
        Console.WriteLine($"最慢那帧分阶段  = {r.SlowestStages}（需求六：卡顿发生在哪个阶段；" +
                          "毫秒那六笔与字节那四段各有各的口径——任何一套加起来明显小于整帧那一笔，" +
                          "就说明还有没接表的段，不许拿“这段代码慢”当结论）");
        if (r.Hud.Frames > 0)
        {
            Console.WriteLine($"HUD 单独耗时    = 均 {r.Hud.HudAvg:F1} / P50 {r.Hud.HudP50:F1} / P95 {r.Hud.HudP95:F1} / " +
                              $"最慢 {r.Hud.HudMax:F1} ms（样本 {r.Hud.Frames} 帧）");
            Console.WriteLine($"普通画布绘制    = 均 {r.Hud.CanvasAvg:F1} / P50 {r.Hud.CanvasP50:F1} / P95 {r.Hud.CanvasP95:F1} / " +
                              $"最慢 {r.Hud.CanvasMax:F1} ms（同一批帧减去 HUD 那一段）");
            Console.WriteLine($"HUD 重算次数    = 几何换算 {r.HudGeometryRebuilds} 次、文字面板 {r.HudPanelRebuilds} 次" +
                              $"（{r.Paints} 帧：面板上限 10Hz，静止时文本没变就不该重建）");
            // 面板重建拆成两条路各量一次（进程累计）：需求三要说清"开读数层多花的钱"花在哪，
            // 光报一个"面板重建 N 次"分不清"重画字贵"还是"每次都要新分配一张半 MB 的缓冲区贵"。
            Console.WriteLine($"面板重建两条路  = 分配新缓冲区 {r.HudPanelAllocations} 次 共 {r.HudPanelAllocMs:F1}ms、" +
                              $"复用同一张 {r.HudPanelRebuilds - r.HudPanelAllocations} 次 共 {r.HudPanelReuseMs:F1}ms" +
                              "（两组的毫秒除以各自次数就是每次的代价）");
            // 第十五改那一行：上面那行只说"复用了几次、共多少钱"，说不出<b>复用那一次干了什么</b>。
            // 行级重画开了一条新路（只重排变了的那几行往下），它绕不绕得开整块重排，
            // 全看那个算术闸在<b>真窗口的 DPI</b> 下成不成立——96 DPI 的电池里成立不代表 150% 成立。
            // 没有这一行，本批那 5 趟的"每次复用 4.1~5.1ms 与整块那批一字不差"根本分不清是
            // "行级省不到钱"还是"行级压根没走"（这一族叫"计数器没接上"，本项目栽过很多次）。
            Console.WriteLine($"面板两条重画路  = {r.PanelRoads}");
            // 读数层那一段整块的拆账：几项加起来若明显小于「HUD 单独耗时」的合计，
            // 剩下的钱就在计时器读不到的地方（GDI+ 延迟提交、或者这一帧被系统切走），不能硬派给某一项。
            Console.WriteLine($"读数层拆账(本趟) = 几何换算 {r.HudGeometryMs:F1}ms、拼文本 {r.HudTextMs:F1}ms、" +
                              $"描边 {r.HudOutlineMs:F1}ms（边界 {r.HudBorderMs:F1}＋命中区 {r.HudHitsMs:F1}" +
                              $"＋残差 {r.HudOutlineMs - r.HudBorderMs - r.HudHitsMs:F1}，" +
                              $"命中区真画了 {r.HudHitRectsDrawn} 笔）、贴图 {r.HudBlitMs:F1}ms" +
                              $"（四项都是这一趟的增量，不再报进程累计；本趟 HUD 整段合计 {r.HudOfFrames.Sum():F1}ms，" +
                              "残差＝夹取矩形与取时钟那两次之间的空隙，它大就是拆得不够）");
        }
        else
        {
            Console.WriteLine("HUD 单独耗时    = 没量到（这一趟读数层是关着的——需求三要求 HUD 关闭/开启分开报，就得各跑一趟）");
        }
        // 重影对比层是产品默认态，**关着读数层也要画**，所以这两笔拆账在两种档位都得打
        // （它落在画布那一段里，不在读数层的计时段内）。
        Console.WriteLine($"对比层拆账      = 两张图的设备矩形描边 {r.OverlayStrokeMs:F1}ms、" +
                          $"重叠带填充＋描边 {r.OverlayFillMs:F1}ms（进程累计；整层的钱另看 --no-compare 差分）");
    }

    /// <summary>
    /// 一条阈值按"趟趟都得过"判：任何一趟超了就红，并报出<b>最差</b>那一趟的数。
    /// 需求五明确不许只报最好的一次，所以这里连"平均"都不给，只给最大值。
    /// </summary>
    private static int CheckAllRuns(ref int failed, string name, IReadOnlyList<RunReadings> runs,
        Func<RunReadings, double> value, double limit, string unit)
    {
        if (runs.Count == 0) return Check(ref failed, name, false, "一趟都没跑成");
        double worst = double.MinValue;
        int worstRun = 0, over = 0;
        foreach (RunReadings r in runs)
        {
            double v = value(r);
            if (v > limit) over++;
            if (v > worst) { worst = v; worstRun = r.Run; }
        }
        return Check(ref failed, name, over == 0,
            $"{runs.Count} 趟里 {runs.Count - over} 趟达标；最差第 {worstRun} 趟 {worst:F1}{unit}（限值 {limit:F1}）");
    }

    /// <summary>
    /// 面板那块框装不装得下它自己那段读数（宽与高两头都要够）。
    /// 【第三十六改】真窗口那道门与电池那两格<b>共用这一个对照</b>——
    /// 两处各写一遍就会有两份规则（同一件事两份写法，早晚只有一份被改）。
    /// 反证 <c>guard/真窗口那道面板闸门要能抓住算窄的框</c> 钉它不是恒真。
    /// </summary>
    internal static bool PanelFitsReadouts(Size box, Size need) =>
        box.Width >= need.Width && box.Height >= need.Height;

    /// <summary>一轮混合会话的读数（每轮一行，逐轮最严判，不挑最好那轮）。</summary>
    private sealed class CycleReadings
    {
        public int Cycle;
        public int Samples;
        public double P50, P95, Max;
        public long PreviewFrames, Paints, MouseEvents, ClearsGesture, WholeGesture, LayoutBuildsAll;
        public long PackageMs, WallMs;
        public int GcGen0;
        public double CacheHit;
        // 第三十二改：这一趟里产品自己按帧结算的托管堆分配（字节与帧数分开交，按帧归一才是可比的）。
        public long AllocBytes, AllocFrames;
        public double AliveMs = -1;
        public bool UndoClicked;
        public int SchedThreads = -1;
        public double SchedBusyMs = -1, SchedSumMs = -1;
        public string SchedNote = "";
        public bool CutPhaseSkipped;
    }

    /// <summary>
    /// 【混合长会话·需求三的"长期"那一档】以前所有真窗口读数都是<b>同一个手势反复跑</b>
    /// （拖动连打 20 趟、滚轮／缩放各 5 趟）——那是同一段路来回走，不是人连着用几十分钟里
    /// 换缝、切档、撤销、存盘、重开这一串。两种形状不一样：后者会<b>一遍遍触发设计内的重算</b>
    /// （换缝要重算预览、切档要重铺一屏瓦片、开包要把 17 张重新解一遍），
    /// 这些重算把瓦片缓存与 LRU 打成什么样，本轮之前没人看过。
    ///
    /// 这一档在同一进程、同一真窗口里把这些动作<b>串成一轮</b>再连打 N 轮，每轮单独结清：
    /// 换缝 → 切四档缩放（35／100／25／200）→ 带区拖动（对齐）→ 蓝线拖动（改 CutY）→
    /// 普通区拖动（只挪视口）→ 撤销（走产品自己的菜单项）→ 存包 → 开包。
    ///
    /// 三条口径纪律，一条都不放宽：
    /// · "手势阶段不调用 RenderWhole""手势阶段整块清 ≤ 三段松手"——切档与存／开包的重算属<b>设计内</b>，
    ///   单列计时，不混进手势那笔（混进去就会把设计行为算成缺陷，或反过来把缺陷算成设计）；
    /// · 阈值<b>逐轮最严判</b>：任何一轮 P95 超 33ms、任何一帧超 50ms、任何一轮撤销后画不出下一帧，都红；
    /// · 额外报"前半 vs 后半"——长期退化（缓存越用越脏、订阅越攒越多）只有这一条抓得到。
    /// </summary>
    private static int MixedCycles(MainForm form, StitchCanvasControl canvas, int cycles, bool hud)
    {
        int failed = 0, skipped = 0;
        string pkg = Path.Combine(Path.GetTempPath(), "ls-mixed-" + Guid.NewGuid().ToString("N") + ".lstickx");
        var rows = new List<CycleReadings>();

        Console.WriteLine();
        Console.WriteLine($"─── 混合长会话（需求三·长期档）：{cycles} 轮 ×（换缝·切档·三种拖动·撤销·存包·开包）" +
                          $" · 读数层{(hud ? "开" : "关")} ───");
        long sessionStart = Environment.TickCount64;
        try
        {
            for (int c = 1; c <= cycles; c++)
            {
                Project project = form.ProjectForTest;
                List<Seam> seams = project.Doc.Seams.ToList();
                if (seams.Count < 2)
                {
                    Console.WriteLine($"第 {c} 轮：拼缝不足两条（开包后没把项目读回来？）——这一轮按未量记账，不替实现圆场");
                    skipped++;
                    continue;
                }

                // ① 换缝：每轮换一条，覆盖「选中态变了要重算预览」这一族
                Seam seam = seams[c % seams.Count];
                canvas.SelectSeam(seam.Id);
                Pump(8);

                // ② 切档：四档轮着切，每一档都要重铺一屏瓦片（设计内重算，单列在下面）
                foreach (int z in new[] { 35, 100, 25, 200 })
                {
                    canvas.Viewport.SetZoom(z / 100.0);
                    canvas.Invalidate();
                    Pump(10);
                }

                CanvasLayout layout = project.Layout;
                if (layout.DescribeSeam(seam) is not SeamGeometry geo)
                {
                    Console.WriteLine($"第 {c} 轮：这条缝算不出几何——这一轮按未量记账");
                    skipped++;
                    continue;
                }

                // 把这条缝的带滚到屏上正中：手势必须发生在真视口里，否则「画了多少、画多久」都不成立
                int bandCenter = geo.OverlapRect.Y + geo.OverlapRect.Height / 2;
                int rowsVisible = Math.Max(1, (int)(canvas.Viewport.DeviceSize.Height / canvas.Viewport.Zoom));
                canvas.Viewport.MoveTopLeftTo(new IntPoint(0, Math.Max(0, bandCenter - rowsVisible / 2)));
                canvas.Invalidate();
                Pump(20);

                var row = new CycleReadings { Cycle = c };
                IntPtr hwnd = canvas.Handle;
                // ResetFrameStats 连 PaintCount 与 MouseMoveCount 一起清零（它清的是"这一窗"的所有计数），
                // 所以**必须先清再记基线**——反过来会把基线记在旧窗口上，作差作 out 负数
                // （本工装第一版就是这么红的：逐轮 Paint 报 -16、鼠标事件报 -7）。
                canvas.ResetFrameStats();
                long clearsBefore = canvas.TileFullInvalidationsForTest;
                long wholeBefore = LongStitch.Core.CanvasComposer.WholeRunCountForTest;
                long paintsBefore = canvas.PaintCount;
                long previewBefore = canvas.PreviewFramesForTest;
                long movesBefore = canvas.MouseMoveCount;
                int gen0Before = GC.CollectionCount(0);
                long allocBefore = canvas.FrameAllocBytesTotalForTest;
                long allocFramesBefore = canvas.FrameAllocSamplesForTest;
                Scheduling schedBefore = Scheduling.Read(canvas);
                long wallBefore = Environment.TickCount64;

                Rectangle band = canvas.Viewport.CanvasToDevice(geo.OverlapRect);
                if (band.Width < 4 || band.Height < 4)
                {
                    Console.WriteLine($"第 {c} 轮：带在屏上没有面积（这一档缩放太小）——这一轮按未量记账");
                    skipped++;
                    continue;
                }
                var bandPoint = new Point(band.X + band.Width / 2, band.Y + band.Height / 2);

                // ③ 带区拖动（对齐）
                DragOnceInCycle(hwnd, bandPoint, 8);

                // ④ 蓝线拖动（只改 CutY）：蓝线在屏上时才拖，量不到就明写「这一轮跳过」
                int cutDeviceY = canvas.Viewport.CanvasToDevice(new IntRect(0, geo.CutYCanvas, 1, 1)).Y;
                if (cutDeviceY > 4 && cutDeviceY < canvas.ClientSize.Height - 4)
                    DragOnceInCycle(hwnd, new Point(bandPoint.X, cutDeviceY), 6);
                else
                    row.CutPhaseSkipped = true;

                // ⑤ 普通区拖动（只挪视口）：挑一条离带够远的横线
                int plainY = Math.Max(24, bandPoint.Y - Math.Min(240, bandPoint.Y - 24));
                DragOnceInCycle(hwnd, new Point(bandPoint.X, plainY), 8);

                row.ClearsGesture = canvas.TileFullInvalidationsForTest - clearsBefore;
                row.WholeGesture = LongStitch.Core.CanvasComposer.WholeRunCountForTest - wholeBefore;
                row.P50 = canvas.FramePercentileMs(0.5);
                row.P95 = canvas.FramePercentileMs(0.95);
                row.Max = canvas.MaxFrameMs;
                row.Samples = canvas.FrameSampleCount;
                row.Paints = canvas.PaintCount - paintsBefore;
                row.PreviewFrames = canvas.PreviewFramesForTest - previewBefore;
                row.MouseEvents = canvas.MouseMoveCount - movesBefore;
                row.CacheHit = canvas.CacheHitRate;
                row.GcGen0 = GC.CollectionCount(0) - gen0Before;
                row.AllocBytes = canvas.FrameAllocBytesTotalForTest - allocBefore;
                row.AllocFrames = canvas.FrameAllocSamplesForTest - allocFramesBefore;

                // ⑥ 撤销：点产品自己的「编辑→撤销」菜单项（与 Ctrl+Z 落到同一个处理器）
                row.UndoClicked = form.ClickUndoMenuItemForTest();
                Pump(20);
                // 活着的证明：撤销之后再投一条移动，看它还收不收（需求三那句「长期」里最坏的形状是界面冻住）
                long aliveBefore = canvas.MouseMoveCount;
                long alivePosted = Environment.TickCount64;
                PostMessage(hwnd, WmMouseMove, IntPtr.Zero, MakeLParam(bandPoint.X, bandPoint.Y));
                PumpUntil(() => canvas.MouseMoveCount > aliveBefore, 3000);
                row.AliveMs = Environment.TickCount64 - alivePosted;

                // ⑦⑧ 存包 + 开包（重算与解码都发生在这里，单列计时，不混进手势那笔）
                long pkgStart = Environment.TickCount64;
                form.SavePackageForTest(pkg);
                form.OpenPackageForTest(pkg);
                Pump(60);
                row.PackageMs = Environment.TickCount64 - pkgStart;
                row.LayoutBuildsAll = CanvasLayout.BuildCountForTest;
                row.WallMs = Environment.TickCount64 - wallBefore;
                Scheduling schedAfter = Scheduling.Read(canvas);
                row.SchedThreads = schedAfter.Ok ? schedAfter.Threads : -1;
                // 线程条数在两轮之间会掉（后台解码干完活就退），那时「全部线程求和」跟着掉，
                // 作差会负（第一批就在第 10 轮读到 −1594ms）——这种轮次报"没读到"，不报负数。
                if (Scheduling.Delta(schedBefore, schedAfter, out double busyMs, out double sumMs, out string schedNote))
                {
                    row.SchedBusyMs = busyMs;
                    row.SchedSumMs = sumMs;
                }
                else row.SchedNote = schedNote;

                rows.Add(row);
                Console.WriteLine($"第 {c,2} 轮 帧 {row.Samples,3} 个 P50 {row.P50,5:F1} P95 {row.P95,5:F1} 最慢 {row.Max,6:F1}ms" +
                    $" · 手势整块清 {row.ClearsGesture}（三段松手各一次属设计）· 手势 RenderWhole {row.WholeGesture}" +
                    $" · 鼠标事件 {row.MouseEvents} · 预览帧 {row.PreviewFrames} · Paint {row.Paints}" +
                    $" · gen0 {row.GcGen0} · 每帧分配 {(row.AllocBytes / Math.Max(1, row.AllocFrames)):F0}B／{row.AllocFrames}帧 · 命中率 {row.CacheHit:F2}" +
                    $" · 撤销 {(row.UndoClicked ? "触发" : "不可点")} · 撤销后补一帧 {row.AliveMs:F0}ms" +
                    $" · 存+开包 {row.PackageMs}ms · 本轮墙钟 {row.WallMs}ms" +
                    $" · 调度 {row.SchedThreads} 条／本线程 {row.SchedBusyMs:F0}ms／全线程 {row.SchedSumMs:F0}ms" +
                    (row.CutPhaseSkipped ? " · 蓝线那一段这轮不在屏上，跳过（没量到就明写）" : "") +
                    (row.SchedNote == "" ? "" : " · 调度增量没读到：" + row.SchedNote));
            }
        }
        finally
        {
            try { if (File.Exists(pkg)) File.Delete(pkg); }
            catch (IOException) { /* 临时包删不掉不影响读数，但也不许假装删掉了 */ }
        }

        Console.WriteLine($"会话墙钟 {(Environment.TickCount64 - sessionStart) / 1000.0:F0}s" +
                          $"（{rows.Count} 轮跑成、{skipped} 轮未量）");
        if (rows.Count == 0)
        {
            Console.WriteLine("结论：混合长会话一轮都没跑成——长期档按未量记账，不许写成「跑过了」。");
            return failed + 1;
        }

        var sorted = rows.Select(x => x.P95).ToList();
        sorted.Sort();
        var sortedMax = rows.Select(x => x.Max).ToList();
        sortedMax.Sort();
        Console.WriteLine($"逐轮 P95 = {string.Join("  ", rows.Select(x => $"{x.P95:F1}"))}");
        Console.WriteLine($"逐轮 最慢 = {string.Join("  ", rows.Select(x => $"{x.Max:F1}"))}");
        Console.WriteLine($"逐轮 手势整块清 = {string.Join("  ", rows.Select(x => $"{x.ClearsGesture}"))}" +
                          $" · 逐轮手势 RenderWhole = {string.Join("  ", rows.Select(x => $"{x.WholeGesture}"))}");
        Console.WriteLine($"逐轮 存+开包 = {string.Join("  ", rows.Select(x => $"{x.PackageMs}ms"))}" +
                          $" · 逐轮命中率 = {string.Join("  ", rows.Select(x => $"{x.CacheHit:F2}"))}");
        Console.WriteLine($"全程 P95：中位 {Percentile(sorted, 0.5):F1}ms、最差 {sorted[^1]:F1}ms；" +
                          $"最慢帧：中位 {Percentile(sortedMax, 0.5):F1}ms、最差 {sortedMax[^1]:F1}ms");

        int half = rows.Count / 2;
        if (half >= 1)
        {
            var first = rows.Take(half).Select(x => x.P95).ToList();
            var tail = rows.Skip(half).Select(x => x.P95).ToList();
            first.Sort();
            tail.Sort();
            Console.WriteLine($"前半 {first.Count} 轮 P95 中位 {Percentile(first, 0.5):F1}ms ｜ " +
                              $"后半 {tail.Count} 轮 P95 中位 {Percentile(tail, 0.5):F1}ms" +
                              "（同机波动能让后半偶然更快，所以这里只判阈值、不判「不许比前半慢」——" +
                              "把它写成退化就红在环境上，同一形状见 25% 那两批）");
        }

        failed += Check(ref failed, "混合会话每轮都真画过东西（帧样本与 Paint 都 >0，否则下面全是空判据）",
            rows.All(x => x.Samples > 0 && x.Paints > 0),
            $"每轮帧样本 {rows.Min(x => x.Samples)}–{rows.Max(x => x.Samples)} 个、Paint {rows.Min(x => x.Paints)}–{rows.Max(x => x.Paints)} 次");
        failed += Check(ref failed, "混合会话每轮手势阶段不调用 RenderWhole（切档与存／开包的重算不计在这里）",
            rows.All(x => x.WholeGesture == 0),
            $"逐轮手势段 = {string.Join("/", rows.Select(x => x.WholeGesture))}");
        failed += Check(ref failed, "混合会话每轮手势阶段整块清缓存 ≤ 3（每段手势松手一次属设计）",
            rows.All(x => x.ClearsGesture <= 3),
            $"逐轮 = {string.Join("/", rows.Select(x => x.ClearsGesture))} 次，闸放在 3（蓝线那一段跳过的轮次只会更少）");
        failed += Check(ref failed, "混合会话逐轮 P95 ≤ 33ms（最严判，不挑最好那轮）",
            rows.All(x => x.P95 <= 33.0),
            $"最差一轮 P95={sorted[^1]:F1}ms（{rows.Count} 轮各报，见上面逐轮行）");
        failed += Check(ref failed, "混合会话逐轮最慢帧 ≤ 50ms",
            rows.All(x => x.Max <= 50.0),
            $"最差一轮最慢帧={sortedMax[^1]:F1}ms");
        failed += Check(ref failed, "混合会话每轮撤销后 UI 仍能收下一条消息（≤ 200ms）",
            rows.All(x => x.AliveMs <= 200.0),
            $"最差一轮 {rows.Max(x => x.AliveMs):F0}ms");
        failed += Check(ref failed, "混合会话每轮都撤销得动（历史空了就是通路断了）",
            rows.All(x => x.UndoClicked),
            $"{rows.Count(x => x.UndoClicked)}/{rows.Count} 轮点得动；点不动的那些轮上面逐轮行写了「不可点」");
        failed += Check(ref failed, "开包后项目还读得回（每轮 17 张仍在）",
            rows.Count > 0,
            $"最后一轮之后拼缝数 {form.ProjectForTest.Doc.Seams.Count} 条（少于两条早按未量记走了）");
        return failed;
    }

    /// <summary>投一段「按下—移动—松手」的真窗口鼠标通路（与用户的手落到的是同一条路）。</summary>
    private static void DragOnceInCycle(IntPtr hwnd, Point at, int steps)
    {
        PostMessage(hwnd, WmLButtonDown, (IntPtr)MkLButton, MakeLParam(at.X, at.Y));
        Pump(6);
        for (int i = 1; i <= steps; i++)
        {
            int delta = i <= steps / 2 ? i * 6 : (steps - i) * 6;
            PostMessage(hwnd, WmMouseMove, (IntPtr)MkLButton, MakeLParam(at.X, at.Y + delta));
            if (i % 4 == 0) Pump(2);
        }
        Pump(6);
        PostMessage(hwnd, WmLButtonUp, IntPtr.Zero, MakeLParam(at.X, at.Y));
        Pump(24);
    }

    private static int Measure(string dir, int moves, bool hud, int runs, bool compare,
        int wheel, int zoom, int atZoom, int mixedCycles)
    {
        Console.WriteLine("═══ 真实 GUI 性能测量（需求五）═══");
        (string[] files, bool realSamples) = ResolveImages(dir);
        if (files.Length == 0)
        {
            Console.WriteLine("NOT-RUN：一张图都拿不到，测量无法开始。");
            return 2;
        }
        Console.WriteLine($"素材：{files.Length} 张 · {(realSamples ? $"真截图（{dir}/）" : "合成 1179×2556（test/ 不在本机）")}");

        Application.EnableVisualStyles();
        var form = new MainForm();
        try
        {
            form.Show();
            PumpUntil(() => form.Visible && form.CanvasForTest.IsHandleCreated, 5000);
            if (!form.CanvasForTest.IsHandleCreated)
            {
                Console.WriteLine("NOT-RUN：窗口句柄没建起来（这台机器没有可用的桌面会话）——真实 GUI 手感/帧率仍未验证。");
                return 2;
            }

            // 第六十改：导入闸要求先有模式；夹具直接写字段（不走 Pick，避免多一笔 Mutate）。
            form.ProjectForTest.Doc.Mode = StitchMode.LongScroll;
            form.ImportPaths(files);
            Project project = form.ProjectForTest;
            project.Mutate("切到长截图并设定重叠", doc =>
            {
                doc.Mode = StitchMode.LongScroll;
                foreach (Seam seam in doc.Seams) seam.SetOverlap(120);
            });
            Seam target = project.Doc.Seams[^2];            // 倒数第二条缝：它下面还有一段，落位最像真实使用
            form.CanvasForTest.SelectSeam(target.Id);
            CanvasLayout layout = project.Layout;
            if (layout.DescribeSeam(target) is not SeamGeometry geo)
            {
                Console.WriteLine("NOT-RUN：这条缝算不出几何（布局空），测量无法继续。");
                return 2;
            }

            StitchCanvasControl canvas = form.CanvasForTest;
            canvas.Viewport.SetZoom(atZoom / 100.0);
            // 把带滚到屏上正中：拖动必须在真视口里发生，否则"画了多少""画多久"都不成立
            int bandCenter = geo.OverlapRect.Y + geo.OverlapRect.Height / 2;
            int visibleCanvasRows = Math.Max(1, (int)(canvas.Viewport.DeviceSize.Height / canvas.Viewport.Zoom));
            canvas.Viewport.MoveTopLeftTo(new IntPoint(0, Math.Max(0, bandCenter - visibleCanvasRows / 2)));
            canvas.Invalidate();
            Pump(30);

            Rectangle bandDevice = canvas.Viewport.CanvasToDevice(geo.OverlapRect);
            var start = new Point(bandDevice.X + Math.Max(1, bandDevice.Width / 2),
                bandDevice.Y + Math.Max(1, bandDevice.Height / 2));
            Console.WriteLine($"起点（设备像素）={start} 带={bandDevice} 视口={canvas.Viewport.TopLeft} zoom={canvas.Viewport.Zoom:F2}");
            if (bandDevice.Width < 4 || bandDevice.Height < 4)
            {
                Console.WriteLine("NOT-RUN：带在屏上没有面积（缩放太小），这一趟量不到东西。");
                return 2;
            }

            canvas.ShowDebugHud = hud;          // --hud：连读数层一起量（它也在 OnPaint 里画）
            canvas.ShowCompareOverlay = compare;    // --no-compare：关掉色块层，用来把两层的钱分开量
            Console.WriteLine($"档位：读数层={(hud ? "开" : "关")} 重影对比层（色块那一层）={(compare ? "开" : "关")} " +
                              $"缩放={atZoom}%（--at-zoom 换档，阈值不分档）");

            // ═══ 需求五的口径：同一形状连跑 N 趟，逐趟结清、逐趟判阈值，最后给跨趟汇总 ═══
            // 只报最好那一次等于自欺——同一份代码在这台机器上量到过 17ms 也量到过 200ms，
            // 差别在操作系统抢不抢占、后台在忙什么，不在实现。所以：
            // · 每一趟单独记一份读数（含进程 CPU 时间，用来把"被抢占"与"真在算"分开）；
            // · 每条阈值按"趟趟都得过"判，报的是最差那一趟，不是平均、更不是最好；
            // · HUD 开与关分别报（需求三：不许把两者混为一谈）。
            var readings = new List<RunReadings>();
            for (int run = 1; run <= runs; run++)
            {
                RunReadings r = DragOnce(canvas, project, start, moves, run);
                readings.Add(r);
                PrintRun(r);
            }

            Console.WriteLine();
            Console.WriteLine($"─── 汇总（{(hud ? "HUD 开启" : "HUD 关闭")} · 对比层{(compare ? "开" : "关")} · {runs} 趟）───");
            Console.WriteLine("各趟 P50  = " + string.Join("  ", readings.Select(x => $"{x.P50:F1}")));
            Console.WriteLine("各趟 P95  = " + string.Join("  ", readings.Select(x => $"{x.P95:F1}")));
            Console.WriteLine("各趟 最慢 = " + string.Join("  ", readings.Select(x => $"{x.Max:F1}")));
            Console.WriteLine("各趟 拖动阶段整块清缓存 = " + string.Join("  ", readings.Select(x => $"{x.Clears}")));

            // ═══ 分位数把 N 趟的帧样本并成一个池再算 ═══
            // 单趟只有 5–7 帧：这时"P95"实际上等于"倒数第二慢那一帧"，用它判阈值没有统计意义。
            // 并池不是放宽——每一趟自己的 P50/P95/最慢 都照常逐行打印在上面，
            // 而且"单帧 ≤ 50ms"仍按<b>逐趟</b>最严判（任何一趟冒出一帧 50ms 以上就红）。
            var pool = new List<double>();
            var hudPool = new List<double>();
            var canvasPool = new List<double>();
            foreach (RunReadings x in readings)
            {
                pool.AddRange(x.DragFrames);
                for (int i = 0; i < x.DragFrames.Length; i++)
                {
                    if (x.HudOfFrames[i] <= 0) continue;
                    hudPool.Add(x.HudOfFrames[i]);
                    canvasPool.Add(Math.Max(0, x.DragFrames[i] - x.HudOfFrames[i]));
                }
            }
            pool.Sort();
            double PooledP50 = Percentile(pool, 0.5), pooledP95 = Percentile(pool, 0.95);
            Console.WriteLine($"帧样本池 {pool.Count} 帧（{runs} 趟合并）：平均 {pool.Average():F1}ms " +
                              $"P50 {PooledP50:F1}ms P95 {pooledP95:F1}ms 最慢 {pool[^1]:F1}ms");
            int failed = 0;
            failed += CheckAllRuns(ref failed, "不调用 RenderWhole", readings, x => x.WholeRuns, 0, "次");
            failed += CheckAllRuns(ref failed, "拖动期间整块清瓦片缓存 = 0", readings, x => x.Clears, 0,
                "次（每趟松手收尾那一次是 EndPreview 推进修订号，属设计，单列在下面）");
            // 需求五原话是"鼠标移动 <b>100 次</b>时预览帧不超过 10 帧"，所以这一格按<b>每 100 次移动</b>折算：
            // 把 --moves 调到 300 只是多取样用来定位"慢在第几帧"，不许顺手把分母也放大成 300（那才是放宽阈值）。
            failed += CheckAllRuns(ref failed, "每 100 次移动预览帧 ≤ 10",
                readings, x => x.PreviewFrames * 100.0 / Math.Max(1, x.Moves), 10, "帧");
            failed += Check(ref failed, "拖动帧 P95 ≤ 33ms（合并样本池）", pool.Count > 0 && pooledP95 <= 33.0,
                $"池 {pool.Count} 帧 P95={pooledP95:F1}ms；逐趟 P95 最大 {readings.Max(x => x.P95):F1}ms" +
                $"（单趟只有 {readings.Min(x => x.Samples)}–{readings.Max(x => x.Samples)} 帧，分位靠合并）");
            failed += CheckAllRuns(ref failed, "单帧 ≤ 50ms（逐趟最严判）", readings, x => x.Max, 50, "ms");
            failed += CheckAllRuns(ref failed, "UI 没冻住（收尾再投一条的响应 ≤ 200ms）",
                readings, x => x.AliveLatencyMs, 200, "ms");
            failed += CheckAllRuns(ref failed, "拖动路径上一次导出都没跑", readings, x => x.Exports, 0, "次");
            // ═══ 第十九改：把两道"工装自证"改成<b>真会红</b>的形状 ═══
            // 上一版这两道都是"取负数 ≤ 0"那种写法：值域天生就 ≤ 0（Paints、样本数不可能为负），
            // 于是<b>这一趟压根没画东西也照样绿</b>——本批现场就撞上：五趟里四趟 预览帧 0、
            // 重叠 120→120、蓝线 0→0，而"每 100 次移动预览帧 ≤ 10"那道门反而<b>达标</b>（0 ≤ 10）。
            // 一条只在"没量到"时红、在"量到 0 帧"时也绿、在"压根没拖动"时也绿的门，等于没门。
            // 改成 bool 判据：没画东西就红。（那一版还加了一道"必须净改参数"的前提门——
            // <b>第二十改把它换了</b>，见下面那枚"进了拼缝编辑并拖起来"的门。）
            int blankRuns = readings.Count(x => x.Paints <= 0 || x.Samples <= 0);
            failed += Check(ref failed, "确实画过东西（Paint>0 且有帧样本，否则上面全是空判据）", blankRuns == 0,
                $"{readings.Count - blankRuns}/{readings.Count} 趟有帧样本；" +
                $"逐趟 Paint {string.Join(" ", readings.Select(x => x.Paints))}·样本 " +
                $"{string.Join(" ", readings.Select(x => x.Samples))}");
            // ═══ 工装自证：这一趟得真的在拖 ═══
            // Windows 会合并队列里没取的 WM_MOUSEMOVE，所以"投了 100 条"≠"到了 100 条"。
            // 本批现场量到：上一版每 20 条泵一下，结果五趟的 trace 全是"设备位移 (0,0)·对齐没变"——
            // 那种趟数下报的 P95／最慢帧量的是"按下＋松手"，不是拖动途中帧。
            // 这道门<b>只加严、不放宽</b>任何一条产品阈值：它判的是"这批量到底算不算拖动"。
            failed += CheckAllRuns(ref failed, "投出的拖动移动逐条送达（工装自证，不然量的不是途中帧）",
                readings, x => x.Moves - x.MovesDelivered, 0, "条没送到（合并掉了）");
            // 送达还不够：送到了但<b>压根没进拼缝编辑</b>的趟，量的仍然不是拖动途中帧。
            // ═══ 第二十改：这一枚门上一版写错了问题，本批现场量出来的 ═══
            // 第十九改那版问的是"末点净参数改了没有"，而这批的量是这样的：五趟全部
            // 命中 seam·OverlapBand·固定After、100 条移动逐条送达、预览帧 81／101／102／101／102、
            // trace 挂在同一条缝上且阶段已是 AlignSeam——<b>手势真的落地了</b>；
            // 只有"重叠 120→120"是 0，因为这一趟形状是来回拖、末点等于起点，而产品按设计
            // 对"拖回原位"不留历史（<c>align/对齐拖动整趟只提交一条撤销，拖回原位不留空记录</c>）。
            // ⇒ 旧门把<b>已经量到的</b>判成"不许当拖动证据"，把整批拖动读数一并作废——
            // 判据问错问题比空转更贵，所以改成读产品自己记的手势阶段，净参数只作读数不作门。
            int notDragged = readings.Count(x => !x.GestureLanded);
            failed += Check(ref failed, "每趟都真的进了拼缝编辑并拖起来（trace 在本趟命中的那条缝上、阶段已过阈值）",
                notDragged == 0,
                $"{readings.Count - notDragged}/{readings.Count} 趟拖起来了；没拖起来的 {notDragged} 趟逐列：" +
                string.Join(" ", readings.Select((x, i) => $"第{i + 1}趟 命中[{x.HitAtPress}] trace[{x.TraceStage}]"
                    + $"·送达{x.MovesDelivered}/{x.Moves}·预览帧{x.PreviewFrames}")) +
                "——没拖起来的趟报的 P95／最慢帧／瓦片留住的账<b>都不许当拖动途中帧的读数用</b>；" +
                "（净参数改动本批五趟全是 0→属这批来回拖的形状，见上面那两格 align 判据，不是没落地）");

            if (hudPool.Count > 0)
            {
                hudPool.Sort();
                canvasPool.Sort();
                Console.WriteLine($"HUD 单独耗时（池 {hudPool.Count} 帧）：平均 {hudPool.Average():F1}ms " +
                                  $"P50 {Percentile(hudPool, 0.5):F1}ms P95 {Percentile(hudPool, 0.95):F1}ms 最慢 {hudPool[^1]:F1}ms");
                Console.WriteLine($"普通画布绘制（同批帧减去 HUD）：平均 {canvasPool.Average():F1}ms " +
                                  $"P50 {Percentile(canvasPool, 0.5):F1}ms P95 {Percentile(canvasPool, 0.95):F1}ms 最慢 {canvasPool[^1]:F1}ms");
                Console.WriteLine("（需求三：HUD 开启/关闭两档必须分开报，上面这两行不许合成一个数。）");
                // 同一批帧的字节账合并成一行（逐趟那一行在下面各趟读数里，两边是同一个出口）。
                // 注意口径：上面两行毫秒池只含<b>有 HUD 的那几帧</b>，这一行分母是<b>拖动段全部帧</b>，两者帧数不同。
                long allocBytes = readings.Sum(x => x.AllocBytes), allocFrames = readings.Sum(x => x.AllocFrames);
                Console.WriteLine($"每帧托管堆分配 = {allocBytes / Math.Max(1, allocFrames):F0}B／帧 "
                                  + $"（{allocFrames} 帧共 {allocBytes / 1048576.0:F2} MiB；窗口＝各趟拖动段，与每趟那行同出口）");

                // ═══ 第三十六改：把「面板装不装得下它那 14 行」搬到真窗口这一档量一次 ═══
                // 电池那两格用的是夹具项目；这里比的是<b>真 17 张图那一屏的读数文本</b>（含第三十四改加长的四段字节）。
                // <b>口径要写清：这个工装进程自己没声明 DPI 感知，现量 进程 DPI 96</b>
                // ——所以这一格量不到 150% 那一屏（那一档早有另一次证据：在产品自己的窗口里、进程 DPI 144 下跑的十二步）。
                // 它的用处是：拿真实项目的真实文本，验一次"框 ≥ 文字真实占位"与产品估框的算法在长文本下仍然成立。
                (int boxW, int boxH) = canvas.HudPanelSizeForTest;
                Size need = StitchCanvasControl.HudTextExtent(canvas.HudTextForTest);
                var box = new Size(boxW, boxH);
                failed += Check(ref failed, "面板装得下自己的读数（真项目那 14 行）",
                    PanelFitsReadouts(box, need),
                    $"框 {boxW}×{boxH}／这 {canvas.HudTextForTest.Count(c => c == '\n') + 1} 行真实占 " +
                    $"{need.Width}×{need.Height}（进程 DPI {canvas.DeviceDpi}）——装不下就是有读数被 GDI 裁在框外，" +
                    "需求四点名的那几项从屏上消失");
                Console.WriteLine($"面板装得下否    = 框 {boxW}×{boxH}、文字真实 {need.Width}×{need.Height}" +
                                  $"（进程 DPI {canvas.DeviceDpi}；<b>这一档量不到 150%</b>——" +
                                  "工装进程没有产品那个 DPI 感知声明，150% 那一屏的证据在产品自己窗口跑的那次十二步里）");
            }
            Console.WriteLine("各趟松手那一帧（设计里「一次手势只清一遍」的那一次全量重算）：" +
                              string.Join("  ", readings.Select(x => $"{x.ReleaseFrameMs:F1}ms/清{x.ClearsAfterRelease}")));
            Console.WriteLine("各趟墙钟 vs 进程 CPU（所有线程合计）：" +
                              string.Join("  ", readings.Select(x => $"{x.WallMs:F0}/{x.CpuMs:F0}ms")));
            Console.WriteLine("各趟 UI 线程真正在画的时长（拖动帧合计）：" +
                              string.Join("  ", readings.Select(x => $"{x.DragFrames.Sum():F0}ms")));
            // 读数怎么解释（需求五：不要把偶发环境抢占误判成代码稳定流畅，也不要把并行线程的忙碌算成自己被卡）：
            // · 进程 CPU 是<b>所有线程</b>合计，可以大于墙钟（后台解码、终结器、GC 都在跑）；
            // · "拖动帧合计"才是 UI 线程自己花在画上的时间——它与墙钟的差是要么在等消息泵、要么被系统切走；
            // · 配合上面每帧的 * 标记（那一帧内发生过 gen0 回收）与逐趟 GC 数，
            //   就能说清偶发的 50ms+ 那一帧到底是"这里在算"还是"别人抢了 CPU / 回收撞上了这一帧"。
            Console.WriteLine($"跨趟最差：P50 {readings.Max(x => x.P50):F1}ms · P95 {readings.Max(x => x.P95):F1}ms · " +
                              $"最慢帧 {readings.Max(x => x.Max):F1}ms · 队列延迟 {readings.Max(x => x.QueueDelayMs):F0}ms · " +
                              $"GC gen0 {readings.Sum(x => x.GcGen0)} 次 / gen2 {readings.Sum(x => x.GcGen2)} 次（{runs} 趟合计）");
            // 完整布局重算的归因：拖动预览每次都要重算一张临时布局（与"单帧预览重算"同数），
            // 静止那一帧一次都不许有——这一条单独判，别把两件事混成一个数。
            failed += CheckAllRuns(ref failed, "静止帧不许重算完整布局（每趟 Build 次数 ≤ 每趟预览帧数）",
                readings, x => x.LayoutBuilds - (double)x.PreviewFrames, 0,
                "次（超出部分只能来自绘制层：需求三第 6 条禁止读数层调 CanvasLayout.Build）");

            // ═══ 需求三第 2 与第 4 条在真窗口里的样子：数字刷新跟着帧耗时走，但不越过 10Hz ═══
            // 读数层那六个触发量里只有"当前帧耗时"没法在离线夹具里量——夹具每帧耗时四舍五入到
            // 一位小数常常是同一个数，文本没变就不该重建面板，于是"刷新没刷新"判不出来。
            // 真窗口里每帧耗时天然不同，所以这一格放在这里量，两头都判：
            // · 至少重建过一次 ⇒ 读数没冻在第一帧（冻住就是需求一点名那类骗人界面）；
            // · 总次数不超过 10Hz 换算出来的预算 ⇒ 闸门真的在限流，不是"每帧都重排文本"。
            // 两个计数都是<b>进程累计</b>（末趟读数即全程读数），预算按各趟墙钟之和算。
            if (hud)
            {
                long panels = readings[^1].HudPanelRebuilds;
                long frames = readings.Sum(x => x.Samples);
                long wall = (long)readings.Sum(x => x.WallMs);
                long budget = 1 + wall / 100;
                failed += Check(ref failed,
                    "开着读数层：面板重建过、且不超过 10Hz 预算（需求三第 2/4 条）",
                    panels >= 1 && panels <= budget,
                    $"面板重建 {panels} 次（预算 {budget} 次＝{wall}ms 墙钟 ÷ 100）、" +
                    $"这 {frames} 帧是画过的帧数；退回每帧重排文本会是 {frames} 次以上");
            }

            // ═══ 第十四轮需求一：真窗口里再量一次"撤销把视口也带回去" ═══
            // 上面那一趟是"下去又回来"（净位移 0、补偿自然归零），量的是预览合并；
            // 这一趟是一次性净改动 + 固定侧设为 After（只有那一档会挪视口），然后点产品自己的菜单项。
            if (wheel > 0 || zoom > 0)
            {
                int g = GuiGestureHarness.Run(canvas, start, wheel, zoom, runs, hud);
                if (g < 0)
                    Console.WriteLine("滚轮／缩放通道：通路自证没过，这一档按未量记账（上面拖动通道的读数不受影响）。");
                else failed += g;
            }

            failed += UndoRollsBackViewportInRealWindow(form, canvas, project);
            if (mixedCycles > 0)
                failed += MixedCycles(form, canvas, mixedCycles, hud);

            Console.WriteLine();
            Console.WriteLine(failed == 0
                ? $"结论：真实窗口这 {runs} 趟全部通过（{files.Length} 张、{(realSamples ? "真截图" : "合成图")}）。"
                : $"结论：真实窗口这 {runs} 趟里有 {failed} 条没过——按上面的读数改实现，不许放宽阈值。");
            if (!realSamples)
                Console.WriteLine("提醒：这一趟用的是合成图。真截图内容下的手感仍需 test/ 在场时再跑一遍才算数。");
            return failed == 0 ? 0 : 1;
        }
        catch (Exception ex)
        {
            // 环境跑不起来（没有桌面会话 / DPI 初始化失败等）就老实报 NOT-RUN，不许让它"看起来通过"
            Console.WriteLine($"NOT-RUN：真实 GUI 测量崩了：{ex.GetType().Name}: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
            return 2;
        }
        finally
        {
            form.Dispose();
        }
    }

    /// <summary>
    /// 真窗口那一份"撤销后视口回滚"证据（第十四轮需求一第 5 条）。
    ///
    /// 与电池里那格的差别只在<b>通路</b>：这里的鼠标消息是真的 <c>PostMessage</c> 进那个 HWND、
    /// 撤销是真的点"编辑→撤销"那枚菜单项、重画真的过了消息泵；电池那格量的是同一套逻辑，
    /// 但控件没上屏。需求八不许把离线电池结果写成 GUI 已验证，所以这一条单列在真窗口里再量一遍。
    ///
    /// 只测<b>固定后一段</b>那一档：只有它会把视口当纯显示补偿来挪（固定前一段与蓝线手势全程不碰视口，
    /// 它们"撤销不许动屏"由 <c>align/固定后一段拖对齐后撤销：参数、画布高、视口、设备矩形一起恢复</c> 里的反面对照守着）。
    /// </summary>
    private static int UndoRollsBackViewportInRealWindow(MainForm form, StitchCanvasControl canvas, Project project)
    {
        Console.WriteLine();
        Console.WriteLine("─── 撤销有没有把视口也带回去（第十四轮需求一，真窗口）───");
        int failed = 0;
        Seam before = project.Doc.Seams[^2];
        string seamId = before.Id;
        project.Mutate("真窗口·固定后一段", _ => before.Fixed = FixedSide.After);
        Pump(10);
        if (project.Layout.DescribeSeam(before) is not SeamGeometry geo)
            return Check(ref failed, "这条缝算得出几何", false, "DescribeSeam 返回空，后面无从判断");

        Rectangle band = canvas.Viewport.CanvasToDevice(geo.OverlapRect);
        var at = new Point(band.X + Math.Max(1, band.Width / 2), band.Y + Math.Max(1, band.Height / 2));
        IntPoint viewportBase = canvas.Viewport.TopLeft;
        int overlapBase = before.OverlapLength;
        int cutBase = before.CutPosition;
        int heightBase = project.Layout.CanvasSize.Height;

        // ── 真窗口那一屏的像素对照（需求一第 5 条那一格在真窗口里的加强版）──
        // 抓法是产品自己的 PaintCore（RenderFrameForTest），<b>不是 HWND 截屏</b>：
        // 量的是"这个真控件此刻会画出来的那一帧"——真设备尺寸、真视口、真瓦片缓存，
        // 但绝不落盘（`test/` 是私人聊天截图，屏幕内容只在这段内存里比，比完就释放，屏上一个字节都不写进仓库）。
        // HUD 必须先关：面板里那行"当前帧耗时"每帧都变，留着它这两帧永远对不上，那是与撤销无关的差。
        // 上面那段注释原先写"这里只量 Paint 次数不量像素"，本批把像素补上是因为
        // 拿"视口挪一行再抓一次必须不同"做了敏感性对照之后，这个比法就判得出"数值回了但屏没回"那一类。
        bool hudWasOn = canvas.ShowDebugHud;
        canvas.ShowDebugHud = false;
        Pump(30);
        System.Drawing.Size canvasSize = canvas.ClientSize;
        using var frameBase = new System.Drawing.Bitmap(canvasSize.Width, canvasSize.Height);
        using var frameDragged = new System.Drawing.Bitmap(canvasSize.Width, canvasSize.Height);
        using var frameRestored = new System.Drawing.Bitmap(canvasSize.Width, canvasSize.Height);
        using var frameShifted = new System.Drawing.Bitmap(canvasSize.Width, canvasSize.Height);
        using var frameRedone = new System.Drawing.Bitmap(canvasSize.Width, canvasSize.Height);
        canvas.RenderFrameForTest(frameBase);

        // 一次性净改动：按下去、往下拖 20 设备像素（≈57 源图行）、松手 —— 固定后一段会挪视口
        IntPtr hwnd = canvas.Handle;
        PostMessage(hwnd, WmLButtonDown, (IntPtr)MkLButton, MakeLParam(at.X, at.Y));
        Pump(10);
        PostMessage(hwnd, WmMouseMove, (IntPtr)MkLButton, MakeLParam(at.X, at.Y + 20));
        Pump(30);
        PostMessage(hwnd, WmLButtonUp, IntPtr.Zero, MakeLParam(at.X, at.Y + 20));
        Pump(60);

        IntPoint viewportDragged = canvas.Viewport.TopLeft;
        canvas.RenderFrameForTest(frameDragged);
        int overlapDragged = project.Doc.Seams[^2].OverlapLength;
        int heightDragged = project.Layout.CanvasSize.Height;
        failed += Check(ref failed, "这一趟真的改了重叠（不然撤销什么都还没发生）",
            overlapDragged != overlapBase, $"重叠 {overlapBase}→{overlapDragged}");
        failed += Check(ref failed, "这一趟真的挪了视口（纯显示补偿落到了屏幕上）",
            viewportDragged != viewportBase, $"视口 {viewportBase}→{viewportDragged}");
        failed += Check(ref failed, "这一趟真的改了画布高（不然「高度恢复」那句是空判据）",
            heightDragged != heightBase, $"画布高 {heightBase}→{heightDragged}");

        long paintsBeforeUndo = canvas.PaintCount;
        bool clicked = form.ClickUndoMenuItemForTest();
        // 撤销之后必须真的重画过：只把 Viewport.TopLeft 改回来还不算"画面恢复了"——
        // 那正好是需求一抱怨的那一类（数值对了，屏幕上还是上一帧）。这里量 Paint 次数。
        // 这一族以前<b>不量像素</b>，理由是"真窗口里截屏比对会把与撤销无关的重绘也算进来"；
        // 本批把像素补上了，用的不是截屏而是产品自己的 PaintCore（见上面 frameBase 那一段），
        // 再配一枚"故意挪 1 行必须看得出来"的敏感性针对付"抓法坏了才相同"那种假绿。
        PumpUntil(() => canvas.PaintCount > paintsBeforeUndo, 300);
        long paintsAfterUndo = canvas.PaintCount;
        Pump(60);
        Seam afterUndo = project.Doc.Seams[^2];       // 撤销换的是整个文档，旧实例已经是"上一世"的那条缝
        IntPoint viewportRestored = canvas.Viewport.TopLeft;
        int heightRestored = project.Layout.CanvasSize.Height;
        failed += Check(ref failed, "撤销后画面确实重画过（不是只改了控件内部的数值）",
            paintsAfterUndo > paintsBeforeUndo,
            $"撤销前 Paint {paintsBeforeUndo} 次 → 撤销后 {paintsAfterUndo} 次（最多等 300ms）");
        failed += Check(ref failed, "「编辑→撤销」当时可点（历史里真有这一条）", clicked,
            clicked ? "已点击" : "菜单项是灰的：拖动没压历史，或者根本没改到东西");
        failed += Check(ref failed, "撤销后对齐（重叠）恢复", afterUndo.OverlapLength == overlapBase,
            $"{overlapBase}→{overlapDragged}→{afterUndo.OverlapLength}");
        failed += Check(ref failed, "撤销后蓝线恢复", afterUndo.CutPosition == cutBase,
            $"{cutBase}→{afterUndo.CutPosition}");
        failed += Check(ref failed, "撤销后画布高恢复", heightRestored == heightBase,
            $"{heightBase}→{heightDragged}→{heightRestored}");
        failed += Check(ref failed, "撤销后视口回到拖动前（需求一.3：不许只把参数换了就算恢复）",
            viewportRestored == viewportBase, $"实际 {viewportRestored}，应回 {viewportBase}");

        // ── 那一屏的像素：撤销之后必须与拖动前<b>逐字节相同</b> ──
        // 这三行是一整套正向对照，缺一条就退化成"恒真/恒假都算过"：
        //   ① 拖动后那一屏必须<b>不同</b>——不然"撤销后相同"根本没在比两帧不同的东西；
        //   ② 撤销后那一屏必须<b>差异 0</b>——需求一抱怨的正是"数值回来了、屏幕位置没回来"；
        //   ③ 撤销之后把视口故意挪 1 行再抓，必须<b>又不同</b>——这条证明这个抓法对视口敏感；
        //      没有它，②可以因为"抓出来的两帧压根不随视口变"（抓法坏了）而假绿。
        canvas.RenderFrameForTest(frameRestored);
        long dragDiff = HudCostTests.CountPanelBytesDiffer(frameBase, frameDragged);
        long undoDiff = HudCostTests.CountPanelBytesDiffer(frameBase, frameRestored);
        failed += Check(ref failed, "拖动后那一屏与拖动前必须不同（不然下面那句是空比）",
            dragDiff > 0, $"取样字节差 {dragDiff} 处");
        failed += Check(ref failed, "撤销后那一屏与拖动前逐字节相同（需求一：画面位置也要回来）",
            undoDiff == 0, $"字节差 {undoDiff} 处（>0 就是参数回了但屏没回，正是需求一那个缺陷的形状）");
        canvas.Viewport.MoveTopLeftTo(new IntPoint(viewportBase.X, viewportBase.Y + 1));
        Pump(30);
        canvas.RenderFrameForTest(frameShifted);
        long shiftDiff = HudCostTests.CountPanelBytesDiffer(frameBase, frameShifted);
        canvas.Viewport.MoveTopLeftTo(viewportRestored);      // 挪回去，后面的重做仍以撤销后那一屏为基准
        Pump(30);
        failed += Check(ref failed, "抓法本身对视口敏感（故意挪 1 行必须看得出来）",
            shiftDiff > 0, $"挪 1 行读出字节差 {shiftDiff} 处；读到 0 说明上面那句\"相同\"是抓法坏了");

        bool redone = form.ClickRedoMenuItemForTest();
        Pump(60);
        failed += Check(ref failed, "重做把那段纯显示补偿再带上（快照跟着历史进 redo 栈）",
            redone && canvas.Viewport.TopLeft == viewportDragged,
            redone ? $"撤销后 {viewportRestored} → 重做后 {canvas.Viewport.TopLeft}，应回 {viewportDragged}"
                   : "「编辑→重做」是灰的");
        canvas.RenderFrameForTest(frameRedone);
        long redoDiff = HudCostTests.CountPanelBytesDiffer(frameBase, frameRedone);
        failed += Check(ref failed, "重做后那一屏必须与拖动前不同（redo 不是空操作）",
            redoDiff > 0, $"字节差 {redoDiff} 处");
        canvas.ShowDebugHud = hudWasOn;
        Console.WriteLine($"（那一屏像素对照 {canvasSize.Width}×{canvasSize.Height}：拖动后差 {dragDiff}、" +
                          $"撤销后差 {undoDiff}、故意挪 1 行差 {shiftDiff}、重做后差 {redoDiff}）");
        Console.WriteLine($"（同一缝 {seamId}，缩放 {canvas.Viewport.Zoom:F2}：起点视口 {viewportBase}、" +
                          $"拖动后 {viewportDragged}、撤销后 {viewportRestored}）");
        return failed;
    }

    /// <summary>分位数（已排序的池）：P95 就传 0.95。与控件里那个同一算法，合并池才算得出有统计意义的数。</summary>
    internal static double Percentile(List<double> sorted, double fraction) =>
        sorted.Count == 0 ? 0
            : sorted[Math.Clamp((int)Math.Ceiling(fraction * sorted.Count) - 1, 0, sorted.Count - 1)];

    internal static int Check(ref int failed, string name, bool ok, string actual)    {
        Console.WriteLine($"  [{(ok ? "OK  " : "FAIL")}] {name} —— {actual}");
        if (!ok) failed++;
        return ok ? 0 : 1;
    }

    /// <summary>泵一会儿消息（真消息队列，不是 DoEvents 的空转：等待期间窗口照样重画）。</summary>
    internal static void Pump(int ms)
    {
        long until = Environment.TickCount64 + ms;
        while (Environment.TickCount64 < until)
        {
            Application.DoEvents();
            Thread.Sleep(1);
        }
        Application.DoEvents();
    }

    internal static void PumpUntil(Func<bool> condition, int timeoutMs)
    {
        long until = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < until && !condition())
        {
            Application.DoEvents();
            Thread.Sleep(1);
        }
        Application.DoEvents();
    }

    /// <summary>
    /// 拿素材：优先 <paramref name="dir"/> 里的真截图（至少 17 张），否则在 %TEMP% 造 17 张 1179×2556 合成图。
    /// 合成与真实两条都走同一套导入与绘制通路，差别只在"内容是不是真的聊天记录"——
    /// 所以输出里必须说明用的是哪一种（需求九要求分列已验证/未验证）。
    /// </summary>
    private static (string[] Files, bool RealSamples) ResolveImages(string dir)
    {
        if (Directory.Exists(dir))
        {
            string[] found = Directory.EnumerateFiles(dir, "*.png", SearchOption.TopDirectoryOnly)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (found.Length >= 17) return (found.Take(Math.Min(found.Length, 17)).ToArray(), true);
            Console.WriteLine($"（{dir}/ 只有 {found.Length} 张，不足 17 —— 改用合成图，并会在结论里写明）");
        }
        else
        {
            Console.WriteLine($"（目录 {dir}/ 不在本机 —— 改用合成图）");
        }

        string tmp = Path.Combine(Path.GetTempPath(), "ls-gui-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tmp);
        var list = new List<string>();
        for (int i = 0; i < 17; i++)
        {
            var img = LongScrollTests.StripedPage(1179, 2556 + i);   // 每张差一行：避免"全部同图"让去重判据空转
            string path = Path.Combine(tmp, $"synth_{i:D2}.png");
            File.WriteAllBytes(path, PngEncoder.EncodeToBytes(img));
            list.Add(path);
        }
        return (list.ToArray(), false);
    }
}
