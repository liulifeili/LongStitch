using LongStitch.App.UI;
using LongStitch.Core;
using LongStitch.Core.Export;
using LongStitch.Core.Geometry;
using LongStitch.Core.Imaging;
using LongStitch.Core.Imaging.Png;
using LongStitch.Core.Model;
using LongStitch.Core.Preview;

namespace LongStitch.Checks;

/// <summary>
/// 画布交互契约电池（第八轮）。
///
/// ═══ 这组格子守的是什么 ═══
/// 用户报的现场是"整个画布拖不动"。根因是一行看起来无害的便利写法：
/// <c>if (SelectedSeamId is not null) → 进入拖动拼缝</c>。
/// 它把"当前选中了哪条缝"当成了"这次按下点在哪"——这两件事根本不是一回事：
/// 前者是状态栏与右侧面板要显示的状态，后者才决定这一次手势该平移画布还是该调拼缝。
/// 于是用户只要点过一次拼缝，之后按在画布任何位置都在拖那条缝，画布再也推不动。
///
/// 所以这里断言的都是**每条手势的可观察后果**：视口动没动、参数动没动、修订号动没动、
/// 屏幕上究竟是哪一段在动。判定全部走产品自己的入口
/// （<see cref="StitchCanvasControl.MousePress"/> / <see cref="StitchCanvasControl.MouseMoveTo"/> /
/// <see cref="StitchCanvasControl.MouseRelease"/> / <see cref="StitchCanvasControl.ApplyWheel"/>），
/// 工装不复述优先级——复述出来的绿不算绿。
///
/// 夹具用缩放 1:1、视口原点 (0,0)，于是一道算术题：设备像素数 == 源图像素数，
/// 期望值都是能手算出来的整数，不需要"差不多就行"。
/// </summary>
public static class CanvasInteractionTests
{
    private const int Width = 80, Height = 200, Overlap = 60;

    /// <summary>拼缝线的设备纵坐标（1:1 缩放、视口原点在 0 时等于画布坐标）。前一段占 0..199，后一段从 140 开始。</summary>
    private const int LineY = Height - Overlap;      // 140

    /// <summary>重叠带（用户在截图里圈成"红框"的那一块）= 画布 140..199。</summary>
    private const int BandTop = LineY, BandBottom = Height;

    public static void Register()
    {
        Battery.Add("canvas", "选着拼缝点空白处拖动只平移画布，参数一字不改", SelectedSeamBlankAreaPansOnly);
        Battery.Add("canvas", "Shift+左键即使在重叠带里也只平移", ShiftDragInsideBandPansOnly);
        Battery.Add("canvas", "中键按在蓝线上也只平移", MiddleButtonOnLinePansOnly);
        Battery.Add("canvas", "竖拼点线上下拖动：offsetY 跟着走、半缩放折算不丢、视口不动", VerticalLineDragChangesOffsetYOnly);
        Battery.Add("canvas", "固定前一段：后一段在动，前一段与视口都不动", FixedBeforeMovesAfterSegment);
        Battery.Add("canvas", "固定后一段：前一段跟手，后一段在屏幕上原地不动", FixedAfterMovesBeforeSegment);
        Battery.Add("canvas", "松手才产生一条撤销记录，且一次退回整趟拖动", CommitOnlyOnReleaseOneHistoryEntry);
        Battery.Add("canvas", "拖回原位不留空撤销记录、视口补偿也收回", NoEmptyUndoWhenDraggedBack);
        Battery.Add("canvas", "Ctrl+滚轮只改缩放与视口，锚点像素不漂", CtrlWheelZoomsAtAnchorOnly);
        Battery.Add("canvas", "普通滚轮只平移视口，不碰缩放与参数", WheelPansViewportOnly);
        Battery.Add("canvas", "滚轮与方向键必须同侧：向上滚就是往回看", WheelAndArrowPanSameSide);
        Battery.Add("guard", "滚轮方向两档各挪多少格必须说得出数", WheelPanMagnitudeIsCountable);
        Battery.Add("canvas", "内容装得下就摆正：画面中心与视口中心重合", NarrowContentIsCentredInView);
        Battery.Add("guard", "装不下那一轴居中时必须原地不动", OversizedAxisIsLeftAlone);
        Battery.Add("canvas", "工具条缩放后装得下那一轴自动居中", ToolbarZoomRecentresFittingAxis);
        Battery.Add("canvas", "窗口变化后 Resize 事件拿到的是新尺寸", ResizeEventSeesNewDeviceSize);
        Battery.Add("canvas", "视口里每个内容像素都得被某块瓦片盖住", TileCoverageHasNoHoles);
        Battery.Add("guard", "覆盖判据必须能看见一行高的洞", CoverageYardSeesOneRowHole);
        Battery.Add("canvas", "缩小浏览时内容区里不许出现整行底色", NoBackgroundRowInsideContent);
        Battery.Add("guard", "底色行判据必须能看见自己涂的那一行", BackgroundRowYardSeesItsOwnPaint);
        Battery.Add("canvas", "局部重画那一趟也不许留底色行", PartialRepaintLeavesNoBackgroundRow);
        Battery.Add("guard", "局部重画判据必须真的只画了那一块", PartialRepaintReallyOnlyPaintsTheClip);
        Battery.Add("canvas", "缩小浏览时未选中的缝画状态色实线加一枚状态方框", UnselectedSeamDrawsStatusLineAndBoxWhenZoomedOut);
        Battery.Add("guard", "缩小档缝线颜色必须跟着匹配状态走", ZoomedOutSeamLineColourFollowsStatus);
        Battery.Add("guard", "放大到 100% 未选中的缝必须连状态标签一起画", UnselectedSeamChromeStillDrawnAtFullZoom);
        Battery.Add("canvas", "空白处单击退出编辑而空白处拖动仍然平移", BlankClickExitsEditButBlankDragStillPans);
        Battery.Add("guard", "点缝线必须仍是进编辑而不是退编辑", ClickingTheLineStillEntersEdit);
        Battery.Add("canvas", "点过线条再点空白是选侧别不是退编辑", ClickLineThenBlankClickPicksSide);
        Battery.Add("guard", "带 Shift 的空白单击仍是平移不退编辑", ShiftClickOnBlankKeepsEdit);
        Battery.Add("guard", "任意缩放档内容区不许出现混底灰带", NoBlendedBandInsideContentAtAnyZoom);
        Battery.Add("guard", "最小缩放档欠帧那一帧不许画棋盘格", NoDeferredCheckerWallAtMinZoom);
        Battery.Add("guard", "看得清的缩放档欠帧仍要画棋盘格且只画在内容里", DeferredCheckerStillDrawnInsideContentAtLegibleZoom);
        Battery.Add("guard", "欠帧续画只要没画的那一段且在有限帧内收敛", DeferredTailConvergesWithoutRepaintingDrawnTiles);
        Battery.Add("canvas", "重叠为零的缝按方向键必须出声", ZeroOverlapSeamSpeaksWhenArrowKeyDoesNothing);
        Battery.Add("canvas", "拖完的 offset 一路用到导出文件，与预览逐字节相同", DragResultFlowsIntoExportByteIdentical);
        Battery.Add("canvas", "拼缝线九像素内可点、带里拖缝、带外拖画布", HitRegionCoversLineBandAndOutside);
    }

    // ───────────────────────── 夹具 ─────────────────────────

    private sealed class Rig : IDisposable
    {
        public required StitchCanvasControl Canvas { get; init; }
        public required Project P { get; init; }
        public required MemoryResolver R { get; init; }
        public required CanvasComposer Composer { get; init; }

        public Seam Seam => P.Doc.Seams[0];
        public int Revision => P.Revision;
        public IntPoint TopLeft => Canvas.Viewport.TopLeft;
        public double Zoom => Canvas.Viewport.Zoom;

        /// <summary>1:1 缩放下的线段设备坐标（判"屏幕上谁在动"要看这个，不是看画布坐标）。</summary>
        public Point DeviceOf(int placedIndex) =>
            Canvas.Viewport.CanvasToDevice(P.Layout.Placed[placedIndex].Rect).Location;

        public StitchCanvasControl.SeamHit? HitAt(Point device) => Canvas.HitTestSeamEditRegion(device);

        public void Dispose() => Canvas.Dispose();
    }

    /// <summary>
    /// 建夹具。第九轮之后这批格子分成两族：
    /// · <b>手势归属</b>（平移 vs 编辑）留在长截图里量——那是用户报的缺陷现场；
    /// · <b>位移语义</b>（offsetY、固定侧、视口补偿、拖完进导出）改到<b>竖拼</b>里量——
    ///   长截图的拼缝从"移动图片"变成了"裁切拼缝"，那部分判据由 <c>crop</c> 组接手；
    ///   在这里继续钉着 offsetY 只会得到一格假红，把两件事钉在两个模式里才各量各的。
    /// </summary>
    private static Rig Harness(StitchMode mode = StitchMode.LongScroll)
    {
        var a = Fixture.OpaqueLike(Width, Height, seed: 5);
        var b = Fixture.OpaqueLike(Width, Height, seed: 6);
        (Project p, MemoryResolver r, _) = TestProject.Create(mode, a, b);
        var composer = new CanvasComposer(r);

        // 给这条缝一份"分析出来的"重叠，画布上才真的存在重叠带（红框），命中测试才有东西可量
        p.Mutate("设定重叠", _ =>
        {
            Seam seam = p.Doc.Seams[0];
            seam.AutoOffset = new IntPoint(0, LineY);
            // 第九轮：重叠必须走 SetOverlap，它同时把两侧裁切量摆成默认档（b=O、t=0）。
            // 直接赋 OverlapLength 会留下 (0,0) 的裁切——裁切档画布就真的不长那么高了。
            seam.SetOverlap(Overlap);
            seam.Analyzed = true;
        });

        var canvas = new StitchCanvasControl(p, composer) { Size = new Size(300, 260) };
        canvas.Viewport.SetOneToOne(new Point(0, 0));
        return new Rig { Canvas = canvas, P = p, R = r, Composer = composer };
    }

    /// <summary>
    /// 多段的长条夹具（覆盖／底色那一族专用）。
    /// <b>为什么不能沿用上面那个两段夹具</b>：两段的内容只有 80×340，纵向<b>不足一块瓦片</b>（512 行），
    /// 于是"上下相邻两块瓦片之间会不会漏出一行"这件事在那套夹具里压根没被走到——判据绿着也不说明任何事，
    /// 那正是"建了不等于走了"。这里 12 段 ⇒ 内容 80×1740 ⇒ 一屏里同时压着三四块瓦片，接缝才真的在量。
    /// <b>宽度为什么也能换</b>：状态色那格要数"方框之外的线像素"，而方框固定 24 设备像素宽——
    /// 默认 80 像素宽的内容缩到 30% 只有 24 像素，整条线正好被方框盖满，那一格就没东西可数了。
    /// 真实截图宽上千像素，缩小档下线远宽于方框，所以那格把夹具放宽到接近真机的形状。
    /// </summary>
    private static Rig TallRig(int segments = 12, int width = Width)
    {
        var images = new RgbaImage[segments];
        for (int i = 0; i < segments; i++) images[i] = Fixture.OpaqueLike(width, Height, seed: 200 + i);
        (Project p, MemoryResolver r, _) = TestProject.Create(StitchMode.LongScroll, images);
        var composer = new CanvasComposer(r);
        p.Mutate("给每条缝设定重叠", _ =>
        {
            foreach (Seam seam in p.Doc.Seams)
            {
                seam.AutoOffset = new IntPoint(0, LineY);
                seam.SetOverlap(Overlap);
                seam.Analyzed = true;
            }
        });
        var canvas = new StitchCanvasControl(p, composer) { Size = new Size(300, 260) };
        canvas.Viewport.SetOneToOne(new Point(0, 0));
        return new Rig { Canvas = canvas, P = p, R = r, Composer = composer };
    }

    /// <summary>一次完整手势：按下 → 两段移动（越过"单击 vs 拖动"的阈值）→ 松手。</summary>
    private static void Drag(Rig rig, Point from, Point to, Keys mods = Keys.None,
        MouseButtons button = MouseButtons.Left)
    {
        rig.Canvas.MousePress(button, mods, from);
        rig.Canvas.MouseMoveTo(new Point((from.X + to.X) / 2, (from.Y + to.Y) / 2));
        rig.Canvas.MouseMoveTo(to);
        rig.Canvas.MouseRelease(to);
    }

    // ───────────────────────── ① 平移归平移（画布必须能拖动） ─────────────────────────

    private static void SelectedSeamBlankAreaPansOnly() => UiSmokeTests.RunOnStaThread(() =>
    {
        using Rig rig = Harness();
        rig.Canvas.SelectSeam(rig.Seam.Id);       // 复现缺陷现场：已经进入拼缝编辑状态
        int revision = rig.Revision;
        Battery.Assert(rig.HitAt(new Point(40, 40)) is null, "y=40 在重叠带与蓝线之外，夹具本身要站得住");

        Drag(rig, new Point(40, 40), new Point(90, 100));

        // 两小段各 (25,30) 设备像素，1:1 缩放 ⇒ 视口原点左移 50、上移 60
        Battery.AssertEqual(new IntPoint(-50, -60), rig.TopLeft, "画布必须跟着鼠标走");
        Battery.AssertEqual(revision, rig.Revision, "平移一个字节的参数都不许改");
        Battery.Assert(rig.Seam.ManualOffset is null, "平移不许留下手动偏移");
        // 第九轮：长截图里"平移"这一族还必须完全不碰裁切账（需求七.7 的手势侧对照）
        Battery.AssertEqual(Overlap, rig.Seam.OverlapLength, "平移不许改动总重叠");
        Battery.AssertEqual(0, rig.Seam.CutPosition, "平移不许改动切点");
        Battery.AssertEqual(Overlap, rig.Seam.BeforeCropBottom, "平移不许改动两侧裁切量");
        Battery.Assert(!rig.Seam.IsManualCut, "平移不许把切点标成手动");
        Battery.AssertEqual(MoveConstraint.Both, rig.Seam.Constraint, "平移不许改移动约束");
        Battery.Assert(rig.Canvas.LastSeamEditTraceForTest is null, "没命中编辑区就不该开拼缝编辑的流水账");
        Battery.Evidence("选中拼缝后点空白拖动：TopLeft (0,0)→(-50,-60)，ManualOffset 仍为 null，Revision 不变");
    });

