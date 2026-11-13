using LongStitch.Core.Geometry;
using LongStitch.Core.Imaging;

namespace LongStitch.Checks;

/// <summary>
/// 守卫的守卫。
///
/// 这类项目最怕的不是有 bug，而是**验收判据本身坏掉却显示通过**：
/// 一条永远为真的断言，比没有断言更危险，因为它会让后面每一轮都放心地跳过真实核对。
/// 所以每写一条"能抓 bug 的断言"，这里就配一格故意注入缺陷、并要求它必须变红。
/// </summary>
public static class GuardTests
{
    public static void Register()
    {
        Battery.Add("guard", "1 像素平移会被区域哈希抓到", OnePixelShiftIsDetected);
        Battery.Add("guard", "逐步取整确实不同于整段取整", PerStepRoundingDrifts);
        Battery.Add("guard", "AssertThrows 在没有异常时确实报错", AssertThrowsWorksBothWays);
        Battery.Add("guard", "分块与整块的哈希定义同源", ChunkHashMatchesWholeHash);
        Battery.Add("guard", "缩小导出把输出坐标当源坐标必被抓到", DownscaleWrongCoordinateSpaceIsCaught);
    }

    /// <summary>把 b 叠在 a 下方、重叠 25 行，得到"正确"的拼接结果。</summary>
    private static RgbaImage Stitch(RgbaImage a, RgbaImage b, int overlap, int extraShift)
    {
        var canvas = new RgbaImage(a.Width, a.Height + b.Height - overlap);
        canvas.Blit(a, a.Bounds, 0, 0);
        canvas.Blit(b, new IntRect(overlap, 0, b.Width - overlap, b.Height),
            0, a.Height - overlap + extraShift);
        return canvas;
    }

    private static void OnePixelShiftIsDetected()
    {
        var a = Fixture.ScreenshotLike(32, 50, seed: 3);
        var b = Fixture.ScreenshotLike(32, 50, seed: 4);

        RgbaImage expected = Stitch(a, b, overlap: 20, extraShift: 0);
        RgbaImage correct = Stitch(a, b, overlap: 20, extraShift: 0);
        RgbaImage mutated = Stitch(a, b, overlap: 20, extraShift: 1);   // 故意差 1 像素

        // 先自证：同一套构造两次结果必须一致，否则下面那格"必须不同"毫无意义
        Battery.AssertBytesEqual(expected.PixelsRo, correct.PixelsRo, "同参数重复拼接");

        // 真正的守卫：差 1 像素必须让哈希不同。若这里"仍然相同"，说明哈希压根没在量像素。
        byte[] h1 = expected.RegionSha256(new IntRect(0, 40, 32, 20));
        byte[] h2 = mutated.RegionSha256(new IntRect(0, 40, 32, 20));
        Battery.Assert(!h1.AsSpan().SequenceEqual(h2),
            "接缝处平移 1 像素后区域哈希竟然相同 —— 判据失效，这格必须红");
        Battery.Evidence($"正确 {Convert.ToHexString(h1, 0, 8)} ≠ 错位 {Convert.ToHexString(h2, 0, 8)}");
    }

    /// <summary>
    /// 需求 §5.2 要求"鼠标移动量换算为原图坐标，缩放改变时视觉拖动仍稳定"。
    /// 实现上有一条红线：整段拖动结束才取整一次。这格证明"每步都取整"确实会产生不同结果，
    /// 于是 LayoutTests 里那条按整段取整的断言是有牙齿的，不是同义反复。
    /// </summary>
    private static void PerStepRoundingDrifts()
    {
        const double zoom = 0.3;           // 30% 预览
        const int steps = 10;              // 每步屏幕位移 1 像素

        // 错误做法：每一步都换算并取整，再累加整数
        int perStep = 0;
        for (int i = 0; i < steps; i++) perStep += (int)Math.Round(1 / zoom);

        // 正确做法：累加屏幕位移，结束时换算并取整一次
        int wholeDrag = (int)Math.Round(steps / zoom);

        Battery.Assert(perStep != wholeDrag,
            $"两种算法结果相同（都是 {perStep}），本格已失去意义，请改用会产生漂移的缩放比");
        Battery.Evidence($"逐步取整 {perStep}px vs 整段取整 {wholeDrag}px，差 {wholeDrag - perStep}px（zoom={zoom}）");
    }

