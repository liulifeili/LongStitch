using LongStitch.Core.Geometry;
using LongStitch.Core.Imaging;
using LongStitch.Core.Layout;
using LongStitch.Core.Model;

namespace LongStitch.Core;

/// <summary>
/// 画布合成器：把"参数"变成"像素"。这是导出与预览共同的唯一取数入口。
///
/// ═══ 与"预览/导出两套代码"这条红线怎么共存 ═══
/// 红线要防的是"导出复用了带插值/带浮点坐标的显示路径"。本类<b>只做整数搬运</b>：
/// 逐段算出整数源矩形与整数落点，调 <see cref="RgbaImage.Blit(RgbaImage, IntRect, int, int)"/>，不含任何缩放。
/// 缩放只发生在显示层（把本类产出的行带贴到屏幕上时）。
/// 所以"共享取数、分离呈现"既避免了逻辑重复，也保住了红线。
///
/// ═══ 为什么按行带工作 ═══
/// 长截图动辄几万行。一次性合成整张会要 几百 MB 到几 GB，还会让"取消导出"无从下手。
/// 按行带合成后：峰值内存 = 一条带，导出可边算边编码，取消即停。
/// </summary>
public sealed class CanvasComposer
{
    private readonly ISourceResolver _resolver;

    /// <summary>
    /// 匹配输入的临时副本累计字节数（第五十九改第一阶段的 MatchTemporaryBytes 读数）。
    /// <see cref="RenderMatchInput"/> 每次都把"未裁完整段"从源图拷成一份临时像素
    /// （一条 1170×2532 的段就是 ~11.8MB），一次匹配两张输入。这个数只增不减，
    /// 用来回答"2.41GB 里有多少是匹配阶段反复拷出来的"——下一阶段若把"复制匹配区域"
    /// 改成"按区域读原图"，改前改后这个数的增速就是最直接的对照。
    /// 为什么只记这里：<see cref="RenderBand"/> 的行带是"本来就要产出"的成品像素，
    /// 不算临时副本；检测取数（<c>RenderForDetection</c>）另有通路、频率也低得多。
    /// </summary>
    private static long _matchInputBytes;
    public static long MatchInputBytesTotalForTest => System.Threading.Interlocked.Read(ref _matchInputBytes);

