using System.IO.Compression;
using LongStitch.Core.Geometry;

namespace LongStitch.Core.Imaging.Png;

/// <summary>PNG 编码选项。</summary>
public sealed record PngEncodeOptions
{
    /// <summary>zlib 压缩力度。默认 <see cref="CompressionLevel.SmallestSize"/>：
    /// 长截图动辄几万行，体积差异非常明显。</summary>
    public CompressionLevel CompressionLevel { get; init; } = CompressionLevel.SmallestSize;

    /// <summary>全图不透明时改用 colorType 2（RGB）保存，可省约 1/4 体积且仍然无损。
    /// 设为 false 可以强制 RGBA，便于做"同一张图两种写法"的对比实验。</summary>
    public bool StripAlphaIfOpaque { get; init; } = true;

    /// <summary>逐行自适应选滤波（无损，压缩率更好）；false 则一律用 None 滤波（更快、更大）。</summary>
    public bool AdaptiveFilter { get; init; } = true;

    /// <summary>IDAT 分块大小。PNG 允许把压缩流切成多个 IDAT；这里用 64KB，
    /// 与常见编码器一致，也保证流式写入时内存占用恒定。</summary>
    public int IdatChunkSize { get; init; } = 65536;
}

/// <summary>
/// 纯托管 PNG 编码器（流式）。
///
/// 三个设计要点，都和"一像素不出错"直接相关：
/// 1. **不写任何色彩元数据**：不输出 gAMA/cHRM/iCCP/sRGB。截图的像素值就是我们要保住的值，
///    加色彩元数据等于邀请查看器做重映射，那会让"同一张图在不同软件里颜色不同"变成不可控。
/// 2. **滤波是无损可逆的**：自适应滤波只改变压缩效率，不改变解出来的像素；
///    Checks 里用 GDI+ 读回来逐字节核对，正是为了证明这一点。
/// 3. **按行喂入**：几万行的长图不需要一次驻留内存，导出峰值内存与总高度无关。
///
/// 关键不变量：<b>滤波的字节步长必须与最终写进压缩流的字节布局完全一致</b>。
/// RGBA 输出时步长 4、每行 width*4 字节；RGB 输出时先把 alpha 丢掉、步长 3、每行 width*3 字节。
/// 早期版本曾在"按 RGBA 算滤波、再丢 alpha 写出"上翻过车（解码结果花屏），
/// 现在 _encLine 就是"将要被写出的那一行"，只有一份布局，从结构上排除这类不一致。
/// </summary>
public static class PngEncoder
{
    /// <summary>把整幅图编码写入流。</summary>
    public static void Encode(RgbaImage image, Stream output, PngEncodeOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(image);
        bool withAlpha = !(options?.StripAlphaIfOpaque ?? true) || !image.IsFullyOpaque();
        using var writer = new PngStreamWriter(output, image.Width, image.Height, withAlpha, options);
        for (int y = 0; y < image.Height; y++)
            writer.WriteRgbaRow(image.Row(y));
    }

    /// <summary>编码整幅图到字节数组。</summary>
    public static byte[] EncodeToBytes(RgbaImage image, PngEncodeOptions? options = null)
    {
        using var ms = new MemoryStream();
        Encode(image, ms, options);
        return ms.ToArray();
    }

    /// <summary>
    /// 把图中某个矩形区域单独编码（分段导出与"逐片字节自证"都用它）。
    /// 不先裁出新图再编码，而是直接从原图按行取字节：少一次搬运就少一处可能出错的地方。
    /// </summary>
    public static void EncodeRegion(RgbaImage image, IntRect region, Stream output,
        PngEncodeOptions? options = null)
    {
        IntRect r = region.Intersect(image.Bounds);
        if (r.IsEmpty) throw new ArgumentException($"编码区域与图像无交集：{region}", nameof(region));

        bool withAlpha = !(options?.StripAlphaIfOpaque ?? true) || !RegionIsOpaque(image, r);
        using var writer = new PngStreamWriter(output, r.Width, r.Height, withAlpha, options);
        var line = new byte[r.Width * 4];
        for (int y = r.Y; y < r.Bottom; y++)
        {
            image.Row(y).Slice(r.X * 4, r.Width * 4).CopyTo(line);
            writer.WriteRgbaRow(line);
        }
    }

