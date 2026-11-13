namespace LongStitch.App.UI;

/// <summary>
/// 第五十五改④（用户图4「滚动条学右侧红框那种蓝色粗圆角线条」）：
/// 给 <see cref="ListBox"/> 套的滚动条宿主——把原生滚动条藏到界外，换成 <see cref="ChromeScrollBar"/>。
///
/// ═══ 为什么要这么绕 ═══
/// 54改⑥ 已经给了自绘滚动条（<see cref="ChromeScrollBar"/>，蓝色圆弧胶囊），但它当时只接在
/// ToolPanel 的手动滚动面板上。左栏素材列表与右栏拼缝列表是两枚<b>原生</b> <see cref="ListBox"/>——
/// 原生滚动条不吃调色板，黑夜档下是一根白条（用户图4 黄框），产品内禁 P/Invoke，
/// WinForms 也没有"只换滚动条皮肤"的口子。唯一的纯托管出路是几何裁剪：
/// ListBox 比宿主<b>宽出</b>一根系统滚动条的宽度 + 8px，原生条（贴在 ListBox 窗口右缘）
/// 整根落在宿主界外被裁掉；子窗口永远被裁进父窗口客户区，这是窗口管理的铁律，不用碰一行 API。
///
/// ═══ 三条同步链 ═══
/// ① 滚轮：列表子类 <see cref="ChromeScrollListBox"/> 在 WndProc 里拦下 WM_MOUSEWHEEL
///    （托管重写，非 P/Invoke），转给宿主的 <see cref="ChromeScrollBar"/>；不让原生滚动，
///    也不让滚轮偷偷动选中项。
/// ② 条 → 列表：<see cref="ChromeScrollBar.ValueChanged"/> 把像素偏移换算回 <c>TopIndex</c>。
/// ③ 列表 → 条：键盘↑↓、程序设置 SelectedIndex、原生 autoscroll 都会改 TopIndex 而不经过条
///    ——统一靠 <c>DrawItem</c>（行被画出来的那一刻）回读同步，加上刷新方法末尾显式
///    <see cref="SyncFromList"/> 兜底。两条链都有恒等收敛：值相同时 setter 原路返回，不会对振。
///
/// ═══ 行宽账 ═══
/// 行自绘拿到的 <c>e.Bounds</c> 是整个（加宽后的）列表宽，右端有一截在界外。
/// 宿主把"真正可见的行宽"写进 <see cref="ChromeScrollListBox.RowWidth"/>，
/// 画行的一方用它代替 <c>e.Bounds.Width</c>，卡片才不会右半截被吃掉。
/// </summary>
internal sealed class ChromeScrollHost : Panel
{
    private const int WheelNotch = 120;
    private const int NativeScrollbarClearance = 8;   // 原生条之外再留的余量
    private const int BarInset = 2;

    private readonly ListBox _list;
    private readonly ChromeScrollBar _bar = new();
    private bool _syncing;

    /// <summary>宿主里那枚自绘滚动条（判据要读它的显隐与值）。</summary>
    internal ChromeScrollBar Bar => _bar;

    public ChromeScrollHost(ListBox list)
    {
        _list = list;
        BackColor = list.BackColor;
        if (list is ChromeScrollListBox chromeList)
        {
            chromeList.Host = this;
        }
        else
        {
            // 宿主只认 ChromeScrollListBox：普通 ListBox 的滚轮会被原生滚动条吃掉，
            // 界外裁剪对它照样成立，但滚轮就没法走自绘条了。两个调用点都是子类，这条只兜底。
            throw new ArgumentException("ChromeScrollHost 需要 ChromeScrollListBox（滚轮拦截在其 WndProc 里）");
        }

        _bar.ValueChanged += () =>
        {
            int rowHeight = Math.Max(1, _list.ItemHeight);
            int top = _bar.Value / rowHeight;
            _list.TopIndex = top;
            // 实量：原生 ListBox 对本次会话的第一发 LB_SETTOPINDEX 可能静默拒收
            // （读回还是旧值，之后同一操作就收）。真窗口里这就是"滚轮第一格死掉"，
            // 所以这里读回核对、被拒就补一发；补发幂等（值已生效时读回相等、不再发）。
            if (_list.TopIndex != top) _list.TopIndex = top;
        };
        // 行被画出来的那一刻回读一次 TopIndex：键盘↑↓、程序设 SelectedIndex、原生 autoscroll
        // 都只改 TopIndex 不经过条，这里是唯一的"列表 → 条"通道（见类注释②③）。
        _list.DrawItem += (_, _) => SyncFromList();

        Controls.Add(_list);
        Controls.Add(_bar);
        Controls.SetChildIndex(_bar, 0);   // 条要叠在列表上面（列表是先加的、默认沉底）
    }

