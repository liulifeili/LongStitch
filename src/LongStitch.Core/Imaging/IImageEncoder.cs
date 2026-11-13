using LongStitch.Core.Geometry;
using LongStitch.Core.Imaging.Png;
using LongStitch.Core.Model;

namespace LongStitch.Core.Imaging;

/// <summary>编码参数。不同格式各取所需，未使用的字段被忽略。</summary>
public sealed record EncodeOptions
{
    /// <summary>透明像素合成到的底色（导出 JPEG 等不支持透明的格式时必需）。</summary>
    public RgbaColor Background { get; init; } = RgbaColor.White;

    /// <summary>
    /// 第五十六改②：真写之前先把透明像素合成到 <see cref="Background"/> 上。
    /// 只有 PNG 编码器读这面旗（它的历史行为是透明原样进文件）；JPEG 编码器无条件压底色，
    /// 不经过这里。逐像素独立运算，分块与整块两条路的结果逐字节一致——精度红线不受影响。
    /// </summary>
    public bool FlattenBackground { get; init; }

    /// <summary>JPEG 质量 1–100。</summary>
    public int JpegQuality { get; init; } = 92;

    /// <summary>PNG 专属选项。</summary>
    public PngEncodeOptions Png { get; init; } = new();
}

/// <summary>
/// 图像编码器抽象。
///
/// 为什么要有这一层：Core 刻意不引用 System.Drawing（保持 net8.0、可在任何环境跑测试），
/// 所以 PNG 由我们自己实现并放在 Core，JPEG/WebP 这类需要平台编码器的格式由 App 层注入。
/// 关键点是 <see cref="IsLossless"/> 与 <see cref="UnavailableReason"/> 必须诚实：
/// 界面据此决定能不能写"字节级无损"，以及某格式不可用时给出原因而不是静默换格式。
/// </summary>
public interface IImageEncoder
{
    ExportFormat Format { get; }

    /// <summary>当前环境是否真能编码该格式。</summary>
    bool IsAvailable { get; }

    /// <summary>不可用时的可读原因，界面直接显示。</summary>
    string? UnavailableReason { get; }

    /// <summary>是否无损。只有 true 才允许对用户承诺"像素与预览逐字节一致"。</summary>
    bool IsLossless { get; }

    /// <summary>把图像的某个矩形区域编码写入流（一次性，需要该区域整块驻留内存）。</summary>
    void Encode(RgbaImage image, IntRect region, Stream output, EncodeOptions options);

    /// <summary>
    /// 开一个<b>流式</b>区域写入器：调用方一行带一行地喂，编码器边收边写。
    /// 长图导出必须走这条路，否则"导出 52000 像素高"就等于"先申请 1GB 内存"。
    /// 不支持流式的实现（例如依赖平台一次性编码的 JPEG）可以在内部攒完整块，
    /// 但必须在 <see cref="IRegionWriter"/> 的文档里写清楚自己的内存代价。
    /// </summary>
    IRegionWriter CreateWriter(IntSize region, Stream output, EncodeOptions options);
}

/// <summary>
/// 分块写入器。不变量：所有带的宽度必须等于 <see cref="Width"/>，
/// 累计行数必须等于目标区域高度——收尾时对不上就抛错，绝不产出被截断的文件。
/// </summary>
public interface IRegionWriter : IDisposable
{
    /// <summary>每条带都必须正好是这个宽度。</summary>
    int Width { get; }

    /// <summary>已接受的行数。</summary>
    int RowsWritten { get; }

    /// <summary>写入一条带（宽度必须等于 <see cref="Width"/>）。</summary>
    void WriteBand(RgbaImage band);

    /// <summary>收尾。行数不足时抛错。</summary>
    void Finish();
}

/// <summary>PNG 编码器：走自研 <see cref="PngEncoder"/>，承诺无损。</summary>
public sealed class PngImageEncoder : IImageEncoder
{
    public ExportFormat Format => ExportFormat.Png;
    public bool IsAvailable => true;
    public string? UnavailableReason => null;
    public bool IsLossless => true;

    public void Encode(RgbaImage image, IntRect region, Stream output, EncodeOptions options)
    {
        // 第五十六改②：「透明」勾勾掉时先压底色再编码（一次性路径，整图驻留内存，无长图）
        if (options.FlattenBackground)
            image = image.FlattenOnto(options.Background);
        PngEncoder.EncodeRegion(image, region, output, options.Png);
    }

    public IRegionWriter CreateWriter(IntSize region, Stream output, EncodeOptions options) =>
        new PngRegionWriter(output, region.Width, region.Height, options);