    private static void ShiftDragInsideBandPansOnly() => UiSmokeTests.RunOnStaThread(() =>
    {
        using Rig rig = Harness();
        var inBand = new Point(40, 160);
        Battery.Assert(rig.HitAt(inBand) is { Kind: StitchCanvasControl.SeamHitKind.OverlapBand },
            "y=160 必须落在重叠带里，否则这条正向对照没有意义");
        int revision = rig.Revision;

        Drag(rig, inBand, new Point(40, 200), Keys.Shift);

        Battery.AssertEqual(new IntPoint(0, -40), rig.TopLeft, "Shift+左键在带里也只平移画布（想平移就有一个 escape hatch）");
        Battery.AssertEqual(revision, rig.Revision, "Shift 平移不许改参数");
        Battery.Assert(rig.Seam.ManualOffset is null, "Shift 平移不许留下手动偏移");
        Battery.AssertEqual(0, rig.Seam.CutPosition, "Shift 平移不许改动切点");
        Battery.Evidence("重叠带内 Shift+左键：TopLeft→(0,-40)，参数未动");
    });

    private static void MiddleButtonOnLinePansOnly() => UiSmokeTests.RunOnStaThread(() =>
    {
        using Rig rig = Harness();
        var onLine = new Point(40, LineY);
        Battery.Assert(rig.HitAt(onLine) is { Kind: StitchCanvasControl.SeamHitKind.CutLine },
            "y=140 必须命中蓝线，否则中键那条不构成对照");
        int revision = rig.Revision;

        Drag(rig, onLine, new Point(40, 120), button: MouseButtons.Middle);

        Battery.AssertEqual(new IntPoint(0, 20), rig.TopLeft, "中键按在拼缝线上也应只平移画布");
        Battery.AssertEqual(revision, rig.Revision, "中键不许改参数");
        Battery.Assert(rig.Seam.ManualOffset is null, "中键不许留下手动偏移");
        Battery.AssertEqual(0, rig.Seam.CutPosition, "中键按在蓝线上也不许改动切点");
        Battery.Evidence("中键在蓝线上：TopLeft→(0,20)，参数未动");
    });

    // ───────────────────────── ② 拼缝编辑归拼缝编辑 ─────────────────────────

    /// <summary>
    /// 竖拼里点蓝线上下拖动：改的是<b>位移</b>（这是位移档的本职），
    /// 而且设备像素要按缩放折算回源像素。
    ///
    /// 第九轮的口径分档：同样这个手势在<b>长截图</b>里改的是<b>裁切</b>、不写 ManualOffset，
    /// 那条路由 <c>crop/压着蓝线滑动下方那张图：蓝线行号不变、只一条撤销</c> 负责；
    /// 这一格继续钉 offsetY 只会得到一格假红，所以把它挪到真正按位移落位的竖拼里。
    /// 需求五要的"三种概念分家"就是这样量的：这一格是"图片位置偏移"，
    /// <c>crop/点普通画布只平移视口：裁切参数与导出字节一字不改</c> 是"视口平移"，
    /// <c>crop/压着蓝线滑动下方那张图：蓝线行号不变、只一条撤销</c> 是"长截图裁切"。
    /// </summary>
    private static void VerticalLineDragChangesOffsetYOnly() => UiSmokeTests.RunOnStaThread(() =>
    {
        using Rig rig = Harness(StitchMode.Vertical);
        int revision = rig.Revision;
        IntPoint topLeftBefore = rig.TopLeft;
        double zoomBefore = rig.Zoom;
        int cutBefore = rig.Seam.CutPosition;

        Drag(rig, new Point(40, LineY), new Point(40, LineY + 20));

        Battery.Assert(rig.Seam.ManualOffset is not null, "竖拼里点线上下拖动应改这条拼缝的位移");
        Battery.AssertEqual(160, rig.Seam.ManualOffset!.Value.Y, "源图位移 = 起点 140 + 手指 20");
        Battery.AssertEqual(0, rig.Seam.ManualOffset.Value.X, "这一趟手指没横着动，X 应仍是 0");
        Battery.AssertEqual(topLeftBefore, rig.TopLeft, "拖拼缝不许顺手把画布平移走");
        Battery.AssertEqual(zoomBefore, rig.Zoom, "拖拼缝不许改缩放");
        Battery.AssertEqual(revision + 1, rig.Revision, "只记一条改动");
        Battery.AssertEqual(160, rig.P.Layout.Placed[1].Origin.Y, "布局用的是同一个 offset");
        Battery.AssertEqual(cutBefore, rig.Seam.CutPosition, "位移档里拖线不该改动裁切切点（两套参数不串）");

        // 上面全程 1:1，量不到"除以缩放比"那一步。换 50% 缩放再拖同样 10 个设备像素：
        // 源图位移必须是 round(10 / 0.5) = 20，即再往下 20 行。
        rig.Canvas.Viewport.SetZoom(0.5);
        int currentLineY = rig.P.Layout.Placed[1].Origin.Y;
        int lineDeviceY = rig.Canvas.Viewport
            .CanvasToDevice(new IntRect(0, currentLineY, 1, 1)).Y;
        int offsetYBefore = rig.P.Layout.EffectiveOffset(rig.Seam).Y;
        Drag(rig, new Point(40, lineDeviceY), new Point(40, lineDeviceY + 10));
        Battery.AssertEqual(offsetYBefore + 20, rig.P.Layout.EffectiveOffset(rig.Seam).Y,
            "50% 缩放下拖 10 屏幕像素必须等于 20 源图像素（sourceDelta = round(deviceDelta / Zoom)）");
        Battery.Evidence($"设备 20px@1.0 → 源图 20；设备 10px@0.5 → 源图 20；" +
                         $"最终 effective=({rig.P.Layout.EffectiveOffset(rig.Seam).X}," +
                         $"{rig.P.Layout.EffectiveOffset(rig.Seam).Y})");
    });

    /// <summary>
    /// 固定侧只有在<b>位移档</b>才有落位意义：长截图里切点移动是高度守恒的，
    /// 两段谁在屏幕上都不会被推走（需求四的两种说法在裁切档不再冲突，见架构说明）。
    /// 所以这一族留在竖拼里量。
    /// </summary>
    private static void FixedBeforeMovesAfterSegment() => UiSmokeTests.RunOnStaThread(() =>
    {
        using Rig rig = Harness(StitchMode.Vertical);
        Battery.AssertEqual(FixedSide.Before, rig.Seam.Fixed, "默认就是固定前一段");
        Battery.Assert(!rig.P.Layout.UsesCropPlacement, "夹具自证：这一格走位移档");
        Point before0 = rig.DeviceOf(0), after0 = rig.DeviceOf(1);

        Drag(rig, new Point(40, LineY), new Point(40, LineY + 20));

        Battery.AssertEqual(before0, rig.DeviceOf(0), "固定前一段时上面那张不动");
        Battery.AssertEqual(after0.Y + 20, rig.DeviceOf(1).Y, "下面那张跟手下移 20");
        Battery.AssertEqual(new IntPoint(0, 0), rig.TopLeft, "视口一动不动（需求一.5）");
        Battery.Assert(rig.Canvas.LastSeamEditTraceForTest!.ViewportChanged == false,
            "固定前一段的流水账里视口不许被改过——日志判据读的就是这一格");
        Battery.Assert(rig.Canvas.LastSeamEditTraceForTest!.CropMode == false,
            "位移档的流水账要标 crop_mode=false，事件名才会是 seam.edit.*");
        Battery.AssertEqual(FixedSide.Before, rig.Canvas.LastSeamEditTraceForTest!.FixedSide,
            "流水账记的固定侧要和参数一致");
        Battery.Evidence($"前段屏幕位置 {before0} 未变，后段 {after0}→{rig.DeviceOf(1)}，TopLeft 仍 (0,0)");
    });

    private static void FixedAfterMovesBeforeSegment() => UiSmokeTests.RunOnStaThread(() =>
    {
        using Rig rig = Harness(StitchMode.Vertical);
        rig.P.Mutate("改为固定后一段", _ => rig.Seam.Fixed = FixedSide.After);
        Point before0 = rig.DeviceOf(0), after0 = rig.DeviceOf(1);
        int revision = rig.Revision;
        int cutBefore = rig.Seam.CutPosition;

        // 同样的"往下拖 20"：固定后一段时手指抓的是上面那张 ⇒ 位移要变小
        Drag(rig, new Point(40, LineY), new Point(40, LineY + 20));

        Battery.AssertEqual(120, rig.Seam.ManualOffset!.Value.Y, "固定后一段：往下拖 20 ⇒ 位移 140−20=120");
        Battery.AssertEqual(0, rig.Seam.ManualOffset.Value.X, "X 仍为 0");
        Battery.AssertEqual(new IntPoint(0, -20), rig.TopLeft, "视口按补偿量平移（纯显示，不写参数）");
        Battery.AssertEqual(new Point(before0.X, before0.Y + 20), rig.DeviceOf(0), "上面那张跟手下移 20");
        Battery.AssertEqual(after0, rig.DeviceOf(1), "下面那张在屏幕上原地不动（这才是\"固定后一段\"）");
        Battery.AssertEqual(revision + 1, rig.Revision, "只记一条改动");
        Battery.AssertEqual(MoveConstraint.Both, rig.Seam.Constraint, "补偿不许顺手改约束");
        Battery.AssertEqual(cutBefore, rig.Seam.CutPosition, "位移档的补偿不许改动裁切切点");
        Battery.Assert(rig.Canvas.LastSeamEditTraceForTest!.ViewportChanged,
            "固定后一段这一族必须把视口补偿记进流水账，好让日志读得出补偿来自哪一侧");
        Battery.Evidence($"前段 {before0}→{rig.DeviceOf(0)}（跟手），后段 {after0}→{rig.DeviceOf(1)}（钉住），" +
                         $"TopLeft→{rig.TopLeft}；与\"固定前一段\"那格互为反向对照");
    });

    // ───────────────────────── ③ 提交时机与历史 ─────────────────────────

    private static void CommitOnlyOnReleaseOneHistoryEntry() => UiSmokeTests.RunOnStaThread(() =>
    {
        using Rig rig = Harness(StitchMode.Vertical);
        int revision = rig.Revision;
        var from = new Point(40, LineY);
        var to = new Point(40, LineY + 20);

        rig.Canvas.MousePress(MouseButtons.Left, Keys.None, from);
        rig.Canvas.MouseMoveTo(new Point(40, LineY + 10));
        rig.Canvas.MouseMoveTo(to);
        Battery.Assert(rig.Seam.ManualOffset is null, "拖动过程中只许用预览布局，不许写参数");
        Battery.AssertEqual(revision, rig.Revision, "没松手就不该有修订号变化");
        Battery.AssertEqual(140, rig.P.Layout.Placed[1].Origin.Y, "项目布局在松手前保持原样");

        rig.Canvas.MouseRelease(to);
        Battery.AssertEqual(revision + 1, rig.Revision, "松手才提交一条");
        Battery.Assert(rig.P.CanUndo, "松手后可撤销");
        Battery.AssertEqual("拖动拼缝", rig.P.NextUndoLabel, "撤销栈顶应当就是这次拖动");

        rig.P.Undo();
        Battery.Assert(rig.Seam.ManualOffset is null, "一次撤销退回整趟拖动（不是退回 1 像素）");
        Battery.AssertEqual(140, rig.P.Layout.Placed[1].Origin.Y, "布局回到起点");
        Battery.Evidence("预览期间零写入；松手 1 条历史；Ctrl+Z 一次整趟退回");
    });

    private static void NoEmptyUndoWhenDraggedBack() => UiSmokeTests.RunOnStaThread(() =>
    {
        // 两侧都要量：固定后一段还要多收一次视口补偿，拖回原位时两者都得干净地归零
        foreach (FixedSide side in new[] { FixedSide.Before, FixedSide.After })
        {
            using Rig rig = Harness(StitchMode.Vertical);
            rig.P.Mutate("设定固定侧", _ => rig.Seam.Fixed = side);
            int revision = rig.Revision;
            string? labelBefore = rig.P.NextUndoLabel;
            var from = new Point(40, LineY);
            var outAndBack = new Point(40, LineY + 20);

            rig.Canvas.MousePress(MouseButtons.Left, Keys.None, from);
            rig.Canvas.MouseMoveTo(outAndBack);          // 拖出去 20
            rig.Canvas.MouseMoveTo(from);                // 又拖回来
            rig.Canvas.MouseRelease(from);

            string tag = side == FixedSide.Before ? "固定前一段" : "固定后一段";
            Battery.Assert(rig.Seam.ManualOffset is null, $"{tag}：净位移为 0 不该写手动值");
            Battery.AssertEqual(revision, rig.Revision, $"{tag}：净位移为 0 不该进修订号");
            Battery.AssertEqual(labelBefore, rig.P.NextUndoLabel, $"{tag}：不该留下一条空撤销记录");
            Battery.AssertEqual(new IntPoint(0, 0), rig.TopLeft, $"{tag}：视口补偿必须跟着收回起点");
            Battery.Evidence($"{tag}：出去 20 再回来 ⇒ Revision {revision} 未变、TopLeft 归 (0,0)");
        }
    });

    // ───────────────────────── ④ 滚轮与缩放 ─────────────────────────

    private static void CtrlWheelZoomsAtAnchorOnly() => UiSmokeTests.RunOnStaThread(() =>
    {
        using Rig rig = Harness();
        int revision = rig.Revision;
        var anchor = new Point(20, 20);
        IntPoint anchorCanvasBefore = rig.Canvas.Viewport.DeviceToCanvas(anchor);

        rig.Canvas.ApplyWheel(120, Keys.Control, anchor);

        Battery.Assert(Math.Abs(rig.Zoom - 1.15) < 1e-9, $"Ctrl+滚轮应放大一档，实际 {rig.Zoom}");
        Battery.AssertEqual(new IntPoint(3, 3), rig.TopLeft, "以指针为中心：新原点要按锚点反解");
        Battery.AssertEqual(anchorCanvasBefore, rig.Canvas.Viewport.DeviceToCanvas(anchor),
            "指针下那个画布点在缩放前后必须还是同一个点（否则画面每缩放一次就漂一下）");
        Battery.AssertEqual(revision, rig.Revision, "缩放不许改参数");
        Battery.Assert(rig.Seam.ManualOffset is null && rig.Seam.Constraint == MoveConstraint.Both,
            "缩放不许留下拼缝改动");
        Battery.AssertEqual(0, rig.Seam.CutPosition, "缩放不许改动切点");
        Battery.Evidence($"zoom 1.00→{rig.Zoom:F2}，TopLeft→{rig.TopLeft}，锚点画布坐标仍 {anchorCanvasBefore}");
    });

