using LongStitch.Core.Geometry;
using LongStitch.Core.Model;

namespace LongStitch.Core.Layout;

/// <summary>一个片段在画布上的落位结果。</summary>
public sealed class PlacedSegment
{
    public required Segment Segment { get; init; }
    public required SourceImage Source { get; init; }

    /// <summary>在 <see cref="CanvasLayout.Placed"/> 中的序号，从 0 开始。</summary>
    public required int Index { get; init; }

    /// <summary>裁边之后真正取用的源图矩形。</summary>
    public required IntRect SourceRect { get; init; }

    /// <summary>画布坐标（整数，左上角）。画布原点已由 <see cref="CanvasLayout"/> 归一到 0。</summary>
    public required IntPoint Origin { get; init; }

    /// <summary>
    /// 公共坐标里<b>未裁重叠</b>的完整矩形（需求里的 Rect1 / Rect2），画布坐标。
    /// 裁切档里它与 <see cref="Rect"/> 不同：完整矩形之间<b>是相交的</b>，那个交就是重叠带，
    /// 红框与命中区用的就是它；<see cref="Rect"/> 是蓝线切完之后真正进像素的那一块。
    /// 位移档里两者相同。
    /// </summary>
    public required IntRect FullRect { get; init; }

    /// <summary><see cref="FullRect"/> 对应的源图矩形（未裁重叠那一版）。</summary>
    public required IntRect FullSourceRect { get; init; }

    /// <summary>与前一段之间的那条拼缝；首段为 null。</summary>
    public Seam? IncomingSeam { get; init; }

    /// <summary>
    /// 本次布局使用的全局裁边。带上它，是为了让"检测专用取数"（见
    /// <see cref="CanvasComposer.RenderForDetection"/>）能在不读整个文档的情况下
    /// 复现同一条裁边链——少传一份状态就少一处可能算不一致的地方。
    /// </summary>
    public Insets GlobalCrop { get; init; }

    public IntSize Size => SourceRect.Size;
    public IntRect Rect => new(Origin.X, Origin.Y, SourceRect.Width, SourceRect.Height);

    /// <summary>零尺寸片段（被裁没了）：占位但不产生像素，布局层保留它以维持拼缝链。</summary>
    public bool IsEmpty => SourceRect.IsEmpty;

    public override string ToString() => $"#{Index} {Segment.Id} {Rect}";
}

/// <summary>
/// 拖动预览用的一次覆盖：按拼缝 Id 临时给出（对齐位移 Y, 蓝线带内行号），不写回参数。
/// 与 <see cref="CanvasLayout.Build"/> 的 seamOffsetOverrides 同一个用途——
/// "拖动的每一帧都改真参数"会留下半套撤销记录，所以覆盖只活在这一次布局里。
///
/// 第十二轮：这里给的是<b>核心那两个量</b>，不再是一对裁切数量。
/// 于是"拖红箭头"只覆盖 AlignmentY、"拖蓝线"只覆盖 CutY，两条通路各改各的，
/// 不可能出现"一次拖动既改重叠又改切点"（需求七）。
/// </summary>
/// <param name="AlignmentY">对齐位移 Y（以紧接位置为零点；负 = 压上去 |Y| 行）。</param>
/// <param name="CutY">蓝线在重叠带里的行号（0 = 带首）。</param>
public readonly record struct SeamAlignment(int AlignmentY, int CutY);

/// <summary>
/// 布局引擎：把"参数"算成"每个片段在画布上的整数位置"。
///
/// ═══ 两种去重方式，按模式二选一 ═══
/// <list type="bullet">
///   <item><b>长截图 = 先对齐、再切一刀</b>（<see cref="StitchAxes.UsesCropPlacement"/>，第十二轮）：
///   每条缝先按<b>对齐位移</b>把两张<b>完整</b>图摆进公共坐标（Rect1、Rect2，这时它们相交），
///   交叠那一段就是重叠带 OverlapRect；蓝线 CutY 落在带里，把带分成两半：
///   蓝线以上只从图1 取像素、以下只从图2 取像素。两侧"各裁几行"是<b>这一步算出来的结果</b>
///   （<see cref="Seam.AfterCropTop"/> / <see cref="Seam.BeforeCropBottom"/> 都是派生读法），
///   不是先分配好的输入。于是"任意两段的可见矩形不相交"仍然是结构性质
///   （判据 <c>crop/长截图里任意两段的矩形都不相交</c>），而且它现在是由蓝线保证的，
///   不是由"两个数加起来刚好等于重叠"保证的。</item>
///   <item><b>竖拼 / 横拼 = 位移</b>：那是<b>不同</b>的图，重叠多少由用户拿位移决定，
///   照旧按 offset 落位（两段可以相交、也可以留缝）。</item>
/// </list>
///
/// ═══ 为什么新的推导方式与旧的默认档导出的像素完全相同 ═══
/// 蓝线落在带首（CutY=0）时，图1 让出整带、图2 一行不让——
/// 这正是老写法"后一段盖上来"之后的结果。改的是<b>谁被画过</b>与<b>谁说了算</b>
/// （从"两个裁切数"变成"一条蓝线"），不是画布上看到的数。
/// 这条等价性是常驻判据 <c>crop/默认切分与旧的覆盖式落位逐字节相同</c>。
///
/// 计算规则：
///   裁切档：Rect1 从 (0,0) 起；Rect[i].Top = Rect[i-1].Bottom + 间隙 − 重叠；
///           蓝线 cut[i] = clamp(CutY, 0, 重叠) + Rect[i].Top；
///           第 i 段可见块 = [cut[i-1], cut[i]]（首段上界是自己的顶边、末段下界是自己的底边）
///   位移档：pos[0] = (0, 0)；pos[i] = pos[i-1] + 拼缝 i 解析出的整数位移
///   未分析的拼缝：裁切档按重叠 0（不切、首尾相接），位移档退化为"紧接排列"
///   最后把整体平移到左上角为原点（负坐标被归一，画布尺寸取可见块的包围盒）
/// </summary>
public sealed class CanvasLayout
{
    public StitchMode Mode { get; }
    public IReadOnlyList<PlacedSegment> Placed { get; }
    public IntSize CanvasSize { get; }
    public IntRect CanvasBounds => new(0, 0, CanvasSize.Width, CanvasSize.Height);

    /// <summary>主轴是否为横向（横拼=true，长截图/竖拼=false）。布局与界面共用这一个判定。</summary>
    public bool IsHorizontal => _horizontal;

    /// <summary>本布局是否走"裁切去重"（长截图）。界面据此决定显示 Y 位移框还是重叠/切分框。</summary>
    public bool UsesCropPlacement => StitchAxes.UsesCropPlacement(Mode);

    /// <summary>是否处于"内容全部被裁光"的状态；界面据此给出可执行的修复提示。</summary>
    public bool IsEmpty => CanvasSize.IsEmpty;

    /// <summary>主轴线（横拼取 X，其余取 Y）。</summary>
    private readonly bool _horizontal;

