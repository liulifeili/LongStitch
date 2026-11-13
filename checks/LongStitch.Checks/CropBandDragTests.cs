using LongStitch.App.Diagnostics;
using LongStitch.App.Imaging;
using LongStitch.App.UI;
using LongStitch.Core;
using LongStitch.Core.Geometry;
using LongStitch.Core.Imaging;
using LongStitch.Core.Imaging.Png;
using LongStitch.Core.Layout;
using LongStitch.Core.Model;
using LongStitch.Core.Preview;
using LongStitch.Core.ProjectIO;

namespace LongStitch.Checks;

/// <summary>
/// 蓝线与对齐是两个手势（第十二轮）。
///
/// ═══ 这一组格子守的是哪一句话 ═══
/// 「长截图不是把两张完整图片上下移动后重叠，而是先通过像素对比调整两张图在公共坐标中的
/// 相对位置，再用蓝色裁切线决定蓝线上方由图1 提供、蓝线下方由图2 提供。」
/// 这句话落到界面上就是<b>两根可拖的东西、两个互不重叠的字段</b>：
/// <list type="bullet">
///   <item>拖<b>蓝色裁切线</b> ⇒ 只改 <c>CutY</c>：对齐、带、画布高、视口全部一字不动；</item>
///   <item>拖<b>红色箭头 / 重叠带</b> ⇒ 只改 <c>AlignmentOffset</c>：带重算、画布变高变矮、
///         蓝线跟着重新夹取，而视口仍然不许动。</item>
/// </list>
/// 第十一轮之前这两件事共用一条通路（带里拖动=改切点），所以那一轮的 8 格在这里全部
/// <b>搬家</b>而不是删掉：想验蓝线的改成按住蓝线拖，想验对齐的新写一格。
/// 判据搬家比判据消失诚实。
///
/// ═══ 夹具为什么是这个尺寸 ═══
/// 60×600 两张、重叠 120 ⇒ 画布 1080 行 &gt; 一块 512 瓦片，于是"带外的瓦片"真的存在，
/// "缓存复用"与"逐字节比对"都不是空判据。带在画布上恒为 [480, 600)（与蓝线位置无关），
/// 蓝线在 480+CutY。视口 400×500 时可见瓦片只有 2 块，所以"画布外不重渲染"是量得出来的。
/// </summary>
public static class CropBandDragTests
{
    private const int Width = 60, Height = 600, Overlap = 120;

    /// <summary>默认档（CutY=0，整带取图2）时蓝线所在的画布行号 = H − O。</summary>
    private const int BandTopY = Height - Overlap;        // 480

    /// <summary>带尾：图1 的底边。带 = [480, 600)，与蓝线在带里的位置无关。</summary>
    private const int BandBottomY = Height;               // 600

    /// <summary>带区在任何蓝线位置下都是这一段（这是"带就是 Rect1∩Rect2"的直接后果）。</summary>
    private static readonly IntRect ExpectedBand = new(0, BandTopY, Width, Overlap);

    /// <summary>异本标记色：纯红。<see cref="LongScrollTests.StripedPage"/> 的 R 通道来自 y%233，到不了 255。</summary>
    private static readonly RgbaColor Marker = new(255, 0, 0, 255);

    /// <summary>
    /// 标记比起点重叠多涂这么多行。第十三轮的对齐判据会把带拖长（120→140），
    /// 于是"蓝线以下取图2"那几行落在图2 自己原来的内容里——标记色覆盖不住就判不出来源
    /// （第一次跑就红在第 580 行，正是这个原因）。多涂一段，带再怎么长都有东西可比。
    /// </summary>
    private const int ExtraMarkerRows = 200;

    public static void Register()
    {
        // ── 公共坐标与命中（需求二、九）──
        Battery.Add("align", "带就是 Rect1∩Rect2，与蓝线位置无关", BandIsIntersectionOfFullRects);
        Battery.Add("align", "两侧裁切量是蓝线两侧的分界位置，和恒等于重叠", CropsAreDerivedFromCutY);
        Battery.Add("align", "命中四档各归其位：把手开关微调、线上与带里都滑动那张图", () =>
            UiSmokeTests.RunOnStaThread(ThreeHitKindsRouteToTwoGestures));
        Battery.Add("align", "裁后两矩形不相交时红框仍然可命中：命中区只认带区", BandHittableWhileRectsDisjoint);
        Battery.Add("align", "30%·50%·100% 缩放下红框命中与显示同源，拖动折算仍按缩放", () =>
            UiSmokeTests.RunOnStaThread(HitAndDragMatchDrawingAtThreeZooms));

        // ── 蓝线手势（需求四、七.2）──
        Battery.Add("align", "压着蓝线滑动下方那张图：蓝线的画布行号与视口都不动", () =>
            UiSmokeTests.RunOnStaThread(LineDragChangesCutOnly));
        Battery.Add("align", "关掉调试层不改对齐、蓝线、导出字节与命中区", () =>
            UiSmokeTests.RunOnStaThread(DebugLayerOffChangesNothingButPixels));

        // ── 对齐手势（需求六、七.1）──
        Battery.Add("align", "在带里滑动下方那张图：带重算、蓝线画布行号不动、视口不动", () =>
            UiSmokeTests.RunOnStaThread(ArrowDragChangesAlignmentOnly));
        Battery.Add("align", "带里选中后方向键滑动那张图恰好一源像素", () =>
            UiSmokeTests.RunOnStaThread(ArrowKeysNudgeAlignmentOnePixel));
        Battery.Add("align", "对齐拖动整趟只提交一条撤销，拖回原位不留空记录", () =>
            UiSmokeTests.RunOnStaThread(ArrowDragPushesExactlyOneUndo));
        Battery.Add("align", "滑动滑到头时被夹掉的行数要出声、参数停在边界", () =>
            UiSmokeTests.RunOnStaThread(AlignmentShrinkReclampsCut));
        // ── 真实屏幕坐标（第十三轮需求二：量屏上那两块矩形，不量模型字段）──
        Battery.Add("align", "固定前一段：拖对齐前后图1 的屏幕矩形一字不动、图2 跟着动", () =>
            UiSmokeTests.RunOnStaThread(FixedBeforeAlignDragKeepsImage1OnScreen));
        Battery.Add("align", "固定后一段：拖对齐前后图2 的屏幕矩形一字不动、图1 跟着动", () =>
            UiSmokeTests.RunOnStaThread(FixedAfterAlignDragKeepsImage2OnScreen));
        // ── 第十四轮需求一：撤销要把「纯显示补偿」也一起退回去 ──
        Battery.Add("align", "固定后一段拖对齐后撤销：参数、画布高、视口、设备矩形一起恢复", () =>
            UiSmokeTests.RunOnStaThread(UndoAfterFixedAfterDragRestoresViewport));
        Battery.Add("align", "重新分析不许抹掉用户调好的对齐", ReanalysisKeepsUserAlignment);
        Battery.Add("align", "对齐拖动 100 次鼠标移动：不整块清缓存、只重画带顶往下且在视口内", () =>
            UiSmokeTests.RunOnStaThread(HundredMovesDuringAlignmentDrag));
        // #95：真窗口工装那 5 趟连跑读到的形状是"第 1 趟重叠 120→1071，第 2~5 趟一字不动"，
        // 缺的是能把"产品棘轮"与"工装命中错位"分开的数。这一格把<b>同一形状</b>在进程内重放一遍：
        // 净位移 0 的一趟来回拖动，不许改动任何参数、不许留撤销记录。
        Battery.Add("align", "来回拖同一趟（净位移 0）在小缩放档与固定后一段下也一字不改", () =>
            UiSmokeTests.RunOnStaThread(ClosedLoopDragAtSmallZoomChangesNothing));
        Battery.Add("guard", "来回拖那一格必须真进了对齐手势（正向对照在反证里）", () =>
            UiSmokeTests.RunOnStaThread(ClosedLoopCellDragReallyEntersAlignmentGesture));

        // ── 日志（需求十一）──
        Battery.Add("align", "滑动两趟的日志各自齐备且互不覆盖", () =>
            UiSmokeTests.RunOnStaThread(TwoSlideTracesAreSeparate));
        Battery.Add("ui", "面板蓝线读数跟着画布拖动的结果走，不停在旧值", () =>
            UiSmokeTests.RunOnStaThread(PanelCutReadoutFollowsCanvasDrag));
        Battery.Add("ui", "画布点选拼缝不反向滚动视口", () =>
            UiSmokeTests.RunOnStaThread(CanvasSelectionDoesNotScrollViewport));

        // ── 第十四轮交互重设计：线不动、图动 + 把手只开关微调档 + 点选上下侧决定方向键改哪个量 ──
        Battery.Add("align", "滑动一张图时蓝线的屏幕行号一字不动", () =>
            UiSmokeTests.RunOnStaThread(LineScreenRowStaysPutWhilePictureMoves));
        Battery.Add("align", "把手开档再点一下完成一次Esc就退出编辑", () =>
            UiSmokeTests.RunOnStaThread(AdjustHandleTogglesModeOnly));
        Battery.Add("align", "微调档里点蓝线上下各挑中那一张", () =>
            UiSmokeTests.RunOnStaThread(AdjustModeClickPicksTheSide));

        // ── 第十四轮新增：导入／分析期间画布虚化 + 进度条 ──
        Battery.Add("ui", "忙碌遮罩虚化的是画布那一帧且进度条真跟着走", () =>
            UiSmokeTests.RunOnStaThread(BusyOverlayReallyBlursAndProgressAdvances));
        Battery.Add("ui", "导入那一趟把遮罩开到收掉且进度走到最后一张", () =>
            UiSmokeTests.RunOnStaThread(ImportDrivesTheOverlayTrip));
        Battery.Add("guard", "遮罩那一层不进画布自己那一帧", () =>
            UiSmokeTests.RunOnStaThread(OverlayNeverEntersCanvasFrameOrParameters));

        // 这两格量的是"预览帧到底改了几块瓦片"，与手势无关，仍留在 crop 组里
        Battery.Add("crop", "100 次鼠标移动只换几帧预览：不整块清缓存、不逐帧写日志", () =>
            UiSmokeTests.RunOnStaThread(HundredMouseMovesCoalesce));
        Battery.Add("crop", "切点预览只改带内瓦片：带外逐字节相同", OnlyBandTilesChangeDuringCutPreview);
        // 第十八改：对齐那一手势<b>不是</b>高度守恒那个形状，所以带以下每一块都得重算（见那格的注释）
        Battery.Add("align", "拖对齐时带以下每一块瓦片的内容确实变了：作废范围已是最小",
            AlignmentDragChangesEveryTileBelowBand);

        // ═══ 守卫的守卫（精度红线 §3：每写一条能抓 bug 的断言，就配一格注缺陷要求它必须红）═══
        Battery.Add("guard", "命中区若改用裁后矩形相交则红框整块点不到", HitRegionFromIntersectionWouldBeEmpty);
        Battery.Add("guard", "预览若整块清瓦片缓存则计数立刻翻红", FullCacheClearIsWhatTheCounterMeasures);
        Battery.Add("guard", "面板读数不跟修订号刷新则停在旧值", () =>
            UiSmokeTests.RunOnStaThread(PanelStaysStaleWithoutRefresh));
        Battery.Add("guard", "绕开核心字段把蓝线写到带外必被夹取与去重账抓到", CutWrittenOutOfBandIsCaught);
        Battery.Add("guard", "蓝线与对齐若共用一个字段则两条手势会互相顶掉", SharedFieldWouldCrossOverwrite);
        Battery.Add("guard", "固定后一段若撤掉显示补偿则图2 在屏上会漂", WithoutAnchorCompensationImage2WouldDrift);
        Battery.Add("guard", "滑动若沿用旧的换算或只夹不换则蓝线会在屏上跑掉", OldClampOnlyRuleWouldDriftTheCutRatio);
        Battery.Add("guard", "拖对齐若只丢带区那几块则屏上会留上一帧的像素",
            BandOnlyDropOnAlignmentDragWouldShowStalePixels);
        // ── 第十四轮需求五补量：滚轮那一档的每帧瓦片预算（详见下面那三格的文档注释）──
        Battery.Add("canvas", "滚轮那一档一帧画不完的瓦片必须欠帧、追一帧、欠的那块画成棋盘格", () =>
            UiSmokeTests.RunOnStaThread(TileBudgetDefersTheRestOfTheScreen));
        Battery.Add("canvas", "瓦片限流连着画满的那一帧与不限流那一帧逐字节相同", () =>
            UiSmokeTests.RunOnStaThread(BudgetedFramesSettleToTheSamePicture));
        Battery.Add("guard", "撤销若只换文档不带显示快照则视口停在补偿位置", UndoWithoutDisplaySnapshotLeavesViewportDrifted);
        Battery.Add("guard", "瓦片预算写得比一整屏还大时不许出现欠帧", () =>
            UiSmokeTests.RunOnStaThread(HugeBudgetMustNotDeferAnything));
        Battery.Add("canvas", "滚轮途中走便宜插值、停手后只补一帧高质量", () =>
            UiSmokeTests.RunOnStaThread(WheelUsesCheapTierDuringGestureAndOneSettleFrame));
        Battery.Add("guard", "滚轮补帧次数不许随滚轮格数线性增长", () =>
            UiSmokeTests.RunOnStaThread(SettleCountDoesNotScaleWithWheelNotches));
    }

    // ─────────────────── ⑤b 每帧瓦片预算（需求五补量） ───────────────────

    /// <summary>
    /// 需求五那两条阈值（单帧 ≤ 50ms、P95 ≤ 33ms）在<b>拖动</b>档一直过，
    /// 原因很具体：拖动只在<b>已经缓存过</b>的带区里挪，一帧几乎不取数。
    /// <b>滚轮不是这个形状</b>——150% 真窗口实测（一趟 20 格、前半下后半上）：
    /// 二十格只换来 3~4 帧，其中冷缓存那一帧「取数 30.5ms ＋ 贴图 20.1ms」＝ <b>55.6ms</b>，
    /// 当场超了「单帧 ≤ 50ms」，而那一帧<b>没有</b>回收标记、也不是被抢占（同帧内最忙那条线程的
    /// CPU 与墙钟一起涨）——是真在算。按铁律只能改实现，不能改阈值：
    /// 一帧里画瓦片按时间预算限流，画不完的下一帧接着取
    /// （产品出口 <c>StitchCanvasControl.TileBudgetMsForTest</c>，默认 12ms）。
    ///
    /// 这一格只管「限流真的在限」这一头：预算压到几乎为零时必须有欠帧、必须当场追加要帧、
    /// 欠下的那块必须画成棋盘格而不是留成画布底色（留白＝用户以为这里没内容）。
    /// 「补上之后画面不变」在 <c>canvas/瓦片限流连着画满的那一帧与不限流那一帧逐字节相同</c>，
    /// 「开关不是恒真」在 <c>guard/瓦片预算写得比一整屏还大时不许出现欠帧</c>。
    /// </summary>
    private static void TileBudgetDefersTheRestOfTheScreen()
    {
        using Rig rig = NewRig(zoom: 0.35, origin: IntPoint.Zero);
        StitchCanvasControl canvas = rig.Canvas;
        canvas.CreateControl();
        int cells = VisibleCellCount(rig);
        Battery.Assert(cells >= 2,
            $"夹具自证：这一档视口里得有 ≥2 块瓦片才谈得上「画不完」，实际 {cells} 块");

        using var deferredFrame = new System.Drawing.Bitmap(400, 500);
        canvas.TileBudgetMsForTest = 0.0001;              // 预算压到几乎为零：把欠帧变成必然（第五十八改起欠帧一刀落在行边界）
        canvas.RenderFrameForTest(deferredFrame);
        int deferred = canvas.TilesDeferredLastFrameForTest;
        Battery.Assert(deferred > 0,
            $"预算压到几乎为零时必须有欠帧，实际欠 {deferred} 块——0 就是限流没生效"
            + "（预算没读进去、或者压根没按块停手）");
        Battery.Assert(canvas.DeferredRepaintRequestsForTest >= 1,
            "欠帧必须当场再要一帧，否则屏上永久缺这一块（需求五问的就是滚动时的画面）");

        // 欠下的那块不许留成画布底色：它得是「还没取到」的形状（棋盘格）
        Rectangle pending = canvas.Viewport.CanvasToDevice(
            new IntRect(0, TileRenderer.TileSize, Width, TileRenderer.TileSize));
        Color at = deferredFrame.GetPixel(pending.X + pending.Width / 2, pending.Y + pending.Height / 2);
        Battery.Assert(at.ToArgb() != Theme.CanvasBackground.ToArgb(),
            $"欠帧那块的中心是画布底色 {at}——留白等于让用户以为这里没内容，"
            + "限流的代价必须画成棋盘格，让人看得出「还没取到」");
        Battery.Evidence($"预算 0.0001ms ⇒ 视口 {cells} 块里第一帧只画第 0 行（欠帧一刀落在行边界，第五十八改）、欠 {deferred} 块、"
            + $"追加要帧 {canvas.DeferredRepaintRequestsForTest} 次；欠帧那块中心＝{at}");
    }

    /// <summary>
    /// 限流只许改「什么时候画」，不许改「画成什么」：
    /// 冷缓存下先用极小预算一帧一行地连着画满（第五十八改起欠帧一刀落在行边界），再与另一份冷缓存一次画满（预算 0＝不限流）的画面逐字节比。
    /// 这一条是整个改动的保命绳——两支只要差一个像素，就说明「欠帧」不是节奏问题而是画面问题。
    /// </summary>
    private static void BudgetedFramesSettleToTheSamePicture()
    {
        using Rig whole = NewRig(zoom: 0.35, origin: IntPoint.Zero);
        using Rig limited = NewRig(zoom: 0.35, origin: IntPoint.Zero);
        whole.Canvas.CreateControl();
        limited.Canvas.CreateControl();
        using var a = new System.Drawing.Bitmap(400, 500);
        using var b = new System.Drawing.Bitmap(400, 500);

        whole.Canvas.TileBudgetMsForTest = 0;             // 0＝不限流：一次画满，作为对照
        whole.Canvas.RenderFrameForTest(a);

        limited.Canvas.TileBudgetMsForTest = 0.0001;
        int frames = 0;
        do
        {
            limited.Canvas.RenderFrameForTest(b);
            frames++;
        }
        while (limited.Canvas.TilesDeferredLastFrameForTest > 0 && frames < 40);

        Battery.Assert(limited.Canvas.TilesDeferredLastFrameForTest == 0,
            $"连着画了 {frames} 帧还没画满——限流之后补不上，屏上就永久缺块（这是产品缺陷，不是判据太严）");
        Battery.Assert(frames >= 2,
            $"夹具自证：限流那一支必须真的分了几帧才谈得上「补上之后相同」，实际 {frames} 帧");
        Battery.AssertEqual(0, HudCostTests.CountPanelBytesDiffer(a, b),
            $"限流分 {frames} 帧画满的画面必须与不限流一次画满的逐字节相同（一帧一行也只改节奏不改像素）");
        Battery.Evidence($"一帧一行、共 {frames} 帧画满 ⇒ 与一次画满 0 字节之差；"
            + $"追加要帧 {limited.Canvas.DeferredRepaintRequestsForTest} 次");
    }

    /// <summary>夹具自证用：这一档视口里一共几块瓦片（与产品同一套 TileX/TileY 折算，不另造一套）。</summary>
    private static int VisibleCellCount(Rig rig)
    {
        IntRect visible = rig.Canvas.Viewport.VisibleCanvas(TileRenderer.TileSize);
        int x0 = TileRenderer.TileX(visible.X), x1 = TileRenderer.TileX(visible.Right);
        int y0 = TileRenderer.TileY(visible.Y), y1 = TileRenderer.TileY(visible.Bottom);
        return (x1 - x0 + 1) * (y1 - y0 + 1);
    }

    /// <summary>
    /// 反证（精度红线 §3：能抓 bug 的断言必须配一格注缺陷要求它红）。
    /// 预算写得比一整屏还大（一百万毫秒）时<b>必须一帧画满、不许欠帧</b>。
    /// 上一格说「小预算必须欠帧」，本格说「大预算不许欠帧」——
    /// 若实现压根不读预算（恒欠或恒不欠），两格里必有一格红；
    /// 若「欠帧计数」是个写死的数，本格当场红。
    /// </summary>
    private static void HugeBudgetMustNotDeferAnything()
    {
        using Rig rig = NewRig(zoom: 0.35, origin: IntPoint.Zero);
        rig.Canvas.CreateControl();
        int cells = VisibleCellCount(rig);
        Battery.Assert(cells >= 2, $"夹具自证：视口里得有两块以上，否则「不许欠帧」是空判，实际 {cells} 块");

        rig.Canvas.TileBudgetMsForTest = 1_000_000;
        long repaintsBefore = rig.Canvas.DeferredRepaintRequestsForTest;
        using var frame = new System.Drawing.Bitmap(400, 500);
        rig.Canvas.RenderFrameForTest(frame);
        Battery.AssertEqual(0, rig.Canvas.TilesDeferredLastFrameForTest,
            "预算大到一屏装得下时一帧就该画满，不许报欠帧");
        Battery.AssertEqual(repaintsBefore, rig.Canvas.DeferredRepaintRequestsForTest,
            "更不许为一帧画满的画面追加要帧（那会变成白刷）");
        // 视口里的"格子"比画布上有内容的瓦片多（画布只有 60px 宽，一列之外全是空的），
        // 所以这里只证"这一帧真的取到了东西"，不把两个口径硬扯成相等——
        // 那种类相等判据曾经把 81 vs 3 报成产品缺陷，其实是夹具在量两件不同的事。
        Battery.Assert(rig.Canvas.TileCountForTest >= 1,
            $"夹具自证：大到一百万毫秒的预算这一帧至少该取到一块瓦片，实际 {rig.Canvas.TileCountForTest} 块"
            + $"（视口格数 {cells}，其中有内容的没这么多）");
    }

    // ───────────────────────── 夹具 ─────────────────────────

    // ─────────── ⑤c 滚轮档的重采样（需求三"停手那一帧同质量"的滚轮版本） ───────────

