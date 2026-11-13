using System.Diagnostics;
using System.Windows.Forms;
using LongStitch.App.UI;
using LongStitch.Core;
using LongStitch.Core.Geometry;
using LongStitch.Core.Layout;

namespace LongStitch.Checks;

/// <summary>
/// 真窗口的<b>第二、第三种手势</b>：普通滚轮平移、Ctrl+滚轮缩放（第十四轮需求五长跑那一档欠的账）。
///
/// ═══ 为什么单开一个类，而不是往 <see cref="GuiPerfHarness"/> 里插 ═══
/// 那个文件里的拖动通道已经被文档按字面量引用住了（README／精度红线里那几句"某趟 P95＝多少毫秒"
/// 是照着它的输出抄的）。往里插几十行中文注释的改动，风险全落在"抄错的锚点"上，
/// 而这一档要的读数虽然同名，逻辑却是另一套（没有按下／松手、没有预览合并、没有提交重画）。
/// 所以：消息投递与泵这些原语复用 <see cref="GuiPerfHarness"/>（把用到的成员改成 internal），
/// 读数各通道各读一份，<b>字段名与顺序刻意与 DragOnce 保持一致</b>，改一边要记得看另一边。
///
/// ═══ 为什么滚轮走真消息、缩放走产品方法 ═══
/// 普通滚轮只需要一条 <c>WM_MOUSEWHEEL</c>，投进那个 HWND 就是真人通路。
/// Ctrl+滚轮缩放还要带住 Ctrl 键态：产品里读的是 <c>ModifierKeys</c>（Win32 GetKeyState），
/// 而 <c>PostMessage</c> 不会改线程键态——唯一的合成办法是往系统注入真按键，
/// 那会打到用户此刻正在用的别的程序上。所以缩放这一档直接调产品的公开入口
/// <c>StitchCanvasControl.ApplyWheel(120, Keys.Control, device)</c>：
/// <b>控件在上屏的真窗口里、重画真过了消息泵</b>，缺的只是"Ctrl 由键盘产生"这一截。
/// 文档里必须照这个口径写，不许把这一档写成"真按键滚轮已验证"。
///
/// ═══ 通路自证（这一档最容易被空量骗过去） ═══
/// 滚轮消息可能被静默丢掉（焦点不在控件、DPI 换算、坐标落在屏外）。
/// 丢掉之后的读数最好看：<b>0 帧、0 次整块清理、0 次布局重算</b>——每条阈值都绿，其实什么都没量。
/// 所以每一趟先证"手势真被产品接住"：普通滚轮必须让 <c>Viewport.TopLeft.Y</c> 挪过至少一格，
/// 缩放必须让 <c>Viewport.Zoom</c> 变过；证不出来就报 NOT-RUN，不判任何阈值。
/// </summary>
internal static class GuiGestureHarness
{
    /// <summary>WM_MOUSEWHEEL：高 16 位是格子增量（±120），低 16 位是键态标志，lParam 是<b>屏幕</b>坐标。</summary>
    private const uint WmMouseWheel = 0x020A;

    /// <summary>一次滚轮格子的增量，与产品 <c>WheelStepPixels</c> 期待的 120 一格一致。</summary>
    private const int WheelNotch = 120;

    /// <summary>
    /// 跑两个通道。返回未通过的判据条数（0 全过）；<c>-1</c> 表示整条通道通路自证失败，
    /// 调用方按 NOT-RUN 记账（不许把"消息没投进去"写成"这一档全绿"）。
    /// </summary>
    public static int Run(StitchCanvasControl canvas, Point client, int wheelNotches,
        int zoomSteps, int runs, bool hud)
    {
        int failed = 0;
        bool notRun = false;
        Console.WriteLine();
        Console.WriteLine($"═══ 真窗口第二／三种手势（读数层={(hud ? "开" : "关")}，逐趟判阈值）═══");
        if (wheelNotches > 0)
        {
            int f = Channel(canvas, client, "滚轮平移", wheelNotches, runs, hud, false);
            if (f < 0) notRun = true; else failed += f;
        }
        if (zoomSteps > 0)
        {
            int f = Channel(canvas, client, "Ctrl+滚轮缩放", zoomSteps, runs, hud, true);
            if (f < 0) notRun = true; else failed += f;
        }
        return notRun ? -1 : failed;
    }

