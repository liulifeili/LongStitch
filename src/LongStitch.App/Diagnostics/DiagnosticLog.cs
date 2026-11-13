using System.Diagnostics;
using System.Text;

namespace LongStitch.App.Diagnostics;

/// <summary>
/// 一次会话一份 UTF-8 日志。记诊断绝不能把产品弄失败——这是第十一轮改的重点之一。
///
/// ═══ 为什么要改成"一个开着的写句柄 + 批量刷" ═══
/// 原来每一行都 <c>File.AppendAllText</c>：一次打开、一次关闭。界面每帧写一条的话，
/// 拖一次拼缝就是几十次文件开关，滚轮一格两行也是两次——用户报的"画布非常卡顿、
/// 滚轮不流畅"里就有这一份账（另一份在瓦片缓存与面板重建上）。
///
/// 现在：
/// · 行先进 <see cref="StreamWriter"/> 缓冲，<b>攒够一批或遇到关键事件才落盘</b>；
/// · <c>*.preview</c> 这类高频事件不单独刷盘（一次拖动的头尾各刷一次就够复盘用了）；
/// · commit / cancel / error / 退出 一律当场刷，出事时不会缺最后几行。
///
/// 只写数字、坐标、Id 与状态——日志里永远不许出现像素内容或隐私数据（精度红线）。
/// </summary>
public static class DiagnosticLog
{
    private static readonly object Sync = new();
    private static readonly UTF8Encoding Utf8NoBom = new(false);
    private static string? _filePath;
    private static StreamWriter? _writer;
    private static int _buffered;

    /// <summary>
    /// 累计<b>要求</b>写入的行数。判据读这个数，而不是去数磁盘上的行：
    /// 工装跑电池时压根没开日志文件，数磁盘会得到一个恒等于 0 的假绿。
    /// </summary>
    public static long AppendRequests { get; private set; }

    /// <summary>真正刷盘的次数（批量写生效的证据：它远小于 <see cref="AppendRequests"/>）。</summary>
    public static long Flushes { get; private set; }

    /// <summary>攒够这么多行自动刷一次盘。关键事件不等这个数，当场刷。</summary>
    public const int FlushEveryBufferedLines = 64;

    public static string DirectoryPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LongStitch", "logs");

    public static string? FilePath => _filePath;

    public static void Start()
    {
        lock (Sync)
        {
            try
            {
                Directory.CreateDirectory(DirectoryPath);
                _filePath = Path.Combine(DirectoryPath,
                    $"longstitch-{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}-{Guid.NewGuid():N}.log");
                _writer = new StreamWriter(_filePath, append: false, Utf8NoBom) { AutoFlush = false };
                WriteCore("INFO", "app.start",
                    $"version={typeof(DiagnosticLog).Assembly.GetName().Version} os={Environment.OSVersion} " +
                    $"runtime={Environment.Version} x64={Environment.Is64BitProcess} cwd={Environment.CurrentDirectory}");
                Flush();
            }
            catch
            {
                _filePath = null;
                _writer = null;
            }
        }
    }

    public static void Info(string eventName, string detail) => Write("INFO", eventName, detail);

    public static void Error(string eventName, Exception error, string detail = "") =>
        Write("ERROR", eventName, $"{detail}{Environment.NewLine}{error}");

    public static void OpenDirectory()
    {
        Directory.CreateDirectory(DirectoryPath);
        Process.Start(new ProcessStartInfo { FileName = DirectoryPath, UseShellExecute = true });
    }

    /// <summary>
    /// 把缓冲里的行落盘。会话结束、以及每一次"这之后可能就没有下一条了"的场合都要调。
    /// </summary>
    public static void Flush()
    {
        lock (Sync)
        {
            if (_writer is null || _buffered == 0) return;
            try
            {
                _writer.Flush();
                Flushes++;
            }
            catch { /* 诊断不许变成产品失败 */ }
            _buffered = 0;
        }
    }

    /// <summary>关掉写句柄（退出前调用；调用后仍可继续写，只是落不了盘）。</summary>
    public static void Close()
    {
        lock (Sync)
        {
            try { _writer?.Flush(); _writer?.Dispose(); } catch { /* 同上 */ }
            _writer = null;
            _buffered = 0;
        }
    }

    /// <summary>
    /// 取出本次会话<b>已经落盘</b>的那部分日志（「导出当前交互会话」用它）。
    ///
    /// 为什么要专门写一个读取方法而不用 <c>File.ReadAllText</c>：写句柄此刻还开着，
    /// 而 ReadAllText 内部以 <c>FileShare.Read</c> 打开——那个共享模式表示"别人不许再写"，
    /// 于是"读自己正在写的日志"在 Windows 上直接抛 IOException（提示"正由另一进程使用"）。
    /// 正确写法是 <c>FileShare.ReadWrite</c>：允许写句柄同时存在。
    /// 这个坑由 <c>ui/导出当前交互会话：导出的文件必须含缓冲里最后几行</c> 那一格抓出来——
    /// 不这样写，帮助菜单里那个导出项一点就弹"导出失败"。
    ///
    /// 只返回已在磁盘上的内容：<b>缓冲里没刷的行列不出来</b>。这正是"导出前必须先 Flush"
    /// 能被量出来的原因（调用方忘了刷盘，导出的就是半截日志，判据会红而不是静默成功）。
    /// </summary>
    public static string ReadSessionLog()
    {
        lock (Sync)
        {
            if (_filePath is null || !File.Exists(_filePath)) return "";
            try
            {
                using var fs = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(fs, Utf8NoBom);
                return reader.ReadToEnd();
            }
            catch
            {
                // 诊断读失败不许把"导出"整个弄挂：交回空内容，由调用方在文件里写明这一段没取到。
                return "";
            }
        }
    }

    private static void Write(string level, string eventName, string detail)
    {
        lock (Sync)
        {
            try
            {
                WriteCore(level, eventName, detail);
                // 拖动预览那一族是"每帧都可能来一条"的高频行：只进缓冲，
                // 让一次手势的头（begin）和尾（commit/cancel）各刷一次盘就足够复盘。
                if (!eventName.EndsWith(".preview", StringComparison.Ordinal)) Flush();
                else if (_buffered >= FlushEveryBufferedLines) Flush();
            }
            catch { /* Diagnostics must not become a product failure. */ }
        }
    }

    private static void WriteCore(string level, string eventName, string detail)
    {
        AppendRequests++;
        if (_writer is null) return;
        _writer.Write($"{DateTimeOffset.Now:O} [{level}] pid={Environment.ProcessId} " +
                      $"thread={Environment.CurrentManagedThreadId} {eventName} {detail}{Environment.NewLine}");
        _buffered++;
    }
}
