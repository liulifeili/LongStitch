using System.Text;

namespace LongStitch.Checks;

/// <summary>
/// 电池工装的最小运行器。
///
/// 为什么不用测试框架（详见 csproj 注释）：本项目要交付的判据是"字节级自证 + 变异守卫"，
/// 必须能在拉不到 NuGet 包的环境里，用一条 dotnet run 跑出逐格结果与退出码。
///
/// 三条硬规矩：
///   1. 每格要么 pass 要么 fail，没有"跳过算通过"。跳过必须显式写成 skip 并计入末尾汇总，
///      绝不让"没跑的检查"被省略成"看起来跑过了"。
///   2. 断言要连着写判据：失败消息里必须说清"量的是什么、期望什么、实际什么"。
///   3. 反向验证：凡是"能抓到 bug 的断言"，都配一格 guard.*，故意注入缺陷并要求它必须变红。
///      守卫自己坏掉时会绿灯，这是这类工装最危险的失效模式。
/// </summary>
public static class Battery
{
    /// <summary>一格检查。Group 用于 --only 过滤与末尾分类汇总。</summary>
    public sealed record Case(string Group, string Name, Action Run);

    private static readonly List<Case> _cases = new();

    /// <summary>当前正在跑的用例名，供 <see cref="Check"/> 组装失败消息。</summary>
    [ThreadStatic] private static string? _current;

    public static void Add(string group, string name, Action run) =>
        _cases.Add(new Case(group, name, run));

    /// <summary>跳过一格（例如本机缺少某种格式解码器）。会显式出现在汇总里，不算通过。</summary>
    public static void Skip(string group, string name, string why) =>
        _cases.Add(new Case(group, name, () => throw new Skipped(why)));

    public sealed class Skipped : Exception
    {
        public Skipped(string why) : base(why) { }
    }

