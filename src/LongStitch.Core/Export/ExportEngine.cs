using LongStitch.Core.Geometry;
using LongStitch.Core.Imaging;
using LongStitch.Core.Layout;
using LongStitch.Core.Model;

namespace LongStitch.Core.Export;

/// <summary>导出进度。</summary>
/// <param name="Percent">0–100 的整体进度。</param>
/// <param name="Stage">当前阶段的可读文字，界面直接显示。</param>
/// <param name="PieceDone">已完成的文件数。</param>
/// <param name="PieceTotal">计划文件总数。</param>
public sealed record ExportProgress(int Percent, string Stage, int PieceDone, int PieceTotal);

/// <summary>导出结果。失败/取消时 <see cref="Files"/> 里是"已经完整写出的文件"，
/// 半成品已被删除——需求 §7 明确要求取消或失败时清理不完整的临时输出。</summary>
public sealed record ExportOutcome(bool Success, bool Cancelled, IReadOnlyList<string> Files, string? Error)
{
    public static ExportOutcome Canceled(IReadOnlyList<string> done) =>
        new(false, true, done, "导出已取消，未完成的临时文件已删除");

    public static ExportOutcome Failed(string error, IReadOnlyList<string> done) =>
        new(false, false, done, error);

    public static ExportOutcome Ok(IReadOnlyList<string> done) => new(true, false, done, null);
}

/// <summary>
/// 分块导出引擎。
///
/// ═══ 它怎么兑现"长图优先保证完成、不静默压缩" ═══
/// · 每次只合成一条 <see cref="BandHeight"/> 行的带，编码后立即丢弃：
///   峰值内存与总高度无关，52000 像素高也不会 OOM；
/// · 先写 <c>xxx.part</c>，整片成功后才改名为正式文件名。
///   取消、异常、磁盘满都会留下"要么完整、要么不存在"的结果；
/// · 硬性限制在动手前就被 <see cref="ExportPlan.Verdict"/> 拦住，
///   引擎不会"试试看，不行就缩一点"。
/// </summary>
public sealed class ExportEngine
{
    /// <summary>每次合成/编码的行数。太小会让 PNG 的 zlib 频繁小写，太大白占内存；256 行是实测舒适区。</summary>
    public const int BandHeight = 256;

    private static long _runCount;

    /// <summary>
    /// 导出被真正执行过几次（测试钩子，走 <see cref="Run"/> 的唯一入口计数）。
    /// 需求十最后那条「UI 线程不执行完整导出」要能量出来——拖动判据读"这一趟拖动期间它没涨"，
    /// 而不是靠"看起来没调用"。读法用 <see cref="Interlocked.Read{T}(ref long)"/>：
    /// 导出本身在后台线程跑，普通读法在 32 位运行时不保证原子。
    /// </summary>
    public static long RunCountForTest => Interlocked.Read(ref _runCount);

    private readonly CanvasComposer _composer;
    private readonly EncoderRegistry _encoders;

    public ExportEngine(CanvasComposer composer, EncoderRegistry encoders)
    {
        _composer = composer ?? throw new ArgumentNullException(nameof(composer));
        _encoders = encoders ?? throw new ArgumentNullException(nameof(encoders));
    }

    /// <summary>
    /// 执行导出计划。同步方法：调用方（界面）负责放到后台线程上跑。
    /// </summary>
    /// <param name="layout">要导出的布局。必须与 <paramref name="plan"/> 同源，
    /// 否则尺寸会对不上——这里做了显式一致性检查而不是默默出错图。</param>
    /// <param name="plan">由 <see cref="ExportPlanner"/> 生成的计划。</param>
    /// <param name="settings">导出设置（格式、底色、质量、缩放倍数）。</param>
    /// <param name="outputDirectory">输出目录，必须已存在。</param>
    /// <param name="progress">进度回调，可为 null。</param>
    /// <param name="ct">取消令牌。取消发生在带与带之间，最多多写一行带的量。</param>
    public ExportOutcome Run(
        CanvasLayout layout,
        ExportPlan plan,
        ExportSettings settings,
        string outputDirectory,
        IProgress<ExportProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(settings);
        Interlocked.Increment(ref _runCount);

        var written = new List<string>();

        if (layout.CanvasSize != plan.OriginalPixelSize)
            return ExportOutcome.Failed(
                $"布局尺寸 {layout.CanvasSize} 与导出计划 {plan.OriginalPixelSize} 不一致，" +
                "已中止。这通常意味着计划生成后参数又变了，请重新生成计划。", written);

        if (plan.IsHardBlock)
            return ExportOutcome.Failed($"无法导出：{plan.BlockingReason}", written);

        if (string.IsNullOrWhiteSpace(outputDirectory) || !Directory.Exists(outputDirectory))
            return ExportOutcome.Failed("输出目录不存在，请重新选择位置", written);

        IImageEncoder encoder;
        try
        {
            encoder = _encoders.Require(plan.Format);
        }
        catch (Exception ex)
        {
            return ExportOutcome.Failed(ex.Message, written);
        }

        int divisor = Math.Max(1, plan.DownscaleDivisor);
        long totalRows = plan.Pieces.Sum(p => SourceRowsOf(p, divisor, layout.CanvasSize.Height));
        long doneRows = 0;
        var encodeOptions = new EncodeOptions
        {
            Background = settings.Background,
            JpegQuality = settings.JpegQuality,
            // 第五十六改②：只有 PNG 走这面旗——JPEG 编码器本来就无条件压底色，
            // TIFF 维持历史行为（不压底）。「透明」勾勾掉时透明像素合成到所选底色。
            FlattenBackground = settings.Format == ExportFormat.Png && !settings.PngTransparent,
        };

        for (int i = 0; i < plan.Pieces.Count; i++)
        {
            ExportPiece piece = plan.Pieces[i];
            string finalPath = Path.Combine(outputDirectory, piece.FileName);
            string partPath = finalPath + ".part";

            progress?.Report(new ExportProgress(
                Percent(doneRows, totalRows),
                plan.Pieces.Count > 1 ? $"正在写第 {piece.Index}/{piece.Total} 片" : "正在写出",
                i, plan.Pieces.Count));

            try
            {
                WriteOnePiece(layout, piece, encoder, encodeOptions, divisor, partPath, progress,
                    ref doneRows, totalRows, ct);
            }
            catch (OperationCanceledException)
            {
                TryDelete(partPath);
                return ExportOutcome.Canceled(written);
            }
            catch (Exception ex)
            {
                TryDelete(partPath);
                return ExportOutcome.Failed(
                    $"写第 {piece.Index} 片时失败：{ex.Message}\n" +
                    "已删除未完成的临时文件，之前完整写出的片子保留。", written);
            }

            // 完整写完才改名：任何中途失败都不会留下一个"能打开但内容截断"的正式文件
            File.Move(partPath, finalPath, overwrite: true);
            written.Add(finalPath);
        }

        progress?.Report(new ExportProgress(100, "完成", plan.Pieces.Count, plan.Pieces.Count));
        return ExportOutcome.Ok(written);
    }

