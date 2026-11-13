using LongStitch.Core.Geometry;
using LongStitch.Core.Model;

namespace LongStitch.Core.Imaging;

/// <summary>
/// 一对相邻截图里"固定不动的 UI 带"的位置。
///
/// 需求 §5.1 明确要求"识别并降低状态栏、导航栏、底部输入栏、悬浮按钮、滚动条等固定元素的
/// 匹配权重，避免把相同固定 UI 当作真正的内容重叠"。这里就是那条需求的落点：
/// 被认定为固定带的行，在打分时<b>完全不参与</b>（比"降权"更彻底，也更好解释）。
///
/// 为什么必须这么做：两张连续滚动截图的状态栏几乎逐像素相同。如果不排除，
/// 搜索会立刻发现"把后一张整体上移 0 行、状态栏对状态栏"是个极佳解，
/// 于是判定重叠 = 整张图高度 → 后一张被整张吃掉。这是长截图拼接工具最经典的一类错误。
/// </summary>
public readonly record struct FixedBands(int Top, int Bottom)
{
    public static readonly FixedBands None = new(0, 0);

    public bool IsEmpty => Top == 0 && Bottom == 0;

    /// <summary>参与打分的行区间。</summary>
    public IntRange ContentRows(int height) => new(Top, Math.Max(0, height - Top - Bottom));

    public override string ToString() => $"顶部 {Top}px / 底部 {Bottom}px";
}

/// <summary>固定带检测器。</summary>
public static class FixedRegionAnalyzer
{
    /// <summary>
    /// 比较相邻两图，找出"两图同一位置几乎一样"的顶部与底部横带。
    /// 只有两张图都参与判断才可靠——单张图无法区分"固定 UI"与"恰好没滚动的内容"。
    /// </summary>
    /// <param name="a">前一张。</param>
    /// <param name="b">后一张。</param>
    /// <param name="maxBand">允许认定的最大带高（源图像素）。状态栏不可能占半张图。</param>
    /// <param name="rowThreshold">单行平均通道差小于此值算"这行相同"。</param>
    /// <param name="maxGaps">允许带内出现的连续不匹配行数；超过就认定带到此结束。</param>
    public static FixedBands Detect(
        RgbaImage a, RgbaImage b,
        int maxBand = 320, int rowThreshold = 10, int maxGaps = 4)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        // 宽度不同（例如跨设备、或一张被裁过）时不做固定带认定：
        // 逐行比较本身就没有意义，硬猜会把正文当成固定带排除掉。
        if (a.Width != b.Width) return FixedBands.None;

        int top = DetectEdge(a, b, fromTop: true, maxBand, rowThreshold, maxGaps);
        int bottom = DetectEdge(a, b, fromTop: false, maxBand, rowThreshold, maxGaps);

        // 两条带加起来不能吃光整张图；超了就只保留顶部
        int height = Math.Min(a.Height, b.Height);
        if (top + bottom > height / 2) bottom = Math.Max(0, height / 2 - top);

        return new FixedBands(top, bottom);
    }

    private static int DetectEdge(RgbaImage a, RgbaImage b, bool fromTop,
        int maxBand, int rowThreshold, int maxGaps)
    {
        int height = Math.Min(a.Height, b.Height);
        int limit = Math.Min(maxBand, height / 3);   // 带高不超过图高的 1/3，避免误伤正文
        int matched = 0;
        int gaps = 0;
        int lastMatch = 0;

        for (int k = 0; k < limit; k++)
        {
            int y = fromTop ? k : height - 1 - k;
            bool same = RowAverageDifference(a, b, y) < rowThreshold;
            if (same)
            {
                matched++;
                lastMatch = k;
                gaps = 0;
            }
            else if (++gaps > maxGaps)
            {
                break;
            }
        }

        // 匹配行太少就认为没有固定带：宁可不排除，也不许把正文悄悄排除掉
        return matched >= Math.Min(24, limit / 2) ? lastMatch + 1 : 0;
    }

    /// <summary>两图同一行的平均通道差。宽度不同或行越界时返回极大值。</summary>
    private static int RowAverageDifference(RgbaImage a, RgbaImage b, int y)
    {
        if (y < 0 || y >= a.Height || y >= b.Height) return int.MaxValue / 4;
        ReadOnlySpan<byte> ra = a.Row(y);
        ReadOnlySpan<byte> rb = b.Row(y);
        long sum = 0;
        for (int i = 0; i < ra.Length; i += 4)
        {
            sum += Math.Abs(ra[i] - rb[i]);
            sum += Math.Abs(ra[i + 1] - rb[i + 1]);
            sum += Math.Abs(ra[i + 2] - rb[i + 2]);
        }
        return (int)(sum / (a.Width * 3));
    }
}
