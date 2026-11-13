using LongStitch.Core.Geometry;
using LongStitch.Core.Imaging;

namespace LongStitch.Core.Model;

/// <summary>
/// 一个参与拼合的片段：某张源图的一个整数矩形区域，外加若干清理补丁。
///
/// 为什么"片段"和"源图"要分成两层：需求 §6 的切分会把一张图分成两段独立移动/删除，
/// 而"删段"删的也是片段；同时同一张源图可能被多个片段引用（例如状态栏替换的来源）。
/// 把"取哪块"与"是哪张图"分开，切分/合并就不会去动源图记录，符合"原图不被覆盖"。
/// </summary>
public sealed class Segment
{
    public required string Id { get; init; }

    /// <summary>指向 <see cref="SourceImage.Id"/>。</summary>
    public required string SourceId { get; set; }

    /// <summary>从源图取出的区域（源图坐标系，整数）。导入时等于整张源图。</summary>
    public IntRect SourceRect { get; set; }

    /// <summary>本段额外的四边内缩（单张裁边）。</summary>
    public Insets EdgeCrop { get; set; } = Insets.Zero;

    /// <summary>清理补丁，按顺序应用。全部是整数区域操作。</summary>
    public List<PixelPatch> Patches { get; set; } = new();

    /// <summary>
    /// 有效区域 = 源区域先裁全局边、再内缩用户单张裁边。缩到空是合法状态（表示这段没有内容），
    /// 由布局层跳过并提示，而不是在这里抛异常打断整个拼合。
    /// </summary>
    public IntRect EffectiveRect(Insets globalCrop) => EdgeCrop.Apply(globalCrop.Apply(SourceRect));

    public override string ToString() => $"片段 {Id} ← {SourceId} {SourceRect}";
}

/// <summary>
/// 片段上的一块像素修改。清理归一到这一个概念，
/// 于是"可撤销、可逐张关闭、可预览掩膜、有前后对比"只需要实现一次。
/// </summary>
public sealed class PixelPatch
{
    /// <summary>稳定标识，界面"仅对某张取消处理"按它开关。</summary>
    public required string Id { get; init; }

    public PatchKind Kind { get; set; }

    /// <summary>被修改的区域，坐标相对片段**有效区域**左上角（不是源图坐标）。</summary>
    public IntRect Target { get; set; }

    /// <summary>
    /// <see cref="PatchKind.ExtendColumn"/> 时取哪一列（有效区域局部坐标）向右延展。
    /// 抹滚动条用这个：滚动条紧贴右缘时，把左边第一列非滚动条像素延展过去，
    /// 既去掉了滚动条，又不改变画布宽度、也不涂抹正文。
    /// </summary>
    public int ExtendColumnX { get; set; }

    /// <summary>是否生效。关掉它不删除记录，便于逐项开关与前后对比。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>可读来源说明，界面在掩膜预览与前后对比里显示它（例如"检测到右侧滚动条，宽 5px"）。</summary>
    public string Reason { get; set; } = "";

    /// <summary>置信度 0–1。低于阈值的清理必须标"需检查"，不许自动涂抹正文（需求 §6）。</summary>
    public double Confidence { get; set; } = 1.0;
}
