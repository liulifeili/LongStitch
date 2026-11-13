using LongStitch.Core.Geometry;

namespace LongStitch.Core.Imaging.Tiff;

/// <summary>一次 TIFF 解码的结果：像素 + 界面要如实说出的那几个事实。</summary>
/// <param name="Image">RGBA8888 像素。</param>
/// <param name="Width">ImageWidth。</param>
/// <param name="Height">ImageLength。</param>
/// <param name="SamplesPerPixel">258/277 决定的样本数（3=RGB，4=RGBA）。</param>
/// <param name="StripCount">条带数量。</param>
public sealed record TiffDecodeResult(
    RgbaImage Image, int Width, int Height, int SamplesPerPixel, int StripCount);

/// <summary>
/// 基线 TIFF 读取器（小端、未压缩、条带布局、8 位/样本、3 或 4 样本）。
///
/// ═══ 为什么要自己写一个 ═══
/// 两件事都需要它：
///   ① 导出 TIFF 后要有**不依赖系统解码器**的读回校验——否则"自己写的编码器"和"系统解码器"
///      一起把同一处布局错读成同一个样子，判据就退化成自证；
///   ② 导入 .tif 时不再只能走 GDI+（系统解码器对部分变体不支持时会给出难以理解的错误）。
///
/// ═══ 明确不支持的（一律抛错，绝不硬解） ═══
/// 大端（MM）、压缩（LZW/Deflate/CCITT…）、平铺（tile）布局、位深非 8、样本数不是 3/4、
/// 调色板（Palette）解释方式。这些都在异常里点名是哪一条，因为它们都意味着
/// "如果我继续读，像素位置或数值就会是错的"。
/// </summary>
public static class TiffDecoder
{
    /// <summary>从内存解码。失败抛 <see cref="InvalidDataException"/>，消息即界面可读原因。</summary>
    public static TiffDecodeResult Decode(byte[] file)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (file.Length < 8) throw new InvalidDataException("文件太短，不是 TIFF");
        if (file[0] == 'M' && file[1] == 'M')
            throw new InvalidDataException("只支持小端（II）TIFF；这份是大端（MM）");
        if (file[0] != 'I' || file[1] != 'I')
            throw new InvalidDataException("文件头不是 II，扩展名可能与实际格式不符");
        if (ReadU16(file, 2) != 42)
            throw new InvalidDataException($"魔数不是 42（实际 {ReadU16(file, 2)}），不是基线 TIFF");

        int ifd = (int)ReadU32(file, 4);
        if (ifd <= 0 || ifd + 2 > file.Length)
            throw new InvalidDataException($"IFD 偏移 {ifd} 越界");

        int count = ReadU16(file, ifd);
        if (ifd + 2 + count * 12 + 4 > file.Length)
            throw new InvalidDataException($"IFD 声明 {count} 项，超出文件长度");

        // ── 收集需要的 tag ──
        int width = 0, height = 0, compression = -1, photometric = -1;
        int samplesPerPixel = 1, rowsPerStrip = 0;
        int[]? bits = null;
        int[]? stripOffsets = null;
        int[]? stripBytes = null;
        for (int i = 0; i < count; i++)
        {
            int entry = ifd + 2 + i * 12;
            ushort tag = ReadU16(file, entry);
            ushort type = ReadU16(file, entry + 2);
            uint cnt = ReadU32(file, entry + 4);
            int valueField = entry + 8;

            switch (tag)
            {
                case 256: width = (int)ReadValue(file, type, cnt, valueField); break;
                case 257: height = (int)ReadValue(file, type, cnt, valueField); break;
                case 258: bits = ReadArray(file, type, cnt, valueField); break;
                case 259: compression = (int)ReadValue(file, type, cnt, valueField); break;
                case 262: photometric = (int)ReadValue(file, type, cnt, valueField); break;
                case 273: stripOffsets = ReadArray(file, type, cnt, valueField); break;
                case 277: samplesPerPixel = (int)ReadValue(file, type, cnt, valueField); break;
                case 278: rowsPerStrip = (int)ReadValue(file, type, cnt, valueField); break;
                case 279: stripBytes = ReadArray(file, type, cnt, valueField); break;
                case 322: throw new InvalidDataException("这是平铺（tile）布局的 TIFF，本读取器只支持条带");
            }
        }

        if (width <= 0 || height <= 0)
            throw new InvalidDataException($"TIFF 尺寸为 {width}×{height}，无效");
        if (compression != 1)
            throw new InvalidDataException($"只支持未压缩（compression=1）TIFF，这份是 {compression}");
        if (photometric != 2 && photometric != 1)
            throw new InvalidDataException($"只支持 RGB(2)/灰度(1) 解释方式，这份是 {photometric}");
        if (samplesPerPixel is not (3 or 4))
            throw new InvalidDataException($"只支持 3 或 4 个样本，这份是 {samplesPerPixel} 个");
        if (bits is null || bits.Length != samplesPerPixel || bits.Any(b => b != 8))
            throw new InvalidDataException($"只支持每样本 8 位；BitsPerSample={(bits is null ? "缺失" : string.Join(",", bits))}");
        if (stripOffsets is null || stripBytes is null || stripOffsets.Length != stripBytes.Length)
            throw new InvalidDataException("缺少 StripOffsets/StripByteCounts，或两者条数不一致");
        if (rowsPerStrip <= 0)
            rowsPerStrip = height;   // 单条带的老文件常省略这一项
        if (!new IntSize(width, height).CanAllocateBuffer)
            throw new InvalidDataException($"TIFF 尺寸 {width}×{height} 超出可处理上限");

