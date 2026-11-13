using LongStitch.Core.Geometry;

namespace LongStitch.Core.Model;

/// <summary>
/// 一条拼缝：相邻两个片段之间的<b>对齐关系</b>与它的可信度记录。
///
/// ═══ 第十二轮：核心数据换成"公共坐标里的两个量" ═══
/// 第九到十一轮把这条缝记成"前一段底部裁 b 行、后一段顶部裁 t 行，且 b + t = O"。
/// 那句话本身没说错，但它是<b>先凭空分配两个裁切数量、再拿它们去摆像素</b>——
/// 于是"裁切"成了一个需要先相信参数、再去核对落位的东西，而且界面上没有
/// "两张图在公共坐标里各自摆在哪"这个概念，用户拖红框时其实是在改切点而不是在改对齐。
///
/// 现在核心只有两个数，都是<b>公共画布坐标</b>里的量：
/// <code>
///   AlignmentOffset = (dx, dy)   图2 相对图1 的对齐位移（源图像素，整数）
///   CutY                         蓝色裁切线在重叠带里的位置（0 = 带首）
/// </code>
/// 由这两个数，布局在公共坐标里摆出两个<b>完整</b>矩形 Rect1、Rect2（它们这时是相交的），
/// 取交得到 OverlapRect，再用蓝线把带分成两半：
/// <code>
///   Rect1 = (0, 0, W1, H1)                       以图1 左上角为原点
///   Rect2 = (dx, H1 − O, W2, H2)                 图2 往上压 O 行
///   OverlapRect = Rect1 ∩ Rect2 = [H1 − O, H1)
///   蓝线 CutY ∈ [OverlapRect.Top, OverlapRect.Bottom]
///   图1 贡献 [Rect1.Top, CutY)      图2 贡献 [CutY, Rect2.Bottom)
/// </code>
/// 于是"两侧各裁多少行"不再是输入，而是<b>蓝线两侧的分界位置</b>：
/// <see cref="AfterCropTop"/> = CutY − OverlapRect.Top（带里由图1 提供的行数）、
/// <see cref="BeforeCropBottom"/> = OverlapRect.Bottom − CutY（带里由图2 提供的行数）。
/// 两者之和恒等于 <see cref="OverlapLength"/> 这件事从此<b>不需要有人去守住</b>——
/// 它是同一段区间的两种数法（判据 <c>align/两侧裁切量是蓝线两侧的分界位置，和恒等于重叠</c>）。
///
/// ═══ 为什么 dy 以"紧接位置"为零点 ═══
/// 需求里的 AlignmentOffset 是"图2 相对图1"的绝对位移 dy = H1 − O。
/// 但 Seam 不知道自己上面那张图多高：把绝对 dy 存进参数，就得把 H1 也存进来，
/// 于是"图被裁边/被清理改高"之后会出现两份真相。所以这里存的是
/// <c>dy − 紧接位移 = −O</c>：一个纯由这条缝自己决定的量，绝对 dy 由布局按 H1 现算
/// （读法见 <see cref="AbsoluteAlignment(int)"/>）。
///
/// ═══ 为什么还留着 OverlapLength / AfterCropTop / BeforeCropBottom 这三个可写口 ═══
/// 它们是<b>别名</b>，不是第二套状态：后备字段只有 <c>AlignmentOffset</c> 与 <c>CutY</c> 两个，
/// 三个别名各自按固定规则写进这两个字段，所以"多个字段互相覆盖"这个形状写不出来。
/// 留着它们有两个必要用途：① 旧项目文件（v1 到 v11）里存的就是这些名字，读档要落进新模型；
/// ② 界面与日志本来就说"重叠几行 / 带里前几行"，那是用户语言，不该为了换内部模型去改说法。
/// 派生读法一律 <b>先夹取再返回</b>，所以读档时 JSON 字段到达的先后顺序不影响结论
/// （<c>align/旧项目文件的裁切字段按任意顺序到达都归一到同一组核心值</c>）。
/// </summary>
public sealed class Seam
{
    public required string Id { get; init; }

    /// <summary>前一段 / 后一段的 <see cref="Segment.Id"/>。顺序变化后拼缝按这一对重新匹配。</summary>
    public required string BeforeId { get; init; }
    public required string AfterId { get; init; }

    /// <summary>
    /// 自动匹配给出的建议位移（绝对：后一段原点相对前一段原点）；null 表示没有可用建议
    /// （此时"恢复自动"不可用并说明原因）。
    /// 长截图模式下这里<b>可能</b>记着一个 X≠0 的建议——那是"数据确实想往左右挪"的证据，
    /// 必须留着才能在界面说清原因；布局与导出仍按 X=0 落位（见 <see cref="EffectiveOffset"/>）。
    /// </summary>
    public IntPoint? AutoOffset { get; set; }

    /// <summary>用户手动设定的位移；只有位移档（竖拼/横拼）拿它落位。</summary>
    public IntPoint? ManualOffset { get; set; }

    /// <summary>自动建议的置信度 0–1，界面展示"匹配强度"。</summary>
    public double Confidence { get; set; }

    // ═══════════════════════════════════════════════════════════════════
    // 核心：公共坐标里的对齐位移与蓝线（第十二轮）
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 图2 相对图1 的<b>对齐位移</b>。X 在长截图里恒为 0（<see cref="StitchAxes.Enforce(StitchMode, LongStitch.Core.Geometry.IntPoint)"/>）；
    /// Y 以"紧接排列"（两图首尾相接、零重叠）为零点：<b>Y = −重叠行数</b>。
    /// 也就是说 <c>Rect2.Top = Rect1.Bottom + gap + Y</c>。
    /// 默认 (0,0) = 不重叠、首尾相接，与"没分析过"的老含义一致。
    /// </summary>
    public IntPoint AlignmentOffset { get; set; }

