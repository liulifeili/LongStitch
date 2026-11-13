namespace LongStitch.App.UI;

using System.Windows.Forms.Layout;

/// <summary>
/// 第五十三改（用户图黄框「元素没有间隙」、绿框「文字的上方没有空隙，太挤了」）：
/// 右侧参数面板专用的停靠布局引擎——<b>吃 Margin 的 Dock=Top 竖排</b>。
///
/// ═══ 为什么必须有它 ═══
/// 本面板的每一行控件其实早就写好了 Margin（按钮 4/4、标签 3/3、说明段 4/8、分组标题 10/4），
/// 但实测（判据 <c>ui/拼缝面板相邻控件留有呼吸间距</c> 首趟）证明默认的 <c>DefaultLayout</c>
/// 对 Dock=Top 的子控件<b>完全不消费 Margin</b>——13 对相邻控件逐对量出来间隙全部是 0px。
/// Margin 这个属性只被 FlowLayoutPanel／TableLayoutPanel 这类布局引擎吃，Dock 停靠不吃，
/// 面板上写的 Margin 一直是死字面量。这就是用户看到的「按钮挤成一整块」。
///
/// ═══ 第五十四改⑦：滚动从 AutoScroll 换成「布局时直接减偏移」═══
/// 旧版把内容总高写进 <c>AutoScrollMinSize</c>——那是与原生滚动条的一个反馈环：
/// 条出现 → 客户区变窄 → 说明段折行变高 → 总高再变 → 再写 → 再触发布局……
/// 「最大化后滚到最底整块抽搐然后卡死」（用户图7）就是这一族自激。
/// 现在滚动偏移由宿主面板通过 <see cref="IStackScrollOffset"/> 交出来，布局把每一行
/// 直接摆到「减过偏移」的位置上；内容总高由宿主自己量（ToolPanel.MeasureExtent，
/// 累加口径与本引擎逐行同账），滚动条是自绘的 <see cref="ChromeScrollBar"/>，
/// 整根叠在面板右留白里——条的显隐不再改变客户区，自激在结构上不可能再发生。
///
/// ═══ 实现约定 ═══
/// ① 叠放次序：从 <see cref="Control.Controls"/> 集合<b>末尾往前</b>排——集合末尾（z 序最底）先排、
///    占外侧（顶部）。ToolPanel.Rebuild 把子控件下标整体翻转成阅读顺序，首行控件恰好排在集合末尾，
///    于是保持「阅读顺序第一行在最上」的现行行为。
/// ② 起点：<see cref="Control.DisplayRectangle"/>——它已经含面板 <see cref="Control.Padding"/>。
/// ③ <see cref="ChromeScrollBar"/> 不参与叠放（它是叠加在右留白上的工具，不是一行内容），
///    也不许被写 Bounds——宿主自己摆它。
/// </summary>
internal sealed class StackedDockLayout : LayoutEngine
{
    /// <summary>
    /// 一次布局里排到几何<b>稳定</b>为止。<see cref="NoteLabel"/> 会在宽度定下来之后自己量折行、
    /// 改自己的高度（见它的 OnSizeChanged）；高度一变，后面所有行都要往下顺移。
    /// 正常两趟收敛（第一趟定宽触发改高、第二趟用新高排完）；上限 6 趟防意外失控。
    /// </summary>
    public override bool Layout(object container, LayoutEventArgs layoutEventArgs)
    {
        if (container is not Control panel) return false;
        int offset = panel is IStackScrollOffset scrolled ? scrolled.StackScrollOffset : 0;

        for (int pass = 0; pass < 6; pass++)
        {
            bool changed = false;
            Rectangle display = panel.DisplayRectangle;
            int y = display.Top - offset;
            for (int i = panel.Controls.Count - 1; i >= 0; i--)
            {
                Control child = panel.Controls[i];
                if (!child.Visible || child is ChromeScrollBar) continue;
                Padding margin = child.Margin;
                var want = new Rectangle(display.Left + margin.Left, y + margin.Top,
                    Math.Max(1, display.Width - margin.Horizontal), child.Height);
                if (child.Bounds != want)
                {
                    child.Bounds = want;
                    changed = true;
                }
                // SetBounds 可能触发 NoteLabel 改高，读它改完之后的高度往下叠
                y += margin.Top + child.Height + margin.Bottom;
            }

            if (!changed) break;
        }
        return false;
    }
}

/// <summary>
/// 竖排停靠布局的滚动偏移出处（第五十四改⑦）。宿主面板实现它、交出当前滚动值；
/// 布局把行摆到「DisplayRectangle 顶 − 偏移」的位置上。符号约定：
/// 正值 = 内容向上滚（第一行已经滚出视口顶部若干像素）。
/// </summary>
internal interface IStackScrollOffset
{
    int StackScrollOffset { get; }
}
