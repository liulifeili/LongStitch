namespace LongStitch.Core.Model;

/// <summary>
/// 界面外观偏好（第四十八改：用户要「白天黑夜切换」与「是否跟随系统」两枚按钮）。
///
/// ═══ 为什么这一枚枚举放在 Core 而不是 App ═══
/// 它要被 <see cref="LongStitch.Core.ProjectIO.AppPreferences"/> 写进磁盘、也要被 App 的调色板读，
/// 而 Core 不认识 System.Drawing，而 App 不认识存储格式。把"用户选了哪一档"这件事
/// 表达成一个纯枚举，两边就都不用认识对方的细节。
///
/// ═══ 为什么 Dark 必须是 0 ═══
/// 需求（模拟 UI §6）与本项目此前全部行为都是深色中性底，几百条常驻判据也是按深色底数的像素。
/// 把 Dark 放在 0，`default(ThemePreference)` 与"偏好文件不存在／读坏了"这两条路
/// 就都天然落在黑夜上——不需要谁记得去写一句兜底。这是结构上的默认，不是约定俗成的默认。
/// </summary>
public enum ThemePreference
{
    /// <summary>黑夜（深色）。默认档，也是读不到偏好时的退路。</summary>
    Dark = 0,

    /// <summary>白天（浅色）。</summary>
    Light = 1,

    /// <summary>
    /// 跟随 Windows 的「设置 → 个性化 → 颜色 → 选择应用模式」。
    /// 系统那一枚读不出来时退回黑夜，并且要在界面上说出"读不到"这件事，
    /// 不许静默装作跟随成功（那与"跟随了但系统就是深色"在屏上长得一模一样）。
    /// </summary>
    FollowSystem = 2,
}