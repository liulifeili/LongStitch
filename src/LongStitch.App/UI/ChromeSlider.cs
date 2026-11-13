namespace LongStitch.App.UI;

/// <summary>
/// 第五十一改（用户图黄框）：自绘滑杆。系统 TrackBar 是虚线刻度 + 方形滑块，
/// 深色底上像是十年前的控件，与整套圆角卡面语言对不上；这里整条自己画——
/// 圆角轨道 + 已走过的一段强调蓝 + 一枚圆形滑块，悬停放大描边。
///
/// 只承担「重叠透明度」这一处（0–100），所以接口就收在最小：
/// <see cref="Minimum"/>／<see cref="Maximum"/>／<see cref="Value"/>／<see cref="ValueChanged"/>，
/// 鼠标按下／拖动改值，方向键与 Home／End 也能走（触屏与键盘验收的需求 §6 同源）。
/// 颜色全部在 OnPaint 当下从 <see cref="Chrome"/> 现算（换肤零接线）。
/// </summary>
internal sealed class ChromeSlider : Control
{
    private bool _hover;
    private bool _dragging;

    /// <summary>值被用户（或程序）改动时触发；换肤不触发。</summary>
    public event EventHandler? ValueChanged;

    public ChromeSlider()
    {
        // 第五十三改（用户图绿框「滑杆点击后没法使用左右方向键」）：Selectable 不能省。
        // 没有这一位，WinForms 在焦点链上把这枚控件当"不可选中"看待——OnMouseDown 里的 Focus()
        // 是空转，焦点根本进不来，OnKeyDown／IsInputKey 里那套方向键・Home・End 处理永远收不到。
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                 ControlStyles.Selectable | ControlStyles.StandardClick |
                 ControlStyles.StandardDoubleClick, true);
        BackColor = Theme.PanelBackground;
        ForeColor = Theme.TextPrimary;
        Cursor = Cursors.Hand;
        Height = 28;
        TabStop = true;
    }

    private int _minimum;
    private int _maximum = 100;
    private int _value;

    public int Minimum { get => _minimum; set { _minimum = value; if (_value < value) Set(value); Invalidate(); } }
    public int Maximum { get => _maximum; set { _maximum = value; if (_value > value) Set(value); Invalidate(); } }

    public int Value
    {
        get => _value;
        set { Set(value); Invalidate(); }
    }

    private void Set(int value)
    {
        int clamped = Math.Clamp(value, _minimum, _maximum);
        if (clamped == _value) return;
        _value = clamped;
        ValueChanged?.Invoke(this, EventArgs.Empty);
        Invalidate();
    }

    /// <summary>
    /// 供判据：把一枚键按产品键盘通路走一遍（与真按键最终调用的同一个 <see cref="OnKeyDown"/>）。
    /// 判据 <c>ui/透明度滑杆可获焦点且方向键改值并带百分比读数</c> 用它量「点击后方向键可操控」。
    /// </summary>
    internal void PressKeyForTest(Keys key) => OnKeyDown(new KeyEventArgs(key));

    /// <summary>
    /// 供判据：这一枚键会不会被控件认作输入键（<see cref="IsInputKey"/> 的同一条算式）。
    /// 不认领的话方向键在容器那一层就被吞掉，OnKeyDown 根本收不到。
    /// </summary>
    internal bool AcceptsKeyForTest(Keys key) => IsInputKey(key);

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; _dragging = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left && Enabled)
        {
            _dragging = true;
            Focus();
            DragTo(e.X);
        }
        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_dragging && e.Button == MouseButtons.Left) DragTo(e.X);
        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseEventArgs e) { _dragging = false; base.OnMouseUp(e); }

    private void DragTo(int x)
    {
        int span = Math.Max(1, Width - TrackMargin * 2);
        double t = Math.Clamp((x - TrackMargin) / (double)span, 0.0, 1.0);
        Set(_minimum + (int)Math.Round(t * (_maximum - _minimum)));
    }

    protected override bool IsInputKey(Keys keyData) =>
        keyData is Keys.Left or Keys.Right or Keys.Up or Keys.Down or Keys.Home or Keys.End || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        int step = e.Shift ? 10 : 1;
        switch (e.KeyCode)
        {
            case Keys.Left or Keys.Down: Set(_value - step); e.Handled = true; break;
            case Keys.Right or Keys.Up: Set(_value + step); e.Handled = true; break;
            case Keys.Home: Set(_minimum); e.Handled = true; break;
            case Keys.End: Set(_maximum); e.Handled = true; break;
        }
        base.OnKeyDown(e);
    }

    private const int TrackMargin = 8;
    private const int TrackHeight = 4;
    private const int ThumbRadius = 7;

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.PanelBackground);

        int trackY = Height / 2 - TrackHeight / 2;
        int span = Math.Max(1, Width - TrackMargin * 2);
        double t = _maximum == _minimum ? 0.0 : Math.Clamp((_value - _minimum) / (double)(_maximum - _minimum), 0.0, 1.0);
        int thumbX = TrackMargin + (int)Math.Round(span * t);

        var smooth = g.SmoothingMode;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        // 轨道（整条）＋已走过的一段（强调蓝）：两段都是圆角短棒
        var rest = new Rectangle(TrackMargin, trackY, span, TrackHeight);
        using (var path = Chrome.RoundRect(rest, TrackHeight / 2))
        using (var brush = new SolidBrush(Enabled ? Chrome.HairlineSoft : Chrome.Mix(Chrome.Surface, Chrome.Base, 0.5)))
            g.FillPath(brush, path);
        if (thumbX > TrackMargin)
        {
            var walked = new Rectangle(TrackMargin, trackY, thumbX - TrackMargin, TrackHeight);
            using var path = Chrome.RoundRect(walked, TrackHeight / 2);
            using var brush = new SolidBrush(Enabled ? Theme.Accent : Theme.TextDisabled);
            g.FillPath(brush, path);
        }

        // 滑块：一枚圆，悬停／拖动时描强调色环
        int d = ThumbRadius * 2;
        var thumb = new Rectangle(thumbX - ThumbRadius, trackY + TrackHeight / 2 - ThumbRadius, d, d);
        using (var fill = new SolidBrush(Enabled ? (Theme.IsDark ? Chrome.Raised : Chrome.Surface) : Chrome.Mix(Chrome.Surface, Chrome.Base, 0.5)))
            g.FillEllipse(fill, thumb);
        using (var ring = new Pen(Enabled && (_hover || _dragging) ? Theme.Accent : Chrome.HairlineSoft, 1f))
            g.DrawEllipse(ring, thumb);

        g.SmoothingMode = smooth;

        if (Focused && Enabled) Chrome.FocusRing(g, new Rectangle(0, 0, Width - 1, Height - 1), Chrome.RadiusSmall);
    }

    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
}
