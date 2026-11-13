using System.Runtime.InteropServices;
using LongStitch.Core.Geometry;
using LongStitch.Core.Imaging;
using LongStitch.Core.Imaging.Png;
using LongStitch.Core.Imaging.Tiff;
using LongStitch.Core.Model;
using GdiBitmap = System.Drawing.Bitmap;
using GdiGraphics = System.Drawing.Graphics;
using GdiImagelockMode = System.Drawing.Imaging.ImageLockMode;
using GdiPixelFormat = System.Drawing.Imaging.PixelFormat;

namespace LongStitch.App.Imaging;

/// <summary>解码结果：像素 + 界面要如实告诉用户的那些事实。</summary>
public sealed record DecodedImage(
    RgbaImage Image,
    int ExifOrientation,
    string Format,
    IntSize OriginalSize,
    IReadOnlyList<string> Warnings);

/// <summary>
/// 从磁盘按需解码源图，带 LRU 缓存。
///
/// ═══ 为什么 PNG 不走系统解码器 ═══
/// 本产品对 PNG 承诺"字节级无损"。系统解码器（WIC/GDI+）对 16 位、调色板、
/// 带 ICC 的 PNG 可能做色彩管理或位深转换——肉眼看不出，sha256 一比就红。
/// 所以 PNG 一律走 Core 里自研的 <see cref="PngDecoder"/>，
/// 只有 JPEG/WebP/BMP 这些"本来就无损不了"的格式才交给 GDI+。
///
/// ═══ EXIF 方向 ═══
/// 应用方向是一次<b>无损的像素重排</b>（只改位置，不改值），所以不违反精度红线；
/// 但它会改变宽高，因此必须在导入时就定下来、把"应用方向后的尺寸"记进项目文件，
/// 否则重新打开时布局会整体错位。
/// </summary>
public sealed class FileSourceResolver : ISourceResolver
{
    private readonly Dictionary<string, string> _paths = new();
    private readonly Dictionary<string, Entry> _cache = new();
    private readonly LinkedList<string> _lru = new();
    private long _cachedBytes;

    /// <summary>缓存上限。默认 512 MB ≈ 43 张 1170×2532 的 RGBA；超出按最久未用淘汰。</summary>
    public long CacheLimitBytes { get; set; } = 512L * 1024 * 1024;

    private sealed record Entry(RgbaImage Image, long Bytes);

    /// <summary>登记一张源图的路径。打开项目后必须先全部登记，Resolve 才找得到。</summary>
    public void Register(SourceImage source) => _paths[source.Id] = source.Path;

    /// <summary>
    /// 【仅归因用】<b>这一条线程上</b>经由取数通路发生的首次解码花了多少毫秒、解了几张。
    ///
    /// 为什么要单独开这一笔：真窗口里"取数那一笔偶尔读到 30ms 以上"已经记了几批，
    /// 归因一直停在"一块瓦片内部的冷取数拆不开（含那张源图首次解码）"——
    /// 括号里那半句<b>从来没被量过</b>，只是最可疑的猜测。
    /// 现在能当场分开两种形状：解码撞在 UI 线程上（→ 该挪到后台或预热）vs 纯渲染慢（→ 该缩块）。
    ///
    /// 为什么是 <c>[ThreadStatic]</c> 而不是进程累计：分析、导入都在别的线程上解码，
    /// 进程累计会把<b>别人的</b>解码算进这一帧（那正是"归因写错"的形状）。
    /// 画布只读 UI 线程这一份，跨线程的一律不进帧账。
    /// </summary>
    [ThreadStatic] private static double _decodeMsHere;
    [ThreadStatic] private static int _decodeRunsHere;
    public static double DecodeMsOnCurrentThread => _decodeMsHere;
    public static int DecodeRunsOnCurrentThread => _decodeRunsHere;

    private static long _decodeMsAll, _decodeRunsAll;
    public static long DecodeMsTotalForTest => System.Threading.Interlocked.Read(ref _decodeMsAll);
    public static long DecodeRunsTotalForTest => System.Threading.Interlocked.Read(ref _decodeRunsAll);