    /// <summary>紧接排列用的间隙（已在构建时夹过 ≥0）。</summary>
    private readonly int _gap;

    // ═══ 裁切账（每段一组，主轴方向）═══
    // 这些数组是"落位之后才知道的事实"，与参数里的 Seam 值可能不同（夹取过）。
    // 日志与判据要读它们，而不是再拿参数减一遍——那是"多处同值就是契约"的反面教材。
    /// <summary>每段<b>未裁切</b>（只经过裁边与清理）的主轴长度。</summary>
    private readonly int[] _baseExtents;
    /// <summary>每段在主轴起点侧让出的行数 = 上一条缝的蓝线到自己顶边的距离（带里由前一段提供的行数）。</summary>
    private readonly int[] _topTrims;
    /// <summary>每段在主轴终点侧让出的行数 = 下一条缝的蓝线到自己底边的距离。</summary>
    private readonly int[] _bottomTrims;
    /// <summary>
    /// 每条缝的蓝线在<b>本布局公共坐标</b>里的主轴位置：cut[i] 是第 i 段与第 i+1 段之间那条缝。
    /// 界面上那条蓝线、命中区、以及 <c>seam.geometry</c> 日志读的都是这一个数——
    /// 参数里的 CutY 是"带内第几行"，屏上画的是"画布第几行"，两者只能由布局换算一次。
    /// </summary>
    private readonly int[] _cutLines;

    private CanvasLayout(StitchMode mode, PlacedSegment[] placed, IntSize size, bool horizontal, int gap,
        int[] baseExtents, int[] topTrims, int[] bottomTrims, int[] cutLines)
    {
        Mode = mode;
        Placed = placed;
        CanvasSize = size;
        _horizontal = horizontal;
        _gap = gap;
        _baseExtents = baseExtents;
        _topTrims = topTrims;
        _bottomTrims = bottomTrims;
        _cutLines = cutLines;
    }

    public PlacedSegment? Find(string segmentId) =>
        Placed.FirstOrDefault(p => p.Segment.Id == segmentId);

    public int IndexOf(string segmentId)
    {
        for (int i = 0; i < Placed.Count; i++)
            if (Placed[i].Segment.Id == segmentId) return i;
        return -1;
    }

    /// <summary>第 i 条拼缝 = 第 i 段与第 i+1 段之间的那条。</summary>
    public Seam? SeamAfter(int index) =>
        index >= 0 && index + 1 < Placed.Count ? Placed[index + 1].IncomingSeam : null;

    public int SeamCount => Math.Max(0, Placed.Count - 1);

    // ───────────────────────── 拼缝的"去重账" ─────────────────────────

    /// <summary>
    /// 这条拼缝处于"紧接排列"（不去重、不留缝）时的位移：主轴上<b>未裁切</b>的前一段长度 + 间隙。
    /// 布局内部、界面显示的 Y、日志与判据都必须读这一条——过去这三处各自用
    /// <c>before.Rect.Height</c> 复述了一遍规则，漏加间隙或漏判模式就会各算出不同的数。
    /// </summary>
    /// <remarks>
    /// 读的是 <see cref="_baseExtents"/>（裁边之后、拼缝裁切之前）而不是 <c>before.Rect</c>：
    /// 裁切档里可见矩形的高度<b>已经被这条缝的蓝线改过</b>，拿它算"紧接量"会把去重量自己吃掉一次。
    /// 前一段的未裁切长度不依赖任何位移（裁边只改源矩形），所以这条公式在构建前后同值，拖动预览里用它也安全。
    /// </remarks>
    public IntPoint TightOffset(Seam seam)
    {
        ArgumentNullException.ThrowIfNull(seam);
        int beforeIndex = IndexOf(seam.BeforeId);
        int extent = beforeIndex < 0 ? 0 : _baseExtents[beforeIndex];
        return _horizontal ? new IntPoint(extent + _gap, 0) : new IntPoint(0, extent + _gap);
    }

    /// <summary>
    /// 这条拼缝在<b>本布局下真正生效</b>的位移。
    ///
    /// 位移档（竖拼/横拼）：按"手动 &gt; 自动 &gt; 紧接"解析再按模式归零——参数就是落位。
    /// 裁切档（长截图）：位移是<b>对齐 + 蓝线摆出来的结果</b>，直接从已算好的两段原点相减得到，
    /// 于是界面显示的那个 Y 与像素真的落在哪儿必然是同一个数（不是"参数说 Y、像素按另一个 Y 摆"）。
    /// </summary>
    public IntPoint EffectiveOffset(Seam seam)
    {
        ArgumentNullException.ThrowIfNull(seam);
        if (!UsesCropPlacement) return seam.EffectiveOffset(TightOffset(seam), Mode);

        int beforeIndex = IndexOf(seam.BeforeId);
        int afterIndex = IndexOf(seam.AfterId);
        if (beforeIndex < 0 || afterIndex < 0) return TightOffset(seam);
        IntPoint delta = Placed[afterIndex].Origin - Placed[beforeIndex].Origin;
        return StitchAxes.Enforce(Mode, delta);
    }

    /// <summary>
    /// 这条拼缝的<b>对齐位移</b>（需求二的 AlignmentOffset）在公共坐标里把 Rect2 摆到哪儿：
    /// 主轴上 = Rect1 底边 + 间隙 + Y（Y 以紧接位置为零点，负=压上去）。
    /// 界面画红箭头、日志写 alignmentOffsetBefore/After 都读参数那一份，读这里是为了拿到<b>夹过之后</b>的落位。
    /// </summary>
    public IntPoint AppliedAlignment(Seam seam)
    {
        ArgumentNullException.ThrowIfNull(seam);
        int beforeIndex = IndexOf(seam.BeforeId);
        int afterIndex = IndexOf(seam.AfterId);
        if (beforeIndex < 0 || afterIndex < 0) return IntPoint.Zero;
        IntPoint delta = Placed[afterIndex].Origin - Placed[beforeIndex].Origin;
        int tight = _horizontal ? _baseExtents[beforeIndex] + _gap : _baseExtents[beforeIndex] + _gap;
        int primary = _horizontal ? delta.X : delta.Y;
        int cross = _horizontal ? delta.Y : delta.X;
        return _horizontal
            ? new IntPoint(primary - tight, cross)
            : new IntPoint(cross, primary - tight);
    }

