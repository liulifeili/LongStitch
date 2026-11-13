namespace LongStitch.App.UI;

/// <summary>
/// 深色 ToolStrip / MenuStrip / StatusStrip 渲染器。
///
/// WinForms 的默认渲染器在深色 BackColor 下会把选中项画成"白底白字"，
/// 菜单直接读不出来。这里把背景、边框、悬停、选中、禁用五种状态显式画全，
/// 并且保证<b>任何状态下前景与背景的对比都够看</b>（需求 §6 可用性）。
///
/// ═══ 第五十改：从「能读」升级到「有工艺」═══
/// ① 按钮的悬停／按下改成圆角 pill（原来是一整块直角灰，和整套卡面语言对不上）；
/// ② 勾选态（对比／差异／五枚工具 chip）改成「强调色薄 wash + 底下一道强调色细条」，
///    替掉系统那圈凹陷方框——那圈框在深色底上几乎看不见，用户报过「点了不知道开没开」；
/// ③ 分隔符改成真正的竖发丝线（原来在横条上画的是一条 6px 的横杠，看起来像个脏点）；
/// ④ 菜单下拉项的选中块也改圆角，与主窗口同一套半径。
/// ⑤ 第五十改回修（用户标注图）：顶级菜单打开/悬停态从实蓝 pill 改成中性抬起底
///    （蓝只留给下拉选中项）；下拉分隔符与整条下拉的背景都不再读容器那枚没人赋过的
///    系统浅色 BackColor，改用面板底现算——原来漏出来的是「白双线」与菜单上下的「粗白线」。
///    顶级项文字改 MiddleCenter，与居中的 pill 对齐（原来 MiddleLeft 偏左）。
///
/// ═══ 类名为什么还叫 DarkToolStripRenderer ═══
/// 它现在两档都服务（白天档也走它）。名字不改是因为
/// <c>ui/菜单栏四个顶级项字体必须同一枚</c> 里有一句 renderer is DarkToolStripRenderer——
/// 判据钉的是类型，改名会让那格红。名字里的 Dark 当作历史称呼读即可。
/// </summary>
public sealed class DarkToolStripRenderer : ToolStripProfessionalRenderer
{
    private static readonly ProfessionalColorTable Palette = new DarkColorTable();