    /// <summary>
    /// 【第五十九改②口径修正】全部解码调用数（进程累计）——<b>含导入时直接调用的那批</b>。
    /// 之前的 <c>DecodeRunsTotalForTest</c> 只数经由 <see cref="Resolve"/> 的解码，
    /// 导入循环里直接调 <see cref="Decode"/> 的那一批根本不进账，"导入了 N 张实际解了几遍"
    /// 这个问题它答不了。本批把两类都数上，判据
    /// <c>ui/导入 N 张 PNG 全程只解码 N 次（预热缓存与重新解码逐字节同源）</c> 从这里取数。
    /// </summary>
    private static long _decodeCalls;
    public static long DecodeCallsTotalForTest => System.Threading.Interlocked.Read(ref _decodeCalls);

    /// <summary>
    /// 【第五十九改②口径修正】解码并发峰值（进程启动以来最高"同时在飞"的解码次数）。
    ///
    /// 为什么从"瞬时在途数"改成"峰值"：快照是在操作<b>结束之后</b>记的，
    /// 瞬时值那种读法在快照里恒为 0——一个永远读 0 的读数没有任何信息量，
    /// 还容易被误读成"缓冲区数量/字节数"（它都不是，它只数调用）。
    /// 峰值至少能回答"解码有没有并发到值得警惕"这一问；字节数那一头
    /// 要等解码器按流式重写时才有东西可记（第五十九改⑤的事，本批不动解码器）。
    /// </summary>
    private static int _decodeInFlight;
    private static int _decodePeakInFlight;
    public static int DecodePeakInFlight => System.Threading.Volatile.Read(ref _decodePeakInFlight);

    /// <summary>
    /// 【第五十九改③】把导入时已经解码出来的像素直接放进缓存——所有权移交给本类。
    ///
    /// 为什么必须有这一步：之前导入循环里 <c>Decode(file)</c> 只是为了拿尺寸/方向/格式这些
    /// 元数据，<b>整幅像素当场丢弃</b>；等画布首屏、缩略图、分析各自 <see cref="Resolve"/> 时，
    /// 同一个文件又从头解码一遍——一张 PNG 两遍解码、峰值翻倍，这正是内存盘点里
    /// 瞬时大分配的第一笔。现在导入解码一次，像素经本方法进 LRU（进 <see cref="CacheLimitBytes"/>
    /// 预算、可被淘汰、仍是"可丢弃副本"），之后的取数全走缓存命中。
    /// 像素内容不变（就是同一次解码的产物），匹配/布局/导出读到的与"第二遍重新解码"逐字节相同。
    /// </summary>
    public void Prime(string sourceId, RgbaImage image)
    {
        if (_cache.ContainsKey(sourceId)) return;
        long bytes = image.Pixels.Length;
        _cache[sourceId] = new Entry(image, bytes);
        _lru.AddFirst(sourceId);
        _cachedBytes += bytes;
        Trim();
    }

    public void Forget(string sourceId)
    {
        _paths.Remove(sourceId);
        Invalidate(sourceId);
    }

    public RgbaImage Resolve(string sourceId)
    {
        if (_cache.TryGetValue(sourceId, out Entry? hit))
        {
            Touch(sourceId);
            return hit.Image;
        }

        if (!_paths.TryGetValue(sourceId, out string? path))
            throw new FileNotFoundException($"项目里没有 Id={sourceId} 的源图记录");
        if (!File.Exists(path))
            throw new FileNotFoundException($"源文件已丢失：{path}。请在左侧列表里重新定位。");

        // 只夹这一次 Decode：读文件与缓存写入都在里面，因为它们就是"首次拿到这张图"的真实代价。
        long decodeStart = System.Diagnostics.Stopwatch.GetTimestamp();
        DecodedImage decoded = Decode(path);
        double decodeMs = (System.Diagnostics.Stopwatch.GetTimestamp() - decodeStart) * 1000.0
                          / System.Diagnostics.Stopwatch.Frequency;
        _decodeMsHere += decodeMs;
        _decodeRunsHere++;
        System.Threading.Interlocked.Add(ref _decodeMsAll, (long)System.Math.Round(decodeMs));
        System.Threading.Interlocked.Increment(ref _decodeRunsAll);

        long bytes = decoded.Image.Pixels.Length;
        _cache[sourceId] = new Entry(decoded.Image, bytes);
        _lru.AddFirst(sourceId);
        _cachedBytes += bytes;
        Trim();
        return decoded.Image;
    }

