using LongStitch.Core;
using LongStitch.Core.Geometry;
using LongStitch.Core.Imaging;
using LongStitch.Core.Model;

namespace LongStitch.Checks;

/// <summary>
/// 布局引擎电池。布局是"参数 → 像素去向"的唯一换算处，
/// 这里断言的都是可以直接讲给用户听的性质，而不是内部字段值。
/// </summary>
public static class LayoutTests
{
    public static void Register()
    {
        Battery.Add("layout", "竖排紧接：高度相加", VerticalTight);
        Battery.Add("layout", "横排紧接：宽度相加", HorizontalTight);
        Battery.Add("layout", "偏移改 1 像素，画布恰好错 1 像素", OnePixelOffsetExact);
        Battery.Add("layout", "切分不改变任何可见像素", SplitKeepsPixelsIdentical);
        Battery.Add("layout", "重排后仍相邻的缝保留参数", ReorderKeepsAdjacentSeam);
        Battery.Add("layout", "源图丢失不崩且该段尺寸为 0", MissingSourceIsSafe);
        Battery.Add("layout", "裁边缩到空不崩", OverCropIsSafe);
        Battery.Add("layout", "交叉轴居中按最大宽度对齐", CrossAxisCentering);
        Battery.Add("layout", "撤销一步回到上一版偏移", UndoRestoresOffset);
        Battery.Add("layout", "自动分析写回不盖手动显示状态", AutoResultKeepsManualDisplay);
    }

    /// <summary>
    /// 审查第四轮 P1：已有手动偏移的缝，重新分析只许刷新"自动侧信息"，
    /// 显示状态必须保持手动——否则布局按手动偏移摆放、界面却显示"✓ 自动通过"。
    /// </summary>
    private static void AutoResultKeepsManualDisplay()
    {
        var auto = new IntPoint(0, 120);
        var manual = new IntPoint(0, 100);
        var candidates = new List<MatchCandidate> { new(auto, 0.99, 120) };

        // 手动未确认：写回自动结论后 Status 必须还是 ManualPending
        var s1 = new Seam { Id = "s1", BeforeId = "a", AfterId = "b" };
        s1.ApplyManual(manual, StitchMode.Vertical);
        s1.ApplyAutoResult(auto, SeamStatus.AutoPassed, SeamCause.None, null, 0.99, 120, candidates);
        Battery.AssertEqual(manual, s1.ManualOffset!.Value, "手动偏移不许丢");
        Battery.AssertEqual(auto, s1.AutoOffset!.Value, "自动建议照常更新");
        Battery.AssertEqual(SeamStatus.AutoPassed, s1.AutoStatus, "AutoStatus 记录自动结论");
        Battery.AssertEqual(SeamStatus.ManualPending, s1.Status, "手动缝的显示状态不许被盖成自动通过");
        Battery.Assert(!s1.Confirmed, "确认状态不许被自动分析改动");

        // 手动已确认：写回后仍是 ManualConfirmed + Confirmed
        var s2 = new Seam { Id = "s2", BeforeId = "a", AfterId = "b" };
        s2.ApplyManual(manual, StitchMode.Vertical);
        s2.Confirm();
        s2.ApplyAutoResult(auto, SeamStatus.NeedsReview, SeamCause.MultipleCandidates, "多解", 0.8, 120, candidates);
        Battery.AssertEqual(SeamStatus.ManualConfirmed, s2.Status, "已确认的手动缝不许被降级或改口");
        Battery.Assert(s2.Confirmed, "Confirmed 保持");
        Battery.AssertEqual(SeamStatus.NeedsReview, s2.AutoStatus, "自动侧信息仍然刷新了");

        // 对照：纯自动缝该覆盖就覆盖
        var s3 = new Seam { Id = "s3", BeforeId = "a", AfterId = "b" };
        s3.ApplyAutoResult(auto, SeamStatus.AutoPassed, SeamCause.None, null, 0.99, 120, candidates);
        Battery.AssertEqual(SeamStatus.AutoPassed, s3.Status, "没有手动值时显示状态跟着自动结论");
        Battery.Assert(s3.Analyzed, "Analyzed 置位");

        // 恢复自动（ResetToAuto）之后再来一次写回：此时无手动值，显示跟自动走
        s1.ManualOffset = null; s1.Status = s1.AutoStatus;
        s1.ApplyAutoResult(auto, SeamStatus.NeedsReview, SeamCause.OverlapTooShort, "重叠太短", 0.5, 20, new List<MatchCandidate>());
        Battery.AssertEqual(SeamStatus.NeedsReview, s1.Status, "清掉手动后显示状态恢复跟随自动结论");
        Battery.Evidence("ApplyAutoResult 三段对照：手动未确认/手动已确认/纯自动 各得其所");
    }

