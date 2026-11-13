namespace LongStitch.Core.Geometry;

/// <summary>
/// 整数矩形。项目里所有"取哪一块像素""放到哪里"都用它表达。
///
/// 约定：
///   · 左上角 (X, Y) 含，右下角 (Right, Bottom) 不含，即半开区间 [X, Right) × [Y, Bottom)。
///     这样 Width/Height 与"拷贝多少像素"永远一致，不会出现 off-by-one。
///   · 所有相交/裁剪运算都返回规范化结果（宽高不为负）。
///   · 空矩形（Width 或 Height &lt;= 0）在 Union 中不参与，避免"空框污染并集"。
/// </summary>
public readonly record struct IntRect(int X, int Y, int Width, int Height)
{
    public static readonly IntRect Empty = new(0, 0, 0, 0);

    public int Right => X + Width;
    public int Bottom => Y + Height;
    public IntPoint Location => new(X, Y);
    public IntSize Size => new(Width, Height);
    public bool IsEmpty => Width <= 0 || Height <= 0;
    public long PixelCount => IsEmpty ? 0 : (long)Width * Height;

    /// <summary>用左上角 + 尺寸构造。</summary>
    public static IntRect FromSize(IntPoint location, IntSize size) =>
        new(location.X, location.Y, size.Width, size.Height);

    /// <summary>由半开区间的左右上下构造（Right/Bottom 不含）。</summary>
    public static IntRect FromLTRB(int left, int top, int right, int bottom)
    {
        int w = right - left;
        int h = bottom - top;
        return w < 0 || h < 0 ? Empty : new IntRect(left, top, w, h);
    }

    /// <summary>把可能为负的宽高翻正（负宽高表示"从右下往左上画"，产品里不该出现，这里兜底规范化）。</summary>
    public IntRect Normalized()
    {
        int x = X, y = Y, w = Width, h = Height;
        if (w < 0) { x += w; w = -w; }
        if (h < 0) { y += h; h = -h; }
        return new IntRect(x, y, w, h);
    }

    public bool Contains(IntPoint p) =>
        p.X >= X && p.X < Right && p.Y >= Y && p.Y < Bottom;

    public bool Contains(IntRect other) =>
        other.X >= X && other.Y >= Y && other.Right <= Right && other.Bottom <= Bottom;

    public bool Intersects(IntRect other) =>
        !IsEmpty && !other.IsEmpty &&
        other.X < Right && other.Right > X &&
        other.Y < Bottom && other.Bottom > Y;

    /// <summary>交集；不相交返回 <see cref="Empty"/>。</summary>
    public IntRect Intersect(IntRect other)
    {
        if (!Intersects(other)) return Empty;
        int left = Math.Max(X, other.X);
        int top = Math.Max(Y, other.Y);
        int right = Math.Min(Right, other.Right);
        int bottom = Math.Min(Bottom, other.Bottom);
        return FromLTRB(left, top, right, bottom);
    }

    /// <summary>并集（包围盒）；空矩形不参与。</summary>
    public IntRect Union(IntRect other)
    {
        if (IsEmpty) return other;
        if (other.IsEmpty) return this;
        return FromLTRB(
            Math.Min(X, other.X),
            Math.Min(Y, other.Y),
            Math.Max(Right, other.Right),
            Math.Max(Bottom, other.Bottom));
    }

    public IntRect Offset(int dx, int dy) => new(X + dx, Y + dy, Width, Height);
    public IntRect Offset(IntPoint d) => Offset(d.X, d.Y);

    /// <summary>四边同时外扩/内缩（负值表示内缩）。内缩过度会得到空矩形而不是翻转。</summary>
    public IntRect Inflate(int left, int top, int right, int bottom) =>
        FromLTRB(X - left, Y - top, Right + right, Bottom + bottom);

    public IntRect WithSize(IntSize size) => new(X, Y, size.Width, size.Height);

    /// <summary>
    /// 把这个矩形裁剪进 <paramref name="bounds"/>，并同步返回裁剪后它在原矩形内部的偏移。
    ///
    /// 这是 blit 的核心安全阀：源矩形被裁掉左边 3 像素时，目标 x 必须同时 +3，
    /// 否则整块内容会错位——而错位 1 像素正是本项目最不能接受的缺陷。
    /// </summary>
    public readonly IntRect ClipTo(IntRect bounds, out IntPoint sourceShift)
    {
        IntRect clipped = Intersect(bounds);
        sourceShift = new IntPoint(clipped.X - X, clipped.Y - Y);
        return clipped;
    }

    public override string ToString() => IsEmpty ? "空矩形" : $"[{X},{Y} {Width}×{Height}]";
}
