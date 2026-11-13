using LongStitch.Core.Geometry;
using LongStitch.Core.Imaging;
using LongStitch.Core.Model;

namespace LongStitch.Core.Matching;

/// <summary>
/// 匹配参数。
///
/// ═══ 这些阈值全部需要真图标定 ═══
/// 需求 §11 把"自动匹配通过阈值"列为待产品确认项，不是随便写的：
/// 阈值定高了大量正常拼缝被标成"需检查"（用户烦），定低了错缝被标成 ✓（用户更烦，
/// 而且他不知道哪条是错的）。所以这里每个阈值都带注释说明它偏松/偏严的后果，
/// 标定时的判据写在 docs/匹配算法.md，靠 Checks 里的合成样本电池驱动。
/// </summary>
public sealed record MatchOptions
{
    /// <summary>少于这么多行的重叠不认为是有效重叠（太短的重叠没有区分度）。</summary>
    public int MinOverlap { get; init; } = 24;

    /// <summary>粗搜阶段的横向分块数。越大越能分辨横向错位，也越慢。</summary>
    public int Bins { get; init; } = 48;

    /// <summary>粗搜每个候选最多采样多少行（超过则等间隔抽样）。</summary>
    public int TargetSamples { get; init; } = 128;

    /// <summary>精修时纵向上下搜索范围（源图像素）。</summary>
    public int RefineDy { get; init; } = 4;

    /// <summary>精修时横向左右搜索范围（源图像素）。需求 §5.1 允许少量横向位移。</summary>
    public int RefineDx { get; init; } = 6;

    /// <summary>精修最多比较多少行（靠近接缝的部分最有信息量，全比太慢）。</summary>
    public int RefineRows { get; init; } = 240;

    /// <summary>精修时的行/列抽样步长。2 表示隔一行取一行；只影响速度，不影响结论方向。</summary>
    public int RefineStride { get; init; } = 2;

    /// <summary>相似度（1 − 归一化平均差）达到此值才允许自动判 ✓。偏严 → 更多"需检查"。</summary>
    // Automatic acceptance is intentionally strict. A mostly-flat screenshot
    // can score 97% while containing no common content at all; that must be a
    // manual-review result rather than a green checkmark.
    public double PassSimilarity { get; init; } = 0.985;

    /// <summary>
    /// 唯一性下限：最佳解与"最近对手"的归一化差距小于此值时，判"存在多个候选"。
    ///
    /// 这个门限是真图跑出来的第一个<b>有区分力</b>的阈值。绝对拟合度（<see cref="PassSimilarity"/>）
    /// 在真样本上从 0.90 扫到 0.999 结果一格不变——对得上的和差 4 行的都报 100.00%，
    /// 因为它只回答"贴得好不好"，不回答"是不是只有这一个地方贴得好"。
    /// </summary>
    public double MinUniqueness { get; init; } = 0.0005;

    /// <summary>判定"两个候选算不同解"的最小间距；小于它的都视为同一个峰。</summary>
    public int PeakSeparation { get; init; } = 12;

    /// <summary>参与打分的列区间（把滚动条排除在外）。null 表示整幅宽度都参与。</summary>
    public IntRange? ContentColumns { get; init; }

    /// <summary>
    /// 是否锁定横向（长截图模式）。为 true 时：
    ///   · 采纳的重叠与位移只在 <c>dx = 0</c> 这一列里选，输出的 <see cref="MatchResult.Offset"/>
    ///     的 X 恒为 0——像素不会因为这次匹配而左右错开；
    ///   · 但"不限横向时最佳解的 dx"照样量出来，放进
    ///     <see cref="MatchResult.DetectedHorizontalShift"/>，并把这条缝降级成"需检查"。
    /// 悄悄把 X 抹平是最坏的做法：那等于用一个看起来通过的结论掩盖截图宽度不一致。
    ///
    /// 这个字段由 <see cref="For"/> 按拼合模式生成，别在调用点手写。
    /// </summary>
    public bool LockHorizontal { get; init; }

    public static MatchOptions Default { get; } = new();

    /// <summary>
    /// 按拼合模式生成参数。<b>模式是唯一依据</b>：传进来的 <see cref="LockHorizontal"/>
    /// 在这里会被覆写，所以"竖拼的分析被误锁横向"和"长截图的分析忘了锁"两种漏改都到不了产品里。
    /// </summary>
    /// <param name="mode">当前项目的拼合模式。</param>
    /// <param name="baseOptions">其他已调好的阈值（保留），null 用默认值。</param>
    public static MatchOptions For(StitchMode mode, MatchOptions? baseOptions = null)
    {
        MatchOptions opt = baseOptions ?? Default;
        bool lockHorizontal = StitchAxes.LocksHorizontal(mode);
        return opt.LockHorizontal == lockHorizontal ? opt : opt with { LockHorizontal = lockHorizontal };
    }
}