    private static void WheelPansViewportOnly() => UiSmokeTests.RunOnStaThread(() =>
    {
        using Rig rig = Harness();
        int revision = rig.Revision;
        double zoomBefore = rig.Zoom;

        rig.Canvas.ApplyWheel(-120, Keys.None, new Point(20, 20));   // 往下滚 = 往下看

        Battery.AssertEqual(zoomBefore, rig.Zoom, "普通滚轮不许改缩放");
        // 期望值从 (0,-60) 翻成 (0,+60)：这不是"为了让判据变绿而改期望值"——用户明确说这个方向本身是错的
        // （"鼠标滚轮向下，画面现在却是向上"）。旧期望钉住的正是那个错方向，改完必须连反证一起钉，
        // 见下面 <c>canvas/滚轮与方向键必须同侧：向上滚就是往回看</c> 与 <c>guard/滚轮方向两档各挪多少格必须说得出数</c>。
        Battery.AssertEqual(new IntPoint(0, 60), rig.TopLeft, "向下滚一格：视口原点要往大走（往下看），一格 60 屏幕像素");
        Battery.AssertEqual(revision, rig.Revision, "普通滚轮不许改参数");
        Battery.Assert(rig.Seam.ManualOffset is null, "普通滚轮不许留下手动偏移");
        Battery.AssertEqual(0, rig.Seam.CutPosition, "普通滚轮不许改动切点");
        Battery.Evidence("滚轮 -120：TopLeft→(0,+60)，缩放与参数不变");
    });

    /// <summary>
    /// 滚轮的<b>方向</b>（用户报的第一、二条）。这一格量的是"两路手势必须同侧"，不是"某个具体数"：
    /// 滚轮向上（delta 为正）与按 ↑ 都必须把 <c>TopLeft.Y</c> 推向<b>变小</b>那一侧（往回看），滚轮向下与之相反。
    /// 为什么拿方向键当对照而不是写死一个符号：方向键那一路（<see cref="StitchCanvasControl.PanByArrow"/>）
    /// 用户从没抱怨过，它是现场里唯一已经对的参照物。把滚轮钉成"和它一致"，
    /// 下一轮谁再把滚轮的负号抄回来，这一格必红；把滚轮做成常量 0（手势根本不使力）也必红，
    /// 因为"同侧"要求两个符号都是非零且相等。补量的绝对格数在 <c>guard/滚轮方向两档各挪多少格必须说得出数</c>。
    /// </summary>
    private static void WheelAndArrowPanSameSide() => UiSmokeTests.RunOnStaThread(() =>
    {
        using Rig rig = Harness();
        int revision = rig.Revision;

        int y0 = rig.TopLeft.Y;
        rig.Canvas.ApplyWheel(120, Keys.None, new Point(20, 20));      // 向上滚一格
        int wheelUp = rig.TopLeft.Y - y0;
        rig.Canvas.ApplyWheel(-120, Keys.None, new Point(20, 20));     // 倒回去，起点复原
        int wheelDown = rig.TopLeft.Y - (y0 + wheelUp);

        bool arrowConsumed = rig.Canvas.PanByArrow(Keys.Up, shift: false);   // 按 ↑
        // ↑ 的起点是"两轮滚轮之后"的那个位置（滚轮那两档互为反数，本该已经复原到 y0）。
        // 基准抄错就会量成"↑ 与滚轮上滚差 60"——这格第一次跑红就是这个原因，不是方向错。
        int afterWheels = y0 + wheelUp + wheelDown;
        int arrowUp = rig.TopLeft.Y - afterWheels;

        Battery.Assert(arrowConsumed, "夹具自证：↑ 这一路必须真的使力（关了浏览平移的话它就不是对照物）");
        Battery.Assert(wheelUp != 0 && arrowUp != 0 && wheelDown != 0,
            $"夹具自证：三路都得真的把视口挪开，读到 0 等于这格在拿不动当一致（滚轮上 {wheelUp}／下 {wheelDown}／↑ {arrowUp}）");
        Battery.Assert(Math.Sign(wheelUp) == Math.Sign(arrowUp),
            $"滚轮向上与按 ↑ 必须同侧（都是往回看）：实际 {wheelUp} vs {arrowUp}");
        Battery.Assert(Math.Sign(wheelDown) == -Math.Sign(wheelUp),
            $"滚轮向下必须与向上相反、把画面推向更下面的内容：实际 {wheelDown} vs {wheelUp}");
        Battery.Assert(wheelDown > 0, $"向下滚一格 TopLeft.Y 必须变大（用户原话：向下滚画面却往上）：实际 {wheelDown}");
        Battery.AssertEqual(revision, rig.Revision, "滚轮平移不许顺手改参数账（修订号）");
        Battery.Evidence($"向上滚 {wheelUp}、向下滚 {wheelDown}、按 ↑ {arrowUp} ⇒ 滚轮上滚与 ↑ 同侧、下滚反侧");
    });

    /// <summary>
    /// <c>canvas/滚轮与方向键必须同侧：向上滚就是往回看</c> 的反证，也是它的前提门。
    /// 同侧判据只比符号，所以它有个能喂绿的做法：让滚轮一格挪一点点（符号对、手感没有），
    /// 或者反过来让两档挪同一侧但幅度都非零——那种写法符号判据照样绿。
    /// 这一格把<b>绝对幅度</b>钉死：一格 = 60 屏幕像素、两格 = 120，且两档必须互为相反数
    /// （把负号抄回来时这里读到的是一对同号，当场红）。
    /// </summary>
    private static void WheelPanMagnitudeIsCountable() => UiSmokeTests.RunOnStaThread(() =>
    {
        using Rig rig = Harness();

        int y0 = rig.TopLeft.Y;
        rig.Canvas.ApplyWheel(-120, Keys.None, new Point(20, 20));   // 向下滚一格
        int one = rig.TopLeft.Y - y0;
        rig.Canvas.ApplyWheel(-240, Keys.None, new Point(20, 20));   // 向下滚两格
        int two = rig.TopLeft.Y - (y0 + one);
        rig.Canvas.ApplyWheel(360, Keys.None, new Point(20, 20));    // 向上滚三格 = 前两档的总和倒回去
        int back = rig.TopLeft.Y - (y0 + one + two);

        Battery.AssertEqual(60, one, "一格滚轮必须正好挪 60 屏幕像素（WheelStepPixels 的口径）");
        Battery.AssertEqual(120, two, "两格必须翻倍：读到 60 就是符号/格数没跟着 delta 走");
        Battery.AssertEqual(-180, back, "向上滚三格必须与向下三格幅度相同、方向相反（同号即抄回负号，判据当场红）");
        Battery.AssertEqual(y0, rig.TopLeft.Y, "来回滚完视口要回到起点——这格量的是纯方向与幅度，不留残留");
        Battery.Evidence($"下 60／下 120／上 −180：幅度跟着格数走，两档严格反号");
    });

    /// <summary>
    /// 用户报的第七条里<b>算式</b>那一半：「画布中的图片无论窗口多大都得保持居中显示，你每次都放在左对齐」。
    /// 夹具是 1:1 缩放下的 80×340 内容摆进 300×420 的视口 ⇒ 两轴都装得下，期望值全部能手算：
    /// 视口原点 = (内容一半 − 视口一半) = ((80−300)/2, (340−420)/2) = (−110, −40)，
    /// 于是内容设备矩形的中心正好落在 (150,210) = 视口正中。
    /// 为什么判据钉"中心重合"而不是钉那个负数：负原点只是实现顺手写的数，
    /// 用户看的是"画面在不在中间"——中心重合这条在高 DPI／非整数缩放下同样成立。
    /// 装不下的那一轴（长截图的纵向）不归这里管，见 <c>guard/装不下那一轴居中时必须原地不动</c>。
    /// </summary>
    private static void NarrowContentIsCentredInView() => UiSmokeTests.RunOnStaThread(() =>
    {
        using Rig rig = Harness();
        rig.Canvas.Size = new Size(300, 420);      // 比内容（80×340）既宽又高 ⇒ 两轴都装得下
        IntSize content = rig.P.Layout.CanvasSize;
        Battery.AssertEqual((Width, Height * 2 - Overlap), (content.Width, content.Height),
            "夹具自证：两段图的画布尺寸就是 80×340（下面全部期望值都从它算）");
        Battery.AssertEqual(new Size(300, 420), rig.Canvas.Viewport.DeviceSize,
            "夹具自证：视口设备尺寸必须跟着控件尺寸走，否则量的是另一个框");
        Battery.AssertEqual(IntPoint.Zero, rig.TopLeft,
            "夹具自证：摆正之前视口贴着左上角——这正是用户抱怨的那个位置");

        rig.Canvas.FollowContentCentre();

        var rect = rig.Canvas.Viewport.CanvasToDevice(new IntRect(0, 0, content.Width, content.Height));
        Battery.Assert(rect.Left > 0, $"横向不许再贴着左边缘：内容左缘的设备 x 应大于 0，实际 {rect.Left}");
        Battery.AssertEqual(new Point(150, 210),
            new Point(rect.Left + rect.Width / 2, rect.Top + rect.Height / 2),
            "内容中心必须与视口中心重合（300×420 的正中是 150,210）");
        Battery.AssertEqual(new IntPoint((Width - 300) / 2, (Height * 2 - Overlap - 420) / 2), rig.TopLeft,
            "视口原点=两侧留白相等的那个数（负数就是留白，产品没有夹取，允许为负）");
        Battery.Evidence($"80×340 摆进 300×420：TopLeft={rig.TopLeft}，内容设备矩形={rect}");
    });

    /// <summary>
    /// 上一格的前提门：<b>装不下的那一轴必须不动</b>。
    /// 如果实现是"两轴一律居中"，长截图（纵向几十万行）每次导入或每次改窗口都会把人从
    /// 正在看的那一行拽到中间去——那不是居中，是丢工作。
    /// 这一格拿 340&gt;260 的现场量：纵向先挪到第 1000 行，摆正之后 1000 必须还在，
    /// 而横向（80&lt;300 装得下）必须被摆回正中。两轴各判各的，才能同时抓住"整条都不动"和"整条都居中"两种错写。
    /// </summary>
    private static void OversizedAxisIsLeftAlone() => UiSmokeTests.RunOnStaThread(() =>
    {
        using Rig rig = Harness();
        rig.Canvas.Size = new Size(300, 260);      // 340 的内容装不进 260 高的视口
        int revision = rig.Revision;
        rig.Canvas.Viewport.MoveTopLeftTo(new IntPoint(0, 1000));   // 用户正看着第 1000 行

        rig.Canvas.FollowContentCentre();

        Battery.AssertEqual(1000, rig.TopLeft.Y,
            $"装不下的那一轴必须原地不动（把人从第 1000 行拽走就是丢工作），实际 Y={rig.TopLeft.Y}");
        Battery.Assert(rig.TopLeft.X < 0,
            $"装得下的横向必须被摆回正中（负原点=两侧留白相等），实际 X={rig.TopLeft.X}");
        Battery.AssertEqual(revision, rig.Revision, "摆正只动视口，一个字节的项目参数都不许碰");
        Battery.Evidence($"300×260 视口里的 80×340 内容：X 摆到 {rig.TopLeft.X}（居中）·Y 保持 {rig.TopLeft.Y}（不动）");
    });

    /// <summary>
    /// 第五十二改（用户图「中间的画布预览……在放大后无法自动居中」）：工具条「＋」与「100%」
    /// 走的是 <see cref="StitchCanvasControl.ZoomStep"/>／<see cref="StitchCanvasControl.ZoomActualSize"/>——
    /// 改完缩放必须把<b>装得下</b>的那一轴摆回正中（长截图横向永远装得下 ⇒ 那条窄内容放大缩小后都停在水平正中）。
    ///
    /// ═══ 为什么另开入口而不塞进 Ctrl+滚轮 ═══
    /// Ctrl+滚轮的契约是「指针下的源像素缩放前后不动」（<c>canvas/Ctrl+滚轮只改缩放与视口，锚点像素不漂</c>），
    /// 居中会把它推翻。工具条缩放是「重新取景」，两件事各走各的入口，这一格只钉工具条这条。
    ///
    /// ═══ 两轴各判各的 ═══
    /// 夹具 80×340 内容摆进 300×260 视口：横向装得下（80×1.25=100≤300）⇒ 必须居中（原点为负=两侧留白相等）；
    /// 纵向装不下（340×1.25=425&gt;260）⇒ 不许把用户正看着的那一行拽走。纵向这条用「视口中心对应的画布行
    /// 缩放前后不变」来量（SetZoom 本就以视口中心为锚，RecentreIfFits 只许动装得下的轴），
    /// 与 <c>guard/装不下那一轴居中时必须原地不动</c> 同一口径，但量的是工具条缩放这条真通路。
    /// </summary>
    private static void ToolbarZoomRecentresFittingAxis() => UiSmokeTests.RunOnStaThread(() =>
    {
        using Rig rig = Harness();
        rig.Canvas.Size = new Size(300, 260);      // 80×340 内容：横向装得下、纵向装不下
        IntSize content = rig.P.Layout.CanvasSize;
        int revision = rig.Revision;
        var anchor = new Point(150, 130);          // 视口正中（SetZoom/SetOneToOne 的锚点）
        rig.Canvas.Viewport.MoveTopLeftTo(new IntPoint(50, 1000));   // 用户正看着第 1000 行，横向也偏着
        int rowBefore = rig.Canvas.Viewport.DeviceToCanvas(anchor).Y;

        // 工具条「＋」：放大一档
        rig.Canvas.ZoomStep(1.25);
        Battery.Assert(Math.Abs(rig.Zoom - 1.25) < 1e-9, $"「＋」应放大到 1.25，实际 {rig.Zoom}");
        IntPoint centre = rig.Canvas.Viewport.CentreOriginFor(content);
        Battery.Assert(centre.X < 0,
            $"夹具自证：1.25 档横向仍装得下（80×1.25=100≤300），居中原点应是负数，实际 {centre.X}");
        Battery.AssertEqual(centre.X, rig.TopLeft.X,
            $"放大后装得下的横向必须自动居中（用户：放大后无法自动居中），实际 X={rig.TopLeft.X}，应 {centre.X}");
        int xAfterStep = rig.TopLeft.X;
        Battery.AssertEqual(rowBefore, rig.Canvas.Viewport.DeviceToCanvas(anchor).Y,
            "装不下的纵向：视口中心对应的画布行不许被居中拽走（RecentreIfFits 只动装得下的轴）");
        Battery.AssertEqual(revision, rig.Revision, "工具条缩放是纯显示量，不许改参数");

        // 工具条「100%」：回到实际像素，横向仍要居中、纵向仍不许拽走
        rig.Canvas.ZoomActualSize();
        Battery.Assert(Math.Abs(rig.Zoom - 1.0) < 1e-9, $"「100%」应回到 1.0，实际 {rig.Zoom}");
        Battery.AssertEqual(rig.Canvas.Viewport.CentreOriginFor(content).X, rig.TopLeft.X,
            "回到 100% 后装得下的横向仍要自动居中");
        int xAfterActual = rig.TopLeft.X;
        Battery.AssertEqual(rowBefore, rig.Canvas.Viewport.DeviceToCanvas(anchor).Y,
            "回到 100% 也不许把纵向拽走（用户原来看的那一行还在）");
        Battery.AssertEqual(revision, rig.Revision, "全程不许改参数");
        Battery.Assert(rig.Seam.ManualOffset is null && rig.Seam.CutPosition == 0,
            "工具条缩放不许留下任何拼缝改动");
        Battery.Evidence($"ZoomStep(1.25)：X→{xAfterStep}（居中）；ZoomActualSize：X→{xAfterActual}（居中）；" +
                         $"纵向锚点行 {rowBefore} 全程不变");
    });

