using LongStitch.Core;
using LongStitch.Core.Cleanup;
using LongStitch.Core.Geometry;
using LongStitch.Core.Imaging;
using LongStitch.Core.Model;

namespace LongStitch.Checks;

/// <summary>
/// 清理电池（第十四轮末次起只剩滚动条这一项自动清理）。
///
/// 清理是唯一会<b>真的改动像素</b>的一组操作，也是最容易"越修越坏"的地方：
/// 误判一次涂掉正文、或者重复运行叠加着多裁一层，用户往往在导出之后才发现。
/// 所以这里除了"该检出的要检出"，更重要的是三条"不许伤害正文"的判据。
///
/// 随"重复状态栏 / 底部固定栏"那一族一起撤掉的判据见 README「第四十改」的作废记录。
/// </summary>
public static class CleanupTests
{
    public static void Register()
    {
        Battery.Add("cleanup", "右缘滚动条被检出并抹除", ScrollbarDetectedAndRemoved);
        Battery.Add("cleanup", "抹除后画布宽度不变", ScrollbarKeepsCanvasWidth);
        Battery.Add("cleanup", "没有滚动条时不许涂任何东西", NoScrollbarNoTouch);
        Battery.Add("cleanup", "重复运行清理是幂等的", RerunIsIdempotent);
    }

    /// <summary>一张右缘带滚动条的截图（宽度取 8 的倍数附近，考验厚度搜索）。</summary>
    private static RgbaImage WithScrollbar(int width = 80, int height = 300, int seed = 7)
    {
        var img = Fixture.OpaqueLike(width, height, seed);
        img.Fill(new IntRect(width - 8, height / 3, 4, height / 3), new RgbaColor(120, 120, 128, 255));
        return img;
    }

    /// <summary>
    /// 一张干净的正文图（无滚动条）。
    /// 两个坑都在这版里修掉了：
    ///   ① seed 必须真的改变内容——早先忽略了 seed，"两张不同的图"其实逐像素相同，
    ///      状态栏检测于是把整段相同正文都算成固定带；
    ///   ② <paramref name="contentStart"/> 要让正文从固定带下缘立刻开始，否则带下方那段
    ///      空白在两图里同样相同，检测"多检出几行"是对的，测试却会误判成算法过检。
    /// </summary>
    private static RgbaImage PlainContent(int width = 80, int height = 300, int seed = 7, int contentStart = 20)
    {
        var img = new RgbaImage(width, height);
        img.Fill(RgbaColor.White);
        uint state = ((uint)seed * 2654435761u) | 1u;
        int Next() { state = state * 1664525u + 1013904223u; return (int)(state >> 9); }

        for (int i = 0; i < 10; i++)
        {
            int w = 20 + Next() % Math.Max(1, width - 30);
            int x = 6 + Next() % Math.Max(1, width - w - 8);
            byte v = (byte)(30 + Next() % 90);
            int y = contentStart + i * 24;
            if (y + 6 > height) break;
            img.Fill(new IntRect(x, y, w, 6), new RgbaColor(v, 40, 40, 255));
        }
        return img;
    }

    private static void ScrollbarDetectedAndRemoved()
    {
        RgbaImage img = WithScrollbar();
        ScrollbarDetection? det = ScrollbarDetector.Detect(img);

        Battery.Assert(det is not null, "应检出右缘滚动条");
        Battery.AssertEqual(EdgeSide.Right, det!.Value.Side, "侧别");
        Battery.Assert(det.Value.Confidence >= 0.6, $"置信度应过默认门限，实际 {det.Value.Confidence:P0}");

        (Project p, MemoryResolver r, _) = TestProject.Create(StitchMode.Vertical, img);
        var composer = new CanvasComposer(r);
        CleanupReport report = CleanupPlanner.Apply(p, r, composer);

        Battery.AssertEqual(1, report.ScrollbarPatches, "应生成 1 个滚动条补丁");
        RgbaImage cleaned = composer.RenderWhole(p.Layout);

        // 判据必须能指认"改成了什么"：条体那一列现在应当等于延展来源列的颜色。
        // （曾经写成"A 变了 或 A 没变"这种永真式，那等于没检查。）
        PixelPatch patch = p.Doc.Segments[0].Patches.Single();
        Battery.AssertEqual(PatchKind.ExtendColumn, patch.Kind, "滚动条应使用水平延展");
        int probeRow = img.Height / 2;
        int probeCol = img.Width - 6;
        RgbaColor want = img.GetPixel(patch.ExtendColumnX, probeRow);
        RgbaColor original = img.GetPixel(probeCol, probeRow);
        RgbaColor got = cleaned.GetPixel(probeCol, probeRow);
        Battery.Assert(want != original,
            $"探针选得没意义：延展来源列与滚动条列同色（{original}），换一行或换一列再测");
        Battery.AssertEqual(want, got,
            $"滚动条列 ({probeCol},{probeRow}) 应被延展列 x={patch.ExtendColumnX} 的颜色覆盖");
        Battery.Evidence($"检出 {det.Value.Thickness}px 厚、{det.Value.Length} 行长，置信度 {det.Value.Confidence:P0}");
    }