/// <summary>一条拼缝的匹配结论。</summary>
public sealed record MatchResult
{
    /// <summary>建议位移：后一段原点相对前一段原点的整数偏移。</summary>
    public required IntPoint Offset { get; init; }

    /// <summary>
    /// 唯一性 0–1：最佳解与"附近同样好的解"之间的差距，衡量的是<b>这个位置是不是唯一解释</b>。
    ///
    /// 为什么不能只看 <see cref="Confidence"/>：真图跑过一轮后发现，相似度对
    /// "对得上"和"差 4 行但同样说得通"给出的都是 100.00%——绝对拟合度只说"贴得好不好"，
    /// 不说"是不是只有这一个地方贴得好"。用它当阈值，从 0.90 扫到 0.999 结果一格不变，
    /// ✓ / ! 就成了装饰。所以自动放行必须同时看唯一性。
    /// </summary>
    public double Uniqueness { get; init; }

    /// <summary>相似度（1 − 归一化平均差）。</summary>
    public double Confidence { get; init; }

    /// <summary>判定的重叠行数（源图像素）。</summary>
    public int OverlapLength { get; init; }

    /// <summary>横向位移（少量错位）。长截图锁横向后这里恒为 0——它是"真正会被采用的值"。</summary>
    public int HorizontalShift { get; init; }

    /// <summary>
    /// 不做任何限制时最佳解的横向位移。锁横向时它是"数据想横着挪多少"的原始证据：
    /// 采纳值归零、这个值保留，界面与日志才能说出被锁掉的到底是什么，而不是掩盖。
    /// </summary>
    public int DetectedHorizontalShift { get; init; }

    /// <summary>是否因为"模式锁横向但匹配到了横向位移"而需要人工确认。</summary>
    public bool HorizontalShiftLocked { get; init; }

    // ───────────────────────── 公共坐标里的对齐与重叠带（第十二轮，需求三） ─────────────────────────
    //
    // 匹配器原来只交"位移 + 重叠行数"，界面与布局还需要知道
    // **重叠带落在公共坐标的哪一段**、以及**建议蓝线从哪一格切开**。
    // 这几个量全部做成**派生属性**：只存 HeightBefore / WidthBefore / OverlapLength / SeamCutPosition
    // 四个原始数，其余由公式来，所以<b>不可能出现"带起止与重叠行数互相矛盾"这种半套结果</b>。

    /// <summary>参与匹配的前一段高度（源图行）。重叠不足等提前返回的路径里为 0。</summary>
    public int HeightBefore { get; init; }

    /// <summary>参与匹配的前一段宽度（重叠不足等提前返回的路径里为 0）。</summary>
    public int WidthBefore { get; init; }

    /// <summary>参与匹配的后一段宽度。</summary>
    public int WidthAfter { get; init; }

    /// <summary>
    /// 图2 相对图1 的<b>对齐位移</b>（需求二/三）。X 就是真正采用的横向位移（长截图恒 0），
    /// Y 是绝对 dy = H1 − 重叠，也就是 Rect2.Top − Rect1.Top。
    /// 与 <see cref="Offset"/> 同值，单独给这个名字是为了让"匹配结果自带 AlignmentOffset"
    /// 这句话在代码里真的能指到一个字段（判据 <c>match/匹配结果自带对齐位移与带的起止</c>）。
    /// </summary>
    public IntPoint AlignmentOffset => new(HorizontalShift, HeightBefore - OverlapLength);

    /// <summary>最佳相似度（需求三点名的名字，就是 <see cref="Confidence"/>）。</summary>
    public double BestSimilarity => Confidence;

    /// <summary>
    /// 重叠带的起始 Y（含），名字照需求三点的那张清单写。
    /// 公共坐标以 Rect1 左上角为原点，所以这一个数同时就是"带在前一段里的第几行"
    /// （<see cref="OverlapRect"/> 用的正是它）。
    /// </summary>
    public int OverlapStartY => Math.Max(0, HeightBefore - OverlapLength);

    /// <summary>重叠带的结束 Y（不含）——也就是前一段的可见底边。</summary>
    public int OverlapEndY => HeightBefore;

    /// <summary>
    /// <c>OverlapRect = Rect1 ∩ Rect2</c>（以 Rect1 左上角为原点的公共坐标）。
    /// 宽取两段里小的那一块——宽度不一致时多出来的那一条本来就没有对应像素可对齐。
    /// </summary>
    public IntRect OverlapRect => OverlapLength <= 0
        ? IntRect.Empty
        : new IntRect(0, OverlapStartY, Math.Min(WidthBefore, WidthAfter), OverlapLength);

    /// <summary>
    /// 建议蓝线（重叠带内行号，0 = 整带取图2）。优先在两张图的正文交集里取中点，
    /// 避免把后一张的固定顶部栏或前一张的固定底部栏带入成品。
    /// </summary>
    public int SeamCutPosition { get; init; }