    /// <summary>
    /// 滚轮档原来不管三七二十一每帧都按"静止"给最贵的那档重采样：35% 真窗口下同一屏瓦片数
    /// 与拖动档差不多，拖动贴图 11~15ms、滚轮档 16~27ms，差就差在这档上。
    /// 这一格钉住改动的两头：
    /// <list type="bullet">
    ///   <item>途中的帧走便宜档 <c>Cheap</c>，静止基线仍是 <c>HighQuality</c>——
    ///         用户停下来审视的那一张必须与改动前同质量；</item>
    ///   <item>一次手势（连打几格滚轮）只在停手后补<b>一帧</b>高质量，滚动中途一帧都不补。</item>
    /// </list>
    /// "补几帧"这件事的反证在 <c>guard/滚轮补帧次数不许随滚轮格数线性增长</c>。
    ///
    /// 口径边界：滚轮的<b>两档</b>（普通平移与 Ctrl+缩放）都算"正在动"，因为它们走的是同一个
    /// <see cref="StitchCanvasControl.ApplyWheel"/> 入口；缩放这一档纳进来是真窗口量出来的——
    /// 开着读数层时缩放通道途中帧读到 35.5ms、把 P95 顶过 33ms。
    /// 100% 及以上永远走 <c>Nearest</c>（放大看接缝必须看到真像素），这一改对它无效。
    /// </summary>
    private static void WheelUsesCheapTierDuringGestureAndOneSettleFrame()
    {
        using Rig rig = NewRig(zoom: 0.35, origin: IntPoint.Zero);
        StitchCanvasControl canvas = rig.Canvas;
        canvas.CreateControl();
        canvas.TileBudgetMsForTest = 0;         // 关每帧瓦片限流：本格只管重采样档，别让欠帧混进读数
        using var frame = new System.Drawing.Bitmap(400, 500);
        var device = new Point(200, 250);

        canvas.RenderFrameForTest(frame);
        Battery.AssertEqual("HighQuality", canvas.LastFrameInterpolationForTest,
            "没碰过滚轮时是静止基线（高质量档）——这一头既是改动的对照组，也是“停手回到原样”里的那个原样");
        Battery.AssertEqual(0L, canvas.SettlePaintsForTest, "夹具自证：这份夹具此刻还没补过帧");

        canvas.ApplyWheel(120, Keys.None, device);
        Battery.Assert(canvas.SettleTimerArmedForTest,
            "滚轮当场就得把补帧计时器挂上；没挂上＝停手后没人补那一帧，屏上永久停在便宜档");
        canvas.RenderFrameForTest(frame);
        Battery.AssertEqual("Cheap", canvas.LastFrameInterpolationForTest,
            "滚轮之后紧接那一帧必须走便宜档——需求五滚轮档那枚红（55.6ms）就出在这种帧上");

        // Ctrl+滚轮（缩放）这一档同样要算"正在动"，否则缩放途中每帧都给最贵的那档：
        // 读数层开着时缩放通道量到 35.5ms，就是这一头。夹具从 0.35 起缩放，几格之内仍小于 1.0，
        // 所以还谈得上"便宜档"（到 1.0 以上由 Nearest 接管，不归这一格管）。
        using Rig zoomRig = NewRig(zoom: 0.35, origin: IntPoint.Zero);
        zoomRig.Canvas.CreateControl();
        zoomRig.Canvas.TileBudgetMsForTest = 0;
        zoomRig.Canvas.RenderFrameForTest(frame);
        Battery.AssertEqual("HighQuality", zoomRig.Canvas.LastFrameInterpolationForTest,
            "缩放夹具的起点也得是静止基线（不然下面那句 Cheap 无从跟谁比）");
        zoomRig.Canvas.ApplyWheel(120, Keys.Control, device);
        Battery.Assert(zoomRig.Canvas.Viewport.Zoom < 1.0,
            $"夹具自证：Ctrl+滚轮几格之后仍得留在 100% 以下才谈得上便宜档，实际 {zoomRig.Canvas.Viewport.Zoom:F3}");
        Battery.Assert(zoomRig.Canvas.SettleTimerArmedForTest,
            "Ctrl+滚轮也得挂补帧计时器——不挂就是缩放途中那一帧永久停在便宜档");
        zoomRig.Canvas.RenderFrameForTest(frame);
        Battery.AssertEqual("Cheap", zoomRig.Canvas.LastFrameInterpolationForTest,
            "缩放途中的帧必须走便宜档（35.5ms 那枚红改的就是这一头）");

        // "一次手势只补一帧"单独用一份夹具量：连打期间一帧都不渲染，
        // 免得"渲染花了多久"混进 120ms 窗口的账里（那会把机器慢变成产品红）。
        using Rig burst = NewRig(zoom: 0.35, origin: IntPoint.Zero);
        burst.Canvas.CreateControl();
        burst.Canvas.TileBudgetMsForTest = 0;
        long settlesBefore = burst.Canvas.SettlePaintsForTest;
        long burstStart = Environment.TickCount64;
        int notches = 0;
        while (notches < 5 && Environment.TickCount64 - burstStart < 60)
        {
            burst.Canvas.ApplyWheel(120, Keys.None, device);
            notches++;
        }
        long burstMs = Environment.TickCount64 - burstStart;
        Battery.Assert(notches >= 2,
            $"夹具自证：得有两格以上落在同一个窗口里，才谈得上「按格排会补多帧」，"
            + $"本机 {burstMs}ms 只打进 {notches} 格");
        Battery.AssertEqual(0L, burst.Canvas.SettlePaintsForTest - settlesBefore,
            "连续滚动中途一次都不许补：每格都补＝把停手那笔钱提前付掉（这正是被换掉的那种写法踩的坑）");

        // 停手：自适应泵消息直到补帧发生（不钉固定墙钟窗口——本轮另一格已经吃过这一课）
        long waitStart = Environment.TickCount64;
        while (burst.Canvas.SettlePaintsForTest == settlesBefore
               && Environment.TickCount64 - waitStart < 3000)
        {
            Application.DoEvents();
            System.Threading.Thread.Sleep(5);
        }
        long waitedMs = Environment.TickCount64 - waitStart;
        long settles = burst.Canvas.SettlePaintsForTest - settlesBefore;
        Battery.Assert(settles == 1,
            $"连打 {notches} 格之后停手，只许补一帧高质量（每次滚轮对计时器是「重启」不是「追加」）；"
            + $"实测等了 {waitedMs}ms、补了 {settles} 帧");
        Battery.Assert(!burst.Canvas.SettleTimerArmedForTest,
            "补完那一帧计时器就该停下——还挂着说明它会继续补第二帧");

        long extraStart = Environment.TickCount64;
        while (Environment.TickCount64 - extraStart < 400)
        {
            Application.DoEvents();
            System.Threading.Thread.Sleep(5);
        }
        Battery.AssertEqual(1L, burst.Canvas.SettlePaintsForTest - settlesBefore,
            "再泵 400ms 仍只补了一帧（“每帧续排”的写法在这里会涨到两帧以上）");
        burst.Canvas.RenderFrameForTest(frame);
        Battery.AssertEqual("HighQuality", burst.Canvas.LastFrameInterpolationForTest,
            "补的那一帧与静止基线同档：用户停下来看到的那张图必须与改动前一字不差");
        Battery.Evidence($"静止＝HighQuality、滚轮途中＝Cheap、连打 {notches} 格（{burstMs}ms）中途 0 补帧、"
            + $"停手 {waitedMs}ms 后补 1 帧、再等 400ms 没有第二帧");
    }

    /// <summary>
    /// 反证（精度红线 §3：能抓 bug 的断言必须配一格注缺陷要求它红）。
    /// 上一格钉"一次手势只补一帧"，本格钉这个 1 <b>不是写死的</b>：
    /// 实现成<b>每格滚轮各排一帧</b>（那是最初试的 BeginInvoke 自续排的形状，
    /// 实测把 35% 滚轮档池 P95 从 27.3ms 顶到 30.5ms、25% 从 28.3 顶到 32.0），
    /// 五格就会补出与格数成正比的帧数；把补帧<b>整个撤掉</b>则一帧都不补。
    /// 两种缺陷各红一头，只有正确实现才两档都读到 1。
    /// </summary>
    private static void SettleCountDoesNotScaleWithWheelNotches()
    {
        static long SettleAfter(int notches)
        {
            using Rig rig = NewRig(zoom: 0.35, origin: IntPoint.Zero);
            rig.Canvas.CreateControl();
            rig.Canvas.TileBudgetMsForTest = 0;
            long before = rig.Canvas.SettlePaintsForTest;
            var device = new Point(200, 250);
            long start = Environment.TickCount64;
            for (int i = 0; i < notches; i++) rig.Canvas.ApplyWheel(120, Keys.None, device);
            long burstMs = Environment.TickCount64 - start;
            Battery.Assert(burstMs < 60,
                $"夹具自证：{notches} 格必须打在 120ms 窗口之内（本机用了 {burstMs}ms），"
                + "否则两档的差会被窗口过期吃掉，量出来的相等是假的");
            long waitStart = Environment.TickCount64;
            while (rig.Canvas.SettlePaintsForTest == before && Environment.TickCount64 - waitStart < 3000)
            {
                Application.DoEvents();
                System.Threading.Thread.Sleep(5);
            }
            long settles = rig.Canvas.SettlePaintsForTest - before;
            Battery.Evidence($"{notches} 格（{burstMs}ms 内打完）→ 停手后补 {settles} 帧");
            return settles;
        }

        long one = SettleAfter(1);
        long five = SettleAfter(5);
        Battery.Assert(one == 1,
            $"一格滚轮之后必须补一帧；撤掉补帧机制这里读到 0，判据当场红（实际 {one}）");
        Battery.AssertEqual(one, five,
            "补帧次数不许随滚轮格数增长；按格排的写法会把五格读成 5 帧，判据当场红");
    }

    private sealed class Rig : IDisposable
    {
        public required StitchCanvasControl Canvas { get; init; }
        public required Project P { get; init; }
        public required CanvasComposer Composer { get; init; }
        public required MemoryResolver Resolver { get; init; }

        public Seam Current => P.Doc.Seams[0];
        public SeamGeometry Geometry => P.Layout.DescribeSeam(Current)!;
        public int Revision => P.Revision;
        public IntPoint TopLeft => Canvas.Viewport.TopLeft;
        public int CanvasHeight => P.Layout.CanvasSize.Height;

        public void Dispose() => Canvas.Dispose();
    }

    /// <summary>
    /// 建一份异本夹具（后一段的带内那一列涂标记）。
    /// 为什么必须异本——同本样本里"换蓝线/换对齐"改的是用哪一份副本，而两份副本逐字节相同，
    /// 于是"带内变了、带外没变"这件事判不出来（第九轮同一课）。
    /// </summary>
    private static Rig NewRig(double zoom = 1.0, IntPoint? origin = null)
    {
        RgbaImage page = LongScrollTests.StripedPage(Width, Height + Height - Overlap);
        (RgbaImage before, RgbaImage after, int expectedOverlap) =
            TestProject.CutScrollPair(page, firstTop: 0, firstHeight: Height, overlap: Overlap);
        Battery.AssertEqual(Overlap, expectedOverlap, "夹具自证：切出来的重叠就是 120 行");
        for (int row = 0; row < Math.Min(after.Height, Overlap + ExtraMarkerRows); row++)
            after.Fill(new IntRect(0, row, 1, 1), Marker);
        Battery.Assert(!before.Row(Height - 1).SequenceEqual(after.Row(Overlap - 1)),
            "夹具自证：两份副本必须能分辨，否则带内外比对全是空转");

        (Project p, MemoryResolver r, _) = TestProject.Create(StitchMode.LongScroll, before, after);
        p.Mutate("设定重叠", _ =>
        {
            Seam seam = p.Doc.Seams[0];
            seam.AutoOffset = new IntPoint(0, BandTopY);
            seam.SetOverlap(Overlap);
            seam.Analyzed = true;
        });

        var canvas = new StitchCanvasControl(p, new CanvasComposer(r)) { Size = new Size(400, 500) };
        canvas.Viewport.SetOneToOne();
        canvas.Viewport.SetZoom(zoom);
        canvas.Viewport.MoveTopLeftTo(origin ?? IntPoint.Zero);
        canvas.SelectSeam(p.Doc.Seams[0].Id);
        return new Rig { Canvas = canvas, P = p, Composer = new CanvasComposer(r), Resolver = r };
    }

    /// <summary>把蓝线写成参数（模拟"上一次已经拖过"）。</summary>
    private static void SetCut(Rig rig, int cut) =>
        rig.P.Mutate("预设蓝线", _ => rig.Current.ApplyCut(cut));

    /// <summary>蓝线在设备坐标里的那一行（1:1、视口原点 0 时就是画布行号）。</summary>
    private static int CutLineY(Rig rig) => rig.Geometry.CutYCanvas;

    /// <summary>
    /// 画布行号 → 设备行号。夹具里视口原点经常不是 0，把画布行号直接当设备坐标用会点到画面外
    /// （本轮一开始就踩了一次：100 次移动那格预览帧数恒为 0，因为按下点根本不在带里）。
    /// </summary>
    private static int ToDeviceY(Rig rig, int canvasY) =>
        rig.Canvas.Viewport.CanvasToDevice(new IntRect(0, canvasY, 1, 1)).Y;

    /// <summary>带内部（离蓝线与两条带边都足够远）的一个设备点。
    /// 第十四轮末次起它按下去进的是<b>滑动</b>手势：抓的是蓝线下方那张图（点在线下方）。</summary>
    /// <summary>
    /// 带内部、蓝线<b>上方</b>的一个设备点 —— 按它抓的是上方那张图，
    /// 于是屏上钉住的是下方那张（旧口径里叫「固定后一段」）。
    /// 取带首与蓝线的中点：既要避开骑在蓝线上的「调整」把手（纵向各出 10 设备像素），
    /// 也要避开带首那条红箭头把手。
    /// 横坐标取<b>带自己的正中</b>：小缩放档下带可能只有十几个设备像素宽，
    /// 写死一个 x 会点到带外去（那连命中都算不上，整格就成恒真判据）。
    /// </summary>
    private static Point BandAboveLinePoint(Rig rig) =>
        new(BandCentreX(rig), ToDeviceY(rig, (BandTopY + CutLineY(rig)) / 2));

    /// <summary>带内部、蓝线<b>下方</b>的一个设备点 —— 按它抓的是下方那张图。</summary>
    private static Point BandInteriorPoint(Rig rig) =>
        new(BandCentreX(rig), ToDeviceY(rig, (CutLineY(rig) + BandBottomY) / 2));

    /// <summary>这条缝的重叠带在设备坐标里的正中那一列（两个"带里"取点共用）。</summary>
    private static int BandCentreX(Rig rig)
    {
        Rectangle band = rig.Canvas.Viewport.CanvasToDevice(rig.Geometry.OverlapRect);
        return band.X + Math.Max(1, band.Width / 2);
    }

    /// <summary>蓝线上的一个设备点（横坐标取正中）。
    /// 注意：正中那一点现在压在「调整」把手上，命中的是 AdjustHandle 而不是 CutLine。</summary>
    private static Point OnCutLinePoint(Rig rig) => new(HandleRect(rig).Right + 4, ToDeviceY(rig, CutLineY(rig)));

    /// <summary>这条缝的「调整」把手在设备像素里的矩形（判据自己复述尺寸就等于什么都没量）。</summary>
    private static Rectangle HandleRect(Rig rig) => rig.Canvas.AdjustHandleDeviceRect(rig.Current.Id);

    /// <summary>
    /// 蓝线上、但<b>避开</b>那枚「调整」把手的一个设备点。
    /// 把手就骑在线正中：不避开的话量到的是把手那一档（它只开关微调模式、不开拖动手势），
    /// 后面那些"拖了多少行"的断言就会全读到 0，红得莫名其妙。
    /// </summary>
    private static Point OnCutLineOffHandle(Rig rig)
    {
        Rectangle handle = HandleRect(rig);
        return new Point(handle.Right + 4, ToDeviceY(rig, CutLineY(rig)));
    }

    // ───────────────────────── ① 公共坐标（需求二、四、五、九） ─────────────────────────

    /// <summary>
    /// 需求二与九：带必须就是<b>两个完整矩形的交</b>，而且不随蓝线位置变。
    /// 这一条是本轮最结构性的断言：它把"带"从"两个凭空的裁切数摆出来的区间"
    /// 变成"公共坐标里本来就重叠的那一段"。
    /// </summary>
    private static void BandIsIntersectionOfFullRects()
    {
        using Rig rig = NewRig();
        foreach (int cut in new[] { 0, 40, Overlap })
        {
            SetCut(rig, cut);
            SeamGeometry g = rig.Geometry;

            Battery.AssertEqual(ExpectedBand, g.OverlapRect, $"CutY={cut} 时带的位置");
            Battery.AssertEqual(ExpectedBand, g.BandRect, "BandRect 就是 OverlapRect（同一个出处）");
            Battery.AssertEqual(BandTopY, g.OverlapStartY, "带首 = Rect2.Top");
            Battery.AssertEqual(BandBottomY, g.OverlapEndY, "带尾 = Rect1.Bottom");
            Battery.AssertEqual(Overlap, g.OverlapRect.Height, "带高 = 重叠行数");
            Battery.AssertEqual(0, g.Rect1.X, "Rect1 与 Rect2 同列（长截图锁横向）");
            Battery.AssertEqual(0, g.Rect2.X, "Rect2 的 X 也必须是 0");
            Battery.AssertEqual(Height, g.Rect1.Height, "Rect1 是<b>完整</b>的图1（600 行），不是裁过的");
            Battery.AssertEqual(Height, g.Rect2.Height, "Rect2 是完整的图2（600 行）");
            Battery.Assert(g.Rect1.Intersects(g.Rect2), "两个完整矩形必须相交——那个交就是带");
            Battery.Assert(g.CutInsideBand, $"CutY={cut} 时蓝线要落在带里");
            Battery.AssertEqual(BandTopY + cut, g.CutYCanvas, "蓝线的绝对位置 = 带首 + 带内行号");
        }
        Battery.Evidence("带 = Rect1∩Rect2 = [480,600) 恒不变；蓝线在 480+CutY；两个完整矩形仍相交");
    }

    /// <summary>
    /// 需求四那句"不要再把 b + t = O 当输入"的可执行版本：
    /// 两侧裁切量必须<b>就是蓝线两侧的分界位置</b>，也就是
    /// <c>t = CutY − OverlapRect.Top</c>、<c>b = OverlapRect.Bottom − CutY</c>。
    /// 只要这两条成立，b+t=O 就不再需要有人去守。
    /// </summary>
    private static void CropsAreDerivedFromCutY()
    {
        using Rig rig = NewRig();
        foreach (int cut in new[] { 0, 1, 40, Overlap - 1, Overlap })
        {
            SetCut(rig, cut);
            SeamGeometry g = rig.Geometry;
            Battery.AssertEqual(cut, g.AppliedAfterCrop, $"带里由图1 提供的行数 = 蓝线到带首的距离（CutY={cut}）");
            Battery.AssertEqual(BandBottomY - (BandTopY + cut), g.AppliedBeforeCrop,
                $"带里由图2 提供的行数 = 蓝线到带尾的距离（CutY={cut}）");
            Battery.Assert(g.CropInvariantHolds, "两侧之和不再等于重叠——那说明有人又把它当输入了");
            Battery.AssertEqual(Overlap, g.Overlap, "去掉的行数恒等于重叠");
            // 源矩形也要能指回蓝线：图1 的底边就在蓝线，图2 的顶边也在蓝线
            Battery.AssertEqual(g.CutYCanvas - g.Rect1.Y, g.BeforeSourceRect.Height,
                "sourceRect1 的高度 = Rect1 顶边到蓝线");
            Battery.AssertEqual(g.Rect2.Bottom - g.CutYCanvas, g.AfterSourceRect.Height,
                "sourceRect2 的高度 = 蓝线到 Rect2 底边");
        }
        Battery.Evidence("t = CutY、b = O − CutY、b+t=O；两个源矩形的高各自指到蓝线那一行");
    }

    // ───────────────────────── ② 命中三档 → 两个手势（需求九） ─────────────────────────

    /// <summary>
    /// 需求九：命中测试要能报出 <c>CutLine / AlignmentAdjust / OverlapBand</c> 三档，
    /// 而且第一档进蓝线手势、后两档进对齐手势。这里量的就是"按下点落在哪一档"，
    /// 并顺手钉住"带外一律不命中"（否则又是"选中之后画布推不动"那个缺陷）。
    /// </summary>
    private static void ThreeHitKindsRouteToTwoGestures()
    {
        using Rig rig = NewRig();
        SetCut(rig, 40);                       // 蓝线在 520，带 [480,600)：四档都摆得开
        int line = CutLineY(rig);
        Rectangle handle = HandleRect(rig);
        Battery.Assert(handle.Width > 0 && handle.Height > 0,
            $"夹具自证：这条缝已落位，把手该有矩形，实际 {handle}");

        // ① 把手优先于线本身：它就骑在线上，不先判它的话"点把手开微调"按了也不算按到
        Battery.Assert(rig.Canvas.HitTestSeamEditRegion(
                new Point(handle.X + handle.Width / 2, handle.Y + handle.Height / 2)) is
            { Kind: StitchCanvasControl.SeamHitKind.AdjustHandle }, "蓝线正中那枚「调整」把手必须优先命中");
        // ② 线上、避开把手 ⇒ 仍是线命中（容差 9 设备像素那一档没变）
        Point offHandle = OnCutLineOffHandle(rig);
        Battery.Assert(rig.Canvas.HitTestSeamEditRegion(offHandle) is
            { Kind: StitchCanvasControl.SeamHitKind.CutLine }, "压在蓝线上（避开把手）必须报 CutLine");
        Battery.Assert(rig.Canvas.HitTestSeamEditRegion(new Point(offHandle.X, line + 9)) is
            { Kind: StitchCanvasControl.SeamHitKind.CutLine }, "蓝线容差 9 设备像素内仍算线命中");
        Battery.Assert(rig.Canvas.HitTestSeamEditRegion(new Point(30, BandTopY + 3)) is
            { Kind: StitchCanvasControl.SeamHitKind.AlignmentAdjust }, "带首那条边是红箭头把手");
        Battery.Assert(rig.Canvas.HitTestSeamEditRegion(new Point(30, BandBottomY - 3)) is
            { Kind: StitchCanvasControl.SeamHitKind.AlignmentAdjust }, "带尾那条边也是红箭头把手");
        Battery.Assert(rig.Canvas.HitTestSeamEditRegion(BandInteriorPoint(rig)) is
            { Kind: StitchCanvasControl.SeamHitKind.OverlapBand }, "带里其余处报重叠带命中");

        // ═══ 四档命中，但手势只有一档：滑动上方或下方那张图，蓝线钉在原地 ═══
        // 压着蓝线拖 = 抓下方那张（边界下面那一张跟着手走，这是唯一不歧义的读法）。
        (int cut, int overlap) = DragAndRead(rig, offHandle, 20);
        Battery.AssertEqual(Overlap - 20, overlap, "压着蓝线往下拖 20 行 ⇒ 下方那张跟着下移，重叠少 20 行");
        Battery.AssertEqual(20, cut, "同一路径上带内行号也少 20 行（40→20）：两者对消才钉得住线");
        Battery.AssertEqual(line, CutLineY(rig),
            $"蓝线的画布行号必须一字不动（{line}），否则这一档就又把线拖走了");

        // 带里（蓝线下方）往上拖 = 同样抓下方那张，方向反过来
        (cut, overlap) = DragAndRead(rig, BandInteriorPoint(rig), -20);
        Battery.AssertEqual(Overlap, overlap, "往上拖 20 行把刚才那 20 行还回来：重叠回到 120");
        Battery.AssertEqual(40, cut, "带内行号也回到 40");
        Battery.AssertEqual(line, CutLineY(rig), "来回一趟之后蓝线仍在原行");

        // 带里（蓝线上方）往下拖 = 抓上方那张：重叠变多、带内行号不动，
        // 蓝线在画布里往上走，靠视口补偿钉在屏幕原地（那一头由「固定后一段」那两格量）。
        Point aboveLine = new(30, ToDeviceY(rig, (BandTopY + CutLineY(rig)) / 2));
        Battery.Assert(rig.Canvas.HitTestSeamEditRegion(aboveLine) is
            { Kind: StitchCanvasControl.SeamHitKind.OverlapBand, Side: FixedSide.Before },
            "夹具自证：蓝线上方的带内点要报 Before 侧，否则下面这趟量的不是抓上方那张");
        (cut, overlap) = DragAndRead(rig, aboveLine, 20);
        Battery.AssertEqual(Overlap + 20, overlap, "抓上方那张往下拖 20 行 ⇒ 两张图多压 20 行");
        Battery.AssertEqual(40, cut, "抓上方那张时带内行号不动（动的是画布行号与视口补偿）");
        Battery.AssertEqual(line - 20, CutLineY(rig), "蓝线在画布坐标里往上走 20 行——屏幕那一头由补偿钉住");
        Battery.Evidence("AdjustHandle→开关微调；CutLine/AlignmentAdjust/OverlapBand→同一档滑动手势，" +
                         "抓下方那张时蓝线画布行号恒不变，抓上方那张时靠视口补偿钉住屏幕行号");
    }

    /// <summary>
    /// 拖出去再原样拖回来（净位移 0）：这一趟<b>不该</b>产生任何改动或撤销记录。
    /// 写成"按下→移动 +d→移动回起点→在起点松手"，而不是"从新位置再反向拖 d"——
    /// 后者是<b>另一次真实改动</b>，拿它当"拖回原位"会把判据读反（本轮一开始就踩了这个坑）。
    /// </summary>
    private static void DragBackToStart(Rig rig, Point start, int outDeltaY)
    {
        rig.Canvas.MousePress(MouseButtons.Left, Keys.None, start);
        rig.Canvas.MouseMoveTo(new Point(start.X, start.Y + outDeltaY));
        rig.Canvas.MouseMoveTo(new Point(start.X, start.Y));
        rig.Canvas.MouseRelease(new Point(start.X, start.Y));
    }

    /// <summary>按下→拖→松手一整趟，回来读参数里的蓝线与对齐各是多少。</summary>
    private static (int Cut, int Overlap) DragAndRead(Rig rig, Point start, int deviceDeltaY)
    {
        rig.Canvas.MousePress(MouseButtons.Left, Keys.None, start);
        rig.Canvas.MouseMoveTo(new Point(start.X, start.Y + deviceDeltaY / 2));
        rig.Canvas.MouseMoveTo(new Point(start.X, start.Y + deviceDeltaY));
        rig.Canvas.MouseRelease(new Point(start.X, start.Y + deviceDeltaY));
        return (rig.Current.CutPosition, rig.Current.OverlapLength);
    }

    /// <summary>
    /// 需求一（第十一轮那一格搬家到这里）：裁切档里两段可见矩形必然不相交，
    /// 但红框里每一行仍然要能点中这条缝。两件事在同一条格子里量。
    /// </summary>
    private static void BandHittableWhileRectsDisjoint()
    {
        using Rig rig = NewRig();
        SetCut(rig, 40);
        SeamGeometry g = rig.Geometry;

        Battery.Assert(g.RectsDisjoint, "夹具没做出裁后可见矩形不相交的形状：" + g);
        Battery.Assert(!g.BeforeRect.Intersects(g.AfterRect), "两段可见矩形仍在相交，夹具不对");
        Battery.AssertEqual(ExpectedBand, g.BandRect, "带区位置不随蓝线移动（它就是两个完整矩形的交）");

        int bandHits = 0, handleHits = 0;
        for (int y = g.BandRect.Y; y < g.BandRect.Bottom; y++)
        {
            StitchCanvasControl.SeamHit? hit = rig.Canvas.HitTestSeamEditRegion(new Point(30, y));
            Battery.Assert(hit?.SeamId == rig.Current.Id, $"带内 y={y} 点不中这条缝（带={g.BandRect}）");
            switch (hit?.Kind)
            {
                case StitchCanvasControl.SeamHitKind.OverlapBand: bandHits++; break;
                case StitchCanvasControl.SeamHitKind.AlignmentAdjust: handleHits++; break;
            }
        }
        // 线容差吃 19 行、两条把手各吃 10 行：剩下的必须真的报带命中（不是"全都算把手"）
        Battery.Assert(bandHits > Overlap - 60,
            $"带里大部分行要报「重叠带」命中，实际 {bandHits}/{Overlap}（把手 {handleHits}）");
        Battery.Assert(handleHits is > 0 and < 40,
            $"带边两条红箭头把手要真有可点厚度，实际 {handleHits} 行");

        Battery.Assert(rig.Canvas.HitTestSeamEditRegion(new Point(30, g.BandRect.Bottom + 30)) is null,
            "带外（后一段自己的画面里）不该命中拼缝，否则又变成拖不动画布");
        Battery.Assert(rig.Canvas.HitTestSeamEditRegion(new Point(30, g.BandRect.Y - 40)) is null,
            "带外（前一段自己的画面里）不该命中拼缝");

        IntRect oldWay = g.BeforeRect.Intersect(g.AfterRect);
        Battery.Assert(oldWay.IsEmpty, "对照自证：按裁后两矩形取交算出来是空集，那正是需求一点名的形状");
        Battery.Evidence($"带 {g.BandRect} 内 {Overlap} 行全部命中（带 {bandHits} + 把手 {handleHits}）；" +
                         "可见矩形不相交；旧算法的交集为空集");
    }

