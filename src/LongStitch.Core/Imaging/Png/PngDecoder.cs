using System.IO.Compression;
using LongStitch.Core.Geometry;

namespace LongStitch.Core.Imaging.Png;

/// <summary>
/// 纯托管 PNG 解码器（无损路径的一半，另一半见 <see cref="PngEncoder"/>）。
///
/// 支持：colorType 0/2/3/4/6，位深 1/2/4/8/16，tRNS 透明，以及 <b>Adam7 隔行</b>。
/// 隔行不是截图的常见形态，但"读不出来"与"读错了"是两件事：
/// 拒绝至少是诚实的，而把七遍的像素按非隔行的方式硬解会静默产出一张错图——
/// 所以这里实现完整的反交错，并把每遍的行数/字节数都当作可核对的账。
///
/// 输出统一为 RGBA8888 非预乘。除 16 位降位外不做任何数值改动：
/// 不做色彩管理、不做伽马校正、不做反预乘——这三件事正是别的库最容易"顺手"做掉的。
/// </summary>
public static class PngDecoder
{
    /// <summary>从内存解码。失败抛 <see cref="PngException"/>，消息即界面可读原因。</summary>
    public static PngDecodeResult Decode(byte[] fileBytes)
    {
        ArgumentNullException.ThrowIfNull(fileBytes);
        if (fileBytes.Length < 8 ||
            !fileBytes.AsSpan(0, PngChunks.Signature.Length).SequenceEqual(PngChunks.Signature))
            throw new PngException("文件头不是 PNG 签名，扩展名可能与实际格式不符");

        // ── 1. 遍历分块，收集头部与调色板/透明信息，拼接 IDAT ──────────────
        int width = 0, height = 0, bitDepth = 0, colorTypeRaw = 0;
        bool interlaced = false;
        byte[]? palette = null;
        byte[]? trns = null;
        byte[]? exif = null;
        bool hasIcc = false, hasGamma = false, hasChrm = false, hasBkgd = false, hasSrgb = false;
        using var idat = new MemoryStream();

        var cursor = new PngChunkCursor(fileBytes);
        while (cursor.MoveNext())
        {
            switch (cursor.Type)
            {
                case PngChunks.ChunkIhdr:
                    if (cursor.Data.Length < 13) throw new PngException("IHDR 长度异常");
                    width = (int)PngChunks.ReadUInt32BigEndian(cursor.Data);
                    height = (int)PngChunks.ReadUInt32BigEndian(cursor.Data.Slice(4));
                    bitDepth = cursor.Data[8];
                    colorTypeRaw = cursor.Data[9];
                    if (cursor.Data[10] != 0) throw new PngException("只支持 deflate 压缩（compression=0）");
                    if (cursor.Data[11] != 0) throw new PngException("只支持标准滤波（filter=0）");
                    interlaced = cursor.Data[12] != 0;
                    break;

                case PngChunks.ChunkPlte:
                    palette = cursor.Data.ToArray();
                    break;

                case PngChunks.ChunkTrns:
                    trns = cursor.Data.ToArray();
                    break;

                case PngChunks.ChunkIdat:
                    idat.Write(cursor.Data);
                    break;

                case PngChunks.ChunkExif: exif = cursor.Data.ToArray(); break;
                case PngChunks.ChunkIccp: hasIcc = true; break;
                case PngChunks.ChunkGama: hasGamma = true; break;
                case PngChunks.ChunkChrm: hasChrm = true; break;
                case PngChunks.ChunkBkgd: hasBkgd = true; break;
                case PngChunks.ChunkSrgb: hasSrgb = true; break;
                case PngChunks.ChunkIend:
                    goto done;
            }
        }
    done:

        var colorType = (PngColorType)colorTypeRaw;
        if (width <= 0 || height <= 0)
            throw new PngException($"PNG 尺寸为 {width}×{height}，无效");
        if (!IsKnownColorType(colorType))
            throw new PngException($"不支持的 PNG colorType={colorTypeRaw}");
        if (!IsValidBitDepth(colorType, bitDepth))
            throw new PngException($"colorType={colorType} 下位深 {bitDepth} 不合法");
        if (colorType == PngColorType.Indexed && palette is null)
            throw new PngException("调色板 PNG 缺少 PLTE 块");
        if (!new IntSize(width, height).CanAllocateBuffer)
            throw new PngException($"PNG 尺寸 {width}×{height} 超出可处理上限，请分段处理");

        int channels = ChannelsOf(colorType);

        // ── 2. 解压 IDAT，并按扫描线反滤波 ────────────────────────────────
        int bytesPerRow = (width * channels * bitDepth + 7) / 8;
        long rawSize = interlaced
            ? InterlacedRawSize(width, height, channels, bitDepth)
            : (long)height * (1 + bytesPerRow);
        byte[] inflated = Inflate(idat, rawSize);

        var image = new RgbaImage(width, height);

        if (interlaced)
        {
            // 隔行：七遍各自独立滤波，展开后再按 Adam7 的网格撒回整张图（见 DecodeInterlaced）
            DecodeInterlaced(inflated, width, height, channels, bitDepth, colorType,
                palette, trns, image);
        }
        else
        {
            byte[] unfiltered = Unfilter(inflated, height, bytesPerRow,
                filterBpp: Math.Max(1, channels * bitDepth / 8));
            ExpandToRgba(unfiltered, width, height, bytesPerRow, bitDepth, colorType,
                palette, trns, image);
        }

        var info = new PngFormatInfo
        {
            Width = width,
            Height = height,
            BitDepth = bitDepth,
            ColorType = colorType,
            Interlaced = interlaced,
            HasTransparency = trns is not null
                || colorType is PngColorType.GrayAlpha or PngColorType.Rgba,
            HasIccProfile = hasIcc,
            HasGamma = hasGamma,
            HasChromaticities = hasChrm,
            HasBackground = hasBkgd,
            HasSrgbChunk = hasSrgb,
            ExifOrientation = exif is null ? 1 : PngExif.ReadOrientation(exif),
        };
        return new PngDecodeResult(image, info);
    }