    /// <summary>
    /// 这条拼缝的<b>重叠带</b>在画布坐标里占哪一段（红框、命中测试、调试叠层都读这一条）。
    ///
    /// 第十二轮：带就是需求九要的那个 <c>OverlapRect = Rect1 ∩ Rect2</c>——
    /// 两个<b>完整</b>矩形（未裁重叠）的交。可见矩形之间本来就不该相交，
    /// 拿 <c>before.Rect ∩ after.Rect</c> 去量会永远得到空，红框就消失了（第八轮就是这么坏的）；
    /// 第九到十一轮改成"从裁切账里摆出来"，本轮直接回到公共坐标里那两个矩形的交，
    /// 于是"带在哪"这件事与"两侧各裁几行"彻底解耦（判据 <c>align/带就是 Rect1∩Rect2，与蓝线位置无关</c>）。
    /// 位移档保持原义：带 = 两段矩形的交（那里 Rect == FullRect）。
    /// </summary>
    public IntRect? BandCanvasRect(Seam seam)
    {
        ArgumentNullException.ThrowIfNull(seam);
        int beforeIndex = IndexOf(seam.BeforeId);
        int afterIndex = IndexOf(seam.AfterId);
        if (beforeIndex < 0 || afterIndex < 0) return null;

        IntRect overlap = Placed[beforeIndex].FullRect.Intersect(Placed[afterIndex].FullRect);
        return overlap.IsEmpty ? null : overlap;
    }

    /// <summary>
    /// 量出这条拼缝的去重账（重叠到底被吃掉了几行、蓝线落在哪儿、位移到底等于多少）。
    /// 任一段不在本布局里时返回 null。
    /// </summary>
    public SeamGeometry? DescribeSeam(Seam seam)
    {
        ArgumentNullException.ThrowIfNull(seam);
        PlacedSegment? before = Find(seam.BeforeId);
        PlacedSegment? after = Find(seam.AfterId);
        if (before is null || after is null) return null;

        IntPoint offset = EffectiveOffset(seam);
        int beforeBase = _baseExtents[before.Index];
        IntRect? band = BandCanvasRect(seam);
        // 裁切档：去重量就是"蓝线两侧各让出多少行"，与绘制顺序无关；
        // 位移档：只能从画布落位反量（前一段未裁切长度 − 位移）。
        int appliedBefore = _bottomTrims[before.Index];
        int appliedAfter = _topTrims[after.Index];
        int overlap = UsesCropPlacement ? appliedBefore + appliedAfter : beforeBase - (_horizontal ? offset.X : offset.Y);

        return new SeamGeometry
        {
            SeamId = seam.Id,
            Index = after.Index,
            // 这里报的是<b>源图那两段的尺寸</b>（裁边之后、裁重叠之前），也就是公式里的 H1/H2。
            // "裁过之后真正进像素的那一块"读 BeforeRect / AfterRect（画布）与
            // BeforeSourceRect / AfterSourceRect（源图）——三个数各管一头，判据才分得清
            // "重叠扣了一次"和"扣了两次"。
            BeforeSize = _horizontal
                ? new IntSize(_baseExtents[before.Index], before.Rect.Height)
                : new IntSize(before.Rect.Width, _baseExtents[before.Index]),
            AfterSize = _horizontal
                ? new IntSize(_baseExtents[after.Index], after.Rect.Height)
                : new IntSize(after.Rect.Width, _baseExtents[after.Index]),
            Offset = offset,
            PrimaryOffset = _horizontal ? offset.X : offset.Y,
            CrossOffset = _horizontal ? offset.Y : offset.X,
            Overlap = overlap,
            ReportedOverlap = seam.OverlapLength,
            ExpectedPrimaryOffset = UsesCropPlacement
                ? beforeBase - _topTrims[before.Index] - appliedBefore
                : beforeBase - seam.OverlapLength,
            PairExtent = _horizontal ? after.Rect.Right : after.Rect.Bottom,
            CanvasMainAxisExtent = _horizontal ? CanvasSize.Width : CanvasSize.Height,
            Manual = seam.IsManual,
            Horizontal = _horizontal,
            HorizontalLocked = StitchAxes.LocksHorizontal(Mode),
            CropPlacement = UsesCropPlacement,
            BeforeBaseExtent = beforeBase,
            AfterBaseExtent = _baseExtents[after.Index],
            BeforeTopTrim = _topTrims[before.Index],
            AppliedBeforeCrop = appliedBefore,
            AppliedAfterCrop = appliedAfter,
            RequestedBeforeCrop = Math.Max(0, seam.OverlapLength - seam.CutY),
            // 参数里写的那一个<b>裸值</b>（不夹）：只有拿它跟 AppliedAfterCrop 比，
            // "蓝线写到带外"这件事才会在这本账上露出来（判据
            // <c>guard/绕开核心字段把蓝线写到带外必被夹取与去重账抓到</c>）。
            RequestedAfterCrop = seam.CutY,
            // 报的是<b>本布局真正生效</b>的那一个数：预览覆盖时参数还是旧值，
            // 屏上画的却是覆盖后的切点——日志与判据要跟屏幕对齐，只能读落位。
            CutPosition = UsesCropPlacement ? appliedAfter : seam.CutPosition,
            CutYCanvas = UsesCropPlacement ? _cutLines[before.Index] : (_horizontal ? after.Origin.X : after.Origin.Y),
            AlignmentOffset = UsesCropPlacement ? AppliedAlignment(seam) : offset - TightOffset(seam),
            Rect1 = before.FullRect,
            Rect2 = after.FullRect,
            OverlapRect = band ?? default,
            OverlapStartY = band is null ? 0 : (_horizontal ? band.Value.X : band.Value.Y),
            OverlapEndY = band is null ? 0 : (_horizontal ? band.Value.Right : band.Value.Bottom),
            CropMode = seam.CropMode,
            ManualCut = seam.IsManualCut,
            ManualAlignment = seam.IsManualAlignment,
            BandRect = band ?? default,
            HasBand = band is not null,
            BeforeSourceRect = before.SourceRect,
            AfterSourceRect = after.SourceRect,
            BeforeRect = before.Rect,
            AfterRect = after.Rect,
            // 被裁掉的那几行仍然用<b>源图坐标</b>报出来：判据要问的是
            // "前一段最后 O 行有没有进成品"，答案只能在源图那一侧核对，不能拿画布坐标猜。
            BeforeDroppedRect = DroppedRect(before.SourceRect, _bottomTrims[before.Index], false),
            AfterDroppedRect = DroppedRect(after.SourceRect, _topTrims[after.Index], true),
        };
    }

    /// <summary>
    /// 由裁过的源矩形反推<b>被裁掉的那一条</b>源矩形（startSide=true 表示裁的是起点那一侧）。
    /// 只做整数加减，不读像素——判据用它核对"被裁掉的行不许出现在成品里"。
    /// </summary>
    private static IntRect DroppedRect(IntRect cropped, int trim, bool startSide)
    {
        if (trim <= 0 || cropped.IsEmpty) return default;
        return startSide
            ? new IntRect(cropped.X, cropped.Y - trim, cropped.Width, trim)
            : new IntRect(cropped.X, cropped.Bottom, cropped.Width, trim);
    }