    /// <summary>
    /// 需求七.6：三档缩放下红框的显示矩形与命中矩形必须同源，
    /// 设备像素→源像素的折算仍按缩放比走。每一档都带一个非零视口原点，
    /// 顺带钉住"平移之后蓝线不许停在旧位置"。
    /// </summary>
    private static void HitAndDragMatchDrawingAtThreeZooms()
    {
        foreach ((double zoom, IntPoint origin) in new[]
                 {
                     (0.3, new IntPoint(0, 420)),
                     (0.5, new IntPoint(0, 400)),
                     (1.0, new IntPoint(0, 300)),
                 })
        {
            using Rig rig = NewRig(zoom, origin);
            SetCut(rig, 30);
            SeamGeometry g = rig.Geometry;
            Rectangle drawn = rig.Canvas.Viewport.CanvasToDevice(g.BandRect);      // 画出来的那一块
            // 取显示矩形的水平中心：30% 缩放下 60 宽的图只剩 18 设备像素，
            // 再照 1:1 那样点 X=30 就点到图外面去了——那不是命中区错，是点本来就在画面外。
            int insideX = drawn.X + Math.Max(1, drawn.Width / 2);

            // 蓝线的显示位置：按它才走线那一档。「调整」把手骑在线正中，
            // 所以要各来一次：把手正中报 AdjustHandle，避开把手的线上点报 CutLine。
            Rectangle handle = rig.Canvas.AdjustHandleDeviceRect(rig.Current.Id);
            var onHandle = new Point(handle.X + handle.Width / 2, handle.Y + handle.Height / 2);
            Battery.Assert(rig.Canvas.HitTestSeamEditRegion(onHandle) is
                { Kind: StitchCanvasControl.SeamHitKind.AdjustHandle },
                $"zoom={zoom} 把手正中 {onHandle} 没命中把手（把手 {handle}、带显示 {drawn}）");
            var onLine = new Point(handle.Right + 4, (int)Math.Round((g.CutYCanvas - origin.Y) * zoom));
            Battery.Assert(rig.Canvas.Viewport.DeviceToCanvas(onLine).Y is int backY
                           && Math.Abs(backY - g.CutYCanvas) <= 1,
                $"zoom={zoom} 夹具自证：这个设备点折算回画布要落在蓝线附近（{backY} vs {g.CutYCanvas}）");
            Battery.Assert(rig.Canvas.HitTestSeamEditRegion(onLine) is
                { Kind: StitchCanvasControl.SeamHitKind.CutLine },
                $"zoom={zoom} 显示蓝线上的点 {onLine} 没命中蓝线（显示 {drawn}、把手 {handle}）");

            var outside = new Point(insideX, drawn.Bottom + 20);
            Battery.Assert(rig.Canvas.HitTestSeamEditRegion(outside) is null,
                $"zoom={zoom} 显示矩形之外还命中，说明命中区比画出来的红框大");

            int cutBefore = rig.Current.CutPosition;
            int lineBefore = g.CutYCanvas;
            int sourceDelta = (int)Math.Round(20 / zoom);
            DragAndRead(rig, onLine, 20);
            // 折算仍按 round(deviceDelta / Zoom)；这一档往下拖 = 抓下方那张，
            // 重叠与带内行号同减，减到 0 就停（被夹掉的行数由界面出声，不静默吞掉）。
            Battery.AssertEqual(Math.Max(0, cutBefore - sourceDelta), rig.Current.CutPosition,
                $"zoom={zoom} 拖 20 设备像素应折成 {sourceDelta} 源图行（round(deviceDelta / Zoom)）");
            Battery.AssertEqual(Overlap - (cutBefore - rig.Current.CutPosition), rig.Current.OverlapLength,
                $"zoom={zoom} 带内行号减了多少，重叠就要减多少");
            Battery.AssertEqual(lineBefore, rig.Geometry.CutYCanvas,
                $"zoom={zoom} 滑动之后蓝线的画布行号必须仍是 {lineBefore}（线不动、图动）");
            Battery.Evidence($"zoom={zoom} 原点={origin}：显示与命中同为 {drawn}，20px → {sourceDelta} 行，" +
                             $"带内行号 {cutBefore}→{rig.Current.CutPosition}、蓝线画布行号恒为 {lineBefore}");
        }
    }

    // ───────────────────────── ③ 蓝线手势（需求四、七.2） ─────────────────────────

    /// <summary>
    /// 第十四轮末次交互重设计：压着<b>蓝线</b>往下拖 20 个设备像素 = 抓住下方那张图往下挪 20 行。
    ///
    /// 要改的是重叠 O 与带内行号 Cut（两者同减，在 <c>H1 − O + Cut</c> 里对消）；
    /// 不许动的是<b>蓝线在画布里的行号</b>、视口、图片横向位置、以及带外那一段落位的相对关系。
    /// 画布总高<b>会</b>变（重叠少 20 行就高 20 行）——那是这一档的物理结果，
    /// 不是"偷偷改了别的量"：判据要钉的是"线没动"，不是"什么都没动"。
    /// </summary>
    private static void LineDragChangesCutOnly()
    {
        using Rig rig = NewRig();
        SetCut(rig, 40);
        Seam seam = rig.Current;
        int revision = rig.Revision;
        IntPoint topLeftBefore = rig.TopLeft;
        int heightBefore = rig.CanvasHeight;
        int lineBefore = CutLineY(rig);
        int firstOriginBefore = rig.P.Layout.Placed[0].Origin.Y;
        Point start = OnCutLineOffHandle(rig);

        Battery.Assert(rig.Canvas.HitTestSeamEditRegion(start) is
            { Kind: StitchCanvasControl.SeamHitKind.CutLine }, "按下点必须在蓝线上（避开把手），否则这格量的不是滑动手势");

        rig.Canvas.MousePress(MouseButtons.Left, Keys.None, start);
        rig.Canvas.MouseMoveTo(new Point(start.X, start.Y + 10));
        rig.Canvas.MouseMoveTo(new Point(start.X, start.Y + 20));
        Battery.AssertEqual(40, seam.CutPosition, "拖动过程中不许写参数（松手才提交，需求六.6）");
        Battery.AssertEqual(revision, rig.Revision, "预览期间不许有修订号变化");

        rig.Canvas.MouseRelease(new Point(start.X, start.Y + 20));

        Battery.AssertEqual(Overlap - 20, seam.OverlapLength, "往下拖 20 行 ⇒ 抓的下方那张下移，重叠少 20 行");
        Battery.AssertEqual(20, seam.CutPosition, "带内行号同减 20（40→20）：这一对必须同增同减才钉得住线");
        Battery.AssertEqual(20, seam.AfterCropTop, "AfterCropTop = 蓝线");
        Battery.AssertEqual(seam.OverlapLength - 20, seam.BeforeCropBottom, "BeforeCropBottom = O − 蓝线");
        Battery.Assert(seam.CropInvariantHolds, "不变式破了");
        Battery.AssertEqual(lineBefore, CutLineY(rig),
            $"蓝线的画布行号必须一字不动（{lineBefore}）——这一档的全部意义就在那一行");
        Battery.AssertEqual(heightBefore + 20, rig.CanvasHeight, "重叠少 20 行 ⇒ 画布总高多 20 行");
        Battery.AssertEqual(topLeftBefore, rig.TopLeft,
            "抓下方那张时视口一个像素都不许动：画布坐标里线本来就没动，再去补偿就是偷偷挪屏");
        Battery.AssertEqual(revision + 1, rig.Revision, "整趟拖动只记一条改动");
        Battery.AssertEqual("滑动图片", rig.P.NextUndoLabel, "撤销栈顶就是这次滑动");
        Battery.AssertEqual(firstOriginBefore, rig.P.Layout.Placed[0].Origin.Y, "前一段原点不动");
        Battery.AssertEqual(0, rig.P.Layout.Placed[1].Origin.X, "图片 X 恒为 0");
        Battery.AssertEqual(FixedSide.Before, seam.Fixed,
            "固定侧是手势的<b>结论</b>：抓的是下方那张 ⇒ 屏上钉住的是上方那张");
        Battery.Assert(rig.Canvas.LastSeamEditTraceForTest is { } trace
                       && !trace.LineDeviceMoved
                       && trace.LineCanvasRowBefore == trace.LineCanvasRowAfter
                       && trace.Grabbed == FixedSide.After
                       && trace.EventPrefix == "seam.slide" && trace.Mutated && trace.UndoPushed,
            "日志要把这一档说清：line_device_moved=false、grabbed=After、前缀 seam.slide、真的写了参数与撤销");

        string? labelBefore = rig.P.NextUndoLabel;
        DragBackToStart(rig, start, 20);        // 拖出去再原样拖回来：净位移 0
        Battery.AssertEqual(revision + 1, rig.Revision, "拖回原位不该再多记一条改动");
        Battery.AssertEqual(labelBefore, rig.P.NextUndoLabel, "拖回原位不许留空撤销记录");
        Battery.Evidence($"压线拖 20px：O {Overlap}→{seam.OverlapLength}、Cut 40→{seam.CutPosition}、" +
                         $"蓝线画布行号恒为 {lineBefore}、总高 {heightBefore}→{rig.CanvasHeight}、" +
                         $"视口 {topLeftBefore} 未变；Revision 只 +1");
    }

    /// <summary>
    /// 需求八最后那段：调试层（重影/绿黄框/箭头）关掉之后，
    /// 对齐、蓝线、导出字节、命中区域四样一字不变。
    /// </summary>
    private static void DebugLayerOffChangesNothingButPixels()
    {
        using Rig rig = NewRig();
        SetCut(rig, 20);

        byte[] withOverlay = CompositeHash(rig);
        IntPoint alignmentBefore = rig.Current.AlignmentOffset;
        int cutBefore = rig.Current.CutPosition;
        var probe = BandInteriorPoint(rig);
        StitchCanvasControl.SeamHit? hitBefore = rig.Canvas.HitTestSeamEditRegion(probe);

        rig.Canvas.ShowCompareOverlay = false;
        rig.Canvas.ShowDifference = false;

        Battery.AssertEqual(alignmentBefore, rig.Current.AlignmentOffset, "关掉调试层不许改对齐");
        Battery.AssertEqual(cutBefore, rig.Current.CutPosition, "关掉调试层不许改蓝线");
        Battery.AssertBytesEqual(withOverlay, CompositeHash(rig), "关掉调试层之后的成品像素");
        Battery.AssertEqual(hitBefore, rig.Canvas.HitTestSeamEditRegion(probe),
            "关掉调试层不许改命中区域（那一层不参与命中）");

        // 关着调试层，滑动手势的两个入口仍然要照常工作（线上一次、带里一次）
        int lineBeforeDrag = CutLineY(rig);
        int overlapBeforeDrag = rig.Current.OverlapLength;
        int cutBeforeDrag = rig.Current.CutPosition;
        (int cut, int overlap) = DragAndRead(rig, OnCutLineOffHandle(rig), 10);
        Battery.AssertEqual(cutBeforeDrag - 10, cut, "关着调试层也要能压着蓝线滑动下方那张图（带内行号同减）");
        Battery.AssertEqual(overlapBeforeDrag - 10, overlap, "重叠同减 10 行：这一对必须一起动，线才钉得住");
        Battery.AssertEqual(lineBeforeDrag, CutLineY(rig), "关着调试层，蓝线行号照样一字不动");

        int overlapMid = rig.Current.OverlapLength;
        (_, int overlap2) = DragAndRead(rig, BandInteriorPoint(rig), -20);
        Battery.AssertEqual(overlapMid + 20, overlap2, "关着调试层也要能在带里往上滑动");

        // ═══ 需求六的 F8 读数层是同一种"只画在屏幕上"的层，这一格顺手把它也管上 ═══
        // 它比"关掉不改东西"多出的一条是<b>正向对照</b>：打开之后屏幕帧必须有像素变化，
        // 否则"这一层只影响显示"可能只是"这一层压根什么都没画"。
        // 基准要<b>重新取</b>：上面那两趟拖动手势本来就故意改了参数，拿旧基准比会红得没有道理。
        StitchCanvasControl hudCanvas = rig.Canvas;
        hudCanvas.CreateControl();
        IntPoint hudAlignmentBefore = rig.Current.AlignmentOffset;
        int hudCutBefore = rig.Current.CutPosition;
        int hudRevisionBefore = rig.P.Revision;
        byte[] hudComposite = CompositeHash(rig);
        StitchCanvasControl.SeamHit? hudHit = hudCanvas.HitTestSeamEditRegion(probe);
        using var withoutHud = new System.Drawing.Bitmap(400, 500);
        hudCanvas.RenderFrameForTest(withoutHud);
        hudCanvas.ShowDebugHud = true;
        using var withHud = new System.Drawing.Bitmap(400, 500);
        hudCanvas.RenderFrameForTest(withHud);

        Battery.AssertEqual(hudAlignmentBefore, rig.Current.AlignmentOffset, "打开 F8 读数层不许改对齐");
        Battery.AssertEqual(hudCutBefore, rig.Current.CutPosition, "打开 F8 读数层不许改蓝线");
        Battery.AssertEqual(hudRevisionBefore, rig.Revision, "打开 F8 读数层不许产生任何可撤销的改动");
        Battery.AssertBytesEqual(hudComposite, CompositeHash(rig), "打开 F8 读数层之后的成品像素仍与关掉时逐字节相同");
        Battery.AssertEqual(hudHit, hudCanvas.HitTestSeamEditRegion(probe), "打开 F8 读数层不许改命中区");
        int changed = CountDifferentPixels(withoutHud, withHud);
        Battery.Assert(changed > 0,
            $"F8 那层必须真的往屏幕上画出东西来（不然上面那句「只影响显示」是空判据），取样到不同的像素 {changed} 个");
        hudCanvas.ShowDebugHud = false;
        Battery.Evidence("调试层关掉：对齐/蓝线/成品像素/命中区四项一字不变，两个手势照样能用；" +
                         $"F8 读数层打开只改屏幕像素（取样差异 {changed} 处）、修订号与成品字节一字未动");
    }

    /// <summary>两张屏幕帧里取样到的像素差异数（每 3 个像素取一枚：这条只问"有没有画东西上去"）。</summary>
    private static int CountDifferentPixels(System.Drawing.Bitmap a, System.Drawing.Bitmap b)
    {
        int n = 0;
        int h = Math.Min(a.Height, b.Height), w = Math.Min(a.Width, b.Width);
        for (int y = 0; y < h; y += 3)
            for (int x = 0; x < w; x += 3)
                if (!a.GetPixel(x, y).Equals(b.GetPixel(x, y))) n++;
        return n;
    }

    /// <summary>
    /// 整张成品的内容哈希。
    /// 这里量的是<b>合成通路</b>那一份（与导出编码进文件的字节同源，
    /// 那条等价性由常驻判据 <c>crop/预览合成与导出文件逐字节相同</c> 钉着），
    /// 因为这一格要问的是"那一层调试叠图有没有参与取数"，而不是"PNG 编码器稳不稳定"。
    /// </summary>
    private static byte[] CompositeHash(Rig rig)
    {
        CanvasLayout layout = rig.P.Layout;
        return rig.Composer.RenderWhole(layout).RegionSha256(layout.CanvasBounds);
    }

    // ───────────────────────── ④ 对齐手势（需求六、七.1） ─────────────────────────

    /// <summary>
    /// 第十四轮末次：按住带里（蓝线<b>下方</b>那一片）往上拖 20 个设备像素 = 抓住下方那张图往上挪 20 行。
    /// 带要重算、画布要变矮、预览期间不写参数、视口不许动，
    /// 而蓝线在<b>画布里的行号</b>必须一字不动——它现在也跟着一起挪，就不再是参照物了。
    /// </summary>
    private static void ArrowDragChangesAlignmentOnly()
    {
        using Rig rig = NewRig();
        SetCut(rig, 40);
        Seam seam = rig.Current;
        int revision = rig.Revision;
        IntPoint topLeftBefore = rig.TopLeft;
        int cutBefore = seam.CutPosition;
        int heightBefore = rig.CanvasHeight;
        int lineBefore = CutLineY(rig);
        Point start = BandInteriorPoint(rig);

        Battery.Assert(rig.Canvas.HitTestSeamEditRegion(start) is
            { Kind: StitchCanvasControl.SeamHitKind.OverlapBand, Side: FixedSide.After },
            "按下点必须在带里、且在蓝线下方，否则这格量的不是「抓下方那张」");

        rig.Canvas.MousePress(MouseButtons.Left, Keys.None, start);
        rig.Canvas.MouseMoveTo(new Point(start.X, start.Y - 10));
        Battery.AssertEqual(revision, rig.Revision, "预览期间不许写参数（需求六.6）");
        // 松手点必须与最后一帧移动点一致：MouseRelease 不再产生一次 MouseMove，
        // 少写这一句的话提交用的还是 -10 那一帧，测出来的"20 行"其实是 10 行。
        Battery.AssertEqual(cutBefore, seam.CutPosition, "预览期间不许改蓝线");
        rig.Canvas.MouseMoveTo(new Point(start.X, start.Y - 20));
        rig.Canvas.MouseRelease(new Point(start.X, start.Y - 20));

        Battery.AssertEqual(Overlap + 20, seam.OverlapLength,
            "抓下方那张往上拖 20 行 = 两张图多压 20 行");
        Battery.AssertEqual(-140, seam.AlignmentOffset.Y, "对齐位移 Y = −重叠（以紧接位置为零点）");
        // ═══ 蓝线不再"按相对位置换算"：它跟着带内行号一起加，绝对行号才钉得住 ═══
        // 第十三轮那一条（40/120 → 47/140，比例守恒）在本次重设计里被<b>换掉</b>了：
        // 比例守恒的代价是蓝线在用户手里自己漂走，而用户要的参照物恰恰是"线不动"。
        // 新的守恒量是画布绝对行号 H1 − O + Cut：O 与 Cut 同加 20，两者对消。
        Battery.AssertEqual(cutBefore + 20, seam.CutPosition,
            "带内行号同加 20（40→60）：与重叠同增同减，蓝线的画布行号才不动");
        Battery.AssertEqual(lineBefore, CutLineY(rig),
            $"蓝线的画布行号必须一字不动（{lineBefore}）");
        Battery.AssertEqual(FixedSide.Before, seam.Fixed, "抓下方那张 ⇒ 屏上钉住的是上方那张");
        Battery.Assert(seam.CutPosition > 0 && seam.CutPosition < seam.OverlapLength,
            "蓝线没被这趟滑动甩到带首或带尾");
        Battery.AssertEqual(BandTopY - 20, rig.Geometry.OverlapStartY, "带首跟着重叠重算（Rect2 顶边挪了）");
        Battery.AssertEqual(BandBottomY, rig.Geometry.OverlapEndY, "带尾 = Rect1 底边，不随滑动动");
        Battery.AssertEqual(heightBefore - 20, rig.CanvasHeight, "画布总高随重叠一起变");
        Battery.AssertEqual(topLeftBefore, rig.TopLeft,
            "抓下方那张时视口一动不动：画布坐标里线本来就没动");
        Battery.AssertEqual(revision + 1, rig.Revision, "整趟拖动只提交一条撤销（需求六.7）");
        Battery.AssertEqual("滑动图片", rig.P.NextUndoLabel, "撤销栈顶是这次滑动");
        Battery.Assert(seam.IsManualAlignment, "滑动要留「用户改过」的痕迹，重新分析才不敢覆盖");
        StitchCanvasControl.SeamEditTrace trace = rig.Canvas.LastSeamEditTraceForTest!;
        Battery.AssertEqual("seam.slide", trace.EventPrefix, "滑动这一族走 seam.slide.*");
        Battery.Assert(trace.AlignmentChanged && trace.CutChanged,
            "重叠与带内行号这两对 before/after 必须都变了（这一档就是同时改两个量）");
        Battery.Assert(!trace.LineDeviceMoved && trace.LineCanvasRowBefore == trace.LineCanvasRowAfter,
            $"日志里那一对新读数要证住「线没动」：画布 {trace.LineCanvasRowBefore}→{trace.LineCanvasRowAfter}、" +
            $"设备 {trace.LineDeviceBefore}→{trace.LineDeviceAfter}");
        Battery.Evidence($"带里上拖 20px：重叠 120→140、AlignmentOffset.Y −120→−140、带首 480→460、" +
                         $"画布 {heightBefore}→{rig.CanvasHeight}、带内行号 {cutBefore}→{seam.CutPosition}、" +
                         $"蓝线画布行号恒为 {lineBefore}、视口未动");
    }

