using System.Diagnostics;
using LongStitch.App.Imaging;
using LongStitch.App.UI;
using LongStitch.Core;

namespace LongStitch.App.Diagnostics;

/// <summary>
/// 内存快照出口（第五十九改第一阶段：只加读数，不改任何行为）。
///
/// ═══ 为什么要有这一层 ═══
/// 用户报"导入的 PNG 一多，内存占用巨量增高"（任务管理器专用工作集 2.41 GB）。
/// 静态盘点给出的理论上限是：源图 LRU 512MB ＋ 瓦片缓存 192MB ＋ GDI 瓦片位图 ≤48MB ≈ 752MB，
/// 与 2.41GB 差着三倍多——差额只能来自瞬时大分配（导入整幅解码、匹配整段 SubImage、
/// 缩略图先做全尺寸 GDI 位图）落在大对象堆后 GC 不回缩工作集，或某处缓存没按设想淘汰。
/// 猜归猜，<b>量出来才算数</b>：这一层把两边（长期驻留 ＋ 过程量）各接一把尺，
/// 每次导入、分析、滚动、导出、关闭/打开项目各记一行，下一阶段动刀前后各读一遍。
///
/// ═══ 读数分两组 ═══
/// ① 长期驻留（各缓存自报）：SourceCache（源图 LRU）、TileCache（显示瓦片）、
///    TileBitmap（GDI 位图缓存）、Thumbnail（列表缩略图）——它们都是"可丢弃副本"，
///    数值直接来自缓存自己的计数器，不许界面猜。
/// ② 过程/进程量：托管堆、各代 GC 次数、LOH 大小、Private/WorkingSet、
///    全句柄数、解码并发峰值（数调用次数，不是缓冲区数量）、匹配临时副本累计分配量。
///
/// ═══ ProcessHandles 为什么替代不了 GdiHandleCount ═══
/// 用户方案里点名的"真 GDI 句柄数"要用 <c>GetGuiResources</c> 那种 Win32 调用取，
/// 而本产品的红线是<b>产品内不得 P/Invoke</b>。这里记的 <c>processHandles</c> 是
/// <b>进程全部句柄数（HandleCount），不是 GDI 对象数</b>，GDI 位图泄不泄漏
/// <b>不能</b>从它读出来。GDI 对象数要单独统计：那条路在本产品里开不了
/// （红线禁 P/Invoke），逐字节级精确的 GDI 句柄审计留给 Checks 工装那一侧
/// （那里允许 P/Invoke），本批未做、如实记账。GDI 位图那一头先靠我们自己创建、
/// 自己记账的 <c>tileBitmapCount/thumbnailCount</c>（精确）来盯；判"旧的缩略图真的被释放了"
/// 另有 SourcePanel 的 <c>ThumbsDisposedForTest</c> 计数器（第五十九改②）。
///
/// ═══ 两个名字里的"陷阱"（第五十九改②口径修正） ═══
/// <c>matchTemporaryBytesTotal</c> 是进程启动以来匹配输入副本的<b>累计分配量</b>——
/// 同一块内存分配了又回收会重复计数，它<b>不是</b>当前驻留，不能拿它解释
/// "当前 2.41GB 里占了多少"；当前驻留要看 sourceCache/tileCache 两笔。
/// <c>decodePeakInFlight</c> 是解码并发<b>峰值</b>（快照记在操作结束后，瞬时值恒为 0，
/// 峰值至少能答"解码有没有并发"）；它数的是调用次数，不是缓冲区数量或字节数。
///
/// ═══ 隐私红线 ═══
/// 只写字节数、计数与阶段名——与 <see cref="DiagnosticLog"/> 同一条规矩：
/// 日志里永远不许出现像素内容或路径之外的隐私数据。
/// </summary>
public static class MemorySnapshot
{
    private static FileSourceResolver? _resolver;
    private static StitchCanvasControl? _canvas;
    private static SourcePanel? _sourcePanel;

    /// <summary>
    /// 把三个读数来源挂进来（主窗口构造完三件套之后调一次）。
    /// 为什么走静态而不是构造传参：快照的触发点分散在主窗口、画布、导出对话框三处，
    /// 三处各自拿全这三样引用会把接线翻三倍；静态注册一处挂、退出时摘。
    /// </summary>
    public static void Attach(FileSourceResolver resolver, StitchCanvasControl canvas, SourcePanel panel)
    {
        _resolver = resolver;
        _canvas = canvas;
        _sourcePanel = panel;
    }

    /// <summary>
    /// 主窗口释放时必须摘干净：静态引用会把整个窗口（连同它钉住的全部位图）
    /// 钉在托管堆上——主窗口 Dispose 的注释里已经踩过一模一样的坑。
    /// </summary>
    public static void Detach()
    {
        _resolver = null;
        _canvas = null;
        _sourcePanel = null;
    }

