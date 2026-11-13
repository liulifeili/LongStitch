using LongStitch.Core.Geometry;
using LongStitch.Core.Imaging;
using LongStitch.Core.Layout;

namespace LongStitch.Core.Preview;

/// <summary>
/// 视口瓦片层：预览用的低内存图层（需求 §7"预览使用独立的低内存图层"）。
///
/// ═══ 为什么必须分块 ═══
/// 一张 1170×52000 的拼合结果，RGBA 是 243 MB。任何"把整张画布做成一张位图再缩放"的做法
/// 都会先在这里崩掉，而且 GPU 纹理通常也放不下（常见上限 16384）。
/// 所以预览只渲染<b>看得见的那几块 512×512</b>。
///
/// ═══ 为什么瓦片永远是 1:1 源像素 ═══
/// 瓦片内容就是源分辨率的整数 blit，缩放只在贴到屏幕上那一刻由显示层做。
/// 好处有两个：① 放大到 800% 看接缝时看到的是真像素，不是插值糊；
/// ② 预览与导出共用同一套取数代码，不会出现"预览对、导出错"。
/// </summary>
public sealed class TileRenderer
{
    /// <summary>瓦片边长（源图像素）。太小则块数多、开销大；太大则单块渲染卡顿。</summary>
    public const int TileSize = 512;

    private readonly CanvasComposer _composer;

    public TileRenderer(CanvasComposer composer) =>
        _composer = composer ?? throw new ArgumentNullException(nameof(composer));

    /// <summary>画布坐标 → 瓦片列号。</summary>
    public static int TileX(int canvasX) => FloorDiv(canvasX, TileSize);

    /// <summary>画布坐标 → 瓦片行号。</summary>
    public static int TileY(int canvasY) => FloorDiv(canvasY, TileSize);

    public static int TilesAcross(int width) => width <= 0 ? 0 : FloorDiv(width - 1, TileSize) + 1;

    public static int TilesDown(int height) => height <= 0 ? 0 : FloorDiv(height - 1, TileSize) + 1;

    /// <summary>负坐标也要落在正确的瓦片上，所以用向下取整而不是整除（C# 的 / 是向零取整）。</summary>
    private static int FloorDiv(int a, int b) => a >= 0 ? a / b : (a - b + 1) / b;

    /// <summary>渲染一块瓦片。越界的块返回 null（画布没那么长）。</summary>
    public RgbaImage? RenderTile(CanvasLayout layout, int tileX, int tileY)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (layout.IsEmpty) return null;

        var origin = new IntPoint(tileX * TileSize, tileY * TileSize);
        IntRect area = new IntRect(origin.X, origin.Y, TileSize, TileSize).Intersect(layout.CanvasBounds);
        if (area.IsEmpty) return null;

        // 边缘瓦片会小于 TileSize；宽度仍按 area 走，显示层按实际尺寸贴
        return _composer.RenderBand(layout, area);
    }
}

/// <summary>
/// 瓦片缓存（LRU + 按参数修订号整体失效）。
///
/// 失效策略选了最笨也最不会错的一种：参数一变就全清。
/// 原因是"哪几块瓦片受影响"要精确推导（改一条缝只影响它之后的所有段、改裁边影响一段…），
/// 推错一次的后果是屏幕上留着上一版的像素——用户会以为拼错了，而这正是本项目最怕的误判。
/// 重画一屏瓦片只是十几次整数 blit，代价可以忽略。
/// </summary>
public sealed class TileCache
{
    private readonly TileRenderer _renderer;
    private readonly Dictionary<(int X, int Y), Entry> _map = new();
    private readonly LinkedList<(int X, int Y)> _lru = new();
    private long _bytes;

    /// <summary>缓存的字节上限。默认 192 MB ≈ 96 块 512×512 瓦片。</summary>
    public long MemoryLimitBytes { get; set; } = 192L * 1024 * 1024;

    private int _revision = -1;

    /// <summary>
    /// 一共<b>整块清空</b>过多少次（判据读它）。
    /// 拖动预览期间这个数必须一动不动：每帧全清就等于每帧把视口里所有瓦片从源图重算一遍，
    /// 那正是"拖一下拼缝整块画布卡成幻灯片"的根因。
    /// </summary>
    public long FullInvalidations { get; private set; }

    /// <summary>一共真渲染了多少块瓦片（判据读它：缓存命中不算渲染）。</summary>
    public long TileRenders { get; private set; }

    /// <summary>
    /// 取瓦片时命中缓存多少次（第十三轮需求五：状态栏那个"缓存命中率"读这一条与 <see cref="TileRenders"/>）。
    /// 命中率低就说明每帧都在从源图重算——那是"拖动卡顿"最直接的一个读数，
    /// 所以这两个计数器必须由缓存自己记，而不是让界面去猜。
    /// </summary>
    public long CacheHits { get; private set; }

    /// <summary>
    /// 被 <see cref="DropRegion"/> <b>丢掉的块数</b>累计（需求五"整块缓存清理次数"的那一半缺账）。
    /// 为什么必须单独记：拖动途中那一族尖峰的归因是"取数 42.5~49.7ms／命中率 4~6%"，
    /// 可"每帧都在重算"这件事有两种<b>修法相反</b>的原因——
    /// ① 作废范围太宽（局部丢下去的块本来就多），② 缓存太小被 LRU 挤掉（<see cref="TrimEvictions"/>）。
    /// 只有 <see cref="FullInvalidations"/> 一个数的话，这两种形状读起来是同一个"整块清 0 次"，
    /// 于是下一轮只能猜。这两个计数把两者分开，判据与真窗口读数才有主语。
    /// </summary>
    public long RegionDrops { get; private set; }

