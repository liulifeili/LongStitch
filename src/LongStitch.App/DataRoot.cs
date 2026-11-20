namespace LongStitch.App;

/// <summary>
/// 数据根目录解析（第六十四改）。
///
/// ═══ 这一枚解决什么问题 ═══
/// 用户要求发布两种形态：绿色版（解压到任何目录都能用，包括中文目录）与安装版（可选安装盘），
/// 并且"即使安装到非 C 盘，也要减少在 C 盘乱堆文件的习惯"。可本工程原有的三个存储出口
/// （偏好 <c>preferences.txt</c>、最近项目 <c>recent.txt</c>、诊断日志）全部按 Windows 惯例
/// 写进用户配置目录（%APPDATA% / %LOCALAPPDATA% 下的 LongStitch 子目录）——
/// 这些目录<b>永远在 C 盘</b>，装到 D 盘也拦不住。于是要给用户一个看得见、摸得着的开关：
///
///     若 exe 同目录下存在 <c>data</c> 文件夹，三个存储出口全部落到 <c>data</c> 里面；
///     不存在则维持 Windows 惯例（老用户升级后行为不变）。
///
/// 两种发布形态都会在包里放一个空的 <c>data</c> 文件夹，所以装/解到哪个盘，数据就跟到哪个盘，
/// 删掉整个目录 = 数据一并清干净——这才是"绿色"该有的样子。
///
/// ═══ 为什么是"文件夹存在"而不是标记文件或命令行参数 ═══
/// 用户自认是小白：命令行参数看不见；标记文件（如 portable.flag）打不开看不懂。
/// 一个叫 data 的文件夹，在资源管理器里一眼就知道"我的东西在这里"，
/// 想挪位置就把整个目录拖走——这是最不解释也能理解的形状。
///
/// ═══ 与 <c>StoreOverride</c> 的分工 ═══
/// AppPreferences / RecentProjects 上那枚 override 是<b>测试专用接缝</b>，架构文档写明
/// "产品代码不读它"，判据工装靠它把写入改道到沙箱。所以产品级改道<b>另立字段</b>
/// （DefaultStore / DefaultDirectory），优先级排在其下：
///     测试闸（StoreOverride） &gt; 产品默认（DefaultStore） &gt; Windows 惯例目录
/// 这样工装沙箱永远赢——哪怕哪天判据在装了 data 文件夹的机器上跑，也不会往真 data 里写。
///
/// ═══ 失败模式 ═══
/// 解析本身只在内存里做（BaseDirectory + Directory.Exists），理论上抛不出异常，
/// 但按本工程的惯例仍整体兜住：解析失败当作"没有 data"，退回 Windows 惯例目录，
/// 绝不许因为这层把启动搞崩。真正的写入失败（比如用户把目录放进了只读位置）
/// 由各存储出口自己兜——它们的惯例本来就是"写不进去不许崩"。
/// </summary>
public static class DataRoot
{
    /// <summary>exe 旁边那个数据文件夹的名字。两种发布形态都在包里放它，文档与安装器都按这个字面量对账。</summary>
    public const string FolderName = "data";

    /// <summary>
    /// 解析数据根目录。返回 null 表示没有 data 文件夹，各存储出口退回 Windows 惯例目录。
    /// 只在启动时调一次（结果缓存在调用方手里），不缓存到本类里——
    /// 离屏判据工装一趟建几十个窗口，谁先调谁认目录的顺序耦合是隐患，干脆让入口自己持有结果。
    /// </summary>
    public static string? Resolve()
    {
        try
        {
            // AppContext.BaseDirectory 在单文件与普通目录两种发布形态下都指向 exe 所在目录，
            // 不受当前工作目录影响（用户从快捷方式/命令行任何位置启动都一样）。
            string candidate = Path.Combine(AppContext.BaseDirectory, FolderName);
            return Directory.Exists(candidate) ? candidate : null;
        }
        catch
        {
            return null;
        }
    }
}
