using LongStitch.Core.Geometry;
using LongStitch.Core.Imaging;

namespace LongStitch.Core.Model;

/// <summary>导出格式。只有 PNG 承诺字节级无损；JPEG 天生有损，界面必须写清楚。</summary>
public enum ExportFormat
{
    Png,
    Jpeg,
    /// <summary>需要平台解码器支持，Windows 上不一定可用；不可用时界面给出原因而不是静默回退。</summary>
    WebP,
    /// <summary>首版未实现编码器，选择时界面禁用并说明。</summary>
    Tiff,
}

/// <summary>交叉轴对齐（竖拼统一宽度 / 横拼统一高度时，尺寸不一致的那一维怎么摆）。</summary>
public enum CrossAlign
{
    /// <summary>靠左/靠上。</summary>
    Near,

    /// <summary>居中。</summary>
    Center,

    /// <summary>靠右/靠下。</summary>
    Far,
}

/// <summary>全局统一裁边（需求 §6"可按全局统一边距裁剪，也可单张调整"）。</summary>
public sealed class CropSettings
{
    public bool Enabled { get; set; }
    public Insets Uniform { get; set; } = Insets.Zero;
}

/// <summary>滚动条清理设置。默认开启（需求 §6），但只在置信度足够时才真的动手。</summary>
public sealed class ScrollbarSettings
{
    public bool Enabled { get; set; } = true;

    /// <summary>低于此置信度只标"需检查"，不自动涂抹正文。</summary>
    public double MinConfidence { get; set; } = 0.60;

    /// <summary>允许检测的最大条宽（源图像素）。超过就不该被当成滚动条。</summary>
    public int MaxThickness { get; set; } = 10;
}

/// <summary>
/// 清理设置。第十四轮末次下线了"重复状态栏/底部固定栏"的合并、替换、裁剪、留白与时间替换，
/// 只剩滚动条这一项（需求 §6 的另外两项见 README「第四十改」的作废记录）。
/// </summary>
public sealed class CleanupSettings
{
    public ScrollbarSettings Scrollbar { get; set; } = new();

    /// <summary>
    /// 新项目里所有"会改动像素"的破坏性选项默认关闭（需求 §9）。
    /// 滚动条清理虽然默认开启，但它只覆盖边缘窄条且受置信度门限约束。
    /// </summary>
    public static CleanupSettings CreateSafeDefault() => new();
}

/// <summary>竖拼/横拼的间距与对齐（需求 §5.3）。</summary>
public sealed class SpacingSettings
{
    /// <summary>相邻片段之间的额外间隙（源图像素，补透明）。</summary>
    public int Gap { get; set; }

    /// <summary>交叉轴对齐方式。</summary>
    public CrossAlign Align { get; set; } = CrossAlign.Near;

    /// <summary>
    /// 是否按目标宽/高等比缩放统一。这是显式的有损重采样，默认关闭；
    /// 开启后预览与导出都必须标注新像素值（需求 §5.3）。
    /// </summary>
    public bool ResampleToUniformCrossSize { get; set; }

    /// <summary>重采样目标尺寸（仅在上一项为 true 时生效）。</summary>
    public int UniformCrossSize { get; set; }
}

/// <summary>
/// 导出设置。用 record 是为了让界面能安全地"拿一份副本去试算"而不改动项目里的真值
/// （导出对话框每次刷新预览计划都要改格式/分段参数，那些在用户点"开始导出"之前不该生效）。
/// </summary>
public sealed record ExportSettings
{
    public ExportFormat Format { get; set; } = ExportFormat.Png;

    /// <summary>JPEG/WebP 不支持透明时使用的背景色（需求 §7"透明画布导出 JPEG 时须选择背景色"）。</summary>
    public RgbaColor Background { get; set; } = RgbaColor.White;

    /// <summary>
    /// 第五十六改②：PNG 是否保留透明。true（默认）＝透明像素原样进 PNG（历史行为，旧项目文件
    /// 缺这个字段的反序列化也落在这里）；false＝写文件前把透明像素按整数合成公式压到
    /// <see cref="Background"/> 上（界面上的「透明」勾勾掉之后的承诺）。
    /// JPEG 没有透明档，编码器本来就压底色，不读这项；TIFF 维持历史行为（不压）。
    /// </summary>
    public bool PngTransparent { get; set; } = true;

    /// <summary>JPEG 质量 1–100。仅对 JPEG 生效。</summary>
    public int JpegQuality { get; set; } = 92;

    /// <summary>分段导出：每片最大高度（源图像素）。0 表示不分段。</summary>
    public int MaxSegmentHeight { get; set; }

    /// <summary>相邻分段之间保留的重叠行数，便于阅读上下文。0 表示严格首尾相接。</summary>
    public int SegmentOverlap { get; set; }

    /// <summary>
    /// 用户主动选择的缩小倍数（1 = 不缩，2 = 输出宽高各减半）。
    ///
    /// 为什么限定为整数而不是任意浮点比例：整数倍才能用<b>精确的整数盒式平均</b>做降采样，
    /// 并且可以流式分块（每 N 行进、一行出，跨带不会错位）。任意浮点比例必须整图驻留内存
    /// 才能算准，与"长图分块导出"直接冲突。
    /// 这也是唯一允许改变导出像素倍数的入口，默认必须是 1；
    /// 触及限制时程序不许自动改它（需求 §7"禁止静默压缩"）。
    /// </summary>
    public int DownscaleDivisor { get; set; } = 1;

    /// <summary>输出目录与文件名模板（{index} 用于分段编号）。</summary>
    public string OutputDirectory { get; set; } = "";
    public string FileNameTemplate { get; set; } = "stitched_{index}.{ext}";
}
