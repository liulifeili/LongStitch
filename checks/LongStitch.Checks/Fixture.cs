using System.IO.Compression;
using LongStitch.Core.Geometry;
using LongStitch.Core.Imaging;

namespace LongStitch.Checks;

/// <summary>
/// 测试素材工厂。
///
/// 重要：**这里故意自带一套独立的 PNG 写入实现**（自己的 CRC32、自己的分块、自己的 zlib 封装），
/// 不复用 LongStitch.Core 的任何代码。原因很直接——如果用被测实现自己生成输入、
/// 再用被测实现读回来比较，那只能证明"它和自己一致"，证不了"它对"。
/// 独立实现 + GDI+ 两条外部参照，才构成有效的交叉验证。
/// </summary>
public static class Fixture
{
    /// <summary>确定性伪随机（同一 seed 永远同一张图，保证电池可复现）。</summary>
    public static RgbaImage ScreenshotLike(int width, int height, int seed = 12345)
    {
        var img = new RgbaImage(width, height);
        uint state = (uint)seed | 1;
        int Next() { state = state * 1664525u + 1013904223u; return (int)(state >> 8); }

        // 背景：浅灰底 + 轻微竖向渐变，让 PNG 的 Up/Paeth 滤波真的被用到
        for (int y = 0; y < height; y++)
        {
            byte g = (byte)(238 + (y % 7));
            Span<byte> row = img.Row(y);
            for (int x = 0; x < width; x++)
            {
                int o = x * 4;
                row[o] = g; row[o + 1] = g; row[o + 2] = (byte)(g + 2); row[o + 3] = 255;
            }
        }

        // 顶部"状态栏"：深色条 + 几个亮块当图标（模拟 iOS 截图的固定区）
        int barH = Math.Min(94, height / 4);
        img.Fill(new IntRect(0, 0, width, barH), new RgbaColor(28, 28, 30, 255));
        for (int i = 0; i < 6; i++)
            img.Fill(new IntRect(20 + i * (width / 8), barH / 3, width / 14, barH / 3),
                new RgbaColor(255, 255, 255, 255));

        // 中部"消息气泡"：白底圆角矩形（这里用直角矩形，够用）
        for (int i = 0; i < 8; i++)
        {
            int bw = 120 + Next() % Math.Max(1, width / 2);
            int bh = 40 + Next() % 60;
            int bx = (i % 2 == 0) ? 24 : Math.Max(0, width - bw - 40);
            int by = barH + 30 + i * (bh + 18);
            if (by + bh >= height) break;
            img.Fill(new IntRect(bx, by, bw, bh), new RgbaColor(255, 255, 255, 255));
            // 气泡里的"文字"：几条深色横线，提供匹配所需的水平纹理
            for (int t = 0; t < 3; t++)
                img.Fill(new IntRect(bx + 10, by + 10 + t * 12, Math.Max(8, bw - 20 - t * 17), 6),
                    new RgbaColor(28, 28, 30, 255));
        }

        // 右缘"滚动条"：细长高对比度竖条，清理检测的目标
        if (width > 30)
            img.Fill(new IntRect(width - 8, height / 3, 4, height / 3),
                new RgbaColor(120, 120, 128, 200));
        return img;
    }

    /// <summary>全不透明素材（用于验证"自动降为 RGB 但像素不变"）。</summary>
    public static RgbaImage OpaqueLike(int width, int height, int seed = 999)
    {
        var img = ScreenshotLike(width, height, seed);
        // ScreenshotLike 里的滚动条带 alpha=200，这里抹平成不透明
        for (int y = 0; y < height; y++)
        {
            Span<byte> row = img.Row(y);
            for (int x = 0; x < width; x++) row[x * 4 + 3] = 255;
        }
        return img;
    }

    /// <summary>
    /// 均匀单色的全不透明图（第四十六改专用夹具）。
    /// <b>为什么要单色</b>：用户圈的两处缺陷——缩小档瓦片边缘渗色留下的灰带、
    /// 最小缩放档内容之外铺的棋盘格——都是"本不该存在的颜色"。
    /// 截图样夹具的内容本身花花绿绿，判据得先猜每个像素"应该"是什么色；
    /// 单色则把判据写成一句话：内容矩形内每枚像素必须等于这一色，内容外每枚必须等于画布底色。
    /// </summary>
    public static RgbaImage Solid(int width, int height, RgbaColor color)
    {
        var img = new RgbaImage(width, height);
        img.Fill(color);
        return img;
    }

