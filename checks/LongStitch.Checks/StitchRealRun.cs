using System.Diagnostics;
using System.Text;
using LongStitch.Core;
using LongStitch.Core.Cleanup;
using LongStitch.Core.Export;
using LongStitch.Core.Geometry;
using LongStitch.Core.Imaging;
using LongStitch.Core.Imaging.Png;
using LongStitch.Core.Layout;
using LongStitch.Core.Matching;
using LongStitch.Core.Model;
using LongStitch.Core.ProjectIO;

namespace LongStitch.Checks;

/// <summary>
/// 真机连拍端到端拼合工装：<c>--stitch &lt;目录&gt; [输出目录]</c>。
///
/// ═══ 为什么需要它（和 --calibrate 的区别）═══
/// <c>--calibrate</c> 是自监督：把<b>同一张</b>图按已知位移切开再拼回去，
/// 所以它量到的是"算法上限"，里面不含真实连拍的动态差异（时钟跳动、"正在输入"、懒加载）。
/// 本工装跑的是<b>用户真机连拍的一组截图</b>，走的是产品同一条链路
/// （解码 → 项目 → 布局 → 匹配 → 清理 → 导出），正确答案<b>没有人标注过</b>。
///
/// ═══ 没有标准答案时怎么判"完美"═══
/// 不靠"看起来对"。用四条互相独立、都能机器核对的性质：
///   1. <b>重叠区自洽</b>：按布局对齐后，两张源图的重叠行必须逐像素相同
///      （固定 UI 带除外，且被排除的行数有硬上限、逐行打印供复核）。
///      差 1 行就会在几百行上乘出成千上万个差异像素——这条能抓住任何错位。
///   2. <b>1 像素判别力</b>：把位移改成 dy±1 再比一次，差异必须严格变大。
///      若 dy-1 / dy / dy+1 三者差异相同，说明落在平台上，"对"只是碰巧。
///   3. <b>行数守恒</b>：画布高度必须等于「各图高度之和 − 各重叠之和」。
///      多吃一行或少吃一行都会在这里露出来，且与匹配算法无关（纯整数核算）。
///   4. <b>内容守恒</b>：每张图"只属于自己"的那些行，必须在成品里逐字节出现。
///      这条抓的是"接缝处吃掉唯一内容"和"任何静默重采样"。
/// 加上导出核对：成品文件读回来的尺寸与若干带的哈希，必须与离屏渲染一致。
///
/// 退出码：0 = 四条判据全过且导出成功；1 = 有判据不过；2 = 素材/环境不对。
/// </summary>
public static class StitchRealRun
{
    /// <summary>
    /// 单通道允许的最大偏差（超过才算"结构差异"）。
    /// 取 12 的依据在真图上量过：同一坐标的背景噪声最大 4 级，
    /// 而文字/气泡边缘错开 1 行会产生几十到上百级的偏差——两者差一个量级以上，
    /// 门限放在中间任何位置结论都一样（下面会把偏差分布打印出来供复核）。
    /// </summary>
    private const int NoiseTolerance = 12;

    public static int Run(string[] args)
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch (IOException) { }

        string dir = args.Length > 1 ? args[1] : "test";
        string outDir = args.Length > 2 ? args[2] : "out";
        if (!Directory.Exists(dir))
        {
            Console.WriteLine($"目录不存在：{dir}");
            return 2;
        }

        string[] files = Directory.EnumerateFiles(dir, "*.png", SearchOption.TopDirectoryOnly)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (files.Length < 2)
        {
            Console.WriteLine($"{dir} 下至少要 2 张 PNG 才能拼合，实际 {files.Length} 张");
            return 2;
        }

        Directory.CreateDirectory(outDir);
        var sw = Stopwatch.StartNew();
        var failures = new List<string>();

        // ───────────────────────── 1. 导入（产品同一条 PNG 解码路径）─────────────────────────
        Console.WriteLine($"真机连拍端到端拼合：{files.Length} 张，来源 {dir}");
        Console.WriteLine();

        var resolver = new MemoryResolver();
        var project = new Project(new ProjectDocument { Mode = StitchMode.LongScroll });
        var images = new List<RgbaImage>();
        var names = new List<string>();

        foreach (string file in files)
        {
            var t = Stopwatch.StartNew();
            PngDecodeResult decoded = PngDecoder.Decode(File.ReadAllBytes(file));
            SourceImage src = resolver.Add(decoded.Image, Path.GetFileName(file));
            project.Doc.Sources.Add(src);
            project.Doc.Segments.Add(new Segment
            {
                Id = Project.NewId("seg"),
                SourceId = src.Id,
                SourceRect = new IntRect(0, 0, decoded.Image.Width, decoded.Image.Height),
            });
            images.Add(decoded.Image);
            names.Add(Path.GetFileName(file));
            Console.WriteLine($"  导入 {Path.GetFileName(file)}  {decoded.Image.Width}×{decoded.Image.Height}  {t.ElapsedMilliseconds} ms");
        }
        project.EnsureSeams();
        var composer = new CanvasComposer(resolver);

