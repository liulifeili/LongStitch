using LongStitch.Core.Geometry;
using LongStitch.Core.Model;

namespace LongStitch.Core;

/// <summary>撤销/重做刚把文档换掉——界面据此决定要不要把显示层也一起换回去。</summary>
public enum HistoryStep
{
    /// <summary>退回了某条改动（恢复到该条目存档的「之前」）。</summary>
    Undo,

    /// <summary>重做了某条改动（回到该条目存档的「之后」）。</summary>
    Redo,
}

/// <summary>
/// 跟着一条撤销历史一起存档的<b>显示层</b>快照。
///
/// ═══ 为什么"撤销"需要它 ═══
/// <see cref="Project.Undo"/> 恢复的是 <see cref="ProjectDocument"/>：参数、布局、画布高
/// 都回到改之前，这本来就够了。但"固定后一段拖对齐"那一趟手势在<b>屏幕上</b>还留下了
/// 一次纯显示补偿——为了让图2 原地不动，视口原点跟着 Rect2 顶边挪了几行（判据
/// <c>align/固定后一段：拖对齐前后图2 的屏幕矩形一字不动、图1 跟着动</c>）。
/// 视口不属于文档，通用 Undo 从来不碰它，于是缺陷形状是：
/// <b>参数恢复了、画面位置没恢复</b>，用户得再滚一下才看起来正常（第十四轮需求一点名的那件事）。
///
/// ═══ 三条约定 ═══
/// <list type="number">
///   <item><b>Core 只搬运、不解读。</b>它不知道"视口"是什么，只是在 Undo/Redo 把文档换好之后
///     把这份快照原样交还给界面（<see cref="Project.HistoryStepped"/>）。
///     撤销栈的真相仍然只有"文档"那一份，显示状态是挂在历史条目上的一件附件。</item>
///   <item><b>Before 在按下那一刻填，After 只能填在提交之后。</b>补偿量要用<b>新参数</b>算出来的
///     布局才得出，而那份布局在 <see cref="Project.Mutate"/> 之前根本不存在。
///     所以这份快照是先交给 Mutate、再回填的同一个对象。</item>
///   <item><b>没挪过屏就不许还屏。</b><see cref="ViewportMoved"/> 为 false 时界面必须什么都不做——
///     固定前一段、蓝线那些手势从没补偿过视口，若撤销仍按绝对值把视口拽回按下那一刻的位置，
///     就等于"撤销一次参数改动"顺手抹掉了用户自己滚出来的视野。</item>
/// </list>
/// </summary>
public sealed class HistoryDisplayState
{
    /// <param name="viewportBefore">按下那一刻的视口原点（画布坐标）。</param>
    /// <param name="canvasHeightBefore">按下那一刻的画布主轴长。</param>
    /// <param name="alignmentBefore">按下那一刻这条缝的对齐位移。</param>
    /// <param name="cutBefore">按下那一刻这条缝的切点（带内行号）。</param>
    /// <param name="fixedSide">按下那一刻这条缝的固定侧。</param>
    public HistoryDisplayState(IntPoint viewportBefore, int canvasHeightBefore,
        IntPoint alignmentBefore, int cutBefore, FixedSide fixedSide)
    {
        ViewportBefore = viewportBefore;
        CanvasHeightBefore = canvasHeightBefore;
        AlignmentBefore = alignmentBefore;
        CutBefore = cutBefore;
        FixedSide = fixedSide;
        // After 先取同一个数：这趟还没提交，"屏幕被挪过"这件事此时还不成立。
        // 若中途取消（没有 Mutate），这份快照根本进不了历史，这些值也就不会被读到。
        ViewportAfter = viewportBefore;
        CanvasHeightAfter = canvasHeightBefore;
        AlignmentAfter = alignmentBefore;
        CutAfter = cutBefore;
    }

