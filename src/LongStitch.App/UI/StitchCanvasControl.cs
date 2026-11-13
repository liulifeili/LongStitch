using LongStitch.Core;
using LongStitch.Core.Geometry;
using LongStitch.Core.Imaging;
using LongStitch.Core.Layout;
using LongStitch.Core.Model;
using LongStitch.Core.Preview;
using LongStitch.App.Diagnostics;
using GdiBitmap = System.Drawing.Bitmap;
using GdiColorMatrix = System.Drawing.Imaging.ColorMatrix;
using GdiImageAttributes = System.Drawing.Imaging.ImageAttributes;
using GdiInterpolation = System.Drawing.Drawing2D.InterpolationMode;
using GdiPixelOffset = System.Drawing.Drawing2D.PixelOffsetMode;
using GdiSmoothing = System.Drawing.Drawing2D.SmoothingMode;

namespace LongStitch.App.UI;

/// <summary>
/// 中央拼合画布。需求里最严的三条都落在这个控件上：
///
///   ① <b>鼠标职责明确</b>（模拟 UI 需求 §4 那张表）：浏览态左键拖动=平移；
///      编辑态左键拖动=移动活动侧；<c>Shift+左键</c>永远=平移；滚轮=滚动；
///      <c>Ctrl+滚轮</c>=以指针为中心缩放。后三者<b>一个字节都不许改动拼合参数</b>。
///   ② <b>方向键一次走 1 个源图像素</b>，与缩放无关（不是 1 屏幕像素）。
///   ③ <b>拖动过程中不写历史</b>：只在松手时提交一条可撤销记录。
///
/// 实现上的关键取舍：拖动期间用 <see cref="CanvasLayout.Build(ProjectDocument, IReadOnlyDictionary{string, IntPoint}?)"/>
/// 生成一份"预览布局"，参数一个字都不改；松手才 <see cref="Project.Mutate"/>。
/// 于是取消拖动 = 丢掉预览布局，不需要回滚任何东西。
/// </summary>
public sealed class StitchCanvasControl : Control
{
    private readonly Project _project;
    private readonly CanvasComposer _composer;
    private readonly TileCache _tiles;

    /// <summary>拖动中生成的预览布局；null 表示没有正在进行的拖动。</summary>
    private CanvasLayout? _previewLayout;

    /// <summary>
    /// 预览帧是否已经排队（第十一轮的合并闸门）。
    ///
    /// ═══ 为什么必须合并 ═══
    /// 鼠标每动一个像素就来一个 MouseMove，而一帧预览要重摆布局、重画带区瓦片、
    /// 记一条流水账。原始做法是"每个事件同步做一遍"，于是 100 个移动 = 100 遍全套重活
    /// ——用户手上的感觉就是"拖动卡、滚轮跟不住"。现在只留最新位置，一帧最多做一遍：
    /// 有句柄时 <see cref="Control.BeginInvoke(System.Action)"/> 排队（消息队列天然把同一帧内的多次移动并成一次），
    /// 没句柄时（电池工装、控件尚未创建窗口句柄）就地执行，保证判据量的还是同一条计算路径。
    /// </summary>
    private bool _previewPending;

    /// <summary>排队期间最后一次收到的鼠标设备位置（合并后就地取这一个，中间帧丢掉不影响结果）。</summary>
    private Point _latestMouse;

    /// <summary>
    /// 真正执行过的预览帧数。判据 <c>crop/100 次鼠标移动只换几帧预览：不整块清缓存、不逐帧写日志</c>
    /// 读的就是它：100 次移动后这个数必须远小于 100。
    /// </summary>
    public long PreviewFramesForTest { get; private set; }

    /// <summary>上一次悬停命中结果（只在它<b>变了</b>的时候才往外发提示，见 MouseMoveTo 的浏览分支）。</summary>
    private SeamHit? _lastHoverHit;

    /// <summary>上一条 seam.*.preview 记出的时刻（时间限流，与像素距离限流并用，见 LogPreview）。</summary>
    private long _previewLoggedAtTicks = long.MinValue;

    /// <summary>预览帧之间至少隔这么多毫秒才记一条日志（需求五：最多每 50–100ms 一条）。</summary>
    public const int PreviewLogMinIntervalMs = 60;

    /// <summary>
    /// 预览帧只重画带区时向外扩这么多<b>设备</b>像素：拼缝线的把手（26 px）、
    /// 线上方的状态字（约 20 px）与拖动浮标都要落在同一块里，否则它们会在带外留下残影。
    /// </summary>
    private const int PreviewDirtyInflateDevicePx = 64;

    /// <summary>上一帧预览重画过的设备区域（下一帧与之一起作废，见 <see cref="InvalidatePreviewArea"/>）。</summary>
    private Rectangle _lastPreviewDirty = Rectangle.Empty;

    /// <summary>瓦片失效计数。项目参数变、或预览布局变，都要让它 +1。</summary>
    private int _renderRevision;

    private int _projectRevisionSeen = -1;

    // ── 交互状态 ────────────────────────────────────────────
    //
    // ═══ 为什么"已选中拼缝"不能决定鼠标行为 ═══
    // 之前 OnMouseDown 里有一条 `if (SelectedSeamId is not null) → MoveSeam`，
    // 后果是：只要进过拼缝编辑，之后点画布任何地方都在拖那条缝，整个画布再也推不动。
    // "当前选中了哪条缝"与"这次按下有没有点在这条缝上"是两个独立条件，
    // 拖动模式只由后者决定：命中拼缝线或重叠带才进编辑，否则一律平移画布。
    private enum DragMode
    {
        /// <summary>没有进行中的手势。</summary>
        None,

        /// <summary>浏览：平移整个画布（普通左键点空白、Shift+左键、中键、滚轮都归这一类）。</summary>
        PanCanvas,

        /// <summary>按在拼缝编辑区里，还没越过"单击 vs 拖动"的阈值。</summary>
        PendingSeamClick,

        /// <summary>越过阈值后确定在调这条缝的<b>位移</b>（竖拼／横拼那一档，改的是生效位移）。</summary>
        MoveSeam,

        /// <summary>
        /// 越过阈值后确定在<b>滑动这条缝上方或下方那一张图</b>（长截图那一档）。
        ///
        /// ═══ 第十四轮末次交互重设计：这一档取代了原来的「拖蓝线」与「拖红箭头」两档 ═══
        /// 用户看到的参照物是那条蓝线：<b>线钉在屏幕那一行不动，手里的图在线下面滑</b>。
        /// 于是"抓下方那张往下拖 d 行"＝重叠少 d 行、带内行号也少 d 行（两者在
        /// <c>蓝线画布行号 = H1 − O + Cut</c> 里对消），"抓上方那张往下拖 d 行"＝重叠多 d 行、
        /// 带内行号不动，蓝线在画布里往上走 d 行、由视口补偿钉回屏幕原地。
        /// 两档都只走 Core 的 <see cref="Seam.SlidePicture"/> 那一个出口。
        /// 旧的两档为什么撤：它们要求用户先理解"固定侧"再理解"蓝线 vs 对齐"，
        /// 而这两个概念在屏上根本看不出来——用户只会觉得"我拖的东西自己乱跑"。
        /// </summary>
        SlidePicture,

        /// <summary>点选切分位置。</summary>
        SplitClick,
    }
    private DragMode _drag = DragMode.None;
    private Point _downAt;
    private Point _lastAt;
    private string? _seamUnderCursor;

    /// <summary>按下那一刻的完整命中结果：它决定这次手势进 <see cref="DragMode.MoveSeam"/> 还是 <see cref="DragMode.SlidePicture"/>。</summary>
    private SeamHit? _hitUnderCursor;

    private IntPoint _dragBaseOffset;
    private IntPoint _dragDelta;          // 本次拖动的累计源像素位移
    private bool _dragStarted;

    /// <summary>拖动开始时的视口原点：固定后一段的补偿每帧都从这里算，避免增量累加漂移。</summary>
    private IntPoint _dragViewportBase;

    /// <summary>上一条 seam.edit.preview 记过的设备位置（限流用，见 LogPreview）。</summary>
    private Point _previewLoggedAt = new(int.MinValue, int.MinValue);

    /// <summary>本次动作里"撞上横向锁"是否已经提示过（按住鼠标横向拖只报一次）。</summary>
    private bool _lockNoticed;

    /// <summary>平移手势起点：视口原点与项目修订号。canvas.pan.end 用它们说明"只动了视口、参数没动"。</summary>
    private IntPoint _panStartTopLeft;
    private int _panStartRevision;

    /// <summary>本次拼缝编辑手势的流水账（一次按下开一本，提交或取消后合上）。</summary>
    private SeamEditTrace? _trace;

    /// <summary>最近一次拼缝编辑的流水账。公开只为一件事：让电池能直接核对日志里那些字段本身。</summary>
    public SeamEditTrace? LastSeamEditTraceForTest { get; private set; }

    /// <summary>
    /// 一次拼缝拖动的日志流水账。字段与 <c>seam.edit.*</c> 那些事件里写的**同名**，
    /// 判据读的就是这份数——所以界面"看起来动了"与日志"记下了什么"不可能各说一套。
    /// 需求里两条互相冲突的约束（拖动拼缝不许动视口 / 固定后一段时要能看到前一段在动）
    /// 在这里被分开记录：固定前一段时 <see cref="ViewportAfter"/> 恒等于起点，
    /// 固定后一段时它按补偿量整体平移，而 <see cref="Mutated"/> 与修订号只跟着参数走。
    /// </summary>
    public sealed class SeamEditTrace
    {
        /// <summary>每越过这么多个设备像素才记一条 seam.edit.preview（见 LogPreview 的说明）。</summary>
        public const int PreviewLogStepDevicePx = 12;

        public required string SeamId { get; init; }
        /// <summary>按下时命中的是哪一块：蓝线还是重叠带。写日志与判据都读它。</summary>
        public required SeamHitKind Hit { get; init; }
        /// <summary>按下时判定的"抓的是这条缝上方还是下方那张图"。</summary>
        public FixedSide GestureSide { get; set; }
        /// <summary>生效的固定侧（来自参数，手势不改它）。</summary>
        public FixedSide FixedSide { get; set; }
        /// <summary>PendingSeamClick / MoveSeam，与拖动的实际阶段一致。</summary>
        public string DragMode { get; set; } = "PendingSeamClick";
        public IntPoint BeforeOffset { get; set; }
        public IntPoint RequestedOffset { get; set; }
        public IntPoint EffectiveOffset { get; set; }
        public IntPoint SourceDelta { get; set; }
        public Point DeviceDelta { get; set; }
        public IntPoint ViewportBefore { get; set; }
        public IntPoint ViewportAfter { get; set; }
        public double Zoom { get; set; }
        public int RevisionBefore { get; set; }
        /// <summary>被模式锁掉的横向位移（长截图里就是用户想横挪的那一段）。</summary>
        public int RejectedX { get; set; }
        /// <summary>本次手势最终有没有产生 Project.Mutate。</summary>
        public bool Mutated { get; set; }
        /// <summary>有没有真的往撤销栈里压了一条。</summary>
        public bool UndoPushed { get; set; }

        /// <summary>
        /// 视口这次有没有被改动。
        /// <b>固定前一段</b>（含所有蓝线手势）必须为 false——那一档不许偷偷补偿（需求一）；
        /// <b>固定后一段</b>的对齐手势必须为 true，而且改的恰好是"图2 顶边在画布里挪的那几行"：
        /// 那是把图2 钉回屏幕原地的<b>纯显示</b>补偿（判据
        /// <c>align/固定后一段：拖对齐前后图2 的屏幕矩形一字不动、图1 跟着动</c>）。
        /// 位移档从第八轮起就是同一个形状（固定后一段靠视口补偿）。
        /// </summary>
        public bool ViewportChanged => ViewportBefore != ViewportAfter;

        // ═══ 第十二轮曾把裁切档拆成"蓝线"与"对齐"两族事件；第四十一改又并回一族 ═══
        // 旧口径里这条缝上有两个可拖的东西（蓝线只换这几行重复内容要哪一份、红箭头只改两张图
        // 对齐在哪），所以事件名分成 seam.cut.* 与 seam.alignment.*。
        // 现在长截图只剩一条手势（滑动上方或下方那张图，蓝线是参照物），两个前缀也就并成
        // **seam.slide.***；位移档（竖拼/横拼）仍走 seam.edit.*，两档靠 EventPrefix 分。
        // 三档共用同一个 trace 与同一批字段名——日志与判据读同一份数，不许各摆一套。

        /// <summary>本次手势是否走裁切档（长截图=true）。</summary>
        public bool CropMode { get; set; }

        /// <summary>本次手势是不是"拖红箭头改对齐"（false 且 CropMode=true 就是拖蓝线）。</summary>
        public bool AlignMode { get; set; }

        /// <summary>这条缝的总重叠行数（蓝线手势全程不许改它；对齐手势正是来改它的）。</summary>
        public int OverlapLength { get; set; }

        /// <summary>手势开始时的切点（带内行号）。</summary>
        public int CutBefore { get; set; }

        /// <summary>切点请求值（用户拖到哪）。</summary>
        public int RequestedCut { get; set; }

        /// <summary>切点生效值（夹进 [0, O] 之后）。</summary>
        public int EffectiveCut { get; set; }

        /// <summary>生效的前一段底部裁切量（= O − 切点）。</summary>
        public int EffectiveBeforeCrop => OverlapLength - EffectiveCut;

        /// <summary>生效的后一段顶部裁切量（= 切点）。</summary>
        public int EffectiveAfterCrop => EffectiveCut;

        /// <summary>被夹掉的切点行数（顶到带首/带尾时不为 0，必须回话）。</summary>
        public int RejectedCut { get; set; }

        /// <summary>手势开始时的<b>对齐位移</b>（核心量，以紧接位置为零点）。</summary>
        public IntPoint AlignmentOffsetBefore { get; set; }

        /// <summary>手势结束/这一帧的<b>对齐位移</b>。与 Before 一比就知道"这次只改了蓝线还是只改了对齐"。</summary>
        public IntPoint AlignmentOffsetAfter { get; set; }

        /// <summary>图1 的完整公共坐标矩形（Rect1，未切重叠）。</summary>
        public IntRect Rect1 { get; set; }

        /// <summary>图2 的完整公共坐标矩形（Rect2）。</summary>
        public IntRect Rect2 { get; set; }

        /// <summary>重叠带 = Rect1 ∩ Rect2（红框、命中区那一个）。</summary>
        public IntRect OverlapRect { get; set; }

        /// <summary>手势开始时（按下那一刻）的重叠带；与 <see cref="OverlapRect"/> 一比就知道带被拖长了还是拖短了。</summary>
        public IntRect OverlapRectBefore { get; set; }

        // ═══ 第十三轮需求二/七：屏幕（设备）坐标那一对 ═══
        // 公共坐标只能证明"数据模型里谁动了"，证明不了"用户在窗口里看见谁没动"。
        // 固定侧的语义是屏幕上的事：Rect1/Rect2 落到屏幕上那两块（已过视口与缩放换算）
        // 才判得出"固定前一段时图1 真的原地不动""固定后一段时图2 真的原地不动"。
        // 手势开始那一帧按<b>当时的视口</b>算一次，之后每一帧按<b>那一帧的视口</b>算，
        // 于是界面自己有没有偷偷补偿视口，日志里这一对读数直接就能看出来。

        /// <summary>图1 在<b>设备像素</b>里的矩形（按下那一刻）。</summary>
        public Rectangle Rect1DeviceBefore { get; set; }

        /// <summary>图1 在设备像素里的矩形（这一帧/提交那一刻）。</summary>
        public Rectangle Rect1DeviceAfter { get; set; }

        /// <summary>图2 在设备像素里的矩形（按下那一刻）。</summary>
        public Rectangle Rect2DeviceBefore { get; set; }

        /// <summary>图2 在设备像素里的矩形（这一帧/提交那一刻）。</summary>
        public Rectangle Rect2DeviceAfter { get; set; }

        /// <summary>蓝线在带里的相对位置（按下那一刻；需求四那条"相对位置不变"就是比这两个数）。</summary>
        public double CutRatioBefore { get; set; }

        /// <summary>蓝线在带里的相对位置（这一帧/提交那一刻）。</summary>
        public double CutRatioAfter { get; set; }

        // ═══ 第十四轮末次交互重设计：「线不动、图动」要能被日志直接判出来 ═══
        // 重叠 O 与带内行号 Cut 都会变，画布高也会变，所以"蓝线没动"这件事
        // 在公共坐标那一列里根本读不出来——必须记下它在<b>画布</b>里的绝对行号，
        // 以及它落到<b>屏幕</b>上的那一行（过了视口与缩放换算）。
        // 抓下方那张时两个数都不变；抓上方那张时画布行号会变、屏幕行号靠视口补偿仍不变。
        // 判据读的就是这一对：<c>align/滑动一张图时蓝线的屏幕行号一字不动</c>。

        /// <summary>蓝线在画布坐标里的主轴行号（按下那一刻）。</summary>
        public int LineCanvasRowBefore { get; set; }

        /// <summary>蓝线在画布坐标里的主轴行号（这一帧/提交那一刻）。</summary>
        public int LineCanvasRowAfter { get; set; }

        /// <summary>蓝线在<b>设备像素</b>里的主轴坐标（按下那一刻）。纵向模式是 Y，横向模式是 X。</summary>
        public int LineDeviceBefore { get; set; }

        /// <summary>蓝线在设备像素里的主轴坐标（这一帧/提交那一刻）。</summary>
        public int LineDeviceAfter { get; set; }

        /// <summary>本次滑动手势抓住的是哪一张图（Before=上方那张、After=下方那张）。</summary>
        public FixedSide Grabbed { get; set; }

        /// <summary>蓝线在屏幕上有没有挪过：这一条就是"线不动、图动"那个观感的机器判据。</summary>
        public bool LineDeviceMoved => LineDeviceBefore != LineDeviceAfter;

        /// <summary>
        /// 本次手势生效期间<b>图1 真正进像素的那一块</b>源矩形（需求十一的 sourceRect1）。
        /// 只有裁切量而没有矩形，出事时看不出"切到了图的哪一块"，也分不清是被夹过还是按原样切的。
        /// 取的是<b>当时那张布局</b>（预览帧就是预览布局），所以日志里的那一对矩形与屏幕所见一致。
        /// </summary>
        public IntRect SourceRect1 { get; set; }

        /// <summary>图2 真正进像素的那一块源矩形（需求十一的 sourceRect2，起点已被蓝线往后挪）。</summary>
        public IntRect SourceRect2 { get; set; }

        /// <summary>画布高（手势前后各量一次；蓝线手势必须一模一样，对齐手势必须跟着重叠一起变）。</summary>
        public int CanvasHeightBefore { get; set; }
        public int CanvasHeightAfter { get; set; }

        /// <summary>画布高这次有没有变（蓝线手势必须 false、对齐手势必须 true）。</summary>
        public bool CanvasHeightChanged => CanvasHeightBefore != CanvasHeightAfter;

        /// <summary>
        /// 这一次手势有没有改<b>对齐</b>。蓝线手势必须 false——
        /// 需求七那句"两个动作不共用一个字段"，在日志侧就是靠这一条与 <see cref="CutChanged"/>
        /// 互斥来表达的（判据 <c>align/滑动两趟的日志各自齐备且互不覆盖</c>）。
        /// </summary>
        public bool AlignmentChanged => AlignmentOffsetBefore != AlignmentOffsetAfter;

        /// <summary>
        /// 这一次手势有没有改<b>蓝线的带内行号</b>。
        /// 蓝线手势必须 true；对齐手势在带变长变短时<b>也会</b> true——那是需求四要的
        /// "按相对位置换算"，不是把两个字段混用。两者怎么分：对齐手势必须
        /// <c>cutRatioBefore == cutRatioAfter</c>（比例守恒），蓝线手势必须
        /// <c>alignment_changed=false</c>（判据 <c>align/滑动两趟的日志各自齐备且互不覆盖</c>）。
        /// </summary>
        public bool CutChanged => CutBefore != EffectiveCut;

        /// <summary>这一族事件的前缀：滑动一张图用 seam.slide，位移档用 seam.edit。</summary>
        public string EventPrefix => CropMode ? "seam.slide" : "seam.edit";

        /// <summary>拼成日志用的一行字段。三族事件都用它，字段名不漂移。</summary>
        public string Fields() =>
            $"seam={SeamId} drag_mode={DragMode} hit={Hit} gesture_side={GestureSide} " +
            $"fixedSide={FixedSide} before_offset=({BeforeOffset.X},{BeforeOffset.Y}) " +
            $"requested_offset=({RequestedOffset.X},{RequestedOffset.Y}) " +
            $"effective_offset=({EffectiveOffset.X},{EffectiveOffset.Y}) " +
            $"source_delta=({SourceDelta.X},{SourceDelta.Y}) device_delta=({DeviceDelta.X},{DeviceDelta.Y}) " +
            $"viewportAfter=({ViewportAfter.X},{ViewportAfter.Y}) viewportBefore=({ViewportBefore.X},{ViewportBefore.Y}) " +
            $"zoom={Zoom:F4} x_locked={Low(RejectedX != 0)} rejected_x={RejectedX} revision={RevisionBefore} " +
            // mutated / undoPushed 只有这一处出口：commit 与 cancel 各自改字段，不再往行尾追加同名字，
            // 否则一行里出现两次 mutated=，grep 出来的就是两份互相矛盾的账。
            // 字段名按需求十一的写法逐字对齐（viewportBefore/viewportAfter、canvasHeightBefore/After、
            // undoPushed）——日志是拿给人和判据 grep 的，同义但不同名的写法等于没有这个字段。
            // 布尔一律走 Low()：C# 的 bool.ToString() 给 "True"/"False"，而日志与判据要比的是
            // 小写的 true/false（人 grep 也按小写写）——大小写混在一行里，"总高没变"这类
            // 关键判据就会永远读不到，红成一格"日志没证明"的空账。
            $"mutated={Low(Mutated)} undoPushed={Low(UndoPushed)} " +
            (CropMode
                ? $"crop_mode=true align_mode={Low(AlignMode)} overlap={OverlapLength} " +
                  // 需求十一那四个"必须区分"的量：对齐与蓝线各自的 before/after。
                  // 一次手势只该有一对变、另一对不动——把两对都写出来才判得出"谁没动"。
                  $"alignmentOffsetBefore=({AlignmentOffsetBefore.X},{AlignmentOffsetBefore.Y}) " +
                  $"alignmentOffsetAfter=({AlignmentOffsetAfter.X},{AlignmentOffsetAfter.Y}) " +
                  $"alignment_changed={Low(AlignmentOffsetBefore != AlignmentOffsetAfter)} " +
                  $"cutYBefore={CutBefore} cutYAfter={EffectiveCut} cut_changed={Low(CutBefore != EffectiveCut)} " +
                  $"cut_requested={RequestedCut} rejected_cut={RejectedCut} " +
                  $"before_crop_bottom={EffectiveBeforeCrop} after_crop_top={EffectiveAfterCrop} " +
                  $"rect1=({Rect1.X},{Rect1.Y},{Rect1.Width},{Rect1.Height}) " +
                  $"rect2=({Rect2.X},{Rect2.Y},{Rect2.Width},{Rect2.Height}) " +
                  $"overlapRectBefore=({OverlapRectBefore.X},{OverlapRectBefore.Y},{OverlapRectBefore.Width},{OverlapRectBefore.Height}) " +
                  $"overlapRectAfter=({OverlapRect.X},{OverlapRect.Y},{OverlapRect.Width},{OverlapRect.Height}) " +

                  // ═══ 需求七点名的那批 before/after（第十三轮）═══
                  // 公共坐标那三行只能证明"数据里谁动了"，屏上那两块矩形才证明"用户看见谁没动"。
                  // 「线不动、图动」那一轮之后，fixedSide 读的是<b>这一趟的结论</b>：
                  // 抓下方那张 ⇒ 屏上钉住的是上方那张（Fixed=Before）⇒ 图1 的设备矩形一字不动。
                  // cutRatio 那两个数<b>不再守恒</b>（守恒的是蓝线的画布行号）：
                  // 带被压缩时比例本来就该变，留着它是为了事后能算出带被压掉了多少。
                  $"rect1DeviceBefore=({Rect1DeviceBefore.X},{Rect1DeviceBefore.Y},{Rect1DeviceBefore.Width},{Rect1DeviceBefore.Height}) " +
                  $"rect1DeviceAfter=({Rect1DeviceAfter.X},{Rect1DeviceAfter.Y},{Rect1DeviceAfter.Width},{Rect1DeviceAfter.Height}) " +
                  $"rect2DeviceBefore=({Rect2DeviceBefore.X},{Rect2DeviceBefore.Y},{Rect2DeviceBefore.Width},{Rect2DeviceBefore.Height}) " +
                  $"rect2DeviceAfter=({Rect2DeviceAfter.X},{Rect2DeviceAfter.Y},{Rect2DeviceAfter.Width},{Rect2DeviceAfter.Height}) " +
                  $"cutRatioBefore={CutRatioBefore:F4} cutRatioAfter={CutRatioAfter:F4} " +

                  // 「线不动、图动」这一条必须在日志里能直接 grep 出来：只给 O 与 Cut 让人自己推，
                  // 等于把判据留给读日志的人（与需求七那句"不许只报两个 offset"同一个理由）。
                  $"lineCanvasRowBefore={LineCanvasRowBefore} lineCanvasRowAfter={LineCanvasRowAfter} " +
                  $"lineDeviceBefore={LineDeviceBefore} lineDeviceAfter={LineDeviceAfter} " +
                  $"line_device_moved={Low(LineDeviceMoved)} grabbed={Grabbed} " +

                  $"sourceRect1=({SourceRect1.X},{SourceRect1.Y},{SourceRect1.Width},{SourceRect1.Height}) " +
                  $"sourceRect2=({SourceRect2.X},{SourceRect2.Y},{SourceRect2.Width},{SourceRect2.Height}) " +
                  $"canvasHeightAfter={CanvasHeightAfter} canvasHeightBefore={CanvasHeightBefore} " +
                  $"height_changed={Low(CanvasHeightChanged)} " +
                  // 需求七那句"一次手势只该动一件事"要能在日志里直接读出来：
                  // 只报两个 offset 让人自己比，等于把判据留给读日志的人。
                  // 「线不动、图动」那一轮之后，滑动这一档 O 与蓝线是<b>一起</b>改的（守恒的是画布行号），
                  // 所以这里报的是"重叠变没变"／"位移档的 offset 有没有被偷偷改掉"。
                  (AlignMode
                      ? $"overlap_changed={Low(OverlapLength != (0 - AlignmentOffsetBefore.Y))}"
                      : $"offset_unchanged={Low(BeforeOffset == EffectiveOffset)}")
                : "crop_mode=false");

        /// <summary>布尔写成小写 true/false：日志是给人和判据 grep 的，不是给 C# 读的。</summary>
        private static string Low(bool value) => value ? "true" : "false";
    }

    /// <summary>
    /// 本次拖动开始时<b>图2 顶边</b>在画布里的行号（<see cref="ScreenAnchorRow"/>）。
    /// 固定后一段的视口补偿每帧都减这一个基准，绝对赋值 ⇒ 取整不累加、拖回原位自然归零。
    /// 记"按下那一刻"而不是每帧现取项目布局：提交之后项目布局已经跟着变了，
    /// 只有这个数还能说"整趟一共挪了几行"。
    /// </summary>
    private int _dragAnchorBase;

    // ── 「线不动、图动」这一档的手势状态（第十四轮末次交互重设计）──

    /// <summary>
    /// 本次手势抓住的是蓝线<b>上方</b>还是<b>下方</b>那一张图。
    /// 按下那一刻定下来，整趟不再变——中途改侧别会让"线钉在屏幕哪一行"这件事失去基准。
    /// </summary>
    private FixedSide _slideGrabbed = FixedSide.After;

    /// <summary>
    /// 微调模式：点蓝线上那枚「调整」把手之后进入。
    /// 进来之后画布上"点哪一侧"就是选哪一张图跟着方向键走，而不是平移画布。
    /// </summary>
    private bool _adjustMode;

    /// <summary>微调模式里已经选定的那一张图（还没选就是 null，此时按方向键不动参数）。</summary>
    private FixedSide? _adjustSide;

    /// <summary>微调模式是否开着。公开给电池与状态栏读。</summary>
    public bool AdjustModeActive => _adjustMode;

    /// <summary>微调模式里选中的是哪一张图；没选返回 null。</summary>
    public FixedSide? AdjustSideForTest => _adjustSide;

    /// <summary>本次滑动手势抓住的是哪一张图（判据据此核对"屏上钉住的是另一张"）。</summary>
    public FixedSide SlideGrabbedSideForTest => _slideGrabbed;

    // ── 点选切分模式（需求 §6"在画布上点选位置切分"，审查第三轮 #5）──
    private bool _splitPick;
    private Point _splitHover = new(-1, -1);

    /// <summary>点选切分模式是否进行中。</summary>
    public bool SplitPickActive => _splitPick;

    /// <summary>用户在点选模式里单击了一个画布纵坐标（主窗口据此执行切分）。</summary>
    public event Action<int>? SplitPicked;

    /// <summary>悬停到拼缝上时的提示文字（ToolTip 与冒烟判据共用这一条链路）。</summary>
    public event Action<string?, Point>? HoverSeamTip;

    /// <summary>指针位移换算成源像素的阈值：小于它算"单击"，不算"拖动"。</summary>
    private const int ClickThresholdDevicePx = 4;

    /// <summary>
    /// 拼缝线的可点容差（设备像素）。需求 §3.2 要求缩小后也容易点中；
    /// 第八轮把它钉成"至少 8 设备像素"，所以常量取 9（留 1 像素余量，判据扫到 9 才算命中），
    /// 判据 <c>canvas/拼缝线九像素内可点、带里拖缝、带外拖画布</c> 逐行扫 1–9 像素要求都命中线、
    /// 10 像素起命中重叠带、带外回落成"无命中＝平移画布"，容差被改小就会红。
    /// </summary>
    private const int SeamHitTolerancePx = 9;

    public ViewportState Viewport { get; } = new();

    /// <summary>当前正在编辑的拼缝 Id；null 表示浏览画布。</summary>
    public string? SelectedSeamId { get; private set; }

    /// <summary>是否叠加差异显示。</summary>
    public bool ShowDifference { get; set; }

    /// <summary>
    /// 浏览态方向键是否平移视口（需求 §4："默认按 1 px 屏幕单位平移视口；可在设置中关闭"）。
    /// 关掉后方向键在浏览态什么都不做；编辑态的 1 源像素微调不受影响。
    /// </summary>
    public bool BrowseArrowPanEnabled { get; set; } = true;

    /// <summary>是否叠加半透明"重影"对比层（与 <see cref="ShowDifference"/> 互斥，由调用方保证）。</summary>
    public bool ShowCompareOverlay { get; set; } = true;

    /// <summary>是否在画布上标出所有清理补丁的位置（需求 §3.5"应用预览后高亮修改区域，不能隐藏修改"）。</summary>
    public bool ShowPatchOverlay { get; set; }

    /// <summary>重叠预览的不透明度（0–100）。</summary>
    public int OverlapOpacityPercent { get; set; } = 50;

    public event Action<string?>? SelectionChanged;

    /// <summary>
    /// 拖动中实时回报当前位移，右侧面板据此刷新 X/Y 数字与浮标。
    ///
    /// ═══ 订阅者必须做轻活（第十一轮的卡顿根因之一）═══
    /// 这个事件<b>每个预览帧都会来一次</b>，而预览期间参数一个字都没写（松手才提交），
    /// 所以拿它去"清空重建拼缝列表 / 整块刷新面板"买到的只有卡顿，没有新信息。
    /// 主窗口现在只在 <see cref="Project.Changed"/>（真改动）时刷外壳；要显示实时值，
    /// 请只改那一两个数字，或者干脆看画布上的拖动浮标（<c>DrawDragBadge</c>，它读的就是同一个数）。
    /// </summary>
    public event Action<IntPoint>? Dragging;

    /// <summary>
    /// 长截图锁横向时，用户仍试图左右移动的通知（参数=被丢弃的横向位移，源图像素）。
    /// 主窗口据此在状态栏说明"为什么按了没反应"——不许悄悄什么都不做。
    /// </summary>
    public event Action<int>? HorizontalShiftRejected;

    /// <summary>
    /// 长截图里切点顶到重叠带边界时的通知（参数=被夹掉的行数，负数=想切到带首之前）。
    /// 与 <see cref="HorizontalShiftRejected"/> 同一个道理：<b>夹取必须出声</b>，
    /// 否则用户只会觉得"线拖不动了"，不知道已经整带用上一张／下一张了。
    /// </summary>
    public event Action<int>? CutClamped;

    /// <summary>本画布当前是否走"裁切去重"（长截图）。界面文案与判据都读它，不自己判模式。</summary>
    public bool UsesCropPlacement => ActiveLayout.UsesCropPlacement;

    /// <summary>瓦片缓存<b>整块清空</b>的次数（判据 <c>crop/100 次鼠标移动只换几帧预览：不整块清缓存、不逐帧写日志</c> 读它：拖动期间必须为 0）。</summary>
    public long TileFullInvalidationsForTest => _tiles.FullInvalidations;

    /// <summary>真渲染过的瓦片块数（缓存命中不算）。</summary>
    public long TileRendersForTest => _tiles.TileRenders;

    /// <summary>当前缓存里有多少块瓦片（用来自证"这一帧确实有东西可复用"，不是空判据）。</summary>
    public int TileCountForTest => _tiles.Count;

    /// <summary>
    /// 被<b>局部作废</b>（<c>TileCache.DropRegion</c>）丢掉的块数累计。
    /// 需求五那道"整块 TileCache 清理 = 0"只管得到"全清"，管不到"一次丢十几块"——
    /// 拖动途中那 4~6% 的命中率正是后者造的（对齐手势把带顶往下整段作废），
    /// 没有这一枚读数，两种相反的原因（作废范围宽／LRU 挤掉）在屏上读起来一模一样。
    /// </summary>
    public long TileRegionDropsForTest => _tiles.RegionDrops;

    /// <summary>因超过缓存字节上限而被 <b>LRU 挤掉</b>的块数累计（上面那句话里的第二种原因）。</summary>
    public long TileTrimEvictionsForTest => _tiles.TrimEvictions;

    /// <summary>
    /// 把瓦片缓存整块倒掉（只给工装用）。<b>为什么必须有这道出口</b>：
    /// 滚轮的真实代价只在"第一次滚进没见过的区域"时露出来，而缓存装得下整张画布
    /// （本机 168 块、每块约 1MB，仍在 192MB 上限之内），第二趟开始就全在读上一趟的缓存——
    /// 报出来的 P95 不是用户第一次滚动的那个形状。工装每趟先调这里一次，
    /// 才有"每趟同一起点"可比。
    /// </summary>
    public void ClearTileCacheForTest() => _tiles.Invalidate();

    // ═════════════════ 第十三轮需求五/六：真实帧测量 ═════════════════
    // 为什么这几个数必须由控件自己在 OnPaint 里记：需求五要的是"这一屏真的画了多久、画了几次"，
    // 拿"离线调了几次渲染函数"去当它，就是需求八明令禁止的"把 GUI 性能测试改成纯函数测试后宣称 GUI 已流畅"。
    // 状态栏那三个读数（FPS / 帧耗时 / 缓存命中率）与 --gui 工装读的是<b>同一批</b>字段，
    // 所以"界面自己觉得卡不卡"和"判据量出来卡不卡"不可能各说一套。
    // 帧耗时样本用固定容量的环形数组：拖动一次几千帧也只占 32 KB，不会把测量本身变成卡顿源。

    private const int FrameSampleCap = 4096;
    private readonly double[] _frameMs = new double[FrameSampleCap];
    private readonly bool[] _frameSawGc = new bool[FrameSampleCap];

    /// <summary>
    /// 与 <see cref="_frameMs"/> 同下标的"这一帧里 F8 读数层自己占掉的那几毫秒"。
    /// 关掉读数层的那些帧记 0。<b>为什么要单独一份</b>：需求三明确不许把 HUD 开启的开销
    /// 与普通画布性能混为一谈——只有把一帧拆成「画布」与「读数层」两段，
    /// 才说得出"P95 超阈值是读数层贡献的、撤掉它就回到多少"。
    /// </summary>
    private readonly double[] _frameHudMs = new double[FrameSampleCap];
    private int _frameFilled;
    private int _frameCursor;

    /// <summary>已经落进环里的帧总数（<b>单调递增，不因池满而停</b>）。
    /// 环形下标会被复用，光看 <see cref="_frameCursor"/> 分不出"这一段还在池里"还是"已经被转过一圈"，
    /// 所以拖动段那几笔读数要靠这个总数与 <see cref="_gestureMarkTotal"/> 相减才知道段长没长过池子。</summary>
    private long _frameTotal;

    /// <summary>最近一次手势按下那一刻，环里的写入下标；-1 表示这一趟还没按过（拖动段不作数）。
    /// <b>为什么产品要自己记这一笔</b>：需求五那条 `单帧 ≤ 50ms` 要判的是"拖动时单帧慢不慢"，
    /// 而真窗口量下来整个进程的样本池里最慢那一枚（71.7–83.7ms）出现在<b>导入/首次绘制</b>那几帧上——
    /// 池子有 4096 格、一趟拖动只有 179 帧，根本不轮转，所以导入帧一直留在池里，
    /// 拿"全池最慢"去判拖动就是拿环境的钱记在产品的账上。有了这个标记，
    /// 「拖动段平均 / P50 / P95 / 最慢」才只算按下之后的帧。判据
    /// <c>canvas/拖动段的帧读数只算最近一次按下之后的帧</c>，反证
    /// <c>guard/拖动段若退回整个样本池则导入帧会冒充拖动最慢帧</c>。</summary>
    private int _gestureMark = -1;

    /// <summary>打下标那一刻的 <see cref="_frameTotal"/>（与 <see cref="_gestureMark"/> 配成一对）。</summary>
    private long _gestureMarkTotal;

    /// <summary>松手那一刻的 <see cref="_frameTotal"/>；-1 = 还没有一次完整的"按下→松手"结束过。
    /// <b>为什么还要这一笔</b>：第一次量出来才发现，"自最近一次按下起"那一段里最慢那一枚
    /// （真窗口 150% 量到 97.6ms）与全池最慢是<b>同一枚</b>——那枚不是拖动帧，
    /// 而是<b>松手提交</b>触发的重画（设计里一次手势只提交一条可撤销记录，那一次重算是该做的）。
    /// 于是"按下→取数"这一段把两种完全不同的帧混在一个最慢值里：需求五问的是<b>拖动时</b>卡不卡。
    /// 有了松手这个终点，出口那行能分开报"拖动中 N 帧"与"松手提交那几帧"。</summary>
    private long _gestureEndTotal = -1;

    private readonly Queue<long> _paintTicks = new();

    /// <summary>OnPaint 进来过多少次（需求五点名的"Paint 次数"）。</summary>
    public long PaintCount { get; private set; }

    /// <summary>收到过多少次鼠标移动（含没有落成预览的那几次）。</summary>
    public long MouseMoveCount { get; private set; }

    /// <summary>最近一帧 OnPaint 的耗时（毫秒）。</summary>
    public double LastFrameMs { get; private set; }

    /// <summary>拖动途中"预览布局重算"累计耗时（毫秒）与次数——这段工作<b>不在 OnPaint 里</b>，
    /// 帧耗时样本量不到它（需求六"卡顿发生在哪个阶段"要多问一句：阶段可能压根不在画上）。</summary>
    public double PreviewRecomputeMs { get; private set; }
    public long PreviewRecomputeCount { get; private set; }

    /// <summary>本次测量里最慢的一帧（毫秒）。需求五的"单帧不超过 50ms"读它。</summary>
    public double MaxFrameMs { get; private set; }

    /// <summary>最后一次处理鼠标消息时的 <see cref="Environment.TickCount64"/>（工装用它量队列延迟）。</summary>
    public long LastInputTicks { get; private set; } = long.MinValue;

    /// <summary>已经记下多少个帧耗时样本。0 就是"这一趟根本没画过"，读数一律不作数。</summary>
    public int FrameSampleCount => _frameFilled;

    /// <summary>
    /// 帧样本里"这一帧之内发生过 GC"的帧数（需求五：偶发 80–150ms 那批要能归因到回收，
    /// 而不是写成"代码稳定流畅"）。量的是<b>当前样本池</b>，不是进程累计——池子满了以后是滑动窗口，
    /// 与 <see cref="FrameSampleCount"/> 一起读才是"这 N 帧里有 M 帧撞上回收"。
    /// 判据：<c>canvas/交互诊断读数里需求五点名的 GC 与整块合成两笔都得有</c>。
    /// </summary>
    public int GcHitFramesForTest
    {
        get
        {
            int n = 0;
            for (int i = 0; i < _frameFilled; i++)
                if (_frameSawGc[i]) n++;
            return n;
        }
    }

    /// <summary>帧耗时均值（毫秒）。没有样本时给 0，由调用方用 <see cref="FrameSampleCount"/> 判"没量到"。</summary>
    public double FrameAvgMs
    {
        get
        {
            if (_frameFilled == 0) return 0;
            double sum = 0;
            for (int i = 0; i < _frameFilled; i++) sum += _frameMs[i];
            return sum / _frameFilled;
        }
    }

    /// <summary>帧耗时分位数（P95 就传 0.95）：把样本排序后取第 fraction 位。</summary>
    public double FramePercentileMs(double fraction)
    {
        if (_frameFilled == 0) return 0;
        var copy = new double[_frameFilled];
        Array.Copy(_frameMs, copy, _frameFilled);
        Array.Sort(copy);
        int at = Math.Clamp((int)Math.Ceiling(fraction * _frameFilled) - 1, 0, _frameFilled - 1);
        return copy[at];
    }

    /// <summary>
    /// 按时间顺序取出这一批帧耗时样本（毫秒）。工装用它把多趟的样本并成一个池再算分位——
    /// 单趟只有 5–7 帧时，"P95"其实等于"倒数第二慢那一帧"，用它判阈值没有统计意义。
    /// 必须在松手之前取：松手那一帧（设计里的那一次全量重算）会进同一个环，混进来会顶高读数。
    /// </summary>
    public double[] FrameSamplesMsForTest()
    {
        var copy = new double[_frameFilled];
        Array.Copy(_frameMs, copy, _frameFilled);
        return copy;
    }

    /// <summary>与 <see cref="FrameSamplesMsForTest"/> 同下标的"这一帧里读数层占掉的毫秒"（关着是 0）。
    /// 两列并起来才能把多趟的样本汇成"HUD 单独耗时"与"普通画布绘制耗时"两个池。</summary>
    public double[] FrameHudSamplesMsForTest()
    {
        var copy = new double[_frameFilled];
        Array.Copy(_frameHudMs, copy, _frameFilled);
        return copy;
    }

    // ═════════════════ 拖动段：只算"最近一次按下之后"的那些帧 ═════════════════
    //
    // 这一段的存在理由是一条实测出来的错归因：真窗口 150% 那一档连跑 5 趟，读到的"最慢帧"
    // 一直是 71.7～83.7ms，而这枚最慢<b>出现在导入/首次绘制那几帧上</b>——同一趟里
    // 拖动前取一次读数（那时池里只有 2～4 枚样本，全在导入期）最慢是同一个数，
    // 拖动后再取（池里 177～189 枚）最慢<b>一字不差还是那个数</b>；
    // 而样本池有 4096 格，一趟拖动根本不把它转出去 ⇒ 拿"全池最慢"去判
    // 需求五那条 `单帧 ≤ 50ms`，量到的其实是环境的钱，不是拖动的钱。
    // 所以产品自己记住"最近一次按下"落在环里的哪一格，出口那行按这一段重算。
    // 这些算法<b>只在取数出口跑</b>（复制诊断信息／导出会话），不进 OnPaint、也不进 10Hz 的面板文本。

    /// <summary>把手势起点标到环里当前的写入下标。由 <see cref="BeginPan"/> 与拼缝/对齐拖动起点各调一次
    /// ——<b>必须是产品的手势通路</b>，不许由测试自己标，否则"没按过也算了一段"这种退化没人抓。</summary>
    private void MarkGestureFrames()
    {
        _gestureMark = _frameCursor;
        _gestureMarkTotal = _frameTotal;
        _gestureEndTotal = -1;      // 下一次松手之前，"拖动中"那一段还不存在
    }

    /// <summary>这一趟有没有按过（没按过 ⇒ 拖动段那几笔读数一律不作数，出口会照实写"还没有过手势"）。</summary>
    public bool HasGestureMark => _gestureMark >= 0;

    /// <summary>按下之后已经画了多少帧（上限就是池子容量）。</summary>
    public int GestureFrameCount
    {
        get
        {
            if (_gestureMark < 0) return 0;
            long since = _frameTotal - _gestureMarkTotal;
            if (since <= 0) return 0;
            return (int)Math.Min(since, _frameFilled);
        }
    }

    /// <summary>这一段是不是已经长过样本池（长过就说明段头被环形覆盖掉了，读到的只是"池里还剩的那截"）。
    /// 4096 格 ÷ 每帧 = 要拖四千多帧才会撞上；真撞上时这句必须挂在读数上，不许悄悄给个偏小的分位。</summary>
    public bool GestureSegmentTruncated => _gestureMark >= 0 && _frameTotal - _gestureMarkTotal > FrameSampleCap;

    /// <summary>按下之后那些帧的耗时（按时间顺序）。取法是从当前写点往回走 N 格，
    /// 因此<b>池子已经转过圈也对</b>。</summary>
    public double[] GestureFrameSamplesMsForTest() => GestureTail(_frameMs);

    /// <summary>与 <see cref="GestureFrameSamplesMsForTest"/> 同一段的"读数层自己占的毫秒"。</summary>
    public double[] GestureHudSamplesMsForTest() => GestureTail(_frameHudMs);

    private double[] GestureTail(double[] src)
    {
        int n = GestureFrameCount;
        var tail = new double[n];
        for (int i = 0; i < n; i++)
        {
            int idx = (_frameCursor - n + i) % FrameSampleCap;
            if (idx < 0) idx += FrameSampleCap;
            tail[i] = src[idx];
        }
        return tail;
    }

    /// <summary>拖动段的分位（P95 就传 0.95）。段里一帧都没有时给 0，由调用方看
    /// <see cref="HasGestureMark"/> 与 <see cref="GestureFrameCount"/> 判"这一档没量到"。</summary>
    public static double PercentileOf(double[] samples, double fraction)
    {
        if (samples.Length == 0) return 0;
        var sorted = (double[])samples.Clone();
        Array.Sort(sorted);
        int at = Math.Clamp((int)Math.Ceiling(fraction * sorted.Length) - 1, 0, sorted.Length - 1);
        return sorted[at];
    }

    /// <summary>拖动段里最慢的一帧（毫秒）。需求五那条 `单帧 ≤ 50ms` 该判的是它，不是 <see cref="MaxFrameMs"/>。</summary>
    public double GestureMaxFrameMs
    {
        get
        {
            double max = 0;
            foreach (double ms in GestureTail(_frameMs)) if (ms > max) max = ms;
            return max;
        }
    }

    /// <summary>这一次手势"按下→松手"之间画了几帧（还没松手 = 0，因为那一段还没封口）。</summary>
    public int GestureDragFrameCount =>
        _gestureMark < 0 || _gestureEndTotal < _gestureMarkTotal
            ? 0
            : (int)Math.Min(_gestureEndTotal - _gestureMarkTotal, _frameFilled);

    /// <summary><b>拖动中</b>那几帧（不含松手提交触发的重画）。取法是从按下那一格<b>往后数</b>：
    /// 尾巴那一种取法（<see cref="GestureTail"/>）必然把提交帧圈进来，而那一帧慢是设计如此
    /// （一次手势只提交一条可撤销记录，提交要重算一次布局与可见瓦片）——需求五问的是拖动时卡不卡，
    /// 两者必须分开，否则"最慢 97.6ms"会被读成"拖动时最慢 97.6ms"。</summary>
    public double[] GestureDragSamplesMsForTest() => SamplesFrom(_frameMs, _gestureMark, GestureDragFrameCount);

    /// <summary>与 <see cref="GestureDragSamplesMsForTest"/> 同一段的读数层耗时。</summary>
    public double[] GestureDragHudSamplesMsForTest() => SamplesFrom(_frameHudMs, _gestureMark, GestureDragFrameCount);

    /// <summary>拖动中那几帧里最慢的一帧（毫秒）——需求五那条阈值真正该读的那一枚。</summary>
    public double GestureDragMaxFrameMs
    {
        get
        {
            double max = 0;
            foreach (double ms in GestureDragSamplesMsForTest()) if (ms > max) max = ms;
            return max;
        }
    }

    /// <summary>从环里某一格开始往后取 N 枚（N 超出的部分按池子实际填充截断）。</summary>
    private double[] SamplesFrom(double[] src, int startRingIdx, int n)
    {
        var arr = new double[Math.Max(0, n)];
        for (int i = 0; i < arr.Length; i++)
            arr[i] = src[(startRingIdx + i) % FrameSampleCap];
        return arr;
    }

    /// <summary>
    /// 按时间顺序给出所有帧样本（毫秒）。工装打印它是因为 P95 只说"有一帧慢"，
    /// 这一行说的是"慢在第几帧"——只有首帧贵（JIT + 首次解码源图）与每一帧都贵
    /// 是两种完全不同的问题，只看平均数会把它们混成一个数。
    /// 带 <c>*</c> 的那一枚表示"这一帧里发生过一次 GC"（按代计数增量当场标的，不是估算）。
    /// </summary>
    public string FrameSamplesForTest
    {
        get
        {
            var parts = new string[_frameFilled];
            for (int i = 0; i < _frameFilled; i++)
                parts[i] = _frameMs[i].ToString("F1") + (_frameSawGc[i] ? "*" : "");
            return string.Join(' ', parts);
        }
    }

    /// <summary>
    /// 需求三新增的口径：把"开着读数层的那几帧"拆成 <b>HUD 单独耗时</b> 与
    /// <b>普通画布绘制耗时</b>（= 整帧减去 HUD 那一段）两组，各给均值 / P50 / P95 / 最慢。
    /// 没有 HUD 样本时 <see cref="HudSplit.Frames"/> 给 0，由调用方判"没量到"——
    /// 需求三明确要求文档分别报告 HUD 关闭与 HUD 开启的性能，这个元组就是那两份读数的出处。
    /// </summary>
    public HudSplit HudSplitStats
    {
        get
        {
            var hud = new List<double>(_frameFilled);
            var canvas = new List<double>(_frameFilled);
            for (int i = 0; i < _frameFilled; i++)
            {
                if (_frameHudMs[i] <= 0) continue;
                hud.Add(_frameHudMs[i]);
                canvas.Add(Math.Max(0, _frameMs[i] - _frameHudMs[i]));
            }
            if (hud.Count == 0) return new HudSplit(0, 0, 0, 0, 0, 0, 0, 0, 0);
            hud.Sort();
            canvas.Sort();
            static double Sum(List<double> v) { double s = 0; foreach (double x in v) s += x; return s / v.Count; }
            static double At(List<double> sorted, double fraction) =>
                sorted[Math.Clamp((int)Math.Ceiling(fraction * sorted.Count) - 1, 0, sorted.Count - 1)];
            return new HudSplit(hud.Count, Sum(hud), At(hud, 0.5), At(hud, 0.95), hud[^1],
                Sum(canvas), At(canvas, 0.5), At(canvas, 0.95), canvas[^1]);
        }
    }

    /// <summary>一帧拆成「读数层那一段」与「其余画布那一段」两组读数的形状（HUD 关闭/开启分开报就用它）。</summary>
    public readonly record struct HudSplit(int Frames, double HudAvg, double HudP50, double HudP95, double HudMax,
        double CanvasAvg, double CanvasP50, double CanvasP95, double CanvasMax);

    /// <summary>开着读数层画过多少帧（0 就是"这一趟没开 HUD"，<see cref="HudSplitStats"/> 那组数不作数）。</summary>
    public int HudFrameCount => HudSplitStats.Frames;

    /// <summary>最近一秒内的 Paint 次数——状态栏那个 FPS 读它（不是"整场平均"，平均会把卡顿抹平）。</summary>
    public double Fps
    {
        get
        {
            long now = Environment.TickCount64;
            while (_paintTicks.Count > 0 && now - _paintTicks.Peek() > 1000) _paintTicks.Dequeue();
            return _paintTicks.Count;
        }
    }

    /// <summary>瓦片缓存命中率 = 命中 / (命中 + 真渲染)。低就说明每帧都在从源图重算。</summary>
    public double CacheHitRate
    {
        get
        {
            long renders = _tiles.TileRenders, hits = _tiles.CacheHits;
            long total = renders + hits;
            return total == 0 ? 0 : (double)hits / total;
        }
    }

    /// <summary>清空帧测量（工装每一趟开始前调一次，读数才是"这一趟"的而不是整个进程寿命的）。</summary>
    public void ResetFrameStats()
    {
        PaintCount = 0;
        MouseMoveCount = 0;
        LastFrameMs = 0;
        MaxFrameMs = 0;
        PreviewRecomputeMs = 0;
        PreviewRecomputeCount = 0;
        SlowestStageBreakdown = "";
        SlowestOverlaySplit = "";
        _slowestHudFrameNumbersValid = false;
        _hudFrameNumbersValid = false;
        _frameFilled = 0;
        _frameCursor = 0;
        Array.Clear(_frameSawGc, 0, _frameSawGc.Length);
        Array.Clear(_frameHudMs, 0, _frameHudMs.Length);
        _paintTicks.Clear();
    }

    /// <summary>把一帧的耗时记进环形样本。<paramref name="sawGc"/> 标记"这一帧之内发生过 GC"——
    /// 一帧 15ms 与一帧 130ms 若是同一套绘制算出来的，差别就不在绘制，而在回收（位图缓冲与
    /// 大对象堆的分配都发生在这一层）。工装把这两种帧分开打印（带 <c>*</c> 的那一枚）。
    /// <paramref name="hudMs"/> 是这一帧里 F8 读数层自己占掉的毫秒（关着就是 0）——
    /// 需求三要求"HUD 开启会增加绘制成本，不能与普通画布性能混为一谈"，靠的就是这一列。</summary>
    private void RecordFrame(double ms, bool sawGc, string stages, double hudMs)
    {
        LastFrameMs = ms;
        if (ms > MaxFrameMs)
        {
            MaxFrameMs = ms;
            SlowestStageBreakdown = stages;
            // 叠层拆账与分阶段同一帧（都读"刚结算的那一帧"），所以必须在这里一起封口：
            // 否则下一轮会把"最后一帧的拆账"当成"最慢那帧的拆账"读，那正好是这一族判据要防的形状。
            SlowestOverlaySplit = LastOverlaySplit;
            // 读数层那一笔的拆账跟着同一帧封口（第十四改）——它与上面那两笔读的是同一个 if，
            // 分开封就会读出"最慢那帧的分阶段 × 最后一帧的读数层账"那种对不上的组合。
            _slowestHudFrameNumbers = _hudFrameNumbers;
            _slowestHudFrameNumbersValid = _hudFrameNumbersValid;
        }
        _frameMs[_frameCursor] = ms;
        _frameSawGc[_frameCursor] = sawGc;
        _frameHudMs[_frameCursor] = hudMs;
        _frameCursor = (_frameCursor + 1) % FrameSampleCap;
        _frameTotal++;
        if (_frameFilled < FrameSampleCap) _frameFilled++;
    }

    /// <summary>
    /// 「复制诊断信息」与「导出当前交互会话」用的那一坨（第十四轮需求四）。
    /// 内容<b>与屏上读数同源</b>（就是 <see cref="BuildHudText"/> 那一段），另加窗口级几行：
    /// 素材与拼缝数、选中哪条缝、帧样本池的分位（含"开着读数层的那几帧"单列）、瓦片命中率、
    /// 读数层自身的重算次数。
    /// <b>只写数字、坐标、Id 与状态</b>：这里不许出现文件路径、图片标题或任何像素内容——
    /// 这段文本是要被粘给别人的（精度红线里那条隐私约定，与诊断日志同一条）。
    /// </summary>
    public string DescribeDiagnostics()
    {
        EnsureHudRects(ActiveLayout);
        HudSplit split = HudSplitStats;
        var lines = new List<string>
        {
            "═══ LongStitch 交互诊断（与屏上读数同源）═══",
        };
        lines.AddRange(BuildHudText(ActiveLayout, _hudSeam, _hudGeo, _hudRects).Split('\n'));
        lines.Add($"模式={_project.Doc.Mode} 段数={_project.Doc.Segments.Count} " +
                  $"拼缝数={_project.Doc.Seams.Count} 选中={(SelectedSeamId ?? "无")} " +
                  $"状态={(SelectedSeamId is null ? "浏览" : "编辑")}");
        lines.Add($"帧样本 {FrameSampleCount} 帧：平均 {FrameAvgMs:F1} P50 {FramePercentileMs(0.5):F1} " +
                  $"P95 {FramePercentileMs(0.95):F1} 最慢 {MaxFrameMs:F1}ms");
        lines.Add(split.Frames > 0
            ? $"其中开着读数层的 {split.Frames} 帧：HUD 单独 P50 {split.HudP50:F1} P95 {split.HudP95:F1} " +
              $"最慢 {split.HudMax:F1}ms｜同帧画布部分 P50 {split.CanvasP50:F1} P95 {split.CanvasP95:F1}ms"
            : "这一趟没开读数层（HUD 单独耗时＝未量到，需求三要求两档分开报）");
        lines.Add($"瓦片 缓存 {TileCountForTest} 块 命中率 {CacheHitRate:P0} 渲染 {TileRendersForTest} " +
                  $"整块清 {TileFullInvalidationsForTest}");
        // ═══ 第三十七改：把"面板装不装得下自己的读数"交给一次菜单点击就能交出来 ═══
        // 需求二把「F8 调试层的视觉效果」挂在<b>未验证</b>那一档；第三十六改又确认工装进程是 96 DPI，
        // 所以 150% 那一屏装不装得下，<b>工装量不到</b>——只有产品自己的窗口（用户实际的 DPI）量得到。
        // 而截图会拍到聊天内容（隐私红线：屏上内容不许进仓库，也不该贴给任何人），
        // 所以这里改成交<b>两个数字加一次判定</b>：框多大、这段读数真实占多大、量它时进程 DPI 多少。
        // 用户不必自己判断"有没有被截断"，也不必让隐私内容离开这台机器；
        // <b>还没画过面板时判词必须是"量不到"</b>（说成"装得下"就是没量过的东西被当成已验证——本族老坑）。
        (int fitBoxW, int fitBoxH) = HudPanelSizeForTest;
        Size fitNeed = HudTextExtent(BuildHudText(ActiveLayout, _hudSeam, _hudGeo, _hudRects));
        string fitVerdict = fitBoxW <= 0 || fitBoxH <= 0
            ? "量不到（面板还没画过，别读成装得下）"
            : fitBoxW >= fitNeed.Width && fitBoxH >= fitNeed.Height
                ? "装得下" : "装不下，有读数被裁在框外";
        lines.Add($"面板自证：框 {fitBoxW}×{fitBoxH}、这段读数真实占 {fitNeed.Width}×{fitNeed.Height}" +
                  $"、进程 DPI {DeviceDpi} ⇒ {fitVerdict}" +
                  "（这一行就是 150% 那一屏的判据，不必截图）");
        // 需求五那句"偶发 80–150ms 时记录 GC 与进程调度信息"：这三笔就是归因材料，
        // 而且要能在<b>真窗口那一档</b>读到——`--gui` 工装量的是 96 DPI 那档窗口，
        // 150% 屏上这一档过去只有面板上几枚毫秒数，没有 GC/整块合成/进程 CPU，
        // 所以那档的数一直没法按需求五"连跑 5 次报 P50/P95/max"的口径交出去。
        // 只走诊断出口（`Process.GetCurrentProcess()` 自己就分配），一帧都不读。
        using (System.Diagnostics.Process proc = System.Diagnostics.Process.GetCurrentProcess())
            lines.Add($"GC 与调度：gen0 {GC.CollectionCount(0)}、gen1 {GC.CollectionCount(1)}、gen2 {GC.CollectionCount(2)} 次" +
                      $"（进程累计）· 当前帧样本 {FrameSampleCount} 帧里撞上回收的 {GcHitFramesForTest} 帧 · " +
                      $"UI 线程累计分配 {GC.GetAllocatedBytesForCurrentThread() / 1048576} MiB · " +
                      $"进程 CPU 用户 {proc.UserProcessorTime.TotalMilliseconds:F0}ms／总 " +
                      $"{proc.TotalProcessorTime.TotalMilliseconds:F0}ms（与墙上时间差出去的就是被抢占的那截）· " +
                      $"整块合成 RenderWhole {CanvasComposer.WholeRunCountForTest} 次");
        // 需求五那句里的"进程调度信息"过去只有一行推论（墙钟减 CPU 时间差＝被抢占的那截）。
        // 这一行把它变成实量：线程条数、累计 CPU 最多的那条线程的用户／内核时间、那一刻的状态与等待原因。
        // 这一平台拿不到的那一笔（上下文切换次数）在出口里明写成"按未量记"，不写成 0。
        // 判据 <c>canvas/交互诊断读数里需求五点名的进程调度那一笔必须从操作系统读来</c> 钉住它，
        // 反证 <c>guard/调度读数若写成固定值则空转攒够十五毫秒再取第二次它也不涨</c>：
        // 在这条线程上空转到攒够 15ms CPU（消耗的就是它自己的用户态 CPU），第二次读数必须跟着涨——
        // 写死的数或只在启动时取过一次的缓存都会当场翻红。
        lines.Add(ThreadScheduling.Describe(ThreadScheduling.Take()));
        // 需求五那条 `单帧 ≤ 50ms` 到底该判在哪一段时间上：上面「帧样本」是全进程的池子，
        // 里面一直留着导入/首次绘制那几帧（池 4096 格，一趟拖动转不出去），所以那一行的"最慢"
        // 不是拖动的最慢。这一行才是：只算最近一次按下之后的帧。屏上的 F8 面板没有这一行
        // （面板有 10Hz 闸门，不该为它多排一次文本），要读就走出口。
        if (!HasGestureMark)
            lines.Add("拖动段：这一趟还没按过鼠标，上面的分位全是导入期的帧，别拿它判拖动");
        else
        {
            double[] seg = GestureFrameSamplesMsForTest();
            double[] segHud = GestureHudSamplesMsForTest();
            double sum = 0;
            foreach (double ms in seg) sum += ms;
            // 再切一刀：按下→松手是"拖动中"，松手之后那几帧是"提交重画"（设计里一次手势只提交一条记录）。
            // 不分这一刀，"最慢那一枚"会把两种帧混成一个数——真窗口第一次量就撞上了：
            // 段最慢 97.6ms 与全池最慢同一枚，而那枚正是松手提交的重画。
            double[] drag = GestureDragSamplesMsForTest();
            double[] dragHud = GestureDragHudSamplesMsForTest();
            double dragSum = 0;
            foreach (double ms in drag) dragSum += ms;
            lines.Add($"拖动段（自最近一次按下起）{seg.Length} 帧" +
                      $" 平均 {(seg.Length == 0 ? 0 : sum / seg.Length):F1} P50 {PercentileOf(seg, 0.5):F1}" +
                      $" P95 {PercentileOf(seg, 0.95):F1} 最慢 {GestureMaxFrameMs:F1}ms" +
                      $"（其中读数层 P95 {PercentileOf(segHud, 0.95):F1}）" +
                      (GestureSegmentTruncated ? " · 这一段比样本池还长，头已被覆盖，只读到池里剩的那截" : ""));
            lines.Add($"  其中拖动中（按下→松手，不含提交重画）{drag.Length} 帧" +
                      $" 平均 {(drag.Length == 0 ? 0 : dragSum / drag.Length):F1}" +
                      $" P50 {PercentileOf(drag, 0.5):F1} P95 {PercentileOf(drag, 0.95):F1}" +
                      $" 最慢 {GestureDragMaxFrameMs:F1}ms" +
                      $"（其中读数层 P95 {PercentileOf(dragHud, 0.95):F1}）" +
                      (drag.Length == 0 ? " · 手势还没松手，这一段尚未封口" : ""));
        }

        lines.Add($"读数层自身重算：几何换算 {HudGeometryRebuilds} 次、文字面板 {HudPanelRebuilds} 次；" +
                  $"预览合并 {PreviewFramesForTest} 帧 / 鼠标 {MouseMoveCount} 次事件、" +
                  $"布局重算 {CanvasLayout.BuildCountForTest} 次（进程累计）");
        // 第二十一改：行级那道算术闸跟着进这份文本，三个出口（复制／导出／会话文件）就都能读到它。
        lines.Add(DescribeHudPanelGate());
        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }

    /// <summary>
    /// 可视化调试层的读数面板开关（第十三轮需求六，默认关）。
    /// 打开只多画框与字，一字节都不写进参数、也不参与取数——
    /// 这条等价性由常驻判据 <c>align/关掉调试层不改对齐、蓝线、导出字节与命中区</c> 守着。
    /// </summary>
    public bool ShowDebugHud { get; set; }

    /// <summary>
    /// 把想要的切点夹进重叠带（纯函数，不写参数）。
    ///
    /// 第十四轮末次交互重设计之后，手势与方向键都改走 Core 的 <c>SlideTarget</c>／<c>SlidePicture</c>
    /// （那一条算式里已经含了夹取），所以这一个只留给电池与旧档核对"带里第几行合法"用。
    /// 留在公开面上的理由：判据要用<b>产品自己的</b>夹取口径去核对，
    /// 自己在测试里复述一遍 Math.Clamp 就等于什么都没量。
    /// </summary>
    public static int ClampCut(Seam seam, int wantedCut) =>
        Math.Clamp(wantedCut, 0, Math.Max(0, seam.OverlapLength));

    /// <summary>
    /// 裁切档的方向键／拖动折算：横向恒被丢掉（长截图没有左右移动），纵向原样。
    /// 公开是为了让电池直接量"左右按下去不会挪切点"这件事，与 <see cref="ConstrainDrag"/> 同一地位。
    /// </summary>
    public IntPoint ConstrainCutDelta(Seam seam, IntPoint sourceDelta) =>
        StitchAxes.Enforce(_project.Doc.Mode, sourceDelta);

    public StitchCanvasControl(Project project, CanvasComposer composer)
    {
        _project = project ?? throw new ArgumentNullException(nameof(project));
        _composer = composer ?? throw new ArgumentNullException(nameof(composer));
        _tiles = new TileCache(new TileRenderer(composer));

        // 自绘控件：双缓冲 + 尺寸变化即重绘，避免缩放时长图闪烁
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                 | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.CanvasBackground;
        TabStop = true;

        _project.Changed += OnProjectChanged;
        _project.HistoryStepped += OnHistoryStepped;
        Viewport.Changed += OnViewportChanged;
        MouseWheel += OnMouseWheel;

        // 第四十八改：换肤。先把常驻画具刷成当前档（类型初始化只跑一次，若上一次换肤发生在
        // 没有画布实例的时候，静态字段里留的还是旧档的颜色），再订阅之后的每一次换肤。
        RefreshPaintColors();
        Theme.Changed += OnThemeChanged;
    }

    /// <summary>
    /// 换肤时把<b>常驻画具自己的颜色</b>就地改掉（第四十八改）。
    ///
    /// ═══ 为什么是改 Color，而不是把字段改成非 readonly 再重新赋值 ═══
    /// 这批 Pen/SolidBrush 是第三十一/三十二改按需求三第 5 条"不要每帧新建 Brush"提到静态的，
    /// 它们的<b>声明行</b>被门禁逐字钉着（见 checks/round14_audit.py 里那几枚"提到静态"的证据）。
    /// 而 <c>Pen.Color</c> 与 <c>SolidBrush.Color</c> 都是可写属性：改它不换对象、不改线宽虚线型、
    /// 字段仍是 static readonly —— "画具提静态、不许每帧新建"那条红线一个字没动，
    /// 每帧分配计数也不会因为换肤而涨。
    ///
    /// ═══ 哪些故意不刷 ═══
    /// 白色字形（<c>SeamHandleGlyphBrush/Pen</c>）与 F8 读数层的灰/紫/白四件是<b>画在用户截图之上</b>
    /// 的契约色，两档配色里同值；跟着主题翻反而会让"绿=图1、黄=图2"那份约定失效。
    /// <c>Theme.Overlay*</c> 三枚虽然两档同值，这里仍照刷一遍：将来谁改了浅色档的叠层色，
    /// 这条通路不用第二次改。
    /// </summary>
    private static void RefreshPaintColors()
    {
        PatchHighlightPen.Color = Theme.Warning;
        CanvasBackgroundBrush.Color = Theme.CanvasBackground;
        CheckerLightBrush.Color = Theme.CheckerLight;
        CheckerDarkBrush.Color = Theme.CheckerDark;

        SeamLineOkPen.Color = Theme.Accent;
        SeamLineProblemPen.Color = Theme.Warning;
        SeamLinePendingPen.Color = Theme.TextSecondary;
        SeamLineOkSelectedPen.Color = Theme.Accent;
        SeamLineProblemSelectedPen.Color = Theme.Warning;
        SeamLinePendingSelectedPen.Color = Theme.TextSecondary;
        ((SolidBrush)SeamBoxFillOk).Color = Theme.Accent;
        ((SolidBrush)SeamBoxFillProblem).Color = Theme.Warning;
        ((SolidBrush)SeamBoxFillPending).Color = Theme.TextSecondary;
        SeamHandleBorderPen.Color = Theme.WindowBackground;
        SeamBoxBorderAdjust.Color = Theme.Accent;
        ((SolidBrush)SeamHandleFillSelected).Color = Theme.Accent;
        ((SolidBrush)SeamStatusLabelBg).Color = WithAlpha(Theme.PanelBackground, 220);

        HudBeforePen.Color = WithAlpha(Theme.OverlayBefore, 220);
        HudAfterPen.Color = WithAlpha(Theme.OverlayAfter, 220);
        HudArrowPen.Color = Theme.OverlayAlign;
    }

    /// <summary>
    /// 换肤那一趟除了刷画具，还要把<b>已经烤进位图</b>的那两块颜色作废：
    /// F8 读数面板是一张复用缓冲区，底色与字色是上一次画进去的像素，不重画就永远留着旧档。
    /// 这里只把"文本没变就不重建"那道闸的钥匙清掉（<c>_hudText</c>/<c>_hudLines</c>），
    /// 下一帧走<b>复用同一张缓冲区</b>的整块重画路——不新分配位图，所以面板分配计数不涨。
    /// </summary>
    private void OnThemeChanged(ThemePalette old, ThemePalette current)
    {
        RefreshPaintColors();
        _hudText = "";
        _hudLines = null;
        _hudPanelBuiltAt = 0;
        Invalidate();
    }

    /// <summary>
    /// Undo / Redo 换好文档之后，把<b>这一趟改动在屏幕上留下的那段纯显示补偿</b>也一起换回去（第十四轮需求一）。
    ///
    /// ═══ 为什么"撤销"要管视口 ═══
    /// 固定后一段拖对齐时，为了让图2 在屏上原地不动，视口原点跟着 Rect2 顶边挪了几行——那是
    /// <b>纯显示</b>补偿，不写参数、不进撤销栈。文档能靠 Undo 恢复，屏幕上的那一段偏移却没人还：
    /// 于是"参数恢复了、画面位置没恢复，用户得再滚一下才看起来正常"（判据
    /// <c>align/固定后一段拖对齐后撤销：参数、画布高、视口、设备矩形一起恢复</c>）。
    ///
    /// ═══ 三条纪律 ═══
    /// <list type="number">
    ///   <item><b>没挪过屏就不许动屏。</b>快照为 null（面板改参数、列表重排、蓝线手势那些从没补偿过
    ///     视口的改动）或 <see cref="HistoryDisplayState.ViewportMoved"/> 为 false（固定前一段）时
    ///     一句都不做——否则"撤销一次参数改动"会顺手把用户自己滚出来的视野拽走，那是另一个缺陷。</item>
    ///   <item><b>绝对赋值。</b>回到按下那一刻记下的那个原点，不做增量加减：撤销/重做来回几趟也不会攒出取整误差。</item>
    ///   <item><b>只改显示层。</b>不写参数、不再碰修订号（修订号是 Undo 自己 +1 的）。
    ///     预览布局已由 <see cref="OnProjectChanged"/> 作废，瓦片按新修订号整体重来，
    ///     于是 HUD 那批读数（对齐、蓝线、带、视口、设备矩形）读的就是恢复后的那一套公共坐标。</item>
    /// </list>
    /// </summary>
    private void OnHistoryStepped(HistoryStep step, HistoryDisplayState? display)
    {
        if (display is null || !display.ViewportMoved) return;
        IntPoint target = display.ViewportFor(step);
        IntPoint from = Viewport.TopLeft;
        if (target == from) return;
        Viewport.MoveTopLeftTo(target);
        DiagnosticLog.Info(step == HistoryStep.Undo ? "history.viewport.restored" : "history.viewport.reapplied",
            $"from=({from.X},{from.Y}) to=({target.X},{target.Y}) " +
            $"delta=({target.X - from.X},{target.Y - from.Y}) " +
            $"revision={_project.Revision} canvas_height_now={_project.Layout.CanvasSize.Height} " +
            $"parameters_written=false {display.Fields()}");
    }

    /// <summary>
    /// 从这一次手势的流水账里抽出「显示层快照」，交给即将压入撤销栈的那条历史（需求一第 1 条）。
    /// 五个量全部取按下的那一刻记进 <see cref="_trace"/> 的那一份——同一个出处，不在这里另算一遍，
    /// 否则日志里"这一趟挪了几行"与撤销时"该还几行"就可能各说一套。
    /// 没有流水账（不是拼缝手势）就没有快照，撤销照旧只管文档。
    /// </summary>
    private HistoryDisplayState? NewDisplaySnapshot() =>
        _trace is SeamEditTrace t
            ? new HistoryDisplayState(t.ViewportBefore, t.CanvasHeightBefore,
                t.AlignmentOffsetBefore, t.CutBefore, t.FixedSide)
            : null;

    /// <summary>
    /// 提交之后回填「这一趟在屏上留下的结果」。只能在回填：补偿量要用<b>新参数</b>算出来的布局才得出，
    /// 而那份布局在 <see cref="Project.Mutate"/> 之前不存在，所以快照早一步挂进了历史条目、这里补第二笔。
    /// </summary>
    private void CompleteDisplaySnapshot(HistoryDisplayState? display, Seam seam) =>
        display?.Complete(Viewport.TopLeft, _project.Layout.CanvasSize.Height,
            seam.AlignmentOffset, seam.CutPosition);

    protected override void Dispose(bool disposing)
    {
        // 瓦片位图是自行创建的 GDI 对象，不受托管堆回收保证：控件销毁时必须逐个 Dispose，
        // 否则长期开合窗口会累积 GDI 句柄。
        if (disposing)
        {
            _project.Changed -= OnProjectChanged;
            _project.HistoryStepped -= OnHistoryStepped;
            Theme.Changed -= OnThemeChanged;
            DisposeTileBitmaps();
            _settleTimer?.Dispose();          // 计时器也是组件，不释放就挂在容器上
            _hudPanel?.Dispose();
            _hudPanel = null;
        }
        base.Dispose(disposing);
    }

    // 视口的"设备尺寸"必须跟着控件客户区走，否则 VisibleCanvas 恒为空、
    // FitWhole/CenterOn 全部失效（画布看起来就是"不显示"）。
    // 句柄创建时先量一次：Resize 事件在 Show 之前可能已经发完。
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Viewport.SetDeviceSize(ClientSize);
    }

    /// <summary>
    /// 第五十三改（用户：「缩小的时候分析完毕是居中显示，一旦最大化窗口，就不会自动回到居中位置」）：
    /// 两行的顺序不能换。base.OnResize 里会同步发 <see cref="Control.Resize"/> 事件，
    /// 主窗口挂在那上面的 <c>_canvas.Resize += FollowContentCentre</c>（MainForm）就要居中；
    /// 若先把事件发出去再更新 DeviceSize，居中算的是<b>旧</b>客户区尺寸——最大化时算出来
    /// 还是缩小那一档的居中值，看起来就是"没回正中"。先 SetDeviceSize 再发事件，
    /// 事件那一侧读到的永远是新尺寸（判据见 <c>canvas/窗口变化后 Resize 事件拿到的是新尺寸</c>）。
    /// </summary>
    protected override void OnResize(EventArgs e)
    {
        Viewport.SetDeviceSize(ClientSize);
        base.OnResize(e);
    }

    /// <summary>
    /// 让视图跟住内容中心（用户第七条：「分析结束后，画布中的图片无论窗口多大都得保持居中显示，你每次都放在左对齐」）。
    /// 尺寸从画布此刻正在用的那份布局现取，调用方不需要知道 CanvasSize 在哪；
    /// 装不下的那一轴（长截图的纵向）保持原位，见 <see cref="ViewportState.RecentreIfFits"/>。
    /// 纯显示量：一个字节的项目参数都不碰。
    /// 挂点选在主窗口（导入结束／分析写回／窗口尺寸变化），不在这里的 OnResize：
    /// 画布一改尺寸就自己摆正的话，每个拿画布尺寸当夹具的判据起点都会跟着漂，
    /// 而且"用户手动平移之后拖动窗口又被拽回中间"本身就是错的。
    /// </summary>
    public void FollowContentCentre() => Viewport.RecentreIfFits(ActiveLayout.CanvasSize);

    /// <summary>
    /// 工具条「＋／－」的缩放入口（第五十二改，用户图「放大后无法自动居中」）。
    /// 先按视口中心改缩放，再把<b>装得下</b>的那一轴摆回正中：长截图横向永远装得下，
    /// 于是放大缩小之后那条窄内容都停在窗口水平正中，而不是漂在左边。
    /// 装不下的那一轴（纵向几十万行）保持用户此刻看的行，不被拽走（见 <see cref="FollowContentCentre"/>）。
    /// 刻意<b>不</b>把这套居中塞进 <see cref="ViewportState.ZoomAt"/>／Ctrl+滚轮：那条通路契约是
    /// 「指针下的源像素缩放前后不动」（需求 §4，判据 <c>canvas/Ctrl+滚轮只改缩放与视口，锚点像素不漂</c>），
    /// 居中会把它推翻。工具条缩放是"重新取景"，与指针锚定是两件事，各走各的入口。
    /// 判据见 <c>canvas/工具条缩放后装得下那一轴自动居中</c>。
    /// </summary>
    public void ZoomStep(double factor)
    {
        Viewport.SetZoom(Viewport.Zoom * factor);
        FollowContentCentre();
    }

    /// <summary>工具条「100%」入口：实际像素 + 装得下那一轴居中（与 <see cref="ZoomStep"/> 同一口径）。</summary>
    public void ZoomActualSize()
    {
        Viewport.SetOneToOne();
        FollowContentCentre();
    }

    private void OnProjectChanged()
    {
        // 参数变了：预览布局作废，瓦片整体失效
        _previewLayout = null;
        _dragDelta = IntPoint.Zero;
        RequestRepaint();
    }

    /// <summary>视口变了要重画——但批次期间这一笔也要挂起（见 <see cref="SuspendRepaints"/>）。</summary>
    private void OnViewportChanged() => RequestRepaint();

    /// <summary>
    /// 导入／分析那一段批量期间把画布的重画挂起：每一次 <see cref="Invalidate"/> 改成记一笔账，
    /// 放闸时一次补上。逐张导入时"每追加一段整页就刷一次"的闪烁，就是这一趟趟 Invalidate
    /// 被循环里那下 <c>PumpUi()</c> 泵上屏造成的（用户第四十三改报了闪烁、第四十四改复看仍闪：
    /// 上一轮只闸住了主窗口的 <c>RefreshChrome</c>，画布与左侧素材列表这两条直通的车没闸）。
    /// 挂起期间画布内容照旧是旧的——那正是用户要的"进度条走完之前页面不更新"。
    /// </summary>
    /// <param name="suspend">true 挂起；false 放闸，欠着的重画一次补上。</param>
    public void SuspendRepaints(bool suspend)
    {
        _repaintSuspended = suspend;
        if (!suspend && _repaintOwed)
        {
            _repaintOwed = false;
            Invalidate();
        }
    }

    private bool _repaintSuspended;
    private bool _repaintOwed;

    /// <summary>批次期间被挂起的重画请求笔数（进程累计）——判据拿它对账"不是没通知，是通知被接住了"。</summary>
    public long RepaintsDeferredForTest { get; private set; }

    /// <summary>此刻是否还欠着一帧没画（放闸后必须为 false：欠账不许挂着永远不画）。</summary>
    public bool RepaintOwedForTest => _repaintOwed;

    /// <summary>真窗口上屏的帧数（进程累计，不含 <see cref="RenderFrameForTest(System.Drawing.Bitmap)"/> 那些离屏取帧）。</summary>
    public long ScreenPaintsForTest { get; private set; }

    private void RequestRepaint()
    {
        if (_repaintSuspended)
        {
            _repaintOwed = true;
            RepaintsDeferredForTest++;
        }
        else
        {
            Invalidate();
        }
    }

    /// <summary>当前用于绘制与命中的布局：拖动中用预览版，否则用项目版。</summary>
    private CanvasLayout ActiveLayout => _previewLayout ?? _project.Layout;

    /// <summary>
    /// 仅供判据：此刻画的是不是那份<b>临时预览布局</b>。
    /// 需求一第 2 条把"预览布局"列为撤销必须恢复的一项，而它此前只被"撤销后那一帧与拖动前逐处相同"
    /// 间接推到——间接推论在有句柄的真窗口里成立（那会真的重画），在离屏工装里却不成立：
    /// 离屏只画显式那一帧，临时布局若还挂着，视口对了、像素对了也照样看不出来。
    /// 现在直接量这个布尔，拖动中途必须为真（否则这条判据量的就是"什么都没发生"），撤销后必须为假。
    /// </summary>
    public bool UsingPreviewLayoutForTest => _previewLayout is not null;

    // ───────────────────────── 选择与编辑 ─────────────────────────

    /// <summary>选中/退出某条拼缝。传 null 回到浏览态。</summary>
    public void SelectSeam(string? seamId, bool scrollIntoView = false)
    {
        if (SelectedSeamId == seamId)
        {
            if (seamId is null) return;
        }
        SelectedSeamId = seamId;
        _previewLayout = null;
        // 退出选中就是退出编辑：微调模式与已选的那一侧一起收掉，
        // 否则下一条缝被选中时会莫名带着上一条的侧别（那是"按了不等于走了"的另一种形状）。
        if (seamId is null && _adjustMode) SetAdjustMode(false);
        Cursor = seamId is null ? Cursors.Default : Cursors.SizeAll;
        if (seamId is not null && scrollIntoView) EnsureSeamVisible(seamId);
        SelectionChanged?.Invoke(seamId);
        Invalidate();
    }

    // ───────────────────────── 微调模式（「线不动、图动」的键盘入口）─────────────────────────

    /// <summary>微调模式开关变化时往外报一句话（主窗口拿去写状态栏）。</summary>
    public event Action<string?>? AdjustModeMessage;

    /// <summary>
    /// 方向键"按了没反应"时用一句话说明为什么没反应（主窗口拿去写状态栏）。
    /// 项目自己的纪律：按了没反应必须说清，否则用户只能猜是卡了还是键没被认。
    /// 两条静默早退（整笔位移被夹没／滑到头弹回原值）过去一声不吭——
    /// 用户第四十六改报的"方向键偶发失灵"就藏在这里：现在它会出声，也会进诊断日志，
    /// 下一次再发生就能被状态栏与日志点名，而不是靠猜（判据
    /// <c>canvas/重叠为零的缝按方向键必须出声</c>）。
    /// </summary>
    public event Action<string>? NudgeNotice;

    /// <summary>
    /// 开/关微调模式。
    ///
    /// 这个模式存在的理由只有一条：<b>重叠带可以薄到几行</b>（缩到 25% 时屏上只剩一两个像素），
    /// 那时候鼠标根本没有可抓的地方。开了模式之后"选哪一张图"变成一次明确的点击，
    /// 之后的 ↑/↓ 每次走 1 个源像素——用户不必再瞄准那条带。
    /// </summary>
    public void SetAdjustMode(bool on)
    {
        if (_adjustMode == on)
        {
            // 已经开着还再开一次：只清掉侧别，让用户重新点一张图（对应"再点一次把手"的直觉）
            if (on && _adjustSide is not null)
            {
                _adjustSide = null;
                ReportAdjustMode();
                Invalidate();
            }
            return;
        }
        _adjustMode = on;
        _adjustSide = null;
        Cursor = on ? Cursors.Cross : (SelectedSeamId is null ? Cursors.Default : Cursors.SizeAll);
        DiagnosticLog.Info("seam.adjust.mode",
            $"on={(on ? "true" : "false")} seam={SelectedSeamId ?? "none"} side=none revision={_project.Revision}");
        ReportAdjustMode();
        Invalidate();
    }

    /// <summary>
    /// 选定"跟着方向键走的是哪一张图"。<paramref name="source"/> 只用于日志与状态栏，
    /// 它记的是用户到底从哪儿选的侧（点把手后点图、点蓝线、还是拖动时命中的那一侧）。
    /// </summary>
    public void PickAdjustSide(FixedSide side, string source)
    {
        if (!_adjustMode) SetAdjustMode(true);
        if (_adjustSide == side)
        {
            ReportAdjustMode();
            return;
        }
        _adjustSide = side;
        DiagnosticLog.Info("seam.adjust.side",
            $"seam={SelectedSeamId ?? "none"} side={side} source={source} " +
            $"fixedSide={OppositeOfSide(side)} revision={_project.Revision}");
        ReportAdjustMode();
        Invalidate();
    }

    /// <summary>"没被选中的那一张"＝屏幕上钉住的那一张（与 Core 的 SlidePicture 同一个口径）。</summary>
    private static FixedSide OppositeOfSide(FixedSide grabbed) =>
        grabbed == FixedSide.After ? FixedSide.Before : FixedSide.After;

    /// <summary>把微调模式那句提示报到状态栏（关着就报 null，让主窗口清掉那一行）。</summary>
    private void ReportAdjustMode()
    {
        string? message = !_adjustMode
            ? null
            : _adjustSide is FixedSide side
                ? $"微调：↑/↓ 每次挪 1 个源像素，挪的是{(side == FixedSide.Before ? "上方" : "下方")}那张图" +
                  $"（{(side == FixedSide.Before ? "下方" : "上方")}那张钉在屏幕上不动）· Shift 加速 ×10 · 点蓝线上的 ✓ 或按 Esc 退出编辑"
                : "微调：点一下要挪的那张图（上方或下方都算），再按 ↑/↓ 逐像素调 · 点蓝线上的 ✓ 或按 Esc 退出编辑";
        AdjustModeMessage?.Invoke(message);
    }

    /// <summary>
    /// 指针落在选中那条缝的蓝线上方还是下方（微调模式点选用）。
    /// 返回 null 表示"这条缝现在没落位"——那时候不猜侧别，让按下退化成平移。
    /// </summary>
    private FixedSide? SideAcrossSelectedLine(Point devicePoint)
    {
        if (SelectedSeamId is null) return null;
        PlacedSegment? placed = ActiveLayout.Placed.FirstOrDefault(p => p.IncomingSeam?.Id == SelectedSeamId);
        if (placed is null) return null;
        return SideAcrossLine(SelectedSeamId, devicePoint, ActiveLayout.Mode == StitchMode.Horizontal);
    }

    private void EnsureSeamVisible(string seamId)
    {
        Seam? seam = _project.Doc.Seams.FirstOrDefault(s => s.Id == seamId);
        if (seam is null) return;
        PlacedSegment? placed = ActiveLayout.Find(seam.AfterId);
        if (placed is null) return;
        Viewport.EnsureVisible(new IntRect(placed.Origin.X, placed.Origin.Y - 40,
            Math.Max(1, placed.Rect.Width), 80));
    }

    /// <summary>
    /// 方向键：一次移动 1 个<b>源图像素</b>（需求 §5.2，与预览缩放无关）。
    ///
    /// 长截图那一档只有一种含义：<b>把选中的那一张图上下挪几行，蓝线钉在屏幕原地不动</b>
    /// （走 Core 的 <see cref="Seam.SlidePicture"/>，与鼠标拖动同一个出口）。
    /// 挪哪一张由 <see cref="ActiveSlideSide"/> 定：微调模式里点过的那一侧优先，
    /// 其次是上次按下时命中的那一侧，都没有就默认下方那张。
    /// 竖拼／横拼（位移档）照旧移动图片位置。两档里 ←→ 都不能改动横向：长截图压根没有左右移动。
    /// </summary>
    public void NudgeSelected(IntPoint sourceDelta)
    {
        Seam? seam = SelectedSeam();
        if (seam is null || sourceDelta == IntPoint.Zero) return;

        if (ActiveLayout.UsesCropPlacement)
        {
            IntPoint allowed = ConstrainCutDelta(seam, sourceDelta);
            // ←→ 在长截图里必须"按了没反应"，而且要说清为什么没反应，不能让用户以为卡住了
            ReportHorizontalLock(seam, sourceDelta, allowed, "方向键");
            if (allowed.Y == 0)
            {
                ReportNudgeSilence(seam, sourceDelta, seam.OverlapLength == 0
                    ? "这条缝的重叠是 0 行，没有可滑的行：先重新分析，或在面板里手动给重叠"
                    : $"这个方向已到重叠尽头（重叠 {seam.OverlapLength} 行、切点 {seam.CutPosition}），再按只会原样弹回");
                return;
            }

            FixedSide grabbed = ActiveSlideSide(seam);
            (int wantOverlap, int wantCut, int rejected) = seam.SlideTarget(grabbed, allowed.Y);
            bool mutated = wantOverlap != seam.OverlapLength || wantCut != seam.CutPosition;
            if (rejected != 0) CutClamped?.Invoke(rejected);
            // 滑到头又回到原值：不留空撤销记录（否则用户下一次 Ctrl+Z 退掉一个他根本没做的改动）
            if (!mutated)
            {
                ReportNudgeSilence(seam, sourceDelta,
                    $"这一按被夹回原值（重叠 {seam.OverlapLength} 行、切点 {seam.CutPosition}）：这个方向已经到头");
                return;
            }

            int oldOverlap = seam.OverlapLength;
            int oldCut = seam.CutPosition;
            _dragViewportBase = Viewport.TopLeft;
            _dragAnchorBase = ScreenAnchorRow(_project.Layout, seam);
            var display = new HistoryDisplayState(Viewport.TopLeft, _project.Layout.CanvasSize.Height,
                seam.AlignmentOffset, seam.CutPosition, seam.Fixed);
            SlideApply applied = default;
            // 每次按键都记一条历史：需求 §4 的表格明确要求"每次移动 1 源像素"要能逐次退回
            _project.Mutate("方向键滑动图片", _ => applied = seam.SlidePicture(grabbed, allowed.Y), display);
            ApplyFixedSideAnchor(seam, OppositeOfSide(grabbed),
                ScreenAnchorRow(_project.Layout, seam), "seam.slide.fixed_side");
            CompleteDisplaySnapshot(display, seam);
            DiagnosticLog.Info("seam.slide.keyboard",
                $"seam={seam.Id} grabbed={grabbed} rows={allowed.Y} fixedSide={seam.Fixed} " +
                $"overlapBefore={oldOverlap} overlapAfter={seam.OverlapLength} " +
                $"cutBefore={oldCut} cutAfter={seam.CutPosition} rejected={applied.RejectedRows} " +
                $"lineCanvasRow={(_project.Layout.DescribeSeam(seam)?.CutYCanvas ?? 0)} " +
                $"lineDevice={LineDeviceRow(_project.Layout, seam)} " +
                $"viewport={Viewport.TopLeft} mutated=true undoPushed=true");
            Invalidate();
            Dragging?.Invoke(EffectiveOffset(seam));
            return;
        }

        IntPoint current = EffectiveOffset(seam);
        IntPoint wantedOffset = current + ApplyFixedSide(seam, sourceDelta);
        IntPoint next = ConstrainDrag(seam, wantedOffset);
        // ←→ 在长截图里必须"按了没反应"，而且要说清为什么没反应，不能让用户以为卡住了
        ReportHorizontalLock(seam, wantedOffset, next, "方向键");
        if (next == current)
        {
            ReportNudgeSilence(seam, sourceDelta, "这一按落在可移动范围的尽头，位移一字没变");
            return;
        }

        // 每次按键都记一条历史：需求 §4 的表格明确要求"每次移动 1 源像素"要能逐次退回
        _project.Mutate("方向键微调拼缝", _ => seam.ApplyManual(next, _project.Doc.Mode));
        Invalidate();
        Dragging?.Invoke(next);
    }

    /// <summary>方向键没产生任何变化时，把"为什么"交出去（状态栏一句＋日志一条）。</summary>
    private void ReportNudgeSilence(Seam seam, IntPoint delta, string why)
    {
        DiagnosticLog.Info("seam.slide.keyboard_silent",
            $"seam={seam.Id} delta=({delta.X},{delta.Y}) overlap={seam.OverlapLength} " +
            $"cut={seam.CutPosition} status={seam.Status} why={why}");
        NudgeNotice?.Invoke(why);
    }

    /// <summary>把拼缝恢复到手动调整前的生效位移（供"取消本次调整"用）。</summary>
    public void CancelAdjustment()
    {
        Seam? seam = SelectedSeam();
        if (seam is null) return;
        _project.Mutate("取消本次调整", _ =>
        {
            seam.ManualOffset = null;
            seam.Status = seam.AutoStatus;
            seam.Cause = SeamCause.None;
            // 裁切档里"本次调整"就是那一次滑动。而「线不动、图动」那一轮之后，
            // 一次滑动<b>同时</b>改了重叠与蓝线（守恒的是蓝线的画布行号），
            // 所以两半都得还回去：只钉蓝线不还重叠，图片就还停在被挪走的地方，那等于没取消。
            // 顺序有意义：SetOverlap 会按相对位置换算蓝线，先还原重叠、再把蓝线钉回自动那一格。
            if (ActiveLayout.UsesCropPlacement)
            {
                seam.CancelAlignmentAdjustment();
                seam.CancelCutAdjustment();
            }
        });
        Invalidate();
    }

    public Seam? SelectedSeam() =>
        SelectedSeamId is null ? null : _project.Doc.Seams.FirstOrDefault(s => s.Id == SelectedSeamId);

    /// <summary>
    /// 方向键此刻要挪的是哪一张图。
    ///
    /// 三个出处按优先级取，取到就停：
    /// ① 微调模式里用户<b>点过</b>的那一侧（最明确，它就是用户刚才那一下点的图）；
    /// ② 上一次按下时命中的那一侧（用户拖过红框/带之后直接按方向键，不该要求他再点一次）；
    /// ③ 都没有就默认<b>下方</b>那张——长截图里"把下一张往上拉一点"是压倒性多数的意图。
    ///
    /// 侧别只在选中缝没变的时候才算数：换了缝还沿用上一条的侧别，
    /// 就是"按了不等于走了"的另一种形状（用户以为在挪这张，其实挪的是另一张）。
    /// </summary>
    private FixedSide ActiveSlideSide(Seam seam)
    {
        if (_adjustSide is FixedSide picked) return picked;
        if (_hitUnderCursor is SeamHit hit && hit.SeamId == seam.Id) return hit.Side;
        return FixedSide.After;
    }

    /// <summary>
    /// 这条缝的蓝线现在落在<b>设备像素</b>的哪一行（横向模式是哪一列）。
    /// 与 <see cref="FillLineAnchor"/> 走同一个换算，公开出来给键盘那一条日志与判据读。
    /// </summary>
    public int LineDeviceRow(CanvasLayout layout, Seam seam)
    {
        if (layout.DescribeSeam(seam) is not SeamGeometry geo) return 0;
        bool horizontal = layout.Mode == StitchMode.Horizontal;
        IntRect lineRow = horizontal
            ? new IntRect(geo.CutYCanvas, 0, 1, 1)
            : new IntRect(0, geo.CutYCanvas, 1, 1);
        Rectangle device = Viewport.CanvasToDevice(lineRow);
        return horizontal ? device.X : device.Y;
    }

    /// <summary>
    /// 生效位移。直接取布局算好的那一个数——紧接量、轴规则都在 Core 里，
    /// 这里不再用 <c>before.Rect.Height</c> 复述一遍，免得屏上显示的 X 与像素落位分叉。
    /// </summary>
    private IntPoint EffectiveOffset(Seam seam) => ActiveLayout.EffectiveOffset(seam);

    /// <summary>固定侧换算：固定"后一段"时，用户拖动的是前一段，位移符号要反过来。</summary>
    private static IntPoint ApplyFixedSide(Seam seam, IntPoint userDelta) =>
        seam.Fixed == FixedSide.Before ? userDelta : new IntPoint(-userDelta.X, -userDelta.Y);

    /// <summary>
    /// 把用户想要的位移折算成允许的位移：先看这条缝自己选的约束（仅上下/仅左右/双向），
    /// 再按拼合模式收一道——<b>长截图的横向一律抹平</b>，所以拖动往左右走不会动拼缝。
    /// 公开是为了让电池直接量这条折算（拖动与方向键都走它），不是在外面复述一遍规则。
    /// </summary>
    public IntPoint ConstrainDrag(Seam seam, IntPoint offset)
    {
        IntPoint byChoice = StitchAxes.Enforce(_project.Doc.Mode, seam.Constraint) switch
        {
            MoveConstraint.VerticalOnly => new IntPoint(0, offset.Y),
            MoveConstraint.HorizontalOnly => new IntPoint(offset.X, 0),
            _ => offset,
        };
        return StitchAxes.Enforce(_project.Doc.Mode, byChoice);
    }

    /// <summary>
    /// 拖动或方向键想要横向位移、但被模式锁掉时出声：一次动作只在"第一次撞上锁"时报一回，
    /// 免得按住鼠标横向拖 200 像素就刷 200 条提示与日志。
    /// </summary>
    /// <param name="seam">当前拼缝。</param>
    /// <param name="wanted">折算前的位移（用户意图）。</param>
    /// <param name="allowed">折算后真正允许的位移。</param>
    /// <param name="source">动作来源，写进日志供复核（拖动/方向键）。</param>
    private void ReportHorizontalLock(Seam seam, IntPoint wanted, IntPoint allowed, string source)
    {
        int dropped = wanted.X - allowed.X;
        if (dropped == 0)
        {
            _lockNoticed = false;      // 这一轮没有横向意图：下次再撞上锁时重新报一次
            return;
        }
        DiagnosticLog.Info("analysis.longscroll.constraint",
            $"seam={seam.Id} source={source} mode={_project.Doc.Mode} " +
            $"requested=({wanted.X},{wanted.Y}) allowed=({allowed.X},{allowed.Y}) dropped_x={dropped}");
        if (_lockNoticed) return;
        _lockNoticed = true;
        HorizontalShiftRejected?.Invoke(dropped);
    }

    // ───────────────────────── 鼠标 ─────────────────────────

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        MousePress(e.Button, ModifierKeys, e.Location);
    }

    /// <summary>
    /// 按下时决定这一次手势是什么。<see cref="OnMouseDown"/> 只做转发，
    /// 公开出来是为了让电池能按同样的入参走**同一条**判定，而不是在外面复述一遍优先级。
    ///
    /// 优先级（模拟 UI 需求 §4 的表格，第八轮按"点哪儿办哪儿的事"重排）：
    /// ① Shift + 左键 / 中键 → 平移画布，永不改参数；
    /// ② 点选切分这种临时模式 → 先满足临时模式（否则点不中要切的那一行）；
    /// ③ 命中拼缝线或重叠带 → 进这条缝的编辑；
    /// ④ 其它一切 → 平移画布。<b>这里不看 SelectedSeamId</b>。
    /// </summary>
    public void MousePress(MouseButtons button, Keys modifiers, Point device)
    {
        _downAt = device;
        _lastAt = device;
        _dragDelta = IntPoint.Zero;
        _dragStarted = false;
        _lockNoticed = false;
        _trace = null;          // 一次按下一本流水账；没命中编辑区就没有 seam.edit.* 那一族
        _previewLoggedAt = device;   // 按下这一点算第一条 preview 的基准（别用哨兵值，Distance 会溢出）

        bool shift = (modifiers & Keys.Shift) != 0;

        // 中键：无论按在哪儿都只平移（需求 §4：浏览画布不该被编辑状态抢走）
        if (button == MouseButtons.Middle)
        {
            BeginPan(shift ? "Shift+中键" : "中键", device);
            return;
        }
        if (button != MouseButtons.Left) { _drag = DragMode.None; return; }

        // 优先级最高：Shift + 左键永远只平移画布
        if (shift)
        {
            BeginPan("Shift+左键", device);
            return;
        }

        if (_splitPick)
        {
            _drag = DragMode.SplitClick;
            return;
        }

        SeamHit? hit = HitTestSeamEditRegion(device);
        _seamUnderCursor = hit?.SeamId;
        _hitUnderCursor = hit;      // 这次手势到底改哪个量，由它决定（见 PendingSeamClick 分支）
        _lastHitKindText = HitKindText(hit);      // 需求四：F8 上"当前命中类型"就是这一句
        LogHitTest(device, hit);

        // 把手那一档只切换"精调模式"，<b>永不开拖动手势</b>：它就骑在蓝线上，
        // 若也进拖动，那等于又给了用户一条"把线拖走"的路——正是这次重设计要拆掉的那一条。
        if (hit is { Kind: SeamHitKind.AdjustHandle })
        {
            if (_adjustMode)
            {
                // 第二下就是<b>「完成」</b>：退档 + 取消选中，一次点干净。
                // 以前它只把开关拨回 off、选中的缝还留着，于是用户还得再按两次 Esc
                // （第一次退档、第二次才取消选中）才算退出编辑这条缝——
                // 他把"点这枚按钮"当成一件事，界面就不该把它拆成三下。
                SetAdjustMode(false);
                SelectSeam(null);
            }
            else
            {
                SelectSeam(hit.Value.SeamId);
                SetAdjustMode(true);
            }
            _drag = DragMode.None;
            return;
        }

        if (hit is not null)
        {
            // 压着蓝线按下 ⇒ 拖的是<b>下方</b>那张（边界下面那一张跟着手走，这是唯一不歧义的读法）；
            // 压在带里或红箭头上 ⇒ 按"在线上方还是下方"定侧别（SideAcrossLine 早就算好了）。
            _slideGrabbed = hit.Value.Kind == SeamHitKind.CutLine ? FixedSide.After : hit.Value.Side;
            if (hit.Value.Kind == SeamHitKind.CutLine || _adjustMode)
            {
                // 点蓝线本身也进微调模式：用户不必先找到那枚把手就能开始按方向键
                SetAdjustMode(true);
                PickAdjustSide(_slideGrabbed, hit.Value.Kind == SeamHitKind.CutLine ? "点蓝线" : "点选");
            }
            // 单击与拖动由位移阈值区分：先记成"待定点击"，一旦超过阈值就转成拖动
            _drag = DragMode.PendingSeamClick;
            _trace = StartSeamTrace(SeamById(hit.Value.SeamId), hit.Value, device);
            return;
        }

        // ═══ 微调模式下点在带外、线外：那也算"点选上方或下方那张图" ═══
        // 用户要点的就是那两张图，而它们的绝大部分面积本来就在重叠带之外；
        // 只认带内命中就等于"必须先瞄准那条薄到几行的带"，那正是这次要消掉的复杂度。
        // 浏览不会被抢走：Shift+左键与中键在这之前就已经拦成平移了，Esc 随时退模式。
        Seam? adjustSeam = SelectedSeam();
        if (_adjustMode && adjustSeam is not null && SideAcrossSelectedLine(device) is FixedSide picked)
        {
            _slideGrabbed = picked;
            PickAdjustSide(picked, "点选");
            _seamUnderCursor = SelectedSeamId;
            _hitUnderCursor = new SeamHit(adjustSeam.Id, SeamHitKind.OverlapBand, picked);
            _drag = DragMode.PendingSeamClick;      // 就地也能拖：越过阈值就滑这张图
            _trace = StartSeamTrace(adjustSeam, _hitUnderCursor.Value, device);
            return;
        }

        // 带外、线外：普通画布区域 ⇒ 平移。即使上一条拼缝还处在选中状态也一样
        BeginPan("左键", device);
    }

    /// <summary>进入平移：记下起点视口，供 canvas.pan.end 报"这一趟挪了多少、参数没动"。</summary>
    private void BeginPan(string source, Point device)
    {
        _drag = DragMode.PanCanvas;
        _panStartTopLeft = Viewport.TopLeft;
        _panStartRevision = _project.Revision;
        Cursor = Cursors.Hand;
        MarkGestureFrames();
        DiagnosticLog.Info("canvas.pan.begin",
            $"source={source} revision={_project.Revision} viewport={_panStartTopLeft} " +
            $"zoom={Viewport.Zoom:F4} device={device}");
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        MouseMoveTo(e.Location);
    }

    /// <summary>
    /// 移动中的手势分发。与 <see cref="MousePress"/> 一样公开出来给电池走同一条通路。
    /// </summary>
    public void MouseMoveTo(Point device)
    {
        // 鼠标事件计数与"处理到这一刻的时钟"（需求五：工装拿后者减出 UI 消息队列延迟）。
        // 记在这里而不是 OnMouseMove：真实消息与电池直接调用走的是同一条通路，读数才有可比性。
        MouseMoveCount++;
        LastInputTicks = Environment.TickCount64;
        if (_drag == DragMode.None)
        {
            // 点选模式：只为画引导线跟踪悬停位置，光标保持十字
            if (_splitPick)
            {
                _splitHover = device;
                Invalidate();
                return;
            }
            SeamHit? hover = HitTestSeamEditRegion(device);
            _lastHitKindText = HitKindText(hover);       // 屏上那一行"当前命中类型"跟着指针走（需求四）
            // 命中编辑区就给"可以拖"的手型，让用户不点下去也知道这一块是能点的
            Cursor = hover is null ? Cursors.Default : Cursors.SizeAll;
            // 提示只在命中结果<b>变了</b>的时候才往外发（第十一轮）。
            // 原来每个 MouseMove 都发一次 ⇒ 主窗口每移动一像素 Show/Hide 一次 ToolTip，
            // 那是系统窗口级操作，鼠标在拼缝上来回扫一遍就够把界面拖成幻灯片。
            if (!hover.Equals(_lastHoverHit))
            {
                _lastHoverHit = hover;
                HoverSeamTip?.Invoke(DescribeSeamAt(hover), device);
            }
            return;
        }

        switch (_drag)
        {
            case DragMode.PanCanvas:
            {
                Viewport.PanByDevice(new IntPoint(device.X - _lastAt.X, device.Y - _lastAt.Y));
                break;
            }
            case DragMode.SplitClick:
            {
                // 按下后拖出阈值：这次不算"点选"，退化为平移，模式保持开启
                if (Distance(device, _downAt) >= ClickThresholdDevicePx)
                    Viewport.PanByDevice(new IntPoint(device.X - _lastAt.X, device.Y - _lastAt.Y));
                break;
            }
            case DragMode.PendingSeamClick:
            {
                if (Distance(device, _downAt) < ClickThresholdDevicePx) break;
                // 在编辑区里按下去又拖动：进入"调这条缝"，不误判为退出编辑
                Seam? seam = _project.Doc.Seams.FirstOrDefault(s => s.Id == _seamUnderCursor);
                if (seam is null) break;
                SelectSeam(seam.Id);
                _dragBaseOffset = EffectiveOffset(seam);
                _dragViewportBase = Viewport.TopLeft;
                // 滑动手势不再单独记"起点切点/起点重叠"：预览期间参数一个字都不写，
                // 所以 seam.CutPosition 与 seam.OverlapLength 全程就是起点那一组，
                // SlideTarget 拿累计位移去算即可（多记一份基准反而会有"两份起点各说一套"的风险）。
                // 固定后一段的视口补偿基准：图2 顶边在按下这一刻的画布行号（每帧减它，见 ApplyFixedSideAnchor）。
                _dragAnchorBase = ScreenAnchorRow(ActiveLayout, seam);

                // ═══ 长截图只有一档手势：滑动那一张图，蓝线钉在屏幕原地 ═══
                // 原来这里是"命中蓝线改 CutY / 命中带改对齐"两档，用户得先分清两个概念才敢下手；
                // 现在侧别在按下那一刻就定好了（_slideGrabbed），手势自始至终只做一件事。
                bool slide = ActiveLayout.UsesCropPlacement;
                _drag = slide ? DragMode.SlidePicture : DragMode.MoveSeam;
                MarkGestureFrames();
                if (_trace is not null)
                {
                    _trace.AlignMode = slide;
                    _trace.GestureSide = _slideGrabbed;
                    _trace.DragMode = slide ? "SlidePicture" : "MoveSeam";
                }
                Cursor = Cursors.SizeAll;
                goto case DragMode.MoveSeam;
            }
            case DragMode.MoveSeam:
            case DragMode.SlidePicture:
            {
                if (Distance(device, _downAt) >= ClickThresholdDevicePx) _dragStarted = true;
                if (!_dragStarted) break;

                // 屏幕位移 → 源像素位移：整段结束时才取整一次（见 GuardTests 的那条红线）
                int dx = (int)Math.Round((device.X - _downAt.X) / Viewport.Zoom);
                int dy = (int)Math.Round((device.Y - _downAt.Y) / Viewport.Zoom);
                _dragDelta = new IntPoint(dx, dy);
                if (_trace is not null) _trace.DeviceDelta = new Point(device.X - _downAt.X, device.Y - _downAt.Y);

                Seam? active = SelectedSeam();
                if (active is not null) QueuePreviewFrame(device);
                Dragging?.Invoke(_dragDelta);
                break;
            }
        }
        _lastAt = device;
    }

    /// <summary>
    /// 把这一帧预览排队，而不是就地做完整重算（第十一轮）。
    ///
    /// 累计位移 <see cref="_dragDelta"/> 已经在调用方按最新指针位置更新过了，
    /// 所以"丢掉中间帧"不会丢输入：排队期间无论并掉多少个 MouseMove，
    /// 真正执行的那一帧用的都是<b>当时最新</b>的位移，松手时提交的也是同一个数。
    /// </summary>
    private void QueuePreviewFrame(Point device)
    {
        _latestMouse = device;
        if (_previewPending) return;
        _previewPending = true;

        if (!IsHandleCreated)
        {
            // 控件还没上屏（电池的工装就是这样）：BeginInvoke 会抛 InvalidOperationException。
            // 就地执行同一份计算——合并是"什么时候做"的问题，判据量的是"做了什么"，
            // 两条路走的是同一个 RunPreviewFrame，不存在"测的是一套、跑的是另一套"。
            RunPreviewFrame();
            return;
        }
        BeginInvoke(RunPreviewFrame);
    }

    /// <summary>一帧预览：只重摆这条缝的临时布局、只重画受它影响的那一小块。</summary>
    private void RunPreviewFrame()
    {
        _previewPending = false;
        // 排队期间用户已经松手了：这一帧作废（参数该不该写由 MouseRelease 说了算）
        if (_drag is not (DragMode.MoveSeam or DragMode.SlidePicture)) return;
        PreviewFramesForTest++;
        Seam? active = SelectedSeam();
        if (active is null) return;
        // ═══ 分档：长截图滑动那一张图，竖拼／横拼移动生效位移 ═══
        // 两者共用"只建临时布局、参数一个字都不写"这一条纪律（松手才提交）。
        if (_drag == DragMode.SlidePicture) UpdatePreviewSlide(active, _dragDelta, _latestMouse);
        else UpdatePreviewOffset(active, _dragDelta, _latestMouse);
    }

    /// <summary>
    /// 拖动中的一帧预览：只建临时布局，参数一个字都不写（松手才提交，需求 §5.2）。
    /// </summary>
    private void UpdatePreviewOffset(Seam seam, IntPoint screenDelta, Point device)
    {
        IntPoint wanted = _dragBaseOffset + ApplyFixedSide(seam, screenDelta);
        IntPoint next = ConstrainDrag(seam, wanted);
        ReportHorizontalLock(seam, wanted, next, "拖动预览");

        _previewLayout = CanvasLayout.Build(_project.Doc,
            new Dictionary<string, IntPoint> { [seam.Id] = next });

        // ═══ 固定侧决定"屏幕上谁不动" ═══
        // 布局永远把最左上角归到 (0,0)，所以画布里"上面那条链"必然钉在画布原点：
        // 固定前一段时后一段跟手（视口一动不动，正是需求要的），
        // 固定后一段时要让**前一段**跟手，只能反过来把视口挪掉这段位移——
        // 这是纯显示补偿：不写参数、不改修订号，补偿量由 (生效位移 − 起点位移) 精确算出，
        // 拖回原位时补偿自然归零。详见 docs/架构说明.md 与 README 的交互表。
        if (seam.Fixed == FixedSide.After)
        {
            IntPoint compensation = next - _dragBaseOffset;
            // 符号是"加"：固定后一段时采纳位移变小 ⇒ 后一段在画布坐标里往上走，
            // 视口原点也必须同步往上走同样这么多，它才在屏幕上原地不动，
            // 于是"跟着手指走的"就变成了前一段。
            Viewport.MoveTopLeftTo(_dragViewportBase + compensation);
            if (_trace is not null && _trace.ViewportAfter != Viewport.TopLeft)
            {
                _trace.ViewportAfter = Viewport.TopLeft;
                DiagnosticLog.Info("seam.edit.fixed_side",
                    $"seam={seam.Id} fixed_side=After compensation=({compensation.X},{compensation.Y}) " +
                    $"viewport={Viewport.TopLeft} zoom={Viewport.Zoom:F4} revision={_project.Revision}");
            }
        }

        _renderRevision++;
        LogPreview(wanted, next, device);
        Invalidate();
    }

    /// <summary>
    /// <b>滑动一张图</b>的一帧预览（第十四轮末次交互重设计）：把 (重叠 O, 带内行号 Cut) 这一对
    /// 临时换成 <see cref="Seam.SlideTarget"/> 算出来的那一组，只用预览布局画出来，
    /// <b>参数一个字都不写</b>（松手才提交，需求 §5.2）。
    ///
    /// ═══ 这一档要保证的观感：蓝线在屏幕上钉住不动，图在线下面滑 ═══
    /// 蓝线的画布行号 = 图1 底边 H1 − O + Cut。抓<b>下方</b>那张时 SlideTarget 让 O 与 Cut 同增同减，
    /// 两者在这一式里对消 ⇒ 画布行号不变、视口也不必动；抓<b>上方</b>那张时 O 变、Cut 不变
    /// ⇒ 画布行号挪几行，由 <see cref="ApplyFixedSideAnchor"/> 那段<b>纯显示</b>的视口补偿钉回屏幕原地
    /// （补偿锚的是图2 顶边 = H1 − O，与蓝线同一条基准，所以两者在屏上一起钉住）。
    ///
    /// ═══ "屏上谁不动"由手势自己决定，不再读下拉框 ═══
    /// 布局永远把最左上角归到 (0,0)，而裁切档里"图1 的位置"只由它上面那条链决定，与本缝的对齐无关
    /// ⇒ 画布坐标里动的总是图2。于是抓下方那张＝图1 屏上不动（固定前一段），
    /// 抓上方那张＝图2 屏上不动（固定后一段，要补偿）。提交时 SlidePicture 把这一结论落进
    /// <c>seam.Fixed</c>，所以日志与 F8 读数里的 <c>fixedSide=</c> 说的仍然是真话。
    /// </summary>
    /// <param name="seam">正在编辑的拼缝。</param>
    /// <param name="sourceDelta">指针位移折算成的源像素（X 不参与，只用于报锁）。</param>
    /// <param name="device">当前指针的设备坐标（限流日志用）。</param>
    private void UpdatePreviewSlide(Seam seam, IntPoint sourceDelta, Point device)
    {
        IntPoint allowed = ConstrainCutDelta(seam, sourceDelta);
        ReportHorizontalLock(seam, sourceDelta, allowed, "滑动图片");

        // 与提交走<b>同一条算式</b>（Core 里纯计算的那一半）：预览看见的那一帧与松手落下的那一格
        // 不可能各说一套——否则用户松手那一刻图会跳一下。
        (int overlap, int cut, int rejected) = seam.SlideTarget(_slideGrabbed, allowed.Y);

        int bandTopBefore = ActiveLayout.BandCanvasRect(seam) is IntRect oldBand ? oldBand.Y : 0;
        // 拖动途中的预览重算<b>不在 OnPaint 里</b>（它发生在处理鼠标消息那一刻），所以帧耗时那批样本
        // 天生量不到它。需求五要"UI 队列延迟"、需求六要"卡顿发生在哪个阶段"——如果卡在这一段，
        // 只量 OnPaint 的读数会让人以为"绘制慢"，其实是"消息处理慢"。这一对计数就是为了分开这两件事。
        long recomputeStart = System.Diagnostics.Stopwatch.GetTimestamp();
        _previewLayout = CanvasLayout.Build(_project.Doc, null,
            new Dictionary<string, SeamAlignment>
                { [seam.Id] = new SeamAlignment(-overlap, cut) });
        PreviewRecomputeMs += (System.Diagnostics.Stopwatch.GetTimestamp() - recomputeStart)
            * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        PreviewRecomputeCount++;
        int bandTopAfter = _previewLayout.BandCanvasRect(seam) is IntRect newBand ? newBand.Y : 0;
        // ═══ 抓上方那张（＝固定后一段）：先补偿视口，再填流水账 ═══
        // 顺序有意义：FillCropGeometry 用<b>此刻的视口</b>换算设备矩形，补偿在前才能让日志里的
        // rect1DeviceAfter/rect2DeviceAfter 与屏幕那一眼看到的一致（否则记的是补偿前的位置）。
        ApplyFixedSideAnchor(seam, OppositeOfSide(_slideGrabbed),
            ScreenAnchorRow(_previewLayout, seam), "seam.alignment.fixed_side");

        if (_trace is not null)
        {
            SeamGeometry? geo = _previewLayout.DescribeSeam(seam);
            _trace.OverlapLength = overlap;
            _trace.AlignmentOffsetAfter = new IntPoint(seam.AlignmentOffset.X, -overlap);
            // 蓝线这一帧落在带里第几行，读<b>预览布局</b>：参数里存的还是手势起点那一组。
            _trace.EffectiveCut = geo?.CutPosition ?? cut;
            _trace.RequestedCut = cut;
            _trace.RejectedCut = rejected;
            _trace.RejectedX = sourceDelta.X - allowed.X;
            _trace.CanvasHeightAfter = _previewLayout.CanvasSize.Height;
            _trace.ViewportAfter = Viewport.TopLeft;
            FillCropGeometry(_trace, _previewLayout, seam);
            FillLineAnchor(_trace, _previewLayout, seam);
        }
        if (rejected != 0) CutClamped?.Invoke(rejected);

        // ═══ 作废"带顶往下"的每一块瓦片，但不整块清缓存 ═══
        // 对齐一改，带顶往下所有行的取数来源与落位都变了（画布高度也变了），所以这一条带以下
        // 必须重画；带<b>以上</b>一行都不该重画——那正是需求十第 7 条"只更新当前拼缝附近的可见瓦片"
        // 与第 5 条"不清空整个 TileCache"的分界。DropRegion 是<b>局部</b>作废，计数上仍算 0 次整块清空。
        int top = Math.Min(bandTopBefore, bandTopAfter);
        int height = Math.Max(_previewLayout.CanvasSize.Height, ActiveLayout.CanvasSize.Height);
        var area = new IntRect(0, top, Math.Max(1, _previewLayout.CanvasSize.Width), Math.Max(1, height - top));
        _tiles.DropRegion(area);
        InvalidatePreviewArea(area);

        LogPreview(_dragBaseOffset, ActiveLayout.EffectiveOffset(seam), device);
    }

    /// <summary>
    /// 只重画带区（外扩 <see cref="PreviewDirtyInflateDevicePx"/> 留给拼缝线、把手、状态字与拖动浮标）。
    /// 上一帧画过的那块也一起作废：切点顶到带边时浮标可能停在带外，不带上它就会留下残影。
    /// </summary>
    private void InvalidatePreviewArea(IntRect canvasBand)
    {
        Rectangle dirty = Viewport.CanvasToDevice(canvasBand);
        dirty.Inflate(PreviewDirtyInflateDevicePx, PreviewDirtyInflateDevicePx);
        Invalidate(Rectangle.Union(dirty, _lastPreviewDirty));
        _lastPreviewDirty = dirty;
    }

    /// <summary>
    /// "固定侧在屏上该钉住的那一点"：这条缝的<b>图2 顶边</b>（Rect2.Top，未裁重叠那一版）在画布坐标里的行号。
    /// 读布局的落位、不自己按 H1−O 算：布局末尾那一次整体归一化（shift）也包含在这个读数里，
    /// 所以"归一化把固定侧悄悄挪了 N 行"这件事会被它抓出来，而不是被绕过去（需求一第 2/3 条）。
    /// </summary>
    private static int ScreenAnchorRow(CanvasLayout layout, Seam seam) =>
        layout.DescribeSeam(seam) is SeamGeometry geo ? geo.Rect2.Y : 0;

    /// <summary>
    /// 施加"固定后一段"所需的那一段<b>纯显示</b>视口补偿（第八轮偏移档同款，需求一要求的例外）。
    ///
    /// 补偿量 = 图2 顶边在画布里挪的那几行；每帧都从<b>同一个起点</b>（按下时的视口）绝对赋值，
    /// 于是逐帧取整不会累加、拖回原位时补偿自然归零。它不写参数、不改修订号、不进撤销栈，
    /// 也不动横向（X 恒按起点）——长截图本来就锁横向。
    ///
    /// <b>固定前一段一句都不做</b>：那一档画布里动的本来就是图2，屏上与画布一致，
    /// 再去挪视口就是需求一明令禁止的"偷偷补偿"。
    /// </summary>
    /// <param name="seam">正在拖的这条缝。</param>
    /// <param name="fixedSide">
    /// 这一趟<b>生效</b>的固定侧。滑动手势传"没被抓的那一张"（<c>OppositeOfSide(_slideGrabbed)</c>），
    /// 而不是读 <c>seam.Fixed</c>：参数要到松手提交那一刻才被改写，预览期间读它读的是上一趟的结论，
    /// 于是"抓上方那张"的第一帧就不会补偿——屏上会看见线先跳一下再钉住。
    /// </param>
    /// <param name="anchorAfter">这一帧（或提交后）图2 顶边的画布行号。</param>
    /// <param name="eventName">这一族事件里那条补偿记录的名字。</param>
    private void ApplyFixedSideAnchor(Seam seam, FixedSide fixedSide, int anchorAfter, string eventName)
    {
        if (fixedSide != FixedSide.After) return;
        int compensation = anchorAfter - _dragAnchorBase;
        IntPoint next = new(_dragViewportBase.X, _dragViewportBase.Y + compensation);
        if (Viewport.TopLeft == next) return;
        Viewport.MoveTopLeftTo(next);
        if (_trace is not null) _trace.ViewportAfter = next;
        // 需求一："如果公共画布必须重新归一化，必须…补偿恢复固定侧的屏幕位置，并且要明确记录原因"。
        // 所以这一行不只是数值，还要把"为什么动视口"写在日志里，事后能分辨这是补偿不是误操作。
        DiagnosticLog.Info(eventName,
            $"seam={seam.Id} fixedSide=After reason=图2钉在屏幕上所以视口跟着Rect2顶边挪 " +
            $"compensation={compensation} anchorBase={_dragAnchorBase} anchorAfter={anchorAfter} " +
            $"viewportBefore={_dragViewportBase} viewportAfter={next} " +
            $"revision={_project.Revision} mutated=false undoPushed=false");
    }

    /// <summary>
    /// 预览结束（提交或取消）：临时布局作废，瓦片按<b>项目布局</b>整体重来一次。
    /// 一次手势只清一遍缓存，代价可以忽略；不清的话屏幕上会留着预览那一帧的像素，
    /// 而用户看到的就是"成品与预览不一致"——那是本项目最怕的误判。
    /// </summary>
    private void EndPreview()
    {
        _previewLayout = null;
        _previewPending = false;
        _renderRevision++;
        _lastPreviewDirty = Rectangle.Empty;
    }

    /// <summary>
    /// <c>seam.edit.preview</c> / <c>seam.crop.preview</c> 的限流记录（第十一轮改成两道闸）：
    /// ① 指针至少走够 <see cref="SeamEditTrace.PreviewLogStepDevicePx"/> 个设备像素；
    /// ② 距上一条至少 <see cref="PreviewLogMinIntervalMs"/> 毫秒。
    ///
    /// 只留①的话，鼠标快速扫过一次就是几十条（每像素折算的源图位移都不同），
    /// 而需求五要的是"最多每 50–100ms 一条"。两道闸一起用，日志既留得住轨迹又不会变成洪水；
    /// 一次拖动的头（begin）和尾（commit/cancel）各一条是必记的，缺了它就复原不出起点与终点。
    /// </summary>
    private void LogPreview(IntPoint requested, IntPoint effective, Point device)
    {
        if (_trace is null) return;
        if (Distance(device, _previewLoggedAt) < SeamEditTrace.PreviewLogStepDevicePx) return;
        long now = Environment.TickCount64;
        if (_previewLoggedAtTicks != long.MinValue &&
            now - _previewLoggedAtTicks < PreviewLogMinIntervalMs) return;
        _previewLoggedAt = device;
        _previewLoggedAtTicks = now;
        _trace.RequestedOffset = requested;
        _trace.EffectiveOffset = effective;
        _trace.SourceDelta = effective - _trace.BeforeOffset;
        DiagnosticLog.Info($"{_trace.EventPrefix}.preview", _trace.Fields());
    }

    /// <summary>
    /// 按下这一刻把"命中判定用到的全部数"记一条 <c>seam.hit_test</c>（需求三）。
    ///
    /// "红框看得见但点不到"这类事，事后只有这一行说得清：设备点、画布点、
    /// 带在画布上的矩形、同一块带映射到屏幕上的矩形、命中的是哪一块、切点与总重叠各是多少。
    /// 前两个一对，就能看出是"带的算错了"还是"换算错了"。
    /// 只在<b>按下</b>时记：悬停每个像素都算一次命中，跟着记就又变成日志洪水。
    /// </summary>
    private void LogHitTest(Point device, SeamHit? hit)
    {
        CanvasLayout layout = ActiveLayout;
        IntPoint canvas = Viewport.DeviceToCanvas(device);
        Seam? seam = hit is null ? null : _project.Doc.Seams.FirstOrDefault(s => s.Id == hit.Value.SeamId);
        IntRect? band = seam is null ? null : layout.BandCanvasRect(seam);
        string bandText = band is IntRect b
            ? $"bandRect={b} deviceBandRect={Viewport.CanvasToDevice(b)} overlapRect={b}"
            : "bandRect=none deviceBandRect=none overlapRect=none";
        // 需求十一那一批"必须区分"的量里，命中这一行能给出的都给出：
        // 公共坐标那三块（rect1/rect2/overlapRect）、蓝线在画布上的绝对行号、对齐位移。
        string geoText = seam is not null && layout.DescribeSeam(seam) is SeamGeometry geo
            ? $"rect1={geo.Rect1} rect2={geo.Rect2} cutY={geo.CutYCanvas} cutYInBand={geo.CutInsideBand.ToString().ToLowerInvariant()} " +
              $"alignmentOffset=({geo.AlignmentOffset.X},{geo.AlignmentOffset.Y}) " +
              $"sourceRect1={geo.BeforeSourceRect} sourceRect2={geo.AfterSourceRect} "
            : "";
        DiagnosticLog.Info("seam.hit_test",
            $"seamId={hit?.SeamId ?? "none"} devicePoint={device} canvasPoint=({canvas.X},{canvas.Y}) " +
            $"{bandText} {geoText}hitKind={hit?.Kind.ToString() ?? "None"} side={hit?.Side.ToString() ?? "None"} " +
            $"cutPosition={seam?.CutPosition ?? 0} cutY={seam?.CutY ?? 0} overlapLength={seam?.OverlapLength ?? 0} " +
            $"zoom={Viewport.Zoom:F4} viewport={Viewport.TopLeft} crop={layout.UsesCropPlacement.ToString().ToLowerInvariant()}");
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        MouseRelease(e.Location);
    }

    /// <summary>松手：只有真正拖过才提交一条可撤销记录；没越过阈值的一律当单击或取消。</summary>
    public void MouseRelease(Point device)
    {
        DragMode finished = _drag;
        // 先把"拖动中"这一段封口，再做提交那一次重算：这样出口里"拖动 N 帧"不包含
        // 松手提交触发的重画（那一次按设计就该重算，不该记在"拖动卡"的账上）。
        if (finished != DragMode.None) _gestureEndTotal = _frameTotal;
        _drag = DragMode.None;
        Cursor = SelectedSeamId is null ? Cursors.Default : Cursors.SizeAll;

        switch (finished)
        {
            case DragMode.PanCanvas:
            {
                DiagnosticLog.Info("canvas.pan.end",
                    $"device_delta=({device.X - _downAt.X},{device.Y - _downAt.Y}) " +
                    $"viewport_before={_panStartTopLeft} viewport_after={Viewport.TopLeft} " +
                    $"zoom={Viewport.Zoom:F4} revision={_project.Revision} " +
                    $"revision_changed={_panStartRevision != _project.Revision}");
                Diagnostics.MemorySnapshot.Log("scroll");
                // ═══ 空白处单击＝退出编辑（用户第四十五改，作用域经用户当场裁定）═══
                // 用户原话："点击了线条但是没有做任何操作，那么我点击画布的空白处应该能退出编辑，
                // 而不是只能按 esc。" 三个条件缺一不可，每条都有格子钉着：
                //   ① 没拖出阈值——平移手势中途松手不许把用户正在编辑的缝踢掉
                //      （<c>canvas/空白处单击退出编辑而空白处拖动仍然平移</c>）；
                //   ② 按下时没命中任何缝／带／方框——点在线上那是"选这条"，不是"退编辑"
                //      （<c>guard/点缝线必须仍是进编辑而不是退编辑</c>）。
                //      这一条还管着 Shift+左键那一支：它在命中测试之前就转成平移手势，
                //      "光标下那条缝"还是上一次点线留下的旧值——少了这一条，
                //      Shift 按一下空白也会退编辑（<c>guard/带 Shift 的空白单击仍是平移不退编辑</c>）；
                //   ③ 此刻确实处在编辑态——浏览态下单击空白保持无操作（没东西可退）。
                // <b>作用域</b>：点蓝线那一下会顺手进微调模式，而微调模式下点带外线外<b>一律</b>算
                // "点选上方／下方那张图"（见 MousePress 里那一支）——它与"点空白退编辑"是同一下单击。
                // 这一处冲突由用户裁定：<b>保留选侧</b>。所以退编辑只在"选着缝但没进微调档"那一档生效
                // （左侧列表点选、或 Esc 退档之后就是这一档）；微调档里退编辑走 Esc、✓（再点一次方框）。
                // 裁定与量法写在 <c>canvas/点过线条再点空白是选侧别不是退编辑</c>。
                if (Distance(device, _downAt) < ClickThresholdDevicePx &&
                    _seamUnderCursor is null && SelectedSeamId is not null)
                {
                    SetAdjustMode(false);
                    SelectSeam(null);
                    DiagnosticLog.Info("canvas.edit.exit", "reason=blank_click");
                }
                break;
            }
            case DragMode.SplitClick:
            {
                // 没拖出阈值 = 有效点选；拖了 = 刚才只是平移，模式继续开着
                if (Distance(device, _downAt) < ClickThresholdDevicePx)
                    HandleSplitPickClick(device);
                break;
            }
            case DragMode.PendingSeamClick:
            {
                // 单击没拖动：蓝线上再点一次 = <b>退出编辑这条缝</b>（第一次点已经进了微调，见 MousePress）。
                // 退就退干净——退档 + 取消选中一起做，与那枚 ✓ 和 Esc 同一个口径。
                // 带里/图上单击只切换方向键要挪的那一侧，<b>不退编辑</b>——
                // 否则用户没法先点一张图再按方向键精调：单击会直接把他踢回浏览态。
                if (_seamUnderCursor == SelectedSeamId && _adjustMode &&
                    _hitUnderCursor?.Kind == SeamHitKind.CutLine)
                {
                    SetAdjustMode(false);
                    SelectSeam(null);
                }
                else if (_seamUnderCursor is not null) SelectSeam(_seamUnderCursor);
                LogCancel("单击未拖动");
                break;
            }
            case DragMode.SlidePicture when _dragStarted:
            {
                // ═══ 滑动提交：一次拖动只写一条撤销记录，走 Core 的 SlidePicture 那一个出口 ═══
                // 预览期间用的是同一条算式的纯计算那一半（SlideTarget），所以松手不会跳。
                Seam? seam = SelectedSeam();
                EndPreview();
                if (seam is null) { Invalidate(); break; }

                IntPoint allowed = ConstrainCutDelta(seam, _dragDelta);
                FixedSide grabbed = _slideGrabbed;
                (int wantOverlap, int wantCut, int rejected) = seam.SlideTarget(grabbed, allowed.Y);
                bool mutated = wantOverlap != seam.OverlapLength || wantCut != seam.CutPosition;
                bool undoBefore = _project.CanUndo;
                string? undoLabelBefore = _project.NextUndoLabel;
                // 结果留在闭包外面接住：被夹掉的行数只有它知道（界要据此出声）。
                SlideApply applied = default;
                // 这一趟的显示快照挂在<b>同一条</b>历史里（需求一）：撤销恢复文档时，
                // 抓上方那张时的纯显示补偿也照原样还回去。没改动就没有历史，也就不需要快照。
                HistoryDisplayState? display = mutated ? NewDisplaySnapshot() : null;
                if (mutated)
                    _project.Mutate("滑动图片", _ => applied = seam.SlidePicture(grabbed, allowed.Y), display);
                // ═══ 补偿留到提交之后（第十三轮需求一）═══
                // 抓上方那张时这一趟在屏上"移动的是图1"，那个观感必须<b>提交之后还在</b>：
                // 项目布局里图2 已经按新重叠挪好了，视口就按同一个量补过去，屏幕位置与预览最后一帧一致。
                // 净位移为 0 时补偿自然算出 0 ⇒ 视口回到按下那一刻，不留偏移。
                ApplyFixedSideAnchor(seam, OppositeOfSide(grabbed),
                    ScreenAnchorRow(_project.Layout, seam), "seam.alignment.fixed_side");
                // 补偿做完之后才能回填"这一趟在屏上留下的结果"（撤销/重做读的就是这两个数）。
                CompleteDisplaySnapshot(display, seam);

                if (_trace is not null)
                {
                    _trace.OverlapLength = seam.OverlapLength;
                    _trace.AlignmentOffsetAfter = seam.AlignmentOffset;
                    _trace.EffectiveCut = seam.CutPosition;
                    _trace.RequestedCut = wantCut;
                    _trace.RejectedCut = rejected;
                    _trace.EffectiveOffset = EffectiveOffset(seam);
                    _trace.SourceDelta = allowed;
                    _trace.RejectedX = _dragDelta.X - allowed.X;
                    _trace.ViewportAfter = Viewport.TopLeft;
                    _trace.CanvasHeightAfter = _project.Layout.CanvasSize.Height;
                    _trace.Mutated = mutated;
                    _trace.UndoPushed = mutated && _project.CanUndo && _project.NextUndoLabel != undoLabelBefore;
                    // 松手这一帧的矩形要取<b>提交之后</b>的项目布局：日志里这对矩形就是成品用的裁切矩形，
                    // 拿预览那一帧的数会写成"预览用的矩形"，事后核对成品就对不上。
                    FillCropGeometry(_trace, _project.Layout, seam);
                    FillLineAnchor(_trace, _project.Layout, seam);
                }
                DiagnosticLog.Info("seam.slide.commit", (_trace?.Fields()
                        ?? $"seam={seam.Id} crop_mode=true align_mode=true mutated={mutated} undoPushed=false") +
                    $" undo_before={undoBefore}(\"{undoLabelBefore}\") grabbed={grabbed} " +
                    $"x_locked={StitchAxes.LocksHorizontal(_project.Doc.Mode)}");
                _trace = null;
                // 滑到头必须出声，否则用户看到的是"图拖不动了"却不知道是到头了还是卡住了
                if (applied.RejectedRows != 0) CutClamped?.Invoke(applied.RejectedRows);
                Invalidate();
                break;
            }
            case DragMode.MoveSeam when _dragStarted:
            {
                Seam? seam = SelectedSeam();
                EndPreview();
                if (seam is null) { Invalidate(); break; }

                IntPoint wanted = _dragBaseOffset + ApplyFixedSide(seam, _dragDelta);
                IntPoint final = ConstrainDrag(seam, wanted);
                ReportHorizontalLock(seam, wanted, final, "拖动提交");

                // 拖回原位不留历史：空历史会让用户下一次 Ctrl+Z 退掉一个他根本没做的改动。
                // 同时把固定后一段的视口补偿收回来（补偿量按起点算，Δ=0 时本就等于起点）。
                bool mutated = final != EffectiveOffset(seam);
                bool undoBefore = _project.CanUndo;
                string? undoLabelBefore = _project.NextUndoLabel;
                // 位移档（竖拼/横拼）同裁切档一样：固定后一段靠视口补偿，撤销时得把那一屏还回去。
                HistoryDisplayState? display = mutated ? NewDisplaySnapshot() : null;
                if (mutated)
                    _project.Mutate("拖动拼缝", _ => seam.ApplyManual(final, _project.Doc.Mode), display);
                if (seam.Fixed == FixedSide.After)
                    Viewport.MoveTopLeftTo(_dragViewportBase + (final - _dragBaseOffset));
                CompleteDisplaySnapshot(display, seam);

                if (_trace is not null)
                {
                    _trace.RequestedOffset = wanted;
                    _trace.EffectiveOffset = final;
                    _trace.SourceDelta = final - _trace.BeforeOffset;
                    _trace.ViewportAfter = Viewport.TopLeft;
                    _trace.RejectedX = wanted.X - final.X;
                    _trace.Mutated = mutated;
                    _trace.UndoPushed = mutated && _project.CanUndo && _project.NextUndoLabel != undoLabelBefore;
                }
                DiagnosticLog.Info("seam.edit.commit", (_trace?.Fields()
                        ?? $"seam={seam.Id} crop_mode=false mutated={mutated} undoPushed=false") +
                    $" undo_before={undoBefore}(\"{undoLabelBefore}\") x_locked={StitchAxes.LocksHorizontal(_project.Doc.Mode)}");
                _trace = null;
                // Mutate 会触发 OnProjectChanged，瓦片按新 revision 重画；
                // 没有改动时也得把拖动预览的那一帧抹掉。
                Invalidate();
                break;
            }
            case DragMode.MoveSeam:
            case DragMode.SlidePicture:
            {
                EndPreview();
                // 进了编辑但没改变生效值（例如长截图里纯左右拖）：撤销预览、收回补偿。
                // 抓上方那张的滑动预览会挪视口（纯显示补偿），这一下既然没落成改动，
                // 屏上就不该留下那几行偏移。位移档本来就这么收；
                // 抓下方那张与固定前一段从没挪过视口，这句对它们是空操作。
                Viewport.MoveTopLeftTo(_dragViewportBase);
                LogCancel("拖动未改变生效值");
                Invalidate();
                break;
            }
        }
        _dragDelta = IntPoint.Zero;
        _dragStarted = false;
    }

    /// <summary>本次编辑没有落成参数改动时记一条 cancel，说明到底是不是一下都没动。</summary>
    private void LogCancel(string reason)
    {
        if (_trace is null) return;
        _trace.ViewportAfter = Viewport.TopLeft;
        // 显式写死而不是依赖默认值：cancel 事件的语义就是"这一下没进参数"，
        // 万一将来某条路径在预览里就置了 Mutated，这里也不能让它把 cancel 写成 mutated=true。
        _trace.Mutated = false;
        _trace.UndoPushed = false;
        DiagnosticLog.Info($"{_trace.EventPrefix}.cancel", _trace.Fields() + $" reason={reason}");
        _trace = null;
    }

    /// <summary>
    /// 把这一次手势"当时真正生效的那一组公共坐标量"抄进流水账：
    /// Rect1 / Rect2 / OverlapRect / sourceRect1 / sourceRect2 各是什么。
    /// 需求十一把这五个列为每条 seam.cut.* / seam.alignment.* 都要带的字段——
    /// 只有裁切量而没有矩形，事后分不清"切到的位置"与"被布局夹小之后的位置"。
    /// 传进来的布局决定了数值：预览帧传预览布局，提交传项目布局。
    ///
    /// <b>第十三轮（需求二/七）再往下走一步</b>：公共坐标之外，还要把这两块矩形<b>换算到屏幕</b>
    /// （过当前视口与缩放），并记下蓝线在带里的相对位置。
    /// 固定侧对不对、"我拖的时候图1 有没有偷偷跟着跑"，只有设备坐标那一列判得出来；
    /// 而"蓝线在带里的相对位置保持没保持"，带的绝对行号会变（需求四），能比较的只有比例。
    /// </summary>
    private void FillCropGeometry(SeamEditTrace trace, CanvasLayout layout, Seam seam)
    {
        SeamGeometry? geometry = layout.DescribeSeam(seam);
        if (geometry is null) return;
        trace.SourceRect1 = geometry.BeforeSourceRect;
        trace.SourceRect2 = geometry.AfterSourceRect;
        trace.Rect1 = geometry.Rect1;
        trace.Rect2 = geometry.Rect2;
        trace.OverlapRect = geometry.OverlapRect;
        trace.OverlapLength = geometry.ReportedOverlap;
        // 画布高要跟着<b>当时那张布局</b>一起记：预览帧记预览布局的高，提交记项目布局的高。
        // 少了这一笔，height_changed 就只剩"按下那一刻的两个数相比"，
        // 等于永远自证"没变"——而需求十一要的正反对照是"这一帧的高与起点是否一致"。
        trace.CanvasHeightAfter = layout.CanvasSize.Height;
        // 设备坐标：用<b>此刻的视口</b>换算，所以界面偷偷补偿视口时，这一列会跟着变——
        // 这正是需求一/二要能被发现的那个形状（补偿本身合法，无补偿却让固定侧漂了才不合法）。
        trace.Rect1DeviceAfter = Viewport.CanvasToDevice(geometry.Rect1);
        trace.Rect2DeviceAfter = Viewport.CanvasToDevice(geometry.Rect2);
        // 生效比例读<b>落位</b>那一侧（几何账），不读参数：预览期参数还是旧值。
        trace.CutRatioAfter = geometry.Overlap <= 0 ? 0 : (double)geometry.CutPosition / geometry.Overlap;
    }

    /// <summary>
    /// 把"蓝线这一帧落在哪一行"记进流水账：画布行号与<b>设备</b>行号各一份。
    ///
    /// 设备那一份才是判据要读的数：滑动手势全程它必须一字不动（线不动、图动）。
    /// 只记画布行号不够——抓上方那张时画布行号本来就会变，屏上不动是靠视口补偿做到的，
    /// 而补偿有没有真的做，只有"过了视口换算之后的那个数"能回答。
    /// 主轴取法与 <see cref="SeamLineDevice"/> 一致：横向模式取 X，其余取 Y。
    /// </summary>
    private void FillLineAnchor(SeamEditTrace trace, CanvasLayout layout, Seam seam)
    {
        if (layout.DescribeSeam(seam) is not SeamGeometry geo) return;
        bool horizontal = layout.Mode == StitchMode.Horizontal;
        IntRect lineRow = horizontal
            ? new IntRect(geo.CutYCanvas, 0, 1, 1)
            : new IntRect(0, geo.CutYCanvas, 1, 1);
        Rectangle device = Viewport.CanvasToDevice(lineRow);
        trace.LineCanvasRowAfter = geo.CutYCanvas;
        trace.LineDeviceAfter = horizontal ? device.X : device.Y;
    }

    /// <summary>
    /// 按下那一刻把"起点形状"钉进流水账：先按当前布局与视口填一遍"生效值"，
    /// 再原样抄成 Before。于是未拖动的cancel 读起来 before==after，而真拖过的每一列都有对照。
    /// </summary>
    private void CaptureGestureBaseline(SeamEditTrace trace, CanvasLayout layout, Seam seam)
    {
        FillCropGeometry(trace, layout, seam);
        FillLineAnchor(trace, layout, seam);
        trace.OverlapRectBefore = trace.OverlapRect;
        trace.Rect1DeviceBefore = trace.Rect1DeviceAfter;
        trace.Rect2DeviceBefore = trace.Rect2DeviceAfter;
        trace.CutRatioBefore = trace.CutRatioAfter;
        trace.LineCanvasRowBefore = trace.LineCanvasRowAfter;
        trace.LineDeviceBefore = trace.LineDeviceAfter;
    }

    /// <summary>
    /// 按下时开一本流水账，同时记 <c>seam.edit.begin</c>（位移档）或 <c>seam.slide.begin</c>（裁切档），
    /// 并补一条 <c>seam.geometry</c>。两档共用同一个 trace 与同一批字段名，方便 grep，
    /// 也保证界面与日志读的是同一份数。
    /// </summary>
    private SeamEditTrace StartSeamTrace(Seam seam, SeamHit hit, Point device)
    {
        // 长截图那一档只有一种手势：滑动上方或下方那张图（蓝线是参照物，不再单独可拖）。
        bool slide = ActiveLayout.UsesCropPlacement;
        var trace = new SeamEditTrace
        {
            SeamId = seam.Id,
            Hit = hit.Kind,
            GestureSide = hit.Side,
            // 裁切档里"屏上钉住哪一张"是这一趟手势的<b>结论</b>（抓哪一张就钉住另一张），
            // 不是按下之前就摆在面板下拉框里的前提——那一档已经作废，所以这里报结论而不是
            // 照抄 seam.Fixed 那个上一条手势留下的旧值（连着两趟反向拖动时，旧值会把这一趟记反）。
            // 位移档没有"抓哪一张"这件事，仍然照抄参数。
            FixedSide = slide ? OppositeOfSide(_slideGrabbed) : seam.Fixed,
            Grabbed = _slideGrabbed,
            DragMode = "PendingSeamClick",
            BeforeOffset = EffectiveOffset(seam),
            ViewportBefore = Viewport.TopLeft,
            ViewportAfter = Viewport.TopLeft,
            Zoom = Viewport.Zoom,
            RevisionBefore = _project.Revision,
            // 从按下这一刻就把"对齐多少、蓝线在第几行、画布多高"记下来：
            // commit 时要能报出"蓝线从第 12 行挪到第 20 行、对齐与总高仍是 60 / 340"，
            // 而不是让人只能看到两个裁切数却不知道它们合起来该等于几。
            CropMode = ActiveLayout.UsesCropPlacement,
            AlignMode = slide,
            OverlapLength = seam.OverlapLength,
            CutBefore = seam.CutPosition,
            AlignmentOffsetBefore = seam.AlignmentOffset,
            AlignmentOffsetAfter = seam.AlignmentOffset,
            CanvasHeightBefore = ActiveLayout.CanvasSize.Height,
        };
        trace.EffectiveOffset = trace.BeforeOffset;
        trace.RequestedOffset = trace.BeforeOffset;
        trace.EffectiveCut = seam.CutPosition;
        trace.RequestedCut = seam.CutPosition;
        trace.CanvasHeightAfter = ActiveLayout.CanvasSize.Height;
        // begin 这一条也要带那三对矩形：一次拖动的起点形状只有这里记得住，
        // 少了它，事后只剩"蓝线从 0 挪到 20"，看不出起点那两刀裁在哪个坐标上。
        // 第十三轮起还要带<b>屏幕</b>那一对（Rect1/Rect2 设备坐标）与蓝线比例——
        // 固定侧的判据要比的是"拖动前后图1 在屏上动没动"，起点没有读数就无从比起。
        if (trace.CropMode) CaptureGestureBaseline(trace, ActiveLayout, seam);
        LastSeamEditTraceForTest = trace;
        DiagnosticLog.Info($"{trace.EventPrefix}.begin", trace.Fields() + $" device={device}");
        // 需求十一点名的 seam.geometry：这条缝此刻的公共坐标全貌，与手势无关，单独成行才好用
        // 布局自己的那一行去比（那一行是"算出来的事实"，这一行是"手势里记下的事实"）。
        if (ActiveLayout.DescribeSeam(seam) is SeamGeometry geo)
            DiagnosticLog.Info("seam.geometry", geo.ToString() + $" gesture={trace.EventPrefix}");
        return trace;
    }

    // ═══ 滚轮途中走便宜插值，停手后自己补一帧高质量（需求三"停手那一帧同质量"的滚轮版本）═══
    // 窗口取 120ms：真人滚轮的格子间隔远小于它（一次滚动连打十几格），而停手后最多再补两三帧就收敛。
    private const long WheelCountsAsMovingMs = 120;
    private long _lastWheelTicks;
    private System.Windows.Forms.Timer? _settleTimer;

    /// <summary>本帧实际用的重采样档（<c>Nearest</c>／<c>Cheap</c>／<c>HighQuality</c>），给判据读。</summary>
    public string LastFrameInterpolationForTest { get; private set; } = "";

    /// <summary>真正发生过的"停手补帧"次数（计时器响过几次）。撤掉补帧机制它就是 0，判据当场红。</summary>
    public long SettlePaintsForTest { get; private set; }

    /// <summary>补帧计时器此刻是否还挂着（滚动中途应当一直是 true，停手 120ms 后变 false）。</summary>
    public bool SettleTimerArmedForTest => _settleTimer?.Enabled == true;

    /// <summary>
    /// 每次滚轮都把补帧计时器<b>重启</b>一次：于是"最后一次滚轮之后 120ms"才补那一帧高质量，
    /// 连续滚动期间一次都不补（一次手势只补一帧）。
    /// 用 <c>System.Windows.Forms.Timer</c> 而不是 <c>BeginInvoke</c> 自续排：后者在窗口内每帧都能插一帧，
    /// 等于把停手那笔钱在滚动中途提前付掉（实测池 P95 反而从 27.3 涨到 30.5ms）。
    /// </summary>
    private void RestartSettleTimer()
    {
        if (_settleTimer is null)
        {
            _settleTimer = new System.Windows.Forms.Timer { Interval = (int)WheelCountsAsMovingMs };
            _settleTimer.Tick += (_, _) =>
            {
                _settleTimer!.Stop();           // 一次性：要再补就等下一次滚轮
                SettlePaintsForTest++;
                Invalidate();                   // 此刻距最后一次滚轮已过窗口 ⇒ 那一帧走高质量档
            };
        }
        _settleTimer.Stop();
        _settleTimer.Start();
    }

    private Seam SeamById(string seamId) =>
        _project.Doc.Seams.FirstOrDefault(s => s.Id == seamId)
        ?? throw new InvalidOperationException($"拼缝 {seamId} 已不在项目里（命中测试与拖动之间被改过）");

    private void OnMouseWheel(object? sender, MouseEventArgs e) =>
        ApplyWheel(e.Delta, ModifierKeys, e.Location);

    /// <summary>
    /// 滚轮。<see cref="OnMouseWheel"/> 只做转发，公开出来让电池能量
    /// "Ctrl+滚轮只改 zoom 与 TopLeft、普通滚轮只改视口，两者都不碰参数"。
    /// </summary>
    public void ApplyWheel(int delta, Keys modifiers, Point device)
    {
        IntPoint beforeTopLeft = Viewport.TopLeft;
        double beforeZoom = Viewport.Zoom;
        int revision = _project.Revision;

        // 滚轮的两档（平移与 Ctrl+缩放）都算"正在动"：之后 120ms 内的帧走便宜档，停手后自己补一帧高质量。
        // 缩放这一档也纳进来是因为开着读数层量到的：缩放通道途中帧 35.5ms，把 P95 顶过 33ms 限值。
        _lastWheelTicks = Environment.TickCount64;
        RestartSettleTimer();

        if ((modifiers & Keys.Control) != 0)
        {
            Viewport.ZoomAt(beforeZoom * (delta > 0 ? 1.15 : 1 / 1.15), device);
            DiagnosticLog.Info("canvas.zoom",
                $"anchor={device} zoom_before={beforeZoom:F4} zoom_after={Viewport.Zoom:F4} " +
                $"viewport_before={beforeTopLeft} viewport_after={Viewport.TopLeft} " +
                $"revision={revision}→{_project.Revision}");
        }
        else
        {
            _panStartTopLeft = beforeTopLeft;
            _panStartRevision = revision;
            DiagnosticLog.Info("canvas.pan.begin",
                $"source=滚轮 revision={revision} viewport={beforeTopLeft} zoom={Viewport.Zoom:F4} device={device}");
            // 符号必须和方向键那一路一致（见 PanByArrow）：往上滚一格 == 按 ↑ == 往回看。
            // 之前这里多写了一个负号，于是"往下滚"把视口推向顶部——用户报的就是这个：
            // 滚轮／触控板双指向下，画面反而往上走。判据见 <c>canvas/滚轮与方向键必须同侧：向上滚就是往回看</c>，
            // 它的幅度反证在 <c>guard/滚轮方向两档各挪多少格必须说得出数</c>。
            // 触控板双指滚动在 Windows 上也是同一条 WM_MOUSEWHEEL（向下为负 delta），所以这一处改完两路一起正。
            int deviceStep = Math.Sign(delta) * WheelStepPixels(delta);
            Viewport.PanByDevice(new IntPoint(0, deviceStep));
            DiagnosticLog.Info("canvas.pan.end",
                $"device_delta=(0,{deviceStep}) " +
                $"viewport_before={_panStartTopLeft} viewport_after={Viewport.TopLeft} " +
                $"zoom={Viewport.Zoom:F4} revision={revision}→{_project.Revision} revision_changed={revision != _project.Revision}");
            Diagnostics.MemorySnapshot.Log("scroll");
        }
        Invalidate();
    }

    /// <summary>一次滚轮格子的屏幕距离。需求 §11 把它列为待定值，这里给一个可感知的默认。</summary>
    private static int WheelStepPixels(int delta) => Math.Clamp(Math.Abs(delta) / 120 * 60, 40, 240);

    /// <summary>
    /// 拼缝线在设备像素里的落点。<b>绘制与命中测试必须共用这一个换算</b>——
    /// 之前命中测试自己重算坐标（漏减 <see cref="ViewportState.TopLeft"/>），
    /// 画布一平移蓝线的视觉位置和可点位置就错开。
    /// </summary>
    private Rectangle SeamLineDevice(CanvasLayout layout, PlacedSegment placed, bool horizontal)
    {
        IntRect content = new IntRect(layout.CanvasBounds.X, layout.CanvasBounds.Y,
            layout.CanvasSize.Width, layout.CanvasSize.Height);
        return horizontal
            ? Viewport.CanvasToDevice(new IntRect(placed.Origin.X, content.Y, 1, content.Height))
            : Viewport.CanvasToDevice(new IntRect(content.X, placed.Origin.Y, content.Width, 1));
    }

    /// <summary>命中测试：返回鼠标下的拼缝 Id。横向模式匹配竖线，纵向匹配横线。</summary>
    public string? HitTestSeam(Point device)
    {
        CanvasLayout layout = ActiveLayout;
        bool horizontal = _project.Doc.Mode == StitchMode.Horizontal;

        for (int i = layout.Placed.Count - 1; i >= 1; i--)
        {
            PlacedSegment placed = layout.Placed[i];
            Seam? seam = placed.IncomingSeam;
            if (seam is null) continue;

            Rectangle line = SeamLineDevice(layout, placed, horizontal);
            // 横拼：拼缝是一条竖线，比 X；竖排/长截图：一条横线，比 Y
            int distance = horizontal ? Math.Abs(device.X - line.X) : Math.Abs(device.Y - line.Y);
            if (distance <= SeamHitTolerancePx) return seam.Id;
        }
        return null;
    }

    /// <summary>命中的是哪一块可编辑区域（写日志、光标、手势分档与悬停提示都要用它）。</summary>
    public enum SeamHitKind
    {
        /// <summary>
        /// 那条蓝色裁切线（含容差）。
        /// 第十四轮末次起它<b>不再是"拖蓝线改切点"那一档</b>：线是参照物，不许被拖走。
        /// 命中它 ⇒ 选中这条缝并进微调模式，同时把手势侧别认成<b>下方那张</b>
        /// （压着边界往下拖，直觉上拖的就是边界下面那一张）。
        /// </summary>
        CutLine,

        /// <summary>
        /// 蓝线正中那枚「调整」把手。命中它 ⇒ 开/关微调模式（不开拖动手势）。
        /// 它是给"带薄到几行、鼠标抓不住"的场合准备的入口：开了之后点上方或下方那张图选侧，
        /// 再按 ↑/↓ 每次走 1 个源像素。
        /// </summary>
        AdjustHandle,

        /// <summary>带边上的红色箭头把手（带首、带尾各一条）。命中它 ⇒ 滑动那一张图。</summary>
        AlignmentAdjust,

        /// <summary>
        /// 重叠带内部（<c>OverlapRect</c>，也就是屏幕上看成的"红框"）。
        /// 需求九：点击带里同样进入图片对齐调整，所以它与 <see cref="AlignmentAdjust"/> 走同一个手势，
        /// 分成两名的用途是让日志能说清"用户是照着箭头拖的，还是在带里随手一拖"。
        /// </summary>
        OverlapBand,
    }

    /// <summary>一次拼缝编辑区命中的结果。</summary>
    /// <param name="SeamId">命中的拼缝。</param>
    /// <param name="Kind">命中的是蓝线、红箭头还是重叠带——它直接决定这次手势改哪个量。</param>
    /// <param name="Side">
    /// 用户抓的是这条缝<b>上方</b>还是<b>下方</b>那张图：按蓝线分（线上方=Before，下方=After）。
    /// 它只用于提示与日志——<b>不擅自改写"固定侧"下拉框</b>，
    /// 免得用户看到"固定前一段"却发现手势在动另一张。
    /// </param>
    public readonly record struct SeamHit(string SeamId, SeamHitKind Kind, FixedSide Side)
    {
        public override string ToString() => $"{SeamId} {Kind} {Side}";
    }

    /// <summary>
    /// 命中测试（编辑区版）：这次按下/悬停落在哪条缝的可编辑区域上。
    ///
    /// ═══ 三档命中，各对应一个手势（需求九）═══
    /// ① 蓝线（含 <see cref="SeamHitTolerancePx"/> 容差）⇒ <see cref="SeamHitKind.CutLine"/>：只改 CutY；
    /// ② 带的上下两条边（红箭头把手）⇒ <see cref="SeamHitKind.AlignmentAdjust"/>：只改对齐；
    /// ③ 带内部其余处 ⇒ <see cref="SeamHitKind.OverlapBand"/>：按需求九也进对齐调整。
    /// 带的来源是<b>公共坐标里的 OverlapRect = Rect1 ∩ Rect2</b>（布局的 BandCanvasRect），
    /// <b>不是</b>裁切后可见矩形的交——裁切之后两段本来就不该相交，拿那个交去量红框会永远量不到。
    /// 带外一律返回 null ⇒ 按下走平移。<b>"当前选中了哪条缝"不参与这里的判断</b>，
    /// 那正是"选中之后画布推不动"这个缺陷的来源。
    /// </summary>
    public SeamHit? HitTestSeamEditRegion(Point devicePoint)
    {
        CanvasLayout layout = ActiveLayout;
        bool horizontal = layout.Mode == StitchMode.Horizontal;

        // 「调整」把手优先于线本身：它就骑在线上，不先判它的话点它永远只报成线命中，
        // 于是"点把手开微调"这件事在屏上按了也不算按到（与第四十改那颗 PerformClick 空转同族）。
        // 只判<b>选中那条缝</b>的把手——把手也只画在选中那条上（绘制与命中同源）。
        if (SelectedSeamId is not null)
        {
            PlacedSegment? selectedPlaced = layout.Placed.FirstOrDefault(p => p.IncomingSeam?.Id == SelectedSeamId);
            if (selectedPlaced is not null &&
                AdjustHandleRect(SeamLineDevice(layout, selectedPlaced, horizontal)).Contains(devicePoint))
                return new SeamHit(SelectedSeamId, SeamHitKind.AdjustHandle,
                    SideAcrossLine(SelectedSeamId, devicePoint, horizontal));
        }

        // 线优先：压在线上语义最明确，不该因为它的带也盖着这里就报成带命中
        string? onLine = HitTestSeam(devicePoint);
        if (onLine is not null)
            return new SeamHit(onLine, SeamHitKind.CutLine, SideAcrossLine(onLine, devicePoint, horizontal));

        foreach (PlacedSegment placed in BandScanOrder(layout))
        {
            Seam? seam = placed.IncomingSeam;
            PlacedSegment? before = seam is null ? null : layout.Find(seam.BeforeId);
            if (seam is null || before is null) continue;

            IntRect? bandRegion = layout.BandCanvasRect(seam);
            if (bandRegion is null || bandRegion.Value.IsEmpty) continue;
            Rectangle device = Viewport.CanvasToDevice(bandRegion.Value);
            if (!device.Contains(devicePoint)) continue;

            FixedSide side = SideAcrossLine(seam.Id, devicePoint, horizontal);
            // 把手贴在带的两条边上：按"设备像素"量，所以缩小到 10% 也还点得中（需求 §3.2）
            int at = horizontal ? devicePoint.X : devicePoint.Y;
            int bandStart = horizontal ? device.X : device.Y;
            int bandEnd = horizontal ? device.Right : device.Bottom;
            bool onHandle = at - bandStart <= AlignmentHandleDevicePx || bandEnd - at <= AlignmentHandleDevicePx;
            return new SeamHit(seam.Id,
                onHandle ? SeamHitKind.AlignmentAdjust : SeamHitKind.OverlapBand, side);
        }
        return null;
    }

    /// <summary>红箭头把手在<b>设备</b>像素里的可点厚度（带的上下两条边各这么宽）。</summary>
    private const int AlignmentHandleDevicePx = 10;

    /// <summary>
    /// 蓝线上那枚微调按钮的设备尺寸。
    /// 按<b>设备像素</b>定尺寸而不是按源像素：缩到 10% 时它也得还点得中（需求 §3.2 那条口径）。
    /// 绘制（<see cref="DrawSeamHandle"/>）与命中（<see cref="HitTestSeamEditRegion"/>）共用这一个函数，
    /// 免得又出现"看得见按不到"。
    /// <para>
    /// 宽度从 46 收到 24：里面不再写字（旧版写「调整」两个字），只剩一枚图形，
    /// 盒子跟着收窄才不会把蓝线盖掉一大截。纵向也从 20 收到 18，理由同上——
    /// 小缩放档下重叠带本身只有几十个设备像素高，按钮每占一行，用户就少一行可抓。
    /// </para>
    /// </summary>
    private const int AdjustHandleWidth = 24;
    private const int AdjustHandleHeight = 18;

    /// <summary>微调按钮在设备像素里的矩形：骑在蓝线正中，上下各出一半。
    /// 宽度不超过线本身的设备宽度：缩到 30% 时整条线只剩十几个像素，
    /// 一枚 46 像素的按钮会把线整个吞掉，那时候连"点在线上"都报不成线命中。</summary>
    public static Rectangle AdjustHandleRect(Rectangle seamLineDevice)
    {
        int width = Math.Min(AdjustHandleWidth, Math.Max(12, seamLineDevice.Width));
        return new Rectangle(
            seamLineDevice.X + seamLineDevice.Width / 2 - width / 2,
            seamLineDevice.Y + seamLineDevice.Height / 2 - AdjustHandleHeight / 2,
            width, AdjustHandleHeight);
    }

    /// <summary>
    /// 某条缝的「调整」把手在<b>设备像素</b>里的矩形（这条缝没落位就返回 <see cref="Rectangle.Empty"/>）。
    ///
    /// 公开只为一件事：判据要"点在把手上"与"点在线上但避开把手"两种点各来一次。
    /// 让判据自己按 46×20 这两个常量复述一遍矩形，等于把命中口径抄了一份到测试里——
    /// 产品改了尺寸，判据照旧绿，那正是"接线不对、只断言名字"那一族缺陷。
    /// </summary>
    public Rectangle AdjustHandleDeviceRect(string seamId)
    {
        PlacedSegment? placed = ActiveLayout.Placed.FirstOrDefault(p => p.IncomingSeam?.Id == seamId);
        if (placed is null) return Rectangle.Empty;
        return AdjustHandleRect(SeamLineDevice(ActiveLayout, placed,
            ActiveLayout.Mode == StitchMode.Horizontal));
    }

    /// <summary>线命中的上下侧：点在蓝线的上/左侧算"抓上面那张"（Before），下/右侧算"抓下面那张"（After）。</summary>
    private FixedSide SideAcrossLine(string seamId, Point devicePoint, bool horizontal)
    {
        // 注意 Find 吃的是**片段** Id，这里手上只有拼缝 Id，所以按 IncomingSeam 找那条缝的后一段
        PlacedSegment? placed = ActiveLayout.Placed.FirstOrDefault(p => p.IncomingSeam?.Id == seamId);
        if (placed is null) return FixedSide.Before;
        Rectangle line = SeamLineDevice(ActiveLayout, placed, horizontal);
        int at = horizontal ? devicePoint.X : devicePoint.Y;
        int lineAt = horizontal ? line.X : line.Y;
        return at < lineAt ? FixedSide.Before : FixedSide.After;
    }

    /// <summary>
    /// 重叠带的扫描顺序：当前选中那条<strong>先</strong>。
    /// 正在编辑一条缝时，在它自己的红框里按下去不该因为邻缝的带也盖到同一点就跳去编辑邻缝；
    /// 其余各条按从下往上的顺序（后画的盖在上面，视觉上更接近指针下的那块）。
    /// </summary>
    private IEnumerable<PlacedSegment> BandScanOrder(CanvasLayout layout)
    {
        for (int i = layout.Placed.Count - 1; i >= 1; i--)
            if (layout.Placed[i].IncomingSeam?.Id == SelectedSeamId) yield return layout.Placed[i];
        for (int i = layout.Placed.Count - 1; i >= 1; i--)
            if (layout.Placed[i].IncomingSeam is { } s && s.Id != SelectedSeamId) yield return layout.Placed[i];
    }

    private static double Distance(Point a, Point b) =>
        Math.Sqrt((a.X - b.X) * (double)(a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    // ───────────────────────── 键盘 ─────────────────────────

    protected override bool IsInputKey(Keys keyData) =>
        keyData is Keys.Up or Keys.Down or Keys.Left or Keys.Right
            ? true : base.IsInputKey(keyData);

    /// <summary>
    /// 浏览态的方向键属于视口（需求 §4 表格第一列）：默认按 1 个<b>屏幕像素</b>平移，
    /// 按住 Shift 走一整格（40 px）方便连续浏览；「视图 → 方向键浏览画布」可以整条关掉。
    /// 公开是为了让电池能直接量方向与"关掉后不动"这两件事。
    /// </summary>
    /// <returns>是否消费了这个按键（false 表示不是方向键或被关了）。</returns>
    public bool PanByArrow(Keys key, bool shift)
    {
        if (!BrowseArrowPanEnabled) return false;
        int fine = 1, coarse = 40;
        IntPoint pan = key switch
        {
            Keys.Up => new IntPoint(0, shift ? coarse : fine),
            Keys.Down => new IntPoint(0, shift ? -coarse : -fine),
            Keys.Left => new IntPoint(shift ? coarse : fine, 0),
            Keys.Right => new IntPoint(shift ? -coarse : -fine, 0),
            _ => IntPoint.Zero,
        };
        if (pan == IntPoint.Zero) return false;
        // 方向与箭头一致：按 ↓ 是"往下看"，内容相对视口往上走
        Viewport.PanByDevice(pan);
        return true;
    }

    /// <summary>
    /// Esc 这一档的全部逻辑，从 <see cref="OnKeyDown"/> 里抽出来给判据用。
    /// 抽出来的理由不是"方便测试"这一句空话：离屏工装没有真键盘消息，
    /// 而"一下 Esc 就退出编辑这条缝"这件事必须有能跑的判据兜着——
    /// 留在 OnKeyDown 里就等于判据与键盘各写一套，两处同值就是两份会各自漂移的真相。
    /// </summary>
    /// <returns>这一下 Esc 有没有被吃掉（没在编辑任何缝时返回 false，让上层照常处理别的键）。</returns>
    public bool TryHandleEscape()
    {
        // 临时模式开着时，Esc 先退模式，不动拼缝选择
        if (_splitPick) { CancelSplitPick(); return true; }
        if (!_adjustMode && SelectedSeamId is null) return false;
        // <b>一下</b>就退出"编辑这条缝"：退档 + 取消选中一起做。
        // 旧口径第一下只退档、选中的缝留着，于是用户要按两下才回到浏览态——
        // 他把"退出"当一件事，界面就不该替他拆成两下。
        // 蓝线上那枚 ✓ 与"再点一次蓝线"走的是同一个退出组合，三个出口一句话。
        SetAdjustMode(false);
        SelectSeam(null);
        return true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape)
        {
            e.Handled = TryHandleEscape();
            return;
        }

        if (SelectedSeamId is null)
        {
            if (PanByArrow(e.KeyCode, e.Shift)) e.Handled = true;
            return;
        }

        int step = e.Shift ? 10 : 1;          // 按住 Shift 加速，仍按源图像素计
        IntPoint delta = e.KeyCode switch
        {
            Keys.Up => new IntPoint(0, -step),
            Keys.Down => new IntPoint(0, step),
            Keys.Left => new IntPoint(-step, 0),
            Keys.Right => new IntPoint(step, 0),
            _ => IntPoint.Zero,
        };
        if (delta == IntPoint.Zero) return;
        // 每次按键都算一次独立动作：允许它各自回话一次
        // （鼠标拖动则是"一次手势只说一遍"，靠 OnMouseDown 里的那个复位）
        _lockNoticed = false;
        NudgeSelected(delta);
        e.Handled = true;
    }

    // ───────────────────────── 点选切分 ─────────────────────────

    /// <summary>进入点选切分模式：十字光标 + 悬停引导线，单击即报画布 Y。</summary>
    public void EnterSplitPick()
    {
        if (_project.Layout.IsEmpty) return;
        _splitPick = true;
        _splitHover = new Point(-1, -1);
        Cursor = Cursors.Cross;
        Invalidate();
    }

    public void CancelSplitPick()
    {
        if (!_splitPick) return;
        _splitPick = false;
        _splitHover = new Point(-1, -1);
        Cursor = SelectedSeamId is null ? Cursors.Default : Cursors.SizeAll;
        Invalidate();
    }

    /// <summary>
    /// 处理一次点选：设备点 →（减去平移、除以缩放）→ 画布 Y，报给主窗口后退出模式。
    /// 换算只用 <see cref="ViewportState.DeviceToCanvas"/>——与平移/缩放同一套坐标语言。
    /// 公开是为了让电池判据能直接量这条换算链。
    /// </summary>
    public void HandleSplitPickClick(Point devicePoint)
    {
        int canvasY = Viewport.DeviceToCanvas(devicePoint).Y;
        CancelSplitPick();
        SplitPicked?.Invoke(canvasY);
    }

    /// <summary>指针下（或指定 Id）的拼缝的悬停提示文字；不在拼缝上返回 null。</summary>
    /// <summary>
    /// 悬停提示文字（需求 §3.2）。线命中说"点击编辑"，重叠带命中说"拖动调整"并点明抓的是哪一侧——
    /// 两者都可点，但用户的意图不同，提示也就不该是同一句话。
    /// </summary>
    public string? DescribeSeamAt(SeamHit? hit)
    {
        if (hit is null) return null;
        int index = _project.Doc.Seams.FindIndex(s => s.Id == hit.Value.SeamId);
        if (index < 0) return null;
        string pair = $"拼缝 {index + 1}→{index + 2}";
        return hit.Value.Kind switch
        {
            SeamHitKind.AdjustHandle =>
                $"{pair}：点这枚小按钮进微调，再点要挪的那一张图，↑/↓ 每次 1 个源像素；再点一次＝完成并退出编辑",
            SeamHitKind.CutLine =>
                $"{pair}：蓝线是参照物、不会被拖走；压着它拖动＝挪下方那张图，单击进微调",
            _ =>
                $"{pair}：拖动{(hit.Value.Side == FixedSide.Before ? "上方" : "下方")}那张图" +
                "（蓝线钉在屏幕原地不动）",
        };
    }

    // ───────────────────────── 绘制 ─────────────────────────

    /// <summary>
    /// 把"绘制一帧"这条通路完整走一遍（取视口内瓦片 → 贴图 → 画线与叠层）。
    /// 判据用它量真实成本：另写一套取数逻辑的话，量到的就不是屏幕上那一次重画了。
    /// 控件没上屏时 WinForms 压根不发 WM_PAINT，所以电池必须有这一道入口。
    /// </summary>
    public void RenderFrameForTest(System.Drawing.Bitmap target)
    {
        ArgumentNullException.ThrowIfNull(target);
        using System.Drawing.Graphics g = System.Drawing.Graphics.FromImage(target);
        PaintOffscreen(() => OnPaint(new PaintEventArgs(g, new Rectangle(0, 0, target.Width, target.Height))));
    }

    /// <summary>
    /// 同上，但把 GDI 的裁剪区<b>限成调用方给的那一块</b>。
    /// 真窗口里滚动时系统发下来的更新区常常只是客户区的一部分，产品按
    /// <c>Graphics.VisibleClipBounds</c> 反算这一帧要贴哪几块瓦片；
    /// 整块裁剪的量法永远走不到那一条分支，所以这一道入口是给
    /// <c>canvas/局部重画那一趟也不许留底色行</c> 用的。
    /// </summary>
    public void RenderFrameForTest(System.Drawing.Bitmap target, Rectangle clip)
    {
        ArgumentNullException.ThrowIfNull(target);
        using System.Drawing.Graphics g = System.Drawing.Graphics.FromImage(target);
        g.SetClip(clip);
        PaintOffscreen(() => OnPaint(new PaintEventArgs(g, clip)));
    }

    /// <summary>
    /// 离屏取帧的记号：忙碌遮罩开工前要先拍一张画布现状去虚化，那条路走的是
    /// <see cref="RenderFrameForTest(System.Drawing.Bitmap)"/>——它同样过 <see cref="OnPaint"/>，但<b>不是上屏的那一帧</b>，
    /// 用户看不见它，也就不构成闪烁。闪烁判据（<c>ui/导入一整批画布一帧都不许提前画</c>）
    /// 只认 <see cref="ScreenPaintsForTest"/>，所以这两类帧必须分得开。
    /// </summary>
    private void PaintOffscreen(Action paint)
    {
        _offscreenPainting = true;
        try { paint(); }
        finally { _offscreenPainting = false; }
    }

    private bool _offscreenPainting;

    protected override void OnPaint(PaintEventArgs e)
    {
        // 帧测量（需求五）： stopwatch 的读数只用来算毫秒，不参与任何几何或像素决定。
        long startTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        // 第三十二改：这一帧的托管堆分配（字节）。需求五那批读数里"GC 次数"太粗——
        // gen0 一次能吞掉几万到几十万字节的差别，"每帧多造几支笔刷"这种事它根本看不见。
        // 取数窗口只罩住 OnPaint 自己：<c>Graphics.FromImage</c> 与 <c>new PaintEventArgs</c>
        // 都在 OnPaint 之外（工装路径），算进来就成了一口替工装背的账。
        long allocStart = GC.GetAllocatedBytesForCurrentThread();
        // GC 归因（同一趟里"某一帧突然 100ms+"最可疑的来源就是回收）：只比较代计数，
        // 不引入任何分配（拿 GC.GetTotalMemory 会自己触发一次回收，读数就成了假的）。
        int gcBefore = GC.CollectionCount(0);
        PaintCount++;
        // 上屏帧与离屏取帧分开记：遮罩开工前拍现状那几帧走 RenderFrameForTest，用户看不见，
        // 不算闪烁；判据 <c>ui/导入一整批画布一帧都不许提前画</c> 只读 ScreenPaintsForTest。
        if (!_offscreenPainting) ScreenPaintsForTest++;
        _hudMsThisFrame = 0;          // 关着读数层就是 0；开着由 PaintCore 里那一段计时填上
        // 读数层往下那几笔的出口是**进程累计**（第十改那批就这么读的），要按帧归因就得在这里记下基线，
        // 结算时作差——第十四改要分开的就是"最慢那帧里读数层那 33.5ms 是面板重建、描边，还是贴图"。
        _hudBaseAtFrameStart = HudCumulativeSnapshot();
        _paintTicks.Enqueue(Environment.TickCount64);
        long coreStart = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            PaintCore(e);
        }
        finally
        {
            // 帧总时长与 PaintCore 那一段<b>共用同一个终点时刻</b>取数：分开取两次时钟的话，
            // "帧前"那一笔里会掺进取时钟自己的开销，那一笔就成了自己造出来的数。
            long endTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            // 分配读数在<b>拼那行分阶段文本之前</b>取：那行文本本身要新建字符串，
            // 取晚了就是这一格自己造出来的数（同一个道理见上面 allocStart 那段）。
            LastFrameAllocBytesForTest = GC.GetAllocatedBytesForCurrentThread() - allocStart;
            FrameAllocBytesTotalForTest += LastFrameAllocBytesForTest;
            FrameAllocSamplesForTest++;
            double ms = ElapsedMs(startTicks, endTicks);
            string stages = TakeStageBreakdown(ms, ElapsedMs(coreStart, endTicks));
            LastStageBreakdown = stages;
            RecordFrame(ms, GC.CollectionCount(0) != gcBefore, stages, _hudMsThisFrame);
        }
    }

    /// <summary>两个时刻之间的毫秒数（时钟读数只用来算毫秒，不参与任何几何或像素决定）。</summary>
    private static double ElapsedMs(long fromTicks, long toTicks) =>
        (toTicks - fromTicks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

    /// <summary>本帧里 F8 读数层占掉的毫秒（PaintCore 末尾填，OnPaint 结算时取）。需求三那两档读数的来源。</summary>
    private double _hudMsThisFrame;
    public double LastHudMs => _hudMsThisFrame;

    /// <summary>最近一帧的分阶段耗时（取数/棋盘格/贴图）——需求六"卡顿发生在哪个阶段"就看这一行。</summary>
    public string LastStageBreakdown { get; private set; } = "";

    /// <summary>
    /// 最近一帧 <see cref="OnPaint"/> 自己造成的<b>托管堆</b>分配（字节）。
    /// 需求五那批读数里"GC 次数"太粗——一次 gen0 能吞掉几万字节，"每帧多造几支笔刷"这种事它看不见。
    /// 只数托管堆：位图那 1MB 在 GDI 非托管堆上，那笔账归贴图预算那两枚门管。
    /// 判据：<c>canvas/稳态帧每帧的堆分配要有上限且画布那一族也算</c>。
    /// </summary>
    public long LastFrameAllocBytesForTest { get; private set; }

    /// <summary>
    /// 最近一帧里<b>取数那一段</b>（每块瓦片的 <c>TileCache.Get</c>，含冷块真渲染）的托管堆分配（字节）。
    /// 判据：<c>canvas/每帧分配要按取数与转换两段各自交出</c>，
    /// 反证：<c>guard/每帧分配的分段读数只许跟着真取数真转换涨</c>。
    /// </summary>
    public long LastFrameAllocFetchBytesForTest { get; private set; }

    /// <summary>最近一帧里 <c>RgbaImage → GDI 位图</c> 那一段（<see cref="BitmapOfTile"/>）的托管堆分配（字节）。</summary>
    public long LastFrameAllocConvertBytesForTest { get; private set; }

    /// <summary>最近一帧里 F8 读数层那一段（<c>DrawDebugHud</c>）的托管堆分配（字节）。HUD 关着必须恒为 0。</summary>
    public long LastFrameAllocHudBytesForTest { get; private set; }

    /// <summary>
    /// 最近一帧里<b>对照层整段</b>（<c>DrawOverlapPreview</c>）的托管堆分配（字节）。
    /// 第二十六改那两笔数的是"拼对照这一步取回几行／几字节"（子步），这把尺量的是整段——两把尺不是一把，
    /// 所以本表要把"其余"那一档里属于对照层的份额单独交出来。
    /// 判据：<c>canvas/每帧分配要按取数与转换两段各自交出</c>（四段加起来不许超过整帧），
    /// 反证：<c>guard/每帧分配的分段读数只许跟着真取数真转换涨</c>（这格新加第三个独立出处：整层关掉）。
    /// </summary>
    public long LastFrameAllocOverlayPreviewBytesForTest { get; private set; }

    /// <summary>进程累计的每帧分配之和与样本数（真窗口拿它按帧归一，避免"整趟累计"被帧数带跑）。</summary>
    public long FrameAllocBytesTotalForTest { get; private set; }

    /// <summary>取到分配读数的帧数（与 <see cref="PaintCount"/> 同数量级；对不上就是有一帧没结算）。</summary>
    public long FrameAllocSamplesForTest { get; private set; }

    /// <summary><b>最慢那一帧</b>的分阶段耗时。与上一个读数的区别必须分开：把"最后一帧的拆分"
    /// 当成"最慢那帧的拆分"去读，会得出"最慢那帧只花了 30ms"这种与 127ms 对不上的结论。</summary>
    public string SlowestStageBreakdown { get; private set; } = "";

    private void PaintCore(PaintEventArgs e)
    {
        System.Drawing.Graphics g = e.Graphics;
        // 帧账的"首次解码"归因从这里起点开始量（只量这一条线程，见 FileSourceResolver 那段注释）。
        _decodeMsAtPaintStart = Imaging.FileSourceResolver.DecodeMsOnCurrentThread;
        _decodeRunsAtPaintStart = Imaging.FileSourceResolver.DecodeRunsOnCurrentThread;
        g.SmoothingMode = GdiSmoothing.None;
        // ═══ 缩小浏览时的插值档：拖动途中用双线性，停手那一帧立刻回到高质量双线性 ═══
        // 第十三轮 --gui 量到"贴图"这一段就是拖动帧的全部开销（GDI+ 软件渲染把 512×512
        // 重采样成 179×179，一屏十几块就是三十多毫秒）。拖动途中那几十毫秒的一帧里，
        // 用户看的是"哪张图在跟着手走"，不是"边缘的抗锯齿够不够好"；而停手后的那一帧
        // 才是他停下来审视的那一张，那一帧必须与今天完全同质量。
        // 这条只在<b>显示</b>这一层，导出与合成永远不走它（红线三：导出不从屏幕取图），
        // 成品字节不受任何影响——判据 <c>align/关掉调试层不改对齐、蓝线、导出字节与命中区</c>
        // 与整条导出核对（真机 --stitch 逐行比对）继续钉着这件事。
        // 滚轮与 Ctrl+滚轮缩放也算"正在动"（第十四轮补量滚轮那一档时量出来的）：
        // 原来只有拖拽三档算 moving，滚轮期间每帧都按"静止"给最贵的那档重采样——
        // 35% 下同一屏瓦片数差不多，拖动档贴图 11~15ms、滚轮档 16~27ms，差在这一档上。
        long sinceWheel = Environment.TickCount64 - _lastWheelTicks;
        bool wheelMoving = _lastWheelTicks != 0 && sinceWheel >= 0 && sinceWheel < WheelCountsAsMovingMs;
        bool moving = _drag is DragMode.MoveSeam or DragMode.SlidePicture or DragMode.PanCanvas || wheelMoving;
        LastFrameInterpolationForTest = Viewport.Zoom >= 1.0
            ? "Nearest" : moving ? "Cheap" : "HighQuality";
        g.InterpolationMode = LastFrameInterpolationForTest switch
        {
            "Nearest" => GdiInterpolation.NearestNeighbor,        // 放大看接缝：必须看到真像素
            "Cheap" => GdiInterpolation.Bilinear,                // 途中（拖拽或滚轮）：先跟上手
            _ => GdiInterpolation.HighQualityBilinear,           // 静止那一帧：平滑一点更可读
        };
        g.PixelOffsetMode = GdiPixelOffset.Half;
        // 停手那一帧回到高质量由 _settleTimer 负责（每次滚轮重启它，最后一次之后 120ms 补一帧）。
        // 这里刻意<b>不</b>在每帧里再排一帧：试过的写法是"窗口内每帧 BeginInvoke 续排"，
        // 结果是补那一帧的钱提前付了、还在连续滚动中间多插了几帧——
        // 35% 滚轮档池 P95 从 27.3ms 变成 30.5ms，25% 从 28.3 变成 32.0。
        // 一次手势只补一帧才是这条机制的正确形状（判据 canvas/滚轮途中走便宜插值、停手后只补一帧高质量）。

        CanvasLayout layout = ActiveLayout;
        // ═══ 叠层拆账（第十四轮第五改）═══
        // "叠层"那一笔在第四改里已经能加满账，但它仍然是一整笔：读到 5~57ms 时说不清是
        // 背景、重叠预览、拼缝线（含选中那条的对齐层）、补丁层、角标还是点选引导线里的哪一笔。
        // 更关键的是上一批的证据：一枚带回收标记的帧，回收的钱会落在"它发生时正在被计时的那一段"里
        // （三批分别顶在贴图 62.1／68.3 与叠层 56.7）——所以"叠层大"单独不能当成"叠层绘制慢"。
        // 把没计时的段逐段计时之后，两件事才分得开：常态下哪一段真的贵（看各段的均值），
        // 以及尖峰是不是散在一段上（散＝回收，集中＝那一段的代码）。
        // 计时点只夹住调用本身，画法与坐标换算一律没动（红线：显示层的读数不许改像素）。
        long tBackground = StageTimestamp();
        DrawBackground(g);
        // 空态那行提示与背景同一档（这一趟压根不画瓦片，取数／棋盘／贴图都是 0，
        // 归到"背景"这一档能让恒等式照样加得满）。
        _msOvBackground += ElapsedMs(tBackground, StageTimestamp());

        if (layout.IsEmpty)
        {
            long tEmpty = StageTimestamp();
            DrawEmptyState(g);
            _msOvBackground += ElapsedMs(tEmpty, StageTimestamp());
            return;
        }

        DrawTiles(g, layout);
        // 第三十四改：叠层那六段里只有对照层早有"这一步取回几行／几字节"（第二十六改），
        // 而那批字节数与第三十三改的字节表<b>不是同一把尺</b>（一把数取回的行缓冲，一把数整段分配）。
        // "其余 1.3MB／帧"要落地就得先知道整段对照层自己占多少——所以这里加第四只表，
        // 与那六段毫秒共用同一对边界（画多少这件事由第七/八改那两格像素判据管，本表不改画法）。
        long tPreview = StageTimestamp();
        long allocPreviewStart = AllocNow();
        DrawOverlapPreview(g, layout);
        AllocAdd(ref _allocOverlayPreview, allocPreviewStart);
        _msOvPreview += ElapsedMs(tPreview, StageTimestamp());
        long tSeams = StageTimestamp();
        DrawSeams(g, layout);       // 选中那条缝时，重影对齐层（描边＋半透明填充）也在这一段里
        _msOvSeams += ElapsedMs(tSeams, StageTimestamp());
        if (ShowPatchOverlay)
        {
            long tPatch = StageTimestamp();
            DrawPatchOverlay(g, layout);
            _msOvPatch += ElapsedMs(tPatch, StageTimestamp());
            _ovPatchCallsAccum++;              // 与上面那笔毫秒同时起同落：数的是"真调了几次"

        }
        long tBadge = StageTimestamp();
        DrawDragBadge(g);
        _msOvBadge += ElapsedMs(tBadge, StageTimestamp());
        long tPick = StageTimestamp();
        if (_splitPick && _splitHover.X >= 0) DrawSplitPick(g);
        _msOvPick += ElapsedMs(tPick, StageTimestamp());
        if (ShowDebugHud)
        {
            // HUD 单独计时（需求三）：这一段的耗时单独记一列，OnPaint 结算时并进帧样本。
            // 没有这一列就说不清"P95 超阈值"是读数层贡献的还是画布本身变慢了。
            long hudStart = StageTimestamp();
            long allocHudStart = AllocNow();
            DrawDebugHud(g, layout);
            AllocAdd(ref _allocHud, allocHudStart);
            _ovHudCallsAccum++;         // 读数层这一档同样是"真调了几次"，不是毫秒
            _hudMsThisFrame = (StageTimestamp() - hudStart) * 1000.0
                / System.Diagnostics.Stopwatch.Frequency;
        }
    }

    /// <summary>
    /// 画布上那两枚跟着鼠标走的字块（拖动角标 <see cref="DrawDragBadge"/>、点选切分引导线
    /// <see cref="DrawSplitPick"/>）<b>共用</b>的量法与画法：一次量出这行字在当前 DPI 下占多大，
    /// 框就是那块 + 12×6。之所以要抽出来共用：这两块以前各自写了一遍同样的三行，
    /// 而"量的 flags"与"画的 flags"是必须成对改的一件事——只改一处就成了量的人与画的人各说一套。
    /// 判据：<c>canvas/画布角标的框必须装得下它那行字</c>（真画一遍数框外的墨），
    /// 反证：<c>guard/角标的框故意做窄则墨一定变少</c>。
    /// </summary>
    public const TextFormatFlags BadgeFlags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter;

    /// <summary>角标那行字在<b>画它的那个 Graphics</b>（因而那个 DPI）下占多大一块。</summary>
    public static Size BadgeTextSize(System.Drawing.Graphics g, string text) =>
        TextRenderer.MeasureText(g, text, Theme.MonoFont);

    /// <summary>点选切分的引导线：一条跟随鼠标的虚线横 + 它对应的画布 Y。</summary>
    private void DrawSplitPick(System.Drawing.Graphics g)
    {
        using (var pen = new Pen(Theme.Accent, 1f)
        {
            DashStyle = System.Drawing.Drawing2D.DashStyle.Dash,
        })
            g.DrawLine(pen, 0, _splitHover.Y, Width, _splitHover.Y);

        IntPoint canvas = Viewport.DeviceToCanvas(_splitHover);
        string text = $"切分线 Y={canvas.Y}（源图）· 单击切分，Esc 取消";
        var size = BadgeTextSize(g, text);
        var box = new Rectangle(_splitHover.X + 14, _splitHover.Y - size.Height - 6,
            size.Width + 12, size.Height + 6);
        using var bg = new SolidBrush(WithAlpha(Theme.PanelBackground, 235));
        using var border = new Pen(Theme.Accent);
        g.FillRectangle(bg, box);
        g.DrawRectangle(border, box);
        TextRenderer.DrawText(g, text, Theme.MonoFont, box, Theme.TextPrimary, BadgeFlags);
    }

    /// <summary>
    /// 清理补丁高亮层：每个生效补丁画一圈琥珀色虚线框。
    /// 需求 §3.5 的原话是"不能直接提交隐藏修改"——补丁一旦应用就真的进了像素，
    /// 所以这里把它动过哪里全部圈出来，而不是只在面板里报个数。
    /// </summary>
    private void DrawPatchOverlay(System.Drawing.Graphics g, CanvasLayout layout)
    {
        foreach (PlacedSegment placed in layout.Placed)
            foreach (PixelPatch patch in placed.Segment.Patches)
            {
                if (!patch.Enabled) continue;
                // Target 相对片段有效区域左上角（见 PixelPatch 注释），换算到画布就是加有效区域原点
                IntRect canvasRect = patch.Target with
                {
                    X = placed.Origin.X + patch.Target.X,
                    Y = placed.Origin.Y + patch.Target.Y,
                };
                Rectangle device = Viewport.CanvasToDevice(canvasRect);
                if (device.Right < 0 || device.Bottom < 0 ||
                    device.X > Width || device.Y > Height) continue;
                g.DrawRectangle(PatchHighlightPen, device);
            }
    }

    /// <summary>
    /// 补丁高亮那支琥珀虚线笔。<b>第三十二改提到静态</b>：这一段每帧都跑（哪怕一个补丁都没生效，
    /// 也要先把笔造出来再进循环），而颜色与线宽与虚线型全是常量——需求三第 5 条那句
    /// "不要每个 OnPaint 都重新创建 Brush"点的就是它。判据：
    /// <c>canvas/稳态帧每帧的堆分配要有上限且画布那一族也算</c>。
    /// </summary>
    private static readonly Pen PatchHighlightPen = new(Theme.Warning, 1.5f)
    {
        DashStyle = System.Drawing.Drawing2D.DashStyle.Dash,
    };

    /// <summary>画布底色刷子（每帧一次填充，颜色常量 ⇒ 静态）。同上，需求三第 5 条。</summary>
    private static readonly System.Drawing.SolidBrush CanvasBackgroundBrush = new(Theme.CanvasBackground);

    private void DrawBackground(System.Drawing.Graphics g)
    {
        g.FillRectangle(CanvasBackgroundBrush, ClientRectangle);
    }

    /// <summary>
    /// 逐块画瓦片。缩放由 GDI+ 在贴图时完成，<b>瓦片内容永远是源分辨率的整数 blit</b>——
    /// 这是"预览与导出共享取数、只在呈现处分家"的落点。
    /// </summary>
    /// <summary>
    /// 判据开关：开着时 <see cref="DrawTiles"/> 把<b>这一帧真的贴到屏上的</b>每一块设备矩形记下来。
    /// 用户第四条报的是"滑快了出现灰色的线条"——那是"某个设备像素没有被任何一块瓦片盖住"的形状，
    /// 所以判据必须问产品自己贴了哪些块，而不是在工装里按取整规则再算一遍
    /// （复述出来的规则永远和自己一致，抓不到产品写错的那一次）。
    /// 产品路径一像素不改：关掉时只多一个 bool 判断。
    /// </summary>
    public bool CaptureTileRectsForTest { get; set; }

    /// <summary>本帧记下的那些矩形（只在 <see cref="CaptureTileRectsForTest"/> 开着时有内容）。</summary>
    public IReadOnlyList<Rectangle> LastFrameTileRectsForTest => _frameTileRects;

    private readonly List<Rectangle> _frameTileRects = new();

    private void DrawTiles(System.Drawing.Graphics g, CanvasLayout layout)
    {
        if (_projectRevisionSeen != _project.Revision)
        {
            _projectRevisionSeen = _project.Revision;
            _renderRevision++;
        }

        IntRect visible = Viewport.VisibleCanvas(TileRenderer.TileSize);
        // ═══ 第四十六改：瓦片遍历不许越出内容矩形 ═══
        // 缩到 10% 档时视口反算出来的"可见画布"有一万多画布像素宽，而内容只有一千多——
        // 内容之外的那些瓦片列取出来是<b>全透明</b>的块，于是不透明判定为假、整列铺棋盘格，
        // 用户看到的就是"缩到最小整屏花掉"（棋盘格本该只表示"这一块还没取到"或"内容本身透明"）。
        // 内容之外本来就该是画布底色（PaintCore 开头已经铺过），这里直接取交把它排除掉。
        visible = visible.Intersect(new IntRect(0, 0, layout.CanvasSize.Width, layout.CanvasSize.Height));
        if (visible.IsEmpty)
        {
            if (CaptureTileRectsForTest) _frameTileRects.Clear();
            TilesDeferredLastFrame = 0;
            return;
        }
        // ═══ 第十三轮需求五：只画系统真正要求重画的那一块 ═══
        // 预览那一帧只 Invalidate 了带区那个小矩形，但原来这里按<b>整个视口</b>遍历瓦片，
        // 于是"拖动只重画一小块"在派发层被抹平了：每帧仍要重采样十几块 512×512。
        // 按 GDI 给的裁剪区（设备像素）反算回画布矩形再取交，才是需求十第 7 条那句话的意思。
        // 这一步不改变任何一帧的最终画面：裁剪区之外 GDI 本来就不让画。
        Rectangle clip = ClipDeviceOf(g);
        if (!clip.IsEmpty) visible = visible.Intersect(VisibleClipInCanvas(clip));

        int x0 = TileRenderer.TileX(visible.X), x1 = TileRenderer.TileX(visible.Right);
        int y0 = TileRenderer.TileY(visible.Y), y1 = TileRenderer.TileY(visible.Bottom);

        // 判据开着才记：量"这一帧真的贴上去的那几块盖住了哪些设备像素"（用户第四条：滑快了出现灰色线条）。
        if (CaptureTileRectsForTest) _frameTileRects.Clear();

        int cols = x1 - x0 + 1, rows = y1 - y0 + 1;
        int total = cols * rows;
        double coldMs = 0;
        int deferred = 0;
        // ═══ 冷活＝这一帧第一次碰这块瓦片的那两笔：现取渲染 + 首次转成 GDI 位图 ═══
        // 预算只盯冷活，绝不把"每帧必做的贴图"算进去。上一版把贴图也计进预算，
        // 结果是<b>全命中的稳定帧每帧都"超预算"</b> ⇒ 每帧都 Invalidate
        // ⇒ Application.DoEvents() 永远排不空队列 ⇒ 工装整趟挂死（真挂过一次，七分钟没出来）。
        long rendersAtStart = _tiles.TileRenders;
        long convertsAtStart = TileBitmapConversions;
        for (int i = 0; i < total; i++)
        {
            int tx = x0 + i % cols, ty = y0 + i / cols;
            long rendersBeforeTile = _tiles.TileRenders;
            long convertsBeforeTile = TileBitmapConversions;
            long stageStart = StageTimestamp();
            long allocFetchStart = AllocNow();
            RgbaImage? tile = _tiles.Get(layout, tx, ty, _renderRevision);
            AllocAdd(ref _allocFetch, allocFetchStart);
            StageAdd(ref _msTileFetch, stageStart);
            if (_tiles.TileRenders > rendersBeforeTile) coldMs += StageElapsedMs(stageStart);
            if (tile is null) continue;

            var dest = Viewport.CanvasToDevice(new IntRect(
                tx * TileRenderer.TileSize, ty * TileRenderer.TileSize, tile.Width, tile.Height));

            // 透明区域要露出棋盘格，否则"补透明"看起来像丢了内容。
            // 【第三十一改】全不透明的那一块不必铺：它马上会被一张同样不透明的位图整块盖住。
            // 为什么以前每帧都铺：真实截图的瓦片 alpha 恒为 255，这一笔就是纯搬运——
            // 一块 512×512 要填 4096 个 8×8 方格，真窗口现量 棋盘 1.2~2.9ms／帧（需求五那一档里
            // 唯一"屏上看不出、但每帧都在做"的一笔）。判定与位图<b>同一条缓存记录</b>：
            // 每块只在冷的时候扫一次，命中缓存的帧一次都不扫
            // （判据 <c>canvas/棋盘格只铺在不透明判定为假的瓦片上</c>，
            // 反证 <c>guard/棋盘格判定不许把带透明的瓦片判成不透明</c>）。
            // <b>欠帧那一族照旧整块铺</b>（下面 break 前那一段）：那块压根还没有内容，
            // 棋盘格是"还没取到"的唯一标志，不是"透明"。
            // ═══ 第二十四改：转换与贴图各取一次时钟（原来这一行把两件事包在同一对时钟里）═══
            // 第十一改虽然把"冷块那一笔"单列成"首次转换"，但那一列拿的是<b>整笔 blitMs</b>——
            // 里面既有这块瓦片第一次 1MB 的 `RgbaImage → GDI Bitmap` 拷贝（一次性的，修法是该分摊到几帧），
            // 也有每帧必做的重采样（修法是该走便宜档）。两件混在一列里 ⇒ 冷块帧上那一列永远≈整段贴图，
            // "一块 512×512 的转换到底值几毫秒"因此<b>没有读数</b>；没有读数就没资格决定动哪一件。
            // 现在两半各一对时钟，整段的账（`blitMs`）与拆之前完全相同 ⇒ 六笔加满整帧那条恒等式一字不动。
            long convertStart = StageTimestamp();
            long allocConvertStart = AllocNow();
            GdiBitmap tileBitmap = BitmapOfTile(tile, out bool opaque);
            AllocAdd(ref _allocConvert, allocConvertStart);
            double convertMs = StageElapsedMs(convertStart);
            // 判定要读同一条缓存记录，所以拿位图这一步挪到铺格子<b>之前</b>。这一处换序不改一像素：
            // 转换只是拷内存、不往屏上画任何东西，而棋盘格仍然在同一块瓦片被盖住之前铺好。
            stageStart = StageTimestamp();
            if (!opaque) DrawChecker(g, dest, clip);
            StageAdd(ref _msChecker, stageStart);
            long drawStart = StageTimestamp();
            // 边缘钳制见 <see cref="TileEdgeClamp"/> 的注释：不传它，缩小档瓦片外缘会混进透明、
            // 瓦片交界与内容外缘就是一条灰线（判据 <c>guard/任意缩放档内容区不许出现混底灰带</c>）。
            g.DrawImage(tileBitmap, dest, 0, 0, tileBitmap.Width, tileBitmap.Height,
                System.Drawing.GraphicsUnit.Pixel, TileEdgeClamp);
            if (CaptureTileRectsForTest) _frameTileRects.Add(dest);
            double drawMs = StageElapsedMs(drawStart);
            double blitMs = convertMs + drawMs;
            _msBlit += blitMs;
            // 只有"这一块第一次上屏"的那一笔才算冷活（整块约 1MB 的拷贝）；
            // 命中缓存的贴图（GDI 重采样）推给下一帧也只是换个时间疼，不解决问题。
            if (TileBitmapConversions > convertsBeforeTile)
            {
                coldMs += blitMs;
                // 同一笔耗时只算一次（convertMs／drawMs 各是独立的一段，不重复取时钟、也不互相顶包）。
                _msBlitConvert += convertMs;
                BlitConvertTilesThisFrame++;
            }
            // 重采样那一列<b>每块都算</b>（冷块热块都要画到屏上），所以它的块数就是这一帧贴的块数。
            _msBlitDraw += drawMs;
            BlitDrawTilesThisFrame++;

            // ═══ 停手条件三条，缺一条都会出事 ═══
            // ① 预算开着（0＝不限流，电池拿它做 A/B 对照）；
            // ② 后面还有没画的块——最后一块没有"下一帧"可推，必须当场画完，否则屏上永久缺一角；
            // ③ 这一帧确实做过冷活。没做过就不许停：停了等于把同一件事推给下一帧白刷一遍。
            if (_tileBudgetMs <= 0 || i + 1 >= total || coldMs <= _tileBudgetMs) continue;
            if (_tiles.TileRenders == rendersAtStart && TileBitmapConversions == convertsAtStart) continue;
            // ═══ 第五十八改：欠帧这一刀只许落在行边界上——先把当前行画完，再从下一行起欠 ═══
            // 瓦片按行主序遍历，欠着的块是遍历的<b>后缀</b>，但这个后缀是 <b>L 形</b>：
            // 切断行还剩右半截，下面每一行都是整行宽。第五十四改③ 的续画区域取的是
            // 「下一块 ∪ 右下角块」的包围盒——它从切断行剩余部分的左沿起算，于是<b>下面各行
            // 左半截永远不在续画区域里</b>：那几块第一帧被画成棋盘格（看得清的档）或底色
            // （最小缩放档），之后的每一帧 GDI 只重画续画区域那一块矩形，左下角那块矩形
            // 就永远停在"还没取到"的样子，直到用户再滚一下／缩一下触发全量重画才消失——
            // 用户报的"左下角矩形透明底或纯黑，重新滑动或轻微缩放就消失"正是它。
            // （守卫当年没拦住：泵完之后先整帧重画再验像素，把屏面残留洗掉了——
            //   判据 <c>guard/欠帧续画只要没画的那一段且在有限帧内收敛</c> 已改成直接验屏面。）
            //
            // 为什么不把续画区域改成 L 形两刀：真窗口里两刀 Invalidate 会并成一个更新区，
            // 下一帧 GDI 发下来的 <c>VisibleClipBounds</c> 是它的<b>包围盒</b>——已画行又被罩回去，
            // 队头重新变冷（位图缓存挤掉的那批），预算被队头吃光，第五十四改③ 的闪烁就回来了。
            // 先把当前行画完（最多多花 cols-1 块冷活，上界有界；且只发生在本来就已超预算的帧上），
            // 欠着的后缀从下一行起变成<b>整行宽矩形</b>：一块续画区域罩住全部欠块，
            // 已画块一像素不碰，<b>有限帧内必收敛</b>。
            int rowEnd = Math.Min(total - 1, (i / cols + 1) * cols - 1);
            if (i != rowEnd) continue;   // 行还没画完：继续画到行尾再欠（最后一行的行尾就是 total-1，条件②已兜住）
            deferred = total - i - 1;
            // 内容设备矩形两处要用（棋盘格钳边、欠帧区域钳边），提出来只算一次。
            Rectangle contentDevice = Viewport.CanvasToDevice(
                new IntRect(0, 0, layout.CanvasSize.Width, layout.CanvasSize.Height));
            // 欠下的块这一帧先画成棋盘格——"还没取到"要看得见，不许留成画布底色让人以为内容丢了。
            // ═══ 第四十六改：但缩到"一屏装下整份文档"那一档不画这一墙 ═══
            // 瓦片网格在<b>画布</b>坐标里，缩到 10% 时一屏要取的块数＝屏上像素÷瓦片屏上尺寸，
            // 真项目（1179×171494）在 10% 档一屏六百多块，预算一限流就是<b>整墙棋盘格</b>——
            // 用户图4 报的"缩小到最小出现渲染异常"就是这一墙（整屏宽、半屏高）。
            // 而一块瓦片在屏上只有几十设备像素时，8 像素的棋盘方格本身就是噪声，
            // 欠帧只持续不到一秒：那一档留画布底色，比糊一整墙既好读也不骗人（块照样在后续帧补齐）。
            // 阈值取"一块瓦片在屏上至少 64 设备像素"（zoom ≥ 12.5%）：看得清的档位照旧画，
            // 判据 <c>guard/最小缩放档欠帧那一帧不许画棋盘格</c> 与
            // <c>guard/看得清的缩放档欠帧仍要画棋盘格且只画在内容里</c> 各钉一头。
            if (TileRenderer.TileSize * Viewport.Zoom >= DeferredCheckerMinTileDevicePx)
            {
                // 欠帧那几块的棋盘格按<b>整块瓦片</b>画，而边缘瓦片的整块矩形比内容宽——
                // 不取交的话棋盘格会铺到内容右边那一列去（内容外没有东西可取，铺出去就是
                // 用户图4 那一墙的窄版）。判据 <c>guard/看得清的缩放档欠帧仍要画棋盘格且只画在内容里</c>。
                for (int j = i + 1; j < total; j++)
                {
                    Rectangle r = Rectangle.Intersect(TileDeviceRect(x0 + j % cols, y0 + j / cols), contentDevice);
                    if (!r.IsEmpty) DrawChecker(g, r, clip);
                }
            }
            // ═══ 第五十四改③：欠帧续画只把"还没画的那一段"要回来 ═══
            // 原来 break 之后 Invalidate() 的是<b>整个控件</b>——下一帧又从第一块瓦片起全量重画。
            // 缩小档一屏上百块瓦片，而 GDI 位图缓存只留最后 <see cref="TileBitmapCap"/> 块：
            // 每帧全量重画时队头那些刚画过的块早已被挤出缓存，又变回冷活，12ms 预算全被它们
            // 吃掉，队伍前沿永远推不到头 ⇒ deferred 永远 &gt;0 ⇒ 每帧全量重画＋要帧无限循环——
            // 已画区与底色/棋盘格逐帧交替，这就是横拼缩小档"闪烁不停"（用户第五十四改第 3 条）。
            // ═══ 第五十八改：续画区域按"下一行起的整行宽矩形"算 ═══
            // 走到这里的 i 必是行尾（见上面 rowEnd 那段），欠着的后缀从下一行起是整行宽矩形，
            // 「下一行首块 ∪ 右下角块」的包围盒就是它本来的样子——已画块一像素不在里面。
            // （旧版在行中间就断刀、还用同一对首尾块算包围盒，左下角那块 L 形缺口就是这么来的。）
            // 判据 <c>guard/欠帧续画只要没画的那一段且在有限帧内收敛</c>：
            // 夹具特意把一屏块数撑过位图缓存容量，量"续画区域不含已画的首块＋按区域续画必收敛＋
            // 泵完的屏面逐像素纯色"。
            Rectangle tail = Rectangle.Intersect(
                Rectangle.Union(TileDeviceRect(x0, y0 + i / cols + 1), TileDeviceRect(x1, y1)),
                contentDevice);
            LastDeferredRegionForTest = tail;
            if (tail.IsEmpty) Invalidate();   // 算不出区域就退回全量要帧：宁可多画，也不许卡住不收敛
            else Invalidate(tail);
            break;
        }
        TilesDeferredLastFrame = deferred;
        if (deferred > 0)
        {
            DeferredRepaintRequests++;
            // 续画的要帧已经在 break 那里按"欠着的后缀区域"发过了（第五十四改③），
            // 这里只记账，不再补一刀全量 Invalidate。
        }
        else
        {
            LastDeferredRegionForTest = Rectangle.Empty;
        }
    }

    /// <summary>一整块瓦片在设备像素里的落点。欠帧那几块还没有内容，只能按整块大小画棋盘格。</summary>
    private Rectangle TileDeviceRect(int tx, int ty) =>
        Viewport.CanvasToDevice(new IntRect(tx * TileRenderer.TileSize, ty * TileRenderer.TileSize,
            TileRenderer.TileSize, TileRenderer.TileSize));

    /// <summary>
    /// 这一帧 GDI 真正允许重画的设备矩形；拿不到（裁剪区是无限大或空）时退回"没有裁剪限制"，
    /// 那样就照旧按整个视口画——宁可多画，也不许漏画。
    /// </summary>
    private static Rectangle ClipDeviceOf(System.Drawing.Graphics g)
    {
        RectangleF bounds = g.VisibleClipBounds;
        if (bounds.Width <= 0 || bounds.Height <= 0 ||
            float.IsNaN(bounds.Width) || float.IsInfinity(bounds.Width)) return Rectangle.Empty;
        return Rectangle.Ceiling(bounds);
    }

    /// <summary>设备裁剪区 → 画布坐标矩形（两端各取整后取交，绝不因为取整少一行）。</summary>
    private IntRect VisibleClipInCanvas(Rectangle clipDevice)
    {
        IntPoint a = Viewport.DeviceToCanvas(new Point(clipDevice.X, clipDevice.Y));
        IntPoint b = Viewport.DeviceToCanvas(new Point(clipDevice.Right, clipDevice.Bottom));
        return new IntRect(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y),
            Math.Abs(b.X - a.X) + 1, Math.Abs(b.Y - a.Y) + 1);
    }

    // ═══ 第十三轮需求五：贴图那一段才是真窗口的瓶颈（实测 42.9 / 41.3ms 一帧）═══
    // 每帧对每块可见瓦片做一次 <c>RgbaImage → GDI Bitmap</c> 的整块拷贝（512×512×4 ≈ 1 MB），
    // 一屏十几块就是十几 MB 的纯搬运——这跟"拖动卡不卡"直接相关，又不是取数或重排造成的。
    // 缓存键用的是 <b>那块 RgbaImage 实例自己</b>（RgbaImage 不重写 Equals，天然是引用相等）：
    // 于是瓦片被丢掉重画 ⇒ 新实例 ⇒ 自动不命中，<b>不可能</b>出现"屏上留着上一帧的像素"。
    // 修订号一变（含松手后的 EndPreview）整表作废并释放 GDI 句柄；表满同样清一次，上限可控。
    private readonly Dictionary<RgbaImage, TileEntry> _tileBitmaps = new();

    /// <summary>进程累计铺过几次棋盘格（一次＝一块瓦片那一整片方格）。</summary>
    public long CheckerCalls { get; private set; }

    /// <summary>进程累计填了多少个 8×8 方格——第三十一改要的就是这个数：真实截图的瓦片全不透明，它必须不再涨。</summary>
    public long CheckerSquares { get; private set; }

    /// <summary>这一帧填了几格（帧末快照，见 <see cref="LastFrameCheckerSquaresForTest"/>）。</summary>
    private int CheckerSquaresThisFrame;

    /// <summary>这一帧的棋盘格方格数读数。</summary>
    public int LastFrameCheckerSquaresForTest { get; private set; }

    /// <summary>不透明判定扫过几块瓦片（每块只该在冷的时候扫一次；命中缓存的帧一次都不该扫）。</summary>
    public long TileOpacityScans { get; private set; }

    /// <summary>
    /// 判据用：这块瓦片底下该不该铺棋盘格。走的是产品同一个入口（<see cref="BitmapOfTile"/> 里那条记录），
    /// 所以反证那一格喂进去的两块瓦片判的就是<b>产品真正用的那一条判定</b>，不是判据自己抄的一份。
    /// 传进来的瓦片因此会进那张缓存（一次转换、一条记录），随缓存一起作废——判据喂的是合成瓦片，
    /// 它们不与真实帧的瓦片同实例，不会串到屏上那一帧。
    /// </summary>
    public bool TileNeedsCheckerForTest(RgbaImage tile)
    {
        BitmapOfTile(tile, out bool opaque);
        return !opaque;
    }

    private int _tileBitmapRevision = -1;
    private long TileBitmapConversions;   // 累计转换次数（进程内只增；也是预算的冷活依据）
    private const int TileBitmapCap = 48;

    /// <summary>
    /// GDI 位图缓存的容量（判据用：把夹具的一屏块数撑到比它大，才量得到真窗口缩小档
    /// 「队头反复变冷」的那一形状——容量之内每一块都能留在缓存里，永远造不出那种卡法）。
    /// </summary>
    public int TileBitmapCapForTest => TileBitmapCap;

    /// <summary>
    /// 上一帧欠帧续画要回来的设备矩形（<see cref="Rectangle.Empty"/> ＝ 退回了全量要帧）。
    /// 真窗口里它就是发给系统的更新区，GDI 下一帧的裁剪区就是它——判据拿它把续画那一帧的
    /// 裁剪区限成同一块，量「续画只碰没画过的那段、有限帧内收敛」（第五十四改③）。
    /// </summary>
    public Rectangle LastDeferredRegionForTest { get; private set; }

    private GdiBitmap BitmapOfTile(RgbaImage tile, out bool opaque)
    {
        if (_tileBitmapRevision != _renderRevision)
        {
            DisposeTileBitmaps();
            _tileBitmapRevision = _renderRevision;
        }
        if (_tileBitmaps.TryGetValue(tile, out TileEntry cached))
        {
            opaque = cached.Opaque;
            return cached.Bitmap;
        }
        if (_tileBitmaps.Count >= TileBitmapCap) DisposeTileBitmaps();
        GdiBitmap bmp = Imaging.FileSourceResolver.ToBitmap(tile);
        // 判定与位图<b>同一条记录</b>。第三十一改第一版把判定单独记了一张表，判据当场抓到：
        // 修订号一变，<c>BitmapOfTile</c> 在<b>帧中途</b>把两张表一起清掉，于是刚扫过的那块
        // 判定被抹掉、而位图还在——下一帧冷块 0 却仍要扫一次。两张表两个寿命，就会有
        // "其中一张先死"的帧；合成一条记录之后，寿命问题压根不存在。
        TileOpacityScans++;
        opaque = tile.IsFullyOpaque();
        _tileBitmaps[tile] = new TileEntry(bmp, opaque, tile.Pixels.Length);
        TileBitmapBytesForTest += tile.Pixels.Length;
        TileBitmapConversions++;      // 冷活记账：见 TileBitmapConversionsForTest
        return bmp;
    }

    /// <summary>一块瓦片的上屏记录：GDI 位图，加上"要不要在它底下铺棋盘格"的判定（同进同退）。
    /// Bytes 是这块瓦片的像素字节数，内存快照按它折算 GDI 位图缓存的占用。</summary>
    private readonly record struct TileEntry(GdiBitmap Bitmap, bool Opaque, long Bytes);

    private void DisposeTileBitmaps()
    {
        foreach (TileEntry entry in _tileBitmaps.Values) entry.Bitmap.Dispose();
        _tileBitmaps.Clear();
        TileBitmapBytesForTest = 0;
    }

    /// <summary>位图缓存里现在有多少张 GDI 位图（调试层读数，也用来自证"这一帧真的复用了"）。</summary>
    public int TileBitmapCountForTest => _tileBitmaps.Count;

    // ── 第五十九改第一阶段的内存快照读数（只加读数，不改行为） ──

    /// <summary>显示瓦片缓存现在驻留几块（内存快照的 TileCacheCount 读数）。</summary>
    public int TileCacheCountForTest => _tiles.Count;

    /// <summary>显示瓦片缓存现在占多少字节（内存快照的 TileCacheBytes 读数，来自缓存自己的记账）。</summary>
    public long TileCacheBytesForTest => _tiles.Bytes;

    /// <summary>GDI 位图缓存现在占多少字节（每条记录记一次加、清表时归零）。</summary>
    public long TileBitmapBytesForTest { get; private set; }

    /// <summary>
    /// 判据用：位图缓存现在挂着哪些<b>瓦片</b>（与 <see cref="TileBitmapCountForTest"/> 同一批）。
    /// 第三十一改那一格要拿它<b>独立</b>数一次"这些瓦片到底全不全不透明"——
    /// 那样"这一帧棋盘格 0 格"才不是恒真：判据自己数出来的前提成立，产品跳过的才是该跳的。
    /// </summary>
    public IReadOnlyCollection<RgbaImage> CachedTilesForTest => _tileBitmaps.Keys;

    /// <summary>
    /// 进程累计做过多少次 <c>RgbaImage → GDI 位图</c> 转换（每块瓦片只有第一次会走）。
    /// 这是"冷活"的另一半：取数只渲染出内存像素，第一次上屏还要整块拷成 GDI 位图（约 1MB/块）。
    /// 预算必须把它算进冷活——只盯取数的第一版量出来是"追加要帧 0 次"，
    /// 因为滚轮稳定帧的取数只有 2ms，贵的是首次转换那一段。
    /// </summary>
    public long TileBitmapConversionsForTest => TileBitmapConversions;

    // ═══ 每帧瓦片预算（第十四轮：滚轮滚进没缓存的一屏，实测一帧 55.6ms）═══
    // 拖动档一直过「单帧 ≤ 50ms」，是因为它只在<b>已经缓存过</b>的带区里挪；滚轮二十格只画三帧，
    // 每帧要现取九块新瓦片（取数 30.5ms）再首次转成 GDI 位图（贴图 20.1ms）。
    // 那是<b>真在算</b>，不是被系统抢占，所以只能改实现：一帧里画瓦片累计超过预算就停手，
    // 剩下的块这一帧画成棋盘格，再立刻要一帧接着取。
    // 最终像素与不限流时逐字节相同——这条由判据
    // <c>canvas/瓦片限流连着画满的那一帧与不限流那一帧逐字节相同</c> 钉住，
    // 反证 <c>guard/瓦片预算写得比一整屏还大时不许出现欠帧</c> 保证它不是个恒真开关。
    private const double TileBudgetMsDefault = 12.0;
    private double _tileBudgetMs = TileBudgetMsDefault;
    private int TilesDeferredLastFrame;
    private long DeferredRepaintRequests;

    /// <summary>
    /// 每帧画瓦片的时间预算（毫秒）。<c>0</c> ＝ 不限流；给电池当 A/B 对照用，
    /// 也留出"这台机器画不动就关掉限流"的退路（默认开）。
    /// </summary>
    public double TileBudgetMsForTest { get => _tileBudgetMs; set => _tileBudgetMs = value; }

    /// <summary>上一帧因预算没画而欠下的瓦片块数（0 ＝ 这一帧画满了）。</summary>
    public int TilesDeferredLastFrameForTest => TilesDeferredLastFrame;

    /// <summary>因欠帧追加的要帧次数（进程累计）。真窗口里它就是"下一帧接着取"发生过的证据。</summary>
    public long DeferredRepaintRequestsForTest => DeferredRepaintRequests;

    // ═══ 需求六最后那句"画布卡顿发生在哪个阶段"：按阶段累计耗时 ═══
    // 只有"整帧 62ms"这一个数，改的人不知道该动瓦片渲染、棋盘格还是 blit。
    // 这三条累计器只读不写状态，读数直接进调试层与 --gui 工装。
    private double _msTileFetch;
    private double _msChecker;
    private double _msBlit;
    /// <summary>贴图那一笔里"这一块瓦片第一次拷成 GDI 位图"的那半笔（第十一改，子项，不进六笔恒等式）。</summary>
    private double _msBlitConvert;
    private int BlitConvertTilesThisFrame;
    // 第二十四改：贴图那一笔的另一半（每块都做的重采样）。它与上面两行加起来必须等于整段贴图，
    // 这条恒等式就是那枚新判据核对的东西。
    private double _msBlitDraw;
    private int BlitDrawTilesThisFrame;

    private static long StageTimestamp() => System.Diagnostics.Stopwatch.GetTimestamp();

    private static void StageAdd(ref double acc, long startTicks) => acc += StageElapsedMs(startTicks);

    // ═══ 第三十三改：把"每帧多少字节"往下拆到段 ═══
    // 第三十二改刚开的那枚整帧分配读数，在真窗口量到 <b>6.4~8.3 MB／帧</b>（同命令行里每帧重算 6~8 块瓦片），
    // 而电池那一档稳态帧只有 88 KB／帧——两个数差 70 多倍，可这一笔究竟落在哪一段，当时<b>没有读数</b>。
    // 只能是猜测的两个方向修法完全相反：
    // · 落在「取数」＝每帧冷渲染一块瓦片，新分配一个整块像素缓冲（该收窄作废范围／复用缓冲）；
    // · 落在「转换」＝RgbaImage→GDI 位图那一次拷贝（第十二改已把它改成按行走，那它就不该是 MB 级）。
    // 按老规矩（精度红线 §3＋"某一笔很大"≠"那段代码慢"那一族教训），<b>先给每一段各接一只字节表</b>，
    // 下一改才有权决定动谁。计时点与毫秒那几笔共用同一对边界，加起来不许超过整帧那一笔（判据钉这一条）。
    private static long AllocNow() => GC.GetAllocatedBytesForCurrentThread();

    private static void AllocAdd(ref long acc, long start) => acc += AllocNow() - start;

    private long _allocFetch;
    private long _allocConvert;
    private long _allocHud;
    // 第三十四改：叠层里对照层那一段（"其余"那一档目前唯一有独立出处的嫌疑人）。
    private long _allocOverlayPreview;

    /// <summary>从 <paramref name="startTicks"/> 到现在的毫秒数。预算那一笔与三段累计共用这一个算法。</summary>
    private static double StageElapsedMs(long startTicks) =>
        (System.Diagnostics.Stopwatch.GetTimestamp() - startTicks) * 1000.0
            / System.Diagnostics.Stopwatch.Frequency;

    /// <summary>
    /// 一帧分阶段的<b>数值</b>版（与 TakeStageBreakdown 那句人读的话同源）。
    /// 判据读这一份，不去解析文案——解析字符串的判据一旦改了措辞就自己变红。
    /// Overlay＝PaintCore 里没逐段计时的那些绘制本身，Prologue＝OnPaint 入口到 PaintCore 之前那一小截
    /// （双缓冲交换与 WM_PAINT 派发在 OnPaint <b>之外</b>，这一套帧样本天生量不到，别把它算进任何一笔）。
    /// </summary>
    public sealed record StageNumbers(double Fetch, double Checker, double Blit, double Hud,
                                      double Overlay, double Prologue, double Frame);

    /// <summary>最近结算的那一帧的分阶段数值（六笔加满一帧，含帧总时长）。</summary>
    public StageNumbers? LastStageNumbersForTest { get; private set; }

    /// <summary>
    /// 叠层那一笔再往下拆的六段（与 PaintCore 里那六个调用一一对应）＋残差。
    /// 残差＝叠层 −（六段）＝取布局那一次与两次调用之间的空隙；它必须小，
    /// 否则"拆账"是假拆（判据 <c>canvas/叠层那一笔要能再拆到底</c> 钉的就是这件事）。
    /// </summary>
    public sealed record OverlayNumbers(double Background, double Preview, double Seams, double Patch,
                                        double Badge, double Pick, double Residual, double Overlay);

    public OverlayNumbers? LastOverlayNumbersForTest { get; private set; }
    /// <summary>预览那一笔里的三段与本帧位图转换次数（第二十五改）。判据：
    /// <c>canvas/预览那一笔要拆成渲染、拼对照与比并贴三段</c>。</summary>
    public double OverlayPreviewRenderMsForTest { get; private set; }
    public double OverlayPreviewStitchMsForTest { get; private set; }
    public double OverlayPreviewBlitMsForTest { get; private set; }
    /// <summary>这一帧对照层做了几次整块位图转换（重影档与差异档各只有一条分支会走，所以正常是 1）。</summary>
    public int OverlayPreviewConversionsForTest { get; private set; }
    /// <summary>这一帧"拼对照"那一步<b>真从两份被裁掉的区域里取回多少行</b>（第二十六改）。
    /// 判据：<c>canvas/拼对照那一步要交出自己真取了几行</c>。</summary>
    public int OverlayPreviewDiscardedRowsForTest { get; private set; }
    /// <summary>这一帧"拼对照"那一步<b>分配了多少字节</b>（对照图那一块＋两次取数各一块）。
    /// 它与行数相除就是"每行多宽"，两个数放在一起才能把"行多"与"行宽"分开。</summary>
    public long OverlayPreviewDiscardedBytesForTest { get; private set; }

    /// <summary>最近一帧的叠层拆账（人读的那句）与<b>最慢那帧</b>的拆账——与分阶段那两笔同一种分工。</summary>
    public string LastOverlaySplit { get; private set; } = "";

    public string SlowestOverlaySplit { get; private set; } = "";

    /// <summary>
    /// 【第十四改·归因】读数层（F8 那一整段）往下的<b>本帧</b>拆账。
    /// 前面几改留在进程累计口径上（工装自己作差报"这一趟"），这一份是<b>逐帧</b>的：
    /// 真窗口量到"最慢那帧 97.9ms 里读数层自己占 33.5ms"，可 33.5ms 里面装的三种钱
    /// （面板重建带一次大对象投放／那几笔虚线描边／把面板贴上去）在累计口径里分不开，
    /// 而它们各自的修法完全不同 ⇒ 先按帧劈开，再决定动谁（这一族第三次这么走）。
    /// <c>Residual</c>＝本帧读数层整段 −（本段那些笔），它大就是拆得不够；
    /// <c>Geometry</c> 单列<b>不计进残差</b>——EnsureHudRects 是色块层与读数层共用的那份缓存，
    /// 先跑的那一次在叠层里，它的毫秒落在读数层那两个计时点之外（硬算进去恒等式天生为负）。
    /// <b>为什么是 <c>readonly record struct</c> 而不是 <c>record</c></b>：这一份<b>每帧都要写</b>，
    /// 引用类型就是每帧一次托管分配，而 <c>canvas/读数层每帧的额外堆分配：不新建 Brush、Font，也不重排文本</c> 那格判据（限值 512B／帧）
    /// 正是为了抓住"给调试层加一笔就顺手加一次分配"这件事——本批第一次跑就把它撞红了，
    /// 于是实现改成值类型＋文案<b>按需格式化</b>（读数出口在工装那边，一趟一次，不在帧里）。
    /// </summary>
    public readonly record struct HudFrameNumbers(double Geometry, double Text, double Border, double Hits,
                                                  double PanelAlloc, double PanelReuse,
                                                  double PanelClear, double PanelDrawText,
                                                  double Blit,
                                                  double HudTotal, double Residual,
                                                  int GeometryRebuilds, int PanelRebuilds, int PanelAllocations,
                                                  int HitRects, int PanelBlits);

    private HudFrameNumbers _hudFrameNumbers;
    private bool _hudFrameNumbersValid;
    private HudFrameNumbers _slowestHudFrameNumbers;
    private bool _slowestHudFrameNumbersValid;

    /// <summary>最近结算那一帧的读数层数值（关着读数层时为 null）。</summary>
    public HudFrameNumbers? LastHudNumbersForTest => _hudFrameNumbersValid ? _hudFrameNumbers : null;

    /// <summary><b>最慢那帧</b>的读数层数值——封口与叠层／分阶段同一个 <c>if</c>，三笔永远同帧。</summary>
    public HudFrameNumbers? SlowestHudNumbersForTest => _slowestHudFrameNumbersValid ? _slowestHudFrameNumbers : null;

    /// <summary>人读的那两句<b>按需</b>格式化（每帧格式化会多一次字符串分配，见上面那条注释）。
    /// 关着读数层时交回空串，工装把它打成"量不到"而不是"量到了 0"。</summary>
    public string LastHudSplit => _hudFrameNumbersValid ? FormatHudSplit(_hudFrameNumbers) : "";

    public string SlowestHudSplit => _slowestHudFrameNumbersValid ? FormatHudSplit(_slowestHudFrameNumbers) : "";

    private static string FormatHudSplit(HudFrameNumbers n) =>
        $"文本{n.Text:F1} 描边(边界{n.Border:F1}／命中区{n.Hits:F1}) "
        + $"面板重建(分配{n.PanelAlloc:F1}／复用{n.PanelReuse:F1}，其中清底{n.PanelClear:F1}／排字{n.PanelDrawText:F1} 是子项) "
        + $"贴图{n.Blit:F1} 残差{n.Residual:F1} ms"
        + $"（本帧读数层整段 {n.HudTotal:F1}ms；几何换算另有 {n.Geometry:F1}ms／{n.GeometryRebuilds} 次，"
        + "它与色块层共用那份缓存、先跑的是叠层那一次，所以不计进本段；"
        + $"这一帧重建 {n.PanelRebuilds} 次／其中分配 {n.PanelAllocations} 次、贴图上屏 {n.PanelBlits} 次；"
        + "残差＝整段减去本段那些笔，它大就是拆得不够）";

    /// <summary>读数层各笔的<b>进程累计</b>取一份快照——帧起点记下它，结算时作差就是"这一帧的增量"。</summary>
    private HudFrameNumbers HudCumulativeSnapshot() => new(
        HudGeometryRebuildMs, HudTextBuildMs, HudBorderOutlineMs, HudHitsOutlineMs,
        HudPanelAllocRebuildMs, HudPanelReuseRebuildMs,
        HudPanelClearMs, HudPanelDrawTextMs, HudBlitMs, 0, 0,
        (int)HudGeometryRebuilds, (int)HudPanelRebuilds, (int)HudPanelAllocations,
        HudHitRectsDrawn, (int)HudPanelUnscaledBlits);

    private HudFrameNumbers _hudBaseAtFrameStart;

    // 本帧各段的累计（PaintCore 里逐段加，TakeStageBreakdown 结算时清零）。

    /// <summary>
    /// 【第九改·归因】最近一帧里"经取数通路首次解码源图"花的毫秒数与张数（只算这一条线程）。
    /// 它是<b>取数那一笔的子项</b>（所以不进六笔恒等式、不参与任何几何或像素决定）：
    /// 要能把"45.4ms 那种帧到底是被一次 PNG 解码撞了、还是渲染本身就慢"这两件事分开。
    /// </summary>
    private double _decodeMsAtPaintStart;
    private int _decodeRunsAtPaintStart;
    public double LastPaintDecodeMsForTest { get; private set; }
    public int LastPaintDecodeRunsForTest { get; private set; }

    /// <summary>
    /// 【第十一改·归因】最近一帧里"贴图那一笔经首次 <c>RgbaImage → GDI 位图</c> 转换"的毫秒数与块数。
    /// 与上一笔同一个身份：<b>贴图那一笔的子项</b>，不进六笔恒等式、不参与任何几何或像素决定。
    /// 要分开的是"这一帧有 N 块瓦片第一次上屏（每块约 1MB 拷贝，一次性的）"与
    /// "每帧必做的重采样（高质量档把 512×512 画成屏幕尺寸）"——前者的修法是分摊到几帧，
    /// 后者的修法是途中走便宜档；不劈开就没法决定动哪一个，也就不许说"最慢帧慢在贴图"就算归因完成。
    /// </summary>
    public double LastBlitConvertMsForTest { get; private set; }
    public int LastBlitConvertTilesForTest { get; private set; }
    /// <summary>这一帧贴图里<b>重采样</b>那一半（每块都算，冷块热块都算）。判据：<c>canvas/贴图那一笔的两半各自要有自己的计时点</c>。</summary>
    public double LastBlitDrawMsForTest { get; private set; }
    /// <summary>这一帧贴了几块（＝重采样那一列的块数）。全命中缓存的一帧里它必须 &gt; 0 而首次转换必须是 0 块。</summary>
    public int LastBlitDrawTilesForTest { get; private set; }

    private double _msOvBackground, _msOvPreview, _msOvSeams, _msOvPatch, _msOvBadge, _msOvPick;
    // 第二十五改：预览那一笔往下的三段（同一对时钟不许再共用——第二十四改那条教训的同一个形状）。
    // 身份与"取数里首次解码""贴图里首次转换"完全相同：<b>子项</b>，不进叠层那六列之和，也不进六笔恒等式。
    private double _msOvPvRender, _msOvPvStitch, _msOvPvBlit;
    // 本帧对照层真的做了几次「RgbaImage → GDI 位图」转换（与毫秒无关，第二十二改那条：要判有没有做就数次数）。
    private int _ovPvConversionsAccum;
    // 第二十六改：拼对照那一步<b>真取的行数</b>与<b>真分配的字节数</b>（都不带时钟）。
    // 为什么不用毫秒判这一步该不该动：上一改只读到"它 17ms"，而 17ms 里有三种完全不同的钱
    // （行数多→拷贝多／行宽大→单次拷贝大→落进大对象堆→GC 压力／源图缓存没命中→现解码），
    // 修法各不相同。行数与字节数能把前两种分开，且<b>与机器无关</b>，所以这一改先交这两个数。
    private int _ovPvDiscardRowsAccum;
    private long _ovPvDiscardBytesAccum;

    /// <summary>
    /// <b>这一帧</b>补丁层那段被真调了几次、读数层那段被真调了几次（第二十二改，#104）。
    /// ═══ 为什么要有这两个计数 ═══
    /// 守卫 <c>guard/分阶段只报前三段时这一帧的账加不满</c> 里那条"关掉叠层之后叠层必须变小"，
    /// 原本比的是<b>五帧的毫秒平均</b>——同一台机器上它会自己翻面（第十九改那批读到过
    /// 开着 1.35ms → 关着 2.00ms 的倒挂 0.65ms，随后两批又全绿），判的其实是这台机器那一刻忙不忙。
    /// 叠层这一笔本身是<b>减出来的残差</b>，拿它的毫秒去比"有没有东西画"永远隔着一层。
    /// 这两个计数把那句话换成与机器无关的形状：<b>层开着 ⇒ 那段被调了几次就是几次；层关着 ⇒ 必须是 0 次</b>。
    /// 毫秒照旧交出去当读数（拆账那两行没动），只是<b>不再拿它当闸门</b>。
    /// </summary>
    public int OverlayPatchCallsForTest { get; private set; }
    public int OverlayHudCallsForTest { get; private set; }

    /// <summary>本帧累计中的两次调用计数；<see cref="OverlayPatchCallsForTest"/> 交回的是<b>上一帧结算时的快照</b>
    /// （与那六段毫秒同一生命周期——毫秒也是在那一步清零的，读原始累计值只会读到 0）。</summary>
    private int _ovPatchCallsAccum, _ovHudCallsAccum;

    /// <summary>
    /// 结算一帧的分阶段耗时：先前的累计清零，返回这一帧的拆分。
    ///
    /// ═══ 为什么要拆到"叠层"与"帧前"两笔 ═══
    /// 前三段是 DrawTiles 里逐块瓦片真计时的，读数层那一段在 PaintCore 末尾单独立了一列；
    /// 但一帧里另有六段<b>从没计过时</b>的绘制（背景、色块对比层、拼缝线、补丁层、拖动角标、点选引导线）。
    /// 于是"最慢那帧 37.4ms 而分阶段只加到 20.4ms"这句话说的<b>不是系统抢占，是这里的账目缺口</b>——
    /// 分不清"另有未计时的段"与"被抢占"，就等于把归因写成了猜测。
    /// 上一批只补了一笔笼统的"其余"，本批按 PaintCore 的边界把它切成两笔：
    /// <list type="bullet">
    ///   <item><b>叠层</b>＝PaintCore 里没逐段计时的绘制（<b>我们自己的代码</b>，要优化就优化这里）；</item>
    ///   <item><b>帧前</b>＝OnPaint 入口到 PaintCore 之前那一小截（帧计数、时间戳入队），
    ///         实测这一笔恒在 0.0ms（一位小数）量不到的量级。</item>
    /// </list>
    ///
    /// ═══ 这一套帧样本<b>看不见</b>什么（口径的边界，必须与读数一起说）═══
    /// 帧总时长是从 OnPaint 进门取到 finally 取时钟，所以：
    /// WinForms 的双缓冲把位图贴回屏幕、WM_PAINT 的派发与消息队列等待都在 OnPaint <b>之外</b>，
    /// 天生不在这一列里。上一批把这第二笔写成"帧外＝PaintCore 之外，含缓冲交换与派发"是<b>错的</b>：
    /// 交换发生在 OnPaint 返回之后，帧样本根本没有覆盖到它，把没量到的东西写进口径就是编故事。
    /// 所以"叠层很大"指向我们的绘制代码，"帧前很大"指向帧计数那几行，
    /// 而<b>屏上感觉比这些数还慢</b>时，能查的是派发与交换，不是这两笔。
    /// 两笔都只负责让账加得满，<b>不许单凭某一笔大就写成"被抢占"</b>——那还得同帧的回收标记与
    /// 调度那行一起说。判据 <c>canvas/最慢帧分阶段要把整帧的账加满</c>，
    /// 反证 <c>guard/分阶段只报前三段时这一帧的账加不满</c>。
    /// </summary>
    private string TakeStageBreakdown(double frameMs, double coreMs)
    {
        double overlay = coreMs - (_msTileFetch + _msChecker + _msBlit + _hudMsThisFrame);
        double prologue = frameMs - coreMs;
        LastStageNumbersForTest = new StageNumbers(
            _msTileFetch, _msChecker, _msBlit, _hudMsThisFrame, overlay, prologue, frameMs);
        // 叠层往下再拆一层（第五改）：六段与残差加起来必须等于上面那一笔 overlay，
        // 恒等式由 TakeStageBreakdown 自己算——残差是减出来的，不是写死的。
        double subs = _msOvBackground + _msOvPreview + _msOvSeams + _msOvPatch + _msOvBadge + _msOvPick;
        double residual = overlay - subs;
        LastOverlayNumbersForTest = new OverlayNumbers(
            _msOvBackground, _msOvPreview, _msOvSeams, _msOvPatch, _msOvBadge, _msOvPick, residual, overlay);
        // 预览那一笔的三段快照（与上面那六段同一时机结算：判据读原始累计值只会读到 0）
        OverlayPreviewRenderMsForTest = _msOvPvRender;
        OverlayPreviewStitchMsForTest = _msOvPvStitch;
        OverlayPreviewBlitMsForTest = _msOvPvBlit;
        OverlayPreviewConversionsForTest = _ovPvConversionsAccum;
        OverlayPreviewDiscardedRowsForTest = _ovPvDiscardRowsAccum;
        OverlayPreviewDiscardedBytesForTest = _ovPvDiscardBytesAccum;
        LastOverlaySplit = $"背景{_msOvBackground:F1} 预览{_msOvPreview:F1} 拼缝{_msOvSeams:F1} "
            + $"补丁{_msOvPatch:F1} 角标{_msOvBadge:F1} 点选{_msOvPick:F1} 残差{residual:F1} ms"
            + "（六段与残差加起来＝叠层那一笔；残差＝取布局那一次与调用之间的空隙，它大就是拆得不够）"
            + $"｜预览拆账 渲染{_msOvPvRender:F1}／拼对照{_msOvPvStitch:F1}／比并贴{_msOvPvBlit:F1}ms"
            + $"，位图转换 {_ovPvConversionsAccum} 次（三段都是预览那一列的子项，不进上面那六列之和）"
            // 第二十六改：拼对照那一步"这一帧真干了哪些行"——不改一个像素，只把下一改的靶子
            // 从"它 17ms"变成"它取回 N 行、分配 M MB"（行宽从这两个数除出来）。
            + $"｜拼对照取回 {_ovPvDiscardRowsAccum} 行／分配 {_ovPvDiscardBytesAccum / 1048576.0:F2} MB"
            + "（这一步的真活，与毫秒无关）";
        // 读数层往下的本帧拆账（第十四改）：与上面那一笔 overlay 拆法同一族——
        // 各笔是<b>本帧增量</b>（基线在 OnPaint 入口记），残差是减出来的，不是写死的。
        // 关着读数层时整段是 0，那一行读数就报"这一帧没这一笔"（LastHudSplit 留空）而不是报一串 0
        // （报 0 会被读成"量到了 0"）；开关判据用 ShowDebugHud 而不是 hudMs>0——时钟精度给得出 0.000 的一帧。
        HudFrameNumbers now = HudCumulativeSnapshot();
        HudFrameNumbers b = _hudBaseAtFrameStart;
        if (ShowDebugHud)
        {
            double hudGeom = now.Geometry - b.Geometry, hudText = now.Text - b.Text,
                   hudBorder = now.Border - b.Border, hudHits = now.Hits - b.Hits,
                   hudAlloc = now.PanelAlloc - b.PanelAlloc, hudReuse = now.PanelReuse - b.PanelReuse,
                   hudClear = now.PanelClear - b.PanelClear, hudDraw = now.PanelDrawText - b.PanelDrawText,
                   hudBlit = now.Blit - b.Blit;
            // 几何换算那一笔<b>不进本段的账</b>（见 HudFrameNumbers 那段注释），所以残差只减本段那些笔。
            double hudResidual = _hudMsThisFrame - (hudText + hudBorder + hudHits + hudAlloc + hudReuse + hudBlit);
            _hudFrameNumbers = new HudFrameNumbers(hudGeom, hudText, hudBorder, hudHits,
                hudAlloc, hudReuse, hudClear, hudDraw, hudBlit, _hudMsThisFrame, hudResidual,
                now.GeometryRebuilds - b.GeometryRebuilds, now.PanelRebuilds - b.PanelRebuilds,
                now.PanelAllocations - b.PanelAllocations, now.HitRects - b.HitRects,
                now.PanelBlits - b.PanelBlits);
            _hudFrameNumbersValid = true;
        }
        else
        {
            _hudFrameNumbersValid = false;
        }
        // 取数那一笔里的"首次解码"（第九改）：<b>子项</b>，不是第七笔——
        // 六笔加满整帧那个恒等式因此完全不受影响（判据照旧核对六笔）。
        // 为什么必须量它：文档里"一块瓦片内部的冷取数拆不开（含那张源图首次解码）"这句
        // 挂了四批，括号那半句从来没被量过；现在能当场分开"解码撞在 UI 线程上"与"渲染本身慢"。
        double decodeMs = Imaging.FileSourceResolver.DecodeMsOnCurrentThread - _decodeMsAtPaintStart;
        int decodeRuns = Imaging.FileSourceResolver.DecodeRunsOnCurrentThread - _decodeRunsAtPaintStart;
        LastPaintDecodeMsForTest = decodeMs;
        LastPaintDecodeRunsForTest = decodeRuns;
        // 贴图那一笔里的"首次转换"（第十一改）：同样是<b>子项</b>，恒等式照旧只核六笔。
        LastBlitConvertMsForTest = _msBlitConvert;
        LastBlitConvertTilesForTest = BlitConvertTilesThisFrame;
        LastBlitDrawMsForTest = _msBlitDraw;
        LastBlitDrawTilesForTest = BlitDrawTilesThisFrame;
        // 第三十一改：这一帧铺了几格棋盘格。与"贴了几块"配成一对读数——真实截图那批瓦片全不透明，
        // 于是正确的形状是「贴了 N 块、棋盘格 0 格」；判据就钉这一对数（毫秒留给需求五那一档）。
        LastFrameCheckerSquaresForTest = CheckerSquaresThisFrame;
        // ═══ 第三十三改：整帧那一笔托管堆分配往下拆三段 ═══
        // 上一改刚开的那枚读数在真窗口读到 6.4~8.3 MB／帧（同命令行每帧重算 6~8 块），
        // 而电池稳态帧只有 88 KB／帧。差 70 倍这件事<b>落在哪一段</b>当时没有读数，
        // 而取数（每帧冷渲染一块瓦片、新造一个整块像素缓冲）与转换（RgbaImage→GDI 位图，
        // 第十二改已改成按行走）的修法相反 ⇒ 先各接一只字节表，下一改才有权决定动谁。
        LastFrameAllocFetchBytesForTest = _allocFetch;
        LastFrameAllocConvertBytesForTest = _allocConvert;
        LastFrameAllocHudBytesForTest = _allocHud;
        LastFrameAllocOverlayPreviewBytesForTest = _allocOverlayPreview;
        long allocRest = LastFrameAllocBytesForTest - _allocFetch - _allocConvert - _allocHud - _allocOverlayPreview;
        string text = $"取数{_msTileFetch:F1} 棋盘{_msChecker:F1} 贴图{_msBlit:F1} "
            + $"读数层{_hudMsThisFrame:F1} 叠层{overlay:F1} 帧前{prologue:F1} ms"
            + $"（这一帧托管堆分配 {LastFrameAllocBytesForTest / 1024.0:F0}KB：取数 {_allocFetch / 1024.0:F0}KB／"
            + $"转换 {_allocConvert / 1024.0:F0}KB／读数层 {_allocHud / 1024.0:F0}KB／对照层 {_allocOverlayPreview / 1024.0:F0}KB／"
            + $"其余 {allocRest / 1024.0:F0}KB；"
            + $"取数里首次解码 {decodeMs:F1}ms／{decodeRuns} 张；"
            + $"贴图里首次转换 {LastBlitConvertMsForTest:F1}ms／{LastBlitConvertTilesForTest} 块、"
            + $"重采样 {LastBlitDrawMsForTest:F1}ms／{LastBlitDrawTilesForTest} 块；"
            + "叠层＝PaintCore 里没逐段计时的绘制；帧前＝OnPaint 入口到 PaintCore 之前；"
            + "双缓冲交换与派发在 OnPaint 之外，这一套帧样本量不到）";

        _msTileFetch = 0;
        _msChecker = 0;
        _allocFetch = 0;
        _allocConvert = 0;
        _allocHud = 0;
        _allocOverlayPreview = 0;
        _msBlit = 0;
        _msBlitConvert = 0;
        BlitConvertTilesThisFrame = 0;
        _msBlitDraw = 0;
        BlitDrawTilesThisFrame = 0;
        CheckerSquaresThisFrame = 0;
        _msOvBackground = 0;
        _msOvPreview = 0;
        _msOvPvRender = 0;
        _msOvPvStitch = 0;
        _msOvPvBlit = 0;
        _ovPvConversionsAccum = 0;
        _ovPvDiscardRowsAccum = 0;
        _ovPvDiscardBytesAccum = 0;
        _msOvSeams = 0;
        _msOvPatch = 0;
        _msOvBadge = 0;
        _msOvPick = 0;
        // 两笔调用计数在<b>同一时机</b>结算成"最近那一帧的快照"再清零：那六段毫秒也是在这一步清零的，
        // 判据读原始累加值只会读到 0（读到 0 会被当成"这层没画东西"，那就是又一个能被误满足的读数）。
        OverlayPatchCallsForTest = _ovPatchCallsAccum;
        OverlayHudCallsForTest = _ovHudCallsAccum;
        _ovPatchCallsAccum = 0;
        _ovHudCallsAccum = 0;
        return text;
    }

    /// <summary>
    /// 棋盘格那两把刷子。<b>第三十一改提到静态</b>：需求三第 5 条明写"不要每个 OnPaint 都重新创建 Brush"，
    /// 而这里原来是每块瓦片每次铺都新建两支 <c>SolidBrush</c>。颜色来自 <see cref="Theme"/>，
    /// 提上去不会把配色冻住：第四十八改起 Theme 的颜色是转发到当前调色板的属性，
    /// 换肤时 <see cref="RefreshPaintColors"/> 就地把这两把刷子的 Color 改掉（对象仍是同一枚）。
    /// </summary>
    private static readonly System.Drawing.SolidBrush CheckerLightBrush = new(Theme.CheckerLight);
    private static readonly System.Drawing.SolidBrush CheckerDarkBrush = new(Theme.CheckerDark);

    /// <summary>
    /// 瓦片贴图用的环绕模式：边缘像素向外钳制重复（第四十六改）。
    /// <b>没有它会发生什么</b>：GDI+ 在<b>缩小</b>重采样时会对位图边缘外取样，而边缘外被视为
    /// "透明黑"，于是每块瓦片最外一圈设备像素被混进透明 ⇒ 半透明 ⇒ 深色画布底透上来 ⇒
    /// 瓦片交界与内容外缘出现一条灰线（用户第四十六改图1圈出的那一横一竖就是它；
    /// 灰线只在某些缩放档出现，因为混多少取决于缩放的小数相位）。
    /// TileFlipXY 让边缘像素自己重复出去，插值就不再往透明里混。静态一枚，不每帧新建。
    /// </summary>
    /// <summary>
    /// 欠帧棋盘格的最低档位：一块瓦片在屏上小于这个设备像素数（即 zoom 太小、一屏装下整份文档）时，
    /// 欠帧那一帧不画棋盘格——理由与阈值出处见 <c>DrawTiles</c> 欠帧那一段的注释。
    /// </summary>
    private const int DeferredCheckerMinTileDevicePx = 64;

    private static readonly System.Drawing.Imaging.ImageAttributes TileEdgeClamp = BuildTileEdgeClamp();

    private static System.Drawing.Imaging.ImageAttributes BuildTileEdgeClamp()
    {
        var attrs = new System.Drawing.Imaging.ImageAttributes();
        attrs.SetWrapMode(System.Drawing.Drawing2D.WrapMode.TileFlipXY);
        return attrs;
    }

    /// <summary>
    /// 铺棋盘格：透明区（这一块有 alpha&lt;255 的像素）与欠帧区（压根还没有内容）共用这一份画法。
    /// <paramref name="clip"/> 非空时跳过裁剪区之外的格子。
    /// 相位照旧按<b>整块瓦片</b>的起点算（不是按裁剪区起点），所以画出来的格子图案与逐格全画时
    /// 逐像素相同——省下来的只是 GDI 调用次数，不改一像素。
    /// 方格数一并计数：判据要能问"这一帧到底铺了几格"，光有毫秒就又是拿噪声当证据。
    /// </summary>
    private void DrawChecker(System.Drawing.Graphics g, Rectangle r, Rectangle clip)
    {
        const int cell = 8;
        CheckerCalls++;
        int xStart = (r.X / cell) * cell, yStart = (r.Y / cell) * cell;
        for (int y = yStart; y < r.Bottom; y += cell)
            for (int x = xStart; x < r.Right; x += cell)
            {
                var square = Rectangle.Intersect(new Rectangle(x, y, cell, cell), r);
                if (!clip.IsEmpty && !clip.IntersectsWith(square)) continue;
                bool even = (((x - xStart) / cell + (y - yStart) / cell) & 1) == 0;
                g.FillRectangle(even ? CheckerLightBrush : CheckerDarkBrush, square);
                CheckerSquares++;
                CheckerSquaresThisFrame++;
            }
    }

    /// <summary>
    /// 画所有拼缝：未选中是半透明蓝线，选中的加粗并带把手与状态文字。
    ///
    /// ═══ 这一段现在是"叠层里最大的一笔"，所以这里的每次分配都要算账 ═══
    /// 第五改把叠层拆开之后，真窗口在 200% 档读到 拼缝 4.2~6.8ms/帧（整帧约 12ms），
    /// 是这个项目一帧里最大的一段。查下来钱不在几何（一条线而已），而在<b>每条缝每帧都新建
    /// 三支 GDI 对象、还要为状态标签量一次文字宽度</b>：16 条缝 × 每帧 = 一帧里几十次分配与十几次
    /// MeasureText，而这些东西<b>只在文字内容或字体变化时才会不一样</b>。
    /// 于是这里的取舍改成：颜色与宽度固定的笔/刷提到静态（换肤时由 <see cref="RefreshPaintColors"/>
    /// 就地改 Color，对象不换、字段仍 readonly，所以"不许每帧新建"这条依旧成立）；
    /// 标签宽度走一张以"文字 × 字体 × DPI"为键的小表。
    /// 判据 <c>canvas/缝状态标签的宽度只量一次</c>，反证 <c>guard/标签宽度缓存的键必须含字体与 DPI</c>。
    /// 画法、矩形公式、颜色一个字节都没改——这是显示层，不参与取数与导出（红线三）。
    /// </summary>
    private void DrawSeams(System.Drawing.Graphics g, CanvasLayout layout)
    {
        bool horizontal = layout.Mode == StitchMode.Horizontal;

        for (int i = 1; i < layout.Placed.Count; i++)
        {
            PlacedSegment placed = layout.Placed[i];
            Seam? seam = placed.IncomingSeam;
            if (seam is null) continue;

            Rectangle line = SeamLineDevice(layout, placed, horizontal);

            bool selected = seam.Id == SelectedSeamId;
            bool zoomedOut = Viewport.Zoom < SeamChromeMinZoom;
            // ═══ 缝线颜色＝匹配结论，线正中一枚方框＝结论的图章（用户第四十五改）═══
            // 用户原话："匹配成功就用蓝色，线条居中放一个方框，方框里是勾；匹配有问题就变成橙色的线条，
            // 方框里是⚠。" 于是这一层从"选中才看得出状态"改成<b>扫一眼全图就知道哪几条要管</b>：
            //   成功（自动通过／手动确认）⇒ 蓝线 + 勾；有问题（需检查／手动未确认）⇒ 橙线 + 警告；
            //   还没结论（未分析／分析中）⇒ 灰线 + 空框（不画勾也不画警告，免得把"没算"说成"算坏了"）。
            // 线任何档位都画、方框任何档位都画（方框就是状态图章，收掉它就回到"看不见哪条有问题"）；
            // 缩小档收掉的只剩<b>带文字的状态标签盒子</b>——那才是 100 条缝时的一片灰块。
            // 笔与刷全部静态（六种状态色 × 选中与否），不每帧新建 GDI 对象。
            // 只动显示层：命中区、取数、导出一个字节都不变（红线三）。
            // 判据：<c>canvas/缩小浏览时未选中的缝画状态色实线加一枚状态方框</c> 数线／框／标签三笔账、
            // <c>guard/缩小档缝线颜色必须跟着匹配状态走</c> 逐像素核两种状态色、
            // <c>guard/放大到 100% 未选中的缝必须连状态标签一起画</c> 证明标签只是缩小档收、不是删。
            bool ok = seam.Status is SeamStatus.AutoPassed or SeamStatus.ManualConfirmed;
            bool problem = seam.Status is SeamStatus.NeedsReview or SeamStatus.ManualPending;
            Pen pen = selected
                ? (ok ? SeamLineOkSelectedPen : problem ? SeamLineProblemSelectedPen : SeamLinePendingSelectedPen)
                : (ok ? SeamLineOkPen : problem ? SeamLineProblemPen : SeamLinePendingPen);
            // ═══ 第五十五改①（用户图1「分割线条，放大后不能太小」）═══
            // 线宽原来写死 1/2 设备像素：缩小档看得见（第四十五改的方框与标签也在），
            // 但放大到 200%、400% 之后内容一格比一格巨大，缝线还是那根 1 像素发丝，
            // 横穿整屏都读不出"这里有条可点的缝"。现在线宽随缩放档长上去：
            // ≤100% 维持原样（1/2 像素），>100% 每过一档加一像素、上限 4/5。
            // 笔仍是那六支静态对象（只改 Width，不每帧新建——"画具提静态"红线原样成立）；
            // 只动显示层，命中区与导出一个字节不变（红线三）。
            // 判据 <c>canvas/缝线宽度随缩放档走且放大档有下限</c>。
            pen.Width = SeamLinePenWidthForZoom(Viewport.Zoom, selected);
            g.DrawLine(pen, line.X, line.Y, line.Right, line.Bottom);
            if (!selected) SeamHairlinesDrawnForTest++;

            DrawSeamStatusBox(g, seam, line, selected, ok, problem);
            if (!selected && zoomedOut)
            {
                SeamChromeZoomSkippedForTest++;
            }
            else
            {
                DrawSeamStatus(g, seam, line, selected);
            }
            if (selected) DrawAlignmentOverlay(g, layout, seam);
        }
    }

    /// <summary>成功档缝线笔：不透明纯强调色（半透明混底就是用户圈过的灰线一族）。</summary>
    private static readonly Pen SeamLineOkPen = new(Theme.Accent, 1f);
    private static readonly Pen SeamLineProblemPen = new(Theme.Warning, 1f);
    private static readonly Pen SeamLinePendingPen = new(Theme.TextSecondary, 1f);
    private static readonly Pen SeamLineOkSelectedPen = new(Theme.Accent, 2f);
    private static readonly Pen SeamLineProblemSelectedPen = new(Theme.Warning, 2f);
    private static readonly Pen SeamLinePendingSelectedPen = new(Theme.TextSecondary, 2f);

    /// <summary>
    /// 拼缝线的设备像素线宽（第五十五改①，用户图1：缝线「放大后不能太小」）。
    ///
    /// 规则（纯函数，判据可直接对表）：
    ///   · ≤100%：维持 1（未选中）/ 2（选中）——缩小与原大档的观感与历史逐像素一致，
    ///     第四十五改那三格缝线判据的量法原样成立；
    ///   · &gt;100%：用 Ceiling 让"只要放大了就至少加粗一档"，再随缩放继续长：
    ///     150%→2/3、250%→3/4、400% 及以上钳到 4/5 封顶（再粗就会盖住缝两侧的像素内容，
    ///     用户在放大档恰恰是在逐像素核对拼接质量）。
    /// 进制口径：Ceiling(round 半路就往上走) 保证 101% 与 199% 都读作"放大档"，不出现
    /// "明明放大了线还是 1 像素"的死角——那正是用户圈出来的形状。
    /// </summary>
    /// <remarks>public：本仓没有 InternalsVisibleTo，电池工装（LongStitch.Checks）要按表核这把尺。</remarks>
    public static int SeamLinePenWidthForZoom(double zoom, bool selected)
    {
        int core = Math.Clamp((int)Math.Ceiling(zoom), 1, 4);
        return Math.Min(core + (selected ? 1 : 0), 5);
    }
    /// <summary>状态方框的三枚底色刷（与线同色），静态：省每帧新建 GDI 对象。</summary>
    private static readonly Brush SeamBoxFillOk = new SolidBrush(Theme.Accent);
    private static readonly Brush SeamBoxFillProblem = new SolidBrush(Theme.Warning);
    private static readonly Brush SeamBoxFillPending = new SolidBrush(Theme.TextSecondary);
    private static readonly Pen SeamHandleBorderPen = new(Theme.WindowBackground, 1f);
    /// <summary>微调模式开着时方框的描边：比底色更抢眼的强调色，让人一眼看出"现在在调这条缝"。</summary>
    private static readonly Pen SeamBoxBorderAdjust = new(Theme.Accent, 2f);
    private static readonly Brush SeamHandleFillSelected = new SolidBrush(Theme.Accent);
    // 蓝线上那枚小按钮的<b>图形</b>画具（上下两枚三角 / 一枚对勾）。提静态：这一层每条缝每帧
    // 都要画两笔，没必要每次新建 GDI 对象再销毁（与"最后两支每帧新建的笔刷提静态"同一课）。
    // 对勾那支笔宽度写死 2 设备像素并带圆头：1 像素的折线在 25% 缩放档上会糊成一团，
    // 而那正是用户唯一还在找"我点的是不是完成"的时刻。
    private static readonly Brush SeamHandleGlyphBrush = new SolidBrush(Color.White);
    private static readonly Pen SeamHandleGlyphPen = new Pen(Color.White, 2f)
    {
        StartCap = System.Drawing.Drawing2D.LineCap.Round,
        EndCap = System.Drawing.Drawing2D.LineCap.Round,
        LineJoin = System.Drawing.Drawing2D.LineJoin.Round,
    };
    // 画三角形与对勾都要一个三点折线。每帧新建 Point[] 就是每帧一笔小分配，
    // 而 FillPolygon / DrawLines 在调用内部就把点抄走了 ⇒ 同一份缓冲可以一直复用。
    private readonly Point[] _glyphScratch = new Point[3];
    private static readonly Brush SeamStatusLabelBg = new SolidBrush(WithAlpha(Theme.PanelBackground, 220));

    /// <summary>
    /// 状态标签的宽度缓存。键必须<b>六项齐全</b>：文字、字体名、字号、字形、单位、DpiX、DpiY。
    /// 第一版只写了"字体名 + DPI"，被反证那一格当场抓到：<b>Font.Name 不含字号</b>，
    /// 同一句"已通过"在 8.5 与 20 字号下族名相同，缓存于是把 96px 的量结果当 44px 用——
    /// 盒子比字窄、标签被截，正是需求四复看时量到过的那一族截字缺陷。少任何一项都可能拿回别的量结果。
    /// 键的空间是"状态枚举 × 一小撮字体 × 机器上的 DPI"，量到几十条就该怀疑哪里在漏，
    /// 于是超过 64 条整表清掉重来（宁可重新量一次，也不让一张表跟着项目寿命无限长）。
    /// </summary>
    private readonly record struct SeamLabelKey(string Label, string Font, float Size,
        FontStyle Style, GraphicsUnit Unit, int DpiX, int DpiY);

    private static readonly Dictionary<SeamLabelKey, Size> SeamLabelCache = new();

    /// <summary>真的去量文字宽度的次数（进程累计）——判据用它证明"缓存建好之后一帧都没再量"。</summary>
    public static int SeamLabelMeasureCountForTest { get; private set; }

    /// <summary>命中缓存的次数（进程累计）。</summary>
    public static int SeamLabelCacheHitsForTest { get; private set; }

    public static int SeamLabelCacheEntriesForTest => SeamLabelCache.Count;

    /// <summary>量状态标签的宽度（走那张"文字 × 字体 × DPI"的小表）。判据直接调它核对键的组成。</summary>
    public static Size MeasureSeamLabel(System.Drawing.Graphics g, string label, Font font)
    {
        var key = new SeamLabelKey(label, font.Name, font.Size, font.Style,
            font.Unit, (int)g.DpiX, (int)g.DpiY);
        if (SeamLabelCache.TryGetValue(key, out Size hit))
        {
            SeamLabelCacheHitsForTest++;
            return hit;
        }
        SeamLabelMeasureCountForTest++;
        Size size = TextRenderer.MeasureText(g, label, font);
        if (SeamLabelCache.Count > 64) SeamLabelCache.Clear();
        SeamLabelCache[key] = size;
        return size;
    }

    /// <summary>
    /// 【仅屏幕上的调试叠层，绝不进出成品】需求八要的那三件概念，一次画全：
    /// 图1 有效区域（绿）、图2 有效区域（黄）、重叠带（白/灰）、蓝色裁切线、红色箭头。
    ///
    /// ═══ 为什么它必须只是"叠层" ═══
    /// 这一层只读布局算出来的公共坐标（Rect1 / Rect2 / OverlapRect / 蓝线），
    /// 一个字节都不写进参数、也不参与取数：关掉它之后
    /// AlignmentOffset、CutY、导出字节、命中区域四样全都一字不变
    /// （判据 <c>align/关掉调试层不改对齐、蓝线、导出字节与命中区</c>）。
    /// 蓝线本身由 <see cref="DrawSeams"/> 那条线画（绘制与命中同源），这里只补框与箭头。
    ///
    /// ═══ 第十三轮需求六：两个开关都能叫出这一层 ═══
    /// 「重影对比」是日常用来肉眼核对齐的那一层；<b>F8 读数层</b>是"把需求六点名的那套几何一次摊开"的
    /// 调试层，两者画的是<b>同一份</b>公共坐标（同一批 CanvasToDevice 换算、同一批 Theme.Overlay* 颜色），
    /// 所以不必也不许各写一套画法。只开 F8 时也必须看到绿/黄/白/红，否则需求六那句
    /// "一个开关显示 Rect1/Rect2/OverlapRect/CutY/命中区"就没兑现。
    /// </summary>
    private void DrawAlignmentOverlay(System.Drawing.Graphics g, CanvasLayout layout, Seam seam)
    {
        if (!layout.UsesCropPlacement || (!ShowCompareOverlay && !ShowDebugHud)) return;

        // ═══ 换算只做一次：设备矩形进缓存 ═══
        // 需求三明确"不许每个 OnPaint 都重算全部矩形"，而叠层那几块矩形的<b>画布</b>坐标
        // 只在视口、缩放、这条缝的落位（对齐 / 裁切 / 带）变化时才动；把六个设备矩形连同
        // 箭头位置缓存下来，静止时每帧就只剩几条 GDI 调用。
        EnsureHudRects(layout);
        HudRects r = _hudRects;
        if (!r.HasSeam || r.SeamId != seam.Id) return;
        // 走到这里就是"这一帧真的进了色块层"（早退的两条路都不算）。反证那一格要靠它分清
        // "没进这一层"与"进了但一笔都没下"——后者才是裁到可见区的效果。
        OverlayLayerFrames++;

        // 两段各自的"有效区域"边框：绿=图1、黄=图2。只画框不填充，免得盖住正文看不清对齐。
        // 这两笔与读数层那笔同一种形状：Rect1/Rect2 是两张图各自的设备矩形，成品几千行时
        // 它们也有几千上万像素高。**为什么这里也要单独计时**：这一层是产品默认态（重影对比），
        // 它的钱以前只有一个"整层 3.8ms/帧"的差分总数（`--no-compare` 量出来的），
        // 分不清是描边贵还是那一下半透明填充贵——不拆开就没法说该裁哪一笔。
        // 【第二十七改】这三笔也要先裁到可见区——与读数层那几笔（VisiblePart）同一个规矩。
        // 为什么以前漏了这里：Rect1/Rect2/Band 走的是缓存好的<b>设备</b>矩形，看着像"已经算好了"，
        // 但拖对齐时重叠带可以有几万行，CanvasToDevice 之后照样是几万设备像素高的一条；
        // FillRectangle 那半透明填充沿整条屏幕外路径铺、虚线框沿整条路径排 dashes。
        // 真窗口现量（进程累计）：重叠带填充＋描边 127~206ms／趟，是色块层里最大那一笔。
        // 裁之后屏上<b>一个像素都不变</b>（填充本来就看不见屏幕外那段；框线的笔宽只有 1~2，
        // 外扩半个笔宽＋1 保证压在边界上那一圈照样露出来），唯一代价是虚线相位平移——与第十改那一笔交易双方相同。
        // 这一帧真的下笔了没有：箭头与两种画法共用同一份 op 序列，所以这里只负责"按可见区裁过的三笔"。
        DrawColorOps(g, r, r.View, 0, 0, counted: true);

        // 红色箭头：带首一条向下、带尾一条向上，表示"这两张图可以沿上下方向挪来对对齐"。
        // 位置与命中区同源（都来自缓存里那一个 Band），所以不会出现"看得见却点不到"——
        // 那正是需求三与需求九合在一起最容易做错的地方。带太窄太矮就整个不画（点不准不如不画）。
        DrawColorArrows(g, r, 0, 0);
    }

    /// <summary>
    /// 色块层那两支箭头（与上面那三笔合起来才是"这一层的全部"）。
    /// 第三十改把它与那三笔一起收出来，是为了让判据能拿<b>整层</b>做对照：
    /// 只搬三笔、把箭头留在屏上直接画，两条路的差别里就掺进了"少画了一层"，量的还是不是合成方式就说不清了。
    /// </summary>
    private void DrawColorArrows(System.Drawing.Graphics g, HudRects r, int offX, int offY)
    {
        if (!r.HasArrows) return;
        DrawArrowHead(g, HudArrowPen, r.ArrowX + offX, r.ArrowTopY + offY, +1, HudArrowHead);
        DrawArrowHead(g, HudArrowPen, r.ArrowX + offX, r.ArrowBottomY + offY, -1, HudArrowHead);
        ColorArrowsDrawnForTest += 2;
    }

    /// <summary>
    /// 色块层那三笔（绿框、黄框、重叠带的半透明填充＋虚线框）唯一的出处。
    ///
    /// ═══ 为什么第三十改要把它们收进一个方法 ═══
    /// 需求三第 3 条建议"颜色区域、文字面板、设备矩形等调试内容使用<b>缓存位图</b>"。
    /// 这一层的<em>矩形</em>早就缓存了（<see cref="EnsureHudRects"/>），要验的是<em>像素</em>要不要再缓存一层：
    /// 也就是"先把这几笔画进一张可见区大小的透明位图，再一次 <c>DrawImage</c> 贴上去"。
    /// 这条路<b>不是不能实现</b>，而是<b>必须能证明它与直接画法在屏上等价</b>才有资格换——
    /// 所以两条画法共用这一份 op 序列，差别只剩"分几次合成"。任何一份 op 序列被写第二遍，
    /// 这条判据就变成在比两份不同的代码，量的就不再是合成成本。
    /// </summary>
    /// <param name="view">可见区（<b>设备</b>坐标）：裁不裁都按它判，两条路必须裁到同一个矩形。</param>
    /// <param name="offX">下笔前给矩形加的横向偏移。直接画法是 0；画进位图那一头是 <c>-view.X</c>，
    /// 因为那张层以可见区左上角为原点。</param>
    /// <param name="counted">是否记产品那三本账（毫秒与 <see cref="OverlayOutlinesClamped"/> 等）。
    /// 判据拿同一段代码做对照时必须传 false，否则量一次两条路就把产品的计数器顶脏。</param>
    private void DrawColorOps(System.Drawing.Graphics g, HudRects r, Rectangle view,
        int offX, int offY, bool counted)
    {
        // 每次调用都从 0 起算，让"这一次下了几笔"成为可核对的读数而不是累计。
        // 两个计数都在这里归零：这一方法是两条画法各自的<b>第一个</b>动作，箭头紧随其后。
        ColorOpsDrawnForTest = 0;
        ColorArrowsDrawnForTest = 0;
        long strokeStart = counted ? StageTimestamp() : 0L;
        bool anyStroke = false;
        if (VisiblePart(view, r.Rect1, HudBeforePen.Width) is Rectangle clip1)
        {
            g.DrawRectangle(HudBeforePen, Shift(clip1, offX, offY));
            ColorOpsDrawnForTest++;
            if (counted) OverlayOutlinesClamped++;
            anyStroke = true;
        }
        if (VisiblePart(view, r.Rect2, HudAfterPen.Width) is Rectangle clip2)
        {
            g.DrawRectangle(HudAfterPen, Shift(clip2, offX, offY));
            ColorOpsDrawnForTest++;
            if (counted) OverlayOutlinesClamped++;
            anyStroke = true;
        }
        if (counted) StageAdd(ref _overlayStrokeMs, strokeStart);

        long fillStart = counted ? StageTimestamp() : 0L;
        if (VisiblePart(view, r.Band, HudBandPen.Width) is Rectangle clipBand)
        {
            Rectangle shifted = Shift(clipBand, offX, offY);
            g.FillRectangle(HudBandFill, shifted);
            g.DrawRectangle(HudBandPen, shifted);
            ColorOpsDrawnForTest++;
            if (counted)
            {
                OverlayOutlinesClamped++;
                // 记下<b>未偏移</b>的那个矩形：判据 <c>canvas/色块层那三笔大矩形也要先裁到可见区</c>
                // 拿它和判据自己独立算出的相交比，产品口径一直是设备坐标。
                LastOverlayBandDrawForTest = clipBand;
            }
        }
        else if (counted)
        {
            // 带整条在屏外：这一帧一笔都没下，读数留空当作"没画"而不是"画了个 0×0"
            LastOverlayBandDrawForTest = Rectangle.Empty;
        }
        if (counted)
        {
            LastOverlayStrokeDrewForTest = anyStroke;
            StageAdd(ref _overlayFillMs, fillStart);
        }
    }

    /// <summary>把已裁好的设备矩形平移到目标画布的坐标系（画进缓存位图那一条路用）。</summary>
    private static Rectangle Shift(Rectangle r, int dx, int dy) => new(r.X + dx, r.Y + dy, r.Width, r.Height);

    /// <summary>
    /// 判据专用（第三十改）：把色块层按<b>两种画法之一</b>画到调用方给的画布上，一毫秒也不改产品状态。
    /// · <paramref name="viaLayer"/> = false：与产品这一帧走完全同一条路（直接下笔）。
    /// · true：先画进一张与可见区同尺寸的 32 位 ARGB 层，再一次 <c>DrawImage</c> 合成。
    /// 两条路共用 <see cref="DrawColorOps"/>，所以这两帧之间的<b>全部</b>差别就是需求三第 3 条要买的那个东西。
    /// 依赖上一次渲染把 <see cref="_hudRects"/> 焐热：判据先 <c>Render()</c> 一次再调这里，
    /// 这样两条路读的是<b>同一份</b>缓存矩形（若各算一遍矩形，比的就不是合成方式了）。
    /// </summary>
    public void DrawColorLayerIntoForTest(System.Drawing.Graphics g, bool viaLayer)
    {
        HudRects r = _hudRects;
        if (!r.HasSeam)
        {
            ColorLayerBlitsForTest = 0;
            return;
        }
        ColorLayerBlitsForTest = 0;
        Rectangle view = r.View;
        if (!viaLayer)
        {
            DrawColorOps(g, r, view, 0, 0, counted: false);
            DrawColorArrows(g, r, 0, 0);
            return;
        }
        // 层的尺寸只取<b>可见区</b>——这正是需求三第 3 条"调试内容使用缓存位图"唯一能兑现的形状：
        // 层比可见区大的话，省下的那几笔会被一次更大的合成吃掉。
        using var layer = new GdiBitmap(view.Width, view.Height,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (System.Drawing.Graphics lg = System.Drawing.Graphics.FromImage(layer))
        {
            DrawColorOps(lg, r, view, -view.X, -view.Y, counted: false);
            DrawColorArrows(lg, r, -view.X, -view.Y);
        }
        g.DrawImage(layer, view.X, view.Y);
        ColorLayerBlitsForTest = 1;
    }

    /// <summary>
    /// 叠层那几块<b>设备</b>矩形的缓存：签名就是需求三点名"只有这些变化才重绘"那一串
    /// （Viewport.TopLeft、Zoom、对齐、CutY、OverlapRect，再加控件尺寸与选中哪条缝）。
    /// 签名一字不变就不重算；<see cref="HudGeometryRebuilds"/> 是给判据量的——
    /// 静止画 20 帧只该重算一次，改成每帧重算立刻翻红，不靠读代码保证。
    /// 「重影对比」层与 F8 读数层共用这一份缓存（同一批换算、同一批颜色，不许各写一套）。
    /// </summary>
    private void EnsureHudRects(CanvasLayout layout)
    {
        Seam? seam = SelectedSeam();
        // 签名用<b>参数</b>而不是算好的矩形：落位是这些量的纯函数，签名没变就不必再问布局
        // ——顺带把 DescribeSeam 那一次对象分配也从每帧省掉了（需求三第 5 条"每帧不许新建"）。
        var signature = new HudSignature(
            Viewport.TopLeft.X, Viewport.TopLeft.Y, (int)Math.Round(Viewport.Zoom * 10000),
            ClientSize.Width, ClientSize.Height, _project.Revision, seam?.Id ?? "",
            seam?.AlignmentOffset.X ?? 0, seam?.AlignmentOffset.Y ?? 0,
            seam?.CutY ?? 0, seam?.OverlapLength ?? 0, seam?.Fixed ?? FixedSide.Before);
        // 拖动预览那一帧<b>不许</b>走缓存：几何来自临时覆盖值、参数还没写，光看签名看不出"变了"。
        // 宁可重算（正确性优先；预览帧本来就在重画瓦片，这一次换算不在瓶颈上）。
        bool cacheable = ReferenceEquals(layout, _project.Layout);
        if (cacheable && _hudRects.Valid && signature == _hudSignature) return;
        long geometryStart = StageTimestamp();   // 只量"真的重算了"那一次，命中缓存那一趟不计时

        _hudSignature = signature;
        SeamGeometry? geo = seam is not null && layout.UsesCropPlacement &&
            layout.DescribeSeam(seam) is SeamGeometry g && g.HasBand ? g : null;
        HudRects rects = new()
        {
            Valid = true,
            CanvasBounds = Viewport.CanvasToDevice(layout.CanvasBounds),
            View = Viewport.CanvasToDevice(Viewport.VisibleCanvas()),
        };
        if (geo is not null && seam is not null)
        {
            Rectangle band = Viewport.CanvasToDevice(geo.OverlapRect);
            int cutY = (int)Viewport.CanvasToDeviceF(new IntPoint(0, geo.CutYCanvas)).Y;
            rects.HasSeam = true;
            rects.SeamId = seam.Id;
            rects.Rect1 = Viewport.CanvasToDevice(geo.Rect1);
            rects.Rect2 = Viewport.CanvasToDevice(geo.Rect2);
            rects.Band = band;
            rects.CutZone = new Rectangle(band.X, cutY - SeamHitTolerancePx,
                band.Width, SeamHitTolerancePx * 2);
            rects.HandleTop = new Rectangle(band.X, band.Y, band.Width, AlignmentHandleDevicePx);
            rects.HandleBottom = new Rectangle(band.X, band.Bottom - AlignmentHandleDevicePx,
                band.Width, AlignmentHandleDevicePx);
            rects.ArrowX = band.X + band.Width / 2;
            rects.ArrowTopY = band.Y + 2;
            rects.ArrowBottomY = band.Bottom - 2;
            // 带太窄太矮就不画箭头：点不准的地方画了反而误导（与命中区那格判据同口径）
            rects.HasArrows = band.Width >= 8 && band.Height >= 4;
        }
        _hudRects = rects;
        _hudGeo = geo;
        _hudSeam = seam;
        HudGeometryRebuilds++;
        _hudGeometryMs += (StageTimestamp() - geometryStart) * 1000.0
            / System.Diagnostics.Stopwatch.Frequency;
    }

    /// <summary>
    /// 叠层几何换算累计花掉的毫秒。为什么要单独记：拖动预览那几帧走的是<b>不缓存</b>那条分支
    /// （临时布局的落位来自还没写进参数的覆盖值，签名看不出来），一帧要换算两遍
    /// （色块层一遍、读数层一遍）。"这一次换算不在瓶颈上"这句以前只是散文里的断言，
    /// 有了这个数才能拿读数说它占整段的几分之一。
    /// </summary>
    public double HudGeometryRebuildMs => _hudGeometryMs;
    private double _hudGeometryMs;

    /// <summary>缓存里那一条缝的公共坐标与那条缝本身（读数文本与叠层共用，一帧只算一次）。</summary>
    private SeamGeometry? _hudGeo;
    private Seam? _hudSeam;

    /// <summary>叠层缓存的换算签名——需求三点名的那几个量全在这里（含修订号：撤销/重做之后必然重算）。</summary>
    private readonly record struct HudSignature(int Tlx, int Tly, int ZoomBits, int Width, int Height,
        int Revision, string SeamId, int AlignX, int AlignY, int CutY, int Overlap, FixedSide Fixed);

    /// <summary>换算好一次的叠层设备矩形（画布边界 / 视口 / 绿黄白三块 / 命中区三块 / 箭头）。</summary>
    private struct HudRects
    {
        public bool Valid;
        public bool HasSeam;
        public string SeamId;
        public Rectangle CanvasBounds;
        public Rectangle View;
        public Rectangle Rect1;
        public Rectangle Rect2;
        public Rectangle Band;
        public Rectangle CutZone;
        public Rectangle HandleTop;
        public Rectangle HandleBottom;
        public int ArrowX;
        public int ArrowTopY;
        public int ArrowBottomY;
        public bool HasArrows;
    }

    private HudRects _hudRects;
    private HudSignature _hudSignature;

    /// <summary>叠层换算重算过多少次（判据量的是"静止时不重算"）。</summary>
    public long HudGeometryRebuilds { get; private set; }

    /// <summary>读数面板位图重建过多少次（判据量的是"≤10Hz 且文本没变就不重建"）。</summary>
    public long HudPanelRebuilds { get; private set; }

    /// <summary>
    /// 为了画读数面板，<b>真的分配过几张位图</b>（不是"重建了几次"）。
    /// 这两个数必须分开看：面板尺寸没变时，重画字（清底 + 一次文本输出）不必再要一张新缓冲区，
    /// 而这张约 600×200 的 32 位位图有半 MB 以上，已超过 85KB 的大对象门槛——
    /// 每次重建都新分配一张就是每秒往大对象堆里扔几 MB，代价不体现在这一次重画上，
    /// 而是体现在随后那次回收把整帧顶到几十毫秒。
    /// 判据：<c>canvas/面板重画复用同一张缓冲区 重建多次但分配只涨必要那几次</c>，
    /// 反证：<c>guard/面板分配计数分得清复用与每次重建都分配</c>。
    /// </summary>
    public long HudPanelAllocations { get; private set; }

    /// <summary>
    /// 面板缓冲区当前那张的长宽。<b>只给判据核对"这一趟重画有没有跨档"</b>：
    /// 跨档才会名正言顺地重新分配，不跨档还分配就是浪费——判据要把这两种情况分开判，
    /// 不然"允许跨档那一次"与"每次都分配"这两件事在这一个数里混成了同一种读数。
    /// </summary>
    public (int Width, int Height) HudPanelSizeForTest => (_hudPanelWidth, _hudPanelHeight);

    /// <summary>「不得不分配新缓冲区」那几次重建累计花掉的毫秒（每包必含一次大对象投放）。</summary>
    public double HudPanelAllocRebuildMs { get; private set; }

    /// <summary>「复用同一张缓冲区」那几次重建累计花掉的毫秒（只剩清底与一次文本输出）。</summary>
    public double HudPanelReuseRebuildMs { get; private set; }

    private static void DrawArrowHead(System.Drawing.Graphics g, Pen pen, int x, int y, int dir, int size)
    {
        g.DrawLine(pen, x, y, x, y + size * dir);
        g.DrawLine(pen, x, y + size * dir, x - size / 2, y + size * dir - size * dir);
        g.DrawLine(pen, x, y + size * dir, x + size / 2, y + size * dir - size * dir);
    }

    /// <summary>
    /// 【第十三轮需求六：调试读数层，只画在屏幕上，绝不进出成品】
    /// 把"这一帧到底是怎么算出来的"直接写在窗口里，并把<b>当前真的可点</b>的三块命中区域圈出来：
    /// <list type="bullet">
    ///   <item>灰虚线 = 画布/视口边界（<c>Viewport.TopLeft</c> 落点这一眼就能核对）；</item>
    ///   <item>紫虚线 = 命中区三段：整条带（拖=对齐）、蓝线上下各 <see cref="SeamHitTolerancePx"/> 像素（拖=蓝线）、
    ///         带首尾各 <see cref="AlignmentHandleDevicePx"/> 像素（红箭头把手）；</item>
    ///   <item>左上角读数 = Rect1 / Rect2 / OverlapRect / CutY 与比例 / AlignmentOffset /
    ///         视口原点 / 缩放 / 固定侧 / 瓦片数 / 渲染·整块清·命中率 / 帧耗时（最近·最大·P95）/ FPS / Paint / 鼠标事件。</item>
    /// </list>
    /// 绿框=图1、黄框=图2、白半透明=重叠带、蓝线=CutY、红箭头=可拖对齐区，这几样由
    /// <see cref="DrawAlignmentOverlay"/> 画（本层与它叠在一起才是需求六那一整张图）。
    ///
    /// <b>为什么命中区也要画出来</b>：需求九那个真实缺陷就是"红框看得见、点不到"。
    /// 把命中矩形与显示矩形同屏圈出来，这类事不用读代码、一眼就能看出来。
    /// </summary>
    private void DrawDebugHud(System.Drawing.Graphics g, CanvasLayout layout)
    {
        // 这一层是"边拖边看"用的，所以它自己**不许成为卡顿的来源**（需求三）：
        // · 笔与刷子按进程生命周期建一次（每帧 new Pen/Brush 是纯浪费）；
        // · 设备矩形走 <see cref="EnsureHudRects"/> 的换算缓存（签名不变不重算）；
        // · 读数文字（现量：选中一条缝 14 行、没选中拼缝 7 行）与面板位图只在"≥100ms 且文本真的变了"
        //   时才重排重画一次，其余帧只贴缓存；
        // · 一帧都不许调 RenderWhole / 完整 CanvasLayout.Build / 全量清缓存（常驻判据量这三个计数）。
        EnsureHudRects(layout);
        HudRects r = _hudRects;
        // 下面这几笔"每帧都做的描边"与"每帧一次的贴图"各自计时，是为了让"读数层那几毫秒花在哪"
        // 有数可对：以前只有整段 HUD 的耗时，剩下的钱只能靠散文猜（精度红线那条"归因要么有差分读数"）。
        long outlineStart = StageTimestamp();
        // ① 画布边界（灰虚线）与<b>视口当前可见范围</b>（灰实线）——需求六那句"Viewport 边界：灰色"。
        //    这几笔**必须先裁到可见区**：画布边界那条矩形在设备坐标里可以有几万画素长
        //    （成品高 41532 行、缩放 0.35 也有 14537 像素），而屏上只看得到其中一屏。
        //    虚线要沿<b>整条路径</b>排相位，GDI+ 不会因为屏幕裁剪就跳过看不见的部分——
        //    这一笔实测占了读数层整段的八成多（每帧约 7ms，见 --gui 的「读数层拆账」那行）。
        //    第十改：①与③各计一笔。"描边"整笔在真窗口里已经是读数层最大那一块
        //    （每趟 6.5~8.9ms／整段 11~15ms），但它仍然说不清是<b>永远要画</b>的边界两笔花的
        //    还是<b>只有选中缝才画</b>的命中区四笔花的——这两笔的下法完全不同，不能靠散文挑一个。
        long borderStart = StageTimestamp();
        Rectangle vis = new(0, 0, ClientSize.Width, ClientSize.Height);
        if (VisiblePart(vis, r.CanvasBounds, HudDashPen.Width) is Rectangle bounds)
            g.DrawRectangle(HudDashPen, bounds);
        if (VisiblePart(vis, r.View, HudViewPen.Width) is Rectangle view)
            g.DrawRectangle(HudViewPen, view);
        HudOutlinesClamped++;     // 走到这里就说明这一帧那两笔大矩形是按可见区裁着画的
        StageAdd(ref _hudBorderMs, borderStart);

        // ② 需求六点名的颜色层（绿/黄/白/红箭头）由 <see cref="DrawAlignmentOverlay"/> 画——
        //    它读的就是同一份缓存，两个开关都开也不会画两遍（第十三轮需求六那条"共用同一份画法"）。
        // ③ 紫色那三块是"当前真的可点"的命中区：整条带 / 蓝线上下容差 / 带首尾把手。
        long hitsStart = StageTimestamp();
        if (r.HasSeam)
        {
            if (VisiblePart(vis, r.Band, HudHitPen.Width) is Rectangle band)
            {
                g.DrawRectangle(HudHitPen, band);
                HudHitRectsDrawn++;
            }
            if (VisiblePart(vis, r.CutZone, HudHitPen.Width) is Rectangle cut)
            {
                g.DrawRectangle(HudHitPen, cut);
                HudHitRectsDrawn++;
            }
            if (VisiblePart(vis, r.HandleTop, HudHitPen.Width) is Rectangle top)
            {
                g.DrawRectangle(HudHitPen, top);
                HudHitRectsDrawn++;
            }
            if (VisiblePart(vis, r.HandleBottom, HudHitPen.Width) is Rectangle bottom)
            {
                g.DrawRectangle(HudHitPen, bottom);
                HudHitRectsDrawn++;
            }
        }
        StageAdd(ref _hudHitsMs, hitsStart);
        StageAdd(ref _hudOutlineMs, outlineStart);

        // 读数文本与叠层共用缓存里那一条缝的公共坐标：一帧之内不第二次问布局
        Seam? seam = _hudSeam;
        SeamGeometry? geo = _hudGeo;
        const int pad = 6;
        long now = Environment.TickCount64;
        if (_hudPanel is null || now - _hudPanelBuiltAt >= HudRefreshMs)
        {
            long textStart = StageTimestamp();
            string text = BuildHudText(layout, seam, geo, r);
            StageAdd(ref _hudTextBuildMs, textStart);   // 拼那段读数（现量 14 行）这一步每次都做，单独记一笔
            // 文本没变就不重建：原来每 100ms 无条件重排一次，一秒钟里有 1/5 的帧白白多背一次文本布局，
            // 静止看数那一档尤其浪费（需求三点名的"数字刷新限到 5–10Hz"是上限，不是必须每次都刷）。
            if (_hudPanel is null || text != _hudText)
            {
                long rebuildStart = StageTimestamp();
                _hudText = text;
                Size extent = HudTextExtent(text);   // 宽高一次量完，两个尺寸都从这同一个读数派生
                int w = HudPanelWidth(extent), h = HudPanelHeight(extent);
                // 尺寸没变就**复用那张缓冲区**（清底 + 重画字），只有第一次或长宽真的变了才分配新位图。
                // 这条区分只能靠计数看出来：两条路的像素结果一样，但每次重建都分配一张半 MB 以上的
                // 32 位位图 = 每秒最多 10 次大对象堆投放，代价落在随后那次回收顶起来的帧上。
                // 判据：<c>canvas/面板重画复用同一张缓冲区 重建多次但分配只涨必要那几次</c>，
                // 反证：<c>guard/面板分配计数分得清复用与每次重建都分配</c>。
                bool allocated = _hudPanel is null || w != _hudPanelWidth || h != _hudPanelHeight;
                if (allocated)
                {
                    _hudPanel?.Dispose();
                    _hudPanel = BuildHudPanel(text, w, h);
                    _hudPanelWidth = w;
                    _hudPanelHeight = h;
                    // 这张新缓冲区是<b>刚按整块画法把整段文字画进去的</b>，所以行级的比较基准要一起换成这一帧的行；
                    // （若在这里留 null，下一次重画就白付一次整块重排——省的那一路压根走不到。）
                    _hudLines = text.Split('\n');
                    HudPanelAllocations++;
                }
                else PaintHudPanel(text);
                _hudPanelBuiltAt = now;
                HudPanelRebuilds++;
                double ms = (StageTimestamp() - rebuildStart) * 1000.0
                    / System.Diagnostics.Stopwatch.Frequency;
                if (allocated) HudPanelAllocRebuildMs += ms; else HudPanelReuseRebuildMs += ms;
            }
        }
        if (_hudPanel is not null)
        {
            // 这里原来写的是 `g.DrawImage(_hudPanel, pad, pad, _hudPanelWidth, _hudPanelHeight)`——
            // 目标尺寸与源尺寸**完全相同**，却调了带尺寸的 DrawImage 重载：那一族重载一律走缩放管线，
            // 而本控件的 InterpolationMode 拖动中是 Bilinear、静止是 HighQualityBilinear，
            // 于是每帧都把这张约 600×250 的面板按双线性/高质量双三次**重采样一遍**，
            // 重采样的结果与源像素本来就该一模一样（比例 1:1），那几毫秒是白花的。
            // DrawImageUnscaled 走的是不缩放的位图拷贝，像素不变而代价只剩一次 blit。
            // 判据：<c>canvas/面板位图每帧只走一次不缩放贴图 贴几帧就计几</c>，
            // 反证：<c>guard/面板贴图计数分得清不缩放与重采样两种贴法</c>。
            long blitStart = StageTimestamp();
            g.DrawImageUnscaled(_hudPanel, pad, pad);
            HudPanelUnscaledBlits++;
            StageAdd(ref _hudBlitMs, blitStart);
        }
    }

    /// <summary>
    /// 读数层这一段里"每帧都做"的四笔，各量各的累计毫秒（都是进程累计，工装一次、判据读一次）：
    /// <list type="bullet">
    ///   <item><see cref="HudGeometryRebuildMs"/>：叠层几何换算（拖动预览一帧要算两遍，色块层一遍、读数层一遍）；</item>
    ///   <item><see cref="HudTextBuildMs"/>：拼那段读数字符串（现量 14 行）（每个 100ms 窗口一次，不管最后重不重画）；</item>
    ///   <item><see cref="HudOutlineMs"/>：那几笔虚线/实线描边（画布边界那个矩形有上万像素周长）；</item>
    ///   <item><see cref="HudBorderOutlineMs"/>／<see cref="HudHitsOutlineMs"/>：把上面那一笔再劈两半——
    ///         "永远要画"的边界两笔与"只有选中缝才画"的命中区四笔（第十改；两半加起来必须不超过整笔）；</item>
    ///   <item><see cref="HudBlitMs"/>：把面板位图贴到屏上那一次（约 600×230 带 alpha）。</item>
    /// </list>
    /// 加上面板重建那两条（<see cref="HudPanelAllocRebuildMs"/>／<see cref="HudPanelReuseRebuildMs"/>），
    /// <b>"读数层整段 8ms"这句话就有了可以对账的拆法</b>：这几项加起来若明显小于整段，
    /// 就说明剩下的钱在计时器读不到地方（GDI+ 的延迟提交、或者干脆是被系统切走）。
    /// </summary>
    public double HudOutlineMs => _hudOutlineMs;

    /// <summary>描边里"永远要画"那一半：画布边界（灰虚线）＋视口范围（灰实线）两笔，裁到可见区后画。</summary>
    public double HudBorderOutlineMs => _hudBorderMs;

    /// <summary>
    /// 描边里"只有选中缝才画"那一半：整条带／蓝线容差／首尾把手最多四笔紫虚线矩形。
    /// 这一笔与 <see cref="HudHitRectsDrawn"/> 一起看：没选缝时两者都不许涨（第十改的下手处就在这四笔）。
    /// </summary>
    public double HudHitsOutlineMs => _hudHitsMs;

    /// <summary>命中区矩形<b>真的画出去</b>了几笔（裁到可见区后为 null 的那些不计）。</summary>
    public int HudHitRectsDrawn { get; private set; }

    public double HudBlitMs => _hudBlitMs;
    public double HudTextBuildMs => _hudTextBuildMs;
    private double _hudOutlineMs, _hudBlitMs, _hudTextBuildMs, _hudBorderMs, _hudHitsMs;

    /// <summary>
    /// 重影对比层（产品默认开着，不属于需求三那笔账）自己的两段累计毫秒：
    /// <c>OverlayStrokeMs</c> 是绿/黄那两张图的设备矩形边框，<c>OverlayFillMs</c> 是白色半透明带
    /// 与它的边框。整层的钱先前只量到一个差分总数（`--gui` 的 `--no-compare` 两档相减），
    /// 拆到这两笔才知道该不该照读数层那样把描边裁到可见区。
    /// </summary>
    public double OverlayStrokeMs => _overlayStrokeMs;
    public double OverlayFillMs => _overlayFillMs;
    private double _overlayStrokeMs, _overlayFillMs;

    /// <summary>
    /// 色块层这几笔<b>真的裁过可见区并且下了笔</b>的次数（每帧最多 3：两张图的框＋重叠带那组）。
    /// 与机器无关的计数——判"有没有裁"不看屏（屏幕外那几万行本来就看不见），也不看毫秒（批间噪声 25ms）。
    /// 判据：<c>canvas/色块层那三笔大矩形也要先裁到可见区</c>，
    /// 反证：<c>guard/带整条在屏外时色块层那一组一笔都不许下</c>。
    /// </summary>
    public long OverlayOutlinesClamped { get; private set; }

    /// <summary>走到色块层"已经拿到缓存矩形、准备下笔"那一步的帧数（早退那两条路不算）。</summary>
    public long OverlayLayerFrames { get; private set; }

    /// <summary>这一帧重叠带那一组实际画到的矩形；整条在屏外时是空矩形（＝一笔都没下，不是"画了个 0×0"）。</summary>
    public Rectangle LastOverlayBandDrawForTest { get; private set; }

    /// <summary>这一帧那两张图的设备矩形框有没有至少下一笔（带在屏外时它可能仍在屏上）。</summary>
    public bool LastOverlayStrokeDrewForTest { get; private set; }

    /// <summary>
    /// <see cref="DrawColorOps"/> 这<b>一次调用</b>实际下笔的笔数（两条画法都记，与产品计数器互不干扰）。
    /// 第三十改要靠它把"两条路做的是同一件活"钉成与机器无关的读数：若缓存位图那条路少下了笔，
    /// 两帧"像素相同"就只是因为其中一帧压根没画。每次调用开头归零，所以这不是累计。
    /// 判据：<c>canvas/色块层换成缓存位图会改屏上像素</c>。
    /// </summary>
    public int ColorOpsDrawnForTest { get; private set; }

    /// <summary>同上，记色块层那两支箭头（整层对照必须连箭头一起比，否则差里掺了"少画一层"）。</summary>
    public int ColorArrowsDrawnForTest { get; private set; }

    /// <summary>上一次调 <see cref="DrawColorLayerIntoForTest"/> 做了几次<b>整层合成</b>（直接画法 0、缓存位图 1）。</summary>
    public int ColorLayerBlitsForTest { get; private set; }

    /// <summary>
    /// 读数层的描边这一帧真的按可见区裁过（每帧一次）。
    /// 这个计数钉的是"那几笔大矩形不许沿整条屏幕外路径排虚线"这件事本身：
    /// 谁把 <see cref="VisiblePart"/> 去掉、改回直接画 <c>r.CanvasBounds</c>，
    /// 屏上看不出区别（屏幕外那几万像素本来就看不见），只有这个计数会归零。
    /// 判据：<c>canvas/超出屏幕那几笔描边必须先裁到可见区 裁过才算数</c>，
    /// 反证：<c>guard/描边若不裁到可见区则虚线路径代价翻红</c>。
    /// </summary>
    public long HudOutlinesClamped { get; private set; }

    /// <summary>
    /// 取矩形落在可见区里的那一段（各边外扩半个笔宽 +1，保证屏上那一圈的像素与裁之前一致：
    /// 压在边界上的那条线，笔宽有一半本来就该露出来）。整条都在屏幕外就返回 null，一笔都不下。
    ///
    /// 裁之后<b>虚线的相位会平移</b>：相位沿路径从头量，路径变短，看得见那一段里第几根 dashes 就变了。
    /// 这层是给人看的调试框（灰虚线一条），相位平移不影响它指认任何东西，
    /// 也不碰对齐、蓝线、命中区与导出（那些读的是同一份缓存矩形，与本函数无关）。
    /// 拿这笔换每帧约 7 毫秒，是这笔交易的双方。
    /// </summary>
    private static Rectangle? VisiblePart(Rectangle visible, Rectangle r, float penWidth)
    {
        int pad = (int)System.Math.Ceiling(penWidth / 2f) + 1;
        Rectangle hit = Rectangle.Intersect(Rectangle.Inflate(visible, pad, pad), r);
        return hit is { Width: > 0, Height: > 0 } ? hit : null;
    }

    /// <summary>
    /// 仅供判据：把 <see cref="VisiblePart"/> 这条裁法原样暴露出来量语义
    /// （大矩形裁完确实短了、整条在屏幕外的返回 null）。产品通路走的还是私有那一份，没有第二套规则。
    /// </summary>
    public static Rectangle? ClipOverlayForTest(Rectangle visible, Rectangle r, float penWidth) =>
        VisiblePart(visible, r, penWidth);

    /// <summary>
    /// 不缩放贴图走了几次。**只有工装与判据读**（与 <see cref="HudPanelRebuilds"/> 同一地位）。
    /// 它是"面板这一帧真的贴过、且贴的是不缩放那条路"的可见证据：
    /// 谁把这里改回 `DrawImage(bmp, x, y, w, h)`，这个计数就不再增长，判据立刻红——
    /// 光看屏幕像素是看不出区别的（1:1 重采样与直接 blit 结果相同），只能靠计数钉住代价本身。
    /// 判据：<c>canvas/面板位图每帧只走一次不缩放贴图 贴几帧就计几</c>。
    /// </summary>
    public long HudPanelUnscaledBlits { get; private set; }

    /// <summary>
    /// 仅供 `guard/` 那一格使用：把同一张面板按**旧写法**（带目标尺寸的 DrawImage）贴一次，
    /// 用来证明上面那个计数分得清两种贴法——它不是产品通路，产品里任何一帧都不该走到这里。
    /// </summary>
    public void BlitHudPanelResampledForTest(System.Drawing.Graphics g)
    {
        if (_hudPanel is not null)
            g.DrawImage(_hudPanel, 6, 6, _hudPanelWidth, _hudPanelHeight);
    }

    /// <summary>
    /// 仅供 `guard/` 那一格使用：按<b>改造前的写法</b>重画一次面板——每次都先把旧的释放掉再分配一张新的，
    /// 哪怕尺寸一模一样。用它证明 <see cref="HudPanelAllocations"/> 真的分得清"复用缓冲区"与
    /// "每次重建都分配"两种做法；它不是产品通路，产品里任何一帧都不该走到这里。
    /// </summary>
    public void RebuildHudPanelByAllocatingForTest()
    {
        _hudPanel?.Dispose();
        _hudPanel = BuildHudPanel(_hudText, _hudPanelWidth, _hudPanelHeight);
        HudPanelAllocations++;
        HudPanelRebuilds++;
    }

    /// <summary>
    /// 仅供判据：把面板缓冲区<b>此刻的内容</b>原样复制一份出去。
    /// 判据要用它证一件事——"复用同一张缓冲区"不等于"留着上一帧的字"：
    /// 两次重画之间这张缓冲区的像素必须真的变过。拿屏上整帧比做不到这件事（底下还有画布内容在动），
    /// 只能量这张缓冲区自己。
    /// </summary>
    public System.Drawing.Bitmap? HudPanelSnapshotForTest() => _hudPanel?.Clone() as System.Drawing.Bitmap;

    /// <summary>面板那些读数的文本（现量：选中一条缝 14 行、没选中拼缝 7 行）。只在面板真要重建时才拼——
    /// 字符串本身就是每帧开销的大头。</summary>
    private string BuildHudText(CanvasLayout layout, Seam? seam, SeamGeometry? geo, HudRects r)
    {
        var lines = new List<string>();
        // 需求十一那批"必须能当场读出来"的量：公共坐标 + 屏幕坐标 + 命中区 + 帧耗时。
        // 与日志 <c>seam.*.commit</c> 读的是同一批字段名，屏幕上的数与日志里的数不可能各说一套。
        if (geo is not null && seam is not null)
        {
            lines.Add($"命中类型={(r.HasSeam ? _lastHitKindText : "无")} SeamId={seam.Id} 固定侧={seam.Fixed}");
            lines.Add($"Rect1=({geo.Rect1.X},{geo.Rect1.Y},{geo.Rect1.Width},{geo.Rect1.Height}) 绿");
            lines.Add($"Rect2=({geo.Rect2.X},{geo.Rect2.Y},{geo.Rect2.Width},{geo.Rect2.Height}) 黄");
            lines.Add($"OverlapRect=({geo.OverlapRect.X},{geo.OverlapRect.Y},{geo.OverlapRect.Width},{geo.OverlapRect.Height}) 白");
            lines.Add($"CutY={geo.CutPosition}/{geo.Overlap} 比例={(geo.Overlap <= 0 ? 0 : (double)geo.CutPosition / geo.Overlap):F3} 画布行={geo.CutYCanvas}");
            // 两个数分开写，因为它们本来是两个概念（第七轮"原值 vs 生效值"那条规矩）：
            // · AlignmentOffset = <b>参数</b>里那一个数（以紧接为零点、负=压上去，= −重叠）；
            // · 落位Δ = 布局<b>真的</b>把 Rect2 摆在 Rect1 底边之前几行（裁切档里还要扣掉前段被裁掉的那一截）。
            // 只印后一个的话，屏上的数与日志里的 alignmentOffset 看着像对不上，正是最难查的那一类。
            lines.Add($"AlignmentOffset=({seam.AlignmentOffset.X},{seam.AlignmentOffset.Y}) " +
                      $"落位Δvs紧接=({geo.AlignmentOffset.X},{geo.AlignmentOffset.Y}) 固定侧={seam.Fixed}");
            lines.Add($"屏上矩形1={r.Rect1} 矩形2={r.Rect2}");
            lines.Add($"重叠带屏上={r.Band} 命中(紫)={r.CutZone}");
        }
        else
        {
            lines.Add(seam is null ? "命中类型=无 SeamId=无（没选中拼缝）" : "这条缝没有重叠带（紧接排列）");
        }
        lines.Add($"Viewport.TopLeft=({Viewport.TopLeft.X},{Viewport.TopLeft.Y}) zoom={Viewport.Zoom:F3} 画布高={layout.CanvasSize.Height}");
        lines.Add($"瓦片 缓存{_tiles.Count}块 渲染{_tiles.TileRenders} 整块清{_tiles.FullInvalidations} 命中率{CacheHitRate:P0}");
        lines.Add($"帧 最近{LastFrameMs:F1}ms P50 {FramePercentileMs(0.5):F1} P95 {FramePercentileMs(0.95):F1} 最大{MaxFrameMs:F1} FPS {Fps:F0}");
        var split = HudSplitStats;
        lines.Add($"HUD 单独 均{split.HudAvg:F1} P50 {split.HudP50:F1} P95 {split.HudP95:F1}｜画布 P95 {split.CanvasP95:F1}ms");
        // 需求六最后那句"卡顿发生在哪个阶段"：与 `--gui` 的「最慢那帧分阶段」共用同一套六笔口径。
        // 旧写法只报取数／棋盘／贴图三段——现量下来一帧里有近一半的耗时不在这三段里
        // （背景、重影对比层、拼缝线、补丁层、角标、点选引导线都没单独计时），
        // 于是这一行读起来就像"钱不见了"，人（和下一轮的 AI）只能猜。现在把账加满再给人看。
        //
        // 每个数字都钉成<b>定宽</b>：面板的宽度是按当前这行字的墨量量出来的，
        // 只要有一行的宽度跟着数字位数跳，缓冲区就得在帧与帧之间重新分配一次
        // ——判据 `canvas/面板重画复用同一张缓冲区 重建多次但分配只涨必要那几次` 就是被这个顶红的
        // （第一版没定宽，实测 608×202 → 672×202 来回跳）。定宽之后同尺寸跨帧一字不变。
        StageNumbers? st = LastStageNumbersForTest;
        lines.Add(st is null
            // 还没结算过一帧（读数层画在第一帧里，此刻只有这一种可能）：占位符与上面<b>同宽</b>，
            // 否则这一行的墨量会在第一帧与第二帧之间跳一次，面板缓冲区就得多分配一次。
            ? "分阶段 取    - 棋    - 贴    - 读    - 叠    - 前    -｜换  0 板  0 配  0"
            : $"分阶段 取{st.Fetch,5:F1} 棋{st.Checker,5:F1} 贴{st.Blit,5:F1} 读{st.Hud,5:F1}"
              + $" 叠{st.Overlay,5:F1} 前{st.Prologue,5:F1}"
              + $"｜换{HudGeometryRebuilds,3} 板{HudPanelRebuilds,3} 配{HudPanelAllocations,3}");
        lines.Add($"Paint {PaintCount} 鼠标 {MouseMoveCount} 预览帧 {PreviewFramesForTest} 修订 {_project.Revision}");
        return string.Join('\n', lines);
    }

    /// <summary>
    /// 面板宽度：<b>量出来的最长行 + 10</b>，再<b>向上取到 <see cref="HudPanelWidthStepPx"/> 的整数倍</b>。
    ///
    /// ═══ 为什么改成量（与 <see cref="HudPanelHeight"/> 是同一家族的第二个缺陷）═══
    /// 这里原来写的是「最长行的<b>字符数</b> × <see cref="HudCharWidth"/> + 10」，
    /// 而 <c>HudCharWidth</c> 是量 <c>"MMMMMMMMMM"</c> 得到的<b>等宽西文</b>单字宽。
    /// 读数里有大量中日韩全角字（"分阶段""命中率""（其中重新分配缓冲区 … 次）"），
    /// 它们走字体回退，<b>一个占两格</b>——于是"字符数 × 西文单字宽"系统性低估含全角字的行。
    /// <b>真窗口 150% 那一档实测</b>：未选中拼缝时那 7 行读数里，第 6 行
    /// 「分阶段 … 面板1（其中重新分配缓冲区 1 次）」共 50 个字符、其中 27 个全角，
    /// 屏上只画到「面板1（其中」就没了——后半句"重新分配缓冲区 1 次）"整段裁在框外，
    /// 需求三点名的"面板重建/分配次数"这项读数从屏上消失（剪贴板那份同源读数里它是完整的，
    /// 所以这不是数据没算出来，是<b>框比字窄</b>）。
    /// 电池为什么没抓到：那一格拿的是<b>选中一条缝</b>的 14 行文本，那里旧算法估到 579、
    /// 真实要 581——<b>只差 2 像素，被"向上取到 32 的整数倍"那一步（579→608）掩盖掉了</b>；
    /// 换成"没选中拼缝"这一档，估 395、真实 546，分档后 416 仍旧差 130 像素，判据当场就能红。
    /// 现在改成量，两档形状都装得下；判据
    /// <c>canvas/F8 面板的背景框必须装得下它自己那些读数</c> 也跟着把"没选中拼缝"那一档一起核，
    /// 反证 <c>guard/面板宽度若退回按字符数估则含全角字的行一定被截断</c>。
    /// 分档（向上取整到步长）保留，理由不变：拖动时数字位数会抖，逐像素贴合会让每次重建都跨档，
    /// "复用同一张缓冲区"就成空话；分档的代价写在 <see cref="HudPanelWidthStepPx"/> 那条注释里
    /// ——但代价之一是它<b>会掩盖"框只比字窄一点点"的估窄</b>，这一条记在这里。
    /// </summary>
    private static int HudPanelWidth(Size extent)
    {
        int exact = extent.Width + 10;
        return (exact + HudPanelWidthStepPx - 1) / HudPanelWidthStepPx * HudPanelWidthStepPx;
    }

    /// <summary>
    /// 面板该有多高：<b>量出来的整块文字高度 + 6</b>，不是「行数 × 一个写死的行距」。
    ///
    /// ═══ 为什么改成量 ═══
    /// 这里原来写的是 <c>(行数) * HudLineHeight + 6</c>，而 <c>HudLineHeight</c> 是常量 16——
    /// 那是 96 DPI 下 Consolas 9pt 的行距。面板位图里的字是按<b>进程 DPI</b> 排的
    /// （产品按系统主屏 DPI 渲染，见 Program.cs；150% 的主屏上就是 144），于是缩放一高，行距越过那个 16，
    /// 框就比字短：<b>真窗口 150% 那一档实测——选中一条缝时的 14 行读数只画得出前 10 行，
    /// 最后 4 行（帧耗时 / HUD 单独耗时 / 分阶段 / Paint 计数）整行不见了</b>，
    /// 需求四点名的「当前帧耗时」「P95 帧耗时」正是被切掉的那两行。
    /// 96 DPI 下反过来：实测每行只有 14 像素，那个 16 还宽裕（每行白留 2 像素，看不出问题）——
    /// 这就是电池 186 格全绿而缺陷一直活着的原因（Checks 进程没声明 DPI 感知，跑在 96 DPI）。
    /// 判据：<c>canvas/F8 面板的背景框必须装得下它自己那些读数</c>，
    /// 反证：<c>guard/面板行高若退回写死的 16 像素则在高分屏下切掉最后几行</c>。
    /// </summary>
    private static int HudPanelHeight(Size extent) => extent.Height + 6;

    /// <summary>
    /// 面板宽度的分档步长（像素）。步长越大，宽度跨档越少、重新分配越少，但背景框右侧的空白最多能多出步长减一。
    /// 32px ≈ 4 个等宽字符：拖动时数字位数抖动基本被吃掉，而多出来的这点底只有在读数正好卡在某一档边界时才看得见。
    /// </summary>
    private const int HudPanelWidthStepPx = 32;

    /// <summary>新分配一张面板缓冲区并把读数画进去（只有第一次与长宽真的变了才走到这里）。</summary>
    private static GdiBitmap BuildHudPanel(string text, int width, int height)
    {
        var bmp = new GdiBitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        using (System.Drawing.Graphics g = System.Drawing.Graphics.FromImage(bmp))
            PaintHudPanelInto(g, text, width, height);
        return bmp;
    }

    /// <summary>尺寸没变时复用那张缓冲区：清底 + 把新读数画进<b>同一块内存</b>，不再投放新的大对象。</summary>
    private void PaintHudPanel(string text)
    {
        // ═══ 第十五改：只重排<b>变了的那几行</b>，不再整块重排那 14 行 ═══
        // （注释里原先写"21 行"，那是 150% 那档的<b>行距 21 像素</b>被抄成了行数；行数由电池现量：
        //  选中一条缝 14 行、没选中 7 行。数字抄错这件事本批一并订正，出处见 <c>canvas/F8 面板的背景框必须装得下它自己那些读数</c>。）
        // 第十四改的逐帧拆账量到：HUD 开档最慢那帧里最大那一笔是"面板重画"那一路（复用 4.1~5.0ms），
        // 而那一路里"一次排那 14 行字"占 2.7~3.1ms、清底只 0.0~0.1ms。需求三第 5 条点名的就是
        // "不要每帧绘制 11 行文字"——一帧里通常只有 1~3 行的数字变了（帧耗时、视口、P95 那几行），
        // 整块重排付的是 14 行的钱。行级重画的<b>唯一</b>前提是像素与整块那条路逐字节相同：
        // 判据 <c>canvas/面板走行级增量重画必须与整块重画逐字节相同</c>，
        // 反证 <c>guard/行级重画错开一行必须被逐字节判据抓到</c>。
        // 走不了行级（行数变了、行盒高 × 行数对不上面板高）就照旧整块重画，并由计数报出来。
        string[] lines = text.Split('\n');
        int pitch = HudPanelLinePitchPx;
        bool lineWise = _hudLines is not null && _hudLines.Length == lines.Length
                        && pitch > 0 && pitch * lines.Length + 6 == _hudPanelHeight;
        using System.Drawing.Graphics g = System.Drawing.Graphics.FromImage(_hudPanel!);
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        if (!lineWise)
        {
            PaintHudPanelInto(g, text, _hudPanelWidth, _hudPanelHeight);
            HudPanelWholeRepaints++;
        }
        else
        {
            // 找第一条变了行，<b>从它往下</b>连着画到最后一行——不是"只画变了的那几行"。
            // 为什么必须连着画：整块那条路是 GDI 自上而下排版，某一行字在上一次那行字之上叠过什么
            // 由排版决定；只画中间那一行会让那行的抗锯齿落点与整块不同。
            // 本批第一次就是这么写的（只画变了的那几行 ＋ 用 FillRectangle 清行带），
            // 判据 <c>canvas/面板走行级增量重画必须与整块重画逐字节相同</c> 当场量出 218832 个字节不同——
            // 红得正是它该红的样子：那既不是"排版本来就有出入"，也不是判据太严，是两处都写错了。
            // 面板那几行"帧耗时／P95／分阶段／Paint 计数"本来就<b>在最底下</b>，从第一条变化行往下画
            // 依旧省掉上面那十几行（需求三第 5 条点名的"每帧绘制 11 行文字"）。
            int first = 0;
            while (first < lines.Length && lines[first] == _hudLines![first]) first++;
            if (first < lines.Length)
            {
                var below = new Rectangle(0, first * pitch, _hudPanelWidth, _hudPanelHeight - first * pitch);
                long clearStart = StageTimestamp();
                g.SetClip(below);
                // 清底必须用 <c>Clear</c>（替换像素），不能用 <c>FillRectangle</c>：
                // 面板是 32bppPArgb，半透明的底色用填充会<b>与底下已有的像素再做一次混合</b>，
                // 那样整张缓冲区的 alpha 全变了——上面那 218832 个字节里绝大部分就是它造的。
                g.Clear(WithAlpha(Theme.PanelBackground, 215));
                StageAdd(ref _panelClearMs, clearStart);
                long drawStart = StageTimestamp();
                // ═══ 第十六改：这一带<b>一次多行调用</b>，不再逐行调用 ═══
                // 一次性探针（同机、只做证据不做闸门）量到的形状：整块 14 行一次 4.88ms、下面 6 行一次 3.61ms、
                // 而<b>逐行</b> 6 次 3.95ms／14 次 8.53ms；Clear 0.015ms、SetClip＋ResetClip 0.0002ms 都免费。
                // ⇒ 第十五改那条"逐行调用"的路之所以省不到毫秒，是<b>每次调用自己的开销</b>把少排的行钱吃回去了；
                // 同一条带改成一次多行调用，才是探针里那笔 3.61ms 的形状。
                // 落位由 <see cref="HudPanelLastRepaintTopForTest"/> 交出去：判据要能指认"这次排版只从
                // 第一条变化行那一带开始"，而不是拿代码自己算的 `first` 反过来数一遍（那种断言是自证的）。
                string band = string.Join('\n', lines, first, lines.Length - first);
                TextRenderer.DrawText(g, band, Theme.MonoFont, below, Theme.TextPrimary, HudPanelFlags);
                HudPanelLinesRepainted += lines.Length - first;
                HudPanelLastRepaintTop = first * pitch;
                // 行级这一路<b>也必须</b>把清底与排字这两笔计上。第十五改第一版没接这两笔，
                // 于是真窗口的拆账读成"复用 7.9ms，其中清底 0.0／排字 0.0"——那一路的钱
                // 从有名字的桶里搬进了没名字的桶，比慢更糟（第十四改那把尺就废了）。
                // 判据 <c>canvas/行级那条重画路必须自己交得出清底与排字两笔</c>，
                // 反证 <c>guard/两笔面板计时只许跟着真重画涨整块那条路也一样</c>。
                StageAdd(ref _panelDrawMs, drawStart);
                g.ResetClip();
            }
            HudPanelLineWiseRepaints++;
        }
        _hudLines = lines;
    }

    /// <summary>
    /// GDI 给这个字体排多行时<b>每行占的高度</b>——用两行探针量出来除二，不猜 <c>Font.Height</c>。
    /// 为什么要量：面板高就是"整块文字的高度＋6"（<see cref="HudPanelHeight"/> 那条注释写的正是
    /// 写死行距在 150% 屏上切掉最后 4 行的事），行级重画要是按一个猜的行距落位，就会把字画错行。
    /// 进程内量一次（第一次重建时），之后只做算术。
    /// </summary>
    private static int HudPanelLinePitchPx
    {
        get
        {
            if (_hudPanelLinePitch < 0)
            {
                int twoLines = HudTextExtent("X\nX").Height;
                // 量不出整除的行距就报 0 ⇒ 行级那条路整个关掉（算术闸在 <c>pitch > 0</c> 那一步拦），
                // 宁可每次都整块重画，也不按一个猜的行距把字画到隔壁行上去。
                _hudPanelLinePitch = twoLines % 2 == 0 ? twoLines / 2 : 0;
            }
            return _hudPanelLinePitch;
        }
    }

    private static int _hudPanelLinePitch = -1;

    /// <summary>行带清底用的刷子（进程一次）。面板底色与整块那条路的 <c>g.Clear</c> 是同一个颜色。</summary>
    /// <summary>面板上一次画出去的那些行（行级增量重画的比较基准；尺寸或行数一变就整块重画）。</summary>
    private string[]? _hudLines;

    /// <summary>行级重画共画过几次、其中真重排了几行、退回整块重画几次。<b>只给判据与工装读</b>。</summary>
    public long HudPanelLineWiseRepaints { get; private set; }
    public long HudPanelWholeRepaints { get; private set; }
    public long HudPanelLinesRepainted { get; private set; }

    /// <summary>
    /// 上一次行级重画<b>真正交给 GDI 排版的那一条带的上沿</b>（像素）。
    /// 做成出口的理由：判据要能独立指认"这次只重排了第一条变化行以下那一带"，
    /// 而不是拿代码自己算出来的行数反过去数一遍（那种断言是自证的、不会红）。
    /// 第十六改把逐行调用改成一次多行调用之后，"重排了几行"这件事<b>只剩这一个证据</b>。
    /// </summary>
    public int HudPanelLastRepaintTopForTest => HudPanelLastRepaintTop;

    private int HudPanelLastRepaintTop;
    public int HudPanelLinePitchForTest => HudPanelLinePitchPx;

    /// <summary>
    /// 面板当前那几行读数的<b>行数</b>（行级重画之后留在那张缓冲区上的行）。
    /// 做成出口是因为"这一屏有几行字"这句话以前只写在注释里、各处抄了一次，
    /// 结果同一个数字在注释里同时存在 11、14、21 三个版本——现在以现量读数为准。
    /// </summary>
    public int HudPanelLineCountForTest => _hudLines?.Length ?? 0;

    /// <summary>
    /// 行级重画那道<b>算术闸</b>的产品自己那一句话（第二十一改，跟着进 <see cref="DescribeDiagnostics"/>）。
    /// ═══ 为什么这句话必须由产品交出来，不该只留在工装里 ═══
    /// 行级这条路开不开，判的是一道整数算术：<c>行距 × 行数 ＋ 6 == 面板高</c>
    /// （量行距那条路见 <see cref="HudPanelLinePitchPx"/> 的注释，用它的那个闸在 <c>PaintHudPanel</c> 里）。而<b>行距是按进程 DPI 现量的</b>——
    /// 96 DPI 那一档量到 14 像素、150% 屏上是 21 像素。过去这道闸<b>只有</b> `--gui` 工装算得出来，
    /// 而那个进程没声明 DPI 感知 ⇒ 它算的永远是 96 DPI 那一档；用户那台 150% 屏上这道闸成不成立，
    /// 屏上看不到、复制诊断信息里没有、导出会话里也没有——
    /// "行级这条路在 150% 上其实关着（每次整块重画）"这种可能就<b>没有任何出口能读</b>，只能挂着"没量过"。
    /// 现在这句话由产品在<b>自己的 DPI</b> 下拼出来，三个出口（复制／导出／会话文件）都能读到，
    /// 工装那侧改为<b>复用这一句</b>：同一道算术不许有两份写法（多处同值就是契约）。
    /// 隐私：这一句只有整数与"成立／不成立"，不含像素内容、也不含文件路径。
    /// </summary>
    public string DescribeHudPanelGate()
    {
        int pitch = HudPanelLinePitchForTest, count = HudPanelLineCountForTest, height = _hudPanelHeight;
        int expected = pitch * count + 6;
        // 行数 0（还没画过面板）时 0×pitch＋6＝6 对不上面板高 0 ⇒ 报"关着"，
        // 这一档同时是反证那一格的负对照：这句话不是写出来的常数，画之前它必须说不成立。
        bool open = count > 0 && pitch > 0 && expected == height;
        return $"行级算术闸（进程 DPI {DeviceDpi}）：行距 {pitch} × 行数 {count} ＋ 6 ＝ {expected} " +
               $"对面板高 {height}（{(open ? "成立，行级这条路开着" : "不成立⇒行级这条路关着，每次整块重画")}）";
    }

    /// <summary>
    /// 面板画字用的 flags。做成常量交出去的理由与 <see cref="BadgeFlags"/> 同：判据要用<b>同一条路</b>
    /// 去量"这段字在这块框里到底画出了多少墨"，各写一套 flags 就成了量的人与画的人各说一套。
    /// </summary>
    public const TextFormatFlags HudPanelFlags =
        TextFormatFlags.NoPadding | TextFormatFlags.Left | TextFormatFlags.Top;

    /// <summary>
    /// 两条重画路共用的<b>唯一</b>画法（分配新缓冲与复用旧缓冲必须画出一样的像素，
    /// 否则"复用"就成了另一种外观，判据也就测不到同一件事）。
    /// 第十四改往里加了两笔<b>只计时不分配</b>的累计读数（清底／画字）：真窗口逐帧拆账量到
    /// "最慢那帧的读数层里最大一笔是面板重画那一路（复用 4.9~12.3ms），描边只有 1.2~2.9ms"，
    /// 可"面板重画"里仍混着两件事——<c>Graphics.FromImage</c>＋<c>Clear</c> 那块 608×202 的底，
    /// 与一次 <c>TextRenderer.DrawText</c> 排那 14 行字。这两件的修法也不同（前者可以并到底色不变就不清，
    /// 后者才是需求三第 5 条点名的"每帧画 11 行文字"（现量 14 行）），所以再劈一刀。
    /// </summary>
    private static void PaintHudPanelInto(System.Drawing.Graphics g, string text, int width, int height)
    {
        // 半透明离屏层不能使用依赖最终屏幕底色的 ClearType 子像素字形。
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        long clearStart = StageTimestamp();
        g.Clear(WithAlpha(Theme.PanelBackground, 215));
        StageAdd(ref _panelClearMs, clearStart);
        long drawStart = StageTimestamp();
        TextRenderer.DrawText(g, text, Theme.MonoFont, new Rectangle(0, 0, width, height),
            Theme.TextPrimary, HudPanelFlags);
        StageAdd(ref _panelDrawMs, drawStart);
    }

    /// <summary>面板重画那一路里"清底"那一笔的<b>进程累计</b>毫秒（第十四改的子项，不参与六笔恒等式）。</summary>
    public static double HudPanelClearMs => _panelClearMs;

    /// <summary>面板重画那一路里"一次排那 14 行字"那一笔的<b>进程累计</b>毫秒（同上，是子项）。</summary>
    public static double HudPanelDrawTextMs => _panelDrawMs;

    private static double _panelClearMs, _panelDrawMs;

    /// <summary>读数面板的缓存位图与它的重建时刻（10Hz 闸门）。</summary>
    private GdiBitmap? _hudPanel;

    /// <summary>
    /// 面板那段读数文字在<b>面板自己那个 DPI</b> 下真正占多大一块（含换行、含中日韩全角字）。
    /// 探针位图与面板位图是同一种来路（都是 <c>new Bitmap(...)</c>），所以这里的 DPI 就是
    /// 面板里 GDI 实际排版用的那个 DPI——<b>量出来的框才装得下装进去的东西</b>。
    /// 只在面板真要重建时量一次（10Hz 闸门之内、且文本没变就根本不重建），不进每帧路径。
    /// </summary>
    public static Size HudTextExtent(string text)
    {
        using System.Drawing.Bitmap probe = new(8, 8);
        using System.Drawing.Graphics g = System.Drawing.Graphics.FromImage(probe);
        return TextRenderer.MeasureText(g, text, Theme.MonoFont);
    }

    /// <summary>
    /// 仅供 <c>guard/</c> 那一格使用：把<b>改造前</b>那条宽度算法<b>真正会得到那块框</b>的宽度
    /// （最长行<b>字符数</b> × 西文单字宽 + 10，再向上取到 <see cref="HudPanelWidthStepPx"/> 的整数倍）
    /// 与"这段文字在同一 DPI 下真正占多宽"并排交回来，让
    /// <c>canvas/F8 面板的背景框必须装得下它自己那些读数</c> 能被证明"不是恒真"。
    /// 交回<b>分档之后</b>的数而不是裸估算：产品当年用的就是分档后的框，掩盖那一缺陷的正是分档那几步，
    /// 只比裸估算会把"差 2 像素"读成"没事"、把"差 130 像素"读成另一回事。
    /// 产品里任何一帧都不该走到这里。<see cref="HudCharWidth"/> 如今<b>只</b>被这一条路引用：
    /// 留着它不是留一条还在生效的估算，是为了让反证能量出"旧写法到底差多少像素"。
    /// </summary>
    public static (int OldBoxWidth, int Measured) HudWidthsForTest(string text)
    {
        int max = 0;
        foreach (string line in text.Split('\n')) max = Math.Max(max, line.Length);
        int exact = (int)(Math.Max(1, max) * HudCharWidth) + 10;
        int stepped = (exact + HudPanelWidthStepPx - 1) / HudPanelWidthStepPx * HudPanelWidthStepPx;
        return (stepped, HudTextExtent(text).Width);
    }

    /// <summary>
    /// 仅供 <c>guard/</c> 那一格使用：把<b>改造前</b>那条算法（行数 × 写死的 16 像素）放到指定 DPI 下
    /// 重算一遍，交回「那样得到的框高」与「那个 DPI 下文字真正需要的高度」。
    /// 产品里任何一帧都不该走到这里。它存在的唯一理由是让
    /// <c>canvas/F8 面板的背景框必须装得下它自己那些读数</c> 能被证明"不是恒真"：
    /// 96 DPI 下量出来的行距是 14，比那个写死的 16 还小，所以<b>旧写法在那台电池上根本不出错</b>
    /// （只是每行白多留 2 像素）；行距一旦超过 16（约 115% 缩放以上）才开始整行切掉。
    /// 顺带一句实测：<c>Bitmap.SetResolution(288,288)</c> 改不动 <c>TextRenderer</c> 排出来的字高
    /// （两档都量到 196）——GDI 排版只认进程 DPI，而电池进程是 DPI 无感知的。
    /// 所以这一档只能走 <see cref="Font.GetHeight(System.Drawing.Graphics)"/> 这条纯算术的路，
    /// 它跟着 <c>Graphics.DpiY</c> 走，才量得出"DPI 一变这字就得变大"。
    /// </summary>
    public static (int FixedPitchBoxHeight, int LineHeightAtThisDpi) HudHeightsAtDpiForTest(string text, float dpi)
    {
        using System.Drawing.Bitmap probe = new(8, 8);
        probe.SetResolution(dpi, dpi);
        using System.Drawing.Graphics g = System.Drawing.Graphics.FromImage(probe);
        int lines = text.Count(c => c == '\n') + 1;
        return (lines * HudFixedLineHeight + 6, (int)Math.Ceiling(Theme.MonoFont.GetHeight(g)));
    }

    /// <summary>改造前那个写死的行距（96 DPI 下 Consolas 9pt 的行高）——只留给反证用。</summary>
    private const int HudFixedLineHeight = 16;

    private long _hudPanelBuiltAt;
    private int _hudPanelWidth;
    private int _hudPanelHeight;

    /// <summary>面板上一次画的那段文本——与本次相同就根本不重建（静止看数那一帧都不该重排文本）。</summary>
    private string _hudText = "";

    /// <summary>
    /// 最近一次命中判定的文字（需求四要在 F8 上直接看到"当前命中类型"）。
    /// 只存字符串，不参与任何几何决定；悬停与按下都会更新，所以"看得见却点不到"这类事一眼能看出来。
    /// </summary>
    private string _lastHitKindText = "无";

    /// <summary>供判据读"屏上那一行的命中类型是不是指针下那一块"。</summary>
    public string LastHitKindForTest => _lastHitKindText;

    /// <summary>
    /// 面板上那一段文本（最后一次重建时画的内容）。判据靠它量两件事：
    /// ① 需求三"≤10Hz 且文本没变就不重建"（同一批读数连画两帧，这段文本必须一字不改）；
    /// ② 需求一"撤销之后 HUD 数值也恢复"——屏上那几个数就是这段文本，比它而不是比像素稳。
    /// </summary>
    public string HudTextForTest => _hudText;

    /// <summary>命中结果写成屏上那一行的文字：命中的是哪一块 + 抓的是这条缝上方还是下方那张图。</summary>
    private static string HitKindText(SeamHit? hit) =>
        hit is null ? "无（这块按下去是平移画布）" : $"{hit.Value.Kind}·{hit.Value.Side}";

    private const int HudRefreshMs = 100;

    // ── F8 读数层的常驻画具：只在首次用到时建一次，之后每帧复用（它们不含任何每帧变化的状态）──
    private static readonly Pen HudDashPen = new(WithAlpha(Color.Gray, 210), 1f)
    { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash };
    private static readonly Pen HudViewPen = new(WithAlpha(Color.Gray, 235), 2f);
    private static readonly Pen HudHitPen = new(WithAlpha(Color.Orchid, 230), 1f)
    { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash };

    // 颜色层那四件（绿=Rect1、黄=Rect2、白=重叠带、红=箭头）以前每帧 new 五个 GDI 对象，
    // 需求三明确"不要每个 OnPaint 都创建 Brush"，所以全部转成常驻（颜色与线宽都是常量）。
    private static readonly Pen HudBeforePen = new(WithAlpha(Theme.OverlayBefore, 220), 2f);
    private static readonly Pen HudAfterPen = new(WithAlpha(Theme.OverlayAfter, 220), 2f);
    private static readonly Pen HudBandPen = new(WithAlpha(Color.White, 160), 1f)
    { DashStyle = System.Drawing.Drawing2D.DashStyle.Dot };
    private static readonly SolidBrush HudBandFill = new(WithAlpha(Color.White, 38));
    private static readonly Pen HudArrowPen = new(Theme.OverlayAlign, 2f);

    /// <summary>红色箭头的边长（设备像素）——与换算缓存里那两个箭头端点配套。</summary>
    private const int HudArrowHead = 5;

    /// <summary>等宽字体一个字符的像素宽（量一次就够：宽度是估算，宁可留宽也不每帧量 11 次文本）。</summary>
    private static double? _hudCharWidth;
    private static double HudCharWidth => _hudCharWidth ??= MeasureHudCharWidth();

    private static double MeasureHudCharWidth()
    {
        using System.Drawing.Bitmap probe = new(8, 8);
        using System.Drawing.Graphics g = System.Drawing.Graphics.FromImage(probe);
        return Math.Max(1.0, TextRenderer.MeasureText(g, "MMMMMMMMMM", Theme.MonoFont).Width / 10.0);
    }

    /// <summary>把手与状态标签各画了几次、各早退了几次（进程累计）——屏外早退这条机制的能数的出口。</summary>
    public static int SeamChromeDrawnForTest { get; private set; }
    public static int SeamChromeSkippedForTest { get; private set; }

    /// <summary>
    /// 缩小浏览到低于这个档位时，<b>未选中</b>的缝收掉带文字的状态标签盒子（见 <see cref="DrawSeams"/>）；
    /// 缝线与状态方框任何档位都画（第四十五改：方框就是状态图章，收掉就看不出哪条有问题）。
    /// 判据读的是它两侧的行为，不是这个数本身。
    /// </summary>
    private const double SeamChromeMinZoom = 1.0;

    /// <summary>因上面那个档位而收掉文字标签的未选中缝数（进程累计）——"这一帧真的收过"的能数的出口。</summary>
    public static int SeamChromeZoomSkippedForTest { get; private set; }

    /// <summary>真画出去的未选中缝线条数（进程累计）——反证那一格靠它证明"不是永不画"。</summary>
    public static int SeamHairlinesDrawnForTest { get; private set; }

    /// <summary>真画出去的状态方框数（进程累计，含选中）——第四十五改那枚"线居中的图章"的能数的出口。</summary>
    public static int SeamStatusBoxDrawnForTest { get; private set; }

    /// <summary>真画出去的带文字状态标签数（进程累计）——证明标签只是缩小档收、不是被删了。</summary>
    public static int SeamStatusLabelDrawnForTest { get; private set; }

    /// <summary>
    /// 最近一帧对照层用的两个矩形：<see cref="LastCompareBandForTest"/> 是整条重叠带（几何），
    /// <see cref="LastCompareShownForTest"/> 是它里面<b>真的取数并画出去</b>的那一块（第八改裁出来的）。
    ///
    /// 这两个数就是"裁了没有"的能数的出口：只写"对照层变便宜了"没法证它真在按可见区取数，
    /// 也没法证它没被做成"整带不画"（那会是把功能删掉换性能）。
    /// 带整条在屏外时 shown 是空矩形——那一帧确实一笔都不该取。
    /// </summary>
    public IntRect LastCompareBandForTest { get; private set; } = IntRect.Empty;
    public IntRect LastCompareShownForTest { get; private set; } = IntRect.Empty;

    /// <summary>
    /// 这块矩形是否<b>整个</b>在可见区之外（是 ⇒ 它的把手／状态标签可以整块不画）。
    ///
    /// ═══ 为什么这里可以早退，而重影对比层那一层当初判定不可以 ═══
    /// 对比层那两笔（设备矩形描边、重叠带半透明填充）花的钱<b>就在屏上看得见的那部分</b>，
    /// 裁到可见区只是让 GDI 少算一点，省不到调用本身；而一条远在屏外的缝，它的把手与标签
    /// <b>一个像素都不会落在屏上</b>——GDI 也已经把它们裁掉了，剩下的全是 CPU 白烧：
    /// 16 条缝 × 每帧两次文字渲染与两次填充，25% 档现量到 拼缝段 6.7ms／整帧约 27ms。
    /// 判据 <c>canvas/屏外的缝不画把手与状态标签</c>，反证 <c>guard/屏外早退不许粘住可见的那一条</c>。
    /// </summary>
    private static bool SeamChromeOffScreen(System.Drawing.Graphics g, Rectangle r)
    {
        RectangleF clip = g.VisibleClipBounds;
        // 拿不到可见区（空或退化）就一律当"看得见"：早退判断不确定时必须退回照画——
        // 宁可少省几毫秒，也不许把屏上的东西省掉（红线：显示层不许改可见内容）。
        if (clip.Width <= 0f || clip.Height <= 0f) return false;
        var visible = new Rectangle((int)clip.X, (int)clip.Y,
            (int)Math.Ceiling(clip.Width), (int)Math.Ceiling(clip.Height));
        return !visible.IntersectsWith(r);
    }

    /// <summary>
    /// 缝线正中的<b>状态方框</b>（用户第四十五改）：匹配成功画勾、有问题画警告、还没结论留空框。
    /// 矩形仍走 <see cref="AdjustHandleRect"/>——与命中测试同一个出处，不会"看得见按不到"；
    /// 点它照旧进微调模式（重叠带薄到几行时那是唯一入口），微调档开着时框里改画对勾＝"点一下完成"。
    /// 屏外早退照旧（第七改）：方框一个像素都不在屏上时一次都不许画。
    /// </summary>
    private void DrawSeamStatusBox(System.Drawing.Graphics g, Seam seam, Rectangle line,
        bool selected, bool ok, bool problem)
    {
        Rectangle rect = AdjustHandleRect(line);
        if (SeamChromeOffScreen(g, rect))
        {
            SeamChromeSkippedForTest++;
            return;
        }
        SeamChromeDrawnForTest++;
        SeamStatusBoxDrawnForTest++;

        bool adjusting = selected && _adjustMode;
        Brush fill = adjusting ? SeamHandleFillSelected
            : ok ? SeamBoxFillOk
            : problem ? SeamBoxFillProblem
            : SeamBoxFillPending;
        // 第五十一改（用户图5绿框）：这枚状态图章原来是一枚纯直角方块，用户报「底很丑，不如改成圆弧框」。
        // 改成圆角胶囊（半径夹到短边一半 ⇒ 20px 高画成近半圆的弧框）。矩形仍走 AdjustHandleRect——
        // 命中测试与绘制共用同一个出处，圆角只改外观，「看得见按得到」这条不变；
        // 判据数的是 SeamStatusBoxDrawnForTest 这一笔账，不数直角，所以计数照旧。
        var boxSmooth = g.SmoothingMode;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using (var boxPath = Chrome.RoundRect(rect, Math.Min(Chrome.Radius, rect.Height / 2)))
        {
            g.FillPath(fill, boxPath);
            g.DrawPath(adjusting ? SeamBoxBorderAdjust : SeamHandleBorderPen, boxPath);
        }
        g.SmoothingMode = boxSmooth;
        if (adjusting || ok) DrawDoneGlyph(g, rect);
        else if (problem) DrawWarnGlyph(g, rect);
        // 还没结论（Pending／Analyzing）留空框：不画勾也不画警告，免得把"没算"说成"算坏了"。

        // 微调模式里把"↑↓ 挪的是哪一张 + 怎么退出"直接写在方框旁边：
        // 状态栏那一行离得太远，用户在盯着蓝线，视线不会跑到窗口底部去。
        if (adjusting)
        {
            string hint = _adjustSide is FixedSide side
                ? $"↑↓ 挪{(side == FixedSide.Before ? "上方" : "下方")}那张 · 点 ✓ 完成"
                : "点一下要挪的那张图 · 点 ✓ 完成";
            Size size = MeasureSeamLabel(g, hint, Theme.UiFontSmall);
            var box = new Rectangle(rect.Right + 6, rect.Y + rect.Height / 2 - size.Height / 2,
                size.Width + 8, size.Height + 4);
            if (!SeamChromeOffScreen(g, box))
            {
                g.FillRectangle(SeamStatusLabelBg, box);
                TextRenderer.DrawText(g, hint, Theme.UiFontSmall, box, Theme.Accent,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            }
        }
        _ = seam;
    }

    /// <summary>
    /// 警告图章：一枚直边三角框加一竖一点（⚠ 的画法版）。不引字体、不引 emoji：
    /// 任何 DPI 与缩放档下都不会把字形挤出盒子，也不会因缺字形变成豆腐块。
    /// 第五十一改（用户图5）：原来用 DrawClosedCurve 画三角，三条边会被样条插值鼓成圆弧、
    /// 读起来像一团墨渍（用户报「有点丑，也不是矢量的」）。改成 DrawPolygon 的直边三角
    /// （圆头笔闭合三个顶点），边是真正的直线段，感叹号竖笔与点也按三角高度比例落位。
    /// </summary>
    private void DrawWarnGlyph(System.Drawing.Graphics g, Rectangle rect)
    {
        int inset = Math.Max(3, rect.Width / 6);
        int top = rect.Y + 3;
        int bottom = rect.Bottom - 3;
        int cx = rect.X + rect.Width / 2;
        _glyphScratch[0] = new Point(cx, top);
        _glyphScratch[1] = new Point(rect.Right - inset, bottom);
        _glyphScratch[2] = new Point(rect.X + inset, bottom);
        g.DrawPolygon(SeamHandleGlyphPen, _glyphScratch);
        int height = bottom - top;
        int barTop = top + height * 2 / 5;
        int barBottom = bottom - height / 4;
        g.DrawLine(SeamHandleGlyphPen, cx, barTop, cx, barBottom - 2);
        g.FillRectangle(SeamHandleGlyphBrush, cx - 1, barBottom, 2, 2);
    }

    /// <summary>对勾：微调档开着时画在按钮里，意思是"点这一下=完成并退出编辑这条缝"。</summary>
    private void DrawDoneGlyph(System.Drawing.Graphics g, Rectangle rect)
    {
        int inset = Math.Max(2, rect.Width / 5);
        _glyphScratch[0] = new Point(rect.X + inset, rect.Y + rect.Height * 5 / 9);
        _glyphScratch[1] = new Point(rect.X + rect.Width * 2 / 5, rect.Y + rect.Height * 2 / 3);
        _glyphScratch[2] = new Point(rect.Right - inset, rect.Y + rect.Height / 3);
        g.DrawLines(SeamHandleGlyphPen, _glyphScratch);
    }

    private void DrawSeamStatus(System.Drawing.Graphics g, Seam seam, Rectangle line, bool selected)
    {
        string label = Theme.StatusText(seam.Status);
        if (seam.Status is SeamStatus.AutoPassed or SeamStatus.ManualConfirmed
            or SeamStatus.NeedsReview or SeamStatus.ManualPending)
        {
            // 宽度走缓存（同文字 × 同字体 × 同 DPI 只会量一次），盒子公式与原来一模一样。
            var size = MeasureSeamLabel(g, label, Theme.StatusFont);
            var box = new Rectangle(line.X + 8, line.Y - size.Height - 3, size.Width + 8, size.Height + 4);
            // 标签盒子在缝线上方，可能与把手一个看得见一个看不见 ⇒ 各判各的矩形。
            if (SeamChromeOffScreen(g, box))
            {
                SeamChromeSkippedForTest++;
                _ = selected;
                return;
            }
            g.FillRectangle(SeamStatusLabelBg, box);
            TextRenderer.DrawText(g, label, Theme.StatusFont, box,
                Theme.StatusColor(seam.Status), TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            SeamStatusLabelDrawnForTest++;
        }
        _ = selected;
    }

    /// <summary>
    /// 【仅屏幕上的调试叠层，绝不进出成品】重叠对比 / 差异显示。
    ///
    /// 长截图改成裁切去重之后，这一层的意义更清楚了：
    /// 屏上那一条带显示的只是<b>被留下的那一份</b>，这一层把<b>被裁掉的那一份</b>半透明叠上来，
    /// 于是"两份副本到底差在哪一列"能直接看见；关掉它，红框里就只剩一份内容。
    /// 取被裁掉那份必须走 <see cref="CanvasComposer.RenderDebugSource"/>，
    /// 因为那些行已经不在任何布局矩形里了（瓦片与导出都取不到它们，正是想要的效果）。
    /// </summary>
    private void DrawOverlapPreview(System.Drawing.Graphics g, CanvasLayout layout)
    {
        // 出口每帧从"这一帧没取过"起算。不清的话判据会把上一帧的 shown 读成本帧的，
        // "裁到可见区"这个结论就会被陈旧值冒充——那一格于是永远绿着。
        LastCompareBandForTest = IntRect.Empty;
        LastCompareShownForTest = IntRect.Empty;

        Seam? seam = SelectedSeam();
        if (seam is null || (!ShowCompareOverlay && !ShowDifference)) return;
        if (!ShowDifference && OverlapOpacityPercent <= 0) return;

        PlacedSegment? before = layout.Find(seam.BeforeId);
        PlacedSegment? after = layout.Find(seam.AfterId);
        if (before is null || after is null) return;

        SeamGeometry? geo = layout.DescribeSeam(seam);
        if (geo is null) return;

        // 带的位置一律问布局（两条档共用同一个出处），不再自己 Intersect
        IntRect band = geo.HasBand ? geo.BandRect : before.Rect.Intersect(after.Rect);
        if (band.IsEmpty) return;

        // 【第八改】这一层要比的是"带里屏上看得见的那几行"，不是整条带。
        // 为什么必须裁：对齐拖动会让重叠带随手势变长（带可以有几万行），
        // 而下面三次取数每次都按矩形面积分配 RgbaImage——不裁的话每帧的分配量与遍历量
        // 都正比于<b>整条带</b>，25% 那一档真窗口量到过一帧 316.2ms、其中"预览"291.6ms，
        // 拆账指认的就是这一段（前七改裁的是"没画的东西"，这一改裁的是"画了但屏上没有的东西"）。
        // 屏上没有的行本来就画不进缓冲区，裁掉它们不改一个像素；
        // "裁与不裁在同一行上逐字节相同"由判据 <c>canvas/对照层裁到可见区后每行像素不许变</c> 钉住。
        IntRect visible = Viewport.VisibleCanvas();
        // 视口还没量到设备尺寸时（CreateControl 之前、离屏夹具）VisibleCanvas 返回空——
        // 那种场合退回整条带，不许因为裁不出可见区就把这一层整个画没了。
        IntRect shown = visible.IsEmpty ? band : band.Intersect(visible);
        if (shown.IsEmpty)
        {
            // 带整条在屏外：这一帧没可比的东西要画，计数仍要留下（反证要看它）。
            LastCompareBandForTest = band;
            LastCompareShownForTest = IntRect.Empty;
            return;
        }
        LastCompareBandForTest = band;
        LastCompareShownForTest = shown;

        Rectangle target = Viewport.CanvasToDevice(shown);

        if (layout.UsesCropPlacement)
        {
            // 裁切档：屏上这份 = 正常合成（RenderBand 走布局矩形，已经裁过）；
            // 对比对象 = 两份被裁掉的副本按各自的落点拼起来。
            long tRender = StageTimestamp();
            RgbaImage survivor = _composer.RenderBand(layout, shown);
            StageAdd(ref _msOvPvRender, tRender);
            long tStitch = StageTimestamp();
            RgbaImage? alternative = BuildDiscardedCopy(before, after, geo, band, shown);
            StageAdd(ref _msOvPvStitch, tStitch);
            if (alternative is null) return;
            long tBlit = StageTimestamp();
            DrawCompare(g, target, survivor, alternative);
            StageAdd(ref _msOvPvBlit, tBlit);
            return;
        }

        // 必须<b>分别只渲染一侧</b>再比较。
        // 之前写成"同一块重叠区渲染两次然后对比"，两侧永远逐字节相同，
        // 差异图于是恒为全透明——一个看起来在工作、实际什么也没显示的假功能。
        long tBoth = StageTimestamp();
        RgbaImage top = _composer.RenderBand(layout, shown, onlySegmentId: after.Segment.Id);
        RgbaImage bottom = _composer.RenderBand(layout, shown, onlySegmentId: before.Segment.Id);
        StageAdd(ref _msOvPvRender, tBoth);      // 非裁切档没有"拼对照"那一步，两侧渲染都记进渲染段
        long tBlit2 = StageTimestamp();
        DrawCompare(g, target, top, bottom);
        StageAdd(ref _msOvPvBlit, tBlit2);
    }

    /// <summary>
    /// 把"被裁掉的那两份"拼成<b>与屏上那一段同样大小</b>的一张图：
    /// <paramref name="band"/> 里前 t 行是后一段被裁掉的顶部，后 b 行是前一段被裁掉的底部（t/b 见裁切账）。
    ///
    /// <paramref name="shown"/> 是带里屏上看得见的那一块（第八改）。两张源图都必须按
    /// "shown 的顶边落在带的哪一行"去取子矩形，<b>不能整张取回来再放在 0 行</b>：
    /// 那样分回来的行号会错位，屏上半截显示的就是错行的对照——比"少画几行"糟得多，
    /// 因为它看起来在工作、给出的是错的结论。行号换算由
    /// <c>canvas/对照层裁到可见区后每行像素不许变</c> 钉住（拿不裁的整带当参照，逐行比字节）。
    /// </summary>
    /// <returns>拼好的对照图；两份都被裁成 0 行时返回 null（没重叠就没有可比的）。</returns>
    private RgbaImage? BuildDiscardedCopy(PlacedSegment before, PlacedSegment after,
        SeamGeometry geo, IntRect band, IntRect shown)
    {
        int t = geo.AppliedAfterCrop;   // 带里由前一段提供的行数 = 后一段被裁掉的顶部
        int b = geo.AppliedBeforeCrop;  // 带里由后一段提供的行数 = 前一段被裁掉的底部
        if (t + b <= 0) return null;

        // shown 在带里的行区间 [top, top+rows)，列偏移 left。
        int top = shown.Y - band.Y;
        int rows = shown.Height;
        int left = shown.X - band.X;
        var alt = new RgbaImage(shown.Width, rows);
        // 对照图本身这一块缓冲也算这一步的账，且必须算：它的行数＝<b>可见区行</b>而不是"被裁掉的行"，
        // 于是这一步分配的字节数 ＝（可见行 ＋ 取回行）× 行宽 × 4。
        // 光数"取回几行"会漏掉这一块——它跟取数无关，只跟这一帧屏上看得见几行有关。
        _ovPvDiscardBytesAccum += alt.Pixels.Length;

        // 第一块：带的 [0, t) 行 ← 后一段被裁掉的顶部（源图坐标）
        int a0 = Math.Max(0, top), a1 = Math.Min(top + rows, t);
        if (a1 > a0)
        {
            var src = new IntRect(geo.AfterDroppedRect.X + left, geo.AfterDroppedRect.Y + a0,
                shown.Width, a1 - a0);
            RgbaImage? fromAfter = _composer.RenderDebugSource(after, src);
            if (fromAfter is not null)
            {
                alt.Blit(fromAfter, new IntRect(0, 0, fromAfter.Width, fromAfter.Height), 0, a0 - top);
                _ovPvDiscardRowsAccum += fromAfter.Height;
                _ovPvDiscardBytesAccum += fromAfter.Pixels.Length;
            }
        }

        // 第二块：带的 [t, t+b) 行 ← 前一段被裁掉的底部；它在带里的行号要减掉 t 才是自己的局部行号
        int b0 = Math.Max(top, t), b1 = Math.Min(top + rows, t + b);
        if (b1 > b0)
        {
            var src = new IntRect(geo.BeforeDroppedRect.X + left, geo.BeforeDroppedRect.Y + (b0 - t),
                shown.Width, b1 - b0);
            RgbaImage? fromBefore = _composer.RenderDebugSource(before, src);
            if (fromBefore is not null)
            {
                alt.Blit(fromBefore, new IntRect(0, 0, fromBefore.Width, fromBefore.Height), 0, b0 - top);
                _ovPvDiscardRowsAccum += fromBefore.Height;
                _ovPvDiscardBytesAccum += fromBefore.Pixels.Length;
            }
        }
        return alt;
    }

    /// <summary>
    /// 【仅判据用】把对照层在 <paramref name="shown"/> 这一段上拼出来的那张图交出去。
    /// 为什么要开这条而不是让判据自己调那三次取数：行号换算一旦写错，屏上看不出来
    /// （它就是"少画/错行"），只有拿"不裁的整带"当参照逐行比字节才能抓出来——
    /// 那个比较必须走<b>产品自己的</b>换算，否则判据是在比自己抄的一份逻辑。
    /// </summary>
    public RgbaImage? DiscardedCopyForTest(PlacedSegment before, PlacedSegment after,
        SeamGeometry geo, IntRect band, IntRect shown) =>
        BuildDiscardedCopy(before, after, geo, band, shown);

    /// <summary>把两份同尺寸的对照图画到屏幕上：差异模式走红→透明，重影模式走半透明叠加。</summary>
    private void DrawCompare(System.Drawing.Graphics g, Rectangle target, RgbaImage a, RgbaImage b)
    {
        if (ShowDifference)
        {
            RgbaImage diff = BuildDifferenceImage(a, b);
            _ovPvConversionsAccum++;      // 差异档：整张差异图每帧重新转一次位图
            using GdiBitmap bmp = Imaging.FileSourceResolver.ToBitmap(diff);
            g.DrawImage(bmp, target);
            return;
        }

        // 画布上这一带显示的已经是"后一段"，所以叠上去的应当是前一段的那半，
        // 两侧错位时用户看到的就是重影
        _ovPvConversionsAccum++;          // 重影档：整张对照图每帧重新转一次位图
        using GdiBitmap overlay = Imaging.FileSourceResolver.ToBitmap(b);
        using var attrs = new GdiImageAttributes();
        float t = Math.Clamp(OverlapOpacityPercent, 0, 100) / 100f;
        attrs.SetColorMatrix(new GdiColorMatrix { Matrix00 = 1f, Matrix11 = 1f, Matrix22 = 1f, Matrix33 = t });
        g.DrawImage(overlay, target, 0, 0, overlay.Width, overlay.Height,
            System.Drawing.GraphicsUnit.Pixel, attrs);
    }

    /// <summary>
    /// 差异图：把两侧同位置的平均通道差映射成红→透明。
    /// 目前比较的是"画布上重叠带的同一位置"，也就是用户实际会看到的那一块。
    /// </summary>
    private static RgbaImage BuildDifferenceImage(RgbaImage a, RgbaImage b)
    {
        int w = Math.Min(a.Width, b.Width), h = Math.Min(a.Height, b.Height);
        var outImg = new RgbaImage(w, h);
        for (int y = 0; y < h; y++)
        {
            ReadOnlySpan<byte> ra = a.Row(y), rb = b.Row(y);
            Span<byte> ro = outImg.Row(y);
            for (int x = 0; x < w; x++)
            {
                int i = x * 4;
                int d = (Math.Abs(ra[i] - rb[i]) + Math.Abs(ra[i + 1] - rb[i + 1])
                         + Math.Abs(ra[i + 2] - rb[i + 2])) / 3;
                int alpha = Math.Clamp(d * 4, 0, 200);
                ro[i] = 255; ro[i + 1] = 60; ro[i + 2] = 60; ro[i + 3] = (byte)alpha;
            }
        }
        return outImg;
    }

    private void DrawDragBadge(System.Drawing.Graphics g)
    {
        if (_drag is not (DragMode.MoveSeam or DragMode.SlidePicture) || !_dragStarted) return;

        string text = _drag == DragMode.SlidePicture
            // 滑动手势报的是"这张图挪了几行"与"蓝线钉在哪一行"——蓝线是参照物，
            // 报它会让人以为线在动；报重叠行数也一样误导（那是结果，不是用户手里的东西）。
            ? $"滑动{(_slideGrabbed == FixedSide.Before ? "上方" : "下方")}那张 {_dragDelta.Y:+#;-#;0} 行" +
              $"（蓝线钉在第 {_trace?.LineDeviceAfter ?? 0} 屏行不动）"
            : $"X {_dragDelta.X:+#;-#;0} px   Y {_dragDelta.Y:+#;-#;0} px（源图）";
        var size = BadgeTextSize(g, text);
        var box = new Rectangle(_lastAt.X + 14, _lastAt.Y + 14, size.Width + 12, size.Height + 6);
        using var bg = new SolidBrush(WithAlpha(Theme.PanelBackground, 235));
        using var border = new Pen(Theme.Accent);
        g.FillRectangle(bg, box);
        g.DrawRectangle(border, box);
        TextRenderer.DrawText(g, text, Theme.MonoFont, box, Theme.TextPrimary, BadgeFlags);
    }

    /// <summary>
    /// 画布空态：一张居中的引导卡，告诉用户"这里将来会长出什么"以及"第一步怎么做"。
    ///
    /// ═══ 为什么值得单独画这一块 ═══
    /// 空态是这个应用被打开时看到的第一屏。原来那一圈虚线框加一行灰字，
    /// 在一块 900×600 的深灰画布上几乎读不出来——用户不知道这块区域是干什么的，
    /// 也不知道除了左侧那枚按钮还能直接把文件拖进来。这一版把它做成一张有层级的卡：
    /// 图标（这是什么）→ 标题（现在什么状态）→ 正文（怎么开始）→ 快捷键胶囊（最快的那条路）。
    ///
    /// ═══ 三条纪律 ═══
    /// ① 这里只画屏幕，一个像素都不进导出。导出走 <c>StitchComposer</c> 那条独立通路，
    ///    根本不经过本控件的 OnPaint（精度红线：导出绝不从屏幕取图）。
    /// ② 颜色全部走 <see cref="Chrome"/> 现算，不新增调色板键，换肤后下一次重绘自然就是新档。
    ///    蓝色一枚都不用——需求 §6 把蓝留给"选中"与"可交互拼缝"，空态不属于这两者。
    /// ③ 快捷键只写真的存在的：<c>MainForm</c> 里注册了 Ctrl+O，<b>没有</b> Ctrl+V，
    ///    所以这里绝不许诺"粘贴剪贴板"。诺一个不存在的快捷键比不诺更糟。
    /// </summary>
    private void DrawEmptyState(System.Drawing.Graphics g)
    {
        int width = Math.Max(1, Math.Min(560, Width - 48));
        int x = (Width - width) / 2;
        int titleHeight = Theme.UiFontTitle.Height + 12;
        int lineHeight = Theme.UiFontSmall.Height + 10;
        int keyHeight = Theme.MonoFont.Height + 16;
        int contentHeight = 148 + titleHeight + lineHeight * 2 + keyHeight + 60;
        int y = Math.Max(16, (Height - contentHeight) / 2);
        const TextFormatFlags centered = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter;

        if (width >= 240 && Height >= contentHeight)
        {
            int middle = Width / 2;
            var rear = new Rectangle(middle - 86, y + 20, 104, 100);
            var front = new Rectangle(middle - 18, y, 104, 132);
            Chrome.Card(g, rear, Chrome.Raised, 12, bevel: false);
            Chrome.Card(g, front, Theme.PanelBackground, 12, bevel: false);
            Chrome.Glyph(g, ChromeGlyph.Picture,
                new Rectangle(rear.X + 16, rear.Y + 12, 48, 44), Theme.TextSecondary, Chrome.IconFontLarge);
            using var ink = new SolidBrush(Chrome.Hover);
            for (int row = 0; row < 3; row++)
                g.FillRectangle(ink, front.X + 16, front.Y + 20 + row * 30, 72, 20);
            using var seam = new Pen(Theme.TextSecondary) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash };
            g.DrawLine(seam, middle - 102, y + 72, middle + 102, y + 72);
            y += 156;
        }

        Chrome.Text(g, "从截图，到完整长图", new Rectangle(x, y, width, titleHeight),
            Theme.TextPrimary, Theme.UiFontTitle, centered);
        y += titleHeight + 12;
        // 第六十改：模式还没选时，"把截图拖到这里开始"是一句会让人白拖的引导——
        // 产品在导入入口把没选模式的图片全拦下了。这行文案按文档模式分叉：
        // 未选 → 先去顶栏选模式；已选 → 照旧引导拖图。
        bool modeUnset = _project.Doc.Mode == StitchMode.Unset;
        Chrome.Text(g, modeUnset ? "先在顶栏选择拼合模式（长截图／竖拼／横拼）" : "把截图拖到这里开始",
            new Rectangle(x, y, width, lineHeight),
            Theme.TextSecondary, Theme.UiFontSmall, centered);
        y += lineHeight;
        Chrome.Text(g, "自动识别重叠 · 逐条检查拼缝 · 按原始像素导出",
            new Rectangle(x, y, width, lineHeight), Theme.TextSecondary, Theme.UiFontSmall, centered);
        y += lineHeight + 24;

        int keyWidth = Chrome.Measure(g, "Ctrl + O", Theme.MonoFont).Width + 24;
        int labelWidth = Chrome.Measure(g, "添加图片", Theme.UiFontSmall).Width;
        int start = (Width - keyWidth - labelWidth - 12) / 2;
        var key = new Rectangle(start, y, keyWidth, keyHeight);
        Chrome.Card(g, key, Theme.PanelBackground, Chrome.RadiusSmall, bevel: false);
        Chrome.Text(g, "Ctrl + O", key, Theme.TextPrimary, Theme.MonoFont, centered);
        Chrome.Text(g, "添加图片", new Rectangle(key.Right + 12, y, labelWidth, keyHeight),
            Theme.TextSecondary, Theme.UiFontSmall, centered);
    }

    private static Color WithAlpha(Color c, int alpha) => Color.FromArgb(alpha, c.R, c.G, c.B);
}
