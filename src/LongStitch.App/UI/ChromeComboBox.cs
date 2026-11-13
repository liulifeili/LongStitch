namespace LongStitch.App.UI;

/// <summary>
/// 第五十四改⑤（用户图5「黑夜模式还有如此白的选择框」）：自绘外观的下拉框。
///
/// 系统 ComboBox 在黑夜档下收起时是白底黑字、展开后整块白名单，与整套深色圆角卡面
/// 完全对不上；而 WinForms 没有给 ComboBox 留「只换配色」的口子——收起那一格与
/// 展开列表的每一行都走 <see cref="OnDrawItem"/> 自绘，底色/字色/选中态全部从
/// <see cref="Chrome"/> 现算（换肤零接线）。仅限 DropDownList 档（本产品两处都是
/// 纯选择的格式/档位，不需要可输入），可编辑档的自绘要另算。
///
/// 仍然是 <see cref="ComboBox"/> 的派生类：Items、SelectedIndex、SelectedIndexChanged、
/// 判据里按 <c>SelectedIndex</c> 对账的量法一个字不用改。
/// </summary>
internal sealed class ChromeComboBox : ComboBox
{
    private const int WmPaint = 0x000F;

    /// <summary>第五十六改②工装口：收起格补画那一笔的执行次数（判据据此证明箭头真被接管过）。</summary>
    internal int ArrowPaintCount { get; private set; }

    internal ChromeComboBox()
    {
        DropDownStyle = ComboBoxStyle.DropDownList;
        DrawMode = DrawMode.OwnerDrawFixed;
        FlatStyle = FlatStyle.Flat;
        BackColor = Theme.WindowBackground;   // 只放调色板认得的值；自绘时整块盖掉
        ForeColor = Theme.TextPrimary;
        Font = Theme.UiFont;
        ItemHeight = Theme.UiFont.Height + 10;
        Cursor = Cursors.Hand;
    }

