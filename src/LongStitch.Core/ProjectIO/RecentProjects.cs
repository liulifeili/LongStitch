namespace LongStitch.Core.ProjectIO;

/// <summary>
/// 最近项目列表。模拟 UI 需求 §6 点名要它，并且点名要能<b>清空</b>——
/// 截图工具的最近列表等于一份"我在哪些聊天截图上工作过"的清单，
/// 所以这里只存文件路径，不存标题、不存任何图片内容，也不写进日志。
///
/// 存放位置：<c>%APPDATA%\LongStitch\recent.txt</c>，一行一个路径，最新的在最上面。
/// 纯文本是刻意的：用户想手工删几行随时能删，不需要程序配合。
/// </summary>
public static class RecentProjects
{
    /// <summary>最多记几条。再长就只是把列表变成滚轮练习。</summary>
    public const int Limit = 8;

    /// <summary>
    /// 整进程改写存储位置（null = 用真的 <see cref="StorePath"/>）。
    ///
    /// ═══ 为什么需要这一枚（这一份比偏好那一份更要紧）═══
    /// 本类的注释开头就写了：这份列表等于"我在哪些聊天截图上工作过"的清单，是<b>隐私敏感</b>的。
    /// 而产品那四个调用点（保存／打开／另存／拖入各一处）调的都是 <c>Add(path)</c>——<b>不带路径</b>，
    /// 所以离屏判据只要经产品保存过一次项目，就会往<b>开发者真实的</b>那份 recent.txt 里写一行真路径。
    /// 判据那头没有别的办法拦住它：调用点在产品里，参数是可选的，默认值就是真路径。
    /// 修法是给产品加一枚<b>测试专用接缝</b>，工装进程一启动就把两个存储出口一起改道到沙箱目录，
    /// 产品代码本身一个字都不读它。形状与 <see cref="AppPreferences.StoreOverride"/> 完全一致。
    ///
    /// 判据 <c>ui/离屏工装的两个存储出口都必须改道到沙箱</c> 把这条钉住，
    /// 反证 <c>guard/改道判据的尺子必须能认出真实那份存储位置</c> 证明那把尺子不是恒真。
    /// </summary>
    public static string? StoreOverride { get; set; }

    /// <summary>
    /// 产品级默认存储文件（第六十四改，形状与 AppPreferences.DefaultStore 一致）：
    /// 启动代码发现 exe 旁有 data 文件夹时指过去，没有则保持 null。
    /// 优先级同样是 测试闸（本枚）&gt; 产品默认 &gt; Windows 惯例目录，测试闸永远赢。
    /// </summary>
    public static string? DefaultStore { get; set; }

    /// <summary>存储文件路径（目录不存在时由写入方创建）。优先级：测试闸 &gt; 产品默认（data 文件夹）&gt; Windows 惯例。</summary>
    public static string StorePath => StoreOverride ?? DefaultStore ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "LongStitch", "recent.txt");

    /// <summary>
    /// 读取列表。已经在磁盘上消失的路径会被过滤掉并顺手写回——
    /// 留着它们只会让用户点了之后收到一句"打不开"。
    /// 任何读取失败都退回空列表：最近列表属于便利功能，不许因为它把程序启动搞崩。
    /// </summary>
    public static IReadOnlyList<string> Load(string? overridePath = null)
    {
        string store = overridePath ?? StorePath;
        try
        {
            if (!File.Exists(store)) return Array.Empty<string>();
            var kept = File.ReadAllLines(store)
                .Select(l => l.Trim())
                .Where(l => l.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(Limit)
                .ToList();

            var existing = kept.Where(File.Exists).ToList();
            if (existing.Count != kept.Count)
                WriteUnlocked(existing, store);   // 一次性收敛，下次读取就不用再清理了
            return existing;
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>把 path 记为最近一条（去重、置顶、截断）。</summary>
    public static void Add(string path, string? overridePath = null)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        string store = overridePath ?? StorePath;
        string full = Path.GetFullPath(path);
        var list = Load(store).Where(p => !string.Equals(p, full, StringComparison.OrdinalIgnoreCase)).ToList();
        list.Insert(0, full);
        if (list.Count > Limit) list.RemoveAt(list.Count - 1);
        try { WriteUnlocked(list, store); } catch { /* 写不进去只是下次没有这条记录，不影响本次保存成功 */ }
    }

    /// <summary>清空（需求 §6：允许清空）。文件留着但内容置空，便于用户核对确实清了。</summary>
    public static void Clear(string? overridePath = null)
    {
        try { WriteUnlocked(Array.Empty<string>(), overridePath ?? StorePath); } catch { /* 同上 */ }
    }

    private static void WriteUnlocked(IEnumerable<string> paths, string store)
    {
        string? dir = Path.GetDirectoryName(store);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllLines(store, paths);
    }
}