        // ───────────────────────── 2. 自动匹配（与 MainForm.AnalyzeAll 同一套字段）─────────────
        Console.WriteLine();
        Console.WriteLine("── 自动匹配 ──");
        CanvasLayout preLayout = project.Layout;
        var matches = new List<MatchResult>();
        foreach (Seam seam in project.Doc.Seams)
        {
            PlacedSegment before = preLayout.Find(seam.BeforeId)!;
            PlacedSegment after = preLayout.Find(seam.AfterId)!;
            RgbaImage imgBefore = composer.RenderBand(preLayout, before.Rect);
            RgbaImage imgAfter = composer.RenderBand(preLayout, after.Rect);

            var t = Stopwatch.StartNew();
            // 与界面同一条参数链：长截图锁横向。这里若量出 X≠0，就该像产品一样
            // 按 X=0 采用并把这条缝标成需检查，而不是让工装比界面多知道一件事。
            MatchResult m = OverlapMatcher.Match(imgBefore, imgAfter,
                MatchOptions.For(StitchMode.LongScroll));
            matches.Add(m);

            Console.WriteLine(
                $"  {NameOf(names, before)} → {NameOf(names, after)}  " +
                $"重叠 {m.OverlapLength,4} 行  位移 X{m.Offset.X:+0;-0;0} Y{m.Offset.Y:+0;-0;0}  " +
                $"拟合 {m.Confidence * 100,6:F2}%  唯一性 {m.Uniqueness:F5}  " +
                $"{StatusIcon(m.Status)}  {m.CauseText}  {t.ElapsedMilliseconds} ms");
        }

        project.Mutate("自动匹配", d =>
        {
            for (int i = 0; i < d.Seams.Count; i++)
            {
                MatchResult m = matches[i];
                Seam s = d.Seams[i];
                // 与 SeamAnalyzer 走同一个写回出口：重叠、切点、状态一次摆正，
                // 不留"OverlapLength 改了、两侧裁切还是 0"的半套参数（那会真的让成品变高）。
                s.ApplyAutoResult(m.HasSuggestion ? m.Offset : null, m.Status, m.Cause, m.CauseText,
                    m.Confidence, m.OverlapLength, m.Candidates.ToList(), m.SeamCutPosition);
            }
        });

        int passed = matches.Count(m => m.Status == SeamStatus.AutoPassed);
        Console.WriteLine();
        Console.WriteLine($"  合计 {matches.Count} 条拼缝：{passed} 条自动通过，{matches.Count - passed} 条需检查");
        if (passed != matches.Count)
            foreach (MatchResult m in matches.Where(m => m.Status != SeamStatus.AutoPassed))
                failures.Add($"有拼缝未自动通过：{m.CauseText}（唯一性 {m.Uniqueness:F5}）");

        // ───────────────────────── 3. 诊断：哪些屏幕行根本没随滚动而变（= 固定 UI）──────────
        // 这一步与匹配算法完全无关：只问"同一屏幕坐标上，17 张图是不是同一批像素"。
        // 滚动内容的行不可能在所有图里都一样（每张停在不同的滚动位置），
        // 而状态栏、导航栏、底部输入栏这些固定 UI 会一样。
        Console.WriteLine();
        Console.WriteLine("── 诊断：逐行量「同一屏幕坐标上，相邻两张有几个结构差异像素」（极少 = 固定 UI）──");
        int width = images[0].Width;
        int rowBudget = width * 15 / 100;
        int[] agree = MeasureSameCoordinateAgree(images, NoiseTolerance, rowBudget);

        // 自证：同一张图和自己比，结构差异必须是 0（量法坏了这里会先红）
        Console.WriteLine($"  自证：图1 第0行 vs 图1 第0行 结构差异 {RowDiffs(images[0], 0, images[0], 0, 1, 0, NoiseTolerance)[0]}");

        // 固定 UI 的判据：≥80% 的相邻对在这一行上结构差异 ≤ 行宽的 15%。
        // 这条规则是<b>工装自己的</b>（第十四轮末次起产品里已经没有公共固定带检测器可对账了），
        // 用途只剩一个：把"哪些行不随滚动而变"量出来，供下面的噪声分布与排除带取数。
        int pairs = images.Count - 1;
        bool[] fixedRow = agree.Select(c => c * 5 >= pairs * 4).ToArray();

        int fixedTop = 0;
        while (fixedTop < fixedRow.Length && fixedRow[fixedTop]) fixedTop++;
        int fixedBottom = 0;
        while (fixedBottom < fixedRow.Length - fixedTop && fixedRow[fixedRow.Length - 1 - fixedBottom]) fixedBottom++;
        int fixedMiddle = fixedRow.Count(r => r) - fixedTop - fixedBottom;
        Console.WriteLine($"  规则：{pairs} 组相邻对里 ≥80% 同意、且单行结构差异 ≤ {rowBudget} 像素（行宽的 15%）");
        Console.WriteLine($"  顶部固定带 {fixedTop} 行（第 0–{fixedTop - 1} 行）");
        Console.WriteLine($"  底部固定带 {fixedBottom} 行（第 {fixedRow.Length - fixedBottom}–{fixedRow.Length - 1} 行）");
        Console.WriteLine($"  中间另有 {fixedMiddle} 行同坐标差异也在预算内（纯色背景或重复列表项，不是固定栏）");
        Console.WriteLine($"  顶部同意数轮廓（几组相邻对同意）：{RunProfile(agree, 0, Math.Min(fixedTop + 24, agree.Length))}");
        Console.WriteLine($"  底部同意数轮廓：{RunProfile(agree, Math.Max(0, agree.Length - fixedBottom - 24), agree.Length)}");