    // ═════════════════ 独立 PNG 写入（测试专用，不进产品代码） ═════════════════

    private static readonly uint[] CrcTable = BuildTable();

    private static uint[] BuildTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }

    private static uint Crc32(byte[] type, byte[] data)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (byte b in type) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        foreach (byte b in data) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }

    private static void BE(Span<byte> dst, uint v)
    {
        dst[0] = (byte)(v >> 24); dst[1] = (byte)(v >> 16); dst[2] = (byte)(v >> 8); dst[3] = (byte)v;
    }

    /// <summary>把一个块追加到流：长度 + 类型 + 数据 + CRC。</summary>
    public static void WriteChunk(Stream s, string type, byte[] data)
    {
        byte[] t = type.Select(c => (byte)c).ToArray();
        Span<byte> head = stackalloc byte[8];
        BE(head, (uint)data.Length);
        t.CopyTo(head.Slice(4, 4));
        s.Write(head);
        s.Write(data);
        Span<byte> crc = stackalloc byte[4];
        BE(crc, Crc32(t, data));
        s.Write(crc);
    }

    /// <summary>zlib 压缩（独立于被测实现，用 BCL 的 ZLibStream）。</summary>
    public static byte[] ZlibCompress(byte[] raw)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            z.Write(raw);
        return ms.ToArray();
    }

    /// <summary>
    /// 手工拼一个 PNG：签名 + IHDR(+可选 PLTE/tRNS) + 一个 IDAT + IEND。
    /// <paramref name="scanlines"/> 必须是"每行前置 1 字节滤波类型"的原始（未压缩）数据。
    /// </summary>
    public static byte[] MakePng(int width, int height, int bitDepth, int colorType,
        byte[] scanlines, byte[]? palette = null, byte[]? trns = null, bool interlace = false)
    {
        using var ms = new MemoryStream();
        ms.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });

        byte[] ihdr = new byte[13];
        BE(ihdr.AsSpan(0, 4), (uint)width);
        BE(ihdr.AsSpan(4, 4), (uint)height);
        ihdr[8] = (byte)bitDepth;
        ihdr[9] = (byte)colorType;
        ihdr[10] = 0; ihdr[11] = 0; ihdr[12] = (byte)(interlace ? 1 : 0);
        WriteChunk(ms, "IHDR", ihdr);
        if (palette is not null) WriteChunk(ms, "PLTE", palette);
        if (trns is not null) WriteChunk(ms, "tRNS", trns);
        WriteChunk(ms, "IDAT", ZlibCompress(scanlines));
        WriteChunk(ms, "IEND", Array.Empty<byte>());
        return ms.ToArray();
    }

    /// <summary>造一张 16 位 RGBA PNG（滤波全用 None）。用于验证"降位必须上报"。</summary>
    public static byte[] Png16BitRgba(int width, int height)
    {
        int rowBytes = width * 4 * 2;
        var raw = new byte[height * (1 + rowBytes)];
        for (int y = 0; y < height; y++)
        {
            int o = y * (1 + rowBytes);
            raw[o] = 0; // 滤波 None
            for (int x = 0; x < width; x++)
            {
                int p = o + 1 + x * 8;
                ushort r = (ushort)(x * 257 % 65536);
                ushort g = (ushort)(y * 257 % 65536);
                ushort b = (ushort)((x + y) * 517 % 65536);
                ushort a = 65535;
                raw[p] = (byte)(r >> 8); raw[p + 1] = (byte)r;
                raw[p + 2] = (byte)(g >> 8); raw[p + 3] = (byte)g;
                raw[p + 4] = (byte)(b >> 8); raw[p + 5] = (byte)b;
                raw[p + 6] = (byte)(a >> 8); raw[p + 7] = (byte)a;
            }
        }
        return MakePng(width, height, 16, 6, raw);
    }

    /// <summary>造一张 8 位调色板 PNG（含 tRNS），用于验证索引展开的正确性。</summary>
    public static byte[] PalettePng(int width, int height, out byte[] palette, out byte[] trns)
    {
        palette = new byte[]
        {
            255, 0, 0,     // 索引 0
            0, 255, 0,     // 索引 1
            0, 0, 255,     // 索引 2
            255, 255, 0,   // 索引 3
        };
        trns = new byte[] { 255, 128, 0, 255 }; // 索引 2 完全透明，索引 1 半透明

        int rowBytes = width; // colorType=3、位深 8 → 每像素 1 字节
        var raw = new byte[height * (1 + rowBytes)];
        for (int y = 0; y < height; y++)
        {
            raw[y * (1 + rowBytes)] = 0;
            for (int x = 0; x < width; x++)
                raw[y * (1 + rowBytes) + 1 + x] = (byte)((x + y) % 4);
        }
        return MakePng(width, height, 8, 3, raw, palette, trns);
    }

    /// <summary>造一张声明为 Adam7 隔行的 PNG（数据本身无所谓，解码器应在解析头部时就拒绝）。</summary>
    public static byte[] InterlacedPng(int width, int height)
    {
        var raw = new byte[height * (1 + width * 4)];
        return MakePng(width, height, 8, 6, raw, interlace: true);
    }

    /// <summary>
    /// 造一张<b>真正的</b> Adam7 隔行 PNG：按七遍网格把像素撒成各遍自己的扫描线，
    /// 每遍行宽按<b>该遍宽度</b>算、滤波一律用 None。
    ///
    /// 这段代码故意照着规范独立写一遍，不复用 Core 的解码器——
    /// 否则"编码器与解码器共享同一套遍数算术"会让两边一起错成同一个样子，
    /// 判据就退化成"它和自己一致"。
    /// </summary>
    public static byte[] InterlacedPngOf(RgbaImage image)
    {
        int w = image.Width, h = image.Height;
        int[] xOff = { 0, 4, 0, 2, 0, 1, 0 };
        int[] yOff = { 0, 0, 4, 0, 2, 0, 1 };
        int[] xStep = { 8, 8, 4, 4, 2, 2, 1 };
        int[] yStep = { 8, 8, 8, 4, 4, 2, 2 };

        using var ms = new MemoryStream();
        for (int pass = 0; pass < 7; pass++)
        {
            int pw = w <= xOff[pass] ? 0 : (w - xOff[pass] + xStep[pass] - 1) / xStep[pass];
            int ph = h <= yOff[pass] ? 0 : (h - yOff[pass] + yStep[pass] - 1) / yStep[pass];
            if (pw <= 0 || ph <= 0) continue;

            for (int py = 0; py < ph; py++)
            {
                ms.WriteByte(0);   // 滤波类型 None：让判据只考"遍数算术"这一件事
                int sourceY = yOff[pass] + py * yStep[pass];
                ReadOnlySpan<byte> row = image.Row(sourceY);
                for (int px = 0; px < pw; px++)
                {
                    int o = (xOff[pass] + px * xStep[pass]) * 4;
                    ms.WriteByte(row[o]); ms.WriteByte(row[o + 1]);
                    ms.WriteByte(row[o + 2]); ms.WriteByte(row[o + 3]);
                }
            }
        }
        return MakePng(w, h, 8, 6, ms.ToArray(), interlace: true);
    }

    /// <summary>同一张图的非隔行版本（同样用本工厂自己的 PNG 写法），用来与隔行版对照。</summary>
    public static byte[] PlainPngOf(RgbaImage image)
    {
        int w = image.Width, h = image.Height, rowBytes = w * 4;
        var raw = new byte[h * (1 + rowBytes)];
        for (int y = 0; y < h; y++)
        {
            int o = y * (1 + rowBytes);
            raw[o] = 0;
            image.Row(y).Slice(0, rowBytes).CopyTo(raw.AsSpan(o + 1, rowBytes));
        }
        return MakePng(w, h, 8, 6, raw);
    }

    /// <summary>把任意 PNG 字节里的某个字节改掉，用来验证 CRC 守卫真的会拦。</summary>
    public static byte[] Corrupt(byte[] png, int index)
    {
        var copy = (byte[])png.Clone();
        copy[index] ^= 0xFF;
        return copy;
    }

    /// <summary>IDAT 数据区的中间位置（跳过签名 8 + IHDR 25 + PLTE 之类，取靠后的压缩数据）。</summary>
    public static int MiddleOfIdat(byte[] png) => png.Length / 2;
}