    /// <summary>
    /// 整条链的去重总账：<b>画布主轴长度 = 各段未裁切长度之和 − 各重叠之和</b>。
    /// 这是"没有多吃一行、也没有重画一行"的整数级证明，与匹配算法完全无关。
    ///
    /// 裁切档里"重叠"是<b>本布局真正切掉的行数</b>（蓝线两侧让出之和，含被夹取修正的部分）。
    /// 用落位反量在裁切档是错的：两段可见块本来就首尾相接，量出来永远是 0，
    /// 那会让"重复内容被真删掉"这件事看起来像"一行都没删"。
    /// </summary>
    public DedupAccount MeasureDedup()
    {
        if (Placed.Count == 0) return new DedupAccount(0, 0, 0, 0, 0, true);

        long sumExtents = 0;
        foreach (int baseExtent in _baseExtents) sumExtents += baseExtent;

        long sumOverlap = 0;
        if (UsesCropPlacement)
        {
            // 每条缝去掉的行数 = 前一段被裁的底 + 后一段被裁的顶
            for (int i = 1; i < Placed.Count; i++) sumOverlap += _bottomTrims[i - 1] + _topTrims[i];
        }
        else
        {
            for (int i = 1; i < Placed.Count; i++)
            {
                IntPoint delta = Placed[i].Origin - Placed[i - 1].Origin;
                sumOverlap += PrimaryExtent(Placed[i - 1]) - (_horizontal ? delta.X : delta.Y);
            }
        }

        long expected = sumExtents - sumOverlap;
        // 链末端（最后一段的底边）与包围盒（所有段的最大底边）：
        // 正常链两者相同；一旦某段被挪到"倒着压住前面"，包围盒会比链末端更大，
        // 这时候必须看得出来，而不是让总账对不上却不知差在哪。
        long chainExtent = PrimaryExtent(Placed[^1]) + (_horizontal
            ? Placed[^1].Origin.X - Placed[0].Origin.X
            : Placed[^1].Origin.Y - Placed[0].Origin.Y);
        int canvasExtent = _horizontal ? CanvasSize.Width : CanvasSize.Height;

        return new DedupAccount(sumExtents, sumOverlap, expected, chainExtent, canvasExtent,
            expected == chainExtent && chainExtent == canvasExtent);
    }

    /// <summary>片段在主轴（横拼=X，其余=Y）上的长度。</summary>
    private int PrimaryExtent(PlacedSegment p) => _horizontal ? p.Rect.Width : p.Rect.Height;

    /// <summary>与给定画布矩形相交的片段（瓦片渲染与行带导出都靠它把工作量限制在视口内）。</summary>
    public IEnumerable<PlacedSegment> Intersecting(IntRect area)
    {
        foreach (PlacedSegment p in Placed)
            if (!p.IsEmpty && p.Rect.Intersects(area))
                yield return p;
    }

    /// <summary>画布坐标 → 该片段内的源图坐标。越界返回 null。</summary>
    public IntPoint? CanvasToSource(IntPoint canvasPt)
    {
        foreach (PlacedSegment p in Placed)
        {
            if (p.Rect.Contains(canvasPt))
                return new IntPoint(p.SourceRect.X + canvasPt.X - p.Origin.X,
                                    p.SourceRect.Y + canvasPt.Y - p.Origin.Y);
        }
        return null;
    }

    // ───────────────────────── 构建 ─────────────────────────

    /// <summary>
    /// <b>完整布局重算</b>发生过多少次。它是给判据量的，不是给产品逻辑用的：
    /// 需求三第 6 条明确"调试 HUD 不得调用完整 CanvasLayout.Build"——这句话只能靠计数判得出真假
    /// （绘制那一层本来就该只读 <c>Project.Layout</c> 那份缓存）。
    /// 与 <c>CanvasComposer.WholeRunCountForTest</c> 同一地位：只有工装与判据读它。
    /// </summary>
    public static long BuildCountForTest { get; private set; }

