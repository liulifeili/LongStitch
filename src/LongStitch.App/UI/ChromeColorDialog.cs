namespace LongStitch.App.UI;

/// <summary>
/// 第五十七改③（用户图3「点击背景颜色选择后弹出的窗口太老，并且不适配当前的整体UI」）：
/// 自绘取色器，替掉系统 <see cref="ColorDialog"/>。
///
/// ═══ 为什么必须换 ═══
/// 系统 ColorDialog 是 Win32 通用对话框：浅灰底、系统按钮、左侧一片空白自定义区，
/// 在整套深色圆角自绘界面里弹出来就是「贴上去的异物」（与第五十一改换掉 MessageBox 同因）。
/// 这里整面走 Chrome 语言：窗体底 Theme.WindowBackground、数字框复用 ChromeNumericBox、
/// 按钮复用 ChromeButton、色卡与预览用 Chrome.RoundRect/Card 同一族圆角发丝边。
///
/// ═══ 结构 ═══
/// ① 32 枚预设色（8×4）：灰阶一排（导出背景最常用的白→黑）、暖色一排、冷色一排、
///    浅色一排。点一枚＝把 RGB 三框同步成那枚色。
/// ② RGB 三枚数字框（0–255）：键盘直接改，走 ValueChanged 真通路。
/// ③ 预览块 + hex 读数：任何一条路改色都立刻映到这两处。
/// ④ 确定／取消：确定把 SelectedColor 交回调用方（DialogResult.OK），取消不交。
///
/// ═══ 纪律 ═══
/// 预设色是「数据」不是界面色：只在 OnPaint 当下画，不进 BackColor，
/// 因此换肤翻译表不需要认得它们（Chrome 类注释那条纪律）。
/// 布局先量后摆：宽度取色板网格与最宽文字的较大者，高度由 OnLayout 按内容收口。
/// public 是为了让电池能构造（本仓无 InternalsVisibleTo）。
/// </summary>
public sealed class ChromeColorDialog : Form
{
    private const int Pad = 20;
    private const int Gap = 14;
    private const int SwatchSize = 36;   // 需求 §6：主要点击目标 ≥ 36×36
    private const int SwatchPitch = 44;  // 36 色卡 + 8 缝
    private const int PresetCols = 8;

    /// <summary>
    /// 32 枚预设：第一排灰阶（白→黑，导出背景最高频），第二排暖色，第三排冷色，
    /// 第四排浅色与两枚深石板灰。全部是不透明实色——导出背景本来就不带透明。
    /// </summary>
    private static readonly Color[] Presets =
    {
        Color.FromArgb(255, 255, 255), Color.FromArgb(242, 242, 242), Color.FromArgb(217, 217, 217), Color.FromArgb(191, 191, 191),
        Color.FromArgb(140, 140, 140), Color.FromArgb(89, 89, 89), Color.FromArgb(38, 38, 38), Color.FromArgb(0, 0, 0),
        Color.FromArgb(255, 224, 178), Color.FromArgb(255, 183, 77), Color.FromArgb(255, 112, 67), Color.FromArgb(229, 57, 53),
        Color.FromArgb(216, 27, 96), Color.FromArgb(142, 36, 170), Color.FromArgb(94, 53, 177), Color.FromArgb(57, 73, 171),
        Color.FromArgb(30, 136, 229), Color.FromArgb(3, 155, 229), Color.FromArgb(0, 172, 193), Color.FromArgb(0, 137, 123),
        Color.FromArgb(67, 160, 71), Color.FromArgb(124, 179, 66), Color.FromArgb(192, 202, 51), Color.FromArgb(253, 216, 53),
        Color.FromArgb(227, 242, 253), Color.FromArgb(232, 245, 233), Color.FromArgb(255, 248, 225), Color.FromArgb(252, 228, 236),
        Color.FromArgb(237, 231, 246), Color.FromArgb(236, 239, 241), Color.FromArgb(55, 71, 79), Color.FromArgb(38, 50, 56),
    };

    private readonly FlowLayoutPanel _body = new()
    {
        FlowDirection = FlowDirection.TopDown,
        WrapContents = false,
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
    };