    /// <summary>建议的后一段顶部裁切量（= 蓝线在带内的位置）。</summary>
    public int CropAfterTop => SeamCutPosition;

    /// <summary>建议的前一段底部裁切量（= 重叠 − 蓝线）。</summary>
    public int CropBeforeBottom => OverlapLength - SeamCutPosition;

    /// <summary>裁切建议自不自洽（恒为 true；留着给判据当钉子，改公式时它会先红）。</summary>
    public bool CropInvariantHolds => CropBeforeBottom + CropAfterTop == OverlapLength;

    /// <summary>这条缝需不需要人工过一眼（绿色"自动匹配"以外都要）。</summary>
    public bool NeedsReview => Status != SeamStatus.AutoPassed;

    public SeamStatus Status { get; init; } = SeamStatus.AutoPassed;

    public SeamCause Cause { get; init; } = SeamCause.None;

    /// <summary>给人看的结论说明。需求 §8 要求界面"说明原因"，这句话就是那句原因。</summary>
    public string CauseText { get; init; } = "";

    /// <summary>候选列表（按分数从好到差），界面右侧可逐条查看。</summary>
    public IReadOnlyList<MatchCandidate> Candidates { get; init; } = Array.Empty<MatchCandidate>();

    /// <summary>是否给出了可用的自动建议（false 时"恢复自动"按钮应禁用）。</summary>
    public bool HasSuggestion { get; init; } = true;

    /// <summary>本次分析实际排除的固定带（前图/后图），用于界面解释"为什么这几行没算"。</summary>
    public FixedBands BandsBefore { get; init; } = FixedBands.None;
    public FixedBands BandsAfter { get; init; } = FixedBands.None;
}

