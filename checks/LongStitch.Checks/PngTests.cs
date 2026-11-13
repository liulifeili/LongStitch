using System.Runtime.InteropServices;
using LongStitch.Core.Imaging;
using LongStitch.Core.Imaging.Png;
using DrawingBitmap = System.Drawing.Bitmap;
using DrawingImagelockMode = System.Drawing.Imaging.ImageLockMode;
using DrawingPixelFormat = System.Drawing.Imaging.PixelFormat;

namespace LongStitch.Checks;

/// <summary>PNG 编解码电池：无损路径的地基。</summary>
public static class PngTests
{
    public static void Register()
    {
        Battery.Add("png", "RGBA 往返逐字节无损", RgbaRoundTrip);
        Battery.Add("png", "全不透明自动降 RGB 且像素不变", OpaqueStripsAlphaLossless);
        Battery.Add("png", "GDI+ 独立解码核对", GdiPlusCrossCheck);
        Battery.Add("png", "坏 CRC 必须被拒", BadCrcRejected);
        Battery.Add("png", "16 位降位必须上报为有损", Bit16ReportedAsLossy);
        Battery.Add("png", "调色板 + tRNS 展开正确", PaletteAndTrns);
        Battery.Add("png", "隔行 PNG 的字节账对不上必须拒绝", InterlacedRejected);
        Battery.Add("png", "Adam7 隔行解成与非隔行逐字节相同", Adam7DecodesToSamePixels);
        Battery.Add("png", "行数不足时不留半成品", ShortFileThrows);
        Battery.Add("png", "eXIf 方向要从真 PNG 块里读出来", ExifOrientationLittleEndian);
        Battery.Add("png", "eXIf 大端序同样要读对", ExifOrientationBigEndian);
        Battery.Add("png", "坏 eXIf 退回 1 且不抛", ExifGarbageFallsBackToOne);
        Battery.Add("png", "EXIF 取向 1–8 的重排逐个对上独立复算", AllEightOrientationsMatchIndependentMapping);
        Battery.Add("png", "BMP 导入按行主序还原且行按 4 字节对齐", BmpImportRestoresPixels);
    }

    /// <summary>
    /// EXIF 取向的<b>像素重排</b>此前只有"解析方向值"的判据，实际重排（ApplyOrientation）没有——
    /// README 的缺口清单点名过这条。这里给每个像素打上 (x,y) 的唯一编码，
    /// 然后按 EXIF 规范各取向的定义<b>独立算一遍目标位置</b>（不复用被测实现的那张 switch 表），
    /// 逐个取向比对；顺带量尺寸交换与未知取向的行为。
    /// </summary>
    private static void AllEightOrientationsMatchIndependentMapping()
    {
        const int w = 6, h = 4;
        var src = new RgbaImage(w, h);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                src.SetPixel(x, y, new RgbaColor((byte)(x * 30 + 5), (byte)(y * 30 + 5), 7, 255));

        // 每个取向给一个"从目标坐标反查源坐标"的独立定义（EXIF 规范 6.4 节）
        static (int x, int y) SourceOf(int orientation, int tx, int ty, int width, int height) =>
            orientation switch
        {
            1 => (tx, ty),
            2 => (width - 1 - tx, ty),                       // 水平镜像
            3 => (width - 1 - tx, height - 1 - ty),          // 转 180°
            4 => (tx, height - 1 - ty),                      // 垂直镜像
            5 => (ty, tx),                                   // 沿主对角线转置
            6 => (ty, height - 1 - tx),                      // 顺时针 90°
            7 => (width - 1 - ty, height - 1 - tx),          // 沿副对角线转置
            8 => (width - 1 - ty, tx),                       // 逆时针 90°
            _ => (tx, ty),
        };

        for (int o = 1; o <= 8; o++)
        {
            RgbaImage got = LongStitch.App.Imaging.FileSourceResolver.ApplyOrientation(src, o);
            bool swapped = o is 5 or 6 or 7 or 8;
            int ew = swapped ? h : w, eh = swapped ? w : h;
            Battery.AssertEqual(ew, got.Width, $"取向 {o} 的宽");
            Battery.AssertEqual(eh, got.Height, $"取向 {o} 的高");

            for (int ty = 0; ty < eh; ty++)
                for (int tx = 0; tx < ew; tx++)
                {
                    (int sx, int sy) = SourceOf(o, tx, ty, w, h);
                    Battery.AssertEqual(src.GetPixel(sx, sy), got.GetPixel(tx, ty),
                        $"取向 {o} 的 ({tx},{ty}) ← 源 ({sx},{sy})");
                }
        }

        // 取向 0/1 原样返回；未知取向不许乱改尺寸（ReadFormat 那侧已把越界值退回 1，这里是第二道）
        Battery.Assert(ReferenceEquals(src, LongStitch.App.Imaging.FileSourceResolver.ApplyOrientation(src, 1)),
            "取向 1 应直接返回原图（不做无意义的复制）");
        RgbaImage unknown = LongStitch.App.Imaging.FileSourceResolver.ApplyOrientation(src, 42);
        Battery.AssertEqual(src.Size, unknown.Size, "未知取向不该改尺寸");
        Battery.Evidence("1–8 逐取向、逐像素与独立反查表比对；0/1 原样；未知取向尺寸不变");
    }