    /// <summary>因超过 <see cref="MemoryLimitBytes"/> 而被 LRU 挤掉的块数累计（上面那两种原因的第二种）。</summary>
    public long TrimEvictions { get; private set; }

    private sealed record Entry(RgbaImage Image, long Bytes);

    public TileCache(TileRenderer renderer) =>
        _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));

    public int Count => _map.Count;
    public long Bytes => _bytes;

    /// <summary>取一块瓦片；必要时渲染并放入缓存。</summary>
    public RgbaImage? Get(CanvasLayout layout, int tileX, int tileY, int revision)
    {
        if (layout.IsEmpty) return null;

        if (revision != _revision)
        {
            Invalidate();
            _revision = revision;
        }

        var key = (tileX, tileY);
        if (_map.TryGetValue(key, out Entry? hit))
        {
            // 命中就挪到 LRU 头部：滚动时反复用到的中心区不会被边缘块挤掉
            _lru.Remove(key);
            _lru.AddFirst(key);
            CacheHits++;
            return hit.Image;
        }

        RgbaImage? image = _renderer.RenderTile(layout, tileX, tileY);
        if (image is null) return null;
        TileRenders++;

        long size = (long)image.Width * image.Height * 4;
        _map[key] = new Entry(image, size);
        _lru.AddFirst(key);
        _bytes += size;
        Trim();
        return image;
    }

    /// <summary>清空缓存（参数变化、源文件被外部替换、内存压力时）。</summary>
    public void Invalidate()
    {
        FullInvalidations++;
        _map.Clear();
        _lru.Clear();
        _bytes = 0;
    }

    /// <summary>
    /// 只丢掉<b>与这块画布区域相交</b>的瓦片，其余原样留着。
    ///
    /// ═══ 两个手势的"该丢多少"完全不同，所以分开说 ═══
    /// <b>拖蓝线（切点）</b>：带在画布上的位置
    /// [切点−t, 切点+b) 里，带顶 = 前一段起点 + 前段基准高 − 顶裁 − O + 间隙，与切点无关；
    /// 带底同理。带<b>外</b>的任意一行，"来自哪张图的哪一行"折算下来也和切点无关
    /// （后一段整体平移了多少，它的顶裁就跟着少多少，两笔账互相抵掉）。
    /// 所以只丢带区那几块，其余留着上一帧的像素<b>就是对的</b>——
    /// 判据 <c>crop/切点预览只改带内瓦片：带外逐字节相同</c> 把两版布局的带外瓦片逐块比过字节。
    ///
    /// <b>拖红箭头（对齐）不是这个形状</b>，第十八改把这件事量清楚了（原先这里写的是"带外都留得住"，
    /// 那句话只对蓝线成立，把下一轮引到 #96 那条走不通的路上）：重叠多 1 行 ⇒ 画布矮 1 行 ⇒
    /// 带以下<b>每一行</b>的源行号都跟着挪 1 行（8 段夹具实测 delta=1/3/8/40 时
    /// 带以下 5 块<b>全部</b>字节变化，只有带以上那 1 块不变）。
    /// 所以作废区从带顶一直铺到画布底是<b>必要范围</b>，不是"范围写宽了"：
    /// 判据 <c>align/拖对齐时带以下每一块瓦片的内容确实变了：作废范围已是最小</c>，
    /// 反证在 <c>guard/拖对齐若只丢带区那几块则屏上会留上一帧的像素</c>。
    ///
    /// ═══ 因此"把缓存键换成内容身份（段号＋源矩形）"救不了拖对齐 ═══
    /// 内容每帧都变，键也每帧都变，命中次数照样是 0。真省下来的只有带以上那几块，
    /// 而那几块<b>现在就已经留住</b>（作废区顶边就是带顶）。
    /// </summary>
    /// <returns>被丢掉的块数（0 表示这块区域压根没缓存过）。</returns>
    public int DropRegion(IntRect canvasArea)
    {
        if (canvasArea.IsEmpty || _map.Count == 0) return 0;

        int dropped = 0;
        // 先取快照再删：遍历中改字典会直接抛
        foreach ((int tx, int ty) key in _map.Keys.ToList())
        {
            var tileRect = new IntRect(key.tx * TileRenderer.TileSize, key.ty * TileRenderer.TileSize,
                TileRenderer.TileSize, TileRenderer.TileSize);
            if (tileRect.Intersect(canvasArea).IsEmpty) continue;
            if (_map.Remove(key, out Entry? removed))
            {
                _lru.Remove(key);
                _bytes -= removed.Bytes;
                dropped++;
            }
        }
        RegionDrops += dropped;
        return dropped;
    }

    private void Trim()
    {
        while (_bytes > MemoryLimitBytes && _lru.Last is { } last)
        {
            _lru.RemoveLast();
            if (_map.Remove(last.Value, out Entry? removed))
            {
                _bytes -= removed.Bytes;
                // 这一条与 RegionDrops 是两笔<b>不同</b>的账：一个是"作废范围宽"，一个是"缓存装不下"。
                // 挤掉的块下一帧回来就得重渲染，读数上看着和"被丢"一模一样，只能在这里分开记。
                TrimEvictions++;
            }
        }
    }
}