        // ── 按条带读回像素 ──
        var image = new RgbaImage(width, height);
        int rowBytes = width * samplesPerPixel;
        int targetRow = 0;
        for (int s = 0; s < stripOffsets.Length && targetRow < height; s++)
        {
            long start = stripOffsets[s];
            int bytes = stripBytes[s];
            if (start < 0 || start + bytes > file.Length)
                throw new InvalidDataException(
                    $"第 {s + 1} 条带越界：偏移 {start} 长 {bytes}，文件只有 {file.Length} 字节");

            int rowsHere = Math.Min(rowsPerStrip, height - targetRow);
            long need = (long)rowsHere * rowBytes;
            // 允许条带比推算的多一点（写家按整除留的行数），但不能少
            if (bytes < need)
                throw new InvalidDataException(
                    $"第 {s + 1} 条带只有 {bytes} 字节，按 {rowsHere} 行×{rowBytes} 需要 {need} 字节");

            for (int r = 0; r < rowsHere; r++)
            {
                Span<byte> dst = image.Row(targetRow + r);
                for (int x = 0; x < width; x++)
                {
                    int from = (int)(start + (long)r * rowBytes + x * samplesPerPixel);
                    dst[x * 4] = file[from];
                    if (samplesPerPixel == 4)
                    {
                        dst[x * 4 + 1] = file[from + 1];
                        dst[x * 4 + 2] = file[from + 2];
                        dst[x * 4 + 3] = file[from + 3];
                    }
                    else
                    {
                        dst[x * 4 + 1] = file[from + 1];
                        dst[x * 4 + 2] = file[from + 2];
                        dst[x * 4 + 3] = 255;
                    }
                }
            }
            targetRow += rowsHere;
        }

        if (targetRow != height)
            throw new InvalidDataException(
                $"条带加起来只有 {targetRow} 行，声明 {height} 行——文件被截断");

        return new TiffDecodeResult(image, width, height, samplesPerPixel, stripOffsets.Length);
    }

    /// <summary>不抛异常的版本，供导入流程逐项收集失败原因。</summary>
    public static bool TryDecode(byte[] file, out TiffDecodeResult? result, out string? error)
    {
        try { result = Decode(file); error = null; return true; }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or NotSupportedException
                                      or OutOfMemoryException)
        { result = null; error = ex.Message; return false; }
    }

    /// <summary>只看字节头判断是不是 TIFF（II/MM + 42）。用于导入时按内容而不是扩展名决定解码路径。</summary>
    public static bool LooksLikeTiff(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 4 && ((bytes[0] == 'I' && bytes[1] == 'I') || (bytes[0] == 'M' && bytes[1] == 'M'))
        && (bytes[2] == 42 || bytes[3] == 42);

    private static ushort ReadU16(byte[] f, int i) => (ushort)(f[i] | (f[i + 1] << 8));

    private static uint ReadU32(byte[] f, int i) =>
        (uint)f[i] | ((uint)f[i + 1] << 8) | ((uint)f[i + 2] << 16) | ((uint)f[i + 3] << 24);

    /// <summary>读一个 count=1 的值（SHORT 放在低两字节，LONG/BYTE 直接是值）。</summary>
    private static uint ReadValue(byte[] f, ushort type, uint count, int valueField)
    {
        if (count != 1)
        {
            // 单值字段里装了数组：跟着偏移去读，取第一个
            int[] arr = ReadArray(f, type, count, valueField);
            return arr.Length > 0 ? (uint)arr[0] : 0;
        }
        return type switch
        {
            3 => ReadU16(f, valueField),
            4 => ReadU32(f, valueField),
            1 => f[valueField],
            _ => throw new InvalidDataException($"tag 的值类型 {type} 不认识"),
        };
    }

    /// <summary>读一个数组型 tag：≤4 字节放字段内，否则按偏移读。</summary>
    private static int[] ReadArray(byte[] f, ushort type, uint count, int valueField)
    {
        int size = type switch { 1 or 7 => 1, 3 => 2, 4 => 4, _ => throw new InvalidDataException($"tag 的数组元素类型 {type} 不认识") };
        int n = checked((int)count);
        long total = (long)n * size;
        int offset = total <= 4 ? valueField : (int)ReadU32(f, valueField);
        if (offset < 0 || offset + total > f.Length)
            throw new InvalidDataException($"tag 数组（{n} 个 {size} 字节）偏移 {offset} 越界");

        var values = new int[n];
        for (int i = 0; i < n; i++)
        {
            int at = offset + i * size;
            values[i] = size switch { 1 => f[at], 2 => ReadU16(f, at), _ => (int)ReadU32(f, at) };
        }
        return values;
    }
}