    private static void AssertThrowsWorksBothWays()
    {
        // 正向：确实抛异常时，AssertThrows 应安静返回消息
        string msg = Battery.AssertThrows(
            () => throw new InvalidOperationException("预期内的异常"), "自检");
        Battery.Assert(msg.Contains("预期内"), "AssertThrows 应把异常消息交回来");

        // 反向：不抛异常时，AssertThrows 自己必须报错——否则所有 AssertThrows 型守卫都可能假绿
        bool flagged = false;
        try
        {
            Battery.AssertThrows(() => { }, "不该通过的调用");
        }
        catch (Exception)
        {
            flagged = true;
        }
        Battery.Assert(flagged, "被守卫的代码没抛异常时，AssertThrows 必须报错（守卫失效）");
    }

    /// <summary>
    /// 分块导出会一片一片算哈希，整图核对则一次算完。
    /// 两者的"行序 + 区域定义"必须同源，否则会出现"每片都对、拼起来不对"这种最坑的漏网。
    /// </summary>
    private static void ChunkHashMatchesWholeHash()
    {
        var img = Fixture.ScreenshotLike(24, 40, seed: 21);
        byte[] whole = img.RegionSha256(img.Bounds);

        // 用 System.Security.Cryptography 独立串接：逐行喂，等价于产品里"按行带渲染"的取数顺序
        using var sha = System.Security.Cryptography.SHA256.Create();
        for (int y = 0; y < img.Height; y++)
        {
            byte[] line = img.Row(y).ToArray();
            sha.TransformBlock(line, 0, line.Length, null, 0);
        }
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);

        Battery.AssertBytesEqual(whole, sha.Hash!, "整图区域哈希 vs 逐行独立串接");
        Battery.Evidence($"{img.Height} 行逐行串接与一次算完同哈希，行序定义无歧义");
    }

    /// <summary>
    /// 复刻曾经的严重缺陷：缩小导出把计划里的**输出坐标**直接当**源坐标**用（漏乘缩小倍数）。
    /// 必须证明 `export/缩小导出全链路逐字节对得上独立复算` 抓得住它——
    /// 行数与内容两头都对不上，那格判据不是同义反复。
    /// </summary>
    private static void DownscaleWrongCoordinateSpaceIsCaught()
    {
        var src = Fixture.OpaqueLike(8, 12, seed: 77);
        const int divisor = 2;

        // 正确结果：12 源行 → 6 输出行
        RgbaImage correct = ExportTests.BoxDownscale(src, divisor);

        // 旧缺陷单文件路径：只读 piece.Height=6 条**源**行 → 降采样只交出 3 行，
        // 编码器声明了 6 行，收尾时行数不足必抛
        RgbaImage oldSingle = ExportTests.BoxDownscale(
            src.SubImage(new IntRect(0, 0, src.Width, correct.Height)), divisor);
        Battery.Assert(oldSingle.Height != correct.Height,
            $"旧缺陷行数 {oldSingle.Height} 与正确 {correct.Height} 相同——判据失去杀伤力");

        // 旧缺陷分段路径：第二片输出起点 3 没有乘回倍数，取源行 3..6 而不是 6..12
        RgbaImage correctSecond = ExportTests.BoxDownscale(src.SubImage(new IntRect(0, 6, 8, 6)), divisor);
        RgbaImage oldSecond = ExportTests.BoxDownscale(src.SubImage(new IntRect(0, 3, 8, 6)), divisor);
        Battery.Assert(!correctSecond.PixelsRo.SequenceEqual(oldSecond.PixelsRo),
            "旧缺陷在分段上内容与正确结果相同——判据失去杀伤力");

        Battery.Evidence($"旧映射两头都露馅：单文件行数 {oldSingle.Height}≠{correct.Height}（收尾必抛）、" +
                         "分段内容错位（逐字节比对必红）");
    }
}
