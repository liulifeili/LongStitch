using LongStitch.Core;
using LongStitch.Core.Analysis;
using LongStitch.Core.Geometry;
using LongStitch.Core.Imaging;
using LongStitch.Core.Layout;
using LongStitch.Core.Matching;
using LongStitch.Core.Model;

namespace LongStitch.Checks;

/// <summary>
/// 重叠匹配电池。
///
/// 判据全部建立在"由构造已知正确答案"的合成样本上：从一张长图里切两屏，
/// 重叠行数是我们自己定的，所以匹配结果可以被<b>精确断言</b>，
/// 而不是"看起来差不多"。阈值标定也依赖这套样本（见 docs/匹配算法.md）。
/// </summary>
public static class MatchingTests
{
    public static void Register()
    {
        Battery.Add("match", "找回 300 行重叠且分毫不差", FindsExactOverlap);
        Battery.Add("match", "能分辨 1 像素的错位", DistinguishesOnePixel);
        Battery.Add("match", "固定栏陷阱不得判成整张重叠", FixedBarTrapNotConsumed);
        Battery.Add("match", "无重叠时必须降级为需检查", NoOverlapIsHonest);
        Battery.Add("match", "重复纹理降级为需检查", RepeatingPatternFlagsAmbiguity);
        Battery.Add("match", "宽度不同不给自动结论", MixedWidthRefusesSuggestion);
        Battery.Add("match", "匹配结果自带对齐位移与带的起止", ResultCarriesAlignmentAndOverlapRect);
        Battery.Add("match", "自动裁切线避开固定栏并让两张图共同贡献重叠区", AutoCutUsesSharedContent);
        Battery.Add("match", "在已有重叠与切点上重新分析必须量回同一个重叠", ReanalysisRecoversPlantedOverlap);
        Battery.Add("guard", "匹配取数必须含被蓝线切掉的那几行", MatchInputContainsTrimmedRows);
    }

    /// <summary>
    /// 需求三：自动匹配交回来的不能只是"重叠 300 行"这一个数，必须自带
    /// <c>AlignmentOffset / OverlapRect / OverlapStartY / OverlapEndY /
    /// BestSimilarity / Uniqueness / DetectedHorizontalShift / NeedsReview</c>。
    /// 这里两头都量：正向（对得准时这些量互相吻合）与反向（宽度不同时
    /// <c>AlignmentOffset.X</c> 仍是 0、被锁掉的横向位移留在 DetectedHorizontalShift 里、NeedsReview 为真）。
    /// </summary>
    private static void ResultCarriesAlignmentAndOverlapRect()
    {
        RgbaImage page = TallPage(64, 900, seed: 777);
        const int firstHeight = 400, overlap = 260;
        (RgbaImage before, RgbaImage after, int expected) =
            TestProject.CutScrollPair(page, firstTop: 0, firstHeight, overlap);
        Battery.AssertEqual(overlap, expected, "夹具自证：切出来的重叠就是 260 行");

        MatchResult r = OverlapMatcher.Match(before, after, MatchOptions.For(StitchMode.LongScroll));
        Battery.Assert(r.HasSuggestion, "夹具自证：这个样本该给出自动建议，状态 " + r.Status);
        Battery.AssertEqual(new IntPoint(0, firstHeight - overlap), r.AlignmentOffset,
            "AlignmentOffset = (0, H1 − 重叠)，就是需求二里图2 相对图1 的位移");
        Battery.AssertEqual(0, r.AlignmentOffset.X, "长截图里对齐位移的 X 恒为 0");
        Battery.AssertEqual(new IntRect(0, firstHeight - overlap, 64, overlap), r.OverlapRect,
            "OverlapRect = Rect1 ∩ Rect2（以 Rect1 左上角为原点）");
        Battery.AssertEqual(firstHeight - overlap, r.OverlapStartY, "带首 = H1 − 重叠");
        Battery.AssertEqual(firstHeight, r.OverlapEndY, "带尾 = H1");
        Battery.AssertEqual(r.OverlapRect.Height, r.OverlapLength, "带高就是重叠行数");
        Battery.AssertEqual(r.Confidence, r.BestSimilarity, "BestSimilarity 就是那份相似度（同一个出处）");
        Battery.Assert(r.Uniqueness > 0, "唯一性必须量出来，否则自动放行没有依据：" + r.Uniqueness);
        Battery.AssertEqual(0, r.DetectedHorizontalShift, "同宽样本不该量到横向位移");
        Battery.AssertEqual(overlap / 2, r.SeamCutPosition,
            "两图固定栏为空时自动蓝线取重叠带中点，使图1底部与图2顶部都按蓝线裁切");
        Battery.Assert(r.CropInvariantHolds, "裁切建议自不自洽");
        Battery.Assert(!r.NeedsReview, "这个样本该自动通过，实际：" + r.Cause + " / " + r.CauseText);

        // 反向：宽度不同 ⇒ 不给建议，但也不能编出一个 X
        (RgbaImage wide, RgbaImage narrow, _) =
            TestProject.CutScrollPair(page, firstTop: 0, firstHeight, overlap);
        MatchResult mixed = OverlapMatcher.Match(wide, PngCrop(narrow, 60),
            MatchOptions.For(StitchMode.LongScroll));
        Battery.Assert(!mixed.HasSuggestion, "宽度不同必须不给自动结论");
        Battery.AssertEqual(0, mixed.AlignmentOffset.X, "就算不给结论，也不许偷偷带一个 X 回来");
        Battery.Assert(mixed.NeedsReview, "宽度不同要标成需要检查");
        Battery.Evidence($"对齐位移 ({r.AlignmentOffset.X},{r.AlignmentOffset.Y})、带 {r.OverlapRect}、" +
                         $"相似度 {r.BestSimilarity:F4}、唯一性 {r.Uniqueness:F5}；宽度不同那侧仍 X=0 且需检查");
    }

