using LongStitch.Core.Geometry;
using LongStitch.Core.Imaging;
using LongStitch.Core.Layout;
using LongStitch.Core.Model;

namespace LongStitch.Core.Export;

/// <summary>一个待写出的文件（单张导出时只有一片）。</summary>
/// <param name="Index">序号，从 1 开始。</param>
/// <param name="Total">总片数。</param>
/// <param name="CanvasRegion">这片覆盖画布的哪个矩形（整数）。</param>
/// <param name="FileName">最终文件名。</param>
public sealed record ExportPiece(int Index, int Total, IntRect CanvasRegion, string FileName)
{
    public int Width => CanvasRegion.Width;
    public int Height => CanvasRegion.Height;
}

/// <summary>
/// 导出计划：把"用户想要的东西"翻译成一份可展示、可核对的事实清单。
///
/// 界面上的导出面板基本就是把这个对象念出来——像素尺寸、格式、预估大小、
/// 是否触及限制、要分成几片、每片多大。之所以做成一个对象而不是一堆返回值，
/// 是因为需求 §7 要求"导出前显示真实尺寸与限制提示"，这些信息必须同源，
/// 不能"预览显示一个尺寸、导出写另一个尺寸"。
/// </summary>
public sealed record ExportPlan
{
    /// <summary>按源图与编辑参数算出的原始像素尺寸（不受预览缩放影响）。</summary>
    public required IntSize OriginalPixelSize { get; init; }

    /// <summary>实际输出尺寸。只有用户显式降尺寸时才与上者不同。</summary>
    public required IntSize OutputPixelSize { get; init; }

    /// <summary>用户主动选择的缩小倍数（1 = 不缩）。</summary>
    public int DownscaleDivisor { get; init; } = 1;

    public ExportFormat Format { get; init; }

    /// <summary>该格式能否承诺字节级无损。界面用它决定"无损"三个字能不能写。</summary>
    public bool Lossless { get; init; }

    /// <summary>采样估算的总字节数；0 表示未估算（例如还没接编码器）。</summary>
    public long EstimatedTotalBytes { get; init; }

    /// <summary>限制判定。Fits=false 时不许直接导单张。</summary>
    public FormatLimits.Verdict Verdict { get; init; }

    /// <summary>待写出的文件列表。</summary>
    public required IReadOnlyList<ExportPiece> Pieces { get; init; }

    /// <summary>是否因为限制或用户设置而需要分段。</summary>
    public bool NeedsSplit { get; init; }

    /// <summary>不能单文件导出时的原因；null 表示可以。</summary>
    public string? BlockingReason => Verdict.Fits ? null : Verdict.Reason;

    /// <summary>硬性阻塞（true）还是仅兼容性警告（false）。警告不拦用户。</summary>
    public bool IsHardBlock => Verdict.Fits is false && Verdict.Hard;

    /// <summary>人类可读的一行摘要，给状态栏与确认对话框用。</summary>
    public string Summary =>
        $"{OutputPixelSize.Width}×{OutputPixelSize.Height}px · {Format}" +
        $" · {Pieces.Count} 个文件 · 约 {FormatBytes(EstimatedTotalBytes)}" +
        (Lossless ? " · 无损" : " · 有损");

    public static string FormatBytes(long bytes) => bytes switch
    {
        <= 0 => "未知",
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024.0:F1} MB",
        _ => $"{bytes / 1024.0 / 1024.0 / 1024.0:F2} GB",
    };
}