    /// <summary>
    /// 第五十三改（用户：「缩小的时候分析完毕是居中显示，一旦最大化窗口，就不会自动回到居中位置」）：
    /// 控件尺寸变化时，<c>Resize</c> 事件那一侧（主窗口挂的 <c>FollowContentCentre</c>，
    /// 与 MainForm 同一条接线）读到的<b>必须已是新</b>设备尺寸。旧实现 OnResize 里先 base.OnResize
    /// （同步发事件）再 SetDeviceSize——居中拿旧尺寸算，最大化之后内容停在缩小那一档的居中值上。
    /// 牙：夹具把视口先挪偏（X=30），居中必须真发生；300→500 宽变化下新旧尺寸算出的居中 X
    /// 差 100px，先发事件后更新尺寸的实现会红出这 100px。纵向装不下，用户正看着的行不许被拽走。
    /// </summary>
    private static void ResizeEventSeesNewDeviceSize() => UiSmokeTests.RunOnStaThread(() =>
    {
        using Rig rig = Harness();
        rig.Canvas.Size = new Size(300, 260);      // 80×340 内容：横向装得下、纵向装不下
        IntSize content = rig.P.Layout.CanvasSize;
        int revision = rig.Revision;
        rig.Canvas.Viewport.MoveTopLeftTo(new IntPoint(30, 900));   // 先偏着，居中必须真发生

        // 与 MainForm._canvas.Resize 同一条接线：尺寸一变就把装得下的轴摆回居中
        rig.Canvas.Resize += (_, _) => rig.Canvas.FollowContentCentre();

        rig.Canvas.Size = new Size(500, 260);      // 模拟窗口拉大／最大化
        IntPoint want = rig.Canvas.Viewport.CentreOriginFor(content);
        Battery.Assert(want.X < 0,
            $"夹具自证：500 宽下横向仍装得下（80≤500），居中原点应是负数，实际 {want.X}");
        Battery.AssertEqual(want.X, rig.TopLeft.X,
            $"拉大后装得下的横向必须按<b>新</b>尺寸居中：应 {want.X}，实际 {rig.TopLeft.X}" +
            "——若 OnResize 先发事件再更新 DeviceSize，居中会用旧尺寸 300 算出少 100px 的值");
        Battery.AssertEqual(900, rig.TopLeft.Y,
            "纵向装不下（340>260）：用户正看着的行不许被居中拽走");
        Battery.AssertEqual(revision, rig.Revision, "窗口尺寸变化是纯显示量，不许改参数");
        Battery.Evidence($"300→500 宽：TopLeft.X={rig.TopLeft.X}（新尺寸居中值 {want.X}）；TopLeft.Y 保持 {rig.TopLeft.Y}");
    });

    /// <summary>
    /// 用户报的第四条：「渲染的时候，滑快了会出现渲染灰色的线条」。
    /// 那种线在代码里的形状只有一种可能：<b>某个设备像素没被任何一块瓦片盖住</b>，露出画布底色（灰色）。
    /// 所以这一格不比毫秒、也不猜是瓦片取整还是限流欠帧，它直接问产品"这一帧你把哪些块贴上去了"
    /// （<c>CaptureTileRectsForTest</c>），然后逐像素核对"视口 ∩ 内容"那块区域有没有洞。
    /// 判据能红的形状：任何一处取整让相邻两块之间漏出哪怕一行（就是用户看到的那条线）；
    /// 反证那一格（<c>guard/覆盖判据必须能看见一行高的洞</c>）证明这把尺看得见一行的洞。
    /// </summary>
    private static void TileCoverageHasNoHoles() => UiSmokeTests.RunOnStaThread(() =>
    {
        double[] zooms = { 0.10, 0.30, 0.35, 0.50, 1.00, 2.00 };
        int[] offsets = { 0, 1, 7, 33, 100, 409 };   // 视口原点的几个不同位置（取整最容易在"不对齐"时出错）
        var worst = new List<string>();
        int totalHoles = 0;

        foreach (double zoom in zooms)
        {
            foreach (int off in offsets)
            {
                using Rig rig = TallRig();
                rig.Canvas.Size = new Size(300, 260);
                rig.Canvas.TileBudgetMsForTest = 0;              // 不限流：欠帧那几块是棋盘格，不算"内容盖住了"
                rig.Canvas.CaptureTileRectsForTest = true;
                rig.Canvas.Viewport.MoveTopLeftTo(new IntPoint(0, off));
                rig.Canvas.Viewport.ZoomAt(zoom, new Point(0, 0));

                using var bmp = new Bitmap(300, 260);
                rig.Canvas.RenderFrameForTest(bmp);

                Rectangle area = ContentDeviceArea(rig);
                if (area.Width <= 0 || area.Height <= 0) continue;
                List<(int X, int Y)> holes = Uncovered(rig.Canvas.LastFrameTileRectsForTest, area);
                totalHoles += holes.Count;
                if (holes.Count > 0)
                    worst.Add($"zoom={zoom:F2} 原点Y={off} 洞={holes.Count} 首处=({holes[0].X},{holes[0].Y})");
            }
        }

        Battery.AssertEqual(0, totalHoles,
            "视口里的内容像素必须逐个被某一块瓦片盖住，否则屏上就是那条灰线：" +
            string.Join(" | ", worst.Take(6)));
        Battery.Evidence($"{zooms.Length} 档缩放 × {offsets.Length} 个原点：0 个未盖住的设备像素");
    });

    /// <summary>"视口 ∩ 内容"在设备像素里的那一块（判覆盖的区域）。</summary>
    private static Rectangle ContentDeviceArea(Rig rig)
    {
        IntSize content = rig.P.Layout.CanvasSize;
        IntRect visible = rig.Canvas.Viewport.VisibleCanvas();
        IntRect contentRect = new IntRect(0, 0, content.Width, content.Height);
        IntRect inside = contentRect.Intersect(visible);
        if (inside.IsEmpty) return Rectangle.Empty;
        Rectangle device = rig.Canvas.Viewport.CanvasToDevice(inside);
        Rectangle client = new Rectangle(0, 0, rig.Canvas.ClientSize.Width, rig.Canvas.ClientSize.Height);
        device.Intersect(client);
        return device;
    }

    /// <summary>逐像素核对：把这一块里没有任何矩形盖住的点列出来（最多返回全部，数量由调用方断言）。</summary>
    private static List<(int X, int Y)> Uncovered(IReadOnlyList<Rectangle> rects, Rectangle area)
    {
        var holes = new List<(int, int)>();
        for (int y = area.Top; y < area.Bottom; y++)
            for (int x = area.Left; x < area.Right; x++)
            {
                bool covered = false;
                foreach (Rectangle r in rects)
                    if (r.Contains(x, y)) { covered = true; break; }
                if (!covered) holes.Add((x, y));
            }
        return holes;
    }

    /// <summary>
    /// 用户报的第四条（灰线）真正能被量的那一头：<b>屏上有没有一整行是画布底色</b>。
    /// 上一格量的是"产品打算贴哪些矩形"，矩形算得再全，GDI 真画出来的像素仍可能少一行
    /// （半像素偏移 + 非整数缩放 + 双线性，就是这一族 artifact 的经典形状），
    /// 于是相邻两块瓦片之间露出一行底色——用户在 30% 下快滑时看到的就是它。
    /// 量法：产品真帧（<see cref="StitchCanvasControl.RenderFrameForTest"/>）逐行比底色，
    /// 只在"视口 ∩ 内容"那块区域里数——区域外本来就该是底色。
    /// 尺的活性由 <c>guard/底色行判据必须能看见自己涂的那一行</c> 保证。
    /// </summary>
    private static void NoBackgroundRowInsideContent() => UiSmokeTests.RunOnStaThread(() =>
    {
        double[] zooms = { 0.25, 0.30, 0.35, 0.50, 0.75 };
        int[] offsets = { 0, 1, 7, 33, 100, 409, 900 };
        var found = new List<string>();

        foreach (double zoom in zooms)
        {
            foreach (int off in offsets)
            {
                using Rig rig = TallRig();
                rig.Canvas.Size = new Size(300, 260);
                rig.Canvas.TileBudgetMsForTest = 0;
                rig.Canvas.Viewport.MoveTopLeftTo(new IntPoint(0, off));
                rig.Canvas.Viewport.ZoomAt(zoom, new Point(0, 0));
                using var bmp = new Bitmap(300, 260);
                rig.Canvas.RenderFrameForTest(bmp);

                Rectangle area = ContentDeviceArea(rig);
                if (area.Width <= 0 || area.Height <= 0) continue;
                List<int> rows = BackgroundRows(bmp, area);
                foreach (int y in rows) found.Add($"zoom={zoom:F2} 原点Y={off} 行 y={y}");
            }
        }

        Battery.AssertEqual(0, found.Count,
            "内容区域里不许出现整行画布底色（那就是用户圈出来的那条灰线）：" +
            string.Join(" | ", found.Take(8)));
        Battery.Evidence($"{zooms.Length} 档缩放 × {offsets.Length} 个原点：内容区里 0 行是底色");
    });

    /// <summary>把某一块里"整行都是画布底色"的行号列出来（底色由产品自己的常量给，不在判据里另写一个数）。</summary>
    private static List<int> BackgroundRows(Bitmap bmp, Rectangle area)
    {
        Color bg = Theme.CanvasBackground;
        var rows = new List<int>();
        for (int y = area.Top; y < area.Bottom; y++)
        {
            bool all = true;
            for (int x = area.Left; x < area.Right; x++)
            {
                Color c = bmp.GetPixel(x, y);
                if (c.R != bg.R || c.G != bg.G || c.B != bg.B) { all = false; break; }
            }
            if (all) rows.Add(y);
        }
        return rows;
    }

    /// <summary>
    /// 上一格的前提门：这把"数底色行"的尺必须看得见一行。
    /// 做法是自己往产品那一帧上涂一行底色再交给同一个函数——它必须正好数出那一行，
    /// 否则上一格那个 0 就可能是"尺压根没在数"（区域算空、底色常量写错都会得到 0）。
    /// </summary>
    private static void BackgroundRowYardSeesItsOwnPaint() => UiSmokeTests.RunOnStaThread(() =>
    {
        using Rig rig = TallRig();
        rig.Canvas.Size = new Size(300, 260);
        rig.Canvas.TileBudgetMsForTest = 0;
        rig.Canvas.Viewport.ZoomAt(0.30, new Point(0, 0));
        using var bmp = new Bitmap(300, 260);
        rig.Canvas.RenderFrameForTest(bmp);

        Rectangle area = ContentDeviceArea(rig);
        Battery.Assert(area.Width > 4 && area.Height > 4, $"夹具自证：这一块得有尺寸，尺才有东西可量（实际 {area}）");
        Battery.AssertEqual(0, BackgroundRows(bmp, area).Count,
            "同一帧先量一次：产品帧不该有底色行（这一句与上一格同形状，这里只做前提）");

        int painted = area.Top + area.Height / 2;
        using (var g = System.Drawing.Graphics.FromImage(bmp))
            g.FillRectangle(new SolidBrush(Theme.CanvasBackground), area.Left, painted, area.Width, 1);

        List<int> rows = BackgroundRows(bmp, area);
        Battery.AssertEqual(1, rows.Count, $"自己涂的那一行必须正好数出来（实际数到 {rows.Count} 行）");
        Battery.AssertEqual(painted, rows[0], $"数出来的必须是涂的那一行 y={painted}，实际 {rows[0]}");
        Battery.Evidence($"涂 y={painted} 一行底色 ⇒ 尺数到 {rows.Count} 行、行号一致 ⇒ 上一格那个 0 是真 0");
    });

    /// <summary>
    /// 同一条灰线，换成<b>真窗口那个形状</b>再量一遍：滚动时系统发下来的更新区常常只是客户区的一块，
    /// 产品按 <c>VisibleClipBounds</c> 反算要贴哪几块瓦片。上一格量的是整块裁剪，走不到这条分支。
    /// 量法照真窗口的顺序来：先把上一帧画满（缓冲区里存着旧内容），再滚到别处，
    /// 然后<b>只重画中间那一条</b>；扫的是"裁剪区 ∩ 内容"里有没有整行底色。
    /// 这条分支真被走过没有，由 <c>guard/局部重画判据必须真的只画了那一块</c> 单独证明。
    /// </summary>
    private static void PartialRepaintLeavesNoBackgroundRow() => UiSmokeTests.RunOnStaThread(() =>
    {
        double[] zooms = { 0.25, 0.30, 0.35, 0.50 };
        int[] offsets = { 0, 33, 100, 409 };
        var found = new List<string>();

        foreach (double zoom in zooms)
        {
            foreach (int off in offsets)
            {
                using Rig rig = TallRig();
                rig.Canvas.Size = new Size(300, 260);
                rig.Canvas.TileBudgetMsForTest = 0;
                rig.Canvas.Viewport.ZoomAt(zoom, new Point(0, 0));
                using var bmp = new Bitmap(300, 260);
                rig.Canvas.RenderFrameForTest(bmp);                      // 缓冲区里先有"上一帧"
                var clip = new Rectangle(0, 90, 300, 80);                // 系统只要求重画中间这一块
                rig.Canvas.Viewport.MoveTopLeftTo(new IntPoint(0, off)); // 滚到别处
                rig.Canvas.RenderFrameForTest(bmp, clip);

                Rectangle area = ContentDeviceArea(rig);
                area.Intersect(clip);
                if (area.Width <= 0 || area.Height <= 0) continue;
                foreach (int y in BackgroundRows(bmp, area))
                    found.Add($"zoom={zoom:F2} 原点Y={off} 行 y={y}");
            }
        }

        Battery.AssertEqual(0, found.Count,
            "只重画一块的时候，那一块里的内容行同样不许露底色（真窗口滚动就是这条分支）：" +
            string.Join(" | ", found.Take(8)));
        Battery.Evidence($"{zooms.Length} 档缩放 × {offsets.Length} 个原点，每趟只重画中间 80 行：0 行底色");
    });

