using LongStitch.Core;
using LongStitch.Core.Export;
using LongStitch.Core.Geometry;
using LongStitch.Core.Imaging;
using LongStitch.Core.Imaging.Png;
using LongStitch.Core.Layout;
using LongStitch.Core.Matching;
using LongStitch.Core.Model;

namespace LongStitch.Checks;

/// <summary>
/// 长截图自动拼合的核心判据。
///
/// ═══ 这一组格子守的是哪两件事 ═══
/// ① <b>横向锁死</b>：长截图是同一页面纵向滚动的连拍，物理上不存在左右位移。
///    允许横向挪 N 像素的后果是——后一段盖住 x∈[N, W+N)，前一段在 x∈[0, N) 又露出来，
///    同一段内容在成品里出现两次、画布凭空变宽，而且缩略图上看不出来。
///    所以规则只写在 <see cref="StitchAxes"/> 一处，界面/匹配/布局/导出都引用它，
///    并由 <see cref="CanvasLayout.Build"/> 在像素落位前兜最后一道。
/// ② <b>重叠真的被去掉了</b>：不是"看着像去掉了"，而是能用整数核对的三条性质——
///    <c>offsetY = 前段高 − 重叠</c>、<c>总高 = 前段高 + 后段高 − 重叠</c>、
///    以及"重叠区里的那一行在整张成品里只出现一次"。
///    每条都配了反例（故意不去重、故意把第二张整张画在下面），否则判据可能只是在空转。
/// </summary>
public static class LongScrollTests
{
    public static void Register()
    {
        Battery.Add("longscroll", "X 到不了像素：手动/自动/拖动预览三条路都归零", HorizontalNeverReachesPixels);
        Battery.Add("longscroll", "匹配到横向偏移时按 X=0 采用并降级需检查", SidewaysFitIsRejectedAndSaid);
        Battery.Add("longscroll", "匹配参数只认模式：长截图锁、竖拼横拼不锁", MatchOptionsFollowMode);
        Battery.Add("longscroll", "去重公式：offsetY=H1-overlap，总高=H1+H2-overlap", DedupFormulaOnGeometry);
        Battery.Add("longscroll", "重叠那一行在成品里只出现一次（不去重时会出现两次）", OverlapContentAppearsExactlyOnce);
        Battery.Add("longscroll", "导出文件解码回来：高度=ΣH−Σ重叠，内容不重复", ExportedFileDecodesToDedupedHeight);
        Battery.Add("longscroll", "切模式不抹掉用户存的横向值，只是不再生效", ModeSwitchKeepsStoredCrossOffset);
    }

    // ───────────────────────── 合成样本 ─────────────────────────

    /// <summary>
    /// 一张"每行都长得不一样"的长页面：行内是横条 + 随行号平移的竖条。
    /// 行与行唯一 → 重叠区里的某一行可以在成品里被逐行数出来出现了几次；
    /// 列方向有明显边缘 → 横向挪 1 列就能被像素比对量出来。
    /// internal 是因为第九轮的 crop 组要用同一份样本（两处各写一遍"每行唯一"迟早漂移）。
    /// </summary>
    internal static RgbaImage StripedPage(int width, int height)
    {
        var img = new RgbaImage(width, height);
        for (int y = 0; y < height; y++)
        {
            byte v = (byte)(y % 233);
            Span<byte> row = img.Row(y);
            for (int x = 0; x < width; x++)
            {
                int o = x * 4;
                row[o] = v;
                row[o + 1] = (byte)(255 - v);
                row[o + 2] = (byte)((x + y) % 211);
                row[o + 3] = 255;
            }
            // 两条宽度随行号变化的横条：保证任意两行都不相同（周期 233 与 11 互质）
            img.Fill(new IntRect(2, y, 3 + y % 11, 1), new RgbaColor(9, 9, 9, 255));
            img.Fill(new IntRect(3, y + 1 > height - 1 ? y : y + 1, 2 + (y * 7) % 9, 1),
                new RgbaColor(250, 250, 250, 255));
        }
        return img;
    }

