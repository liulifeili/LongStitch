using LongStitch.Core.Geometry;
using LongStitch.Core.Imaging;

namespace LongStitch.Checks;

/// <summary>
/// 整数搬运原语电池。
///
/// 这一组比 PNG 那组更接近"一像素不出错"的本质：导出与预览都建立在 Blit 上，
/// 只要 Blit 在边界处错位或分块与整块不一致，后面所有环节都会跟着错，
/// 而且错得极难用肉眼发现（通常只是接缝处重复一行或吃掉一行）。
/// </summary>
public static class BlitTests
{
    public static void Register()
    {
        Battery.Add("blit", "双侧越界裁剪不错位", ClipWithoutMisalign);
        Battery.Add("blit", "完全不相交不改动目标", DisjointIsNoop);
        Battery.Add("blit", "拷贝不许越过源矩形", CopyStaysInsideSourceRect);
        Battery.Add("blit", "SubImage 与原图逐字节相同", SubImageExact);
        Battery.Add("blit", "尺寸不等的替换必须抛", ReplaceSizeMismatchThrows);
        Battery.Add("blit", "分块 blit 与整块 blit 一致", ChunkedEqualsWhole);
        Battery.Add("blit", "FlattenOnto 与独立复算一致", FlattenMatchesIndependentMath);
    }

    /// <summary>每像素编码为 R = x + y*16，便于从字节值反推坐标。</summary>
    private static RgbaImage CoordinateBoard(int w, int h)
    {
        var img = new RgbaImage(w, h);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                img.SetPixel(x, y, new RgbaColor((byte)(x + y * 16), 0, 0, 255));
        return img;
    }

    private static void ClipWithoutMisalign()
    {
        var src = CoordinateBoard(10, 10);
        var dst = new RgbaImage(10, 10);

        // 源矩形故意超出源图（5,5 起、10×10 → 右下角到 15,15），目标故意放到画布外 (-3,-3)
        IntRect written = dst.Blit(src, new IntRect(5, 5, 10, 10), -3, -3);

        Battery.AssertEqual(new IntRect(0, 0, 2, 2), written, "实际写入矩形");
        // 推导：dst(0,0) ← src(0-(-3)+5, 0-(-3)+5) = src(8,8)
        Battery.AssertEqual(src.GetPixel(8, 8), dst.GetPixel(0, 0), "dst(0,0) 应等于 src(8,8)");
        Battery.AssertEqual(src.GetPixel(9, 8), dst.GetPixel(1, 0), "dst(1,0) 应等于 src(9,8)");
        Battery.AssertEqual(src.GetPixel(8, 9), dst.GetPixel(0, 1), "dst(0,1) 应等于 src(8,9)");
        Battery.AssertEqual(src.GetPixel(9, 9), dst.GetPixel(1, 1), "dst(1,1) 应等于 src(9,9)");
        // 第 2 列起不该被写：源图只到 x=9，越界部分必须被裁掉而不是绕回读 src(0,*)
        Battery.AssertEqual(RgbaColor.Transparent, dst.GetPixel(2, 0), "dst(2,0) 应保持未写入");
        Battery.Evidence("双侧越界联立裁剪：写入 2×2，且逐格坐标可指认");
    }

    private static void DisjointIsNoop()
    {
        var src = CoordinateBoard(8, 8);
        var dst = CoordinateBoard(8, 8);
        byte[] before = dst.PixelsRo.ToArray();

        IntRect written = dst.Blit(src, new IntRect(2, 2, 4, 4), 100, 100);
        Battery.Assert(written.IsEmpty, "不相交的 blit 应返回空矩形");
        Battery.AssertBytesEqual(before, dst.PixelsRo, "目标图未被改动");
    }

    /// <summary>
    /// 从大源图里只取一小块贴进来，**不许顺手把源矩形之外的行也拷进来**。
    ///
    /// 这条是被真图标定跑出来的：早期实现的越界求解只约束了"在本图内"和"在源图内"，
    /// 漏了"在请求矩形内"，于是 <c>dst.Blit(大图, 顶部130行, 0, 0)</c> 会一路拷到 dst 底部，
    /// 把整张目标覆盖成源图的前若干行——表现为"两张本不相关的图变得一模一样"，
    /// 匹配器于是永远判定"整张重叠"。真机上这条一旦破，所有自动拼缝都是错的。
    /// </summary>
    private static void CopyStaysInsideSourceRect()
    {
        var src = CoordinateBoard(10, 20);          // 20 行，每像素 R = x + y*16
        var dst = new RgbaImage(10, 20);
        dst.Fill(new RgbaColor(1, 2, 3, 255));      // 标记色：被越界覆盖就会消失

        IntRect written = dst.Blit(src, new IntRect(0, 0, 10, 5), 0, 0);

        Battery.AssertEqual(new IntRect(0, 0, 10, 5), written, "实际写入矩形");
        Battery.AssertEqual(src.GetPixel(3, 4), dst.GetPixel(3, 4), "第 4 行应来自源第 4 行");
        Battery.AssertEqual(new RgbaColor(1, 2, 3, 255), dst.GetPixel(3, 5),
            "第 5 行必须还是标记色：源矩形只有 0..4 行");
        Battery.AssertEqual(new RgbaColor(1, 2, 3, 255), dst.GetPixel(3, 19),
            "最后一行必须还是标记色（越界覆盖的典型受害者）");

        // 再来一次带偏移的：源矩形 (2,7,3,4) 贴到 (5,1)，只该影响 dst 的 5..7 列、1..4 行
        dst.Fill(new RgbaColor(1, 2, 3, 255));
        IntRect w2 = dst.Blit(src, new IntRect(2, 7, 3, 4), 5, 1);
        Battery.AssertEqual(new IntRect(5, 1, 3, 4), w2, "带偏移时的写入矩形");
        Battery.AssertEqual(src.GetPixel(2, 7), dst.GetPixel(5, 1), "左上角对应");
        Battery.AssertEqual(src.GetPixel(4, 10), dst.GetPixel(7, 4), "右下角对应");
        Battery.AssertEqual(new RgbaColor(1, 2, 3, 255), dst.GetPixel(8, 1), "矩形右侧不该被碰");
        Battery.AssertEqual(new RgbaColor(1, 2, 3, 255), dst.GetPixel(5, 5), "矩形下侧不该被碰");
        Battery.Evidence("三种落点都严格停在请求矩形边界内；旧实现会在第一种情形下多拷 15 行");
    }