    private readonly PresetSwatch[] _swatches = new PresetSwatch[Presets.Length];
    private readonly ChromeNumericBox _r = new() { Minimum = 0, Maximum = 255, Increment = 1 };
    private readonly ChromeNumericBox _g = new() { Minimum = 0, Maximum = 255, Increment = 1 };
    private readonly ChromeNumericBox _b = new() { Minimum = 0, Maximum = 255, Increment = 1 };
    private readonly PreviewBlock _preview = new() { Width = 72, Height = 72 };
    private readonly Label _hex = new()
    {
        AutoSize = true, Font = Theme.MonoFont, ForeColor = Theme.TextPrimary,
        BackColor = Theme.WindowBackground, Margin = new Padding(0, 0, 0, 4),
    };
    private readonly Label _rgbLine = new()
    {
        AutoSize = true, Font = Theme.UiFontSmall, ForeColor = Theme.TextSecondary,
        BackColor = Theme.WindowBackground, Margin = new Padding(0),
    };
    private readonly ChromeButton _ok = new() { Text = "确定", Kind = ChromeButtonKind.Primary, Width = 96 };
    private readonly ChromeButton _cancel = new() { Text = "取消", Kind = ChromeButtonKind.Secondary, Width = 96 };

    // 预设→数字框的回填途中不许再触发 ValueChanged 反过来改色（会绕一圈把选中态抖掉）。
    private bool _updating;

    /// <summary>当前选中的颜色（任何一条路改色都实时更新；确定后由调用方取走）。</summary>
    public Color SelectedColor { get; private set; }

    public ChromeColorDialog(Color initial)
    {
        Text = "选择背景色";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        // 与关于/设置对话框同一条纪律：宽度全是运行时现量的设备像素，关掉字体自动缩放。
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Theme.WindowBackground;
        ForeColor = Theme.TextPrimary;
        Font = Theme.UiFont;

        BuildContent(initial);
        Theme.Changed += OnThemeChanged;
    }

    private void BuildContent(Color initial)
    {
        int gridW = PresetCols * SwatchPitch - (SwatchPitch - SwatchSize);   // 8 列 × 44 − 8 = 344
        int gridH = (Presets.Length / PresetCols) * SwatchPitch - (SwatchPitch - SwatchSize);
        int boxH = Math.Max(30, Theme.MonoFont.Height + 10);
        _r.Height = boxH; _g.Height = boxH; _b.Height = boxH;

        // 先量后摆：内容宽＝色板网格与最宽那行文字的较大者（换肤/换 DPI 都跟着现量走）。
        int widest = gridW;
        foreach (string text in new[] { "选择背景色", "预设色，或自定义 RGB；导出时透明区域将填充此色", "R 255 · G 255 · B 255", "#FFFFFF" })
            widest = Math.Max(widest, TextRenderer.MeasureText(text, Font).Width);
        int contentW = widest;
        int clientW = contentW + Pad * 2;
        ClientSize = new Size(clientW, 400);   // 高度占位，真值由 OnLayout 收口

        _body.Location = new Point(Pad, Pad);
        _body.Width = contentW;
        _body.BackColor = Theme.WindowBackground;
        Controls.Add(_body);

        _body.Controls.Add(new Label
        {
            Text = "选择背景色",
            AutoSize = true,
            Font = Theme.UiFontBold,
            ForeColor = Theme.TextPrimary,
            BackColor = Theme.WindowBackground,
            Margin = new Padding(0, 0, 0, 4),
        });
        _body.Controls.Add(new Label
        {
            Text = "预设色，或自定义 RGB；导出时透明区域将填充此色",
            AutoSize = true,
            MaximumSize = new Size(contentW, 0),
            Font = Theme.UiFontSmall,
            ForeColor = Theme.TextSecondary,
            BackColor = Theme.WindowBackground,
            Margin = new Padding(0, 0, 0, Gap),
        });

        // ── ① 预设色板：8×4，色卡是自绘小控件，点击走与真鼠标同一条处理器 ──
        var grid = new Panel
        {
            Width = gridW,
            Height = gridH,
            BackColor = Theme.WindowBackground,
            Margin = new Padding(0, 0, 0, Gap),
        };
        for (int i = 0; i < Presets.Length; i++)
        {
            int index = i;
            var swatch = new PresetSwatch(Presets[i])
            {
                Location = new Point((i % PresetCols) * SwatchPitch, (i / PresetCols) * SwatchPitch),
            };
            swatch.Click += (_, _) => ApplyPreset(index);
            _swatches[i] = swatch;
            grid.Controls.Add(swatch);
        }
        _body.Controls.Add(grid);

        // ──  RGB 三框：标签宽现量，三框平分剩余宽度 ──
        int labelW = Math.Max(TextRenderer.MeasureText("R", Font).Width,
                     Math.Max(TextRenderer.MeasureText("G", Font).Width,
                              TextRenderer.MeasureText("B", Font).Width)) + 4;
        const int labelGap = 6, groupGap = 18;
        int boxW = Math.Max(56, (contentW - labelW * 3 - labelGap * 3 - groupGap * 2) / 3);
        _r.Width = boxW; _g.Width = boxW; _b.Width = boxW;
        var rgbRow = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Theme.WindowBackground,
            Margin = new Padding(0, 0, 0, Gap),
        };
        void AddChannel(string name, ChromeNumericBox box, int leftMargin)
        {
            rgbRow.Controls.Add(new Label
            {
                Text = name,
                AutoSize = false,
                Width = labelW,
                Height = boxH,
                TextAlign = ContentAlignment.MiddleLeft,
                Font = Theme.UiFontSmall,
                ForeColor = Theme.TextSecondary,
                BackColor = Theme.WindowBackground,
                Margin = new Padding(leftMargin, 0, labelGap, 0),
            });
            box.Margin = new Padding(0);
            rgbRow.Controls.Add(box);
        }
        AddChannel("R", _r, 0);
        AddChannel("G", _g, groupGap);
        AddChannel("B", _b, groupGap);
        _body.Controls.Add(rgbRow);