/// <summary>导出计划计算器。</summary>
public static class ExportPlanner
{
    /// <summary>
    /// 生成计划。
    /// </summary>
    /// <param name="layout">当前布局（决定原始像素尺寸与拼缝所在行）。</param>
    /// <param name="settings">导出设置。</param>
    /// <param name="encoders">编码器注册表，用于判断格式可用性与无损性。</param>
    /// <param name="outputDir">目标目录；用于查磁盘剩余空间。可为 null。</param>
    /// <param name="sampler">给了就按真实编码器采样几带来估算体积（准，但要算）；不给则预估为 0。</param>
    public static ExportPlan Build(
        CanvasLayout layout,
        ExportSettings settings,
        EncoderRegistry encoders,
        string? outputDir = null,
        CanvasComposer? sampler = null,
        ISourceResolver? probe = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(encoders);

        IntSize original = layout.CanvasSize;
        int divisor = Math.Max(1, settings.DownscaleDivisor);
        IntSize output = ApplyDivisor(original, divisor);

        long freeDisk = GetFreeDiskBytes(outputDir);
        FormatLimits.Verdict verdict = FormatLimits.Check(settings.Format, output.Width, output.Height, freeDisk);

        // 缺失源图在**计划阶段**点名硬拦（审查第三轮 #3）：
        // 不拦的话缺失片段在布局里尺寸为 0，会被静默跳过——用户拿到的是"少了几段"的成品。
        // probe 给了就用真解析器逐段验（能同时抓住"标记缺失/文件被删/尺寸与记录不符"）；
        // 没给就只按布局快照里的缺失标记与零尺寸兜底。
        string[] missingNames = FindUnusableSources(layout, probe);
        bool missingBlocked = missingNames.Length > 0;
        if (missingBlocked)
            verdict = new FormatLimits.Verdict(false, true,
                $"有 {missingNames.Length} 张源图不可用，导出已阻止：\n· " +
                string.Join("\n· ", missingNames.Take(12)) +
                (missingNames.Length > 12 ? $"\n… 还有 {missingNames.Length - 12} 张" : "") +
                "\n请在左侧列表右键「重新定位源文件」，或删除对应片段。",
                verdict.SuggestedMaxHeight);

        // 分段**只由用户显式选择**（审查第三轮 #2）：0 = 不分段，哪怕超出查看器建议上限。
        // 建议上限只进 Verdict 提示文字，绝不替用户自动分段——
        // 界面写着"0=不分段"却偷偷出多个文件，比报错更伤信任。
        int effectiveMax = settings.MaxSegmentHeight;
        bool needsSplit = effectiveMax > 0 && output.Height > effectiveMax;

        var pieces = new List<ExportPiece>();
        string extension = ExtensionOf(settings.Format);
        if (needsSplit)
        {
            var ranges = ComputeRanges(output.Height, effectiveMax,
                Math.Max(0, settings.SegmentOverlap), SeamRows(layout, output.Height, divisor));
            for (int i = 0; i < ranges.Count; i++)
            {
                (int start, int end) = ranges[i];
                pieces.Add(new ExportPiece(i + 1, ranges.Count,
                    new IntRect(0, start, output.Width, end - start),
                    RenderName(settings.FileNameTemplate, i + 1, extension)));
            }

            // 用户既然显式选了分段，"整图单边超限/整块缓冲超限"就不该再拦这条路
            //（审查第四轮 P1）：总高 70000 的 JPEG 被拦住时，"每片 30000"正是解法。
            // 尺寸/内存限制改按**最高的那一片**检查；磁盘仍按整图总量检查（所有片加起来
            // 要写的字节数没变）；缺失源图的硬拦不可被分段豁免。
            int tallest = pieces.Max(p => p.Height);
            FormatLimits.Verdict pieceDims = FormatLimits.Check(settings.Format, output.Width, tallest, -1);
            long roughTotal = new IntSize(output.Width, output.Height).ByteLength / 2;
            bool diskOk = freeDisk <= 0 || freeDisk >= roughTotal;

            if (!missingBlocked)
            {
                if (pieceDims.Fits && diskOk)
                    verdict = new FormatLimits.Verdict(true, false,
                        verdict.Fits ? verdict.Reason
                            : $"总高 {output.Height} 超出单文件上限；按每片 ≤{effectiveMax} 显式分段后每片都在限制内，可以导出。",
                        verdict.SuggestedMaxHeight);
                else if (pieceDims.Fits)
                    verdict = new FormatLimits.Verdict(false, true,
                        $"目标磁盘剩余 {freeDisk / 1024 / 1024} MB，全部片合计预计仍需约 {roughTotal / 1024 / 1024} MB。",
                        0);
                else
                    verdict = new FormatLimits.Verdict(false, true,
                        $"即使按每片 {effectiveMax} 分段，单片仍是 {output.Width}×{tallest}：" +
                        $"{pieceDims.Reason} 请把每片最大高度调得更小。",
                        0);
            }
        }
        else
        {
            pieces.Add(new ExportPiece(1, 1, new IntRect(0, 0, output.Width, output.Height),
                RenderName(settings.FileNameTemplate, 1, extension)));
        }

        long estimated = sampler is null || layout.IsEmpty
            ? 0
            : EstimateBytes(sampler, layout, pieces, settings, encoders);

        return new ExportPlan
        {
            OriginalPixelSize = original,
            OutputPixelSize = output,
            DownscaleDivisor = divisor,
            Format = settings.Format,
            Lossless = encoders.IsLossless(settings.Format),
            EstimatedTotalBytes = estimated,
            Verdict = verdict,
            Pieces = pieces,
            NeedsSplit = needsSplit,
        };
    }