    /// <summary>
    /// 把图整体横向挪 <paramref name="dx"/> 列，空出来的边用同一行的边缘像素复制填满。
    /// 模拟"两张截图的左边框宽度不一样"——内容完全相同，只是整体左右错开。
    /// 不引入新颜色，所以匹配器有充分理由认为"贴得上"，横向偏移也就一定会被量出来。
    /// </summary>
    private static RgbaImage ShiftHorizontally(RgbaImage src, int dx)
    {
        var outImg = new RgbaImage(src.Width, src.Height);
        for (int y = 0; y < src.Height; y++)
        {
            ReadOnlySpan<byte> row = src.Row(y);
            Span<byte> dst = outImg.Row(y);
            for (int x = 0; x < src.Width; x++)
            {
                int from = Math.Clamp(x - dx, 0, src.Width - 1);
                int o = x * 4, s = from * 4;
                dst[o] = row[s];
                dst[o + 1] = row[s + 1];
                dst[o + 2] = row[s + 2];
                dst[o + 3] = row[s + 3];
            }
        }
        return outImg;
    }

    /// <summary>
    /// 把"重叠 120 行"这件事写进项目：走 AutoOffset 而不是 ManualOffset，
    /// 这样 <see cref="SeamGeometry.FormulaHolds"/> 才是真判定（手动缝本来就允许与公式不符）。
    /// </summary>
    private static void WriteAutoResult(Project project, int offsetY, int overlap)
    {
        project.Mutate("写回自动结论", d =>
        {
            Seam seam = d.Seams[0];
            seam.AutoOffset = new IntPoint(0, offsetY);
            // 第九轮：裁切档的去重量是 SetOverlap 摆出来的两侧裁切量，直接赋 OverlapLength
            // 会留下 (0,0) 的裁切——那等于"一行都没去掉"，画布会凭空高出一段。
            seam.SetOverlap(overlap);
            seam.Analyzed = true;
            seam.Status = SeamStatus.AutoPassed;
            seam.AutoStatus = SeamStatus.AutoPassed;
        });
    }

    /// <summary>数画布里有几行与指定的那一行逐字节相同（crop 组同样用它数"重复了几次"）。</summary>
    internal static int CountRowsEqualTo(RgbaImage canvas, ReadOnlySpan<byte> reference)
    {
        int hits = 0;
        for (int y = 0; y < canvas.Height; y++)
            if (canvas.Row(y).SequenceEqual(reference)) hits++;
        return hits;
    }

    // ───────────────────────── ① 横向锁死 ─────────────────────────

