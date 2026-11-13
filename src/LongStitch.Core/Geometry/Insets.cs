namespace LongStitch.Core.Geometry;

/// <summary>
/// 四边内缩量（源图像素，非负）。全局裁边与单张裁边都用它表达。
///
/// 之所以不用"左右上下各一个 int 字段"散落在各模型里：内缩一旦过度（缩过头得到
/// 零宽或负宽）必须统一变成空矩形并让上层显式提示，集中一处实现才不会被漏掉。
/// </summary>
public readonly record struct Insets(int Left, int Top, int Right, int Bottom)
{
    public static readonly Insets Zero = new(0, 0, 0, 0);

    public bool IsZero => Left == 0 && Top == 0 && Right == 0 && Bottom == 0;

    /// <summary>对矩形施加内缩；缩过头返回 <see cref="IntRect.Empty"/> 而不是翻转坐标。</summary>
    public IntRect Apply(IntRect rect)
    {
        int l = Math.Max(0, Left), t = Math.Max(0, Top);
        int r = Math.Max(0, Right), b = Math.Max(0, Bottom);
        return IntRect.FromLTRB(rect.X + l, rect.Y + t, rect.Right - r, rect.Bottom - b);
    }

    public Insets Offseted(int delta) => new(
        Math.Max(0, Left + delta), Math.Max(0, Top + delta),
        Math.Max(0, Right + delta), Math.Max(0, Bottom + delta));
}