    private static void SubImageExact()
    {
        var src = Fixture.ScreenshotLike(64, 48);
        var region = new IntRect(7, 5, 33, 21);
        RgbaImage cut = src.SubImage(region);

        Battery.AssertEqual(new IntSize(33, 21), cut.Size, "裁出尺寸");
        // 判据：裁出图的整图哈希 必须等于 原图对应区域的区域哈希。
        // 这一条同时验证了 SubImage 的取数与 RegionSha256 的行序定义一致。
        Battery.AssertBytesEqual(src.RegionSha256(region), cut.RegionSha256(cut.Bounds),
            "原图区域哈希 vs 裁出图整图哈希");
    }

    private static void ReplaceSizeMismatchThrows()
    {
        var dst = new RgbaImage(16, 16);
        var src = new RgbaImage(16, 16);
        string msg = Battery.AssertThrows(
            () => dst.ReplaceRegion(src, new IntRect(0, 0, 10, 10), new IntRect(0, 0, 11, 10)),
            "尺寸不等的区域替换");
        Battery.Assert(msg.Contains("尺寸必须一致"), $"应明确拒绝隐式缩放，实际：{msg}");
    }

    /// <summary>
    /// 本项目最重要的一格不变量：**按行带分块渲染的结果，必须与一次性整块渲染逐字节相同**。
    /// 导出走分块（否则几万行长图会 OOM），预览与自证测试却常常一次算完；
    /// 两者若差 1 像素，用户看到的就是"预览对、导出错"这种最难排查的缺陷。
    /// </summary>
    private static void ChunkedEqualsWhole()
    {
        var a = Fixture.ScreenshotLike(40, 60, seed: 7);
        var b = Fixture.ScreenshotLike(40, 60, seed: 11);

        // 一次性整块：b 放在 a 下面、纵向重叠 25 像素
        var whole = new RgbaImage(40, 60 + 60 - 25);
        whole.Blit(a, a.Bounds, 0, 0);
        whole.Blit(b, new IntRect(25, 0, 40, 60), 0, 60 - 25);

        // 分块：每次只算 7 行带，逐带 blit 再比对
        var chunked = new RgbaImage(40, whole.Height);
        const int band = 7;
        for (int y0 = 0; y0 < whole.Height; y0 += band)
        {
            int h = Math.Min(band, whole.Height - y0);
            chunked.Blit(a, new IntRect(0, y0, 40, h), 0, y0);
            IntRect srcBand = new IntRect(25, y0 - (60 - 25), 40, h);
            chunked.Blit(b, srcBand, 0, y0);
        }

        Battery.AssertBytesEqual(whole.PixelsRo, chunked.PixelsRo, "分块渲染 vs 整块渲染");
        Battery.Evidence($"带高 {band}px、共 {(whole.Height + band - 1) / band} 带，与整块逐字节相同");
    }

    private static void FlattenMatchesIndependentMath()
    {
        var img = new RgbaImage(4, 2);
        img.SetPixel(0, 0, new RgbaColor(10, 200, 250, 0));
        img.SetPixel(1, 0, new RgbaColor(10, 200, 250, 255));
        img.SetPixel(2, 0, new RgbaColor(13, 129, 244, 77));
        img.SetPixel(3, 0, new RgbaColor(200, 30, 30, 128));
        img.SetPixel(0, 1, new RgbaColor(1, 2, 3, 1));
        img.SetPixel(1, 1, new RgbaColor(254, 253, 252, 254));
        img.SetPixel(2, 1, new RgbaColor(64, 64, 64, 127));
        img.SetPixel(3, 1, new RgbaColor(128, 128, 128, 129));

        var bg = new RgbaColor(255, 255, 255, 255);
        RgbaImage flat = img.FlattenOnto(bg);

        for (int y = 0; y < 2; y++)
        {
            for (int x = 0; x < 4; x++)
            {
                RgbaColor s = img.GetPixel(x, y);
                // 独立复算：同一公式（先乘后加半个除数）在这里重写一遍，
                // 而不是调用被测实现，否则这格只是自我确认。
                byte Mix(byte c, byte b) => (byte)((c * s.A + b * (255 - s.A) + 127) / 255);
                var want = s.A == 255 ? s : s.A == 0 ? bg
                    : new RgbaColor(Mix(s.R, bg.R), Mix(s.G, bg.G), Mix(s.B, bg.B), 255);
                Battery.AssertEqual(want, flat.GetPixel(x, y), $"({x},{y}) 合成到白底");
            }
        }
        Battery.Evidence("透明像素按整数公式合成，无浮点参与");
    }
}