    /// <summary>
    /// 拼缝在画布上的行位置（每段的上边界，首段除外）。
    /// 分段时优先在这些位置断开，让"一片结束于两张图的自然边界"，
    /// 而不是把某张图的内容从中间劈开。
    /// </summary>
    /// <param name="layout">已构建的布局，片段上边界即拼缝行。</param>
    /// <param name="outputHeight">输出画布高度（分段边界要落在这个坐标系里）。</param>
    /// <param name="divisor">缩小倍数；1 表示不缩。</param>
    public static int[] SeamRows(CanvasLayout layout, int outputHeight, int divisor)
    {
        divisor = Math.Max(1, divisor);
        var rows = new List<int>();
        foreach (PlacedSegment p in layout.Placed)
        {
            if (p.Index == 0) continue;
            // 整数除法与 RowDownsampler 的分组规则完全一致：输出行 = 画布行 ÷ 倍数
            int y = p.Origin.Y / divisor;
            if (y > 0 && y < outputHeight) rows.Add(y);
        }
        return rows.Distinct().OrderBy(y => y).ToArray();
    }

    /// <summary>应用用户显式选择的缩小倍数。向上取整，保证最后一组不完整的行也被保留下来。</summary>
    public static IntSize ApplyDivisor(IntSize original, int divisor)
    {
        divisor = Math.Max(1, divisor);
        if (divisor == 1) return original;
        int w = (original.Width + divisor - 1) / divisor;
        int h = (original.Height + divisor - 1) / divisor;
        return new IntSize(Math.Max(1, w), Math.Max(1, h));
    }

    /// <summary>
    /// 列出"读不出来就不该假装能导出"的片段源图。
    /// 三层检查由浅入深：布局快照里的缺失标记/零尺寸占位 →（给了 probe 时）真实解析 →
    /// 解析成功但尺寸与项目记录不符（文件被外部替换过）。
    /// </summary>
    private static string[] FindUnusableSources(CanvasLayout layout, ISourceResolver? probe)
    {
        var names = new List<string>();
        foreach (PlacedSegment p in layout.Placed)
        {
            SourceImage s = p.Source;
            string name = string.IsNullOrWhiteSpace(s.FileName) ? $"(片段 {p.Segment.Id})" : s.FileName;
            if (s.SourceMissing || s.Size.IsEmpty)
            {
                names.Add($"{name}：源文件缺失");
                continue;
            }
            if (probe is null) continue;
            if (!probe.TryResolve(s.Id, out RgbaImage? img, out string? err))
                names.Add($"{name}：{(err ?? "读不到源文件")}");
            else if (img is not null && img.Size != s.Size)
                names.Add($"{name}：实际 {img.Size.Width}×{img.Size.Height} 与项目记录 " +
                          $"{s.Size.Width}×{s.Size.Height} 不符");
        }
        return names.Distinct().ToArray();
    }

    /// <summary>
    /// 算出每片的 [起, 止) 行区间。
    /// 规则：从 0 开始，每次推进到"不超过 max 且最接近 max 的拼缝行"；
    /// 附近没有拼缝就硬切。相邻片按 <paramref name="overlap"/> 重叠，
    /// 但重叠不会让下一片的起点退回到上一片的起点（那会产生零长或负长片）。
    /// </summary>
    public static List<(int Start, int End)> ComputeRanges(int totalHeight, int maxPieceHeight,
        int overlap, int[] seamRows)
    {
        var result = new List<(int, int)>();
        if (maxPieceHeight <= 0 || totalHeight <= maxPieceHeight)
        {
            result.Add((0, totalHeight));
            return result;
        }

        int start = 0;
        int previousEnd = 0;
        int safeOverlap = Math.Clamp(overlap, 0, maxPieceHeight - 1);
        int tolerance = Math.Max(1, maxPieceHeight / 4);

        while (start < totalHeight)
        {
            int target = (int)Math.Min((long)start + maxPieceHeight, totalHeight);
            if (target < totalHeight)
            {
                int floor = Math.Max(previousEnd + 1,
                    Math.Max(start + maxPieceHeight / 2, target - tolerance));
                int? snapped = seamRows
                    .Where(s => s >= floor && s <= target)
                    .OrderByDescending(s => s)
                    .Cast<int?>()
                    .FirstOrDefault();
                if (snapped is not null) target = snapped.Value;
            }
            result.Add((start, target));
            if (target == totalHeight) break;
            previousEnd = target;
            start = Math.Max(start + 1, target - safeOverlap);
        }
        return result;
    }