/// <summary>
/// 长截图重叠匹配器。
///
/// ═══ 算法（粗到细，全程整数）═══
/// 1. 检测两图的固定 UI 带（状态栏/输入栏），打分时整带排除；
/// 2. 每行压成行签名（亮度分块 + 水平梯度分块）；
/// 3. 对每个候选重叠行数 d，把"前图末尾 d 行"与"后图开头 d 行"按行配对，
///    等间隔抽样求平均归一化距离 → 得到 d 的相似度曲线；
/// 4. 取峰值附近若干候选，用全分辨率像素 SAD 在 ±dy、±dx 的小窗口里精修；
/// 5. 用"绝对相似度 + 与次佳峰的间隔 + 有效重叠长度"三者一起决定 ✓ 还是 !，
///    并给出可读原因。
///
/// 为什么不用特征点（ORB/SIFT 一类）：滚动截图是<b>纯平移</b>，没有旋转缩放，
/// 行配对已经把平移约束用满了；特征点在这种场景下又慢又容易在重复列表项上误配。
/// 纯色区、重复列表项这些确实会让相似度曲线出现多个峰——我们不假装能自动消歧，
/// 而是按需求 §5.1 明确降级成"! 需检查 + 存在多个候选"，把决定权交回用户。
/// </summary>
public static class OverlapMatcher
{
    /// <summary>
    /// 匹配一对相邻截图。
    /// </summary>
    /// <param name="before">前一段的实际像素（已按布局裁切好的有效区域）。</param>
    /// <param name="after">后一段的实际像素。</param>
    /// <param name="bandsBefore">前图的固定带；null 表示自动检测。</param>
    /// <param name="bandsAfter">后图的固定带；null 表示自动检测。</param>
    public static MatchResult Match(
        RgbaImage before,
        RgbaImage after,
        MatchOptions? options = null,
        FixedBands? bandsBefore = null,
        FixedBands? bandsAfter = null)
    {
        MatchOptions opt = options ?? MatchOptions.Default;
        int heightBefore = before.Height;
        int heightAfter = after.Height;

        // ── 前置：宽度不一致时不给自动结论 ─────────────────────────────
        if (before.Width != after.Width)
        {
            return new MatchResult
            {
                Offset = new IntPoint(0, heightBefore),
                Confidence = 0,
                OverlapLength = 0,
                Status = SeamStatus.NeedsReview,
                Cause = SeamCause.MixedResolution,
                CauseText = $"两张图宽度不同（{before.Width} 与 {after.Width}），自动对齐不可靠，请手动确定拼缝",
                HasSuggestion = false,
            };
        }

        FixedBands bandsB = bandsBefore ?? FixedRegionAnalyzer.Detect(before, after);
        FixedBands bandsA = bandsAfter ?? bandsB;   // 同一对图检测出的带，两侧共用一次检测结果

        IntRange rowsBefore = bandsB.ContentRows(heightBefore);
        IntRange rowsAfter = bandsA.ContentRows(heightAfter);
        if (rowsBefore.IsEmpty || rowsAfter.IsEmpty)
        {
            return new MatchResult
            {
                Offset = new IntPoint(0, heightBefore),
                Confidence = 0,
                OverlapLength = 0,
                Status = SeamStatus.NeedsReview,
                Cause = SeamCause.NoOverlap,
                CauseText = "整张图都被判定为固定界面，没有可用来对齐的正文行，请关闭该项清理后重试",
                HasSuggestion = false,
                BandsBefore = bandsB,
                BandsAfter = bandsA,
            };
        }

        IntRange columns = opt.ContentColumns ?? new IntRange(0, before.Width);

        // ── 粗搜：行签名相似度曲线 ─────────────────────────────────────
        var sigBefore = ImageSignature.Build(before, opt.Bins, columns);
        var sigAfter = ImageSignature.Build(after, opt.Bins, columns);
        double maxRowDistance = sigBefore.FeatureCount * 255.0;

        int dMin = Math.Max(opt.MinOverlap, 1);
        int dMax = Math.Min(heightBefore, heightAfter);
        if (dMax < dMin)
            return NoEnoughOverlap(heightBefore, bandsB, bandsA);

        // bestDistance[d] = 平均归一化距离（越小越好）；未算的保持 double.PositiveInfinity
        var distance = new double[dMax + 1];
        Array.Fill(distance, double.PositiveInfinity);

        for (int d = dMin; d <= dMax; d++)
        {
            int baseRow = heightBefore - d;      // 后图第 0 行对应前图第 baseRow 行
            int lo = Math.Max(baseRow, rowsBefore.Start);
            int hi = Math.Min(rowsBefore.End, heightBefore);
            if (hi - lo <= 0) continue;

            int step = Math.Max(1, (hi - lo) / opt.TargetSamples);
            long total = 0;
            int used = 0;
            for (int i = lo; i < hi; i += step)
            {
                int j = i - baseRow;
                if (!rowsAfter.Contains(j)) continue;
                total += sigBefore.DistanceTo(sigAfter, i, j);
                used++;
            }
            // 采样行太少就没有区分度，直接不给这个 d 打分（而不是给个 0 骗过比较）
            if (used < Math.Max(4, Math.Min(16, (hi - lo) / step))) continue;

            distance[d] = total / (double)used / maxRowDistance;
        }

        var peaks = FindPeaks(distance, dMin, dMax, opt.PeakSeparation, top: 5);
        if (peaks.Count == 0)
            return NoEnoughOverlap(heightBefore, bandsB, bandsA);

        // ── 精修：全分辨率像素 SAD，在小窗口里找最优 dy/dx ──────────────
        // 锁横向时采纳的是"dx=0 这一列里的最优"，同时把不限横向的最优 dx 留下来当证据。
        // Exact, informative rows retain details that bin averages can erase, especially
        // small text and pictures surrounded by flat background. Only use this evidence
        // for vertically locked screenshots; repeat rows are deliberately excluded.
        ExactRowMatcher.Evidence? exact = opt.LockHorizontal
            ? ExactRowMatcher.Find(before, after, rowsBefore, rowsAfter, columns, opt.MinOverlap)
            : null;
        int coarseOverlap = exact?.Overlap ?? peaks[0].Overlap;
        (int dy, double similarity, double rivalGap, int appliedDx, int detectedDx) best =
            Refine(before, after, opt, rowsBefore, rowsAfter, columns, coarseOverlap, opt.LockHorizontal);
        if (exact is not null)
        {
            double exactDistance = PixelDistance(before, after, coarseOverlap, 0,
                rowsBefore, rowsAfter, columns, opt.RefineRows, opt.RefineStride);
            best = (0, 1.0 - Math.Clamp(exactDistance, 0.0, 1.0), best.rivalGap, 0, best.detectedDx);
        }

        // 粗搜层面的"次佳峰差距"与精修层面的"最近对手差距"取小的那个：
        // 两个层面任何一个说不清位置唯一，就不该自动放行。
        double coarseGap = peaks.Count > 1
            ? Math.Max(0, peaks[1].Distance - peaks[0].Distance)
            : 1.0;
        double uniqueness = Math.Min(best.rivalGap, coarseGap);

        // ── 结论与原因 ─────────────────────────────────────────────────
        int overlap = coarseOverlap + best.dy;
        int overlapLength = Math.Max(0, overlap);
        var offset = new IntPoint(best.appliedDx, heightBefore - overlapLength);
        bool shiftLocked = opt.LockHorizontal && best.detectedDx != 0;
        double structuralMismatch = StructuralMismatchRatio(before, after, overlapLength, best.appliedDx,
            rowsBefore, rowsAfter, columns);

        var candidates = new List<MatchCandidate>(peaks.Count);
        for (int i = 0; i < peaks.Count; i++)
        {
            candidates.Add(new MatchCandidate(
                new IntPoint(0, heightBefore - peaks[i].Overlap),
                1.0 - peaks[i].Distance,
                peaks[i].Overlap)
            {
                IsChosen = exact is null && i == 0,
            });
        }
        if (exact is ExactRowMatcher.Evidence exactCandidate)
            candidates.Insert(0, new MatchCandidate(offset, best.similarity, overlapLength) { IsChosen = true });

        SeamStatus status;
        SeamCause cause;
        string text;

        if (shiftLocked)
        {
            // 排在最前：一旦量出"数据其实想横着挪"，它就是这条缝所有对不上的<b>解释</b>。
            // 这时候再报"对齐后仍有明显差异，建议逐条复核"等于把已知原因藏起来让用户重新猜。
            // （宽度不同、整张都是固定区这两种情况在上面已经提前返回，不会走到这里。）
            status = SeamStatus.NeedsReview;
            cause = SeamCause.HorizontalShiftLocked;
            text = "检测到横向偏移，但长截图模式已锁定左右方向" +
                   $"（最佳贴合需要 X={best.detectedDx:+#;-#;0}px，已按 X=0 采用，" +
                   $"正上方对齐的相似度 {best.similarity:P1}）：" +
                   "说明两张截图的宽度或左右边框不一致，请人工确认截图宽度或拼缝";
        }
        else if (exact is ExactRowMatcher.Evidence evidence && Math.Abs(evidence.Overlap - peaks[0].Overlap) > opt.RefineDy)
        {
            status = SeamStatus.NeedsReview;
            cause = SeamCause.MultipleCandidates;
            text = $"精确像素行支持重叠 {evidence.Overlap} 行（{evidence.Rows} 行一致，次选 {evidence.RunnerUpRows} 行），" +
                   $"与粗匹配 {peaks[0].Overlap} 行不同；已采用像素行建议，请检查拼缝";
        }
        else if (overlapLength < opt.MinOverlap)
        {
            status = SeamStatus.NeedsReview;
            cause = SeamCause.OverlapTooShort;
            text = $"只找到 {overlapLength} 行重叠，少于判定所需的 {opt.MinOverlap} 行：" +
                   "可能两张图之间漏了一段，或顺序不对";
        }
        else if (structuralMismatch > 0.10)
        {
            status = SeamStatus.NeedsReview;
            cause = SeamCause.LargeDifference;
            text = $"重叠区结构不一致（{structuralMismatch:P0} 行的文字/边缘无法对应）：两张图可能不是连续截图，已禁止自动拼合";
        }
        else if (best.similarity < opt.PassSimilarity)
        {
            status = SeamStatus.NeedsReview;
            cause = SeamCause.LargeDifference;
            text = $"对齐后仍有明显差异（相似度 {best.similarity:P1}，通过线 {opt.PassSimilarity:P1}）：" +
                   "接缝附近有动态内容，建议逐条复核或切掉该区域";
        }
        else if (uniqueness < opt.MinUniqueness)
        {
            status = SeamStatus.NeedsReview;
            cause = SeamCause.MultipleCandidates;
            text = $"附近还有同样贴得上的位置（最佳与最近对手的差距 {uniqueness:F5}，" +
                   $"低于判定所需的 {opt.MinUniqueness:F5}）：" +
                   "多为纯色区或重复列表项，位置说不准，请人工确认";
        }
        else
        {
            status = SeamStatus.AutoPassed;
            cause = SeamCause.None;
            text = $"重叠 {overlapLength} 行，相似度 {best.similarity:P1}" +
                (best.appliedDx == 0 ? "" : $"，横向错位 {best.appliedDx:+#;-#;0}px") +
                (bandsB.IsEmpty && bandsA.IsEmpty ? "" : $"，已排除固定区（前图 {bandsB}、后图 {bandsA}）");
        }

        // 重叠坐标中：[0, bandsA.Top) 是图2顶部固定栏；
        // [overlap - bandsB.Bottom, overlap) 是图1底部固定栏。
        // 两者之间有正文交集时在其中选中点，两张图各裁一部分。
        int safeStart = Math.Clamp(bandsA.Top, 0, overlapLength);
        int safeEnd = Math.Clamp(overlapLength - bandsB.Bottom, 0, overlapLength);
        bool hasSafeCut = safeStart <= safeEnd;
        int autoCut = hasSafeCut ? safeStart + (safeEnd - safeStart) / 2 : overlapLength / 2;
        int divider = FindDividerRow(before, overlapLength, best.appliedDx, columns);
        if (divider >= safeStart && divider < safeEnd)
            autoCut = Math.Clamp(divider + 2, safeStart, safeEnd);
        if (!hasSafeCut)
        {
            status = SeamStatus.NeedsReview;
            cause = SeamCause.LargeDifference;
            text = "重叠区不足以同时避开上下固定栏，请检查裁切线。";
        }

        return new MatchResult
        {
            Offset = offset,
            Confidence = best.similarity,
            Uniqueness = uniqueness,
            OverlapLength = overlapLength,
            HorizontalShift = best.appliedDx,
            DetectedHorizontalShift = best.detectedDx,
            HorizontalShiftLocked = shiftLocked,
            // 真正裁切由 CanvasLayout 完成，这里只建议位置。
            HeightBefore = heightBefore,
            WidthBefore = before.Width,
            WidthAfter = after.Width,
            SeamCutPosition = autoCut,
            Status = status,
            Cause = cause,
            CauseText = text,
            Candidates = candidates,
            HasSuggestion = structuralMismatch <= 0.10,
            BandsBefore = bandsB,
            BandsAfter = bandsA,
        };
    }