    /// <summary>
    /// 蓝色裁切线：最终内容来源的分界线，单位是源图像素行。
    /// 原点取 <see cref="OverlapStartY"/>（带首）：0 = 整带用图2，<see cref="OverlapLength"/> = 整带用图1。
    /// 绝对公共坐标写法见 <see cref="CutYCanvas(int)"/>。
    /// 这个字段允许被写进带外（旧文件读档时 O 可能后到），<b>所有读法都会夹取</b>。
    /// </summary>
    public int CutY { get; set; }

    /// <summary>
    /// 重叠行数 O = 带里同一份内容重复了几行，也就是 <c>−AlignmentOffset.Y</c>。
    /// 这是"对齐"这一件事在用户语言里的名字，不是第二套状态。
    /// setter 是<b>兼容写入口</b>：它把行数换算回 <see cref="AlignmentOffset"/>。
    /// </summary>
    public int OverlapLength
    {
        get => Math.Max(0, -AlignmentOffset.Y);
        set => AlignmentOffset = new IntPoint(AlignmentOffset.X, -Math.Max(0, value));
    }

    /// <summary>带首在公共坐标里的位置（以 Rect1.Top 为原点）= H1 − O；布局用绝对坐标那一份。</summary>
    public int OverlapStartY(int baseHeightBefore) => Math.Max(0, baseHeightBefore - OverlapLength);

    /// <summary>带尾在公共坐标里的位置（以 Rect1.Top 为原点）= H1。</summary>
    public int OverlapEndY(int baseHeightBefore) => Math.Max(0, baseHeightBefore);

    /// <summary>把"以紧接位置为零点"的 Y 换算成需求里那个绝对 dy = H1 − O。</summary>
    public int AbsoluteAlignment(int baseHeightBefore) => baseHeightBefore + AlignmentOffset.Y;

    /// <summary>蓝线的绝对位置（以 Rect1.Top 为原点）= 带首 + 带内行号。</summary>
    public int CutYCanvas(int baseHeightBefore) => OverlapStartY(baseHeightBefore) + AfterCropTop;

    /// <summary>
    /// 带里由<b>图1</b>提供的那几行 = 蓝线在带内的位置（= <see cref="CutY"/>，夹进 [0, O]）。
    /// 老名字叫"后一段顶部裁掉的行数"，含义没变，只是现在是<b>读出来的</b>。
    /// </summary>
    public int AfterCropTop
    {
        get => Math.Clamp(CutY, 0, OverlapLength);
        // 兼容写入口（旧项目文件里存的就是这个名字）：<b>故意不夹取</b>——
        // 读档时 JSON 字段的到达顺序不保证，写的时候 O 可能还是 0，
        // 一夹就把用户的切点抹成带首。夹取统一放在读出口与 NormalizeAlignment 里。
        set => CutY = value;
    }

    /// <summary>
    /// 带里由<b>图2</b>提供的那几行 = 蓝线到带尾的距离（= O − <see cref="AfterCropTop"/>）。
    ///
    /// <b>这一侧没有写入口</b>（第十二轮）：旧项目文件里 b 与 t 是同时存的两个数，
    /// 而反序列化时 JSON 字段的到达顺序不保证——两个别名各写一次 <see cref="CutY"/>
    /// 就是需求七明令禁止的"多个字段互相覆盖"，坏文件里 b+t≠O 时先后顺序会决定结论。
    /// 所以只有 t（= 蓝线）留一个写入口，b 永远由 <c>O − t</c> 算出来。
    /// </summary>
    public int BeforeCropBottom => OverlapLength - AfterCropTop;

    /// <summary>两侧裁掉的总行数。恒等于 <see cref="OverlapLength"/>（同一段区间的两种数法）。</summary>
    public int CropTotal => AfterCropTop + BeforeCropBottom;

    /// <summary>不变式是否成立。写不出 false 是本轮的结构性目标；留着给判据当钉子。</summary>
    public bool CropInvariantHolds => CropTotal == OverlapLength && OverlapLength >= 0;

    /// <summary>切点是怎么来的。<see cref="SeamCropMode.Auto"/> 之外都不许被"重新分析"悄悄覆盖。</summary>
    public SeamCropMode CropMode { get; set; } = SeamCropMode.Auto;

    /// <summary>用户拖动过蓝色裁切线（或显式选过保留哪一份）。为 true 时重新分析只改重叠量、不改切点。</summary>
    public bool IsManualCut { get; set; }

    /// <summary>用户拖动过红箭头（改过对齐）。为 true 时重新分析要按 <see cref="AutoOverlapLength"/> 才能回滚。</summary>
    public bool IsManualAlignment { get; set; }

    /// <summary>
    /// 切点在重叠带内的位置（0=带首，整带用图2；O=带尾，整带用图1）。
    /// 它就是 <see cref="AfterCropTop"/>，也就是"带里前多少行来自上一张图"——
    /// 单独给个名字的用途是让界面与日志说"蓝线在带内第几行"，而不是让人自己从两个裁切量里推。
    /// </summary>
    public int CutPosition => AfterCropTop;

    public SeamStatus Status { get; set; } = SeamStatus.Pending;

    /// <summary>状态原因（结构化），界面据此出具体文案与处理入口。</summary>
    public SeamCause Cause { get; set; } = SeamCause.None;

    /// <summary>原因的可读补充，例如"存在 3 个候选，最佳与次佳仅差 0.8%"。</summary>
    public string? CauseText { get; set; }

    /// <summary>候选位置列表（含最佳），供右侧面板列出"候选位置与偏移"。</summary>
    public List<MatchCandidate> Candidates { get; set; } = new();

    /// <summary>固定哪一侧。默认固定前一段、移动后一段（需求 §5.2）。</summary>
    public FixedSide Fixed { get; set; } = FixedSide.Before;

    /// <summary>
    /// 拖动时的位移约束（用户的选择）。
    /// 长截图模式下它会被 <see cref="StitchAxes.Enforce(StitchMode, MoveConstraint)"/>
    /// 再收一道：横向本来就动不了。这里保留用户原样记的值，切回竖拼就恢复他选的档位。
    /// </summary>
    public MoveConstraint Constraint { get; set; } = MoveConstraint.Both;