    private static void AutoCutUsesSharedContent()
    {
        RgbaImage page = TallPage(64, 900, seed: 818);
        (RgbaImage before, RgbaImage after, int overlap) = TestProject.CutScrollPair(page, 0, 420, 260);
        const int fixedTop = 36, fixedBottom = 24;
        MatchResult r = OverlapMatcher.Match(before, after, MatchOptions.For(StitchMode.LongScroll),
            new FixedBands(fixedTop, fixedBottom), new FixedBands(fixedTop, fixedBottom));
        int safeStart = fixedTop;
        int safeEnd = overlap - fixedBottom;
        int expectedCut = safeStart + (safeEnd - safeStart) / 2;
        Battery.AssertEqual(expectedCut, r.SeamCutPosition,
            "蓝线须落在避开图2顶部固定栏与图1底部固定栏的共同正文中点");
        Battery.Assert(r.SeamCutPosition > fixedTop && r.SeamCutPosition < overlap - fixedBottom,
            $"蓝线 {r.SeamCutPosition} 必须使两侧都贡献正文，固定区 {fixedTop}/{fixedBottom}、重叠 {overlap}");
        Battery.Evidence($"重叠 {overlap} 行，安全裁切区 [{safeStart},{safeEnd}]，自动切点 {r.SeamCutPosition}");
    }

    /// <summary>把宽度裁掉几列（造"两张截图宽度不一致"的形状）。</summary>
    private static RgbaImage PngCrop(RgbaImage src, int width)
    {
        var dst = new RgbaImage(width, src.Height);
        dst.Blit(src, new IntRect(0, 0, width, src.Height), 0, 0);
        return dst;
    }

    /// <summary>一张有丰富纵向纹理的"长页面"。</summary>
    private static RgbaImage TallPage(int width, int height, int seed)
    {
        var img = new RgbaImage(width, height);
        uint state = (uint)seed | 1;
        int Next() { state = state * 1664525u + 1013904223u; return (int)(state >> 9); }

        for (int y = 0; y < height; y++)
        {
            byte g = (byte)(236 + (y % 5));
            Span<byte> row = img.Row(y);
            for (int x = 0; x < width; x++)
            {
                int o = x * 4;
                row[o] = g; row[o + 1] = g; row[o + 2] = g; row[o + 3] = 255;
            }
        }

        // 每隔若干行放一条"文字行"：位置、长度、灰度都由 seed 决定，保证纵向唯一可辨
        for (int y = 12; y < height - 6; y += 9 + Next() % 14)
        {
            int len = 10 + Next() % Math.Max(1, width - 14);
            byte v = (byte)(40 + Next() % 90);
            img.Fill(new IntRect(4, y, Math.Min(len, width - 5), 3), new RgbaColor(v, v, v, 255));
        }
        return img;
    }

    private static void FindsExactOverlap()
    {
        RgbaImage page = TallPage(64, 900, seed: 4242);
        const int firstHeight = 400, overlap = 300;
        (RgbaImage before, RgbaImage after, int expected) =
            TestProject.CutScrollPair(page, firstTop: 0, firstHeight, overlap);

        MatchResult r = OverlapMatcher.Match(before, after);

        Battery.AssertEqual(expected, r.OverlapLength, "重叠行数");
        Battery.AssertEqual(new IntPoint(0, before.Height - expected), r.Offset, "建议位移");
        Battery.AssertEqual(SeamStatus.AutoPassed, r.Status, $"应自动通过；实际原因：{r.CauseText}");
        Battery.Evidence($"相似度 {r.Confidence:P3}，{r.CauseText}");
    }