    private static MatchResult NoEnoughOverlap(int heightBefore, FixedBands bandsB, FixedBands bandsA) =>
        new MatchResult
        {
            Offset = new IntPoint(0, heightBefore),
            Confidence = 0,
            OverlapLength = 0,
            Status = SeamStatus.NeedsReview,
            Cause = SeamCause.NoOverlap,
            CauseText = "没有找到足够重叠：这两张图之间可能没有重叠内容，或滚动幅度超过了单屏",
            HasSuggestion = false,
            BandsBefore = bandsB,
            BandsAfter = bandsA,
        };

    private readonly record struct Peak(int Overlap, double Distance);

    /// <summary>
    /// 从距离曲线里取若干局部峰（距离的局部极小）。
    /// 要求峰之间至少隔 <paramref name="separation"/> 行，否则一个宽谷会被当成"多个候选"误报。
    /// </summary>
    private static List<Peak> FindPeaks(double[] distance, int dMin, int dMax, int separation, int top)
    {
        var local = new List<Peak>();
        for (int d = dMin; d <= dMax; d++)
        {
            if (double.IsPositiveInfinity(distance[d])) continue;
            double v = distance[d];
            bool isMin = true;
            int lo = Math.Max(dMin, d - separation), hi = Math.Min(dMax, d + separation);
            for (int k = lo; k <= hi; k++)
            {
                if (k == d || double.IsPositiveInfinity(distance[k])) continue;
                if (distance[k] < v || (distance[k] == v && k < d)) { isMin = false; break; }
            }
            if (isMin) local.Add(new Peak(d, v));
        }

        if (local.Count == 0)
        {
            // 没有严格局部极小（曲线单调）时取全局最小，避免"看起来没候选"但其实有唯一解
            double best = double.PositiveInfinity;
            int bestD = -1;
            for (int d = dMin; d <= dMax; d++)
                if (distance[d] < best) { best = distance[d]; bestD = d; }
            if (bestD > 0) local.Add(new Peak(bestD, best));
            return local;
        }

        // 按距离升序，并保证彼此至少相隔 separation 行
        var picked = new List<Peak>();
        foreach (Peak p in local.OrderBy(x => x.Distance))
        {
            if (picked.Any(x => Math.Abs(x.Overlap - p.Overlap) < separation)) continue;
            picked.Add(p);
            if (picked.Count >= top) break;
        }
        return picked;
    }