    /// <summary>本条拼缝是否经过完整分析。</summary>
    public bool Analyzed { get; set; }

    /// <summary>是否被用户确认过（自动通过或手动确认）。</summary>
    public bool Confirmed { get; set; }

    /// <summary>分析结束时记下的"自动结论状态"。"恢复自动"要回到的是它，而不是凭当前值猜。</summary>
    public SeamStatus AutoStatus { get; set; } = SeamStatus.Pending;

    /// <summary>自动结论给的重叠行数（"恢复自动 / 取消本次调整"要回到这一格，不是回到 0）。</summary>
    public int AutoOverlapLength { get; set; }

    /// <summary>自动结论给的切点（"恢复自动 / 取消本次调整"要回到这一格，不是回到带首）。</summary>
    public int AutoCutY { get; set; }

    /// <summary>是否存在可用的自动建议；决定"恢复自动"按钮是否可用。</summary>
    public bool HasAutoSuggestion => AutoOffset.HasValue;

    /// <summary>当前是否处于手动状态（位移档）。</summary>
    public bool IsManual => ManualOffset.HasValue;

    /// <summary>
    /// 用户改过这条缝的任何一头（对齐、切点或位移档的位移）。
    /// 右侧面板的"待确认"与"✓ 失效"、以及"重新分析不许覆盖"都读这一条。
    /// </summary>
    public bool HasUserEdit => IsManual || IsManualCut || IsManualAlignment;

    /// <summary>
    /// 解析出位移档真正使用的位移。
    /// <paramref name="tightOffset"/> 由布局层按前一段的有效尺寸算出（紧接排列），
    /// 所以片段被裁边之后，未分析的拼缝会自动跟着保持"首尾相接"，不会留下空洞。
    ///
    /// 注意这一条<b>不看拼合模式</b>，返回的是参数里存的原值。
    /// 界面显示、日志、判据要的是"真正生效的位移"，请用
    /// <see cref="EffectiveOffset(IntPoint, StitchMode)"/>；布局自己也会再过一道
    /// <see cref="StitchAxes.Enforce(StitchMode, IntPoint)"/>，所以即使这里返回带 X 的原值，像素也不会错开。
    /// </summary>
    public IntPoint ResolveOffset(IntPoint tightOffset) =>
        ManualOffset ?? AutoOffset ?? tightOffset;

    /// <summary>
    /// 本条拼缝在给定模式下的<b>生效</b>位移：先按"手动 &gt; 自动 &gt; 紧接"解析，
    /// 再按模式的轴规则归零。屏上的 X、状态栏的数字、日志与画布落位都取这个值，
    /// 免得出现"数字显示 3、图却按 0 摆"。
    /// </summary>
    public IntPoint EffectiveOffset(IntPoint tightOffset, StitchMode mode) =>
        StitchAxes.Enforce(mode, ResolveOffset(tightOffset));

    /// <summary>
    /// 把当前生效位移固化为手动值（数值框、方向键、拖动结束都走这里）。
    ///
    /// 位移在写入前就按模式归零，所以<b>不存在"绕过去把 X 写进参数"的口子</b>：
    /// 长截图里传进来的 X 有多少都会被丢掉，丢掉的量通过返回值如实报出来，
    /// 由界面提示用户，而不是假装这次输入完全生效了。
    /// </summary>
    /// <param name="offset">用户想要的位移（源图像素，整数）。</param>
    /// <param name="mode">当前拼合模式——轴规则的唯一依据。</param>
    /// <returns>是否真的写入了新值，以及被锁掉的横向位移是多少。</returns>
    public ManualApply ApplyManual(IntPoint offset, StitchMode mode)
    {
        IntPoint applied = StitchAxes.Enforce(mode, offset);
        int dropped = offset.X - applied.X;    // 未锁横向时恒为 0

        // 与现有手动值完全一致：不写、不进历史、不刷新状态（否则一次空点击就多一条撤销）
        if (dropped == 0 && ManualOffset == applied)
            return new ManualApply(false, 0);

        ManualOffset = applied;
        Status = SeamStatus.ManualPending;
        Cause = SeamCause.ManualEdit;
        CauseText = dropped == 0
            ? null
            : $"输入横向位移 X={offset.X}，但长截图模式已锁定左右方向，按 X=0 采用（纵向 Y={applied.Y} 已生效）";
        // 需求 §5.2：每次有效移动使原有 ✓ 失效
        Confirmed = false;
        return new ManualApply(true, dropped);
    }

    /// <summary>恢复自动建议：清掉手动值，回到分析当时记下的自动结论状态。没有建议时返回 false。</summary>
    public bool ResetToAuto()
    {
        if (!HasAutoSuggestion) return false;
        ManualOffset = null;
        Status = AutoStatus;
        Cause = SeamCause.None;
        CauseText = null;
        Confirmed = false;
        // 第十二轮：对齐与切点都是"用户改过的东西"，"恢复自动"必须一起恢复，
        // 而且回到的是<b>自动结论那一格</b>，不是硬邦邦的带首/零重叠。
        IsManualAlignment = false;
        SetOverlap(AutoOverlapLength);
        SetAutoCut(AutoCutY);
        return true;
    }

    // ───────────────────────── 蓝线：内容来源的分界（第十二轮） ─────────────────────────