        // 把源图噪声量出来：只在"滚动内容行"上量。固定栏在两张图里本来就不是同一块画面
        // （顶栏盖住的是不同的正文），把它的差异算进噪声分布只会把数搅浑。
        {
            PlacedSegment p0 = project.Layout.Placed[0], p1 = project.Layout.Placed[1];
            int ov = p0.Rect.Bottom - p1.Rect.Y;
            int dx0 = p1.Rect.X - p0.Rect.X;
            int aRow = p0.SourceRect.Y + p0.Rect.Height - ov;
            var contentRows = new List<int>();
            for (int k = 0; k < ov; k++)
            {
                int aCoord = aRow + k - p0.SourceRect.Y;
                if (!fixedRow[k] && !fixedRow[aCoord]) contentRows.Add(k);
            }
            (int l1, int l2, int l3, int l4) = DeviationBucketsOnRows(
                images[0], aRow, images[1], p1.SourceRect.Y, contentRows, dx0);
            Console.WriteLine($"  源图噪声分布（第 1→2 条缝，{contentRows.Count} 行滚动内容 × {width} 列）：");
            Console.WriteLine($"    偏差 1–4 级 {l1} 像素，5–8 级 {l2}，9–12 级 {l3}，>{NoiseTolerance} 级（结构差异）{l4}");
            if (l4 > 0)
                failures.Add($"对齐后滚动内容里仍有 {l4} 个像素偏差超过 {NoiseTolerance} 级——不是噪声，是拼缝错位或内容被改动");
        }

        // ───────────────────────── 4. 判据 1+2：滚动行必须逐像素对齐 & 1 像素判别力 ───────────
        Console.WriteLine();
        Console.WriteLine("── 判据 1：随滚动而变的行，按布局对齐后结构差异必须为 0 ──");
        CanvasLayout layout = project.Layout;
        PlacedSegment[] placed = layout.Placed.Where(p => !p.IsEmpty).ToArray();

        for (int i = 0; i + 1 < placed.Length; i++)
        {
            PlacedSegment a = placed[i], b = placed[i + 1];
            // 第九轮之后不能再拿"两个画布矩形相交几行"当重叠：裁切档里两段首尾相接，
            // 那个数恒为 0，于是判据 1 会把 16 条缝全部"跳过"还打印一行说明——
            // 这比红更危险，因为它看起来像跑过了。重叠必须从这条缝的裁切账里取。
            SeamGeometry? band = b.IncomingSeam is null ? null : layout.DescribeSeam(b.IncomingSeam);
            int overlap = band?.Overlap ?? 0;
            int dx = b.Rect.X - a.Rect.X;
            if (overlap <= 0)
            {
                failures.Add($"{names[i]} → {names[i + 1]}：这条缝的去重量为 0（overlap={overlap}），谈不上拼合");
                Console.WriteLine($"  {i + 1}→{i + 2}  不去重，跳过");
                continue;
            }

            // A 的原点在画布上比 B 高 (overlap - A高) 行；换算成"A 的第几行对 B 的第 k 行"。
            // 裁切档里 a.Rect.Height 是**裁过之后**的可见长，所以要先把它被裁掉的那 b 行加回去，
            // 才回到"源图第 H1−O 行"这个对齐锚点（O 行重复内容在 A 的末尾、也在 B 的开头）。
            int aRowOfK = a.SourceRect.Y + a.Rect.Height + (band?.AppliedBeforeCrop ?? 0) - overlap;
            int bRowOf0 = b.SourceRect.Y - (band?.AppliedAfterCrop ?? 0);   // B 的源图第 0 行
            int[] perRow = RowDiffs(images[i], aRowOfK, images[i + 1], bRowOf0, overlap, dx, NoiseTolerance);

            // 排除规则：这一行在 A 或 B 的**屏幕坐标**上属于固定 UI，就不参与核对。
            // 两侧都要看——B 的顶部固定栏对上的是 A 的正文，A 的底部固定栏对上的是 B 的正文。
            var content = new List<int>();
            var excluded = 0;
            for (int k = 0; k < overlap; k++)
            {
                // fixedRow 的索引是原图行号。SourceRect.Y 包含上一条缝对 A 顶部的裁切，
                // 不能再减掉它；双侧裁切后那样做会把正文行误认成固定栏（或反过来）。
                int aCoord = aRowOfK + k;
                int bCoord = bRowOf0 + k;
                if ((bCoord >= 0 && bCoord < fixedRow.Length && fixedRow[bCoord]) ||
                    (aCoord >= 0 && aCoord < fixedRow.Length && fixedRow[aCoord])) excluded++;
                else content.Add(k);
            }

            var bad = content.Where(k => perRow[k] > 0).ToList();
            long contentDiff = bad.Sum(k => (long)perRow[k]);
            Console.WriteLine(
                $"  {i + 1}→{i + 2}  重叠 {overlap,4} 行 = 滚动内容 {content.Count,4} 行 + 固定 UI {excluded,4} 行；" +
                $"内容行结构差异像素 {contentDiff}，对不上的行 {bad.Count}");

            if (bad.Count > 0)
            {
                int worst = bad.OrderByDescending(k => perRow[k]).First();
                failures.Add(
                    $"{names[i]} → {names[i + 1]}：{bad.Count} 行滚动内容对不上" +
                    $"（最差是 B 的第 {worst} 行，{perRow[worst]}/{images[i + 1].Width} 个像素偏差超过 {NoiseTolerance} 级）" +
                    "——拼缝错位，或内容被改动过");
            }

            // 判据 2：只在滚动内容的行上比。固定 UI 的差异是常量噪声，
            // 混在里面会把"dy 比 dy±1 好多少"稀释到看不出来（实测 7%，几乎没有判别力）。
            long atZero = contentDiff;
            long atMinus = TotalDiffOnRows(images[i], aRowOfK - 1, images[i + 1], bRowOf0, content, dx, NoiseTolerance);
            long atPlus = TotalDiffOnRows(images[i], aRowOfK + 1, images[i + 1], bRowOf0, content, dx, NoiseTolerance);
            long contentPixels = (long)content.Count * width;
            double signalMinus = contentPixels == 0 ? 0 : 100.0 * atMinus / contentPixels;
            double signalPlus = contentPixels == 0 ? 0 : 100.0 * atPlus / contentPixels;
            Console.WriteLine(
                $"           1 像素判别（只算内容行）：dy-1 破坏 {signalMinus,5:F1}% 内容像素，" +
                $"dy 破坏 0.0%，dy+1 破坏 {signalPlus,5:F1}%");
            if (!(atMinus > atZero && atPlus > atZero))
                failures.Add(
                    $"{names[i]} → {names[i + 1]}：dy±1 的内容行差异（{atMinus} / {atPlus}）没有严格大于 dy（{atZero}），" +
                    "说明这个位置不是唯一最优，1 像素精度无从谈起");
            // 防空转：挪 1 行必须真的破坏足够多的内容像素。
            // 若内容行全是纯色背景，dy±1 的差异也是 0，判据 1 就会"绿得没有意义"。
            if (signalMinus < 2 || signalPlus < 2)
                failures.Add(
                    $"{names[i]} → {names[i + 1]}：挪 1 行只破坏 {signalMinus:F1}% / {signalPlus:F1}% 的内容像素（<2%），" +
                    "参与核对的内容太平，判据 1 的 0 差异说明不了对齐正确");
        }