    /// <summary>
    /// 三条能把 X 带进来的路——手动值、自动值、拖动预览覆盖——都必须被布局拦在像素之外。
    /// 只测一条的话，另一条哪天漏了就没人知道；竖拼那半边是同一段代码的正向对照，
    /// 用来证明"归零"不是无条件把所有 X 都吃掉（那会破坏竖拼/横拼）。
    /// </summary>
    private static void HorizontalNeverReachesPixels()
    {
        foreach (StitchMode mode in new[] { StitchMode.LongScroll, StitchMode.Vertical })
        {
            bool locked = mode == StitchMode.LongScroll;
            var a = Fixture.OpaqueLike(40, 300, seed: 11);
            var b = Fixture.OpaqueLike(40, 300, seed: 12);
            (Project p, _, _) = TestProject.Create(mode, a, b);
            Seam seam = p.Doc.Seams[0];
            string tag = locked ? "长截图" : "竖拼";

            // 路 1：参数里直接存着横向值（旧项目文件、别处写进来的都算这种形状）
            p.Mutate("塞进横向自动值", d => d.Seams[0].AutoOffset = new IntPoint(7, 180));
            IntPoint tight = p.Layout.TightOffset(seam);
            Battery.AssertEqual(locked ? 0 : 7, p.Layout.EffectiveOffset(seam).X, $"{tag}：自动值的 X");
            Battery.AssertEqual(locked ? 0 : 7, p.Layout.Placed[1].Origin.X, $"{tag}：像素落位的 X");
            // 横挪 7 会把画布撑宽 7（两段各占一边）——这正是"长截图必须锁横向"的直观代价
            Battery.AssertEqual(locked ? 40 : 47, p.Layout.CanvasSize.Width, $"{tag}：画布宽");
            // ResolveOffset 给的是"参数原值"，EffectiveOffset 给的是"真正生效值"——两者必须能分开读
            Battery.AssertEqual(7, seam.ResolveOffset(tight).X, $"{tag}：原值仍可读出（界面要说清被锁了多少）");

            // 路 2：ApplyManual 写入时按模式归零，并把丢掉的量报出来
            ManualApply result = default;
            p.Mutate("手调", _ => result = seam.ApplyManual(new IntPoint(5, 160), mode));
            Battery.Assert(result.Applied, $"{tag}：Y 从 180 改到 160 属于有效改动，必须写入");
            Battery.AssertEqual(locked ? 5 : 0, result.RejectedHorizontalShift,
                $"{tag}：被锁掉的横向位移要如实报 5（竖拼不锁，应为 0）");
            Battery.AssertEqual(locked ? 0 : 5, seam.ManualOffset!.Value.X, $"{tag}：手动值落库的 X");
            Battery.AssertEqual(locked ? 0 : 5, p.Layout.Placed[1].Origin.X, $"{tag}：拖动后的像素 X");
            Battery.AssertEqual(locked ? 40 : 45, p.Layout.CanvasSize.Width, $"{tag}：手动后再看画布宽");
            if (locked)
                Battery.Assert(seam.CauseText is not null && seam.CauseText.Contains("锁定", StringComparison.Ordinal),
                    $"被锁掉必须在原因里说出来，实际：{seam.CauseText}");
            else
                Battery.Assert(seam.CauseText is null, $"竖拼里没有东西被锁掉，不该有补充原因，实际：{seam.CauseText}");

            // 路 3：拖动预览的覆盖值
            var preview = CanvasLayout.Build(p.Doc,
                new Dictionary<string, IntPoint> { [seam.Id] = new IntPoint(9, 150) });
            Battery.AssertEqual(locked ? 0 : 9, preview.Placed[1].Origin.X, $"{tag}：拖动预览的 X");
            Battery.AssertEqual(150, preview.Placed[1].Origin.Y, $"{tag}：主轴位移不许被连带改掉");

            Battery.Evidence($"{tag}：自动值/手动值/拖动预览三条路的 X 分别被处理为 " +
                             $"{p.Layout.EffectiveOffset(seam).X}、{seam.ManualOffset!.Value.X}、" +
                             $"{preview.Placed[1].Origin.X}");
        }
    }