    /// <summary>
    /// 一个通道跑 <paramref name="runs"/> 趟：每趟当场结清、当场判，报的是最差那一趟而不是平均。
    /// 与拖动通道同一条理由——把 N 趟摊成一个平均数，正好抹平需求五不许抹平的那一帧偶发。
    /// </summary>
    private static int Channel(StitchCanvasControl canvas, Point client, string gesture,
        int steps, int runs, bool hud, bool zoom)
    {
        var pool = new List<double>();
        long proofFailures = 0;
        int failed = 0;
        Console.WriteLine();
        Console.WriteLine($"─── {gesture}：每趟 {steps} 次（前半正着来、后半倒回去，净位移≈0）───");

        for (int run = 1; run <= runs; run++)
        {
            Read r = GestureOnce(canvas, client, steps, run, zoom);
            if (r.Proof == 0)
            {
                proofFailures++;
                Console.WriteLine($"  第 {run} 趟 NOT-RUN：{r.ProofText}——手势没被接住，这一趟的读数一律不判");
                continue;
            }
            pool.AddRange(r.Frames);
            Print(r, gesture, runs);
            failed += Judge(r, pool, hud);
        }

        if (proofFailures > 0)
        {
            Console.WriteLine($"  [{(proofFailures == runs ? "NOT-RUN" : "FAIL")}] {gesture}通路自证 —— " +
                              $"{runs} 趟里有 {proofFailures} 趟手势没被产品接住；这属工装／环境问题，" +
                              "按未量记账，不许当成通过");
            return runs - proofFailures == 0 ? -1 : failed;
        }
        Console.WriteLine($"  {gesture}：{runs} 趟读数齐，池 {pool.Count} 帧");
        return failed;
    }

    /// <summary>一趟手势的读数（字段名与顺序对齐 <c>GuiPerfHarness.DragOnce</c>，别漂移）。</summary>
    private sealed class Read
    {
        public int Run;
        public int Steps;
        public long PreviewFrames;
        public long Paints;
        public int Samples;
        public double Avg, P50, P95, Max;
        public long TileRenders;
        public long WholeRuns;
        public long LayoutBuilds;
        public long Clears;
        public long PreviewRecomputes;
        public double PreviewRecomputeMs;
        public long Exports;
        public long HudPanelRebuilds;
        // 每帧瓦片预算那一档的读数（见 StitchCanvasControl.DrawTiles）：欠帧与追加要帧是"限流真发生了"的证据。
        public int TilesDeferredLastFrame;
        // 停手补帧：滚轮窗口过后产品自己补的那一帧高质量。它<b>不计进"途中"那一池</b>——
        // 与拖动通道把"松手那一帧"单列同一条口径（需求五问的是滚动途中卡不卡）。
        public int SettleFrames;
        public double SettleMs;
        public string GestureStats = "";
        public long DeferredRepaints;
        public long SettlePaints;
        public bool Armed;
        public double TileBudgetMs;
        public int GcGen0, GcGen2;
        public double WallMs, CpuMs;
        public double CacheHit;
        public double AliveLatencyMs;
        public double SumCpuMs = -1, ThreadCpuMs = -1;
        public int SchedThreads = -1;
        public StitchCanvasControl.HudSplit Hud;
        public double[] Frames = Array.Empty<double>();
        public double[] HudOfFrames = Array.Empty<double>();
        public string SamplesText = "";
        public string SlowestStages = "";
        // 第五改：叠层那一笔往下的六列拆账（同帧同一次结算，不是最后一帧的）。
        public string SlowestOverlay = "";
        public string ProofText = "";

        /// <summary>通路自证的量：普通滚轮＝视口挪过的格数，缩放＝zoom 变过的倍数。0＝手势根本没进产品。</summary>
        public double Proof;
    }