    private static void ArrowKeysNudgeAlignmentOnePixel()
    {
        using Rig rig = NewRig(origin: new IntPoint(0, 300));
        SetCut(rig, 40);
        Seam seam = rig.Current;
        int overlapBefore = seam.OverlapLength;
        int cutBefore = seam.CutPosition;
        int heightBefore = rig.CanvasHeight;
        int lineBefore = CutLineY(rig);
        rig.Canvas.SelectSeam(null);
        Point band = BandInteriorPoint(rig);
        Battery.Assert(rig.Canvas.HitTestSeamEditRegion(band) is
            { Side: FixedSide.After }, "夹具自证：这个点在蓝线下方，↑ 挪的应当是下方那张图");
        rig.Canvas.MousePress(MouseButtons.Left, Keys.None, band);
        rig.Canvas.MouseRelease(band);
        Battery.AssertEqual(seam.Id, rig.Canvas.SelectedSeamId, "单击红框应进入该拼缝编辑");
        // 走控件实际的 KeyDown 分发，不直接调用微调函数。
        var key = new KeyEventArgs(Keys.Up);
        typeof(StitchCanvasControl).GetMethod("OnKeyDown",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(rig.Canvas, new object[] { key });
        Battery.Assert(key.Handled, "↑ 必须被画布消费");
        Battery.AssertEqual(overlapBefore + 1, seam.OverlapLength,
            "带里（蓝线下方）命中状态下 ↑ 把下方那张图往上挪 1 源像素 ⇒ 重叠多 1 行");
        Battery.AssertEqual(cutBefore + 1, seam.CutPosition,
            "带内行号也同加 1：这一对必须一起动，蓝线的画布行号才钉得住");
        Battery.AssertEqual(lineBefore, CutLineY(rig),
            $"按一次 ↑ 之后蓝线仍在第 {lineBefore} 行（线不动、图动）");
        Battery.AssertEqual(heightBefore - 1, rig.CanvasHeight,
            "重叠增加 1 行，画布高度应恰好减少 1 行");
        Battery.Evidence($"重叠 {overlapBefore}→{seam.OverlapLength}、带内行号 {cutBefore}→{seam.CutPosition}、" +
                         $"蓝线画布行号恒为 {lineBefore}、画布高 {heightBefore}→{rig.CanvasHeight}");
    }

    /// <summary>需求六.7 的反面：拖回原位不许留一条空撤销。</summary>
    private static void ArrowDragPushesExactlyOneUndo()
    {
        using Rig rig = NewRig();
        SetCut(rig, 30);
        int revision = rig.Revision;
        Point start = BandInteriorPoint(rig);

        DragAndRead(rig, start, 20);            // 往下拖：重叠变少
        Battery.AssertEqual(Overlap - 20, rig.Current.OverlapLength, "夹具自证：这一趟真的改了重叠");
        Battery.AssertEqual(revision + 1, rig.Revision, "一次手势一条撤销");

        string? labelBefore = rig.P.NextUndoLabel;
        DragBackToStart(rig, start, 20);        // 拖出去再原样拖回来：净位移 0
        Battery.AssertEqual(Overlap - 20, rig.Current.OverlapLength,
            "净位移 0 ⇒ 重叠停在上一趟的结果（这不算一次新改动）");
        Battery.AssertEqual(revision + 1, rig.Revision, "拖回原位不该再多记一条改动");
        Battery.AssertEqual(labelBefore, rig.P.NextUndoLabel, "拖回原位不许留空撤销记录");

        rig.P.Undo();
        Battery.AssertEqual(Overlap, rig.Current.OverlapLength,
            "撤销一次就退回起点：中间那趟净位移 0 的没进栈，所以一次撤销该直接回到 120");
        Battery.Evidence("一趟真拖动一条撤销；净位移 0 的那趟不进栈 ⇒ 一次撤销就回到起点");
    }

    /// <summary>
    /// 需求六最后那句"超出重叠区域要夹到边界并在状态栏提示"在<b>滑动手势</b>下的形状。
    ///
    /// 第十四轮末次之前这一格量的是"带缩短把蓝线挤出去"：那一档只改重叠、蓝线按比例换算，
    /// 换算完仍装不下才夹。现在这一档同时改重叠与带内行号，两者同增同减，
    /// 蓝线<b>永远不会被挤出带</b>——它压根不动。于是"夹"这件事换了个位置：
    /// <list type="bullet">
    ///   <item><b>没滑到头</b>：一声不报（换算不是夹取，报一声用户就不信提示了）；</item>
    ///   <item><b>滑到头</b>（想再往下，重叠或带内行号要变负）：参数停在边界上，
    ///         多要的那几行通过 <c>CutClamped</c> 报给界面。<b>不许静默吞掉</b>——
    ///         用户看到"图拖不动了"却不知道为什么，只会以为软件卡死。</item>
    /// </list>
    /// 两支都要量：只留后一支，"没到头也乱报一声"这个缺陷就抓不出来。
    /// </summary>
    private static void AlignmentShrinkReclampsCut()
    {
        using Rig rig = NewRig();
        SetCut(rig, 100);                       // 蓝线在带内第 100 行（带 120）⇒ 画布第 580 行
        int revision = rig.Revision;
        int lineBefore = CutLineY(rig);
        int clamped = -1;
        rig.Canvas.CutClamped += rows => clamped = rows;

        // 带尾附近、但避开那枚「调整」把手（把手骑在蓝线上，纵向各出 10 设备像素）：
        // 压在把手上量到的是"开关微调模式"，拖不动任何参数，这一格就会红得莫名其妙。
        Point start = new(30, BandBottomY - 5);
        Battery.Assert(rig.Canvas.HitTestSeamEditRegion(start) is
            { Kind: StitchCanvasControl.SeamHitKind.AlignmentAdjust or StitchCanvasControl.SeamHitKind.OverlapBand,
              Side: FixedSide.After },
            "夹具自证：这个点在蓝线下方，抓的是下方那张图");

        // ── 第一支：还没滑到头，一声不报 ──
        DragAndRead(rig, start, 60);            // 往下拖 60 ⇒ 重叠 120−60=60、带内行号 100−60=40
        Battery.AssertEqual(60, rig.Current.OverlapLength, "重叠按手势缩到 60");
        Battery.AssertEqual(40, rig.Current.CutPosition, "带内行号同减 60：100→40");
        Battery.AssertEqual(lineBefore, CutLineY(rig), $"蓝线的画布行号仍是 {lineBefore}");
        Battery.Assert(rig.Geometry.CutInsideBand, "生效蓝线必须落在带里");
        Battery.AssertEqual(-1, clamped, "没滑到头就不许报『被夹掉几行』：那种提示报多了没人信");
        Battery.AssertEqual(revision + 1, rig.Revision, "仍然只有一条撤销");

        // ── 第二支：滑到头（重叠 60、带内行号 40，再多要 20 行就要变负了）──
        int revisionBeforeSecond = rig.Revision;
        DragAndRead(rig, start, 60);            // 再往下要 60，但只滑得动 40
        Battery.AssertEqual(20, rig.Current.OverlapLength, "参数停在边界上：重叠 60−40=20");
        Battery.AssertEqual(0, rig.Current.CutPosition, "带内行号顶到 0（整带都用图2）");
        Battery.AssertEqual(lineBefore, CutLineY(rig), "滑到头蓝线也不许跑：600−20+0 仍是 580");
        Battery.Assert(rig.Geometry.CutInsideBand, "夹完仍然在带里");
        Battery.AssertEqual(20, clamped, "多要的那 20 行必须通知界面（状态栏那句提示的来源）");
        Battery.AssertEqual(revisionBeforeSecond + 1, rig.Revision, "第二趟同样只一条撤销");
        Battery.Evidence("带 120→60：重叠与带内行号同减 60（100→40），一声不报；" +
                         "再要 60 只滑得动 40 ⇒ 停在 O=20/Cut=0，多要的 20 行报给界面");
    }

    /// <summary>需求"恢复自动/重新分析不许覆盖用户"：对齐与蓝线都算用户改过的东西。</summary>
    private static void ReanalysisKeepsUserAlignment()
    {
        using Rig rig = NewRig();
        rig.Current.ApplyAutoResult(new IntPoint(0, BandTopY), SeamStatus.AutoPassed,
            SeamCause.None, null, 0.99, Overlap, new List<MatchCandidate>(), 0);
        int revision = rig.Revision;
        rig.P.Mutate("用户调对齐与蓝线", _ =>
        {
            rig.Current.ApplyAlignment(-30);    // 重叠 120→90
            rig.Current.ApplyCut(50);
        });
        Battery.AssertEqual(90, rig.Current.OverlapLength, "夹具自证：用户把重叠调成 90");
        Battery.Assert(rig.Current.HasUserEdit, "HasUserEdit 要把对齐那一头也算进去");

        // 重新分析：自动侧信息更新，但用户摆好的对齐与蓝线不许被抹掉
        rig.Current.ApplyAutoResult(new IntPoint(0, BandTopY), SeamStatus.AutoPassed,
            SeamCause.None, null, 0.99, Overlap, new List<MatchCandidate>(), 0);
        Battery.AssertEqual(90, rig.Current.OverlapLength, "重新分析不许覆盖用户调好的对齐");
        Battery.AssertEqual(50, rig.Current.CutPosition, "重新分析不许覆盖用户选的蓝线");
        Battery.AssertEqual(Overlap, rig.Current.AutoOverlapLength, "自动结论要原样留档，恢复自动才有地方可回");
        Battery.AssertEqual(0, rig.Current.AutoCutY, "自动结论的蓝线也留档");

        Battery.Assert(rig.Current.ResetToAuto(), "有自动建议时「恢复自动」该可用");
        Battery.AssertEqual(Overlap, rig.Current.OverlapLength, "恢复自动 ⇒ 回到自动结论那一格对齐");
        Battery.AssertEqual(0, rig.Current.CutPosition, "恢复自动 ⇒ 回到自动结论那一格蓝线");
        // 夹具自证：这一格里"用户改两趟"包在同一次 Mutate 里，"恢复自动"由面板那条通路
        // 自己包 Mutate（这里直接调模型），所以修订号只 +1。断言它别再往前跳。
        Battery.AssertEqual(revision + 1, rig.Revision, "只有那次 Mutate 改了修订号");
        Battery.Assert(!rig.Current.IsManualAlignment && !rig.Current.IsManualCut,
            "恢复自动之后两头的「用户改过」旗都要落下");
        Battery.Evidence("重新分析只改留档不改用户；恢复自动回到分析当时那一格（120 / 0）");
    }

    // ───────────────────────── ⑤ 性能（需求十） ─────────────────────────

    /// <summary>
    /// 需求十那条"连续 100 次 MouseMove"的对齐手势版本：
    /// 预览帧数远小于 100、整块清缓存 0 次、日志只有几行、
    /// <b>视口外的瓦片一块都不重渲染</b>（夹具里画布有 3 块瓦片、视口只看得见 2 块）。
    /// </summary>
    private static void HundredMovesDuringAlignmentDrag()
    {
        using Rig rig = NewRig(zoom: 1.0, origin: new IntPoint(0, 400));   // 屏上带 80..200
        SetCut(rig, 0);
        StitchCanvasControl canvas = rig.Canvas;
        canvas.CreateControl();
        using var frame = new System.Drawing.Bitmap(400, 500);
        canvas.RenderFrameForTest(frame);                                   // 预热：把视口内瓦片喂进缓存
        long warmRenders = canvas.TileRendersForTest;
        long warmClears = canvas.TileFullInvalidationsForTest;
        long warmLogs = DiagnosticLog.AppendRequests;
        long warmExports = LongStitch.Core.Export.ExportEngine.RunCountForTest;
        // 视口 400×500 在 1080 行的画布上只盖得住两块瓦片（画布共 3 块），
        // 于是"重渲染块数 ≤ 视口内块数"这一条真的有东西可挡——不是恒真的上限。
        int visibleTiles = (400 + 500) / TileRenderer.TileSize - 400 / TileRenderer.TileSize + 1;
        Battery.Assert(canvas.TileCountForTest > 0, "夹具自证：视口里一块瓦片都没有，那「没整块清缓存」就是空判据");
        Battery.Assert(visibleTiles < TileRenderer.TilesDown(rig.CanvasHeight),
            $"夹具自证：视口里 {visibleTiles} 块、画布共 {TileRenderer.TilesDown(rig.CanvasHeight)} 块，" +
            "否则「画布外不重渲染」这一条无从判断");

        Point start = BandInteriorPoint(rig);
        canvas.MousePress(MouseButtons.Left, Keys.None, start);
        for (int i = 1; i <= 100; i++) canvas.MouseMoveTo(new Point(start.X, start.Y - i));
        Battery.AssertEqual(0L, canvas.PreviewFramesForTest,
            "有句柄时预览必须排队：100 次移动期间一帧都不该就地做完");
        Application.DoEvents();
        Battery.Assert(canvas.PreviewFramesForTest is >= 1 and <= 2,
            $"一次消息泵最多做 1 帧预览，实际 {canvas.PreviewFramesForTest}");
        canvas.RenderFrameForTest(frame);
        long renders = canvas.TileRendersForTest;
        long clears = canvas.TileFullInvalidationsForTest;
        canvas.MouseRelease(new Point(start.X, start.Y - 100));
        Application.DoEvents();

        Battery.AssertEqual(warmClears, clears, "拖动期间瓦片缓存一次都不许整块清空（需求十.5）");
        Battery.Assert(renders - warmRenders <= visibleTiles,
            $"重渲染的瓦片不许超过视口里那 {visibleTiles} 块（画布外的像素不许重渲染），" +
            $"实际 {renders - warmRenders} 块");
        Battery.Assert(renders - warmRenders > 0,
            "正向对照：带顶往下确实要重画，否则上面那个「没清空」没意义");
        Battery.Assert(DiagnosticLog.AppendRequests - warmLogs is >= 2 and <= 12,
            $"一次手势的日志行数要远小于鼠标移动数，实际 {DiagnosticLog.AppendRequests - warmLogs}/100");
        Battery.AssertEqual(Overlap + 100, rig.Current.OverlapLength,
            "合并丢帧不许丢输入：最终重叠仍按最后那个指针位置算");
        Battery.AssertEqual(warmExports, LongStitch.Core.Export.ExportEngine.RunCountForTest,
            "需求十最后一条：UI 线程这趟拖动期间不许执行完整导出（导出计数一次都没涨才算量到）");
        Battery.Evidence($"对齐手势 100 次移动 → {canvas.PreviewFramesForTest} 帧预览、整块清 " +
                         $"{clears - warmClears} 次、重渲染 {renders - warmRenders}/{visibleTiles} 块可见瓦片、" +
                         $"日志 {DiagnosticLog.AppendRequests - warmLogs} 行");
    }

    /// <summary>
    /// 带内那个按下点，<b>从当下的布局现算</b>（与真窗口工装同一算法：带矩形 → 设备 → 取中心）。
    /// 为什么不用 <see cref="BandInteriorPoint"/>：那一格用的是带区常量推出的行号，
    /// 而"固定后一段"一改，画布里的落位就整段挪，常量推出来的点会掉到带外
    /// （这一格第一次就是这么红的——按下点没命中带 ⇒ 量的其实是平移，四条"没变"成了恒真）。
    /// </summary>
    private static Point BandPressPoint(Rig rig)
    {
        Rectangle band = rig.Canvas.Viewport.CanvasToDevice(rig.Geometry.OverlapRect);
        // 「调整」把手就骑在蓝线正中、纵向各出 10 设备像素。在 0.35 那种小缩放档下整条带才 42 设备像素，
        // 带<b>正中</b>那一点会命中把手（它只开关微调档、不开拖动手势），
        // "来回拖不改任何东西"那一格就会变成恒真的空判据——所以这里主动避开它。
        Rectangle handle = HandleRect(rig);
        // 优先取把手<b>上沿</b>再让 2 设备像素：那一点在蓝线上方 ⇒ 抓的是上方那张 ⇒ 屏上钉住下方那张，
        // 正是这两格要的"带视口补偿"那一档（把手下沿那一点抓下方那张，压根不补偿，量不到想量的东西）。
        int above = handle.Top - 2;
        int y = above > band.Y ? above : handle.Bottom + 2;
        // 带尾那条红箭头把手各占 10 设备像素，贴边会被读成 AlignmentAdjust；带太矮时退回带首往下 11 行。
        if (y >= band.Bottom - 11) y = band.Y + 11;
        return new Point(band.X + Math.Max(1, band.Width / 2), Math.Min(Math.Max(y, band.Y + 1), band.Bottom - 1));
    }

    /// <summary>
    /// 【#95】把真窗口工装那 100 次移动的<b>形状</b>原样搬到进程内，判一句用户视角的恒等式：
    /// <b>净位移 0 的一趟来回拖动，不该改动任何东西、也不该留撤销记录</b>。
    /// 为什么专门照搬形状：工装用整数除法算步长（<c>step * 10 / half</c>），所以那一百个点是
    /// 0,0,0,0,0,1,1,…,10,…,1,0——<b>大量重复坐标</b>，与电池里"两个点就走完"的写法不一样；
    /// 而真窗口 5 趟连跑读到的正是"第 1 趟重叠 120→1071、第 2~5 趟一字不动"（见精度红线 ⑧ 末段），
    /// 当时缺的就是能把"产品棘轮"与"工装命中错位"分开的数。这一格在进程内用同一套鼠标入口重放：
    /// 红了 ⇒ 产品确实在来回拖动里留了净改动；绿了 ⇒ 那笔红得由工装的读数（命中谁、起止重叠多少）解释。
    /// 缩放档与固定侧照真窗口那一档取（<b>0.35 ＋ 固定后一段</b>）——只有这一组合里"重叠"与"视口补偿"
    /// 会同时跟着同一条 delta 走，1:1 或固定前段都盖不住它（已有的"拖回原位"两格是 1:1、固定前段那一档）。
    /// </summary>
    private static void ClosedLoopDragAtSmallZoomChangesNothing()
    {
        using Rig rig = NewRig(zoom: 0.35);
        rig.P.Mutate("固定后一段", _ => rig.Current.Fixed = FixedSide.After);
        // 起点必须把蓝线摆进带里：默认 Cut=0 时"抓下方那张往下滑"一步都滑不动
        // （滑动量被 min(O,Cut) 夹成 0），这一趟就是恒真的空判据，
        // 而 guard 那一格（末点不退回去）也会跟着红成"手势压根没跑"。
        SetCut(rig, 40);
        rig.Canvas.CreateControl();
        Point start = BandPressPoint(rig);
        Battery.Assert(rig.Canvas.HitTestSeamEditRegion(start) is
            { Kind: StitchCanvasControl.SeamHitKind.OverlapBand or StitchCanvasControl.SeamHitKind.AlignmentAdjust },
            $"夹具自证：按下点得在带里（点 {start}、带 {rig.Canvas.Viewport.CanvasToDevice(rig.Geometry.OverlapRect)}、" +
            $"命中的是 {rig.Canvas.HitTestSeamEditRegion(start)?.Kind.ToString() ?? "无"}）" +
            "——否则这格量的是平移而不是滑动手势，四条断言就成了恒真");

        int overlap0 = rig.Current.OverlapLength, cut0 = rig.Current.CutPosition;
        int height0 = rig.CanvasHeight, revision0 = rig.Revision;
        IntPoint viewport0 = rig.TopLeft;

        const int moves = 100, half = 50, maxDevice = 10;
        rig.Canvas.MousePress(MouseButtons.Left, Keys.None, start);
        for (int i = 1; i <= moves; i++)
        {
            int step = i <= half ? i : moves - i + 1;         // 先下去再回来，末点等于起点
            rig.Canvas.MouseMoveTo(new Point(start.X, start.Y + step * maxDevice / half));
        }
        rig.Canvas.MouseRelease(start);

        Battery.AssertEqual(overlap0, rig.Current.OverlapLength,
            $"来回拖 {moves} 次（最大 {maxDevice} 设备像素、末点回到按下点）之后重叠没回到起始值：" +
            $"{overlap0}→{rig.Current.OverlapLength}——这就是真窗口那批读到的同一种形状，" +
            "在进程内也复现得出来 ⇒ 属产品（来回一次留下净改动），不是工装的命中错位");
        Battery.AssertEqual(cut0, rig.Current.CutPosition, "来回拖动不许把蓝线留在中间值");
        Battery.AssertEqual(height0, rig.CanvasHeight, "来回拖动不许改画布总高");
        Battery.AssertEqual(revision0, rig.Revision,
            "净位移 0 的一趟不该压进任何撤销记录（有的话用户按 Ctrl+Z 撤掉的是他自认为没做的那一下）");
        Battery.AssertEqual(viewport0, rig.TopLeft,
            "固定后一段的纯显示补偿必须回到按下那一刻的视口——补偿留在屏上就是需求一抱怨的那一类");
        Battery.Evidence($"0.35 · 固定后一段 · 来回 {moves} 次：重叠 {overlap0}→{rig.Current.OverlapLength}、" +
                         $"蓝线 {cut0}→{rig.Current.CutPosition}、画布高 {height0}→{rig.CanvasHeight}、" +
                         $"视口 {viewport0}→{rig.TopLeft}、Revision {revision0}→{rig.Revision}" +
                         "（反向对照在 guard 那一格：同一形状只在末点不退回去，重叠就必须真的动）");
    }

    /// <summary>
    /// 反证（精度红线 §3）。上一格断言的是"四样都没变"，那么<b>什么都没发生</b>的实现也能全绿——
    /// 手势压根没进对齐档（按下点没命中带、CreateControl 之前坐标换算不通……）都会是这个形状。
    /// 这一格用<b>完全相同</b>的夹具与同一条 100 次移动序列，只改一件事：末点不退回去（在 +10 设备像素处松手）。
    /// 此时重叠必须<b>真的</b>变、Revision 必须 +1——它证明上面那格的"不动"是真的守住了恒等式，
    /// 而不是那趟手势压根没跑。
    /// </summary>
    private static void ClosedLoopCellDragReallyEntersAlignmentGesture()
    {
        using Rig rig = NewRig(zoom: 0.35);
        rig.P.Mutate("固定后一段", _ => rig.Current.Fixed = FixedSide.After);
        SetCut(rig, 40);      // 与正判据那一格同一个起点：默认 Cut=0 时这一趟压根滑不动
        rig.Canvas.CreateControl();
        Point start = BandPressPoint(rig);   // 与正判据那一格同一个取点算法，两格才量的是同一趟手势
        int overlap0 = rig.Current.OverlapLength, revision0 = rig.Revision;

        const int moves = 100, half = 50, maxDevice = 10;
        rig.Canvas.MousePress(MouseButtons.Left, Keys.None, start);
        for (int i = 1; i <= moves; i++)
        {
            int step = i <= half ? i : moves - i + 1;
            rig.Canvas.MouseMoveTo(new Point(start.X, start.Y + step * maxDevice / half));
        }
        // 与正判据那一格唯一的区别：末点不退回去（净位移 +10 设备像素）。
        // 松手点必须与<b>最后一帧移动点一致</b>——MouseRelease 自己不会再产生一次 MouseMove
        // （这一课在本文件第 844 行那条注释里已经写过一次，本格第一版就是踩了它才恒红）。
        rig.Canvas.MouseMoveTo(new Point(start.X, start.Y + maxDevice));
        rig.Canvas.MouseRelease(new Point(start.X, start.Y + maxDevice));

        Battery.Assert(rig.Current.OverlapLength != overlap0,
            $"末点不退回去（+{maxDevice} 设备像素）重叠却还是 {overlap0}：" +
            $"按下命中 {rig.Canvas.HitTestSeamEditRegion(start)?.Kind.ToString() ?? "无"}、" +
            $"起点 {start}、带 {rig.Canvas.Viewport.CanvasToDevice(rig.Geometry.OverlapRect)}、" +
            $"这趟的 trace {(rig.Canvas.LastSeamEditTraceForTest is { } t ? $"设备位移 ({t.DeviceDelta.X},{t.DeviceDelta.Y}) 对齐变了 {t.AlignmentChanged}" : "没有")}" +
            "——这一趟手势压根没进对齐档，上一格那句『四样都没变』就是恒真的，不算证据");
        Battery.AssertEqual(revision0 + 1, rig.Revision,
            "真改动必须留下恰好一条撤销记录（留 0 条说明没提交，留多条说明每帧都在提交）");
        Battery.Evidence($"同一形状、末点不退回去：重叠 {overlap0}→{rig.Current.OverlapLength}、" +
                         $"Revision {revision0}→{rig.Revision} ⇒ 正判据那一格的『不动』是守住了恒等式，不是手势没跑");
    }

    /// <summary>
    /// 需求四第 7 条的蓝线版本（第十一轮那一格）：100 次移动只换几帧预览，
    /// 而且一帧只重渲染带区那 1–2 块瓦片。
    /// </summary>
    private static void HundredMouseMovesCoalesce()
    {
        using Rig rig = NewRig(zoom: 1.0, origin: new IntPoint(0, 400));
        // 起点把蓝线摆到带内第 100 行：默认 Cut=0 时"抓下方那张往下滑"一步都滑不动
        // （滑动量被 min(O,Cut) 夹成 0），那 100 次移动就一帧预览都换不出来，
        // 这一格会红成"合并没生效"而其实是工装没把手势跑起来。
        SetCut(rig, 100);
        StitchCanvasControl canvas = rig.Canvas;
        canvas.CreateControl();
        using var frame = new System.Drawing.Bitmap(400, 500);
        canvas.RenderFrameForTest(frame);
        long rendersWarm = canvas.TileRendersForTest;
        long clearsWarm = canvas.TileFullInvalidationsForTest;
        long logsWarm = DiagnosticLog.AppendRequests;
        long exportsWarm = LongStitch.Core.Export.ExportEngine.RunCountForTest;
        Battery.Assert(canvas.TileCountForTest > 0,
            "夹具自证：视口里一块瓦片都没有，那「没整块清缓存」就是空判据");

        Point down = OnCutLinePoint(rig);
        canvas.MousePress(MouseButtons.Left, Keys.None, down);
        for (int i = 1; i <= 100; i++) canvas.MouseMoveTo(new Point(down.X, down.Y + i));
        Battery.AssertEqual(0L, canvas.PreviewFramesForTest,
            "有句柄时预览必须排队：100 次移动期间一帧都不该就地做完（需求四的合并）");

        Application.DoEvents();
        Battery.Assert(canvas.PreviewFramesForTest is >= 1 and <= 2,
            $"一次消息泵最多做 1 帧预览（合并生效），实际 {canvas.PreviewFramesForTest}");
        canvas.RenderFrameForTest(frame);
        long rendersDuring = canvas.TileRendersForTest;
        long clearsDuring = canvas.TileFullInvalidationsForTest;

        canvas.MouseRelease(new Point(down.X, down.Y + 100));
        Application.DoEvents();

        long logged = DiagnosticLog.AppendRequests - logsWarm;
        Battery.Assert(canvas.PreviewFramesForTest < 10,
            $"100 次移动换到的预览帧数必须远小于 100，实际 {canvas.PreviewFramesForTest}");
        Battery.AssertEqual(clearsWarm, clearsDuring,
            "拖动期间瓦片缓存一次都不许整块清空（只许丢带区那几块）");
        Battery.Assert(rendersDuring > rendersWarm,
            "正向对照：带区瓦片必须真被重渲染过，否则上面那个「没清空」没意义");
        Battery.Assert(rendersDuring - rendersWarm <= 2,
            $"一帧预览最多重渲染带区那 1–2 块瓦片（视口里共 {canvas.TileCountForTest} 块），" +
            $"实际 {rendersDuring - rendersWarm} 块");
        Battery.Assert(logged is >= 2 and <= 12,
            $"一次手势的日志行数要远小于鼠标移动数（begin/commit 各一条 + 极少数 preview），实际 {logged}/100");
        Battery.AssertEqual(0, rig.Current.CutPosition,
            "合并丢帧不许丢输入：最终仍按最后那个指针位置算（带内行号 100 − 100 = 0）");
        Battery.AssertEqual(Overlap - 100, rig.Current.OverlapLength,
            "重叠也同步走到 20 行：两个量一起动，蓝线才钉得住");
        Battery.AssertEqual(exportsWarm, LongStitch.Core.Export.ExportEngine.RunCountForTest,
            "需求十最后一条：这 100 次移动期间一次导出都没跑（导出不许出现在 UI 线程的拖动路径上）");
        Battery.Evidence($"100 次移动 → {canvas.PreviewFramesForTest} 帧预览、整块清缓存 " +
                         $"{clearsDuring - clearsWarm} 次、日志 {logged} 行、" +
                         $"带区瓦片重渲染 {rendersDuring - rendersWarm} 块、导出 {LongStitch.Core.Export.ExportEngine.RunCountForTest - exportsWarm} 次");
    }

    /// <summary>
    /// 「拖动预览只丢带区那几块瓦片」的正确性依据：带外区域分别按项目布局与预览布局各渲染一次、
    /// 逐字节比；同时要求带内区域真的不同（异本样本里标记换了来源），否则整格是空判据。
    /// </summary>
    private static void OnlyBandTilesChangeDuringCutPreview()
    {
        using Rig rig = NewRig();
        SetCut(rig, 0);
        CanvasLayout projectLayout = rig.P.Layout;
        IntRect band = projectLayout.DescribeSeam(rig.Current)!.BandRect;
        Battery.AssertEqual(ExpectedBand, band, "带区位置自证");

        CanvasLayout preview = CanvasLayout.Build(rig.P.Doc, null,
            new Dictionary<string, SeamAlignment> { [rig.Current.Id] = new SeamAlignment(-Overlap, 60) });
        Battery.AssertEqual(band, preview.BandCanvasRect(rig.Current)!.Value,
            "蓝线挪了 60 行，带在画布上的位置必须一模一样（这是「只丢带区」成立的前提）");
        Battery.AssertEqual(projectLayout.CanvasSize.Height, preview.CanvasSize.Height, "画布总高不变");

        var renderer = new TileRenderer(rig.Composer);
        int tileRows = TileRenderer.TilesDown(projectLayout.CanvasSize.Height);
        int tileCols = TileRenderer.TilesAcross(projectLayout.CanvasSize.Width);
        int outsideCompared = 0, insideCompared = 0;
        bool insideDiffers = false;

        for (int ty = 0; ty < tileRows; ty++)
        {
            for (int tx = 0; tx < tileCols; tx++)
            {
                var tileRect = new IntRect(tx * TileRenderer.TileSize, ty * TileRenderer.TileSize,
                    TileRenderer.TileSize, TileRenderer.TileSize);
                bool touchesBand = !tileRect.Intersect(band).IsEmpty;
                RgbaImage fromProject = renderer.RenderTile(projectLayout, tx, ty)!;
                RgbaImage fromPreview = renderer.RenderTile(preview, tx, ty)!;
                byte[] a = fromProject.RegionSha256(fromProject.Bounds);
                byte[] b = fromPreview.RegionSha256(fromPreview.Bounds);
                if (touchesBand)
                {
                    insideCompared++;
                    if (!a.SequenceEqual(b)) insideDiffers = true;
                }
                else
                {
                    outsideCompared++;
                    Battery.AssertBytesEqual(a, b, $"带外瓦片 ({tx},{ty}) 两版布局的内容");
                }
            }
        }
        Battery.Assert(outsideCompared >= 1, "夹具自证：得真有带外瓦片可比，否则这一格什么都没量");
        Battery.Assert(insideDiffers,
            "正向对照：带内瓦片必须真的因蓝线而不同（异本标记），否则上面的「带外相同」是恒真的");
        Battery.Evidence($"带外 {outsideCompared} 块逐字节相同、带内 {insideCompared} 块确有变化 ⇒ " +
                         "预览期只丢带区瓦片是安全的");
    }

    /// <summary>
    /// ═══ 第十八改：拖<b>对齐</b>时"带外留得住"这句话<b>不成立</b>，所以作废区从带顶铺到底是最小范围 ═══
    /// 上一版 <see cref="TileCache.DropRegion"/> 的注释把蓝线那一手势的<b>高度守恒</b>
    /// 当成了两个手势共同的性质，于是 #96 被写成"缓存键落在源矩形上，带外那几块就能留住"。
    /// 这条推断可以机器核对，所以先量它：8 段链（每段 60×600、每缝重叠 120 ⇒ 画布 3960 行＝8 块瓦片行），
    /// 拖第 2 条缝，逐块比"拖之前"与"拖之后"同一画布位置的瓦片字节，并读那块瓦片顶行
    /// 在两版布局里各来自哪一段的哪一行（<c>CanvasToSource</c>，也就是内容身份键真正会算出来的那个键）。
    ///
    /// 量出来是：带<b>以上</b>那 1 块字节相同（同时这也证明比对仪器会说"同"，不是恒判"变"）；
    /// 跨带 2 块全变；带<b>以下 5 块全变，且每一块的源行号正好挪 delta 行</b>。
    /// 最后一句才是关键：<b>内容每帧都变，键也每帧都变</b>——换成源矩形身份键，命中次数照样是 0，
    /// 一块都省不下。真留得住的只有带以上那几块，而那几块<b>现在就已经留住</b>（作废区顶边就是带顶）。
    /// 所以这一格断的是两件事：① 变化的范围与作废的范围<b>一致</b>（没多丢），
    /// ② 变化确实覆盖了带以下每一块（没少丢，也说明"换键"这条路已被量死）。
    /// 反证（少丢会怎样）在 <c>guard/拖对齐若只丢带区那几块则屏上会留上一帧的像素</c>。
    /// </summary>
    private static void AlignmentDragChangesEveryTileBelowBand()
    {
        (Project p, MemoryResolver r) = NewChain(8);
        var renderer = new TileRenderer(new CanvasComposer(r));
        CanvasLayout a = p.Layout;
        Seam dragged = p.Doc.Seams[1];
        IntRect band = a.BandCanvasRect(dragged)!.Value;
        int cut = a.DescribeSeam(dragged)!.CutYCanvas - band.Y;
        int rows = TileRenderer.TilesDown(a.CanvasSize.Height);
        Battery.Assert(rows >= 6,
            $"夹具自证：画布得有 ≥6 块瓦片行，才谈得上「带以下好几块」这个形状，实际 {rows} 块");

        var detail = new System.Text.StringBuilder();
        foreach (int delta in new[] { 1, 3, 8, 40 })
        {
            CanvasLayout b = CanvasLayout.Build(p.Doc, null,
                new Dictionary<string, SeamAlignment> { [dragged.Id] = new SeamAlignment(-(Overlap + delta), cut) });
            Battery.AssertEqual(a.CanvasSize.Height - delta, b.CanvasSize.Height,
                $"拖多 {delta} 行之后的画布高（夹具自证：这一笔账不对，下面比的就不是同一个手势）");

            int aboveSame = 0, aboveDiff = 0, changedCross = 0, changedBelow = 0, shiftedBelow = 0;
            for (int ty = 0; ty < rows; ty++)
            {
                int top = ty * TileRenderer.TileSize, bottom = top + TileRenderer.TileSize;
                RgbaImage ta = renderer.RenderTile(a, 0, ty)!, tb = renderer.RenderTile(b, 0, ty)!;
                bool differs = !ta.RegionSha256(ta.Bounds).SequenceEqual(tb.RegionSha256(tb.Bounds));
                int srcA = a.CanvasToSource(new IntPoint(0, top))!.Value.Y;
                int srcB = b.CanvasToSource(new IntPoint(0, Math.Min(top, b.CanvasSize.Height - 1)))!.Value.Y;
                bool touchesBand = top < band.Bottom && bottom > band.Y;

                if (bottom <= band.Y) { if (differs) aboveDiff++; else aboveSame++; }
                else if (touchesBand) { if (differs) changedCross++; }
                else
                {
                    if (differs) changedBelow++;
                    if (srcB == srcA + delta) shiftedBelow++;
                }
            }

            Battery.Assert(aboveSame >= 1 && aboveDiff == 0,
                $"拖多 {delta} 行时带以上必须一字不变（同 {aboveSame}／异 {aboveDiff}）："
                + "异了就是作废区顶边写到了带以上，或者布局本身算错了");
            Battery.Assert(changedCross >= 1, $"拖多 {delta} 行：跨带那几块必须真的变（实际 {changedCross} 块）");
            Battery.Assert(changedBelow >= 1, $"拖多 {delta} 行：带以下必须真有变化（实际 {changedBelow} 块）");
            Battery.Assert(shiftedBelow == changedBelow,
                $"拖多 {delta} 行：带以下每块的顶行源行号都要正好挪 {delta} 行"
                + $"（挪对 {shiftedBelow}／变了 {changedBelow}）——两数不等才说明有某块内容变了但源身份没挪，"
                + "那才轮到「换成源矩形身份键」去救");

            // 产品的作废形状：从 min(带顶A, 带顶B) 一直铺到画布底（UpdatePreviewAlignment 就是这么算的）
            int areaTop = Math.Min(band.Y, b.BandCanvasRect(dragged)!.Value.Y);
            var cache = new TileCache(renderer);
            for (int ty = 0; ty < rows; ty++) cache.Get(a, 0, ty, 0);
            long hitsBefore = cache.CacheHits;
            int dropped = cache.DropRegion(new IntRect(0, areaTop,
                a.CanvasSize.Width, Math.Max(a.CanvasSize.Height, b.CanvasSize.Height) - areaTop));
            Battery.AssertEqual(rows - aboveSame, dropped,
                $"拖多 {delta} 行：这个作废形状丢掉 {dropped} 块，必须正好是跨带＋带以下那 {rows - aboveSame} 块"
                + $"（带以上留住 {aboveSame} 块）——多一块是浪费，少一块是留旧像素");
            Battery.Assert(cache.Get(b, 0, 0, 0) is not null && cache.CacheHits > hitsBefore,
                $"拖多 {delta} 行：带以上那块必须还在缓存里，否则「局部作废」这件事根本没生效");
            Battery.Assert(cache.Get(b, 0, rows - 1, 0) is not null && cache.TileRenders == rows + 1,
                $"拖多 {delta} 行：带以下最后一块要走重渲染，且整趟只该多渲染这一款"
                + $"（现在 {cache.TileRenders} 块，暖缓存 {rows} ＋ 重算 1）");
            RgbaImage redrawn = cache.Get(b, 0, rows - 1, 0)!;
            RgbaImage fresh = renderer.RenderTile(b, 0, rows - 1)!;
            Battery.AssertBytesEqual(fresh.RegionSha256(fresh.Bounds), redrawn.RegionSha256(redrawn.Bounds),
                $"拖多 {delta} 行：重渲染出来的那块必须与新布局现算逐字节相同（分块＝整块那条红线）");
            detail.Append($"Δ{delta}：带以上同{aboveSame}、跨带变{changedCross}、带以下变{changedBelow}"
                + $"（源行号挪对{shiftedBelow}）、作废{dropped}块 ");
        }
        Battery.Evidence(detail.ToString());
        Battery.Evidence("结论：换「内容身份键」（段号＋源矩形）对本手势命中 0 块——键跟着内容每帧一起变。"
            + "所以关档那 68.6ms 尖峰不是「缓存策略没做好」，而是拖对齐本来就要重算带以下每一块");
    }

    /// <summary>
    /// 上一条判据的反证：<b>把作废区写成「只丢带区那几块」</b>（也就是照抄蓝线那一手势的守恒结论）会怎样。
    /// 这一格量的是缺陷形状下缓存<b>真的</b>会交出上一帧的像素，
    /// 而且交出的那块与新布局现算<b>字节不同</b>：屏上就是留着旧内容，用户看到的拼接位置是错的。
    /// 同一段代码里再用产品的作废形状做正向对照（那块必须重渲染并与新布局相同），
    /// 两支合起来才说明「少丢＝错、按现范围丢＝对」。
    /// </summary>
    private static void BandOnlyDropOnAlignmentDragWouldShowStalePixels()
    {
        (Project p, MemoryResolver r) = NewChain(8);
        var renderer = new TileRenderer(new CanvasComposer(r));
        CanvasLayout a = p.Layout;
        Seam dragged = p.Doc.Seams[1];
        IntRect band = a.BandCanvasRect(dragged)!.Value;
        int cut = a.DescribeSeam(dragged)!.CutYCanvas - band.Y;
        int rows = TileRenderer.TilesDown(a.CanvasSize.Height);
        int below = rows - 1;                      // 最后一块肯定在带以下

        CanvasLayout b = CanvasLayout.Build(p.Doc, null,
            new Dictionary<string, SeamAlignment> { [dragged.Id] = new SeamAlignment(-(Overlap + 8), cut) });

        // ── 缺陷形状：只丢带区 ──
        var defect = new TileCache(renderer);
        for (int ty = 0; ty < rows; ty++) defect.Get(a, 0, ty, 0);
        long rendersBefore = defect.TileRenders;
        defect.DropRegion(band);
        RgbaImage? served = defect.Get(b, 0, below, 0);
        Battery.Assert(defect.TileRenders == rendersBefore,
            "夹具自证：缺陷形状下那块没被丢，所以取它必须命中旧缓存（不命中这格就什么都没量到）");
        RgbaImage fresh = renderer.RenderTile(b, 0, below)!;
        Battery.Assert(served is not null
                && !served.RegionSha256(served.Bounds).SequenceEqual(fresh.RegionSha256(fresh.Bounds)),
            "缺陷形状下缓存交出的那块必须与新布局现算<b>不同</b>——若相同，那「少丢一点」就是安全的，"
            + "上面那条判据的「带以下全变」也就没有主语了");

        // ── 正向对照：产品的形状 ──
        var product = new TileCache(renderer);
        for (int ty = 0; ty < rows; ty++) product.Get(a, 0, ty, 0);
        long rendersAtFix = product.TileRenders;
        product.DropRegion(new IntRect(0, band.Y - 8, a.CanvasSize.Width, a.CanvasSize.Height - band.Y + 8));
        RgbaImage? redrawn = product.Get(b, 0, below, 0);
        Battery.Assert(product.TileRenders > rendersAtFix,
            "按产品的作废范围那一块要被丢掉，取它必须重渲染（这里不重渲染就是作废范围写窄了）");
        Battery.AssertBytesEqual(fresh.RegionSha256(fresh.Bounds), redrawn!.RegionSha256(redrawn.Bounds),
            "重渲染出来的那块与新布局现算逐字节相同");
        Battery.Evidence($"只丢带区：缓存交出旧那块（{fresh.Height} 行高，字节不同＝屏上留上一帧像素）；"
            + "按带顶往下丢：那块重渲染且字节相同");
    }

    /// <summary>
    /// 长链夹具：8 段各 60×600、每缝重叠 120 ⇒ 画布 3960 行＝8 块瓦片行，
    /// 于是「带以下好几块」这件事真的存在（两段的旧夹具只有 3 块，量不出这个形状）。
    /// </summary>
    private static (Project P, MemoryResolver Resolver) NewChain(int segments)
    {
        RgbaImage page = LongScrollTests.StripedPage(Width, Height * segments + Overlap * (segments - 1));
        var images = new RgbaImage[segments];
        for (int i = 0; i < segments; i++)
            images[i] = page.SubImage(new IntRect(0, i * (Height - Overlap), Width, Height));
        (Project p, MemoryResolver r, _) = TestProject.Create(StitchMode.LongScroll, images);
        p.Mutate("链上每条缝都设成 120 行重叠", _ =>
        {
            for (int i = 0; i < p.Doc.Seams.Count; i++)
            {
                Seam seam = p.Doc.Seams[i];
                seam.AutoOffset = new IntPoint(0, (i + 1) * Height - (i + 2) * Overlap);
                seam.SetOverlap(Overlap);
                seam.Analyzed = true;
            }
        });
        return (p, r);
    }

    // ───────────────────────── ⑥ 日志一族两趟（需求十一） ─────────────────────────

    /// <summary>
    /// 需求十一那批"必须区分"的字段，滑动两趟各自齐备，而且<b>互不覆盖</b>：
    /// 抓下方那张那一趟改蓝线的带内行号、不改视口；抓上方那张那一趟反过来——
    /// 蓝线的<b>画布</b>行号被推走、由视口补偿顶回来，所以<b>设备</b>行号两趟都不动。
    /// 这正是这次重设计的全部主张，日志要能自己把它读出来。
    /// <para>
    /// 旧口径这里量的是 seam.cut 与 seam.alignment <b>两个前缀</b>（拖蓝线只改 CutY、拖红箭头只改对齐）。
    /// 手势并成一条之后前缀也并成一个 seam.slide，那一族已经作废；
    /// 与它并列的另一族现在是<b>位移档</b>（竖拼／横拼用 seam.edit，见 `canvas/` 那一组），
    /// 所以这一格改成量"同一族里两趟互不覆盖"，把"两族前缀不同"那半交给
    /// `crop/滑动图片的流水账带齐需求八那批字段与线不动读数` 与位移档那格。
    /// </para>
    /// 读的是 trace 本身（与日志同一份数），不是 grep 文案。
    /// </summary>
    private static void TwoSlideTracesAreSeparate()
    {
        using Rig rig = NewRig();
        SetCut(rig, 40);

        // 需求十一那批"必须区分"的字段名逐个点名（读的是 Fields() 本身，不是提示文案）。
        // 名字逐字对齐需求：同义但不同名（viewport= 与 viewportAfter=）等于按需求去 grep 的人拿空结果。
        string[] specFields =
        {
            "alignmentOffsetBefore=", "alignmentOffsetAfter=", "cutYBefore=", "cutYAfter=",
            "rect1=", "rect2=", "sourceRect1=", "sourceRect2=",
            "viewportBefore=", "viewportAfter=", "canvasHeightBefore=", "canvasHeightAfter=",
            "mutated=", "undoPushed=",
            // 第十三轮需求七点名的那一批（两趟都要同一批）
            "fixedSide=", "overlapRectBefore=", "overlapRectAfter=",
            "rect1DeviceBefore=", "rect1DeviceAfter=", "rect2DeviceBefore=", "rect2DeviceAfter=",
            "cutRatioBefore=", "cutRatioAfter=",
            // 「线不动、图动」这一轮加的六个读数——两趟都必须带，否则"线到底动没动"读不出来
            "lineCanvasRowBefore=", "lineCanvasRowAfter=", "lineDeviceBefore=", "lineDeviceAfter=",
            "line_device_moved=", "grabbed=",
        };

        // ═══ 第一趟：压着蓝线往下拖 ⇒ 抓的是<b>下方</b>那张 ═══
        DragAndRead(rig, OnCutLinePoint(rig), 15);
        StitchCanvasControl.SeamEditTrace afterTrace = rig.Canvas.LastSeamEditTraceForTest!;
        Battery.AssertEqual("seam.slide", afterTrace.EventPrefix, "滑动这一族走 seam.slide.*");
        Battery.AssertEqual(FixedSide.After, afterTrace.Grabbed, "压着蓝线按下抓的就是下方那张");
        Battery.AssertEqual(FixedSide.Before, afterTrace.FixedSide, "抓下方那张 ⇒ 屏上钉住的是上方那张");
        Battery.Assert(afterTrace.CutChanged, "cutYBefore/cutYAfter 必须真的不同（40→25）");
        Battery.Assert(afterTrace.AlignmentChanged, "重叠同减 ⇒ alignmentOffsetBefore/After 也必须不同");
        Battery.Assert(afterTrace.CanvasHeightChanged, "带被压掉 15 行 ⇒ 画布总高必须跟着变");
        Battery.Assert(afterTrace.Mutated && afterTrace.UndoPushed, "提交那一帧要报 mutated/undoPushed");
        string afterFields = afterTrace.Fields();
        foreach (string key in specFields)
        {
            Battery.Assert(afterFields.Contains(key, StringComparison.Ordinal), $"第一趟的日志行里缺字段 {key}");
            Battery.AssertEqual(1, CountOccurrences(afterFields, key),
                $"{key} 在一行里出现了多次：两处同值就是两份会各自漂移的真相，实际行 " + afterFields);
        }
        // 「线不动」在这一趟里是最干净的形式：画布行号与设备行号都不动，视口也不动。
        Battery.AssertEqual(afterTrace.LineCanvasRowBefore, afterTrace.LineCanvasRowAfter,
            "抓下方那张时蓝线的画布行号一字不动（O 与 Cut 对消）");
        Battery.Assert(!afterTrace.LineDeviceMoved, "设备行号同理不动：line_device_moved 要读成 false");
        Battery.AssertEqual(afterTrace.ViewportBefore, afterTrace.ViewportAfter, "这一趟不需要视口补偿");
        Battery.AssertEqual(afterTrace.Rect1DeviceBefore, afterTrace.Rect1DeviceAfter,
            "屏上钉住的是图1 ⇒ 它的设备矩形一字不动");
        Battery.Assert(afterTrace.Rect2DeviceBefore != afterTrace.Rect2DeviceAfter,
            "被抓的是图2 ⇒ 它在屏幕上必须真的跟着手走了（不然这一趟没在拖图）");

        // ═══ 第二趟：在带里蓝线<b>上方</b>按下 ⇒ 抓的是上方那张 ═══
        int cutAfterTripOne = afterTrace.EffectiveCut;
        DragAndRead(rig, BandAboveLinePoint(rig), -25);
        StitchCanvasControl.SeamEditTrace beforeTrace = rig.Canvas.LastSeamEditTraceForTest!;
        Battery.AssertEqual("seam.slide", beforeTrace.EventPrefix, "同一族的另一趟还是 seam.slide");
        Battery.AssertEqual(FixedSide.Before, beforeTrace.Grabbed, "在蓝线上方按下抓的是上方那张");
        Battery.AssertEqual(FixedSide.After, beforeTrace.FixedSide, "抓上方那张 ⇒ 屏上钉住的是下方那张");
        Battery.Assert(!beforeTrace.CutChanged,
            "抓上方那张只改重叠，带内行号不动（cutYBefore=cutYAfter）——这一条把两趟分开");
        Battery.Assert(beforeTrace.AlignmentChanged, "重叠改了 ⇒ alignmentOffset 那一对必须不同");
        Battery.Assert(beforeTrace.CanvasHeightChanged, "画布高跟着带一起变");
        // 互不覆盖：第二趟的起点必须就是第一趟的终点。
        // 上一趟的 before/after 若被这一份复用（历史上"闭包旧值"那一族缺陷的形状），这里立刻红。
        Battery.AssertEqual(cutAfterTripOne, beforeTrace.CutBefore,
            $"第二趟的起点蓝线要等于第一趟的终点（{cutAfterTripOne}），实际 {beforeTrace.CutBefore}");
        Battery.Assert(beforeTrace.LineCanvasRowBefore != beforeTrace.LineCanvasRowAfter,
            "抓上方那张 ⇒ 蓝线的<b>画布</b>行号这一趟确实被推走了（不动的是屏上那一行）");
        Battery.Assert(!beforeTrace.LineDeviceMoved,
            $"画布行号被推走、视口补偿顶回来 ⇒ 设备行号仍一字不动" +
            $"（{beforeTrace.LineDeviceBefore}→{beforeTrace.LineDeviceAfter}）");
        Battery.Assert(beforeTrace.ViewportChanged, "正向对照：视口真的补偿过了，上面那句『设备不动』才不是恒真");
        Battery.AssertEqual(beforeTrace.Rect2DeviceBefore, beforeTrace.Rect2DeviceAfter,
            "屏上钉住的是图2 ⇒ 它的设备矩形一字不动");
        Battery.Assert(beforeTrace.Rect1DeviceBefore != beforeTrace.Rect1DeviceAfter,
            "被抓的图1 必须在屏幕上跟着动");
        Battery.Assert(!beforeTrace.Fields().Contains("fixed_side=", StringComparison.Ordinal),
            "字段名按需求原文写 fixedSide（旧写法 fixed_side 不许留在同一行里当第二套名字）");
        Battery.Evidence("seam.slide 两趟的 changed 标志正好相反（抓下方改蓝线不改视口 / 抓上方改视口不改蓝线），" +
                         "而 line_device_moved 两趟都是 false；需求七那批 before/after 两趟都齐、每个名字一行只出现一次");
    }

    /// <summary>数一个子串在整行里出现几次（不引正则：日志判据只看这一行，够简单才好读）。</summary>
    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0, at = haystack.IndexOf(needle, StringComparison.Ordinal);
        while (at >= 0)
        {
            count++;
            at = haystack.IndexOf(needle, at + needle.Length, StringComparison.Ordinal);
        }
        return count;
    }