    /// <summary>
    /// 由文档算出布局。<paramref name="doc"/> 为 null 或无片段时返回空布局（0×0），
    /// 不抛异常——首屏就是空项目，界面要能正常画。
    /// </summary>
    /// <param name="seamOffsetOverrides">
    /// <b>仅供拖动预览</b>（位移档）：按拼缝 Id 临时覆盖其生效位移。
    /// 存在的意义是"拖动过程中一格都不写进项目参数"——于是
    /// ① 一次拖动结束时只产生一条可撤销记录（需求 §5.2 明确要求），
    /// ② 拖动中途松手/取消不会留下半套参数。
    /// 除拖动预览外，任何地方都不该传这个参数。
    /// </param>
    /// <param name="seamAlignmentOverrides">
    /// <b>仅供拖动预览</b>（长截图）：按拼缝 Id 临时覆盖（对齐位移 Y, 蓝线带内行号）。
    /// 拖红箭头改前一项、拖蓝线改后一项，两条手势各走各的（需求七）。
    /// 预览期间两个都不许写进参数。
    /// </param>
    public static CanvasLayout Build(ProjectDocument? doc,
        IReadOnlyDictionary<string, IntPoint>? seamOffsetOverrides = null,
        IReadOnlyDictionary<string, SeamAlignment>? seamAlignmentOverrides = null)
    {
        BuildCountForTest++;
        if (doc is null || doc.Segments.Count == 0)
        {
            StitchMode emptyMode = doc?.Mode ?? StitchMode.LongScroll;
            return new CanvasLayout(emptyMode, Array.Empty<PlacedSegment>(), IntSize.Zero,
                emptyMode == StitchMode.Horizontal, 0,
                Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>());
        }

        bool horizontal = doc.Mode == StitchMode.Horizontal;
        bool cropPlacement = StitchAxes.UsesCropPlacement(doc.Mode);
        int gap = Math.Max(0, doc.Spacing.Gap);
        Insets globalCrop = doc.Crop.Enabled ? doc.Crop.Uniform : Insets.Zero;
        var placed = new List<PlacedSegment>(doc.Segments.Count);

        // 第一遍：算出每段的有效矩形（全局裁边与单张裁边叠加，取更狠的一边）
        var rects = new IntRect[doc.Segments.Count];
        for (int i = 0; i < doc.Segments.Count; i++)
        {
            Segment seg = doc.Segments[i];
            SourceImage? src = doc.FindSource(seg.SourceId);
            IntRect baseRect = seg.EffectiveRect(globalCrop);

            if (src is null)
            {
                // 源图丢失：保留占位但尺寸为 0。绝不用低清预览顶替原图（需求 §4.2）。
                rects[i] = IntRect.Empty;
            }
            else
            {
                // 有效矩形必须再夹进源图实际尺寸：项目文件可能被外部改过，
                // 或者源图被替换成了更小的同名文件。夹住之后宁可少画，也不许越界读像素。
                rects[i] = baseRect.Intersect(new IntRect(0, 0, src.Size.Width, src.Size.Height));
            }
        }

        // ═══ 第一遍半（第十二轮）：先在公共坐标里摆出两个<b>完整</b>矩形，再用蓝线切出可见块 ═══
        // 只有长截图走这里。顺序与需求四/五逐条对应：
        //   ① Rect1 / Rect2 由<b>对齐位移</b>落位——这时两段<b>是相交的</b>，交叠那一段就是重叠带；
        //   ② OverlapRect = Rect1 ∩ Rect2；
        //   ③ 蓝线 CutY 夹进带里，得到这条缝的"内容来源分界" cut；
        //   ④ 两侧各让出多少行 = 蓝线两侧的分界位置（算出来的），反算回源图就是裁切矩形。
        // 于是 _topTrims / _bottomTrims 不再是"参数里凭空给的两个数"，
        // 而 b + t = O 也从"需要有人守住的不变式"降级成"同一段区间的两种数法"。
        int count = doc.Segments.Count;
        var baseExtents = new int[count];
        for (int i = 0; i < count; i++) baseExtents[i] = horizontal ? rects[i].Width : rects[i].Height;

        var topTrims = new int[count];
        var bottomTrims = new int[count];
        var fullRects = (IntRect[])rects.Clone();     // Rect1/Rect2 的源图那一版（未裁重叠）
        var origins = new IntPoint[count];
        origins[0] = IntPoint.Zero;
        var cutLines = new int[count];                // cutLines[i] = 第 i 段与第 i+1 段之间那条缝的蓝线
        if (cropPlacement)
        {
            for (int i = 1; i < count; i++)
            {
                Seam? seam = doc.FindSeam(doc.Segments[i - 1].Id, doc.Segments[i].Id);

                // ──  对齐位移与蓝线：核心那两个量，拖动预览的覆盖值优先 ──
                int alignmentY = seam?.AlignmentOffset.Y ?? 0;
                int cutY = seam?.CutY ?? 0;
                if (seam is not null && seamAlignmentOverrides is not null &&
                    seamAlignmentOverrides.TryGetValue(seam.Id, out SeamAlignment forced))
                {
                    alignmentY = forced.AlignmentY;
                    cutY = forced.CutY;
                }
                // 横向：长截图恒 0（轴规则在这里再兜一道，不假设调用点已经夹过）
                int alignmentX = seam is null || StitchAxes.LocksHorizontal(doc.Mode)
                    ? 0 : seam.AlignmentOffset.X;
                int wantedOverlap = Math.Max(0, -alignmentY);

                // ═══ 旧项目文件的迁移：账上没记重叠、却记了一个主轴位移 ═══
                // v1 到第八轮的项目文件里根本没有对齐这个概念，只有 ManualOffset/AutoOffset
                // （旧项目、以及"直接写 Y"这类调用点都是这个形状）。落回"紧接排列"就等于把
                // 用户摆出来的那一段重叠悄悄吃掉：屏上看着拼好了，参数却说一行都没去。
                // 这里按主轴位移反推这条缝实际要去多少行，<b>仍然只走对齐这一条路</b>（Y = −重叠）。
                // 若账上明明写着 O>0，绝不能拿 Y 把它悄悄补回来——
                // 那正好把"参数与落位不一致"这个最该报警的形状抹平。
                if (seam is not null && wantedOverlap == 0 && cutY == 0)
                {
                    int? explicitPrimary =
                        seamOffsetOverrides is not null &&
                        seamOffsetOverrides.TryGetValue(seam.Id, out IntPoint legacyForced)
                            ? (horizontal ? legacyForced.X : legacyForced.Y)
                            : seam.ManualOffset is IntPoint manual
                                ? (horizontal ? manual.X : manual.Y)
                                : seam.AutoOffset is IntPoint auto
                                    ? (horizontal ? auto.X : auto.Y)
                                    : null;
                    if (explicitPrimary is int y)
                    {
                        int implied = baseExtents[i - 1] + gap - y;
                        if (implied > 0)
                        {
                            wantedOverlap = implied;
                            alignmentY = -implied;
                        }
                    }
                }

                // ──  落位 Rect2：图2 顶边 = 图1 底边 + 间隙 − 重叠 ──
                // 夹两头的理由：重叠不能把前一段吃光（还要留自己的可见块），也不能超过后一段本身
                // （带必须真在 Rect2 里，否则蓝线无处可放）。"重叠写得比图还高"这种坏参数
                // （手工编辑过的项目文件真能造出来）只会让带变短，既不会裁出负尺寸也不会让总账对不上。
                int roomBefore = Math.Max(0, baseExtents[i - 1] - topTrims[i - 1]);
                int appliedOverlap = Math.Clamp(wantedOverlap, 0, Math.Min(roomBefore + gap, baseExtents[i]));
                origins[i] = origins[i - 1] + (horizontal
                    ? new IntPoint(alignmentX + baseExtents[i - 1] + gap - appliedOverlap, 0)
                    : new IntPoint(alignmentX, baseExtents[i - 1] + gap - appliedOverlap));

                // ──  蓝线：带 = [Rect2.Top, Rect1.Bottom)，蓝线只能落在带里 ──
                int bandTop = horizontal ? origins[i].X : origins[i].Y;
                int bandBottom = (horizontal ? origins[i - 1].X : origins[i - 1].Y) + baseExtents[i - 1];
                int cut = bandTop + Math.Clamp(cutY, 0, Math.Max(0, bandBottom - bandTop));
                cutLines[i - 1] = cut;

                // ── ④ 两侧各让出多少行：蓝线两侧的分界位置，<b>算出来的</b> ──
                topTrims[i] = cut - bandTop;                    // 带里由图1 提供 → 图2 顶部让出
                bottomTrims[i - 1] = bandBottom - cut;          // 带里由图2 提供 → 图1 底部让出
            }

            // 把切掉的行数折进矩形：主轴起点往后挪 topTrim、长度减掉两侧让出的行数。
            // 这就是需求五说的"最终渲染必须使用裁切后的矩形"——矩形从这里开始就是切过的，
            // 合成器、瓦片、导出读的都是它，没有任何一条通路还能拿到未切的完整原图。
            for (int i = 0; i < count; i++)
            {
                int trim = topTrims[i] + bottomTrims[i];
                if (trim <= 0 || rects[i].IsEmpty) continue;
                rects[i] = horizontal
                    ? new IntRect(rects[i].X + topTrims[i], rects[i].Y,
                        Math.Max(0, rects[i].Width - trim), rects[i].Height)
                    : new IntRect(rects[i].X, rects[i].Y + topTrims[i],
                        rects[i].Width, Math.Max(0, rects[i].Height - trim));
            }
        }

        // 第二遍：位移档沿拼缝链累加位置（裁切档的落位已经在上面按对齐 + 蓝线摆好了）
        int minX = 0, minY = 0, maxX = 0, maxY = 0;

        for (int i = 1; i < count; i++)
        {
            if (cropPlacement) continue;

            Segment prev = doc.Segments[i - 1];
            Segment cur = doc.Segments[i];
            Seam? seam = doc.FindSeam(prev.Id, cur.Id);

            IntRect prevRect = rects[i - 1];
            // 紧接排列：纵向模式下推一段的高度，横向模式下推一段的宽度
            var tight = horizontal
                ? new IntPoint(prevRect.Width + gap, 0)
                : new IntPoint(0, prevRect.Height + gap);

            IntPoint raw = seam is null ? tight
                : seamOffsetOverrides is not null && seamOffsetOverrides.TryGetValue(seam.Id, out IntPoint forcedOffset)
                    ? forcedOffset
                    : seam.ResolveOffset(tight);

            // ═══ 像素落位的最后一道闸 ═══
            // 位移在这里按模式归零（长截图锁横向）。参数里可能仍存着一个带 X 的建议
            // （那是"数据想横着挪"的证据，界面要说清楚），但它到不了像素这一层。
            // 这一道必须留在布局里：旧项目文件、别的调用点、忘了走 MatchOptions.For 的分析
            // 都可能送来带 X 的位移，只有落位处兜住，才能保证"屏上说的 X"与"图上的 X"永远是 0。
            IntPoint offset = StitchAxes.Enforce(doc.Mode, raw);
            origins[i] = origins[i - 1] + offset;
        }

        // 第三遍：交叉轴对齐（竖拼按宽度、横拼按高度）。
        // 说明：对齐是在拼缝链算出的坐标之上"附加"一个整数偏移，所以用户手动调过的
        // 交叉位移依然有效、只是整体平移；这样两种意图不会互相抹掉。
        // 长截图模式不做对齐——它的横向位移本身就是匹配结果的一部分，动它会破坏去重。
        if (doc.Mode != StitchMode.LongScroll && doc.Spacing.Align != CrossAlign.Near)
        {
            int crossMax = horizontal
                ? rects.Max(r => r.Height)
                : rects.Max(r => r.Width);
            for (int i = 0; i < doc.Segments.Count; i++)
            {
                int segCross = horizontal ? rects[i].Height : rects[i].Width;
                int delta = CrossDelta(crossMax, segCross, doc.Spacing.Align);
                origins[i] = horizontal
                    ? origins[i] + new IntPoint(0, delta)
                    : origins[i] + new IntPoint(delta, 0);
            }
        }

        // 第四遍：包围盒与归一化（把最左上角挪到 (0,0)）。
        // 量的是<b>可见块</b>的包围盒：裁切档里完整矩形是相交的，拿完整矩形量会把画布算大；
        // 而可见块的原点 = 完整矩形原点 + 起点侧让出的行数，所以这里必须先把两个原点分开算。
        var visibleOrigins = new IntPoint[count];
        for (int i = 0; i < count; i++)
            visibleOrigins[i] = origins[i] + (horizontal ? new IntPoint(topTrims[i], 0) : new IntPoint(0, topTrims[i]));
        for (int i = 0; i < doc.Segments.Count; i++)
        {
            int x = visibleOrigins[i].X, y = visibleOrigins[i].Y;
            minX = Math.Min(minX, x); minY = Math.Min(minY, y);
            maxX = Math.Max(maxX, x + rects[i].Width);
            maxY = Math.Max(maxY, y + rects[i].Height);
        }
        var shift = new IntPoint(-minX, -minY);

        // 蓝线跟着整体平移；位移档没有蓝线（cutLines 全 0），不参与判定。
        if (cropPlacement)
            for (int i = 0; i < count; i++) cutLines[i] += horizontal ? shift.X : shift.Y;

        for (int i = 0; i < doc.Segments.Count; i++)
        {
            Segment seg = doc.Segments[i];
            SourceImage? src = doc.FindSource(seg.SourceId);
            if (src is null)
            {
                // 缺失源图：仍然占位，让序号与拼缝链保持完整，界面据此显示"源文件丢失"
                src = new SourceImage
                {
                    Id = seg.SourceId,
                    Path = "",
                    FileName = "(源文件丢失)",
                    Size = IntSize.Zero,
                    SourceMissing = true,
                };
            }
            IntPoint fullOrigin = origins[i] + shift;      // Rect1/Rect2 的原点（未切重叠）
            placed.Add(new PlacedSegment
            {
                Segment = seg,
                Source = src,
                Index = i,
                SourceRect = rects[i],
                // Origin 是<b>可见块</b>的原点：所有取数通路（瓦片、合成、导出、蓝线）都读它，
                // 于是"完整矩形相交"这件事只存在于 FullRect / BandCanvasRect 这一侧，
                // 不可能漏到像素层去——像素层从头到尾只见过切过的矩形。
                Origin = visibleOrigins[i] + shift,
                FullSourceRect = fullRects[i],
                FullRect = new IntRect(fullOrigin.X, fullOrigin.Y, fullRects[i].Width, fullRects[i].Height),
                GlobalCrop = globalCrop,
                IncomingSeam = i == 0 ? null : doc.FindSeam(doc.Segments[i - 1].Id, seg.Id),
            });
        }

        var size = new IntSize(Math.Max(0, maxX - minX), Math.Max(0, maxY - minY));
        return new CanvasLayout(doc.Mode, placed.ToArray(), size, horizontal, gap,
            baseExtents, topTrims, bottomTrims, cutLines);
    }

