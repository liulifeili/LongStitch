using LongStitch.App.Diagnostics;
using LongStitch.Core.Model;
using LongStitch.Core.ProjectIO;

namespace LongStitch.App.UI;

/// <summary>
/// 外观档位的唯一写入出口（第四十八改）。
///
/// ═══ 三件事只在这里发生 ═══
/// ① 记住用户选了哪一档（<see cref="Mode"/>）；② 把那一档翻译成"现在到底该用哪套配色"
/// 并交给 <see cref="Theme"/>；③ 写盘（<see cref="AppPreferences"/>）。
/// 设置界面那三枚按钮、启动时的回读、跟随系统的轮询，三条路都汇到这一个出口——
/// 与本项目"重叠画法三个出口必须落在同一份状态上"是同一课：
/// 分散写就会出现"界面说跟随、实际却是手动"这种两套真相。
///
/// ═══ 为什么 Load 只在 Program.cs 里调一次 ═══
/// 主窗口构造时<b>不</b>读偏好。原因是离屏工装（Checks）会构造 MainForm，
/// 而工装那台进程不该被开发者自己机器上的偏好文件影响——几百条按深色底数像素的判据
/// 要是跟着"这台机器恰好选了白天"一起翻红，那就不是产品的错。
/// 所以：产品启动读一次，工装不读，默认永远是黑夜（<see cref="ThemePreference.Dark"/> 是枚举 0 档）。
/// </summary>
public static class ThemeController
{
    /// <summary>用户选的那一档。默认黑夜。</summary>
    public static ThemePreference Mode { get; private set; } = ThemePreference.Dark;

    /// <summary>最近一次问到的系统读数（null = 还没问过或读不到）。</summary>
    public static bool? SystemReading { get; private set; }

    /// <summary>
    /// 有话要说的时候在这里（偏好读坏了／写不进去／系统读数取不到）。
    /// 界面原样显示，不许吞掉：静默退回与"本来就选了黑夜"在屏上分不出来。
    /// </summary>
    public static string? Note { get; private set; }

    /// <summary>
    /// 档位、生效配色、系统读数或说明有任何一项真的变了时广播一次。
    /// 什么都没变就不广播——跟随系统那枚轮询每 2 秒一趟，空转的广播会让整棵控件树白翻一遍。
    /// </summary>
    public static event Action? ModeChanged;

    /// <summary>启动时回读一次（只有 Program.cs 调）。</summary>
    public static void Load(string? overridePath = null)
    {
        AppPreferences prefs = AppPreferences.Load(overridePath);
        Mode = prefs.Theme;
        Note = AppPreferences.LastLoadNote;
        ApplyEffective();
        DiagnosticLog.Info("theme.loaded", $"mode={AppPreferences.Token(Mode)} dark={Theme.IsDark} note={(Note is null ? "none" : "present")}");
        ModeChanged?.Invoke();
    }

    /// <summary>
    /// 用户点了一枚按钮：记住这一档、写盘、立刻生效。
    /// 写盘失败不影响本次生效（说明留在 <see cref="Note"/> 里由界面说出来）。
    /// </summary>
    public static void SetMode(ThemePreference mode, string? overridePath = null)
    {
        Mode = mode;
        Note = null;
        AppPreferences.Save(new AppPreferences(mode), overridePath);
        if (AppPreferences.LastSaveNote is { } saveNote) Note = saveNote;
        ApplyEffective();
        DiagnosticLog.Info("theme.mode_set", $"mode={AppPreferences.Token(mode)} dark={Theme.IsDark} saved={(AppPreferences.LastSaveNote is null)}");
        ModeChanged?.Invoke();
    }

    /// <summary>
    /// 再问一次系统（轮询与窗口激活都走这里）。只有"跟随系统"档才会因此换配色；
    /// 但无论哪一档都把读数记下来，因为设置界面要显示"系统当前：…"那一句。
    /// </summary>
    public static void RefreshSystemReading()
    {
        bool? before = SystemReading;
        string? noteBefore = Note;
        ThemePreference modeBefore = Mode;
        bool darkBefore = Theme.IsDark;

        SystemReading = SystemTheme.TryReadIsLight();
        if (Mode == ThemePreference.FollowSystem) ApplyEffective();

        bool changed = before != SystemReading || noteBefore != Note
                       || modeBefore != Mode || darkBefore != Theme.IsDark;
        if (changed) ModeChanged?.Invoke();
    }

    /// <summary>现在生效的那一档的中文名。</summary>
    public static string EffectiveLabel => Theme.IsDark ? "黑夜" : "白天";

    /// <summary>用户选的那一档的中文名。</summary>
    public static string ModeLabel => Mode switch
    {
        ThemePreference.Light => "白天",
        ThemePreference.FollowSystem => "跟随系统",
        _ => "黑夜",
    };

    /// <summary>给状态栏与设置界面用的整句（选了什么 + 现在实际是哪一档）。</summary>
    public static string Describe() =>
        Mode == ThemePreference.FollowSystem
            ? $"外观：跟随系统（当前生效：{EffectiveLabel}）"
            : $"外观：{ModeLabel}";

    /// <summary>
    /// 把档位翻译成配色。跟随系统时读不到就退回黑夜，并且把这件事写进 <see cref="Note"/>——
    /// 这一条是需求级的诚实：不许把"没读到"装成"读到了深色"。
    /// </summary>
    private static void ApplyEffective()
    {
        bool dark;
        if (Mode == ThemePreference.Dark)
        {
            dark = true;
        }
        else if (Mode == ThemePreference.Light)
        {
            dark = false;
        }
        else
        {
            SystemReading = SystemTheme.TryReadIsLight();
            if (SystemReading is { } light)
            {
                dark = !light;
                // 读到了就把上一轮那句"读不到"撤掉，否则说明会一直挂在界面上吓人。
                if (Note == UnreadableNote) Note = null;
            }
            else
            {
                dark = true;
                Note = UnreadableNote;
            }
        }
        Theme.UsePalette(dark ? ThemePalette.Dark : ThemePalette.Light);
    }

    /// <summary>读不到系统读数时那句说明。写成常量是为了能在"读到了"那一支精确地把它撤掉。</summary>
    private const string UnreadableNote = "读不到系统的深浅色设置，已退回黑夜（可在上面直接选白天或黑夜）";
}