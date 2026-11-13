using Microsoft.Win32;

namespace LongStitch.App.UI;

/// <summary>
/// 读 Windows 的「应用模式」深浅色（第四十八改的"跟随系统"那一档）。
///
/// ═══ 为什么读注册表而不是调 Win32 ═══
/// 本项目有一条硬约束：<b>产品内不许 P/Invoke</b>（只有 Checks 工装可以投消息）。
/// 系统深浅色没有托管 API 直接给（.NET 8 的 WinForms 还没有 Application.SetColorMode，
/// 那是 .NET 9 才有的），而 DwmSetWindowAttribute / ShouldAppsUseDarkMode 都要 P/Invoke。
/// 剩下这条零依赖的路：Windows 把用户选的那一档写在
/// <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize</c> 的
/// <c>AppsUseLightTheme</c>（REG_DWORD，1=浅色、0=深色）里，
/// 而 <see cref="Registry"/> 就在 .NET 的基础共享框架里（Microsoft.Win32.Registry.dll），
/// 不需要引任何 NuGet 包。
///
/// ═══ 为什么是轮询而不是事件 ═══
/// 托管那条 SystemEvents.UserPreferenceChanged 会常驻一条原生窗口线程，
/// 在离屏工装里既不好收也不好判；而注册表本身没有托管的变更通知。
/// 所以由主窗口挂一枚 2 秒的 WinForms Timer 只在"跟随系统"档开着时问一次，
/// 外加窗口被激活时立刻再问一次（用户从 Windows 设置切回来那一下不用等）。
/// 一次就是一枚注册表读，代价可以忽略；2 秒内跟上，这件事写在设置界面那行说明里。
///
/// ═══ 读不到怎么办 ═══
/// 返回 null，由 <see cref="ThemeController"/> 退回黑夜<b>并且说出来</b>。
/// 不许把"读不到"静默当成"系统就是深色"——那两种情况在屏上长得一模一样，
/// 用户只会以为跟随坏了。判据 <c>ui/跟随系统读不到时必须退回黑夜并且出声</c> 量的就是这一条。
/// </summary>
public static class SystemTheme
{
    /// <summary>用户那一级键（不动 HKLM，也不需要管理员权限）。</summary>
    public const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    /// <summary>值名：1 = 浅色（白天），0 = 深色（黑夜）。</summary>
    public const string ValueName = "AppsUseLightTheme";

    /// <summary>
    /// 取数出口。判据往这里注入一个假的读数（true=浅色／false=深色／null=读不到），
    /// 于是"跟随系统"这条通路能在不碰真注册表的离线工装里被量到。
    /// 产品自己永远走 <see cref="ReadFromRegistry"/>。
    /// </summary>
    public static Func<bool?> Probe { get; set; } = ReadFromRegistry;

    /// <summary>问一次系统是不是浅色。null = 读不到（键不在、值不在、类型不对、权限不够）。</summary>
    public static bool? TryReadIsLight()
    {
        try
        {
            return Probe();
        }
        catch
        {
            // 注入的探针自己抛也算"读不到"：这一层对上游只承诺三态，不许把异常甩给 UI。
            return null;
        }
    }

    /// <summary>给界面显示的那句话（不要只显示"跟随系统"四个字，用户要知道跟到了哪一档）。</summary>
    public static string Describe()
    {
        bool? light = TryReadIsLight();
        return light switch
        {
            true => "系统当前：浅色（白天）",
            false => "系统当前：深色（黑夜）",
            null => "系统当前：读不到（已退回黑夜）",
        };
    }

    /// <summary>真读数。REG_DWORD 在托管这边通常是 int，也可能是 long，两种都认。</summary>
    private static bool? ReadFromRegistry()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
        if (key is null) return null;
        object? value = key.GetValue(ValueName);
        return value switch
        {
            int i => i != 0,
            long l => l != 0,
            _ => null,
        };
    }
}