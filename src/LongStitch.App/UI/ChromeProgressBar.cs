namespace LongStitch.App.UI;

/// <summary>
/// 第五十四改⑤（用户图5「导出界面和整体 UI 完全不搭」）：自绘进度条。
/// 系统 ProgressBar 的绿色分段块在黑夜档下是刺眼的亮绿白底旧组件；这里整根自己画：
/// 凹槽底 + 强调蓝胶囊填充，颜色绘制当下从 <see cref="Chrome"/> 现算（换肤零接线）。
/// 只做「0–100 百分比」这一种语义，导出进度恰好就是这个形状。
/// </summary>
internal sealed class ChromeProgressBar : Control
{
    private int _value;

    /// <summary>0–100 的进度读数；越界夹取。</summary>
    internal int Value
    {
        get => _value;
        set { int clamped = Math.Clamp(value, 0, 100); if (clamped != _value) { _value = clamped; Invalidate(); } }
    }

    internal ChromeProgressBar()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.WindowBackground;   // 只放调色板认得的值；自绘时整块盖掉
        Height = 18;
        TabStop = false;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.PanelBackground);
        var track = new Rectangle(0, 0, Width - 1, Height - 1);
        var smooth = g.SmoothingMode;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using (var path = Chrome.RoundRect(track, Math.Min(Chrome.RadiusSmall, track.Height / 2)))
        using (var bg = new SolidBrush(Chrome.Sunken))
            g.FillPath(bg, path);
        if (_value > 0)
        {
            int fillWidth = Math.Max(Height, track.Width * _value / 100);   // 最短填出一枚完整圆头
            var fill = new Rectangle(0, 0, Math.Min(fillWidth, track.Width), track.Height);
            using var path = Chrome.RoundRect(fill, Math.Min(Chrome.RadiusSmall, fill.Height / 2));
            using var brush = new SolidBrush(Theme.Accent);
            g.FillPath(brush, path);
        }
        g.SmoothingMode = smooth;
    }
}