    /// <summary>
    /// 判据必须能分辨 1 像素：把后一张图人为挪 1 行，匹配结果就该跟着挪 1 行。
    /// 这条不过关的话，"自动匹配"就只是个大概位置，撑不起"一像素不出错"的承诺。
    /// </summary>
    private static void DistinguishesOnePixel()
    {
        RgbaImage page = TallPage(64, 900, seed: 777);
        (RgbaImage before, RgbaImage after, int expected) =
            TestProject.CutScrollPair(page, 0, 420, 260);

        int baseOffsetY = OverlapMatcher.Match(before, after).Offset.Y;

        // 人为把后图整体上移 1 行（等价于真实重叠少 1 行）
        RgbaImage shifted = after.SubImage(new IntRect(0, 1, after.Width, after.Height - 1));
        int shiftedOffsetY = OverlapMatcher.Match(before, shifted).Offset.Y;

        Battery.AssertEqual(before.Height - expected, baseOffsetY, "基准位置");
        Battery.AssertEqual(baseOffsetY + 1, shiftedOffsetY, "后图上移 1 行后的建议位移");
        Battery.Evidence($"基准 {baseOffsetY} → 挪 1 行后 {shiftedOffsetY}，逐像素可分辨");
    }

    /// <summary>
    /// 需求 §5.1 的核心陷阱：两张图顶部状态栏与底部输入栏几乎逐像素相同，
    /// 如果不排除固定带，"把后一张整张吃掉"会是个得分很高的假解。
    /// 这里构造"固定带相同、正文完全不同"的一对图，要求匹配器<b>不</b>给出大重叠。
    /// </summary>
    private static void FixedBarTrapNotConsumed()
    {
        const int width = 80, height = 400, bar = 60;
        var statusStrip = new RgbaImage(width, bar);
        statusStrip.Fill(new RgbaColor(30, 30, 32, 255));
        for (int i = 0; i < 5; i++)
            statusStrip.Fill(new IntRect(6 + i * 14, 20, 8, 18), RgbaColor.White);

        var a = new RgbaImage(width, height);
        var b = new RgbaImage(width, height);
        // 正文区：两张完全不同的稀疏内容（模拟"没滚多少但页面本身很空"）
        a.Fill(new RgbaColor(245, 245, 247, 255));
        b.Fill(new RgbaColor(245, 245, 247, 255));
        for (int i = 0; i < 12; i++)
        {
            a.Fill(new IntRect(8, bar + 10 + i * 22, 30 + (i % 5) * 6, 6), new RgbaColor(60, 60, 66, 255));
            b.Fill(new IntRect(20, bar + 16 + i * 22, 24 + (i % 3) * 9, 6), new RgbaColor(90, 40, 40, 255));
        }
        // 顶部与底部贴上完全相同的固定栏
        a.Blit(statusStrip, statusStrip.Bounds, 0, 0);
        a.Blit(statusStrip, statusStrip.Bounds, 0, height - bar);
        b.Blit(statusStrip, statusStrip.Bounds, 0, 0);
        b.Blit(statusStrip, statusStrip.Bounds, 0, height - bar);

        MatchResult r = OverlapMatcher.Match(a, b);

        // 判据一：固定带必须真的被检出并排除（这是需求 §5.1 那条要求本身）
        Battery.Assert(r.BandsBefore.Top >= bar - 20 && r.BandsBefore.Bottom >= bar - 20,
            $"未检出两侧的固定栏（实际 顶部 {r.BandsBefore.Top}、底部 {r.BandsBefore.Bottom}，应为 ~{bar}）：" +
            "排除没生效，后面的结论都不可信");

        // 判据二：最坏的失效模式是"把后一张整张吃掉"，即判定重叠 ≈ 整图高
        Battery.Assert(r.OverlapLength < a.Height - bar,
            $"判定重叠 {r.OverlapLength} 行已接近整张（正文完全不同），固定栏骗过了匹配器");

        // 判据三：不许自动通过。稀疏内容下"哪个 d 都好"是本质多解，产品答案是交给人看
        Battery.Assert(r.Status == SeamStatus.NeedsReview,
            $"正文无重叠时不该报自动通过；实际 {r.Status}：{r.CauseText}");
        Battery.Evidence($"排除固定带 {r.BandsBefore}；建议重叠 {r.OverlapLength} 行但降级为需检查：{r.CauseText}");
    }