    /// <summary>
    /// 把蓝线设到重叠带内的第 <paramref name="cutPosition"/> 行：0=整带都用图2 那份，
    /// O=整带都用图1 那份。
    ///
    /// <b>对齐位移一字不动</b>——这是"只挪蓝线"这一档的定义：它回答的是
    /// "这 O 行重复内容里，蓝线落在哪一格、各留多少"，因此不可能让画布变高变矮。
    /// 注意它<b>不是</b>界面上那条手势：第十四轮的交互改成了「线不动、图动」，
    /// 用户拖动的是图片，走的是 <see cref="SlidePicture"/>（O 与蓝线同步改，画布行号才守恒）。
    /// 本方法留给"直接指定蓝线行号"的那些通路（面板读数、自动结论、迁移补齐）。
    /// 越界一律夹进 [0, O]：蓝线只能在带里，出带就意味着有一份内容被凭空删掉。
    /// </summary>
    /// <param name="cutPosition">想要的蓝线（带内行号，可越界，内部夹取）。</param>
    /// <returns>是否真的改了、夹好后的蓝线、被夹掉的行数（界面要据此回话）。</returns>
    public CutApply ApplyCut(int cutPosition)
    {
        int o = OverlapLength;
        int clamped = Math.Clamp(cutPosition, 0, o);
        int rejected = cutPosition - clamped;

        if (clamped == AfterCropTop && CropMode == SeamCropMode.Manual)
            return new CutApply(false, clamped, rejected);   // 一次空点击不该多一条撤销记录

        SetCut(clamped, SeamCropMode.Manual, manual: true);
        Status = SeamStatus.ManualPending;
        Cause = SeamCause.ManualEdit;
        // 顶到带边不是错误，但必须说清是哪一头被顶到了——否则用户只会觉得"线拖不动了"。
        string edge = cutPosition < 0 ? "首" : "尾";
        CauseText = rejected == 0
            ? null
            : rejected > 0
                ? $"蓝线已到重叠带尾边：想要 {cutPosition} 行，最多 {clamped} 行（整带用图1，被夹掉 {rejected} 行）"
                : $"蓝线已到重叠带首边：想要 {cutPosition} 行，最少 {clamped} 行（整带用图2，被夹掉 {-rejected} 行）";
        Confirmed = false;    // 需求 §5.2：每次有效调整使原有 ✓ 失效
        return new CutApply(true, clamped, rejected);
    }

    /// <summary>按增量移动蓝线（拖动与方向键走这一条：向下拖为正=图1 多留几行）。</summary>
    public CutApply ShiftCut(int deltaRows) => ApplyCut(AfterCropTop + deltaRows);

    /// <summary>
    /// 显式选一种切分策略（右侧面板的"保留上方 / 保留下方 / 对半切"）。
    /// 与 <see cref="ApplyCut"/> 一样算用户决定：<see cref="IsManualCut"/> 置真，重新分析不覆盖切点。
    /// </summary>
    public void ApplyCropStrategy(SeamCropMode strategy)
    {
        int o = OverlapLength;
        int cut = strategy switch
        {
            SeamCropMode.KeepBefore => o,
            SeamCropMode.SplitMiddle => o / 2,      // 整除偏向图1，规则固定可预期
            _ => 0,                                  // KeepAfter / Auto：整带用图2
        };
        SetCut(cut, strategy == SeamCropMode.Auto ? SeamCropMode.KeepAfter : strategy, manual: true);
        Status = SeamStatus.ManualPending;
        Cause = SeamCause.ManualEdit;
        Confirmed = false;
    }

    /// <summary>
    /// 匹配器写回用的蓝线：不标记成手动（它只是自动结论的一部分，"恢复自动"要能清掉它）。
    /// 同时把它记进 <see cref="AutoCutY"/>——那是"取消本次调整"要回到的地方。
    /// </summary>
    public void SetAutoCut(int cutPosition)
    {
        int clamped = Math.Clamp(cutPosition, 0, OverlapLength);
        AutoCutY = clamped;
        // 自动结论落地 = 用户那一次拖动作废（"恢复自动"走的就是这条路），
        // 所以这里必须把 IsManualCut 也清掉，否则蓝线永远回不到自动那一格。
        IsManualCut = false;
        SetCut(clamped, SeamCropMode.Auto, manual: false);
    }

    /// <summary>自动结论给的切点（"恢复自动 / 取消本次调整"要回到这一格，不是回到带首）。</summary>
    public int AutoAfterCropTop
    {
        get => AutoCutY;
        set => AutoCutY = value;
    }

    /// <summary>
    /// 撤掉用户切过的痕迹，回到自动结论那一格。没切过就什么都不做（不产生空改动）。
    /// </summary>
    /// <returns>是否真的改回来了。</returns>
    public bool CancelCutAdjustment()
    {
        if (!IsManualCut) return false;
        IsManualCut = false;
        SetCut(AutoCutY, SeamCropMode.Auto, manual: false);
        return true;
    }

    /// <summary>
    /// 撤掉用户对<b>对齐</b>的改动，回到自动结论那一格（重叠行数回到 <see cref="AutoOverlapLength"/>）。
    /// 没拖过就什么都不做（不产生空改动）。
    /// </summary>
    public bool CancelAlignmentAdjustment()
    {
        if (!IsManualAlignment) return false;
        IsManualAlignment = false;
        SetOverlap(AutoOverlapLength);
        return true;
    }

    /// <summary>
    /// 改总重叠 O（自动分析、面板的"对齐"框、以及红箭头拖动都走这里）。
    ///
    /// ═══ 蓝线怎么跟着带一起变：按比例换算（第十三轮需求四），不是原地不动 ═══
    /// 第十二轮在这里是"只夹不换"（带从 120 变到 140 时蓝线仍在带内第 40 行）。
    /// 需求三/四把这条否了：它要的是<b>蓝线在重叠带里的相对位置不变</b>——
    /// "普通情况下 CutY 的相对位置保持不变，只有在越界时才被夹紧"。
    /// 不换的后果是看得见的：带变高 20 行时，图1 的可见底边也跟着长 20 行、图2 那一侧一行没变，
    /// 用户会觉得"我调对齐，图1 自己多吃了一段"。按相对位置换算后两侧按同一比例分，
    /// 蓝线也不会因为带变化而突然跳到带首或带尾。
    /// 需求六那句"仍然必须保持在新的重叠区域内；如果超出就夹到边界"仍然成立——它由
    /// <see cref="SetOverlap"/> 末尾那一道夹取与带高为 0 的特例一起兑现。
    /// </summary>
    public void SetOverlap(int rows)
    {
        int o = Math.Max(0, rows);
        int oldOverlap = OverlapLength;
        int oldCut = CutY;                    // 裸值：允许是带外的那个数，换算后再夹
        AlignmentOffset = new IntPoint(AlignmentOffset.X, -o);
        CutY = Math.Clamp(ScaleCut(oldCut, oldOverlap, o), 0, o);
    }