    /// <summary>
    /// 带里"蓝线以上取图1、以下取图2"的<b>像素级</b>自证：异本夹具只在图2 顶部那 O 行涂了标记色，
    /// 所以"这一行到底是谁"在成品里读得出来（图1 的 R 通道来自 y%233，永远到不了 255）。
    /// 读的是合成通路那一份——它与导出编码进文件的字节同源，那条等价性由常驻判据
    /// <c>crop/预览合成与导出文件逐字节相同</c> 钉着，所以这一句说的就是成品。
    /// </summary>
    private static void AssertBandOwnedByCutLine(Rig rig, string stage)
    {
        SeamGeometry g = rig.Geometry;
        IntRect band = g.OverlapRect;
        int cut = g.CutYCanvas;
        Battery.Assert(cut >= band.Y && cut <= band.Bottom,
            $"{stage}：蓝线先要落在新带里（带 {band}，蓝线 {cut}）");
        RgbaImage canvas = rig.Composer.RenderWhole(rig.P.Layout);
        int fromImage1 = 0, fromImage2 = 0;
        for (int y = band.Y; y < band.Bottom; y++)
        {
            bool fromAfter = canvas.GetPixel(0, y).R == 255;
            if (y < cut)
            {
                Battery.Assert(!fromAfter, $"{stage}：带里第 {y} 行在蓝线之上，取的却是图2 那份");
                fromImage1++;
            }
            else
            {
                Battery.Assert(fromAfter, $"{stage}：带里第 {y} 行在蓝线之下，取的却不是图2 那份");
                fromImage2++;
            }
        }
        Battery.Assert(fromImage1 == cut - band.Y && fromImage2 == band.Bottom - cut,
            $"自证：这一趟真的按蓝线把整条带逐行核对过（{fromImage1}+{fromImage2} 行）");
    }

    /// <summary>
    /// 把这条缝的固定侧预置成 Before（屏上钉住上方那张）。
    ///
    /// 第十四轮末次起面板上那个下拉框撤了，固定侧改成<b>手势的结论</b>：
    /// 抓上方那张 ⇒ Core 把它写成 After，抓下方那张 ⇒ 写成 Before。
    /// 判据仍然要能预置一个与手势相反的起点，才量得出"手势把它翻过来了"，
    /// 而不是"它本来就等于手势要写的那个值"（后者是恒真判据）。
    /// </summary>
    private static void SetFixedBefore(Rig rig) =>
        rig.P.Mutate("预置固定侧", _ => rig.Current.Fixed = FixedSide.Before);