    /// <summary>交叉轴居中/靠右(靠下)的整数偏移。居中用整除，偏向左上，规则固定可预期。</summary>
    private static int CrossDelta(int crossMax, int segCross, CrossAlign align) => align switch
    {
        CrossAlign.Center => Math.Max(0, (crossMax - segCross) / 2),
        CrossAlign.Far => Math.Max(0, crossMax - segCross),
        _ => 0,
    };
}

/// <summary>
/// 一条拼缝的"去重账"——长截图的核心承诺全部落在这几个整数上：
///
/// <code>
///   裁切档（长截图）：Rect2.Top = Rect1.Bottom + gap + AlignmentOffset.Y
///                    OverlapRect = Rect1 ∩ Rect2
///                    CutY ∈ [OverlapRect.Top, OverlapRect.Bottom]
///                    图1 可见 = Rect1 ∩ (-∞, CutY)     图2 可见 = Rect2 ∩ [CutY, +∞)
///                    画布主轴长 = Σ各段未裁切长 − Σ重叠
///   位移档（竖拼/横拼）：offsetY = 前一段长 − 重叠
/// </code>
///
/// 为什么要把它做成一个可查询的结果，而不是在测试里各算一遍：
/// 用户担心的正是"嘴上说切掉了重叠，实际把第二张整张重画了一遍"。
/// 那种做法在这里会被三处同时抓出来：<see cref="RectsDisjoint"/>（两段可见块还盖在一起）、
/// <see cref="AppliedBeforeCrop"/>+<see cref="AppliedAfterCrop"/>（让出的行数根本不等重叠）、
/// 以及 <see cref="CanvasLayout.MeasureDedup"/> 的总账。
/// </summary>
public sealed record SeamGeometry
{
    public required string SeamId { get; init; }

    /// <summary>后一段在布局里的序号（从 0 开始）。这条缝就是 #Index-1 与 #Index 之间那一条。</summary>
    public required int Index { get; init; }

    public required IntSize BeforeSize { get; init; }
    public required IntSize AfterSize { get; init; }