        // ───────────────────────── 5. 判据 3：行数守恒 ─────────────────────────
        Console.WriteLine();
        Console.WriteLine("── 判据 3：画布高度 = 各图高度之和 − 各重叠之和（不吃行、不重行）──");
        // 同样不能从画布矩形反量：裁切档里两段首尾相接，那个差恒 0，
        // 于是"应为 43452、实为 28409"会红成"画布莫名变矮"——其实变矮正是去重本身。
        // 读布局自己的总账：Σ各段（裁边后、裁重叠前）− Σ各缝实际去掉的行 = 画布主轴长。
        DedupAccount account = layout.MeasureDedup();
        long sumHeights = account.SumExtents;
        long sumOverlap = account.SumOverlaps;
        long expected = account.Expected;
        Console.WriteLine($"  各段长度和 {sumHeights} − 重叠和 {sumOverlap} = 应为 {expected}；画布实为 {layout.CanvasSize.Height}" +
                          $"（原始各图高度和 {images.Sum(im => (long)im.Height)}）");
        if (layout.CanvasSize.Height != expected)
            failures.Add($"行数不守恒：应为 {expected}，画布实为 {layout.CanvasSize.Height}（差 {layout.CanvasSize.Height - expected} 行）");
        if (layout.CanvasSize.Width != images[0].Width)
            failures.Add($"画布宽 {layout.CanvasSize.Width} 与源图宽 {images[0].Width} 不一致");

        // ───────────────────────── 5. 判据 4：内容守恒 ─────────────────────────
        Console.WriteLine();
        Console.WriteLine("── 判据 4：每张图「只属于自己」的行，必须在成品里逐字节出现 ──");
        VerifyContentPreservation(layout, composer, images, names, failures);

        // ───────────────────────── 6. 滚动条清理，再核一遍 ─────────────────────────
        // 第十四轮末次起这里只剩滚动条一项："裁掉重复的顶部/底部固定带"那一步随功能一起下线了
        // （作废记录见 README「第四十改」）。滚动条补丁只改宽度方向的边缘窄条、不动任何几何，
        // 所以这一段的期望是"高度与重叠账一字不动，而内容守恒仍必须成立"。
        Console.WriteLine();
        Console.WriteLine("── 清理：抹除右缘滚动条（需求 §6），然后重跑判据 3/4 ──");
        Console.WriteLine($"  清理前：画布 {layout.CanvasSize.Width}×{layout.CanvasSize.Height}");
        long rawHeight = layout.CanvasSize.Height;

        project.Doc.Cleanup = new CleanupSettings { Scrollbar = new ScrollbarSettings { Enabled = true } };
        CleanupReport applied = CleanupPlanner.Apply(project, resolver, composer);
        Console.WriteLine($"  清理报告：处理 {applied.SegmentsProcessed} 段，滚动条补丁 {applied.ScrollbarPatches} 个，" +
                          $"需检查 {applied.NeedsReview.Count} 项");
        foreach (string note in applied.NeedsReview) Console.WriteLine($"    ! {note}");

        CanvasLayout cleaned = project.Layout;
        PlacedSegment[] cleanedPlaced = cleaned.Placed.Where(p => !p.IsEmpty).ToArray();
        Console.WriteLine($"  清理后画布 {cleaned.CanvasSize.Width}×{cleaned.CanvasSize.Height}" +
                          $"（滚动条清理用的是水平延展，宽高都不该变）");
        if (cleaned.CanvasSize.Height != rawHeight || cleaned.CanvasSize.Width != layout.CanvasSize.Width)
            failures.Add($"清理后画布尺寸 {cleaned.CanvasSize} 与清理前 {layout.CanvasSize} 不一致——" +
                         "滚动条补丁不许改变几何");

