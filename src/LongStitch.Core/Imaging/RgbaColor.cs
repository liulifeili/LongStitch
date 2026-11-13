namespace LongStitch.Core.Imaging;

/// <summary>
/// 一个 RGBA8888 像素，<see cref="A"/> 为"非预乘"alpha。
///
/// 为什么强调非预乘：很多图形管线（包括部分框架的默认表面格式）内部用预乘 alpha。
/// 预乘 ↔ 非预乘的往返换算是有损的（除以 alpha 再乘回去会丢低位），
/// 一次看不出来，两次就可能改变字节。本项目全链路固定非预乘，
/// 任何需要预乘的显示场合都在显示层临时换算，不回写。
/// </summary>
public readonly record struct RgbaColor(byte R, byte G, byte B, byte A)
{
    public static readonly RgbaColor Transparent = new(0, 0, 0, 0);
    public static readonly RgbaColor Black = new(0, 0, 0, 255);
    public static readonly RgbaColor White = new(255, 255, 255, 255);

    public const uint OpaqueAlpha = 255;

    /// <summary>打包成 uint，便于比较与填充：高位→低位依次是 R、G、B、A。</summary>
    public uint ToPacked() => ((uint)R << 24) | ((uint)G << 16) | ((uint)B << 8) | A;

    public static RgbaColor FromPacked(uint v) =>
        new((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v);

    /// <summary>从十六进制字符串解析（用于 JPEG 背景色设置项），支持 #RRGGBB 与 RRGGBB。</summary>
    public static bool TryParse(string text, out RgbaColor color)
    {
        color = White;
        if (string.IsNullOrWhiteSpace(text)) return false;
        string s = text.Trim().TrimStart('#');
        if (s.Length != 6 && s.Length != 8) return false;
        Span<byte> bytes = stackalloc byte[s.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
        {
            if (!byte.TryParse(s.AsSpan(i * 2, 2),
                    System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out bytes[i]))
                return false;
        }
        color = bytes.Length == 3
            ? new RgbaColor(bytes[0], bytes[1], bytes[2], 255)
            : new RgbaColor(bytes[0], bytes[1], bytes[2], bytes[3]);
        return true;
    }

    /// <summary>把半透明色按整数运算合成到不透明底色上（仅用于显示层的重叠预览，不参与导出）。</summary>
    public RgbaColor CompositeOver(RgbaColor bg, int alphaNumerator, int alphaDenominator)
    {
        int n = Math.Clamp(alphaNumerator, 0, alphaDenominator);
        int d = alphaDenominator;
        // 先乘后除、四舍五入，避免连续小数累加
        static byte Mix(byte a, byte b, int n, int d) =>
            (byte)((a * n + b * (d - n) + d / 2) / d);
        return new RgbaColor(
            Mix(R, bg.R, n, d), Mix(G, bg.G, n, d), Mix(B, bg.B, n, d), A);
    }
}