    /// <summary>
    /// 投一趟手势：先投第一格并当场自证，剩下的格子投完用一条哨兵鼠标移动把队列抽干
    /// （Windows 消息队列先进先出，哨兵被处理就说明它前面那些滚轮消息都处理完了，
    /// 不需要靠固定 sleep 猜"应该画完了"——固定 sleep 会让两趟量出不同的帧数）。
    /// </summary>
    private static Read GestureOnce(StitchCanvasControl canvas, Point client, int steps, int run, bool zoom)
    {
        var r = new Read { Run = run, Steps = steps };
        canvas.ResetFrameStats();
        // 每趟都从冷缓存起量（为什么必须这样：见 ClearTileCacheForTest 的注释）。
        // 注意这次清自己在下面 clearsBefore 之前，所以不算进"这一趟的整块清缓存"那笔账。
        canvas.ClearTileCacheForTest();
        long previewBefore = canvas.PreviewFramesForTest;
        long tilesBefore = canvas.TileRendersForTest;
        long clearsBefore = canvas.TileFullInvalidationsForTest;
        long wholeBefore = CanvasComposer.WholeRunCountForTest;
        long exportsBefore = LongStitch.Core.Export.ExportEngine.RunCountForTest;
        long layoutBefore = CanvasLayout.BuildCountForTest;
        long hudPanelBefore = canvas.HudPanelRebuilds;
        long deferredRepaintsBefore = canvas.DeferredRepaintRequestsForTest;
        long settlesBefore = canvas.SettlePaintsForTest;
        // 与拖动通道同一条规矩：调度读数先于 GC 基线取，否则诊断出口自己的分配会冒充"这一趟回收了几次"。
        GuiPerfHarness.Scheduling schedBefore = GuiPerfHarness.Scheduling.Read(canvas);
        int gen0Before = GC.CollectionCount(0), gen2Before = GC.CollectionCount(2);
        IntPtr hwnd = canvas.Handle;
        Process proc = Process.GetCurrentProcess();
        TimeSpan cpuBefore = proc.TotalProcessorTime;
        long wallBefore = Environment.TickCount64;

        // WM_MOUSEWHEEL 的 lParam 用的是<b>屏幕</b>坐标（与移动消息的客户端坐标不同），窗口这一趟不动，换算一次就够。
        Point screen = canvas.PointToScreen(client);
        int half = Math.Max(1, steps / 2);
        long topBefore = canvas.Viewport.TopLeft.Y;
        double zoomBefore = canvas.Viewport.Zoom;
        long firstShift = 0;
        long topAfterFirst = 0;
        double maxZoomRatio = 1.0;

        for (int i = 1; i <= steps; i++)
        {
            int dir = i <= half ? 1 : -1;
            if (zoom) canvas.ApplyWheel(WheelNotch * dir, Keys.Control, client);
            else GuiPerfHarness.PostMessage(hwnd, WmMouseWheel, WheelWParam(WheelNotch * dir),
                GuiPerfHarness.MakeLParam(screen.X, screen.Y));
            if (i == 1)
            {
                GuiPerfHarness.Pump(12);            // 等第一格真被接住，再取自证的量
                topAfterFirst = canvas.Viewport.TopLeft.Y;
                firstShift = Math.Abs(topAfterFirst - topBefore);
            }
            if (zoom) maxZoomRatio = Math.Max(maxZoomRatio, Math.Max(
                canvas.Viewport.Zoom / zoomBefore, zoomBefore / Math.Max(1e-6, canvas.Viewport.Zoom)));
            if (i % 10 == 0) GuiPerfHarness.Pump(2);
        }

        // 抽干队列：哨兵移动被处理 ⇒ 前面所有手势消息都已处理；哨兵自己那一帧留在读数里（下面明写）。
        long beforeAlive = canvas.MouseMoveCount;
        long alivePosted = Environment.TickCount64;
        GuiPerfHarness.PostMessage(hwnd, GuiPerfHarness.WmMouseMove, IntPtr.Zero,
            GuiPerfHarness.MakeLParam(client.X, client.Y));
        GuiPerfHarness.PumpUntil(() => canvas.MouseMoveCount > beforeAlive, 3000);
        r.AliveLatencyMs = Environment.TickCount64 - alivePosted;
        GuiPerfHarness.Pump(20);                    // WM_PAINT 是队列空了才补发的，再泵一小会儿让最后几帧落地

        // ── 途中那一池到此为止，后面的帧属于"停手补帧"──
        int gestureFrames = canvas.FrameSampleCount;
        long paintsAtGestureEnd = canvas.PaintCount;
        GuiPerfHarness.PumpUntil(() => canvas.PaintCount > paintsAtGestureEnd, 400);

        r.Proof = zoom ? maxZoomRatio : firstShift;
        r.ProofText = zoom
            ? $"zoom 从 {zoomBefore:F3} 最多走到 ×{maxZoomRatio:F3}（要求：变过）"
            : $"第一格后视口 Y 从 {topBefore} 挪到 {topAfterFirst}，这一格挪了 {firstShift}px（要求：≥ 一格）";

        r.PreviewFrames = canvas.PreviewFramesForTest - previewBefore;
        r.TileRenders = canvas.TileRendersForTest - tilesBefore;
        r.Clears = canvas.TileFullInvalidationsForTest - clearsBefore;
        r.WholeRuns = CanvasComposer.WholeRunCountForTest - wholeBefore;
        r.LayoutBuilds = CanvasLayout.BuildCountForTest - layoutBefore;
        r.Avg = canvas.FrameAvgMs;
        r.P50 = canvas.FramePercentileMs(0.5);
        r.P95 = canvas.FramePercentileMs(0.95);
        r.Max = canvas.MaxFrameMs;
        r.Paints = canvas.PaintCount;
        r.Samples = canvas.FrameSampleCount;
        r.SamplesText = canvas.FrameSamplesForTest;
        double[] allFrames = canvas.FrameSamplesMsForTest();
        r.Frames = allFrames.Take(Math.Min(gestureFrames, allFrames.Length)).ToArray();
        r.SettleFrames = Math.Max(0, (int)(canvas.PaintCount - paintsAtGestureEnd));
        r.SettleMs = allFrames.Length > gestureFrames ? allFrames.Skip(gestureFrames).Sum() : 0;
        r.GestureStats = r.Frames.Length == 0 ? "（途中一帧都没有，跳过）"
            : Stats(r.Frames);
        r.HudOfFrames = canvas.FrameHudSamplesMsForTest();
        r.SlowestStages = canvas.SlowestStageBreakdown;
        r.SlowestOverlay = canvas.SlowestOverlaySplit;
        r.CacheHit = canvas.CacheHitRate;
        r.PreviewRecomputes = canvas.PreviewRecomputeCount;
        r.PreviewRecomputeMs = canvas.PreviewRecomputeMs;
        r.GcGen0 = GC.CollectionCount(0) - gen0Before;
        r.GcGen2 = GC.CollectionCount(2) - gen2Before;
        r.Exports = LongStitch.Core.Export.ExportEngine.RunCountForTest - exportsBefore;
        r.HudPanelRebuilds = canvas.HudPanelRebuilds - hudPanelBefore;
        r.TilesDeferredLastFrame = canvas.TilesDeferredLastFrameForTest;
        r.DeferredRepaints = canvas.DeferredRepaintRequestsForTest - deferredRepaintsBefore;
        r.SettlePaints = canvas.SettlePaintsForTest - settlesBefore;
        r.Armed = canvas.SettleTimerArmedForTest;
        r.TileBudgetMs = canvas.TileBudgetMsForTest;
        r.WallMs = Environment.TickCount64 - wallBefore;
        r.CpuMs = (proc.TotalProcessorTime - cpuBefore).TotalMilliseconds;
        GuiPerfHarness.Scheduling schedAfter = GuiPerfHarness.Scheduling.Read(canvas);
        if (schedBefore.Ok && schedAfter.Ok)
        {
            r.SchedThreads = schedAfter.Threads;
            r.SumCpuMs = schedAfter.SumTotalMs - schedBefore.SumTotalMs;
            r.ThreadCpuMs = schedAfter.BusyTotalMs - schedBefore.BusyTotalMs;
        }
        r.Hud = canvas.HudSplitStats;
        return r;
    }

