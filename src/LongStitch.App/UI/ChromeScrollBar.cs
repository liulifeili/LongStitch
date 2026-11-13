using System.Drawing.Drawing2D;

namespace LongStitch.App.UI;

/// <summary>
/// 第五十四改⑥⑦：自绘的纵向滚动条——用户点名的「蓝色圆弧粗线条」。
///
/// ═══ 为什么不再用 WinForms 的原生滚动条 ═══
/// 原生条有两个治不好的病：
/// ① 黑夜档下它是一根<b>白底</b>的旧组件（用户图6 红框），系统滚动条不吃我们的调色板，
///    而产品内禁 P/Invoke，藏不掉也重漆不了；
/// ② 它藏在 <see cref="System.Windows.Forms.ScrollableControl"/> 的 AutoScroll 反馈环里——
///    条一出现客户区就变窄 → 说明段折行变高 → 内容总高再变 → AutoScrollMinSize 再写 →
///    又触发布局……「滚到底之后整块抽搐然后卡死」（用户图7）就是这一族自激。
/// 手动滚动（见 <see cref="ToolPanel"/>）配这枚自绘条之后，条的显隐<b>不再改变客户区</b>
/// ——它整根叠在面板自己的右留白里，反馈环从结构上被拆掉。
///
/// ═══ 画法与整套 Chrome 语言同一条规矩 ═══
/// 颜色全部「绘制当下」从 <see cref="Theme"/> 现算（不进调色板、不吃翻译表）；
/// 滑块用 <see cref="Chrome.RoundRect"/> 画成整根圆弧胶囊，静止 6px、悬停/拖动加宽到 9px，
/// 强调蓝只在"可拖动的那根滑块"上——与需求 §6「蓝只用于可交互」的口径一致：
/// 滑块正是这根条上唯一可交互的东西，轨道本身不画蓝。
/// </summary>
internal sealed class ChromeScrollBar : Control
{
    private const int ThumbNarrow = 6;
    private const int ThumbWide = 9;
    private const int MinThumbHeight = 40;

    private int _maximum;
    private int _largeChange = 1;
    private int _value;
    private bool _hover;
    private bool _dragging;
    private int _dragGrab;

    /// <summary>滚动值变化（含被夹回合法区间）。宿主据此重摆行位置。</summary>
    public event Action? ValueChanged;

    public ChromeScrollBar()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.PanelBackground;
        TabStop = false;
        Width = 8;
    }

    /// <summary>内容总高（像素）。</summary>
    public int Maximum
    {
        get => _maximum;
        set { _maximum = Math.Max(0, value); ClampValue(); Invalidate(); }
    }

    /// <summary>一屏的高（视口）。决定滑块长度与翻页步长。</summary>
    public int LargeChange
    {
        get => _largeChange;
        set { _largeChange = Math.Max(1, value); ClampValue(); Invalidate(); }
    }

    /// <summary>当前滚动偏移。永远落在 [0, Max(0, Maximum − LargeChange)]。</summary>
    public int Value
    {
        get => _value;
        set
        {
            int clamped = Clamp(value);
            if (clamped == _value) return;
            _value = clamped;
            Invalidate();
            ValueChanged?.Invoke();
        }
    }

    /// <summary>滚轮一格走的像素（约三行说明文字）。</summary>
    public const int WheelStep = 72;

    /// <summary>内容真的装不下时滑块才出现；装得下时这根条什么都不画。</summary>
    public bool ThumbVisible => _maximum > _largeChange && Height >= MinThumbHeight * 2;

    private int MaxValue => Math.Max(0, _maximum - _largeChange);

    private int Clamp(int value) => Math.Max(0, Math.Min(MaxValue, value));

    private void ClampValue()
    {
        int clamped = Clamp(_value);
        if (clamped != _value)
        {
            _value = clamped;
            ValueChanged?.Invoke();
        }
    }

    /// <summary>滑块的屏上矩形（本控件坐标系）。不显示时是空矩形。</summary>
    private Rectangle ThumbRect
    {
        get
        {
            if (!ThumbVisible) return Rectangle.Empty;
            int track = Height - 8;
            int thumbH = Math.Max(MinThumbHeight, (int)((long)track * _largeChange / Math.Max(1, _maximum)));
            int scrollable = Math.Max(1, track - thumbH);
            int y = 4 + (int)((long)scrollable * _value / Math.Max(1, MaxValue));
            int w = _hover || _dragging ? ThumbWide : ThumbNarrow;
            return new Rectangle((Width - w) / 2, y, w, thumbH);
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (!ThumbVisible) return;
        var thumb = ThumbRect;
        var smooth = e.Graphics.SmoothingMode;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        // 滑块本体：整根圆弧胶囊。拖动时比悬停再实一档，让"抓着了"有读数。
        using var path = Chrome.RoundRect(thumb, thumb.Width / 2);
        using var brush = new SolidBrush(_dragging ? Theme.Accent : Chrome.Fade(Theme.Accent, _hover ? 230 : 190));
        e.Graphics.FillPath(brush, path);
        e.Graphics.SmoothingMode = smooth;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left || !ThumbVisible) return;
        var thumb = ThumbRect;
        if (thumb.Contains(e.Location))
        {
            _dragging = true;
            _dragGrab = e.Y - thumb.Y;
        }
        else
        {
            // 点轨道 = 翻页（朝点的方向），与系统滚动条同一习惯
            Value += e.Y < thumb.Y ? -_largeChange : _largeChange;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging)
        {
            int track = Height - 8;
            int thumbH = Math.Max(MinThumbHeight, (int)((long)track * _largeChange / Math.Max(1, _maximum)));
            int scrollable = Math.Max(1, track - thumbH);
            Value = (int)((long)(e.Y - 4 - _dragGrab) * MaxValue / scrollable);
        }
        else
        {
            bool hover = ThumbRect.Contains(e.Location);
            if (hover != _hover) { _hover = hover; Invalidate(); }
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_dragging) { _dragging = false; Invalidate(); }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hover) { _hover = false; Invalidate(); }
    }
}