    /// <summary>
    /// 蓝线在重叠带里的<b>相对位置</b>（0 = 带首、1 = 带尾），纯派生量：
    /// 状态里仍然只有 <see cref="AlignmentOffset"/> 与 <see cref="CutY"/> 两个数，
    /// 不再多存一份"比例"（多存一份就是第十三轮需求八禁止的"两个字段互相覆盖"）。
    /// </summary>
    public double CutRatio => OverlapLength <= 0 ? 0 : (double)AfterCropTop / OverlapLength;

    /// <summary>
    /// 把带内行号按"相对位置不变"从旧带高换算到新带高：<c>round(旧行号 / 旧带高 × 新带高)</c>。
    ///
    /// 全程整数（<c>分子 × 新带高 + 旧带高/2</c> 再整除 = 四舍五入），因为这条算式的结果
    /// 直接决定"哪一行像素归哪张图"——精度红线一不许它经过浮点。
    /// <b>这里返回的是没夹过的那一个数</b>：调用方要用它分清两件不同的事——
    /// "按相对位置换算"（第十三轮需求四，正常）与"带短到装不下、被挤到带边"（需求六.5，要出声）。
    /// 夹取本身留在 <see cref="SetOverlap"/> 与 <see cref="AfterCropTop"/> 那一侧。
    /// </summary>
    private static int ScaleCut(int oldCutRows, int oldOverlap, int newOverlap)
    {
        if (newOverlap <= 0 || oldOverlap <= 0 || oldCutRows <= 0)
            return 0;                                     // 带没了 / 本来就在带首
        return (int)(((long)oldCutRows * newOverlap + oldOverlap / 2) / oldOverlap);
    }

    /// <summary>
    /// 唯一的蓝线写入出口：写的是核心字段 <see cref="CutY"/>，越界先夹。
    /// 所有能改蓝线的地方都必须走它——留任何一处直接赋值，就会出现
    /// "读出来与参数里写的不一样"这种界面与像素对不上的形状。
    /// </summary>
    private void SetCut(int cutY, SeamCropMode mode, bool manual)
    {
        CutY = Math.Clamp(cutY, 0, OverlapLength);
        CropMode = mode;
        if (manual) IsManualCut = true;
    }

    // ───────────────────────── 红箭头：对齐调整（第十二轮，需求六/七） ─────────────────────────

    /// <summary>
    /// 手动调整<b>对齐</b>：把重叠行数改 <paramref name="overlapDeltaRows"/> 行。
    ///
    /// 这一条只碰 <see cref="AlignmentOffset"/>，<b>绝不碰蓝线的带内行号以外的东西</b>：
    /// 对齐一改，带就变长变短，蓝线随之重新夹取（需求六"如果蓝线超出重叠区域，
    /// 要自动夹到重叠区域边界"）。视口、片段顺序、其他拼缝都不在这条通路上（需求二）。
    ///
    /// 手势方向由调用方折算：<c>固定前一段 ⇒ 图2 往下拖 = 重叠变少 ⇒ 传负数</c>；
    /// <c>固定后一段 ⇒ 图1 往下拖 = 重叠变多 ⇒ 传正数</c>。界面上那根红箭头负责说清是谁在动。
    /// </summary>
    /// <param name="overlapDeltaRows">重叠行数的变化量（正=多压上去几行）。</param>
    /// <returns>是否真的改了、新的重叠与蓝线、被夹掉的行数、蓝线有没有因带变短而被夹。</returns>
    public AlignmentApply ApplyAlignment(int overlapDeltaRows)
    {
        int old = OverlapLength;
        int wanted = old + overlapDeltaRows;
        int clamped = Math.Max(0, wanted);
        int rejected = wanted - clamped;   // 正数=用户想把重叠压得比整张图还多

        if (clamped == old)
            return new AlignmentApply(false, old, AfterCropTop, rejected, false, 0);

        int cutBefore = CutY;
        // 先算"按相对位置该落到哪一行"（还没夹），再让 SetOverlap 把夹过的落进字段。
        // 两者之差才是需求六.5 那句"被新带挤短"；换算本身不算被挤——
        // 否则用户每调一次对齐都会看见一句"蓝线被夹掉 N 行"，而那其实只是他选的档位跟着带一起变长。
        int scaledCut = ScaleCut(cutBefore, old, clamped);
        SetOverlap(clamped);               // 带变短时在这里把蓝线夹回带边
        Status = SeamStatus.ManualPending;
        Cause = SeamCause.ManualEdit;
        Confirmed = false;                 // 需求 §5.2：每次有效调整使原有 ✓ 失效
        IsManualAlignment = true;

        // "被夹到带边"要单独说：带变短把蓝线顶到带尾，与"重叠本身压不动了"是两件事。
        // 报的是<b>行数</b>而不是布尔：界面那句提示要说"被夹掉几行"，与蓝线手势同一个口径。
        bool cutClamped = scaledCut != CutY;
        int cutRejected = scaledCut - CutY;
        CauseText = rejected == 0
            ? null
            : $"重叠已到 0 行：想再压 {-rejected} 行会让两张图之间出现空洞，长截图按首尾相接采用";
        return new AlignmentApply(true, OverlapLength, AfterCropTop, rejected, cutClamped, cutRejected);
    }

    // ───────────────────────── 「线不动、图动」：滑动手势（第十四轮末次交互重设计） ─────────────────────────

