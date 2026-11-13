using LongStitch.App.Diagnostics;
using LongStitch.App.Imaging;
using LongStitch.App.UI;
using LongStitch.Core;
using LongStitch.Core.Cleanup;
using LongStitch.Core.Export;
using LongStitch.Core.Geometry;
using LongStitch.Core.Imaging;
using LongStitch.Core.Layout;
using LongStitch.Core.Model;
using LongStitch.Core.ProjectIO;

namespace LongStitch.Checks;

/// <summary>
/// UI 冒烟工装（不显示窗口、不弹给用户）。
///
/// 它只回答一个问题：<b>控件树能不能被安全地构造与布局出来</b>。
/// 建 Form、强制创建句柄、跑一遍布局与绘制、切一遍右侧工具面板、开一次导出对话框——
/// 这一层能抓到"字段还没赋值就被引用""停靠顺序写反导致菜单被盖住"
/// "面板重建时访问了已释放控件"这类一上线就炸的问题，
/// 而且完全不需要真的把窗口摆到用户桌面上。
///
/// 抓不到的是交互手感与视觉正确性：那些必须真机看，列在 README 的 NOT-RUN 清单里。
/// </summary>
public static class UiSmokeTests
{
    public static void Register()
    {
        Battery.Add("ui", "主窗口可构造并完成布局", ConstructAndLayout);
        Battery.Add("ui", "导入图片后画布与面板都能刷新", ImportThenRefresh);
        Battery.Add("ui", "五个工具面板来回切换不炸", SwitchAllToolPanels);
        Battery.Add("ui", "导出对话框可构造并算出计划", ExportDialogBuilds);
        Battery.Add("ui", "导出对话框任何控件不许互相重叠也不许出界", ExportDialogControlsNeverOverlap);
        Battery.Add("ui", "视口尺寸跟着控件客户区走", ViewportTracksClientSize);
        Battery.Add("ui", "平移后拼缝命中与绘制位置一致", SeamHitTestFollowsPan);
        Battery.Add("ui", "确认拼缝进撤销栈并置脏", ConfirmSeamEntersUndoAndDirty);
        Battery.Add("ui", "点选切分线把设备点换算成画布Y并退出模式", SplitPickTranslatesToDeviceCanvasY);
        Battery.Add("ui", "底部五工具按钮切换右侧面板且不改参数", BottomToolbarSwitchesPanels);
        Battery.Add("ui", "添加图片在界面上只有一枚按钮", AddImageButtonIsSingleton);
        Battery.Add("ui", "对比与差异互斥且按钮勾选与画布开关一致", CompareAndDiffAreMutuallyExclusive);
        Battery.Add("ui", "画法三个出口必须落在同一份状态上", OverlapViewThreeEntriesOneState);
        Battery.Add("guard", "面板回填不许把画法命令再发回主窗口", PanelSyncDoesNotEchoCommands);
        Battery.Add("ui", "悬停拼缝给出可指认的序号提示", HoverTipNamesTheSeamUnderCursor);
        Battery.Add("ui", "状态栏持续显示缩放比例与画布实际像素", StatusBarReportsZoomAndCanvasSize);
        Battery.Add("ui", "清理面板逐张开关滚动条补丁可撤销且置脏", PerImagePatchToggleIsUndoableAndDirties);
        Battery.Add("ui", "导出对话框的模板与质量真的接进计划", ExportDialogTemplateAndQualityAreWired);
        Battery.Add("ui", "导出分段和缩小设置不会被计划刷新顶回", ExportOptionsStaySelected);
        Battery.Add("ui", "浏览态方向键平移视口且可关闭", BrowseArrowPansViewportAndCanBeOff);
        Battery.Add("ui", "不足两张图时拼缝工具禁用", SeamToolDisabledBelowTwoImages);
        Battery.Add("ui", "侧栏折叠后画布接管空间", SidePanelsCollapseWithoutHidingCanvas);
        Battery.Add("ui", "拼缝列表筛选后行号仍指回真实拼缝", SeamListFilterKeepsRealSeamMapping);
        Battery.Add("ui", "打包后删掉原图还能从容器打开", PackageThroughMainFormSurvivesDeletedSources);
        Battery.Add("ui", "分析写回闸门拦掉过期结果", AnalysisGateRejectsStaleResults);
        Battery.Add("ui", "X 与 Y 两格连着改不会互相顶回", SeamXYBoxesDoNotClobberEachOther);
        Battery.Add("ui", "长截图面板锁横向：X 格禁用、档位显示仅上下、硬填会被拦且回话", LongScrollPanelLocksHorizontal);
        Battery.Add("ui", "长截图方向键与拖动折算都不能改横向，竖拼照旧", LongScrollArrowsAndDragCannotMoveHorizontally);
        Battery.Add("ui", "重影对比只画在屏幕上：不改参数、不改布局、不改导出字节", GhostOverlayChangesNeitherLayoutNorPixels);
        Battery.Add("ui", "重新定位源文件的四条分支都有明确回话", RelocateReportsEveryBranch);
        Battery.Add("ui", "导入顺序保持传入顺序", ImportKeepsGivenOrderAndSortsFolders);
        Battery.Add("ui", "导入一整批只刷一次屏", ImportBatchCoalescesUiRefresh);
        Battery.Add("guard", "逐张通知必须真的被批次接住", ImportBatchReallyQueuedPerFile);
        Battery.Add("ui", "导入结束后画布把内容摆回正中", ImportCentresContentInView);
        Battery.Add("ui", "导入一整批画布一帧都不许提前画", ImportBatchHoldsCanvasPaintsUntilRelease);
        Battery.Add("guard", "画布与素材列表挂起的笔数必须一笔不缺", ImportBatchDefersEveryChangedNotice);
        Battery.Add("ui", "LRU 命中要真的把条目挪回队头", LruHitMovesEntryToHead);
        Battery.Add("canvas", "确认拼缝一次退出编辑", ConfirmSeamExitsEditInOneClick);
        Battery.Add("ui", "菜单栏四个顶级项字体必须同一枚", MenuTopLevelFontsAreOneFont);
        Battery.Add("ui", "离屏工装的两个存储出口都必须改道到沙箱", HarnessRedirectsBothStoresIntoSandbox);
        Battery.Add("guard", "改道判据的尺子必须能认出真实那份存储位置", HarnessRedirectRulerHasTeeth);
        Battery.Add("ui", "分段控件与文档模式两向同源", ModeSegmentTwoWaySync);
        Battery.Add("guard", "分段程序回填必须静音且用户选择只发一次", ModeSegmentBackfillIsSilent);
        Battery.Add("ui", "顶栏只有一条带且分析按钮文字不裁", TopBandSingleAndTextFits);
        Battery.Add("guard", "顶栏量宽尺子能认出裁字", TextFitsRulerHasTeeth);
        Battery.Add("ui", "底部五工具点击目标不小于36", ToolButtonsMeetTouchTarget);
        Battery.Add("guard", "点击目标尺子能认出不足36", TouchTargetRulerHasTeeth);
        Battery.Add("ui", "拼缝面板三组置顶顺序为按钮重影位移", SeamPanelActionGroupsComeFirst);
        Battery.Add("guard", "分段文字区与下划线几何不相交", SegmentTextClearsUnderline);
        Battery.Add("ui", "拼缝面板相邻控件留有呼吸间距", SeamPanelBreathingGaps);
        Battery.Add("ui", "透明度滑杆可获焦点且方向键改值并带百分比读数", OpacitySliderKeyboardAndPercent);
        Battery.Add("guard", "分段文字顶缘不出选中胶囊", SegmentTextStaysInsideThumb);
        Battery.Add("guard", "数字框下箭头点序必须朝下", SpinGlyphDownPointsDown);
        // ───── 第五十五改（用户六条缺陷报告）─────
        Battery.Add("canvas", "缝线宽度随缩放档走且放大档有下限", SeamLineWidthFollowsZoom);
        Battery.Add("ui", "导出格式下拉只列大写三格式且扩展名仍小写", ExportFormatLabelsUppercaseNoWebP);
        Battery.Add("ui", "导出对话框行内控件垂直居中且输入框宿主无浅底", ExportDialogRowsVerticallyAligned);
        Battery.Add("ui", "素材列表换蓝色圆弧自绘滚动条且原生条被裁出界", SourceListUsesChromeScrollBar);
        Battery.Add("ui", "拼缝列表同样换自绘滚动条（几何裁剪成立）", SeamListUsesChromeScrollBar);
        Battery.Add("ui", "主窗口默认尺寸放得下全部读数", MainFormDefaultSizeFitsStatusBar);
        // ───── 第五十六改（用户两条缺陷报告，2 张截图）─────
        Battery.Add("ui", "素材行卡片与添加图片外框同一条竖线", SourceRowCardAlignsAddButton);
        Battery.Add("ui", "导出下拉的箭头由自绘接管而不是系统残箭", ExportComboArrowSelfDrawn);
        Battery.Add("ui", "透明勾管住背景色卡且规则随格式切换", TransparentCheckGatesBackgroundSwatch);
        // ───── 第五十七改（用户四条缺陷报告，4 张截图）─────
        Battery.Add("ui", "关于正文铺满内容宽且最宽一行不折行", AboutLinesFillContentWidth);
        Battery.Add("ui", "缩小下拉收起格装得下最长选项整串", DownscaleComboFitsLongestOption);
        Battery.Add("ui", "自绘取色器预设与RGB两路都通且读数一致", ChromeColorDialogBothPathsAgree);
        Battery.Add("ui", "导出进度行文字与进度条互不重叠", ExportProgressRowNeverOverlaps);
        // ───── 第五十九改②（资源生命周期三件 + 诊断口径修正）─────
        Battery.Add("ui", "导入 N 张 PNG 全程只解码 N 次（预热缓存与重新解码逐字节同源）", ImportDecodesEachFileExactlyOnce);
        Battery.Add("ui", "新建项目后源图缓存必须清零", NewProjectClearsSourceCache);
        Battery.Add("ui", "素材列表刷新与销毁时旧缩略图必须被显式释放", ThumbnailDisposalIsDeterministic);
        Battery.Add("ui", "缓存预算装不下时按 LRU 淘汰，被淘汰的图按需重新解码且像素逐字节同源", CacheBudgetEvictsLruAndRedecodesOnDemand);
        Battery.Add("ui", "操作收尾的强制回收必须收走死掉的大对象并计数", ReleaseAfterOperationCollectsDeadObjects);
        Battery.Add("ui", "批量分析确认写回后必须触发一次释放回收", AnalysisCompletionTriggersRelease);
        // ───── 第六十改回修（未选模式不拦导入，选长截图才自动分析）─────
        Battery.Add("ui", "未选模式时添加图片照常导入（只入列不分析），选了长截图才自动分析", ImportWithoutModeThenAnalysisFollowsMode);
        Battery.Add("ui", "新建项目保留已选模式，全新会话仍是未选", NewProjectKeepsChosenModeFreshStartsUnset);
    }

    /// <summary>
    /// 第四十八改（收尾）：<b>跑一趟电池不许在开发者机器上留下真文件</b>。
    ///
    /// ═══ 这一格量的是"改道闸有没有真的合上"，不是"某个功能对不对" ═══
    /// 判据会经产品保存项目、真点外观那三枚按钮；而产品那几个调用点都是 <c>Add(path)</c> /
    /// <c>SetMode(mode)</c>——<b>不带路径</b>，默认值就是 <c>%APPDATA%\LongStitch\*.txt</c>。
    /// 第一趟就是这么红的：判据量临时文件"写盘了没有"，产品却把开发者真实那份写了，两头都没错，错在缺闸。
    /// 当时只给偏好那一份补了闸（并且只在 ThemeTests 的沙箱里改道），最近项目那一份<b>漏着</b>——
    /// 而它按自己的类注释是"一份『我在哪些聊天截图上工作过』的清单"，属隐私敏感，漏不得。
    /// 现在改道提到了工装进程的入口（<c>Checks/Program.Main</c>），这一格钉的就是"入口处那两行还在、
    /// 并且指向的确实不是真实那份"。
    ///
    /// ═══ 为什么量"路由"而不是量"真实那份文件有没有变多一行" ═══
    /// 后者依赖开发者机器上恰好有没有那份文件、以及他本人有没有真在用这个程序——
    /// 换一台机器结论就变，那是把判据钉在环境上。路由是本进程自己设的，任何机器上都同一个形状。
    /// "真实那份没被写"由<b>路由不在真实目录下</b>这条推出来，而不是靠去读它。
    /// </summary>
    private static void HarnessRedirectsBothStoresIntoSandbox()
    {
        string realAppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        Battery.Assert(realAppData.Length > 0, "取不到 %APPDATA% 本身就该出声，否则下面的比较全是空话");

        foreach ((string name, string path) in new[]
                 {
                     ("偏好 preferences.txt", AppPreferences.StorePath),
                     ("最近项目 recent.txt", RecentProjects.StorePath),
                 })
        {
            Battery.Assert(!IsUnderRealStoreRoot(path, realAppData),
                $"{name} 的存储出口仍指向真实那份（{path}）——跑一趟电池就会在开发者机器上留下真文件");
            Battery.Assert(Path.IsPathRooted(path) && path.Contains("LongStitch.Checks-", StringComparison.Ordinal),
                $"{name} 的存储出口必须落在工装自己的沙箱目录里，实际是 {path}");
        }

        // 两个出口必须<b>各自</b>有自己的文件，不许共用一枚（共用就等于偏好被写进最近项目那份里）
        Battery.Assert(!string.Equals(AppPreferences.StorePath, RecentProjects.StorePath, StringComparison.OrdinalIgnoreCase),
            "两个存储出口不许是同一个文件");

        Battery.Evidence($"偏好出口 {AppPreferences.StorePath}；最近项目出口 {RecentProjects.StorePath}；" +
                         $"真实根 {realAppData}（两枚都不在它下面）");
    }

    /// <summary>
    /// 上一格那把尺子的反证：<b>它必须能认出"真实那份"长什么样</b>，否则"不在真实目录下"就是恒真。
    ///
    /// 尺子是按<b>真实根目录 + 产品自己那两级子路径</b>判的（<c>%APPDATA%\LongStitch\</c>），
    /// 不是按"含不含 LongStitch 这个词"——沙箱目录名里也有 <c>LongStitch.Checks-</c>，
    /// 按词判就会把沙箱也算成真实那份，于是正反两枚一起恒真、判据彻底空转。
    /// 这一格把两种形状各喂一次：真实那份必须被判成"在真实根下"，沙箱那份必须被判成"不在"。
    /// </summary>
    private static void HarnessRedirectRulerHasTeeth()
    {
        string realAppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string realPrefs = Path.Combine(realAppData, "LongStitch", "preferences.txt");
        string realRecent = Path.Combine(realAppData, "LongStitch", "recent.txt");

        Battery.Assert(IsUnderRealStoreRoot(realPrefs, realAppData),
            "尺子必须把真实的 preferences.txt 判成「在真实根下」——判不出来上一格就是恒真");
        Battery.Assert(IsUnderRealStoreRoot(realRecent, realAppData),
            "尺子必须把真实的 recent.txt 判成「在真实根下」");

        // 反向那一半：沙箱路径（名字里同样含 "LongStitch"）必须被判成「不在真实根下」
        Battery.Assert(!IsUnderRealStoreRoot(AppPreferences.StorePath, realAppData),
            "沙箱那份不许被尺子误判成真实那份");
        Battery.Assert(!IsUnderRealStoreRoot(
                Path.Combine(Path.GetTempPath(), "LongStitch.Checks-1", "recent.txt"), realAppData),
            "沙箱形状的 recent.txt 同样不许被误判");

        Battery.Evidence($"尺子对真实 {realPrefs} 判 true、对沙箱 {AppPreferences.StorePath} 判 false；" +
                         "两边都能判，说明它不是恒真也不是恒假");
    }