    /// <summary>
    /// 匹配到横向贴合更好时：采用值仍是 X=0，但"数据想横着挪"必须被记录下来并降级为需检查。
    /// 反向对照用竖拼跑同一对图——那里 X 该跟着匹配结果走，
    /// 用以证明上面的 0 是"锁掉的"，不是"算法根本没看见横向偏移"。
    /// </summary>
    private static void SidewaysFitIsRejectedAndSaid()
    {
        RgbaImage page = StripedPage(64, 760);
        (RgbaImage before, RgbaImage after, int expectedOverlap) =
            TestProject.CutScrollPair(page, firstTop: 0, firstHeight: 400, overlap: 300);
        RgbaImage skewed = ShiftHorizontally(after, 3);

        MatchResult locked = OverlapMatcher.Match(before, skewed, MatchOptions.For(StitchMode.LongScroll));
        MatchResult free = OverlapMatcher.Match(before, skewed, MatchOptions.For(StitchMode.Vertical));

        Battery.Assert(free.HorizontalShift != 0,
            "反向对照：竖拼里匹配器必须量出横向偏移，否则下面的\"锁定\"等于什么都没锁");
        Battery.AssertEqual(0, locked.Offset.X, "长截图里采纳的位移 X 必须为 0");
        Battery.AssertEqual(0, locked.HorizontalShift, "长截图里回报的采用值 X 为 0");
        Battery.Assert(locked.DetectedHorizontalShift != 0,
            $"被锁掉的那个横向偏移必须原样报出来，实际 detected_x={locked.DetectedHorizontalShift}");
        Battery.Assert(locked.HorizontalShiftLocked, "结果要带上\"因锁横向而需检查\"这一面旗");
        Battery.AssertEqual(SeamStatus.NeedsReview, locked.Status, $"应降级为需检查，实际 {locked.Status}");
        Battery.AssertEqual(SeamCause.HorizontalShiftLocked, locked.Cause, "原因要能指认是横向被锁");
        Battery.Assert(locked.CauseText.StartsWith("检测到横向偏移，但长截图模式已锁定左右方向", StringComparison.Ordinal),
            $"文案要按约定的那句话开头，实际：{locked.CauseText}");
        Battery.Assert(locked.CauseText.Contains("请人工确认截图宽度或拼缝", StringComparison.Ordinal),
            $"文案要给出下一步动作，实际：{locked.CauseText}");
        Battery.AssertEqual(expectedOverlap, free.OverlapLength,
            "竖拼里匹配到的重叠仍应是构造时的真值（说明这对图本身是能对上的）");
        Battery.Evidence($"锁横向：采纳 X=0、需要 X={locked.DetectedHorizontalShift}、{locked.Status}；" +
                         $"不锁：X={free.HorizontalShift}、重叠 {free.OverlapLength} 行");
    }

    /// <summary>
    /// 锁不锁横向只由模式决定：传进来的 LockHorizontal 会被模式覆写。
    /// 这条守的是"多处同值迟早漂移"——界面、后台分析、命令行工装各写一遍
    /// <c>mode == LongScroll</c>，漏一处就出现"竖拼的分析被误锁 / 长截图忘了锁"。
    /// </summary>
    private static void MatchOptionsFollowMode()
    {
        Battery.Assert(MatchOptions.For(StitchMode.LongScroll).LockHorizontal, "长截图必须锁横向");
        Battery.Assert(!MatchOptions.For(StitchMode.Vertical).LockHorizontal, "竖拼不许锁横向（独立图片可以自由摆）");
        Battery.Assert(!MatchOptions.For(StitchMode.Horizontal).LockHorizontal, "横拼的交叉轴是纵向，横向本来就该能动");

        // 模式说了算：外部硬塞的锁位要被改回模式对应的值
        Battery.Assert(MatchOptions.For(StitchMode.LongScroll,
            new MatchOptions { LockHorizontal = false }).LockHorizontal, "长截图下传 false 也要被锁上");
        Battery.Assert(!MatchOptions.For(StitchMode.Vertical,
            new MatchOptions { LockHorizontal = true }).LockHorizontal, "竖拼下传 true 必须被取消");

        // 其他阈值原样保留：锁横向不该顺手把标定过的参数换成默认值
        var tuned = new MatchOptions { MinOverlap = 77, PassSimilarity = 0.9, RefineDx = 2 };
        MatchOptions derived = MatchOptions.For(StitchMode.LongScroll, tuned);
        Battery.AssertEqual(77, derived.MinOverlap, "MinOverlap 必须保留");
        Battery.AssertEqual(0.9, derived.PassSimilarity, "PassSimilarity 必须保留");
        Battery.AssertEqual(2, derived.RefineDx, "RefineDx 必须保留（搜索窗口仍要量横向偏移，只是不采用）");
        Battery.Assert(ReferenceEquals(tuned, MatchOptions.For(StitchMode.Vertical, tuned)),
            "模式与参数已经一致时应返回同一个实例，别每轮分析都造一份新的");

        // 轴规则本身：只有长截图锁，且只归零 X
        Battery.Assert(StitchAxes.LocksHorizontal(StitchMode.LongScroll), "长截图锁横向");
        Battery.Assert(!StitchAxes.LocksHorizontal(StitchMode.Vertical), "竖拼不锁");
        Battery.AssertEqual(new IntPoint(0, -13),
            StitchAxes.Enforce(StitchMode.LongScroll, new IntPoint(9, -13)), "只抹 X，Y 原样");
        Battery.AssertEqual(new IntPoint(9, -13),
            StitchAxes.Enforce(StitchMode.Horizontal, new IntPoint(9, -13)), "横拼原样");
        Battery.AssertEqual(MoveConstraint.VerticalOnly,
            StitchAxes.Enforce(StitchMode.LongScroll, MoveConstraint.HorizontalOnly),
            "长截图里用户即使选了「仅左右」，生效的也只能是「仅上下」");
        Battery.AssertEqual(MoveConstraint.HorizontalOnly,
            StitchAxes.Enforce(StitchMode.Vertical, MoveConstraint.HorizontalOnly),
            "竖拼里「仅左右」照旧生效");
    }

