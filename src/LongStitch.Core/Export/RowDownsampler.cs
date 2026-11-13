using LongStitch.Core.Geometry;
using LongStitch.Core.Imaging;

namespace LongStitch.Core.Export;

/// <summary>
/// 流式整数倍降采样器：把"宽 W、任意高"的源行带，变成"宽 ceil(W/divisor)"的输出行带。
///
/// 只做整数盒式平均（box filter），没有任何浮点权重、没有双三次/兰开索一类插值。
/// 这是"用户主动降尺寸"唯一的实现方式，理由有两个：
///   1. 整数倍才能跨带续算——纵向累加器记着"这一组已经攒了几行"，带与带之间不丢行也不重复；
///   2. 盒式平均的每个输出像素都是"确定的整数求和再求平均"，可以在测试里用另一套实现复算核对。
///
/// 不足一整组的尾巴（最后一组行数 &lt; divisor）按实际行数平均，所以内容不会被丢掉，
/// 这也解释了为什么输出尺寸用 <c>ceil</c> 而不是 <c>floor</c>。
/// </summary>
public sealed class RowDownsampler : IRegionWriter
{
    private readonly int _divisor;
    private readonly int _srcWidth;
    private readonly IRegionWriter _target;

    /// <summary>累加器：每个输出像素 4 个通道的整数和。</summary>
    private readonly long[] _acc;

    /// <summary>当前组已累加的行数。</summary>
    private int _rowsInGroup;

    /// <summary>当前组里各列实际参与平均的列数（只有最后一组会小于 divisor）。</summary>
    private readonly int[] _columnCount;

    /// <summary>待冲刷的输出行缓冲（攒够一批再交给下游，避免每行都分配）。</summary>
    private RgbaImage _pending;
    private int _pendingRows;

    private bool _finished;

    public RowDownsampler(int sourceWidth, int divisor, IRegionWriter target)
    {
        if (sourceWidth <= 0) throw new ArgumentOutOfRangeException(nameof(sourceWidth));
        _divisor = Math.Max(1, divisor);
        _srcWidth = sourceWidth;
        _target = target;
        OutputWidth = (sourceWidth + _divisor - 1) / _divisor;
        _acc = new long[OutputWidth * 4];
        _columnCount = new int[OutputWidth];
        _pending = new RgbaImage(OutputWidth, BatchRows);
        Width = OutputWidth;
    }

    /// <summary>每次攒多少输出行再往下游交一次。256 行 × 宽 600 ≈ 0.6MB，够小也够省。</summary>
    private const int BatchRows = 256;

    public int Width { get; }
    public int OutputWidth { get; }

    /// <summary>已产出的输出行数。</summary>
    public int RowsWritten => _target.RowsWritten;

    /// <summary>喂入一条源分辨率的带（宽度必须等于构造时的 sourceWidth）。</summary>
    public void WriteBand(RgbaImage band)
    {
        if (_finished) throw new ObjectDisposedException(nameof(RowDownsampler));
        if (band.Width != _srcWidth)
            throw new ArgumentException($"带宽应为 {_srcWidth}，收到 {band.Width}", nameof(band));

        for (int y = 0; y < band.Height; y++)
        {
            AccumulateRow(band.Row(y));
            _rowsInGroup++;
            if (_rowsInGroup == _divisor) FlushGroup();
        }
    }

    /// <summary>把不足一整组的尾巴也输出（内容不丢）。之后不许再喂。</summary>
    public void Finish()
    {
        if (_finished) return;
        _finished = true;
        if (_rowsInGroup > 0) FlushGroup();
        FlushPending();
        _target.Finish();
    }

    public void Dispose() => Finish();

    // ───────────────────────── 内部 ─────────────────────────

    private void AccumulateRow(ReadOnlySpan<byte> srcRow)
    {
        Array.Clear(_columnCount);
        for (int ox = 0; ox < OutputWidth; ox++)
        {
            int x0 = ox * _divisor;
            int x1 = Math.Min(_srcWidth, x0 + _divisor);
            int n = x1 - x0;
            _columnCount[ox] = n;

            long r = 0, g = 0, b = 0, a = 0;
            for (int x = x0; x < x1; x++)
            {
                int i = x * 4;
                r += srcRow[i]; g += srcRow[i + 1]; b += srcRow[i + 2]; a += srcRow[i + 3];
            }
            int o = ox * 4;
            _acc[o] += r; _acc[o + 1] += g; _acc[o + 2] += b; _acc[o + 3] += a;
        }
    }

    private void FlushGroup()
    {
        if (_pendingRows == BatchRows) FlushPending();

        Span<byte> dst = _pending.Row(_pendingRows);
        long rowFactor = _rowsInGroup;
        for (int ox = 0; ox < OutputWidth; ox++)
        {
            int n = (int)(rowFactor * _columnCount[ox]);
            int o = ox * 4;
            if (n <= 0) { dst[o] = dst[o + 1] = dst[o + 2] = dst[o + 3] = 0; continue; }
            // 先乘后除、加半个除数完成四舍五入；全程整数
            dst[o] = (byte)((_acc[o] + n / 2) / n);
            dst[o + 1] = (byte)((_acc[o + 1] + n / 2) / n);
            dst[o + 2] = (byte)((_acc[o + 2] + n / 2) / n);
            dst[o + 3] = (byte)((_acc[o + 3] + n / 2) / n);
            _acc[o] = _acc[o + 1] = _acc[o + 2] = _acc[o + 3] = 0;
        }
        _pendingRows++;
        _rowsInGroup = 0;
    }

    private void FlushPending()
    {
        if (_pendingRows == 0) return;
        // 只把真正写满的那部分交给下游：最后一批可能不满 BatchRows 行
        _target.WriteBand(_pending.SubImage(new IntRect(0, 0, OutputWidth, _pendingRows)));
        _pendingRows = 0;
    }
}