    public bool TryResolve(string sourceId, out RgbaImage? image, out string? error)
    {
        try { image = Resolve(sourceId); error = null; return true; }
        catch (Exception ex) when (ex is IOException or FileNotFoundException or PngException
                                      or OutOfMemoryException or ArgumentException)
        { image = null; error = ex.Message; return false; }
    }

    public void Invalidate(string? sourceId = null)
    {
        if (sourceId is null)
        {
            _cache.Clear();
            _lru.Clear();
            _cachedBytes = 0;
            return;
        }
        if (_cache.TryGetValue(sourceId, out Entry? e))
        {
            _cache.Remove(sourceId);
            _cachedBytes -= e.Bytes;
            _lru.Remove(sourceId);
        }
    }

    /// <summary>当前缓存了多少 MB，状态栏/诊断用。</summary>
    public long CachedBytes => _cachedBytes;

    /// <summary>当前驻留几张源图（内存快照的 SourceCacheCount 读数）。</summary>
    public int CachedCount => _cache.Count;

    /// <summary>
    /// 当前驻留的源图 Id，按"最近使用在前"排序。
    /// 诊断界面与判据都读它：LRU 是不是真的在挪头，从缓存字节数看不出来，必须看顺序。
    /// </summary>
    public IReadOnlyList<string> CachedIdsNewestFirst => _lru.ToArray();

    /// <summary>缓存命中时把该源挪到 LRU 头部。不做这一步的话，
    /// 长图来回滚动会反复淘汰"其实正在用"的图，每次都重新解码。</summary>
    private void Touch(string sourceId)
    {
        if (_lru.First?.Value == sourceId) return;   // 已在头部，LinkedList 挪动不是白给的
        _lru.Remove(sourceId);
        _lru.AddFirst(sourceId);
    }

    private void Trim()
    {
        while (_cachedBytes > CacheLimitBytes && _lru.Last is { } last)
        {
            _lru.RemoveLast();
            if (_cache.Remove(last.Value, out Entry? removed))
                _cachedBytes -= removed.Bytes;
        }
    }

    // ───────────────────────── 解码 ─────────────────────────

    /// <summary>
    /// 解码一个图片文件为 RGBA8888（已应用 EXIF 方向）。
    /// 失败时抛带可读原因的异常，由导入流程逐项收集。
    /// </summary>
    public static DecodedImage Decode(string path)
    {
        System.Threading.Interlocked.Increment(ref _decodeCalls);
        // 峰值用 CAS 收敛更新：读到旧峰值 → 更大就换上新值 → 有人抢先就重读再试。
        int now = System.Threading.Interlocked.Increment(ref _decodeInFlight);
        int seen = System.Threading.Volatile.Read(ref _decodePeakInFlight);
        while (now > seen &&
               System.Threading.Interlocked.CompareExchange(ref _decodePeakInFlight, now, seen) != seen)
        {
            seen = System.Threading.Volatile.Read(ref _decodePeakInFlight);
        }
        try { return DecodeCore(path); }
        finally { System.Threading.Interlocked.Decrement(ref _decodeInFlight); }
    }

    /// <summary><see cref="Decode"/> 的实体，签名与行为与拆分前逐字节一致（第五十九改只加计数不改解码）。</summary>
    private static DecodedImage DecodeCore(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        string ext = Path.GetExtension(path).ToLowerInvariant();

        // PNG：优先按内容判断（扩展名可能骗人），走自研解码器
        if (PngDecoder.LooksLikePng(bytes))
        {
            PngDecodeResult png = PngDecoder.Decode(bytes);
            var warnings = new List<string>(png.Info.LossyNotes);

            // PNG 也能带 eXIf，方向必须应用；不应用的话横屏截图会整条拼缝坐标全错
            int orientation = png.Info.ExifOrientation;
            RgbaImage image = ApplyOrientation(png.Image, orientation);
            return new DecodedImage(image, orientation, "png", png.Image.Size, warnings);
        }

        // TIFF：先走本程序的自研读取器（小端、未压缩、条带布局）。
        // 理由与 PNG 一样——系统解码器对 alpha 与位深的处理不可控，
        // 而这条通路可以做到"像素逐字节保留"。读不动的变体（大端/压缩/平铺）才退回 GDI+。
        if (TiffDecoder.LooksLikeTiff(bytes) && bytes[0] == (byte)'I' &&
            TiffDecoder.TryDecode(bytes, out TiffDecodeResult? tiff, out _) && tiff is not null)
        {
            return new DecodedImage(tiff.Image, 1,
                string.IsNullOrEmpty(ext) ? "tiff" : ext.TrimStart('.'),
                tiff.Image.Size,
                new List<string>
                {
                    $"TIFF 由本程序自研读取器解码（{tiff.SamplesPerPixel} 样本、{tiff.StripCount} 条带），" +
                    "像素逐字节保留，未经系统解码器的色彩处理"
                });
        }

        // 其余格式交给 GDI+（JPEG / BMP / GIF / 大文件或压缩变体的 TIFF / 部分 WebP）
        return DecodeWithGdiPlus(bytes, ext);
    }