    public CanvasComposer(ISourceResolver resolver)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
    }

    /// <summary>
    /// 合成画布坐标系下 <paramref name="band"/> 这一条横带的像素。
    /// <paramref name="band"/> 的宽度不必等于画布宽度（预览瓦片就是窄块），
    /// 落在带外的内容被裁掉，落在带内的像素与整图逐字节一致。
    /// </summary>
    /// <param name="layout">要合成的布局（通常来自 <see cref="Project.Layout"/>）。</param>
    /// <param name="band">画布坐标系下的一条横带（或矩形块），宽高都必须为正。</param>
    /// <param name="revision">调用方读到的 <see cref="Project.Revision"/>。
    /// 仅用于断言"传进来的布局与项目是同一时刻的"，防止异步渲染读到半套参数。</param>
    /// <param name="applyPatches">是否叠加清理补丁。检测类算法要看<b>未清理</b>的原始像素，
    /// 否则第二次运行会在上一次的结果上再检测一遍，误差逐轮累积。</param>
    /// <param name="onlySegmentId">只渲染这一个片段（重叠预览与差异显示用）。
    /// 不给就按正常的层叠顺序画全部片段。</param>
    public RgbaImage RenderBand(CanvasLayout layout, IntRect band, int revision = -1,
        bool applyPatches = true, string? onlySegmentId = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        IntRect b = band.Normalized();
        if (b.Width <= 0 || b.Height <= 0)
            throw new ArgumentException($"行带尺寸非法：{band}", nameof(band));
        _ = revision;   // 目前只作调用点自证用途，不参与计算

        // 新带一律先全透明：尺寸不一致的片段之间补的就是透明（需求 §5.3）
        var output = new RgbaImage(b.Width, b.Height);

        foreach (PlacedSegment p in layout.Intersecting(b))
        {
            if (onlySegmentId is not null && p.Segment.Id != onlySegmentId) continue;
            IntRect overlap = p.Rect.Intersect(b);
            if (overlap.IsEmpty) continue;

            RgbaImage source = _resolver.Resolve(p.Segment.SourceId);
            // 画布坐标 → 源图坐标：先落到片段有效矩形，再叠上片段在源图里的起点
            int srcX = p.SourceRect.X + (overlap.X - p.Origin.X);
            int srcY = p.SourceRect.Y + (overlap.Y - p.Origin.Y);
            output.Blit(source, new IntRect(srcX, srcY, overlap.Width, overlap.Height),
                overlap.X - b.X, overlap.Y - b.Y);

            if (applyPatches)
                foreach (PixelPatch patch in p.Segment.Patches)
                    if (patch.Enabled)
                        ApplyPatch(output, patch, p, b);
        }

        return output;
    }

    /// <summary>合成整张画布。仅供小图、单元测试与"逐片自证"的参照实现使用。</summary>
    public RgbaImage RenderWhole(CanvasLayout layout, int revision = -1)
    {
        // 计数给"UI 线程这趟拖动到底有没有整块合成过"用（第十三轮需求五的 RenderWhole 次数）。
        // 与导出那条通路上的 RunCountForTest 同一手法：读的是增量，不是绝对值。
        System.Threading.Interlocked.Increment(ref _wholeRuns);
        return RenderBand(layout, layout.CanvasBounds, revision);
    }

    private static long _wholeRuns;

    /// <summary>整块合成（一次算完整张画布）被执行过多少次。拖动路径上一次都不该有。</summary>
    public static long WholeRunCountForTest => System.Threading.Interlocked.Read(ref _wholeRuns);

    /// <summary>
    /// <b>检测专用</b>取数：只应用用户裁边与全局裁边，<b>忽略清理写入的补丁</b>。
    ///
    /// 为什么必须单独开这一条：检测算法要回答"原始截图里哪里有滚动条"。
    /// 如果让它读到"上一轮清理已经改过一块"的画面，改动量就会反馈进下一轮的检测结论，
    /// 于是"多点一次应用预览"会再改一层——这是本项目真实踩到过的非幂等缺陷。
    /// 当年同一件事还牵扯"自动裁边"（`Segment.AutoCrop`），由 Checks 的 cleanup 组
    /// 那格「裁剪方式重复运行不叠加」守住；该字段与那格判据已随"裁剪固定带"一起下线
    /// （README「第四十改」），现在幂等这条由 <c>cleanup/重复运行清理是幂等的</c> 单独盯着。
    /// </summary>
    /// <returns>该片段的检测画面；有效区域为空时返回 null。</returns>
    public RgbaImage? RenderForDetection(PlacedSegment placed)
    {
        ArgumentNullException.ThrowIfNull(placed);
        IntRect rect = placed.Segment.EdgeCrop.Apply(placed.GlobalCrop.Apply(placed.Segment.SourceRect));
        if (rect.IsEmpty) return null;

        RgbaImage source = _resolver.Resolve(placed.Segment.SourceId);
        IntRect clipped = rect.Intersect(source.Bounds);
        return clipped.IsEmpty ? null : source.SubImage(clipped);
    }

    /// <summary>
    /// <b>匹配专用</b>取数：取这一段的<b>未裁重叠</b>完整内容（<see cref="PlacedSegment.FullSourceRect"/>），
    /// 再叠上它启用的清理补丁。重叠匹配的两个输入<b>只能</b>从这里来。
    ///
    /// 为什么不能复用 <see cref="RenderBand"/>：裁切档里 <c>placed.Rect</c> 是蓝线切完之后真正进像素的那一块，
    /// 被切掉的那几行<b>已经不在任何布局矩形里</b>——而它们恰恰是两张截图互相重复的那一段，
    /// 也就是匹配器唯一能用来对位的内容。拿切完的两块去匹配，两侧一行公共内容都没有，
    /// 匹配器只能回答"重叠 0"：首分析（重叠还是 0、蓝线还在带顶）碰巧没事，
    /// 「全部重新分析」就整批归零、段与段直接堆在一起（用户第四十四改报的现场）。
    /// 这条取数不进成品合成通路（导出／瓦片仍走 <see cref="RenderBand"/>），
    /// 它只回答"两张原始截图重叠多少"，与 <see cref="RenderDebugSource"/> 那条调试出口也不是一回事：
    /// 那条不叠补丁、只服务屏幕比较视图；这条要叠补丁，因为首分析走 <see cref="RenderBand"/> 时叠的就是补丁，
    /// 重分析必须与首分析看到同一份像素，否则"重新分析"量的是另一道题。
    /// </summary>
    /// <param name="placed">要取内容的那一段。</param>
    /// <returns>与 <see cref="PlacedSegment.FullRect"/> 同尺寸的像素带（被源图边界夹掉多少就少多少）。</returns>
    public RgbaImage RenderMatchInput(PlacedSegment placed)
    {
        ArgumentNullException.ThrowIfNull(placed);
        RgbaImage source = _resolver.Resolve(placed.Segment.SourceId);
        IntRect want = placed.FullSourceRect.Intersect(source.Bounds);
        if (want.IsEmpty)
            throw new ArgumentException($"这一段的完整源矩形落在源图之外：{placed.FullSourceRect}", nameof(placed));

        RgbaImage output = source.SubImage(want);
        System.Threading.Interlocked.Add(ref _matchInputBytes, output.Pixels.Length);
        // 补丁的 Target 相对"有效区域"左上角，ApplyPatch 按画布坐标算交集；
        // 本带在画布里的位置就是 FullRect 按"被源图边界夹掉的量"平移后的那个矩形。
        IntRect band = new(
            placed.FullRect.X + (want.X - placed.FullSourceRect.X),
            placed.FullRect.Y + (want.Y - placed.FullSourceRect.Y),
            want.Width, want.Height);
        foreach (PixelPatch patch in placed.Segment.Patches)
            if (patch.Enabled) ApplyPatch(output, patch, placed, band);
        return output;
    }

    /// <summary>
    /// <b>【仅调试取数】</b>把某片段源图里的一块矩形原样取出来，<b>不经布局、不叠加清理补丁</b>。
    ///
    /// 为什么长截图改用裁切去重之后必须有这条出口：重影/差异层要比的是
    /// <b>被裁掉的那一份</b>，而那些行已经不在任何布局矩形里了（<see cref="RenderBand"/> 永远取不到它们）。
    /// 它只服务屏幕上的比较视图：导出、瓦片、检测三条通路一律不许用它——
    /// 那条承诺由判据 <c>crop/调试取数不进出成品的那条合成通路</c> 守着
    /// （做法是把整张合成与这条取数各跑一次，要求前者一个像素都不是从后者来的）。
    /// </summary>
    /// <returns>该矩形对应的像素；矩形为空或完全落在源图之外时返回 null。</returns>
    public RgbaImage? RenderDebugSource(PlacedSegment placed, IntRect sourceRect)
    {
        ArgumentNullException.ThrowIfNull(placed);
        IntRect want = sourceRect.Normalized();
        if (want.IsEmpty) return null;

        RgbaImage source = _resolver.Resolve(placed.Segment.SourceId);
        IntRect clipped = want.Intersect(source.Bounds);
        if (clipped.IsEmpty) return null;
        return source.SubImage(clipped);
    }

    // ───────────────────────── 补丁 ─────────────────────────

    /// <summary>
    /// 把一个清理补丁画到行带上。<paramref name="patch"/> 的 Target 相对片段有效区域左上角，
    /// 所以先换算到画布坐标，再与行带求交。
    /// </summary>
    private void ApplyPatch(RgbaImage output, PixelPatch patch, PlacedSegment p, IntRect band)
    {
        var inCanvas = new IntRect(p.Origin.X + patch.Target.X, p.Origin.Y + patch.Target.Y,
            patch.Target.Width, patch.Target.Height);
        IntRect overlap = inCanvas.Intersect(band);
        if (overlap.IsEmpty) return;

        int localX = overlap.X - band.X;
        int localY = overlap.Y - band.Y;

        switch (patch.Kind)
        {
            case PatchKind.ExtendColumn:
            {
                // 抹滚动条：把"滚动条左边第一列内容像素"水平延展盖住它。
                // 列坐标是片段有效区域局部的，换算到源图绝对坐标后逐行取一个像素。
                RgbaImage src = _resolver.Resolve(p.Segment.SourceId);
                int srcCol = p.SourceRect.X + patch.ExtendColumnX;
                if (srcCol < 0 || srcCol >= src.Width) break;

                for (int row = 0; row < overlap.Height; row++)
                {
                    int canvasY = overlap.Y + row;
                    int sy = p.SourceRect.Y + (canvasY - p.Origin.Y);
                    if (sy < 0 || sy >= src.Height) continue;
                    RgbaColor c = src.GetPixel(srcCol, sy);
                    Span<byte> line = output.Row(localY + row);
                    for (int x = 0; x < overlap.Width; x++)
                    {
                        int o = (localX + x) * 4;
                        line[o] = c.R; line[o + 1] = c.G; line[o + 2] = c.B; line[o + 3] = c.A;
                    }
                }
                break;
            }
        }
    }
}
