using LongStitch.Core.Geometry;

namespace LongStitch.Core.Imaging;

/// <summary>
/// 项目的唯一像素容器：一段宽度 × 高度、每像素 4 字节（R,G,B,A 非预乘）的连续内存。
///
/// ═══ 它承担"一像素不出错"承诺的方式 ═══
/// 1. 只有整数接口。类里没有任何接受 float/double 的方法，也没有缩放、旋转、
///    镜像、重采样能力——想改尺寸必须走显式的、会被记为"有损操作"的入口。
/// 2. 唯一的搬运原语是 <see cref="Blit(RgbaImage, IntRect, int, int)"/>：逐行 memmove，源矩形与目标位置都是 int。
///    导出与预览都建立在它之上，所以"预览看到的"和"导出生成的"来自同一套整数拷贝。
/// 3. 越界不抛异常也不静默丢弃整块：自动裁剪源矩形，并保证目标落点跟着同步平移，
///    见 <see cref="IntRect.ClipTo"/>。错位比裁剪更致命，所以裁剪必须与落点联动。
///
/// 线程约定：实例本身不是线程安全的。后台解码/匹配产出的新实例可以跨线程交接，
/// 但同一实例不要并发写。
/// </summary>
public sealed class RgbaImage
{
    private readonly byte[] _px;

    public int Width { get; }
    public int Height { get; }

    /// <summary>一行占多少字节（= Width * 4）。这里不做行对齐填充，保持"内存布局 = 像素布局"，
    /// 便于与 PNG 扫描线、导出缓冲直接对接，少一层换算就少一处可能出错的地方。</summary>
    public int Stride => Width * 4;

    public IntSize Size => new(Width, Height);
    public IntRect Bounds => new(0, 0, Width, Height);

    /// <summary>整幅图的像素字节视图。</summary>
    public Span<byte> Pixels => _px;
    public ReadOnlySpan<byte> PixelsRo => _px;

    /// <summary>第 y 行的字节视图。调用方负责保证 0 &lt;= y &lt; Height。</summary>
    public Span<byte> Row(int y) => _px.AsSpan(y * Stride, Stride);