    /// <summary>
    /// 需求二（第一档）：固定前一段拖对齐，量的是<b>屏幕</b>上的矩形，不是模型字段。
    /// 图1 必须原地不动、图2 必须按拖动方向动、视口一个像素都不许被偷偷补偿，
    /// 而蓝线仍然在新的带里、成品仍然只保留蓝线上图1/下图2。
    /// </summary>
    private static void FixedBeforeAlignDragKeepsImage1OnScreen()
    {
        using Rig rig = NewRig(zoom: 1.0, origin: new IntPoint(0, 400));   // 屏上带 80..200
        SetCut(rig, 40);
        Seam seam = rig.Current;
        SeamGeometry g0 = rig.Geometry;
        Rectangle rect1Before = rig.Canvas.Viewport.CanvasToDevice(g0.Rect1);
        Rectangle rect2Before = rig.Canvas.Viewport.CanvasToDevice(g0.Rect2);
        IntPoint viewportBefore = rig.TopLeft;
        int lineBefore = g0.CutYCanvas;
        Battery.Assert(rect1Before != Rectangle.Empty && rect2Before.Y > 0,
            $"夹具自证：两块矩形真在屏上有位置（{rect1Before} / {rect2Before}）");

        Point start = BandInteriorPoint(rig);
        Battery.Assert(rig.Canvas.HitTestSeamEditRegion(start) is { Side: FixedSide.After },
            "夹具自证：这个点在蓝线下方 ⇒ 抓的是下方那张 ⇒ 屏上钉住的必须是图1");
        rig.Canvas.MousePress(MouseButtons.Left, Keys.None, start);
        rig.Canvas.MouseMoveTo(new Point(start.X, start.Y - 20));
        rig.Canvas.MouseRelease(new Point(start.X, start.Y - 20));

        SeamGeometry g1 = rig.Geometry;
        Rectangle rect1After = rig.Canvas.Viewport.CanvasToDevice(g1.Rect1);
        Rectangle rect2After = rig.Canvas.Viewport.CanvasToDevice(g1.Rect2);

        Battery.AssertEqual(rect1Before, rect1After,
            "固定前一段：图1 的设备矩形（含宽与高）拖动前后一字不动（需求二）");
        Battery.AssertEqual(rect2Before.Y - 20, rect2After.Y,
            "固定前一段：图2 跟着对齐往上 20 设备像素（多压 20 行 ⇒ Rect2 顶边上移）");
        Battery.AssertEqual(rig.TopLeft, viewportBefore, "固定前一段：视口没被偷偷补偿（需求一）");
        Battery.Assert(rect1After.X == rect1Before.X && rect2After.X == rect2Before.X,
            "两档都必须 X 不变（长截图锁横向）");
        Battery.AssertEqual(0, g1.Rect2.X, "Rect2 的公共坐标 X 恒 0");
        Battery.AssertEqual(0, g1.Offset.X, "对齐拖动之后横向位移仍是 0");
        Battery.Assert(g1.CutInsideBand, "蓝线仍在新 OverlapRect 内（需求二最后一条）");
        Battery.AssertEqual(60, seam.CutPosition,
            "带内行号同加 20（40→60）：与重叠一起动，蓝线的画布行号才钉得住");
        Battery.AssertEqual(lineBefore, g1.CutYCanvas, $"蓝线的画布行号一字不动（{lineBefore}）");
        Battery.Assert(seam.IsManualCut, "滑动不许把『用户改过蓝线』这件事抹掉");
        AssertBandOwnedByCutLine(rig, "抓下方那张滑动之后");

        var trace = (StitchCanvasControl.SeamEditTrace)rig.Canvas.LastSeamEditTraceForTest!;
        Battery.AssertEqual(trace.Rect1DeviceBefore, trace.Rect1DeviceAfter,
            "界面自己记的那一对图1 屏幕矩形要一致（日志与判据读同一份数）");
        Battery.Assert(trace.Rect2DeviceBefore != trace.Rect2DeviceAfter, "图2 那一对必须不同");
        Battery.AssertEqual(rect1After, trace.Rect1DeviceAfter, "判据自己算的与界面记的必须是同一个数");
        Battery.Assert(!trace.ViewportChanged, "抓下方那张时 ViewportChanged 必须为 false");
        Battery.Assert(!trace.LineDeviceMoved, "蓝线的<b>屏幕</b>行号也必须一字不动（这一档的全部意义）");
        Battery.Evidence($"抓下方那张上拖 20px：图1 设备矩形 {rect1Before} 未动；图2 {rect2Before}→{rect2After}" +
                         $"（上移 20）、视口 {viewportBefore} 未动、蓝线画布行号恒为 {lineBefore}、带内逐行归源正确");
    }

    /// <summary>
    /// 需求二（第二档）：固定后一段拖对齐。这一档画布里动的<b>总是图2</b>（图1 的位置只由它上面那条链决定），
    /// 所以要让"图2 在屏上不动"只能靠视口那一段<b>纯显示</b>补偿——补偿的合法性与它必须存在，
    /// 都由这一格钉住：图2 设备矩形一字不动、图1 跟着手走、参数里只有一个对齐位移。
    /// </summary>
    private static void FixedAfterAlignDragKeepsImage2OnScreen()
    {
        using Rig rig = NewRig(zoom: 1.0, origin: new IntPoint(0, 400));
        SetCut(rig, 40);
        SetFixedBefore(rig);
        Seam seam = rig.Current;
        IntPoint alignmentBefore = seam.AlignmentOffset;
        SeamGeometry g0 = rig.Geometry;
        Rectangle rect1Before = rig.Canvas.Viewport.CanvasToDevice(g0.Rect1);
        Rectangle rect2Before = rig.Canvas.Viewport.CanvasToDevice(g0.Rect2);
        IntPoint viewportBefore = rig.TopLeft;
        int revisionBefore = rig.Revision;

        Point start = BandAboveLinePoint(rig);
        Battery.Assert(rig.Canvas.HitTestSeamEditRegion(start) is
            { Kind: StitchCanvasControl.SeamHitKind.OverlapBand, Side: FixedSide.Before },
            "夹具自证：这一按抓的是蓝线<b>上方</b>那张图 ⇒ 屏上钉住的必须是图2");
        rig.Canvas.MousePress(MouseButtons.Left, Keys.None, start);
        rig.Canvas.MouseMoveTo(new Point(start.X, start.Y + 20));    // 抓上方那张往下拖 = 两张图多压 20 行
        rig.Canvas.MouseRelease(new Point(start.X, start.Y + 20));

        SeamGeometry g1 = rig.Geometry;
        Rectangle rect1After = rig.Canvas.Viewport.CanvasToDevice(g1.Rect1);
        Rectangle rect2After = rig.Canvas.Viewport.CanvasToDevice(g1.Rect2);

        Battery.AssertEqual(Overlap + 20, seam.OverlapLength, "抓上方那张往下拖 20 ⇒ 多压 20 行");
        Battery.AssertEqual(new IntPoint(alignmentBefore.X, -(Overlap + 20)), seam.AlignmentOffset,
            "核心里被改的只有对齐位移那一个数（带内行号这一档不动）");
        Battery.AssertEqual(FixedSide.After, seam.Fixed,
            "固定侧是手势的<b>结论</b>：夹具先写成 Before，抓了上方那张之后必须被翻成 After");
        Battery.AssertEqual(rect2Before, rect2After,
            "抓上方那张：图2 的设备矩形拖动前后一字不动（需求二）——这一条正是视口补偿在兜");
        Battery.AssertEqual(rect1Before.Y + 20, rect1After.Y,
            "抓上方那张：图1 跟着手往下走 20 设备像素");
        Battery.AssertEqual(new IntPoint(viewportBefore.X, viewportBefore.Y - 20), rig.TopLeft,
            "视口挪的就是补偿那 20 行：X 一字未动，Y 恰好等于 Rect2 顶边的位移");
        Battery.Assert(rect2After.X == rect2Before.X && rect1After.X == rect1Before.X,
            "补偿不许把横向带进去（X 不变）");
        Battery.AssertEqual(0, g1.Offset.X, "横向位移恒 0（需求二）");
        Battery.Assert(g1.CutInsideBand, "蓝线仍在新 OverlapRect 内");
        Battery.AssertEqual(40, seam.CutPosition,
            "抓上方那张时带内行号不动（动的是画布行号与视口补偿），与固定侧无关");
        Battery.AssertEqual(g0.CutYCanvas - 20, g1.CutYCanvas,
            "蓝线在画布坐标里往上走 20 行——屏幕那一头由补偿钉住");
        AssertBandOwnedByCutLine(rig, "抓上方那张滑动之后");
        Battery.AssertEqual(revisionBefore + 1, rig.Revision, "补偿不进撤销栈：整趟只那一条参数改动");

        var trace = (StitchCanvasControl.SeamEditTrace)rig.Canvas.LastSeamEditTraceForTest!;
        Battery.AssertEqual(trace.Rect2DeviceBefore, trace.Rect2DeviceAfter,
            "界面自己也要记成『图2 屏幕位置没动』（判据与日志读同一份数）");
        Battery.Assert(trace.Rect1DeviceBefore != trace.Rect1DeviceAfter, "图1 那一对必须不同");
        Battery.Assert(trace.ViewportChanged,
            "抓上方那张这一档视口必须真的动了：没动就说明补偿没生效，屏上动的是图2");
        Battery.Assert(!trace.LineDeviceMoved,
            $"蓝线的<b>屏幕</b>行号必须一字不动（{trace.LineDeviceBefore}→{trace.LineDeviceAfter}）：" +
            "画布行号变了 20，补偿就得跟着变 20，屏上才看不出线动过");
        Battery.Assert(!trace.Mutated || trace.UndoPushed, "视口动了但不许有第二条改动");
        Battery.Evidence($"抓上方那张下拖 20px：图2 设备矩形 {rect2Before} 未动；图1 {rect1Before}→{rect1After}" +
                         $"（下移 20）、视口 {viewportBefore}→{rig.TopLeft}（纯显示补偿）、" +
                         $"蓝线屏幕行号 {trace.LineDeviceBefore} 未动、参数只写了对齐 −120→−140");
    }

    /// <summary>
    /// 第十四轮需求一：<b>撤销必须把「纯显示补偿」那一段屏幕位置一起退回去</b>。
    ///
    /// ═══ 缺陷形状 ═══
    /// 固定后一段拖对齐时，为了让图2 在屏上原地不动，视口跟着 Rect2 顶边挪了几行（见
    /// <see cref="FixedAfterAlignDragKeepsImage2OnScreen"/>）。那段补偿<b>不写参数、不进撤销栈</b>，
    /// 而通用 Undo 只恢复文档 ⇒ 参数回来了、画面还停在补偿后的位置，
    /// 用户看到的正是需求一原话里那句「参数已经恢复，但画面位置不对」，得再滚一下才正常。
    ///
    /// ═══ 这一格量的是四件事一起回来，不是只量参数 ═══
    /// 对齐位移、蓝线、画布高是文档自己恢复的（那一条通路本来就对）；
    /// <b>视口原点与两块设备矩形</b>才是这次补的那一头——所以四个都单独断言，
    /// 再加一条像素级正向对照：撤销之后屏幕上那一整帧必须与拖动<b>之前</b>那一帧逐处相同。
    /// 只断言视口数值还不够：设备矩形是「画布坐标 − 视口」，视口错一行它必错一行，
    /// 而判据要挡的是"界面自己也算对了但没把屏幕换过来"这种半途修复。
    ///
    /// ═══ 反面对照（同样承重）═══
    /// 固定前一段那趟从没挪过视口 ⇒ 撤销<b>不许</b>动屏幕。
    /// 少了这一条，"撤销按快照绝对赋值"会退化成"撤销顺手把用户滚出来的视野也拽回按下那一刻"，
    /// 那是另一个更恼人的缺陷。
    /// </summary>
    private static void UndoAfterFixedAfterDragRestoresViewport()
    {
        using Rig rig = NewRig(zoom: 1.0, origin: new IntPoint(0, 400));
        SetCut(rig, 40);
        SetFixedBefore(rig);

        // ── 拖动开始前：把需求一点名的那五个量连同整帧画面一起记下来当基准 ──
        IntPoint alignmentBefore = rig.Current.AlignmentOffset;
        int cutBefore = rig.Current.CutPosition;
        int overlapBefore = rig.Current.OverlapLength;
        int heightBefore = rig.CanvasHeight;
        IntPoint viewportBefore = rig.TopLeft;
        SeamGeometry g0 = rig.Geometry;
        Rectangle rect1Before = rig.Canvas.Viewport.CanvasToDevice(g0.Rect1);
        Rectangle rect2Before = rig.Canvas.Viewport.CanvasToDevice(g0.Rect2);
        using var frameBefore = new System.Drawing.Bitmap(400, 500);
        rig.Canvas.RenderFrameForTest(frameBefore);

        Point start = BandAboveLinePoint(rig);
        Battery.Assert(rig.Canvas.HitTestSeamEditRegion(start) is { Side: FixedSide.Before },
            "夹具自证：这一按抓的是蓝线上方那张 ⇒ 屏上钉住的是图2，视口必须被补偿");
        rig.Canvas.MousePress(MouseButtons.Left, Keys.None, start);
        rig.Canvas.MouseMoveTo(new Point(start.X, start.Y + 20));    // 抓上方那张往下拖 = 多压 20 行
        bool previewDuringDrag = rig.Canvas.UsingPreviewLayoutForTest;   // 撤销要恢复的那一项，先量"它真的存在过"
        rig.Canvas.MouseRelease(new Point(start.X, start.Y + 20));
        IntPoint viewportAfterDrag = rig.TopLeft;
        Battery.Assert(previewDuringDrag,
            "夹具自证：拖动中途画的必须是临时预览布局——它要是本来就是 false，" +
            "下面那句「撤销后预览布局作废」就成了恒真的白判据");

        Battery.AssertEqual(overlapBefore + 20, rig.Current.OverlapLength, "夹具自证：这一趟真的改了重叠");
        Battery.Assert(viewportAfterDrag != viewportBefore,
            $"夹具自证：固定后一段必须真的挪过视口（纯显示补偿），否则下面那句「撤销把视口也还回去」" +
            $"是空判据；现在视口 {viewportBefore}→{viewportAfterDrag}");
        Battery.Assert(heightBefore != rig.CanvasHeight, "夹具自证：这一趟画布也确实变矮了");

        long geoBeforeUndo = rig.Canvas.HudGeometryRebuilds;   // 「重算了几次」要跨撤销这一步比，先取基线
        rig.P.Undo();
        IntPoint viewportAfterUndo = rig.TopLeft;      // 读数要在被测那一刻接住，别让后面的重做把证据行写成另一个时刻的数
        // 需求一第 2 条那五项里的"预览布局"：撤销之后必须回到项目布局那份缓存上。
        // 这一条以前只被下面那句像素对照"推到"，现在直接量——离屏工装不自己重画，
        // 只量视口与像素会漏掉"临时布局还挂着但视口数值已对"这一格。
        Battery.Assert(!rig.Canvas.UsingPreviewLayoutForTest,
            "撤销后画的必须是项目布局而不是那份临时预览布局（预览布局必须作废）");

        // ── 需求一.3 那四行等式，逐条量 ──
        Battery.AssertEqual(alignmentBefore, rig.Current.AlignmentOffset,
            "撤销后 AlignmentOffset 必须等于原始值");
        Battery.AssertEqual(cutBefore, rig.Current.CutPosition, "撤销后 CutY（带内切点）必须等于原始值");
        Battery.AssertEqual(heightBefore, rig.CanvasHeight, "撤销后画布高必须等于原始值");
        Battery.AssertEqual(viewportBefore, rig.TopLeft,
            "撤销后 Viewport.TopLeft 必须等于原始值：那一段补偿是这次拖动留在屏幕上的，" +
            "参数恢复了就许它继续留着（需求一点名的那个缺陷）");
        SeamGeometry g1 = rig.Geometry;
        Rectangle rect1AfterUndo = rig.Canvas.Viewport.CanvasToDevice(g1.Rect1);
        Rectangle rect2AfterUndo = rig.Canvas.Viewport.CanvasToDevice(g1.Rect2);
        Battery.AssertEqual(rect1Before, rect1AfterUndo, "撤销后图1 的设备矩形必须回到原位");
        Battery.AssertEqual(rect2Before, rect2AfterUndo, "撤销后图2 的设备矩形必须回到原位");

        // ── 像素级正向对照：撤销之后屏幕上那一帧 == 拖动之前那一帧 ──
        // 这一条管的是"设备矩形真的回到原位"（视口差一行、或哪块叠层算错，像素必然对不上）。
        // "预览布局作废"不再靠这条推——上面已经直接量那个布尔了：离屏工装只画显式那一帧，
        // 拿像素推它会把"临时布局还挂着但数值恰好已对"这一格推过去。
        using var frameAfterUndo = new System.Drawing.Bitmap(400, 500);
        rig.Canvas.RenderFrameForTest(frameAfterUndo);
        Battery.AssertEqual(0, CountDifferentPixels(frameBefore, frameAfterUndo),
            "撤销之后屏幕上那一整帧必须与拖动之前逐处相同（取样每 3 像素一枚）");

        // ═══ 需求一第 2 条最后那半句："HUD 数值"也要跟着恢复 ═══
        // 读数层画的就是同一批公共坐标，所以它必须回到原始那一组数；
        // 量文本而不是量像素：面板有 10Hz 闸门，像素级比会撞上"这一帧刚好没重排"。
        rig.Canvas.ShowDebugHud = true;
        System.Threading.Thread.Sleep(150);                    // 越过 10Hz 闸门，逼面板真的重排一次
        rig.Canvas.RenderFrameForTest(frameAfterUndo);
        string hudText = rig.Canvas.HudTextForTest;
        Battery.Assert(rig.Canvas.HudGeometryRebuilds > geoBeforeUndo,
            "撤销换了文档（修订号 +1）⇒ 读数层的几何缓存必须重算，不许留着拖动后那一套矩形");
        Battery.Assert(hudText.Contains($"AlignmentOffset=({alignmentBefore.X},{alignmentBefore.Y})",
                StringComparison.Ordinal),
            $"屏上那行 AlignmentOffset 读数必须回到原始值；实际读数：{hudText.Split('\n')[5]}");
        Battery.Assert(hudText.Contains($"画布高={heightBefore}", StringComparison.Ordinal),
            "屏上的画布高读数也要回到原始值");
        Battery.Assert(hudText.Contains(
                $"Viewport.TopLeft=({viewportBefore.X},{viewportBefore.Y})", StringComparison.Ordinal),
            "屏上的视口原点读数也要回到原始值——这三行一起才是「HUD 数值恢复」的可核对形式");
        rig.Canvas.ShowDebugHud = false;

        // ── 重做：快照跟着历史进 redo 栈，补偿再回来一次 ──
        rig.P.Redo();
        Battery.AssertEqual(viewportAfterDrag, rig.TopLeft,
            "重做必须把同一段纯显示补偿再带上（快照与历史条目一起进 redo 栈，不是只进 undo 栈）");
        Battery.AssertEqual(overlapBefore + 20, rig.Current.OverlapLength, "重做后参数也回来了");
        using var frameAfterRedo = new System.Drawing.Bitmap(400, 500);
        rig.Canvas.RenderFrameForTest(frameAfterRedo);
        Battery.Assert(CountDifferentPixels(frameBefore, frameAfterRedo) > 0,
            "重做之后屏幕必须与撤销前那一帧不同——一样就说明 redo 什么都没做");

        // ═══ 反面对照：固定前一段那趟没挪过视口 ⇒ 撤销不许动屏 ═══
        using Rig plain = NewRig(zoom: 1.0, origin: new IntPoint(0, 400));
        SetCut(plain, 40);                                            // Fixed 默认就是 Before
        Point plainStart = BandInteriorPoint(plain);
        plain.Canvas.MousePress(MouseButtons.Left, Keys.None, plainStart);
        plain.Canvas.MouseMoveTo(new Point(plainStart.X, plainStart.Y + 20));
        plain.Canvas.MouseRelease(new Point(plainStart.X, plainStart.Y + 20));
        // 固定前一段往下拖=把图2 往下拉=重叠变少（与固定后一段方向相反，见本文件那两格屏幕判据）
        Battery.AssertEqual(overlapBefore - 20, plain.Current.OverlapLength,
            "夹具自证：固定前一段这趟也真的改了重叠");
        IntPoint userPanned = plain.TopLeft + new IntPoint(0, 37);     // 用户自己往下滚了一段
        plain.Canvas.Viewport.MoveTopLeftTo(userPanned);
        plain.P.Undo();
        Battery.AssertEqual(userPanned, plain.TopLeft,
            "固定前一段那趟从没补偿过视口 ⇒ 撤销只该退参数，不许把用户自己滚出来的视野拽回按下那一刻");
        Battery.AssertEqual(overlapBefore, plain.Current.OverlapLength, "反面对照里参数照样要退回");
        Battery.Evidence($"固定后一段：视口 {viewportBefore}→{viewportAfterDrag}（补偿 20 行）→ 撤销回 {viewportAfterUndo}、" +
                         $"重做再回 {rig.TopLeft}；撤销后整帧像素与拖动前取样差异 0 处；" +
                         $"反面对照：固定前一段那趟视口全程未动，撤销后仍是用户滚到的 {userPanned}");
    }

    // ───────────────────────── ⑦ 主窗口级（需求一的可见后果） ─────────────────────────
    /// <summary>
    /// 需求一那半"看起来拖不动"的真凶：右侧面板的蓝线读数。
    /// 量的是"屏上的数 == 参数里的数"，不是提示文案。
    /// </summary>
    private static void PanelCutReadoutFollowsCanvasDrag()
    {
        using FormRig rig = FormRig.New();
        // 起点必须把蓝线摆进带里：默认档 Cut=0 时"抓下方那张往下滑"一步都滑不动
        //（滑动量被 min(O,Cut) 夹成 0），这一格就会红在"这趟拖动没改动蓝线"上，
        // 而那读数本来就不动——判据量的是"读数跟不跟着参数走"，参数自己得先动。
        rig.P.Mutate("预设蓝线", _ => rig.Seam.ApplyCut(40));
        int cutBefore = rig.Seam.CutPosition;
        Battery.AssertEqual(40, cutBefore, "夹具自证：起点蓝线在带内第 40 行");
        Battery.AssertEqual(cutBefore, rig.Form.ToolPanelForTest.CutRowValueForTest,
            "夹具自证：一开始屏上的蓝线与参数一致");

        rig.DragCutLineDown(20);

        Battery.Assert(cutBefore != rig.Seam.CutPosition, "这趟拖动没改动蓝线，夹具不对");
        Battery.AssertEqual(rig.Seam.CutPosition, rig.Form.ToolPanelForTest.CutRowValueForTest,
            "拖完之后屏上的「蓝线」必须等于参数里的蓝线（读数不跟参数走=界面在骗人）");
        Battery.Evidence($"抓下方那张往下滑 20 设备像素：参数蓝线 {cutBefore}→{rig.Seam.CutPosition}，" +
                         $"屏上读数同步为 {rig.Form.ToolPanelForTest.CutRowValueForTest}");
    }

    /// <summary>
    /// 需求六.4：画布上按下→拖动→松手这一整趟，视口一个像素都不许动。
    /// 缺陷形状是主窗口的列表高亮回调 <c>SelectSeam(scrollIntoView: true)</c> 反着走一遍，
    /// 于是"刚按下去画面就跳一下"。两档手势都要量。
    /// </summary>
    private static void CanvasSelectionDoesNotScrollViewport()
    {
        using FormRig rig = FormRig.New();
        IntPoint before = rig.Canvas.Viewport.TopLeft;
        foreach (Point start in new[] { rig.PointOnCutLine(), rig.PointInBand(60) })
        {
            rig.Canvas.MousePress(MouseButtons.Left, Keys.None, start);
            Battery.AssertEqual(before, rig.Canvas.Viewport.TopLeft,
                "按下（含选中这条缝、列表跟着高亮）这一趟不许把视口推走");
            rig.Canvas.MouseMoveTo(new Point(start.X, start.Y + 10));
            Battery.AssertEqual(before, rig.Canvas.Viewport.TopLeft, "预览期间同样不许动视口");
            rig.Canvas.MouseRelease(new Point(start.X, start.Y + 10));
            Battery.AssertEqual(before, rig.Canvas.Viewport.TopLeft, "松手提交也不许动视口");
        }
        Battery.Evidence("蓝线与对齐两档手势全程 TopLeft 未变");
    }

    // ═══════════════ 微调把手与点选上下侧（第十四轮交互重设计） ═══════════════