        Console.WriteLine("  清理后判据 4（内容守恒，重跑）：");
        VerifyContentPreservation(cleaned, composer, images, names, failures);

        // ───────────────────────── 7. 导出（产品的导出引擎，不是另写一套）─────────────────────
        Console.WriteLine();
        Console.WriteLine($"── 导出成品到 {outDir}/ ──");
        var encoders = new EncoderRegistry();
        var settings = new ExportSettings
        {
            Format = ExportFormat.Png,
            OutputDirectory = outDir,
            MaxSegmentHeight = 0,          // 不分段：先看能不能一次出整条
            SegmentOverlap = 0,
            DownscaleDivisor = 1,          // 不缩小（需求 §7：降尺寸必须用户主动选）
            FileNameTemplate = "stitched_{index}.{ext}",
        };
        ExportPlan plan = ExportPlanner.Build(project.Layout, settings, encoders, outDir, composer, resolver);
        Console.WriteLine($"  计划：原始像素 {plan.OriginalPixelSize.Width}×{plan.OriginalPixelSize.Height}，" +
                          $"{plan.Pieces.Count} 片，无损 {plan.Lossless}，硬阻断 {plan.IsHardBlock}" +
                          (plan.IsHardBlock ? $"（{plan.BlockingReason}）" : ""));

        var engine = new ExportEngine(composer, encoders);
        ExportOutcome outcome = engine.Run(project.Layout, plan, settings, outDir);
        if (!outcome.Success)
        {
            failures.Add($"导出失败：{outcome.Error}");
            Console.WriteLine($"  ✗ {outcome.Error}");
        }
        else
        {
            foreach (string f in outcome.Files)
                Console.WriteLine($"  ✓ {f}  {new FileInfo(f).Length / 1024} KB");

            // 读回核对：尺寸用独立解析 IHDR 得到，再抽三条带与离屏渲染比哈希
            string first = outcome.Files[0];
            byte[] bytes = File.ReadAllBytes(first);
            (int w, int h) = ReadIhdr(bytes);
            Console.WriteLine($"  读回 IHDR：{w}×{h}");
            if (w != plan.OriginalPixelSize.Width || h != plan.OriginalPixelSize.Height)
                failures.Add($"导出文件尺寸 {w}×{h} 与计划 {plan.OriginalPixelSize} 不一致");

            if (outcome.Files.Count == 1)
            {
                var t = Stopwatch.StartNew();
                RgbaImage back = PngDecoder.Decode(bytes).Image;
                Console.WriteLine($"  全量解码读回 {t.ElapsedMilliseconds} ms，抽带比哈希…");
                int[] probes = { 0, h / 2, Math.Max(0, h - 60) };
                foreach (int y in probes)
                {
                    int rows = Math.Min(60, h - y);
                    var rect = new IntRect(0, y, w, rows);
                    RgbaImage fromFile = back.SubImage(rect);
                    RgbaImage fromComposer = composer.RenderBand(project.Layout, rect);
                    bool same = Convert.ToHexString(fromFile.RegionSha256(fromFile.Bounds))
                                == Convert.ToHexString(fromComposer.RegionSha256(fromComposer.Bounds));
                    Console.WriteLine($"    第 {y,6} 行起 {rows} 行：{(same ? "一致 ✓" : "不一致 ✗")}");
                    if (!same) failures.Add($"导出文件第 {y} 行起 {rows} 行与离屏渲染不一致");
                }

                // 独立性核对：成品文件再交给 GDI+（与自研编解码互不相关的一套实现）解一遍。
                // 只用自己的解码器读回，只能证明"它和自己一致"；两套实现算出同样的字节，
                // 才能说这个 PNG 是任何标准解码器都能正确打开的文件。
                Console.WriteLine("  GDI+ 独立解码核对（同一批抽样带）：");
                foreach (int y in probes)
                {
                    int rows = Math.Min(60, h - y);
                    var rect = new IntRect(0, y, w, rows);
                    RgbaImage viaGdi = GdiBand(bytes, rect);
                    RgbaImage fromComposer = composer.RenderBand(project.Layout, rect);
                    bool same = Convert.ToHexString(viaGdi.RegionSha256(viaGdi.Bounds))
                                == Convert.ToHexString(fromComposer.RegionSha256(fromComposer.Bounds));
                    Console.WriteLine($"    第 {y,6} 行起 {rows} 行：{(same ? "一致 ✓" : "不一致 ✗")}");
                    if (!same) failures.Add($"GDI+ 解出的第 {y} 行起 {rows} 行与离屏渲染不一致——成品文件本身有问题");
                }
            }
        }

