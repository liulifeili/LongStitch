using System.Diagnostics;

namespace LongStitch.App.Diagnostics;

/// <summary>
/// 需求五那句"偶发 80–150ms 时记录 GC 与<b>进程调度信息</b>"里，调度这一半的<b>实量</b>出处。
///
/// ═══ 为什么过去只能推算 ═══
/// 诊断出口早就有 GC 计数、撞上回收的帧数、UI 线程累计分配、进程 CPU 用户/总时间，
/// 但"这一帧到底是慢在自己算，还是慢在被系统切走"这件事，过去只有一句推论：
/// "墙钟减去 CPU 时间差出去的那截就是被抢占的"。它没错，可它不是<b>量</b>出来的，
/// 而且那个差里混着后台解码线程的时间，本身就分不清是"谁"在等。
///
/// ═══ 这一平台到底能给什么（先探过才敢写）═══
/// .NET 8 的 <see cref="ProcessThread"/> 在 Windows 上给：<c>UserProcessorTime</c>、
/// <c>PrivilegedProcessorTime</c>、<c>TotalProcessorTime</c>、<c>ThreadState</c>、<c>WaitReason</c>；
/// <b>不给</b> <c>ContextSwitchCount</c>（.NET Framework 的 Windows-only 成员，没移植过来）。
/// 要切换次数只能装性能计数器包，而本项目零 NuGet 依赖 ⇒ 那一笔在出口里明写成"按未量记"，
/// <b>不许把一个拿不到的数写成 0 交出去</b>（"恒为 0 的字段"与"这趟真的没发生"在屏上长得一模一样，
/// 是本项目反复栽的那一族）。
///
/// ═══ 为什么一次报两笔 CPU，而不是一笔 ═══
/// ①「全部线程合计」＝逐条相加。它的价值是<b>会跟着动</b>：任何一条线程烧了 CPU 它都涨，
///   所以它能当"这一笔到底是不是从操作系统读来的"的正向对照（反证格就这么用它）。
/// ②「最忙那条」才是拖动归因要看的那条（在只画一个窗口的进程里通常就是 UI 线程）。
///   但它<b>不适合</b>做正向对照——本批真红过一次：在一条本来就排不上第一的线程上空转 40 毫秒，
///   "最忙那条"的合计当然一动不动。两笔各管各的事，别互相顶替。
///
/// ═══ 为什么不点名"UI 线程" ═══
/// .NET 不公开"托管当前线程"对应的 Win32 线程 id（<c>Environment.CurrentManagedThreadId</c> 是托管编号，
/// 与 <see cref="ProcessThread.Id"/> 不同源）。要精确点名就得引一条 kernel32 声明，
/// 而这个仓库至今<b>零 P/Invoke</b>——为了一笔诊断读数不开这个口子。
/// 所以措辞是"最忙那条"，并注明它在拖动时通常就是 UI 线程：措辞不越过证据。
///
/// ═══ 只在诊断出口调用，一帧都不许读 ═══
/// 每次取快照都要枚举线程、每项都会分配，放进 OnPaint 就成了"读数层自己变贵"那一族老毛病（需求三）。
/// 调用点只有 <c>DescribeDiagnostics()</c> 与 <c>--gui</c> 工装的结清处。
/// </summary>
internal static class ThreadScheduling
{
    /// <summary>一次调度快照。字段就是出口那一行要印的那几枚数，不做二次加工。</summary>
    internal readonly struct Snapshot
    {
        /// <summary>取快照那一刻进程里的线程条数。</summary>
        public int Threads { get; init; }

        /// <summary>此刻处于 Wait 状态的线程条数（等消息、等锁、等睡眠都算，细分在等待原因里）。</summary>
        public int Waiting { get; init; }

        /// <summary>全部线程的用户态 CPU 之和（毫秒，进程累计）。</summary>
        public double SumUserMs { get; init; }

        /// <summary>全部线程的内核态 CPU 之和（毫秒，进程累计）。</summary>
        public double SumKernelMs { get; init; }

        /// <summary>①全部线程的用户＋内核 CPU 之和（毫秒）——任何一条线程烧 CPU 它都涨。</summary>
        public double SumTotalMs { get; init; }

        /// <summary>②累计 CPU 最多的那条线程自己的用户态时间（毫秒）。</summary>
        public double BusyUserMs { get; init; }

        /// <summary>②那条线程的内核态时间（毫秒）。</summary>
        public double BusyKernelMs { get; init; }

        /// <summary>②那条线程的用户＋内核时间（毫秒）——它明显小于墙钟就是在等，不是在算。</summary>
        public double BusyTotalMs { get; init; }

        /// <summary>②那条线程取读数那一刻的状态与等待原因（例如 <c>Wait/UserRequest</c>＝在等消息）。
        /// 声明成可空、由 <see cref="Take"/> 一定赋值：带初始值设定的 struct 必须有显式构造函数（CS8983）。</summary>
        public string? BusyState { get; init; }

        /// <summary>拿不到（没有桌面会话、线程全部拿不到时间等）时的空快照：宁可少一笔，也不报一个写死的 0。</summary>
        public bool Available => Threads > 0;
    }