    /// <summary>
    /// 用户那句「点击蓝色线上的调整按钮」落成的判据：把手那一档<b>只开关微调档</b>。
    /// 它就骑在蓝线正中——若它也能开拖动手势，等于又给用户一条「把线拖走」的路，
    /// 而那正是这次重设计要拆掉的东西。
    /// 量五件事：① 按下去档位真的开了（按了不等于没反应）；② 松手后档位留着（方向键是接下来才按的）；
    /// ③ 这一下一个字节的参数都不许改、一条撤销都不许留、一本流水账都不许开；
    /// ④ 再点一下＝<b>完成</b>：退档与取消选中<b>一起做</b>——旧写法只把开关拨回 off、选中的缝留着，
    /// 用户于是还得再按两下 Esc 才算退出编辑这条缝（他那句「我手动点击完成后就不需要我再双击 esc 了」）；
    /// ⑤ 一次 Esc 同样退两样，而且浏览态下不许再报「这一下我吃了」。
    /// </summary>
    private static void AdjustHandleTogglesModeOnly()
    {
        using Rig rig = NewRig();
        SetCut(rig, 40);
        rig.Canvas.CreateControl();
        Rectangle handle = HandleRect(rig);
        var center = new Point(handle.X + handle.Width / 2, handle.Y + handle.Height / 2);
        Battery.AssertEqual(StitchCanvasControl.SeamHitKind.AdjustHandle,
            rig.Canvas.HitTestSeamEditRegion(center)?.Kind ?? StitchCanvasControl.SeamHitKind.CutLine,
            "夹具自证：把手正中那一点命中的必须是把手本身（否则这格量的是线那一档）");

        int revision0 = rig.Revision, overlap0 = rig.Current.OverlapLength, cut0 = rig.Current.CutPosition;
        IntPoint viewport0 = rig.TopLeft;
        Battery.Assert(!rig.Canvas.AdjustModeActive, "夹具自证：起点不在微调档");
        Battery.AssertEqual(rig.Current.Id, rig.Canvas.SelectedSeamId, "夹具自证：起点这条缝是选中的");

        rig.Canvas.MousePress(MouseButtons.Left, Keys.None, center);
        Battery.Assert(rig.Canvas.AdjustModeActive, "按下去就得进微调档——屏上那枚按钮按了没反应就是假的");
        rig.Canvas.MouseRelease(center);
        Battery.Assert(rig.Canvas.AdjustModeActive, "松手之后档位必须留着（方向键是松手之后才按的）");
        Battery.AssertEqual(rig.Current.Id, rig.Canvas.SelectedSeamId, "开档不该顺手把这条缝取消选中");
        Battery.AssertEqual(revision0, rig.Revision, "开微调档一个字节的参数都不许改");
        Battery.AssertEqual(overlap0, rig.Current.OverlapLength, "同理不许改重叠");
        Battery.AssertEqual(cut0, rig.Current.CutPosition, "同理不许改蓝线");
        Battery.AssertEqual(viewport0, rig.TopLeft, "同理不许动视口");
        Battery.Assert(rig.Canvas.LastSeamEditTraceForTest is null,
            "把手那一下不许开拖动流水账：开了就说明它偷偷也进了拖动手势（=又能把线拖走）");

        // ═══ 第二下 = 完成：退档 + 取消选中，一次点干净 ═══
        rig.Canvas.MousePress(MouseButtons.Left, Keys.None, center);
        rig.Canvas.MouseRelease(center);
        Battery.Assert(!rig.Canvas.AdjustModeActive, "再点一次必须退出微调档（它是开关，不是只能进不能退）");
        Battery.Assert(rig.Canvas.SelectedSeamId is null,
            "「完成」这一下必须连选中一起退掉：用户点了完成，就不该再按两下 Esc 才回到浏览态");
        Battery.AssertEqual(revision0, rig.Revision, "开档 + 完成 两下点完仍然零改动（退出编辑不算一次改动）");

        // ═══ 一次 Esc 也退两样。走产品自己那一个出口，不在判据里另抄一套退出逻辑 ═══
        rig.Canvas.MousePress(MouseButtons.Left, Keys.None, center);
        rig.Canvas.MouseRelease(center);
        Battery.Assert(rig.Canvas.AdjustModeActive && rig.Canvas.SelectedSeamId is not null,
            "夹具自证：Esc 之前确实处在「正在编辑这条缝」的状态（否则下面两句都是恒真判据）");
        Battery.Assert(rig.Canvas.TryHandleEscape(), "在编辑态里 Esc 必须报「这一下我吃了」");
        Battery.Assert(!rig.Canvas.AdjustModeActive, "一次 Esc 退掉微调档");
        Battery.Assert(rig.Canvas.SelectedSeamId is null, "同一次 Esc 把选中也一起退掉（旧写法要按两下）");
        Battery.Assert(!rig.Canvas.TryHandleEscape(),
            "已经回到浏览态之后 Esc 不许再报「我吃了」——报了就等于把用户那一下吞在没东西可退的地方");
        Battery.Evidence($"点把手：开档（零改动、零流水账）→ 再点＝完成（退档 + 取消选中）；" +
                         $"一次 Esc 同样退两样，浏览态下第二次 Esc 返回 false；修订号全程 {revision0}");
    }

    /// <summary>
    /// 用户那句"再次点选上方和下方的图片，通过上下键盘一个像素一个像素地调整"落成的判据：
    /// 微调档里点哪儿算挑哪儿，而且<b>挑中哪一张决定方向键改的是哪个量</b>。
    /// 点的这两处都在重叠带<b>之外</b>（那两张图的绝大部分面积本来就在带外）——
    /// "必须先瞄准那条薄到几行的带"正是这次要消掉的复杂度。
    /// </summary>
    private static void AdjustModeClickPicksTheSide()
    {
        using Rig rig = NewRig();
        SetCut(rig, 40);
        rig.Canvas.CreateControl();
        rig.Canvas.SetAdjustMode(true);
        Battery.Assert(rig.Canvas.AdjustSideForTest is null,
            "夹具自证：刚开档还没点过任何一张，侧别必须是空的（它要是一开档就自己填好了，下面两句就是抄来的）");

        int lineRow = ToDeviceY(rig, CutLineY(rig));
        var above = new Point(30, lineRow - 120);    // 蓝线上方、带外 —— 图1 那一大片
        var below = new Point(30, lineRow + 120);    // 蓝线下方、带外 —— 图2 那一大片
        Battery.Assert(rig.Canvas.HitTestSeamEditRegion(above) is null &&
                       rig.Canvas.HitTestSeamEditRegion(below) is null,
            "夹具自证：这两个点都在带外线外（旧口径里那只是平移，本轮才把它们读成点选）");

        rig.Canvas.MousePress(MouseButtons.Left, Keys.None, above);
        rig.Canvas.MouseRelease(above);
        Battery.AssertEqual(FixedSide.Before, rig.Canvas.AdjustSideForTest,
            "点蓝线上方 ⇒ 挑中的是上方那张（按了不等于挑中：这句读的是产品自己记的侧别）");
        rig.Canvas.MousePress(MouseButtons.Left, Keys.None, below);
        rig.Canvas.MouseRelease(below);
        Battery.AssertEqual(FixedSide.After, rig.Canvas.AdjustSideForTest, "点蓝线下方 ⇒ 挑中的是下方那张");
        Battery.Assert(rig.Canvas.AdjustModeActive, "点选不许把微调档自己关掉");

        // ═══ 挑中哪一张，方向键改的就是哪一个量 ═══
        // 现在挑着的是下方那张 ⇒ ↓ 把它往下推 ⇒ 重叠少 5 行、带内行号也少 5 行（两者对消，画布行号才不动）。
        int overlap0 = rig.Current.OverlapLength, cut0 = rig.Current.CutPosition;
        int lineRow0 = rig.Geometry.CutYCanvas;
        rig.Canvas.NudgeSelected(new IntPoint(0, 5));
        Battery.AssertEqual(overlap0 - 5, rig.Current.OverlapLength, "挑着下方那张时 ↓ ⇒ 重叠少 5 行");
        Battery.AssertEqual(cut0 - 5, rig.Current.CutPosition, "带内行号同减 5 行");
        Battery.AssertEqual(lineRow0, rig.Geometry.CutYCanvas, "两个量对消 ⇒ 蓝线的画布行号一字不动");

        // 改挑上方那张 ⇒ 同一个 ↓ 改成把上方那张往下推 ⇒ 重叠<b>多</b> 5 行，而带内行号不动。
        rig.Canvas.MousePress(MouseButtons.Left, Keys.None, above);
        rig.Canvas.MouseRelease(above);
        Battery.AssertEqual(FixedSide.Before, rig.Canvas.AdjustSideForTest, "再点上方 ⇒ 侧别改回上方那张");
        int overlap1 = rig.Current.OverlapLength, cut1 = rig.Current.CutPosition;
        rig.Canvas.NudgeSelected(new IntPoint(0, 5));
        Battery.AssertEqual(overlap1 + 5, rig.Current.OverlapLength,
            "挑着上方那张时同一个 ↓ 变成重叠多 5 行（方向键挪的是刚挑中的那一张，不是固定某一张）");
        Battery.AssertEqual(cut1, rig.Current.CutPosition, "抓上方那张不动带内行号");
        Battery.Evidence($"点选侧别：上方⇒Before / 下方⇒After；↓×5 在下方那张是 O {overlap0}→{overlap0 - 5}，" +
                         $"在上方那张是 O {overlap1}→{overlap1 + 5}，蓝线画布行号 {lineRow0} 全程不动");
    }

    // ═══════════════ 忙碌遮罩（导入／分析期间虚化 + 进度条） ═══════════════

    /// <summary>
    /// 虚化必须是<b>真虚化</b>，进度条必须<b>真的跟着走</b>。
    /// 第一版很容易写成"盖一层半透明黑"就交差——那在屏上与"变暗"没区别，半成品内容还是看得清。
    /// 所以这里读产品自己交出的那三个数：有没有做出模糊底、模糊前后差了多少、真的糊了几个通道样本。
    /// <b>反向对照在同一格里</b>：同一套代码拿一张纯色图去做模糊，差值必须是 0——
    /// 于是"差值 &gt; 0"读的是真糊了，而不是"只要做出一张位图就算数"。
    /// </summary>
    private static void BusyOverlayReallyBlursAndProgressAdvances()
    {
        using var rig = new OverlayRig();
        Battery.Assert(rig.CanvasWidth > 0 && rig.CanvasHeight > 0,
            $"夹具自证：离屏工装的画布得有尺寸（实际 {rig.CanvasWidth}×{rig.CanvasHeight}），" +
            "否则 Begin 拿不到任何一帧，下面那些读数就全成了空判据");

        // ── ① 条纹那一趟：必须糊得出来 ──
        rig.Overlay.Begin(rig.StripedFrame, "正在导入 3 张图片", 3);
        Battery.Assert(rig.Overlay.ActiveForTest, "Begin 之后遮罩必须真的进入盖着的状态（没盖上等于没有这一层）");
        Battery.Assert(rig.Overlay.HasBlurForTest,
            "必须做出一张模糊底（拿不到画布那一帧时只会退化成压暗纱，那不算虚化）");
        Battery.Assert(rig.Overlay.BlurSamplesForTest > 0,
            $"箱式模糊那两趟必须真的跑过（样本数 {rig.Overlay.BlurSamplesForTest}）——为 0 说明整个分支被跳过了");
        double stripeDelta = rig.Overlay.BlurDeltaForTest;
        Battery.Assert(stripeDelta > 2.0,
            $"模糊前后的平均绝对差必须量得出（实际 {stripeDelta:F4}）：" +
            "换成「直接贴缩小图」或「只压一层半透明黑」，这个读数就归零");

        // ── ② 反向对照：纯色图模糊前后不可能有差（证明上面那句读的是"糊没糊"，不是"做没做出位图"）──
        rig.Overlay.End();
        rig.Overlay.Begin(rig.FlatFrame, "纯色", 1);
        Battery.Assert(rig.Overlay.HasBlurForTest,
            "夹具自证：纯色那一路同样得出一张模糊底——所以下面那个『差值小』说的是『没得可糊』，不是『没做』");
        double flatDelta = rig.Overlay.BlurDeltaForTest;
        Battery.Assert(flatDelta * 4 < stripeDelta,
            $"纯色那一路的差值（{flatDelta:F4}）必须远小于条纹那一路（{stripeDelta:F4}）：" +
            "两路读数一样才说明那个数读的是『做没做一张位图』而不是『糊没糊』");
        rig.Overlay.End();

        // ── ③ 进度条真的按报数走（不是摆着好看的）──
        rig.Overlay.Begin(rig.StripedFrame, "正在分析 10 条拼缝", 10);
        int empty = rig.BarFill();
        rig.Overlay.Report(1, 10);
        int one = rig.BarFill();
        rig.Overlay.Report(5, 10);
        int half = rig.BarFill();
        rig.Overlay.Report(10, 10);
        int full = rig.BarFill();
        Battery.Assert(empty == 0 && one < half && half < full,
            $"进度条填充必须随报数单调变长（{empty}→{one}→{half}→{full}）");
        Battery.AssertEqual(10, rig.Overlay.DoneForTest, "已完成数要交出来（判据读的就是这一份数）");
        Battery.AssertEqual(10, rig.Overlay.TotalForTest, "总数同理");
        Battery.Assert(rig.Overlay.TitleForTest.Contains("分析", StringComparison.Ordinal),
            "标题得说清这一趟在干什么（实际：" + rig.Overlay.TitleForTest + "）");
        Battery.Assert(full > 0,
            "夹具自证：进度条真的画过（最后一格的填充宽度必须 > 0）——一次都没画过，上面那三个数就全是 0=0=0=0");
        rig.Overlay.End();
        Battery.Assert(!rig.Overlay.ActiveForTest, "End 之后必须收掉——留着就是「导入完了界面还糊着」那个形状");
        Battery.Evidence($"条纹模糊差 {stripeDelta:F4}／纯色那一路 {flatDelta:F4}（反向对照）；" +
                         $"进度条填充 {empty}→{one}→{half}→{full} 设备像素");
    }

    /// <summary>
    /// 导入那一趟走的是<b>产品自己的</b>遮罩接线，这一格量的就是这一条通路：
    /// 每一张都报了一格、总数等于图片数、那一趟收掉了、并且真的做出过模糊底。
    /// 读的是 <c>BusyOverlay.TripsForTest</c> 那本按顺序记着的账——
    /// 只留"上一趟"不行：导入一结束产品会立刻接着跑自动分析，那一趟会把"上一趟"盖掉。
    /// </summary>
    private static void ImportDrivesTheOverlayTrip()
    {
        using FormRig rig = FormRig.New();     // New() 里调的就是产品自己的 ImportPaths
        BusyOverlay overlay = rig.Form.BusyOverlayForTest;
        BusyOverlay.Trip? trip = overlay.TripsForTest
            .FirstOrDefault(t => t.Title.Contains("导入", StringComparison.Ordinal));
        Battery.Assert(trip is not null,
            "导入那一趟必须在遮罩的账上留下一条（一条都没有=产品压根没盖上这一层）");
        if (trip is null) return;
        Battery.AssertEqual(2, trip.Total, "进度总数要等于这次导入的图片数");
        Battery.AssertEqual(2, trip.Done,
            "必须一路报到最后一张才收——只 Begin 不 Report 的那种进度条就是空转（第十一轮那枚 PerformClick 同族）");
        Battery.Assert(trip.HadBlur, "这一趟真做出过模糊底；只有暗纱的那一路在本轮判据里不算虚化");
        // 这里只要求差值<b>不为 0</b>，而不是像上面那格一样卡一个绝对数：
        // 这一格的画布上只有 60 像素宽的两张图、其余全是平的画布底色，
        // 平均差值天然就小（现量 0.35 左右）。真要说"糊得开"，那是
        // `ui/忙碌遮罩虚化的是画布那一帧且进度条真跟着走` 那格用高频率条纹量的事。
        // 而 0 与非 0 这一刀已经足够切开缺陷本身：把模糊那两趟删掉、直接贴缩小图 ⇒ 差值恒为 0。
        Battery.Assert(trip.BlurDelta > 0,
            $"这一趟的模糊差值必须不是 0（实际 {trip.BlurDelta:F4}）——0 就意味着贴的是没糊过的原图");
        Battery.Evidence($"ImportPaths 那一趟：标题「{trip.Title}」、进度 {trip.Done}/{trip.Total}、" +
                         $"模糊差 {trip.BlurDelta:F4}；遮罩账上一共 {overlay.TripsForTest.Count} 趟");
    }

    /// <summary>
    /// 精度红线三（导出绝不从屏幕取图）在这一层上的同一条：<b>遮罩开着的时候，
    /// 画布自己那一帧必须一字不变。</b>它是画布的<b>兄弟控件</b>，不是画布的一层叠图——
    /// 画布的 OnPaint 一个字都不知道它存在，于是瓦片、合成、导出全都不受影响。
    /// 缺陷形状：谁把虚化挪进画布那一层（改"屏上那一帧"就顺带改了"成品那一帧"），
    /// 逐字节对照立刻红。正向对照在同一格里：这一刻遮罩<b>确实开着、确实有模糊底</b>，
    /// 所以那个"0 处不同"不是因为两边都没画东西。
    /// </summary>
    private static void OverlayNeverEntersCanvasFrameOrParameters()
    {
        using FormRig rig = FormRig.New();
        BusyOverlay overlay = rig.Form.BusyOverlayForTest;
        Size size = rig.Canvas.ClientSize;
        Battery.Assert(size.Width > 0 && size.Height > 0,
            $"夹具自证：画布得有尺寸（实际 {size}），否则两次渲染都是一张空图，0 处不同是恒真的");
        using var before = new System.Drawing.Bitmap(size.Width, size.Height);
        rig.Canvas.RenderFrameForTest(before);
        int revision = rig.P.Revision;

        using var frame = new System.Drawing.Bitmap(size.Width, size.Height);
        rig.Canvas.RenderFrameForTest(frame);
        overlay.Begin(frame, "正在分析拼缝", 4);
        overlay.Report(2, 4);

        Battery.Assert(overlay.ActiveForTest && overlay.HasBlurForTest,
            "正向对照：此刻遮罩确实开着且手里有一张模糊底——上面那帧「没变」才是挡住了「虚化漏进画布」这个缺陷" +
            $"（Active={overlay.ActiveForTest} HasBlur={overlay.HasBlurForTest}）。" +
            "注意这里读的是 ActiveForTest 而不是 Control.Visible：后者是有效可见性，" +
            "工装里那张窗体从没 Show 过，它就恒为 false，拿它判等于判了个恒假");
        using var during = new System.Drawing.Bitmap(size.Width, size.Height);
        rig.Canvas.RenderFrameForTest(during);
        Battery.AssertEqual(0, CountDifferentPixels(before, during),
            "遮罩开着的时候画布自己那一帧必须逐处相同（每 3 像素取样一枚）：它是兄弟控件，不是画布的一层叠图");
        Battery.AssertEqual(revision, rig.P.Revision, "挂遮罩不改任何拼合参数");
        overlay.End();
        using var after = new System.Drawing.Bitmap(size.Width, size.Height);
        rig.Canvas.RenderFrameForTest(after);
        Battery.AssertEqual(0, CountDifferentPixels(before, after), "收掉遮罩之后同样一字不变（不许留下暗纱残影）");
        Battery.Evidence($"遮罩开→报数→收 三档，画布那一帧逐处相同（0 处取样差异），修订号仍 {revision}");
    }

    /// <summary>
    /// 用户那句"蓝色的线条不动，线条上下的图片进行移动"落成的判据。
    /// 不问参数、只问<b>屏上那一行</b>：抓上方与抓下方两个方向 × 30%／100%／200% 三个缩放档各拖一趟，
    /// 每趟都要求蓝线的设备行号与按下那一刻一字不差，而<b>两张图里必须有一张真的在屏上动了</b>。
    /// 后半句是这一格的承重墙：只要求"线不动"的话，"什么都没做"也能全绿。
    /// </summary>
    private static void LineScreenRowStaysPutWhilePictureMoves()
    {
        foreach (double zoom in new[] { 0.3, 1.0, 2.0 })
        {
            foreach (bool grabUpper in new[] { false, true })
            {
                string at = $"缩放 {zoom:0.#} · 抓{(grabUpper ? "上方" : "下方")}那张";
                using Rig rig = NewRig(zoom: zoom, origin: new IntPoint(0, 400));
                SetCut(rig, 60);       // 蓝线摆在带正中：两个方向都还有 60 行可滑，也都够不到边界
                rig.Canvas.CreateControl();

                // 按下点：<b>带内</b>、蓝线某一侧，且离把手 14 设备像素以上（把手纵向各出 10）。
                // 为什么不写"取带首与蓝线的中点"那种取法：小缩放档下带一共才三十来个设备像素，
                // 骑在蓝线正中的把手能吃掉半个带，取中点就命中把手那一档——
                // 它只开关微调档、<b>不开拖动手势</b>，于是这一趟压根什么都没做，"线没动"就成了恒真判据。
                int lineDevice = ToDeviceY(rig, CutLineY(rig));
                var start = new Point(BandCentreX(rig), grabUpper ? lineDevice - 14 : lineDevice + 14);
                Battery.Assert(rig.Canvas.HitTestSeamEditRegion(start) is
                {
                    Kind: StitchCanvasControl.SeamHitKind.OverlapBand
                        or StitchCanvasControl.SeamHitKind.AlignmentAdjust
                }, $"夹具自证：{at} 的按下点 {start} 得命中带（带 {rig.Canvas.Viewport.CanvasToDevice(rig.Geometry.OverlapRect)}、" +
                   $"把手 {HandleRect(rig)}），实际命中 " +
                   $"{rig.Canvas.HitTestSeamEditRegion(start)?.Kind.ToString() ?? "无"}");

                int overlapBefore = rig.Current.OverlapLength;
                IntPoint viewportBefore = rig.TopLeft;
                System.Drawing.Rectangle rect1Before = rig.Canvas.Viewport.CanvasToDevice(rig.Geometry.Rect1);
                System.Drawing.Rectangle rect2Before = rig.Canvas.Viewport.CanvasToDevice(rig.Geometry.Rect2);

                // 抓上方那张 ⇒ 往上推；抓下方那张 ⇒ 往下推。两个方向都是把重叠带压小。
                DragAndRead(rig, start, grabUpper ? -12 : 12);

                Battery.Assert(rig.Current.OverlapLength != overlapBefore,
                    $"夹具自证：{at} 这一趟真的动了重叠（{overlapBefore}→{rig.Current.OverlapLength}）");
                Battery.AssertEqual(lineDevice, ToDeviceY(rig, CutLineY(rig)),
                    $"{at}：蓝线的设备行号必须还在 {lineDevice} 那一行——它就是用户盯着的那条参照线");

                System.Drawing.Rectangle rect1After = rig.Canvas.Viewport.CanvasToDevice(rig.Geometry.Rect1);
                System.Drawing.Rectangle rect2After = rig.Canvas.Viewport.CanvasToDevice(rig.Geometry.Rect2);
                Battery.Assert(rect1After != rect1Before || rect2After != rect2Before,
                    $"{at}：线没动，那就必须有<b>图片</b>在屏上动了（两块矩形都纹丝不动意味着这一趟什么都没做，" +
                    "上面那句『线不动』就是恒真的）");
                // 没被抓的那一张就是钉在屏幕上的那一张：它的设备矩形必须一字不动，另一张必须跟着手走。
                if (grabUpper)
                {
                    Battery.AssertEqual(rect2Before, rect2After, $"{at}：没被抓的下方那张在屏幕上一字不动");
                    Battery.Assert(rect1After != rect1Before, $"{at}：被抓的上方那张必须跟着手走");
                    Battery.Assert(rig.TopLeft != viewportBefore,
                        $"{at}：这一档的『线不动』是靠视口补偿换来的，所以视口必须真的动过");
                }
                else
                {
                    Battery.AssertEqual(rect1Before, rect1After, $"{at}：没被抓的上方那张在屏幕上一字不动");
                    Battery.Assert(rect2After != rect2Before, $"{at}：被抓的下方那张必须跟着手走");
                    Battery.AssertEqual(viewportBefore, rig.TopLeft,
                        $"{at}：抓下方那张时压根不需要补偿，视口一个像素都不许动");
                }
            }
        }
        Battery.Evidence("30%/100%/200% × 抓上方/抓下方 六趟：蓝线设备行号全程不动，" +
                         "而没被抓的那一张在屏幕上一字不动、被抓的那一张跟着手走");
    }

    /// <summary>只给忙碌遮罩用的极小夹具：一个真的 BusyOverlay 加两张现成的帧（条纹／纯色）。
    /// 不需要宿主窗口——进度条那一读数现在由 Report 与 OnPaint 共用的同一个公式交出，
    /// 离屏工装里 OnPaint 一次都不跑也量得到。</summary>
    private sealed class OverlayRig : IDisposable
    {
        public BusyOverlay Overlay { get; }
        public System.Drawing.Bitmap StripedFrame { get; }
        public System.Drawing.Bitmap FlatFrame { get; }

        public int CanvasWidth => StripedFrame.Width;
        public int CanvasHeight => StripedFrame.Height;

        public OverlayRig()
        {
            // 条纹底：横向也有纵向的颜色变化，缩小与模糊都改得动它 ⇒ 差值量得出来。
            StripedFrame = new System.Drawing.Bitmap(400, 300);
            using (var g = System.Drawing.Graphics.FromImage(StripedFrame))
            {
                g.Clear(System.Drawing.Color.White);
                for (int y = 0; y < 300; y += 6)
                {
                    using var black = new System.Drawing.SolidBrush(System.Drawing.Color.Black);
                    g.FillRectangle(black, y % 12 == 0 ? 0 : 20, y, 400, 3);
                }
            }
            FlatFrame = new System.Drawing.Bitmap(400, 300);
            using (var g = System.Drawing.Graphics.FromImage(FlatFrame))
                g.Clear(System.Drawing.Color.White);

            Overlay = new BusyOverlay { Size = new Size(400, 300) };
        }

        /// <summary>进度条当前的填充宽度。</summary>
        public int BarFill() => Overlay.LastBarFillWidthForTest;

        public void Dispose()
        {
            Overlay.Dispose();
            StripedFrame.Dispose();
            FlatFrame.Dispose();
        }
    }