    private static bool RegionIsOpaque(RgbaImage image, IntRect r)
    {
        for (int y = r.Y; y < r.Bottom; y++)
        {
            ReadOnlySpan<byte> row = image.Row(y);
            for (int i = r.X * 4 + 3; i < r.Right * 4; i += 4)
                if (row[i] != 255) return false;
        }
        return true;
    }
}

/// <summary>
/// 流式 PNG 写入器：构造时写签名与 IHDR，逐行喂 RGBA，<see cref="Finish"/> 时收尾 IEND。
///
/// 行数不足时 <see cref="Finish"/> 抛异常而不是补黑边——需求 §7 要求
/// "取消或失败时清理不完整的临时输出"，宁可让上层删掉半成品，
/// 也不能产出一个"看起来存在、内容被截断"的文件。
/// </summary>
public sealed class PngStreamWriter : IDisposable
{
    private readonly Stream _output;
    private readonly IdatChunkStream _idat;
    private readonly ZLibStream _zlib;
    private readonly int _width;
    private readonly int _height;
    private readonly bool _withAlpha;
    private readonly bool _adaptive;

    /// <summary>每行"将要写进压缩流"的字节数（不含 1 字节滤波类型）。</summary>
    private readonly int _bytesPerRow;
    /// <summary>滤波步长：一个像素占几字节。</summary>
    private readonly int _bpp;

    private readonly byte[] _encLine;   // 本行待写布局（RGBA 或 RGB）
    private readonly byte[] _prevLine;  // 上一行同布局，Up/Paeth 需要
    private readonly byte[] _cand;      // 候选滤波结果
    private readonly byte[] _best;      // 选中滤波结果
    private bool _finished;

    /// <summary>
    /// 写入过程中是否已经出过错。
    /// 有这个标记时 <see cref="Finish"/> 不再抛"行数不足"——因为那会**盖掉真正的首因异常**，
    /// 让人只看到一句"文件不完整"却查不到是哪一行、为什么断的。
    /// 首因异常会照常从 using 块向外传播，本类只负责不掩盖它。
    /// </summary>
    private bool _faulted;

