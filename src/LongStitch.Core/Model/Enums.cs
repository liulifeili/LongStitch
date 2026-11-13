using LongStitch.Core.Geometry;

namespace LongStitch.Core.Model;

/// <summary>三种拼合模式（产品需求 §1）。</summary>
public enum StitchMode
{
    /// <summary>长截图：同一页面连续滚动所得，自动找重叠、对齐、去重。</summary>
    LongScroll,

    /// <summary>竖拼：独立图片按从上到下首尾排列，不自动去重。</summary>
    Vertical,

    /// <summary>横拼：独立图片按从左到右首尾排列，不自动去重。</summary>
    Horizontal,
}

/// <summary>拼缝状态。界面必须同时用图标与文字表达，不许只靠颜色（需求 §6 可用性）。</summary>
public enum SeamStatus
{
    /// <summary>尚未分析。</summary>
    Pending,

    /// <summary>正在分析。</summary>
    Analyzing,

    /// <summary>达到阈值且无明显冲突：✓ 自动匹配。</summary>
    AutoPassed,

    /// <summary>低置信度/多解/重叠不足/明显断层：! 需检查。</summary>
    NeedsReview,

    /// <summary>用户手动调整过、尚未确认。</summary>
    ManualPending,

    /// <summary>用户已确认：✓ 手动确认（与自动通过在视觉上可区分）。</summary>
    ManualConfirmed,
}

/// <summary>拼缝状态的可读原因（需求 §8 要求界面说明"为什么需要检查"）。</summary>
public enum SeamCause
{
    None = 0,

    /// <summary>重复内容有多个候选位置。</summary>
    MultipleCandidates,

    /// <summary>可用重叠太短，不足以可靠判定。</summary>
    OverlapTooShort,

    /// <summary>找不到足够重叠，可能顺序有误或中间跳过了一段。</summary>
    NoOverlap,

    /// <summary>对齐后两侧内容差异过大（动态内容、动画、时间变化）。</summary>
    LargeDifference,

    /// <summary>相邻两图宽度/来源设备不同，自动匹配不可靠。</summary>
    MixedResolution,

    /// <summary>清理设置与匹配结果冲突（例如重叠区正好被裁掉了）。</summary>
    CleanupConflict,

    /// <summary>用户手动移动过，原自动结论已失效。</summary>
    ManualEdit,

    /// <summary>
    /// 长截图模式匹配到了横向位移，但该模式锁定左右方向，位移已按 X=0 采用。
    ///
    /// 为什么要专门立这一项而不是悄悄把 X 抹平：横向对得上说明两张截图的宽度或
    /// 边框不一致（多截了一条侧栏、分辨率不同、截图被裁过）。这时"看起来贴上了"
    /// 恰恰是最该让人看一眼的情况——悄悄归零会留下一个左右错开半列的成品，
    /// 而用户只会觉得"哪里怪怪的"却查不出原因。
    /// </summary>
    HorizontalShiftLocked,
}

/// <summary>拼缝编辑时固定哪一侧（需求 §5.2：默认固定拼缝前的内容，移动后一张）。</summary>
public enum FixedSide
{
    /// <summary>固定前一段，拖动后一段。</summary>
    Before,

    /// <summary>固定后一段，拖动前一段。</summary>
    After,
}

/// <summary>
/// 一条拼缝的<b>裁切边界</b>是怎么来的（第九轮）。
///
/// 为什么要单独记而不是只存一个 b：
/// "重叠 O 行里切在哪一格"这件事有四种合法来源——匹配器给的默认、用户拖蓝线、
/// 用户点"保留上方/保留下方"、用户点"对中切一半"。来源不同，重新分析时能不能覆盖它也不同：
/// 只有 <see cref="Auto"/> 会被下一次自动结果重写，<see cref="Manual"/> 与三种显式策略都要留着，
/// 否则用户调好的接缝会被"重新分析"悄悄抹掉。
/// </summary>
public enum SeamCropMode
{
    /// <summary>还没人手工切过：切点由匹配器给。</summary>
    Auto,

    /// <summary>重叠区全部保留<b>前一段</b>的内容（b=0、t=O）。</summary>
    KeepBefore,

    /// <summary>重叠区全部保留<b>后一段</b>的内容（b=O、t=0）。这也是历史行为与默认档。</summary>
    KeepAfter,

