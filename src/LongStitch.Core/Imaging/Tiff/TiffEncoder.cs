using LongStitch.Core.Geometry;
using LongStitch.Core.Model;

namespace LongStitch.Core.Imaging.Tiff;

/// <summary>
/// TIFF 编码器适配：把 <see cref="TiffEncoder"/> 接到导出用的 <see cref="IImageEncoder"/> 上。
///
/// 纯托管、零依赖，所以和 PNG 一样默认注册进 <see cref="EncoderRegistry"/>，
/// 不需要 App 层注入平台组件；<see cref="IsLossless"/> 为 true 的依据是
/// "RGBA 字节直存，不重采样、不做色彩管理、不合并 alpha"。
/// </summary>
public sealed class TiffImageEncoder : IImageEncoder
{
    public ExportFormat Format => ExportFormat.Tiff;
    public bool IsAvailable => true;
    public string? UnavailableReason => null;
    public bool IsLossless => true;

    public void Encode(RgbaImage image, IntRect region, Stream output, EncodeOptions options) =>
        TiffEncoder.Encode(image, region, output);

    public IRegionWriter CreateWriter(IntSize region, Stream output, EncodeOptions options) =>
        TiffEncoder.CreateWriter(region, output);
}

/// <summary>
/// 纯托管基线 TIFF 写入器（未压缩、RGBA 四样本、小端）。
///
/// ═══ 为什么自己写 ═══
/// 导出面板上的 TIFF 此前一直显示"未内置编码器"。加一个第三方库就能拿到 TIFF，
/// 但本项目是<b>零 NuGet 依赖</b>的（拉不到包，而且导出字节必须完全由我们自己掌控），
/// 所以按 TIFF 6.0 基线子集自己写：未压缩 + 条带（strip）布局，是最被广泛支持的一种写法。
///
/// ═══ 布局 ═══
/// <code>
/// 偏移 0        : 头 "II" + 42 + 首个 IFD 偏移（先占位，写完数据才回填）
/// 之后          : 各条带的像素数据（行主序 RGBA 原样字节，未压缩）
/// 再之后        : 短数据区（BitsPerSample[4]、StripOffsets[n]、StripByteCounts[n]）
/// 最后          : IFD（10 项，tag 升序；count=1 的值直接放条目里，只有数组才外置）
/// </code>
///
/// ═══ 为什么必须分条带（streaming） ═══
/// 长截图的成品可能有 5 万像素高：一次性攒完整图再写，等于先申请 5×宽度×16 的内存，
/// 与"分块导出"这条红线直接冲突。所以写入器按固定条带高度攒行，
/// 满一条就落盘并记下偏移与字节数，峰值内存 = 一条带。
///
/// ═══ 精度约定 ═══
/// 只做 RGBA 字节直存：不重采样、不做色彩管理、不合并 alpha。
/// 因此"导出与预览逐字节一致"这条承诺对 TIFF 同样成立。
/// </summary>
public static class TiffEncoder
{
    /// <summary>
    /// 每条带的行数。TIFF 规范要求"除最后一条外，各条带行数相同"，
    /// 所以这个值必须是固定的；取 64 行是为了让攒行用的缓冲在 1170 宽下也只有约 300 KB，
    /// 同时不至于把条带数量推到几千（每条带要在 IFD 里占两个数组项）。
    /// </summary>
    public const int RowsPerStrip = 64;

    /// <summary>一次性把整张（或某个矩形）写成 TIFF。小图用这条，长图走 <see cref="CreateWriter"/>。</summary>
    public static void Encode(RgbaImage image, IntRect region, Stream output)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(output);
        region = region.Intersect(image.Bounds);
        if (region.IsEmpty) throw new ArgumentException($"要写出的区域是空的：{region}", nameof(region));

