namespace LongStitch.Core.Imaging.Png;

/// <summary>PNG IHDR 的 colorType 字段。</summary>
public enum PngColorType
{
    Gray = 0,
    Truecolor = 2,
    Indexed = 3,
    GrayAlpha = 4,
    Rgba = 6,
    Unknown = -1,
}

/// <summary>
/// 解码前从一个 PNG 文件里读出的"格式事实"。
///
/// 它存在的唯一理由是把"这张图会不会被有损改动"变成可展示、可断言的信息，
/// 对应产品需求 §2.5「输出尺寸触及限制时不能静默压缩」在色彩维度上的同类问题：
/// 16bit 源图、带 ICC/gAMA 的源图，任何工具都要做转换；我们选择**说清楚**，
/// 而不是假装无损。界面会把这些以"需检查"的形式列出来。
/// </summary>
public sealed record PngFormatInfo
{
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required int BitDepth { get; init; }
    public required PngColorType ColorType { get; init; }

    /// <summary>是否为 Adam7 隔行存储。解码器支持七遍反交错，这里如实标出来供界面显示。</summary>
    public bool Interlaced { get; init; }

    /// <summary>是否存在透明信息（tRNS 块，或 colorType 本身带 alpha）。</summary>
    public bool HasTransparency { get; init; }

    public bool HasIccProfile { get; init; }
    public bool HasGamma { get; init; }
    public bool HasChromaticities { get; init; }
    public bool HasBackground { get; init; }

    /// <summary>是否带 sRGB 块。真实 iPhone 截图普遍带这个（samples/local 下六张里五张是 sRGB）。</summary>
    public bool HasSrgbChunk { get; init; }

    /// <summary>
    /// 从 <c>eXIf</c> 块读出的 EXIF Orientation（1–8；1 = 不旋转）。
    /// PNG 也可以携带 EXIF，之前 PNG 路径把方向硬编码成 1，横屏图会静默转错。
    /// </summary>
    public int ExifOrientation { get; init; } = 1;

    /// <summary>16bit 通道会被取高字节降为 8bit，这是一次有损转换，必须上报。</summary>
    public bool RequiresBitDepthConversion => BitDepth == 16;

    /// <summary>
    /// 汇总"本图在导入时会被改动像素的地方"。空列表表示导入是无损的。
    /// 界面把它显示在源图信息里，Checks 里也有一格断言普通截图的该列表必须为空。
    /// </summary>
    public IReadOnlyList<string> LossyNotes
    {
        get
        {
            var notes = new List<string>();
            if (RequiresBitDepthConversion)
                notes.Add("源图是 16 位通道，已按高字节降为 8 位（有损）");
            if (HasIccProfile)
                notes.Add("源图带 ICC 描述文件；本工具不做色彩管理，导出按原始像素写入（不重映射）");
            if (HasGamma)
                notes.Add("源图带 gAMA 块；本工具不做伽马校正，导出按原始像素写入");
            if (HasChromaticities)
                notes.Add("源图带 cHRM 块；本工具不做色度重映射");
            if (HasSrgbChunk)
                notes.Add("源图声明 sRGB 色彩空间；本工具不做色彩管理，导出按原始像素写入");
            if (ExifOrientation is > 1 and <= 8)
                notes.Add($"已按 EXIF Orientation={ExifOrientation} 旋转/翻转（无损重排，宽高会随之交换）");
            return notes;
        }
    }
}

/// <summary>解码结果：像素 + 格式事实。</summary>
public sealed record PngDecodeResult(RgbaImage Image, PngFormatInfo Info);