    /// <summary>
    /// 第五十七改②第二层修复（真窗口 150% DPI 复验出「缩小到 1…」仍截断）：
    /// 调用方（ExportDialog.BuildContent）摆宽时用 boxH 当箭头宽度的<b>代理</b>，
    /// 而 <see cref="OnDrawItem"/> 的文字盒与 <see cref="PaintArrowChrome"/> 的箭头区
    /// 都用控件的<b>真实 Height</b>——ComboBox 的高度是字体/ItemHeight 决定的内蕴值，
    /// 调用方设不进去；150% DPI 下真实 Height 比 boxH 大出一截，文字盒＝宽−真实高−8
    /// 被挤窄，整串装不下又被 EndEllipsis 收成省略号。电池进程被虚拟化成 96 DPI，
    /// 那里 Height≈boxH、几何关系恒成立，<b>结构上量不出这枚病</b>。
    ///
    /// 挂点为什么是三处（OnHandleCreated ＋ OnVisibleChanged ＋ OnSizeChanged）：
    /// 先只挂 OnHandleCreated 一版，电池开 LS_CHECKS_DPI=aware（PerMonitorV2，判据环境＝
    /// 产品环境）当场红——文字盒 110px 装不下需要 111px 的整串。探针实证两层叠加：
    /// ① handle 创建那一刻 <b>Items.Count==0</b>（条目在构造器后段 RefreshPlan 才加进去，
    ///    但 handle 早被建好），早退分支什么都没做；
    /// ② 最终 Height/Font 在 paint 时才是定值（150% DPI 下 ItemHeight=35、Height=41）。
    /// OnVisibleChanged 在 Show 的同步路径上、此刻条目已在且尺寸已定，是第一枚真正
    /// 能加宽的挂点；OnSizeChanged 兜住之后发生的 DPI 重标定/字体变化。
    /// 只增不减：现量最长条目的不折行宽，宽不够就撑到「最长条目＋一枚真实箭头＋16
    /// 呼吸」；调用方给得更宽（如格式下拉 160px）则原样保留，任何 DPI 下结构性保证
    /// textW＝宽−真实高−8 ≥ 最长条目宽。自己的加宽也会触发 OnSizeChanged，此时
    /// 已满足条件、不再赋值，循环自然终止。
    /// </summary>
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        AdaptWidthToItems();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        AdaptWidthToItems();
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        AdaptWidthToItems();
    }

    private void AdaptWidthToItems()
    {
        if (Items.Count == 0 || DropDownStyle != ComboBoxStyle.DropDownList) return;
        int maxTextW = 0;
        foreach (object item in Items)
        {
            string text = GetItemText(item) ?? "";
            maxTextW = Math.Max(maxTextW, TextRenderer.MeasureText(text, Font,
                new Size(int.MaxValue, int.MaxValue),
                TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine).Width);
        }
        Width = Math.Max(Width, maxTextW + Height + 16);
    }

    /// <summary>
    /// 第五十六改②（用户图2红框「还是老下拉组件」）：收起格右端的箭头按钮区域
    /// 仍由系统按 FlatStyle.Flat 画——白底旧组件的观感就是它，条目自绘（OnDrawItem）
    /// 盖不住这块，因为系统在条目画完<b>之后</b>又把按钮画了上去。
    /// 出路与 <see cref="LongStitch.App.UI.SourcePanel"/> 的空态补画同一族：
    /// base 画完之后在 WM_PAINT 里补自家的那一笔——填底、画圆头折线箭头、补发丝边。
    /// 展开列表是另一个窗口（弹层），不经过这条，行自绘原样管它。
    /// </summary>
    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if (m.Msg != WmPaint) return;
        using Graphics g = Graphics.FromHwnd(Handle);
        PaintArrowChrome(g);
        ArrowPaintCount++;
    }

    private void PaintArrowChrome(Graphics g)
    {
        // 箭头区取整个高度的一格（方中区），宽随 DPI 走——写死 17px 是 96 DPI 的账。
        int arrowW = Height;
        var arrow = new Rectangle(Math.Max(0, Width - arrowW), 0, arrowW, Height);
        using (var bg = new SolidBrush(Theme.WindowBackground))
            g.FillRectangle(bg, arrow);
        var smooth = g.SmoothingMode;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        int cx = arrow.Left + arrow.Width / 2;
        int cy = Height / 2;
        // 圆头折线箭头：与 ChromeCheckBox 的勾同一形状语言，不依赖图标字体在不在。
        using (var chevron = new Pen(Enabled ? Theme.TextSecondary : Theme.TextDisabled, 1.6f)
               {
                   StartCap = System.Drawing.Drawing2D.LineCap.Round,
                   EndCap = System.Drawing.Drawing2D.LineCap.Round,
                   LineJoin = System.Drawing.Drawing2D.LineJoin.Round,
               })
            g.DrawLines(chevron, new[]
            {
                new Point(cx - 4, cy - 2), new Point(cx, cy + 2), new Point(cx + 4, cy - 2),
            });
        g.SmoothingMode = smooth;
        // 发丝边整圈补一遍：系统平边框在黑夜档下偏亮，这里用调色板的 PanelBorder 收口。
        using (var edge = new Pen(Theme.PanelBorder, 1f))
            g.DrawRectangle(edge, 0, 0, Width - 1, Height - 1);
    }

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= Items.Count) return;
        Graphics g = e.Graphics;
        Rectangle row = e.Bounds;
        bool selected = (e.State & DrawItemState.Selected) != 0;
        bool editBox = (e.State & DrawItemState.ComboBoxEdit) != 0;

        // 收起那一格：窗口底 + 发丝边；展开列表行：选中行铺强调 wash，其余行透明底
        using (var bg = new SolidBrush(selected && !editBox ? Chrome.AccentWash : Theme.WindowBackground))
            g.FillRectangle(bg, row);
        if (editBox)
        {
            using var edge = new Pen(Theme.PanelBorder, 1f);
            g.DrawRectangle(edge, 0, 0, Width - 1, Height - 1);
        }

        string text = GetItemText(Items[e.Index]) ?? "";
        // 第五十六改②：收起格的文字不许伸进箭头区（那一格会被补画盖住半截字）；
        // 展开列表行没有箭头区，维持原宽。
        // 第五十七改②（用户图2红框「不…」被截断）：收起格那一枚 DrawItem 的 e.Bounds
        // 本身已经是「去掉系统按钮之后的编辑区」，旧写法在它之上再扣一次 Height，
        // 等于把箭头宽度扣了两遍，文字盒凭空窄了一格箭头——「不缩小」于是只剩「不…」。
        // 改用控件自己的 Width 只扣一次箭头，文字盒＝整宽−一枚箭头−8，与补画那格同源。
        int textW = editBox ? Math.Max(0, Width - Height - 8) : Math.Max(0, row.Width - 10);
        var box = new Rectangle(row.X + 8, row.Y, textW, row.Height);
        TextRenderer.DrawText(g, text, Font, box,
            selected && !editBox ? Theme.TextPrimary : Theme.TextPrimary,
            TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }
}
