namespace LongStitch.Core.Model;

/// <summary>
/// 可序列化的项目状态。这就是"项目文件"的全部内容，也是撤销栈里存的东西。
///
/// 三条约束（对应需求 §2.1 与 §4.2）：
///   1. 只有参数，没有像素。撤销历史因此可以存整份快照而不爆内存（一份通常几 KB）。
///   2. <see cref="Segments"/> 的列表顺序**就是**拼合顺序；缩略图拖动排序即重排这个列表。
///   3. <see cref="Seams"/> 用 (BeforeId, AfterId) 关联片段，不依赖列表下标，
///      这样重排/删除后仍能把手动参数找回对应的缝（找不回的那条会被重建为 Pending）。
/// </summary>
public sealed class ProjectDocument
{
    /// <summary>存档格式版本。读档时只接受 &lt;= 当前版本，升级靠迁移而不是猜字段。</summary>
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public const int CurrentSchemaVersion = 1;

    public string Title { get; set; } = "未命名项目";

    public StitchMode Mode { get; set; } = StitchMode.LongScroll;

    /// <summary>导入的源图（按导入顺序记录，与拼合顺序无关）。</summary>
    public List<SourceImage> Sources { get; set; } = new();

    /// <summary>参与拼合的片段，顺序即拼合顺序。</summary>
    public List<Segment> Segments { get; set; } = new();

    /// <summary>相邻片段之间的拼缝。</summary>
    public List<Seam> Seams { get; set; } = new();

    public CropSettings Crop { get; set; } = new();
    public CleanupSettings Cleanup { get; set; } = CleanupSettings.CreateSafeDefault();
    public SpacingSettings Spacing { get; set; } = new();
    public ExportSettings Export { get; set; } = new();

    /// <summary>按 Id 查源图；找不到返回 null（源文件缺失或项目被外部改动）。</summary>
    public SourceImage? FindSource(string id) =>
        Sources.FirstOrDefault(s => s.Id == id);

    /// <summary>按 Id 查片段。</summary>
    public Segment? FindSegment(string id) =>
        Segments.FirstOrDefault(s => s.Id == id);

    /// <summary>按片段对查拼缝。</summary>
    public Seam? FindSeam(string beforeId, string afterId) =>
        Seams.FirstOrDefault(s => s.BeforeId == beforeId && s.AfterId == afterId);

    /// <summary>
    /// 单张源图对应的片段数。切分后大于 1，用于界面标注"这张图被分成了 N 段"。
    /// </summary>
    public int SegmentCountOfSource(string sourceId) => Segments.Count(s => s.SourceId == sourceId);
}