        // 人眼复核图：字节级判据能证明"算得对"，但接缝处有没有肉眼可见的台阶/重影，
        // 最终还是要人看一眼。把每条缝上下各 60 行取出来竖着叠成一张。
        // 注意：相邻两条带之间在成品里本来就不连续（中间跳过了几百行），
        // 所以带与带之间必须画一条醒目的分隔条，否则"带尾接带首"会被误看成接缝重影。
        // 缝的位置用一条蓝线标在每带正中。
        const int bandRows = 120;
        const int sepRows = 10;
        int bands = Math.Max(1, cleanedPlaced.Length - 1);
        var preview = new RgbaImage(cleaned.CanvasSize.Width, bands * (bandRows + sepRows));
        preview.Fill(new RgbaColor(255, 0, 255, 255));      // 分隔条：品红，真内容里不会出现
        for (int i = 0; i + 1 < cleanedPlaced.Length; i++)
        {
            int ySeam = cleanedPlaced[i + 1].Rect.Y;
            int y0 = Math.Max(0, ySeam - bandRows / 2);
            RgbaImage band = composer.RenderBand(cleaned, new IntRect(0, y0, cleaned.CanvasSize.Width, bandRows));
            int at = i * (bandRows + sepRows);
            preview.Blit(band, band.Bounds, 0, at);
            // 缝的标记只画在左右灰边里（各 120px），绝不穿过正文：
            // 早先整行画蓝线，线压住字形时"标记切的"和"拼缝切的"肉眼分不开，复核就失去意义。
            int mark = at + bandRows / 2 - 1;
            preview.Fill(new IntRect(0, mark, 120, 2), new RgbaColor(0, 90, 255, 255));
            preview.Fill(new IntRect(cleaned.CanvasSize.Width - 120, mark, 120, 2),
                new RgbaColor(0, 90, 255, 255));
        }
        string previewPath = Path.Combine(outDir, "seams_preview.png");
        File.WriteAllBytes(previewPath, PngEncoder.EncodeToBytes(preview, new PngEncodeOptions()));
        Console.WriteLine($"  接缝人眼复核图：{previewPath}（{bands} 条缝 × 每条 {bandRows} 行；" +
                          "蓝标记在左右灰边=缝的高度，品红条=带与带的分隔，都不是内容）");

        // 单缝高清放大：拼图缩过之后看不清字形有没有被切，
        // 用 --zoom <缝序号> 单独导出一条缝上下各 200 行的原尺寸图。
        int zoomArg = Array.IndexOf(args, "--zoom");
        if (zoomArg >= 0 && zoomArg + 1 < args.Length &&
            int.TryParse(args[zoomArg + 1], out int zoomSeam) && zoomSeam >= 1 && zoomSeam < cleanedPlaced.Length)
        {
            const int zoomRows = 400;
            int ySeam = cleanedPlaced[zoomSeam].Rect.Y;
            int y0 = Math.Max(0, ySeam - zoomRows / 2);
            int rows = Math.Min(zoomRows, cleaned.CanvasSize.Height - y0);
            RgbaImage zoom = composer.RenderBand(cleaned, new IntRect(0, y0, cleaned.CanvasSize.Width, rows));
            int zm = ySeam - y0 - 1;
            zoom.Fill(new IntRect(0, zm, 120, 2), new RgbaColor(0, 90, 255, 255));
            zoom.Fill(new IntRect(cleaned.CanvasSize.Width - 120, zm, 120, 2), new RgbaColor(0, 90, 255, 255));
            string zoomPath = Path.Combine(outDir, $"zoom_seam{zoomSeam}.png");
            File.WriteAllBytes(zoomPath, PngEncoder.EncodeToBytes(zoom, new PngEncodeOptions()));
            Console.WriteLine($"  单缝放大图：{zoomPath}（第 {zoomSeam} 条缝上下各 {zoomRows / 2} 行，蓝标记在左右灰边）");
        }

        // ───────────────────────── 8. 汇总 ─────────────────────────
        sw.Stop();
        Console.WriteLine();
        Console.WriteLine($"耗时 {sw.ElapsedMilliseconds / 1000.0:F1} s，成品画布 {project.Layout.CanvasSize.Width}×{project.Layout.CanvasSize.Height}（清理后）");
        if (failures.Count == 0)
        {
            Console.WriteLine("结论：四条判据全过，导出核对一致。");
            return 0;
        }

