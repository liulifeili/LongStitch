using LongStitch.Core;
using LongStitch.Core.Geometry;
using LongStitch.Core.Imaging;
using LongStitch.Core.Model;

namespace LongStitch.Checks;

/// <summary>
/// 内存版源图解析器：让布局、匹配、导出整条链路都能脱离磁盘与 GUI 被测试。
/// Core 只依赖 <see cref="ISourceResolver"/> 接口，所以这件事不需要任何 mock 框架。
/// </summary>
public sealed class MemoryResolver : ISourceResolver
{
    private readonly Dictionary<string, RgbaImage> _images = new();
    private int _nextId = 1;

    /// <summary>登记一张图，返回对应的源图记录（已加入项目所需的尺寸信息）。</summary>
    public SourceImage Add(RgbaImage image, string? name = null)
    {
        string id = $"src_{_nextId++:D3}";
        _images[id] = image;
        return new SourceImage
        {
            Id = id,
            Path = $@"memory:\{id}",
            FileName = name ?? $"{id}.png",
            Size = image.Size,
            Format = "png",
        };
    }

    public RgbaImage Resolve(string sourceId) =>
        _images.TryGetValue(sourceId, out RgbaImage? img)
            ? img
            : throw new KeyNotFoundException($"源图 {sourceId} 不在测试素材里（模拟「源文件丢失」）");

    public bool TryResolve(string sourceId, out RgbaImage? image, out string? error)
    {
        if (_images.TryGetValue(sourceId, out image)) { error = null; return true; }
        error = $"缺少源图 {sourceId}";
        return false;
    }

    public void Invalidate(string? sourceId = null)
    {
        if (sourceId is null) _images.Clear();
        else _images.Remove(sourceId);
    }

    /// <summary>模拟"源文件被外部替换成更小的同名文件"：尺寸变了但 Id 还在。</summary>
    public void Replace(string sourceId, RgbaImage smaller) => _images[sourceId] = smaller;
}

/// <summary>构造测试项目的公共入口。</summary>
public static class TestProject
{
    /// <summary>用几张内存图建一个长截图项目：每图一段，拼缝齐备。</summary>
    public static (Project Project, MemoryResolver Resolver, SourceImage[] Sources) Create(
        StitchMode mode, params RgbaImage[] images)
    {
        var resolver = new MemoryResolver();
        var project = new Project(new ProjectDocument { Mode = mode });
        var sources = new List<SourceImage>();

        foreach (RgbaImage img in images)
        {
            SourceImage src = resolver.Add(img);
            project.Doc.Sources.Add(src);
            sources.Add(src);
            project.Doc.Segments.Add(new Segment
            {
                Id = Project.NewId("seg"),
                SourceId = src.Id,
                SourceRect = new IntRect(0, 0, img.Width, img.Height),
            });
        }
        project.EnsureSeams();
        return (project, resolver, sources.ToArray());
    }

    /// <summary>
    /// 从一张"长图"里切出连续滚动的两屏，用来喂匹配算法。
    /// 返回的两张图重叠 <paramref name="overlap"/> 行——这是唯一能"知道正确答案"的构造方式，
    /// 于是匹配精度可以被量化断言，而不是"看起来对"。
    /// </summary>
    public static (RgbaImage Before, RgbaImage After, int ExpectedOverlap) CutScrollPair(
        RgbaImage longImage, int firstTop, int firstHeight, int overlap)
    {
        RgbaImage before = longImage.SubImage(new IntRect(0, firstTop, longImage.Width, firstHeight));
        int secondTop = firstTop + firstHeight - overlap;
        int secondHeight = Math.Min(longImage.Height - secondTop, firstHeight);
        RgbaImage after = longImage.SubImage(new IntRect(0, secondTop, longImage.Width, secondHeight));
        return (before, after, overlap);
    }
}
