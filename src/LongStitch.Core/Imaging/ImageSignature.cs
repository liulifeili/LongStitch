using LongStitch.Core.Geometry;

namespace LongStitch.Core.Imaging;

/// <summary>
/// 行签名：把一行像素压成一小段整数特征，用于快速搜索重叠位置。
///
/// ═══ 为什么不直接拿像素比 ═══
/// 一对 1170×2532 的截图，候选重叠位置有约 2000 个，每个要比最多 2500 行 × 1170 列 × 3 通道
/// ≈ 170 亿次比较——暴力全分辨率搜索在普通桌面上要跑几十秒，需求 §9 的"导入后画布仍可响应"就废了。
/// 先把每行压成 2×bins 个整数，粗搜阶段代价降三个数量级，再只对少数候选做全分辨率精修。
///
/// ═══ 特征选择 ═══
/// 每个 bin 存两个量：
///   · 亮度均值：抓住"这块是深色文字还是浅色背景"
///   · 水平梯度均值：抓住"这里有没有字的边缘"
/// 只用亮度会在"整片纯色聊天背景"上产生大量并列候选；加上梯度后，文字行与空白行立刻分得开。
/// 两者都是 0–255 的整数，距离就是绝对差之和（SAD），没有浮点、没有开方。
/// </summary>
public sealed class ImageSignature
{
    /// <summary>每行的特征个数（= 2 × Bins）。</summary>
    public int FeatureCount { get; }

    /// <summary>横向分块数。</summary>
    public int Bins { get; }

    /// <summary>参与统计的列区间（滚动条等被排除在外时就收窄在这里）。</summary>
    public IntRange Columns { get; }

    public int Height { get; }

    private readonly ushort[] _features;

    private ImageSignature(int height, int bins, IntRange columns, ushort[] features)
    {
        Height = height;
        Bins = bins;
        FeatureCount = bins * 2;
        Columns = columns;
        _features = features;
    }

    /// <summary>
    /// 为图像的指定列区间构建行签名。
    /// </summary>
    /// <param name="image">源像素。</param>
    /// <param name="bins">横向分块数。越大越能区分横向错位，也越慢；48 对手机截图够用。</param>
    /// <param name="columns">参与统计的列区间；越界会被夹到图像内。</param>
    public static ImageSignature Build(RgbaImage image, int bins, IntRange columns)
    {
        ArgumentNullException.ThrowIfNull(image);
        bins = Math.Clamp(bins, 4, 512);
        IntRange cols = columns.ClampedTo(image.Width);
        if (cols.Width < bins)
        {
            // 列数比分块还少时按列数分块，保证每个 bin 至少有一列，不会出现空 bin 拉低区分度
            bins = Math.Max(4, cols.Width);
            cols = new IntRange(cols.Start, cols.Length);
        }

        var features = new ushort[image.Height * bins * 2];
        double binWidth = (double)cols.Width / bins;

        for (int y = 0; y < image.Height; y++)
        {
            ReadOnlySpan<byte> row = image.Row(y);
            int baseIdx = y * bins * 2;

            for (int b = 0; b < bins; b++)
            {
                int x0 = cols.Start + (int)(b * binWidth);
                int x1 = cols.Start + (int)((b + 1) * binWidth);
                if (x1 <= x0) x1 = x0 + 1;
                x1 = Math.Min(x1, image.Width);

                long lumaSum = 0;
                long gradSum = 0;
                int count = 0;
                int prevLuma = 0;

                for (int x = x0; x < x1; x++)
                {
                    int i = x * 4;
                    // 整数亮度（Rec.601 定点近似）：只做乘加与移位，不引入浮点色彩转换
                    int luma = (row[i] * 77 + row[i + 1] * 150 + row[i + 2] * 28) >> 8;
                    if (count > 0) gradSum += Math.Abs(luma - prevLuma);
                    prevLuma = luma;
                    lumaSum += luma;
                    count++;
                }

                if (count == 0) count = 1;
                features[baseIdx + b] = (ushort)Math.Clamp(lumaSum / count, 0, 255);
                // 梯度除以 (count-1)，再压到 0–255（梯度本身不会超过 255）
                features[baseIdx + bins + b] =
                    (ushort)Math.Clamp(count > 1 ? gradSum / (count - 1) : 0, 0, 255);
            }
        }

        return new ImageSignature(image.Height, bins, cols, features);
    }

    /// <summary>两行特征之间的绝对差之和。行号越界返回极大值，让调用方自然淘汰该候选。</summary>
    public long DistanceTo(ImageSignature other, int rowSelf, int rowOther)
    {
        if (rowSelf < 0 || rowSelf >= Height || rowOther < 0 || rowOther >= other.Height)
            return long.MaxValue / 4;

        int a = rowSelf * FeatureCount;
        int b = rowOther * other.FeatureCount;
        int n = Math.Min(FeatureCount, other.FeatureCount);

        long sum = 0;
        for (int k = 0; k < n; k++)
            sum += Math.Abs((int)_features[a + k] - other._features[b + k]);
        return sum;
    }
}

/// <summary>
/// 一维整数闭开区间 [Start, Start+Length)。用来表达"哪些列/行参与计算"。
/// 单独造这个类型而不是用 (int,int) 元组，是为了把"夹到合法范围"这件事收在一处。
/// </summary>
public readonly record struct IntRange(int Start, int Length)
{
    public int End => Start + Length;

    /// <summary>长度别名。几何代码里习惯写 Width，这里两个名字都给，避免混用时出错。</summary>
    public int Width => Length;

    public bool IsEmpty => Length <= 0;

    public IntRange ClampedTo(int size)
    {
        int start = Math.Clamp(Start, 0, Math.Max(0, size));
        int end = Math.Clamp(End, start, size);
        return new IntRange(start, end - start);
    }

    /// <summary>是否包含该位置。</summary>
    public bool Contains(int index) => index >= Start && index < End;

    public override string ToString() => $"[{Start}..{End})";
}
