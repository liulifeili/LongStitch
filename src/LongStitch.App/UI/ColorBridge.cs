using LongStitch.Core.Imaging;

namespace LongStitch.App.UI;

/// <summary>
/// RgbaColor（Core，与图形 API 无关）与 GDI+ Color（App 层）之间的桥。
///
/// 放在 App 层而不是 Core 里：Core 保持 net8.0、不引用 System.Drawing，
/// 才能在任何环境下被 Checks 直接跑。
/// </summary>
public static class ColorBridge
{
    public static Color ToGdiColor(this RgbaColor color) =>
        Color.FromArgb(color.A, color.R, color.G, color.B);

    public static RgbaColor ToRgba(this Color color) => new(color.R, color.G, color.B, color.A);
}
