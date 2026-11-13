using System.Diagnostics;
using LongStitch.Core.Geometry;
using LongStitch.Core.Imaging;
using LongStitch.Core.Imaging.Png;
using LongStitch.Core.Matching;
using LongStitch.Core.Model;

namespace LongStitch.Checks;

/// <summary>
/// 真实截图探针与<b>自监督标定</b>。
///
/// 用法：
///   dotnet run --project checks/LongStitch.Checks -- --probe     samples/local
///   dotnet run --project checks/LongStitch.Checks -- --calibrate samples/local
///
/// ═══ 为什么需要它 ═══
/// 阈值标定原本要"30–50 组人工标注正确位移的真图"。用户给不了标注，只能给截图。
/// 这里换一个不需要标注的办法：<b>把一张真图按已知位移切成两屏</b>，
/// 正确答案由构造本身决定，于是可以在真实像素（真实中文字形、抗锯齿、照片区、
/// 重复列表项）上量出算法的误差分布。
///
/// ═══ 这个办法测不到什么（必须说清）═══
/// 它测的是「真内容被平移后能不能找回」，测不到「两张分别截取的图之间
/// 因动态内容（时间跳动、正在输入、动画）产生的差异」——那种差异只有真实连拍才有。
/// 所以这里的数字是「算法上限」，不是现场表现。现场表现仍需真机连拍样本。
/// </summary>
public static class RealSamples
{
    public static int Run(string[] args)
    {
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch (IOException) { }

        bool calibrate = args.Length > 0 && args[0] == "--calibrate";
        string dir = args.Length > 1 ? args[1] : "samples/local";
        if (!Directory.Exists(dir))
        {
            Console.WriteLine($"目录不存在：{dir}");
            return 2;
        }

        var files = Directory.EnumerateFiles(dir, "*.png", SearchOption.AllDirectories)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray();
        if (files.Length == 0)
        {
            Console.WriteLine($"{dir} 下没有 PNG");
            return 2;
        }

        Console.WriteLine(calibrate
            ? $"自监督标定：{files.Length} 张真图（正确答案由构造已知，无需人工标注）"
            : $"真图探针：{files.Length} 张");
        Console.WriteLine();

        var decoded = new List<(string File, RgbaImage Image, PngFormatInfo Info)>();
        foreach (string file in files)
        {
            var sw = Stopwatch.StartNew();
            PngDecodeResult result = PngDecoder.Decode(File.ReadAllBytes(file));
            sw.Stop();
            decoded.Add((Path.GetFileName(file), result.Image, result.Info));

            Console.WriteLine($"  {Path.GetFileName(file)}  {result.Image.Width}×{result.Image.Height}");
            Console.WriteLine($"      位深 {result.Info.BitDepth} · colorType {result.Info.ColorType}" +
                              $" · ICC {result.Info.HasIccProfile} · sRGB {result.Info.HasSrgbChunk}" +
                              $" · gAMA {result.Info.HasGamma} · EXIF方向 {result.Info.ExifOrientation}");
            if (result.Info.LossyNotes.Count > 0)
                foreach (string note in result.Info.LossyNotes)
                    Console.WriteLine($"      ⚠ {note}");
            Console.WriteLine($"      解码 {sw.ElapsedMilliseconds} ms");

            // 顺带做一次"编解码往返"核对：真图上验证自研编码器不改动像素
            var sw2 = Stopwatch.StartNew();
            byte[] reEncoded = PngEncoder.EncodeToBytes(result.Image);
            sw2.Stop();
            RgbaImage back = PngDecoder.Decode(reEncoded).Image;
            bool same = back.PixelsRo.SequenceEqual(result.Image.PixelsRo);
            Console.WriteLine($"      往返重编码 {(same ? "字节一致 ✓" : "★ 不一致 ★")}" +
                              $"，{reEncoded.Length / 1024} KB，{sw2.ElapsedMilliseconds} ms");
            if (!same) return 1;
        }

        if (!calibrate) return 0;

        Console.WriteLine();
        Console.WriteLine("── 自监督匹配精度 ──");
        Calibrate(decoded);
        return 0;
    }

    /// <summary>状态栏带的近似高度：这些 iPhone 截图的顶部固定区大约 130 像素。</summary>
    private const int SimulatedFixedBand = 130;

