namespace LongStitch.Core.Imaging.Png;

/// <summary>
/// PNG 里 <c>eXIf</c> 块的最小解析：只取 Orientation（标签 0x0112）。
///
/// 为什么需要它：真实 iPhone 截图导出的 PNG 普遍带 <c>eXIf</c>，里面就有方向标签
/// （本仓库 samples/local 下六张真图都带，值均为 1）。
/// 之前 PNG 路径把方向硬编码成 1，横屏拍摄或被编辑过的图就会<b>静默转错方向</b>——
/// 转错方向会让所有拼缝坐标整体失效，属于最难查的那类问题。
///
/// 只做这一件事，不做完整 EXIF：其它标签对拼合没有意义，多解析一处就多一处出错可能。
/// </summary>
public static class PngExif
{
    private const ushort TagOrientation = 0x0112;
    private const ushort TypeByte = 1;
    private const ushort TypeShort = 3;

    /// <summary>
    /// 从 eXIf 块字节里读方向值。读不到、结构不认识或值不在 1–8 内都返回 1（= 不旋转），
    /// 因为"当作没方向信息"是唯一不会破坏画面的兜底。
    /// </summary>
    public static int ReadOrientation(ReadOnlySpan<byte> exif)
    {
        // TIFF 头：字节序标记(2) + 固定 42(2) + IFD0 偏移(4)
        if (exif.Length < 8) return 1;
        bool little;
        if (exif[0] == (byte)'I' && exif[1] == (byte)'I') little = true;
        else if (exif[0] == (byte)'M' && exif[1] == (byte)'M') little = false;
        else return 1;

        if (Read16(exif, 2, little) != 42) return 1;   // TIFF 魔数
        int ifd = Read32(exif, 4, little);
        if (ifd < 0 || ifd + 2 > exif.Length) return 1;

        int count = Read16(exif, ifd, little);
        // 每个目录项 12 字节；越界就停，不猜
        for (int i = 0; i < count; i++)
        {
            int entry = ifd + 2 + i * 12;
            if (entry + 12 > exif.Length) return 1;
            if (Read16(exif, entry, little) != TagOrientation) continue;

            int type = Read16(exif, entry + 2, little);
            // SHORT 且 count=1 时，值就放在"值/偏移"字段的前两个字节里（同端序）
            if (type == TypeShort) return ClampToValid(Read16(exif, entry + 8, little));
            if (type == TypeByte && Read32(exif, entry + 4, little) == 1)
                return ClampToValid(exif[entry + 8]);
            return 1;
        }
        return 1;
    }

    // 端序由调用方传进来的布尔决定；写成静态方法而不是局部函数，
    // 因为 C# 不允许局部函数捕获 ReadOnlySpan 这类 ref 结构参数。
    private static int Read16(ReadOnlySpan<byte> d, int at, bool little) => little
        ? d[at] | (d[at + 1] << 8)
        : (d[at] << 8) | d[at + 1];

    private static int Read32(ReadOnlySpan<byte> d, int at, bool little) => little
        ? d[at] | (d[at + 1] << 8) | (d[at + 2] << 16) | (d[at + 3] << 24)
        : (d[at] << 24) | (d[at + 1] << 16) | (d[at + 2] << 8) | d[at + 3];

    /// <summary>方向值只认 1–8，其余一律当作"没有方向信息"。</summary>
    private static int ClampToValid(int value) => value is >= 1 and <= 8 ? value : 1;
}