    private static DecodedImage DecodeWithGdiPlus(byte[] bytes, string ext)
    {
        using var ms = new MemoryStream(bytes);
        using var bmp = new GdiBitmap(ms);

        int orientation = ReadExifOrientation(bmp);
        var warnings = new List<string>();

        // 只接受 24/32 位作为"可直接搬运"的输入；其余（8 位索引、48 位、灰度）
        // 都要经过 GDI+ 的格式转换，属于不可控改动，必须说出来而不是假装无损。
        if (bmp.PixelFormat is not (GdiPixelFormat.Format24bppRgb or GdiPixelFormat.Format32bppArgb
                or GdiPixelFormat.Format32bppPArgb))
        {
            warnings.Add($"源图为 {bmp.PixelFormat}，已由系统解码器转换为 8 位 RGB（可能有细微差异）");
        }
        if (ext is ".webp" or ".tif" or ".tiff")
            warnings.Add($"{ext} 的解码依赖本机系统组件，颜色与 alpha 处理不保证与原始文件逐字节一致");

        var rotated = FromGdiBitmap(bmp, out IntSize rawSize);
        RgbaImage image = ApplyOrientation(rotated, orientation);
        if (orientation != 1)
            warnings.Add($"已按 EXIF Orientation={orientation} 旋转/翻转（无损重排，尺寸随之改变）");

        return new DecodedImage(image, orientation, ext.TrimStart('.'), rawSize, warnings);
    }

