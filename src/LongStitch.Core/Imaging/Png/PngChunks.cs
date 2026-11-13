namespace LongStitch.Core.Imaging.Png;

/// <summary>
/// PNG 分块结构与 CRC-32。
///
/// 为什么要自己写：PNG 的每个数据块尾部都有 CRC，解码时必须校验，编码时必须生成。
/// 这是"无损"链条上最容易被第三方库悄悄放宽的一环（有些实现不校验 CRC 就继续解码，
/// 于是坏文件会被静默读成"看起来正常"的图）。我们自己实现，就自己把关。
/// </summary>
internal static class PngChunks
{
    /// <summary>PNG 文件签名（8 字节），同时能挡住大部分误扩展名的文件。</summary>
    public static ReadOnlySpan<byte> Signature =>
        new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 };

    public const uint ChunkIhdr = 0x49484452; // "IHDR"
    public const uint ChunkPlte = 0x504C5445; // "PLTE"
    public const uint ChunkIdat = 0x49444154; // "IDAT"
    public const uint ChunkIend = 0x49454E44; // "IEND"
    public const uint ChunkTrns = 0x74524E53; // "tRNS"
    public const uint ChunkGama = 0x67414D41; // "gAMA"
    public const uint ChunkIccp = 0x69434350; // "iCCP"
    public const uint ChunkChrm = 0x6348524D; // "cHRM"
    public const uint ChunkBkgd = 0x624B4744; // "bKGD"
    public const uint ChunkExif = 0x65584966; // "eXIf"
    public const uint ChunkSrgb = 0x73524742; // "sRGB"

    /// <summary>把 4 个 ASCII 字节读成大端 uint（用于比较块类型）。</summary>
    public static uint TypeCode(ReadOnlySpan<byte> four) =>
        ((uint)four[0] << 24) | ((uint)four[1] << 16) | ((uint)four[2] << 8) | four[3];

    public static void WriteUInt32BigEndian(Span<byte> dst, uint value)
    {
        dst[0] = (byte)(value >> 24);
        dst[1] = (byte)(value >> 16);
        dst[2] = (byte)(value >> 8);
        dst[3] = (byte)value;
    }

    public static uint ReadUInt32BigEndian(ReadOnlySpan<byte> src) =>
        ((uint)src[0] << 24) | ((uint)src[1] << 16) | ((uint)src[2] << 8) | src[3];

    public static ushort ReadUInt16BigEndian(ReadOnlySpan<byte> src) =>
        (ushort)((src[0] << 8) | src[1]);

    /// <summary>
    /// CRC-32（多项式 0xEDB88320，PNG/ZIP 同款），打表一次复用。
    /// 注意 PNG 的 CRC 覆盖"块类型 + 块数据"，不含长度字段——写错这一点会导致
    /// 自己生成的 PNG 被别的解码器判为坏文件，所以 Checks 里专门有一格用 GDI+ 读回来核对。
    /// </summary>
    public static uint Crc32(ReadOnlySpan<byte> typeFourCc, ReadOnlySpan<byte> data, uint seed = 0)
    {
        uint crc = ~seed;
        for (int i = 0; i < 4; i++)
            crc = CrcTable[(crc ^ typeFourCc[i]) & 0xFF] ^ (crc >> 8);
        foreach (byte b in data)
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return ~crc;
    }

    public static uint Crc32(ReadOnlySpan<byte> data) => Crc32(ReadOnlySpan<byte>.Empty, data);

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }
}

/// <summary>
/// 一次遍历 PNG 得到的分块视图。只负责切块与 CRC 校验，不负责解释语义。
/// </summary>
internal ref struct PngChunkCursor
{
    private readonly ReadOnlySpan<byte> _file;
    private int _pos;

    public PngChunkCursor(ReadOnlySpan<byte> file)
    {
        _file = file;
        _pos = PngChunks.Signature.Length;
    }

    /// <summary>当前块类型（<see cref="PngChunks"/> 里的 Chunk* 常量）。</summary>
    public uint Type { get; private set; }

    /// <summary>当前块数据（不含长度、类型与 CRC）。</summary>
    public ReadOnlySpan<byte> Data { get; private set; }

    /// <summary>推进到下一个块。返回 false 表示已到文件尾。</summary>
    /// <exception cref="PngException">长度越界或 CRC 不符。</exception>
    public bool MoveNext()
    {
        if (_pos + 8 > _file.Length) return false;

        uint length = PngChunks.ReadUInt32BigEndian(_file.Slice(_pos, 4));
        ReadOnlySpan<byte> type = _file.Slice(_pos + 4, 4);
        long dataStart = (long)_pos + 8;
        if (dataStart + length + 4 > _file.Length)
            throw new PngException($"PNG 块 {AsText(type)} 声明长度 {length} 超出文件范围");

        ReadOnlySpan<byte> data = _file.Slice((int)dataStart, (int)length);
        uint storedCrc = PngChunks.ReadUInt32BigEndian(
            _file.Slice((int)(dataStart + length), 4));
        uint actualCrc = PngChunks.Crc32(type, data);
        if (storedCrc != actualCrc)
            throw new PngException(
                $"PNG 块 {AsText(type)} 的 CRC 校验失败（期望 {storedCrc:X8}，实际 {actualCrc:X8}）。" +
                "文件可能已损坏，本工具不会在坏文件上继续拼合。");

        Type = PngChunks.TypeCode(type);
        Data = data;
        _pos = (int)(dataStart + length + 4);
        return true;
    }

    private static string AsText(ReadOnlySpan<byte> type)
    {
        Span<char> chars = stackalloc char[4];
        for (int i = 0; i < 4; i++) chars[i] = (char)type[i];
        return new string(chars);
    }
}

/// <summary>PNG 解析/编码错误。携带可读原因，供界面逐项提示。</summary>
public sealed class PngException : Exception
{
    public PngException(string message) : base(message) { }
    public PngException(string message, Exception inner) : base(message, inner) { }
}