    /// <summary>
    /// 上一格的前提门：必须证明"这一趟真的只画了那一块"。
    /// 若产品忽略裁剪区、每帧都整块重画，上一格照样绿（整块那条路本来就没错），
    /// 于是它量的就不是用户现场那条分支。这里两头都钉：
    /// 裁剪区<b>外</b>的像素必须与上一帧一字不差（证明没被重画），
    /// 裁剪区<b>内</b>必须真的变了（证明这一块确实重画过，不是空跑）。
    /// </summary>
    private static void PartialRepaintReallyOnlyPaintsTheClip() => UiSmokeTests.RunOnStaThread(() =>
    {
        using Rig rig = TallRig();
        rig.Canvas.Size = new Size(300, 260);
        rig.Canvas.TileBudgetMsForTest = 0;
        rig.Canvas.Viewport.ZoomAt(0.30, new Point(0, 0));

        using var before = new Bitmap(300, 260);
        rig.Canvas.RenderFrameForTest(before);
        using var after = new Bitmap(300, 260);
        using (var g = System.Drawing.Graphics.FromImage(after))
            g.DrawImage(before, 0, 0);                                   // 缓冲区里先摆上一帧

        var clip = new Rectangle(0, 90, 300, 80);
        rig.Canvas.Viewport.MoveTopLeftTo(new IntPoint(0, 400));         // 滚动：内容整条往上挪
        rig.Canvas.RenderFrameForTest(after, clip);

        int outsideChanged = 0, insideChanged = 0;
        for (int y = 0; y < 260; y++)
            for (int x = 0; x < 300; x++)
            {
                bool same = before.GetPixel(x, y).ToArgb() == after.GetPixel(x, y).ToArgb();
                bool inClip = clip.Contains(x, y);
                if (inClip && !same) insideChanged++;
                if (!inClip && !same) outsideChanged++;
            }

        Battery.AssertEqual(0, outsideChanged,
            $"裁剪区之外的像素必须与上一帧完全相同（实际有 {outsideChanged} 个变了＝产品忽略了裁剪区，整块重画）");
        Battery.Assert(insideChanged > 100,
            $"裁剪区之内必须真的重画过（实际只有 {insideChanged} 个像素变了＝这一趟是空跑，上一格量的不是这条分支）");
        Battery.Evidence($"裁剪区 {clip}：区内 {insideChanged} 个像素变了、区外 {outsideChanged} 个（必须为 0）");
    });

    /// <summary>
    /// 用户第四十五改的口径：缝线颜色＝匹配结论，线正中一枚方框＝结论的图章
    ///（成功蓝线＋勾、有问题橙线＋警告、还没结论灰线＋空框）。
    /// 这一格在 30% 档量四笔账：① 每条未选中缝<b>恰好一笔</b>状态色线；② 每条缝<b>恰好一枚</b>状态方框
    ///（方框就是状态图章，收掉它就回到"看不见哪条有问题"）；③ 带文字的状态标签盒子在缩小档照旧收掉
    ///（那才是 100 条缝时的一片灰块），收的笔数必须等于缝数；④ 缝本身还得点得中（收标签不等于删功能）。
    /// 颜色对不对由 <c>guard/缩小档缝线颜色必须跟着匹配状态走</c> 逐像素钉；
    /// 标签不是被删了由 <c>guard/放大到 100% 未选中的缝必须连状态标签一起画</c> 钉。
    /// </summary>
    private static void UnselectedSeamDrawsStatusLineAndBoxWhenZoomedOut() => UiSmokeTests.RunOnStaThread(() =>
    {
        using Rig rig = TallRig(6);                 // 6 段 ⇒ 5 条缝，内容 80×900
        rig.Canvas.Size = new Size(300, 260);
        rig.Canvas.Viewport.ZoomAt(0.30, new Point(0, 0));
        rig.Canvas.Viewport.MoveTopLeftTo(IntPoint.Zero);
        Battery.Assert(rig.Canvas.SelectedSeamId is null, "夹具自证：这一趟一条缝都没选中，量的才是未选中那一族");
        int seams = rig.P.Doc.Seams.Count;

        int drawn0 = StitchCanvasControl.SeamHairlinesDrawnForTest;
        int box0 = StitchCanvasControl.SeamStatusBoxDrawnForTest;
        int skipped0 = StitchCanvasControl.SeamChromeZoomSkippedForTest;
        using var bmp = new Bitmap(300, 260);
        rig.Canvas.RenderFrameForTest(bmp);
        int drawn = StitchCanvasControl.SeamHairlinesDrawnForTest - drawn0;
        int boxes = StitchCanvasControl.SeamStatusBoxDrawnForTest - box0;
        int skipped = StitchCanvasControl.SeamChromeZoomSkippedForTest - skipped0;

        Battery.AssertEqual(seams, drawn,
            $"30% 档下每条未选中缝必须恰好画一笔状态色线，实际 {drawn} 笔 / {seams} 条缝");
        Battery.AssertEqual(seams, boxes,
            $"30% 档下每条缝必须恰好一枚状态方框（用户第四十五改要的图章），实际 {boxes} 枚 / {seams} 条缝");
        Battery.AssertEqual(seams, skipped,
            $"30% 档下带文字的状态标签盒子必须一条不漏地收掉（那就是灰块一族），实际收 {skipped} / {seams} 条");

        int hits = 0;
        for (int y = 0; y < 260; y++)
            if (rig.HitAt(new Point(40, y)) is not null) hits++;
        Battery.Assert(hits > 0,
            $"标签收掉了，缝本身必须还点得中：逐行扫下来命中 {hits} 个设备行（0 就是把找缝这件事一起删了）");
        Battery.Evidence($"30% 档：状态线 {drawn} 笔、状态方框 {boxes} 枚（各=缝数 {seams}）、收掉文字标签 {skipped} 条，" +
                         $"命中区仍有 {hits} 个设备行可点");
    });

    /// <summary>
    /// 上一格的像素级前提门："画了线、画了框"有个能喂绿的做法——把所有缝都画成同一种颜色，
    /// 于是"哪条有问题"在屏上根本分不出来。所以这里把两条缝分别布置成成功／需检查，逐像素扫这一帧：
    /// 必须同时扫得到<b>纯不透明</b>的强调色与警告色像素（不混底 ⇒ 不是灰线）。
    /// 数的时候<b>把每条缝的状态方框挖掉</b>：方框与线同色，只数"这一帧有没有这个颜色"的话，
    /// 把线改成别的颜色、方框照旧画，判据照样绿——用户要的是"橙色的<b>线条</b>"，那就只数线自己的像素。
    /// 同一趟再量一头反向对照：把全部缝改回"还没结论"，这两种颜色必须一枚都不剩——
    /// 证明扫到的像素真的来自状态色，而不是界面别处碰巧同色。
    /// </summary>
    private static void ZoomedOutSeamLineColourFollowsStatus() => UiSmokeTests.RunOnStaThread(() =>
    {
        using Rig rig = TallRig(6, 400);      // 放宽：30% 档下线要宽于 24 像素的方框，挖掉方框后才有东西可数
        rig.Canvas.Size = new Size(300, 260);
        rig.Canvas.Viewport.ZoomAt(0.30, new Point(0, 0));
        rig.Canvas.Viewport.MoveTopLeftTo(IntPoint.Zero);
        Battery.Assert(rig.Canvas.SelectedSeamId is null, "夹具自证：无选中缝，状态色像素才只可能来自缝线自己");
        rig.P.Mutate("布置两种状态", d =>
        {
            d.Seams[0].Status = SeamStatus.AutoPassed;
            d.Seams[1].Status = SeamStatus.NeedsReview;
        });

        (int accent, int warn) = CountStatusPixels(rig);
        Battery.Assert(accent > 0,
            $"成功那条缝的<b>线</b>必须是纯不透明强调色（方框之外数到 {accent} 枚）——0 枚就是线的颜色没跟着状态走");
        Battery.Assert(warn > 0,
            $"需检查那条缝的<b>线</b>必须是纯不透明警告色（方框之外数到 {warn} 枚）——0 枚就是只有方框在报状态、线还是老样子");

        rig.P.Mutate("全部改回没结论", d =>
        {
            foreach (Seam s in d.Seams) s.Status = SeamStatus.Pending;
        });
        (int accent2, int warn2) = CountStatusPixels(rig);
        Battery.AssertEqual(0, accent2 + warn2,
            $"全部缝都没结论时这两种状态色必须一枚都不剩（实际 {accent2}+{warn2}）——" +
            "剩了就说明扫到的像素不是状态色，上面那两个 >0 是假绿");
        Battery.Evidence($"成功+需检查各一条 ⇒ 强调色 {accent} 枚、警告色 {warn} 枚；全改回 Pending ⇒ {accent2}+{warn2} 枚");
    });

    /// <summary>
    /// 扫一帧 30% 画面，数<b>方框之外</b>纯不透明的强调色与警告色像素各几枚（线自己的像素级出口）。
    /// 方框矩形走产品自己的 <see cref="StitchCanvasControl.AdjustHandleDeviceRect"/>，
    /// 判据不把 24×18 这两个常量抄一份——抄了的话产品改尺寸判据照旧绿，那是"接线不对、只断言名字"。
    /// </summary>
    private static (int Accent, int Warn) CountStatusPixels(Rig rig)
    {
        using var bmp = new Bitmap(300, 260);
        rig.Canvas.RenderFrameForTest(bmp);
        int accent = Theme.Accent.ToArgb();
        int warn = Theme.Warning.ToArgb();
        List<Rectangle> boxes = rig.P.Doc.Seams
            .Select(s => rig.Canvas.AdjustHandleDeviceRect(s.Id))
            .Where(r => !r.IsEmpty)
            .ToList();
        int a = 0, w = 0;
        for (int y = 0; y < 260; y++)
            for (int x = 0; x < 300; x++)
            {
                bool inBox = false;
                foreach (Rectangle b in boxes)
                    if (b.Contains(x, y)) { inBox = true; break; }
                if (inBox) continue;
                int px = bmp.GetPixel(x, y).ToArgb();
                if (px == accent) a++;
                else if (px == warn) w++;
            }
        return (a, w);
    }

    /// <summary>
    /// 上一格的前提门：缩小档那几笔账有个能喂绿的做法——把文字标签<b>永远</b>不画
    /// （顺手也把 100% 档那句"为什么需检查"删了）。所以这里换同一份夹具到 1.0 档再量一次：
    /// 线、方框照旧各一笔一枚，而且文字标签必须真画出来、"因缩放而收"的账必须为 0。
    /// 两格合起来才是"按档位收放"，而不是"一律不画"。
    /// </summary>
    private static void UnselectedSeamChromeStillDrawnAtFullZoom() => UiSmokeTests.RunOnStaThread(() =>
    {
        using Rig rig = TallRig(6);
        rig.Canvas.Size = new Size(300, 260);
        rig.Canvas.Viewport.ZoomAt(1.0, new Point(0, 0));
        rig.Canvas.Viewport.MoveTopLeftTo(IntPoint.Zero);
        Battery.Assert(rig.Canvas.SelectedSeamId is null, "夹具自证：这一趟同样不选中任何缝");
        int seams = rig.P.Doc.Seams.Count;
        rig.P.Mutate("布置成需检查", d =>
        {
            foreach (Seam s in d.Seams) s.Status = SeamStatus.NeedsReview;   // 需检查才有文字标签可量
        });

        int drawn0 = StitchCanvasControl.SeamHairlinesDrawnForTest;
        int box0 = StitchCanvasControl.SeamStatusBoxDrawnForTest;
        int label0 = StitchCanvasControl.SeamStatusLabelDrawnForTest;
        int skipped0 = StitchCanvasControl.SeamChromeZoomSkippedForTest;
        using var bmp = new Bitmap(300, 260);
        rig.Canvas.RenderFrameForTest(bmp);
        int drawn = StitchCanvasControl.SeamHairlinesDrawnForTest - drawn0;
        int boxes = StitchCanvasControl.SeamStatusBoxDrawnForTest - box0;
        int labels = StitchCanvasControl.SeamStatusLabelDrawnForTest - label0;
        int skipped = StitchCanvasControl.SeamChromeZoomSkippedForTest - skipped0;

        Battery.AssertEqual(seams, drawn, $"100% 档下未选中的缝必须照旧画线（实际 {drawn} 笔）");
        // 方框与标签都带"屏外早退"（第七改）：100% 档一屏只装得下一条缝线，
        // 所以方框的账要对的是<b>落在屏内的那几条</b>，不是全部缝——全对就会把早退这门好机制判成缺陷。
        int visible = 0;
        for (int i = 1; i < rig.P.Layout.Placed.Count; i++)
            if (rig.DeviceOf(i).Y is >= 0 and < 260) visible++;
        Battery.Assert(visible >= 1, $"夹具自证：100% 档屏内至少要有一条缝线可量（实际 {visible} 条）");
        Battery.AssertEqual(visible, boxes, $"100% 档屏内那 {visible} 条缝的状态方框必须一枚不少（实际 {boxes} 枚）");
        Battery.Assert(labels > 0,
            $"100% 档下文字状态标签必须真画出来（实际 {labels} 个）——0 个就说明缩小档那格是被「永不画标签」喂绿的");
        Battery.AssertEqual(0, skipped, $"100% 档不该有因缩放而收标签的账（实际 {skipped} 条）");
        Battery.Evidence($"100% 档：线 {drawn} 笔、方框 {boxes} 枚、文字标签 {labels} 个、因缩放收 {skipped} 条 ⇒ 收放真的按档位走");
    });