    // ───────────────────────── ② 重叠真的被去掉 ─────────────────────────

    /// <summary>
    /// 用户要的验收公式，直接摆在这条格子里：
    /// <c>offsetY = H1 − overlap</c>、<c>总高 = H1 + H2 − overlap</c>。
    /// 数从 <see cref="CanvasLayout.DescribeSeam"/> 取——它量的是<b>画布上的实际落位</b>，
    /// 不是参数里写的那个值，所以"声称去重 120 行、实际一行没去"会当场对不上。
    /// </summary>
    private static void DedupFormulaOnGeometry()
    {
        const int width = 40, h1 = 300, h2 = 220, overlap = 120;
        var a = Fixture.OpaqueLike(width, h1, seed: 21);
        var b = Fixture.OpaqueLike(width, h2, seed: 22);
        (Project p, _, _) = TestProject.Create(StitchMode.LongScroll, a, b);
        WriteAutoResult(p, h1 - overlap, overlap);

        SeamGeometry g = p.Layout.DescribeSeam(p.Doc.Seams[0])!;
        Battery.AssertEqual(new IntSize(width, h1), g.BeforeSize, "前段尺寸");
        Battery.AssertEqual(new IntSize(width, h2), g.AfterSize, "后段尺寸");
        // ═══ 重叠只能扣一次（第十轮按需求五补的两头对照）═══
        // 上面那两格量的是"源图那两段有多长"（H1/H2），扣完之后的长度在下面这两格：
        // 只扣一次 = 180，扣两次 = 60。哪天布局既裁矩形又减位移，这里当场红。
        Battery.AssertEqual(h1 - overlap, g.BeforeRect.Height, "前段裁过之后进像素的行数（=H1−O，不是 H1−2O）");
        Battery.AssertEqual(overlap, g.AppliedBeforeCrop + g.AppliedAfterCrop, "两侧裁切量之和就是 O，不多不少");
        Battery.Assert(g.RectsDisjoint,
            $"两段在画布上必须首尾相接、不相交（相交说明还在靠绘制顺序去重）：{g}");
        Battery.AssertEqual(h1 - overlap, g.PrimaryOffset, "offsetY 应 = 前段高 − 重叠");
        Battery.AssertEqual(h1 - overlap, g.ExpectedPrimaryOffset, "公式给出的应有值");
        Battery.AssertEqual(overlap, g.Overlap, "画布上量到的重叠");
        Battery.AssertEqual(overlap, g.ReportedOverlap, "分析记录的重叠（两者必须一致）");
        Battery.AssertEqual(h1 + h2 - overlap, g.PairExtent, "总高应 = H1 + H2 − 重叠");
        Battery.Assert(g.FormulaHolds, $"去重公式不成立：{g}");
        Battery.Assert(g.OverlapMatchesReport, "布局量到的重叠与分析记录不一致");
        Battery.Assert(g.CrossAxisHolds, "长截图的交叉轴位移必须为 0");

        // 整条链的总账（三段：只有一段与下一段之间的重叠被计入）
        DedupAccount account = p.Layout.MeasureDedup();
        Battery.AssertEqual<long>(h1 + h2, account.SumExtents, "各段长度之和");
        Battery.AssertEqual<long>(overlap, account.SumOverlaps, "各重叠之和");
        Battery.AssertEqual<long>(h1 + h2 - overlap, account.Expected, "公式算出的画布高");
        Battery.AssertEqual<long>(h1 + h2 - overlap, p.Layout.CanvasSize.Height, "画布实际高");
        Battery.Assert(account.Holds, $"总账不平：{account}");

        // 反例：把去重量真的设成 0（两段首尾相接、一行都不裁），画布必须立刻高出整整一段重叠。
        // 第九轮之后这个反例只剩这一种写法：裁切模型里"声称去掉 120 行、实际一行没裁"
        // 已经<b>写不出来</b>（OverlapLength 的 setter 会同步两侧裁切量，不变式不破）。
        // 于是这一格守的东西也跟着变了：从"账面能不能对上"变成"高度真的由裁切量决定"——
        // 只要布局哪天不看裁切量（写死公式、或忘了读 b/t），这里就是 h1+h2-overlap，当场翻红。
        WriteAutoResult(p, h1, 0);
        SeamGeometry cheat = p.Layout.DescribeSeam(p.Doc.Seams[0])!;
        Battery.AssertEqual(0, cheat.Overlap, "反例里布局量到的去重量必须是 0");
        Battery.Assert(!cheat.HasBand, "没有重叠就不该有重叠带（红框要跟着消失，不然命中区会盖到空处）");
        Battery.AssertEqual(h1 + h2, p.Layout.CanvasSize.Height, "不去重时画布确实高出整段重叠");
        Battery.AssertEqual(h1 + h2, cheat.PairExtent, "两段的总跨度也必须跟着变");
        Battery.Assert(cheat.FormulaHolds,
            "反例自身的账应当是自洽的（去掉 0 行也算得平）——\"声称去掉却实际没去掉\"由上一条不变式挡在门外");
        Battery.Evidence($"正例总跨度 {g.PairExtent} 行；反例去掉 0 行 → 总跨度 {cheat.PairExtent} 行、" +
                         $"画布 {p.Layout.CanvasSize.Height} 行");
    }