    /// <summary>
    /// "这个路径是不是落在用户真实的那份存储根目录下"。
    /// 用<b>带尾分隔符的前缀</b>比，不用"含某词"：沙箱目录名里也有 LongStitch，按词比会误判。
    /// 顺手把两种大小写与两种分隔符都归一，免得 <c>/</c> 与 <c>\</c> 混用时漏判。
    /// </summary>
    private static bool IsUnderRealStoreRoot(string path, string realAppData)
    {
        string Norm(string p) => p.Replace('/', '\\').TrimEnd('\\') + "\\";
        return Norm(path).StartsWith(Norm(Path.Combine(realAppData, "LongStitch")),
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 需求 §5 A："! 优先排序可筛选，但图片顺序保持不变"。
    /// 筛选一改，列表行号就不再等于 <c>Doc.Seams</c> 的下标——
    /// 按行号取数的老写法会把"第 1 行"跳成第 1 条拼缝（其实是第 2 条），
    /// 所以判据必须问：筛完后的第 1 行点下去，画布选中的到底是不是那一条。
    /// </summary>
    private static void SeamListFilterKeepsRealSeamMapping()
    {
        RunOnStaThread(() =>
        {
            (MainForm form, string dir) = FormWithImports(count: 4);   // 4 段 → 3 条拼缝
            try
            {
                Project p = form.ProjectForTest;
                Battery.AssertEqual(3, p.Doc.Seams.Count, "四段应有三条拼缝");
                Battery.AssertEqual(3, form.SeamListRowCountForTest, "未筛选时三行全在");

                // 把中间那条（序号 2→3）标成需检查，其余标成已通过
                p.Mutate("布置状态", _ =>
                {
                    p.Doc.Seams[0].Status = SeamStatus.AutoPassed;
                    p.Doc.Seams[1].Status = SeamStatus.NeedsReview;
                    p.Doc.Seams[2].Status = SeamStatus.ManualConfirmed;
                });
                Battery.AssertTextEqual("3 条拼缝 · 2 已通过 · 1 需检查",
                    form.SeamSummaryTextForTest, "筛选关闭时统计应覆盖全部三条");

                string? firstRowSeamId = form.SetSeamFilterForTest(true);
                Battery.AssertEqual(1, form.SeamListRowCountForTest,
                    "「只看需检查」后应只剩那一条");
                Battery.AssertEqual(p.Doc.Seams[1].Id, firstRowSeamId,
                    "筛选后的第 1 行必须仍是文档里的第 2 条拼缝（2→3）");
                Battery.AssertEqual(p.Doc.Seams[1].Id, form.CanvasForTest.SelectedSeamId,
                    "点这一行应把画布带到同一条拼缝，而不是按行号取到的第 1 条");
                Battery.Assert(form.SeamSummaryTextForTest.StartsWith("3 条拼缝", StringComparison.Ordinal),
                    $"统计口径不该跟着筛选走，实际：{form.SeamSummaryTextForTest}");
                Battery.Assert(form.SeamSummaryTextForTest.Contains("已筛出 1 条", StringComparison.Ordinal),
                    $"筛过之后要说出没说的两句之一：{form.SeamSummaryTextForTest}");

                // 顺序不变：筛选不许重排，取消筛选后仍按原顺序
                firstRowSeamId = form.SetSeamFilterForTest(false);
                Battery.AssertEqual(3, form.SeamListRowCountForTest, "取消筛选应恢复三行");
                Battery.AssertEqual(p.Doc.Seams[0].Id, firstRowSeamId, "恢复后第 1 行回到第 1 条拼缝");

                Battery.Evidence("筛选：3 行 → 1 行（第 1 行=2→3，画布选中同一条）→ 取消回 3 行，顺序未变");
            }
            finally { form.Dispose(); TryDelete(dir); }
        });
    }

    /// <summary>
    /// 打包项目的**界面通路**：菜单里那两条命令背后是 SavePackage/OpenPackageFrom，
    /// 它们除了调 Core，还要换文档、重新登记解析器、按缺失情况给提示。
    /// 判据把原图从磁盘上删掉再打开——只有容器自己带着图才可能成功。
    /// </summary>
    private static void PackageThroughMainFormSurvivesDeletedSources()
    {
        RunOnStaThread(() =>
        {
            (MainForm form, string dir) = FormWithImports(count: 2, width: 60, height: 120);
            string package = Path.Combine(dir, "via-ui" + Core.ProjectIO.ProjectPackage.Extension);
            string extract = Core.ProjectIO.ProjectPackage.DefaultExtractDirectory(package);
            try
            {
                string report = form.SavePackageForTest(package);
                Battery.AssertEqual("2/0", report.Split('/')[0] + "/" + report.Split('/')[1],
                    "两张原图都应打进容器、一张都不该被跳过");
                int heightBefore = form.ProjectForTest.Layout.CanvasSize.Height;

                // 把导入用的原图删掉：证明后面打开读的是容器，不是那几个文件
                foreach (SourceImage src in form.ProjectForTest.Doc.Sources) File.Delete(src.Path);

                form.OpenPackageForTest(package);
                var sources = form.ProjectForTest.Doc.Sources;
                Battery.Assert(sources.All(s => !s.SourceMissing), "从容器打开后不应有任何一张标为缺失");
                Battery.Assert(sources.All(s => s.Path.StartsWith(extract, StringComparison.Ordinal)),
                    "路径必须已被改写为解包目录里的文件（否则画布读的还是被删掉的那份）");
                Battery.Assert(sources.All(s => File.Exists(s.Path)), "解包出来的文件应真实存在");
                Battery.AssertEqual(2, form.ProjectForTest.Doc.Segments.Count, "片段数应原样带回来");
                Battery.Assert(!form.ProjectForTest.Layout.IsEmpty,
                    "打开后布局应重新算得出来——画布不是空的");
                Battery.AssertEqual(heightBefore, form.ProjectForTest.Layout.CanvasSize.Height,
                    "打包再打开后画布高度必须与打包前一致（尺寸口径不该被容器改动）");
                Battery.Assert(heightBefore > 100, $"打包前画布就该有内容，实际高 {heightBefore}");

                Battery.Evidence("打包 → 删掉原图 → 从容器打开：路径指向解包文件、布局与画布尺寸都对");
            }
            finally
            {
                form.Dispose();
                TryDelete(dir);
                TryDelete(extract);
            }
        });
    }

    /// <summary>
    /// 造一个"已经导入 N 张真 PNG"的主窗口。
    /// 走 ImportPaths 这条产品通路（而不是手工塞文档），这样判据量到的是用户实际操作后的状态。
    /// 第六十改：产品在导入入口要求"先选模式"，夹具直接把文档字段写成 LongScroll
    /// （不走 PickModeSegmentForTest——那条真 UI 通路会 Mutate 一次，把「切模式」混进
    /// 各格正在数的通知笔数/释放计数里，账就全对不上了）。
    /// </summary>
    private static (MainForm Form, string Dir) FormWithImports(int count = 3, int width = 60, int height = 120)
    {
        string dir = Path.Combine(Path.GetTempPath(), "ls-ui-" + Guid.NewGuid().ToString("N")[..6]);
        List<string> paths = WritePngFiles(dir, count, width, height);
        var form = new MainForm();
        form.CreateControl();
        form.ProjectForTest.Doc.Mode = StitchMode.LongScroll;   // 夹具指定长截图：文档默认已是 Unset，自动分析只对 LongScroll 起
        form.ImportPaths(paths.ToArray());
        form.PerformLayout();
        return (form, dir);
    }

    /// <summary>往 <paramref name="dir"/> 里写 N 张真 PNG 并返回路径（导入那一族判据要自己建窗口，所以取数与建窗分开）。</summary>
    private static List<string> WritePngFiles(string dir, int count, int width, int height)
    {
        Directory.CreateDirectory(dir);
        var paths = new List<string>();
        for (int i = 0; i < count; i++)
        {
            RgbaImage img = Fixture.OpaqueLike(width, height, seed: 100 + i);
            string file = Path.Combine(dir, $"s{i}.png");
            File.WriteAllBytes(file, Core.Imaging.Png.PngEncoder.EncodeToBytes(img));
            paths.Add(file);
        }
        return paths;
    }

    /// <summary>
    /// 模拟 UI 需求 §3.3 的底部工具栏。
    /// 两个判据一起才成立：五枚按钮按序号切到对应面板（否则四个工具在界面上根本没有入口），
    /// 而且**切换工具不许改动任何拼合参数**（Revision 不变）——它只是换右侧看哪套参数。
    /// </summary>
    private static void BottomToolbarSwitchesPanels()
    {
        RunOnStaThread(() =>
        {
            (MainForm form, string dir) = FormWithImports();
            try
            {
                Battery.AssertEqual(5, form.ToolButtonTitlesForTest.Count, "底部工具按钮数量");
                Battery.AssertTextEqual("拼缝,切分,删段,裁边,清理",
                    string.Join(",", form.ToolButtonTitlesForTest), "底部工具按钮的标题与顺序");

                int revisionAtStart = form.ProjectForTest.Revision;
                var seen = new List<ToolKind>();
                for (int i = 0; i < 5; i++)
                {
                    form.ClickToolButtonForTest(i);
                    seen.Add(form.ActiveToolForTest);
                    Battery.AssertEqual((ToolKind)i, form.ActiveToolForTest,
                        $"点第 {i + 1} 枚按钮后右侧应显示 {((ToolKind)i)} 参数");
                }
                Battery.AssertEqual(revisionAtStart, form.ProjectForTest.Revision,
                    "切换工具面板不应改动任何拼合参数（Revision 必须不变）");

                // 离开清理面板时高亮层要收起，否则切走了框还留在画布上
                form.ClickToolButtonForTest(4);
                form.ClickToolButtonForTest(0);
                Battery.Assert(!form.CanvasForTest.ShowPatchOverlay,
                    "切离清理面板后补丁高亮层应自动关闭");

                Battery.Evidence($"五枚按钮 → {string.Join("→", seen)}，Revision 保持 {revisionAtStart}");
            }
            finally { form.Dispose(); TryDelete(dir); }
        });
    }

    /// <summary>
    /// 用户报的第三条：「整个界面有两个『添加图片』的按钮，只需要留一个左侧即可」。
    /// 量法是走<b>真实控件树</b>（含 ToolStrip 里的项），数标题正好等于「添加图片」的按钮：
    /// 必须恰好一枚，而且那一枚必须长在左侧面板 <see cref="SourcePanel"/> 里。
    /// 两处分开钉的理由：只数总数会放过"把左侧那枚删掉、顶栏那枚留着"这种反着删的形状；
    /// 只判归属会放过"两枚都在左侧"。撤掉重复入口也不许把能力一起撤掉，所以菜单那句必须还在
    /// （菜单那句带省略号「添加图片…」，与按钮的标题不是同一个字面量，恰好能分开量）。
    /// </summary>
    private static void AddImageButtonIsSingleton()
    {
        RunOnStaThread(() =>
        {
            (MainForm form, string dir) = FormWithImports();
            try
            {
                var buttons = new List<string>();      // 每一项是"归属路径"，不是标题——标题都一样，指认不了是谁
                var menuTexts = new List<string>();

                void WalkControl(Control c, string path)
                {
                    string here = string.IsNullOrEmpty(path) ? c.GetType().Name : $"{path}/{c.GetType().Name}";
                    if (c is ButtonBase bb && bb.Text.Trim() == "添加图片") buttons.Add(here);
                    if (c is ToolStrip strip)
                        foreach (ToolStripItem item in strip.Items)
                        {
                            if (item is ToolStripButton tb && (tb.Text ?? "").Trim() == "添加图片")
                                buttons.Add($"{here}/ToolStripButton");
                            if (item.Text is not null) menuTexts.Add(item.Text.Trim());
                            if (item is ToolStripMenuItem mi)
                                foreach (ToolStripItem sub in mi.DropDownItems)
                                    if (sub.Text is not null) menuTexts.Add(sub.Text.Trim());
                        }
                    foreach (Control child in c.Controls) WalkControl(child, here);
                }
                WalkControl(form, "");

                Battery.AssertEqual(1, buttons.Count,
                    "界面上「添加图片」这枚按钮必须只有一枚（用户原话：有两个，只需要留一个左侧）：" +
                    $"实际 {buttons.Count} 枚，归属：{string.Join(" | ", buttons)}");
                Battery.Assert(buttons.Count == 1 && buttons[0].Contains(nameof(SourcePanel), StringComparison.Ordinal),
                    $"留下的那一枚必须在左侧面板里（模拟UI需求 §3.1 首屏线框点的就是它），实际归属：{string.Join(" | ", buttons)}");
                Battery.Assert(menuTexts.Any(t => t.StartsWith("添加图片", StringComparison.Ordinal)),
                    $"顶栏那枚撤掉后，菜单里的「添加图片…」出口必须还在（Ctrl+O 与拖文件进窗口走的是同一个动作），" +
                    $"菜单里读到的标题：{string.Join(",", menuTexts.Where(t => t.Contains("添加")))}");

                Battery.Evidence($"「添加图片」按钮 1 枚 ⇒ {string.Join(" / ", buttons)}；菜单出口仍在（Ctrl+O / 拖拽同路）");
            }
            finally { form.Dispose(); TryDelete(dir); }
        });
    }

    /// <summary>
    /// 「对比」曾经把 ShowDifference 置 false 就完事，自己从不生效——一个看起来在工作的假开关。
    /// 判据一次读四格：两个按钮的勾选态 × 画布的两个真实开关，四格必须自相一致且互斥。
    /// </summary>
    private static void CompareAndDiffAreMutuallyExclusive()
    {
        RunOnStaThread(() =>
        {
            (MainForm form, string dir) = FormWithImports();
            try
            {
                var initial = form.OverlayStateForTest;
                Battery.Assert(initial.CompareChecked && initial.CanvasOverlay &&
                               !initial.DiffChecked && !initial.CanvasDifference,
                    $"初始应为「重影对比开、差异关」，实际 按钮:{initial.CompareChecked}/{initial.DiffChecked} " +
                    $"画布:{initial.CanvasOverlay}/{initial.CanvasDifference}");

                form.ClickDiffForTest();
                var diff = form.OverlayStateForTest;
                Battery.Assert(diff.DiffChecked && diff.CanvasDifference &&
                               !diff.CompareChecked && !diff.CanvasOverlay,
                    $"点「差异」后应只开差异层，实际 按钮:{diff.CompareChecked}/{diff.DiffChecked} " +
                    $"画布:{diff.CanvasOverlay}/{diff.CanvasDifference}");

                form.ClickCompareForTest();
                var back = form.OverlayStateForTest;
                Battery.Assert(back.CompareChecked && back.CanvasOverlay &&
                               !back.DiffChecked && !back.CanvasDifference,
                    $"点回「对比」后应只开重影层，实际 按钮:{back.CompareChecked}/{back.DiffChecked} " +
                    $"画布:{back.CanvasOverlay}/{back.CanvasDifference}");

                Battery.Evidence("对比 ⇄ 差异：任一时刻只有一个开着，且按钮勾选与画布开关四格一致");
            }
            finally { form.Dispose(); TryDelete(dir); }
        });
    }

    /// <summary>
    /// 用户报的第五条：「差异标红／重影对比有时候点了会自动取消，但画布上又显示着，得再点一下才能取消或重新显示」。
    /// 病根是画法有<b>三条写入路</b>、每条只写自己那一半：面板那条命令只开画布的差异层、
    /// 只把「对比」按钮的勾抹掉，却从不关画布上那层重影；而面板<b>整块重建</b>时又把两枚勾选框
    /// 按写死的 true/false 起，把用户已经改出来的画法冲回默认。
    /// 所以这一格一次读<b>六格</b>（上方两枚按钮的勾选 × 画布两个开关 × 面板两枚勾选框），
    /// 并从三个出口各驱动一遍，每一个出口之后六格都必须说同一件事；收尾再走"面板重建之后"那一趟。
    /// 判据能红的形状：任何一格没跟着改（旧写法在面板那条路上必红）、点两下才对上（顺序驱动里每一步都当场核对）、
    /// 重建把状态冲回默认。回填绕成回路那一族不在这里量，由 <c>guard/面板回填不许把画法命令再发回主窗口</c> 单独数。
    /// </summary>
    private static void OverlapViewThreeEntriesOneState()
    {
        RunOnStaThread(() =>
        {
            (MainForm form, string dir) = FormWithImports(count: 3);
            try
            {
                string seamId = form.ProjectForTest.Doc.Seams[0].Id;
                form.ClickToolButtonForTest(0);
                form.CanvasForTest.SelectSeam(seamId);

                var checks0 = form.PanelOverlapChecksForTest;
                Battery.Assert(checks0.PanelGhost is not null && checks0.PanelDiff is not null,
                    "夹具自证：选中拼缝后面板必须真的画出「重影对比」与「差异标红」两枚勾选框——" +
                    "读到 null 的话六格里只有四格在量，这一格就退化成上面那格四件套");
                ExpectOverlapState(form, "起点（画布默认重影开）", ghost: true, difference: false);

                form.ClickDiffForTest();
                ExpectOverlapState(form, "点上方「差异」", ghost: false, difference: true);

                form.ClickDiffForTest();
                ExpectOverlapState(form, "再点一次「差异」应关掉它", ghost: false, difference: false);

                form.ClickCompareForTest();
                ExpectOverlapState(form, "点上方「对比」", ghost: true, difference: false);

                form.ClickPanelDiffForTest();
                ExpectOverlapState(form, "勾面板「差异标红」", ghost: false, difference: true);

                form.ClickPanelGhostForTest();
                ExpectOverlapState(form, "勾面板「重影对比」（另一档赢，差异必须关掉）", ghost: true, difference: false);

                // 面板整块重建（切到清理再切回拼缝）之后，画法必须还是用户改出来的那一档
                form.ClickToolButtonForTest(4);
                form.ClickToolButtonForTest(0);
                form.CanvasForTest.SelectSeam(seamId);
                ExpectOverlapState(form, "面板重建之后", ghost: true, difference: false);

                Battery.Evidence("三出口 × 六格逐趟一致：按钮两趟、面板两趟、中间还走了一次重建");
            }
            finally { form.Dispose(); TryDelete(dir); }
        });
    }

    /// <summary>读六格并逐格核对：上方两枚按钮、画布两个开关、面板两枚勾选框必须说的是同一件事。</summary>
    private static void ExpectOverlapState(MainForm form, string tag, bool ghost, bool difference)
    {
        var s = form.OverlayStateForTest;
        var c = form.PanelOverlapChecksForTest;
        Battery.Assert(s.CompareChecked == ghost && s.DiffChecked == difference &&
                       s.CanvasOverlay == ghost && s.CanvasDifference == difference &&
                       c.PanelGhost == ghost && c.PanelDiff == difference,
            $"{tag}：六格应一致于「重影={ghost} 差异={difference}」，实际 " +
            $"按钮 {s.CompareChecked}/{s.DiffChecked} · 画布 {s.CanvasOverlay}/{s.CanvasDifference} · " +
            $"面板 {c.PanelGhost?.ToString() ?? "无"}/{c.PanelDiff?.ToString() ?? "无"}");
    }

    /// <summary>
    /// <c>ui/画法三个出口必须落在同一份状态上</c> 数不到的一件事：面板被主窗口回填时，
    /// 若没有压住 <c>CheckedChanged</c>，它会再发一条命令回主窗口——值相同、六格仍然一致，
    /// 上面那格照旧绿，但用户每点一次都多绕一圈（而且这一族回路迟早把"点一下没反应"量不出来）。
    /// 所以这里数的不是状态、是<b>面板自己发出的命令条数</b>。
    /// 顺序是"先把尺校准、再拿它量"：第一步真人点面板那枚，计数必须 +1（证明这把尺接在活着的勾选框上），
    /// 第二步才从上方按钮驱动一趟，此时计数必须一字不动——那才是"回填没有绕回来"。
    /// </summary>
    private static void PanelSyncDoesNotEchoCommands()
    {
        RunOnStaThread(() =>
        {
            (MainForm form, string dir) = FormWithImports(count: 3);
            try
            {
                string seamId = form.ProjectForTest.Doc.Seams[0].Id;
                form.ClickToolButtonForTest(0);
                form.CanvasForTest.SelectSeam(seamId);

                int before = form.PanelOverlapCommandCountForTest;

                // 先用真人那一下把尺校准：这一枚勾选框点下去必须让计数器 +1。
                // 顺序有意义——先证明尺是活的，才谈得上后面那句"零增长"不是空转。
                form.ClickPanelDiffForTest();
                int afterUser = form.PanelOverlapCommandCountForTest;
                Battery.AssertEqual(before + 1, afterUser,
                    $"夹具自证：真人点面板「差异标红」必须让计数器 +1（否则下面那句零增长就是拿一把坏尺量的），" +
                    $"实际 {before}→{afterUser}");
                Battery.Assert(form.OverlayStateForTest.CanvasDifference && !form.OverlayStateForTest.CanvasOverlay,
                    "面板那一下得真的把画法改成差异，否则 +1 只是空点一下");

                form.ClickCompareForTest();       // 从上方按钮驱动：面板只许被回填，不许回话
                int afterToolbar = form.PanelOverlapCommandCountForTest;
                Battery.AssertEqual(afterUser, afterToolbar,
                    "上方按钮驱动一趟画法之后，面板回发的命令条数必须一字不动；" +
                    $"涨了就是回填没压住 CheckedChanged（实际 {afterUser}→{afterToolbar}）");
                Battery.Assert(form.OverlayStateForTest.CanvasOverlay && !form.OverlayStateForTest.CanvasDifference,
                    "同一趟的六格里画布那两格必须真的跟着换成重影档");

                Battery.Evidence($"真点面板 +1（{before}→{afterUser}）⇒ 尺是活的；" +
                                 $"按钮驱动一趟 {afterUser}→{afterToolbar}（回填零回话）");
            }
            finally { form.Dispose(); TryDelete(dir); }
        });
    }

    /// <summary>
    /// 用户报的第六条：「导入图片的时候每导入一张界面就会闪烁一次…等到完全导入并完全分析结束后再渲染整个界面」。
    /// 闪烁不是解码造成的，是<b>通知</b>造成的：每追加一段都走一次 <c>Project.Mutate</c>（要进撤销栈，必须走），
    /// 每一次都 raise Changed → <c>RefreshChrome</c> 把标题／撤销菜单／模式按钮／拼缝列表／右侧面板全刷一遍，
    /// 循环里那一下 <c>PumpUi</c> 又真的把它们泵到屏上。
    /// 量法不数毫秒、只<b>数刷新次数</b>：导入 6 张，<c>RefreshChrome</c> 真身最多执行 2 次
    /// ——批次放闸那一次是必然的，另一次只可能来自分析写回（跑没跑完取决于判据这一趟泵没泵消息，
    /// 所以写成上限而不是等号）。旧写法是 6 + 2 = 8 次，每次就是一闪。
    /// 这一格必须与 <c>guard/逐张通知必须真的被批次接住</c> 同读：只比"次数少"的话，
    /// 通知一次都没发出来也能拿绿——被挂起的那几笔由反证那一格数。
    /// </summary>
    private static void ImportBatchCoalescesUiRefresh()
    {
        RunOnStaThread(() =>
        {
            const int files = 6;
            string dir = Path.Combine(Path.GetTempPath(), "ls-batch-" + Guid.NewGuid().ToString("N")[..6]);
            List<string> paths = WritePngFiles(dir, files, 60, 120);
            var form = new MainForm();
            try
            {
                form.CreateControl();
                int refresh0 = form.UiRefreshCountForTest;
                form.ProjectForTest.Doc.Mode = StitchMode.LongScroll;   // 夹具指定长截图：文档默认已是 Unset，自动分析只对 LongScroll 起（不写就到不了分析通路）
                form.ImportPaths(paths.ToArray());

                int refreshed = form.UiRefreshCountForTest - refresh0;
                Battery.AssertEqual(files, form.ProjectForTest.Doc.Segments.Count,
                    "夹具自证：六张必须真进了文档——不然下面那句是在量一趟没发生的导入");
                Battery.Assert(refreshed <= 2,
                    $"导入 6 张期间界面刷新真身最多执行 2 次（批次放闸那一次 + 分析写回至多再一次），实际 {refreshed} 次" +
                    "（旧写法是 6+2 次，每多一次就是一闪）");
                Battery.Evidence($"六张图 ⇒ 真身刷新 {refreshed} 次、被批次合并掉 {form.UiRefreshDeferredForTest} 次");
            }
            finally { form.Dispose(); TryDelete(dir); }
        });
    }

    /// <summary>
    /// <c>ui/导入一整批只刷一次屏</c> 的前提门：那一句"只刷了两次"有个能喂绿的做法——
    /// 通知压根没发出来（比如把 Changed 订阅写断了），于是既没闪烁也没合并，读数一样好看。
    /// 所以这里数<b>被挂起</b>的那几笔，并把账算死：导入 6 张 = 6 笔追加（每张一次 Mutate）
    /// + 1 笔「重排」（EnsureSeams）+ 1 笔 Touch = <b>8 笔</b>，一笔不许少、一笔不许多。
    /// 少一笔就是通知没发出来（那就是"没刷"冒充"合并"），多一笔就是放闸后还往批次里塞。
    /// 真身那一次由 <c>ui/导入一整批只刷一次屏</c> 那一格盯，两格合起来才夹住"批次"这件事的两头。
    /// </summary>
    private static void ImportBatchReallyQueuedPerFile()
    {
        RunOnStaThread(() =>
        {
            const int files = 6;
            string dir = Path.Combine(Path.GetTempPath(), "ls-queue-" + Guid.NewGuid().ToString("N")[..6]);
            List<string> paths = WritePngFiles(dir, files, 60, 120);
            var form = new MainForm();
            try
            {
                form.CreateControl();
                form.ProjectForTest.Doc.Mode = StitchMode.LongScroll;   // 夹具指定长截图：文档默认已是 Unset，自动分析只对 LongScroll 起（不写就到不了分析通路）
                form.ImportPaths(paths.ToArray());

                Battery.AssertEqual(files + 2, form.UiRefreshDeferredForTest,
                    "六张图就该挂起 8 笔（6 次追加 + 1 次重排 + 1 次 Touch），一笔不许差：" +
                    $"实际 {form.UiRefreshDeferredForTest} 笔——少了就是通知压根没发出来，多了就是放闸后又塞进批次");
                Battery.Assert(form.UiRefreshDeferredForTest > form.UiRefreshCountForTest,
                    "被合并掉的笔数必须真的多于真身执行次数，否则就是在拿「没刷」冒充「合并」" +
                    $"（挂起 {form.UiRefreshDeferredForTest} / 真身 {form.UiRefreshCountForTest}）");
                Battery.Evidence($"挂起 {form.UiRefreshDeferredForTest} 笔 ≥ 六张、真身 {form.UiRefreshCountForTest} 次 ⇒ 合并确实发生了");
            }
            finally { form.Dispose(); TryDelete(dir); }
        });
    }

    /// <summary>
    /// 用户报的第七条：「分析结束后画布中的图片无论窗口多大都得保持居中显示，你每次都放在左对齐」。
    /// 这一格量的是<b>接线</b>：只走产品通路（<c>ImportPaths</c>，不手动调用摆正的方法），
    /// 然后问"屏上那块内容矩形的中心与视口中心差几像素"。
    /// 判据不写死坐标：数从视口尺寸与内容尺寸现算，窗口多大都成立——用户那句话点名的就是"无论窗口多大"。
    /// 算术本身（装得下／装不下那一轴怎么办）在 <c>canvas/内容装得下就摆正：画面中心与视口中心重合</c> 那一格里量。
    /// </summary>
    private static void ImportCentresContentInView()
    {
        RunOnStaThread(() =>
        {
            (MainForm form, string dir) = FormWithImports(count: 3);
            try
            {
                StitchCanvasControl canvas = form.CanvasForTest;
                Size view = canvas.Viewport.DeviceSize;
                Battery.Assert(view.Width > 40 && view.Height > 40,
                    $"夹具自证：离屏窗口的画布客户区必须有尺寸，否则居中没处可居（实际 {view}）");

                Core.Geometry.IntSize content = form.ProjectForTest.Layout.CanvasSize;
                Battery.Assert(content.Width > 0 && content.Height > 0, $"夹具自证：三段图必须有画布尺寸，实际 {content}");

                var rect = canvas.Viewport.CanvasToDevice(
                    new Core.Geometry.IntRect(0, 0, content.Width, content.Height));
                int dx = Math.Abs(rect.Left + rect.Width / 2 - view.Width / 2);
                int dy = Math.Abs(rect.Top + rect.Height / 2 - view.Height / 2);
                Battery.Assert(dx <= 2,
                    $"导入结束后内容横向必须摆在视口正中（用户原话：每次都放在左对齐）：中心差 {dx}px，" +
                    $"内容设备矩形 {rect} 对视口 {view}");
                Battery.Assert(dy <= 2, $"纵向同理：中心差 {dy}px（内容装得下这一轴时才要求居中，见反证那一格）");

                Battery.Evidence($"内容设备矩形 {rect} · 视口 {view} ⇒ 中心差 ({dx},{dy})px，两轴都在 2px 内");
            }
            finally { form.Dispose(); TryDelete(dir); }
        });
    }

    /// <summary>
    /// 用户第四十四改复看仍报的那条闪烁：「添加一张图片你整个画面就渲染一次……
    /// 我希望添加完所有图片、页面不更新，直至进度条全部走完消失后才渲染」。
    /// 第四十三改只闸住了主窗口的 <c>RefreshChrome</c>；画布与左侧素材列表各自直通订阅
    /// <c>Project.Changed</c>，循环里那下 <c>PumpUi()</c> 把它们的 Invalidate 一张张泵上屏——
    /// 所以这一格量的不是刷新次数而是<b>画布真身画了几帧</b>（<c>PaintCount</c>）：
    /// 导入期间必须一帧都不画。这不是空转门：导入循环自己每趟都 <c>PumpUi()</c>，
    /// 没闸住的话每一笔 Invalidate 都会在这里变成一帧真画。
    /// 放闸后欠账必须清掉（不许挂着永远不画），被挂起的笔数由反证那一格算死。
    /// </summary>
    private static void ImportBatchHoldsCanvasPaintsUntilRelease()
    {
        RunOnStaThread(() =>
        {
            const int files = 6;
            string dir = Path.Combine(Path.GetTempPath(), "ls-paint-" + Guid.NewGuid().ToString("N")[..6]);
            List<string> paths = WritePngFiles(dir, files, 60, 120);
            var form = new MainForm();
            try
            {
                form.CreateControl();
                StitchCanvasControl canvas = form.CanvasForTest;
                long paints0 = canvas.ScreenPaintsForTest;
                long deferred0 = canvas.RepaintsDeferredForTest;

                form.ProjectForTest.Doc.Mode = StitchMode.LongScroll;   // 夹具指定长截图：文档默认已是 Unset，自动分析只对 LongScroll 起（不写就到不了分析通路）
                form.ImportPaths(paths.ToArray());

                long during = canvas.ScreenPaintsForTest - paints0;
                long deferred = canvas.RepaintsDeferredForTest - deferred0;
                Battery.AssertEqual(files, form.ProjectForTest.Doc.Segments.Count,
                    "夹具自证：六张必须真进了文档，否则下面量的是没发生的导入");
                Battery.AssertEqual(0, during,
                    $"导入期间画布一帧都不许画（进度条走完之前页面不更新），实际画了 {during} 帧——每多一帧就是一闪");
                Battery.Assert(deferred >= files,
                    $"夹具自证：必须有重画请求真的被挂起（实际 {deferred} 笔 ≥ {files}）——0 笔就是通知没发出来，" +
                    "那一帧不画是「没发生」冒充「被合并」");
                Battery.Assert(!canvas.RepaintOwedForTest,
                    "放闸之后不许还欠着帧：欠账挂着永远不画，用户看到的就是导入完画面还是旧的");
                Battery.Evidence($"六张图 ⇒ 导入期间画布 {during} 帧、挂起 {deferred} 笔、放闸后欠账 0 ⇒ 整批只等最后一次渲染");
            }
            finally { form.Dispose(); TryDelete(dir); }
        });
    }

    /// <summary>
    /// 上一格的前提门：「导入期间 0 帧」有个能喂绿的做法——把 <c>Project.Changed</c> 的订阅写断，
    /// 画布与列表压根收不到通知，自然一帧不画、也不闪，但界面从此是死的。
    /// 所以这里把三条直通订阅各自挂起的笔数算死：导入 6 张 = 6 笔追加 + 1 笔重排 + 1 笔 Touch = <b>8 笔</b>，
    /// 画布、素材列表、主窗口 chrome 三家必须<b>一笔不差地都等于 8</b>——
    /// 任何一家少一笔就是它那条订阅没进批次（用户看到的就是那一家的闪烁）。
    /// </summary>
    private static void ImportBatchDefersEveryChangedNotice()
    {
        RunOnStaThread(() =>
        {
            const int files = 6;
            string dir = Path.Combine(Path.GetTempPath(), "ls-defer-" + Guid.NewGuid().ToString("N")[..6]);
            List<string> paths = WritePngFiles(dir, files, 60, 120);
            var form = new MainForm();
            try
            {
                form.CreateControl();
                form.ProjectForTest.Doc.Mode = StitchMode.LongScroll;   // 夹具指定长截图：文档默认已是 Unset，自动分析只对 LongScroll 起（不写就到不了分析通路）
                form.ImportPaths(paths.ToArray());

                const int expected = files + 2;
                Battery.AssertEqual(expected, form.UiRefreshDeferredForTest,
                    $"主窗口 chrome 应挂起 {expected} 笔（6 追加 + 1 重排 + 1 Touch），实际 {form.UiRefreshDeferredForTest}");
                Battery.AssertEqual(expected, form.SourcePanelForTest.RefreshesDeferredForTest,
                    $"素材列表应挂起 {expected} 笔重建，实际 {form.SourcePanelForTest.RefreshesDeferredForTest}" +
                    "——少一笔就是列表那条订阅没进批次（缩略图一列一列闪）");
                // 画布比那两家多一笔：导入末尾的居中会把视口横向推负（装得下那一轴才推），
                // 视口那一笔重画请求也进了批次。先把"真的推了"量出来，再按 expected+1 对账，
                // 否则这个 +1 就是凭空写的数。
                Battery.Assert(form.CanvasForTest.Viewport.TopLeft.X < 0,
                    $"夹具自证：窄内容导入后视口横向必须被推负（居中生效），实际 {form.CanvasForTest.Viewport.TopLeft}");
                Battery.AssertEqual(expected + 1, form.CanvasForTest.RepaintsDeferredForTest,
                    $"画布应挂起 {expected} 笔 Changed + 1 笔居中视口 = {expected + 1} 笔重画，" +
                    $"实际 {form.CanvasForTest.RepaintsDeferredForTest}——少一笔就是画布那条订阅没进批次（用户看到的就是画布在闪）");
                Battery.Evidence($"chrome 与素材列表各挂起 {expected} 笔、画布 {expected + 1} 笔（多那一笔是居中视口）⇒ 三条直通的车都进了批次");
            }
            finally { form.Dispose(); TryDelete(dir); }
        });
    }

    /// <summary>
    /// 需求 §3.2：悬停出现"点击编辑拼缝 2→3"。
    /// 量法：沿画布纵向逐屏像素扫一遍，凡是命中拼缝的地方都要给出**带序号**的句子，
    /// 三张图应恰好扫到 1→2 与 2→3 两句（序号错位或写死成一个都不行）。
    /// </summary>
    private static void HoverTipNamesTheSeamUnderCursor()
    {
        RunOnStaThread(() =>
        {
            (MainForm form, string dir) = FormWithImports(count: 3);
            try
            {
                StitchCanvasControl canvas = form.CanvasForTest;
                int x = canvas.Width / 2;
                var tips = new List<string>();
                for (int y = 0; y < canvas.Height; y++)
                {
                    string? tip = form.HoverTipAtForTest(new Point(x, y));
                    if (tip is not null && (tips.Count == 0 || tips[^1] != tip)) tips.Add(tip);
                }

                Battery.AssertEqual(2, tips.Count,
                    $"三张图应扫到两句提示，实际扫到 {tips.Count} 句：{string.Join(" | ", tips)}");
                // 这一格管的是<b>序号可指认</b>（需求 §3.2），不管后半句怎么说。
                // 后半句第十四轮改成了"蓝线是参照物、压着它拖动＝挪下方那张图"，
                // 那是措辞、不是这一格的承重墙——所以判据钉的是"以『拼缝 i→i+1』开头"这个形状：
                // 序号写死成同一个、或者错位、或者整句换成没有序号的说明，都照样红。
                Battery.Assert(tips[0].StartsWith("拼缝 1→2", StringComparison.Ordinal),
                    "第一条拼缝的悬停提示要按序号点名它（实际：" + tips[0] + "）");
                Battery.Assert(tips[1].StartsWith("拼缝 2→3", StringComparison.Ordinal),
                    "第二条拼缝的悬停提示要按序号点名它（实际：" + tips[1] + "）");
                Battery.Evidence($"逐行扫描命中：{string.Join(" | ", tips)}");
            }
            finally { form.Dispose(); TryDelete(dir); }
        });
    }

    /// <summary>
    /// 需求 §3.3：状态栏右侧持续显示当前模式、画布实际像素、预览缩放比例。
    /// 判据不写死数字，而是要求"状态栏那句话必须与视口/布局的当前值一字不差"——
    /// 这样缩放后再读，它照样得跟着变（写死的百分比是抓不到"忘了刷新"的）。
    /// </summary>
    private static void StatusBarReportsZoomAndCanvasSize()
    {
        RunOnStaThread(() =>
        {
            (MainForm form, string dir) = FormWithImports(count: 3, width: 60, height: 120);
            try
            {
                string expectedSize = $"{form.ProjectForTest.Layout.CanvasSize.Width}×" +
                                      $"{form.ProjectForTest.Layout.CanvasSize.Height} px";
                Battery.AssertTextEqual("60×360 px", expectedSize, "未分析时三张竖排应紧接成 60×360");
                Battery.AssertTextEqual(expectedSize, form.StatusBarSizeTextForTest, "状态栏的画布像素");

                string expectedZoom = $"{form.CanvasForTest.Viewport.Zoom * 100:F0}%";
                Battery.AssertTextEqual(expectedZoom, form.StatusBarZoomTextForTest, "状态栏的缩放比例");

                form.CanvasForTest.Viewport.FitWidth(form.ProjectForTest.Layout.CanvasSize);
                string afterZoom = $"{form.CanvasForTest.Viewport.Zoom * 100:F0}%";
                Battery.AssertTextEqual(afterZoom, form.StatusBarZoomTextForTest,
                    "适应宽度之后状态栏的缩放比例必须跟着变（不刷新就是假的）");
                Battery.Assert(!form.StatusBarZoomTextForTest.StartsWith("0%"),
                    "缩放比例不应被夹到看不见的地步");

                Battery.Evidence($"状态栏：{expectedSize} + {expectedZoom} → 适应宽度后 {afterZoom}");
            }
            finally { form.Dispose(); TryDelete(dir); }
        });
    }

    /// <summary>
    /// 需求 §3.5 的"逐张开关和前后对比"。界面上那一行勾选框不是摆设：
    /// 取消勾选必须真的把该处像素放回原样，而且必须进撤销栈、置脏（它改的是最终产物）。
    /// </summary>
    private static void PerImagePatchToggleIsUndoableAndDirties()
    {
        RunOnStaThread(() =>
        {
            (MainForm form, string dir) = FormWithImports(count: 2, width: 60, height: 120);
            try
            {
                Project p = form.ProjectForTest;
                // 补丁由滚动条清理写入——调的是产品那个方法，用的也是主窗口自己那对 resolver/composer。
                // 不去 PerformClick 面板上那枚「检测并应用预览」：窗体从未显示时面板按钮拿不到句柄，
                // PerformClick 是空转（本轮实测：方法一次都没进），点了等于没量。
                CleanupPlanner.Apply(p, form.ResolverForTest, form.ComposerForTest);
                form.ClickToolButtonForTest(4);   // 切到清理面板，逐张补丁那几行才会被渲染出来

                PixelPatch? patch = p.Doc.Segments.SelectMany(s => s.Patches).FirstOrDefault();
                Battery.Assert(patch is not null,
                    "夹具图右缘有滚动条，跑完滚动条清理应该至少写进一个补丁");
                // 补丁那一行的文字以 "#段序号 " 开头；面板上还有一枚同名的「清理滚动条」功能开关，
                // 只按"含滚动条"找就会点错那一枚（点错的话改的是开关、不是补丁，判据就空转了）。
                // 行的取样**不许依赖 OfType 的枚举顺序**：第五十一改回修把面板子控件下标翻成阅读序
                // （修「当前工具」标题掉到说明文字下面的倒序渲染），OfType 的枚举顺序跟着变了，
                // 裸 FirstOrDefault 会拿到另一段的补丁行、改的就不是手上这个 patch。
                // 这里按补丁所在段的序号拼前缀点名——与 ShowPatchRows 写行的规则同一条账。
                int patchOrdinal = p.Doc.Segments
                    .Select((seg, i) => (seg, i))
                    .Where(t => t.seg.Patches.Contains(patch!))
                    .Select(t => t.i + 1)
                    .First();
                string rowPrefix = $"#{patchOrdinal} ";
                CheckBox? row = form.ToolPanelForTest.Controls.OfType<CheckBox>()
                    .FirstOrDefault(c => c.Text.StartsWith(rowPrefix, StringComparison.Ordinal)
                                      && c.Text.Contains("滚动条", StringComparison.Ordinal) && c.Checked);
                Battery.Assert(row is not null,
                    $"清理面板应渲染出这一张补丁的勾选行（文本以 # 开头且含「滚动条」），实际有 " +
                    $"{form.ToolPanelForTest.Controls.OfType<CheckBox>().Count()} 个勾选框");

                p.MarkSaved();
                // 勾选框没有 PerformClick，改 Checked 就是用户点下去的那条通路（CheckedChanged 事件）
                row!.Checked = false;
                Battery.Assert(!patch!.Enabled, "取消勾选应把该补丁置为不生效");
                Battery.Assert(p.IsDirty, "开关补丁改的是最终像素，必须置脏（否则保存会漏掉）");
                Battery.Assert(p.CanUndo && p.NextUndoLabel == "切换补丁",
                    $"必须能撤销这次开关，实际标签：{p.NextUndoLabel}");

                p.Undo();
                // 撤销会把整个文档换成快照里的那份，所以必须重新取对象——
                // 继续读手上这个 patch 量到的是已被换下的旧副本，与界面实际状态无关
                PixelPatch afterUndo = p.Doc.Segments
                    .SelectMany(s => s.Patches)
                    .First(x => x.Reason.StartsWith(CleanupPlanner.AutoReasonPrefix, StringComparison.Ordinal));
                Battery.Assert(afterUndo.Enabled, "Ctrl+Z 应把补丁恢复成生效状态");
                Battery.Evidence($"勾选框「{row.Text}」：关→置脏→入栈→撤销回生效");
            }
            finally { form.Dispose(); TryDelete(dir); }
        });
    }

    /// <summary>
    /// 导出对话框新加的两个入口：文件名模板与 JPEG 质量。
    /// 判据读的是<b>对话框自己算出的计划</b>——模板没接上时产物名会退回默认值，当场翻红；
    /// 质量框只在 JPEG 下可用，别的格式下必须禁用（可用但无效是骗人）。
    /// </summary>
    private static void ExportDialogTemplateAndQualityAreWired()
    {
        RunOnStaThread(() =>
        {
            (MainForm form, string dir) = FormWithImports(count: 2, width: 60, height: 120);
            string outDir = Path.Combine(dir, "out");
            Directory.CreateDirectory(outDir);
            try
            {
                var withTemplate = form.OpenExportDialogForTest(outDir, "长图_{index}.{ext}", 70);
                Battery.AssertEqual(1, withTemplate.FileNames.Count, "未分段时应只有一个文件");
                Battery.AssertTextEqual("长图_1.png", withTemplate.FileNames[0],
                    "文件名必须由对话框上的模板决定（PNG 下扩展名跟着换）");
                Battery.AssertTextEqual("长图_{index}.{ext}", withTemplate.TemplateText,
                    "输入框应回读项目里记录的那份模板，不能每次打开都回到出厂值");
                Battery.Assert(!withTemplate.QualityEnabled,
                    "PNG 下质量框不该可用（它对 PNG 没有任何作用）");
                Battery.AssertEqual(70, withTemplate.Quality, "质量应读回项目里记录的那份");

                form.ProjectForTest.Doc.Export.FileNameTemplate = "stitched_{index}.{ext}";
                var withDefault = form.OpenExportDialogForTest(outDir);
                Battery.AssertTextEqual("stitched_1.png", withDefault.FileNames[0], "默认模板照旧生效");

                Battery.Evidence($"模板 长图_{{index}}.{{ext}} → {withTemplate.FileNames[0]}；" +
                                 $"默认 → {withDefault.FileNames[0]}；PNG 下质量禁用、值仍读回 70");
            }
            finally { form.Dispose(); TryDelete(dir); }
        });
    }

    private static void ExportOptionsStaySelected()
    {
        RunOnStaThread(() =>
        {
            (Project project, MemoryResolver resolver, _) = TestProject.Create(StitchMode.Vertical,
                Fixture.OpaqueLike(60, 120, seed: 201), Fixture.OpaqueLike(60, 120, seed: 202));
            using var dialog = new ExportDialog(project, new CanvasComposer(resolver), new EncoderRegistry());
            var selected = dialog.SetPreviewOptionsForTest(50, 10, 2);
            Battery.AssertEqual(50, selected.MaxHeight, "每片高度应保留用户输入");
            Battery.AssertEqual(10, selected.Overlap, "分段重叠应保留用户输入");
            Battery.AssertEqual(2, selected.Divisor, "缩小倍数应保留用户输入");
            Battery.Assert(selected.Pieces > 1, "输出高度 120、每片 50 应生成多片");
            Battery.Evidence($"输入 50/10/1:2 后仍显示 {selected.MaxHeight}/{selected.Overlap}/1:{selected.Divisor}，计划 {selected.Pieces} 片");
        });
    }

    /// <summary>
    /// 需求 §4 表格里"↑↓←→ 默认按 1 px 屏幕单位平移视口；可在设置中关闭"这一格。
    /// 之前浏览态的方向键是什么都不做的（代码注释写着"由主窗口处理"，但主窗口没有这段代码）。
    /// 判据量三件事：方向要对（按 ↓ 是往下看）、一次恰好 1 屏幕像素、关掉后一个像素都不动。
    /// </summary>
    private static void BrowseArrowPansViewportAndCanBeOff()
    {
        RunOnStaThread(() =>
        {
            (Project p, MemoryResolver r, _) = TestProject.Create(StitchMode.Vertical,
                Fixture.OpaqueLike(60, 120, seed: 61), Fixture.OpaqueLike(60, 120, seed: 62));
            using var canvas = new StitchCanvasControl(p, new CanvasComposer(r));
            canvas.Size = new Size(400, 300);
            canvas.Viewport.SetOneToOne(new Point(0, 0));   // 1:1，屏幕像素与源像素等量，便于核对

            int y0 = canvas.Viewport.TopLeft.Y;
            Battery.Assert(canvas.PanByArrow(Keys.Down, false), "按 ↓ 应被消费");
            Battery.AssertEqual(y0 + 1, canvas.Viewport.TopLeft.Y,
                "按 ↓ 视口上移一个源像素（= 往下看 1 屏幕像素）");
            Battery.AssertEqual(0, canvas.Viewport.TopLeft.X, "只该动纵轴");

            Battery.Assert(canvas.PanByArrow(Keys.Down, true), "Shift+↓ 也应被消费");
            Battery.AssertEqual(y0 + 41, canvas.Viewport.TopLeft.Y, "Shift 走一格（40 px）");

            Battery.Assert(canvas.PanByArrow(Keys.Up, false), "按 ↑ 应被消费");
            Battery.AssertEqual(y0 + 40, canvas.Viewport.TopLeft.Y, "↑ 与 ↓ 方向必须相反");

            canvas.BrowseArrowPanEnabled = false;
            int before = canvas.Viewport.TopLeft.Y;
            Battery.Assert(!canvas.PanByArrow(Keys.Down, false),
                "关掉后方向键不该再平移，而且不该谎报「已处理」");
            Battery.AssertEqual(before, canvas.Viewport.TopLeft.Y, "关掉后视口一个像素都不动");

            // 编辑态的 1 源像素微调不能被这个开关带走
            canvas.BrowseArrowPanEnabled = false;
            canvas.SelectSeam(p.Doc.Seams[0].Id);
            int seamY = p.Doc.Seams[0].ResolveOffset(new IntPoint(0, 120)).Y;
            canvas.NudgeSelected(new IntPoint(0, 1));
            Battery.AssertEqual(seamY + 1, p.Doc.Seams[0].ResolveOffset(new IntPoint(0, 120)).Y,
                "浏览态关闭方向键不影响编辑态的键盘微调");
            Battery.Evidence("↓ 1px、Shift ↓ 40px、↑ 反向；关掉后不动；编辑态仍可 1 源像素微调");
        });
    }

    /// <summary>
    /// 需求 §3.1："图片数量少于 2 时提示继续添加……拼缝工具禁用"。
    /// 只有一张图时"检查拼缝"没有对象，按钮必须点不动并说明为什么，而不是点了没反应。
    /// </summary>
    private static void SeamToolDisabledBelowTwoImages()
    {
        RunOnStaThread(() =>
        {
            using var empty = new MainForm();
            empty.CreateControl();
            Battery.Assert(!empty.ToolButtonEnabledForTest(0), "空项目里「拼缝」不该可用");
            Battery.Assert(!empty.ToolButtonEnabledForTest(1), "空项目里「切分」不该可用");

            (MainForm one, string dir) = FormWithImports(count: 1);
            try
            {
                Battery.Assert(!one.ToolButtonEnabledForTest(0), "只有 1 张图时「拼缝」仍应禁用");
                Battery.Assert(one.ToolButtonEnabledForTest(1), "有 1 张图后「切分」应可用");
            }
            finally { one.Dispose(); TryDelete(dir); }

            (MainForm two, string dir2) = FormWithImports(count: 2);
            try
            {
                Battery.Assert(two.ToolButtonEnabledForTest(0), "两张图后「拼缝」应自动解禁");
                Battery.Assert(two.ToolButtonEnabledForTest(4), "两张图后「清理」应可用");
            }
            finally { two.Dispose(); TryDelete(dir2); }

            Battery.Evidence("0/1 张：拼缝禁用；2 张：解禁。其余四个工具在 ≥1 张时可用");
        });
    }

    /// <summary>
    /// 需求 §2："窄窗口时侧栏可折叠，不遮挡画布"。
    /// 折叠必须把腾出的空间真的交给画布（宽度变大），并且画布不能被一起藏掉。
    /// </summary>
    private static void SidePanelsCollapseWithoutHidingCanvas()
    {
        RunOnStaThread(() =>
        {
            (MainForm form, string dir) = FormWithImports(count: 2);
            try
            {
                int canvasAtStart = form.CanvasForTest.Width;
                Battery.Assert(canvasAtStart > 400, $"起点中央画布宽度应可见，实际 {canvasAtStart}");

                bool leftHidden = !form.ToggleSidePanelForTest(left: true);
                form.PerformLayout();
                Battery.Assert(leftHidden, "左侧栏应被折叠");
                int afterLeft = form.CanvasForTest.Width;
                Battery.Assert(afterLeft > canvasAtStart,
                    $"折叠左侧栏后画布应接管空间：{canvasAtStart} → {afterLeft}");

                bool rightHidden = !form.ToggleSidePanelForTest(left: false);
                form.PerformLayout();
                Battery.Assert(rightHidden, "右侧栏应被折叠");
                int afterRight = form.CanvasForTest.Width;
                Battery.Assert(afterRight > afterLeft,
                    $"再折叠右侧栏后画布应再宽一点：{afterLeft} → {afterRight}");

                // 展开回来必须与起点一模一样，否则"折叠"就成了永久改布局
                form.ToggleSidePanelForTest(left: true);
                form.ToggleSidePanelForTest(left: false);
                form.PerformLayout();
                Battery.AssertEqual(canvasAtStart, form.CanvasForTest.Width, "两次展开后画布宽度应复原");
                Battery.Evidence($"画布宽 {canvasAtStart} →（折叠左）{afterLeft} →（折叠右）{afterRight} → " +
                                 $"（复原）{form.CanvasForTest.Width}");
            }
            finally { form.Dispose(); TryDelete(dir); }
        });
    }

    /// <summary>
    /// 审查第四轮 P2 的规则本身：取消撤不回已经排进队列的写回回调，
    /// 所以只能靠"轮次号 + 项目实例 + Revision"三重核对。
    /// 这条判据把三种过期情形逐个注入并量，还复刻了修复前的单条件写法证明它拦不住第③种——
    /// 以前这条只能写进 NOT-RUN（要真开线程、真排队），抽成 <see cref="AnalysisGate"/> 之后能当场量。
    /// </summary>
    private static void AnalysisGateRejectsStaleResults()
    {
        (Project p, _, _) = TestProject.Create(StitchMode.LongScroll,
            Fixture.OpaqueLike(60, 120, seed: 71), Fixture.OpaqueLike(60, 120, seed: 72));
        var gate = new AnalysisGate();

        AnalysisGate.Ticket fresh = gate.Begin(p);
        Battery.Assert(gate.Allows(fresh, p), "正向对照：刚取的票必须放行，否则分析永远写不回去");
        Battery.Assert(gate.RejectReason(fresh, p) is null, "能写回时不该给出拒绝原因");

        // ① 分析期间用户动了参数
        p.Mutate("用户挪了一下", _ => p.Doc.Seams[0].ApplyManual(new IntPoint(0, 7), p.Doc.Mode));
        Battery.Assert(!gate.Allows(fresh, p), "参数被改过之后旧票必须失效");
        Battery.Assert(gate.RejectReason(fresh, p)!.Contains("参数", StringComparison.Ordinal),
            $"要能指出是哪一条不符，实际：{gate.RejectReason(fresh, p)}");

        // ② 新一轮分析开始（全量之后又点单条重分析）→ 上一轮整批作废
        (Project q, _, _) = TestProject.Create(StitchMode.LongScroll,
            Fixture.OpaqueLike(60, 120, seed: 73), Fixture.OpaqueLike(60, 120, seed: 74));
        var gate2 = new AnalysisGate();
        AnalysisGate.Ticket first = gate2.Begin(q);
        AnalysisGate.Ticket second = gate2.Begin(q);
        Battery.Assert(!gate2.Allows(first, q), "已有新一轮开始时，旧一轮必须被丢弃");
        Battery.Assert(gate2.Allows(second, q), "最新一轮的票必须可用");
        Battery.AssertEqual(2, gate2.Generation, "两轮分析的轮次号必须递增");

        // ③ 换成了另一个项目：即使修订号碰巧一样也要拦
        (Project other, _, _) = TestProject.Create(StitchMode.LongScroll,
            Fixture.OpaqueLike(60, 120, seed: 75), Fixture.OpaqueLike(60, 120, seed: 76));
        other.Touch(); // Match p's revision so this case isolates the project-instance check.
        var gate3 = new AnalysisGate();
        AnalysisGate.Ticket onP = gate3.Begin(p);
        Battery.AssertEqual(onP.Revision, other.Revision,
            "夹具前提：两个项目的修订号必须相同，否则这条注入量不出实例检查的价值");
        Battery.Assert(!gate3.Allows(onP, other), "换了项目实例必须整批丢弃");
        Battery.Assert(gate3.RejectReason(onP, other)!.Contains("替换", StringComparison.Ordinal),
            $"要指认出是项目被替换，实际：{gate3.RejectReason(onP, other)}");

        // 守卫：只核对修订号的旧写法，在同样这份数据上必须**放过**第 ③ 种过期结果
        bool OldRuleRevisionOnly(AnalysisGate.Ticket t, Project current) => t.Revision == current.Revision;
        Battery.Assert(OldRuleRevisionOnly(onP, other),
            "夹具失效：旧写法也已经拦住了换项目，那这条守卫就没有区分力了");
        Battery.Evidence("三条过期路径逐个注入都被拦；只核修订号的旧写法会放过“换了项目”这一条");
    }

    /// <summary>
    /// 审查问题 #6：数值框回调曾捕获"建面板时"的 current 偏移。
    /// 于是先改 X 再改 Y 时，Y 那次把 X 顶回旧值——用户看到的是"我明明填了 X"。
    /// 判据按用户的实际顺序连填两格（中间面板会整块重建），最后核对两格都在。
    /// </summary>
    private static void SeamXYBoxesDoNotClobberEachOther()
    {
        RunOnStaThread(() =>
        {
            // 这一格量的是"先填 X 再填 Y，Y 不能把刚才的 X 顶回去"（闭包旧值缺陷 #6）。
            // 模式必须是竖拼：长截图里 X 恒为 0（第七轮的锁横向），两格都填不出"X 被顶回"的形状了。
            (Project p, MemoryResolver r, _) = TestProject.Create(StitchMode.Vertical,
                Fixture.OpaqueLike(60, 120, seed: 81), Fixture.OpaqueLike(60, 120, seed: 82));
            string seamId = p.Doc.Seams[0].Id;
            // ToolPanel 要的是会读磁盘的 FileSourceResolver（这里不碰文件，只为了让构造函数拿到真类型）
            var disk = new FileSourceResolver();
            using var panel = new ToolPanel(p, disk, new CanvasComposer(r));
            panel.SeamId = seamId;

            FindIntRow(panel, "X").Value = 9;
            FindIntRow(panel, "Y").Value = -14;

            IntPoint? manual = p.Doc.Seams.First(s => s.Id == seamId).ManualOffset;
            Battery.Assert(manual is not null, "两次输入都应写入手动偏移");
            Battery.AssertEqual(9, manual!.Value.X, "改完 Y 之后 X 必须还是刚才填的 9");
            Battery.AssertEqual(-14, manual.Value.Y, "Y 应为 -14");
            Battery.Evidence($"竖拼里 X=9 → Y=-14 连填：生效偏移 ({manual.Value.X},{manual.Value.Y})");
        });
    }

    /// <summary>
    /// 在拼缝面板里按标签找到那行数值输入框。
    /// 每填一格面板都会整块重建，所以**每一格都要重新找**——
    /// 攥着上一轮的控件引用会写到已 Dispose 的对象上。
    /// </summary>
    private static NumericUpDown FindIntRow(Control root, string label)
    {
        foreach (Control c in root.Controls)
        {
            if (c is FlowLayoutPanel flow &&
                flow.Controls.OfType<Label>().Any(l => l.Text == label) &&
                flow.Controls.OfType<NumericUpDown>().FirstOrDefault() is { } box)
                return box;
            if (TryFindRow(c, label) is { } nested) return nested;
        }
        throw new Exception($"没找到标签为 {label} 的输入行");

        static NumericUpDown? TryFindRow(Control scope, string label)
        {
            foreach (Control inner in scope.Controls)
            {
                if (inner is FlowLayoutPanel f2 &&
                    f2.Controls.OfType<Label>().Any(l => l.Text == label) &&
                    f2.Controls.OfType<NumericUpDown>().FirstOrDefault() is { } b2)
                    return b2;
                if (TryFindRow(inner, label) is { } deeper) return deeper;
            }
            return null;
        }
    }

    /// <summary>
    /// 审查问题 #7：重新定位此前整个埋在文件对话框里，只能真机点。
    /// 现在判定与写入在 <see cref="SourcePanel.RelocateTo"/>，四条分支都要有可指认的回话：
    /// 读不出来 / 尺寸不符要先问（且一个字节都不许改）/ 正常换过去（缺失标记清掉、缓存作废、可撤销）/
    /// 确认后连尺寸一起改。
    /// </summary>
    private static void RelocateReportsEveryBranch()
    {
        RunOnStaThread(() =>
        {
            string dir = Path.Combine(Path.GetTempPath(), "ls-rel-" + Guid.NewGuid().ToString("N")[..6]);
            Directory.CreateDirectory(dir);
            try
            {
                var original = Fixture.OpaqueLike(60, 120, seed: 91);
                var moved = Fixture.OpaqueLike(60, 120, seed: 91);      // 同一份内容，换了文件名
                var different = Fixture.OpaqueLike(60, 200, seed: 92);  // 尺寸不同
                string originalFile = Path.Combine(dir, "orig.png");
                string movedFile = Path.Combine(dir, "moved.png");
                string bigFile = Path.Combine(dir, "big.png");
                File.WriteAllBytes(originalFile, Core.Imaging.Png.PngEncoder.EncodeToBytes(original));
                File.WriteAllBytes(movedFile, Core.Imaging.Png.PngEncoder.EncodeToBytes(moved));
                File.WriteAllBytes(bigFile, Core.Imaging.Png.PngEncoder.EncodeToBytes(different));

                var resolver = new FileSourceResolver();
                var project = new Project(new ProjectDocument { Mode = StitchMode.Vertical });
                var src = new SourceImage
                {
                    Id = Project.NewId("src"), Path = originalFile, FileName = "orig.png",
                    Size = new IntSize(60, 120), Format = "png",
                };
                resolver.Register(src);
                project.Doc.Sources.Add(src);
                project.AppendSegmentForSource(src);
                using var panel = new SourcePanel(project, resolver);

                // ① 读不出来
                SourcePanel.RelocateOutcome bad = panel.RelocateTo(0,
                    Path.Combine(dir, "nothing.png"), out string? badMsg);
                Battery.AssertEqual(SourcePanel.RelocateOutcome.Failed, bad, "不存在的文件必须报失败");
                Battery.Assert(badMsg is not null && badMsg.Contains("读不出来", StringComparison.Ordinal),
                    $"失败要说得出原因，实际：{badMsg}");
                Battery.AssertEqual(originalFile, project.Doc.Sources[0].Path, "失败时路径不许被动过");

                // ② 尺寸不符：先问，而且不落笔
                int revisionBefore = project.Revision;
                SourcePanel.RelocateOutcome ask = panel.RelocateTo(0, bigFile, out string? askMsg);
                Battery.AssertEqual(SourcePanel.RelocateOutcome.NeedsSizeConfirmation, ask, "尺寸不同要先问用户");
                Battery.Assert(askMsg!.Contains("60×200", StringComparison.Ordinal) &&
                               askMsg.Contains("60×120", StringComparison.Ordinal),
                    $"那句话必须把两个尺寸都念出来，实际：{askMsg}");
                Battery.AssertEqual(originalFile, project.Doc.Sources[0].Path, "用户还没同意，路径不许被改");
                Battery.AssertEqual(revisionBefore, project.Revision,
                    "没落笔就不许推进修订号，否则撤销栈里会多出一条什么都没做的记录");

                // ③ 源文件丢失后重新定位成功：缺失标记必须跟着清掉
                project.Mutate("标记丢失", _ => project.Doc.Sources[0].SourceMissing = true);
                Battery.Assert(project.Doc.Sources[0].SourceMissing, "前置：该条应为缺失");
                SourcePanel.RelocateOutcome ok = panel.RelocateTo(0, movedFile, out string? okMsg);
                Battery.AssertEqual(SourcePanel.RelocateOutcome.Applied, ok, $"换路径应成功，实际：{okMsg}");
                SourceImage after = project.Doc.Sources[0];
                Battery.AssertEqual(movedFile, after.Path, "路径应指向新文件");
                Battery.AssertEqual("moved.png", after.FileName, "文件名应跟着更新");
                Battery.Assert(!after.SourceMissing, "重新定位成功必须清掉缺失标记，否则列表仍显示丢失、导出仍被硬拦");
                Battery.Assert(project.CanUndo && project.NextUndoLabel == "重新定位源文件",
                    $"必须可撤销，实际标签：{project.NextUndoLabel}");
                Battery.AssertEqual(new IntSize(60, 120), resolver.Resolve(after.Id).Size,
                    "解析器应能按新路径读出这张图（旧缓存要作废）");

                // ④ 尺寸不符但已确认：应真的换过去并把记录尺寸改成新的
                SourcePanel.RelocateOutcome forced = panel.RelocateTo(0, bigFile, out _, acceptSizeChange: true);
                Battery.AssertEqual(SourcePanel.RelocateOutcome.Applied, forced, "确认后应真的换过去");
                Battery.AssertEqual(new IntSize(60, 200), project.Doc.Sources[0].Size,
                    "记录里的尺寸必须跟着新文件走，否则布局仍按旧的高算");
                Battery.Evidence("四条分支：失败 / 先问（参数未变）/ 换路径（清缺失 + 可撤销）/ 确认后改尺寸");
            }
            finally { TryDelete(dir); }
        });
    }

    /// <summary>
    /// 需求 §4.1："默认按导入顺序排列，不依赖文件名猜测"。
    /// 之前这条只在代码里成立（审查问题 #8 的修复），界面上多选/拖放的顺序没有任何判据盯着，
    /// 很容易被"顺手加个 OrderBy 看起来更整齐"改回去。
    /// 这里故意用**与字母序相反**的文件名走一次真导入，并核对片段顺序 = 传入顺序；
    /// 同时核对拖进**文件夹**时确实按名字排（文件夹本身没有用户排好的顺序）。
    /// </summary>
    private static void ImportKeepsGivenOrderAndSortsFolders()
    {
        RunOnStaThread(() =>
        {
            string dir = Path.Combine(Path.GetTempPath(), "ls-order-" + Guid.NewGuid().ToString("N")[..6]);
            string folder = Path.Combine(dir, "shots");
            Directory.CreateDirectory(folder);
            try
            {
                // 一批：z、m、a 的顺序（字母序会是 a、m、z）
                var picked = new[] { "z_last.png", "m_mid.png", "a_first.png" };
                var pickedPaths = new List<string>();
                for (int i = 0; i < picked.Length; i++)
                {
                    string file = Path.Combine(dir, picked[i]);
                    File.WriteAllBytes(file,
                        Core.Imaging.Png.PngEncoder.EncodeToBytes(Fixture.OpaqueLike(40, 90, seed: 200 + i)));
                    pickedPaths.Add(file);
                }

                using (var form = new MainForm())
                {
                    form.CreateControl();
                    form.ProjectForTest.Doc.Mode = StitchMode.LongScroll;   // 夹具指定长截图：文档默认已是 Unset，自动分析只对 LongScroll 起（不写就到不了分析通路）
                    form.ImportPaths(pickedPaths);
                    var names = form.ProjectForTest.Doc.Segments
                        .Select(s => form.ProjectForTest.Doc.FindSource(s.SourceId)!.FileName)
                        .ToArray();
                    Battery.AssertTextEqual(string.Join(",", picked), string.Join(",", names),
                        "逐个选中的文件必须严格按传入顺序排列");
                }

                // 另一批：整个文件夹，内部按名字排
                foreach (var (name, seed) in new[] { ("b.png", 301), ("a.png", 302), ("c.png", 303) })
                    File.WriteAllBytes(Path.Combine(folder, name),
                        Core.Imaging.Png.PngEncoder.EncodeToBytes(Fixture.OpaqueLike(40, 90, seed)));

                using (var form2 = new MainForm())
                {
                    form2.CreateControl();
                    form2.ProjectForTest.Doc.Mode = StitchMode.LongScroll;   // 夹具指定长截图：文档默认已是 Unset，自动分析只对 LongScroll 起（不写就到不了分析通路）
                    form2.ImportPaths(new[] { folder });
                    var names = form2.ProjectForTest.Doc.Segments
                        .Select(s => form2.ProjectForTest.Doc.FindSource(s.SourceId)!.FileName)
                        .ToArray();
                    Battery.AssertTextEqual("a.png,b.png,c.png", string.Join(",", names),
                        "文件夹展开时按名字排，结果才是确定的");
                }

                Battery.Evidence("多选顺序 z,m,a 原样保留；文件夹 a,b,c 按名字排");
            }
            finally { TryDelete(dir); }
        });
    }

    /// <summary>
    /// 审查问题 #9：缓存命中时 <c>Touch</c> 曾什么都不做（只读了一下），
    /// 于是长图来回滚动会把"其实正在用"的图反复淘汰、反复重解码。
    /// 判据把缓存限成两张图的大小，走一条能区分两种行为的路径：
    /// A → B → 再看一次 A（命中）→ 载入 C。命中后 A 该排到队头，被淘汰的必须是 B；
    /// 旧写法下 A 仍是队尾，被淘汰的是 A。
    /// </summary>
    private static void LruHitMovesEntryToHead()
    {
        RunOnStaThread(() =>
        {
            string dir = Path.Combine(Path.GetTempPath(), "ls-lru-" + Guid.NewGuid().ToString("N")[..6]);
            Directory.CreateDirectory(dir);
            try
            {
                const int w = 60, h = 120;
                long one = w * h * 4L;
                var paths = new List<string>();
                foreach (char name in new[] { 'a', 'b', 'c' })
                {
                    string file = Path.Combine(dir, name + ".png");
                    File.WriteAllBytes(file,
                        Core.Imaging.Png.PngEncoder.EncodeToBytes(Fixture.OpaqueLike(w, h, seed: 400 + name)));
                    paths.Add(file);
                }

                var resolver = new FileSourceResolver { CacheLimitBytes = one * 2 };
                var project = new Project(new ProjectDocument { Mode = StitchMode.Vertical });
                var ids = new List<string>();
                for (int i = 0; i < paths.Count; i++)
                {
                    var src = new SourceImage
                    {
                        Id = Project.NewId("src"), Path = paths[i],
                        FileName = Path.GetFileName(paths[i]), Size = new IntSize(w, h), Format = "png",
                    };
                    resolver.Register(src);
                    project.Doc.Sources.Add(src);
                    project.AppendSegmentForSource(src);
                    ids.Add(src.Id);
                }

                // 起点核对：只装两张的缓存，先放 A、再放 B
                resolver.Resolve(ids[0]);
                resolver.Resolve(ids[1]);
                Battery.AssertEqual(one * 2, resolver.CachedBytes, "两张图应刚好占满缓存上限");

                resolver.Resolve(ids[0]);              // 命中 A —— 这一眼必须把 A 挪回队头
                resolver.Resolve(ids[2]);              // 装 C，挤掉一个

                var headFirst = resolver.CachedIdsNewestFirst.ToArray();
                Battery.AssertEqual(2, headFirst.Length, $"缓存里应仍只有两张，实际 {headFirst.Length} 张");
                Battery.AssertEqual(ids[2], headFirst[0], "刚载入的 C 应在队头");
                Battery.Assert(Array.IndexOf(headFirst, ids[0]) >= 0,
                    "A 刚被看过，不该被淘汰（旧写法在这里就把正在用的图踢了）");
                Battery.Assert(!headFirst.Contains(ids[1]),
                    "被淘汰的必须是最久未看的 B");
                Battery.Evidence($"命中后再装入第三张，驻留顺序 = {string.Join("→", headFirst.Select(id => id[^1..]))}（A 活着、B 被换出）");
            }
            finally { TryDelete(dir); }
        });
    }

    private static void TryDelete(string dir)
    {
        try { Directory.Delete(dir, true); } catch { /* 临时目录留给系统回收 */ }
    }

    /// <summary>
    /// WinForms 控件必须在 STA 线程上创建；Checks 的 Main 默认是 MTA 线程池线程，
    /// 所以这里自己起一个 STA 线程跑，跑完拿回异常。
    /// </summary>
    /// <summary>
    /// 在 STA 线程上跑一段 WinForms 代码。
    /// 内部可见是给 <c>CanvasInteractionTests</c> 复用的——两组装具需要的运行时环境完全一样，
    /// 复制一份"60 秒超时 + 异常回传"的线程壳只会多一处可能不一致的地方。
    /// </summary>
    internal static void RunOnStaThread(Action body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { body(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(60));

        if (!thread.IsAlive && failure is null) return;
        if (thread.IsAlive)
        {
            thread.Interrupt();
            throw new TimeoutException("UI 冒烟超过 60 秒未结束，可能卡在消息循环里");
        }
        throw failure!;
    }

    /// <summary>
    /// 把一枚窗体<b>搬到屏外、全透明、不进任务栏</b>，然后真 Show 一次。
    ///
    /// ═══ 为什么非 Show 不可（两格判据第一趟都红在这里）═══
    /// <c>Control.Visible</c> 交出的是<b>有效可见性</b>：自己那一位 ∧ 所有祖先都可见。
    /// 窗体从没 Show 过时它自己是不可见的，于是整棵子树的 <c>Visible</c> 一律是 <c>false</c>——
    /// 按"只量可见叶子"去枚举就一枚也枚举不到，`ui/导出对话框任何控件不许互相重叠也不许出界`
    /// 与 `ui/设置对话框任何控件不许互相重叠也不许出界` 第一趟都红成"叶子 0 枚"。
    /// 红得对：那两格自带"叶子数下限"的夹具自证，0 枚时"没有重叠"本来就是空话。
    ///
    /// ═══ 为什么不改成"不看 Visible、全都量" ═══
    /// 那样会把<b>故意隐藏</b>的控件也算进来（导出对话框的进度条与取消按钮、外观那一块的说明行），
    /// 它们本来就不画，跟谁重叠都不构成缺陷——判据会红在产品正确的地方。
    /// 要分清"自己那一位是 false"与"因为窗体没 Show 才 false"，只有让窗体真可见这一条路。
    ///
    /// ═══ 为什么搬到屏外 ═══
    /// 电池是控制台工装，不该在用户屏上闪出窗口。-32000 是 WinForms 自己放"最小化窗体"的那个坐标，
    /// 加上 <c>Opacity = 0</c> 与 <c>ShowInTaskbar = false</c>，用户看不见也点不到。
    /// 布局照旧真算（句柄已建、<c>RectangleToScreen</c> 有值），量到的就是用户会看到的那套几何。
    /// 用完调 <see cref="HideOffscreen"/> 收掉，别把它留在 <c>Application.OpenForms</c> 里
    /// ——换肤那条 <c>ApplyEverywhere</c> 会遍历 OpenForms，留着就会多翻一棵无关的树。
    /// </summary>
    internal static void ShowOffscreen(Form form)
    {
        form.StartPosition = FormStartPosition.Manual;
        form.ShowInTaskbar = false;
        form.Opacity = 0;
        form.Location = new Point(-32000, -32000);
        form.Show();
        form.PerformLayout();
        // ── 等布局落定，否则量到的是"半路上"的位置 ──
        // 第一枚 RectangleToScreen 会逼整棵树建句柄，建句柄又触发一次布局（字体这时才真生效、
        // 行高按字体重算）。本批实测：不等这一下，同一行里文本框与按钮的行内坐标是
        // 492/492（正好相接、不重叠），而屏幕坐标却差出 2 像素，判据报成"重叠 2×22"——
        // 红在夹具量得太早，读出来却像产品又重叠了。泵一次消息再走一遍布局，两趟读数才一致。
        Application.DoEvents();
        form.PerformLayout();
    }

    /// <summary>把 <see cref="ShowOffscreen"/> 显示出来的窗体收掉（与它成对用，放在 finally 里）。</summary>
    internal static void HideOffscreen(Form form)
    {
        if (!form.IsDisposed) form.Hide();
    }

    private static void ConstructAndLayout()
    {
        RunOnStaThread(() =>
        {
            using var form = new MainForm();
            form.Size = new Size(1440, 900);
            form.PerformLayout();

            // Form 没显示时 CreateControl() 不会真的建句柄，所以不能拿 IsHandleCreated 当判据。
            // 这里改量"布局结果本身"——特别是停靠顺序：菜单必须在最上沿（Top==0），
            // 否则就是"画布把菜单盖住"那种一上线就看见的缺陷。
            Battery.Assert(form.Controls.Cast<Control>().Any(c => c.Dock == DockStyle.Left) &&
                           form.Controls.Cast<Control>().Any(c => c.Dock == DockStyle.Right),
                "素材栏和参数栏必须同时存在");

            Control? menu = form.Controls.Cast<Control>().FirstOrDefault(c => c is MenuStrip);
            Battery.Assert(menu is not null, "应存在 MenuStrip");
            Battery.Assert(menu!.Bounds.Top == 0,
                $"菜单应贴在最上沿，实际 Top={menu.Bounds.Top}（停靠顺序写反的典型症状）");
            Battery.Assert(menu.Bounds.Height > 12, $"菜单高度应可见，实际 {menu.Bounds.Height}");

            Control? status = form.Controls.Cast<Control>().FirstOrDefault(c => c is StatusStrip);
            Battery.Assert(status is not null, "应存在 StatusStrip");
            Battery.Assert(status!.Bounds.Bottom >= form.ClientSize.Height - 4,
                $"状态栏应贴底，实际 Bottom={status.Bounds.Bottom} / 客户区高 {form.ClientSize.Height}");

            Control? center = form.Controls.Cast<Control>()
                .FirstOrDefault(c => c.Dock == DockStyle.Fill);
            Battery.Assert(center is not null && center.Width > 400 && center.Height > 300,
                $"中央画布应填满剩余空间，实际 {center?.Width}×{center?.Height}");

            ToolStrip tools = center!.Controls.OfType<ToolStrip>().Single();
            StitchCanvasControl canvas = center.Controls.OfType<StitchCanvasControl>().Single();
            Battery.Assert(tools.Dock == DockStyle.Top && tools.Bounds.Bottom <= canvas.Bounds.Top,
                $"工具条必须在中央画布上方且不遮挡内容：工具条 {tools.Bounds}，画布 {canvas.Bounds}");
            Battery.Assert(tools.Bounds.Left >= center.Padding.Left &&
                           tools.Bounds.Right <= center.ClientSize.Width - center.Padding.Right,
                "画布工具条必须收在中央工作区内，不横跨两侧面板");
            foreach (string text in new[] { "适应宽度", "适应窗口", "100%", "－", "＋", "对比", "差异" })
                Battery.Assert(tools.Items.Cast<ToolStripItem>().Any(item => item.Text == text),
                    $"新布局仍须保留画布操作入口：{text}");
            Battery.Evidence($"布局结果：菜单 Top=0、状态栏 Bottom={status.Bounds.Bottom}、" +
                             $"中央 {center.Width}×{center.Height}，内部工具条 Bottom={tools.Bottom} ≤ 画布 Top={canvas.Top}");
        });
    }

    private static void ImportThenRefresh()
    {
        RunOnStaThread(() =>
        {
            // 造两张真实的临时 PNG，走完整导入路径（解码 → 登记 → 建片段 → 重算布局 → 刷新面板）
            string dir = Path.Combine(Path.GetTempPath(), "ls-ui-" + Guid.NewGuid().ToString("N")[..6]);
            Directory.CreateDirectory(dir);
            try
            {
                var paths = new List<string>();
                for (int i = 0; i < 3; i++)
                {
                    var img = Fixture.OpaqueLike(60, 120, seed: 100 + i);
                    string file = Path.Combine(dir, $"s{i}.png");
                    File.WriteAllBytes(file, Core.Imaging.Png.PngEncoder.EncodeToBytes(img));
                    paths.Add(file);
                }

                using var form = new MainForm();
                form.CreateControl();
                form.ProjectForTest.Doc.Mode = StitchMode.LongScroll;   // 夹具指定长截图：文档默认已是 Unset，自动分析只对 LongScroll 起（不写就到不了分析通路）
                form.ImportPaths(paths.ToArray());
                form.PerformLayout();

                Battery.Assert(true, "导入不应抛异常");
                Battery.Evidence($"导入 {paths.Count} 张临时 PNG 后，主窗口刷新未抛异常");
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { /* 留给系统回收 */ }
            }
        });
    }

    private static void SwitchAllToolPanels()
    {
        RunOnStaThread(() =>
        {
            var project = new Project(new ProjectDocument { Mode = StitchMode.LongScroll });
            using var form = new MainForm();
            form.CreateControl();

            // 工具面板的整块重建是最容易留悬空引用的地方：
            // 旧控件被 Dispose 后，任何还在事件里的回调都会炸
            foreach (ToolKind tool in Enum.GetValues<ToolKind>())
            {
                form.SwitchToolPanelForTest(tool);
                form.PerformLayout();
            }
            foreach (ToolKind tool in Enum.GetValues<ToolKind>())
            {
                form.SwitchToolPanelForTest(tool);
            }
            Battery.Assert(project.Doc.Segments.Count == 0, "冒烟不该改动任何参数");
            Battery.Evidence($"五个工具面板来回切换两轮：{string.Join("→", Enum.GetNames<ToolKind>())}");
        });
    }

    private static void ExportDialogBuilds()
    {
        RunOnStaThread(() =>
        {
            string dir = Path.Combine(Path.GetTempPath(), "ls-ui-exp-" + Guid.NewGuid().ToString("N")[..6]);
            Directory.CreateDirectory(dir);
            try
            {
                string file = Path.Combine(dir, "one.png");
                File.WriteAllBytes(file,
                    Core.Imaging.Png.PngEncoder.EncodeToBytes(Fixture.OpaqueLike(48, 96)));

                using var form = new MainForm();
                form.CreateControl();
                form.ProjectForTest.Doc.Mode = StitchMode.LongScroll;   // 夹具指定长截图：文档默认已是 Unset，自动分析只对 LongScroll 起（不写就到不了分析通路）
                form.ImportPaths(new[] { file });
                form.OpenExportDialogForTest(dir);
                Battery.Evidence("导出对话框构造完成并算出了计划（未点导出，不落盘）");
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        });
    }

    /// <summary>
    /// 用户第四十七改："你这个导出界面做的很烂啊，很多元素都重叠了，请你重做导出界面。"
    /// 旧版的横坐标是按 96 DPI 下某一种字体宽度写死的列（90／270／380／430……），
    /// 真机 150% DPI 时点字号渲染成 1.5 倍像素宽，标签尾就被右边的控件盖住——截图里几乎每行都压。
    /// 重做后所有横坐标由 TextRenderer.MeasureText 现量、纵向由自动容器叠行，
    /// 所以判据直接量<b>几何</b>：把对话框<b>搬到屏外真 Show 一次</b>（见 <see cref="ShowOffscreen"/>：
    /// 窗体不可见时整棵子树的 <c>Visible</c> 一律是 false，按"只量可见叶子"枚举会一枚也枚举不到），
    /// 递归取所有"叶子控件"的屏幕矩形，两两不许有正面积交集、且都必须落在窗体客户区内。
    /// 夹具自证：叶子数必须 ≥ 15——枚举空了的话"没有重叠"就是空话（本批第一趟就是 0 枚红的）。
    /// 隐藏的进度条／取消按钮不参与（它们不画）；导出进行中那一态由行内预留位保证，不另量。
    /// </summary>
    private static void ExportDialogControlsNeverOverlap()
    {
        RunOnStaThread(() =>
        {
            (Project p, MemoryResolver r, _) = TestProject.Create(StitchMode.LongScroll,
                Fixture.OpaqueLike(60, 120, seed: 81), Fixture.OpaqueLike(60, 120, seed: 82));
            using var dialog = new ExportDialog(p, new CanvasComposer(r), new EncoderRegistry());
            ShowOffscreen(dialog);

            var leaves = new List<(string Name, Rectangle Rect)>();
            void Collect(Control root)
            {
                foreach (Control c in root.Controls)
                {
                    if (!c.Visible || c.Width <= 0 || c.Height <= 0) continue;
                    if (c.Controls.Count == 0)
                        // 名字带上类型：只写 Text 的话两枚 TextBox 都叫"TextBox"，红出来分不清是哪一行
                        leaves.Add(($"{c.GetType().Name}「{c.Text}」",
                                    c.RectangleToScreen(new Rectangle(Point.Empty, c.Size))));
                    else
                        Collect(c);
                }
            }
            Collect(dialog);
            Battery.Assert(leaves.Count >= 15,
                $"夹具自证：导出对话框应量到至少 15 枚叶子控件（实际 {leaves.Count}）——枚举空了「没有重叠」就是空话");

            Rectangle client = dialog.RectangleToScreen(new Rectangle(Point.Empty, dialog.ClientSize));
            var overlaps = new List<string>();
            for (int i = 0; i < leaves.Count; i++)
                for (int j = i + 1; j < leaves.Count; j++)
                {
                    Rectangle hit = Rectangle.Intersect(leaves[i].Rect, leaves[j].Rect);
                    if (hit.Width > 0 && hit.Height > 0)
                        overlaps.Add($"「{leaves[i].Name}」{leaves[i].Rect} × 「{leaves[j].Name}」{leaves[j].Rect} " +
                                     $"重叠 {hit.Width}×{hit.Height}");
                }
            Battery.AssertEqual("", string.Join("；", overlaps),
                "导出对话框任何两枚控件不许有正面积交集（用户报的" +
                "「很多元素都重叠」就是标签尾被右边控件盖住）");

            var outside = leaves.Where(l => l.Rect.Left < client.Left || l.Rect.Top < client.Top ||
                                            l.Rect.Right > client.Right || l.Rect.Bottom > client.Bottom)
                                .Select(l => l.Name).ToList();
            Battery.AssertEqual("", string.Join("；", outside),
                "导出对话框任何控件不许画出窗体客户区（旧版右列数值框正好压在 560 宽的边上）");
            Battery.Evidence($"离屏量到 {leaves.Count} 枚叶子控件：两两无正面积交集、全部在客户区内；" +
                             $"窗体客户区 {dialog.ClientSize.Width}×{dialog.ClientSize.Height}");
            HideOffscreen(dialog);
        });
    }

    /// <summary>
    /// 审查问题 #1：SetDeviceSize 曾经没有任何调用点，_deviceSize 恒为 0×0，
    /// VisibleCanvas 恒为空、FitWhole 失效、瓦片不画。修复后控件尺寸变化必须同步进视口。
    /// </summary>
    private static void ViewportTracksClientSize()
    {
        RunOnStaThread(() =>
        {
            (Project p, MemoryResolver r, _) = TestProject.Create(StitchMode.LongScroll,
                Fixture.OpaqueLike(60, 120, seed: 11), Fixture.OpaqueLike(60, 120, seed: 12));
            using var canvas = new StitchCanvasControl(p, new CanvasComposer(r));

            canvas.Size = new Size(400, 300);
            Battery.Assert(canvas.Viewport.DeviceSize.Width > 0 && canvas.Viewport.DeviceSize.Height > 0,
                $"控件给了尺寸后视口必须跟上，实际 {canvas.Viewport.DeviceSize}");
            Battery.AssertEqual(canvas.ClientSize, canvas.Viewport.DeviceSize, "视口尺寸应等于客户区");
            Battery.Assert(!canvas.Viewport.VisibleCanvas().IsEmpty, "可见区域不该再是空的");

            // 再改一次尺寸：视口必须跟着变（OnResize 接线，不是一次性的）
            canvas.Size = new Size(640, 480);
            Battery.AssertEqual(canvas.ClientSize, canvas.Viewport.DeviceSize, "改尺寸后视口应再次同步");
            Battery.Evidence($"400×300 → 640×480 两次同步成功，VisibleCanvas 非空");
        });
    }

    /// <summary>
    /// 审查问题 #2：命中测试曾自己重算坐标、漏减 Viewport.TopLeft，
    /// 平移后蓝线的视觉位置与可点位置错开。修复后二者共用同一个换算，
    /// 判据 = "视觉位置能点中，且旧的错误位置点不中"。
    /// </summary>
    private static void SeamHitTestFollowsPan()
    {
        RunOnStaThread(() =>
        {
            (Project p, MemoryResolver r, _) = TestProject.Create(StitchMode.LongScroll,
                Fixture.OpaqueLike(60, 120, seed: 21), Fixture.OpaqueLike(60, 120, seed: 22));
            p.Mutate("设定重叠", _ => p.Doc.Seams[0].ManualOffset = new IntPoint(0, 90));
            using var canvas = new StitchCanvasControl(p, new CanvasComposer(r));
            canvas.Size = new Size(400, 300);
            // 以 (0,0) 为锚切到 100%：TopLeft 保持在原点，后面的平移量才好断言
            canvas.Viewport.SetOneToOne(new Point(0, 0));

            PlacedSegment placed = p.Layout.Placed[1];
            string seamId = placed.IncomingSeam!.Id;
            int seamCanvasY = placed.Origin.Y;

            // 未平移：视觉位置 = 画布位置
            Battery.AssertEqual(seamId, canvas.HitTestSeam(new Point(30, seamCanvasY)), "未平移时应在画布坐标处点中");

            // 向下平移视口 40 源像素（zoom=1 → 40 设备像素）
            canvas.Viewport.PanByDevice(new IntPoint(0, -40));
            Battery.AssertEqual(40, canvas.Viewport.TopLeft.Y, "平移后视口原点");

            int visualY = seamCanvasY - canvas.Viewport.TopLeft.Y;   // 绘制用的同一换算
            Battery.AssertEqual(seamId, canvas.HitTestSeam(new Point(30, visualY)),
                "平移后必须在**视觉位置**点中拼缝");
            Battery.Assert(canvas.HitTestSeam(new Point(30, seamCanvasY)) != seamId,
                $"旧缺陷位置（画布坐标 {seamCanvasY} 当设备坐标）必须点不中——差 {canvas.Viewport.TopLeft.Y}px 远超 9px 容差");
            Battery.Evidence($"拼缝画布 y={seamCanvasY}，视口下移 40 后视觉 y={visualY}：视觉处点中、旧位置点不中");
        });
    }

    /// <summary>
    /// 审查问题 #5："确认拼缝"曾直接改对象 + Touch()——不进撤销栈、不置 IsDirty，
    /// 保存会漏掉这次确认。修复后走 Mutate，判据 = 确认→脏→可撤销→退回。
    /// </summary>
    private static void ConfirmSeamEntersUndoAndDirty()
    {
        RunOnStaThread(() =>
        {
            (Project p, MemoryResolver r, _) = TestProject.Create(StitchMode.LongScroll,
                Fixture.OpaqueLike(60, 120, seed: 31), Fixture.OpaqueLike(60, 120, seed: 32));
            string seamId = p.Doc.Seams[0].Id;
            var resolver = new FileSourceResolver();
            using var panel = new ToolPanel(p, resolver, new CanvasComposer(r));
            panel.SeamId = seamId;

            Button confirm = panel.Controls.OfType<Button>().First(b => b.Text == "确认拼缝");
            p.MarkSaved();   // 建项目时 EnsureSeams 已置过脏，先复位，才能量"确认"这一下
            Battery.Assert(!p.IsDirty, "还没操作就不该是脏的");
            confirm.PerformClick();

            Battery.Assert(p.Doc.Seams.First(s => s.Id == seamId).Confirmed, "点击后拼缝应为已确认");
            Battery.Assert(p.IsDirty, "确认必须置 IsDirty，否则保存会漏掉这次改变");
            Battery.Assert(p.CanUndo, "确认必须进撤销栈");
            Battery.AssertEqual("确认拼缝", p.NextUndoLabel, "撤销标签");

            p.Undo();
            Battery.Assert(!p.Doc.Seams.First(s => s.Id == seamId).Confirmed, "Ctrl+Z 应退回未确认状态");
            Battery.Evidence("确认拼缝：置脏 + 入栈（标签「确认拼缝」）+ 一次 Undo 完整退回");
        });
    }

    /// <summary>
    /// 审查第三轮 #5："在画布上点选位置切分"从提示文字变成真功能。
    /// 量的是这条换算链：设备点 →（+TopLeft、÷Zoom）→ 画布 Y，且点完自动退出模式。
    /// 平移/缩放后的点选必须落在用户看见的那一行，这是本功能全部的成败点。
    /// </summary>
    private static void SplitPickTranslatesToDeviceCanvasY()
    {
        RunOnStaThread(() =>
        {
            (Project p, MemoryResolver r, _) = TestProject.Create(StitchMode.LongScroll,
                Fixture.OpaqueLike(60, 120, seed: 41), Fixture.OpaqueLike(60, 120, seed: 42));
            using var canvas = new StitchCanvasControl(p, new CanvasComposer(r));
            canvas.Size = new Size(400, 300);
            canvas.Viewport.SetOneToOne(new Point(0, 0));

            canvas.EnterSplitPick();
            Battery.Assert(canvas.SplitPickActive, "进入点选模式后应为激活");

            int? reported = null;
            canvas.SplitPicked += y => reported = y;

            // 向下平移 40 源像素再点设备 (30,100) → 画布 Y 应为 140，不是 100
            canvas.Viewport.PanByDevice(new IntPoint(0, -40));
            canvas.HandleSplitPickClick(new Point(30, 100));

            Battery.AssertEqual(140, reported ?? -1, "点选必须带上升降平移量");
            Battery.Assert(!canvas.SplitPickActive, "点过一次就退出模式，不许残留");

            // 取消路径：进入后 CancelSplitPick → 模式退、不回报
            canvas.EnterSplitPick();
            canvas.CancelSplitPick();
            Battery.Assert(!canvas.SplitPickActive, "CancelSplitPick 应退出模式");
            Battery.Evidence("点选切分：平移 40 后点 (30,100) → 画布 Y=140；点完/取消后模式即退");
        });
    }

    /// <summary>
    /// 第七轮：长截图的横向锁定必须在界面上看得见、说得清，而不只是"图里没错"。
    /// 三件事一起量：X 那一格禁用、移动方向显示生效档位（但不改用户存的参数）、
    /// 以及绕过界面硬塞 X 时 Core 拦下 + 面板出声。
    /// 最后这条是重点：<c>Enabled=false</c> 只挡鼠标，不挡任何直接写控件的代码，
    /// 所以真正的锁必须在参数层，界面只是提前把路标出来。
    /// </summary>
    private static void LongScrollPanelLocksHorizontal()
    {
        RunOnStaThread(() =>
        {
            (MainForm form, string dir) = FormWithImports(count: 2, width: 60, height: 120);
            try
            {
                Project p = form.ProjectForTest;
                Battery.AssertEqual(StitchMode.LongScroll, p.Doc.Mode, "冒烟项目应为长截图");
                string seamId = p.Doc.Seams[0].Id;
                form.ClickToolButtonForTest(0);
                form.CanvasForTest.SelectSeam(seamId);
                ToolPanel panel = form.ToolPanelForTest;

                Battery.Assert(!panel.SeamXRowEnabledForTest, "长截图里 X 那一格必须禁用");
                Battery.AssertEqual("仅上下", panel.SeamConstraintTextForTest,
                    "移动方向要显示**生效**的档位");
                Battery.AssertEqual(MoveConstraint.Both, p.Doc.Seams[0].Constraint,
                    "面板显示「仅上下」不许顺手改写用户存的档位（切回竖拼要能恢复）");

                // 硬塞一个横向值：禁用挡的是鼠标，挡不住绕过界面的写入，所以参数层必须自己拦
                FindIntRow(panel, "X").Value = 9;
                Seam seam = p.Doc.Seams[0];
                Battery.AssertEqual(0, seam.ManualOffset?.X ?? 0, "就算真把 9 塞进去，落库的 X 仍必须是 0");
                Battery.Assert(panel.LastNoticeForTest is not null &&
                               panel.LastNoticeForTest.Contains("已锁定左右方向", StringComparison.Ordinal),
                    $"被锁掉必须出声，否则用户不知道自己那一下白点了。实际：{panel.LastNoticeForTest}");

                // 纵向照旧可填，而且填 Y 时不许把 X 一起带回去。
                // 第九轮：长截图里这个 Y 框改的不再是落位位移，而是"这条缝去掉多少行"
                // （紧接 120 − 输入 100 = 重叠 20 ⇒ 两侧裁切量按默认档摆成 (20, 0)）。
                FindIntRow(panel, "Y").Value = 100;
                seam = p.Doc.Seams[0];
                Battery.AssertEqual(20, seam.OverlapLength, "填 Y=100 落到的是重叠 20 行（紧接位移 120 − 100）");
                Battery.AssertEqual(20, seam.BeforeCropBottom, "上一张底部被裁掉这 20 行");
                Battery.AssertEqual(0, seam.AfterCropTop, "默认档：下一张顶部不裁");
                Battery.Assert(seam.ManualOffset is null,
                    "长截图里填 Y 不许写 ManualOffset（裁切不是移动图片，需求一/需求五）");
                Battery.AssertEqual(0, seam.ManualOffset?.X ?? 0, "填 Y 时 X 仍是 0");
                Battery.AssertEqual(0, p.Layout.Placed[1].Origin.X, "画布上的像素也没跟着挪");
                Battery.AssertEqual(100, p.Layout.Placed[1].Origin.Y, "后一段原点 = 前一段裁短之后的可见底边");
                Battery.AssertEqual(220, p.Layout.CanvasSize.Height, "总高 = 120 + 120 − 20");
                Battery.Evidence($"X 格禁用 / 档位显示仅上下（参数仍 {MoveConstraint.Both}）/ " +
                                 $"硬填 X=9 被拦且回话一句");
            }
            finally { form.Dispose(); TryDelete(dir); }
        });
    }

    /// <summary>
    /// 方向键与拖动折算都不能改动横向，纵向照旧；<b>竖拼跑同一批动作必须全部生效</b>——
    /// 没有这半边对照，"横向一律不动"可能只是把折算写死了。
    ///
    /// 第十四轮的口径分档：长截图里 ↑↓ 走的是<b>滑动那张图</b>（重叠与蓝线一起动，画布行号守恒），
    /// 不再写 ManualOffset；位移折算那一道闸仍按原样量（它是轴规则的唯一出口）。
    /// 滑动那一档的完整判据在 <c>crop/方向键一次滑动一行：蓝线画布行号不变、每次按键一条撤销</c>。
    /// </summary>
    private static void LongScrollArrowsAndDragCannotMoveHorizontally()
    {
        RunOnStaThread(() =>
        {
            (Project p, MemoryResolver r, _) = TestProject.Create(StitchMode.LongScroll,
                Fixture.OpaqueLike(60, 120, seed: 91), Fixture.OpaqueLike(60, 120, seed: 92));
            using var canvas = new StitchCanvasControl(p, new CanvasComposer(r));
            Seam seam = p.Doc.Seams[0];
            canvas.SelectSeam(seam.Id);
            // 先给这条缝 30 行重叠，滑动才有可走的格子（O=0 时带不存在）
            p.Mutate("给 30 行重叠", _ => seam.SetOverlap(30));
            // 再把蓝线摆进带里：默认 Cut=0 时"抓下方那张往下滑"一步都滑不动
            //（滑动量被 min(O,Cut) 夹成 0），那一趟只会报"滑到头"，
            // 下面那句"纵向按键应记一条改动"就会红在工装没造出可动的空间上。
            p.Mutate("预设蓝线", _ => seam.ApplyCut(10));
            int heightBefore = p.Layout.CanvasSize.Height;

            int notices = 0;
            canvas.HorizontalShiftRejected += _ => notices++;
            int revision = p.Revision;

            canvas.NudgeSelected(new IntPoint(-1, 0));      // ←
            Battery.AssertEqual(revision, p.Revision, "长截图里按 ← 不许改动参数");
            Battery.AssertEqual(1, notices, "按了没反应必须回话一次，不然用户以为卡住了");
            Battery.Assert(seam.ManualOffset is null, "纯横向按键不该留下手动值");
            Battery.AssertEqual(10, seam.CutPosition, "← 也不许改动蓝线");

            canvas.NudgeSelected(new IntPoint(0, 3));       // ↓ 抓下方那张往下滑 3 个源像素
            Battery.AssertEqual(revision + 1, p.Revision, "纵向按键应记一条改动");
            Battery.AssertEqual(7, seam.CutPosition, "↓ 一次：带内行号走 3 行（10→7）");
            Battery.AssertEqual(27, seam.OverlapLength,
                "重叠同减 3 行：两个量一起对消，蓝线的画布行号才守恒（完整判据在 `crop/` 那一组）");
            Battery.Assert(seam.ManualOffset is null,
                "裁切档里 ↑↓ 不该写 ManualOffset（需求一：滑动图片不是移动整个 Segment）");
            Battery.AssertEqual(heightBefore + 3, p.Layout.CanvasSize.Height,
                "带被压掉 3 行 ⇒ 总高多 3 行（旧口径那句「总高不变」属于「线动图不动」那一套，已作废）");
            Battery.AssertEqual(0, p.Layout.Placed[1].Origin.X, "X 恒为 0");

            // 拖动折算走的就是鼠标路径上那个函数，这里直接量它
            Battery.AssertEqual(new IntPoint(0, 123), canvas.ConstrainDrag(seam, new IntPoint(45, 123)),
                "位移折算里长截图必须抹掉横向，进不到预览布局里");
            Battery.AssertEqual(new IntPoint(0, 3), canvas.ConstrainCutDelta(seam, new IntPoint(45, 3)),
                "裁切折算同样只留纵向：横向连换算都不参与");

            // 用户选了「仅左右」也绕不过去：长截图里没有可动的横向轴，折算成仅上下
            p.Mutate("选仅左右", _ => seam.Constraint = MoveConstraint.HorizontalOnly);
            Battery.AssertEqual(new IntPoint(0, 123), canvas.ConstrainDrag(seam, new IntPoint(45, 123)),
                "「仅左右」在长截图里必须折算成「仅上下」");

            (Project q, MemoryResolver qr, _) = TestProject.Create(StitchMode.Vertical,
                Fixture.OpaqueLike(60, 120, seed: 93), Fixture.OpaqueLike(60, 120, seed: 94));
            using var vertical = new StitchCanvasControl(q, new CanvasComposer(qr));
            Seam vSeam = q.Doc.Seams[0];
            vertical.SelectSeam(vSeam.Id);
            Battery.AssertEqual(new IntPoint(45, 123), vertical.ConstrainDrag(vSeam, new IntPoint(45, 123)),
                "竖拼里横向照旧允许——长截图的规则不许外溢");
            int vRevision = q.Revision;
            vertical.NudgeSelected(new IntPoint(-1, 0));
            Battery.AssertEqual(vRevision + 1, q.Revision, "竖拼里按 ← 应真的改动拼缝");
            Battery.AssertEqual(-1, q.Doc.Seams[0].ManualOffset!.Value.X, "竖拼里 X 记下 −1");
            Battery.Evidence("长截图：← 不动但回话 / ↓ 走 3 源像素 / 拖动 X 被抹 / 仅左右折算成仅上下；" +
                             "竖拼同一批动作全部生效");
        });
    }

    /// <summary>
    /// 重影对比（半透明叠另一侧）只是屏幕上的一层画法：
    /// 开与关都不许改参数、不许换布局、更不许改动导出的字节。
    /// 用户在截图里看到的"重复的一截内容"很可能就是这一层，
    /// 所以"它进不了成品"这件事必须有常驻判据，而不是靠人每次自己关掉了再核对。
    /// </summary>
    private static void GhostOverlayChangesNeitherLayoutNorPixels()
    {
        RunOnStaThread(() =>
        {
            (Project p, MemoryResolver r, _) = TestProject.Create(StitchMode.LongScroll,
                Fixture.OpaqueLike(60, 120, seed: 61), Fixture.OpaqueLike(60, 120, seed: 62));
            var composer = new CanvasComposer(r);
            using var canvas = new StitchCanvasControl(p, composer);
            canvas.Size = new Size(400, 300);
            canvas.Viewport.SetOneToOne(new Point(0, 0));
            canvas.SelectSeam(p.Doc.Seams[0].Id);

            int revision = p.Revision;
            CanvasLayout before = p.Layout;
            byte[] pixelsOff = composer.RenderWhole(before).RegionSha256(before.CanvasBounds);

            canvas.ShowCompareOverlay = false;
            canvas.ShowCompareOverlay = true;
            canvas.OverlapOpacityPercent = 15;     // 顺带把透明度也拉到另一端
            canvas.Invalidate();

            Battery.AssertEqual(revision, p.Revision, "开关重影层不许改动项目参数");
            Battery.Assert(ReferenceEquals(before, p.Layout), "开关重影层不许让布局重算（连一版新布局都不该有）");
            Battery.AssertEqual(0, p.Layout.Placed[1].Origin.X, "重叠带的显示方式不影响像素落位");

            byte[] pixelsOn = composer.RenderWhole(p.Layout).RegionSha256(p.Layout.CanvasBounds);
            Battery.AssertBytesEqual(pixelsOff, pixelsOn, "重影层开与关两次的整张合成");
            Battery.Evidence($"重影开/关、透明度 50%→15%：Revision {revision}→{p.Revision}（未变）、" +
                             "布局同一实例、整张合成 sha 一致");
        });
    }

    /// <summary>
    /// 用户第四十六改第 3 条："我点击右侧的确认拼缝，应该就能直接退出编辑，
    /// 而不是我需要再次点击蓝色的勾才能退出，有些麻烦了。"
    /// 确认＝"这条缝我看过了、没问题"，与蓝线上那枚 ✓、Esc 是同一件事的三个出口，
    /// 所以确认之后必须一次退干净。
    ///
    /// 这件事有两段通路，各量各的，谁都不许替谁作证：
    /// <list type="number">
    ///   <item>面板那一段：真按钮真按下去（PerformClick），量"写确认"与"发出 exit-edit 命令"是同一下。</item>
    ///   <item>主窗口那一段：拿到面板发来的 exit-edit，量它真把编辑态退干净（选中清空＋微调档关）。</item>
    /// </list>
    /// 为什么第 1 段不挂在真窗体上量——窗体从没 <c>Show()</c> 过时，面板按钮在 WinForms 的
    /// CanSelect 那一关就被挡下，<c>PerformClick</c> 是<b>空转</b>（本仓库实测过：方法一次都没进，
    /// 已记进红账）。挂在窗体上按等于没量，还会绿得很像样。
    /// </summary>
    private static void ConfirmSeamExitsEditInOneClick()
    {
        RunOnStaThread(() =>
        {
            // ── 第 1 段：面板自己那一下 ──
            (Project p, MemoryResolver r, _) = TestProject.Create(StitchMode.LongScroll,
                Fixture.OpaqueLike(60, 120, seed: 71), Fixture.OpaqueLike(60, 120, seed: 72));
            string seamId = p.Doc.Seams[0].Id;
            var commands = new List<string>();
            using (var panel = new ToolPanel(p, new FileSourceResolver(), new CanvasComposer(r)))
            {
                panel.CanvasCommand += commands.Add;
                panel.SeamId = seamId;

                Button confirm = panel.Controls.OfType<Button>().FirstOrDefault(b => b.Text == "确认拼缝") ??
                                 throw new InvalidOperationException(
                                     "面板上找不到「确认拼缝」按钮——夹具与界面对不上了");
                // 夹具自证：按下去之前这枚按钮必须真的可按。
                // PerformClick 对禁用按钮是"什么都不做"、不报错——不先量这一条，
                // 后面那句红就会被读成"产品没写确认"，其实只是工装按了一枚灰按钮。
                Battery.Assert(confirm.Enabled,
                    $"「确认拼缝」按钮此刻是禁用的（Confirmed={p.Doc.Seams[0].Confirmed}、IsManual={p.Doc.Seams[0].IsManual}）");
                int revisionBefore = p.Revision;
                confirm.PerformClick();

                Battery.Assert(p.Revision > revisionBefore,
                    $"点击之后 Revision 必须前进（{revisionBefore}→{p.Revision}）：没前进说明这一下根本没写进项目");
                Battery.Assert(p.Doc.Seams[0].Confirmed,
                    $"确认拼缝必须真的把状态写成已确认（这是它本职工作，不能为了退编辑丢掉）；实际状态 {p.Doc.Seams[0].Status}");
                Battery.AssertEqual("exit-edit", string.Join("|", commands),
                    "确认这一下必须同时发出 exit-edit 命令——只发一次、不许多发也不许不发");
                Battery.Evidence($"面板：点「确认拼缝」⇒ Confirmed=true、状态 {p.Doc.Seams[0].Status}、" +
                                 $"Revision {revisionBefore}→{p.Revision}、发出命令 [{string.Join("|", commands)}]");
            }

            // ── 第 2 段：主窗口收到这条命令之后 ──
            (MainForm form, string dir) = FormWithImports(count: 2, width: 60, height: 120);
            try
            {
                StitchCanvasControl canvas = form.CanvasForTest;
                string formSeamId = form.ProjectForTest.Doc.Seams[0].Id;
                canvas.SelectSeam(formSeamId);
                canvas.SetAdjustMode(true);
                Battery.AssertEqual(formSeamId, canvas.SelectedSeamId ?? "", "夹具自证：先进编辑态，才有编辑态可退");
                Battery.Assert(canvas.AdjustModeActive, "夹具自证：微调档必须真开着");

                form.ToolPanelForTest.RaiseCanvasCommandForTest("exit-edit");

                Battery.Assert(canvas.SelectedSeamId is null,
                    "主窗口收到 exit-edit 之后必须一次退出编辑（选中清空）——不许再要用户回画布点一次蓝线上的 ✓");
                Battery.Assert(!canvas.AdjustModeActive, "同上：微调档也必须一起退掉");
                Battery.Evidence("主窗口：exit-edit ⇒ 选中清空、微调档关（与 Esc／✓ 同一出口）");
            }
            finally
            {
                form.Dispose();
                TryDelete(dir);
            }
        });
    }

    /// <summary>
    /// 用户第四十六改第 1 条："【文件】这个菜单的字体大小和【编辑】【视图】【帮助】都不一致，过大了。"
    /// 四个顶级项在代码里是同一构造器new出来的，肉眼却一大三小——
    /// 所以判据不猜原因，直接把四枚的字体四元组（族名／字号／单位／字形）量出来互相比：
    /// 只要有一枚不同就红，并把各自实际值写进证据——修的时候照着证据修，不照印象修。
    /// </summary>
    private static void MenuTopLevelFontsAreOneFont()
    {
        RunOnStaThread(() =>
        {
            (MainForm form, string dir) = FormWithImports(count: 2, width: 60, height: 120);
            try
            {
                MenuStrip menu = form.MainMenuStrip ??
                                 throw new InvalidOperationException("主窗口没挂菜单栏，夹具与界面对不上了");
                var items = menu.Items.OfType<ToolStripMenuItem>().ToList();
                Battery.AssertEqual(4, items.Count,
                    $"菜单栏顶级项应是 4 枚（文件／编辑／视图／帮助），实际 {items.Count}");
                Font first = items[0].Font;
                foreach (ToolStripMenuItem item in items)
                {
                    Battery.Assert(
                        item.Font.Name == first.Name &&
                        Math.Abs(item.Font.Size - first.Size) < 0.01f &&
                        item.Font.Unit == first.Unit &&
                        item.Font.Style == first.Style,
                        $"顶级项「{item.Text}」的字体（{item.Font.Name} {item.Font.Size:F2} {item.Font.Unit} {item.Font.Style}）" +
                        $"必须与「{items[0].Text}」（{first.Name} {first.Size:F2} {first.Unit} {first.Style}）完全一致");
                    Battery.Assert(
                        item.Font.Name == Theme.UiFontSmall.Name &&
                        Math.Abs(item.Font.Size - Theme.UiFontSmall.Size) < 0.01f,
                        $"顶级项「{item.Text}」的字体必须钉成 UiFontSmall（{Theme.UiFontSmall.Name} {Theme.UiFontSmall.Size:F2}），" +
                        $"实际 {item.Font.Name} {item.Font.Size:F2}——交给渲染器默认就会出现一大三小");
                }
                Battery.Evidence(string.Join("；",
                    items.Select(i => $"{i.Text}={i.Font.Name} {i.Font.Size:F2}{i.Font.Unit} {i.Font.Style}")));

                // ── 第 2 段：渲染器那一段 ──
                // item.Font 只是"代码里写的那枚字体"，屏上字号是渲染器说了算：
                // ToolStripProfessionalRenderer 在视觉样式可用时会拿系统菜单字体画顶级项，
                // 于是出现"一种代码字体、两种屏上字号"（用户报的【文件】比其余三项大一号就是这一族）。
                // 所以这里把渲染器真调一次：故意递进去一枚 20pt 的错字体，出来时必须已被改回 item.Font。
                // 只能走反射——OnRenderItemText 是 protected，WinForms 没有公开入口让外面触发"画文字"这一步；
                // 工装调的正是 WinForms 自己会调的那一个方法，不是照抄实现。
                ToolStripRenderer renderer = menu.Renderer;
                Battery.Assert(renderer is DarkToolStripRenderer,
                    $"菜单栏挂的渲染器应是本项目的 DarkToolStripRenderer，实际 {renderer.GetType().Name}");
                using (var scratch = new Bitmap(8, 8))
                using (Graphics g = Graphics.FromImage(scratch))
                using (var wrongFont = new Font(Theme.UiFontSmall.Name, 20f))
                {
                    var args = new ToolStripItemTextRenderEventArgs(g, items[0], items[0].Text,
                        new Rectangle(0, 0, 8, 8), Color.White, wrongFont, ContentAlignment.MiddleLeft);
                    System.Reflection.MethodInfo draw = typeof(ToolStripRenderer).GetMethod(
                            "OnRenderItemText",
                            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic) ??
                        throw new InvalidOperationException("ToolStripRenderer 上没有 OnRenderItemText，反射口对不上了");
                    draw.Invoke(renderer, new object[] { args });
                    Font drawn = args.TextFont ??
                                 throw new InvalidOperationException("渲染器把 TextFont 交成了 null");
                    Battery.Assert(drawn.Name == items[0].Font.Name &&
                                   Math.Abs(drawn.Size - items[0].Font.Size) < 0.01f,
                        $"渲染器画顶级菜单项时必须用 item.Font（{items[0].Font.Name} {items[0].Font.Size:F2}），" +
                        $"实际交出去的是 {drawn.Name} {drawn.Size:F2}——留着渲染器默认就会一大三小");
                    Battery.Evidence($"渲染器：递进 {wrongFont.Name} {wrongFont.Size:F2}，交出 {drawn.Name} {drawn.Size:F2}（=item.Font）");
                }
            }
            finally
            {
                form.Dispose();
                TryDelete(dir);
            }
        });
    }

    /// <summary>
    /// 第五十改：顶栏那枚分段控件与文档模式必须<b>两向</b>同源。
    /// 只量一向会放过两种坏形状：① 用户点了分段但文档没切（屏上选中档与实际拼合方向不一致）；
    /// ② 文档模式变了但分段停在旧档（撤销／打开项目之后屏上骗人）。
    /// 这里两向各走一次真实通路：方向一走 <c>PickModeSegmentForTest</c>（与鼠标同一条 Pick），
    /// 方向二走 <c>Project.Mutate</c>（与撤销／打开项目同一条 Changed 通知）。
    /// </summary>
    private static void ModeSegmentTwoWaySync()
    {
        RunOnStaThread(() =>
        {
            (MainForm form, string dir) = FormWithImports(count: 2, width: 60, height: 120);
            try
            {
                // 方向一：用户选档 → 文档模式跟着切
                form.PickModeSegmentForTest(1);
                Battery.AssertEqual(1, form.ModeSegmentIndexForTest, "用户选第 2 档后分段应停在第 2 档");
                Battery.AssertEqual((int)StitchMode.Vertical, (int)form.ProjectForTest.Doc.Mode,
                    "用户选档后文档模式应跟着切到竖拼");

                // 方向二：文档模式变（非用户点击）→ 分段回填到对应档
                form.ProjectForTest.Mutate("判据改模式", d => d.Mode = StitchMode.Horizontal);
                Battery.AssertEqual((int)StitchMode.Horizontal, form.ModeSegmentIndexForTest,
                    "文档模式变了分段应回填到横拼那一档");
                Battery.Evidence($"用户选档→模式 {(int)form.ProjectForTest.Doc.Mode}；改模式→分段 {form.ModeSegmentIndexForTest}");
            }
            finally { form.Dispose(); TryDelete(dir); }
        });
    }

    /// <summary>
    /// 上一格那套两向通路之所以安全，靠的是一条纪律：<b>程序回填静音</b>。
    /// 主窗口在 SelectionChanged 里切模式、切完又回填分段；若回填也发事件，就会绕回重入
    /// （弹两次询问框、甚至死循环）。这一格把纪律钉成可数的出口：
    /// 程序改模式计数不许涨；用户 Pick 一次计数必须正好涨一。
    /// 任何一头坏掉（setter 改成发事件 / Pick 通路断了）这里都会红。
    /// </summary>
    private static void ModeSegmentBackfillIsSilent()
    {
        RunOnStaThread(() =>
        {
            (MainForm form, string dir) = FormWithImports(count: 2, width: 60, height: 120);
            try
            {
                int before = form.ModeSegmentEventCountForTest;
                form.ProjectForTest.Mutate("判据改模式", d => d.Mode = StitchMode.Vertical);
                Battery.AssertEqual(before, form.ModeSegmentEventCountForTest,
                    "程序回填必须静音：计数涨了说明 SelectedIndex 的 setter 被改成发事件，两向同源会绕回重入");
                form.PickModeSegmentForTest(2);
                Battery.AssertEqual(before + 1, form.ModeSegmentEventCountForTest,
                    "用户选择必须发且只发一次：没涨说明 Pick 通路断了，方向一判据会假绿");
                Battery.Evidence($"回填后计数 {before}（不变）；Pick 一次后 {form.ModeSegmentEventCountForTest}（+1）");
            }
            finally { form.Dispose(); TryDelete(dir); }
        });
    }

    /// <summary>
    /// 第六十改回修（用户实测：按钮没灰还能点——硬拦改成"只入列不分析"）：
    /// 添加图片按钮<b>永不变灰</b>，没选模式时导入照常进文档，只是不起自动分析；
    /// 选模式这一刻（走真 Pick 通路）才补上"最后自动分析（如果是长截图）"。
    /// 三段各证一头：①未选 + 导入 → 图全进（Sources/Segments 齐）且<b>没有自动分析</b>——
    /// "没分析"的量法：批量分析写回必触发释放回收钩子（第五十九改③），导入后泵 1.5 秒
    /// 释放计数不前进；②状态栏回话告诉用户"只入列，选模式后自动分析"（不拦但要说清）；
    /// ③Pick 长截图 → 泵 15 秒内释放计数必须前进（自动分析真的起来了）。
    /// 注意"没分析"那半的判据形状：<b>泵了 1.5 秒还不前进</b>，不是"立刻不前进"——
    /// 分析写回是 BeginInvoke 异步的，不泵消息连误启动的分析都等不到写回，那半就量成空气。
    /// </summary>
    private static void ImportWithoutModeThenAnalysisFollowsMode()
    {
        RunOnStaThread(() =>
        {
            string dir = Path.Combine(Path.GetTempPath(), "ls-ui-" + Guid.NewGuid().ToString("N")[..6]);
            List<string> paths = WritePngFiles(dir, count: 2, width: 60, height: 120);
            try
            {
                var form = new MainForm();
                try
                {
                    form.CreateControl();
                    _ = form.Handle;

                    // 前提自证：全新窗体的文档必须是「未选」，分段也必须如实显示三档都没抬起——
                    // 前提不成立（比如有人把默认改回 LongScroll）这格就量不到"只入列"了。
                    Battery.AssertEqual(StitchMode.Unset, form.ProjectForTest.Doc.Mode,
                        "前提自证：新建窗体的文档模式必须是 Unset（默认被改走要同步本判据）");
                    Battery.AssertEqual(-1, form.ModeSegmentIndexForTest,
                        "前提自证：未选模式时分段下标必须是 -1（三档都没抬起）");

                    long releaseBefore = MemorySnapshot.ReleaseCountForTest;
                    form.ImportPaths(paths.ToArray());
                    Battery.AssertEqual(2, form.ProjectForTest.Doc.Sources.Count,
                        "没选拼合模式时添加图片必须照常收下：按钮不变灰、导入不被拦（Sources=2）");
                    Battery.AssertEqual(2, form.ProjectForTest.Doc.Segments.Count,
                        "没选拼合模式时图片照常入列（Segments=2，每张一段）");
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    while (sw.ElapsedMilliseconds < 1_500)
                    {
                        Application.DoEvents();
                        System.Threading.Thread.Sleep(10);
                    }
                    Battery.AssertEqual(releaseBefore, MemorySnapshot.ReleaseCountForTest,
                        "未选模式导入后 1.5 秒内不许起自动分析（写回必触发的释放计数不前进）——" +
                        "涨了说明分析提前跑了，「先加图还是先选模式」就不是用户自己定的");

                    var label = typeof(MainForm).GetField("_statusInfo",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?
                        .GetValue(form) as ToolStripStatusLabel;
                    string statusText = label?.Text ?? "";
                    Battery.Assert(statusText.Contains("拼合模式") && statusText.Contains("只入列"),
                        $"导入后状态栏必须说清「没选模式只入列不分析」（现状：{statusText}）——" +
                        "不拦但不说清，用户会以为分析坏了");

                    // 选长截图（走真 Pick 通路）这一刻，自动分析必须自己起来。
                    form.PickModeSegmentForTest(0);
                    Battery.AssertEqual(StitchMode.LongScroll, form.ProjectForTest.Doc.Mode,
                        "Pick 通路必须把文档模式切到长截图");
                    var sw2 = System.Diagnostics.Stopwatch.StartNew();
                    while (MemorySnapshot.ReleaseCountForTest == releaseBefore && sw2.ElapsedMilliseconds < 15_000)
                    {
                        Application.DoEvents();
                        System.Threading.Thread.Sleep(10);
                    }
                    Battery.Assert(MemorySnapshot.ReleaseCountForTest > releaseBefore,
                        $"选了长截图后必须自动起分析（等了 {sw2.ElapsedMilliseconds}ms）——" +
                        "15 秒没等到就是 SwitchMode 的自动分析接线断了");
                    Battery.Evidence("未选：2 张照常入列、1.5 秒无分析（状态栏说清）→ Pick 长截图 → 分析自动起" +
                                     $"（释放计数 {releaseBefore}→{MemorySnapshot.ReleaseCountForTest}）");
                }
                finally { form.Dispose(); }
            }
            finally { TryDelete(dir); }
        });
    }

    /// <summary>
    /// 第六十改的第二格：新建项目对「已选模式」的记忆规则——
    /// ① 全新会话（刚开窗体）仍是未选：模式不该替用户默认；
    /// ② 选过之后 NewProject（反射调私有方法，与真人菜单同一条路）<b>保留</b>已选模式——
    /// 换一份新文档不必让用户重选一遍；这条同时钉住「继承」而不是「重置为 Unset」，
    /// 改成重置会让连续新建的用户每张都要再点一次。
    /// 反射前先 MarkSaved：NewProject 遇到未保存改动会弹确认框，测试不能挂在那儿等真人
    /// （与 NewProjectClearsSourceCache 同一套按法）。
    /// </summary>
    private static void NewProjectKeepsChosenModeFreshStartsUnset()
    {
        RunOnStaThread(() =>
        {
            var form = new MainForm();
            try
            {
                form.CreateControl();
                Battery.AssertEqual(StitchMode.Unset, form.ProjectForTest.Doc.Mode,
                    "全新会话必须是未选：模式不许替用户默认（第一半）");

                var newProject = typeof(MainForm).GetMethod("NewProject",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                Battery.Assert(newProject is not null, "自证：MainForm.NewProject 必须还在（改名要同步本判据）");
                newProject!.Invoke(form, null);
                Battery.AssertEqual(StitchMode.Unset, form.ProjectForTest.Doc.Mode,
                    "全新会话（未选）下新建项目也应保持未选：没有可继承的模式就不许凭空造一个");

                form.PickModeSegmentForTest(1);
                Battery.AssertEqual(StitchMode.Vertical, form.ProjectForTest.Doc.Mode,
                    "Pick 竖拼后文档模式必须是 Vertical（要继承的值得先存在）");
                form.ProjectForTest.MarkSaved();
                newProject.Invoke(form, null);
                Battery.AssertEqual(StitchMode.Vertical, form.ProjectForTest.Doc.Mode,
                    "新建项目必须保留已选模式（继承而不是重置）：改成重置会让连续新建的用户每张都要再点一次");
                Battery.Evidence("全新会话 Unset → NewProject 仍 Unset → Pick 竖拼 → MarkSaved → NewProject 仍 Vertical（继承）");
            }
            finally { form.Dispose(); }
        });
    }

    /// <summary>
    /// 第五十改把菜单／分段／项目名／导出合并成<b>一条</b> 52px 顶栏（模拟UI需求 §2 线框就是一行）。
    /// 这一格钉：① 菜单带（MenuStrip）只有一条且贴最顶、高度等于设计的那条带——
    /// 合并没合干净会留下第二条 MenuStrip 灰带；② 分段与导出确实宿主在这条带里（而不是散在别处）。
    /// 注意前提：画布工具条是<b>另一回事</b>（需求线框里画布上方本就有一行功能条），
    /// 它也是 Dock=Top 但不属于"菜单带"，所以这里数的是 MenuStrip 而不是所有 ToolStrip——
    /// 钉"form 层顶停靠条带总数=1"会钉到设计本来不产生的量，每次有效测量都翻红。
    /// 另钉一族真出过的缺陷：顶栏按钮写死宽度，在 150% DPI 下把「全部重新分析」裁成「部重新分析」。
    /// 量法是拿按钮自己的字体现量文字宽，与按钮实际宽比——不许写死像素当期望。
    /// </summary>
    private static void TopBandSingleAndTextFits()
    {
        RunOnStaThread(() =>
        {
            (MainForm form, string dir) = FormWithImports();
            try
            {
                var topMenus = form.Controls.OfType<MenuStrip>().Where(t => t.Dock == DockStyle.Top).ToList();
                Battery.AssertEqual(1, topMenus.Count,
                    $"菜单带只许有一条，实际 {topMenus.Count}（合并没合干净会留下第二条 MenuStrip 灰带）");
                MenuStrip menu = topMenus[0];
                Battery.AssertEqual(0, menu.Bounds.Top, "顶栏必须贴最顶");
                Battery.AssertEqual(form.TopBarHeightForTest, menu.Height, "顶栏高度应等于设计的那条带");
                Battery.Assert(menu.Items.OfType<ToolStripControlHost>().Count() >= 2,
                    "分段与导出应宿主在同一条顶栏里");

                ToolStripButton analyze = menu.Items.OfType<ToolStripButton>()
                    .First(b => b.Text == "全部重新分析");
                Battery.Assert(TextFits(analyze.Width, analyze.Text ?? "", analyze.Font),
                    $"分析按钮宽 {analyze.Width} 装不下它的文字（高分屏裁字那一族）");
                Battery.Evidence($"菜单带 1 条、高 {menu.Height}、顶 {menu.Bounds.Top}；分析按钮宽 {analyze.Width}");
            }
            finally { form.Dispose(); TryDelete(dir); }
        });
    }

    /// <summary>量宽那把尺子的反证：差 1px 必须判"裁"，否则上一格恒真。</summary>
    private static void TextFitsRulerHasTeeth()
    {
        const string text = "全部重新分析";
        int need = TextRenderer.MeasureText(text, Theme.UiFont, new Size(int.MaxValue, int.MaxValue),
            TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine).Width;
        Battery.Assert(TextFits(need + 24, text, Theme.UiFont), "够宽必须判够");
        Battery.Assert(!TextFits(need - 1, text, Theme.UiFont),
            "差 1px 必须判裁：若这里仍判够，尺子恒真，顶栏裁字就再也抓不到");
        Battery.Evidence($"文字需 {need}px；{need + 24} 判够、{need - 1} 判裁");
    }

    /// <summary>
    /// 需求 §6：主要点击目标 ≥ 36×36。底部五枚工具 chip 是最常点的一组，
    /// 这一格逐枚量实际尺寸（不是量代码里写的常量——常量对而布局把它压小才算缺陷）。
    /// </summary>
    private static void ToolButtonsMeetTouchTarget()
    {
        RunOnStaThread(() =>
        {
            (MainForm form, string dir) = FormWithImports();
            try
            {
                IReadOnlyList<Size> sizes = form.ToolButtonSizesForTest;
                Battery.AssertEqual(5, sizes.Count, "底部工具按钮应是五枚");
                for (int i = 0; i < sizes.Count; i++)
                    Battery.Assert(MeetsTarget(sizes[i]),
                        $"第 {i + 1} 枚工具按钮 {sizes[i].Width}×{sizes[i].Height} 小于 36×36 点击目标");
                Battery.Evidence(string.Join("；", sizes.Select(s => $"{s.Width}×{s.Height}")));
            }
            finally { form.Dispose(); TryDelete(dir); }
        });
    }

    /// <summary>点击目标那把尺子的反证：宽或高任一边差 1 都必须判不足。</summary>
    private static void TouchTargetRulerHasTeeth()
    {
        Battery.Assert(MeetsTarget(new Size(36, 36)), "正好 36×36 必须判达标");
        Battery.Assert(!MeetsTarget(new Size(35, 36)), "宽差 1 必须判不足");
        Battery.Assert(!MeetsTarget(new Size(36, 35)), "高差 1 必须判不足");
    }

    /// <summary>
    /// 第五十二改（用户图右下 1/2/3 三个框）：拼缝面板把「能动手的」三组置顶，顺序固定——
    /// 第一位动作按钮组（重新分析/恢复自动/取消/确认/跳到）、第二位重影对比组、第三位移 X/Y 组。
    /// 原来按钮组沉在整页最底，窄窗口下「跳到这条拼缝」直接被裁掉（用户图绿框 1 的下缘）。
    ///
    /// ═══ 为什么量 Bounds.Top 而不是量 Controls 的下标 ═══
    /// 下标顺序要经过「Dock=Top 后加入的占外侧 + Rebuild 末尾整体翻转」两道换算才等于阅读顺序，
    /// 判据若钉下标，就把 WinForms 的停靠实现细节当成了期望值；用户看的是「谁在上面」，
    /// 所以这里把面板真停进一个离屏窄窗口（320 宽，复现裁字现场），量各控件布局后的实际顶缘。
    /// 量法与 <c>ui/导出对话框任何控件不许互相重叠也不许出界</c> 同源（ShowOffscreen 逼句柄建全、布局落定）。
    /// </summary>
    private static void SeamPanelActionGroupsComeFirst()
    {
        RunOnStaThread(() =>
        {
            // 竖拼：X 格可用、after 段存在 ⇒ 「跳到这条拼缝」与 X/Y 行都真的摆出来（长截图会锁横向、藏切点旋钮）
            (Project p, MemoryResolver r, _) = TestProject.Create(StitchMode.Vertical,
                Fixture.OpaqueLike(60, 120, seed: 81), Fixture.OpaqueLike(60, 120, seed: 82));
            string seamId = p.Doc.Seams[0].Id;
            using var panel = new ToolPanel(p, new FileSourceResolver(), new CanvasComposer(r));
            panel.SeamId = seamId;

            using var host = new Form { ClientSize = new Size(320, 760) };
            panel.Dock = DockStyle.Fill;
            host.Controls.Add(panel);
            ShowOffscreen(host);
            try
            {
                Button first = panel.Controls.OfType<Button>().First(b => b.Text == "重新分析这条");
                Button confirm = panel.Controls.OfType<Button>().First(b => b.Text == "确认拼缝");
                CheckBox ghost = panel.Controls.OfType<CheckBox>()
                    .First(c => c.Text.StartsWith("重影对比", StringComparison.Ordinal));
                Control xRow = FindIntRow(panel, "X").Parent!;   // X 数值框所在的那一行 FlowLayoutPanel

                // 第一位必须是动作按钮组打头的那枚（而不是状态账目或重影开关）。
                // 第五十四改⑦：滚动条是叠在右留白里的覆盖件（Top=6 比首行还高），不参与竖排，
                // 「最上面」只在内容行里取——量法搬家，判据本体（按钮组第一位）没松。
                Control topmost = panel.Controls.OfType<Control>()
                    .Where(c => c.GetType().Name != "ChromeScrollBar")
                    .OrderBy(c => c.Bounds.Top).First();
                Battery.Assert(ReferenceEquals(topmost, first),
                    $"右栏最上面应是动作按钮组的「重新分析这条」，实际是 {topmost.GetType().Name}「{topmost.Text}」" +
                    "（用户要按钮组显示在右侧第一位）");

                // 三组顶缘严格递增：按钮组 < 重影组 < 位移组
                Battery.Assert(confirm.Bounds.Top < ghost.Bounds.Top,
                    $"按钮组（确认拼缝 Top={confirm.Bounds.Top}）必须在重影组（Top={ghost.Bounds.Top}）之上" +
                    "——用户要按钮组第一位、重影组第二位");
                Battery.Assert(ghost.Bounds.Top < xRow.Bounds.Top,
                    $"重影组（Top={ghost.Bounds.Top}）必须在位移组（X 行 Top={xRow.Bounds.Top}）之上" +
                    "——用户要重影组第二位、位移组第三位");
                Battery.Evidence($"离屏 320 宽面板：重新分析 Top={first.Bounds.Top} < 确认 {confirm.Bounds.Top} < " +
                                 $"重影 {ghost.Bounds.Top} < X 行 {xRow.Bounds.Top}（按钮→重影→位移）");
            }
            finally { HideOffscreen(host); }
        });
    }

    /// <summary>
    /// 第五十二改（用户图左上绿框「文字和下划线有重叠」）：分段选中档的文字底缘不许压到底缘那道强调条。
    /// 病因是写死 Height=36 + 文字在整个段格里垂直居中：150% DPI 下 UiFont 长到 ~26px，
    /// 28px 的段格里居中的字一路压到下划线上。修法两条——高度从字体现量（SyncHeight）、
    /// 文字区底缘让出下划线那条带（TextRect）。这一格拿<b>产品绘制用的同一份几何</b>
    /// （SelectedGeometryForTest，经 MainForm 出口读）断言两件事：文字区装得下当前字高、文字底缘 ≤ 下划线顶缘。
    ///
    /// ═══ 怎么在离屏工装里复现 150% DPI ═══
    /// 9.75pt 在 150% DPI 下的像素高度，等价于 96 DPI 下换一枚 14.6pt 的同族字（em 都是 ~19.5px）。
    /// 所以这里真给分段换一枚 14.6pt 的字再量——不是把判据钉死在某个 DPI 上，也不是拿假几何糊弄。
    /// 末尾那条夹具自证是这把尺的牙：模拟字高必须真超过旧固定高度能给的 22px 文字区（36−8−6），
    /// 否则「写死 36」的旧实现也能蒙混过关，这一格就空转了。
    /// </summary>
    private static void SegmentTextClearsUnderline()
    {
        RunOnStaThread(() =>
        {
            (MainForm form, string dir) = FormWithImports();
            Font? tall = null;
            try
            {
                // ① 出厂字体：文字区装得下字、底缘压不到下划线
                Font normal = form.ModeSegmentFontForTest;
                (Rectangle text, _, Rectangle underline) = form.ModeSegmentGeometryForTest;
                int normalTextH = text.Height;
                Battery.Assert(text.Height >= normal.Height,
                    $"出厂字体下文字区高 {text.Height} 必须装得下字高 {normal.Height}");
                Battery.Assert(text.Bottom <= underline.Y,
                    $"出厂字体下文字底缘 {text.Bottom} 不许压到下划线顶缘 {underline.Y}（用户图「文字和下划线重叠」）");

                // ② 模拟 150% DPI：换一枚像素高度等价的更高字体，几何必须跟着长
                tall = new Font(normal.FontFamily, 14.6f);
                form.SetModeSegmentFontForTest(tall);
                (text, _, underline) = form.ModeSegmentGeometryForTest;
                int tallTextH = text.Height;
                Battery.Assert(text.Height >= tall.Height,
                    $"高字体下文字区高 {text.Height} 必须跟着长到装得下字高 {tall.Height}" +
                    "——写死 36px 的实现这里会红（旧文字区只有 22px）");
                Battery.Assert(text.Bottom <= underline.Y,
                    $"高字体下文字底缘 {text.Bottom} 仍不许压到下划线顶缘 {underline.Y}");

                // ③ 夹具自证（牙）：模拟字高真超过旧固定文字区 22px，旧实现才会被这一格抓住
                Battery.Assert(tall.Height > 36 - 8 - 6,
                    $"夹具失效：模拟字高 {tall.Height} 没超过旧固定文字区 22px（36−8−6），" +
                    "旧实现也能过，这一格没有区分力");
                Battery.Evidence($"出厂字高 {normal.Height}→文字区 {normalTextH}；模拟150%字高 {tall.Height}→文字区 {tallTextH}；" +
                                 "两档文字底缘均 ≤ 下划线顶缘");
            }
            finally { form.Dispose(); tall?.Dispose(); TryDelete(dir); }
        });
    }

    /// <summary>
    /// 第五十三改（用户图黄框「元素没有间隙」、绿框「文字上方没有空隙」）：
    /// 右栏拼缝面板里相邻两枚控件的<b>布局后</b> Bounds 必须留有可感知的纵向间隙。
    /// 量的是真实停靠布局的结果（ShowOffscreen 之后读 Bounds），不是 Margin 属性的字面值——
    /// 首趟实测证明 Dock=Top 的 DefaultLayout 对 Margin 完全不消费（13 对相邻控件逐对量出 0px），
    /// 那正是用户看到的「按钮挤成一整块」；换上 <see cref="StackedDockLayout"/> 后这一格钉住成果。
    /// 溢出档还顺带钉住滚动行为：内容装不下时自绘滑块必须出现、且滚到末尾最后一行完整可见。
    /// （第五十四改⑦起滚动从 AutoScroll 改道为手动偏移 + <c>ChromeScrollBar</c>：
    /// AutoScrollMinSize 与滚动条显隐的反馈环会在最大化+滚到底时自激成抽搐卡死——用户图7。）
    /// </summary>
    private static void SeamPanelBreathingGaps()
    {
        RunOnStaThread(() =>
        {
            (Project p, MemoryResolver r, _) = TestProject.Create(StitchMode.Vertical,
                Fixture.OpaqueLike(60, 120, seed: 83), Fixture.OpaqueLike(60, 120, seed: 84));
            string seamId = p.Doc.Seams[0].Id;
            using var panel = new ToolPanel(p, new FileSourceResolver(), new CanvasComposer(r));
            panel.SeamId = seamId;

            using var host = new Form { ClientSize = new Size(320, 900) };
            panel.Dock = DockStyle.Fill;
            host.Controls.Add(panel);
            ShowOffscreen(host);
            try
            {
                // 第五十四改⑦：自绘滚动条（ChromeScrollBar，internal 类型按类型名过滤）不是一行内容，
                // 叠放判据里不许把它当成相邻行。
                List<Control> ordered = panel.Controls.OfType<Control>()
                    .Where(c => c.Visible && c.GetType().Name != "ChromeScrollBar").OrderBy(c => c.Bounds.Top).ToList();
                Battery.Assert(ordered.Count >= 8, $"面板可见控件应 ≥8 枚，实际 {ordered.Count}（夹具失效）");

                // ① 任何相邻两枚都不许互相压住（间隙 ≥0）
                for (int i = 1; i < ordered.Count; i++)
                {
                    Control prev = ordered[i - 1], cur = ordered[i];
                    Battery.Assert(cur.Bounds.Top >= prev.Bounds.Bottom,
                        $"相邻两枚不许重叠：{prev.GetType().Name}「{Truncate(prev.Text)}」Bottom={prev.Bounds.Bottom} " +
                        $"压到了 {cur.GetType().Name}「{Truncate(cur.Text)}」Top={cur.Bounds.Top}");
                }

                // ② 动作按钮组要有可感知的间隙（Margin 4/4 ⇒ 8px；Margin 一旦又不被消费就红成 0）
                Button a = panel.Controls.OfType<Button>().First(b => b.Text == "重新分析这条");
                Button b2 = panel.Controls.OfType<Button>().First(b => b.Text == "恢复自动");
                int gap = b2.Bounds.Top - a.Bounds.Bottom;
                Battery.Assert(gap >= 6,
                    $"动作按钮组相邻两枚的间隙应 ≥6px，实际 {gap}px（重新分析 Bottom={a.Bounds.Bottom} → 恢复自动 Top={b2.Bounds.Top}）" +
                    "——用户图黄框「元素没有间隙」");

                // ③ 引上小节的标签上方要有呼吸（用户图绿框「重叠透明度太挤」）：Margin 8 + 上行 3 ⇒ ≥8
                CheckBox diff = panel.Controls.OfType<CheckBox>()
                    .First(c => c.Text.StartsWith("差异标红", StringComparison.Ordinal));
                Label opacityLabel = panel.Controls.OfType<Label>()
                    .First(l => l.Text.StartsWith("重叠透明度", StringComparison.Ordinal));
                int labelGap = opacityLabel.Bounds.Top - diff.Bounds.Bottom;
                Battery.Assert(labelGap >= 8,
                    $"「重叠透明度」标签上方应留 ≥8px，实际 {labelGap}px（差异标红 Bottom={diff.Bounds.Bottom}）" +
                    "——用户图绿框「文字的上方没有空隙，太挤了」");

                host.ClientSize = new Size(280, 600);
                panel.SeamId = null;
                panel.SeamId = seamId;
                host.PerformLayout();
                Application.DoEvents();
                panel.PerformLayout();
                ordered = panel.Controls.OfType<Control>()
                    .Where(c => c.Visible && c.GetType().Name != "ChromeScrollBar").OrderBy(c => c.Top).ToList();
                a = panel.Controls.OfType<Button>().First(b => b.Text == "重新分析这条");
                // 第五十四改⑦：AutoScroll 退役后 VerticalScroll/HorizontalScroll 恒不可见——
                // 那两枚属性已经量不出任何东西（设计本来就不产生它们），判据改钉「滚动条不压内容行」：
                // 自绘条整根叠在右留白里，它的左缘必须 ≥ 最宽内容行的右缘。
                Control scrollBar = panel.Controls.OfType<Control>().First(c => c.GetType().Name == "ChromeScrollBar");
                Battery.Assert(scrollBar.Left >= ordered.Max(c => c.Right),
                    $"自绘滚动条必须叠在右留白里不许压内容行：bar.Left={scrollBar.Left}，" +
                    $"最宽行 Right={ordered.Max(c => c.Right)}，客户区 {panel.ClientSize}");
                foreach (Control child in ordered)
                    Battery.Assert(child.Right + child.Margin.Right <= panel.ClientSize.Width - panel.Padding.Right,
                        $"参数行必须收在客户区内：{child.GetType().Name} {child.Bounds}，客户区 {panel.ClientSize}");

                // ④ 溢出档：内容装不下时自绘滑块必须出现，滚到末尾最后一行完整可见
                host.ClientSize = new Size(320, 300);
                host.PerformLayout();
                Application.DoEvents();
                panel.PerformLayout();
                Battery.Assert(panel.ScrollbarVisibleForTest,
                    $"内容溢出时（客户区 {panel.ClientSize.Height}、内容总高 {panel.ContentExtentForTest}）滑块必须出现");
                panel.ActiveControl = a;
                Battery.Assert(ReferenceEquals(panel.ActiveControl, a), "夹具必须把焦点留在顶部按钮");
                int bottomPosition = panel.ContentExtentForTest - panel.ClientSize.Height;
                panel.ScrollToForTest(bottomPosition);
                int contentHeight = panel.ContentExtentForTest;
                for (int pass = 0; pass < 3; pass++)
                {
                    panel.PerformLayout();
                    Application.DoEvents();
                    Battery.AssertEqual(bottomPosition, panel.ScrollValueForTest, "滚到底后重复布局不许回跳");
                    Battery.AssertEqual(contentHeight, panel.ContentExtentForTest, "重排后的内容范围应保持稳定");
                }
                Control last = ordered[^1];
                Battery.Assert(last.Bounds.Top >= 0 && last.Bounds.Bottom <= panel.ClientSize.Height,
                    $"滚到底并重排后最后一枚必须完整可见：{last.Bounds}，客户区高 {panel.ClientSize.Height}");
                panel.EnsureVisibleForTest(a);
                Battery.Assert(a.Top >= 0 && a.Bottom <= panel.ClientSize.Height,
                    "显式显露控件必须把顶部按钮滚回视口");
                var wheel = typeof(Control).GetMethod("OnMouseWheel",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                for (int step = 0; step < 80; step++)
                    wheel.Invoke(panel, new object[] { new MouseEventArgs(MouseButtons.None, 0, 100, 100, -120) });
                Battery.AssertEqual(bottomPosition, panel.ScrollValueForTest, "滚轮通路必须能滚到底");
                panel.PerformLayout();
                Battery.AssertEqual(bottomPosition, panel.ScrollValueForTest, "滚轮后重排也不能回跳");

                // 反证改形（第五十四改⑦）：旧反证「内存绕过 _adjustingScrollbars 让焦点回跳」量的是
                // AutoScroll 时代的专用守卫；手动滚动没有那个守卫，红得起的对偶行为是
                // 「越界滚动值必须被夹回合法区间」——夹取被拆掉时这两格红。
                panel.ScrollToForTest(contentHeight + 10000);
                panel.PerformLayout();
                Battery.Assert(panel.ScrollValueForTest <= bottomPosition,
                    "越界滚动值必须被夹回 [0, 内容总高−视口]——夹取被拆掉时这一格红");
                panel.ScrollToForTest(-500);
                panel.PerformLayout();
                Battery.AssertEqual(0, panel.ScrollValueForTest, "负值滚动必须被夹回 0");
                panel.ScrollToForTest(bottomPosition);
                panel.PerformLayout();
                Battery.AssertEqual(bottomPosition, panel.ScrollValueForTest, "恢复后应再次稳定停在末尾");
                Battery.Evidence($"900 高：按钮间隙 {gap}px、标签上方 {labelGap}px；300 高：赋值/滚轮后重排均保持末尾 {bottomPosition}；越界/负值滚动均被夹回，末控件 Bottom={last.Bounds.Bottom}");
            }
            finally { HideOffscreen(host); }
        });
    }

    /// <summary>
    /// 第五十三改（用户图绿框「滑杆点击后没法使用左右方向键」「也没标注透明度的百分比数值」）：
    /// ① ChromeSlider 必须可选中（Selectable）——缺这一位时 Focus() 空转、焦点进不来，
    ///    OnKeyDown 里那套方向键永远收不到（旧实现就死在这）；
    /// ② 焦点落在滑杆上之后，方向键（含 Shift 加速、Home/End 到头）必须真改值；
    /// ③ 「重叠透明度」标签里必须带实时百分比，且与滑杆当前值一字不差。
    /// 键注入走 <see cref="ChromeSlider.PressKeyForTest"/>（产品 OnKeyDown 同一条路），
    /// IsInputKey 的认领走 <see cref="ChromeSlider.AcceptsKeyForTest"/>——不认领的话方向键
    /// 在容器那一层就被吞掉，根本到不了 OnKeyDown。
    /// </summary>
    private static void OpacitySliderKeyboardAndPercent()
    {
        RunOnStaThread(() =>
        {
            (Project p, MemoryResolver r, _) = TestProject.Create(StitchMode.Vertical,
                Fixture.OpaqueLike(60, 120, seed: 85), Fixture.OpaqueLike(60, 120, seed: 86));
            string seamId = p.Doc.Seams[0].Id;
            using var panel = new ToolPanel(p, new FileSourceResolver(), new CanvasComposer(r));
            panel.SeamId = seamId;

            using var host = new Form { ClientSize = new Size(320, 900) };
            panel.Dock = DockStyle.Fill;
            host.Controls.Add(panel);
            ShowOffscreen(host);
            try
            {
                // ChromeSlider 是 App 内部类型，工装经 ToolPanel 的公开 ForTest 出口拿它（与 MainForm 同一约定）
                Control slider = panel.OpacitySliderForTest!;
                Label opacityLabel = panel.Controls.OfType<Label>()
                    .First(l => l.Text.StartsWith("重叠透明度", StringComparison.Ordinal));

                // ① 可选中 + 点它之后焦点真落在滑杆上（用户「点击后」这一幕的同一通路）
                Battery.Assert(slider.CanSelect,
                    "滑杆必须可选中（Selectable）——旧实现缺这一位，Focus() 空转，方向键因此全死");
                slider.Focus();
                Application.DoEvents();
                Battery.Assert(slider.Focused,
                    "点滑杆之后焦点必须真的落在这枚滑杆上（Focus() 进不来 = 方向键无从谈起）");

                // ② 方向键改值：← 退 1、Shift+← 退 10、→ 进 1、Home/End 到头
                Battery.Assert(panel.OpacitySliderAcceptsKeyForTest(Keys.Left),
                    "IsInputKey 必须认领方向键，否则容器层就把按键吞掉、OnKeyDown 收不到");
                int before = panel.OpacitySliderValueForTest;
                panel.OpacitySliderPressKeyForTest(Keys.Left);
                Battery.AssertEqual(before - 1, panel.OpacitySliderValueForTest, "按 ← 应退 1");
                panel.OpacitySliderPressKeyForTest(Keys.Left | Keys.Shift);
                Battery.AssertEqual(before - 11, panel.OpacitySliderValueForTest, "Shift+← 应退 10（加速档）");
                panel.OpacitySliderPressKeyForTest(Keys.Right);
                Battery.AssertEqual(before - 10, panel.OpacitySliderValueForTest, "按 → 应进 1");
                panel.OpacitySliderPressKeyForTest(Keys.Home);
                Battery.AssertEqual(panel.OpacitySliderMinimumForTest, panel.OpacitySliderValueForTest, "Home 应到下限");
                panel.OpacitySliderPressKeyForTest(Keys.End);
                Battery.AssertEqual(panel.OpacitySliderMaximumForTest, panel.OpacitySliderValueForTest, "End 应到上限");

                // ③ 百分比读数与滑杆值一字不差（旧实现根本没有这个数）
                Battery.Assert(opacityLabel.Text.Contains($"{panel.OpacitySliderValueForTest}%"), 
                    $"标签「{opacityLabel.Text}」必须带实时百分比（当前滑杆值 {panel.OpacitySliderValueForTest}%）");
                panel.OpacitySliderPressKeyForTest(Keys.Home);
                Battery.Assert(opacityLabel.Text.Contains($"{panel.OpacitySliderValueForTest}%"),
                    $"滑杆改到 {panel.OpacitySliderValueForTest} 后标签必须跟着变，实际「{opacityLabel.Text}」——读数不许停在旧值");
                Battery.Evidence($"焦点可达；← 1、Shift ← 10、→ 1、Home/End 到头；标签实时显示 {panel.OpacitySliderValueForTest}%");
            }
            finally { HideOffscreen(host); }
        });
    }

    private static string Truncate(string text) => text.Length <= 10 ? text : text[..10] + "…";

    /// <summary>
    /// 第五十三改（用户图红框「文字的最上方有重叠」）：分段选中档的文字<b>顶缘</b>不许越过选中胶囊的顶缘。
    /// 第五十二改只钉了底缘（文字压下划线），顶缘漏了：旧文字区从段格顶（Y=4）起算，垂直居中的字
    /// 在 96 DPI 顶到 Y=6、高 DPI 下 Y=5，都从胶囊顶缘（Y=7）探出头去。修法是文字区从胶囊自己推导
    /// （上缘贴胶囊顶、下缘钉在下划线之上），见 SegmentedControl.TextRect。
    /// 牙：夹具自证胶囊顶缘确实低于段格顶（thumb.Y > 4）——若几何改回「文字区从段格顶起算」，
    /// text.Y=4 < thumb.Y=7，这一格必红。
    /// </summary>
    private static void SegmentTextStaysInsideThumb()
    {
        RunOnStaThread(() =>
        {
            (MainForm form, string dir) = FormWithImports();
            Font? tall = null;
            try
            {
                // ① 出厂字体：文字顶缘 ≥ 胶囊顶缘、底缘 ≤ 胶囊底缘
                Font normal = form.ModeSegmentFontForTest;
                (Rectangle text, Rectangle thumb, _) = form.ModeSegmentGeometryForTest;
                Battery.Assert(text.Y >= thumb.Y,
                    $"出厂字体下文字顶缘 {text.Y} 不许越过胶囊顶缘 {thumb.Y}（用户图红框「文字的最上方有重叠」）");
                Battery.Assert(text.Bottom <= thumb.Bottom,
                    $"出厂字体下文字底缘 {text.Bottom} 不许越过胶囊底缘 {thumb.Bottom}");

                // ② 高字体（模拟 150% DPI）同样不许探出
                tall = new Font(normal.FontFamily, 14.6f);
                form.SetModeSegmentFontForTest(tall);
                (text, thumb, _) = form.ModeSegmentGeometryForTest;
                Battery.Assert(text.Y >= thumb.Y,
                    $"高字体下文字顶缘 {text.Y} 仍不许越过胶囊顶缘 {thumb.Y}");
                Battery.Assert(text.Bottom <= thumb.Bottom,
                    $"高字体下文字底缘 {text.Bottom} 仍不许越过胶囊底缘 {thumb.Bottom}");

                // ③ 夹具自证（牙）：胶囊顶缘必须真的低于段格顶（4），否则「不出胶囊」是空话
                Battery.Assert(thumb.Y > 4,
                    $"夹具失效：胶囊顶缘 {thumb.Y} 没有低于段格顶 4，旧实现（文字区从段格顶起算）也能过，这一格没有区分力");
                Battery.Evidence($"出厂字高 {normal.Height}：文字 {text.Y}..{text.Bottom} ∈ 胶囊 {thumb.Y}..{thumb.Bottom}；" +
                                 $"高字档同样收在胶囊内");
            }
            finally { form.Dispose(); tall?.Dispose(); TryDelete(dir); }
        });
    }

    /// <summary>
    /// 第五十三改（用户图蓝框「下箭头被标注成了上箭头」）：数字框右侧那两枚 chevron 的点序必须分形状。
    /// 病根在 ChromeNumericBox.OnPaint：下半那枚的点序写成 (cy+8),(cy+4),(cy+8)——顶点（中点）
    /// 反而高于两臂，画出来与上半那枚同形，屏上两枚都是 ^。守卫拿产品绘制用的同一份算式
    /// （经 ToolPanel.NumericSpinGlyphsForTest 出口）断言：上枚顶点高于两臂（^）、
    /// 下枚顶点低于两臂（v），且各自落在自己的点击半区、都在右侧 SpinStrip 竖带内——
    /// 「点哪儿就动哪半」的契约与画出来的字形必须对得上。
    /// </summary>
    private static void SpinGlyphDownPointsDown()
    {
        RunOnStaThread(() =>
        {
            (Project p, MemoryResolver r, _) = TestProject.Create(StitchMode.Vertical,
                Fixture.OpaqueLike(60, 120, seed: 87), Fixture.OpaqueLike(60, 120, seed: 88));
            using var panel = new ToolPanel(p, new FileSourceResolver(), new CanvasComposer(r));
            panel.SeamId = p.Doc.Seams[0].Id;

            using var host = new Form { ClientSize = new Size(320, 900) };
            panel.Dock = DockStyle.Fill;
            host.Controls.Add(panel);
            ShowOffscreen(host);
            try
            {
                // 数字框本体与几何都按基类／公开出口出手（ChromeNumericBox 是 internal 类型）
                NumericUpDown box = panel.NumericBoxForTest!;
                (Point[] up, Point[] down) = panel.NumericSpinGlyphsForTest;
                int strip = panel.NumericSpinStripForTest;
                Battery.Assert(up[1].Y < up[0].Y && up[1].Y < up[2].Y,
                    $"上箭头顶点 Y={up[1].Y} 必须高于两臂（{up[0].Y}/{up[2].Y}），读作 ^");
                Battery.Assert(down[1].Y > down[0].Y && down[1].Y > down[2].Y,
                    $"下箭头顶点 Y={down[1].Y} 必须低于两臂（{down[0].Y}/{down[2].Y}），读作 v" +
                    "——点序写反时两枚同形，就是用户看到的「下箭头被标注成了上箭头」");
                Battery.Assert(up[1].Y < box.Height / 2 && down[1].Y > box.Height / 2,
                    $"两枚箭头必须各自落在自己的点击半区（上半加＝Y<{box.Height / 2}，下半减）——" +
                    $"实际上枚 {up[1].Y}、下枚 {down[1].Y}");
                Battery.Assert(down[0].X >= box.Width - strip && down[2].X < box.Width,
                    $"箭头必须画在右侧 SpinStrip={strip}px 竖带内（点击区与字形要同一条带）");
                Battery.Evidence($"框 {box.Width}×{box.Height}；上枚 ^（顶点 Y={up[1].Y} < 臂 {up[0].Y}）；" +
                                 $"下枚 v（顶点 Y={down[1].Y} > 臂 {down[0].Y}）");
            }
            finally { HideOffscreen(host); }
        });
    }

    // ═════════════════════ 第五十五改（用户六条缺陷报告）═════════════════════

    /// <summary>
    /// 55改①（用户图1「分割线条，放大后不能太小」）：
    /// 旧写法六支笔宽度写死 1/2 像素，放大到 400% 缝线仍是一根发丝，屏上找不到。
    /// 判据两段各证一头：
    /// ① 纯函数对表——≤100% 维持历史形状（1/2），&gt;100% Ceiling 保证"放大了就至少加粗一档"，上限 4/5；
    /// ② 接线证明——<see cref="StitchCanvasControl.RenderFrameForTest"/> 真画两帧
    ///    （100% 与 250%），画完六支笔里被这一帧用过的两支，宽度必须等于纯函数在当前档的答案。
    /// 笔是 private static（"画具提静态"红线），电池没有 InternalsVisibleTo，反射只读不动。
    /// </summary>
    private static void SeamLineWidthFollowsZoom()
    {
        RunOnStaThread(() =>
        {
            (Project p, MemoryResolver r, _) = TestProject.Create(StitchMode.Vertical,
                Fixture.OpaqueLike(60, 120, seed: 301), Fixture.OpaqueLike(60, 120, seed: 302));
            // 状态钉成"自动通过"：DrawSeams 按 Status 选笔，钉死之后读哪支笔就不再靠猜。
            p.Doc.Seams[0].Status = SeamStatus.AutoPassed;
            using var canvas = new StitchCanvasControl(p, new CanvasComposer(r));
            canvas.Size = new Size(400, 300);

            Battery.AssertEqual(1, StitchCanvasControl.SeamLinePenWidthForZoom(1.0, selected: false), "100% 仍是 1px（历史形状不许变）");
            Battery.AssertEqual(1, StitchCanvasControl.SeamLinePenWidthForZoom(0.25, selected: false), "缩小档不加粗");
            Battery.AssertEqual(2, StitchCanvasControl.SeamLinePenWidthForZoom(1.5, selected: false), "150% 至少加粗一档（Ceiling）");
            Battery.AssertEqual(4, StitchCanvasControl.SeamLinePenWidthForZoom(4.0, selected: false), "400% 到核心上限 4");
            Battery.AssertEqual(4, StitchCanvasControl.SeamLinePenWidthForZoom(8.0, selected: false), "超过上限钳 4");
            Battery.AssertEqual(3, StitchCanvasControl.SeamLinePenWidthForZoom(2.0, selected: true), "选中在放大档再 +1");
            Battery.AssertEqual(5, StitchCanvasControl.SeamLinePenWidthForZoom(8.0, selected: true), "选中上限 5");

            var okPen = (System.Drawing.Pen)typeof(StitchCanvasControl).GetField("SeamLineOkPen",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null)!;
            var okSelPen = (System.Drawing.Pen)typeof(StitchCanvasControl).GetField("SeamLineOkSelectedPen",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null)!;

            using (var frame = new System.Drawing.Bitmap(400, 300))
            {
                canvas.Viewport.SetOneToOne(new Point(0, 0));
                canvas.RenderFrameForTest(frame);
                Battery.AssertEqual(1, (int)okPen.Width, "100% 真画一帧后 OK 笔宽应回落到 1（接线证明的前半）");
            }
            using (var frame = new System.Drawing.Bitmap(400, 300))
            {
                canvas.Viewport.SetZoom(2.5);
                canvas.SelectSeam(p.Doc.Seams[0].Id);
                canvas.RenderFrameForTest(frame);
                Battery.AssertEqual(4, (int)okSelPen.Width, "250% 选中笔宽应是 Ceiling(2.5)+1=4");
            }
            Battery.Evidence("纯函数对表 1/1/2/4/4 与选中 3/5；真画 100%→1、250% 选中→4，接线吻合");
        });
    }

    /// <summary>
    /// 55改③（用户图3）：格式下拉的两条契约。
    /// ① 只列大写的 PNG/JPEG/TIFF 三档——WebP 编码器 Probe 永远"不可用"，摆在下拉里就是
    ///    「选中了也导不出」的假选项（只许进不许出：导入侧 WebP 解码保留，本格不管它）；
    /// ② 显示大写不许带走存储口径——计划算出的文件扩展名必须仍是小写 .png；
    /// ③ 旧项目存过 WebP 时打开对话框要回退选中 PNG，不许停在"下拉里已不存在"的档位上。
    /// </summary>
    private static void ExportFormatLabelsUppercaseNoWebP()
    {
        RunOnStaThread(() =>
        {
            (Project project, MemoryResolver resolver, _) = TestProject.Create(StitchMode.Vertical,
                Fixture.OpaqueLike(60, 120, seed: 311), Fixture.OpaqueLike(60, 120, seed: 312));
            using var dialog = new ExportDialog(project, new CanvasComposer(resolver), new EncoderRegistry());

            var labels = dialog.FormatLabelsForTest;
            Battery.AssertEqual(3, labels.Count, $"下拉只应有三档（WebP 不许再出现），实际 {labels.Count}：{string.Join("/", labels)}");
            Battery.AssertTextEqual("PNG", labels[0], "第一档显示名应是大写 PNG");
            Battery.AssertTextEqual("JPEG", labels[1], "第二档显示名应是大写 JPEG");
            Battery.AssertTextEqual("TIFF", labels[2], "第三档显示名应是大写 TIFF");
            Battery.AssertTextEqual(".png", dialog.SelectedExtensionForTest ?? "",
                "显示大写不许带走存储口径：计划里的文件扩展名必须仍是小写");

            project.Doc.Export.Format = ExportFormat.WebP;   // 旧项目可能存过这个档
            using var reopened = new ExportDialog(project, new CanvasComposer(resolver), new EncoderRegistry());
            Battery.AssertTextEqual(".png", reopened.SelectedExtensionForTest ?? "",
                "旧项目存过 WebP 时应回退选中 PNG（下拉里已没有 WebP 这一项可停）");
            Battery.Evidence($"下拉 {string.Join("/", labels)}；计划扩展名 .png；旧档 WebP 打开回退 PNG");
        });
    }

    /// <summary>
    /// 55改②（用户图2）：导出对话框行内几何的两条契约。
    /// ① 每行里非标签控件都要按行高垂直居中——旧写法各自顶行首站着，行高被高个儿撑起来后
    ///    矮个儿偏上，就是用户量出的「两个红框没有水平对齐」；
    /// ② 路径框/模板框的发丝边宿主不许再拿 <c>Theme.PanelBorder</c>（浅灰）当底色——
    ///    上下各 5px 的 Padding 带把浅灰整条露出来，就是截图里那圈「难看的浅色底纹」。
    /// 判据读的是 BuildContent 收尾 pass 留下的现场：标签拉满行高、其余 Margin.Top 居中。
    /// </summary>
    private static void ExportDialogRowsVerticallyAligned()
    {
        RunOnStaThread(() =>
        {
            (Project project, MemoryResolver resolver, _) = TestProject.Create(StitchMode.Vertical,
                Fixture.OpaqueLike(60, 120, seed: 321), Fixture.OpaqueLike(60, 120, seed: 322));
            using var dialog = new ExportDialog(project, new CanvasComposer(resolver), new EncoderRegistry());

            var rows = dialog.BodyRowsForTest;
            Battery.Assert(rows.Count >= 5, $"自证：行容器应至少 5 条，实际 {rows.Count}——夹具与对话框结构脱节了");
            int checkedRows = 0;
            foreach (Control row in rows)
            {
                var strip = (FlowLayoutPanel)row;
                int rowHeight = strip.PreferredSize.Height;
                foreach (Control child in strip.Controls)
                {
                    if (Equals(child.Tag, "field-label"))
                    {
                        Battery.AssertEqual(rowHeight, child.Height,
                            $"行 {checkedRows}：标签「{child.Text}」应拉满行高 {rowHeight}，实际 {child.Height}");
                    }
                    else
                    {
                        int want = Math.Max(0, (rowHeight - child.Height) / 2);
                        Battery.AssertEqual(want, child.Margin.Top,
                            $"行 {checkedRows}：{child.GetType().Name} 应垂直居中（行高 {rowHeight}、控件高 {child.Height}），" +
                            $"Margin.Top 实际 {child.Margin.Top}、应 {want}");
                    }
                    if (child is Panel host)
                        Battery.AssertEqual(Theme.PanelBackground, host.BackColor,
                            $"行 {checkedRows}：发丝边宿主底色必须是 PanelBackground（浅色只剩 1px 边框线，不许整块 PanelBorder 当底纹）");
                }
                checkedRows++;
            }
            Battery.Evidence($"逐行核了 {checkedRows} 行：标签拉满、其余垂直居中、{rows.Cast<FlowLayoutPanel>().Sum(s => s.Controls.OfType<Panel>().Count())} 枚宿主底色=PanelBackground");
        });
    }

    /// <summary>
    /// 55改④（用户图4）：素材列表的自绘滚动条三件事。
    /// ① 几何裁剪成立——列表必须比宿主宽（原生条贴在列表窗口右缘，宽出去了就整根落在宿主界外）；
    /// ② 内容装不下时蓝色滑块真的出现；滚轮一格 TopIndex 真动，且条上的像素偏移与列表位置
    ///    <b>同一趟</b>对齐（= TopIndex×行高，不许等下一次重画才抹平）；
    /// ③ 装得下时滑块消失，反着滚回顶一个像素都不留。
    /// </summary>
    private static void SourceListUsesChromeScrollBar()
    {
        RunOnStaThread(() =>
        {
            // SourcePanel 只收 FileSourceResolver（构造器第二参数定死了），
            // 所以照 RelocateReportsEveryBranch 的模式落 5 个真实 PNG 再登记，不用内存解析器。
            string dir = Path.Combine(Path.GetTempPath(), "ls-scroll-" + Guid.NewGuid().ToString("N")[..6]);
            Directory.CreateDirectory(dir);
            try
            {
                // 20 段而不是 5 段：内容高度要高出客户区一大截，滚轮一格（72px）才够折成一行——
                // 段数太少时滚动余量不足一格，TopIndex 折不出来，量的是钳位不是滚轮。
                var resolver = new FileSourceResolver();
                var project = new Project(new ProjectDocument { Mode = StitchMode.Vertical });
                using var panel = new SourcePanel(project, resolver);
                using var hostForm = new Form { ClientSize = new Size(300, 420) };
                panel.Dock = DockStyle.Fill;
                hostForm.Controls.Add(panel);
                ShowOffscreen(hostForm);
                try
                {
                    // 装料放在出窗之后（与真实使用同态：列表先在、条目后到）。
                    // 实量教训：条目在句柄创建前就装满的话，原生 ListBox 的第一发
                    // LB_SETTOPINDEX 会被静默拒收（直设 TopIndex 读回仍是 0），
                    // 那是屏外夹具的坏形态，不是产品通路——装料走 AppendSegmentForSource
                    // 的 Mutate→Changed→RefreshItems，行是带着句柄装进去的。
                    for (int i = 0; i < 20; i++)
                    {
                        RgbaImage img = Fixture.OpaqueLike(60, 200, seed: 40 + i);
                        string file = Path.Combine(dir, $"img{i}.png");
                        File.WriteAllBytes(file, Core.Imaging.Png.PngEncoder.EncodeToBytes(img));
                        var src = new SourceImage
                        {
                            Id = Project.NewId("src"), Path = file, FileName = $"img{i}.png",
                            Size = new IntSize(60, 200), Format = "png",
                        };
                        resolver.Register(src);
                        project.Doc.Sources.Add(src);
                        project.AppendSegmentForSource(src);
                    }

                    int rowH = panel.ListRowHeightForTest;
                    Battery.Assert(panel.ListRowWidthForTest > 0,
                        "自证：宿主应已把可见行宽写进列表（0 = 宿主根本没接管，后面全在量空气）");
                    Battery.Assert(panel.NativeScrollbarClippedForTest,
                        "列表必须比宿主宽：原生滚动条贴在列表右缘，宽出去才整根被裁在界外");
                    Battery.Assert(panel.ScrollbarThumbVisibleForTest,
                        $"20 段 × 行高 {rowH} = {20 * rowH}px 装不下时，蓝色滑块必须出现");

                    panel.ScrollListWheelForTest(-120);
                    Battery.Assert(panel.ListTopIndexForTest >= 1,
                        $"向下滚一格后 TopIndex 应前进，实际 {panel.ListTopIndexForTest}");
                    Battery.AssertEqual(panel.ListTopIndexForTest * rowH, panel.ScrollbarValueForTest,
                        "条上的像素偏移必须等于 TopIndex×行高（同一趟对齐，不许留到下一次重画）");

                    panel.ScrollListWheelForTest(+120);
                    panel.ScrollListWheelForTest(+120);
                    panel.ScrollListWheelForTest(+120);
                    Battery.AssertEqual(0, panel.ListTopIndexForTest, "反向滚回顶后 TopIndex 应归零");
                    Battery.AssertEqual(0, panel.ScrollbarValueForTest, "反向滚回顶后条上的偏移应归零");

                    // 量"装得下滑块消失"这一支不能靠放大窗：1500 高会被屏幕工作面积钳回
                    // （实量 LargeChange 只有 955）。改走真实的删段通路把内容裁到 3 行，
                    // 内容高 3×行高 < 视口，滑块必须自己退场。
                    project.Mutate("裁掉尾段", d =>
                    {
                        for (int i = d.Segments.Count - 1; i >= 3; i--) d.Segments.RemoveAt(i);
                        for (int i = d.Sources.Count - 1; i >= 3; i--) d.Sources.RemoveAt(i);
                    });
                    project.EnsureSeams();
                    Battery.Assert(!panel.ScrollbarThumbVisibleForTest, "内容装得下时自绘滑块不许出现");
                    Battery.Evidence($"行高 {rowH}；列表宽 > 宿主宽（原生条出界）；滚一格 TopIndex→{panel.ListTopIndexForTest} 已回 0；裁到 3 行后滑块隐藏");
                }
                finally { HideOffscreen(hostForm); }
            }
            finally { TryDelete(dir); }
        });
    }

    /// <summary>
    /// 55改④（用户图4）的另一半：右栏拼缝列表走的是同一套宿主。
    /// 判据量几何本身：宿主里的 ListBox 必须是能拦滚轮的 ChromeScrollListBox 子类、
    /// 必须比宿主宽（原生条被裁出界）、高铺满宿主。滚动行为已由素材列表那格证过，这里不重复。
    /// </summary>
    private static void SeamListUsesChromeScrollBar()
    {
        RunOnStaThread(() =>
        {
            using var form = new MainForm();
            ShowOffscreen(form);
            try
            {
                Control host = form.SeamListHostForTest;
                var list = host.Controls.OfType<ListBox>().Single();
                Battery.AssertEqual("ChromeScrollListBox", list.GetType().Name,
                    "拼缝列表必须是 ChromeScrollListBox（普通 ListBox 的滚轮走原生、宽出去也没意义）");
                Battery.Assert(list.Width > host.ClientSize.Width,
                    $"列表宽 {list.Width} 必须大于宿主宽 {host.ClientSize.Width}：原生滚动条要整根落在界外");
                Battery.AssertEqual(host.ClientSize.Height, list.Height, "列表高应铺满宿主");
                Battery.Evidence($"拼缝列表 {list.Width}×{list.Height}，宿主 {host.ClientSize.Width}×{host.ClientSize.Height}——原生条出界、蓝条接管");
            }
            finally { HideOffscreen(form); }
        });
    }

    /// <summary>
    /// 55改⑤（用户图5）：默认窗宽 1440→1600。旧尺寸下状态栏的弹性读数被固定槽位挤掉
    /// （「导出」入口右边的文字被截断，拉一下窗口才出得来）；1600 对 1080p 与 2K@150% 都放得下。
    /// 高度 900 不动：2K@150% 的逻辑高约 955，再高要顶任务栏。
    /// 电池进程是 DPI 无感知的（96），Size 读回的就是字面值——与真窗口 150% 那一档两回事。
    /// </summary>
    private static void MainFormDefaultSizeFitsStatusBar()
    {
        RunOnStaThread(() =>
        {
            using var form = new MainForm();
            form.CreateControl();
            Battery.AssertEqual(1600, form.Size.Width, "默认宽度必须是 1600（1440 会把状态栏弹性读数挤掉）");
            Battery.AssertEqual(900, form.Size.Height, "默认高度必须是 900（2K@150% 下再高顶任务栏）");
            Battery.Assert(form.MinimumSize.Width >= 1024, "最小宽度约束不许被顺手放松");
            Battery.Evidence($"默认 {form.Size.Width}×{form.Size.Height}，最小 {form.MinimumSize.Width}×{form.MinimumSize.Height}");
        });
    }

    /// <summary>
    /// 第五十六改①（用户图1红框）：素材列表行卡片的左右沿必须与「添加图片」按钮外框
    /// 对成<b>同一条竖线</b>。旧写法卡片缩 8px、按钮宿主留 16px——红框量出的正是这 8px 错位。
    /// 修法是两边同吃 <c>SourcePanel.SideInset</c> 一枚常量，判据据此两边对账。
    ///
    /// ═══ 坐标系口径 ═══
    /// <c>LastCardRectForTest</c> 是<b>列表客户区</b>坐标、<c>AddButtonFrameForTest</c> 是
    /// <b>面板客户区</b>坐标，纵向天生不同——所以只对账左右沿。横向两边重合的依据：
    /// 面板自身无 Padding、宿主 Dock=Fill（客户区 x=0）、列表在宿主里 SetBounds(0,0,…)，
    /// 三层横向原点都落在面板客户区 x=0 上，左右沿数值可直接比较。
    /// 这个"原点重合"本身由下面的自证链守着：卡片宽必须等于宿主宽 − 2×SideInset，
    /// 若谁动了布局把列表推离原点，这条会先红。
    /// </summary>
    private static void SourceRowCardAlignsAddButton()
    {
        RunOnStaThread(() =>
        {
            string dir = Path.Combine(Path.GetTempPath(), "ls-align-" + Guid.NewGuid().ToString("N")[..6]);
            Directory.CreateDirectory(dir);
            try
            {
                var resolver = new FileSourceResolver();
                var project = new Project(new ProjectDocument { Mode = StitchMode.Vertical });
                using var panel = new SourcePanel(project, resolver);
                using var hostForm = new Form { ClientSize = new Size(300, 420) };
                panel.Dock = DockStyle.Fill;
                hostForm.Controls.Add(panel);
                ShowOffscreen(hostForm);
                try
                {
                    // 装料放在出窗之后（与 SourceListUsesChromeScrollBar 同款：行要带着句柄装进去）
                    RgbaImage img = Fixture.OpaqueLike(60, 200, seed: 56);
                    string file = Path.Combine(dir, "img0.png");
                    File.WriteAllBytes(file, Core.Imaging.Png.PngEncoder.EncodeToBytes(img));
                    var src = new SourceImage
                    {
                        Id = Project.NewId("src"), Path = file, FileName = "img0.png",
                        Size = new IntSize(60, 200), Format = "png",
                    };
                    resolver.Register(src);
                    project.Doc.Sources.Add(src);
                    project.AppendSegmentForSource(src);

                    panel.ForceRowPaintForTest();
                    var card = panel.LastCardRectForTest;
                    Battery.Assert(card.Width > 0,
                        "自证：行自绘应写下非空卡片矩形（宽 0 = OnDrawItem 根本没跑，后面在量空气）");
                    // 原点重合的自证：卡片宽必须正好是 宿主宽 − 2×SideInset。
                    // 若布局改了把列表/宿主推离面板原点，宽账先崩，左右沿比较作废。
                    Battery.AssertEqual(
                        panel.ListHostForTest.ClientSize.Width - SourcePanel.SideInset * 2, card.Width,
                        $"卡片宽应 = 宿主宽 − 2×SideInset（原点重合的自证），实际 {card.Width}");

                    var frame = panel.AddButtonFrameForTest;
                    Battery.Assert(frame.Width > 0, "自证：按钮外框宽应为正");
                    Battery.AssertEqual(frame.Left, card.Left,
                        $"卡片左沿 {card.Left} 必须与按钮外框左沿 {frame.Left} 同一条竖线（图1红框的错位）");
                    Battery.AssertEqual(frame.Right, card.Right,
                        $"卡片右沿 {card.Right} 必须与按钮外框右沿 {frame.Right} 同一条竖线");
                    Battery.Evidence($"卡片 [{card.Left},{card.Right}] vs 按钮外框 [{frame.Left},{frame.Right}]，共用 SideInset={SourcePanel.SideInset}");
                }
                finally { HideOffscreen(hostForm); }
            }
            finally { TryDelete(dir); }
        });
    }

    /// <summary>
    /// 第五十六改②（用户图2红框）：导出对话框的格式下拉不许再露系统画的箭头。
    /// FlatStyle.Flat 的箭头按钮区域由系统在条目自绘<b>之后</b>画——OnDrawItem 盖不住它，
    /// 所以修法是 WndProc 拦 WM_PAINT、base 画完后补一笔自绘箭头。判据量补笔画没画：
    /// <c>ChromeComboBox.ArrowPaintCount</c> 在每次补笔后 +1，屏外 Show 一趟后必须大于 0
    /// （0 = 补笔通路没接上，用户看到的就还是那枚系统残箭）。探针会反过来砸这一格。
    /// </summary>
    private static void ExportComboArrowSelfDrawn()
    {
        RunOnStaThread(() =>
        {
            (Project project, MemoryResolver resolver, _) = TestProject.Create(StitchMode.Vertical,
                Fixture.OpaqueLike(60, 120, seed: 561), Fixture.OpaqueLike(60, 120, seed: 562));
            using var dialog = new ExportDialog(project, new CanvasComposer(resolver), new EncoderRegistry());
            ShowOffscreen(dialog);
            try
            {
                var combo = (Control?)typeof(ExportDialog).GetField("_format",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(dialog);
                Battery.Assert(combo is not null, "自证：应能从对话框取出格式下拉（字段名改了判据也要跟着改）");
                Battery.Assert(dialog.FormatComboArrowPaintCountForTest > 0,
                    $"下拉补笔必须真的发生过（ArrowPaintCount={dialog.FormatComboArrowPaintCountForTest}，" +
                    "0 = WM_PAINT 补笔通路没接上，箭头仍是系统画的）");
                int before = dialog.FormatComboArrowPaintCountForTest;
                combo!.Invalidate();
                combo.Update();
                Battery.Assert(dialog.FormatComboArrowPaintCountForTest > before,
                    "再逼一次重画，补笔计数必须继续上涨（证明这是每次重画都会补的常驻通路，不是启动时的一次性巧合）");
                Battery.Evidence($"ArrowPaintCount {before} → {dialog.FormatComboArrowPaintCountForTest}，补笔随每次 WM_PAINT 走");
            }
            finally { HideOffscreen(dialog); }
        });
    }

    /// <summary>
    /// 第五十六改②（用户图2蓝框）：「透明」勾与背景色卡的总规则，三个方向都钉死。
    /// ① PNG 档：勾可用且默认勾上（历史行为），勾上时色卡禁用——用户报的
    ///   「背景颜色的选择，无法点开」就是旧规则把 PNG 档色卡整个禁用；
    /// ② 勾掉透明：色卡启用，且选择写进文档（<c>ExportSettings.PngTransparent=false</c>）；
    /// ③ 切到 TIFF：勾禁用（只有 PNG 有透明档）、色卡照旧可用。
    /// 导出时"真的把透明像素压到所选底色"由 export 组的 PngOpaqueFlattenHonoursBackground 量。
    /// </summary>
    private static void TransparentCheckGatesBackgroundSwatch()
    {
        RunOnStaThread(() =>
        {
            (Project project, MemoryResolver resolver, _) = TestProject.Create(StitchMode.Vertical,
                Fixture.OpaqueLike(60, 120, seed: 571), Fixture.OpaqueLike(60, 120, seed: 572));
            using var dialog = new ExportDialog(project, new CanvasComposer(resolver), new EncoderRegistry());
            ShowOffscreen(dialog);
            try
            {
                // ① PNG 档默认态
                Battery.Assert(dialog.TransparentCheckEnabledForTest, "PNG 档「透明」勾应可用");
                Battery.Assert(dialog.TransparentCheckedForTest, "「透明」应默认勾上（历史行为：PNG 透明原样保留）");
                Battery.Assert(!dialog.BackgroundEnabledForTest,
                    "勾上透明时背景色卡必须禁用（用户原话：一旦选了，就不能设置背景色）");

                // ② 勾掉透明 → 色卡解禁 + 选择落进文档
                dialog.SetTransparentForTest(false);
                Battery.Assert(dialog.BackgroundEnabledForTest, "勾掉透明后背景色卡应解禁（可以点开了）");
                Battery.Assert(!project.Doc.Export.PngTransparent, "勾掉透明应写进文档（导出时才压底色）");
                dialog.SetTransparentForTest(true);
                Battery.Assert(!dialog.BackgroundEnabledForTest, "重新勾上后色卡应再次禁用（可逆）");

                // ③ 切 TIFF（EncoderRegistry 默认注册 PNG+TIFF，JPEG 在纯 Core 注册表里不可用）
                // ChromeComboBox 是 internal 类型，这里按 public 基类 ComboBox 出手。
                var combo = (ComboBox?)typeof(ExportDialog).GetField("_format",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(dialog);
                Battery.Assert(combo is not null, "自证：应能取出格式下拉");
                combo!.SelectedIndex = 2;   // PNG, JPEG, TIFF 的第三档
                Battery.Assert(!dialog.TransparentCheckEnabledForTest, "TIFF 档「透明」勾必须禁用（只有 PNG 有透明档）");
                Battery.Assert(dialog.BackgroundEnabledForTest, "TIFF 档背景色卡照旧可用（历史行为不变）");
                Battery.Evidence("PNG：勾可用/默认勾上/勾上锁色卡；勾掉解禁并落文档；TIFF：勾禁用、色卡照旧");
            }
            finally { HideOffscreen(dialog); }
        });
    }

    /// <summary>
    /// 第五十七改①（用户图1红框「右侧空白、文字全缩在左边」）：关于对话框的正文行
    /// 必须铺满内容宽、窗宽贴住内容、最宽那行真的排成一行。
    /// 旧病灶：AutoSize+MaximumSize 在真机 DPI 下提前折行，文字缩在左半、右半留白。
    /// 现在量三件事：① 每行宽＝内容宽；② 每行高≥WordBreak 现量的需高（不裁字）；
    /// ③ 最宽那行的高度＝单行高（没折行），且窗宽＝内容宽＋两侧页边（没有多余空白带）。
    /// </summary>
    private static void AboutLinesFillContentWidth()
    {
        RunOnStaThread(() =>
        {
            using var dialog = new AboutDialog();
            ShowOffscreen(dialog);
            try
            {
                var lines = dialog.BodyLinesForTest;
                Battery.Assert(lines.Count >= 5,
                    $"夹具自证：关于正文应量到至少 5 行（实际 {lines.Count}）——枚举空了「铺满」就是空话");
                int contentW = dialog.ContentWidthForTest;
                foreach (Label line in lines)
                {
                    Battery.AssertEqual(contentW, line.Width,
                        $"正文行「{line.Text}」宽应等于内容宽 {contentW}（不许缩在左边）");
                    int need = TextRenderer.MeasureText(line.Text, line.Font,
                        new Size(contentW, int.MaxValue),
                        TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height;
                    Battery.Assert(line.Height >= need,
                        $"正文行「{line.Text}」高 {line.Height} 应装得下需高 {need}（不许裁字）");
                }
                // 最宽那行必须真的排成一行：它的不折行宽 ≤ 内容宽（构造时已 +4 余量），
                // 所以它的行高应等于单行高——折成两行就是用户红框里那块空白的根因复发。
                Label widest = lines.OrderByDescending(l => TextRenderer.MeasureText(l.Text, l.Font,
                    new Size(int.MaxValue, int.MaxValue),
                    TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine).Width).First();
                int oneLineH = TextRenderer.MeasureText(widest.Text, widest.Font,
                    new Size(int.MaxValue, int.MaxValue),
                    TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine).Height;
                Battery.Assert(widest.Height <= oneLineH + 2,
                    $"最宽一行「{widest.Text}」应排成一行（行高 {widest.Height} vs 单行高 {oneLineH}）");
                Battery.AssertEqual(contentW + 40, dialog.ClientSize.Width,
                    "窗宽应＝内容宽＋两侧 20px 页边（右侧不许多出一条空白带）");
                Battery.Evidence($"关于 {lines.Count} 行正文全部宽 {contentW}px、高度够装；" +
                                 $"最宽行单行排布；窗宽 {dialog.ClientSize.Width}px 贴住内容");
            }
            finally { HideOffscreen(dialog); }
        });
    }

    /// <summary>
    /// 第五十七改②（用户图2红框「不…」被截断）：「主动缩小」下拉的收起格
    /// 必须装得下全部四个选项的整串。量两条：① 收起格文字盒（整宽−一枚箭头−8，
    /// 与 ChromeComboBox.OnDrawItem 同源）≥ 每个选项的不折行宽；② 逐档真选一遍，
    /// 收起格回读文字等于所选条目整串（走 SelectedIndex 真通路，不是量静态宽度）。
    /// </summary>
    private static void DownscaleComboFitsLongestOption()
    {
        RunOnStaThread(() =>
        {
            (Project project, MemoryResolver resolver, _) = TestProject.Create(StitchMode.Vertical,
                Fixture.OpaqueLike(60, 120, seed: 581), Fixture.OpaqueLike(60, 120, seed: 582));
            using var dialog = new ExportDialog(project, new CanvasComposer(resolver), new EncoderRegistry());
            ShowOffscreen(dialog);
            try
            {
                Control combo = dialog.DownscaleComboForTest;
                // 第五十七改②回修：现量宽度不许被 AutoScaleMode 二次缩放——这正是真窗口 150% DPI
                // 下「缩小到 1/4」截成「缩小到 1…」的根因。电池进程 DPI unaware 被虚拟化成 96 DPI，
                // 下面那条几何关系在 96 DPI 恒成立、量不出这枚病，所以必须把开关本身钉住：
                // 撤掉 AutoScaleMode.None 这一格就红（与 AboutDialog／SettingsDialog 同一条约定）。
                Battery.Assert(dialog.AutoScaleMode == AutoScaleMode.None,
                    "导出对话框必须关掉自动缩放：宽度全是现量设备像素，AutoScaleMode 会在高 DPI 二次缩放收起格致截断");
                int textW = combo.Width - combo.Height - 8;   // 与 OnDrawItem 的 editBox 盒同源
                foreach (string option in new[] { "不缩小", "缩小到 1/2", "缩小到 1/3", "缩小到 1/4" })
                {
                    int need = TextRenderer.MeasureText(option, combo.Font,
                        new Size(int.MaxValue, int.MaxValue),
                        TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine).Width;
                    Battery.Assert(textW >= need,
                        $"收起格文字盒 {textW}px 应装得下「{option}」的 {need}px（旧 130px 宽在 150% DPI 下截成「不…」）");
                }
                var box = (ComboBox)combo;
                Battery.AssertEqual(4, box.Items.Count, "夹具自证：缩小下拉应有 4 档");
                for (int i = 0; i < box.Items.Count; i++)
                {
                    box.SelectedIndex = i;
                    Battery.AssertEqual(box.Items[i]!.ToString(), box.Text,
                        $"选第 {i + 1} 档后收起格应显示整串（不许截断）");
                }
                Battery.Evidence($"下拉宽 {combo.Width}px、收起文字盒 {textW}px：四档整串全装下且逐档回读一致");
            }
            finally { HideOffscreen(dialog); }
        });
    }

    /// <summary>
    /// 第五十七改③（用户图3「颜色选择窗口太老、不适配整体UI」）：自绘取色器两条改色路
    /// 都必须通到同一份读数。① 预设路：点第 11 枚 → SelectedColor/hex/RGB 三框全跟上；
    /// ② RGB 路：走 ValueChanged 真通路改三框 → SelectedColor/hex 跟上。
    /// 另钉：32 枚预设两两不同、含纯白纯黑；确定按钮真交出 DialogResult.OK。
    /// 「替换掉系统 ColorDialog」这件事本身由源码门禁钉（产品里不再 new ColorDialog）。
    /// </summary>
    private static void ChromeColorDialogBothPathsAgree()
    {
        RunOnStaThread(() =>
        {
            using var dialog = new ChromeColorDialog(Color.FromArgb(18, 52, 86));
            ShowOffscreen(dialog);
            try
            {
                Battery.AssertEqual(32, dialog.PresetCountForTest, "预设应为 32 枚（8×4 站满）");
                var presets = dialog.PresetColorsForTest;
                Battery.AssertEqual(32, presets.Select(c => c.ToArgb()).Distinct().Count(),
                    "32 枚预设应两两不同（撞色等于少一枚可选）");
                Battery.Assert(presets.Any(c => c.ToArgb() == Color.White.ToArgb()) &&
                               presets.Any(c => c.ToArgb() == Color.Black.ToArgb()),
                    "预设应含纯白与纯黑（导出背景最高频的两枚）");

                // 初始色回读
                Battery.AssertEqual(Color.FromArgb(18, 52, 86).ToArgb(), dialog.SelectedColor.ToArgb(),
                    "初始色应原样回读");
                Battery.AssertEqual("#123456", dialog.HexTextForTest, "hex 读数应与初始色逐位一致");

                // ① 预设路（与真点击同一条处理器）
                dialog.SelectPresetForTest(11);
                Color expect = presets[11];
                Battery.AssertEqual(expect.ToArgb(), dialog.SelectedColor.ToArgb(),
                    "预设路：选中色应等于该枚预设");
                Battery.AssertEqual($"#{expect.R:X2}{expect.G:X2}{expect.B:X2}", dialog.HexTextForTest,
                    "预设路：hex 读数应跟上");
                var r = (NumericUpDown?)typeof(ChromeColorDialog).GetField("_r",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(dialog);
                var g = (NumericUpDown?)typeof(ChromeColorDialog).GetField("_g",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(dialog);
                var b = (NumericUpDown?)typeof(ChromeColorDialog).GetField("_b",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(dialog);
                Battery.Assert(r is not null && g is not null && b is not null,
                    "自证：应能取出 RGB 三框（字段名改了判据也要跟着改）");
                Battery.AssertEqual((int)expect.R, (int)r!.Value, "预设路：R 框应静音回填");
                Battery.AssertEqual((int)expect.G, (int)g!.Value, "预设路：G 框应静音回填");
                Battery.AssertEqual((int)expect.B, (int)b!.Value, "预设路：B 框应静音回填");

                // ② RGB 路（走 ValueChanged 真通路）
                dialog.SetRgbForTest(7, 119, 203);
                Battery.AssertEqual(Color.FromArgb(7, 119, 203).ToArgb(), dialog.SelectedColor.ToArgb(),
                    "RGB 路：选中色应跟上三框");
                Battery.AssertEqual("#0777CB", dialog.HexTextForTest, "RGB 路：hex 读数应跟上");

                // 确定按钮真点：交出 DialogResult.OK（颜色由 SelectedColor 带走）
                dialog.OkButtonForTest.PerformClick();
                Battery.AssertEqual(DialogResult.OK, dialog.DialogResult, "确定应交出 DialogResult.OK");
                Battery.Evidence("预设路（含三框回填）与 RGB 路都汇到 SelectedColor/hex；确定真交出 OK");
            }
            finally { HideOffscreen(dialog); }
        });
    }

    /// <summary>
    /// 第五十七改④（用户图4红框「文字和进度条重叠」）：导出进度行点亮之后，
    /// 定宽文字格／进度条／取消按钮各占各的矩形。旧病灶：进度文字写进定宽 148px 的
    /// 「开始导出」按钮，字溢出压到进度条。现在量四件事：
    /// ① 未导出时进度行隐藏；② 点亮后整棵可见叶子两两无正面积交集、全在客户区内
    /// （与 ExportDialogControlsNeverOverlap 同一把尺子）；③ 文字格宽装得下最宽阶段串；
    /// ④ 导出中按钮文字钉死「导出中…」、结束后收起并还原（进度不再骑在按钮上）。
    /// </summary>
    private static void ExportProgressRowNeverOverlaps()
    {
        RunOnStaThread(() =>
        {
            (Project project, MemoryResolver resolver, _) = TestProject.Create(StitchMode.Vertical,
                Fixture.OpaqueLike(60, 120, seed: 591), Fixture.OpaqueLike(60, 120, seed: 592));
            using var dialog = new ExportDialog(project, new CanvasComposer(resolver), new EncoderRegistry());
            ShowOffscreen(dialog);
            try
            {
                Battery.Assert(!dialog.ProgressLabelForTest.Visible, "未导出时进度行应隐藏（不占一行）");

                // 按真导出同一条接线点亮，给最宽阶段串
                dialog.BeginProgressUiForTest("正在写第 99/99 片", 100);
                Application.DoEvents();
                dialog.PerformLayout();

                Control label = dialog.ProgressLabelForTest;
                Control bar = dialog.ProgressBarForTest;
                Battery.Assert(label.Visible && bar.Visible, "点亮后文字格与进度条都应可见");

                // 整棵可见叶子两两无交集（含文字格×进度条×取消，也含它们与其余控件）
                var leaves = new List<(string Name, Rectangle Rect)>();
                void Collect(Control root)
                {
                    foreach (Control c in root.Controls)
                    {
                        if (!c.Visible || c.Width <= 0 || c.Height <= 0) continue;
                        if (c.Controls.Count == 0)
                            leaves.Add(($"{c.GetType().Name}「{c.Text}」",
                                        c.RectangleToScreen(new Rectangle(Point.Empty, c.Size))));
                        else
                            Collect(c);
                    }
                }
                Collect(dialog);
                Battery.Assert(leaves.Count >= 15,
                    $"夹具自证：进度行点亮后应量到至少 15 枚叶子（实际 {leaves.Count}）");
                Rectangle client = dialog.RectangleToScreen(new Rectangle(Point.Empty, dialog.ClientSize));
                var overlaps = new List<string>();
                for (int i = 0; i < leaves.Count; i++)
                    for (int j = i + 1; j < leaves.Count; j++)
                    {
                        Rectangle hit = Rectangle.Intersect(leaves[i].Rect, leaves[j].Rect);
                        if (hit.Width > 0 && hit.Height > 0)
                            overlaps.Add($"「{leaves[i].Name}」×「{leaves[j].Name}」重叠 {hit.Width}×{hit.Height}");
                    }
                Battery.AssertEqual("", string.Join("；", overlaps),
                    "进度行点亮后任何两枚控件不许有正面积交集（用户报的文字压进度条就是这一族）");
                var outside = leaves.Where(l => l.Rect.Left < client.Left || l.Rect.Top < client.Top ||
                                                l.Rect.Right > client.Right || l.Rect.Bottom > client.Bottom)
                                    .Select(l => l.Name).ToList();
                Battery.AssertEqual("", string.Join("；", outside),
                    "进度行点亮后任何控件不许画出客户区（窗高应跟着内容收口）");

                // 文字格宽装得下最宽阶段串（引擎阶段：正在写第 i/total 片）
                int need = TextRenderer.MeasureText("正在写第 999/999 片 100%", label.Font,
                    new Size(int.MaxValue, int.MaxValue),
                    TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine).Width;
                Battery.Assert(label.Width >= need,
                    $"进度文字格 {label.Width}px 应装得下最宽阶段串 {need}px");

                // 按钮不再承载进度
                var export = (Button?)typeof(ExportDialog).GetField("_export",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(dialog);
                Battery.Assert(export is not null, "自证：应能取出导出按钮");
                Battery.AssertEqual("导出中…", export!.Text, "导出中按钮文字应钉死（进度搬去独立行）");
                Battery.Assert(!export.Enabled, "导出中按钮应禁用");

                // 结束：收起进度行、还原文字
                dialog.EndProgressUiForTest();
                Battery.Assert(!label.Visible, "导出结束后进度行应收起");
                Battery.AssertEqual("开始导出", export.Text, "结束后按钮文字应还原");
                Battery.Evidence($"进度行点亮后 {leaves.Count} 枚叶子两两无交集、全在客户区内；" +
                                 $"文字格 {label.Width}px ≥ 最宽阶段串 {need}px；按钮文字不再承载进度");
            }
            finally { HideOffscreen(dialog); }
        });
    }

    /// <summary>按钮宽度装不装得下它的文字（顶栏裁字判据与它的反证共用这一把尺子）。</summary>
    private static bool TextFits(int width, string text, Font font) =>
        width >= TextRenderer.MeasureText(text, font, new Size(int.MaxValue, int.MaxValue),
            TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine).Width;

    /// <summary>点击目标达不达标（判据与它的反证共用这一把尺子）。</summary>
    private static bool MeetsTarget(Size s) =>
        s.Width >= Theme.MinTouchTarget && s.Height >= Theme.MinTouchTarget;

    // ═══════════════ 第五十九改②：低风险资源生命周期三件 ═══════════════

    /// <summary>
    /// 导入 N 张 PNG 全程只解码 N 次（第五十九改③）。
    ///
    /// 之前导入循环 <c>Decode(file)</c> 只取元数据就把整幅像素扔掉，画布首屏/缩略图/分析
    /// 各自 Resolve 时又把同一个文件从头解码——峰值翻倍。现在导入的像素经
    /// <c>FileSourceResolver.Prime</c> 移交缓存（单一所有权，LRU 预算内的可丢弃副本）。
    /// 取数是 <see cref="FileSourceResolver.DecodeCallsTotalForTest"/>（第五十九改②新开的
    /// 全量调用计数——旧的 DecodeRunsTotalForTest 只数 Resolve 那一路，导入直接调用的
    /// 那批根本不进账，这个问题它答不了）。
    /// 最后一半是正源核对：缓存里的像素必须与"重新解码一次"逐字节相同——
    /// 预热是搬像素，不是换像素，任何近似都会在这里现形。
    /// </summary>
    private static void ImportDecodesEachFileExactlyOnce()
    {
        RunOnStaThread(() =>
        {
            string dir = Path.Combine(Path.GetTempPath(), "ls-ui-" + Guid.NewGuid().ToString("N")[..6]);
            List<string> paths = WritePngFiles(dir, count: 3, width: 60, height: 120);
            try
            {
                long callsBefore = FileSourceResolver.DecodeCallsTotalForTest;
                var form = new MainForm();
                try
                {
                    form.CreateControl();
                    form.ProjectForTest.Doc.Mode = StitchMode.LongScroll;   // 夹具指定长截图：文档默认已是 Unset，自动分析只对 LongScroll 起（不写就到不了分析通路）
                    form.ImportPaths(paths.ToArray());
                    long decodeCalls = FileSourceResolver.DecodeCallsTotalForTest - callsBefore;
                    Battery.AssertEqual(3L, decodeCalls,
                        "三张图全程应只解码三次（导入各一次）——画布/缩略图/分析必须命中预热缓存，" +
                        $"实际解码 {decodeCalls} 次（>3 就是又回到了'一张图解两遍'的老路）");
                    Battery.AssertEqual(3, form.ResolverForTest.CachedCount,
                        "导入解码出的像素应全部移交进缓存（单一所有权），一张都不许就地丢弃");

                    var src = form.ProjectForTest.Doc.Sources[0];
                    RgbaImage viaCache = form.ResolverForTest.Resolve(src.Id);
                    DecodedImage fresh = FileSourceResolver.Decode(paths[0]);
                    Battery.Assert(viaCache.PixelsRo.SequenceEqual(fresh.Image.Pixels),
                        "缓存里的像素必须与重新解码逐字节相同——预热是搬像素不是换像素");
                    Battery.Evidence($"导入 3 张：解码调用 3 次、缓存驻留 3 张、缓存像素与重新解码逐字节同源");
                }
                finally { form.Dispose(); }
            }
            finally { TryDelete(dir); }
        });
    }

    /// <summary>
    /// 缓存预算装不下时的边界（第五十九改②回修）。
    ///
    /// 「导入 N 张 PNG 全程只解码 N 次（预热缓存与重新解码逐字节同源）」用的是 3 张小图，
    /// 全部装得进 512MB 默认预算——它只能证明"未被淘汰的像素能复用"，答不了"图多到装不下会怎样"。
    /// 本格把预算压到只装 2 张（<c>FileSourceResolver.CacheLimitBytes</c> 是产品自己开放的旋钮，
    /// 不靠改产品源码），3 张真 PNG 各解码一次后 Prime 进缓存：最旧那张必须被 LRU 淘汰；
    /// 随后按"最新→最旧"取数，驻留的两张命中不再解码，被淘汰的那张按需重新解码一次——
    /// 这是 LRU 的正常行为，不是像素错误，也不该靠无限扩大缓存去"消灭"它；
    /// 重新解码出来的像素必须与首次解码逐字节相同。探针把 <c>Trim()</c> 从 Prime 里摘掉就能让本格红。
    /// </summary>
    private static void CacheBudgetEvictsLruAndRedecodesOnDemand()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ls-evict-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(dir);
        try
        {
            const int W = 60, H = 120, N = 3, BudgetFit = 2;
            long bytesPerImage = (long)W * H * 4;
            List<string> paths = WritePngFiles(dir, count: N, width: W, height: H);
            var resolver = new FileSourceResolver { CacheLimitBytes = bytesPerImage * BudgetFit };
            var ids = new string[N];
            var originals = new RgbaImage[N];
            long callsBefore = FileSourceResolver.DecodeCallsTotalForTest;

            // 与导入通路同形：每个文件 Decode 一次，像素经 Prime 移交进缓存（所有权移交语义）。
            for (int i = 0; i < N; i++)
            {
                DecodedImage decoded = FileSourceResolver.Decode(paths[i]);
                originals[i] = decoded.Image;
                var src = new SourceImage
                {
                    Id = Project.NewId("src"), Path = paths[i], FileName = Path.GetFileName(paths[i]),
                    Size = new IntSize(W, H), Format = "png",
                };
                resolver.Register(src);
                ids[i] = src.Id;
                resolver.Prime(src.Id, decoded.Image);
            }

            long decodeCalls = FileSourceResolver.DecodeCallsTotalForTest - callsBefore;
            Battery.AssertEqual((long)N, decodeCalls,
                "装料阶段每个文件只许解码一次（Prime 只搬像素，不该再触发解码）");
            Battery.AssertEqual(BudgetFit, resolver.CachedCount,
                $"预算只装得下 {BudgetFit} 张时驻留必须正好 {BudgetFit} 张——实际 {resolver.CachedCount} 张" +
                "（多 = 淘汰通路没跑，少 = 把装得下的也丢了）");
            Battery.Assert(!resolver.CachedIdsNewestFirst.Contains(ids[0]),
                "最旧那张必须已被 LRU 淘汰出驻留名单（淘汰了最旧的才叫 LRU）");
            Battery.Assert(resolver.CachedBytes <= resolver.CacheLimitBytes,
                "驻留字节数不许越过预算线");

            // 按"最新→最旧"取数：驻留的两张命中（解码 +0），被淘汰的那张按需重新解码（+1）。
            resolver.Resolve(ids[2]);
            resolver.Resolve(ids[1]);
            long beforeEvicted = FileSourceResolver.DecodeCallsTotalForTest;
            RgbaImage redecoded = resolver.Resolve(ids[0]);
            Battery.AssertEqual(1L, FileSourceResolver.DecodeCallsTotalForTest - beforeEvicted,
                "只有被淘汰的那张该重新解码：驻留两张命中（+0），淘汰一张按需重解（+1）");
            Battery.Assert(redecoded.PixelsRo.SequenceEqual(originals[0].PixelsRo),
                "被淘汰的图重新解码出的像素必须与首次解码逐字节相同——LRU 丢的是副本，不是保真度");
            Battery.Evidence($"预算 {resolver.CacheLimitBytes}B 只装 {BudgetFit} 张：Prime {N} 张驻留 {BudgetFit} 张、" +
                             "最新→最旧取数只补解码被淘汰的 1 张、像素逐字节同源");
        }
        finally { TryDelete(dir); }
    }

    /// <summary>
    /// 机制格（第五十九改③）：释放钩子必须<b>真的把死掉的大对象收走</b>，并把触发次数记下来。
    ///
    /// 真窗口的账：缓存驻留只有 542MB，托管堆却 1.53GB——差额约 1GB 是匹配临时副本与
    /// 解码中间缓冲这类<b>已经不可达却没人催收</b>的 LOH 大对象（gen2 才 36 次）。
    /// 「操作确认完毕后释放内存」这条用户要求落成 <c>MemorySnapshot.ReleaseAfterOperation</c>，
    /// 本格证明它不是空转：造一枚 ≥85KB（真落 LOH）的死对象，先自证"它此刻还活着"
    /// （被并发回收抢先就重试创建，连试 10 次都不活说明环境吵到量的是空气，宁可红），
    /// 然后调一次钩子。判据是<b>两把一起</b>：<c>GC.CollectionCount(2)</c> 必须前进
    /// （钩子的全额阻塞回收是它唯一的来路——摘掉 <c>GC.Collect</c> 那一行就红），
    /// 且死对象要在这趟回收里被收走。等待窗只有 3 秒：第一版给了 10 秒，
    /// 环境的自然回收在这窗口里把探针假绿救活过一次（判据没牙），收窄到 3 秒后
    /// 只有真跑了 <c>GC.Collect</c> 才绿。计数必须恰好 +1——漏计=判据看不到接线，多计=钩子重复触发。
    /// </summary>
    private static void ReleaseAfterOperationCollectsDeadObjects()
    {
        int before = MemorySnapshot.ReleaseCountForTest;
        GC.Collect();                       // 先把环境噪音回收排空，让 gen2 计数落定
        int gen2Before = GC.CollectionCount(2);
        WeakReference wr = CreateDeadLargeObject();
        int tries = 0;
        while (!wr.IsAlive && tries < 10) { wr = CreateDeadLargeObject(); tries++; }
        Battery.Assert(wr.IsAlive,
            "自证：8MB 大对象此刻必须还活着（连试 10 次都被并发回收抢先就是环境太吵，量的是空气）");
        MemorySnapshot.ReleaseAfterOperation("battery");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        bool collected = false;
        while (sw.ElapsedMilliseconds < 3_000)
        {
            if (!wr.IsAlive || GC.CollectionCount(2) > gen2Before) { collected = true; break; }
            System.Threading.Thread.Sleep(10);
        }
        Battery.Assert(collected,
            $"释放钩子必须在 3 秒内跑完那次全额回收（等了 {sw.ElapsedMilliseconds}ms）——" +
            "死对象没收走且 gen2 计数没前进，说明 GC.Collect 根本没执行，钩子就是空转");
        Battery.AssertEqual(before + 1, MemorySnapshot.ReleaseCountForTest,
            "每调一次释放钩子计数必须恰好 +1（漏计=判据看不到接线，多计=钩子重复触发）");
        Battery.Evidence($"8MB 死对象在 {sw.ElapsedMilliseconds}ms 内被收走（gen2 {gen2Before}→{GC.CollectionCount(2)}），" +
                         $"释放计数 {before}→{before + 1}");
    }

    private static WeakReference CreateDeadLargeObject()
    {
        var big = new byte[8 * 1024 * 1024];   // ≥85KB ⇒ 直接落大对象堆
        big[0] = 0xAB;                          // 写一笔防 JIT 把没被读的分配优化掉
        return new WeakReference(big);
    }

    /// <summary>
    /// 接线格（第五十九改③）：批量分析确认写回后必须触发一次释放回收。
    ///
    /// 用户口径是「把某个需要检查的确认完毕后，应该释放部分内存，而不是继续增大」——
    /// 批量分析（导入 ≥2 张自动起的那趟）正是最大的临时副本制造者（matchTemporaryBytesTotal
    /// 累计 2.41GB 的来路）。本格走真通路：离屏窗体导入 2 张真 PNG → 自动起 <c>AnalyzeAll</c>
    /// → 写回回调末尾的 <c>ReleaseAfterOperation("analyze_all")</c> 必须把计数推上去。
    /// <see cref="RunOnStaThread"/> 的 STA 线程<b>不泵消息</b>，BeginInvoke 回调必须靠
    /// <c>DoEvents</c> 喂进去——只睡不泵的话永远等不到。探针摘掉接线调用那一行本格就红
    /// （判据名与 MemorySnapshot 注释里引用的全名逐字一致，doc_cell_check 会核对）。
    /// </summary>
    private static void AnalysisCompletionTriggersRelease()
    {
        RunOnStaThread(() =>
        {
            string dir = Path.Combine(Path.GetTempPath(), "ls-ui-" + Guid.NewGuid().ToString("N")[..6]);
            List<string> paths = WritePngFiles(dir, count: 2, width: 60, height: 120);
            try
            {
                var form = new MainForm();
                try
                {
                    form.CreateControl();
                    // CreateControl 只造"可见子控件"的树，窗体不可见时句柄未必建出来；
                    // 而分析写回从后台线程 BeginInvoke，句柄不存在就抛"cannot be called on
                    // a control until the window handle has been created"（本格第一趟红在这）。
                    // 访问 Handle 属性是强制创建句柄的唯一公开出口。
                    _ = form.Handle;
                    form.ProjectForTest.Doc.Mode = StitchMode.LongScroll;   // 夹具指定长截图：文档默认已是 Unset，自动分析只对 LongScroll 起（不写就到不了分析通路）
                    int before = MemorySnapshot.ReleaseCountForTest;
                    form.ImportPaths(paths.ToArray());
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    while (MemorySnapshot.ReleaseCountForTest == before && sw.ElapsedMilliseconds < 15_000)
                    {
                        Application.DoEvents();
                        System.Threading.Thread.Sleep(10);
                    }
                    Battery.Assert(MemorySnapshot.ReleaseCountForTest > before,
                        $"批量分析确认写回后必须触发一次释放回收（等了 {sw.ElapsedMilliseconds}ms）——" +
                        "15 秒没等到就是接线断了，或分析根本没跑完");
                    Battery.Evidence($"导入 2 张自动分析：释放计数 {before}→{MemorySnapshot.ReleaseCountForTest}" +
                                     $"（{sw.ElapsedMilliseconds}ms 内）");
                }
                finally { form.Dispose(); }
            }
            finally { TryDelete(dir); }
        });
    }

    /// <summary>
    /// 新建项目后源图缓存必须清零（第五十九改②）。
    ///
    /// NewProject 只换文档不动 Resolver 的话，旧项目的像素还占着 512MB LRU 预算——
    /// 界面已经空白，旧图却驻留到被淘汰为止。打开项目那条路早就 Invalidate 了，
    /// 新建漏掉是盘点查出的第二处泄漏形状。
    /// 只导 1 张（count:1）：两张以上会自动起后台分析，分析线程可能在清空后
    /// 又把缓存重新填上，判据就被并发搅成假红——本格量的是"清没清"，不给并发留缝。
    /// </summary>
    private static void NewProjectClearsSourceCache()
    {
        RunOnStaThread(() =>
        {
            (MainForm form, string dir) = FormWithImports(count: 1);
            try
            {
                Battery.AssertEqual(1, form.ResolverForTest.CachedCount, "导入后缓存应驻留一张");
                // 先把 IsDirty 按掉：NewProject 会弹"未保存改动"确认框，测试不能挂在那儿等真人
                form.ProjectForTest.MarkSaved();
                var newProject = typeof(MainForm).GetMethod("NewProject",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                Battery.Assert(newProject is not null, "自证：MainForm.NewProject 必须还在（改名要同步本判据）");
                newProject!.Invoke(form, null);
                Battery.AssertEqual(0, form.ResolverForTest.CachedCount,
                    "新建项目顶掉旧文档后，旧项目的源图像素必须当场清出缓存，不许等 LRU 淘汰");
                Battery.AssertEqual(0L, form.ResolverForTest.CachedBytes,
                    "缓存字节数也要归零（张数为 0 而字节没归零就是另一处半套账）");
                Battery.AssertEqual(0, form.ProjectForTest.Doc.Sources.Count, "新项目文档应为空");
                Battery.Evidence("导入 1 张（缓存 1 张）→ MarkSaved → NewProject → 缓存 0 张 0 字节、文档 0 段");
            }
            finally { form.Dispose(); TryDelete(dir); }
        });
    }

    /// <summary>
    /// 素材列表刷新与销毁时旧缩略图必须被显式释放（第五十九改②）。
    ///
    /// 缩略图是 GDI 位图（非托管资源），之前整列重建（<c>Items.Clear</c>）和面板销毁
    /// 都直接扔下旧 Row，位图只能等 GC 终结器——连续刷新会反复造缩略图、反复压终结队列。
    /// 判据走真身绘制通路（<see cref="SourcePanel.DrawItemForTest"/>，缩略图的唯一生成入口
    /// 在 OnDrawItem 里）把真缩略图造出来，再用真刷新（Project.Mutate → Changed）触发释放，
    /// 最后核对面板 Dispose 也接住了剩余的。
    /// </summary>
    private static void ThumbnailDisposalIsDeterministic()
    {
        RunOnStaThread(() =>
        {
            string dir = Path.Combine(Path.GetTempPath(), "ls-ui-" + Guid.NewGuid().ToString("N")[..6]);
            List<string> paths = WritePngFiles(dir, count: 2, width: 60, height: 120);
            try
            {
                var resolver = new FileSourceResolver();
                var project = new Project(new ProjectDocument { Mode = StitchMode.Vertical });
                foreach (string file in paths)
                {
                    DecodedImage decoded = FileSourceResolver.Decode(file);
                    var src = new SourceImage
                    {
                        Id = Project.NewId("src"),
                        Path = file,
                        FileName = Path.GetFileName(file),
                        Size = decoded.Image.Size,
                    };
                    resolver.Register(src);
                    resolver.Prime(src.Id, decoded.Image);
                    project.Doc.Sources.Add(src);
                    project.Doc.Segments.Add(new Segment
                    {
                        Id = Project.NewId("seg"),
                        SourceId = src.Id,
                        SourceRect = new IntRect(0, 0, decoded.Image.Width, decoded.Image.Height),
                    });
                }

                var panel = new SourcePanel(project, resolver);
                panel.CreateControl();
                Battery.AssertEqual(2, panel.ListItemsCountForTest, "自证：两段应有两行");

                // 用真绘制通路把两枚真缩略图造出来（懒生成、挂在 Row 上）
                using (var bmp = new Bitmap(320, 400))
                using (var g = Graphics.FromImage(bmp))
                {
                    for (int i = 0; i < 2; i++)
                    {
                        var e = new DrawItemEventArgs(g, panel.Font,
                            new Rectangle(0, i * 64, 320, 64), i, DrawItemState.Default);
                        panel.DrawItemForTest(e);
                    }
                }
                Battery.AssertEqual(2, panel.ThumbnailCountForTest, "自证：真绘制后两行应各挂一枚缩略图");

                long disposedBefore = panel.ThumbsDisposedForTest;
                // 真刷新：Mutate → Changed → RefreshItems 整列重建，旧缩略图必须当场 Dispose
                project.Mutate("触发列表重建", _ => { });
                Battery.AssertEqual(2L, panel.ThumbsDisposedForTest - disposedBefore,
                    "整列重建前旧缩略图必须被逐张显式 Dispose（不许扔给终结器）");
                Battery.AssertEqual(0, panel.ThumbnailCountForTest,
                    "重建后行上不许还挂着已 Dispose 的位图（后续绘制摸到死位图会炸）");

                // 再造一轮，然后核对面板 Dispose 也接住了剩余的
                using (var bmp = new Bitmap(320, 400))
                using (var g = Graphics.FromImage(bmp))
                {
                    for (int i = 0; i < 2; i++)
                    {
                        var e = new DrawItemEventArgs(g, panel.Font,
                            new Rectangle(0, i * 64, 320, 64), i, DrawItemState.Default);
                        panel.DrawItemForTest(e);
                    }
                }
                Battery.AssertEqual(2, panel.ThumbnailCountForTest, "自证：重建后重新绘制应再造出两枚");
                long disposedAtDispose = panel.ThumbsDisposedForTest;
                panel.Dispose();
                Battery.AssertEqual(2L, panel.ThumbsDisposedForTest - disposedAtDispose,
                    "面板销毁必须把还挂在行上的缩略图显式接住，不许留给终结器");
                Battery.Evidence("真绘制造 2 枚 → 重建时 Dispose 2 枚、行上归零 → 再造 2 枚 → 面板 Dispose 再接住 2 枚");
            }
            finally { TryDelete(dir); }
        });
    }
}
