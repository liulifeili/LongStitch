using System.Runtime.InteropServices;
using LongStitch.Core.Geometry;
using LongStitch.Core.Imaging;
using LongStitch.Core.Model;
using GdiBitmap = System.Drawing.Bitmap;
using GdiEncoder = System.Drawing.Imaging.Encoder;
using GdiImagelockMode = System.Drawing.Imaging.ImageLockMode;
using GdiPixelFormat = System.Drawing.Imaging.PixelFormat;

namespace LongStitch.App.Imaging;

/// <summary>
/// JPEG 编码器（走系统 GDI+）。
///
/// 诚实声明：<b>JPEG 是有损格式</b>，所以这个编码器不提供任何字节级承诺，
/// <see cref="IsLossless"/> 固定返回 false，界面据此不许写"无损"两个字。
/// 需求 §7 的"禁止静默压缩"在这里的落点是：质量参数、背景色都要用户看得见、改得动。
///
/// 内存代价也如实标出来：GDI+ 没有流式 JPEG 编码接口，所以
/// <see cref="CreateWriter"/> 必须先把整片攒在内存里再一次性编码。
/// 这正是"超长图请用 PNG 或分段导出"的真实原因，不是软件偷懒。
/// </summary>
public sealed class GdiplusJpegEncoder : IImageEncoder
{
    public ExportFormat Format => ExportFormat.Jpeg;

    public bool IsAvailable => Codec is not null;

    public string? UnavailableReason => Codec is null
        ? "本机没有可用的 JPEG 编码器（GDI+ image/jpeg）"
        : null;

    public bool IsLossless => false;

    private static readonly System.Drawing.Imaging.ImageCodecInfo? Codec =
        System.Drawing.Imaging.ImageCodecInfo.GetImageEncoders()
            .FirstOrDefault(c => string.Equals(c.MimeType, "image/jpeg", StringComparison.OrdinalIgnoreCase));

    public void Encode(RgbaImage image, IntRect region, Stream output, EncodeOptions options)
    {
        IntRect r = region.Intersect(image.Bounds);
        if (r.IsEmpty) throw new ArgumentException("编码区域与图像无交集", nameof(region));

        // 透明像素必须先合成到用户选的背景色上：JPEG 不支持 alpha，
        // 让编码器自己决定底色会得到不可预期的结果（常见是黑底）。
        RgbaImage crop = image.SubImage(r);
        RgbaImage opaque = crop.FlattenOnto(options.Background);
        SaveJpeg(opaque, output, options.JpegQuality);
    }

    public IRegionWriter CreateWriter(IntSize region, Stream output, EncodeOptions options) =>
        new BufferedJpegWriter(region, output, options, this);

    private void SaveJpeg(RgbaImage image, Stream output, int quality)
    {
        if (Codec is null)
            throw new NotSupportedException("本机没有可用的 JPEG 编码器，请改用 PNG");

        using GdiBitmap bmp = ToBitmap(image);
        using var parameters = new System.Drawing.Imaging.EncoderParameters(1);
        parameters.Param[0] = new System.Drawing.Imaging.EncoderParameter(
            GdiEncoder.Quality, (long)Math.Clamp(quality, 1, 100));
        bmp.Save(output, Codec, parameters);
    }

    /// <summary>把 RgbaImage 拷进 GDI+ 位图（内存序 BGRA）。</summary>
    private static GdiBitmap ToBitmap(RgbaImage image)
    {
        var bmp = new GdiBitmap(image.Width, image.Height, GdiPixelFormat.Format32bppArgb);
        var rect = new System.Drawing.Rectangle(0, 0, image.Width, image.Height);
        var data = bmp.LockBits(rect, GdiImagelockMode.WriteOnly, GdiPixelFormat.Format32bppArgb);
        try
        {
            int stride = data.Stride;
            byte[] buffer = new byte[stride * image.Height];
            for (int y = 0; y < image.Height; y++)
            {
                ReadOnlySpan<byte> src = image.Row(y);
                int rowStart = y * stride;
                for (int x = 0; x < image.Width; x++)
                {
                    int s = x * 4, d = rowStart + x * 4;
                    buffer[d] = src[s + 2];
                    buffer[d + 1] = src[s + 1];
                    buffer[d + 2] = src[s];
                    buffer[d + 3] = src[s + 3];
                }
            }
            Marshal.Copy(buffer, 0, data.Scan0, buffer.Length);
        }
        finally
        {
            bmp.UnlockBits(data);
        }
        return bmp;
    }

    /// <summary>
    /// 攒完整片再一次性编码的写入器。
    /// 接口与 PNG 的流式写入器一致，所以导出引擎不需要为格式分叉；
    /// 但内存代价是"整片 RGBA"，由 <see cref="Export.FormatLimits"/> 在导出前就把关拦住。
    /// </summary>
    private sealed class BufferedJpegWriter : IRegionWriter
    {
        private readonly RgbaImage _buffer;
        private readonly Stream _output;
        private readonly EncodeOptions _options;
        private readonly GdiplusJpegEncoder _owner;
        private readonly int _expectedRows;
        private int _rows;
        private bool _finished;

        public BufferedJpegWriter(IntSize region, Stream output, EncodeOptions options, GdiplusJpegEncoder owner)
        {
            Width = region.Width;
            _expectedRows = region.Height;
            _buffer = new RgbaImage(region.Width, region.Height);
            _output = output;
            _options = options;
            _owner = owner;
        }

        public int Width { get; }
        public int RowsWritten => _rows;

        public void WriteBand(RgbaImage band)
        {
            if (_finished) throw new ObjectDisposedException(nameof(BufferedJpegWriter));
            if (band.Width != Width)
                throw new ArgumentException($"带宽 {band.Width} ≠ 期望 {Width}");
            if (_rows + band.Height > _expectedRows)
                throw new InvalidOperationException("写入行数超出声明高度");
            _buffer.Blit(band, band.Bounds, 0, _rows);
            _rows += band.Height;
        }

        public void Finish()
        {
            if (_finished) return;
            _finished = true;
            if (_rows != _expectedRows)
                throw new InvalidOperationException($"JPEG 只攒到 {_rows} 行，声明 {_expectedRows} 行");
            _owner.Encode(_buffer, _buffer.Bounds, _output, _options);
        }

        /// <summary>安静释放：展开异常途中不能再抛（与 PNG 写入器同一套理由）。</summary>
        public void Dispose()
        {
            if (_finished) return;
            _finished = true;   // 没攒够就不编码，避免产出半张 JPEG
        }
    }
}