    /// <summary>宿主尺寸一变就重摆两件东西并重算行宽。</summary>
    protected override void OnResize(EventArgs eventargs)
    {
        base.OnResize(eventargs);
        LayoutChildren();
        SyncFromList();
    }

    private void LayoutChildren()
    {
        // ListBox 比宿主宽出"系统滚动条宽 + 余量"：原生条贴在 ListBox 窗口右缘，
        // 正好整根落在宿主界外，被子窗口裁进父客户区的铁律裁掉。
        int extra = SystemInformation.VerticalScrollBarWidth + NativeScrollbarClearance;
        _list.SetBounds(0, 0,
            Math.Max(0, ClientSize.Width + extra),
            Math.Max(0, ClientSize.Height));
        _bar.SetBounds(Math.Max(0, ClientSize.Width - _bar.Width - BarInset), BarInset,
            _bar.Width, Math.Max(0, ClientSize.Height - BarInset * 2));
        // 可见行宽 = 宿主宽 − 条占位 − 一点呼吸位；画行的一方按这个宽收卡片右沿。
        if (_list is ChromeScrollListBox chromeList)
            chromeList.RowWidth = Math.Max(0, ClientSize.Width - _bar.Width - BarInset - 4);
    }

    /// <summary>把列表当前的滚动状态读回条上（内容高、可视高、像素偏移三件一起算）。</summary>
    public void SyncFromList()
    {
        int rowHeight = Math.Max(1, _list.ItemHeight);
        _bar.Maximum = _list.Items.Count * rowHeight;
        _bar.LargeChange = Math.Max(1, _list.ClientSize.Height);
        if (_syncing) return;
        _syncing = true;
        try { _bar.Value = _list.TopIndex * rowHeight; }
        finally { _syncing = false; }
    }

    /// <summary>滚轮一格走 <see cref="ChromeScrollBar.WheelStep"/> 像素；delta 是 ±120 的倍数。</summary>
    internal void ScrollByWheel(int delta)
    {
        if (!_bar.ThumbVisible) return;
        _bar.Value += -delta * ChromeScrollBar.WheelStep / WheelNotch;
        // 立刻把条上的像素偏移与列表 TopIndex 对齐：像素取整会留 ±(行高-1) 的尾巴，
        // 不当场抹平的话"条上的值"与"列表的位置"要等到下一次重画才一致。
        SyncFromList();
    }
}

/// <summary>
/// <see cref="ListBox"/> 子类：拦滚轮 + 记可见行宽。
/// <see cref="SourcePanel"/> 的空态补画子类（HintListBox）与主窗口的拼缝列表都从它派生。
/// </summary>
internal class ChromeScrollListBox : ListBox
{
    private const int WmMouseWheel = 0x020A;

    /// <summary>宿主（托管滚动条的那块面板）；没进宿主时为 null，滚轮走原生。</summary>
    internal ChromeScrollHost? Host { get; set; }

    /// <summary>
    /// 宿主里真正可见的行宽（<see cref="ChromeScrollHost"/> 写入）。
    /// 0 = 未托管，画行的一方退回 <c>e.Bounds.Width</c>。
    /// </summary>
    internal int RowWidth { get; set; }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmMouseWheel && Host is not null)
        {
            // wParam 高 16 位是滚轮增量（±120 的倍数，短整型有符号）。
            short delta = unchecked((short)((long)m.WParam >> 16));
            Host.ScrollByWheel(delta);
            return;   // 不交给原生：原生会自己滚一遍（双重滚动），还会顺手动选中项
        }
        base.WndProc(ref m);
    }
}
