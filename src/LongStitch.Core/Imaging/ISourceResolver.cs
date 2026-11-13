using LongStitch.Core.Imaging;

namespace LongStitch.Core.Imaging;

/// <summary>
/// 源图像素的提供者。Core 只定义接口，不碰磁盘——这样布局、匹配、导出、预览
/// 全都能在内存里被测（Checks 就是靠它注入合成图跑完整链路的）。
///
/// App 层的实现负责：按路径解码（PNG 走自研解码器，JPEG/WebP 走系统解码器）、
/// 应用 EXIF 方向、LRU 缓存、失败时给出可读原因。
/// </summary>
public interface ISourceResolver
{
    /// <summary>
    /// 取一张源图的完整 RGBA 像素。
    /// 实现可以缓存，但<b>返回的实例必须被视为只读</b>：多个片段共享同一实例，
    /// 任何就地修改都会污染所有引用者。需要可写副本请自己 Clone。
    /// </summary>
    /// <exception cref="KeyNotFoundException">源文件丢失或从未导入。</exception>
    RgbaImage Resolve(string sourceId);

    /// <summary>不抛异常的版本，用于导入校验与"源文件丢失"提示。</summary>
    bool TryResolve(string sourceId, out RgbaImage? image, out string? error);

    /// <summary>让实现丢弃缓存（源文件被外部替换、或内存压力时）。</summary>
    void Invalidate(string? sourceId = null);
}