    /// <summary>
    /// 对最优点做全分辨率精修：在 ±dy、±dx 的小窗口里找像素级最优。
    /// 只搜这么小的窗口有两个原因：① 粗搜已经把位置定在几像素内；② 窗口一大，
    /// 重复纹理就能找到"另一个同样好的位置"，反而把唯一的正确解冲淡。
    ///
    /// <paramref name="lockHorizontal"/> 为真时，<b>重叠与位移取自 dx=0 这一列的最优</b>，
    /// 而不是全窗口的最优——因为"把整段挪到采纳的 X 上再看 dy"与"直接在正上方找 dy"
    /// 是两个不同的问题，采用哪一列就该在哪一列里选行号。
    /// 全窗口最优仍然算出来，只用于报告"数据想横着挪多少"。
    /// 同一次扫描里两列都取自同一批已算好的距离，没有额外像素开销。
    /// </summary>
    private static (int dy, double similarity, double rivalGap, int appliedDx, int detectedDx) Refine(
        RgbaImage before, RgbaImage after, MatchOptions opt,
        IntRange rowsBefore, IntRange rowsAfter, IntRange columns, int coarseD, bool lockHorizontal)
    {
        var scored = new List<(int dy, int dx, double dist)>();

        for (int dy = -opt.RefineDy; dy <= opt.RefineDy; dy++)
        {
            for (int dx = -opt.RefineDx; dx <= opt.RefineDx; dx++)
            {
                double dist = PixelDistance(before, after, coarseD + dy, dx,
                    rowsBefore, rowsAfter, columns, opt.RefineRows, opt.RefineStride);
                scored.Add((dy, dx, dist));
            }
        }

        var finite = scored.Where(s => !double.IsPositiveInfinity(s.dist)).ToList();
        // 窗口里没有一个可比位置：不给结论，交给上层按"重叠不足/差异过大"处理。
        if (finite.Count == 0) return (0, 0, 0, 0, 0);

        (int dy, int dx, double dist) freeBest = finite.OrderBy(s => s.dist).First();

        // 采纳的那一组：锁横向就只在 dx=0 这一列里选。
        // dx=0 一定在窗口里（上面的循环包含 0），而且它的可比性不依赖横向位移，
        // 所以只要 finite 非空，这一列必然也非空——真要取不到就退回全窗口最优，
        // 但绝不允许"因为锁横向就干脆不给出 dy"。
        var column = finite.Where(s => s.dx == 0).ToList();
        (int dy, int dx, double dist) applied = lockHorizontal && column.Count > 0
            ? column.OrderBy(s => s.dist).First()
            : freeBest;

        // 唯一性看的是"在采纳的那一档里有没有第二个同样好的位置"，不是"最佳点有多好"。
        // 真图上 3 例差 4 行的误判，全都因为绝对拟合度接近满分而没被拦住——
        // 只看拟合度的阈值等于没有阈值。
        // 锁横向时只在同一列内比对手：横向的那些位置本来就不可采纳，
        // 让它们参与"多解"判定会把结论说成"纵向位置说不准"，那是误导。
        double rivalGap = NearestRivalGap(scored, applied.dy, applied.dx, applied.dist,
            onlyZeroColumn: lockHorizontal);
        double similarity = 1.0 - Math.Clamp(applied.dist, 0.0, 1.0);
        // 采纳的 X 写死在返回值里：锁横向时**由构造保证**是 0，
        // 不依赖"列里一定取得到最优"这一步推理。
        return (applied.dy, similarity, rivalGap, lockHorizontal ? 0 : applied.dx, freeBest.dx);
    }