    /// <summary>途中那一池的平均/P50/P95/最慢（工装自己算，才敢只算"途中"那几帧）。</summary>
    private static string Stats(double[] frames)
    {
        var sorted = (double[])frames.Clone();
        Array.Sort(sorted);
        double P(double f) => sorted[Math.Clamp((int)Math.Ceiling(f * sorted.Length) - 1, 0, sorted.Length - 1)];
        return $"平均 {frames.Average():F1}ms P50 {P(0.5):F1}ms P95 {P(0.95):F1}ms 最慢 {sorted[^1]:F1}ms（{sorted.Length} 帧）";
    }

    /// <summary>WM_MOUSEWHEEL 的 wParam：高 16 位带符号格子增量，低 16 位键态标志（这里不按键）。</summary>
    private static IntPtr WheelWParam(int delta) =>
        new((nint)((uint)unchecked((ushort)delta) << 16));

    /// <summary>逐趟打印。有些数只报不判（整块清缓存、瓦片渲染、命中率）：需求没给这一档的阈值，先量清楚再说。</summary>
    private static void Print(Read r, string gesture, int runs)
    {
        Console.WriteLine();
        Console.WriteLine($"─── {gesture} 第 {r.Run}/{runs} 趟 · 通路自证：{r.ProofText} ───");
        Console.WriteLine($"手势次数        = {r.Steps} · 预览帧数 = {r.PreviewFrames} · Paint = {r.Paints} · 帧样本 = {r.Samples}" +
                          $"（每 100 次手势折 {r.PreviewFrames * 100.0 / Math.Max(1, r.Steps):F0} 帧，只报数）");
        Console.WriteLine($"途中帧耗时      = {r.GestureStats}");
        Console.WriteLine($"（产品整池读数含停手补帧：平均 {r.Avg:F1} / P50 {r.P50:F1} / P95 {r.P95:F1} / 最慢 {r.Max:F1} ms" +
                          $"；停手补帧 {r.SettleFrames} 帧共 {r.SettleMs:F1}ms，不计进上面那一池）");
        Console.WriteLine($"逐帧样本（毫秒，*=这一帧里发生过回收；末尾含哨兵移动那一帧）= {r.SamplesText}");
        Console.WriteLine($"RenderWhole     = {r.WholeRuns} 次 · 完整 CanvasLayout.Build = {r.LayoutBuilds} 次 · RenderTile = {r.TileRenders}");
        Console.WriteLine($"整块清瓦片缓存  = {r.Clears} 次（这一档只报数：需求五那条「= 0」写的是鼠标移动通路）");
        Console.WriteLine($"单帧预览重算    = {r.PreviewRecomputes} 次 共 {r.PreviewRecomputeMs:F1}ms · 导出 = {r.Exports} 次 · 命中率 {r.CacheHit:P0}");
        Console.WriteLine($"GC 次数         = gen0 {r.GcGen0}、gen2 {r.GcGen2} · 队列抽干延迟（哨兵）{r.AliveLatencyMs:F0}ms");
        Console.WriteLine($"墙钟 {r.WallMs:F0}ms / 进程 CPU（所有线程合计）{r.CpuMs:F0}ms");
        Console.WriteLine("                    这一趟起点＝冷缓存（ClearTileCacheForTest）：量的就是第一次滚进没见过的区域");
        Console.WriteLine(r.SumCpuMs >= 0
            ? $"线程与调度      = {r.SchedThreads} 条线程 · 本趟全部线程 CPU 求和增加 {r.SumCpuMs:F0}ms、" +
              $"最忙那条单条增加 {r.ThreadCpuMs:F0}ms（求和那笔进反证格，最忙那条只作归因）"
            : "线程与调度      = 没读到（这台机器拿不到线程快照）——这一笔按未量记账，不写成 0");
        Console.WriteLine($"最慢那帧叠层拆账= {r.SlowestOverlay}");
        Console.WriteLine($"最慢那帧分阶段  = {r.SlowestStages}");
        // 两笔"多出来的帧"必须分开打，不许混成一个数：
        //   追加要帧＝每帧瓦片预算停手后当场要的那一帧（预算机制的产物）；
        //   补帧计时器累计响＝停手 120ms 后产品自己补的高质量那一帧（重采样档的产物）。
        // 后者偶尔会读到 2 次：那多半是<b>工装自己</b>两格滚轮之间隔了 >120ms（帧渲染挤占了注入节奏），
        // 于是计时器在中途响了一次、最后一格之后又响一次——真人的滚轮不会这么慢。
        // 所以判据钉的是"停手之后补了几帧"（SettleFrames ≤ 1），这一行只作读数、不进阈值。
        Console.WriteLine($"瓦片限流        = 预算 {r.TileBudgetMs:F1}ms/帧 · 本趟追加要帧 {r.DeferredRepaints} 次 · " +
                          $"最后一帧欠 {r.TilesDeferredLastFrame} 块（欠帧必须靠下一帧补上，判据在电池里）" +
                          $" · 补帧计时器累计响 {r.SettlePaints} 次、此刻{(r.Armed ? "还挂着" : "已停")}");
        if (r.Hud.Frames > 0)
        {
            Console.WriteLine($"HUD 单独耗时    = 均 {r.Hud.HudAvg:F1} / P50 {r.Hud.HudP50:F1} / P95 {r.Hud.HudP95:F1} / " +
                              $"最慢 {r.Hud.HudMax:F1} ms（样本 {r.Hud.Frames} 帧）· 普通画布绘制 均 {r.Hud.CanvasAvg:F1}ms");
        }
    }

