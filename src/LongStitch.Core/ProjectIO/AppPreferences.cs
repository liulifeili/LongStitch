using LongStitch.Core.Model;

namespace LongStitch.Core.ProjectIO;

/// <summary>
/// 应用级偏好（第四十八改）。目前只有一项：界面外观（白天／黑夜／跟随系统）。
///
/// ═══ 与"项目文件"的分工 ═══
/// 项目文件（project.json / .lstickx）装的是<b>这一份拼合的参数</b>，跟着项目走、要能拷给别人；
/// 这一份装的是<b>这台机器上这个人的界面习惯</b>，跟着人走，绝不进项目文件——
/// 否则把项目发给同事，对方的界面会莫名其妙跟着变。
///
/// ═══ 为什么是纯文本而不是 JSON ═══
/// 只有一枚枚举，纯文本 `theme=dark` 一眼就能看懂、也能用手改（本项目的读者自认是小白，
/// 能打开记事本核对"我到底选了哪一档"比省几行解析代码值钱）。
/// 形状照 <see cref="RecentProjects"/>：同一个 %APPDATA%\LongStitch 目录、同样带 overridePath
/// 让判据往临时目录里写、同样"读写失败不许把程序搞崩"。
///
/// ═══ 隐私 ═══
/// 这一份文件里只有档位名，不含路径清单以外的任何内容，更不含像素。
/// 读坏了的时候 <see cref="LastLoadNote"/> 只说"读不出来"与异常类型名，不回显文件内容。
/// </summary>
public sealed record AppPreferences(ThemePreference Theme)
{
    /// <summary>文件里那一行的键名。判据与文档都按这个字面量对账。</summary>
    public const string ThemeKey = "theme";

    /// <summary>
    /// 整进程改写存储位置（null = 用真的 <see cref="StorePath"/>）。
    ///
    /// ═══ 为什么需要这一枚 ═══
    /// 界面上那三枚按钮调的是 <c>ThemeController.SetMode(mode)</c>——<b>不带路径</b>，因为产品运行时
    /// 就该写用户真的那一份。可判据要真点那三枚按钮（点处理函数等于没量通路），
    /// 于是那一下会顺着"不带路径"这条路写进用户真的 %APPDATA%：跑一趟电池就把开发者自己选的档位冲掉了。
    /// 本仓库第一趟就是这么红的——判据量临时文件"写盘了没有"，产品却写了真的那一份，两头都没错，错在缺一个改道闸。
    /// 所以这里给一个进程级的改道闸：判据开工时指向临时文件、收工时还回 null，
    /// 产品代码一个字不用改（它照旧不带路径）。
    /// </summary>
    public static string? StoreOverride { get; set; }

    /// <summary>存储文件路径（目录不存在时由写入方创建）。</summary>
    public static string StorePath => StoreOverride ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "LongStitch", "preferences.txt");

    /// <summary>
    /// 上一次 <see cref="Load"/> 有没有话要说（null = 一切正常）。
    /// 界面把这句话原样显示出来：偏好读坏了却"看起来正常"，用户下次启动只会以为软件忘了他的选择。
    /// </summary>
    public static string? LastLoadNote { get; private set; }

    /// <summary>
    /// 读偏好。文件不存在 → 黑夜（首次运行）；文件读坏了 → 黑夜 + 一句说明。
    /// 任何失败都不许抛：外观偏好属于便利功能，不许因为它把启动搞崩。
    /// </summary>
    public static AppPreferences Load(string? overridePath = null)
    {
        string store = overridePath ?? StorePath;
        LastLoadNote = null;
        try
        {
            if (!File.Exists(store)) return new AppPreferences(ThemePreference.Dark);
            string text = File.ReadAllText(store);
            ThemePreference mode = Parse(text);
            // 解析不出档位时 Parse 退回黑夜，但那是"文件在、内容不对"，与"文件不存在"是两件事，
            // 后者不需要解释、前者需要。这里用"原文里有没有我们认得的写法"来分辨。
            if (mode == ThemePreference.Dark && !LooksKnown(text))
                LastLoadNote = "偏好文件里没有认得的档位写法，已退回黑夜";
            return new AppPreferences(mode);
        }
        catch (Exception ex)
        {
            LastLoadNote = $"偏好文件读不出来（{ex.GetType().Name}），已退回黑夜";
            return new AppPreferences(ThemePreference.Dark);
        }
    }

    /// <summary>
    /// 写偏好。写不进去只是"下次启动记不住这一档"，本次切换照样生效，
    /// 所以这里吞掉异常但把它交给调用方去说（见 <see cref="LastSaveNote"/>）。
    /// </summary>
    public static void Save(AppPreferences preferences, string? overridePath = null)
    {
        string store = overridePath ?? StorePath;
        LastSaveNote = null;
        try
        {
            string? dir = Path.GetDirectoryName(store);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(store, Serialize(preferences.Theme));
        }
        catch (Exception ex)
        {
            LastSaveNote = $"偏好没记住（{ex.GetType().Name}）：本次切换已生效，下次启动会回到上一次的档位";
        }
    }

    /// <summary>上一次 <see cref="Save"/> 失败时的说明（null = 写成功了）。</summary>
    public static string? LastSaveNote { get; private set; }

    /// <summary>写成文件内容。带一行注释，让打开记事本的人知道这是什么、可以填哪几个词。</summary>
    public static string Serialize(ThemePreference mode) =>
        "# LongStitch 界面偏好（只有外观档位，不含任何图像内容）\n" +
        "# theme 可以填：dark（黑夜）/ light（白天）/ system（跟随系统）\n" +
        ThemeKey + "=" + Token(mode) + "\n";

    /// <summary>
    /// 从文件内容里取档位。认不得的一律退回黑夜（枚举的 0 档），
    /// 大小写与前后空白都不计较；也认中文写法，因为这份文件是给人手改的。
    /// </summary>
    public static ThemePreference Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return ThemePreference.Dark;
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            if (!line[..eq].Trim().Equals(ThemeKey, StringComparison.OrdinalIgnoreCase)) continue;
            return FromToken(line[(eq + 1)..].Trim());
        }
        return ThemePreference.Dark;
    }

    /// <summary>档位 → 文件里那个词。</summary>
    public static string Token(ThemePreference mode) => mode switch
    {
        ThemePreference.Light => "light",
        ThemePreference.FollowSystem => "system",
        _ => "dark",
    };

    /// <summary>文件里那个词 → 档位。认不得就退回黑夜。</summary>
    public static ThemePreference FromToken(string? token) => (token ?? "").Trim().ToLowerInvariant() switch
    {
        "light" or "day" or "白天" or "浅色" => ThemePreference.Light,
        "system" or "followsystem" or "follow-system" or "跟随系统" or "跟随" => ThemePreference.FollowSystem,
        _ => ThemePreference.Dark,
    };

    /// <summary>
    /// 这份内容里有没有我们认得的档位写法。
    /// 单独抽出来只为一件事：把"文件不存在"与"文件在但写坏了"分开——前者不必解释，后者必须解释。
    /// </summary>
    private static bool LooksKnown(string text)
    {
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            if (!line[..eq].Trim().Equals(ThemeKey, StringComparison.OrdinalIgnoreCase)) continue;
            string v = line[(eq + 1)..].Trim().ToLowerInvariant();
            if (v is "dark" or "light" or "system" or "day" or "followsystem" or "follow-system"
                or "黑夜" or "深色" or "白天" or "浅色" or "跟随系统" or "跟随")
                return true;
        }
        return false;
    }
}