        // ── ③ 预览 + 读数：预览块在左，hex 与 RGB 两行读数在右 ──
        var previewRow = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Theme.WindowBackground,
            Margin = new Padding(0, 0, 0, Gap),
        };
        _preview.Margin = new Padding(0, 0, Gap, 0);
        previewRow.Controls.Add(_preview);
        var readout = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Theme.WindowBackground,
            Margin = new Padding(0, 10, 0, 0),
        };
        readout.Controls.Add(_hex);
        readout.Controls.Add(_rgbLine);
        previewRow.Controls.Add(readout);
        _body.Controls.Add(previewRow);

        // ──  确定／取消：靠右站，与导出对话框动作行同一侧 ──
        var footer = new Panel
        {
            Width = contentW,
            Height = _ok.Height + 8,
            BackColor = Theme.WindowBackground,
            Margin = new Padding(0),
        };
        _ok.Location = new Point(contentW - _ok.Width - Gap - _cancel.Width, 4);
        _cancel.Location = new Point(contentW - _cancel.Width, 4);
        footer.Controls.Add(_ok);
        footer.Controls.Add(_cancel);
        _body.Controls.Add(footer);

        _ok.Click += (_, _) => { DialogResult = DialogResult.OK; Close(); };
        _cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        AcceptButton = _ok;
        CancelButton = _cancel;

        // 三框的真通路：键盘改值 → ValueChanged → 统一 UpdateColor。
        _r.ValueChanged += OnRgbChanged;
        _g.ValueChanged += OnRgbChanged;
        _b.ValueChanged += OnRgbChanged;

        // 初始色回填：走与点预设同一条静音回填路，再统一映到预览/读数/选中环。
        _updating = true;
        _r.Value = initial.R;
        _g.Value = initial.G;
        _b.Value = initial.B;
        _updating = false;
        UpdateColor(initial);
    }

    private void OnRgbChanged(object? sender, EventArgs e)
    {
        if (_updating) return;
        UpdateColor(Color.FromArgb((int)_r.Value, (int)_g.Value, (int)_b.Value));
    }

    /// <summary>点预设：静音回填三框，再统一更新预览/读数/选中环（与真点击同一条路）。</summary>
    private void ApplyPreset(int index)
    {
        Color c = Presets[index];
        _updating = true;
        _r.Value = c.R;
        _g.Value = c.G;
        _b.Value = c.B;
        _updating = false;
        UpdateColor(c);
    }

    /// <summary>
    /// 一处总更新：任何一条路改色都汇到这里。选中环只在颜色恰好等于某枚预设时点亮
    /// （自定义 RGB 不假装是预设）。颜色值本身只进 OnPaint，不进任何 BackColor。
    /// </summary>
    private void UpdateColor(Color c)
    {
        SelectedColor = c;
        _preview.Color = c;
        _preview.Invalidate();
        _hex.Text = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        _rgbLine.Text = $"R {c.R} · G {c.G} · B {c.B}";
        for (int i = 0; i < _swatches.Length; i++)
        {
            bool selected = Presets[i].ToArgb() == c.ToArgb();
            if (_swatches[i].Selected == selected) continue;
            _swatches[i].Selected = selected;
            _swatches[i].Invalidate();
        }
    }

    private void OnThemeChanged(ThemePalette old, ThemePalette current)
    {
        ThemeApplicator.Apply(this);
        Invalidate(true);
        PerformLayout();
    }

    /// <summary>窗体高度跟着内容走（与关于/设置/导出对话框同一套写法，带变化闸防自激）。</summary>
    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        int want = _body.Bottom + Pad;
        if (want > 0 && want != ClientSize.Height)
            ClientSize = new Size(ClientSize.Width, want);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) Theme.Changed -= OnThemeChanged;
        base.Dispose(disposing);
    }

    // ── 工装口（public：本仓无 InternalsVisibleTo，电池只能摸 public）──

    /// <summary>第五十七改③工装口：预设色枚数（应为 32，8×4 站满）。</summary>
    public int PresetCountForTest => _swatches.Length;

    /// <summary>第五十七改③工装口：预设色全量（判两两不同、含纯白纯黑）。</summary>
    public IReadOnlyList<Color> PresetColorsForTest => (Color[])Presets.Clone();

    /// <summary>第五十七改③工装口：按真点击同一条处理器选第 index 枚预设。</summary>
    public void SelectPresetForTest(int index) => ApplyPreset(index);

    /// <summary>第五十七改③工装口：走 ValueChanged 真通路改 RGB（不绕过任何接线）。</summary>
    public void SetRgbForTest(int r, int g, int b)
    {
        _r.Value = r;
        _g.Value = g;
        _b.Value = b;
    }

    /// <summary>第五十七改③工装口：hex 读数（应与 SelectedColor 逐位一致）。</summary>
    public string HexTextForTest => _hex.Text;

    /// <summary>第五十七改③工装口：确定按钮（电池可真点它收 DialogResult）。</summary>
    public Button OkButtonForTest => _ok;

    /// <summary>
    /// 预设色卡：圆角填色 + 发丝边；选中时加一圈强调色焦点环（需求 §6 蓝只用于选中态）。
    /// 颜色是数据，OnPaint 当下画，换肤翻译表不需要认得它。
    /// </summary>
    private sealed class PresetSwatch : Control
    {
        private readonly Color _chip;
        private bool _hover;

        internal bool Selected { get; set; }

        internal PresetSwatch(Color chip)
        {
            _chip = chip;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Size = new Size(SwatchSize, SwatchSize);
            BackColor = Theme.WindowBackground;   // 只放调色板认得的值；自绘时整块盖掉
            Cursor = Cursors.Hand;
            TabStop = false;
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            var rect = new Rectangle(0, 0, Width - 1, Height - 1);
            var smooth = g.SmoothingMode;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (var path = Chrome.RoundRect(rect, Chrome.RadiusSmall))
            {
                using var fill = new SolidBrush(_chip);
                g.FillPath(fill, path);
                using var edge = new Pen(_hover ? Theme.TextSecondary : Theme.PanelBorder, 1f);
                g.DrawPath(edge, path);
            }
            g.SmoothingMode = smooth;
            // 选中环画在色卡内侧（Chrome.FocusRing 的纪律：不许外扩进邻格）。
            if (Selected) Chrome.FocusRing(g, rect, Chrome.RadiusSmall);
        }
    }

    /// <summary>预览块：当前色铺满一枚圆角卡，外圈发丝边，与色卡同一族语言。</summary>
    private sealed class PreviewBlock : Control
    {
        internal Color Color { get; set; } = Color.White;

        internal PreviewBlock()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.WindowBackground;
            TabStop = false;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            var rect = new Rectangle(0, 0, Width - 1, Height - 1);
            var smooth = g.SmoothingMode;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var path = Chrome.RoundRect(rect, Chrome.Radius);
            using var fill = new SolidBrush(Color);
            g.FillPath(fill, path);
            using var edge = new Pen(Theme.PanelBorder, 1f);
            g.DrawPath(edge, path);
            g.SmoothingMode = smooth;
        }
    }
}