    /// <summary>
    /// 用户第四十五改第二条："点击了线条但是没有做任何操作，那么我点击画布的空白处应该能退出编辑，
    /// 而不是只能按 esc。" 这一格量手势的两头：
    /// ① 选中后在空白处<b>单击</b>（按下松手同一点）⇒ 退出编辑（选中清空、微调档关掉）；
    /// ② 选中后在空白处<b>拖动</b> ⇒ 仍然只平移、选中不许被踢掉（平移中途松手把人踢回浏览态是另一个缺陷）。
    /// "点缝线仍是进编辑"由 <c>guard/点缝线必须仍是进编辑而不是退编辑</c> 单独钉——
    /// 否则"单击一律退编辑"这种实现也能把①喂绿。
    /// </summary>
    private static void BlankClickExitsEditButBlankDragStillPans() => UiSmokeTests.RunOnStaThread(() =>
    {
        using Rig rig = Harness();
        rig.Canvas.SelectSeam(rig.Seam.Id);
        Battery.Assert(rig.HitAt(new Point(40, 40)) is null, "夹具自证：(40,40) 是空白处，不在带里也不在线上");

        // ① 单击空白：按下→松手同一点
        rig.Canvas.MousePress(MouseButtons.Left, Keys.None, new Point(40, 40));
        rig.Canvas.MouseRelease(new Point(40, 40));
        Battery.Assert(rig.Canvas.SelectedSeamId is null,
            "空白处单击必须退出编辑（用户第四十五改：而不是只能按 Esc）");

        // ② 拖动空白：仍只平移，选中不许丢
        rig.Canvas.SelectSeam(rig.Seam.Id);
        IntPoint before = rig.TopLeft;
        Drag(rig, new Point(40, 40), new Point(90, 100));
        Battery.AssertEqual(rig.Seam.Id, rig.Canvas.SelectedSeamId ?? "",
            "空白处拖动是平移手势，中途松手不许把用户正在编辑的缝踢掉");
        Battery.Assert(before != rig.TopLeft, $"夹具自证：这一趟必须真的平移了（{before} → {rig.TopLeft}）");
        Battery.Evidence($"空白单击 ⇒ 选中清空；空白拖动 ⇒ 视口 {before} → {rig.TopLeft} 且选中保留");
    });

    /// <summary>
    /// 上一格的前提门："空白单击退编辑"有个能喂绿的做法——把<b>任何</b>单击都做成退编辑，
    /// 于是点缝线也退编辑，选缝这条路就断了。所以这里从浏览态单击缝线：必须进编辑而不是退编辑。
    /// </summary>
    private static void ClickingTheLineStillEntersEdit() => UiSmokeTests.RunOnStaThread(() =>
    {
        using Rig rig = Harness();
        Battery.Assert(rig.Canvas.SelectedSeamId is null, "夹具自证：从浏览态起步");
        Point onLine = FindLinePoint(rig);
        rig.Canvas.MousePress(MouseButtons.Left, Keys.None, onLine);
        rig.Canvas.MouseRelease(onLine);
        Battery.AssertEqual(rig.Seam.Id, rig.Canvas.SelectedSeamId ?? "",
            $"单击缝线（{onLine}）必须进编辑——若被做成退编辑，选缝这条路就断了");
        Battery.Evidence($"浏览态单击缝线 {onLine} ⇒ 选中 {rig.Canvas.SelectedSeamId}");
    });

    /// <summary>沿 x=40 逐行找第一个命中"蓝线"的设备点（与命中测试同一出处，不另算几何）。</summary>
    private static Point FindLinePoint(Rig rig)
    {
        for (int y = 0; y < 260; y++)
            if (rig.HitAt(new Point(40, y)) is { Kind: StitchCanvasControl.SeamHitKind.CutLine })
                return new Point(40, y);
        throw new InvalidOperationException("夹具里找不到蓝线上的命中点，夹具自证失败");
    }

    /// <summary>
    /// 用户第四十五改第 2 条<b>当场裁定的口径</b>（两个行为撞在同一下单击上，由用户选）：
    /// 点蓝线那一下会顺手进「微调模式」，而微调模式下点带外线外<b>一律</b>算"点选上方／下方那张图"。
    /// 用户选<b>保留选侧</b>——退编辑走 Esc、✓（再点一次线上那枚方框）或取消选中；
    /// 只有"选着缝但没进微调档"那一档（从左侧列表点选、或 Esc 退档之后），空白单击才退编辑，
    /// 那一档由 <c>canvas/空白处单击退出编辑而空白处拖动仍然平移</c> 钉住。
    /// 所以这里量的是用户原话那条流程实际走到哪儿：点线条 ⇒ 进编辑且微调档开着；
    /// 再点空白 ⇒ 选中与微调档都<b>不许</b>丢，而且那一下真把侧别选上了（读数跟着变，不是白点）。
    /// </summary>
    private static void ClickLineThenBlankClickPicksSide() => UiSmokeTests.RunOnStaThread(() =>
    {
        using Rig rig = Harness();
        Battery.Assert(rig.Canvas.SelectedSeamId is null, "夹具自证：从浏览态起步");
        Battery.Assert(rig.HitAt(new Point(40, 40)) is null, "夹具自证：(40,40) 不在线上也不在带里");

        Point onLine = FindLinePoint(rig);
        Battery.Assert(onLine.Y > 40, $"夹具自证：(40,40) 必须在线（Y={onLine.Y}）上方，那一下才该选上 Before");
        rig.Canvas.MousePress(MouseButtons.Left, Keys.None, onLine);
        rig.Canvas.MouseRelease(onLine);
        Battery.AssertEqual(rig.Seam.Id, rig.Canvas.SelectedSeamId ?? "",
            $"夹具自证：点线条（{onLine}）那一下必须先进编辑");
        Battery.Assert(rig.Canvas.AdjustModeActive,
            "夹具自证：点蓝线顺手进了微调档——这正是两个行为撞车的地方");
        Battery.AssertEqual(FixedSide.After.ToString(), (rig.Canvas.AdjustSideForTest ?? FixedSide.Before).ToString(),
            "夹具自证：压着蓝线按下抓的是下方那张（After）");

        rig.Canvas.MousePress(MouseButtons.Left, Keys.None, new Point(40, 40));
        rig.Canvas.MouseRelease(new Point(40, 40));
        Battery.AssertEqual(rig.Seam.Id, rig.Canvas.SelectedSeamId ?? "",
            "用户裁定：微调档下点空白是选侧别，不许把用户正在编辑的缝踢掉");
        Battery.Assert(rig.Canvas.AdjustModeActive, "同上：微调档也不许被这一下顺手关掉");
        Battery.AssertEqual(FixedSide.Before.ToString(), (rig.Canvas.AdjustSideForTest ?? FixedSide.After).ToString(),
            "(40,40) 在线上方 ⇒ 那一下必须把侧别选成 Before——没变就是「点选侧别」这条路被删了");
        Battery.Evidence($"点线 {onLine} ⇒ 编辑＋微调档、侧别 After；点空白 (40,40) ⇒ 侧别 Before，选中与微调档都保留");
    });

    /// <summary>
    /// 退编辑三个条件里"按下时没命中任何缝"那一条的前提门。
    /// 有个能喂绿的做法是把它删掉：<b>Shift+左键</b>那一支在命中测试之前就已经转成平移手势，
    /// 于是"光标下那条缝"还是上一次点线留下的旧值——少了这一条，Shift 按一下空白也会退编辑，
    /// 用户本想平移却把正在编辑的缝丢了。所以这里先点线进编辑（把那个内部值写成非空的旧值），
    /// 再 Shift 按一下空白：选中与微调档都必须原样保留。
    /// 反向对照另起一档——"选着缝但没进微调档"（左侧列表点选就是这一档）空白单击必须照旧退编辑，
    /// 否则这一格会被"把退编辑整条删掉"喂绿。
    /// </summary>
    private static void ShiftClickOnBlankKeepsEdit() => UiSmokeTests.RunOnStaThread(() =>
    {
        using Rig rig = Harness();
        Battery.Assert(rig.HitAt(new Point(40, 40)) is null, "夹具自证：(40,40) 是空白处");
        Point onLine = FindLinePoint(rig);
        rig.Canvas.MousePress(MouseButtons.Left, Keys.None, onLine);
        rig.Canvas.MouseRelease(onLine);
        Battery.AssertEqual(rig.Seam.Id, rig.Canvas.SelectedSeamId ?? "",
            "夹具自证：先点线条进编辑，「光标下那条缝」这才真的是非空的旧值");

        rig.Canvas.MousePress(MouseButtons.Left, Keys.Shift, new Point(40, 40));
        rig.Canvas.MouseRelease(new Point(40, 40));
        Battery.AssertEqual(rig.Seam.Id, rig.Canvas.SelectedSeamId ?? "",
            "Shift+单击空白是平移手势的修饰键，不许顺手把用户正在编辑的缝踢掉");
        Battery.Assert(rig.Canvas.AdjustModeActive, "同上：微调档也不许被 Shift 那一下关掉");

        using Rig browse = Harness();
        browse.Canvas.SelectSeam(browse.Seam.Id);
        Battery.Assert(!browse.Canvas.AdjustModeActive, "夹具自证：这一档只有选中、没进微调档（列表点选就是这个形状）");
        browse.Canvas.MousePress(MouseButtons.Left, Keys.None, new Point(40, 40));
        browse.Canvas.MouseRelease(new Point(40, 40));
        Battery.Assert(browse.Canvas.SelectedSeamId is null,
            "反向对照：没进微调档时空白单击必须照旧退编辑——不退就说明上面那两行是被「退编辑整条删掉」喂绿的");
        Battery.Evidence("微调档下 Shift+空白单击 ⇒ 选中与微调档都保留；浏览档（只选中）空白单击 ⇒ 选中清空");
    });

    /// <summary>
    /// 单色夹具（第四十六改）：只有一段、没有缝，内容是一整块纯色。
    /// 没有缝 ⇒ 屏上不会有任何状态色／标签／方框来干扰"这一枚像素该是什么色"的判定。
    /// 瓦片预算关掉 ⇒ 一帧取完，量的是"取完之后的画面"，不是欠帧途中。
    /// </summary>
    private static Rig SolidRig(int width = 400, int height = 300)
    {
        (Project p, MemoryResolver r, _) = TestProject.Create(StitchMode.LongScroll,
            Fixture.Solid(width, height, new RgbaColor(SolidR, SolidG, SolidB, 255)));
        var composer = new CanvasComposer(r);
        var canvas = new StitchCanvasControl(p, composer) { Size = new Size(300, 260) };
        canvas.Viewport.SetOneToOne(new Point(0, 0));
        canvas.TileBudgetMsForTest = 0;
        return new Rig { Canvas = canvas, P = p, R = r, Composer = composer };
    }

    private const int SolidR = 250, SolidG = 250, SolidB = 250;

    /// <summary>
    /// 用户第四十六改图1圈出的那一横一竖灰线：GDI+ 缩小重采样时往位图<b>边缘外</b>取样，
    /// 边缘外被当成透明黑，于是每块瓦片最外一圈设备像素被混进透明、深色画布底透上来——
    /// 瓦片交界与内容外缘就是一条灰带；混多少取决于缩放的小数相位，所以"只在某些缩放档出现"。
    /// 判据写法：单色内容逐缩放档扫一遍，内容设备矩形里<b>每枚像素</b>都必须等于那一种纯色——
    /// 渗色、漏底、棋盘格、瓦片缝，任何一种都会让某一枚像素不等于它。
    /// </summary>
    private static void NoBlendedBandInsideContentAtAnyZoom() => UiSmokeTests.RunOnStaThread(() =>
    {
        using Rig rig = SolidRig();
        var bad = new List<string>();
        foreach (double zoom in new[] { 0.10, 0.15, 0.20, 0.30, 0.45, 0.60, 0.80, 1.00, 1.50, 2.00 })
        {
            rig.Canvas.Viewport.ZoomAt(zoom, new Point(0, 0));
            rig.Canvas.Viewport.MoveTopLeftTo(IntPoint.Zero);
            using var bmp = new Bitmap(300, 260);
            rig.Canvas.RenderFrameForTest(bmp);
            Rectangle content = rig.Canvas.Viewport.CanvasToDevice(
                new IntRect(0, 0, rig.P.Layout.CanvasSize.Width, rig.P.Layout.CanvasSize.Height));
            content.Intersect(new Rectangle(0, 0, 300, 260));
            int off = 0;
            Color first = Color.Empty;
            for (int y = content.Y; y < content.Bottom; y++)
                for (int x = content.X; x < content.Right; x++)
                {
                    Color c = bmp.GetPixel(x, y);
                    if (c.R != SolidR || c.G != SolidG || c.B != SolidB)
                    {
                        off++;
                        if (first.IsEmpty) first = c;
                    }
                }
            if (off > 0) bad.Add($"{zoom:F2} 档 {off} 枚（首处 {first.R},{first.G},{first.B}）");
        }
        Battery.Assert(bad.Count == 0,
            "内容区里不许出现任何不等于内容纯色的像素（渗色灰带／漏底／棋盘格都算）——实际：" +
            string.Join("；", bad));
        Battery.Evidence("10 个缩放档逐像素扫内容区：每枚像素都等于内容纯色");
    });

