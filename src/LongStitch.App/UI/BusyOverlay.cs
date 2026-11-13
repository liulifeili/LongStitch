using System.Drawing.Imaging;

namespace LongStitch.App.UI;

/// <summary>
/// 导入图片与分析拼缝期间盖在画布上的那一层：<b>把画布虚化</b>，中间摆一句说明与一条进度条。
///
/// ═══ 为什么要有这一层 ═══
/// 导入几十张连拍与随后那趟自动匹配都要几秒钟，期间画布上的内容是<b>半成品</b>：
/// 图一张一张冒出来、拼缝一条一条变绿。用户看得见却动不了（后台在跑），
/// 只会怀疑"是不是卡死了"。虚化的作用就是把"现在屏上这些还不是结果"这件事一眼说清，
/// 进度条则回答"还要多久"。
///
/// ═══ 三条硬约束 ═══
/// ① <b>绝不进成品像素</b>。它是画布的<b>兄弟控件</b>，不是画布的一层叠图：
///    画布自己的 <c>OnPaint</c> 一个字都不知道它存在，瓦片、导出、逐字节判据全都不受影响
///    （精度红线三"导出绝不从屏幕取图"在这一层同样成立——它连画布都不读，
///    只在<b>显示</b>的那一刻拿一帧已经画好的位图来做模糊底）。
/// ② <b>虚化必须是真虚化</b>。第一版很容易写成"盖一层半透明黑"就交差，
///    那在屏上与"变暗"没有区别，用户还是能看清半成品内容。所以这里做真的箱式模糊，
///    并把"模糊前后差了多少"作为读数交出去（<see cref="BlurDeltaForTest"/>）：
///    谁把它换成直接贴原图，那个读数就归零，判据当场红。
/// ③ <b>零 NuGet、零 P/Invoke</b>。模糊只用 <c>Bitmap.LockBits</c> 与整数算术，
///    不引任何外部库，也不调任何 Win32。
/// </summary>
public sealed class BusyOverlay : Control
{
    /// <summary>虚化前的取样缩放倍率：先把画布那一帧缩到 1/4，再在小图上做模糊。</summary>
    private const int Downscale = 4;

    /// <summary>小图上的箱式模糊半径（换算回屏幕约等于 8 个像素的糊度）。</summary>
    private const int BlurRadius = 2;

    /// <summary>模糊底（已缩小、已模糊）。显示期间一直复用，不每帧重算。</summary>
    private Bitmap? _blurred;

    /// <summary>压在那层模糊之上的暗纱透明度：太淡看不清进度条，太浓就成了一堵墙。</summary>
    private const int ScrimAlpha = 120;

    /// <summary>
    /// 边框笔。提静态：这一层每帧要描两笔，没必要每次都新建一支 GDI 笔再销毁。
    /// 第四十八改起换肤时就地改它的 Color（见 <see cref="OnThemeChanged"/>），对象仍是同一枚。
    /// </summary>
    private static readonly Pen BorderPen = new(Theme.PanelBorder, 1f);

    private string _title = "";
    private int _done;
    private int _total;