    /// <summary>
    /// 用户抓住蓝线<b>上方</b>或<b>下方</b>那一张图上下滑动时，唯一允许写参数的出口。
    ///
    /// ═══ 为什么要新开一个出口，而不是复用 <see cref="ApplyAlignment"/> + <see cref="ApplyCut"/> ═══
    /// 界面上那条蓝色裁切线是<b>参照物</b>：用户看到的是"线钉在屏幕那一行不动，我手里这张图在线下面滑"。
    /// 蓝线在画布里的绝对行号是 <c>图1 底边 H1 − 重叠 O + 带内行号 Cut</c>，于是"线不动"翻译成参数就是：
    /// <list type="bullet">
    ///   <item><b>抓下方那张（<see cref="FixedSide.After"/>）往下拖 d 行</b>：这张图在画布里往下走 d 行
    ///         ⇒ 重叠少 d 行；同时带内行号也必须少 d 行，两者的 −d 才在 <c>H1 − O + Cut</c> 里对消，
    ///         蓝线的画布行号一字不动（视口也不用补偿）。</item>
    ///   <item><b>抓上方那张（<see cref="FixedSide.Before"/>）往下拖 d 行</b>：布局永远把最左上角归到 (0,0)，
    ///         画布坐标里能动的只有图2 ⇒ 这一档等价于"重叠多 d 行、带内行号不动"，
    ///         蓝线在画布里往上走 d 行，由界面用<b>纯显示</b>的视口补偿把它钉回屏幕原地
    ///         （见 <c>StitchCanvasControl.ApplyFixedSideAnchor</c>，它锚的就是图2 顶边）。</item>
    /// </list>
    /// 复用旧出口做不到这件事：<see cref="SetOverlap"/> 会按<b>相对位置</b>换算蓝线
    /// （第十三轮需求四那一条），换算完的 Cut 与本手势要的"Cut 同增同减 / Cut 不动"都不是一回事，
    /// 蓝线就会在用户手里自己漂走——那正是这次重设计要消掉的观感。
    ///
    /// <b>两个自由度一个都没丢</b>：抓下方那一张改的是 (−1,−1) 方向、抓上方那一张改的是 (+1,0) 方向，
    /// 这两个向量张成 (O, Cut) 整个平面，所以"重叠不变、只挪蓝线"这种老需求仍然可达
    /// （先抓上方拖 d、再抓下方拖 d），只是不再要求用户去理解"固定侧"这个概念。
    ///
    /// <b>顺带把"固定侧"写成手势的结论而不是前提</b>：没被抓的那一张就是屏幕上钉住的那一张，
    /// 所以这里直接把 <see cref="Fixed"/> 落成它的反面。界面那个下拉框因此可以撤掉，
    /// 而日志与 F8 读数里的 <c>fixedSide=</c> 仍然说的是真话。
    /// </summary>
    /// <param name="grabbedSide">用户抓住的是蓝线上方那张（Before）还是下方那张（After）。</param>
    /// <param name="rowsDown">
    /// 这次滑动折算成的<b>源图像素</b>行数，正数=往下。调用方必须已经过轴向约束
    /// （长截图锁横向，所以只有 Y 参与）。
    /// </param>
    /// <returns>是否真的改了、新的重叠与蓝线、被夹掉的行数、以及这次手势认下的固定侧。</returns>
    public SlideApply SlidePicture(FixedSide grabbedSide, int rowsDown)
    {
        int oldOverlap = OverlapLength;
        int oldCut = CutY;
        (int newOverlap, int newCut, int rejected) = SlideTarget(grabbedSide, rowsDown);

        // 没改动就不留痕迹：一次空点击不该往撤销栈里多压一条（与 ApplyCut/ApplyAlignment 同一个纪律）。
        if (newOverlap == oldOverlap && newCut == oldCut && Fixed == OppositeOf(grabbedSide))
            return new SlideApply(false, oldOverlap, oldCut, rejected, grabbedSide, Fixed);

        // 顺序有意义：SetCut 会把蓝线夹进 [0, OverlapLength]，而 OverlapLength 是从 AlignmentOffset 派生的，
        // 所以必须先把新的重叠落进 AlignmentOffset，再写蓝线。
        // 这里<b>绕开 SetOverlap</b>：它那条"按相对位置换算蓝线"正是本手势不要的行为。
        AlignmentOffset = new IntPoint(AlignmentOffset.X, -newOverlap);
        SetCut(newCut, SeamCropMode.Manual, manual: true);
        Fixed = OppositeOf(grabbedSide);
        Status = SeamStatus.ManualPending;
        Cause = SeamCause.ManualEdit;
        Confirmed = false;                 // 需求 §5.2：每次有效调整使原有 ✓ 失效
        IsManualAlignment = true;
        CauseText = rejected == 0
            ? null
            : grabbedSide == FixedSide.After
                ? $"已经滑到头：想再往下 {rejected} 行会让重叠或蓝线变成负数（整带都用图2 了）"
                : $"重叠已到 0 行：想再往上 {-rejected} 行会让两张图之间出现空洞，长截图按首尾相接采用";
        return new SlideApply(true, OverlapLength, AfterCropTop, rejected, grabbedSide, Fixed);
    }

    /// <summary>
    /// 滑动手势的<b>纯计算</b>那一半：给定抓住哪一侧、想滑几行，算出新的 (重叠, 蓝线, 被夹行数)，
    /// 一个字段都不写。
    ///
    /// 为什么要单独拆出来：拖动途中的预览帧必须"只建临时布局、参数一个字都不写"（需求 §5.2），
    /// 而松手提交又必须落到<b>同一条算式</b>上——否则"预览看见的那一帧"与"提交落下的那一格"
    /// 会各说一套，用户松手那一刻图会跳一下。预览与提交共用这一个函数，那种跳就无从产生。
    ///
    /// 夹取口径：抓下方那张往下滑时，重叠与带内行号一起减，先顶住的是 <c>min(O, Cut)</c>
    /// （Cut 先顶到 0 就是"整带都用图2"，再拖蓝线就得自己跑，已经越出手势的定义）；
    /// 往上滑与 <see cref="ApplyAlignment"/> 同口径，只夹下界不设上界。
    /// </summary>
    public (int Overlap, int Cut, int RejectedRows) SlideTarget(FixedSide grabbedSide, int rowsDown)
    {
        int oldOverlap = OverlapLength;
        int oldCut = CutY;
        int applied = grabbedSide == FixedSide.After
            ? Math.Min(rowsDown, Math.Min(oldOverlap, oldCut))
            : Math.Max(rowsDown, -oldOverlap);
        int newOverlap = grabbedSide == FixedSide.After ? oldOverlap - applied : oldOverlap + applied;
        int newCut = Math.Clamp(grabbedSide == FixedSide.After ? oldCut - applied : oldCut, 0, newOverlap);
        return (newOverlap, newCut, rowsDown - applied);
    }

