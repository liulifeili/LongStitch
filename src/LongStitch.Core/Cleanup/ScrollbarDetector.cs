using LongStitch.Core.Geometry;
using LongStitch.Core.Imaging;

namespace LongStitch.Core.Cleanup;

/// <summary>左右侧别。</summary>
public enum EdgeSide { Left, Right }

/// <summary>一次滚动条检测的结果。</summary>
/// <param name="Side">出现在哪一侧。</param>
/// <param name="BarLeft">滚动条条体（含其外侧留白）的最左列，片段有效区域局部坐标。</param>
/// <param name="Top">滚动条竖直起始行。</param>
/// <param name="Length">滚动条本身占多少行。</param>
/// <param name="Thickness">条体厚度（含外侧到边）。</param>
/// <param name="Confidence">0–1。低于门限时只提示、不动手。</param>
public readonly record struct ScrollbarDetection(
    EdgeSide Side, int BarLeft, int Top, int Length, int Thickness, double Confidence);

/// <summary>
/// 滚动条检测。
///
/// 判据（三条必须同时成立，缺一就不报）：
///   1. 靠近边缘的一条窄竖带（厚度 2–10px）<b>纵向颜色几乎不变</b>；
///   2. 这条竖带与它内侧相邻列<b>明显不同色</b>（不是背景的一部分）；
///   3. 竖带是<b>有限长度的一段</b>，不是贯穿整张图（贯穿整张的是分栏线/边框）。
///
/// 为什么保守：误判的代价是把正文涂掉一条，用户往往要放大到 100% 才发现——
/// 这种缺陷比"没清掉滚动条"严重得多。所以宁可漏报，让 <c>MinConfidence</c> 把关。
/// </summary>
public static class ScrollbarDetector
{
    /// <param name="image">片段有效像素。</param>
    /// <param name="maxThickness">允许的最大厚度。</param>
    /// <param name="minContrast">条体与内侧列的最小平均差（0–255）。</param>
    public static ScrollbarDetection? Detect(RgbaImage image, int maxThickness = 10, int minContrast = 24)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.Width < 32 || image.Height < 64) return null;

        ScrollbarDetection? best = null;
        foreach (EdgeSide side in new[] { EdgeSide.Left, EdgeSide.Right })
        {
            for (int t = 2; t <= Math.Min(maxThickness, image.Width / 4); t++)
            {
                // 条体所在列：右侧从边缘往里数 t 列，左侧对称
                int barCol = side == EdgeSide.Right ? image.Width - t : t - 1;
                int refCol = side == EdgeSide.Right ? Math.Max(0, barCol - 2) : Math.Min(image.Width - 1, barCol + 2);
                if (refCol == barCol) continue;

                var run = MeasureRun(image, barCol, refCol, minContrast);
                if (run is null) continue;

                double confidence = Score(image, barCol, refCol, run.Value, minContrast);
                if (confidence < 0.35) continue;

                var candidate = new ScrollbarDetection(
                    side,
                    BarLeft: side == EdgeSide.Right ? barCol : 0,
                    Top: run.Value.start,
                    Length: run.Value.length,
                    Thickness: t + (side == EdgeSide.Right ? image.Width - barCol - 1 : barCol),
                    Confidence: Math.Min(1.0, confidence));

                if (best is null || candidate.Confidence > best.Value.Confidence) best = candidate;
            }
        }
        return best;
    }

    /// <summary>沿条体列找"与内侧列明显不同"的最长连续段。</summary>
    private static (int start, int length)? MeasureRun(RgbaImage image, int barCol, int refCol, int minContrast)
    {
        int height = image.Height;
        var flags = new bool[height];
        for (int y = 0; y < height; y++)
        {
            ReadOnlySpan<byte> row = image.Row(y);
            int b = barCol * 4, r = refCol * 4;
            int diff = Math.Abs(row[b] - row[r]) + Math.Abs(row[b + 1] - row[r + 1]) + Math.Abs(row[b + 2] - row[r + 2]);
            flags[y] = diff / 3 >= minContrast;
        }

        int bestStart = -1, bestLen = 0, curStart = -1, curLen = 0;
        for (int y = 0; y < height; y++)
        {
            if (flags[y])
            {
                if (curStart < 0) curStart = y;
                curLen++;
                if (curLen > bestLen) { bestLen = curLen; bestStart = curStart; }
            }
            else
            {
                // 允许段内有 1 行的断点（抗锯齿/圆角处偶发的同色行）
                if (curLen > 0 && y + 1 < height && flags[y + 1]) continue;
                curStart = -1; curLen = 0;
            }
        }

        if (bestStart < 0) return null;
        // 贯穿全高的不是滚动条：那是分栏线、边框或整块侧边栏
        if (bestLen > height * 0.92) return null;
        if (bestLen < Math.Max(24, height * 0.05)) return null;
        return (bestStart, bestLen);
    }

    /// <summary>
    /// 打分：条体纵向一致性 + 与内侧的反差 + 段长占比。三项各占权重，
    /// 任何一项不成立都会把分数压到门限以下，从而落到"需检查/不动手"。
    /// </summary>
    private static double Score(RgbaImage image, int barCol, int refCol, (int start, int length) run, int minContrast)
    {
        long sumR = 0, sumG = 0, sumB = 0;
        for (int y = run.start; y < run.start + run.length; y++)
        {
            ReadOnlySpan<byte> row = image.Row(y);
            sumR += row[barCol * 4]; sumG += row[barCol * 4 + 1]; sumB += row[barCol * 4 + 2];
        }
        double n = run.length;
        double meanR = sumR / n, meanG = sumG / n, meanB = sumB / n;

        double variance = 0, contrast = 0;
        for (int y = run.start; y < run.start + run.length; y++)
        {
            ReadOnlySpan<byte> row = image.Row(y);
            double dr = row[barCol * 4] - meanR, dg = row[barCol * 4 + 1] - meanG, db = row[barCol * 4 + 2] - meanB;
            variance += (dr * dr + dg * dg + db * db) / 3.0;
            int r = refCol * 4, b = barCol * 4;
            contrast += (Math.Abs(row[b] - row[r]) + Math.Abs(row[b + 1] - row[r + 1]) + Math.Abs(row[b + 2] - row[r + 2])) / 3.0;
        }
        double stdDev = Math.Sqrt(variance / n);
        double consistency = Math.Clamp(1.0 - stdDev / 24.0, 0.0, 1.0);          // 越平越像滚动条
        double contrastScore = Math.Clamp((contrast / n) / (minContrast * 2.0), 0.0, 1.0);
        double coverage = Math.Clamp(run.length / (image.Height * 0.5), 0.0, 1.0); // 太短不给高分

        return consistency * 0.45 + contrastScore * 0.35 + coverage * 0.20;
    }
}
