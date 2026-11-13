using LongStitch.Core.Geometry;
using LongStitch.Core.Imaging;
using LongStitch.Core.Layout;
using LongStitch.Core.Model;

namespace LongStitch.Core.Cleanup;

/// <summary>一次清理的产出统计，界面用它写"处理了 N 张、M 处需检查"。</summary>
public sealed record CleanupReport(
    int SegmentsProcessed,
    int ScrollbarPatches,
    IReadOnlyList<string> NeedsReview);

/// <summary>
/// 清理规划器：把需求 §6 里留下的那一项清理（滚动条）落成一组 <see cref="PixelPatch"/>。
///
/// 第十四轮末次下线了"重复状态栏 / 底部固定栏"的合并、替换、裁剪、留白与时间替换，
/// 连同它们的公共固定带检测（DetectCommonBand）、画布手动框选通路、
/// Segment.AutoCrop 与 Seam.CleanupBandRemoved 一起删掉了——作废记录与恢复方法见
/// README「第四十改」。匹配算法用的 <see cref="FixedRegionAnalyzer"/> 不在这次删除范围内。
///
/// 三条仍然成立的约定：
/// 1. <b>幂等</b>。每次运行先按 <see cref="AutoReasonPrefix"/> 清掉上一轮自动生成的补丁，
///    再重新检测；否则"多点几次清理"会逐轮吃掉正文——这类错误一旦导出就不可挽回。
/// 2. <b>检测看未清理的原始像素</b>（RenderForDetection 不含上一轮写入的补丁），
///    避免在上一次的结果上再检测一遍。
/// 3. <b>不确定就不动手</b>。置信度不够只记进 NeedsReview，由界面标"需检查"。
/// </summary>
public static class CleanupPlanner
{
    /// <summary>自动生成的补丁都以这个前缀开头，用于识别"这是我上一轮写的，可以重来"。</summary>
    public const string AutoReasonPrefix = "清理·";

    public static CleanupReport Apply(
        Project project,
        ISourceResolver resolver,
        CanvasComposer composer,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(composer);

        CanvasLayout layout = project.Layout;
        PlacedSegment[] placed = layout.Placed.Where(p => !p.IsEmpty).ToArray();
        if (placed.Length == 0)
            return new CleanupReport(0, 0, Array.Empty<string>());

        // ── 1. 取每段的"检测画面"：只含用户裁边，不含上一轮写入的补丁 ──
        var review = new List<string>();
        var images = new RgbaImage[placed.Length];
        CleanupSettings s = project.Doc.Cleanup;
        for (int i = 0; i < placed.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            RgbaImage? view = composer.RenderForDetection(placed[i]);
            if (view is null)
            {
                review.Add($"第 {i + 1} 段：裁边后没有剩余内容，跳过清理");
                images[i] = new RgbaImage(1, 1);
                continue;
            }
            images[i] = view;
        }

        // ── 2. 逐段生成滚动条补丁：够自信才生成，否则只记一句需检查 ──────
        int scrollbarCount = 0;
        var patchesBySegment = new PixelPatch[placed.Length][];

        for (int i = 0; i < placed.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            RgbaImage img = images[i];
            var list = new List<PixelPatch>();

            if (s.Scrollbar.Enabled)
            {
                ScrollbarDetection? det = ScrollbarDetector.Detect(img, s.Scrollbar.MaxThickness);
                if (det is not null && det.Value.Confidence < s.Scrollbar.MinConfidence)
                {
                    review.Add($"第 {i + 1} 段：疑似滚动条（{det.Value.Side}，置信度 " +
                               $"{det.Value.Confidence:P0}）低于门限 {s.Scrollbar.MinConfidence:P0}，未自动处理");
                }
                else if (det is not null)
                {
                    int barLeft = det.Value.Side == EdgeSide.Right ? det.Value.BarLeft : 0;
                    int barWidth = img.Width - barLeft;
                    list.Add(new PixelPatch
                    {
                        Id = Project.NewId("patch"),
                        Kind = PatchKind.ExtendColumn,
                        // Target 的口径是"本段有效区域的局部坐标"，而检测画面正是这个口径
                        Target = new IntRect(barLeft, det.Value.Top, barWidth, det.Value.Length),
                        // 取条体内侧 3 列的像素水平延展，保持宽度不变、不涂正文
                        ExtendColumnX = Math.Max(0, barLeft - 3),
                        Reason = $"{AutoReasonPrefix}滚动条：{det.Value.Side} 侧厚 {det.Value.Thickness}px、" +
                                 $"长 {det.Value.Length}px，置信度 {det.Value.Confidence:P0}",
                        Confidence = det.Value.Confidence,
                    });
                    scrollbarCount++;
                }
            }

            patchesBySegment[i] = list.ToArray();
        }

        // ── 3. 一次性写回（一条可撤销记录，而不是 N 条）──────────────────
        project.Mutate("应用清理", d =>
        {
            for (int i = 0; i < placed.Length; i++)
            {
                Segment? seg = d.FindSegment(placed[i].Segment.Id);
                if (seg is null) continue;
                seg.Patches.RemoveAll(x => x.Reason.StartsWith(AutoReasonPrefix, StringComparison.Ordinal));
                seg.Patches.AddRange(patchesBySegment[i]);
            }
        });

        return new CleanupReport(placed.Length, scrollbarCount, review);
    }
}