    /// <summary>主窗口级夹具：真建一个 MainForm、真导入两张 PNG，走产品自己的事件接线。</summary>
    private sealed class FormRig : IDisposable
    {
        public required MainForm Form { get; init; }
        public required StitchCanvasControl Canvas { get; init; }
        public required Project P { get; init; }

        public Seam Seam => P.Doc.Seams[0];

        /// <summary>带内第 rowInBand 行的设备坐标（1:1、视口原点 0 时就是画布坐标）。</summary>
        public Point PointInBand(int rowInBand) => new(30, BandTopY + rowInBand);

        /// <summary>蓝线上的设备坐标。
        /// 纵向取<b>当下</b>那条线的画布行号（默认档它就在带首，蓝线挪过之后就不是了）；
        /// 横向避开骑在线正中的那枚「调整」把手——按在把手上只会开关微调档、不开拖动手势，
        /// 于是"拖 20 设备像素"会变成"什么都没拖"。</summary>
        public Point PointOnCutLine()
        {
            int lineRow = Canvas.Viewport.CanvasToDevice(
                new IntRect(0, P.Layout.DescribeSeam(Seam)!.CutYCanvas, 1, 1)).Y;
            return new Point(Canvas.AdjustHandleDeviceRect(Seam.Id).Right + 4, lineRow);
        }

        public void DragCutLineDown(int deviceDelta)
        {
            var start = PointOnCutLine();
            Canvas.MousePress(MouseButtons.Left, Keys.None, start);
            Canvas.MouseMoveTo(new Point(start.X, start.Y + deviceDelta / 2));
            Canvas.MouseMoveTo(new Point(start.X, start.Y + deviceDelta));
            Canvas.MouseRelease(new Point(start.X, start.Y + deviceDelta));
        }

        public void Dispose() => Form.Dispose();

        public static FormRig New()
        {
            string dir = Path.Combine(Path.GetTempPath(), "ls-align-" + Guid.NewGuid().ToString("N")[..6]);
            Directory.CreateDirectory(dir);
            try
            {
                RgbaImage page = LongScrollTests.StripedPage(Width, Height + Height - Overlap);
                (RgbaImage before, RgbaImage after, _) =
                    TestProject.CutScrollPair(page, 0, Height, Overlap);
                string[] files = { Path.Combine(dir, "a.png"), Path.Combine(dir, "b.png") };
                File.WriteAllBytes(files[0], PngEncoder.EncodeToBytes(before));
                File.WriteAllBytes(files[1], PngEncoder.EncodeToBytes(after));

                var form = new MainForm();
                form.CreateControl();
                // 第六十改：导入闸要求先有模式；夹具直接写字段（不走 Pick，避免多一笔 Mutate 混进账）。
                form.ProjectForTest.Doc.Mode = StitchMode.LongScroll;
                form.ImportPaths(files);
                Project p = form.ProjectForTest;
                p.Mutate("切到长截图并设定重叠", _ =>
                {
                    p.Doc.Mode = StitchMode.LongScroll;
                    Seam seam = p.Doc.Seams[0];
                    seam.AutoOffset = new IntPoint(0, BandTopY);
                    seam.SetOverlap(Overlap);
                    seam.Analyzed = true;
                });
                form.CanvasForTest.Viewport.SetOneToOne();
                form.CanvasForTest.Viewport.MoveTopLeftTo(IntPoint.Zero);
                form.CanvasForTest.SelectSeam(p.Doc.Seams[0].Id);
                form.SwitchToolPanelForTest(ToolKind.Seam);
                Battery.AssertEqual(Overlap, p.Doc.Seams[0].OverlapLength, "夹具自证：这条缝真有 120 行重叠");
                Battery.AssertEqual(0, p.Doc.Seams[0].CutPosition, "夹具自证：起点是默认档蓝线 0");
                return new FormRig { Form = form, Canvas = form.CanvasForTest, P = p };
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch { /* 临时目录清不掉不影响判据 */ }
            }
        }
    }

    // ───────────────────────── 守卫（注缺陷，要求必须变红） ─────────────────────────

    /// <summary>
    /// 需求一猜的那个缺陷形状：命中区改用「裁后两个矩形取交」。
    /// 不改产品源文件，而是把那个算法当场复现一遍并证明它给不出任何可点区——
    /// 于是常驻判据（读 BandCanvasRect 那一格）与旧算法的差别是可翻红的。
    /// </summary>
    private static void HitRegionFromIntersectionWouldBeEmpty()
    {
        using Rig rig = NewRig();
        SetCut(rig, 40);
        SeamGeometry g = rig.Geometry;

        IntRect oldRegion = g.BeforeRect.Intersect(g.AfterRect);
        Battery.Assert(oldRegion.IsEmpty, "变异没注入：这个夹具里裁后可见矩形仍相交");
        int hitsOld = 0, hitsNew = 0;
        for (int y = 0; y < g.CanvasMainAxisExtent; y++)
        {
            if (oldRegion.Contains(new IntPoint(30, y))) hitsOld++;
            if (rig.Canvas.HitTestSeamEditRegion(new Point(30, y)) is not null) hitsNew++;
        }
        Battery.AssertEqual(0, hitsOld, "旧算法居然还能点到东西——变异没生效");
        Battery.Assert(hitsNew > Overlap / 2, $"现行算法必须真能点到带区，实际 {hitsNew} 行");
        Battery.Evidence($"旧算法可点 {hitsOld} 行、现行 {hitsNew} 行：这一族差别确实判得出来");
    }

    /// <summary>
    /// 需求四那个「每帧整块清缓存」的缺陷形状：拿 TileCache 做两种失效策略的对照。
    /// 判据读 FullInvalidations 计数，所以这一格要证明的就是「这个计数真的会动」。
    /// </summary>
    private static void FullCacheClearIsWhatTheCounterMeasures()
    {
        using Rig rig = NewRig();
        var tiles = new TileCache(new TileRenderer(rig.Composer));
        CanvasLayout layout = rig.P.Layout;
        for (int ty = 0; ty < TileRenderer.TilesDown(layout.CanvasSize.Height); ty++)
            tiles.Get(layout, 0, ty, revision: 1);
        Battery.AssertEqual(3, tiles.Count, "夹具自证：三块瓦片都进了缓存");
        // 第一次取数必然带一次"修订号从 -1 变成 1"的整块清空，那是起点，不是被测行为
        long clearsAfterFill = tiles.FullInvalidations;
        Battery.Assert(clearsAfterFill >= 1, "起点自证：首轮取数就该记下一次整块清空");

        int dropped = tiles.DropRegion(ExpectedBand);
        Battery.Assert(dropped is > 0 and < 3, $"带区只该丢掉与之相交的那几块，实际丢了 {dropped}");
        Battery.AssertEqual(clearsAfterFill, tiles.FullInvalidations, "丢带区不算整块清空（现行路径）");
        int keptAfterDrop = tiles.Count;
        Battery.Assert(keptAfterDrop == 3 - dropped,
            $"带外那 {3 - dropped} 块必须还留着（这就是拖动时省下来的部分），实际 {keptAfterDrop}");

        tiles.Get(layout, 0, 0, revision: 2);      // 注入旧行为：每帧换修订号
        Battery.AssertEqual(clearsAfterFill + 1, tiles.FullInvalidations,
            "旧行为（每帧改修订号）必须被这个计数抓到");
        Battery.AssertEqual(1, tiles.Count,
            "整块清空之后缓存里只剩刚要的这一块——带外那几块全被白白重画了一遍");
        Battery.Evidence($"DropRegion：丢 {dropped}/3、留 {keptAfterDrop}、整块清 0 次；" +
                         $"改修订号：整块清 1 次、缓存只剩 1 块");
    }

    /// <summary>
    /// 需求一那半的守卫：面板读数不跟修订号刷新时，它一定停在旧值——
    /// 也就是「<c>RefreshDisplayedSeam</c> 那一句是承重的」，不是装饰。
    /// </summary>
    private static void PanelStaysStaleWithoutRefresh()
    {
        using Rig rig = NewRig();
        SetCut(rig, 0);
        var panel = new ToolPanel(rig.P, new FileSourceResolver(), rig.Composer);
        panel.Tool = ToolKind.Seam;
        panel.SeamId = rig.Current.Id;
        Battery.AssertEqual(0, panel.CutRowValueForTest, "起点：屏上读数与参数一致（都是 0）");

        // 只改参数、不走主窗口那条刷新通路 —— 这就是第十一轮之前的样子
        rig.P.Mutate("外部改蓝线", _ => rig.Current.ApplyCut(35));
        Battery.AssertEqual(35, rig.Current.CutPosition, "参数确实变了");
        Battery.AssertEqual(0, panel.CutRowValueForTest,
            "变异没生效：不调刷新那句，读数本该停在旧值");

        panel.RefreshDisplayedSeam();
        Battery.AssertEqual(35, panel.CutRowValueForTest, "刷新之后读数必须跟上参数");
        Battery.Evidence("不刷新停在 0、刷新后 35：RefreshDisplayedSeam 是承重的那一句");
    }

    /// <summary>
    /// 第十二轮起，蓝线不再有"绕开 setter 的后备字段"可写坏——所以这一格改注另一种缺陷形状：
    /// 直接把 <see cref="Seam.CutY"/> 写到带外（模拟手工编辑过的项目文件、或有人新增写入口），
    /// 要求<b>读出口与布局都必须夹</b>，并且"被夹掉"这件事在去重账里看得见。
    /// 这条守卫守的是"夹取只发生在读出口与布局"这一处，不是"参数永远合法"。
    /// </summary>
    private static void CutWrittenOutOfBandIsCaught()
    {
        using Rig rig = NewRig();
        Seam seam = rig.Current;
        seam.CutY = Overlap + 500;              // 裸写核心字段：蓝线跑到带外 500 行
        Battery.Assert(seam.CutPosition == Overlap, "读出口必须夹回带尾");
        Battery.Assert(!seam.CropInvariantHolds || seam.AfterCropTop + seam.BeforeCropBottom == seam.OverlapLength,
            "夹过之后两侧之和仍要等于重叠，否则布局拿到的就是半套参数");

        SeamGeometry g = rig.Geometry;
        Battery.Assert(g.CutInsideBand, "布局自己也要夹：生效蓝线不许在带外");
        Battery.Assert(!g.CropsAppliedAsRequested,
            "参数与落位不一致这件事必须被这本账抓到（不许悄悄吞）");
        Battery.Assert(g.Overlap == Overlap, "带高不许因为蓝线写坏而跟着变");

        seam.CutY = -30;
        Battery.AssertEqual(0, seam.CutPosition, "负数同样夹到带首（读出口夹）");
        Battery.Evidence("裸写 CutY 到带外：读出口夹、布局夹、账上标出「参数与落位不一致」，带高不受影响");
    }

    /// <summary>
    /// 需求七那句"两个动作不能共用一个字段"的守卫：
    /// 拿两个临时覆盖各走一遍，要求<b>只有该动的那一个量动</b>。
    /// 如果哪天有人把蓝线与对齐塞回同一个字段，这一格会立刻红。
    /// </summary>
    private static void SharedFieldWouldCrossOverwrite()
    {
        using Rig rig = NewRig();
        SetCut(rig, 40);
        Seam seam = rig.Current;
        IntPoint alignmentBefore = seam.AlignmentOffset;
        int cutBefore = seam.CutY;

        // 只覆盖蓝线 ⇒ 对齐必须原样
        CanvasLayout cutOnly = CanvasLayout.Build(rig.P.Doc, null,
            new Dictionary<string, SeamAlignment> { [seam.Id] = new SeamAlignment(alignmentBefore.Y, 90) });
        Battery.AssertEqual(Overlap, cutOnly.DescribeSeam(seam)!.Overlap,
            "只改蓝线却把重叠也改了 ⇒ 两个手势又共用一个字段了");
        Battery.AssertEqual(BandTopY, cutOnly.DescribeSeam(seam)!.OverlapStartY, "带首不许因蓝线而挪");
        Battery.AssertEqual(90, cutOnly.DescribeSeam(seam)!.CutPosition, "蓝线要按覆盖值生效");

        // 只覆盖对齐 ⇒ 蓝线的带内行号必须原样
        CanvasLayout alignOnly = CanvasLayout.Build(rig.P.Doc, null,
            new Dictionary<string, SeamAlignment> { [seam.Id] = new SeamAlignment(-(Overlap - 30), cutBefore) });
        SeamGeometry g = alignOnly.DescribeSeam(seam)!;
        Battery.AssertEqual(Overlap - 30, g.Overlap, "对齐要按覆盖值生效");
        Battery.AssertEqual(cutBefore, g.CutPosition, "只改对齐却把蓝线也改了 ⇒ 又是共用一个字段");
        Battery.AssertEqual(BandTopY + 30, g.OverlapStartY, "带首要跟着对齐挪");
        // 夹具自证：两次覆盖的蓝线/对齐值本来就不同，否则"只有该动的动了"这句话是空转
        Battery.Assert(90 != cutBefore && -(Overlap - 30) != alignmentBefore.Y,
            "夹具自证：两次覆盖用的必须是不同的蓝线与对齐值");
        Battery.Evidence("两次单字段覆盖：蓝线那次重叠未变、对齐那次蓝线行号未变 ⇒ 两条通路确实分开");
    }

    /// <summary>
    /// 需求八点名"不许把 FixedSide 判据降级成只查字段"，所以这一格守的是<b>补偿本身有没有承重</b>：
    /// 拿同一次拖动后的公共坐标，按"没补偿的视口"（= 按下那一刻那个）再换算一遍设备矩形，
    /// 要求它与界面记下的 <c>rect2DeviceAfter</c> <b>不同</b>。
    /// 哪天有人删掉 <c>ApplyFixedSideAnchor</c> 那一段，屏上就会变成"图2 被拖走、图1 钉住"
    /// ——正是需求一说的固定侧反了；那一删会立刻让
    /// <c>align/固定后一段：拖对齐前后图2 的屏幕矩形一字不动、图1 跟着动</c> 与这一格一起红。
    /// 不改真实源文件：这里只是把"如果没有那一步"的那份数当场算一遍（精度红线 §3）。
    /// </summary>
    private static void WithoutAnchorCompensationImage2WouldDrift()
    {
        using Rig rig = NewRig(zoom: 1.0, origin: new IntPoint(0, 400));
        SetCut(rig, 40);
        SetFixedBefore(rig);
        IntPoint viewportBase = rig.TopLeft;
        int rect2CanvasBefore = rig.Geometry.Rect2.Y;
        var traceBefore = (StitchCanvasControl.SeamEditTrace)rig.Canvas.LastSeamEditTraceForTest!;

        Point start = BandAboveLinePoint(rig);   // 抓上方那张 ⇒ 屏上钉住图2 ⇒ 这一趟必须补偿视口
        DragAndRead(rig, start, 20);            // 往下拖 20 ⇒ 多压 20 行

        SeamGeometry g = rig.Geometry;
        var trace = (StitchCanvasControl.SeamEditTrace)rig.Canvas.LastSeamEditTraceForTest!;
        int deviceWithCompensation = trace.Rect2DeviceAfter.Y;
        int deviceWithoutCompensation = g.Rect2.Y - viewportBase.Y;   // 视口留在按下那一刻 = 没有那一步补偿

        Battery.Assert(rig.TopLeft != viewportBase, "夹具自证：这一趟视口确实被补偿过");
        Battery.Assert(deviceWithCompensation != deviceWithoutCompensation,
            $"撤掉补偿也不会改变屏上位置 ⇒ 那一步是空转（补偿后 {deviceWithCompensation} / 不补偿 {deviceWithoutCompensation}）");
        Battery.AssertEqual(rect2CanvasBefore - viewportBase.Y, deviceWithCompensation,
            "有补偿时图2 的屏幕位置等于拖动前那一个（这才是界面写的那句『固定后一段』）");
        Battery.Assert(!ReferenceEquals(trace, traceBefore), "夹具自证：读到的是这一趟的新流水账");
        Battery.Evidence($"图2 屏幕行：不补偿 {deviceWithoutCompensation}、补偿后 {deviceWithCompensation}" +
                         $"（= 拖动前的 {rect2CanvasBefore - viewportBase.Y}）⇒ 补偿承重");
    }

    /// <summary>
    /// 第十三轮需求四那条口径反转的守卫：旧规则（只夹不换）在带变高时把蓝线留在同一行号，
    /// 于是<b>相对位置</b>会漂（40/120=0.333 → 40/140=0.286）。
    /// 这一格把两套规则并排算出来，要求旧的那一套给出的比例<b>与现行不同</b>，
    /// 从而证明"比例守恒"这条判据不是恒真的——真退回旧实现时它会红。
    /// </summary>
    private static void OldClampOnlyRuleWouldDriftTheCutRatio()
    {
        using Rig rig = NewRig();
        SetCut(rig, 40);
        int oldOverlap = rig.Current.OverlapLength;
        int oldCut = rig.Current.CutPosition;
        int lineBefore = CutLineY(rig);
        int h1 = rig.Geometry.Rect1.Bottom;      // 图1 底边：蓝线画布行号 = h1 − O + Cut

        DragAndRead(rig, BandInteriorPoint(rig), -20);        // 带 120 → 140
        int newOverlap = rig.Current.OverlapLength;
        int newCut = rig.Current.CutPosition;
        Battery.AssertEqual(140, newOverlap, "夹具自证：这一趟真的把带拖高了 20 行");
        Battery.AssertEqual(oldCut + 20, newCut, "现行口径：带内行号同加 20（40→60）");
        Battery.AssertEqual(lineBefore, CutLineY(rig), "现行口径：蓝线的画布行号一字不动");
        Battery.AssertEqual(lineBefore, h1 - newOverlap + newCut,
            "判据自己按那条算式复算一遍，要与布局读出来的行号一致（否则上面那句是空转）");

        // 两种旧口径当场算一遍：它们给出的蓝线行号都必须与起点不同，否则这一格是空话
        int scaledCut = (int)(((long)oldCut * newOverlap + oldOverlap / 2) / oldOverlap);   // 按相对位置换算 = 47
        int clampOnlyCut = Math.Clamp(oldCut, 0, newOverlap);                               // 只夹不换 = 40
        Battery.Assert(h1 - newOverlap + scaledCut != lineBefore,
            $"旧口径①按相对位置换算（{oldCut}→{scaledCut}）会把蓝线挪到第 {h1 - newOverlap + scaledCut} 行" +
            $"（起点 {lineBefore}）：线在用户手里自己跑了");
        Battery.Assert(h1 - newOverlap + clampOnlyCut != lineBefore,
            $"旧口径②只夹不换（行号留在 {clampOnlyCut}）同样会把蓝线挪到第 {h1 - newOverlap + clampOnlyCut} 行");
        Battery.Assert(scaledCut != newCut && clampOnlyCut != newCut,
            "两种旧口径给出的行号都必须与现行口径不同，否则上面那两句反证是空的");
        Battery.Evidence($"带 120→140：现行 Cut {oldCut}→{newCut}、蓝线画布行号恒为 {lineBefore}；" +
                         $"旧口径① Cut={scaledCut} ⇒ 行号 {h1 - newOverlap + scaledCut}，" +
                         $"旧口径② Cut={clampOnlyCut} ⇒ 行号 {h1 - newOverlap + clampOnlyCut}，两者都会让线跑掉");
    }

    /// <summary>
    /// 精度红线 §3：常驻判据
    /// <c>align/固定后一段拖对齐后撤销：参数、画布高、视口、设备矩形一起恢复</c>
    /// 里那句「Viewport.TopLeft 必须等于原始值」到底承不承重？
    ///
    /// 这一格把<b>旧写法</b>当场复现一遍：撤销只做「文档 = 反序列化(快照)」，谁也不问显示层——
    /// 那正是第十四轮之前 <c>Project.Undo</c> 的形状（它压根没有 HistoryStepped 这一步）。
    /// 复现走的是产品自己的"换文档"通路 <see cref="Project.ReplaceDocument"/>（它换文档、触发 Changed、
    /// 但不会发显示层事件），不改任何真实源文件。
    ///
    /// 要求三件事同时成立，缺一格就说明另一格是空的：
    /// ① 参数与画布高在旧写法下<b>照样</b>恢复 ⇒ 常驻判据里"只断言参数"的那半挡不住这个缺陷；
    /// ② 视口与图2 设备矩形在旧写法下<b>不</b>恢复 ⇒ 那两条断言一旦退回旧写法必红；
    /// ③ 同一夹具再走一遍现行通路，撤销必须把视口也带回来 ⇒ 只证"能红"不证"能修"，迁移那一环就没被测。
    /// </summary>
    private static void UndoWithoutDisplaySnapshotLeavesViewportDrifted()
    {
        using Rig rig = NewRig(zoom: 1.0, origin: new IntPoint(0, 400));
        SetCut(rig, 40);
        SetFixedBefore(rig);
        IntPoint viewportBefore = rig.TopLeft;
        int overlapBefore = rig.Current.OverlapLength;
        int heightBefore = rig.CanvasHeight;
        Rectangle rect2Before = rig.Canvas.Viewport.CanvasToDevice(rig.Geometry.Rect2);
        // "改之前"的那份文档 = 旧 Undo 会恢复的东西（快照只是项目文件内容，见 ProjectFile 的类注释）
        string baseline = ProjectFile.Serialize(rig.P.Doc);

        Point start = BandAboveLinePoint(rig);   // 抓上方那张 ⇒ 屏上钉住图2 ⇒ 这一趟必须补偿视口
        rig.Canvas.MousePress(MouseButtons.Left, Keys.None, start);
        rig.Canvas.MouseMoveTo(new Point(start.X, start.Y + 20));
        rig.Canvas.MouseRelease(new Point(start.X, start.Y + 20));
        IntPoint viewportAfterDrag = rig.TopLeft;
        Battery.Assert(viewportAfterDrag != viewportBefore,
            "夹具自证：这一趟真的在屏幕上留下了纯显示补偿");

        // ═══ ①② 注入旧写法：只把文档换回去，显示层一句不问 ═══
        rig.P.ReplaceDocument(ProjectFile.Deserialize(baseline), null);
        Battery.AssertEqual(overlapBefore, rig.Current.OverlapLength,
            "旧写法当然恢复得参数——所以「只断言参数」的那半判据根本挡不住这个缺陷");
        Battery.AssertEqual(heightBefore, rig.CanvasHeight, "旧写法也恢复画布高（它是派生量）");
        Battery.AssertEqual(viewportAfterDrag, rig.TopLeft,
            "变异没生效：旧写法必须把视口留在补偿后的位置（第十四轮的 Undo 就是这样），" +
            "否则常驻判据里那句「视口等于原始值」是空判据");
        Rectangle rect2Drifted = rig.Canvas.Viewport.CanvasToDevice(rig.Geometry.Rect2);
        Battery.Assert(rect2Drifted != rect2Before,
            $"旧写法下图2 的屏幕位置必须与拖动前不同（{rect2Before.Y}→{rect2Drifted.Y}），" +
            "那正是需求一原话说的「参数已经恢复，但画面位置不对」");

        // ═══ ③ 现行通路：同一夹具再拖一趟，撤销必须连视口一起带回"这一趟按下那一刻"的位置 ═══
        // 注意基准：此刻视口还停在旧写法留下的漂移位置上，第二趟的起点就是那里——
        // 现行通路还的是"这一趟留下的补偿"，不是"替用户把他没要求退的漂移也退了"。
        IntPoint baseForSecond = rig.TopLeft;
        Rectangle rect2BeforeSecond = rig.Canvas.Viewport.CanvasToDevice(rig.Geometry.Rect2);
        Point next = BandAboveLinePoint(rig);   // 与第一趟同侧：抓上方那张才留下视口补偿
                                                // （换成 BandInteriorPoint 就抓到下方那张，那一档压根不补偿视口，
                                                // 下面那句"第二趟同样留下了补偿"会红在工装自己身上）
        rig.Canvas.MousePress(MouseButtons.Left, Keys.None, next);
        rig.Canvas.MouseMoveTo(new Point(next.X, next.Y + 20));
        rig.Canvas.MouseRelease(new Point(next.X, next.Y + 20));
        Battery.Assert(rig.TopLeft != baseForSecond, "夹具自证：第二趟同样留下了补偿");
        Battery.Assert(rig.P.CanUndo, "夹具自证：第二趟真的压了一条历史（ReplaceDocument 清了栈，靠这趟重新压上）");
        IntPoint viewportAfterSecondDrag = rig.TopLeft;
        rig.P.Undo();
        Battery.AssertEqual(baseForSecond, rig.TopLeft,
            "现行通路：撤销把这一趟那段纯显示补偿原数还回来了（旧写法缺的就是这一步）");
        Battery.AssertEqual(overlapBefore, rig.Current.OverlapLength, "现行通路同样恢复参数");
        Battery.AssertEqual(rect2BeforeSecond, rig.Canvas.Viewport.CanvasToDevice(rig.Geometry.Rect2),
            "现行通路下屏上那块矩形也回到这一趟按下那一刻的位置");
        Battery.Evidence($"旧写法（只换文档）：参数与画布高照样恢复，视口仍停在补偿后的 {viewportAfterDrag}" +
                         $"（屏上图2 从 {rect2Before.Y} 漂到 {rect2Drifted.Y}）⇒ 常驻判据那两条必红；" +
                         $"现行通路：第二趟 {baseForSecond}→{viewportAfterSecondDrag}→撤销回 {rig.TopLeft}、" +
                         $"图2 屏幕矩形与按下那一刻逐字段相同");
    }
}