    /// <summary>生效位移（裁切档里它是<b>对齐 + 蓝线摆出来的结果</b>，不是参数）。长截图里 X 恒为 0。</summary>
    public required IntPoint Offset { get; init; }

    /// <summary>主轴方向上的生效位移（长截图/竖拼=Y，横拼=X）。</summary>
    public required int PrimaryOffset { get; init; }

    /// <summary>交叉轴上的生效位移。长截图锁横向之后这个数必须是 0。</summary>
    public required int CrossOffset { get; init; }

    /// <summary>
    /// 这条缝去掉的行数。
    /// 裁切档 = 蓝线两侧让出的行数之和（布局真切掉的），位移档 = 从画布落位反量出来的重叠。
    /// </summary>
    public required int Overlap { get; init; }

    /// <summary>分析记录在拼缝上的重叠（<c>Seam.OverlapLength</c>），也就是"声称去掉多少行"。</summary>
    public required int ReportedOverlap { get; init; }

    /// <summary>
    /// 按公式应有的主轴位移。
    /// 裁切档：前一段未裁切长 − 它自己的顶裁 − 这条缝给它的底裁；位移档：前一段未裁切长 − 声称重叠。
    /// </summary>
    public required int ExpectedPrimaryOffset { get; init; }

    /// <summary>后一段主轴底边所在的画布坐标：这两段合起来的实际总跨度。</summary>
    public required int PairExtent { get; init; }

    /// <summary>这条缝是否被用户手动挪过位移。手动缝的"声称重叠"是上一次分析的旧值，天然会对不上。</summary>
    public required bool Manual { get; init; }

    /// <summary>当前模式是否锁定横向。</summary>
    public required bool HorizontalLocked { get; init; }

    /// <summary>当前模式是否为横拼（决定"主轴"是 X 还是 Y）。</summary>
    public required bool Horizontal { get; init; }

    // ───────────────────────── 裁切账 ─────────────────────────

    /// <summary>本条缝所在模式是否走裁切去重（长截图=true）。位移档里下面这些数都是 0/默认，不参与判定。</summary>
    public required bool CropPlacement { get; init; }

    /// <summary>前一段<b>未裁切</b>（只经过裁边）的主轴长度，也就是公式里的 H1。</summary>
    public required int BeforeBaseExtent { get; init; }

    /// <summary>后一段未裁切的主轴长度（H2）。</summary>
    public required int AfterBaseExtent { get; init; }

    /// <summary>前一段自己头顶上被上一条缝切掉的行数。算这条缝的期望位移时必须先减掉它。</summary>
    public required int BeforeTopTrim { get; init; }

    /// <summary>布局实际切掉的：前一段底部（= 蓝线到 Rect1 底边的距离，带里由图2 提供的那几行）。</summary>
    public required int AppliedBeforeCrop { get; init; }

    /// <summary>布局实际切掉的：后一段顶部（= 蓝线到 Rect2 顶边的距离，带里由图1 提供的那几行）。</summary>
    public required int AppliedAfterCrop { get; init; }

    /// <summary>参数里写着的前一段底部裁切量（可能因超出可见长度而被夹小）。</summary>
    public required int RequestedBeforeCrop { get; init; }

    /// <summary>参数里写着的后一段顶部裁切量。</summary>
    public required int RequestedAfterCrop { get; init; }

    /// <summary>蓝线在重叠带内的位置（0=带首，整带用图2；O=带尾，整带用图1）。</summary>
    public required int CutPosition { get; init; }

    /// <summary>蓝线在<b>本布局公共坐标</b>里的主轴位置（画布行号）。屏上那条线画的就是它。</summary>
    public required int CutYCanvas { get; init; }

    /// <summary>本布局实际采用的<b>对齐位移</b>（以紧接位置为零点）。需求十一那两条 alignmentOffsetBefore/After 读它。</summary>
    public required IntPoint AlignmentOffset { get; init; }

    /// <summary>图1 的<b>完整</b>公共坐标矩形（Rect1，未切重叠）。红框、命中区、调试叠层的"图1 有效区域"都读它。</summary>
    public required IntRect Rect1 { get; init; }

    /// <summary>图2 的完整公共坐标矩形（Rect2）。</summary>
    public required IntRect Rect2 { get; init; }

    /// <summary>重叠带 = <c>Rect1 ∩ Rect2</c>（需求九要的命中区，与蓝线位置无关）。</summary>
    public required IntRect OverlapRect { get; init; }

    /// <summary>带首（画布主轴坐标）。蓝线的下界。</summary>
    public required int OverlapStartY { get; init; }

    /// <summary>带尾（画布主轴坐标）。蓝线的上界。</summary>
    public required int OverlapEndY { get; init; }

    /// <summary>蓝线的来源（自动 / 保留上 / 保留下 / 对半 / 手动拖）。</summary>
    public required SeamCropMode CropMode { get; init; }

    /// <summary>用户是否拖过这条缝的蓝线。</summary>
    public required bool ManualCut { get; init; }

    /// <summary>用户是否拖过这条缝的对齐（红箭头）。</summary>
    public required bool ManualAlignment { get; init; }

    /// <summary>重叠带在画布坐标里的矩形（红框、命中区、调试叠层都用它）——就是 <see cref="OverlapRect"/>。</summary>
    public required IntRect BandRect { get; init; }

    /// <summary>有没有带（重叠为 0 时没有带）。</summary>
    public required bool HasBand { get; init; }

    /// <summary>前一段真正进像素的那一块源矩形。</summary>
    public required IntRect BeforeSourceRect { get; init; }

    /// <summary>后一段真正进像素的那一块源矩形（起点已被蓝线往后挪）。</summary>
    public required IntRect AfterSourceRect { get; init; }

    /// <summary>两段<b>可见</b>矩形。裁切档必须不相交——相交就说明还在靠绘制顺序去重。</summary>
    public required IntRect BeforeRect { get; init; }
    public required IntRect AfterRect { get; init; }

    /// <summary>前一段在源图里被切掉的那几行（"被切掉的行不许出现在成品里"这类判据读它）。</summary>
    public required IntRect BeforeDroppedRect { get; init; }

    /// <summary>后一段在源图里被切掉的那几行。</summary>
    public required IntRect AfterDroppedRect { get; init; }

    /// <summary>前段未裁切的主轴长度（公式里的 H1）。</summary>
    public int BeforeExtent => BeforeBaseExtent;

    /// <summary>后段未裁切的主轴长度（公式里的 H2）。</summary>
    public int AfterExtent => AfterBaseExtent;

    /// <summary>
    /// 位移是否等于公式值（手动挪过位移的缝不参与这条判定）。
    /// 裁切档额外要求"去掉的行数 == 分析声称的重叠"：
    /// 少了这一半，"把 O 设成 0（等于没去重）但位移仍然自洽"这种形状就会从判据里溜过去。
    /// </summary>
    public bool FormulaHolds => Manual ||
        (PrimaryOffset == ExpectedPrimaryOffset &&
         (!CropPlacement || Overlap == ReportedOverlap));