    /// <summary>
    /// 最硬的一条：重叠区里那一行内容在整张成品里<b>只出现一次</b>。
    /// 前段末尾与后段开头本来就是同一页内容，去重做对了它们合起来只有一份；
    /// 如果实现只是"把第二张整个画在下面"，同样的行就会出现两次——这里能数出来。
    /// </summary>
    private static void OverlapContentAppearsExactlyOnce()
    {
        const int h1 = 300, overlap = 120, markerRow = 250;   // 第 250 行落在重叠区（180–300）内
        RgbaImage page = StripedPage(40, h1 + 220 - overlap);
        RgbaImage before = page.SubImage(new IntRect(0, 0, 40, h1));
        RgbaImage after = page.SubImage(new IntRect(0, h1 - overlap, 40, 220));

        // 判据自己先自证：这一行在整页里唯一，否则"数出现几次"毫无意义
        Battery.AssertEqual(1, CountRowsEqualTo(page, page.Row(markerRow)), "样本页里被数的那一行必须唯一");

        (CanvasComposer composer, Project project, MemoryResolver _) =
            BuildProject(before, after, overlap, dedup: true);
        RgbaImage canvas = composer.RenderWhole(project.Layout);
        Battery.AssertEqual(h1 + 220 - overlap, canvas.Height, "去重后的画布高");
        Battery.AssertEqual(1, CountRowsEqualTo(canvas, page.Row(markerRow)),
            "重叠区里那一行在成品里应当只有一份");
        // 前段独有的那一段（比后段开头更早的行）也必须还在——去重不许顺手吃掉唯一内容
        Battery.AssertEqual(1, CountRowsEqualTo(canvas, page.Row(10)), "前段独有的行必须保留");
        Battery.AssertEqual(1, CountRowsEqualTo(canvas, page.Row(canvas.Height - 1)), "后段独有的末行必须保留");

        // 反例：同样的两张图，位移改成"紧接排列"（一行都不去重）→ 同一行出现两次
        (CanvasComposer cheatComposer, Project cheatProject, _) =
            BuildProject(before, after, overlap, dedup: false);
        RgbaImage cheatCanvas = cheatComposer.RenderWhole(cheatProject.Layout);
        Battery.AssertEqual(h1 + 220, cheatCanvas.Height, "不去重时画布高出整整一段重叠");
        Battery.AssertEqual(2, CountRowsEqualTo(cheatCanvas, page.Row(markerRow)),
            "反例必须真能数出两次，否则上面的\"只出现一次\"是空判据");
        Battery.Evidence($"去重 {canvas.Height} 行/该行出现 1 次；不去重 {cheatCanvas.Height} 行/出现 2 次");
    }