    /// <summary>
    /// 精修窗口内的"最近对手差距"：把所有 (dy,dx) 的归一化距离算出来后，
    /// 取与最佳点相距 ≥2 像素的候选里最好的那个，算它比最佳点差多少。
    /// 差距接近 0 = 窗口里到处都一样好 = 重复纹理，位置其实说不准。
    /// </summary>
    /// <param name="onlyZeroColumn">true（锁横向）时只在 dx=0 那一列里找对手。</param>
    private static double NearestRivalGap(List<(int dy, int dx, double dist)> scored,
        int bestDy, int bestDx, double bestDist, bool onlyZeroColumn)
    {
        double gap = 1.0;
        foreach ((int dy, int dx, double dist) in scored)
        {
            if (onlyZeroColumn && dx != 0) continue;
            if (Math.Abs(dy - bestDy) + Math.Abs(dx - bestDx) < 2) continue;   // 同一个峰不算对手
            if (double.IsPositiveInfinity(dist)) continue;
            gap = Math.Min(gap, dist - bestDist);
        }
        return gap <= 0 ? 0 : Math.Min(gap, 1.0);
    }
    /// <summary>
    /// 给定重叠行数与横向错位，算"前图末尾"与"后图开头"的平均归一化像素差。
    /// 返回 0 表示完全一致，1 表示每通道平均差到 255。
    /// </summary>
    private static double PixelDistance(
        RgbaImage before, RgbaImage after, int overlap, int dx,
        IntRange rowsBefore, IntRange rowsAfter, IntRange columns,
        int maxRows, int stride)
    {
        if (overlap <= 0) return double.PositiveInfinity;

        int baseRow = before.Height - overlap;
        int jLo = Math.Max(rowsAfter.Start, rowsBefore.Start - baseRow);
        int jHi = Math.Min(rowsAfter.End, rowsBefore.End - baseRow);
        jHi = Math.Min(jHi, jLo + maxRows);
        if (jHi - jLo <= 0) return double.PositiveInfinity;

        int strideSafe = Math.Max(1, stride);
        int colStart = columns.Start;
        int colEnd = columns.End;
        // 横向错位时两侧可比对的列会收窄，必须同步调整，否则比的是不同的列
        int aStart = Math.Max(colStart, colStart - dx);
        int aEnd = Math.Min(colEnd, after.Width - Math.Max(0, dx));
        if (aEnd - aStart <= 0) return double.PositiveInfinity;

        long sum = 0;
        long samples = 0;
        for (int j = jLo; j < jHi; j += strideSafe)
        {
            int i = baseRow + j;
            if (i < 0 || i >= before.Height) continue;
            ReadOnlySpan<byte> rowA = before.Row(i);
            ReadOnlySpan<byte> rowB = after.Row(j);
            for (int x = aStart; x < aEnd; x += strideSafe)
            {
                int ib = x * 4;
                int ia = (x + dx) * 4;
                if (ia < 0 || ia + 2 >= rowA.Length) continue;
                sum += Math.Abs(rowA[ia] - rowB[ib]);
                sum += Math.Abs(rowA[ia + 1] - rowB[ib + 1]);
                sum += Math.Abs(rowA[ia + 2] - rowB[ib + 2]);
                samples += 3;
            }
        }

        if (samples == 0) return double.PositiveInfinity;
        double raw = sum / (double)samples / 255.0;
        double edge = EdgeDistance(before, after, overlap, dx, rowsBefore, rowsAfter, columns, maxRows, stride);
        return raw * 0.65 + edge * 0.35;
    }