    /// <summary>
    /// 需求 §6 的清理不许改变画布宽度——宽度一变，竖拼的"统一宽度"与所有已确认拼缝都会被牵动。
    /// </summary>
    private static void ScrollbarKeepsCanvasWidth()
    {
        RgbaImage img = WithScrollbar();
        (Project p, MemoryResolver r, _) = TestProject.Create(StitchMode.Vertical, img);
        var composer = new CanvasComposer(r);
        IntSize before = p.Layout.CanvasSize;

        CleanupPlanner.Apply(p, r, composer);
        Battery.AssertEqual(before, p.Layout.CanvasSize, "清理滚动条前后画布尺寸必须一致");
        Battery.Evidence($"80×300 → 清理后仍 {p.Layout.CanvasSize}（用水平延展而非裁宽）");
    }

    /// <summary>没有滚动条时，清理必须一个像素都不改。这是"宁可漏报也不误涂"的判据。</summary>
    private static void NoScrollbarNoTouch()
    {
        RgbaImage img = PlainContent();
        Battery.Assert(ScrollbarDetector.Detect(img) is null, "干净正文不该被检出滚动条");

        (Project p, MemoryResolver r, _) = TestProject.Create(StitchMode.Vertical, img);
        var composer = new CanvasComposer(r);
        byte[] before = composer.RenderWhole(p.Layout).RegionSha256(p.Layout.CanvasBounds);

        CleanupReport report = CleanupPlanner.Apply(p, r, composer);
        byte[] after = composer.RenderWhole(p.Layout).RegionSha256(p.Layout.CanvasBounds);

        Battery.AssertEqual(0, report.ScrollbarPatches, "不该产生任何滚动条补丁");
        Battery.AssertBytesEqual(before, after, "无滚动条时清理前后的像素");
    }

    /// <summary>
    /// 幂等性：连跑两次清理，结果必须与跑一次完全相同。
    /// 破了这条，用户多点一次"应用预览"就多裁一层，正文会被逐轮吃掉——
    /// 而且第一次看还正常，属于最坑的那类缺陷。
    /// </summary>
    private static void RerunIsIdempotent()
    {
        RgbaImage img = WithScrollbar(80, 300, seed: 21);
        (Project p, MemoryResolver r, _) = TestProject.Create(StitchMode.Vertical, img, img);
        var composer = new CanvasComposer(r);

        CleanupReport first = CleanupPlanner.Apply(p, r, composer);
        byte[] hashFirst = composer.RenderWhole(p.Layout).RegionSha256(p.Layout.CanvasBounds);
        int patchesFirst = p.Doc.Segments.Sum(s => s.Patches.Count);

        CleanupReport second = CleanupPlanner.Apply(p, r, composer);
        byte[] hashSecond = composer.RenderWhole(p.Layout).RegionSha256(p.Layout.CanvasBounds);
        int patchesSecond = p.Doc.Segments.Sum(s => s.Patches.Count);

        Battery.AssertEqual(patchesFirst, patchesSecond, "补丁数量（跑两次不该翻倍）");
        Battery.AssertEqual(first.ScrollbarPatches, second.ScrollbarPatches, "滚动条补丁数");
        Battery.AssertBytesEqual(hashFirst, hashSecond, "两次清理后的画布");
        Battery.Evidence($"跑两次仍是 {patchesFirst} 个补丁、同一哈希（按前缀清掉上一轮自动补丁后重检测）");
    }

}