    private static void VerticalTight()
    {
        var a = Fixture.OpaqueLike(40, 100);
        var b = Fixture.OpaqueLike(40, 60);
        (Project p, _, _) = TestProject.Create(StitchMode.Vertical, a, b);

        Battery.AssertEqual(new IntSize(40, 160), p.Layout.CanvasSize, "两段紧接的画布尺寸");
        Battery.AssertEqual(new IntPoint(0, 100), p.Layout.Placed[1].Origin, "第二段落点");
    }

    private static void HorizontalTight()
    {
        var a = Fixture.OpaqueLike(70, 40);
        var b = Fixture.OpaqueLike(50, 40);
        (Project p, _, _) = TestProject.Create(StitchMode.Horizontal, a, b);

        Battery.AssertEqual(new IntSize(120, 40), p.Layout.CanvasSize, "横拼画布尺寸");
        Battery.AssertEqual(new IntPoint(70, 0), p.Layout.Placed[1].Origin, "第二段落点");
    }

    private static void OnePixelOffsetExact()
    {
        var a = Fixture.OpaqueLike(40, 100);
        var b = Fixture.OpaqueLike(40, 60);
        (Project p, _, _) = TestProject.Create(StitchMode.LongScroll, a, b);
        Seam seam = p.Doc.Seams[0];

        p.Mutate("test", _ => seam.ManualOffset = new IntPoint(0, 100 - 40));
        int y40 = p.Layout.Placed[1].Origin.Y;
        Battery.AssertEqual(60, y40, "重叠 40 行时第二段落点");
        Battery.AssertEqual(new IntSize(40, 120), p.Layout.CanvasSize, "总高 = 100 + 60 - 40");

        p.Mutate("test", _ => seam.ManualOffset = new IntPoint(0, 100 - 41));
        Battery.AssertEqual(y40 - 1, p.Layout.Placed[1].Origin.Y, "多重叠 1 行后落点");
        Battery.AssertEqual(new IntSize(40, 119), p.Layout.CanvasSize, "总高应恰好少 1");
        Battery.Evidence("从 40 行重叠改到 41 行：落点与总高都恰好变化 1 像素，无浮点介入");
    }

    /// <summary>
    /// 切分的正确定义不是"看起来分开了"，而是<b>可见像素一个都没变</b>。
    /// 这条如果破了，用户会遇到"一切分接缝就错位"，而且很难想到原因。
    /// </summary>
    private static void SplitKeepsPixelsIdentical()
    {
        var a = Fixture.OpaqueLike(40, 100);
        var b = Fixture.OpaqueLike(40, 60);
        (Project p, MemoryResolver r, _) = TestProject.Create(StitchMode.LongScroll, a, b);
        var composer = new CanvasComposer(r);

        byte[] before = composer.RenderWhole(p.Layout).RegionSha256(new IntRect(0, 0, 40, 160));
        Battery.Assert(p.SplitSegmentAtCanvas(37), "在画布 y=37 处切分第一段应当成功");

        var after = composer.RenderWhole(p.Layout);
        Battery.AssertEqual(new IntSize(40, 160), p.Layout.CanvasSize, "切分后总尺寸不变");
        Battery.AssertEqual(3, p.Layout.Placed.Count, "切分后应有 3 段");
        Battery.AssertBytesEqual(before, after.RegionSha256(after.Bounds), "切分前后整张画布");
        Battery.Evidence("切成 37 + 63 两半后逐字节相同；段数 2 → 3、拼缝 1 → 2");
    }