    private static double EdgeDistance(RgbaImage before, RgbaImage after, int overlap, int dx,
        IntRange rowsBefore, IntRange rowsAfter, IntRange columns, int maxRows, int stride)
    {
        int baseRow = before.Height - overlap;
        int jLo = Math.Max(rowsAfter.Start, rowsBefore.Start - baseRow);
        int jHi = Math.Min(rowsAfter.End, rowsBefore.End - baseRow);
        jHi = Math.Min(jHi, jLo + maxRows);
        int step = Math.Max(1, stride), start = Math.Max(1, columns.Start), end = Math.Min(columns.End, before.Width - 1);
        long sum = 0, count = 0;
        for (int j = jLo; j < jHi; j += step)
        {
            int i = baseRow + j;
            if (i <= 0 || i >= before.Height || j <= 0 || j >= after.Height) continue;
            var a = before.Row(i); var b = after.Row(j);
            for (int x = start; x < end; x += step)
            {
                int ai = (x + dx) * 4, ap = (x - 1 + dx) * 4, bi = x * 4, bp = (x - 1) * 4;
                if (ai < 0 || ai + 2 >= a.Length || ap < 0 || ap + 2 >= a.Length) continue;
                int ga = Math.Abs(a[ai] - a[ap]) + Math.Abs(a[ai + 1] - a[ap + 1]) + Math.Abs(a[ai + 2] - a[ap + 2]);
                int gb = Math.Abs(b[bi] - b[bp]) + Math.Abs(b[bi + 1] - b[bp + 1]) + Math.Abs(b[bi + 2] - b[bp + 2]);
                sum += Math.Abs(ga - gb); count += 765;
            }
        }
        return count == 0 ? 1 : sum / (double)count;
    }

    private static double StructuralMismatchRatio(RgbaImage before, RgbaImage after, int overlap, int dx,
        IntRange rowsBefore, IntRange rowsAfter, IntRange columns)
    {
        if (overlap <= 0) return 1;
        int baseRow = before.Height - overlap;
        int lo = Math.Max(1, Math.Max(rowsAfter.Start, rowsBefore.Start - baseRow));
        int hi = Math.Min(Math.Min(rowsAfter.End, rowsBefore.End - baseRow), after.Height - 1);
        int bad = 0, total = 0;
        for (int j = lo; j < hi; j++)
        {
            int i = baseRow + j;
            if (i <= 0 || i >= before.Height) continue;
            var a = before.Row(i); var b = after.Row(j); int different = 0, samples = 0;
            for (int x = Math.Max(1, columns.Start); x < Math.Min(columns.End, before.Width - 1); x += 4)
            {
                int ai=(x+dx)*4, ap=(x-1+dx)*4, bi=x*4, bp=(x-1)*4;
                if (ai<0 || ai+2>=a.Length || ap<0 || ap+2>=a.Length) continue;
                int ga=Math.Abs(a[ai]-a[ap])+Math.Abs(a[ai+1]-a[ap+1])+Math.Abs(a[ai+2]-a[ap+2]);
                int gb=Math.Abs(b[bi]-b[bp])+Math.Abs(b[bi+1]-b[bp+1])+Math.Abs(b[bi+2]-b[bp+2]);
                if (Math.Abs(ga-gb)>45) different++; samples++;
            }
            if (samples > 0) { total++; if (different > samples / 8) bad++; }
        }
        return total == 0 ? 1 : bad / (double)total;
    }

    private static int FindDividerRow(RgbaImage before, int overlap, int dx, IntRange columns)
    {
        if (overlap < 20) return -1;
        int baseRow = before.Height - overlap, best = -1, bestScore = 0;
        for (int j = 2; j < overlap - 2; j++)
        {
            int y = baseRow + j;
            if (y <= 1 || y >= before.Height - 1) continue;
            var row = before.Row(y); var prev = before.Row(y - 1); int score = 0;
            for (int x = Math.Max(1, columns.Start); x < Math.Min(columns.End, before.Width - 1); x += 3)
            {
                int p=(x+dx)*4, q=(x+dx)*4;
                if (p<0 || p+2>=row.Length || q<0 || q+2>=prev.Length) continue;
                int gradient=Math.Abs(row[p]-prev[q])+Math.Abs(row[p+1]-prev[q+1])+Math.Abs(row[p+2]-prev[q+2]);
                if (gradient > 90) score++;
            }
            // A real divider spans most of the content width. Random page
            // texture must not move the cut line, so require at least roughly
            // one third of the sampled columns to agree on the row boundary.
            if (score > bestScore && score > Math.Max(20, columns.Length / 3)) { bestScore = score; best = j; }
        }
        return best;
    }
}