    /// <summary>
    /// PNG 的流式区域写入器：直接套在 <see cref="PngStreamWriter"/> 外面，
    /// 只做"宽度/行数"这两条不变量的把关。峰值内存 = 一条带。
    ///
    /// 已知取舍：流式写法必须在一开始就决定 colorType，而此时还没看过全部像素，
    /// 所以一律按 RGBA 写；单张小图走 <see cref="Encode"/> 时才享受"全不透明降 RGB"的省体积优化。
    /// 两者都是无损的，只差文件大小。
    /// </summary>
    private sealed class PngRegionWriter : IRegionWriter
    {
        private readonly PngStreamWriter _inner;
        private readonly int _expectedRows;
        private readonly RgbaColor? _flattenOnto;
        private bool _finished;

        public PngRegionWriter(Stream output, int width, int height, EncodeOptions options)
        {
            Width = width;
            _expectedRows = height;
            // 第五十六改②：分块路径同样要在写行之前压底色。压底是逐像素独立运算，
            // 所以"每条带各自压"与"整图先压再分块"结果逐字节一致——精度红线不受影响。
            _flattenOnto = options.FlattenBackground ? options.Background : null;
            _inner = new PngStreamWriter(output, width, height, withAlpha: true, options.Png);
        }

        public int Width { get; }
        public int RowsWritten => _inner.RowsWritten;

        public void WriteBand(RgbaImage band)
        {
            if (_finished) throw new ObjectDisposedException(nameof(PngRegionWriter));
            if (band.Width != Width)
                throw new ArgumentException(
                    $"带宽必须恒为 {Width}，收到 {band.Width}。分块宽度不一致会让导出图错位。",
                    nameof(band));
            // FlattenOnto 返回全不透明的新图，withAlpha:true 写出的 alpha 恒为 255，无损于正确性
            if (_flattenOnto is not null)
                band = band.FlattenOnto(_flattenOnto.Value);
            for (int y = 0; y < band.Height; y++)
                _inner.WriteRgbaRow(band.Row(y));
        }

        public void Finish()
        {
            if (_finished) return;
            _finished = true;
            _inner.Finish();   // 严格：行数不足会抛，调用方据此判定导出失败
        }

        /// <summary>安静释放：异常展开途中调用时不再追加"文件不完整"，以免盖掉首因。</summary>
        public void Dispose() => _inner.Dispose();
    }
}

/// <summary>
/// 编码器注册表。App 启动时把 GDI+ 的 JPEG 编码器注册进来；
/// 没注册的格式在导出面板里显示为"不可用 + 原因"，而不是被悄悄替换成 PNG。
/// </summary>
public sealed class EncoderRegistry
{
    private readonly Dictionary<ExportFormat, IImageEncoder> _encoders = new();

    public EncoderRegistry()
    {
        // PNG 与 TIFF 都是纯托管自研实现，所以默认就在；
        // JPEG/WebP 依赖平台编码器，由 App 层注册进来，没注册就如实显示"不可用 + 原因"。
        Register(new PngImageEncoder());
        Register(new LongStitch.Core.Imaging.Tiff.TiffImageEncoder());
    }

    public void Register(IImageEncoder encoder) => _encoders[encoder.Format] = encoder;

    public IEnumerable<ExportFormat> SupportedFormats => _encoders.Keys.OrderBy(f => f);

    public IImageEncoder? Find(ExportFormat format) =>
        _encoders.TryGetValue(format, out IImageEncoder? e) ? e : null;

    /// <summary>取编码器；不可用时抛带原因的异常（导出前的预检应先用 <see cref="Probe"/>）。</summary>
    public IImageEncoder Require(ExportFormat format)
    {
        IImageEncoder? encoder = Find(format);
        if (encoder is null)
            throw new NotSupportedException($"没有注册 {format} 编码器");
        if (!encoder.IsAvailable)
            throw new NotSupportedException($"{format} 当前不可用：{encoder.UnavailableReason}");
        return encoder;
    }

    /// <summary>不抛异常的可用性探测，返回 null 表示可用，否则返回不可用的原因。</summary>
    public string? Probe(ExportFormat format)
    {
        IImageEncoder? encoder = Find(format);
        if (encoder is null) return $"本程序未内置 {format} 编码器";
        return encoder.IsAvailable ? null : encoder.UnavailableReason;
    }

    /// <summary>该格式能否承诺字节级无损。未知格式一律按"不能"处理。</summary>
    public bool IsLossless(ExportFormat format) => Find(format)?.IsLossless ?? false;
}