    /// <summary>取一次调度快照。中途有线程退出就跳过那一条，不让整笔作废。</summary>
    internal static Snapshot Take()
    {
        try
        {
            using Process proc = Process.GetCurrentProcess();
            int threads = proc.Threads.Count;
            int waiting = 0;
            double sumUser = 0, sumKernel = 0, sumTotal = 0;
            double best = -1;
            double busyUserMs = 0, busyKernelMs = 0, busyTotalMs = 0;
            string? busyState = null;
            foreach (ProcessThread t in proc.Threads)
            {
                TimeSpan user, priv, total;
                System.Diagnostics.ThreadState state;
                try
                {
                    user = t.UserProcessorTime;
                    priv = t.PrivilegedProcessorTime;
                    total = t.TotalProcessorTime;
                    // 枚举属性本身不会抛；下面的 WaitReason 才会，所以状态先取出来备用。
                    state = t.ThreadState;
                }
                catch (Exception) { continue; }        // 线程刚退出：跳过那一条
                sumUser += user.TotalMilliseconds;
                sumKernel += priv.TotalMilliseconds;
                sumTotal += total.TotalMilliseconds;
                if (state == System.Diagnostics.ThreadState.Wait) waiting++;
                if (total.TotalMilliseconds <= best) continue;
                best = total.TotalMilliseconds;
                busyUserMs = user.TotalMilliseconds;
                busyKernelMs = priv.TotalMilliseconds;
                busyTotalMs = total.TotalMilliseconds;
                // WaitReason 只有在线程确实处于 Wait 状态时才可读，否则抛异常——所以判了状态再读。
                busyState = state == System.Diagnostics.ThreadState.Wait
                    ? $"Wait/{t.WaitReason}"
                    : state.ToString();
            }
            return new Snapshot
            {
                Threads = threads,
                Waiting = waiting,
                SumUserMs = sumUser,
                SumKernelMs = sumKernel,
                SumTotalMs = sumTotal,
                BusyUserMs = busyUserMs,
                BusyKernelMs = busyKernelMs,
                BusyTotalMs = busyTotalMs,
                BusyState = busyState,
            };
        }
        catch (Exception)
        {
            return new Snapshot();
        }
    }

    /// <summary>
    /// 出口那一行。<paramref name="runTotalMs"/> 只在工装里给（那一趟的墙钟），
    /// 产品出口没有"这一趟"的概念，所以给累计口径。
    /// 两句都必须把"等"与"在算"怎么分说清楚，也不许把拿不到的那笔写成 0。
    /// </summary>
    internal static string Describe(Snapshot s, double runTotalMs = 0)
    {
        if (!s.Available)
            return "线程与调度：没读到（这台机器拿不到线程快照）——这一笔属未量，不写成 0";
        string against = runTotalMs > 0
            ? $"／本趟墙钟 {runTotalMs:F0}ms ⇒ 差出去的那截是在等，不是这里在算"
            : "（累计口径；要分「等还是算」请对着本趟墙钟看）";
        return $"线程与调度：{s.Threads} 条线程（此刻 {s.Waiting} 条在等）· " +
               $"全部线程 用户 {s.SumUserMs:F0}／内核 {s.SumKernelMs:F0} 求和 {s.SumTotalMs:F0}ms · " +
               $"最忙那条 用户 {s.BusyUserMs:F0}／内核 {s.BusyKernelMs:F0} 单条 {s.BusyTotalMs:F0}ms" +
               $"（拖动时它通常就是 UI 线程）· 那一刻状态 {(s.BusyState ?? "未知")}{against}" +
               " · 上下文切换次数：.NET 8 的 ProcessThread 没有这个成员，这一笔按未量记";
    }
}
