using System.Text;
using LongStitch.Core.ProjectIO;

namespace LongStitch.Checks;

/// <summary>
/// 精度电池入口。
///
/// 用法：
///   dotnet run --project checks/LongStitch.Checks
///   dotnet run --project checks/LongStitch.Checks -- --only png      # 只跑某一组
///
/// 退出码：0 = 全部通过；1 = 有失败。CI / 提交前门禁直接看退出码即可。
/// 注意"跳过"不算通过，会在末尾单独计数——没跑的检查不许被省略成看起来跑过了。
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        // Windows 控制台默认代码页不是 UTF-8，中文输出会花；显式设置
        try { Console.OutputEncoding = Encoding.UTF8; } catch (IOException) { /* 重定向到文件时可能失败，忽略 */ }

        // ═══ 第五十七改②：判据环境开关 ═══
        // 电池进程默认被虚拟化成 96 DPI（历史几百格几何判据都按这个环境标定，不能动）；
        // 但有些高 DPI 几何病在 96 DPI 恒绿——比如缩小下拉收起格截断：文字盒＝宽−真实高−8，
        // 高 DPI 下真实 Height 比调用方摆宽用的代理值大，格子被挤窄。
        // 设 LS_CHECKS_DPI=aware 让本进程按 PerMonitorV2 真跑，与本机 150% DPI 的产品
        // 环境一致——"判据环境必须与产品环境一致"就是这一条规矩的落地口。
        if (Environment.GetEnvironmentVariable("LS_CHECKS_DPI") == "aware")
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

        // ═══ 工装进程一启动就把两个"写用户真实配置"的出口改道到沙箱 ═══
        // 这不是为了某一格判据好看，是<b>隐私</b>：判据会经产品保存项目、真点外观那三枚按钮，
        // 而产品那几个调用点都是 `Add(path)` / `SetMode(mode)`——<b>不带路径</b>，默认值就是真路径。
        // 于是"跑一趟电池"会在开发者机器上留下两份真文件：
        //   %APPDATA%\LongStitch\preferences.txt（外观档位）
        //   %APPDATA%\LongStitch\recent.txt（最近项目＝一份"我在哪些截图上工作过"的清单）
        // 第一份已经红出来过一次（判据量临时文件、产品却写了真的那份），当时只在 ThemeTests 的沙箱里改道；
        // 这一趟把改道提到<b>整个工装进程</b>的入口，两份一起收，任何一组判据都不可能再漏出去。
        // 判据 ui/离屏工装的两个存储出口都必须改道到沙箱，反证 guard/改道判据的尺子必须能认出真实那份存储位置。
        string sandbox = Path.Combine(Path.GetTempPath(), "LongStitch.Checks-" + Environment.ProcessId);
        Directory.CreateDirectory(sandbox);
        AppPreferences.StoreOverride = Path.Combine(sandbox, "preferences.txt");
        RecentProjects.StoreOverride = Path.Combine(sandbox, "recent.txt");
        try
        {
            return Run(args);
        }
        finally
        {
            // 收工把改道闸还回 null（本进程随后就退出，这一步是给"将来谁把 Main 改成可重入"留的），
            // 并把沙箱目录整个删掉——里面只有档位名与本项目自己造的临时路径，但仍不留残余。
            AppPreferences.StoreOverride = null;
            RecentProjects.StoreOverride = null;
            try { Directory.Delete(sandbox, true); } catch (IOException) { /* 有判据还握着句柄时删不掉，不影响退出码 */ }
        }
    }

    private static int Run(string[] args)
    {
        // 真图探针与自监督标定是"看数据"的工具，不产出 pass/fail，所以单独分流，
        // 不混进电池——混进去会让人以为这些数字是判据。
        if (args.Length > 0 && (args[0] == "--probe" || args[0] == "--calibrate"))
            return RealSamples.Run(args);

        // 真机连拍端到端拼合：会产出 pass/fail（四条判据 + 导出核对），
        // 但它依赖仓库外的真实截图，别人克隆下来跑不了，所以同样不并入电池。
        if (args.Length > 0 && args[0] == "--stitch")
            return StitchRealRun.Run(args);

        // 第十三轮需求五：真实窗口的性能测量。它开真窗口、投真鼠标消息，
        // 与电池那格"100 次 MouseMove"（逻辑夹具）是两件事，所以单独分流，不并入电池。
        if (args.Length > 0 && args[0] == "--gui")
            return GuiPerfHarness.Run(args);

        Console.WriteLine("LongStitch 精度电池");
        Console.WriteLine("判据原则：能证明的才断言；断言必须写明量了什么、期望什么、实际什么。");
        Console.WriteLine();

        // 注册顺序 = 依赖顺序：先地基（搬运、编解码），再模型与算法，最后守卫
        BlitTests.Register();
        PngTests.Register();
        LayoutTests.Register();
        LongScrollTests.Register();
        MatchingTests.Register();
        CleanupTests.Register();
        ExportTests.Register();
        UiSmokeTests.Register();
        CanvasInteractionTests.Register();
        SeamCropTests.Register();
        CropBandDragTests.Register();
        HudCostTests.Register();
        SessionDiagnosticsTests.Register();
        // 第四十八改：外观档位的判据。放在守卫之前、其余各组之后——它会把进程级配色换去换来，
        // 每格自己在 finally 里还回黑夜（见 ThemeTests.InSandbox），所以不会污染前面那些按深色底数像素的格子。
        ThemeTests.Register();
        GuardTests.Register();

        return Battery.Run(args);
    }
}