    public static string ExtensionOf(ExportFormat format) => format switch
    {
        ExportFormat.Png => "png",
        ExportFormat.Jpeg => "jpg",
        ExportFormat.WebP => "webp",
        ExportFormat.Tiff => "tif",
        _ => "bin",
    };

    private static string RenderName(string template, int index, string extension)
    {
        string name = string.IsNullOrWhiteSpace(template) ? "stitched_{index}.{ext}" : template;
        return name.Replace("{index}", index.ToString(System.Globalization.CultureInfo.InvariantCulture))
                   .Replace("{ext}", extension);
    }

    private static long GetFreeDiskBytes(string? outputDir)
    {
        if (string.IsNullOrWhiteSpace(outputDir)) return -1;
        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(outputDir));
            if (string.IsNullOrEmpty(root)) return -1;
            var drive = new DriveInfo(root);
            return drive.IsReady ? drive.AvailableFreeSpace : -1;
        }
        catch
        {
            // 目录还不存在/是网络路径/权限不足：拿不到剩余空间就跳过这项检查，
            // 但不能因此以为"空间足够"——返回 -1 表示未知。
            return -1;
        }
    }

    /// <summary>
    /// 采样估算体积：把最多 3 条带用<b>真实编码器</b>编到计数流里，按行数外推。
    /// 用真实编码器而不是给个经验系数，是因为长截图的内容差异极大（纯色聊天背景 vs 照片），
    /// 任何固定系数的预估都能差出好几倍，报出来反而误导。
    /// </summary>
    private static long EstimateBytes(CanvasComposer sampler, CanvasLayout layout,
        IReadOnlyList<ExportPiece> pieces, ExportSettings settings, EncoderRegistry encoders)
    {
        try
        {
            IImageEncoder encoder = encoders.Require(settings.Format);
            var options = new EncodeOptions
            {
                Background = settings.Background,
                JpegQuality = settings.JpegQuality,
            };

            const int sampleRows = 128;
            long sampledPixels = 0, sampledBytes = 0;

            // 头、中、尾各取一条带：长截图常常只有其中一段含图片区，只采顶部会严重低估
            int[] anchors = { 0, layout.CanvasSize.Height / 2, Math.Max(0, layout.CanvasSize.Height - sampleRows) };
            foreach (int y0 in anchors.Distinct())
            {
                int h = Math.Min(sampleRows, layout.CanvasSize.Height - y0);
                if (h <= 0) continue;
                RgbaImage band = sampler.RenderBand(layout, new IntRect(0, y0, layout.CanvasSize.Width, h));
                using var counting = new CountingStream();
                encoder.Encode(band, band.Bounds, counting, options);
                sampledBytes += counting.Written;
                sampledPixels += (long)band.Width * band.Height;
            }

            long totalPixels = pieces.Sum(p => (long)p.Width * p.Height);
            if (sampledPixels == 0) return 0;
            return (long)((double)sampledBytes / sampledPixels * totalPixels);
        }
        catch
        {
            // 估算失败不许让导出计划整体失败，也不许编一个数糊弄：报 0，界面显示"未知"
            return 0;
        }
    }

    /// <summary>只累加写入字节数、不保存内容的流。用于体积采样。</summary>
    private sealed class CountingStream : Stream
    {
        public long Written { get; private set; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => Written;
        public override long Position { get => Written; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] b, int o, int c) => throw new NotSupportedException();
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => Written += c;
        public override void Write(ReadOnlySpan<byte> b) => Written += b.Length;
    }
}