    public static int Run(string[] args)
    {
        string? filter = ParseFilter(args);
        int passed = 0, failed = 0, skipped = 0;
        var failures = new List<string>();
        var groups = new SortedDictionary<string, (int Ok, int Bad, int Skip)>();

        foreach (var c in _cases)
        {
            if (filter is not null &&
                !c.Group.Contains(filter, StringComparison.OrdinalIgnoreCase) &&
                !c.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                continue;

            _current = $"{c.Group} / {c.Name}";
            var console = Console.Out;
            var buffer = new StringWriter();
            Console.SetOut(buffer);
            try
            {
                c.Run();
                Console.SetOut(console);
                passed++;
                Bump(groups, c.Group, 0);
                Line($"  \u2713 {c.Name}", ConsoleColor.Green);
                string log = buffer.ToString();
                if (log.Length > 0) Console.Write(Indent(log));
            }
            catch (Skipped s)
            {
                Console.SetOut(console);
                skipped++;
                Bump(groups, c.Group, 2);
                Line($"  ○ {c.Name} —— 跳过：{s.Message}", ConsoleColor.DarkYellow);
            }
            catch (Exception ex)
            {
                Console.SetOut(console);
                failed++;
                Bump(groups, c.Group, 1);
                Line($"  ✗ {c.Name}", ConsoleColor.Red);
                Console.WriteLine(Indent(buffer.ToString()));
                Console.WriteLine($"      原因：{ex.Message.Split('\n')[0]}");
                // 只给类型与栈顶一帧：光有消息常常定位不到，而完整栈会把逐格输出淹掉。
                // 需要全栈时用 --only 单跑这一格再临时打印 ex.ToString()。
                Console.WriteLine($"      位置：{ex.GetType().Name} @ {TopFrame(ex)}");
                failures.Add($"{c.Group} / {c.Name}：{ex.Message}");
            }
        }
        _current = null;

        Console.WriteLine();
        Console.WriteLine($"分组汇总：");
        foreach (var (g, v) in groups)
            Console.WriteLine($"  {g,-22} 通过 {v.Ok,3}  失败 {v.Bad,3}  跳过 {v.Skip,3}");
        Console.WriteLine();
        Console.WriteLine($"总计：通过 {passed}，失败 {failed}，跳过 {skipped}");
        if (failures.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("失败清单：");
            foreach (string f in failures) Console.WriteLine("  · " + f);
        }
        return failed == 0 ? 0 : 1;
    }

    private static void Bump(SortedDictionary<string, (int, int, int)> d, string g, int which)
    {
        d.TryGetValue(g, out var v);
        d[g] = which switch { 0 => (v.Item1 + 1, v.Item2, v.Item3),
                             1 => (v.Item1, v.Item2 + 1, v.Item3),
                             _ => (v.Item1, v.Item2, v.Item3 + 1) };
    }

    private static string? ParseFilter(string[] args)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == "--only") return args[i + 1];
        return null;
    }

    private static string Indent(string s)
    {
        var sb = new StringBuilder();
        foreach (string line in s.Split('\n'))
            if (line.Trim().Length > 0) sb.Append("      | ").AppendLine(line.TrimEnd('\r'));
        return sb.ToString();
    }

    /// <summary>取异常栈上第一个"业务帧"（跳过 Battery 自己的断言方法）。</summary>
    private static string TopFrame(Exception ex)
    {
        try
        {
            var frames = new System.Diagnostics.StackTrace(ex, true).GetFrames();
            if (frames is null) return "(无栈)";
            foreach (var f in frames)
            {
                var m = f.GetMethod();
                string decl = m?.DeclaringType?.Name ?? "?";
                if (decl is nameof(Battery) or "<>c" or "RuntimeMethodHandle" or "Math" or "ThrowHelper") continue;
                return $"{decl}.{m?.Name}:{f.GetFileLineNumber()}";
            }
            return "(无业务栈)";
        }
        catch
        {
            return "(栈不可读)";
        }
    }

    private static void Line(string text, ConsoleColor color)
    {
        var old = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.WriteLine(text);
        Console.ForegroundColor = old;
    }

    /// <summary>打印证据（会随用例通过一起展示，便于人工复核判据是否真的量到了东西）。</summary>
    public static void Evidence(string text) => Console.WriteLine("      · " + text);

    // ───────────────────────── 断言 ─────────────────────────

    public static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception($"[{_current}] {message}");
    }

    public static void AssertEqual<T>(T expected, T actual, string what)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception($"[{_current}] {what}：期望 {Short(expected)}，实际 {Short(actual)}");
    }

    /// <summary>
    /// 比较长文本（JSON、文件内容），失败时只报<b>第一处不同的行</b>。
    /// 直接把两份完整 JSON 塞进失败消息等于没写——一屏刷不完，人也看不出差别在哪。
    /// </summary>
    public static void AssertTextEqual(string expected, string actual, string what)
    {
        if (expected == actual) return;

        string[] a = expected.Split('\n');
        string[] b = actual.Split('\n');
        for (int i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            string la = i < a.Length ? a[i].TrimEnd('\r') : "(缺行)";
            string lb = i < b.Length ? b[i].TrimEnd('\r') : "(缺行)";
            if (la != lb)
            {
                // 多带两行上下文：只给第一处不同往往看不出是"少了一个字段"还是"顺序变了"
                var ctx = new StringBuilder();
                for (int k = Math.Max(0, i - 2); k <= Math.Min(a.Length - 1, i + 2); k++)
                    ctx.Append($"\n          {k + 1,3} 期望| {Short(a[k])}");
                for (int k = Math.Max(0, i - 2); k <= Math.Min(b.Length - 1, i + 2); k++)
                    ctx.Append($"\n          {k + 1,3} 实际| {Short(b[k])}");
                throw new Exception($"[{_current}] {what}：第 {i + 1} 行起不同{ctx}");
            }
        }
        throw new Exception($"[{_current}] {what}：逐行相同但整体不同（换行符差异？）");
    }

    /// <summary>失败消息里的值一律截断，避免超长内容把判据本身埋掉。</summary>
    private static string Short(object? value)
    {
        string s = value?.ToString() ?? "null";
        s = s.Replace("\n", "⏎").Replace("\r", "");
        return s.Length <= 160 ? s : s[..120] + $"…（共 {s.Length} 字，已截断）";
    }

    public static void AssertBytesEqual(ReadOnlySpan<byte> expected, ReadOnlySpan<byte> actual, string what)
    {
        if (expected.Length != actual.Length)
            throw new Exception($"[{_current}] {what}：长度不同，期望 {expected.Length} 字节，实际 {actual.Length} 字节");
        for (int i = 0; i < expected.Length; i++)
        {
            if (expected[i] != actual[i])
            {
                int row = i / 4, col = i % 4;
                throw new Exception(
                    $"[{_current}] {what}：第 {i} 字节不同（约第 {row} 像素、通道 {col}），" +
                    $"期望 0x{expected[i]:X2}，实际 0x{actual[i]:X2}");
            }
        }
    }

    /// <summary>断言某段代码必须抛异常；返回异常消息供进一步核对。</summary>
    public static string AssertThrows(Action run, string what)
    {
        try
        {
            run();
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
        throw new Exception($"[{_current}] {what}：期望抛出异常，实际正常返回（守卫失效）");
    }
}