    private static void Calibrate(List<(string File, RgbaImage Image, PngFormatInfo Info)> samples)
    {
        var errors = new List<int>();
        var samples2 = new List<(double Confidence, double Uniqueness, int Error)>();
        int trials = 0, exact = 0, within1 = 0, within3 = 0, wrong = 0;
        var rng = new Random(20261022);   // 固定种子：同一批图每次跑出同一组数字，便于跨轮比较

        foreach ((string file, RgbaImage image, _) in samples)
        {
            int height = image.Height;
            int firstHeight = Math.Min(1400, height / 2);

            for (int t = 0; t < 5; t++)
            {
                // 重叠量在"短重叠(难)"到"长重叠(易)"之间均匀铺开
                int overlap = firstHeight - (int)(firstHeight * 0.25 * t / 4.0) - rng.Next(0, 40);
                overlap = Math.Clamp(overlap, 120, firstHeight - 60);

                int secondTop = firstHeight - overlap;
                if (secondTop + firstHeight > height) continue;

                RgbaImage before = image.SubImage(new IntRect(0, 0, image.Width, firstHeight));
                RgbaImage after = image.SubImage(new IntRect(0, secondTop, image.Width, firstHeight));

                // 把真图自己的顶部带贴回 after 顶端，模拟"滚动后状态栏还在原地"。
                // 不贴的话就测不到固定栏陷阱——那是这类工具最容易翻车的地方。
                IntRect band = new IntRect(0, 0, image.Width, SimulatedFixedBand);
                after.Blit(image, band, 0, 0);

                var sw = Stopwatch.StartNew();
                MatchResult result = OverlapMatcher.Match(before, after);
                sw.Stop();

                int expectedOffsetY = firstHeight - overlap;
                int error = Math.Abs(result.Offset.Y - expectedOffsetY);

                trials++;
                errors.Add(error);
                samples2.Add((result.Confidence, result.Uniqueness, error));
                if (error == 0) exact++;
                else if (error <= 1) within1++;
                else if (error <= 3) within3++;
                else wrong++;

                Console.WriteLine($"  {file,-16} 应重叠 {overlap,5} → 判定 {result.OverlapLength,5}" +
                                  $"  误差 {error,4}px  {(result.Status == SeamStatus.AutoPassed ? "✓自动" : "!需检查")}" +
                                  $"  拟合 {result.Confidence:P2}  唯一性 {result.Uniqueness:F5}  {sw.ElapsedMilliseconds}ms");
            }
        }

        Console.WriteLine();
        Console.WriteLine($"样本 {trials} 对（真图内容 + 构造已知位移）");
        Console.WriteLine($"  完全命中      {exact,4}  ({Pct(exact, trials)})");
        Console.WriteLine($"  误差 ≤1 像素  {within1 + exact,4}  ({Pct(within1 + exact, trials)})");
        Console.WriteLine($"  误差 ≤3 像素  {within3 + within1 + exact,4}  ({Pct(within3 + within1 + exact, trials)})");
        Console.WriteLine($"  明显错位      {wrong,4}  ({Pct(wrong, trials)})");
        if (errors.Count > 0)
            Console.WriteLine($"  误差中位数 {Median(errors)}px，最大 {errors.Max()}px");

        Console.WriteLine();
        Console.WriteLine("── 唯一性门限扫描（MinUniqueness；拟合度固定按 0.97 判）──");
        Console.WriteLine("  门限      自动放行   其中≤1px   精确率   覆盖率   误放   漏放");
        int totalGood = samples2.Count(s => s.Error <= 1);
        foreach (double gate in new[] { 0.0, 0.00002, 0.00005, 0.0001, 0.0002, 0.0005, 0.001, 0.002, 0.005 })
        {
            var passed = samples2.Where(s => s.Confidence >= 0.97 && s.Uniqueness >= gate).ToList();
            int good = passed.Count(s => s.Error <= 1);
            string precision = passed.Count == 0 ? "  —  " : $"{100.0 * good / passed.Count:F1}%";
            string coverage = totalGood == 0 ? "  —  " : $"{100.0 * good / totalGood:F1}%";
            Console.WriteLine($"  {gate,-8:F5} {passed.Count,7} {good,10}   {precision,6} {coverage,6} " +
                              $"{passed.Count - good,5} {totalGood - good,5}");
        }
        Console.WriteLine();
        Console.WriteLine("读法：精确率 = 被自动放行且确实对得上（≤1px）的比例；覆盖率 = 对得上的里面有多少被自动放行。");
        Console.WriteLine("两者天然对立。本项目按「错缝的代价 > 多看一眼的代价」取，宁可覆盖率低一点。");
        Console.WriteLine();
        Console.WriteLine("⚠ 这些数字是「算法上限」：位移由构造决定，所以不含真实连拍时的动态内容差异");
        Console.WriteLine("  （时间跳动、正在输入、图片懒加载）。现场阈值仍需真机连拍样本确认。");
    }

    private static string Pct(int n, int total) => total == 0 ? "—" : $"{100.0 * n / total:F1}%";

    private static double Median(List<int> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        int mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }
}