    /// <summary>从磁盘解码。调用方负责把 IOException 等转成"逐项提示"。</summary>
    public static PngDecodeResult DecodeFile(string path) => Decode(File.ReadAllBytes(path));

    /// <summary>
    /// 只看字节头判断是不是 PNG。<see cref="PngChunks"/> 是内部类型，
    /// 上层（导入流程）需要"按内容而不是扩展名决定走哪条解码路径"，所以在这里开一个公开入口。
    /// </summary>
    public static bool LooksLikePng(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= PngChunks.Signature.Length && bytes[..PngChunks.Signature.Length]
            .SequenceEqual(PngChunks.Signature);

    /// <summary>不抛异常的版本，供导入流程逐项收集失败原因。</summary>
    public static bool TryDecode(byte[] fileBytes, out PngDecodeResult? result, out string? error)
    {
        try { result = Decode(fileBytes); error = null; return true; }
        catch (Exception ex) when (ex is PngException or InvalidDataException or NotSupportedException
                                      or OutOfMemoryException or IOException)
        { result = null; error = ex.Message; return false; }
    }

    /// <summary>只读头部拿尺寸与格式事实，不做解压。用于导入时的快速校验与信息展示。</summary>
    public static PngFormatInfo ReadFormat(byte[] fileBytes)
    {
        if (fileBytes.Length < 8 ||
            !fileBytes.AsSpan(0, PngChunks.Signature.Length).SequenceEqual(PngChunks.Signature))
            throw new PngException("文件头不是 PNG 签名");

        var cursor = new PngChunkCursor(fileBytes);
        int width = 0, height = 0, bitDepth = 0, colorTypeRaw = 0;
        bool interlaced = false, hasIcc = false, hasGamma = false, hasChrm = false, hasBkgd = false, hasSrgb = false;
        byte[]? trns = null;
        byte[]? exif = null;

        while (cursor.MoveNext())
        {
            switch (cursor.Type)
            {
                case PngChunks.ChunkIhdr:
                    width = (int)PngChunks.ReadUInt32BigEndian(cursor.Data);
                    height = (int)PngChunks.ReadUInt32BigEndian(cursor.Data.Slice(4));
                    bitDepth = cursor.Data[8];
                    colorTypeRaw = cursor.Data[9];
                    interlaced = cursor.Data[12] != 0;
                    break;
                case PngChunks.ChunkTrns: trns = cursor.Data.ToArray(); break;
                case PngChunks.ChunkExif: exif = cursor.Data.ToArray(); break;
                case PngChunks.ChunkIccp: hasIcc = true; break;
                case PngChunks.ChunkGama: hasGamma = true; break;
                case PngChunks.ChunkChrm: hasChrm = true; break;
                case PngChunks.ChunkBkgd: hasBkgd = true; break;
                case PngChunks.ChunkSrgb: hasSrgb = true; break;
                case PngChunks.ChunkIend: goto finished;
            }
        }
    finished:
        var ct = (PngColorType)colorTypeRaw;
        return new PngFormatInfo
        {
            Width = width,
            Height = height,
            BitDepth = bitDepth,
            ColorType = ct,
            Interlaced = interlaced,
            HasTransparency = trns is not null
                || ct is PngColorType.GrayAlpha or PngColorType.Rgba,
            HasIccProfile = hasIcc,
            HasGamma = hasGamma,
            HasChromaticities = hasChrm,
            HasBackground = hasBkgd,
            HasSrgbChunk = hasSrgb,
            ExifOrientation = exif is null ? 1 : PngExif.ReadOrientation(exif),
        };
    }

    // ───────────────────────── 内部实现 ─────────────────────────

    private static bool IsKnownColorType(PngColorType t) =>
        t is PngColorType.Gray or PngColorType.Truecolor or PngColorType.Indexed
            or PngColorType.GrayAlpha or PngColorType.Rgba;

    private static bool IsValidBitDepth(PngColorType t, int bitDepth) => t switch
    {
        PngColorType.Gray => bitDepth is 1 or 2 or 4 or 8 or 16,
        PngColorType.Truecolor => bitDepth is 8 or 16,
        PngColorType.Indexed => bitDepth is 1 or 2 or 4 or 8,
        PngColorType.GrayAlpha => bitDepth is 8 or 16,
        PngColorType.Rgba => bitDepth is 8 or 16,
        _ => false,
    };

    private static int ChannelsOf(PngColorType t) => t switch
    {
        PngColorType.Gray or PngColorType.Indexed => 1,
        PngColorType.GrayAlpha => 2,
        PngColorType.Truecolor => 3,
        PngColorType.Rgba => 4,
        _ => throw new PngException($"未知 colorType：{t}"),
    };

    // ───────────────────────── Adam7 隔行 ─────────────────────────

    // 七遍网格的固定参数：起始列、起始行、列间距、行间距（PNG 规范 8.2 节）。
    // 第七遍的间距是 1×2，正好补齐前六遍没覆盖的每一行。
    private static readonly int[] Adam7StartCol = { 0, 4, 0, 2, 0, 1, 0 };
    private static readonly int[] Adam7StartRow = { 0, 0, 4, 0, 2, 0, 1 };
    private static readonly int[] Adam7ColStep = { 8, 8, 4, 4, 2, 2, 1 };
    private static readonly int[] Adam7RowStep = { 8, 8, 8, 4, 4, 2, 2 };

    private static int Adam7PassWidth(int width, int pass) =>
        width <= Adam7StartCol[pass]
            ? 0
            : (width - Adam7StartCol[pass] + Adam7ColStep[pass] - 1) / Adam7ColStep[pass];

    private static int Adam7PassHeight(int height, int pass) =>
        height <= Adam7StartRow[pass]
            ? 0
            : (height - Adam7StartRow[pass] + Adam7RowStep[pass] - 1) / Adam7RowStep[pass];

    /// <summary>
    /// 隔行图解压后的总字节数：每遍都是"每行前置 1 字节滤波类型"，
    /// 行宽按<b>那一遍的宽度</b>算（不是整图宽度），所以七遍各不相同。
    /// 这个数用来校验 IDAT，短一行/多一行都会在这里被抓住。
    /// </summary>
    private static long InterlacedRawSize(int width, int height, int channels, int bitDepth)
    {
        long total = 0;
        for (int pass = 0; pass < 7; pass++)
        {
            int w = Adam7PassWidth(width, pass), h = Adam7PassHeight(height, pass);
            if (w <= 0 || h <= 0) continue;
            int rowBytes = (w * channels * bitDepth + 7) / 8;
            total += (long)h * (1 + rowBytes);
        }
        return total;
    }

    /// <summary>
    /// Adam7 反交错：逐遍取出"该遍自己的扫描线"→ 用<b>该遍自己的行宽</b>反滤波 →
    /// 展开成 RGBA → 按网格撒回整张图。
    ///
    /// 两个最容易写错的地方（都有判据盯着）：
    ///   ① 滤波是<b>按遍</b>独立的：上一遍的最后一行不是这一遍某一行的"上一行"，
    ///      所以必须切成七段分别 Unfilter，绝不能整条流一次反滤波；
    ///   ② 撒点用的是遍内坐标 × 间距 + 起点，行宽也要按遍内宽度算，
    ///      拿整图行宽去切这一遍的数据，像素会逐行错位。
    /// </summary>
    private static void DecodeInterlaced(byte[] inflated, int width, int height,
        int channels, int bitDepth, PngColorType colorType, byte[]? palette, byte[]? trns,
        RgbaImage image)
    {
        int filterBpp = Math.Max(1, channels * bitDepth / 8);
        int pos = 0;

        for (int pass = 0; pass < 7; pass++)
        {
            int w = Adam7PassWidth(width, pass), h = Adam7PassHeight(height, pass);
            if (w <= 0 || h <= 0) continue;

            int rowBytes = (w * channels * bitDepth + 7) / 8;
            long need = (long)h * (1 + rowBytes);
            if (need > int.MaxValue || pos + need > inflated.LongLength)
                throw new PngException(
                    $"隔行第 {pass + 1} 遍需要 {need} 字节，IDAT 只剩 {inflated.LongLength - pos} 字节——文件被截断");

            // 只切这一遍的那一段来反滤波：遍与遍之间没有"上一行"关系
            var block = new byte[(int)need];
            Array.Copy(inflated, pos, block, 0, block.Length);
            pos += block.Length;

            byte[] unfiltered = Unfilter(block, h, rowBytes, filterBpp);
            var sub = new RgbaImage(w, h);
            ExpandToRgba(unfiltered, w, h, rowBytes, bitDepth, colorType, palette, trns, sub);

            int x0 = Adam7StartCol[pass], y0 = Adam7StartRow[pass];
            int dx = Adam7ColStep[pass], dy = Adam7RowStep[pass];
            for (int sy = 0; sy < h; sy++)
            {
                int targetY = y0 + sy * dy;
                ReadOnlySpan<byte> sourceRow = sub.Row(sy);
                Span<byte> targetRow = image.Row(targetY);
                for (int sx = 0; sx < w; sx++)
                {
                    int targetX = x0 + sx * dx;
                    int from = sx * 4, to = targetX * 4;
                    targetRow[to] = sourceRow[from];
                    targetRow[to + 1] = sourceRow[from + 1];
                    targetRow[to + 2] = sourceRow[from + 2];
                    targetRow[to + 3] = sourceRow[from + 3];
                }
            }
        }

        // 七遍加起来必须正好用完：多了说明尾部有垃圾，少了上面已经报错
        if (pos != inflated.LongLength)
            throw new PngException(
                $"隔行 PNG 的 IDAT 比七遍所需的字节多 {inflated.LongLength - pos}，文件可能损坏");
    }

    private static byte[] Inflate(MemoryStream idat, long expectedRawSize)
    {
        try
        {
            idat.Position = 0;
            using var zlib = new ZLibStream(idat, CompressionMode.Decompress);
            // 初始容量只按 1MB 预留：长截图的原始扫描线可能有几百 MB，
            // 一次性按推算值预留会在"只是想读个头"的场景下白占内存。
            using var outMs = new MemoryStream(1 << 20);
            zlib.CopyTo(outMs);
            byte[] result = outMs.ToArray();
            if (result.LongLength != expectedRawSize)
                throw new PngException(
                    $"IDAT 解压后大小 {result.LongLength} 与头部推算的 {expectedRawSize} 不符");
            return result;
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException)
        {
            throw new PngException("IDAT 解压失败（zlib 流损坏）：" + ex.Message, ex);
        }
    }

    /// <summary>
    /// PNG 五种滤波的反变换：None / Sub / Up / Average / Paeth。
    /// 滤波是无损可逆的，所以这一步不引入任何精度损失；写错它才会。
    /// </summary>
    private static byte[] Unfilter(byte[] inflated, int height, int bytesPerRow, int filterBpp)
    {
        int stride = 1 + bytesPerRow;
        var output = new byte[height * bytesPerRow];

        for (int y = 0; y < height; y++)
        {
            byte filterType = inflated[y * stride];
            ReadOnlySpan<byte> line = inflated.AsSpan(y * stride + 1, bytesPerRow);
            Span<byte> cur = output.AsSpan(y * bytesPerRow, bytesPerRow);
            ReadOnlySpan<byte> prev = y == 0
                ? ReadOnlySpan<byte>.Empty
                : output.AsSpan((y - 1) * bytesPerRow, bytesPerRow);

            switch (filterType)
            {
                case 0: // None
                    line.CopyTo(cur);
                    break;

                case 1: // Sub：加左邻居
                {
                    for (int i = 0; i < bytesPerRow; i++)
                    {
                        int left = i >= filterBpp ? cur[i - filterBpp] : 0;
                        cur[i] = (byte)(line[i] + left);
                    }
                    break;
                }

                case 2: // Up：加上邻居
                    for (int i = 0; i < bytesPerRow; i++)
                    {
                        int up = prev.IsEmpty ? 0 : prev[i];
                        cur[i] = (byte)(line[i] + up);
                    }
                    break;

                case 3: // Average：加左右平均
                    for (int i = 0; i < bytesPerRow; i++)
                    {
                        int left = i >= filterBpp ? cur[i - filterBpp] : 0;
                        int up = prev.IsEmpty ? 0 : prev[i];
                        cur[i] = (byte)(line[i] + ((left + up) >> 1));
                    }
                    break;

                case 4: // Paeth：三邻居预测
                    for (int i = 0; i < bytesPerRow; i++)
                    {
                        int left = i >= filterBpp ? cur[i - filterBpp] : 0;
                        int up = prev.IsEmpty ? 0 : prev[i];
                        int upLeft = (prev.IsEmpty || i < filterBpp) ? 0 : prev[i - filterBpp];
                        cur[i] = (byte)(line[i] + Paeth(left, up, upLeft));
                    }
                    break;

                default:
                    throw new PngException($"第 {y} 行滤波类型 {filterType} 非法");
            }
        }
        return output;
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        if (pa <= pb && pa <= pc) return a;
        return pb <= pc ? b : c;
    }

    /// <summary>
    /// 把反滤波后的原始扫描线展开为 RGBA8888。
    /// 所有分支都只做"位深缩放到 8 位 + 通道复制 + 调色板查表"，不做任何色彩换算。
    /// </summary>
    private static void ExpandToRgba(byte[] raw, int width, int height, int bytesPerRow,
        int bitDepth, PngColorType colorType, byte[]? palette, byte[]? trns, RgbaImage image)
    {
        int channels = ChannelsOf(colorType);
        int maxSample = (1 << bitDepth) - 1;

        // tRNS 预解析：索引图是"每个索引一个 alpha"；灰度/真彩是"某个颜色值透明"
        byte[]? indexAlpha = null;
        int grayKey = -1;
        int rKey = -1, gKey = -1, bKey = -1;
        if (trns is not null)
        {
            switch (colorType)
            {
                case PngColorType.Indexed:
                    indexAlpha = new byte[palette!.Length / 3];
                    Array.Fill(indexAlpha, (byte)255);
                    for (int i = 0; i < Math.Min(trns.Length, indexAlpha.Length); i++)
                        indexAlpha[i] = trns[i];
                    break;
                case PngColorType.Gray:
                case PngColorType.GrayAlpha:
                    if (trns.Length >= 2) grayKey = PngChunks.ReadUInt16BigEndian(trns);
                    break;
                case PngColorType.Truecolor:
                case PngColorType.Rgba:
                    if (trns.Length >= 6)
                    {
                        rKey = PngChunks.ReadUInt16BigEndian(trns);
                        gKey = PngChunks.ReadUInt16BigEndian(trns.AsSpan(2));
                        bKey = PngChunks.ReadUInt16BigEndian(trns.AsSpan(4));
                    }
                    break;
            }
        }

        for (int y = 0; y < height; y++)
        {
            ReadOnlySpan<byte> line = raw.AsSpan(y * bytesPerRow, bytesPerRow);
            Span<byte> dst = image.Row(y);

            for (int x = 0; x < width; x++)
            {
                int o = x * 4;
                switch (colorType)
                {
                    case PngColorType.Gray:
                    {
                        int g = ReadSample(line, x, 0, channels, bitDepth);
                        byte v = Scale8(g, bitDepth, maxSample);
                        dst[o] = v; dst[o + 1] = v; dst[o + 2] = v;
                        dst[o + 3] = grayKey >= 0 && g == grayKey ? (byte)0 : (byte)255;
                        break;
                    }
                    case PngColorType.GrayAlpha:
                    {
                        byte v = Scale8(ReadSample(line, x, 0, channels, bitDepth), bitDepth, maxSample);
                        byte a = Scale8(ReadSample(line, x, 1, channels, bitDepth), bitDepth, maxSample);
                        dst[o] = v; dst[o + 1] = v; dst[o + 2] = v; dst[o + 3] = a;
                        break;
                    }
                    case PngColorType.Truecolor:
                    {
                        int r = ReadSample(line, x, 0, channels, bitDepth);
                        int g = ReadSample(line, x, 1, channels, bitDepth);
                        int b = ReadSample(line, x, 2, channels, bitDepth);
                        dst[o] = Scale8(r, bitDepth, maxSample);
                        dst[o + 1] = Scale8(g, bitDepth, maxSample);
                        dst[o + 2] = Scale8(b, bitDepth, maxSample);
                        bool transparent = rKey >= 0 && r == rKey && g == gKey && b == bKey;
                        dst[o + 3] = transparent ? (byte)0 : (byte)255;
                        break;
                    }
                    case PngColorType.Rgba:
                    {
                        dst[o] = Scale8(ReadSample(line, x, 0, channels, bitDepth), bitDepth, maxSample);
                        dst[o + 1] = Scale8(ReadSample(line, x, 1, channels, bitDepth), bitDepth, maxSample);
                        dst[o + 2] = Scale8(ReadSample(line, x, 2, channels, bitDepth), bitDepth, maxSample);
                        dst[o + 3] = Scale8(ReadSample(line, x, 3, channels, bitDepth), bitDepth, maxSample);
                        break;
                    }
                    case PngColorType.Indexed:
                    {
                        int idx = ReadSample(line, x, 0, channels, bitDepth);
                        int p = idx * 3;
                        if (palette is null || p + 2 >= palette.Length)
                        {
                            // 调色板越界：按全透明标记，而不是编一个颜色糊过去
                            dst[o] = dst[o + 1] = dst[o + 2] = 0; dst[o + 3] = 0;
                        }
                        else
                        {
                            dst[o] = palette[p];
                            dst[o + 1] = palette[p + 1];
                            dst[o + 2] = palette[p + 2];
                            dst[o + 3] = indexAlpha is null || idx >= indexAlpha.Length
                                ? (byte)255 : indexAlpha[idx];
                        }
                        break;
                    }
                }
            }
        }
    }

    /// <summary>读取第 x 个像素的第 channel 个通道的原始样本值（未缩放到 8 位）。</summary>
    private static int ReadSample(ReadOnlySpan<byte> line, int x, int channel, int channels, int bitDepth)
    {
        if (bitDepth == 8) return line[x * channels + channel];
        if (bitDepth == 16)
        {
            // 16 位样本按大端存放；这里返回高 8 位（降位），调用方 Scale8 会原样透传
            int i = (x * channels + channel) * 2;
            return (line[i] << 8) | line[i + 1];
        }
        int bitIndex = (x * channels + channel) * bitDepth;
        int byteIndex = bitIndex >> 3;
        int shiftInByte = 8 - bitDepth - (bitIndex & 7);
        return (line[byteIndex] >> shiftInByte) & ((1 << bitDepth) - 1);
    }

    /// <summary>
    /// 把任意位深样本缩放到 8 位。
    /// 1/2/4 位用四舍五入线性映射（0→0、max→255），16 位取高字节。
    /// 这两者都是"必须做且无法无损"的转换，因此由 <see cref="PngFormatInfo.RequiresBitDepthConversion"/>
    /// 与 LossyNotes 上报，绝不静默。
    /// </summary>
    private static byte Scale8(int sample, int bitDepth, int maxSample)
    {
        if (bitDepth == 8) return (byte)sample;
        if (bitDepth == 16) return (byte)(sample >> 8);
        return (byte)((sample * 255 + maxSample / 2) / maxSample);
    }
}