    /// <summary>
    /// 走产品的导出引擎写出 PNG，再解码回来核对：
    /// 尺寸必须等于 <c>ΣH − Σ重叠</c>，且被数的那一行仍只有一份。
    /// 这一格是"预览没问题、导出才发现重复"这类缺陷的唯一防线。
    /// </summary>
    private static void ExportedFileDecodesToDedupedHeight()
    {
        const int h1 = 300, overlap = 120, markerRow = 250;
        RgbaImage page = StripedPage(40, h1 + 220 - overlap);
        RgbaImage before = page.SubImage(new IntRect(0, 0, 40, h1));
        RgbaImage after = page.SubImage(new IntRect(0, h1 - overlap, 40, 220));
        (CanvasComposer composer, Project project, MemoryResolver resolver) =
            BuildProject(before, after, overlap, dedup: true);

        string outDir = Path.Combine(Path.GetTempPath(), "ls-dedup-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(outDir);
        try
        {
            var encoders = new EncoderRegistry();
            var settings = new ExportSettings
            {
                Format = ExportFormat.Png,
                OutputDirectory = outDir,
                MaxSegmentHeight = 0,       // 不分段
                SegmentOverlap = 0,
                DownscaleDivisor = 1,       // 不缩小：缩小过再核尺寸就成了自证
                FileNameTemplate = "dedup_{index}.{ext}",
            };
            ExportPlan plan = ExportPlanner.Build(project.Layout, settings, encoders, outDir, composer, resolver);
            Battery.Assert(!plan.IsHardBlock, $"计划被硬拦：{plan.BlockingReason}");

            var engine = new ExportEngine(composer, encoders);
            ExportOutcome outcome = engine.Run(project.Layout, plan, settings, outDir);

            Battery.Assert(outcome.Success, $"导出失败：{outcome.Error}");
            Battery.AssertEqual(1, outcome.Files.Count, "不分段应只有一个文件");
            byte[] bytes = File.ReadAllBytes(outcome.Files[0]);

            RgbaImage back = PngDecoder.Decode(bytes).Image;
            int expectedHeight = h1 + 220 - overlap;
            Battery.AssertEqual(40, back.Width, "导出宽 = 源图宽（锁横向之后不该被撑宽）");
            Battery.AssertEqual(expectedHeight, back.Height, "导出的高度必须 = ΣH − Σ重叠");
            Battery.AssertEqual(expectedHeight, plan.OriginalPixelSize.Height, "计划尺寸与实际写出一致");
            Battery.AssertEqual(1, CountRowsEqualTo(back, page.Row(markerRow)),
                "文件解码回来仍只有一份重叠内容——导出路径没把重复画回去");
            Battery.AssertEqual(1, CountRowsEqualTo(back, page.Row(back.Height - 1)), "末段独有行也在成品里");
            Battery.Evidence($"导出 {back.Width}×{back.Height}，声称去掉 {overlap} 行、" +
                             $"实际高度与公式一致，被数的那一行出现 1 次");
        }
        finally
        {
            try { Directory.Delete(outDir, recursive: true); } catch { /* 临时目录清不掉不影响判据 */ }
        }
    }

    /// <summary>
    /// 换模式不许改写用户存的手动值：长截图里它只是<b>不生效</b>，切回竖拼应当原样回来。
    /// 静默清掉的话，用户切个模式试一下就被抹了参数，而且撤销栈里都找不到。
    /// </summary>
    private static void ModeSwitchKeepsStoredCrossOffset()
    {
        var a = Fixture.OpaqueLike(40, 300, seed: 31);
        var b = Fixture.OpaqueLike(40, 300, seed: 32);
        (Project p, _, _) = TestProject.Create(StitchMode.Vertical, a, b);
        Seam seam = p.Doc.Seams[0];

        p.Mutate("竖拼里横向挪 7", _ => seam.ApplyManual(new IntPoint(7, 260), StitchMode.Vertical));
        Battery.AssertEqual(7, p.Layout.Placed[1].Origin.X, "竖拼里横向照旧生效");
        // 总高 = offsetY + 后段高 = 260 + 300 = 560（等价于 两段高之和 − 重叠 40）
        Battery.AssertEqual(560, p.Layout.CanvasSize.Height, "竖拼里总高 = offsetY + H2");

        p.Mutate("切到长截图", d => d.Mode = StitchMode.LongScroll);
        Battery.AssertEqual(7, seam.ManualOffset!.Value.X, "参数里的横向值不许被模式切换抹掉");
        Battery.AssertEqual(0, p.Layout.EffectiveOffset(seam).X, "长截图里生效值 X 归零");
        Battery.AssertEqual(0, p.Layout.Placed[1].Origin.X, "长截图里像素落位 X 归零");
        Battery.AssertEqual(260, p.Layout.Placed[1].Origin.Y, "纵向位移不受影响");

        p.Mutate("切回竖拼", d => d.Mode = StitchMode.Vertical);
        Battery.AssertEqual(7, p.Layout.Placed[1].Origin.X, "切回竖拼后用户原来存的横向值又回来了");
        Battery.Evidence("竖拼 X=7 → 长截图 X=0（参数仍是 7）→ 竖拼 X=7；Y 全程 260 未动");
    }

    // ───────────────────────── 工装 ─────────────────────────

    /// <summary>
    /// 造一个两段、模式为长截图的项目，并把重叠写成<b>自动结论</b>（不是手动值——手动缝的
    /// 公式判定按设计不参与，见 <see cref="SeamGeometry.FormulaHolds"/>）。
    /// <paramref name="dedup"/>=false 时把位移写成"紧接排列"，即一行都不去重，专门用来当反例。
    /// </summary>
    /// <summary>
    /// 造一个两段、模式为长截图的项目，并把重叠写成<b>自动结论</b>（不是手动值——手动缝的
    /// 公式判定按设计不参与，见 <see cref="SeamGeometry.FormulaHolds"/>）。
    /// <paramref name="dedup"/>=false 时把<b>去重量真的设成 0</b>：裁切档里这就是"一行都不裁、
    /// 两段首尾相接"，也就是需求里那句"把第二张整张画在下面"现在的表达方式
    /// （旧的"只改位移不改重叠"写法在不变式下已经构造不出来了）。
    /// </summary>
    private static (CanvasComposer Composer, Project Project, MemoryResolver Resolver) BuildProject(
        RgbaImage before, RgbaImage after, int overlap, bool dedup)
    {
        (Project project, MemoryResolver resolver, _) =
            TestProject.Create(StitchMode.LongScroll, before, after);
        WriteAutoResult(project, dedup ? before.Height - overlap : before.Height,
            dedup ? overlap : 0);
        return (new CanvasComposer(resolver), project, resolver);
    }
}