    /// <summary>"没被抓的那一张"＝屏幕上钉住的那一张（滑动结束时落进 <see cref="Fixed"/>）。</summary>
    private static FixedSide OppositeOf(FixedSide grabbed) =>
        grabbed == FixedSide.After ? FixedSide.Before : FixedSide.After;

    /// <summary>
    /// 读档/撤销后把对齐与蓝线修补回规范形（<b>旧项目文件的迁移点</b>）。
    ///
    /// v1 到 v11 的项目文件里存的名字是 overlapLength / beforeCropBottom / afterCropTop，
    /// 反序列化时这些别名各自写进 <see cref="AlignmentOffset"/> 与 <see cref="CutY"/>。
    /// 因为 JSON 字段到达顺序不保证，别名写入口一律<b>不做夹取</b>、读出口一律夹取，
    /// 所以最后由这里把 <see cref="CutY"/> 归一到 [0, O]：
    /// 无论 60 与 20 谁先到，结论都是"O=60、蓝线在带内第 20 行"
    /// （判据 <c>align/旧项目文件的裁切字段按任意顺序到达都归一到同一组核心值</c>）。
    /// 老文件里根本没有重叠（只有位移）的情况由布局层按主轴位移反推，见
    /// <see cref="LongStitch.Core.Layout.CanvasLayout.Build"/>。
    /// </summary>
    public void NormalizeAlignment()
    {
        int o = OverlapLength;
        AlignmentOffset = new IntPoint(AlignmentOffset.X, -o);
        CutY = Math.Clamp(CutY, 0, o);
        AutoOverlapLength = Math.Max(0, AutoOverlapLength);
        AutoCutY = Math.Clamp(AutoCutY, 0, o);
        // 自动来源的缝：<b>只信 AutoCutY 那一个出处</b>。
        // 旧文件里 b/t 两个数都存着（甚至互不相等），而它们都只是"蓝线在哪"的另一种说法——
        // 既然这一格不是用户定的，就按自动结论重摆，而不是留下"最后一个到达的别名赢了"那种偶然。
        if (!IsManualCut) CutY = AutoCutY;
    }

    /// <summary>
    /// 长截图里"面板输入一个 Y 位移"该怎么落：位移本身不再是落位参数，
    /// 它只用来表达"两段重叠多少行"——O = 紧接位移 − Y，再交给 <see cref="SetOverlap"/>。
    /// 竖拼/横拼不走这条（那里位移就是落位，见 <see cref="ApplyManual"/>）。
    /// </summary>
    /// <param name="offsetY">用户输入的纵向位移（源图像素）。</param>
    /// <param name="tightOffsetY">紧接排列（完全不重叠）时的位移 = 前一段长 + 间隙。</param>
    /// <returns>被夹掉的行数（负数=用户想留空洞，长截图的裁切模型表达不了）。</returns>
    public int ApplyOverlapFromOffset(int offsetY, int tightOffsetY)
    {
        int wanted = tightOffsetY - offsetY;
        int clamped = Math.Max(0, wanted);
        SetOverlap(clamped);
        Status = SeamStatus.ManualPending;
        Cause = SeamCause.ManualEdit;
        Confirmed = false;
        CauseText = clamped == wanted
            ? null
            : $"输入位移 Y={offsetY} 会比「紧接排列」还空 {wanted} 行，长截图按裁切去重、不允许留空洞，已按重叠 0 行采用";
        return wanted - clamped;
    }

    /// <summary>
    /// 自动分析结果写回（审查第四轮 P1）。
    /// 自动侧信息（AutoOffset/AutoStatus/强度/重叠/候选）总是更新——它们是"如果不要手动，
    /// 结果会是什么"的记录；但<b>显示状态只在用户没改过时才跟着自动结论走</b>。
    /// 否则会出现"布局按用户的对齐与蓝线摆放、界面却显示 ✓ 自动通过"的自相矛盾状态。
    /// </summary>
    /// <param name="candidates">候选列表。</param>
    /// <param name="autoCutPosition">
    /// 匹配器给的蓝线（重叠带内第几行）。null 表示不指定，按默认档（整带取图2）。
    /// <b>用户切过的缝（<see cref="IsManualCut"/>）一律忽略这一项</b>——重新分析可以改重叠量，
    /// 不能把他选的"这行内容要图1 那份"抹掉。
    /// </param>
    public void ApplyAutoResult(IntPoint? offset, SeamStatus status, SeamCause cause, string? text,
        double confidence, int overlap, List<MatchCandidate> candidates, int? autoCutPosition = null)
    {
        AutoOffset = offset;
        AutoStatus = status;
        Confidence = confidence;
        // 用户拖过红箭头 ⇒ 自动结论只<b>留档</b>，不改他摆好的那一格对齐。
        // 原来这里无条件写重叠，等于"重新分析"一键抹掉用户手工对齐的结果。
        int keep = IsManualAlignment ? OverlapLength : Math.Max(0, overlap);
        SetOverlap(keep);
        AutoOverlapLength = Math.Max(0, overlap);
        Candidates = candidates;
        Analyzed = true;
        if (autoCutPosition is int cut && !IsManualCut) SetAutoCut(cut);
        else AutoCutY = Math.Clamp(AutoCutY, 0, OverlapLength);

        // 用户改过任何一头都不许让显示状态跟着自动结论走
        if (HasUserEdit) return;
        Status = status;
        Cause = cause;
        CauseText = text;
        Confirmed = false;
    }

