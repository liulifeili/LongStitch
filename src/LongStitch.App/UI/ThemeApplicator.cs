namespace LongStitch.App.UI;

/// <summary>
/// 换肤时把整棵控件树上的旧配色翻译成新配色（第四十八改）。
///
/// ═══ 为什么需要这一层 ═══
/// WinForms 的颜色是<b>构造期抄下来的值</b>：<c>BackColor = Theme.PanelBackground</c>
/// 这一行执行完，控件里存的就是一枚具体的 ARGB，此后 Theme 换了它也不知道。
/// 要么在每个构造点都改成"订阅主题变化再刷一次"（几十个文件、几百处，必漏），
/// 要么换肤时遍历一遍控件树统一翻译。这里选后者。
///
/// ═══ 为什么是"翻译"而不是"重新赋一遍标准色" ═══
/// 重新赋值得先知道每枚控件<b>本来该是什么角色</b>（面板底？列表底？输入框底？），
/// 那等于把几百处构造代码再抄一遍。翻译只需要一张"旧档 ARGB → 新档 ARGB"的表：
/// 认得的就换，认不得的原样留着。于是用户图片、透明色、以及那些故意写死的白字
/// （选中行上的白字两档都压在强调蓝上）都不会被误伤。
///
/// ═══ 幂等（这条性质有一枚前提，不许当成天上掉的）═══
/// 翻完之后控件身上是新档的颜色，而翻译表的键是旧档的颜色 ⇒ 再翻一遍什么都不会发生。
/// 主窗口与设置面板可能各翻一次，重复不许把颜色越翻越偏，所以这条性质是有意要的。
///
/// <b>但"再翻一遍什么都不发生"只在两档的颜色互不撞键时成立</b>：若某枚新档颜色恰好等于某枚旧档颜色，
/// 第二遍就会把它当成旧档再翻一次。本仓库现场撞上一枚（浅色档正文原本取 (28,28,30)，
/// 正是黑夜档的窗口底），后果是白底上的近白字——整段文字在屏上消失；而这条路每次从设置窗口切换都走：
/// 主窗口 <c>ApplyEverywhere</c> 翻一遍、对话框自己的订阅再翻一遍、外观那一块还有第三遍。
/// 修在调色板那头（浅色档正文改成 (26,26,28)），这一层不改；前提由
/// <c>guard/翻译表的目标色不许自己又是旧档的键</c> 逐枚钉住，改配色时它会替这一层说话。
/// </summary>
public static class ThemeApplicator
{
    /// <summary>
    /// 翻译一棵控件树，返回真的改了几枚颜色（判据拿它当读数：0 就说明这一趟根本没翻）。
    /// </summary>
    public static int Apply(Control root)
    {
        int changed = 0;
        Walk(root, ref changed);
        root.Invalidate(invalidateChildren: true);
        return changed;
    }

    /// <summary>
    /// 把所有开着的窗口都翻一遍，另外再翻一次 <paramref name="owner"/>。
    ///
    /// 为什么要带 owner：<c>Application.OpenForms</c> 只收<b>真的 Show 过</b>的窗口，
    /// 离屏工装里的主窗口不在里面。产品运行时两条路会重叠，但翻译是幂等的（见类注释），
    /// 重叠那一趟翻不动任何东西。
    /// </summary>
    public static int ApplyEverywhere(Control? owner)
    {
        int changed = 0;
        var seen = new HashSet<Control>();
        foreach (Form open in Application.OpenForms)
        {
            if (!seen.Add(open)) continue;
            changed += Apply(open);
        }
        if (owner is not null && seen.Add(owner)) changed += Apply(owner);
        return changed;
    }

    private static void Walk(Control control, ref int changed)
    {
        changed += TranslateProperty(control.BackColor, v => control.BackColor = v);
        changed += TranslateProperty(control.ForeColor, v => control.ForeColor = v);

        // 扁平按钮的边框与悬停/按下底色不在 BackColor 里，漏翻就会出现"浅色面板上还剩一圈深色边"。
        if (control is ButtonBase { FlatStyle: FlatStyle.Flat } button)
        {
            changed += TranslateProperty(button.FlatAppearance.BorderColor, v => button.FlatAppearance.BorderColor = v);
            changed += TranslateProperty(button.FlatAppearance.MouseOverBackColor, v => button.FlatAppearance.MouseOverBackColor = v);
            changed += TranslateProperty(button.FlatAppearance.MouseDownBackColor, v => button.FlatAppearance.MouseDownBackColor = v);
        }

        if (control is LinkLabel link)
        {
            changed += TranslateProperty(link.LinkColor, v => link.LinkColor = v);
            changed += TranslateProperty(link.ActiveLinkColor, v => link.ActiveLinkColor = v);
            changed += TranslateProperty(link.VisitedLinkColor, v => link.VisitedLinkColor = v);
            changed += TranslateProperty(link.DisabledLinkColor, v => link.DisabledLinkColor = v);
        }

        // ToolStrip 的按钮/标签/菜单项不是 Control，是 ToolStripItem，得单独走一遍。
        // 渲染器（DarkToolStripRenderer）本身每次绘制都现读 Theme，不需要重建；
        // 但顶栏「导出」那枚按钮的 BackColor/ForeColor 是构造期抄的，必须翻。
        if (control is ToolStrip strip)
        {
            WalkItems(strip.Items, ref changed);
            strip.Invalidate();
        }

        foreach (Control child in control.Controls) Walk(child, ref changed);
        control.Invalidate();
    }

    private static void WalkItems(ToolStripItemCollection items, ref int changed)
    {
        foreach (ToolStripItem item in items)
        {
            changed += TranslateProperty(item.BackColor, v => item.BackColor = v);
            changed += TranslateProperty(item.ForeColor, v => item.ForeColor = v);
            if (item is ToolStripDropDownItem dropDown)
            {
                WalkItems(dropDown.DropDownItems, ref changed);
                dropDown.Invalidate();
            }
        }
    }

    /// <summary>认得才改；改了就记一笔。用 setter 委托是为了让"读→翻→写"这三步只写一遍。</summary>
    private static int TranslateProperty(Color current, Action<Color> set)
    {
        Color next = Theme.Translate(current);
        if (next.ToArgb() == current.ToArgb()) return 0;
        set(next);
        return 1;
    }
}