    private void WriteOnePiece(
        CanvasLayout layout,
        ExportPiece piece,
        IImageEncoder encoder,
        EncodeOptions options,
        int divisor,
        string partPath,
        IProgress<ExportProgress>? progress,
        ref long doneRows,
        long totalRows,
        CancellationToken ct)
    {
        using var file = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None,
            // 64KB：长图写入是纯顺序流，大缓冲能明显减少系统调用次数
            bufferSize: 64 * 1024, FileOptions.SequentialScan);

        IRegionWriter sink = encoder.CreateWriter(
            new IntSize(piece.Width, piece.Height), file, options);

        // 需要降尺寸时在真实编码器外面套一层整数盒式降采样；不降时直接用，零额外开销
        IRegionWriter input = divisor == 1
            ? sink
            : new RowDownsampler(layout.CanvasSize.Width, divisor, sink);

        try
        {
            // ═══ 坐标系换算（缩小导出曾经在这里出过严重 bug）═══
            // plan.Pieces 里的 CanvasRegion/Height 全是**缩小后的输出坐标**；
            // RenderBand 要的是**源画布坐标**。divisor>1 时必须把输出行号乘回倍数，
            // 否则只会读到源图顶部 piece.Height 行（内容截半），降采样器交出的行数
            // 也不够编码器收尾。乘回后每片源起点 = 输出起点 × divisor，
            // 恰好落在降采样分组边界上，所以每片重新开一个 RowDownsampler 相位不会错。
            int srcWidth = layout.CanvasSize.Width;
            int srcY0 = piece.CanvasRegion.Y * divisor;
            int srcRows = SourceRowsOf(piece, divisor, layout.CanvasSize.Height);

            for (int sy = 0; sy < srcRows; sy += BandHeight)
            {
                ct.ThrowIfCancellationRequested();
                int h = Math.Min(BandHeight, srcRows - sy);
                RgbaImage band = _composer.RenderBand(
                    layout, new IntRect(0, srcY0 + sy, srcWidth, h));
                input.WriteBand(band);

                doneRows += h;
                progress?.Report(new ExportProgress(
                    Percent(doneRows, totalRows), "正在合成像素",
                    piece.Index - 1, piece.Total));
            }

            // 严格收尾放在 try 内：行数对不上就在这里抛，由外层删掉临时文件。
            // 先收降采样器（它要把最后一组还没吐的行交给下游），再收下游。
            input.Finish();
            sink.Finish();
        }
        finally
        {
            // Dispose 一律安静。取消或出错时它正处在异常展开途中，
            // 再抛一个"文件不完整"就会把真正的首因盖掉。
            if (!ReferenceEquals(input, sink)) SafeDispose(input);
            SafeDispose(sink);
        }
    }

    /// <summary>
    /// 一片需要读多少**源画布行**。输出高度是 ceil(源高/divisor)，
    /// 最后一片乘回去可能越过画布底部，必须夹住；不足一整组的尾巴
    /// 由 <see cref="RowDownsampler.Finish"/> 按实际行数平均输出。
    /// </summary>
    private static int SourceRowsOf(ExportPiece piece, int divisor, int canvasHeight) =>
        Math.Max(0, Math.Min(piece.Height * divisor, canvasHeight - piece.CanvasRegion.Y * divisor));

    private static void SafeDispose(IRegionWriter writer)
    {
        try { writer.Dispose(); } catch { /* 展开期忽略收尾异常，首因另有其人 */ }
    }

    private static int Percent(long done, long total) =>
        total <= 0 ? 100 : (int)Math.Clamp(done * 100 / total, 0, 100);

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* 删不掉也不该盖掉真正的失败原因 */ }
    }
}
