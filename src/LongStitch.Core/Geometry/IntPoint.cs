namespace LongStitch.Core.Geometry;

/// <summary>
/// 整数二维坐标。全项目的几何坐标都用它，不允许出现 float 版本。
///
/// 为什么这么较真：本产品的核心承诺是"拼缝位置一像素不出错"。
/// 只要坐标在任何一个环节变成浮点，就会在 125%/150% DPI 缩放、
/// 或者连续多次拖动之后累积出肉眼看不见、但 sha256 一比就红的偏差。
/// 所以这里从类型上就不给浮点坐标留位置。
/// </summary>
public readonly record struct IntPoint(int X, int Y)
{
    public static readonly IntPoint Zero = new(0, 0);

    public static IntPoint operator +(IntPoint a, IntPoint b) => new(a.X + b.X, a.Y + b.Y);
    public static IntPoint operator -(IntPoint a, IntPoint b) => new(a.X - b.X, a.Y - b.Y);

    /// <summary>按整数比例放大（仅用于显示层把源像素换算成屏幕像素，不参与拼合计算）。</summary>
    public IntPoint Scale(int factor) => new(X * factor, Y * factor);

    public override string ToString() => $"({X}, {Y})";
}