    /// <summary>
    /// 记一行内存快照。<paramref name="stage"/> 取值：import / analyze_all / analyze_one /
    /// scroll / export / project_close / project_open / exit。
    /// 没挂过 <see cref="Attach"/> 时是安静的空操作（离屏工装没建全三件套的场景不该炸）。
    /// </summary>
    public static void Log(string stage)
    {
        if (_resolver is null) return;
        try
        {
            DiagnosticLog.Info("mem.snapshot", BuildDetail(stage));
        }
        catch
        {
            // 诊断不许变成产品失败——与 DiagnosticLog 同一条纪律。
        }
    }

    private static int _releaseCount;
    /// <summary>释放回收已触发的次数（电池判据的取数口；与 mem.release 日志行一对一）。</summary>
    public static int ReleaseCountForTest => System.Threading.Volatile.Read(ref _releaseCount);

    /// <summary>
    /// 操作收尾的强制回收钩子（第五十九改③）。
    ///
    /// ═══ 为什么必须有这一步 ═══
    /// 真窗口账目（mem.snapshot，44 张源图会话）：缓存驻留合计约 542MB
    /// （源图 LRU 530MB ＋ 瓦片 12MB，都在设计预算内），但托管堆 1.53GB、其中 LOH 1.42GB——
    /// 约 1GB 是<b>已经死掉却没人催收</b>的大对象（匹配整段 SubInput 临时副本、解码中间缓冲，
    /// matchTemporaryBytesTotal 累计 2.41GB 就是它们的来路）。gen2 才 36 次，
    /// .NET 对 LOH 从不主动催——用户在任务管理器里看到的工作集 1.94GB 就是这笔账。
    ///
    /// ═══ 在哪些时刻触发 ═══
    /// "用户确认完毕"的四个收尾点：批量分析写回后、单缝确认写回后、导出结束后、
    /// 新建/打开项目清理缓存后。判据
    /// <c>ui/批量分析确认写回后必须触发一次释放回收</c> 钉其中分析这一线。
    ///
    /// ═══ 为什么 GC 放在后台线程 ═══
    /// 全额阻塞回收在大堆上要几百毫秒，落在 UI 线程上就是一次肉眼可见的卡顿——
    /// 与"读数不许成为卡顿来源"同一纪律。freedBytes 可为负（回收期间别的线程又分配了），
    /// 那是方向读数不是缺陷。
    /// </summary>
    public static void ReleaseAfterOperation(string stage)
    {
        long beforeBytes = GC.GetTotalMemory(forceFullCollection: false);
        System.Threading.Interlocked.Increment(ref _releaseCount);
        System.Threading.ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                GC.Collect(2, GCCollectionMode.Forced, blocking: true);
                long afterBytes = GC.GetTotalMemory(forceFullCollection: false);
                DiagnosticLog.Info("mem.release",
                    $"stage={stage} beforeBytes={beforeBytes} afterBytes={afterBytes} freedBytes={beforeBytes - afterBytes}");
            }
            catch
            {
                // 诊断不许变成产品失败——与 Log 同一条纪律。
            }
        });
    }

    private static string BuildDetail(string stage)
    {
        Process self = Process.GetCurrentProcess();
        self.Refresh();

        GCMemoryInfo gc = GC.GetGCMemoryInfo();
        // GenerationInfo 的槽位顺序固定：0=Gen0、1=Gen1、2=Gen2、3=LOH、4=POH（.NET 5+）。
        // 防御式取第 3 槽：运行时要是改了槽位语义，宁可记 -1 也不能记错数。
        long lohBytes = gc.GenerationInfo.Length > 3 ? gc.GenerationInfo[3].SizeAfterBytes : -1;

        return
            $"stage={stage}" +
            $" sourceCacheBytes={_resolver!.CachedBytes} sourceCacheCount={_resolver.CachedCount}" +
            $" tileCacheBytes={_canvas!.TileCacheBytesForTest} tileCacheCount={_canvas.TileCacheCountForTest}" +
            $" tileBitmapCount={_canvas.TileBitmapCountForTest} tileBitmapBytes={_canvas.TileBitmapBytesForTest}" +
            $" thumbnailCount={_sourcePanel!.ThumbnailCountForTest}" +
            $" managedHeapBytes={GC.GetTotalMemory(forceFullCollection: false)}" +
            $" privateBytes={self.PrivateMemorySize64} workingSetBytes={self.WorkingSet64}" +
            $" gen0={GC.CollectionCount(0)} gen1={GC.CollectionCount(1)} gen2={GC.CollectionCount(2)}" +
            $" lohBytes={lohBytes}" +
            $" processHandles={self.HandleCount}" +
            $" decodePeakInFlight={FileSourceResolver.DecodePeakInFlight}" +
            $" matchTemporaryBytesTotal={CanvasComposer.MatchInputBytesTotalForTest}";
    }
}