        Console.WriteLine($"结论：{failures.Count} 项不达标——");
        foreach (string f in failures) Console.WriteLine($"  · {f}");
        return 1;
    }

    private static string NameOf(List<string> names, PlacedSegment p) =>
        p.Index < names.Count ? names[p.Index] : p.Source.FileName;

    private static string StatusIcon(SeamStatus s) => s switch
    {
        SeamStatus.AutoPassed => "✓自动",
        SeamStatus.NeedsReview => "!需检查",
        SeamStatus.ManualPending => "手动待确认",
        SeamStatus.ManualConfirmed => "✓手动确认",
        _ => s.ToString(),
    };

    /// <summary>
    /// 逐行数差异像素：A 从 <paramref name="aRow"/> 起、B 从 <paramref name="bRow"/> 起，
    /// 共 <paramref name="rows"/> 行，横向错开 <paramref name="dx"/>（B 相对 A）。
    ///
    /// <paramref name="tolerance"/> 是"单通道允许的最大偏差"：只统计超过它的像素。
    /// 为什么必须有它：真机连拍的截图并不自洽——同一张图里背景左半 233、中段 237，
    /// 相邻两张之间同一坐标还会再差 1–4 级（有损中间产物留下的低幅噪声）。
    /// 拿"逐字节相同"当判据，在这种数据上永远红，而且红了也说明不了拼缝错。
    /// 真正要抓的是<b>结构差异</b>：文字边缘、气泡边界错开 1 行就会产生成百上千个
    /// 几十级以上的偏差像素，噪声永远到不了那个量级。
    /// </summary>
    private static int[] RowDiffs(RgbaImage a, int aRow, RgbaImage b, int bRow, int rows, int dx,
                                  int tolerance = 0)
    {
        var result = new int[rows];
        int x0 = Math.Max(0, -dx);
        int x1 = Math.Min(a.Width, b.Width - dx);
        for (int k = 0; k < rows; k++)
        {
            int ya = aRow + k, yb = bRow + k;
            if (ya < 0 || ya >= a.Height || yb < 0 || yb >= b.Height)
            {
                result[k] = x1 - x0;      // 越界＝整行都无从比较，按全差计
                continue;
            }
            ReadOnlySpan<byte> ra = a.Row(ya);
            ReadOnlySpan<byte> rb = b.Row(yb);
            int count = 0;
            for (int x = x0; x < x1; x++)
            {
                int pa = x * 4, pb = (x + dx) * 4;
                int d = Math.Max(Math.Abs(ra[pa] - rb[pb]),
                        Math.Max(Math.Abs(ra[pa + 1] - rb[pb + 1]), Math.Abs(ra[pa + 2] - rb[pb + 2])));
                if (d > tolerance) count++;
            }
            result[k] = count;
        }
        return result;
    }

    /// <summary>
    /// 统计指定行对齐后"最大通道偏差"的分布，用来把源图噪声量出来写进结论。
    /// 返回 (偏差1–4 的像素数, 5–8, 9–12, &gt;tolerance)。最后一档就是结构差异。
    /// </summary>
    private static (int L1, int L2, int L3, int L4) DeviationBucketsOnRows(
        RgbaImage a, int aRow, RgbaImage b, int bRow, IReadOnlyList<int> rows, int dx)
    {
        var buckets = new int[4];
        foreach (int k in rows)
        {
            ReadOnlySpan<byte> ra = a.Row(aRow + k);
            ReadOnlySpan<byte> rb = b.Row(bRow + k);
            for (int x = 0; x < a.Width; x++)
            {
                int pa = x * 4, pb = (x + dx) * 4;
                int d = Math.Max(Math.Abs(ra[pa] - rb[pb]),
                        Math.Max(Math.Abs(ra[pa + 1] - rb[pb + 1]), Math.Abs(ra[pa + 2] - rb[pb + 2])));
                if (d == 0) continue;
                buckets[d <= 4 ? 0 : d <= 8 ? 1 : d <= NoiseTolerance ? 2 : 3]++;
            }
        }
        return (buckets[0], buckets[1], buckets[2], buckets[3]);
    }

    /// <summary>只累加指定行号的差异像素数（用于"把位移挪 1 行再比一次"）。</summary>
    private static long TotalDiffOnRows(RgbaImage a, int aRow, RgbaImage b, int bRow,
                                        IReadOnlyList<int> rows, int dx, int tolerance)
    {
        long total = 0;
        foreach (int k in rows)
        {
            int ya = aRow + k, yb = bRow + k;
            if (ya < 0 || ya >= a.Height || yb < 0 || yb >= b.Height)
            {
                total += Math.Max(0, Math.Min(a.Width, b.Width - dx) - Math.Max(0, -dx));
                continue;
            }
            ReadOnlySpan<byte> ra = a.Row(ya);
            ReadOnlySpan<byte> rb = b.Row(yb);
            int x0 = Math.Max(0, -dx), x1 = Math.Min(a.Width, b.Width - dx);
            for (int x = x0; x < x1; x++)
            {
                int pa = x * 4, pb = (x + dx) * 4;
                int d = Math.Max(Math.Abs(ra[pa] - rb[pb]),
                        Math.Max(Math.Abs(ra[pa + 1] - rb[pb + 1]), Math.Abs(ra[pa + 2] - rb[pb + 2])));
                if (d > tolerance) total++;
            }
        }
        return total;
    }

    /// <summary>
    /// 对每个屏幕行坐标 r，数"有多少组相邻图对在这一行上的结构差异在预算内"。
    /// 阈值口径（≥80% 相邻对同意）是<b>工装自己定的</b>：第十四轮末次起产品里已经没有公共固定带
    /// 检测器，不存在"两边逐行对账"这一说了，这一路只服务本工装的诊断与排除带取数。
    /// </summary>
    private static int[] MeasureSameCoordinateAgree(List<RgbaImage> images, int tolerance, int rowBudget)
    {
        int height = images.Min(im => im.Height);
        int pairs = images.Count - 1;
        var perPair = new int[pairs][];
        for (int i = 0; i < pairs; i++)
            perPair[i] = RowDiffs(images[i], 0, images[i + 1], 0, height, 0, tolerance);

        var agree = new int[height];
        for (int r = 0; r < height; r++)
            for (int i = 0; i < pairs; i++)
                if (perPair[i][r] <= rowBudget) agree[r]++;
        return agree;
    }

    /// <summary>把一段"每行差异像素数"的数组压成游程串，便于人眼扫边界。</summary>
    private static string RunProfile(int[] values, int from, int to)
    {
        var sb = new StringBuilder();
        int runStart = from;
        for (int r = from + 1; r <= to; r++)
        {
            if (r == to || values[r] != values[runStart])
            {
                sb.Append($"[{runStart}–{r - 1}]={values[runStart]} ");
                runStart = r;
            }
        }
        string s = sb.ToString().TrimEnd();
        return s.Length > 300 ? s[..300] + "…" : s;
    }

    /// <summary>
    /// 用 GDI+ 解出 PNG 里的一块矩形（BGRA → RGBA）。
    /// 与自研解码器互不相关，用来交叉核对导出文件：两套实现给出同样的字节，
    /// 才说明这个 PNG 是标准解码器都能正确打开的文件，而不是"只有我们自己读得懂"。
    /// </summary>
    private static RgbaImage GdiBand(byte[] png, IntRect rect)
    {
        using var ms = new MemoryStream(png);
        using var bmp = new System.Drawing.Bitmap(ms);
        var bounds = new System.Drawing.Rectangle(rect.X, rect.Y, rect.Width, rect.Height);
        var bd = bmp.LockBits(bounds, System.Drawing.Imaging.ImageLockMode.ReadOnly,
                              System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            var buf = new byte[bd.Stride * rect.Height];
            System.Runtime.InteropServices.Marshal.Copy(bd.Scan0, buf, 0, buf.Length);
            var img = new RgbaImage(rect.Width, rect.Height);
            for (int y = 0; y < rect.Height; y++)
            {
                Span<byte> dst = img.Row(y);
                for (int x = 0; x < rect.Width; x++)
                {
                    int s = y * bd.Stride + x * 4;
                    dst[x * 4] = buf[s + 2];          // R
                    dst[x * 4 + 1] = buf[s + 1];      // G
                    dst[x * 4 + 2] = buf[s];          // B
                    dst[x * 4 + 3] = buf[s + 3];      // A
                }
            }
            return img;
        }
        finally
        {
            bmp.UnlockBits(bd);
        }
    }

    /// <summary>
    /// 判据 4：每张图"只属于自己"的那一段，必须在成品画布里逐字节出现。
    ///
    /// 取法对"清理前/清理后"都成立：第 i 段在画布上真正露出来的区间是
    /// [上一段的底边, 下一段的顶边)——再往上被上一段盖住（清理前）或裁掉（清理后），
    /// 再往下被下一段盖住。把这段画布区域与源图对应行做 sha256 比对，
    /// 就能抓住"接缝吃掉唯一内容""同一段内容出现两次""任何静默重采样"。
    /// </summary>
    private static void VerifyContentPreservation(CanvasLayout layout, CanvasComposer composer,
                                                    List<RgbaImage> images, List<string> names,
                                                    List<string> failures)
    {
        PlacedSegment[] placed = layout.Placed.Where(p => !p.IsEmpty).ToArray();
        int checkedRows = 0;
        for (int i = 0; i < placed.Length; i++)
        {
            int yTop = i == 0 ? placed[0].Rect.Y : placed[i - 1].Rect.Bottom;
            int yBottom = i + 1 < placed.Length ? placed[i + 1].Rect.Y : layout.CanvasSize.Height;
            int rows = yBottom - yTop;
            if (rows <= 0)
            {
                failures.Add($"{names[i]}：在成品里露出 {rows} 行，说明它被前后两张完全吃掉了");
                continue;
            }

            int sourceFrom = placed[i].SourceRect.Y + (yTop - placed[i].Rect.Y);
            var canvasRect = new IntRect(placed[i].Rect.X, yTop, placed[i].Rect.Width, rows);
            RgbaImage composed = composer.RenderBand(layout, canvasRect);
            RgbaImage original = images[i].SubImage(new IntRect(0, sourceFrom, images[i].Width, rows));

            bool same = composed.Height == original.Height
                        && composed.Width == original.Width
                        && Convert.ToHexString(composed.RegionSha256(composed.Bounds))
                           == Convert.ToHexString(original.RegionSha256(original.Bounds));
            checkedRows += rows;
            Console.WriteLine($"  {names[i]}  露出 {rows,4} 行（源图第 {sourceFrom}–{sourceFrom + rows - 1} 行）  逐字节一致 {(same ? "✓" : "✗")}");
            if (!same)
            {
                int shown = 0;
                for (int k = 0; k < rows && shown < 3; k++)
                {
                    ReadOnlySpan<byte> rc = composed.Row(k);
                    ReadOnlySpan<byte> ro = original.Row(k);
                    if (rc.SequenceEqual(ro)) continue;
                    shown++;
                    int firstX = -1;
                    for (int x = 0; x < original.Width && firstX < 0; x++)
                        if (rc[x * 4] != ro[x * 4] || rc[x * 4 + 1] != ro[x * 4 + 1] || rc[x * 4 + 2] != ro[x * 4 + 2])
                            firstX = x;
                    Console.WriteLine(
                        $"      第 {sourceFrom + k} 行对不上（首个差异 x={firstX}）：" +
                        $"成品 ({rc[firstX * 4]},{rc[firstX * 4 + 1]},{rc[firstX * 4 + 2]}) vs " +
                        $"源图 ({ro[firstX * 4]},{ro[firstX * 4 + 1]},{ro[firstX * 4 + 2]})");
                }
                failures.Add($"{names[i]}：它露出的 {rows} 行在成品里不是逐字节相同——被吃掉、被重复或被重采样了");
            }
        }
        Console.WriteLine($"  合计核对 {checkedRows} 行");
    }

    /// <summary>独立解析 IHDR 的宽高（不复用产品解码器，避免"用同一把尺量自己"）。</summary>
    private static (int Width, int Height) ReadIhdr(byte[] png)
    {
        if (png.Length < 24) throw new InvalidDataException("文件太短，不像 PNG");
        // 8 字节签名 + 4 长度 + 4 类型("IHDR") 之后是 宽(4) 高(4)，均为大端
        int w = (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19];
        int h = (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23];
        return (w, h);
    }
}