    /// <summary>
    /// BMP 导入。BMP 的三个坑都在判据里：
    ///   ① 行是<b>自下而上</b>存的（负 height 才表示自上而下）；
    ///   ② 每行按 4 字节补齐（这里故意取宽度 7 像素，24 位下一行 21 字节 → 补 3 字节）；
    ///   ③ 通道顺序是 <b>BGR</b> 而不是 RGB。
    /// 文件由本工装<b>自己手写</b>（不经过被测实现），解码走产品真正用的 <c>FileSourceResolver.Decode</c>。
    /// </summary>
    private static void BmpImportRestoresPixels()
    {
        const int w = 7, h = 5;   // 24 位下行宽 21 字节，逼出 3 字节行补齐
        var expect = new RgbaImage(w, h);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                expect.SetPixel(x, y, new RgbaColor((byte)(20 + x * 30), (byte)(200 - y * 30), 90, 255));

        string dir = Path.Combine(Path.GetTempPath(), "ls-bmp-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(dir);
        try
        {
            string file = Path.Combine(dir, "shot.bmp");
            File.WriteAllBytes(file, BuildBmp24(expect));
            var decoded = LongStitch.App.Imaging.FileSourceResolver.Decode(file);
            Battery.AssertEqual(w, decoded.Image.Width, "BMP 宽");
            Battery.AssertEqual(h, decoded.Image.Height, "BMP 高");
            Battery.AssertBytesEqual(expect.Pixels, decoded.Image.Pixels, "BMP 解码像素");
            Battery.AssertTextEqual("bmp", decoded.Format, "格式标记");

            // 反向对照：把某一行第一像素的 B 与 R 换一下，判据必须能感到"通道顺序写反了"
            byte[] swapped = BuildBmp24(expect);
            int firstPixel = 54 + 14;      // 文件头 54 字节 + 信息头后第一行第 3 像素
            (swapped[firstPixel], swapped[firstPixel + 2]) = (swapped[firstPixel + 2], swapped[firstPixel]);
            string second = Path.Combine(dir, "swapped.bmp");
            File.WriteAllBytes(second, swapped);
            var got = LongStitch.App.Imaging.FileSourceResolver.Decode(second);
            Battery.Assert(!got.Image.Pixels.SequenceEqual(expect.Pixels),
                "夹具失效：改动了 BMP 的 B/R 而解码结果没变，说明这条判据读不出通道顺序");
            Battery.Evidence("24 位 BMP：自下而上 + 3 字节行补齐 + BGR 全部还原；改乱通道顺序会被判出不同");
        }
        finally { try { Directory.Delete(dir, true); } catch { /* 临时目录留给系统回收 */ } }
    }

    /// <summary>
    /// 手写一份 24 位 BMP（自下而上、行按 4 字节补齐、BGR）。
    /// 故意不复用被测实现的任何代码——判据要能指出"解码器把 BMP 的行序或通道顺序搞反了"。
    /// </summary>
    private static byte[] BuildBmp24(RgbaImage image)
    {
        int w = image.Width, h = image.Height;
        int rowBytes = ((w * 3 + 3) / 4) * 4;
        int pixelArea = rowBytes * h;
        var bytes = new byte[54 + pixelArea];

        // BITMAPFILEHEADER
        bytes[0] = (byte)'B'; bytes[1] = (byte)'M';
        WriteInt(bytes, 2, 54 + pixelArea);
        WriteInt(bytes, 10, 54);
        // BITMAPINFOHEADER
        WriteInt(bytes, 14, 40);
        WriteInt(bytes, 18, w);
        WriteInt(bytes, 22, h);              // 正数 = 自下而上
        bytes[26] = 1; bytes[28] = 24;       // planes, bitCount
        WriteInt(bytes, 34, pixelArea);

        for (int y = 0; y < h; y++)
        {
            int rowStart = 54 + (h - 1 - y) * rowBytes;   // 自下而上
            ReadOnlySpan<byte> line = image.Row(y);
            for (int x = 0; x < w; x++)
            {
                bytes[rowStart + x * 3] = line[x * 4 + 2];       // B
                bytes[rowStart + x * 3 + 1] = line[x * 4 + 1];  // G
                bytes[rowStart + x * 3 + 2] = line[x * 4];      // R
            }
        }
        return bytes;

        static void WriteInt(byte[] target, int at, int value)
        {
            target[at] = (byte)value;
            target[at + 1] = (byte)(value >> 8);
            target[at + 2] = (byte)(value >> 16);
            target[at + 3] = (byte)(value >> 24);
        }
    }

    /// <summary>
    /// 造一段最小的 TIFF IFD：一个诱饵条目（ImageDescription）在前，Orientation 在后。
    /// 放诱饵是为了证明解析器真的在**扫描**目录，而不是只读第一个条目。
    /// </summary>
    private static byte[] BuildTiffOrientation(int orientation, bool littleEndian)
    {
        var d = new byte[8 + 2 + 12 * 2];
        d[0] = d[1] = littleEndian ? (byte)'I' : (byte)'M';
        WriteU16(d, 2, 42, littleEndian);            // TIFF 魔数
        WriteU32(d, 4, 8, littleEndian);             // IFD0 偏移
        WriteU16(d, 8, 2, littleEndian);             // 条目数
        WriteU16(d, 10, 0x010E, littleEndian);       // 诱饵：ImageDescription
        WriteU16(d, 12, 2, littleEndian);            // 类型 ASCII
        WriteU32(d, 14, 4, littleEndian);            // count
        WriteU16(d, 22, 0x0112, littleEndian);       // Orientation
        WriteU16(d, 24, 3, littleEndian);            // 类型 SHORT
        WriteU32(d, 26, 1, littleEndian);            // count = 1
        WriteU16(d, 30, orientation, littleEndian);  // 值就放在"值/偏移"字段前两字节
        return d;
    }

    private static void WriteU16(byte[] d, int at, int value, bool littleEndian)
    {
        d[at] = littleEndian ? (byte)value : (byte)(value >> 8);
        d[at + 1] = littleEndian ? (byte)(value >> 8) : (byte)value;
    }

    private static void WriteU32(byte[] d, int at, uint value, bool littleEndian)
    {
        for (int i = 0; i < 4; i++)
            d[at + (littleEndian ? i : 3 - i)] = (byte)(value >> (8 * i));
    }

    /// <summary>把 eXIf 块插到 IHDR 之后（CRC 由测试独立算一遍，块结构必须合法）。</summary>
    private static byte[] InsertExifChunk(byte[] png, byte[] exif)
    {
        // 8 字节 PNG 签名 + 块自身的 12 字节开销（长度4 + 类型4 + CRC4）+ IHDR 数据
        int ihdrEnd = 8 + 12 + (int)ReadU32BigEndian(png, 8);
        var outBytes = new byte[png.Length + 12 + exif.Length];
        png.AsSpan(0, ihdrEnd).CopyTo(outBytes);

        int at = ihdrEnd;
        WriteU32(outBytes, at, (uint)exif.Length, littleEndian: false);
        outBytes[at + 4] = (byte)'e'; outBytes[at + 5] = (byte)'X';
        outBytes[at + 6] = (byte)'I'; outBytes[at + 7] = (byte)'f';
        exif.CopyTo(outBytes, at + 8);
        WriteU32(outBytes, at + 8 + exif.Length, IndependentCrc32(outBytes, at + 4, 4 + exif.Length),
            littleEndian: false);

        png.AsSpan(ihdrEnd).CopyTo(outBytes.AsSpan(at + 12 + exif.Length));
        return outBytes;
    }

    /// <summary>
    /// 测试自己算一遍 PNG 的 CRC-32（IEEE 多项式，反射式）。
    /// 故意不复用产品里的 <c>PngChunks.Crc32</c>：块结构的校验值由独立实现给出，
    /// 才能证明"插进去的 eXIf 块是合法的"，而不是"用同一把尺量自己"。
    /// 算错的话解码器会因 CRC 不符直接抛，不会静默通过。
    /// </summary>
    private static uint IndependentCrc32(byte[] data, int offset, int length)
    {
        uint crc = 0xFFFFFFFF;
        for (int i = offset; i < offset + length; i++)
        {
            crc ^= data[i];
            for (int k = 0; k < 8; k++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
        }
        return crc ^ 0xFFFFFFFF;
    }

    private static uint ReadU32BigEndian(byte[] d, int at) =>
        ((uint)d[at] << 24) | ((uint)d[at + 1] << 16) | ((uint)d[at + 2] << 8) | d[at + 3];

    private static void ExifOrientationLittleEndian()
    {
        byte[] png = InsertExifChunk(
            PngEncoder.EncodeToBytes(Fixture.ScreenshotLike(24, 16), new PngEncodeOptions()),
            BuildTiffOrientation(6, littleEndian: true));

        int orientation = PngDecoder.Decode(png).Info.ExifOrientation;

        // 这格盯着"解析器不许恒返回 1"：只要有人把 IFD 扫描改坏或直接把方向写死成 1，
        // 读出来就是 1 而不是 6，断言立刻变红。
        Battery.Assert(orientation == 6, $"小端 eXIf 应读出方向 6（诱饵条目在后），实际 {orientation}");
    }

    private static void ExifOrientationBigEndian()
    {
        byte[] png = InsertExifChunk(
            PngEncoder.EncodeToBytes(Fixture.ScreenshotLike(24, 16), new PngEncodeOptions()),
            BuildTiffOrientation(8, littleEndian: false));

        int orientation = PngDecoder.Decode(png).Info.ExifOrientation;
        Battery.Assert(orientation == 8, $"大端 eXIf 应读出方向 8，实际 {orientation}");
    }

    private static void ExifGarbageFallsBackToOne()
    {
        byte[] clean = PngEncoder.EncodeToBytes(Fixture.ScreenshotLike(24, 16), new PngEncodeOptions());

        // 五种坏法：空块、字节序标记不认识、魔数错、IFD 偏移越界、方向值超出 1–8。
        // 全部必须退回 1（= 不旋转）且不许抛异常——兜底方向是唯一不会把画面搞坏的选择。
        (string Name, byte[] Exif)[] cases =
        {
            ("空块", Array.Empty<byte>()),
            ("字节序标记不认识", new byte[] { (byte)'X', (byte)'X', 0, 42, 0, 0, 0, 8 }),
            ("TIFF 魔数错", new byte[] { (byte)'I', (byte)'I', 0, 43, 0, 0, 0, 8 }),
            ("IFD 偏移越界", new byte[] { (byte)'I', (byte)'I', 0, 42, 0, 0, 9, 9 }),
            ("方向值 9 超范围", PatchOrientation(BuildTiffOrientation(1, true), 9)),
        };

        foreach ((string name, byte[] exif) in cases)
        {
            var decoded = PngDecoder.Decode(InsertExifChunk(clean, exif));
            Battery.Assert(decoded.Info.ExifOrientation == 1,
                $"坏 eXIf（{name}）应退回方向 1，实际 {decoded.Info.ExifOrientation}");
        }
    }

    /// <summary>把已构造好的小端 IFD 里的方向值改掉（偏移 30 是 SHORT 值的低位字节）。</summary>
    private static byte[] PatchOrientation(byte[] tiff, int orientation)
    {
        var copy = (byte[])tiff.Clone();
        WriteU16(copy, 30, orientation, littleEndian: true);
        return copy;
    }

    private static void RgbaRoundTrip()
    {
        var src = Fixture.ScreenshotLike(64, 40);
        byte[] png = PngEncoder.EncodeToBytes(src, new PngEncodeOptions { StripAlphaIfOpaque = false });
        var back = PngDecoder.Decode(png);

        Battery.Assert(back.Info.HasTransparency, "含 alpha 的图不应被判定为不透明");
        Battery.AssertBytesEqual(src.PixelsRo, back.Image.PixelsRo, "RGBA 往返像素");
        Battery.Evidence($"64×40 → PNG {png.Length} 字节，解回 {back.Image.Width}×{back.Image.Height}");
    }

    private static void OpaqueStripsAlphaLossless()
    {
        var src = Fixture.OpaqueLike(64, 40);
        byte[] png = PngEncoder.EncodeToBytes(src);   // 默认允许降 RGB

        // IHDR 的 colorType 在签名(8) + 长度(4) + 类型(4) 之后第 9 字节 → 偏移 25
        int colorType = png[25];
        Battery.AssertEqual(2, colorType, "全不透明时应写成 colorType=2（RGB）");

        var back = PngDecoder.Decode(png);
        Battery.AssertBytesEqual(src.PixelsRo, back.Image.PixelsRo, "降 RGB 后解回像素");
        Battery.Evidence($"RGB 写法 {png.Length} 字节，比 RGBA 写法小；alpha 丢弃未改变可见像素");
    }

    private static void GdiPlusCrossCheck()
    {
        var src = Fixture.ScreenshotLike(48, 32);
        byte[] png = PngEncoder.EncodeToBytes(src, new PngEncodeOptions { StripAlphaIfOpaque = false });

        // 用完全无关的第三方解码器读回来核对：只有两个独立实现给出同样字节，
        // 才能说"我们的编码器没在滤波/CRC/分块上偷偷改动像素"。
        var theirs = GdiDecode(png);
        Battery.AssertBytesEqual(src.PixelsRo, theirs.PixelsRo, "GDI+ 读回 vs 原始像素");
        Battery.Evidence("GDI+ 独立解码 48×32 与源逐字节一致");
    }

    private static void BadCrcRejected()
    {
        var src = Fixture.ScreenshotLike(32, 32);
        byte[] png = PngEncoder.EncodeToBytes(src);
        byte[] broken = Fixture.Corrupt(png, Fixture.MiddleOfIdat(png));

        string msg = Battery.AssertThrows(() => PngDecoder.Decode(broken), "篡改 IDAT 数据后解码");
        Battery.Assert(msg.Contains("CRC") || msg.Contains("zlib") || msg.Contains("解压"),
            $"失败原因应当指向数据被破坏，而不是被别的检查顺手放过。实际：{msg}");
        Battery.Evidence("篡改一个字节 → " + msg);
    }

    private static void Bit16ReportedAsLossy()
    {
        byte[] png = Fixture.Png16BitRgba(8, 6);
        var back = PngDecoder.Decode(png);

        Battery.AssertEqual(16, back.Info.BitDepth, "IHDR 位深");
        Battery.Assert(back.Info.RequiresBitDepthConversion, "16 位必须被标记为需要降位");
        Battery.Assert(back.Info.LossyNotes.Count > 0, "LossyNotes 必须非空，否则界面无法提示用户");

        // 判据要能指认：取 (3,2) 这个像素，期望值 = 我们写进去的高字节
        var p = back.Image.GetPixel(3, 2);
        byte expectR = (byte)((3 * 257 % 65536) >> 8);
        byte expectG = (byte)((2 * 257 % 65536) >> 8);
        Battery.AssertEqual(expectR, p.R, "(3,2) 红通道高字节");
        Battery.AssertEqual(expectG, p.G, "(3,2) 绿通道高字节");
        Battery.Evidence("16 位 → 8 位取高字节，且被 LossyNotes 记账：" + back.Info.LossyNotes[0]);
    }

    private static void PaletteAndTrns()
    {
        byte[] png = Fixture.PalettePng(8, 4, out byte[] palette, out byte[] trns);
        var back = PngDecoder.Decode(png);

        Battery.AssertEqual(PngColorType.Indexed, back.Info.ColorType, "colorType");
        for (int y = 0; y < 4; y++)
        {
            for (int x = 0; x < 8; x++)
            {
                int idx = (x + y) % 4;
                var got = back.Image.GetPixel(x, y);
                var want = new RgbaColor(palette[idx * 3], palette[idx * 3 + 1], palette[idx * 3 + 2], trns[idx]);
                Battery.AssertEqual(want, got, $"({x},{y}) 索引 {idx} 展开");
            }
        }
        Battery.Evidence("索引 2 → alpha 0、索引 1 → alpha 128 均按 tRNS 展开");
    }

    private static void InterlacedRejected()
    {
        // 现在 Adam7 是**能解**的，所以这一格改成量"账对不上时必须拒绝"：
        // IHDR 声明隔行，但 IDAT 的字节数不是七遍各自行宽加起来的和 → 报错而不是硬拼一张错图。
        byte[] png = Fixture.InterlacedPng(8, 8);   // 数据是"非隔行的 8 行"，与隔行的账不符
        string msg = Battery.AssertThrows(() => PngDecoder.Decode(png),
            "声明 interlace=1 但数据按非隔行排列的 PNG");
        Battery.Assert(msg.Contains("不符") || msg.Contains("截断") || msg.Contains("只剩"),
            $"应报出字节账对不上，实际：{msg}");

        // 反向对照：把同一张图写成**合法**的隔行数据，必须解得开（否则上面那条红可能只是"隔行一律拒绝"）
        var good = Fixture.OpaqueLike(8, 8, seed: 43);
        PngDecodeResult ok = PngDecoder.Decode(Fixture.InterlacedPngOf(good));
        Battery.AssertBytesEqual(good.Pixels, ok.Image.Pixels, "反向对照：合法的 8×8 隔行图应能解开");
        Battery.Evidence("隔行账不符 → 拒绝；同一尺寸的合法隔行图 → 逐字节解开。两条一起才说明拒绝是针对“账不对”");
    }

    /// <summary>
    /// Adam7 隔行解码。这张图故意取 <b>9×5</b>：不是 8 的整数倍，七遍的宽度/高度各不相同，
    /// 遍数算术一错就会露出来（比如把某一遍的行宽按整图算）。
    /// 判据两头核：① 解出来的像素与原图逐字节相同；② 与同一张图的非隔行版本解出来也完全相同。
    /// </summary>
    private static void Adam7DecodesToSamePixels()
    {
        var image = Fixture.ScreenshotLike(9, 5, seed: 4242);
        byte[] interlaced = Fixture.InterlacedPngOf(image);
        byte[] plain = Fixture.PlainPngOf(image);

        PngDecodeResult back = PngDecoder.Decode(interlaced);
        Battery.Assert(back.Info.Interlaced, "PngFormatInfo 应如实报告这张是隔行存储");
        Battery.AssertEqual(image.Size, back.Image.Size, "尺寸应取 IHDR 的整图尺寸，不是某一遍的尺寸");
        Battery.AssertBytesEqual(image.Pixels, back.Image.Pixels, "隔行解码后与原图像素逐字节");

        // 对照：同一张图的非隔行版本必须解出**一模一样**的像素
        PngDecodeResult plainBack = PngDecoder.Decode(plain);
        Battery.Assert(!plainBack.Info.Interlaced, "非隔行那张不该报 interlace");
        Battery.AssertBytesEqual(plainBack.Image.Pixels, back.Image.Pixels, "隔行与非隔行两种存法解码结果");

        // 再跑一组尺寸，覆盖"某几遍整遍为空"的窄图与宽图
        foreach (var (w, h) in new[] { (1, 1), (2, 1), (1, 9), (8, 8), (17, 3), (3, 21) })
        {
            var small = Fixture.OpaqueLike(w, h, seed: 7000 + w * 100 + h);
            PngDecodeResult got = PngDecoder.Decode(Fixture.InterlacedPngOf(small));
            Battery.AssertBytesEqual(small.Pixels, got.Image.Pixels, $"{w}×{h} 隔行解码");
        }

        Battery.Evidence("9×5 + 6 组边界尺寸：七遍撒回网格后与原图逐字节相同，且与非隔行版本一致");
    }

    private static void ShortFileThrows()
    {
        using var ms = new MemoryStream();
        var w = new PngStreamWriter(ms, 16, 10, withAlpha: true);
        var row = new byte[16 * 4];
        for (int i = 0; i < 3; i++) w.WriteRgbaRow(row);   // 只写 3 行，声明 10 行

        string msg = Battery.AssertThrows(w.Finish, "行数不足时收尾");
        Battery.Assert(msg.Contains("不完整") || msg.Contains("行"), $"收尾应报行数不足，实际：{msg}");
        Battery.Evidence("只写 3/10 行 → Finish 抛错，上层据此删除半成品");
    }

    /// <summary>用 GDI+ 解码 PNG 并转成 RGBA8888（内存布局 BGRA → RGBA）。</summary>
    private static RgbaImage GdiDecode(byte[] png)
    {
        using var ms = new MemoryStream(png);
        using var bmp = new DrawingBitmap(ms);
        var rect = new System.Drawing.Rectangle(0, 0, bmp.Width, bmp.Height);
        var data = bmp.LockBits(rect, DrawingImagelockMode.ReadOnly, DrawingPixelFormat.Format32bppArgb);
        try
        {
            int stride = data.Stride;
            byte[] buf = new byte[stride * bmp.Height];
            Marshal.Copy(data.Scan0, buf, 0, buf.Length);
            var img = new RgbaImage(bmp.Width, bmp.Height);
            for (int y = 0; y < bmp.Height; y++)
            {
                Span<byte> dst = img.Row(y);
                for (int x = 0; x < bmp.Width; x++)
                {
                    int s = y * stride + x * 4;
                    dst[x * 4] = buf[s + 2];      // R
                    dst[x * 4 + 1] = buf[s + 1];  // G
                    dst[x * 4 + 2] = buf[s];      // B
                    dst[x * 4 + 3] = buf[s + 3];  // A
                }
            }
            return img;
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }
}
