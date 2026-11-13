using LongStitch.Core.Geometry;
using LongStitch.Core.Imaging.Png;

namespace LongStitch.Core.Model;

/// <summary>
/// 一张导入的源图。
///
/// 核心原则 §1「原图与编辑参数分离」的落点：这里只记录**路径、尺寸与格式事实**，
/// 永远不持有可被编辑污染的像素，也从不写回原文件。
/// 像素由 App 层实现的 <c>ISourceResolver</c> 按需解码（带 LRU），Core 不碰磁盘。
/// </summary>
public sealed class SourceImage
{
    /// <summary>稳定标识。重排、删除、切分都不改它，拼缝与补丁都引用它。</summary>
    public required string Id { get; init; }

    /// <summary>原始文件路径（绝对路径）。项目文件只引用路径，不嵌入原图，除非用户显式"打包项目"。</summary>
    public required string Path { get; set; }

    /// <summary>导入时记录的文件名，用于源文件丢失后仍能指认是哪一张。</summary>
    public required string FileName { get; set; }

    /// <summary>应用 EXIF 方向之后的像素尺寸——后续所有几何都以它为准。</summary>
    public required IntSize Size { get; set; }

    /// <summary>EXIF Orientation 原值（1–8）；1 表示无需旋转。PNG 通常为 0（不适用）。</summary>
    public int ExifOrientation { get; set; } = 1;

    /// <summary>导入时记录的字节数，用于导出前预估与"文件是否被外部改动"的粗判。</summary>
    public long FileSizeBytes { get; set; }

    /// <summary>导入时记录的修改时间（Unix 毫秒）。源文件被替换时能发现。</summary>
    public long FileModifiedUtcMs { get; set; }

    /// <summary>PNG 时为解码前读到的格式事实（位深、ICC、gAMA 等）；其他格式为 null。</summary>
    public PngFormatInfo? PngFormat { get; set; }

    /// <summary>格式名（小写无点），如 png / jpeg / webp。</summary>
    public string Format { get; set; } = "png";

    /// <summary>导入阶段发现的"会被有损改动"的条目；界面据此标"需检查"，不静默通过。</summary>
    public List<string> ImportWarnings { get; set; } = new();

    /// <summary>源文件当前是否还能定位到（由 App 层在打开项目/刷新时回填）。</summary>
    public bool SourceMissing { get; set; }

    public override string ToString() => $"{FileName} {Size.Width}×{Size.Height}";
}
