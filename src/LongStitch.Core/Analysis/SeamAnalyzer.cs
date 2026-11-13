using LongStitch.Core.Geometry;
using LongStitch.Core.Imaging;
using LongStitch.Core.Layout;
using LongStitch.Core.Matching;
using LongStitch.Core.Model;

namespace LongStitch.Core.Analysis;

/// <summary>
/// 拼缝分析调度器：把匹配算法接到项目参数上。
///
/// 单独成一层的原因：算法本身是纯函数（两张图 → 一个结论），
/// 而"读布局、渲染两侧有效像素、把结论写回参数、处理取消与进度"是另一件事。
/// 分开之后算法可以脱离项目结构被直接测（Checks 就是直接调 <see cref="OverlapMatcher"/>）。
/// </summary>
public sealed class SeamAnalyzer
{
    private readonly CanvasComposer _composer;

    public SeamAnalyzer(CanvasComposer composer) =>
        _composer = composer ?? throw new ArgumentNullException(nameof(composer));

    /// <summary>
    /// 重新分析全部拼缝。
    /// 注意：布局只在开始时读一次。分析过程中不许改参数（界面应禁用这些操作），
    /// 否则会出现"用第 3 版的布局算第 1 版的缝"这种难查的错位。
    /// </summary>
    public void AnalyzeAll(
        Project project,
        MatchOptions? options = null,
        IProgress<(int Done, int Total)>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        CanvasLayout layout = project.Layout;
        int total = layout.SeamCount;

        for (int i = 0; i < total; i++)
        {
            ct.ThrowIfCancellationRequested();
            Seam? seam = layout.SeamAfter(i);
            if (seam is not null) AnalyzeOne(project, seam, options, layout);
            progress?.Report((i + 1, total));
        }
    }

    /// <summary>只重算一条拼缝（排序/删段后"仅重算受影响的拼缝"就靠它）。</summary>
    public bool AnalyzeOne(Project project, Seam seam, MatchOptions? options = null, CanvasLayout? layout = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(seam);

        layout ??= project.Layout;
        PlacedSegment? before = layout.Find(seam.BeforeId);
        PlacedSegment? after = layout.Find(seam.AfterId);
        if (before is null || after is null) return false;

        // 取两侧<b>未裁重叠</b>的完整内容：被蓝线切掉的那几行是两张截图唯一公共的部分，
        // 匹配器全靠它对位。用 placed.Rect（切完那块）会让重分析整批归零，
        // 出处与理由见 <see cref="CanvasComposer.RenderMatchInput"/>。
        RgbaImage imageBefore = _composer.RenderMatchInput(before);
        RgbaImage imageAfter = _composer.RenderMatchInput(after);

        // 参数按模式生成：长截图锁横向，竖拼/横拼不锁。
        MatchResult result = OverlapMatcher.Match(imageBefore, imageAfter,
            MatchOptions.For(project.Doc.Mode, options));

        project.Mutate("自动匹配拼缝", _ =>
        {
            Seam target = project.Doc.Seams.FirstOrDefault(s => s.Id == seam.Id) ?? seam;
            // 第九轮：写回一律走 Seam.ApplyAutoResult 这个唯一出口。
            // 原来这里逐个字段自己赋一遍，`OverlapLength` 是直写的——
            // 裁切模型里那会让"两侧裁切之和 ≠ 重叠"（也就是去重静默失效），
            // 而且绕过了"用户切过的缝不被覆盖"这条保护。
            // 传进去的 autoCut 是匹配器选出的正文区切点；用户手动切过的缝由模型保留原值。
            target.ApplyAutoResult(
                result.HasSuggestion ? result.Offset : null,
                result.Status, result.Cause, result.CauseText,
                result.Confidence, result.OverlapLength, result.Candidates.ToList(),
                result.SeamCutPosition);
        });
        return true;
    }
}