    /// <summary>
    /// 真的没有重叠时，唯一必须成立的性质是<b>不许自动通过</b>、且给出的位移不会吃掉内容。
    /// 具体报 NoOverlap 还是 LargeDifference 取决于稀疏程度，两者对用户都是"这里要你看一眼"，
    /// 所以这里不断言具体原因——过度指定只会逼算法去猜一个它没有依据的结论。
    /// </summary>
    private static void NoOverlapIsHonest()
    {
        // 同一段长图的两屏，中间跳过一大段 → 真的没有重叠
        RgbaImage page = TallPage(64, 1200, seed: 31337);
        RgbaImage before = page.SubImage(new IntRect(0, 0, 64, 400));
        RgbaImage after = page.SubImage(new IntRect(0, 700, 64, 400));

        MatchResult r = OverlapMatcher.Match(before, after);
        Battery.Assert(r.Status == SeamStatus.NeedsReview,
            $"无重叠却被判成自动通过：{r.Status}（{r.CauseText}）");
        Battery.Assert(r.Cause is SeamCause.NoOverlap or SeamCause.OverlapTooShort or SeamCause.LargeDifference,
            $"原因应落在「确实对不上」这一类，实际 {r.Cause}");
        Battery.Assert(r.CauseText.Length > 0, "必须给出可读原因，界面不能只亮一个 !");
        Battery.Evidence($"{r.Cause}：{r.CauseText}（建议重叠 {r.OverlapLength} 行）");
    }

    private static void RepeatingPatternFlagsAmbiguity()
    {
        const int width = 64, period = 40;
        var a = new RgbaImage(width, 400);
        var b = new RgbaImage(width, 400);
        a.Fill(new RgbaColor(250, 250, 250, 255));
        b.Fill(new RgbaColor(250, 250, 250, 255));

        // 完全周期性的"重复列表项"：任意整数倍周期的对齐都同样好 → 本质多解
        for (int y = 0; y < 400; y += period)
        {
            a.Fill(new IntRect(6, y + 6, 40, 8), new RgbaColor(70, 70, 70, 255));
            a.Fill(new IntRect(6, y + 20, 28, 8), new RgbaColor(110, 110, 110, 255));
            b.Fill(new IntRect(6, y + 6, 40, 8), new RgbaColor(70, 70, 70, 255));
            b.Fill(new IntRect(6, y + 20, 28, 8), new RgbaColor(110, 110, 110, 255));
        }

        MatchResult r = OverlapMatcher.Match(a, b);
        Battery.Assert(r.Status == SeamStatus.NeedsReview,
            $"周期性纹理必须降级为需检查（需求 §5.1 明列'重复列表项'应降低置信度），实际 {r.Status}");
        Battery.AssertEqual(SeamCause.MultipleCandidates, r.Cause,
            $"原因应为多候选，实际 {r.Cause}：{r.CauseText}");
        Battery.Evidence($"{r.Candidates.Count} 个候选；{r.CauseText}");
    }

    private static void MixedWidthRefusesSuggestion()
    {
        var a = TallPage(64, 300, 5);
        var b = TallPage(60, 300, 5);
        MatchResult r = OverlapMatcher.Match(a, b);
        Battery.AssertEqual(SeamCause.MixedResolution, r.Cause, "宽度不同应给混合分辨率原因");
        Battery.Assert(!r.HasSuggestion, "宽度不同时不该给自动建议");
        Battery.Assert(r.CauseText.Contains("宽度不同"), $"文案要能指认问题，实际：{r.CauseText}");
    }