    /// <summary>去掉的行数与分析声称的是否一致——不一致就是"没真去重"。</summary>
    public bool OverlapMatchesReport => Overlap == ReportedOverlap;

    /// <summary>锁横向的模式下交叉轴必须真的是 0。</summary>
    public bool CrossAxisHolds => !HorizontalLocked || CrossOffset == 0;

    /// <summary>
    /// 不变式：两侧让出的行数之和 == 带高，且蓝线落在带里。
    /// 第十二轮起这是<b>推导的副产品</b>（两侧就是同一段区间的两种数法），
    /// 留着它是为了当钉子：谁把蓝线改成"先分配两个数"，这条会先红。
    /// </summary>
    public bool CropInvariantHolds =>
        AppliedBeforeCrop + AppliedAfterCrop == Overlap &&
        CutPosition >= 0 && CutPosition <= Overlap;

    /// <summary>蓝线是不是真的落在带里（<c>OverlapRect.Top ≤ CutY ≤ OverlapRect.Bottom</c>）。</summary>
    public bool CutInsideBand => !HasBand || (CutYCanvas >= OverlapStartY && CutYCanvas <= OverlapEndY);

    /// <summary>切掉的量有没有被布局夹小（参数与落位不一致）。真出现时界面要出声，不能悄悄吞。</summary>
    public bool CropsAppliedAsRequested =>
        AppliedBeforeCrop == RequestedBeforeCrop && AppliedAfterCrop == RequestedAfterCrop;

    /// <summary>两段<b>可见</b>矩形是否不再交叠。裁切档里这是"重叠只进了一次像素"的<b>结构</b>证明。</summary>
    public bool RectsDisjoint => !BeforeRect.Intersects(AfterRect);

    /// <summary>带的高（横拼是宽）是否正好等于去掉的行数（带画歪了就是命中区与去重对不上）。</summary>
    public bool BandMatchesDedup => !HasBand || (Horizontal ? BandRect.Width : BandRect.Height) == Overlap;

    /// <summary>
    /// 这张布局在主轴上的总长（长截图=画布高，横拼=画布宽）。
    /// 需求十要求 <c>seam.geometry</c> / <c>export.seam.crop</c> 也带 canvasHeight：
    /// 没有这一个数，日志里只剩"这条缝去掉几行"，看不出整张画布因此变成多高，
    /// "总高 = Σ各段 − Σ重叠"那本账就没法从一条日志里核。
    /// </summary>
    public required int CanvasMainAxisExtent { get; init; }

    /// <summary>
    /// 诊断日志用的那一行（<c>seam.geometry</c> 与 <c>export.seam.crop</c> 都拼在它上面）。
    /// 只写尺寸、坐标、裁切、状态——日志里永远不许出现像素内容或隐私数据。
    /// 字段名一律用需求十一那份驼峰写法（本行是状态行，没有 before/after 之分：
    /// <c>cutY</c> 就是当前蓝线，<c>canvasHeight</c> 就是这张布局此刻的主轴总长=画布高）。
    /// </summary>
    public override string ToString() =>
        $"seam={SeamId} pair=#{Index - 1}→#{Index} crop={Low(CropPlacement)} " +
        $"rect1=({Rect1.X},{Rect1.Y},{Rect1.Width},{Rect1.Height}) " +
        $"rect2=({Rect2.X},{Rect2.Y},{Rect2.Width},{Rect2.Height}) " +
        $"overlapRect=({OverlapRect.X},{OverlapRect.Y},{OverlapRect.Width},{OverlapRect.Height}) " +
        $"overlapStartY={OverlapStartY} overlapEndY={OverlapEndY} cutY={CutYCanvas} cutYInBand={Low(CutInsideBand)} " +
        $"alignmentOffset=({AlignmentOffset.X},{AlignmentOffset.Y}) " +
        $"beforeSourceRect=({BeforeSourceRect.X},{BeforeSourceRect.Y},{BeforeSourceRect.Width},{BeforeSourceRect.Height}) " +
        $"afterSourceRect=({AfterSourceRect.X},{AfterSourceRect.Y},{AfterSourceRect.Width},{AfterSourceRect.Height}) " +
        $"beforeRect={BeforeRect} afterRect={AfterRect} " +
        $"beforeBase={BeforeBaseExtent} afterBase={AfterBaseExtent} beforeTopTrim={BeforeTopTrim} " +
        $"beforeCropBottom={AppliedBeforeCrop} afterCropTop={AppliedAfterCrop} " +
        $"requestedCrops=({RequestedBeforeCrop},{RequestedAfterCrop}) overlap={Overlap} " +
        $"overlapLength={ReportedOverlap} reportedOverlap={ReportedOverlap} cut={CutPosition} " +
        $"cropMode={CropMode} manualCut={Low(ManualCut)} manualAlignment={Low(ManualAlignment)} " +
        $"band={BandRect} bandHas={Low(HasBand)} canvasHeight={CanvasMainAxisExtent} " +
        $"offset=({Offset.X},{Offset.Y}) primary={PrimaryOffset} cross={CrossOffset} " +
        $"expectedPrimaryOffset={ExpectedPrimaryOffset} pairExtent={PairExtent} manual={Low(Manual)} " +
        $"xLocked={Low(HorizontalLocked)} formulaHolds={Low(FormulaHolds)} overlapMatches={Low(OverlapMatchesReport)} " +
        $"cropHolds={Low(CropInvariantHolds)} cropsAsRequested={Low(CropsAppliedAsRequested)} " +
        $"rectsDisjoint={Low(RectsDisjoint)} bandMatchesDedup={Low(BandMatchesDedup)}";

    /// <summary>布尔写成小写 true/false：日志要被人 grep、也要被判据逐字比（C# 默认给 True/False）。</summary>
    private static string Low(bool value) => value ? "true" : "false";
}

/// <summary>
/// 整条链的去重总账：把「各段长度之和 − 各重叠之和」与画布实际长度摆在一起。
/// 纯整数核算，与匹配算法无关，所以匹配就算全错也骗不过这一格。
/// </summary>
/// <param name="SumExtents">各段主轴长度之和。</param>
/// <param name="SumOverlaps">各拼缝重叠之和（带符号：留缝为负）。</param>
/// <param name="Expected">公式算出的画布主轴长度。</param>
/// <param name="ChainExtent">沿拼缝链走到最后一段底边的长度。</param>
/// <param name="CanvasExtent">画布包围盒的主轴长度（界面与导出用的那一个数）。</param>
/// <param name="Holds">三者是否完全一致。</param>
public readonly record struct DedupAccount(
    long SumExtents, long SumOverlaps, long Expected, long ChainExtent, int CanvasExtent, bool Holds)
{
    /// <summary>画布比公式多出（负数=少掉）多少行。0 才算账对上了。</summary>
    public long Unexplained => CanvasExtent - Expected;
}