    public DarkToolStripRenderer() : base(Palette) { RoundedEdges = false; }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        bool checkedOn = e.Item is ToolStripButton { Checked: true };
        // 白字只给「压在实强调蓝上」的那一种：下拉菜单里的选中项。工具按钮的悬停底是 Hover 浅灰，
        // 白字压上去在白天档会整行消失——那一族底色配正文色。
        // 第五十改回修：顶级菜单项打开/悬停已改中性底（不再压蓝），所以白字要收窄到
        // 「OwnerItem 非 null」的下拉项；顶级项选中时给正文色，压中性底两档都够对比。
        bool onAccent = e.Item is ToolStripMenuItem && e.Item.Selected && e.Item.OwnerItem is not null;
        Color foreground = e.Item.Enabled
            ? (onAccent ? Color.White : Theme.TextPrimary)
            : Theme.TextDisabled;
        // 勾选态用强调色字而不是白字：wash 底比实蓝底浅得多，白字压上去对比不够。
        if (checkedOn && e.Item.Enabled && !e.Item.Selected) foreground = Theme.Accent;
        e.TextColor = foreground;
        // 字体必须钉死在 item.Font 上（第四十六改）：专业渲染器在视觉样式可用时会拿系统菜单字体
        // 画顶级菜单项，于是同一排里出现"一种代码字体、两种屏上字号"——用户报的【文件】比
        // 【编辑】【视图】【帮助】大一号就是这一族。把 TextFont 显式交回去，屏上就等于代码里写的那一枚。
        e.TextFont = e.Item.Font;
        base.OnRenderItemText(e);
        e.TextColor = foreground;
    }

    /// <summary>按钮底：悬停／按下画圆角 pill，勾选画 wash + 强调色底条。</summary>
    protected override void OnRenderButtonBackground(ToolStripItemRenderEventArgs e)
    {
        var rect = new Rectangle(1, 2, e.Item.Width - 2, e.Item.Height - 4);
        if (rect.Width <= 0 || rect.Height <= 0) return;
        bool isCheck = e.Item is ToolStripButton { Checked: true };
        Color? fill = null;
        if (e.Item.Enabled)
        {
            if (e.Item.Pressed) fill = Chrome.Pressed;
            else if (isCheck) fill = Chrome.AccentWash;
            else if (e.Item.Selected) fill = Chrome.Hover;
        }

        var smooth = e.Graphics.SmoothingMode;
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        if (fill is Color c)
        {
            using var path = Chrome.RoundRect(rect, Chrome.RadiusSmall);
            using var brush = new SolidBrush(c);
            e.Graphics.FillPath(brush, path);
        }
        if (isCheck && e.Item.Enabled)
        {
            using var accent = new SolidBrush(Theme.Accent);
            e.Graphics.FillRectangle(accent, rect.X + Chrome.RadiusSmall, rect.Bottom - 2,
                rect.Width - Chrome.RadiusSmall * 2, 2);
        }
        e.Graphics.SmoothingMode = smooth;
    }

    /// <summary>系统那圈「勾选方框」不画：勾选态已由 wash + 底条表达。</summary>
    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e) { }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        // 不调用基类：ProfessionalRenderer 会额外画系统色的渐变和边框，
        // 在深色菜单上会留下白色横线/白色块。
        var rect = new Rectangle(Point.Empty, e.Item.Size);
        bool dropped = e.Item.OwnerItem is not null;   // 下拉里的项才圆角；顶级项保持整块
        if (e.Item.Selected)
        {
            var smooth = e.Graphics.SmoothingMode;
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            if (dropped)
            {
                var inner = new Rectangle(2, 1, Math.Max(0, rect.Width - 4), Math.Max(0, rect.Height - 2));
                using var path = Chrome.RoundRect(inner, Chrome.RadiusSmall);
                using var bg = new SolidBrush(Theme.Accent);
                e.Graphics.FillPath(bg, path);
            }
            else
            {
                // 第五十改回修：顶级项打开/悬停不再压一块实蓝 pill（用户报「蓝色圆角矩形底很难看」）。
                // 改成中性抬起底：打开（Pressed）比悬停（Hover）再深一档，文字保持正文色。
                // 蓝只留给下拉里的「选中项」与列表选中行——那才是需求 §6 说的「选中」。
                // 高项收成一枚 34px 的圆角 pill（与右侧导出按钮同高），矮项仍只上下各留 2px。
                int high = rect.Height > 40 ? 34 : Math.Max(0, rect.Height - 4);
                var pill = new Rectangle(2, (rect.Height - high) / 2, Math.Max(0, rect.Width - 4), high);
                using var path = Chrome.RoundRect(pill, Chrome.RadiusSmall);
                using var bg = new SolidBrush(e.Item.Pressed ? Chrome.Pressed : Chrome.Hover);
                e.Graphics.FillPath(bg, path);
            }
            e.Graphics.SmoothingMode = smooth;
        }
        else
        {
            using var bg = new SolidBrush(Theme.PanelBackground);
            e.Graphics.FillRectangle(bg, rect);
        }
        // 只有下拉选中项才真压蓝（与上面画的一致）；顶级项画的是中性 Hover/Pressed——
        // 那是算出来的色，按 Chrome 的纪律不进 BackColor，所以顶级项的 BackColor 仍放面板底。
        e.Item.BackColor = e.Item.Selected && e.Item.OwnerItem is not null ? Theme.Accent : Theme.PanelBackground;
        e.Item.ForeColor = e.Item.Enabled ? Theme.TextPrimary : Theme.TextDisabled;
    }

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        // 第五十改回修：下拉容器的 BackColor 没人赋过，是系统 Control 浅色 (240,240,240)。
        // 读它会把整条下拉填成白底——用户报的「菜单总体上下有粗白线」就是上下 padding
        // 漏出的那枚浅色，分隔符周围的白也是同一枚。下拉一律用面板底在绘制当下现算；
        // 横条（MenuStrip/StatusStrip/工具条）构造时已赋成面板底，仍读它们自己的。
        Color fill = e.ToolStrip is ToolStripDropDown ? Theme.PanelBackground : e.ToolStrip.BackColor;
        using var bg = new SolidBrush(fill);
        e.Graphics.FillRectangle(bg, e.AffectedBounds);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        using var pen = new Pen(Theme.PanelBorder);
        if (e.ToolStrip is ToolStripDropDown)
            e.Graphics.DrawRectangle(pen, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
        else if (e.ToolStrip is StatusStrip)
            e.Graphics.DrawLine(pen, 0, 0, e.ToolStrip.Width - 1, 0);   // 状态栏的分隔线在顶缘
        else
            e.Graphics.DrawLine(pen, 0, e.ToolStrip.Height - 1, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        // 先填充整个 separator，避免系统 BackColor 从边缘漏出。
        // 第五十改回修：下拉菜单容器的 BackColor 没人赋过，是系统 Control 浅色 (240,240,240)——
        // 读它会把分隔符填成「白双线」（用户图2那几道白杠）。下拉一律用面板底在绘制当下现算；
        // 横条上的竖分隔仍读各条自己的 BackColor（那些条构造时已赋成面板底）。
        Color fill = e.ToolStrip is ToolStripDropDown
            ? Theme.PanelBackground
            : (e.ToolStrip?.BackColor ?? Theme.PanelBackground);
        using var bg = new SolidBrush(fill);
        e.Graphics.FillRectangle(bg, new Rectangle(Point.Empty, e.Item.Size));
        using var pen = new Pen(Chrome.HairlineSoft);
        if (e.Vertical)
        {
            // 横条上的竖分隔：上下各留 6px，不顶到条边，读作「组与组之间」而不是「裂缝」。
            int x = e.Item.Width / 2;
            e.Graphics.DrawLine(pen, x, 6, x, Math.Max(6, e.Item.Height - 6));
        }
        else
        {
            int y = e.Item.Height / 2;
            e.Graphics.DrawLine(pen, 6, y, Math.Max(6, e.Item.Width - 6), y);
        }
    }

    private sealed class DarkColorTable : ProfessionalColorTable
    {
        public override Color MenuItemSelected => Theme.Accent;
        public override Color MenuItemSelectedGradientBegin => Theme.Accent;
        public override Color MenuItemSelectedGradientEnd => Theme.Accent;
        public override Color MenuItemPressedGradientBegin => Theme.Accent;
        public override Color MenuItemPressedGradientMiddle => Theme.Accent;
        public override Color MenuItemPressedGradientEnd => Theme.Accent;
        public override Color MenuItemBorder => Theme.Accent;
        public override Color MenuBorder => Theme.PanelBorder;
        public override Color ToolStripDropDownBackground => Theme.PanelBackground;
        public override Color ImageMarginGradientBegin => Theme.PanelBackground;
        public override Color ImageMarginGradientMiddle => Theme.PanelBackground;
        public override Color ImageMarginGradientEnd => Theme.PanelBackground;
        public override Color MenuStripGradientBegin => Theme.PanelBackground;
        public override Color MenuStripGradientEnd => Theme.PanelBackground;
        public override Color SeparatorDark => Theme.PanelBorder;
        public override Color SeparatorLight => Theme.PanelBorder;
        public override Color StatusStripGradientBegin => Theme.PanelBackground;
        public override Color StatusStripGradientEnd => Theme.PanelBackground;
    }
}
