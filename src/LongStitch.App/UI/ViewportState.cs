using LongStitch.Core.Geometry;

namespace LongStitch.App.UI;

/// <summary>
/// 画布视口：只管"看哪里、看多大"，不管拼合结果。
///
/// 三条不变量（对应模拟 UI 需求 §4）：
///   1. <see cref="Zoom"/> 与 <see cref="TopLeft"/> 是<b>纯显示量</b>。
///      缩放、平移、适应窗口都不许写进项目参数——验收点就是"Shift 拖动与缩放之后
///      右侧 X/Y 数值完全不变"。
///   2. 视口坐标一律用设备像素（不是 WinForms 的逻辑像素），
///      这样贴图时不需要再乘 DPR，少一次换算就少一处可能差半像素的地方。
///   3. 画布坐标 ↔ 设备像素的换算里，画布侧永远是整数（<see cref="IntPoint"/>），
///      浮点只出现在"除以缩放比"这一步，且只在显示方向上使用。
/// </summary>
public sealed class ViewportState
{
    public const double MinZoom = 0.10;
    public const double MaxZoom = 8.00;

    private Size _deviceSize;

    /// <summary>显示缩放比。1.0 = 一个源图像素占一个屏幕像素。</summary>
    public double Zoom { get; private set; } = 0.30;

    /// <summary>视口左上角对应的画布坐标（源图像素，整数）。</summary>
    public IntPoint TopLeft { get; private set; } = IntPoint.Zero;

    /// <summary>视口或缩放变化。界面用它刷新状态栏与瓦片。</summary>
    public event Action? Changed;

    public Size DeviceSize => _deviceSize;

    public void SetDeviceSize(Size deviceSize)
    {
        if (deviceSize == _deviceSize) return;
        _deviceSize = deviceSize;
        Raise();
    }

    // ───────────────────────── 坐标换算 ─────────────────────────

    /// <summary>设备像素 → 画布坐标。向下取整：一个屏幕像素覆盖的是一整块源像素。</summary>
    public IntPoint DeviceToCanvas(Point device) =>
        new(TopLeft.X + (int)Math.Floor(device.X / Zoom),
            TopLeft.Y + (int)Math.Floor(device.Y / Zoom));

    /// <summary>画布坐标 → 设备像素（浮点，只用于画线；不反过来参与拼合计算）。</summary>
    public PointF CanvasToDeviceF(IntPoint canvas) =>
        new((float)((canvas.X - TopLeft.X) * Zoom), (float)((canvas.Y - TopLeft.Y) * Zoom));

    /// <summary>画布矩形 → 设备像素矩形（向外取整，保证边缘不被切掉半个像素）。</summary>
    public Rectangle CanvasToDevice(IntRect rect)
    {
        PointF a = CanvasToDeviceF(rect.Location);
        PointF b = CanvasToDeviceF(new IntPoint(rect.Right, rect.Bottom));
        int x = (int)Math.Floor(a.X), y = (int)Math.Floor(a.Y);
        return new Rectangle(x, y,
            Math.Max(1, (int)Math.Ceiling(b.X) - x),
            Math.Max(1, (int)Math.Ceiling(b.Y) - y));
    }

    /// <summary>当前可见的画布区域（整数，含边缘余量）。</summary>
    public IntRect VisibleCanvas(int marginCanvasPx = 0)
    {
        if (_deviceSize.Width <= 0 || _deviceSize.Height <= 0) return IntRect.Empty;
        IntPoint tl = DeviceToCanvas(new Point(-marginCanvasPx, -marginCanvasPx));
        IntPoint br = DeviceToCanvas(new Point(_deviceSize.Width + marginCanvasPx, _deviceSize.Height + marginCanvasPx));
        return IntRect.FromLTRB(tl.X, tl.Y, br.X, br.Y);
    }

    // ───────────────────────── 操作 ─────────────────────────