    /// <summary>
    /// 用户第四十六改图4：缩到最小整屏花掉。病根是瓦片遍历按"视口反算的可见画布"走，
    /// 10% 档下它比内容宽出一个数量级，内容之外那些瓦片列取出来是全透明的块 ⇒
    /// 不透明判定为假 ⇒ 整列铺棋盘格（棋盘格本该只表示"还没取到"或"内容本身透明"）。
    /// 判据两头量：内容矩形内每枚像素等于纯色；内容矩形外（留 2 像素余量）每枚像素等于画布底色——
    /// 棋盘格两色（46/36）与底色（20,20,22）不同，铺出来了就一定红。
    /// </summary>
    /// <summary>
    /// 用户第四十六改图4："当我缩小到最小的时候，会出现画布的渲染异常。"
    /// 现量到的形状：10% 档一屏要取几百块瓦片（瓦片网格在<b>画布</b>坐标里，缩得越小一屏块数越多），
    /// 预算一限流，欠帧那一段就把<b>整屏</b>画成棋盘格——整屏宽、半屏高的一墙。
    /// 一块瓦片在屏上只有几十设备像素时，8 像素的棋盘方格本身就是噪声，欠帧又只持续不到一秒，
    /// 所以产品在这一档不画这一墙（欠帧留画布底色、块在后续帧补齐）。
    ///
    /// 判据因此必须<b>先造出欠帧</b>再量：把瓦片预算压到几乎为零，一帧只放行一块冷瓦片，
    /// 其余全是欠帧。这一帧上整张图只许出现两种像素——画布底色与内容纯色，一枚棋盘格都不许有；
    /// 并且 <c>TilesDeferredLastFrameForTest</c> 必须 &gt;0、内容里必须真有洞：
    /// 不量这两条，"没画棋盘格"就可能是"根本没欠帧"喂绿的空话。
    /// 随后把帧补完（产品自己会 Invalidate 接着取），补完之后内容矩形内必须全是纯色、外全是底色。
    /// </summary>
    private static void NoDeferredCheckerWallAtMinZoom() => UiSmokeTests.RunOnStaThread(() =>
    {
        using Rig rig = SolidRig(width: 1200, height: 6000);
        rig.Canvas.TileBudgetMsForTest = 0.0001;   // 逼出欠帧：一帧只放行一块冷瓦片
        rig.Canvas.Viewport.ZoomAt(0.10, new Point(0, 0));
        rig.Canvas.Viewport.MoveTopLeftTo(IntPoint.Zero);
        using var bmp = new Bitmap(300, 260);
        rig.Canvas.RenderFrameForTest(bmp);
        Battery.Assert(rig.Canvas.TilesDeferredLastFrameForTest > 0,
            $"夹具自证：这一帧必须真的欠着帧（实际欠 {rig.Canvas.TilesDeferredLastFrameForTest} 块）——" +
            "没欠帧的话「没画棋盘格」就是空话");

        Rectangle content = rig.Canvas.Viewport.CanvasToDevice(
            new IntRect(0, 0, rig.P.Layout.CanvasSize.Width, rig.P.Layout.CanvasSize.Height));
        int checkerish = 0, holes = 0;
        Color first = Color.Empty;
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
            {
                bool inContent = x >= content.X && x < content.Right && y >= content.Y && y < content.Bottom;
                Color c = bmp.GetPixel(x, y);
                bool isBg = c.ToArgb() == Theme.CanvasBackground.ToArgb();
                bool isSolid = c.R == SolidR && c.G == SolidG && c.B == SolidB;
                if (!isBg && !isSolid) { checkerish++; if (first.IsEmpty) first = c; }
                if (inContent && isBg) holes++;
            }
        Battery.AssertEqual(0, checkerish,
            $"最小缩放档欠帧那一帧不许出现棋盘格（实际 {checkerish} 枚既不是底色也不是纯色，首处 {first.R},{first.G},{first.B}）——" +
            "整屏宽的那一墙就是用户图4 报的渲染异常");
        Battery.Assert(holes > 0,
            $"夹具自证：欠帧那一帧内容里必须真有还没取到的洞（实际 {holes} 枚底色）——没有洞就等于没量这一墙");
        int deferredAtFirstFrame = rig.Canvas.TilesDeferredLastFrameForTest;

        // 欠的块数必须正好等于"与内容取交之后"的瓦片数减去第一帧画掉的整行（第五十八改起：
        // 欠帧这一刀落在行边界上，第一帧画掉的是第 0 行整行，见续画守卫那段根因）。
        // 多出来就意味着遍历把内容外的空瓦片也算进去了——用户图4 那一墙的<b>宽度</b>就是它给的：
        // 10% 档视口换算回画布比内容宽十倍，不取交就是整屏宽的棋盘格。
        // 余量传 0：产品那一趟还会再与 GDI 裁剪区取交（屏外预取的那一圈不上屏），
        // 这里量的是"上屏这一趟遍历了几块"，口径要与屏上那一墙同宽。
        IntRect vis = rig.Canvas.Viewport.VisibleCanvas();
        IntRect clamped = vis.Intersect(
            new IntRect(0, 0, rig.P.Layout.CanvasSize.Width, rig.P.Layout.CanvasSize.Height));
        int cols = TileRenderer.TileX(clamped.Right - 1) - TileRenderer.TileX(clamped.X) + 1;
        int rows = TileRenderer.TileY(clamped.Bottom - 1) - TileRenderer.TileY(clamped.Y) + 1;
        Battery.AssertEqual(cols * rows - cols, deferredAtFirstFrame,
            $"欠帧块数必须等于内容内瓦片数减去第一行（内容内 {cols}×{rows} 块，第一帧画第 0 行整行）" +
            "——多出来就是把内容外的空瓦片也算进了遍历");

        SettleDeferred(rig, bmp);
        int inside = 0, outside = 0;
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
            {
                bool inContent = x >= content.X && x < content.Right && y >= content.Y && y < content.Bottom;
                Color c = bmp.GetPixel(x, y);
                if (inContent)
                {
                    if (c.R != SolidR || c.G != SolidG || c.B != SolidB) inside++;
                }
                else if (c.ToArgb() != Theme.CanvasBackground.ToArgb()) outside++;
            }
        Battery.AssertEqual(0, inside, "欠帧补完之后内容矩形内每枚像素必须等于纯色");
        Battery.AssertEqual(0, outside, "欠帧补完之后内容矩形外必须是画布底色");
        Battery.Evidence($"10% 档欠 {deferredAtFirstFrame} 块的那一帧：整张只有底色与纯色两种像素；" +
                         "补完帧后内容内全纯色、外全底色");
    });

    /// <summary>
    /// 上一格的反证：把"欠帧画棋盘格"整条删掉也能喂绿它（那一帧自然一枚棋盘格都没有），
    /// 但看得清的缩放档上棋盘格是"这块还没取到"的唯一诚实标记，删了它用户会把欠帧读成内容丢了。
    /// 所以这一格在 50% 档（一块瓦片在屏上 256 设备像素，远大于那道 64 的阈值）同样逼出欠帧，
    /// 量两件事：欠帧那一帧内容里<b>必须</b>有棋盘格；并且棋盘格一枚都不许铺到内容矩形外
    /// （内容外没有东西可取，铺出去就是用户图4 那一墙的另一种形状——瓦片遍历越出内容矩形）。
    /// 夹具故意做成"窄而高"（300×6000）：50% 档视口换算回画布比内容宽，
    /// 不取交的话欠帧循环就会把内容右边那一列也铺成棋盘格，这一格才会红。
    /// </summary>
    private static void DeferredCheckerStillDrawnInsideContentAtLegibleZoom() => UiSmokeTests.RunOnStaThread(() =>
    {
        using Rig rig = SolidRig(width: 300, height: 6000);
        rig.Canvas.TileBudgetMsForTest = 0.0001;
        rig.Canvas.Viewport.ZoomAt(0.50, new Point(0, 0));
        rig.Canvas.Viewport.MoveTopLeftTo(IntPoint.Zero);
        using var bmp = new Bitmap(300, 260);
        rig.Canvas.RenderFrameForTest(bmp);
        Battery.Assert(rig.Canvas.TilesDeferredLastFrameForTest > 0,
            $"夹具自证：这一帧必须真的欠着帧（实际欠 {rig.Canvas.TilesDeferredLastFrameForTest} 块）");

        Rectangle content = rig.Canvas.Viewport.CanvasToDevice(
            new IntRect(0, 0, rig.P.Layout.CanvasSize.Width, rig.P.Layout.CanvasSize.Height));
        Battery.Assert(content.Right <= bmp.Width && content.Right > 8,
            $"夹具自证：内容右缘必须落在图内且留出内容外的列（内容设备矩形 {content}）");

        int checkerInside = 0, notBgOutside = 0;
        Color firstOut = Color.Empty;
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
            {
                bool inContent = x >= content.X && x < content.Right && y >= content.Y && y < content.Bottom;
                Color c = bmp.GetPixel(x, y);
                bool isBg = c.ToArgb() == Theme.CanvasBackground.ToArgb();
                bool isSolid = c.R == SolidR && c.G == SolidG && c.B == SolidB;
                if (inContent)
                {
                    if (!isBg && !isSolid) checkerInside++;
                }
                else if (!isBg)
                {
                    notBgOutside++;
                    if (firstOut.IsEmpty) firstOut = c;
                }
            }
        Battery.Assert(checkerInside > 0,
            "看得清的缩放档欠帧必须照旧画棋盘格（一枚都没有＝把「还没取到」的诚实标记一起删了）");
        Battery.AssertEqual(0, notBgOutside,
            $"欠帧棋盘格不许铺到内容矩形外（实际 {notBgOutside} 枚，首处 {firstOut.R},{firstOut.G},{firstOut.B}）——" +
            "内容外没有东西可取，铺出去就是整墙棋盘格的另一种形状");

        SettleDeferred(rig, bmp);
        int inside = 0;
        for (int y = 0; y < bmp.Height; y++)
            for (int x = content.X; x < content.Right; x++)
            {
                Color c = bmp.GetPixel(x, y);
                if (c.R != SolidR || c.G != SolidG || c.B != SolidB) inside++;
            }
        Battery.AssertEqual(0, inside, "欠帧补完之后内容矩形内每枚像素必须等于纯色");
        Battery.Evidence($"50% 档欠帧那一帧：内容里 {checkerInside} 枚棋盘格（诚实标记在）、内容外 0 枚；补完帧后内容内全纯色");
    });

    /// <summary>把欠帧补完：产品每帧自己 Invalidate 接着取，这里只是把那些帧真取出来。</summary>
    private static void SettleDeferred(Rig rig, Bitmap bmp)
    {
        for (int i = 0; i < 200 && rig.Canvas.TilesDeferredLastFrameForTest > 0; i++)
            rig.Canvas.RenderFrameForTest(bmp);
        Battery.AssertEqual(0, rig.Canvas.TilesDeferredLastFrameForTest,
            "补帧 200 次之后不许还欠着——还欠就说明欠帧循环自己卡住了");
    }

    /// <summary>
    /// 用户第五十四改第 3 条：横拼缩小档"闪烁不停"。病根是欠帧续画 <c>Invalidate()</c> 的是<b>整个控件</b>：
    /// 缩小档一屏上百块瓦片，GDI 位图缓存只留最后 48 块，下一帧全量重画时队头那些刚画过的块
    /// 早被挤出缓存、又变回冷活，12ms 预算全被它们吃掉，前沿永远推不到头 ⇒ 每帧全量重画＋要帧
    /// 无限循环——已画区与底色逐帧交替，屏上就是闪烁。修法：续画只把"还没画的后缀"要回来。
    ///
    /// 判据三头量：① 续画区域不许为空、不许罩住已经画过的首行瓦片（罩住＝每帧全量重画）；
    /// ② 按产品自己报的区域把续画帧的裁剪区限成同一块（真窗口里 GDI 发下来的更新区就是它），
    /// 一帧一帧泵到收敛——夹具特意把一屏块数撑到比位图缓存容量大：那样"续画其实还是全量重画"
    /// 的错误实现会让队头反复变冷、永不收敛，这一格才会红（容量之内怎么折腾都收敛，量不出卡法）；
    /// ③ <b>泵完直接验屏面</b>：带 clip 的渲染保留 clip 外的旧像素，泵完的 bmp 就是模拟屏面——
    /// 续画区域算少了哪块，屏面上就残留哪块的底色/棋盘格，这里当场红。
    /// 第五十八改之前这里量的是"收敛后整帧重画一遍再验像素"——残留被那一帧洗掉了，
    /// 第五十四改③ 的续画区域漏了左下角那块 L 形缺口（行主序后缀的包围盒罩不住下面各行的
    /// 左半截），它也照样绿，用户报的"左下角透明底/纯黑残影"因此漏网。
    /// </summary>
    private static void DeferredTailConvergesWithoutRepaintingDrawnTiles() => UiSmokeTests.RunOnStaThread(() =>
    {
        using Rig rig = SolidRig(width: 6000, height: 4500);
        rig.Canvas.Size = new Size(440, 320);
        rig.Canvas.TileBudgetMsForTest = 0.0001;   // 预算压到几乎为零：把"队头变冷"逼到每帧都发生（第五十八改起欠帧一刀落在行边界）
        rig.Canvas.Viewport.ZoomAt(0.10, new Point(0, 0));
        rig.Canvas.Viewport.MoveTopLeftTo(IntPoint.Zero);
        using var bmp = new Bitmap(440, 320);
        rig.Canvas.RenderFrameForTest(bmp);

        IntRect vis = rig.Canvas.Viewport.VisibleCanvas();
        IntRect clamped = vis.Intersect(
            new IntRect(0, 0, rig.P.Layout.CanvasSize.Width, rig.P.Layout.CanvasSize.Height));
        int cols = TileRenderer.TileX(clamped.Right - 1) - TileRenderer.TileX(clamped.X) + 1;
        int rows = TileRenderer.TileY(clamped.Bottom - 1) - TileRenderer.TileY(clamped.Y) + 1;
        Battery.Assert(cols * rows > rig.Canvas.TileBitmapCapForTest,
            $"夹具自证：一屏瓦片数（{cols}×{rows}＝{cols * rows}）必须超过 GDI 位图缓存容量" +
            $"（{rig.Canvas.TileBitmapCapForTest}）——否则量不到真窗口缩小档「队头反复变冷」的形状");
        int deferredAtFirst = rig.Canvas.TilesDeferredLastFrameForTest;
        Battery.Assert(deferredAtFirst > 0,
            $"夹具自证：第一帧必须真的欠着帧（实际欠 {deferredAtFirst} 块）");

        Rectangle tail = rig.Canvas.LastDeferredRegionForTest;
        Battery.Assert(!tail.IsEmpty,
            "欠帧续画必须按「还没画的那一段」要帧（区域为空＝退回了全量 Invalidate——" +
            "缩小档下队头反复变冷、欠帧循环永不收敛，屏上就是闪烁不停）");
        // 首行瓦片（第五十八改起第一帧画掉的是第 0 行整行）的中心点：续画区域不许把它罩进去。
        // 量中心不量整块矩形，是 CanvasToDevice 在块边界上的 1 像素取整方向不该影响判定。
        Rectangle firstTile = rig.Canvas.Viewport.CanvasToDevice(
            new IntRect(0, 0, TileRenderer.TileSize, TileRenderer.TileSize));
        var firstTileCenter = new Point(firstTile.X + firstTile.Width / 2, firstTile.Y + firstTile.Height / 2);
        Battery.Assert(!tail.Contains(firstTileCenter),
            $"续画区域不许罩住已经画过的首块瓦片（区域 {tail} 含首块中心 {firstTileCenter}）——" +
            "罩住了就等于下一帧全量重画，预算被队头吃光、前沿走不到头");

        int pumps = 0;
        while (rig.Canvas.TilesDeferredLastFrameForTest > 0 && pumps < 400)
        {
            rig.Canvas.RenderFrameForTest(bmp, rig.Canvas.LastDeferredRegionForTest);
            pumps++;
        }
        Battery.AssertEqual(0, rig.Canvas.TilesDeferredLastFrameForTest,
            $"按产品自己报的续画区域泵了 {pumps} 帧之后不许还欠着——还欠就说明续画区域没有收窄" +
            "（真窗口里就是内容与底色逐帧交替的闪烁）");

        // ═══ 第三头：泵完直接验屏面 ═══
        // 带 clip 的渲染保留 clip 外的旧像素——泵完的 bmp 就是模拟屏面，真窗口里 GDI
        // 也只重画续画区域、其余原样留在屏上。旧版这里先整帧重画再验像素，把残留洗掉了，
        // 第五十四改③ 的续画区域漏了左下角那块 L 形缺口它也照样绿（第五十八改的根因）。
        // 续画区域算少了哪块，屏面上就残留哪块的底色/棋盘格，这里当场红。
        int stale = 0;
        Color firstStale = Color.Empty;
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
            {
                Color c = bmp.GetPixel(x, y);
                if (c.R != SolidR || c.G != SolidG || c.B != SolidB) { stale++; if (firstStale.IsEmpty) firstStale = c; }
            }
        Battery.AssertEqual(0, stale,
            $"泵完的模拟屏面必须每枚像素都是内容纯色（实际 {stale} 枚不对，首处 {firstStale.R},{firstStale.G},{firstStale.B}）——" +
            "续画区域漏了哪块，屏上就残留哪块的底色/棋盘格（真窗口里就是左下角那块矩形残影）");
        Battery.Evidence($"一屏 {cols}×{rows} 块（超位图缓存容量 {rig.Canvas.TileBitmapCapForTest}）：首帧欠 {deferredAtFirst} 块、" +
                         $"续画区域 {tail}（不含首行中心），{pumps} 帧收敛后屏面逐像素纯色");
    });