    /// <summary>重叠区对半切：两段的中间那一行各留一半（b=⌈O/2⌉）。</summary>
    SplitMiddle,

    /// <summary>用户拖动过蓝色拼缝线，切点是他的决定，重新分析不许覆盖。</summary>
    Manual,
}

/// <summary>鼠标拖动的位移约束（需求 §5.2 工具栏"仅上下 / 仅左右 / 双向"）。</summary>
public enum MoveConstraint
{
    /// <summary>双向：上下与左右都可移动。</summary>
    Both,

    /// <summary>仅上下（纵向滚动的常见情况）。</summary>
    VerticalOnly,

    /// <summary>仅左右。</summary>
    HorizontalOnly,
}

/// <summary>
/// 各拼合模式"哪根轴允许位移"的唯一规则。
///
/// ═══ 为什么必须只有一个出处 ═══
/// 长截图是<b>同一页面纵向滚动</b>得到的连拍，物理上不存在横向位移。
/// 一旦横向能挪 N 像素，重叠带里的两份内容就会左右错开 N 列：后一段盖住
/// x∈[N, W+N)，前一段在 x∈[0, N) 那一条又露出来——同一段聊天内容在成品里出现两次，
/// 而且画布凭空变宽。这个缺陷在缩略图上很难看出来，导出后才藏不住。
///
/// 所以"锁横向"这件事不许在界面、匹配、布局、导出里各写一遍 `mode == LongScroll`：
/// 漏掉任何一处，就会出现"屏上显示 X=0、导出的图却错开 6 像素"这种自相矛盾。
/// 界面与日志一律读 <see cref="Enforce(StitchMode, LongStitch.Core.Geometry.IntPoint)"/>
/// 与 <see cref="LongStitch.Core.Layout.CanvasLayout.EffectiveOffset(LongStitch.Core.Model.Seam)"/>，
/// 像素的落位由布局兜底。
/// </summary>
public static class StitchAxes
{
    /// <summary>该模式是否锁定横向（X 恒为 0）。竖拼/横拼不锁——那两种是独立图片的自由排列。</summary>
    public static bool LocksHorizontal(StitchMode mode) => mode == StitchMode.LongScroll;

    /// <summary>
    /// 该模式是否用<b>裁切</b>去重（第九轮）。只有长截图是"同一页面纵向滚动的连拍"，
    /// 重叠区里那 O 行是<b>同一份内容的两个副本</b>，所以正确做法是把两份都裁掉一份、
    /// 让两段在接缝处首尾相接（见 <see cref="LongStitch.Core.Model.Seam.BeforeCropBottom"/>）。
    /// 竖拼/横拼是<b>不同</b>的图片，它们之间的重叠由用户拿位移决定（可以盖、可以留缝），
    /// 不存在"该裁掉哪一份"的问题，所以这一条对它们是 false——两种语义不许混在一条通路里。
    /// </summary>
    public static bool UsesCropPlacement(StitchMode mode) => mode == StitchMode.LongScroll;

    /// <summary>按模式把不允许的那根轴归零。锁横向时只抹 X，Y 原样保留。</summary>
    public static IntPoint Enforce(StitchMode mode, IntPoint offset) =>
        LocksHorizontal(mode) ? new IntPoint(0, offset.Y) : offset;

    /// <summary>
    /// 该模式下<b>实际生效</b>的拖动约束。长截图的拼接轴只有纵向，
    /// 所以无论用户存的是哪一档，这里都折算成"仅上下"显示与执行——不改写用户记录的那个字段，
    /// 切回竖拼时他自己的选择还在。
    /// </summary>
    public static MoveConstraint Enforce(StitchMode mode, MoveConstraint constraint) =>
        LocksHorizontal(mode) ? MoveConstraint.VerticalOnly : constraint;
}

/// <summary>
/// 片段清理补丁的类型。全部是整数区域操作，不含任何重采样。
///
/// 第十四轮末次只留下滚动条这一种：状态栏替换、留白、时间文字三种补丁随那项清理一起下线
/// （见 README「第四十改」）。补丁这个概念本身保留——"可撤销、可逐张关闭、可高亮、
/// 预览与导出同一条合成路径"仍然是靠它实现的。
/// </summary>
public enum PatchKind
{
    /// <summary>把指定列的像素水平延展填满目标矩形（抹除滚动条，保持画布宽度不变）。</summary>
    ExtendColumn,
}