    public PngStreamWriter(Stream output, int width, int height, bool withAlpha,
        PngEncodeOptions? options = null)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), $"PNG 尺寸非法：{width}×{height}");
        // PNG 规范对尺寸没有 JPEG 那样的 65535 上限，但不少看图软件在超过 32767 时表现不一致。
        // 这里不静默降质也不自动分段，只把事实交给 ExportPlanner 去显式提示。
        _output = output;
        _width = width;
        _height = height;
        _withAlpha = withAlpha;
        _adaptive = options?.AdaptiveFilter ?? true;
        _bpp = withAlpha ? 4 : 3;
        _bytesPerRow = width * _bpp;

        var opts = options ?? new PngEncodeOptions();
        _idat = new IdatChunkStream(output, opts.IdatChunkSize);
        // leaveOpen: true —— 压缩流收尾后还要把 IDAT 的尾巴块写掉，不能让 Dispose 连带关掉包装流
        _zlib = new ZLibStream(_idat, opts.CompressionLevel, leaveOpen: true);

        _encLine = new byte[_bytesPerRow];
        _prevLine = new byte[_bytesPerRow];
        _cand = new byte[1 + _bytesPerRow];
        _best = new byte[1 + _bytesPerRow];

        WriteSignatureAndHeader();
    }

    /// <summary>已写入的行数。</summary>
    public int RowsWritten { get; private set; }

    /// <summary>写入一行 RGBA（长度必须为 Width*4）。是否丢 alpha 由构造时的 withAlpha 决定。</summary>
    public void WriteRgbaRow(ReadOnlySpan<byte> rgbaRow)
    {
        if (_finished) throw new ObjectDisposedException(nameof(PngStreamWriter));
        if (RowsWritten >= _height)
            throw new InvalidOperationException("写入行数超过 IHDR 声明的高度，参数与像素不一致");
        if (rgbaRow.Length != _width * 4)
            throw new ArgumentException($"行长应为 {_width * 4} 字节，实际 {rgbaRow.Length}", nameof(rgbaRow));

        try
        {
            if (_withAlpha)
            {
                rgbaRow.CopyTo(_encLine);
            }
            else
            {
                // 走到这里说明调用方已确认 alpha 全为 255，丢弃它不改变任何可见像素
                for (int p = 0, i = 0; p < _width; p++, i += 4)
                {
                    int o = p * 3;
                    _encLine[o] = rgbaRow[i];
                    _encLine[o + 1] = rgbaRow[i + 1];
                    _encLine[o + 2] = rgbaRow[i + 2];
                }
            }

            EmitFilteredRow();
            Array.Copy(_encLine, _prevLine, _bytesPerRow);
            RowsWritten++;
        }
        catch
        {
            _faulted = true;   // 记住"已经断了"，收尾时就不再补一句会盖掉首因的异常
            throw;
        }
    }

    /// <summary>显式收尾（幂等）。</summary>
    public void Finish()
    {
        if (_finished) return;
        _finished = true;

        if (_faulted)
        {
            // 已经出错：安静释放压缩流即可。此时补写 IEND 会造出一个"结构完整但内容截断"的
            // 假文件，比直接失败更糟；真正的异常正沿调用栈向外传播，交给上层删除临时文件。
            try { _zlib.Dispose(); } catch { /* 释放期的二次异常一律忽略，保住首因 */ }
            return;
        }

        if (RowsWritten != _height)
            throw new InvalidOperationException(
                $"PNG 只写了 {RowsWritten} 行，声明是 {_height} 行；文件不完整，已中止收尾");
        _zlib.Dispose();   // 冲刷 zlib 尾块，剩余字节进入 _idat
        _idat.Finish();    // 写最后一个可能不足整块的 IDAT
        WriteChunk(PngChunks.ChunkIend, ReadOnlySpan<byte>.Empty);
        _output.Flush();
    }

    /// <summary>
    /// 释放。<b>刻意不抛异常</b>：Dispose 常常是在异常展开途中被调用的（例如导出被用户取消），
    /// 这时候再从收尾处抛一个"文件不完整"，就会把真正的首因（取消/中断）盖掉——
    /// 上一轮就是这么把"已取消"报成"写第 1 片时失败"的。
    /// 需要严格检查（行数不足就报错）请显式调用 <see cref="Finish"/>。
    /// 无论走哪条路，都不会写出一个"结构完整但内容被截断"的假文件。
    /// </summary>
    public void Dispose()
    {
        if (!_finished && RowsWritten != _height) _faulted = true;
        try { Finish(); } catch { /* 展开期不再追加异常 */ }
    }

    // ───────────────────────── 内部 ─────────────────────────

    private void WriteSignatureAndHeader()
    {
        _output.Write(PngChunks.Signature);
        Span<byte> ihdr = stackalloc byte[13];
        PngChunks.WriteUInt32BigEndian(ihdr, (uint)_width);
        PngChunks.WriteUInt32BigEndian(ihdr.Slice(4), (uint)_height);
        ihdr[8] = 8;   // 位深固定 8：本项目内部就是 RGBA8
        ihdr[9] = (byte)(_withAlpha ? PngColorType.Rgba : PngColorType.Truecolor);
        ihdr[10] = 0;  // deflate
        ihdr[11] = 0;  // 标准滤波
        ihdr[12] = 0;  // 非隔行
        WriteChunk(PngChunks.ChunkIhdr, ihdr);
    }

    private void WriteChunk(uint type, ReadOnlySpan<byte> data)
    {
        Span<byte> header = stackalloc byte[8];
        PngChunks.WriteUInt32BigEndian(header, (uint)data.Length);
        header[4] = (byte)(type >> 24);
        header[5] = (byte)(type >> 16);
        header[6] = (byte)(type >> 8);
        header[7] = (byte)type;
        _output.Write(header);
        _output.Write(data);

        Span<byte> tail = stackalloc byte[4];
        // CRC 覆盖"块类型 + 块数据"，不含长度字段
        PngChunks.WriteUInt32BigEndian(tail, PngChunks.Crc32(header.Slice(4, 4), data));
        _output.Write(tail);
    }

    /// <summary>为当前行选滤波方式并写入压缩流。</summary>
    private void EmitFilteredRow()
    {
        if (!_adaptive)
        {
            _best[0] = 0;
            _encLine.CopyTo(_best, 1);
            _zlib.Write(_best, 0, 1 + _bytesPerRow);
            return;
        }

        // 五种滤波各算一遍，取"有符号解释下绝对值之和"最小者（libpng 同款启发式）。
        // 它只影响压缩率，不影响还原出的像素。
        long bestScore = long.MaxValue;
        int bestFilter = 0;
        for (int f = 0; f <= 4; f++)
        {
            BuildFilter(f, _cand);
            long score = 0;
            for (int i = 1; i <= _bytesPerRow; i++)
            {
                // 必须先转 int 再取绝对值：滤波字节 0x80 作为有符号数是 sbyte.MinValue，
                // 而 Math.Abs(sbyte) 对 -128 会抛 OverflowException（"取负非法"）。
                // 这是自适应滤波最常见的一个坑，写在这里防止下一轮又踩回去。
                score += Math.Abs((int)(sbyte)_cand[i]);
            }
            if (score < bestScore)
            {
                bestScore = score;
                bestFilter = f;
                Array.Copy(_cand, _best, _bytesPerRow + 1);
            }
        }
        _ = bestFilter; // 滤波类型已写进 _best[0]
        _zlib.Write(_best, 0, 1 + _bytesPerRow);
    }

    /// <summary>按指定滤波类型生成一行数据（dst[0] 为滤波类型字节）。</summary>
    private void BuildFilter(int filterType, byte[] dst)
    {
        dst[0] = (byte)filterType;
        ReadOnlySpan<byte> cur = _encLine;
        ReadOnlySpan<byte> prev = _prevLine;

        switch (filterType)
        {
            case 0:
                cur.CopyTo(dst.AsSpan(1, _bytesPerRow));
                break;
            case 1: // Sub
                for (int i = 0; i < _bytesPerRow; i++)
                    dst[1 + i] = (byte)(cur[i] - (i >= _bpp ? cur[i - _bpp] : 0));
                break;
            case 2: // Up
                for (int i = 0; i < _bytesPerRow; i++)
                    dst[1 + i] = (byte)(cur[i] - prev[i]);
                break;
            case 3: // Average
                for (int i = 0; i < _bytesPerRow; i++)
                {
                    int left = i >= _bpp ? cur[i - _bpp] : 0;
                    dst[1 + i] = (byte)(cur[i] - ((left + prev[i]) >> 1));
                }
                break;
            case 4: // Paeth
                for (int i = 0; i < _bytesPerRow; i++)
                {
                    int left = i >= _bpp ? cur[i - _bpp] : 0;
                    int upLeft = i >= _bpp ? prev[i - _bpp] : 0;
                    dst[1 + i] = (byte)(cur[i] - Paeth(left, prev[i], upLeft));
                }
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(filterType));
        }
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        if (pa <= pb && pa <= pc) return a;
        return pb <= pc ? b : c;
    }

    /// <summary>
    /// 把 zlib 压缩输出切成 IDAT 块的包装流。
    /// PNG 要求压缩流被装进一个或多个 IDAT；除最后一个之外都必须是完整长度。
    /// </summary>
    private sealed class IdatChunkStream : Stream
    {
        private readonly Stream _inner;
        private readonly byte[] _buffer;
        private int _used;
        private static readonly byte[] TypeBytes = { 0x49, 0x44, 0x41, 0x54 }; // "IDAT"

        public IdatChunkStream(Stream inner, int chunkSize)
        {
            _inner = inner;
            _buffer = new byte[chunkSize];
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }
        public override int Read(byte[] b, int i, int c) => throw new NotSupportedException();
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            while (count > 0)
            {
                int take = Math.Min(count, _buffer.Length - _used);
                Buffer.BlockCopy(buffer, offset, _buffer, _used, take);
                _used += take;
                offset += take;
                count -= take;
                if (_used == _buffer.Length) FlushChunk();
            }
        }

        /// <summary>把不足一块的尾巴写出去（最后一个 IDAT 允许短于常规块尺寸）。</summary>
        public void Finish()
        {
            if (_used > 0) FlushChunk();
        }

        private void FlushChunk()
        {
            Span<byte> header = stackalloc byte[8];
            PngChunks.WriteUInt32BigEndian(header, (uint)_used);
            TypeBytes.CopyTo(header.Slice(4, 4));
            _inner.Write(header);
            _inner.Write(_buffer, 0, _used);

            Span<byte> tail = stackalloc byte[4];
            PngChunks.WriteUInt32BigEndian(tail,
                PngChunks.Crc32(TypeBytes, _buffer.AsSpan(0, _used)));
            _inner.Write(tail);
            _used = 0;
        }
    }
}