    private static void ReorderKeepsAdjacentSeam()
    {
        var a = Fixture.OpaqueLike(30, 50);
        var b = Fixture.OpaqueLike(30, 50);
        var c = Fixture.OpaqueLike(30, 50);
        (Project p, _, _) = TestProject.Create(StitchMode.LongScroll, a, b, c);

        string segB = p.Doc.Segments[1].Id, segC = p.Doc.Segments[2].Id;
        p.Mutate("手调", _ => p.Doc.FindSeam(segB, segC)!.ManualOffset = new IntPoint(0, 20));

        p.MoveSegment(0, 2);   // a b c → b c a：B→C 仍然相邻，参数必须留着
        Seam? stillThere = p.Doc.FindSeam(segB, segC);
        Battery.Assert(stillThere is not null, "B→C 仍相邻，拼缝不该被重建");
        Battery.AssertEqual(new IntPoint(0, 20), stillThere!.ManualOffset!.Value, "手动偏移应保留");
        Battery.Evidence("重排后受影响的缝被重建，未受影响的缝原样保留（需求 §4.1）");
    }

    private static void MissingSourceIsSafe()
    {
        var a = Fixture.OpaqueLike(30, 50);
        var b = Fixture.OpaqueLike(30, 50);
        (Project p, MemoryResolver r, SourceImage[] srcs) = TestProject.Create(StitchMode.Vertical, a, b);

        // 模拟"项目文件里的路径指向已经不存在的文件"
        p.Doc.Sources.RemoveAt(1);
        var layout = Core.Layout.CanvasLayout.Build(p.Doc);

        Battery.AssertEqual(2, layout.Placed.Count, "缺失段仍占位，序号与拼缝链保持完整");
        Battery.Assert(layout.Placed[1].IsEmpty, "缺失段的尺寸应为 0");
        Battery.AssertEqual("(源文件丢失)", layout.Placed[1].Source.FileName, "应显示为丢失而不是旧尺寸");
        Battery.AssertEqual(new IntSize(30, 50), layout.CanvasSize, "总尺寸只由还在的那一段决定");
        _ = r;
    }

    private static void OverCropIsSafe()
    {
        var a = Fixture.OpaqueLike(30, 50);
        (Project p, _, _) = TestProject.Create(StitchMode.Vertical, a);
        string segId = p.Doc.Segments[0].Id;

        // 上下各裁 40，图只有 50 高 → 有效区域为空
        p.Mutate("过度裁边", _ => p.Doc.FindSegment(segId)!.EdgeCrop = new Insets(0, 40, 0, 40));
        Battery.Assert(p.Layout.Placed[0].IsEmpty, "裁过头应得到空段而不是负尺寸");
        Battery.AssertEqual(0, p.Layout.CanvasSize.Height, "空段不贡献高度");
    }

    private static void CrossAxisCentering()
    {
        var wide = Fixture.OpaqueLike(40, 30);
        var narrow = Fixture.OpaqueLike(20, 30);
        (Project p, _, _) = TestProject.Create(StitchMode.Vertical, wide, narrow);
        p.Mutate("居中", _ => p.Doc.Spacing.Align = CrossAlign.Center);

        // 画布宽 = max(40,20) = 40；窄段应居中到 x = (40-20)/2 = 10
        Battery.AssertEqual(new IntSize(40, 60), p.Layout.CanvasSize, "画布宽取最宽段");
        Battery.AssertEqual(10, p.Layout.Placed[1].Origin.X, "窄段居中的横坐标");
        Battery.Evidence("整数居中：(40-20)/2 = 10，偏向左上规则固定可预期");
    }

    private static void UndoRestoresOffset()
    {
        var a = Fixture.OpaqueLike(30, 50);
        var b = Fixture.OpaqueLike(30, 50);
        (Project p, _, _) = TestProject.Create(StitchMode.LongScroll, a, b);
        string seamId = p.Doc.Seams[0].Id;

        int y0 = p.Layout.Placed[1].Origin.Y;
        p.Mutate("手调", _ => p.Doc.FindSeam(p.Doc.Segments[0].Id, p.Doc.Segments[1].Id)!
            .ManualOffset = new IntPoint(0, 30));
        Battery.AssertEqual(30, p.Layout.Placed[1].Origin.Y, "改动后落点");

        p.Undo();
        Battery.AssertEqual(y0, p.Layout.Placed[1].Origin.Y, "撤销后应回到原落点");
        Battery.Assert(p.Doc.FindSeam(p.Doc.Segments[0].Id, p.Doc.Segments[1].Id)!.ManualOffset is null,
            "撤销后手动偏移记录应消失");
        Battery.Assert(p.IsDirty, "撤销本身也算一次改动，脏标记不该被清掉");
        _ = seamId;
    }
}