    /// <summary>
    /// 用户第四十六改图3：方向键偶发"按了没反应"。先让静默的那两条早退出声
    /// （<c>NudgeNotice</c> ＋日志 seam.slide.keyboard_silent），这里钉两头：
    /// 重叠为 0 的缝按 ↑ 必须出声且说清是"重叠 0 行"；重叠正常的缝按 ↑ 必须真动且一声不吭
    /// （否则"出声"会被写成每次都嚷嚷的噪音）。
    /// </summary>
    private static void ZeroOverlapSeamSpeaksWhenArrowKeyDoesNothing() => UiSmokeTests.RunOnStaThread(() =>
    {
        using Rig rig = Harness();
        rig.P.Mutate("把重叠抹成 0", d => d.Seams[0].SetOverlap(0));
        rig.Canvas.SelectSeam(rig.Seam.Id);
        string? notice = null;
        rig.Canvas.NudgeNotice += m => notice = m;
        int cutBefore = rig.Seam.CutPosition;
        rig.Canvas.NudgeSelected(new IntPoint(0, 1));
        Battery.Assert(notice is not null && notice.Contains("重叠"),
            $"重叠为 0 的缝按方向键必须出声说清原因（实际：{notice ?? "一声不吭"}）");
        Battery.AssertEqual(cutBefore, rig.Seam.CutPosition, "夹具自证：这一按确实没动参数");

        using Rig alive = Harness();
        alive.Canvas.SelectSeam(alive.Seam.Id);
        string? aliveNotice = null;
        alive.Canvas.NudgeNotice += m => aliveNotice = m;
        int aliveBefore = alive.Seam.CutPosition;
        alive.Canvas.NudgeSelected(new IntPoint(0, -1));   // 切点 0 起步，只有 -1 这个方向还有路
        Battery.Assert(aliveNotice is null, $"重叠正常的缝按方向键不该嚷嚷（实际：{aliveNotice}）");
        Battery.Assert(aliveBefore != alive.Seam.CutPosition,
            $"重叠正常的缝按方向键必须真动（切点 {aliveBefore} → {alive.Seam.CutPosition}）");
        Battery.Evidence($"重叠 0 ⇒ 出声「{notice}」且参数不动；重叠 {Overlap} ⇒ 切点 {aliveBefore}→{alive.Seam.CutPosition} 且不出声");
    });

    /// <summary>
    /// 上一格的前提门：这把"数洞"的尺必须<b>看得见一行的洞</b>。
    /// 判据能绿有一种喂法：区域算成空的、或者产品那一帧压根没记录矩形（列表为空时区域也为空 ⇒ 0 洞）。
    /// 所以这里故意把一块瓦片的矩形<b>削掉一行</b>再交给同一套数洞函数：必须正好数出那一行的像素数。
    /// 同一趟还量另一头——把区域本身算空时确实一个洞都数不出来（证明"0"不是从空区域白捡的）。
    /// </summary>
    private static void CoverageYardSeesOneRowHole() => UiSmokeTests.RunOnStaThread(() =>
    {
        using Rig rig = TallRig();
        rig.Canvas.Size = new Size(300, 260);
        rig.Canvas.TileBudgetMsForTest = 0;
        rig.Canvas.CaptureTileRectsForTest = true;
        using var bmp = new Bitmap(300, 260);
        rig.Canvas.RenderFrameForTest(bmp);

        Rectangle area = ContentDeviceArea(rig);
        Battery.Assert(area.Width > 4 && area.Height > 4 && rig.Canvas.LastFrameTileRectsForTest.Count > 0,
            $"夹具自证：这一帧得有内容也有记录，尺才有东西可量（区域 {area}，记录 {rig.Canvas.LastFrameTileRectsForTest.Count} 块）");

        List<Rectangle> rects = rig.Canvas.LastFrameTileRectsForTest.ToList();
        Battery.AssertEqual(0, Uncovered(rects, area).Count, "同一份矩形：区域里不该有洞（与上一格同形状，这里只做前提）");

        // 把最上面那块削掉一行（模拟"相邻两块之间漏一行"那种错写）
        int top = rects.FindIndex(r => r.Top == area.Top);
        Battery.Assert(top >= 0, "夹具自证：必须能找到贴着区域顶部的那一块，否则削不到东西");
        rects[top] = new Rectangle(rects[top].X, rects[top].Y + 1, rects[top].Width, Math.Max(0, rects[top].Height - 1));
        List<(int X, int Y)> holes = Uncovered(rects, area);
        Battery.AssertEqual(area.Width, holes.Count,
            $"削掉一行就必须正好数出这一行的每个像素（实际数到 {holes.Count}，一行 {area.Width} 个）");
        Battery.Assert(holes.All(h => h.Y == area.Top), $"数出来的洞必须全在那一行上（实际首处 Y={holes[0].Y}）");

        // 另一头：区域为空时洞必须为 0（证明上面那个 0 不是从空区域白捡的）
        Battery.AssertEqual(0, Uncovered(rects, Rectangle.Empty).Count, "空区域不该凭空数出洞");
        Battery.Evidence($"削一行 ⇒ 数到 {holes.Count} 个洞（= 一行 {area.Width} 像素），全在 Y={holes[0].Y}；空区域 ⇒ 0");
    });

    // ───────────────────────── ⑤ 一路到底：同一个 offset 进导出 ─────────────────────────

    /// <summary>
    /// 位移一路从 <c>Seam.ManualOffset</c> 用到导出文件的字节（预览与成品逐字节相同）。
    /// 第九轮把这趟"拖完就导出"的核对留在<b>竖拼</b>：长截图那一路现在由裁切参数驱动，
    /// 由 <c>crop/预览合成与导出文件逐字节相同（分块=整块同样成立）</c> 接管。
    /// </summary>
    private static void DragResultFlowsIntoExportByteIdentical() => UiSmokeTests.RunOnStaThread(() =>
    {
        using Rig rig = Harness(StitchMode.Vertical);
        Drag(rig, new Point(40, LineY), new Point(40, LineY + 20));
        IntPoint effective = rig.P.Layout.EffectiveOffset(rig.Seam);

        // 一.7：布局、预览、导出、日志读的是同一个数
        Battery.AssertEqual(rig.Seam.ManualOffset!.Value, effective, "布局用的生效位移 == 参数里那个");
        Battery.AssertEqual(160, rig.P.Layout.DescribeSeam(rig.Seam)!.Offset.Y, "layout.seam 那本账也是同一个数");

        string outDir = Path.Combine(Path.GetTempPath(), "ls-canvas-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(outDir);
        try
        {
            var encoders = new EncoderRegistry();
            var settings = new ExportSettings
            {
                Format = ExportFormat.Png,
                OutputDirectory = outDir,
                MaxSegmentHeight = 0,
                SegmentOverlap = 0,
                DownscaleDivisor = 1,
                FileNameTemplate = "drag_{index}.{ext}",
            };
            ExportPlan plan = ExportPlanner.Build(rig.P.Layout, settings, encoders, outDir, rig.Composer, rig.R);
            Battery.Assert(!plan.IsHardBlock, $"计划被硬拦：{plan.BlockingReason}");
            Battery.AssertEqual(rig.P.Layout.CanvasSize.Height, plan.OriginalPixelSize.Height, "计划尺寸来自同一份布局");

            ExportOutcome outcome = new ExportEngine(rig.Composer, encoders)
                .Run(rig.P.Layout, plan, settings, outDir);
            Battery.Assert(outcome.Success, "导出失败：" + outcome.Error);

            byte[] preview = rig.Composer.RenderWhole(rig.P.Layout).RegionSha256(rig.P.Layout.CanvasBounds);
            byte[] file = PngDecoder.Decode(File.ReadAllBytes(outcome.Files[0])).Image
                .RegionSha256(new IntRect(0, 0, Width, rig.P.Layout.CanvasSize.Height));
            Battery.AssertBytesEqual(preview, file, "拖完之后的预览与导出文件");
            Battery.AssertEqual(Height + Height - 40, rig.P.Layout.CanvasSize.Height,
                "offsetY=160 ⇒ 总高 = 200 + 200 − 40");
            Battery.Evidence($"导出 {Width}×{rig.P.Layout.CanvasSize.Height}，与整块合成逐字节相同；" +
                             "位移 160 一路从 Seam.ManualOffset 用到文件像素");
        }
        finally
        {
            try { Directory.Delete(outDir, recursive: true); } catch { /* 临时目录清不掉不影响判据 */ }
        }
    });

    // ───────────────────────── ⑥ 可点区域 ─────────────────────────

    private static void HitRegionCoversLineBandAndOutside() => UiSmokeTests.RunOnStaThread(() =>
    {
        using Rig rig = Harness();

        // 线上上下下 1–9 像素都要命中（需求"至少 8 个设备像素"，这里连 9 一起量）
        for (int d = 1; d <= 9; d++)
        {
            var hit = rig.HitAt(new Point(40, LineY + d));
            Battery.Assert(hit?.Kind == StitchCanvasControl.SeamHitKind.CutLine,
                $"线下方 {d} 像素应当命中拼缝线，实际 {hit?.ToString() ?? "未命中"}");
        }
        Battery.AssertEqual(StitchCanvasControl.SeamHitKind.CutLine,
            rig.HitAt(new Point(40, LineY - 8))!.Value.Kind, "线上方 8 像素同样要命中");

        // 越过容差之后，重叠带内部仍然可点（"红框不是只有一条 1 像素线能点"）。
        // 第十二轮起带里再分两档：贴着带边的那一条是红箭头把手（AlignmentAdjust），
        // 其余才是带命中（OverlapBand）——两档都进对齐手势，但日志要能分清用户抓的是哪个。
        int bandHits = 0, handleHits = 0;
        for (int y = BandTop + 12; y < BandBottom; y += 2)
        {
            var hit = rig.HitAt(new Point(40, y));
            switch (hit?.Kind)
            {
                case StitchCanvasControl.SeamHitKind.OverlapBand: bandHits++; break;
                case StitchCanvasControl.SeamHitKind.AlignmentAdjust: handleHits++; break;
                default:
                    Battery.Assert(false, $"带内 y={y} 应当命中带或把手，实际 {hit?.ToString() ?? "未命中"}");
                    break;
            }
        }
        Battery.Assert(bandHits > 10, $"带里大部分行要报带命中，实际 {bandHits}（把手 {handleHits}）");
        Battery.Assert(handleHits > 0, $"贴带边那一条要报红箭头把手命中，实际 {handleHits}");

        // 上下侧按<b>蓝线</b>分（第十二轮）：蓝线才是"内容来源的分界"，
        // 带中线不是——默认档蓝线就在带顶，所以整条带都在它下方，抓的都是图2。
        Battery.AssertEqual(FixedSide.After, rig.HitAt(new Point(40, 150))!.Value.Side,
            "默认档蓝线在带顶：带里的点都在蓝线之下 ⇒ 抓的是图2");
        Battery.AssertEqual(FixedSide.After, rig.HitAt(new Point(40, 190))!.Value.Side, "带下半=抓下面那张");
        Battery.AssertEqual(FixedSide.After, rig.HitAt(new Point(40, LineY + 3))!.Value.Side, "线下方=抓下面那张");
        Battery.AssertEqual(FixedSide.Before, rig.HitAt(new Point(40, LineY - 3))!.Value.Side, "线上方=抓上面那张");

        // 带外但在图里：必须回到"平移画布"，否则用户又拖不动画布了
        Battery.Assert(rig.HitAt(new Point(40, BandBottom + 10)) is null,
            "重叠带之外（后一段自己的画面里）不该命中拼缝，否则又变成拖不动画布");
        Battery.Assert(rig.HitAt(new Point(40, 40)) is null, "前一段自己的画面里也不该命中");

        // 反向对照：命中区不是"整张图都算命中"那种假绿
        int hits = 0;
        for (int y = 0; y < 340; y++) if (rig.HitAt(new Point(40, y)) is not null) hits++;
        Battery.Assert(hits is > 0 and < 340,
            $"可点区必须只占画面的一小段（实际 {hits}/340 行），否则等于把平移画布关掉了");
        Battery.Evidence($"可点 {hits} 行：线 ±9 + 重叠带 [{BandTop},{BandBottom})；其余一律平移");
    });
}