    /// <summary>
    /// 这五个量就是需求一点名"拖动开始时必须记录"的那一组。名字对应关系写全在这里，
    /// 因为按需求原文去 grep 会一个都找不到（需求写 `CutYBefore` / `Viewport.TopLeftBefore` /
    /// `FixedSide`，本类写 <c>CutBefore</c> / <c>ViewportBefore</c> / <c>FixedSide</c>，
    /// 对齐那一个需求与代码都是 <c>AlignmentOffset</c> 口径，本类叫 <c>AlignmentBefore</c>）：
    /// · <see cref="ViewportBefore"/> ＝ 需求的 Viewport.TopLeftBefore
    /// · <see cref="CanvasHeightBefore"/> ＝ 需求的 CanvasHeightBefore
    /// · <see cref="AlignmentBefore"/> ＝ 需求的 AlignmentOffsetBefore
    /// · <see cref="CutBefore"/> ＝ 需求的 CutYBefore
    /// · <see cref="FixedSide"/> ＝ 需求的 FixedSide
    /// 对账由 <c>checks/round14_audit.py</c> 常驻核对，别再靠临时 grep 的搜索词猜。
    /// </summary>
    public IntPoint ViewportBefore { get; }
    public int CanvasHeightBefore { get; }
    public IntPoint AlignmentBefore { get; }
    public int CutBefore { get; }
    public FixedSide FixedSide { get; }

    /// <summary>提交之后（含纯显示补偿）的视口原点——撤销时界面按它判断"这趟到底挪没挪屏"。</summary>
    public IntPoint ViewportAfter { get; private set; }

    /// <summary>提交后的画布主轴长（对齐手势会变、蓝线手势不变）。</summary>
    public int CanvasHeightAfter { get; private set; }

    /// <summary>提交后的对齐位移。</summary>
    public IntPoint AlignmentAfter { get; private set; }

    /// <summary>提交后的切点。</summary>
    public int CutAfter { get; private set; }

    /// <summary>这一趟有没有真的挪过屏幕。为 false 时撤销/重做都不许动视口（见类注释第 3 条）。</summary>
    public bool ViewportMoved => ViewportBefore != ViewportAfter;

    /// <summary>
    /// 回填"提交之后"那一头。只有界面算得出来：补偿发生在 <see cref="Project.Mutate"/> 之后，
    /// 而这份快照早在 Mutate 之前就已经挂进历史条目里了，所以是同一个对象被填第二次。
    /// </summary>
    public void Complete(IntPoint viewportAfter, int canvasHeightAfter,
        IntPoint alignmentAfter, int cutAfter)
    {
        ViewportAfter = viewportAfter;
        CanvasHeightAfter = canvasHeightAfter;
        AlignmentAfter = alignmentAfter;
        CutAfter = cutAfter;
    }

    /// <summary>撤销/重做该把视口放回哪一头：退回→Before，重做→After。</summary>
    public IntPoint ViewportFor(HistoryStep step) => step == HistoryStep.Undo ? ViewportBefore : ViewportAfter;

    /// <summary>
    /// 日志一行。字段名与需求十一那批 <c>seam.*</c> 记录同源（viewport/cut_y/overlap 那几个词），
    /// 事后 grep 能把"参数恢复"与"屏幕恢复"两件事对上号。
    /// </summary>
    public string Fields() =>
        $"viewport_before=({ViewportBefore.X},{ViewportBefore.Y}) " +
        $"viewport_after=({ViewportAfter.X},{ViewportAfter.Y}) viewport_moved={Low(ViewportMoved)} " +
        $"canvas_height_before={CanvasHeightBefore} canvas_height_after={CanvasHeightAfter} " +
        $"alignment_before=({AlignmentBefore.X},{AlignmentBefore.Y}) " +
        $"alignment_after=({AlignmentAfter.X},{AlignmentAfter.Y}) " +
        $"cut_before={CutBefore} cut_after={CutAfter} fixed_side={FixedSide}";

    private static string Low(bool value) => value ? "true" : "false";
}
