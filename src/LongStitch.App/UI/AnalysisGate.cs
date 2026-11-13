using LongStitch.Core;

namespace LongStitch.App.UI;

/// <summary>
/// 分析结果的写回闸门。
///
/// ═══ 它要拦的是什么 ═══
/// 分析在后台线程跑，算完后用 <c>BeginInvoke</c> 把"写回"排进 UI 消息队列。
/// 问题是<b>取消撤不回已经排进队列的回调</b>：用户点了取消、改了参数、甚至换了另一个项目，
/// 那一批旧结果照样会被写进<em>当前</em>项目——旧版就出过"把上一轮的结果盖成新项目的自动结论"。
/// 所以写回前必须再确认三件事，一件都不能少：
///   ① 还是最新一轮（轮次号没被后来的分析递增过）；
///   ② 项目实例没被换过（<c>ReplaceDocument</c> 之后 <see cref="Project"/> 对象会换）；
///   ③ 参数没被动过（开始分析之后的任何 <c>Mutate</c> 都会让 Revision 变化）。
///
/// ═══ 为什么做成单独一个类型 ═══
/// 这段判断原本内联在两个 async 方法里，只能靠"真开线程 + 真排队 + 真取消"才能量到，
/// 于是每次审查都写着"竞态本身无自动化判据"。抽出来之后，三条规则可以当场注入、当场变红
/// （见 Checks 的 <c>ui/分析写回闸门拦掉过期结果</c>），产品代码这边只是把同一句判断换个地方写。
/// </summary>
public sealed class AnalysisGate
{
    private int _generation;

    /// <summary>已开始过的轮次数（每次 <see cref="Begin"/> 递增）。判据用它核对"新一轮作废旧一轮"。</summary>
    public int Generation => _generation;

    /// <summary>一轮分析的凭据：轮次号 + 当时的项目实例与 Revision。</summary>
    /// <param name="Generation">取这张票时的轮次号。</param>
    /// <param name="Revision">取票时项目的修订号，写回时必须仍是这个值。</param>
    /// <param name="Project">取票时的项目实例，写回时必须还是同一个对象。</param>
    public readonly record struct Ticket(int Generation, int Revision, Project Project);

    /// <summary>
    /// 开始一轮分析并取票。<b>任何新一轮开始，上一轮的票立刻失效</b>——
    /// 全量分析与单条重分析共用同一个计数器，正是为了这一点。
    /// </summary>
    public Ticket Begin(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        return new Ticket(++_generation, project.Revision, project);
    }

    /// <summary>这张票现在还能不能写回。</summary>
    public bool Allows(Ticket ticket, Project current) =>
        ReferenceEquals(ticket.Project, current) &&
        ticket.Generation == _generation &&
        ticket.Revision == current.Revision;

    /// <summary>
    /// 不能写回时说清楚是哪一条（界面直接播报这句话）。
    /// 能写回时返回空串——调用方据此决定是丢弃还是落笔。
    /// </summary>
    public string? RejectReason(Ticket ticket, Project current)
    {
        if (!ReferenceEquals(ticket.Project, current)) return "项目已被替换";
        if (ticket.Generation != _generation) return "已有新一轮分析开始";
        if (ticket.Revision != current.Revision) return "分析期间参数被改动过";
        return null;
    }
}