    /// <summary>GDI+ 位图 → RgbaImage。逐行拷贝，BGRA 与 RGBA 的字节顺序在行内交换。</summary>
    public static RgbaImage FromGdiBitmap(GdiBitmap bmp, out IntSize rawSize)
    {
        rawSize = new IntSize(bmp.Width, bmp.Height);
        var rect = new System.Drawing.Rectangle(0, 0, bmp.Width, bmp.Height);
        var data = bmp.LockBits(rect, GdiImagelockMode.ReadOnly, GdiPixelFormat.Format32bppArgb);
        try
        {
            int stride = data.Stride;
            byte[] buffer = new byte[stride * bmp.Height];
            Marshal.Copy(data.Scan0, buffer, 0, buffer.Length);

            var image = new RgbaImage(bmp.Width, bmp.Height);
            for (int y = 0; y < bmp.Height; y++)
            {
                Span<byte> dst = image.Row(y);
                int rowStart = y * stride;
                for (int x = 0; x < bmp.Width; x++)
                {
                    int s = rowStart + x * 4;
                    dst[x * 4] = buffer[s + 2];      // R ← BGRA 的第 3 字节
                    dst[x * 4 + 1] = buffer[s + 1];  // G
                    dst[x * 4 + 2] = buffer[s];      // B
                    dst[x * 4 + 3] = buffer[s + 3];  // A
                }
            }
            return image;
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }

    private static int ReadExifOrientation(GdiBitmap bmp)
    {
        const int OrientationTagId = 0x0112;
        try
        {
            // GDI+ 的 GetPropertyItem 在属性不存在时是抛 ArgumentException；它的可空标注不完整，
            // 所以这里先查 PropertyIdList 再取，取到后用 ! 明确"我们自己确认过非空"。
            if (!bmp.PropertyIdList.Contains(OrientationTagId)) return 1;
            var item = bmp.GetPropertyItem(OrientationTagId);
            byte[] value = item!.Value ?? Array.Empty<byte>();
            return value.Length > 0 && value[0] is >= 1 and <= 8 ? value[0] : 1;
        }
        catch (ArgumentException)
        {
            return 1;   // 没有该属性：PNG 与多数截图都属于这种
        }
    }

    /// <summary>
    /// 按 EXIF Orientation（1–8）做无损重排。
    /// 只做水平/垂直镜像与 90° 的整数倍旋转——没有插值，所以不违反精度红线。
    /// </summary>
    public static RgbaImage ApplyOrientation(RgbaImage src, int orientation)
    {
        if (orientation is 0 or 1) return src;

        bool swap = orientation is 5 or 6 or 7 or 8;
        int w = swap ? src.Height : src.Width;
        int h = swap ? src.Width : src.Height;
        var dst = new RgbaImage(w, h);

        for (int y = 0; y < src.Height; y++)
        {
            ReadOnlySpan<byte> row = src.Row(y);
            for (int x = 0; x < src.Width; x++)
            {
                // 8 种取向各自的 (x,y) → (tx,ty) 映射。写成一处的映射表，
                // 免得界面、导出、缩略图三处各算一遍算出三种结果。
                (int tx, int ty) = orientation switch
                {
                    2 => (src.Width - 1 - x, y),
                    3 => (src.Width - 1 - x, src.Height - 1 - y),
                    4 => (x, src.Height - 1 - y),
                    5 => (y, x),
                    // 6 = "要把存着的画面<b>顺时针</b>转 90° 才正"，8 = 逆时针 90°。
                    // 之前这两条写反了（6 走的是逆时针公式）：横屏拍的 iPhone 照片
                    // 会被转成上下颠倒，而取向 1–5 全对，所以肉眼很容易只当成"个别机型不对"。
                    // 判据 <c>png/EXIF 取向 1–8 的重排逐个对上独立复算</c> 把它抓了出来。
                    6 => (src.Height - 1 - y, x),
                    7 => (src.Height - 1 - y, src.Width - 1 - x),
                    8 => (y, src.Width - 1 - x),
                    _ => (x, y),
                };
                int s = x * 4;
                dst.SetPixel(tx, ty, new RgbaColor(row[s], row[s + 1], row[s + 2], row[s + 3]));
            }
        }
        return dst;
    }

    /// <summary>生成缩略图用的低清位图。<b>只给界面看，永不参与导出</b>。</summary>
    public static System.Drawing.Image? MakeThumbnail(RgbaImage image, int maxSide)
    {
        try
        {
            using var full = ToBitmap(image);
            int scale = Math.Max(1, (int)Math.Ceiling(Math.Max(full.Width, full.Height) / (double)maxSide));
            int w = Math.Max(1, full.Width / scale);
            int h = Math.Max(1, full.Height / scale);
            var thumb = new GdiBitmap(w, h);
            using (var g = GdiGraphics.FromImage(thumb))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                g.DrawImage(full, new System.Drawing.Rectangle(0, 0, w, h),
                    new System.Drawing.Rectangle(0, 0, full.Width, full.Height),
                    System.Drawing.GraphicsUnit.Pixel);
            }
            return thumb;
        }
        catch
        {
            return null;   // 缩略图失败不该阻断导入，画布上会显示占位块
        }
    }