    public BusyOverlay()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                 | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.CanvasBackground;
        TabStop = false;
        Visible = false;
        BorderPen.Color = Theme.PanelBorder;
        Theme.Changed += OnThemeChanged;
    }

    /// <summary>
    /// 换肤：边框笔就地改色，模糊底作废重算。
    /// 模糊底是<b>已经烤进位图</b>的一帧画布，画布换了底色它不会自己跟着变；
    /// 不作废的话，白天档下导入时盖着的还是上一档那张深色底。
    /// </summary>
    private void OnThemeChanged(ThemePalette old, ThemePalette current)
    {
        BorderPen.Color = Theme.PanelBorder;
        _blurred?.Dispose();
        _blurred = null;
        Invalidate();
    }

    /// <summary>这一层现在盖着的是哪一句话（判据读它，证明"进度条不是摆着好看的"）。</summary>
    public string TitleForTest => _title;

    /// <summary>已完成数。</summary>
    public int DoneForTest => _done;

    /// <summary>总数（0 表示这一趟报不出总数，进度条画成"走着"的样子）。</summary>
    public int TotalForTest => _total;

    /// <summary>模糊底有没有真的做出来（画布尺寸为 0 时做不出来，此时只压暗纱）。</summary>
    public bool HasBlurForTest => _blurred is not null;

    /// <summary>
    /// 模糊底与"直接缩小不做模糊"那一版之间的平均绝对差（0–255）。
    ///
    /// 这一格是<b>反证</b>用的：把模糊那两趟循环删掉、直接贴缩小图，它就归零；
    /// 只报"有没有一张位图"是判不出这件事的——贴原图同样有位图。
    /// </summary>
    public double BlurDeltaForTest { get; private set; }

    /// <summary>真的跑过箱式模糊的那几个通道样本数（判据用它证明"模糊不是被跳过的分支"）。</summary>
    public long BlurSamplesForTest { get; private set; }

    /// <summary>
    /// 开始一趟忙碌：拿一帧已经画好的画布做模糊底，盖上来说明这一趟在干什么。
    /// </summary>
    /// <param name="canvasFrame">画布当前那一帧（调用方负责 Dispose 之前先交给我们复制）。</param>
    /// <param name="title">这一趟的说明，例如"正在导入图片"。</param>
    /// <param name="total">总数；报不出总数就传 0。</param>
    public void Begin(Bitmap? canvasFrame, string title, int total)
    {
        _title = title;
        _done = 0;
        _total = Math.Max(0, total);
        ActiveForTest = true;
        RebuildBlur(canvasFrame);
        LastBarFillWidthForTest = BarFillWidth();
        Visible = true;
        BringToFront();
        Invalidate();
    }

    /// <summary>推进一格。<paramref name="title"/> 传 null 就沿用原来那句。</summary>
    public void Report(int done, int total, string? title = null)
    {
        _done = Math.Max(0, done);
        _total = Math.Max(0, total);
        if (title is not null) _title = title;
        // 进度条的宽度在这里就记一次：读数与 OnPaint 画的是<b>同一个公式</b>的两次调用，
        // 不是两套算术。离屏工装没有真实绘制，靠的就是这一句。
        LastBarFillWidthForTest = BarFillWidth();
        Invalidate();
        // 进度条要靠这一层自己重画才看得见；导入那趟是在 UI 线程上跑的同步循环，
        // 调用方还需要泵一次消息（见 MainForm.ImportPaths 里那段说明）。
        Update();
    }

    /// <summary>
    /// 上一趟收掉时留下的读数。
    /// <see cref="End"/> 会把"当前"那批数清零（那一层不该留着上一趟的糊底），
    /// 所以判据要问"这一趟走到第几张才收的、虚化到底开没开过"只能读这份留档——
    /// 读不到留档就等于"进度条收掉之前没人看过它走到哪儿"。
    /// </summary>
    public string LastTitleForTest { get; private set; } = "";

    /// <summary>上一趟最后一次报的已完成数。</summary>
    public int LastDoneForTest { get; private set; }

    /// <summary>上一趟最后一次报的总数。</summary>
    public int LastTotalForTest { get; private set; }

    /// <summary>上一趟有没有真的做出模糊底（false=只压了暗纱）。</summary>
    public bool LastHadBlurForTest { get; private set; }

    /// <summary>上一趟量到的模糊差值（0–255 平均绝对差）。</summary>
    public double LastBlurDeltaForTest { get; private set; }

    /// <summary>
    /// 每一趟的留档（按发生的先后）。
    /// 为什么不能只留"上一趟"：导入一结束产品会<b>立刻</b>接着跑自动分析，
    /// 那一趟的 Begin/End 会把"上一趟"覆盖掉——于是"导入那一趟走到第几张才收"就读不到了。
    /// 判据要点名哪一趟，就得有一本按顺序记着的账。
    /// </summary>
    /// <param name="Title">那一趟结束时的说明。</param>
    /// <param name="Done">最后一次报的已完成数。</param>
    /// <param name="Total">最后一次报的总数。</param>
    /// <param name="HadBlur">那一趟有没有做出模糊底。</param>
    /// <param name="BlurDelta">那一趟量到的模糊差值。</param>
    public sealed record Trip(string Title, int Done, int Total, bool HadBlur, double BlurDelta);

    private readonly List<Trip> _trips = new();

    /// <summary>每一趟的留档（只读）。</summary>
    public IReadOnlyList<Trip> TripsForTest => _trips;

    /// <summary>
    /// 这一层现在是不是"盖着"的状态。
    /// 判据读的是这个，而不是 <see cref="Control.Visible"/>：后者是<b>有效</b>可见性，
    /// 宿主窗口没 Show 的时候它恒为 false，离屏工装里就什么也判不出来。
    /// </summary>
    public bool ActiveForTest { get; private set; }

    /// <summary>这一趟结束了：模糊底连同那一帧的副本一起放掉。</summary>
    public void End()
    {
        ActiveForTest = false;
        Visible = false;
        // 先把这一趟的账留在档上，再清当前值——顺序反过来判据就只读得到 0。
        LastTitleForTest = _title;
        LastDoneForTest = _done;
        LastTotalForTest = _total;
        LastHadBlurForTest = _blurred is not null;
        LastBlurDeltaForTest = BlurDeltaForTest;
        _trips.Add(new Trip(_title, _done, _total, _blurred is not null, BlurDeltaForTest));
        _blurred?.Dispose();
        _blurred = null;
        BlurDeltaForTest = 0;
        BlurSamplesForTest = 0;
        _done = 0;
        _total = 0;
    }

    /// <summary>
    /// 把画布那一帧做成模糊底：先按 <see cref="Downscale"/> 缩小（箱式平均），
    /// 再在小图上横竖各扫一趟箱式模糊。全程整数：除法只在最后一次做，
    /// 于是同一帧输入永远得到同一张模糊底（不会随机器或时序漂）。
    /// </summary>
    private void RebuildBlur(Bitmap? canvasFrame)
    {
        _blurred?.Dispose();
        _blurred = null;
        BlurDeltaForTest = 0;
        BlurSamplesForTest = 0;
        if (canvasFrame is null || canvasFrame.Width <= 0 || canvasFrame.Height <= 0) return;

        int w = Math.Max(1, canvasFrame.Width / Downscale);
        int h = Math.Max(1, canvasFrame.Height / Downscale);
        var small = new Bitmap(w, h, PixelFormat.Format24bppRgb);

        // ── 第一趟：缩小（箱式平均）。用 GetPixel 而不是 LockBits 读原图——
        // 原图可能上万像素宽，但我们要读的点数只有 w×h×(Downscale²) 个，
        // 而 GetPixel 在这个量级上的成本远低于再写一套 LockBits 取样；
        // 真正的模糊（下面那两趟）走 LockBits，那里才是逐像素的大头。
        using (Graphics g = Graphics.FromImage(small))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
            g.DrawImage(canvasFrame, new Rectangle(0, 0, w, h));
        }

        byte[] before = ReadBgr(small, out int strideBefore);
        byte[] after = (byte[])before.Clone();
        BoxBlur(after, before, w, h, strideBefore, horizontal: true);
        BoxBlur(before, after, w, h, strideBefore, horizontal: false);
        WriteBgr(small, before, w, h, strideBefore);

        // 反证读数：模糊之后与模糊之前差了多少。贴原图 ⇒ 0；真糊了 ⇒ 明显大于 0。
        long sum = 0;
        long samples = 0;
        for (int i = 0; i + 2 < before.Length && i + 2 < after.Length; i += 3)
        {
            sum += Math.Abs(before[i] - after[i]);
            samples++;
        }
        BlurSamplesForTest = samples;
        BlurDeltaForTest = samples == 0 ? 0 : (double)sum / samples;
        _blurred = small;
    }

    /// <summary>
    /// 一趟箱式模糊（只扫一个方向）。<paramref name="src"/> 与 <paramref name="dst"/> 必须不是同一个数组：
    /// 就地扫会让窗口里读到"这一趟刚写进去的值"，糊度会随扫描方向不对称。
    /// </summary>
    private void BoxBlur(byte[] dst, byte[] src, int w, int h, int stride, bool horizontal)
    {
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int sumB = 0, sumG = 0, sumR = 0, count = 0;
                for (int k = -BlurRadius; k <= BlurRadius; k++)
                {
                    int sx = horizontal ? Math.Clamp(x + k, 0, w - 1) : x;
                    int sy = horizontal ? y : Math.Clamp(y + k, 0, h - 1);
                    int at = sy * stride + sx * 3;
                    sumB += src[at];
                    sumG += src[at + 1];
                    sumR += src[at + 2];
                    count++;
                }
                int dstAt = y * stride + x * 3;
                dst[dstAt] = (byte)(sumB / count);
                dst[dstAt + 1] = (byte)(sumG / count);
                dst[dstAt + 2] = (byte)(sumR / count);
            }
        }
    }

    private static byte[] ReadBgr(Bitmap bitmap, out int stride)
    {
        Rectangle rect = new(0, 0, bitmap.Width, bitmap.Height);
        BitmapData data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            stride = data.Stride;
            var buffer = new byte[stride * bitmap.Height];
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0, buffer, 0, buffer.Length);
            return buffer;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    private static void WriteBgr(Bitmap bitmap, byte[] buffer, int w, int h, int stride)
    {
        Rectangle rect = new(0, 0, w, h);
        BitmapData data = bitmap.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
        try
        {
            System.Runtime.InteropServices.Marshal.Copy(buffer, 0, data.Scan0, Math.Min(buffer.Length, stride * h));
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        Rectangle client = ClientRectangle;
        if (client.Width <= 0 || client.Height <= 0) return;

        // ① 模糊底：放大了画（双线性），糊度因此比小图上那两趟还要再柔一点
        if (_blurred is not null)
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
            g.DrawImage(_blurred, client);
        }
        else
        {
            using var back = new SolidBrush(Theme.CanvasBackground);
            g.FillRectangle(back, client);
        }

        // ② 暗纱：让中间那块说明与进度条读得出来。
        // 纱色跟着档位走（第四十八改）：黑夜档压深、白天档压浅。两档都写死深色会在浅色界面上
        // 糊出一块突兀的黑幕；写死浅色则反过来在深色界面上发灰。取当前档的画布底色做纱，
        // 于是"虚化 + 纱"合起来始终像同一套界面里的一层，而不是贴上去的一块异物。
        using (var scrim = new SolidBrush(Color.FromArgb(ScrimAlpha, Theme.CanvasBackground)))
            g.FillRectangle(scrim, client);

        // ③ 中间那块：标题 + 进度条。宽度按控件宽度取，最宽 420，避免在超宽窗口上拉成一条线
        int boxWidth = Math.Min(420, Math.Max(220, client.Width - 80));
        // 第五十一改回修（用户图1红框）：标题/读数两行文本盒原来写死 24/18px、整卡写死 92px 高——
        // 都是 96 DPI 的字高。150% DPI 下 UiFontSmall 长到 ~24px，18px 的盒装不下，
        // 「35 / 100」的下缘被裁。现在每一段都从字体现量推导：96 DPI 上算出来仍是 92（外观不变），
        // 150% DPI 上整卡自动长高，字框永远落得下。
        // 宽度一侧（boxWidth 与 <see cref="BarFillWidth"/> 的 boxWidth-28）一字不动——那把尺只管宽。
        int titleH = Theme.UiFontBold.Height + 4;
        int detailH = Theme.UiFontSmall.Height + 4;
        int trackH = 12;
        int detailTop = 12 + titleH + 4;
        int trackTop = detailTop + detailH + 8;
        int boxHeight = trackTop + trackH + 14;
        var box = new Rectangle((client.Width - boxWidth) / 2, (client.Height - boxHeight) / 2, boxWidth, boxHeight);
        // 第五十一改（用户图4）：这块说明卡原来是一枚直角矩形，和整套圆角卡面语言割裂。
        // 改成圆角卡（与 Chrome.Card 同一套半径），底仍半透明、边仍发丝线，几何不变——
        // 进度条宽度公式 BarFillWidth 读的是同一段 boxWidth-28，圆角只改外观不改取数。
        var smooth = g.SmoothingMode;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using (var boxPath = Chrome.RoundRect(box, Chrome.Radius))
        {
            using var bg = new SolidBrush(Color.FromArgb(235, Theme.PanelBackground));
            g.FillPath(bg, boxPath);
            g.DrawPath(BorderPen, boxPath);
        }
        g.SmoothingMode = smooth;

        TextRenderer.DrawText(g, _title, Theme.UiFontBold,
            new Rectangle(box.X + 14, box.Y + 12, box.Width - 28, titleH), Theme.TextPrimary,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

        string detail = _total > 0 ? $"{_done} / {_total}" : "…";
        TextRenderer.DrawText(g, detail, Theme.UiFontSmall,
            new Rectangle(box.X + 14, box.Y + detailTop, box.Width - 28, detailH), Theme.TextSecondary,
            TextFormatFlags.Right | TextFormatFlags.VerticalCenter);

        // 进度条自己画（不摆 ProgressBar 子控件）：这一层要在没有句柄的离屏工装里也能被量到，
        // 子控件在工装里根本不会真的绘制，读数就成了"看起来有进度条"。
        var track = new Rectangle(box.X + 14, box.Y + trackTop, box.Width - 28, trackH);
        // 进度槽也收一枚小圆角（半径夹到短边一半，12px 高 ⇒ 6px 弧），与外卡同一套语言。
        var trackSmooth = g.SmoothingMode;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using (var trackPath = Chrome.RoundRect(track, Chrome.RadiusSmall))
        {
            using var trackBrush = new SolidBrush(Theme.WindowBackground);
            g.FillPath(trackBrush, trackPath);
            g.DrawPath(BorderPen, trackPath);
        }
        g.SmoothingMode = trackSmooth;
        // 宽度用 <see cref="BarFillWidth"/> 这一个公式：OnPaint 与 Report 读的是同一份算术，
        // 不是"画的是一套、交出去的是另一套"（两处同值就是两份会各自漂移的真相）。
        int fillWidth = BarFillWidth();
        if (fillWidth > 0)
        {
            using var fill = new SolidBrush(Theme.Accent);
            g.FillRectangle(fill, new Rectangle(track.X + 1, track.Y + 1, fillWidth, track.Height - 2));
        }
        LastBarFillWidthForTest = fillWidth;
    }

    /// <summary>
    /// 进度条填充宽度（设备像素）。
    /// 单独抽出来是因为 <see cref="Report"/> 也要交出一个数：离屏工装里 OnPaint 可能一次都不跑，
    /// 只靠绘制那一路，"进度真的推进了"就没有能读的出口。两边都调这同一个公式，
    /// 量到的与画出来的才是同一件事。
    /// </summary>
    private int BarFillWidth()
    {
        Rectangle client = ClientRectangle;
        if (client.Width <= 0 || client.Height <= 0 || _total <= 0) return 0;
        int boxWidth = Math.Min(420, Math.Max(220, client.Width - 80));
        int trackWidth = boxWidth - 28;                       // 与 OnPaint 里那条槽同一段留白
        double ratio = Math.Clamp((double)_done / _total, 0, 1);
        return Math.Max(0, (int)Math.Round((trackWidth - 2) * ratio));
    }

    /// <summary>这一帧进度条画出来的填充宽度（设备像素）。判据用它证明"进度真的推进了"。</summary>
    public int LastBarFillWidthForTest { get; private set; }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Theme.Changed -= OnThemeChanged;
            _blurred?.Dispose();
            _blurred = null;
        }
        base.Dispose(disposing);
    }
}