    /// <summary>用户点击"确认拼缝"。只表示接受当前结果，不改动任何像素。</summary>
    public void Confirm()
    {
        Confirmed = true;
        Status = IsManual ? SeamStatus.ManualConfirmed : SeamStatus.AutoPassed;
    }

    /// <summary>相邻标识，用于顺序变化后复用同一条拼缝的参数。</summary>
    public string PairKey => $"{BeforeId}→{AfterId}";

    public override string ToString() => $"{BeforeId}→{AfterId} {Status}";
}

/// <summary>
/// 一次"手动写入位移"的结果。
///
/// 之所以要有返回值而不是让 <see cref="Seam.ApplyManual"/> 默默改完：
/// 长截图锁横向之后，用户输入的 X 会被丢掉。界面必须能区分
/// "什么都没改（不该进撤销栈）"、"改了 Y"、"Y 改了但 X 被锁掉（要出声说明）"，
/// 否则用户会以为自己输入的 6 已经生效了。
/// </summary>
/// <param name="Applied">是否真的写入了新值（false 表示与现有手动值完全一致）。</param>
/// <param name="RejectedHorizontalShift">被模式锁掉的横向位移（源图像素，未锁时为 0）。</param>
public readonly record struct ManualApply(bool Applied, int RejectedHorizontalShift)
{
    /// <summary>这次输入有没有被模式的轴规则吃掉一部分。</summary>
    public bool HorizontalRejected => RejectedHorizontalShift != 0;
}

/// <summary>
/// 一次"移动蓝线"的结果（<see cref="Seam.ApplyCut"/> / <see cref="Seam.ShiftCut"/>）。
///
/// 与 <see cref="ManualApply"/> 同样的道理：<b>夹取掉的量必须回话</b>。
/// 用户在重叠带里往上下猛拖，蓝线会顶到带首或带尾——如果悄悄夹住，
/// 他会以为"还能继续拖"，其实画布早已不动了，只会觉得"拖不动了，是不是软件坏了"。
/// </summary>
/// <param name="Applied">蓝线是否真的变了（没变就不该进撤销栈）。</param>
/// <param name="CutPosition">夹好后的蓝线（带内行号）。</param>
/// <param name="RejectedRows">被夹掉的行数（正数=想切到带尾之外，负数=想切到带首之前）。</param>
public readonly record struct CutApply(bool Applied, int CutPosition, int RejectedRows)
{
    /// <summary>这次是不是顶到了带边（界面据此提示"已经整带用上面/下面那张"）。</summary>
    public bool Clamped => RejectedRows != 0;
}

/// <summary>
/// 一次"调整对齐"的结果（<see cref="Seam.ApplyAlignment"/>，第十二轮）。
///
/// 与蓝线那一组分开，是因为这两件事对用户的意义完全不同：
/// 拖蓝线回答"这几行重复内容要哪一份"，拖红箭头回答"两张图到底对齐在哪"。
/// 后者会真的让画布变高变矮，还可能把蓝线挤出带外——那一步夹取必须说出来，
/// 否则用户看到的是"我调了对齐，蓝线自己跳了"。
/// </summary>
/// <param name="Applied">重叠行数是否真的变了。</param>
/// <param name="Overlap">调整后的重叠行数。</param>
/// <param name="CutPosition">重新夹取后的蓝线（带内行号）。</param>
/// <param name="RejectedRows">想压却压不掉的行数（正数=已经压到 0 重叠还想再压）。</param>
/// <param name="CutWasClamped">蓝线有没有<b>因为带装不下</b>而被挤到带边。
/// 第十三轮起这一条<b>不含"按相对位置换算"</b>：带从 120 变 140、蓝线从 40 走到 47 是换算，
/// 不该报成"被夹"（否则这句提示每拖一次都响，反而没人信它）。</param>
/// <param name="CutRejectedRows">蓝线被夹掉的那几行（正数=想切到带尾之外）。界面那句提示读它。</param>
public readonly record struct AlignmentApply(
    bool Applied, int Overlap, int CutPosition, int RejectedRows, bool CutWasClamped, int CutRejectedRows);

/// <summary>
/// 一次「线不动、图动」滑动的结果（<see cref="Seam.SlidePicture"/> 的返回值）。
/// 与 <see cref="AlignmentApply"/> 分开摆：那一个是"只改重叠、蓝线按比例换算"的旧口径，
/// 这一个是"重叠与蓝线一起改、蓝线在屏幕上钉住"的新口径，混用一个记录就分不清谁在动。
/// </summary>
/// <param name="Applied">参数有没有真的变（false 时界面不该往撤销栈里压东西）。</param>
/// <param name="Overlap">滑动之后的重叠行数 O。</param>
/// <param name="CutPosition">滑动之后的蓝线（带内行号）。</param>
/// <param name="RejectedRows">被夹掉的行数：正数=想再往下滑但重叠或蓝线会变负，负数=想把重叠压到 0 以下。</param>
/// <param name="Grabbed">用户抓住的是上方那张还是下方那张。</param>
/// <param name="Fixed">这次手势认下的固定侧＝没被抓的那一张（屏幕上钉住的那一张）。</param>
public readonly record struct SlideApply(
    bool Applied, int Overlap, int CutPosition, int RejectedRows, FixedSide Grabbed, FixedSide Fixed);

/// <summary>一个匹配候选。需求 §5.1 要求"产出候选位置、置信度、有效重叠长度"，界面可逐条复核。</summary>
/// <param name="Offset">该候选对应的位移（源图像素，整数）。</param>
/// <param name="Score">归一化后的匹配分数，越大越像。</param>
/// <param name="OverlapLength">该候选下判定的重叠行数。</param>
public sealed record MatchCandidate(IntPoint Offset, double Score, int OverlapLength)
{
    /// <summary>是否为最终采纳的候选。</summary>
    public bool IsChosen { get; init; }
}
