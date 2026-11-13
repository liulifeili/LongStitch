namespace LongStitch.Core.Geometry;

/// <summary>
/// 整数尺寸（宽×高，单位：源图像素）。
/// 宽高为 0 表示"空"，产品里任何尺寸为 0 的图片都会在导入阶段被拒绝。
/// </summary>
public readonly record struct IntSize(int Width, int Height)
{
    public static readonly IntSize Zero = new(0, 0);

    public bool IsEmpty => Width <= 0 || Height <= 0;

    /// <summary>RGBA8888 下该尺寸占用的字节数。用 long 是因为长图会超过 int 范围。</summary>
    public long ByteLength => (long)Width * Height * 4;

    /// <summary>
    /// 单个像素缓冲的分配上限（字节）。
    /// 这是"导出超限要显式提示、禁止静默压缩"那条需求的实现点之一：
    /// 超过它的尺寸不会去尝试分配，而是直接给出真实尺寸与分段建议。
    /// </summary>
    public const long MaxBufferBytes = 6L * 1024 * 1024 * 1024;

    public bool CanAllocateBuffer => ByteLength <= MaxBufferBytes;

    public override string ToString() => $"{Width}×{Height}";
}