    /// <summary>按设备像素平移视口。拖动方向与画面移动方向一致（往右下拖，内容跟手往右下走）。</summary>
    public void PanByDevice(IntPoint deviceDelta)
    {
        if (deviceDelta == IntPoint.Zero) return;
        TopLeft = new IntPoint(
            TopLeft.X - (int)Math.Round(deviceDelta.X / Zoom),
            TopLeft.Y - (int)Math.Round(deviceDelta.Y / Zoom));
        Raise();
    }

    /// <summary>
    /// 把视口原点<b>绝对</b>设到指定画布坐标。
    /// 为什么要有这条而不是让调用方反复 <see cref="PanBySource"/>：
    /// "拖动拼缝时把不动的那一侧钉在屏幕上"这类补偿，每帧都要从<b>同一个基准</b>算一次，
    /// 用增量累加会把每次的取整误差攒起来（拖一整趟回来发现画面漂了几像素）。
    /// 纯显示量：不许借此改动任何项目参数。
    /// </summary>
    public void MoveTopLeftTo(IntPoint canvasTopLeft)
    {
        if (canvasTopLeft == TopLeft) return;
        TopLeft = canvasTopLeft;
        Raise();
    }

    /// <summary>
    /// 以某个设备像素位置为中心缩放（需求 §4：<c>Ctrl + 滚轮</c> 以指针所在位置为中心）。
    /// 关键是"指针下的那个源像素，缩放前后必须还落在同一个屏幕像素上"，
    /// 否则每次缩放画面都会漂，用户没法连续放大同一处接缝。
    /// </summary>
    public void ZoomAt(double newZoom, Point anchorDevice)
    {
        newZoom = Math.Clamp(newZoom, MinZoom, MaxZoom);
        if (Math.Abs(newZoom - Zoom) < 1e-9) return;

        // 先在旧缩放下算出锚点对应的"精确画布位置"（保留小数，避免累积漂移），
        // 再解出新 TopLeft 使该位置仍落在同一锚点上。
        double anchorCanvasX = TopLeft.X + anchorDevice.X / Zoom;
        double anchorCanvasY = TopLeft.Y + anchorDevice.Y / Zoom;

        Zoom = newZoom;
        TopLeft = new IntPoint(
            (int)Math.Round(anchorCanvasX - anchorDevice.X / Zoom),
            (int)Math.Round(anchorCanvasY - anchorDevice.Y / Zoom));
        Raise();
    }

    public void SetZoom(double newZoom) => ZoomAt(newZoom,
        new Point(_deviceSize.Width / 2, _deviceSize.Height / 2));

    /// <summary>适应宽度：留 1 像素边距，按内容宽度算缩放比。</summary>
    public void FitWidth(IntSize content)
    {
        if (content.Width <= 0 || _deviceSize.Width <= 0) return;
        TopLeft = new IntPoint(0, TopLeft.Y);
        Zoom = Math.Clamp((_deviceSize.Width - 2) / (double)content.Width, MinZoom, MaxZoom);
        // 缩放比变了以后重新居中（用户第七条）：这一档内容横向刚好铺满，居中只会挪半个边距，
        // 纵向装不下 ⇒ RecentreIfFits 不动它，用户原来看到的那一行还在那一行。
        RecentreIfFits(content);
        Raise();
    }

    /// <summary>适应整个结果。</summary>
    public void FitWhole(IntSize content)
    {
        if (content.IsEmpty || _deviceSize.Width <= 0 || _deviceSize.Height <= 0) return;
        double fit = Math.Min(
            (_deviceSize.Width - 2) / (double)content.Width,
            (_deviceSize.Height - 2) / (double)content.Height);
        Zoom = Math.Clamp(fit, MinZoom, MaxZoom);
        TopLeft = IntPoint.Zero;
        // 两轴都装得下 ⇒ 这一档必然被居中：整条结果停在窗口正中，而不是贴着左上角。
        RecentreIfFits(content);
        Raise();
    }