    /// <summary>
    /// 用户第四十四改报的现场：点「全部重新分析」大概率一条都不拼合、段直接堆在一起。
    /// 病根不在写回、也不在闸门，而在<b>取数</b>：裁切档里 <c>placed.Rect</c> 是蓝线切完之后
    /// 真正进像素的那一块，被切掉的那几行——两张截图唯一公共的内容——已经不在任何布局矩形里。
    /// 拿切完的两块去匹配，两侧一行公共内容都没有，匹配器只能回答"重叠 0"。
    /// 首分析碰巧没事（那时重叠还是 0、蓝线还在带顶），重分析就整批归零。
    /// 这一格把现场原样复现：先分析一次（种下重叠与切点），再在同一份参数上分析第二次，
    /// 第二次的结论必须与第一次一字不差；旧取数在这里第二次必然量回 0。
    /// 取数本身由 <c>guard/匹配取数必须含被蓝线切掉的那几行</c> 单独钉。
    /// </summary>
    private static void ReanalysisRecoversPlantedOverlap()
    {
        RgbaImage page = TallPage(64, 900, seed: 913);
        const int firstHeight = 400, overlap = 260;
        (RgbaImage before, RgbaImage after, _) = TestProject.CutScrollPair(page, 0, firstHeight, overlap);
        (Project p, MemoryResolver r, _) = TestProject.Create(StitchMode.LongScroll, before, after);
        var analyzer = new SeamAnalyzer(new CanvasComposer(r));
        Seam seam = p.Doc.Seams[0];

        analyzer.AnalyzeOne(p, seam);
        int firstOverlap = seam.OverlapLength;
        int firstCut = seam.CutY;
        Battery.AssertEqual(overlap, firstOverlap,
            "夹具自证：首分析必须量回种下去的 260 行，否则这一格量的是另一道题");

        analyzer.AnalyzeOne(p, seam);      // 布局此刻带着 260 行重叠与带中蓝线
        Battery.AssertEqual(firstOverlap, seam.OverlapLength,
            $"在已有重叠与切点上重分析必须量回同一个重叠（首 {firstOverlap} / 重 {seam.OverlapLength}）" +
            "——量回 0 就是用户看到的整批堆叠");
        Battery.AssertEqual(firstCut, seam.CutY, $"切点也必须稳定（首 {firstCut} / 重 {seam.CutY}）");
        Battery.Evidence($"首分析重叠 {firstOverlap} 行、切点 {firstCut}；重分析 {seam.OverlapLength} 行、切点 {seam.CutY} ⇒ 重分析幂等");
    }

    /// <summary>
    /// 上一格那把尺的自证：匹配取数 <c>RenderMatchInput</c> 必须<b>含被蓝线切掉的那几行</b>——
    /// 高度等于未裁重叠的完整高度，且 before 带尾与 after 带头那 overlap 行逐字节相同
    /// （公共内容真的在场，匹配器才有东西可对位）。
    /// 同一趟把旧取数（<c>RenderBand</c> 配 <c>placed.Rect</c>）的高度也量出来：它必须比完整带矮，
    /// 矮的那几行就是旧路重分析量回 0 的原因。两头一起写进证据，下一轮不必再猜一遍。
    /// </summary>
    private static void MatchInputContainsTrimmedRows()
    {
        RgbaImage page = TallPage(64, 900, seed: 927);
        const int firstHeight = 400, overlap = 260;
        (RgbaImage before, RgbaImage after, _) = TestProject.CutScrollPair(page, 0, firstHeight, overlap);
        (Project p, MemoryResolver r, _) = TestProject.Create(StitchMode.LongScroll, before, after);
        Seam seam = p.Doc.Seams[0];
        p.Mutate("种重叠与切点", _ =>
        {
            seam.SetOverlap(overlap);
            seam.CutY = overlap / 2;
            seam.Analyzed = true;
        });

        CanvasLayout layout = p.Layout;
        PlacedSegment b = layout.Placed[0];
        PlacedSegment a = layout.Placed[1];
        Battery.Assert(b.Rect.Height < b.FullRect.Height,
            $"夹具自证：裁切档下切完那块必须比完整带矮（{b.Rect.Height} < {b.FullRect.Height}），否则这一格没走到裁切形状");

        var composer = new CanvasComposer(r);
        RgbaImage mb = composer.RenderMatchInput(b);
        RgbaImage ma = composer.RenderMatchInput(a);
        Battery.AssertEqual(b.FullRect.Height, mb.Height,
            $"匹配取数的高度必须是未裁重叠的完整高度（{b.FullRect.Height}），实际 {mb.Height}");

        int bad = 0;
        for (int i = 0; i < overlap; i++)
            if (!mb.Row(mb.Height - overlap + i).SequenceEqual(ma.Row(i))) bad++;
        Battery.AssertEqual(0, bad,
            $"before 带尾 {overlap} 行与 after 带头 {overlap} 行必须逐字节相同（实际 {bad} 行不同）——不同就是公共内容没取到");

        RgbaImage oldB = composer.RenderBand(layout, b.Rect);
        Battery.Assert(oldB.Height < mb.Height,
            $"旧取数高度 {oldB.Height} 必须矮于完整带 {mb.Height}：矮的那几行就是旧路重分析量回 0 的原因");
        Battery.Evidence($"匹配取数 {mb.Width}×{mb.Height}（完整带），公共 {overlap} 行逐字节相同 {overlap - bad}/{overlap}；" +
                         $"旧取数 {oldB.Width}×{oldB.Height}，矮 {mb.Height - oldB.Height} 行");
    }
}