    /// <summary>构造一块全透明（0,0,0,0）的新图。</summary>
    /// <exception cref="ArgumentOutOfRangeException">宽高非正。</exception>
    /// <exception cref="NotSupportedException">超过 <see cref="IntSize.MaxBufferBytes"/>，
    /// 由调用方转为"导出超限"提示，而不是让进程 OOM。</exception>
    public RgbaImage(int width, int height)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width),
                $"像素图尺寸必须为正整数，收到 {width}×{height}");

        var size = new IntSize(width, height);
        if (!size.CanAllocateBuffer)
            throw new NotSupportedException(
                $"单块像素缓冲需要 {size.ByteLength / 1024 / 1024} MB，超过安全上限 " +
                $"{IntSize.MaxBufferBytes / 1024 / 1024} MB（{width}×{height}）。" +
                "请改用分段导出。");

        Width = width;
        Height = height;
        _px = new byte[size.ByteLength > Array.MaxLength ? Array.MaxLength : size.ByteLength];
    }

    /// <summary>用已有字节构造（字节长度必须正好等于 Width*Height*4，不复制）。</summary>
    public static RgbaImage Wrap(int width, int height, byte[] rgba)
    {
        var img = new RgbaImage(width, height);
        if (rgba.Length != img._px.Length)
            throw new ArgumentException(
                $"字节数不匹配：期望 {img._px.Length}，实际 {rgba.Length}", nameof(rgba));
        Buffer.BlockCopy(rgba, 0, img._px, 0, rgba.Length);
        return img;
    }

    /// <summary>浅拷贝内容（像素会被复制，得到独立可写副本）。</summary>
    public RgbaImage Clone()
    {
        var copy = new RgbaImage(Width, Height);
        Buffer.BlockCopy(_px, 0, copy._px, 0, _px.Length);
        return copy;
    }

    // ───────────────────────── 像素读写 ─────────────────────────

    public RgbaColor GetPixel(int x, int y)
    {
        int i = y * Stride + x * 4;
        return new RgbaColor(_px[i], _px[i + 1], _px[i + 2], _px[i + 3]);
    }

    public void SetPixel(int x, int y, RgbaColor c)
    {
        int i = y * Stride + x * 4;
        _px[i] = c.R; _px[i + 1] = c.G; _px[i + 2] = c.B; _px[i + 3] = c.A;
    }

    public bool InBounds(int x, int y) => x >= 0 && y >= 0 && x < Width && y < Height;

    // ───────────────────────── 搬运原语 ─────────────────────────

    /// <summary>
    /// 把 <paramref name="src"/> 的 <paramref name="srcRect"/> 原样拷贝到本图的
    /// (<paramref name="dstX"/>, <paramref name="dstY"/>) 起处。整数逐行 memmove，无任何插值。
    ///
    /// 越界处理：源矩形与目标位置可以同时越界，双方都被裁剪，且**保证不错位**。
    /// 返回真正被写入的目标矩形；完全不相交时返回 <see cref="IntRect.Empty"/>。
    ///
    /// ═══ 求解方式 ═══
    /// 不能"源矩形裁一次、目标矩形裁一次"，那样两次裁剪对不上，越界时会读到源矩形之外。
    /// 正确做法是只在**请求矩形的局部坐标 u** 上求解：一个目标像素 tx = dstX + u
    /// 对应源像素 s0.X + u，于是同时合法的充要条件是下面三个区间的交集——
    ///   ① 落在本图内：      0 &lt;= u &lt; Width - dstX
    ///   ② 落在请求矩形内：  0 &lt;= u &lt; srcRect.Width   ← 少了这条就会多拷，见下
    ///   ③ 落在源图内：     -s0.X &lt;= u &lt; src.Width - s0.X
    /// 再由固定映射反推源坐标，两侧同时合法是推导出来的，不是拼出来的。
    ///
    /// ═══ 为什么 ② 必须在场 ═══
    /// 之前只写了 ①③，于是"从大源图里取一小块贴到目标左上角"会一路拷到目标矩形底部，
    /// 把本该保留的内容整片覆盖掉——而且只在 srcRect 比源图小的时候发生，
    /// 现有"双侧越界"用例恰好躲过（那里 srcRect 比源图大，③ 先收紧）。
    /// 这个缺陷是被真图标定跑出来的，判据见 <c>blit/拷贝不许越过源矩形</c>。
    /// </summary>
    public IntRect Blit(RgbaImage src, IntRect srcRect, int dstX, int dstY)
    {
        ArgumentNullException.ThrowIfNull(src);
        IntRect s0 = srcRect.Normalized();
        if (s0.IsEmpty) return IntRect.Empty;

        int uLo = Math.Max(0, Math.Max(-dstX, -s0.X));
        int uHi = Math.Min(s0.Width, Math.Min(Width - dstX, src.Width - s0.X));
        int vLo = Math.Max(0, Math.Max(-dstY, -s0.Y));
        int vHi = Math.Min(s0.Height, Math.Min(Height - dstY, src.Height - s0.Y));

        int w = uHi - uLo, h = vHi - vLo;
        if (w <= 0 || h <= 0) return IntRect.Empty;

        int tx0 = dstX + uLo;             // 首个合法目标列
        int ty0 = dstY + vLo;
        int sx0 = s0.X + uLo;             // 对应的首个合法源列，由上面三个区间保证一定合法
        int sy0 = s0.Y + vLo;
        int copyBytes = w * 4;

        for (int row = 0; row < h; row++)
        {
            int srcOff = (sy0 + row) * src.Stride + sx0 * 4;
            int dstOff = (ty0 + row) * Stride + tx0 * 4;
            src._px.AsSpan(srcOff, copyBytes).CopyTo(_px.AsSpan(dstOff, copyBytes));
        }
        return new IntRect(tx0, ty0, w, h);
    }

    /// <summary>整幅源图拷到本图某点（等价于 Blit(src, src.Bounds, dst.X, dst.Y)）。</summary>
    public IntRect Blit(RgbaImage src, IntPoint dst) => Blit(src, src.Bounds, dst.X, dst.Y);

    /// <summary>
    /// 从另一张图取一块 1:1 尺寸的区域覆盖本区域。尺寸不等直接抛，
    /// 因为"不等尺寸"意味着有人偷偷做了缩放——那是本项目明令禁止的隐式行为。
    /// 清理里的"状态栏替换""留白"都走这里。
    /// </summary>
    public void ReplaceRegion(RgbaImage src, IntRect srcRect, IntRect dstRect)
    {
        if (srcRect.Width != dstRect.Width || srcRect.Height != dstRect.Height)
            throw new ArgumentException(
                $"替换区域尺寸必须一致：源 {srcRect.Width}×{srcRect.Height}，" +
                $"目标 {dstRect.Width}×{dstRect.Height}。需要缩放请显式调用有损接口。",
                nameof(srcRect));
        Blit(src, srcRect, dstRect.X, dstRect.Y);
    }

    /// <summary>纯色填充一个矩形（越界部分裁剪）。</summary>
    public void Fill(IntRect rect, RgbaColor color)
    {
        IntRect r = rect.Normalized().Intersect(Bounds);
        if (r.IsEmpty) return;

        Span<byte> first = stackalloc byte[4];
        first[0] = color.R; first[1] = color.G; first[2] = color.B; first[3] = color.A;

        for (int y = r.Y; y < r.Bottom; y++)
        {
            Span<byte> row = Row(y);
            for (int x = r.X; x < r.Right; x++)
                first.CopyTo(row.Slice(x * 4, 4));
        }
    }

    /// <summary>整幅填充。</summary>
    public void Fill(RgbaColor color) => Fill(Bounds, color);

    /// <summary>裁出一块新图（整数裁切，不复制多余像素）。用于分段导出与缩略图取源。</summary>
    public RgbaImage SubImage(IntRect rect)
    {
        IntRect r = rect.Normalized().Intersect(Bounds);
        if (r.IsEmpty) throw new ArgumentException($"裁切区域与图像无交集：{rect}", nameof(rect));
        var outImg = new RgbaImage(r.Width, r.Height);
        outImg.Blit(this, r, 0, 0);
        return outImg;
    }

    // ───────────────────────── 只读分析 ─────────────────────────

    /// <summary>是否所有像素 alpha 都是 255（决定 PNG 用 RGB 还是 RGBA 保存，也决定 JPEG 是否需要背景色）。</summary>
    public bool IsFullyOpaque()
    {
        for (int i = 3; i < _px.Length; i += 4)
            if (_px[i] != 255) return false;
        return true;
    }

    /// <summary>
    /// 把半透明像素按整数运算合成到指定底色上，得到全不透明新图。
    /// 供"透明画布导出 JPEG"使用——需求明确要求这时必须让用户选背景色，
    /// 不能由编码器自行决定（不同编码器对透明的处理不一致，会引入不可控差异）。
    /// </summary>
    public RgbaImage FlattenOnto(RgbaColor background)
    {
        var outImg = new RgbaImage(Width, Height);
        for (int y = 0; y < Height; y++)
        {
            ReadOnlySpan<byte> s = Row(y);
            Span<byte> t = outImg.Row(y);
            for (int x = 0; x < Width; x++)
            {
                int i = x * 4;
                byte a = s[i + 3];
                if (a == 255)
                {
                    s.Slice(i, 4).CopyTo(t.Slice(i, 4));
                    continue;
                }
                if (a == 0)
                {
                    t[i] = background.R; t[i + 1] = background.G; t[i + 2] = background.B; t[i + 3] = 255;
                    continue;
                }
                // 先乘后加半个除数以完成四舍五入；同样的公式在 Checks 里被独立复算一次
                t[i] = (byte)((s[i] * a + background.R * (255 - a) + 127) / 255);
                t[i + 1] = (byte)((s[i + 1] * a + background.G * (255 - a) + 127) / 255);
                t[i + 2] = (byte)((s[i + 2] * a + background.B * (255 - a) + 127) / 255);
                t[i + 3] = 255;
            }
        }
        return outImg;
    }

    /// <summary>
    /// 逐像素平均绝对差（SAD）与参与比较的像素数。用于匹配精修与差异高亮。
    /// <paramref name="ignoreAlpha"/> 为 true 时只比 RGB——截图里 alpha 恒为 255，
    /// 比它没有信息量，还会让"透明补白区"被误判成差异。
    /// </summary>
    public long DifferenceSumAbsolute(RgbaImage other, IntRect rect, bool ignoreAlpha = true, int rowStride = 1)
    {
        IntRect r = rect.Intersect(Bounds).Intersect(other.Bounds);
        long sum = 0;
        int channels = ignoreAlpha ? 3 : 4;
        for (int y = r.Y; y < r.Bottom; y += Math.Max(1, rowStride))
        {
            ReadOnlySpan<byte> a = Row(y);
            ReadOnlySpan<byte> b = other.Row(y);
            for (int i = r.X * 4; i < r.Right * 4; i += 4)
            {
                for (int c = 0; c < channels; c++)
                    sum += Math.Abs(a[i + c] - b[i + c]);
            }
        }
        return sum;
    }

    /// <summary>
    /// 计算某个矩形区域的 SHA-256。逐行取字节，行间顺序固定，因此结果与图像其余部分无关。
    ///
    /// 这是"一像素不出错"的判据原语：Checks 工装用它把导出结果的每一段切片，
    /// 与另一套独立实现算出的同一切片做哈希比对。整图哈希不行——整图相同不代表
    /// 每一段都对，而分段导出恰恰是"多段拼起来"的产物。
    /// </summary>
    public byte[] RegionSha256(IntRect rect)
    {
        IntRect r = rect.Normalized().Intersect(Bounds);
        if (r.IsEmpty)
            throw new ArgumentException(
                $"要哈希的区域 {rect} 与本图 {Bounds} 没有交集。" +
                "空区域返回一个「合法哈希」会让调用方的坐标写错变成假绿，所以这里必须响。",
                nameof(rect));

        using var sha = System.Security.Cryptography.SHA256.Create();
        // .NET 8 的 HashAlgorithm 只有数组版 TransformBlock（Span 版不存在），
        // 所以复用一个行缓冲，逐行喂进去：峰值内存 = 一行，与区域高度无关。
        byte[] buffer = new byte[r.Width * 4];
        for (int y = r.Y; y < r.Bottom; y++)
        {
            Row(y).Slice(r.X * 4, buffer.Length).CopyTo(buffer);
            sha.TransformBlock(buffer, 0, buffer.Length, null, 0);
        }
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return sha.Hash!;
    }

    /// <summary>区域哈希的短十六进制形式，用于日志与 Checks 输出证据。</summary>
    public string RegionChecksumPreview(IntRect rect) =>
        Convert.ToHexString(RegionSha256(rect), 0, 8);
}