    /// <summary>
    /// 内容在当前缩放下"摆正"后的视口原点，逐轴各算各的：
    /// 装得下的那一轴返回<b>负数或 0</b>（两侧留白相等，所以内容中心 == 视口中心）；
    /// 装不下的那一轴返回 0（从第 0 行/列开始）——长截图有几十万行，纵向居中会让人一进来就落在中间。
    /// 算式与 <see cref="CenterOn"/> 同源（内容中心减去视口跨度的一半），差别只在这里用<b>整条内容</b>的中心。
    /// </summary>
    public IntPoint CentreOriginFor(IntSize content) => new(
        Math.Min(0, (int)Math.Round(content.Width / 2.0 - _deviceSize.Width / Zoom / 2.0)),
        Math.Min(0, (int)Math.Round(content.Height / 2.0 - _deviceSize.Height / Zoom / 2.0)));

    /// <summary>
    /// 只在"内容装得下"的那些轴上重新居中；装不下的那一轴<b>保持用户此刻的位置</b>。
    /// 这是全项目唯一的居中入口，三个时机都走它：窗口尺寸变了、导入完了一批、分析写回那一下
    /// （用户第七条「无论窗口多大都得保持居中显示，你每次都放在左对齐」）。
    /// 为什么不是无条件两轴居中：长截图纵向有几十万行，用户可能正看着第 8 万行，
    /// 把他拽回中间或顶部都是丢工作；横向那条窄内容则必须永远跟着窗口居中。
    /// 纯显示量：不许借此改动任何项目参数。
    /// </summary>
    public void RecentreIfFits(IntSize content)
    {
        if (content.Width <= 0 || content.Height <= 0) return;
        if (_deviceSize.Width <= 0 || _deviceSize.Height <= 0) return;
        IntPoint fit = CentreOriginFor(content);
        // "装得下"当场判，不靠"居中原点是不是 0"来反推：内容刚好铺满那一轴时居中原点也是 0，
        // 两种情形数值相同、语义相反，靠数值区分迟早会把用户的行号拽走。
        bool fitsX = content.Width * Zoom <= _deviceSize.Width;
        bool fitsY = content.Height * Zoom <= _deviceSize.Height;
        var target = new IntPoint(fitsX ? fit.X : TopLeft.X, fitsY ? fit.Y : TopLeft.Y);
        if (target == TopLeft) return;
        TopLeft = target;
        Raise();
    }

    /// <summary>实际像素 100%。</summary>
    public void SetOneToOne(Point? anchor = null) =>
        ZoomAt(1.0, anchor ?? new Point(_deviceSize.Width / 2, _deviceSize.Height / 2));

    /// <summary>把某个画布点滚到视口中央（点拼缝列表项跳转用）。</summary>
    public void CenterOn(IntPoint canvas)
    {
        TopLeft = new IntPoint(
            (int)Math.Round(canvas.X - _deviceSize.Width / Zoom / 2),
            (int)Math.Round(canvas.Y - _deviceSize.Height / Zoom / 2));
        Raise();
    }

    /// <summary>把某段画布区域纳入视野（不改变缩放）。</summary>
    public void EnsureVisible(IntRect rect)
    {
        IntRect view = VisibleCanvas();
        int dx = 0, dy = 0;
        if (rect.Bottom > view.Bottom) dy = rect.Bottom - view.Bottom + 8;
        if (rect.Y < view.Y) dy = rect.Y - view.Y - 8;
        if (rect.Right > view.Right) dx = rect.Right - view.Right + 8;
        if (rect.X < view.X) dx = rect.X - view.X - 8;
        if (dx != 0 || dy != 0)
        {
            TopLeft = TopLeft + new IntPoint(dx, dy);
            Raise();
        }
    }

    private void Raise() => Changed?.Invoke();
}
