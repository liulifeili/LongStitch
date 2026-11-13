using LongStitch.Core.Model;

namespace LongStitch.Core.Export;

/// <summary>
/// 格式与环境的尺寸/容量限制判定。
///
/// 需求 §7 的硬要求：触及限制时**提前明确提示**并给出可执行方案，禁止静默压缩。
/// 所以这里只负责"说出事实"，不替用户做决定——降尺寸是界面上另一个必须由用户点的选项。
/// </summary>
public static class FormatLimits
{
    /// <summary>JPEG 规范上限：任一边不超过 65535。这是硬限制，超了就是编不出来。</summary>
    public const int JpegMaxSide = 65535;

    /// <summary>
     /// 常见看图软件的稳定上限。PNG 规范本身没有这个限制，但超过之后
     /// 部分查看器/网页会显示不全或直接拒绝打开。
     /// 我们把它当作"警告"而不是"错误"：仍然允许导出，只是要提示。
     /// </summary>
    public const int ViewerComfortMaxSide = 32767;

    /// <summary>单个像素缓冲的分配上限（与 <see cref="Geometry.IntSize.MaxBufferBytes"/> 同源）。</summary>
    public static long MaxBufferBytes => Geometry.IntSize.MaxBufferBytes;

    /// <summary>限制检查结果。</summary>
    /// <param name="Fits">能否作为单文件导出。</param>
    /// <param name="Hard">false 表示只是"可能不兼容"的警告，用户仍可坚持导出。</param>
    /// <param name="Reason">不能导出（或可能有问题）的可读原因。</param>
    /// <param name="SuggestedMaxHeight">建议的分段最大高度；0 表示不需要分段。</param>
    public readonly record struct Verdict(bool Fits, bool Hard, string? Reason, int SuggestedMaxHeight);

    /// <summary>
    /// 检查 w×h 在该格式下能否单文件写出。
    /// </summary>
    /// <param name="format">目标格式。</param>
    /// <param name="width">输出宽度（像素）。</param>
    /// <param name="height">输出高度（像素）。</param>
    /// <param name="freeDiskBytes">目标磁盘剩余空间；0 或负数表示未知，跳过空间检查。</param>
    public static Verdict Check(ExportFormat format, int width, int height, long freeDiskBytes = -1)
    {
        if (width <= 0 || height <= 0)
            return new Verdict(false, true, $"输出尺寸非法：{width}×{height}", 0);

        var size = new Geometry.IntSize(width, height);
        if (!size.CanAllocateBuffer)
        {
            // 单块像素缓冲超出安全上限：给出一个"每片多大"的建议，而不是让用户自己去试
            int suggested = Math.Max(1024, (int)(MaxBufferBytes / 4 / Math.Max(1, width)));
            return new Verdict(false, true,
                $"{width}×{height} 需要 {size.ByteLength / 1024 / 1024} MB 连续像素缓冲，" +
                $"超过本工具单次处理上限 {MaxBufferBytes / 1024 / 1024} MB。",
                suggested);
        }

        if (format == ExportFormat.Jpeg && (width > JpegMaxSide || height > JpegMaxSide))
            return new Verdict(false, true,
                $"JPEG 单边不得超过 {JpegMaxSide} 像素，当前 {width}×{height}。",
                JpegMaxSide);

        int maxSide = Math.Max(width, height);
        if (maxSide > ViewerComfortMaxSide)
            return new Verdict(true, false,
                $"高度 {height} 超过 {ViewerComfortMaxSide} 像素：文件能正常写出，但部分看图软件" +
                "可能显示不全。建议同时准备分段导出。",
                Math.Min(height, ViewerComfortMaxSide));

        if (freeDiskBytes > 0)
        {
            // 用 RGBA  uncompressed 尺寸的一半作为保守预估上界；真实预估由 ExportPlanner 采样给出
            long rough = size.ByteLength / 2;
            if (freeDiskBytes < rough)
                return new Verdict(false, true,
                    $"目标磁盘剩余 {freeDiskBytes / 1024 / 1024} MB，预计需要约 {rough / 1024 / 1024} MB。",
                    0);
        }

        return new Verdict(true, false, null, 0);
    }
}