    /// <summary>
    /// 逐趟判。阈值只有四类，每一类都能指回需求原话或通路自证：
    /// <list type="bullet">
    ///   <item>不调用 RenderWhole、不跑导出（需求五"预览通路不许走导出／整块合成"）；</item>
    ///   <item><b>完整布局重算 = 0</b>——这一条比拖动通道更严：拖动预览每帧重算一张临时布局是有原因的
    ///         （对齐量变了），而滚轮和缩放只动视口、文档一个字节都没改，一次都不该重算；</item>
    ///   <item>单帧 ≤ 50ms（逐趟最严判）与合并帧池 P95 ≤ 33ms（需求五给的两个数）；</item>
    ///   <item>确实画过东西（Paint、帧样本都 &gt; 0），否则上面全是空判据。</item>
    /// </list>
    /// 开着读数层时再加一条：面板重建不超过 10Hz 预算（需求三第 2/4 条在这一档同样成立——
    /// 滚轮是"帧耗时一直变"的手势，正好是限流闸门最该起作用的地方）。
    /// </summary>
    private static int Judge(Read r, List<double> pool, bool hud)
    {
        int failed = 0;
        Console.WriteLine("  本趟判据：");
        failed += GuiPerfHarness.Check(ref failed, "不调用 RenderWhole", r.WholeRuns == 0, $"{r.WholeRuns} 次");
        failed += GuiPerfHarness.Check(ref failed, "不跑导出", r.Exports == 0, $"{r.Exports} 次");
        failed += GuiPerfHarness.Check(ref failed, "纯显示手势一帧都不重算完整布局", r.LayoutBuilds == 0,
            $"{r.LayoutBuilds} 次（滚轮／缩放只动视口，文档没改）");
        // 判"途中"那几帧的最慢值（r.Max 是产品整池读数，含停手补帧，不能拿它判这一条）。
        double gestureMax = r.Frames.Length == 0 ? 0 : r.Frames.Max();
        failed += GuiPerfHarness.Check(ref failed, "单帧 ≤ 50ms（只算途中帧）", gestureMax <= 50.0,
            $"本趟途中最慢 {gestureMax:F1}ms；停手补帧 {r.SettleFrames} 帧共 {r.SettleMs:F1}ms 另计");
        // 一次手势只该补一帧：补两帧以上就是机制没"重启"（每格各排一次，等于没省）。
        failed += GuiPerfHarness.Check(ref failed, "停手补帧每趟 ≤ 1 帧", r.SettleFrames <= 1,
            $"本趟补了 {r.SettleFrames} 帧（计时器没随每次滚轮重启就会 > 1）");
        failed += GuiPerfHarness.Check(ref failed, "确实画过东西（Paint>0 且有帧样本）",
            r.Paints > 0 && r.Samples > 0, $"Paint {r.Paints} 次、样本 {r.Samples} 帧");
        if (pool.Count > 0)
        {
            pool.Sort();
            double p95 = GuiPerfHarness.Percentile(pool, 0.95);
            failed += GuiPerfHarness.Check(ref failed, "本通道合并帧池 P95 ≤ 33ms", p95 <= 33.0,
                $"池 {pool.Count} 帧 P95={p95:F1}ms（逐趟 P95 最大 {r.P95:F1}ms）");
        }
        if (hud)
        {
            long budget = 1 + (long)r.WallMs / 100;
            failed += GuiPerfHarness.Check(ref failed, "面板重建不超过 10Hz 预算",
                r.HudPanelRebuilds <= budget,
                $"本趟重建 {r.HudPanelRebuilds} 次、预算 {budget} 次（墙钟 {r.WallMs:F0}ms ÷ 100）");
        }
        return failed;
    }
}