        using var writer = CreateWriter(region.Size, output);
        // 按条带高度切片喂进去，与流式写法同一条代码路径——
        // 这样"一次性"与"分块"两种用法产出的是同一个字节序列，可交叉核对。
        for (int y = 0; y < region.Height; y += RowsPerStrip)
        {
            var slice = new IntRect(region.X, region.Y + y, region.Width,
                Math.Min(RowsPerStrip, region.Height - y));
            RgbaImage band = image.SubImage(slice);
            writer.WriteBand(band);
        }
        writer.Finish();
    }

    /// <summary>开一个流式条带写入器：一行行喂，攒满一条带就落盘。</summary>
    public static IRegionWriter CreateWriter(IntSize region, Stream output) =>
        new TiffRegionWriter(output, region.Width, region.Height);

    /// <summary>
    /// 条带式 TIFF 写入器。不变量与 PNG 写入器一致：
    /// 每条带宽度必须等于区域宽度，累计行数必须等于区域高度，收尾对不上就抛错，
    /// 绝不产出被截断的文件。
    /// </summary>
    private sealed class TiffRegionWriter : IRegionWriter
    {
        private readonly Stream _output;
        private readonly int _height;
        private readonly byte[] _strip;          // 一条带的 RGBA 字节
        private readonly List<int> _stripOffsets = new();
        private readonly List<int> _stripBytes = new();
        private int _used;                        // 当前条带已攒了多少像素
        private int _rowsInStrip;                  // 当前条带已攒了多少行
        private long _position;                    // 文件里下一个要写的位置
        private int _rowsWritten;
        private bool _finished;

        public TiffRegionWriter(Stream output, int width, int height)
        {
            if (width <= 0 || height <= 0)
                throw new ArgumentException($"TIFF 尺寸非法：{width}×{height}");
            if ((long)width * height * 4L > IntSize.MaxBufferBytes)
                throw new ArgumentException(
                    $"TIFF 区域 {width}×{height} 超出单次可处理上限");

            _output = output;
            Width = width;
            _height = height;
            _strip = new byte[width * RowsPerStrip * 4];

            // 头 8 字节：II / 42 / 首个 IFD 偏移（先占位，写完数据才知道）
            Write(new byte[] { (byte)'I', (byte)'I', 42, 0, 0, 0, 0, 0 });
        }

        public int Width { get; }
        public int RowsWritten => _rowsWritten;

        public void WriteBand(RgbaImage band)
        {
            if (_finished) throw new ObjectDisposedException(nameof(TiffRegionWriter));
            ArgumentNullException.ThrowIfNull(band);
            if (band.Width != Width)
                throw new ArgumentException(
                    $"条带宽必须恒为 {Width}，收到 {band.Width}。分块宽度不一致会让导出图错位。",
                    nameof(band));

            for (int y = 0; y < band.Height; y++)
            {
                ReadOnlySpan<byte> row = band.Row(y);
                row.CopyTo(_strip.AsSpan(_used, Width * 4));
                _used += Width * 4;
                _rowsInStrip++;
                _rowsWritten++;
                if (_rowsInStrip == RowsPerStrip) FlushStrip();
            }
        }

        public void Finish()
        {
            if (_finished) return;
            _finished = true;

            if (_rowsInStrip > 0) FlushStrip();
            if (_rowsWritten != _height)
                throw new InvalidDataException(
                    $"TIFF 不完整：声明 {_height} 行，实际只收到 {_rowsWritten} 行");

            // ── 短数据区：BitsPerSample 四个样本 + n 个 StripOffsets + n 个 StripByteCounts ──
            // 只有"个数超过 1、装不进 4 字节"的字段才需要外置到短数据区；
            // count=1 的 SHORT/LONG 必须把值直接写在条目里，写偏移进去读取器会把它当像素值。
            int bitsOffset = (int)_position;
            Write(new byte[] { 8, 0, 8, 0, 8, 0, 8, 0 });

            int offsetsOffset = (int)_position;
            WriteInts(_stripOffsets);
            int countsOffset = (int)_position;
            WriteInts(_stripBytes);

            // ── IFD：tag 必须升序，这里共 10 项 ──
            int ifdOffset = (int)_position;
            var body = new List<byte>();
            void Entry(ushort tag, ushort type, uint count, uint valueOrOffset)
            {
                body.AddRange(BitConverter.GetBytes(tag));
                body.AddRange(BitConverter.GetBytes(type));
                body.AddRange(BitConverter.GetBytes(count));
                body.AddRange(BitConverter.GetBytes(valueOrOffset));
            }

            Entry(256, 4, 1, (uint)Width);                          // ImageWidth（LONG，直接放值）
            Entry(257, 4, 1, (uint)_height);                        // ImageLength
            Entry(258, 3, 4, (uint)bitsOffset);                     // BitsPerSample：4 个 SHORT → 外置
            Entry(259, 3, 1, 1);                                    // Compression = 1（无压缩）
            Entry(262, 3, 1, 2);                                    // PhotometricInterpretation = 2（RGB）
            Entry(273, 4, (uint)_stripOffsets.Count, (uint)offsetsOffset);  // StripOffsets 数组 → 外置
            Entry(277, 3, 1, 4);                                    // SamplesPerPixel = 4
            Entry(278, 4, 1, RowsPerStrip);                         // RowsPerStrip
            Entry(279, 4, (uint)_stripBytes.Count, (uint)countsOffset);     // StripByteCounts 数组 → 外置
            Entry(338, 3, 1, 2);                                    // ExtraSamples = 2（未定义 alpha，count=1 → 放值）

            // 头里那个"首个 IFD 偏移"现在才定得下来：回头补写文件头的 4 字节
            long here = _output.Position;
            _output.Seek(4, SeekOrigin.Begin);
            Write(BitConverter.GetBytes(ifdOffset));
            _output.Seek(here, SeekOrigin.Begin);

            Write(BitConverter.GetBytes((ushort)(body.Count / 12)));
            Write(body.ToArray());
            Write(BitConverter.GetBytes(0));                         // 下一个 IFD = 0（只有一页）
        }

        /// <summary>安静释放：异常展开途中调用时不再追加"文件不完整"，以免盖掉首因。</summary>
        public void Dispose() { }

        private void FlushStrip()
        {
            int bytes = _used;
            if (_position > uint.MaxValue)
                throw new InvalidDataException("TIFF 文件已超过 4 GB，条带偏移放不下 LONG 字段");
            _stripOffsets.Add((int)_position);
            _stripBytes.Add(bytes);
            Write(_strip.AsSpan(0, bytes));
            _used = 0;
            _rowsInStrip = 0;
        }

        private void WriteInts(List<int> values)
        {
            byte[] buf = new byte[values.Count * 4];
            for (int i = 0; i < values.Count; i++)
                BitConverter.GetBytes(values[i]).CopyTo(buf, i * 4);
            Write(buf);
        }

        private void Write(ReadOnlySpan<byte> bytes)
        {
            _output.Write(bytes);
            _position += bytes.Length;
        }
    }
}