    /// <summary>
    /// RgbaImage → GDI+ 位图（<b>只给显示层贴图用，永不参与导出</b>）。注意 GDI+ 的内存序是 BGRA。
    ///
    /// ═══ 第十四轮第十二改：这一段的写法换了，因为第十一改把它的代价量了出来 ═══
    /// 真窗口 HUD 关闭 35% 那一档，逐趟最慢帧的分阶段报「贴图 12.4／12.2／14.1／13.1ms」而其中
    /// <b>首次转换就占了 12.4／9.6／10.8／13.1ms（6～11 块）</b>——也就是贴图那一笔几乎全是转换，
    /// 重采样（每帧必做的那半）只有 0～3ms。
    /// 这直接否证了本批原本打算做的那个修法（冷帧走便宜插值档）：便宜档省的是重采样那 0～3ms，
    /// 动不到 12ms 的转换。所以这一批动的是转换本身，两处：
    /// <list type="number">
    ///   <item>老写法每次调用都新分配一整块 <c>stride×height</c> 的临时缓冲（512×512 就是 1MB），
    ///         直接落进大对象堆；一屏十几块就是十几 MB 的分配，回收的钱还会落在"正在被计时的那一段"里。
    ///         现在按行分配（512 像素一行＝int[] 与 byte[] 各 2KB）再逐行拷进锁定的位图内存。
    ///         搬运的总字节数没变（还是填一遍、拷一遍），变的是<b>每块不再压 1MB 托管分配</b>；
    ///         这一笔由 <c>canvas/首次转换的分配量必须按行走而不是整块</c> 钉住，
    ///         它只数<b>托管</b>这一侧——位图那 1MB 像素在 GDI 非托管堆上，本轮没动它；</item>
    ///   <item>换序从<b>逐字节</b>（每像素 4 次读 + 4 次写，各带一遍界检查）改成<b>逐像素 32 位</b>：
    ///         一个像素一次读、一次移位或、一次写，行内边界检查由 JIT 消掉。</item>
    /// </list>
    /// 出来的字节必须与老写法<b>逐字节相同</b>——这条由判据
    /// <c>canvas/首次转换出来的 BGRA 必须与独立复算逐字节相同</c> 钉住（复算写在测试里，不抄产品实现），
    /// 反证 <c>guard/红蓝换序写反必须被逐字节判据抓到</c> 保证那格有牙。
    /// 分配形状那一笔的反证另在 <c>guard/分配量那把尺必须能量出整块写法</c>——它拿本批之前那种
    /// 整块缓冲的写法在同一把尺下重跑一次，量出来必须<b>超线</b>，否则那条限值就是给产品量身定做的宽松数。
    /// JPEG 编码器里另有一份同样的慢写法（<c>GdiplusJpegEncoder.ToBitmap</c>），那一支在<b>导出通路</b>上，
    /// 这一批没动它：要动得另开一批、连着逐行比对一起走。
    /// </summary>
    public static GdiBitmap ToBitmap(RgbaImage image)
    {
        var bmp = new GdiBitmap(image.Width, image.Height, GdiPixelFormat.Format32bppArgb);
        var rect = new System.Drawing.Rectangle(0, 0, image.Width, image.Height);
        var data = bmp.LockBits(rect, GdiImagelockMode.WriteOnly, GdiPixelFormat.Format32bppArgb);
        try
        {
            int stride = data.Stride;
            // 每块瓦片只按「一行」分配：一行 512 像素 = 2KB，两次调用之间不重复分配。
            // 为什么中间要过一道 byte[]：Marshal.Copy 只收 byte[]（不收 int[]），
            // 而逐字节写进 byte[] 又正是本次要换掉的那种写法。所以换序在 int[] 里一次一个像素做完，
            // 再用一次 Buffer.BlockCopy 把这 2KB 原样映进字节视图——两道都是 memcpy 级，不是逐像素。
            int[] row = new int[image.Width];
            byte[] rowBytes = new byte[image.Width * 4];
            for (int y = 0; y < image.Height; y++)
            {
                // RgbaImage 一行是 RGBA 四字节一像素；按 32 位重读它就是一像素一个数（小端：R 在最低位）。
                ReadOnlySpan<uint> src = MemoryMarshal.Cast<byte, uint>(image.Row(y));
                for (int x = 0; x < image.Width; x++)
                {
                    uint v = src[x];
                    // 0xAABBGGRR ← 0xAARRGGBB：红蓝互换，绿与 alpha 原地不动。
                    row[x] = (int)((v & 0xFF00_0000u) | ((v & 0xFFu) << 16)
                                                  | (v & 0x0000_FF00u) | ((v >> 16) & 0xFFu));
                }
                Buffer.BlockCopy(row, 0, rowBytes, 0, rowBytes.Length);
                Marshal.Copy(rowBytes, 0, (IntPtr)(data.Scan0.ToInt64() + (long)y * stride), rowBytes.Length);
            }
        }
        finally
        {
            bmp.UnlockBits(data);
        }
        return bmp;
    }
}
