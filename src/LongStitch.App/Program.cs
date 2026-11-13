using LongStitch.App.UI;
using LongStitch.App.Diagnostics;

namespace LongStitch.App;

/// <summary>
/// 程序入口。
///
/// 关于 DPI：第五十五改⑥（用户图6/图7）起改用 SystemAware，不再用 PerMonitorV2。
///
/// ═══ 为什么换（用户报的缺陷形状）═══
/// PerMonitorV2 下把窗口从 150% 的屏拖到 100% 的屏（或反向），系统会发 WM_DPICHANGED 并
/// 自动缩放控件 Bounds 与"环境字体"；但本工程的字体全部是 <see cref="Theme"/> 里共享的
/// static readonly 磅值字体、布局常量是 Chrome 里写死的整数像素——它们不吃那趟缩放，
/// 结果同一条窗有的部分缩、有的不缩：文字要么特小（150%→100%，图6）要么特大（100%→150%，图7）。
/// 要在 PMv2 下修好，得把全工程的字体、常量、缓存键都接上 DpiChanged 重算——改造面覆盖
/// 每一个自绘控件，失败模式多（任何一处漏接就是新的"某一档下挤压/截断"家族）。
///
/// SystemAware 的口径：进程始终按<b>系统主屏</b>的 DPI 渲染一次，移到别的屏由 DWM 整窗位图
/// 拉伸——文字尺寸永远一致（该多大就多大），代价只是跨屏那一刻整窗轻微发虚（位图缩放），
/// 且只在"两屏缩放不同"时存在。需求 §9 的原文只要求「支持系统 DPI 缩放与高分辨率显示器」，
/// 没有逐屏缩放的硬性要求；用户是单主屏 + 偶尔拖去副屏的使用形状，发虚一瞬远好过字号错乱。
/// 画布控件内部按设备像素算的口径不变（DPI 常住进程级，不再中途变）；缩放与平移仍不参与拼合。
/// </summary>
internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        DiagnosticLog.Start();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
        {
            DiagnosticLog.Error("app.ui_exception", e.Exception);
            MessageBox.Show("程序发生错误。完整堆栈已写入日志：\n" + DiagnosticLog.FilePath,
                "LongStitch 错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Application.Exit();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            DiagnosticLog.Error("app.unhandled_exception", e.ExceptionObject as Exception ??
                new Exception(e.ExceptionObject?.ToString()), $"terminating={e.IsTerminating}");
        TaskScheduler.UnobservedTaskException += (_, e) =>
            DiagnosticLog.Error("app.unobserved_task_exception", e.Exception);

        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        try
        {
            // 第四十八改：外观偏好必须在建窗口<b>之前</b>读回来。控件的 BackColor/ForeColor 是
            // 构造期抄下来的值，窗口建好之后再换档就得靠 ThemeApplicator 整棵树翻译一遍；
            // 先读档则每一枚控件出生就是对的档，省掉那一趟翻译，也不会闪一下深色再变浅。
            //
            // 这一句<b>只在这里</b>调，MainForm 的构造器里不调：离屏判据工装一趟要建几十个窗口，
            // 若每个窗口都去读开发者自己那份偏好文件，电池的结果就会随"跑判据的人选了白天还是黑夜"
            // 而变——判据必须与机器上的个人偏好无关，所以工装进程始终从默认的黑夜档起跑。
            ThemeController.Load();

            var form = new MainForm();

            // 支持"用 LongStitch 打开"：参数可以是项目文件，也可以是一批图片
            if (args.Length > 0)
                form.EnqueueStartupPaths(args);

            DiagnosticLog.Info("app.ready", $"startup_paths={args.Length}");
            Application.Run(form);
            DiagnosticLog.Info("app.exit", "message loop ended");
        }
        catch (Exception ex)
        {
            DiagnosticLog.Error("app.startup_or_run_failed", ex);
            MessageBox.Show("程序无法继续运行。完整堆栈已写入日志：\n" + DiagnosticLog.FilePath,
                "LongStitch 错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            // 日志现在是缓冲写的：退出前必须把最后几行倒出去，否则"崩溃前那一刻"正好丢掉
            DiagnosticLog.Close();
        }
    }
}
