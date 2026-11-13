using System.Text;
using LongStitch.Core;
using LongStitch.Core.Model;
using LongStitch.Core.ProjectIO;
using LongStitch.App.Diagnostics;
using LongStitch.App.Imaging;

namespace LongStitch.App.UI;

/// <summary>
/// 主窗口的"动作"部分：顶栏、模式切换、项目新建/打开/保存、导出、关于。
/// 与 MainForm.cs 分成两个 partial 文件，只是因为一个类同时装布局与业务动作会太长。
/// </summary>
public sealed partial class MainForm
{
    /// <summary>
    /// 顶栏的内容（第五十改：不再自己建一条 ToolStrip）。
    ///
    /// ═══ 为什么改成往菜单里塞 ═══
    /// 需求 §2 的线框把「菜单 / 三模式 / 项目名 / 导出」画在<b>同一行</b>。
    /// 原来这里 new 了一条 Dock=Top 的 ToolStrip，于是屏上是两条同色灰带叠着：
    /// 上面一条只有四个菜单词、下面一条只有三个模式词加一枚导出，两条都没有层级，
    /// 白白吃掉 55px 的画布高度。现在菜单那一条（<see cref="BuildMenu"/>）钉成 52px 高，
    /// 这里只往它的 Items 里加三样东西：模式分段、项目名、导出按钮。
    /// 停靠控件总数因此从 7 回到 6，<c>ui/主窗口可构造并完成布局</c> 量的那几条形不变。
    ///
    /// ═══ 右对齐的顺序 ═══
    /// ToolStrip 的右对齐项是「先加入的占最右」。所以先加导出、再加项目名，
    /// 屏上从左到右才是「项目名 → 导出」，与线框一致。
    ///
    /// ═══ 顶栏这里为什么仍然没有「添加图片」═══
    /// 左侧面板已经有一枚同名按钮，模拟UI需求 §3.1 的首屏线框点的也是左侧那一枚。
    /// 同一个动作在界面上出现两次，用户会先点错一个、再怀疑另一个是不是干别的用的。
    /// 添加的三个出口保持：左侧按钮 / 菜单「添加图片…」(Ctrl+O) / 直接拖文件进窗口。
    /// 判据见 <c>ui/添加图片在界面上只有一枚按钮</c>。
    /// </summary>
    private void BuildToolbar()
    {
        MenuStrip menu = MainMenuStrip ??
                         throw new InvalidOperationException("顶栏内容要塞进菜单栏，但菜单栏还没建（构造顺序被改反了）");

        // ── 三模式分段控件（需求 §3.3 的"顶栏三模式分段"）──
        // 原来这里是三枚各自带勾选框的 ToolStripButton：屏上读作三个松散的词，
        // 而且「选中」靠系统那圈凹陷方框表达，在深色底上几乎看不见。
        // 现在是一条凹槽里三格，选中格抬起并带一道强调色底条（见 SegmentedControl）。
        _modeSegment = new SegmentedControl("长截图", "竖拼", "横拼")
        {
            ToolTipText = "长截图：连续滚动截图自动找重叠去重；竖拼／横拼：独立图片首尾相接，不自动去重",
        };
        _modeSegment.SelectionChanged += index =>
        {
            SwitchMode((StitchMode)index);
            // SwitchMode 在有手动拼缝时会弹框，用户可能选「取消」——那时文档模式没变，
            // 分段必须退回原来那一格，否则屏上选中的档位与实际拼合方向不一致。
            // SelectedIndex 的 setter 不发事件（见 SegmentedControl），所以这里不会绕回来重入。
            UpdateModeButtons();
        };
        menu.Items.Add(new ToolStripControlHost(_modeSegment)
        {
            AutoSize = false,
            Size = new Size(_modeSegment.Width, _modeSegment.Height),
            // 控件 36px + 上下各 8 = 52，正好顶满整条带（与右侧导出按钮同一档垂直余量）。
            Margin = new Padding(14, 14, 0, 14),
        });
        menu.Items.Add(new ToolStripSeparator { Margin = new Padding(10, 0, 6, 0) });

        // 宽度先量后摆：写死 104 在 150% DPI 下只装得下五个字，屏上读出来是「部重新分析」
        // （左边那个「全」被裁掉了）。字号/DPI 一变就复发的缺陷，一律改成按文字现量。
        int analyzeWide = TextRenderer.MeasureText("全部重新分析", Theme.UiFont,
            new Size(int.MaxValue, int.MaxValue),
            TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine).Width;
        ToolStripButton analyze = new("全部重新分析")
        {
            ToolTipText = "重算所有拼缝；已有的手动偏移不会被抹掉（编辑菜单里也有这一条）",
            Font = Theme.UiFont,
            DisplayStyle = ToolStripItemDisplayStyle.Text,
            AutoSize = false,
            Size = new Size(analyzeWide + 24, 34),
            Margin = new Padding(0, 9, 0, 9),
        };
        analyze.Click += (_, _) => AnalyzeAll();
        menu.Items.Add(analyze);

        // ── 右半：导出（主操作）+ 项目名与未保存标记 ──
        _exportButton = new ChromeButton
        {
            Text = "导出",
            Glyph = ChromeGlyph.Save,
            Kind = ChromeButtonKind.Primary,
            Size = new Size(96, 36),
            ToolTipText = "查看最终像素、预估大小与限制提示后导出（Ctrl+E）",
        };
        _exportButton.Click += (_, _) => ExportNow();
        menu.Items.Add(new ToolStripControlHost(_exportButton)
        {
            Alignment = ToolStripItemAlignment.Right,
            AutoSize = false,
            Size = new Size(96, 36),
            Margin = new Padding(0, 8, 0, 8),
        });

        // 项目名原来只活在窗口标题栏里（系统画的那一行字很小，而且被文件名挤到后半段）。
        // 槽位固定宽度、右对齐：标题长短变化不许把导出按钮推来推去。
        _projectTitle = new ToolStripLabel("未命名项目")
        {
            Alignment = ToolStripItemAlignment.Right,
            Font = Theme.UiFontSmall,
            ForeColor = Theme.TextSecondary,
            AutoSize = false,
            Size = new Size(160, Chrome.TopBarHeight),
            TextAlign = ContentAlignment.MiddleRight,
            Margin = new Padding(0, 0, 12, 0),
        };
        menu.Items.Add(_projectTitle);
    }

    /// <summary>
    /// 切换模式。已有手动拼缝参数怎么处理必须问用户——
    /// 长截图的"重叠位移"搬到竖拼里语义完全不同，静默保留会拼出一堆奇怪的重叠。
    /// （需求验收场景 6）
    /// </summary>
    private void SwitchMode(StitchMode mode)
    {
        if (_project.Doc.Mode == mode) return;
        int manual = _project.Doc.Seams.Count(s => s.IsManual);

        if (manual > 0)
        {
            var answer = MessageBox.Show(this,
                $"切换到「{ModeName(mode)}」。\n\n" +
                $"当前有 {manual} 条拼缝是手动设定的。位移的单位是源图像素，" +
                "换排列方向后这些数值多半不再成立。\n\n" +
                "· 保留：数值原样带过去（可能明显不对，但可撤销）\n" +
                "· 清除：把全部拼缝恢复成自动建议或首尾相接\n" +
                "· 取消：不切换",
                "切换模式", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2);

            if (answer == DialogResult.Cancel) return;
            if (answer == DialogResult.No)
                _project.Mutate("清除手动拼缝", d =>
                {
                    foreach (Seam seam in d.Seams)
                    {
                        seam.ManualOffset = null;
                        seam.Status = seam.AutoStatus;
                        seam.Cause = SeamCause.None;
                        seam.Confirmed = false;
                    }
                });
        }

        _project.Mutate("切换模式", d => d.Mode = mode);
        // 换模式会改变"哪根轴能动"，必须把这件事同时写进日志和状态栏：
        // 用户从竖拼切到长截图之后左右突然动不了，不说一句就像软件坏了。
        // 参数里已有的横向手动值保持原样（只是不再生效），切回竖拼就恢复。
        bool locksX = StitchAxes.LocksHorizontal(mode);
        DiagnosticLog.Info("project.mode_changed",
            $"mode={mode} locks_horizontal={locksX} manual_seams={manual} revision={_project.Revision}");
        Status(locksX
            ? $"已切换到「{ModeName(mode)}」：横向位移已锁定，拼缝只能上下移动（X 恒为 0）"
            : $"已切换到「{ModeName(mode)}」");
    }

    private static string ModeName(StitchMode mode) => mode switch
    {
        StitchMode.LongScroll => "长截图",
        StitchMode.Vertical => "竖拼",
        StitchMode.Horizontal => "横拼",
        _ => mode.ToString(),
    };

    // ───────────────────────── 项目 ─────────────────────────

    private void NewProject()
    {
        if (!ConfirmDiscardIfDirty()) return;
        _projectPath = null;
        _project.ReplaceDocument(new ProjectDocument { Mode = _project.Doc.Mode }, null);
        // 第五十九改②：旧文档被顶掉后，它的源图像素对任何人都不可达了，
        // 但还占着 Resolver 的 LRU 预算（上限 512MB）——新项目已经空白的界面上，
        // 旧图片的缓存不该继续驻留。打开项目那一条路（OpenProjectFrom）早就
        // Invalidate 了，新建漏掉是盘点查出的第二处泄漏形状。
        _resolver.Invalidate();
        DiagnosticLog.Info("project.new", $"revision={_project.Revision}");
        // "关闭项目"在本产品里的唯一形状就是新建/打开顶掉当前文档（第五十九改内存快照按它记账）。
        Diagnostics.MemorySnapshot.Log("project_close");
        _canvas.SelectSeam(null);
        Status("新项目");
    }

    private void OpenProject()
    {
        if (!ConfirmDiscardIfDirty()) return;
        using var dialog = new OpenFileDialog
        {
            Title = "打开项目",
            Filter = $"LongStitch 项目|*{ProjectFile.Extension}|所有文件|*.*",
        };
        if (dialog.ShowDialog(this) == DialogResult.OK) OpenProjectFrom(dialog.FileName);
    }

    private void OpenProjectFrom(string path)
    {
        DiagnosticLog.Info("project.open_begin", $"path={path}");
        try
        {
            ProjectDocument doc = ProjectFile.Load(path);
            _project.ReplaceDocument(doc, path);
            _projectPath = path;
            RecentProjects.Add(path);

            foreach (SourceImage src in doc.Sources) _resolver.Register(src);
            _resolver.Invalidate();

            int missing = doc.Sources.Count(s => s.SourceMissing);
            DiagnosticLog.Info("project.open_done", $"path={path} sources={doc.Sources.Count} segments={doc.Segments.Count} missing={missing} revision={_project.Revision}");
            Diagnostics.MemorySnapshot.Log("project_open");
            _canvas.SelectSeam(null);
            Status(missing > 0
                ? $"已打开项目，但有 {missing} 张源文件找不到——请在左侧列表右键重新定位（导出不会用低清预览顶替）"
                : "已打开项目");
            if (missing > 0) WarnMissingSources(doc);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Error("project.open_failed", ex, $"path={path}");
            MessageBox.Show(this, "打不开这个项目文件：\n" + ex.Message + "\n\n你的项目文件没有被改动。",
                "打开项目", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void WarnMissingSources(ProjectDocument doc)
    {
        var missing = doc.Sources.Where(s => s.SourceMissing).ToList();
        var lines = missing.Select(s => $"· {s.FileName} ← {s.Path}");
        MessageBox.Show(this,
            "以下源文件找不到，对应片段暂时按空处理：\n\n" +
            string.Join("\n", lines.Take(15)) +
            (missing.Count > 15 ? $"\n… 还有 {missing.Count - 15} 项" : "") +
            "\n\n把文件放回原位后重新打开项目即可。导出时不会用低清预览替代原图。",
            "源文件丢失", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    /// <summary>保存项目。只写参数，不嵌原图（需求 §9）。</summary>
    private bool SaveProject(bool saveAs)
    {
        string? path = _projectPath;
        if (saveAs || path is null)
        {
            using var dialog = new SaveFileDialog
            {
                Title = "保存项目",
                Filter = $"LongStitch 项目|*{ProjectFile.Extension}",
                FileName = (_project.Doc.Title == "未命名项目" ? "长截图项目" : _project.Doc.Title) + ProjectFile.Extension,
            };
            if (dialog.ShowDialog(this) != DialogResult.OK) return false;
            path = dialog.FileName;
        }

        try
        {
            ProjectFile.Save(_project.Doc, path);
            DiagnosticLog.Info("project.save_done", $"path={path} sources={_project.Doc.Sources.Count} segments={_project.Doc.Segments.Count} revision={_project.Revision}");
            _projectPath = path;
            _project.MarkSaved(path);
            RecentProjects.Add(path);
            Status($"项目已保存：{Path.GetFileName(path)}（只存参数，原图未被改动）");
            return true;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Error("project.save_failed", ex, $"path={path}");
            MessageBox.Show(this, "保存失败：" + ex.Message, "保存项目",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
    }

    private bool ConfirmDiscardIfDirty()
    {
        if (!_project.IsDirty) return true;
        var answer = MessageBox.Show(this,
            "当前项目有未保存的改动。\n\n保存后继续？\n（" +
            "「否」= 丢弃改动继续，「取消」= 留在当前项目）",
            "未保存的改动", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        return answer switch
        {
            DialogResult.Yes => SaveProject(_projectPath is null),
            DialogResult.No => true,
            _ => false,
        };
    }

    private void OnFormClosingCheckSave(object? sender, FormClosingEventArgs e)
    {
        _analysisCts?.Cancel();
        if (!ConfirmDiscardIfDirty()) e.Cancel = true;
    }

    // ───────────────────────── 导出 ─────────────────────────

    private void ExportNow()
    {
        if (_project.Layout.IsEmpty)
        {
            Status("还没有可导出的内容");
            return;
        }

        using var dialog = new ExportDialog(_project, _composer, _encoders, _resolver);
        dialog.ShowDialog(this);
    }

    // ───────────────────────── 打包项目 ─────────────────────────

    /// <summary>
    /// 打包项目：把参数与全部原图写进一个 .lstickx（zip）。
    /// 这是用户显式点的一次复制，程序不会在任何别的时机偷偷做（产品需求 §9）。
    /// </summary>
    private void SavePackage()
    {
        if (_project.Doc.Sources.Count == 0)
        {
            Status("项目里还没有源图，无需打包——直接「保存项目」只存参数即可");
            return;
        }

        long totalBytes = _project.Doc.Sources
            .Where(s => File.Exists(s.Path))
            .Sum(s => { try { return new FileInfo(s.Path).Length; } catch { return 0; } });
        var answer = MessageBox.Show(this,
            $"打包会把 {_project.Doc.Sources.Count} 张原图（约 {totalBytes / 1024 / 1024} MB）" +
            "复制进一个 .lstickx 容器。\n\n" +
            "截图通常含私密内容，复制出来的容器同样私密——请确认存放位置是你自己可控的目录。\n\n" +
            "（普通「保存项目」只记录文件路径，不会复制任何原图。）\n\n要继续打包吗？",
            "打包项目（含原图）", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (answer != DialogResult.Yes) return;

        using var dialog = new SaveFileDialog
        {
            Title = "打包项目",
            Filter = $"LongStitch 打包项目|*{ProjectPackage.Extension}",
            FileName = (_project.Doc.Title == "未命名项目" ? "长截图打包" : _project.Doc.Title) + ProjectPackage.Extension,
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            UseWaitCursor = true;
            ProjectPackage.PackReport report = ProjectPackage.Save(_project.Doc, dialog.FileName);
            DiagnosticLog.Info("package.save_done", $"path={dialog.FileName} packed={report.PackedSources} skipped={report.Skipped.Count} bytes={report.TotalBytes}");
            string tail = report.Skipped.Count == 0
                ? ""
                : $"\n\n{report.Skipped.Count} 张没能打进去：\n· " +
                  string.Join("\n· ", report.Skipped.Take(10));
            MessageBox.Show(this,
                $"已写入 {dialog.FileName}\n\n打进 {report.PackedSources} 张原图，" +
                $"容器 {report.TotalBytes / 1024 / 1024} MB。{tail}\n\n" +
                "原图本体没有被改动，项目里的路径引用也保持不变。",
                "打包完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
            RecentProjects.Add(dialog.FileName);
            Status($"打包完成：{report.PackedSources} 张原图，" +
                   $"{report.TotalBytes / 1024 / 1024} MB" +
                   (report.Skipped.Count > 0 ? $"，{report.Skipped.Count} 张跳过" : ""));
        }
        catch (Exception ex)
        {
            DiagnosticLog.Error("package.save_failed", ex);
            MessageBox.Show(this, "打包失败：" + ex.Message + "\n\n原图与项目参数都没有被改动。",
                "打包项目", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally { UseWaitCursor = false; }
    }

    /// <summary>打开打包项目：解到容器旁的 &lt;名称&gt;.unpacked/，路径全部指向解包出来的文件。</summary>
    private void OpenPackage()
    {
        if (!ConfirmDiscardIfDirty()) return;
        using var dialog = new OpenFileDialog
        {
            Title = "打开打包项目",
            Filter = $"LongStitch 打包项目|*{ProjectPackage.Extension}|所有文件|*.*",
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        OpenPackageFrom(dialog.FileName);
    }

    private void OpenPackageFrom(string path)
    {
        DiagnosticLog.Info("package.open_begin", $"path={path}");
        try
        {
            UseWaitCursor = true;
            string extract = ProjectPackage.DefaultExtractDirectory(path);
            ProjectDocument doc = ProjectPackage.Load(path, extract);
            _project.ReplaceDocument(doc, path);
            _projectPath = path;
            RecentProjects.Add(path);

            foreach (SourceImage src in doc.Sources) _resolver.Register(src);
            _resolver.Invalidate();

            int missing = doc.Sources.Count(s => s.SourceMissing);
            DiagnosticLog.Info("package.open_done", $"path={path} sources={doc.Sources.Count} segments={doc.Segments.Count} missing={missing} revision={_project.Revision}");
            Diagnostics.MemorySnapshot.Log("project_open");
            _canvas.SelectSeam(null);
            Status(missing > 0
                ? $"已打开打包项目，但 {missing} 张原图不在容器里——可在左侧右键重新定位（解包目录：{extract}）"
                : $"已打开打包项目，原图解包到 {Path.GetFileName(extract)}\\");
            if (missing > 0) WarnMissingSources(doc);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Error("package.open_failed", ex, $"path={path}");
            MessageBox.Show(this, "打不开这个打包文件：\n" + ex.Message + "\n\n你的容器文件没有被改动。",
                "打开打包项目", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally { UseWaitCursor = false; }
    }

    // ───────────────────────── 冒烟工装入口 ─────────────────────────

    /// <summary>
    /// 仅供 Checks 的 UI 冒烟工装调用，用来把右侧面板逐个切换一遍（整块重建控件树最容易留悬空引用）。
    /// 产品代码不要调它。
    /// </summary>
    public void SwitchToolPanelForTest(ToolKind tool) => _toolPanel.Tool = tool;

    /// <summary>
    /// 仅供冒烟工装：构造导出对话框并让它算一次计划，<b>不显示、不写文件</b>。
    /// 返回计划里的文件名——判据要量的是"对话框上那个模板输入框真的决定了产物命名"，
    /// 所以走的是控件读回来的那份计划，不是在外面另算一遍。
    /// </summary>
    public (IReadOnlyList<string> FileNames, string TemplateText, bool QualityEnabled, int Quality)
        OpenExportDialogForTest(string outputDirectory,
            string? fileNameTemplate = null, int? jpegQuality = null)
    {
        _project.Doc.Export.OutputDirectory = outputDirectory;
        if (fileNameTemplate is not null) _project.Doc.Export.FileNameTemplate = fileNameTemplate;
        if (jpegQuality is not null) _project.Doc.Export.JpegQuality = jpegQuality.Value;
        using var dialog = new ExportDialog(_project, _composer, _encoders, _resolver);
        dialog.CreateControl();
        dialog.PerformLayout();
        return (dialog.PlanFileNamesForTest, dialog.TemplateTextForTest,
                dialog.QualityEnabledForTest, dialog.QualityValueForTest);
    }

    /// <summary>仅供冒烟工装：右侧工具面板（判据要点它上面真正渲染出来的那些控件）。</summary>
    public ToolPanel ToolPanelForTest => _toolPanel;

    /// <summary>
    /// 仅供判据用：主窗口自己持有的<b>合成器与来源解析器</b>。
    /// 判据要经产品那两个对象跑通路，而不是工装再造一套（再造一套就只能证明"我能算"）。
    /// </summary>
    public CanvasComposer ComposerForTest => _composer;

    /// <summary>仅供判据用：见 <see cref="ComposerForTest"/>。</summary>
    public FileSourceResolver ResolverForTest => _resolver;

    /// <summary>仅供冒烟工装：主窗口持有的项目与画布（判据要读实际生效的参数，不是复述）。</summary>
    public Project ProjectForTest => _project;

    /// <summary>
    /// 仅供真窗口工装：点一下"编辑→撤销 / 重做"那两枚菜单项（与用户按 Ctrl+Z、Ctrl+Y 落到的是
    /// 同一个 Click 处理器）。工装不许直接调 <c>Project.Undo()</c>——那样量不到"这一条历史当时
    /// 到底可不可点、菜单文字对不对"，也绕开了用户那条通路。不可点时返回 false 让工装自己判红。
    /// </summary>
    /// <returns>菜单项可点并且真的触发了才返回 true。</returns>
    public bool ClickUndoMenuItemForTest()
    {
        if (_undoItem is null || !_undoItem.Enabled) return false;
        _undoItem.PerformClick();
        return true;
    }

    /// <summary>同 <see cref="ClickUndoMenuItemForTest"/>，走"重做"那一枚。</summary>
    public bool ClickRedoMenuItemForTest()
    {
        if (_redoItem is null || !_redoItem.Enabled) return false;
        _redoItem.PerformClick();
        return true;
    }

    // ───────────────────── 第十四轮需求四：交互诊断的三个出口 ─────────────────────
    //
    // F8 那层读数只能给"正在看屏幕的人"用。需求四要的是把同一批读数交出去：
    // 复制（贴给别人问"我这卡不卡正常吗"）、打开日志目录（看长期）、导出会话（一份文件带齐）。
    // 三个出口共用 <see cref="StitchCanvasControl.DescribeDiagnostics"/> 一份内容——
    // 出口各写一套读数就等于三个数字互相打架，那正是上一轮 F8 与状态栏口径不一致的毛病。

    /// <summary>
    /// 「复制诊断信息」：把屏上那份读数放进剪贴板。
    /// 刻意<b>不</b>连日志原文一起复制——日志第一行写了本机工作目录，而这个按钮的用途就是
    /// "贴给别人看"；隐私红线说日志里不许有像素与隐私内容，路径虽在允许范围内，
    /// 也不该被顺手贴进群。要日志走「导出当前交互会话」，那个是落成本地文件、由人自己决定给谁。
    /// </summary>
    public void CopyDiagnosticsToClipboard()
    {
        string text = _canvas.DescribeDiagnostics();
        try
        {
            Clipboard.SetText(text);
            _statusInfo.Text = "已复制诊断信息（与 F8 读数同源，不含文件路径与像素）";
            DiagnosticLog.Info("diagnostics.copied", $"chars={text.Length} hud={_canvas.ShowDebugHud}");
        }
        catch (Exception ex)
        {
            // 剪贴板是进程间共享资源，被别的程序占着时 SetClipboardData 会真失败，不是理论分支。
            DiagnosticLog.Error("diagnostics.copy_failed", ex);
            MessageBox.Show(this, "无法写入剪贴板（可能被其它程序占用）：" + ex.Message, "复制诊断信息",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>「导出当前交互会话」：先让人选放哪儿，再写。取消就不写任何东西。</summary>
    public void ExportInteractionSession()
    {
        using var dialog = new SaveFileDialog
        {
            Title = "导出当前交互会话",
            Filter = "文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*",
            FileName = $"longstitch-session-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            WriteSessionFile(dialog.FileName);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Error("diagnostics.session_export_failed", ex);
            MessageBox.Show(this, "导出失败：" + ex.Message, "导出当前交互会话",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>
    /// 真正写文件那一步，与保存对话框分开：电池与 <c>--gui</c> 工装不许点窗口，直接调这里量内容。
    /// 顺序有讲究——<b>先 Flush 再取日志</b>：拖动预览那一族高频行进的是缓冲，
    /// 不刷盘就导出，最后几十行（正好是最想复盘的那一段）在文件里根本不存在。
    /// 读取走 <see cref="DiagnosticLog.ReadSessionLog"/> 而不是 File.ReadAllText：
    /// 写句柄还开着，ReadAllText 的共享模式会把它挡掉（那格电池第一次跑就红在这里）。
    /// </summary>
    public string WriteSessionFile(string path)
    {
        DiagnosticLog.Flush();
        string log = DiagnosticLog.ReadSessionLog();
        var sb = new StringBuilder();
        sb.Append(_canvas.DescribeDiagnostics());
        sb.AppendLine();
        sb.AppendLine("═══ 本次会话诊断日志（数字、坐标、Id 与状态；不含像素内容）═══");
        sb.AppendLine(log.Length == 0
            ? "（这一次会话没建成日志文件——日志目录不可写时产品照常运行，只少一份记录）"
            : log);
        File.WriteAllText(path, sb.ToString(), Utf8NoBom);
        _statusInfo.Text = "已导出交互会话：" + Path.GetFileName(path);
        DiagnosticLog.Info("diagnostics.session_exported",
            $"bytes={new FileInfo(path).Length} hud={_canvas.ShowDebugHud}");
        return path;
    }

    private static readonly UTF8Encoding Utf8NoBom = new(false);

    /// <summary>仅供冒烟工装：中央画布控件。</summary>
    public StitchCanvasControl CanvasForTest => _canvas;

    /// <summary>仅供冒烟工装：左侧素材列表（导入批次要闸住它的逐张重建，判据要读到它挂起几笔）。</summary>
    public SourcePanel SourcePanelForTest => _sourcePanel;

    /// <summary>仅供 Checks 的 UI 冒烟工装调用：底部工具栏的按钮标题（顺序即工具顺序）。</summary>
    public IReadOnlyList<string> ToolButtonTitlesForTest =>
        _toolButtons.Select(b => b.Text ?? "").ToArray();

    /// <summary>仅供冒烟工装：底部五枚工具按钮的实际尺寸（点击目标判据读它）。</summary>
    public IReadOnlyList<Size> ToolButtonSizesForTest =>
        _toolButtons.Select(b => b.Size).ToArray();

    /// <summary>仅供冒烟工装：顶栏那条带的设计高度（Chrome 是 internal，工装经此读）。</summary>
    public int TopBarHeightForTest => Chrome.TopBarHeight;

    /// <summary>仅供冒烟工装：顶栏分段当前选中下标（判据 ui/分段控件与文档模式两向同源 读它）。</summary>
    public int ModeSegmentIndexForTest => _modeSegment.SelectedIndex;

    /// <summary>仅供冒烟工装：模拟用户在顶栏分段上选第 index 档（与鼠标/键盘同一条 Pick 通路）。</summary>
    public void PickModeSegmentForTest(int index) => _modeSegment.Pick(index);

    /// <summary>仅供冒烟工装：分段「用户选择」事件累计发过几次（程序回填不许让它涨）。</summary>
    public int ModeSegmentEventCountForTest => _modeSegment.SelectionChangedRaiseCount;

    /// <summary>
    /// 仅供冒烟工装：分段选中档的「文字区」与「下划线」两枚矩形（产品绘制用的同一份算式）。
    /// 判据 <c>guard/分段文字区与下划线几何不相交</c> 用它断言文字底缘压不到下划线、
    /// 且文字区高度装得下当前字体——SegmentedControl 是 internal，工装经此读它的真几何。
    /// </summary>
    public (Rectangle Text, Rectangle Thumb, Rectangle Underline) ModeSegmentGeometryForTest => _modeSegment.SelectedGeometryForTest;

    /// <summary>仅供冒烟工装：分段当前字体（判据要拿字高与文字区高度比，不许拿别的字体冒充）。</summary>
    public Font ModeSegmentFontForTest => _modeSegment.Font;

    /// <summary>
    /// 仅供冒烟工装：给分段换一枚更高的字体，模拟 150% DPI 下字长高的形状。
    /// 9.75pt 在 150% DPI 下的像素高度，等价于 96 DPI 下换一枚 14.6pt 的同族字——
    /// 判据靠这条在离屏工装里复现高 DPI 现场，而不是把判据钉死在某个 DPI 上。
    /// </summary>
    public void SetModeSegmentFontForTest(Font font) => _modeSegment.Font = font;

    /// <summary>仅供冒烟工装：模拟点击底部第 index 枚工具按钮。</summary>
    public void ClickToolButtonForTest(int index) => _toolButtons[index].PerformClick();

    /// <summary>仅供冒烟工装：右侧面板当前显示的工具。</summary>
    public ToolKind ActiveToolForTest => _toolPanel.Tool;

    /// <summary>
    /// 仅供冒烟工装：叠层四件套的当前状态——上方两个按钮的勾选态 × 画布的两个实际开关。
    /// 判据必须看这四格是否自相一致：只读按钮勾选，证不了画面真的变了。
    /// </summary>
    public (bool CompareChecked, bool DiffChecked, bool CanvasOverlay, bool CanvasDifference)
        OverlayStateForTest =>
        (_compareButton.Checked, _diffButton.Checked, _canvas.ShowCompareOverlay, _canvas.ShowDifference);

    /// <summary>仅供冒烟工装：模拟点击画布上方的「对比」。</summary>
    public void ClickCompareForTest() => _compareButton.PerformClick();

    /// <summary>仅供冒烟工装：模拟点击画布上方的「差异」。</summary>
    public void ClickDiffForTest() => _diffButton.PerformClick();

    /// <summary>
    /// 仅供冒烟工装：面板上那两个「重叠对比画法」勾选框此刻的勾选态。
    /// null 表示本轮面板没显示拼缝那一页（切到切分/删段/裁边/清理时就没有这两枚）。
    /// 判据 <c>ui/画法三个出口必须落在同一份状态上</c> 要读它：只看按钮与画布会漏掉
    /// "面板重建把勾选冲回写死的默认"这一族——用户看到的正是"按钮和画面各说一套"。
    /// </summary>
    public (bool? PanelGhost, bool? PanelDiff) PanelOverlapChecksForTest => _toolPanel.OverlapChecksForTest;

    /// <summary>仅供冒烟工装：模拟用户点面板上的「重影对比」勾选框。</summary>
    public void ClickPanelGhostForTest() => _toolPanel.ClickPanelGhostForTest();

    /// <summary>仅供冒烟工装：模拟用户点面板上的「差异标红」勾选框。</summary>
    public void ClickPanelDiffForTest() => _toolPanel.ClickPanelDiffForTest();

    /// <summary>
    /// 仅供冒烟工装：面板为「画法」往外发过几条命令。
    /// 判据 <c>guard/面板回填不许把画法命令再发回主窗口</c> 数的是这一个数——
    /// 它既能证"回填没绕回来"（主窗口驱动一趟之后这个数不动），也自带正向对照（真点一次必加一），
    /// 所以这把尺不会焊在空转上。
    /// </summary>
    public int PanelOverlapCommandCountForTest => _toolPanel.OverlapCommandCountForTest;

    /// <summary>仅供冒烟工装：状态栏的缩放百分比文字。</summary>
    public string StatusBarZoomTextForTest => _statusZoom.Text ?? "";

    /// <summary>仅供冒烟工装：状态栏的画布实际像素文字。</summary>
    public string StatusBarSizeTextForTest => _statusSize.Text ?? "";

    /// <summary>仅供冒烟工装：某个设备点上的悬停提示文字（没有拼缝时为 null）。</summary>
    public string? HoverTipAtForTest(Point device) => _canvas.DescribeSeamAt(_canvas.HitTestSeamEditRegion(device));

    /// <summary>仅供冒烟工装：拼缝列表当前显示的行数与底部统计文字。</summary>
    public int SeamListRowCountForTest => _seamList.Items.Count;

    /// <summary>仅供冒烟工装：见 SeamListRowCountForTest。</summary>
    public string SeamSummaryTextForTest => _seamSummary.Text;

    /// <summary>仅供冒烟工装：勾/取消「只看需检查」，并回读当前选中行对应的真实拼缝 Id。</summary>
    public string? SetSeamFilterForTest(bool onlyReview)
    {
        _seamFilter.Checked = onlyReview;
        if (_seamList.Items.Count == 0) return null;
        _seamList.SelectedIndex = 0;
        return ((Seam)_seamList.Items[0]).Id;
    }

    /// <summary>仅供冒烟工装：底部第 index 枚工具按钮当前是否可用。</summary>
    public bool ToolButtonEnabledForTest(int index) => _toolButtons[index].Enabled;

    /// <summary>
    /// 折叠/展开左右侧栏（需求 §2："窄窗口时侧栏可折叠，不遮挡画布"）。
    /// 用宽度归零而不是 Visible=false——实测后者藏住了内容但没腾出空间，画布宽度一点不变。
    /// </summary>
    /// <returns>折叠后该栏是否处于展开状态。</returns>
    private bool ToggleSidePanel(bool left)
    {
        Panel panel = left ? _leftPanel : _rightPanel;
        int expanded = left ? LeftPanelWidth : RightPanelWidth;
        panel.Width = panel.Width > 0 ? 0 : expanded;
        return panel.Width > 0;
    }

    /// <summary>仅供冒烟工装：切换并回读左/右侧栏的展开状态。</summary>
    public bool ToggleSidePanelForTest(bool left) => ToggleSidePanel(left);

    /// <summary>仅供冒烟工装：浏览态方向键开关（「视图 → 方向键浏览画布」那一项的实际效果）。</summary>
    public void SetBrowseArrowPanForTest(bool on) => _canvas.BrowseArrowPanEnabled = on;

    /// <summary>仅供冒烟工装：打包项目（含原图），绕开文件对话框。</summary>
    public string SavePackageForTest(string packagePath)
    {
        ProjectPackage.PackReport report = ProjectPackage.Save(_project.Doc, packagePath);
        return $"{report.PackedSources}/{report.Skipped.Count}/{report.TotalBytes}";
    }

    /// <summary>仅供冒烟工装：打开打包项目，绕开文件对话框。</summary>
    public void OpenPackageForTest(string packagePath) => OpenPackageFrom(packagePath);

    // ───────────────────────── 设置（第四十八改）─────────────────────────

    /// <summary>
    /// 打开「设置」。外观档位在对话框里点一下就生效并写盘，所以这里不需要在关闭后回读什么——
    /// 换肤的广播（<see cref="Theme.Changed"/>）在点击那一刻已经把整棵控件树翻完了。
    ///
    /// 对话框是模态的：它开着的时候主窗口收不到输入，于是"跟随系统"那枚轮询与
    /// 主窗口的激活回调都不会和对话框里的点击抢着写同一个档位。
    /// </summary>
    private void OpenSettings()
    {
        using var dialog = new SettingsDialog();
        dialog.ShowDialog(this);
        // 关掉之后报一句现在在哪一档：跟随系统档下用户可能没注意界面已经变了，
        // 状态栏这一句让"到底生效了没有"有个可指认的落点。
        Status(ThemeController.Describe());
    }

    /// <summary>仅供冒烟工装：绕开菜单直接开一次设置（离屏工装不 Show 主窗口）。</summary>
    public void OpenSettingsForTest() => OpenSettings();

    // ───────────────────────── 说明 ─────────────────────────

    private void ShowHelp() => MessageBox.Show(this,
        "鼠标（第八轮起按「点哪儿办哪儿的事」排优先级，不再由\"是否选中过拼缝\"决定）\n" +
        "· 左键点在蓝色拼缝线或重叠带里 = 编辑这条拼缝，拖动只改这条缝的位移\n" +
        "· 左键点在其它任何地方 = 平移整个画布（即使刚才正在编辑某条拼缝）\n" +
        "· 中键拖动 = 平移画布\n" +
        "· Shift + 左键拖动 = 永远只平移画布，不改拼缝（重叠带里也想平移时就用它）\n" +
        "· 滚轮 = 上下滚动；Ctrl + 滚轮 = 以指针为中心缩放\n" +
        "· 点蓝色拼缝进入编辑；再点同一条或按 Esc 退出\n" +
        "· 悬停在拼缝线上提示「点击编辑拼缝 2→3」，在重叠带里提示「拖动调整拼缝 2→3…」；\n" +
        "  拖动时显示即时位移 X/Y\n\n" +
        "长截图模式：拼缝只能上下调，横向一律按 X=0 处理\n" +
        "· 固定前一段 = 下面那张跟手，视口一动不动\n" +
        "· 固定后一段 = 上面那张跟手，下面那张钉在屏幕上（为此视口按补偿量平移；\n" +
        "  这纯是显示补偿，不写参数、不记历史）\n\n" +
        "键盘\n" +
        "· 编辑态 ↑↓←→ = 移动 1 个源图像素（按住 Shift 走 10）\n" +
        "· 浏览态 ↑↓←→ = 平移视口 1 个屏幕像素（Shift 走 40）；可在「视图」里关掉\n" +
        "· Ctrl+Z / Ctrl+Y = 撤销 / 重做；一次拖动算一条记录\n" +
        "· Ctrl+S = 保存项目；Ctrl+E = 导出；F3 = 跳到下一条需检查\n" +
        "· Ctrl+D1 / Ctrl+D2 = 折叠/展开左右侧栏（窄窗口用）\n" +
        "· Esc = 先退临时模式（点选切分 / 框选），再退拼缝编辑\n\n" +
        "底部五枚工具：拼缝 / 切分 / 删段 / 裁边 / 清理，点哪枚右侧就显示哪套参数。\n" +
        "要点：右侧的 X/Y 数字永远是源图像素，缩放只改变预览，不改变导出尺寸。",
        "操作说明", MessageBoxButtons.OK, MessageBoxIcon.Information);

    private void ShowAbout()
    {
        // 第五十一改（用户图2）：系统 MessageBox 与整套圆角卡面语言割裂，改为自绘的关于对话框
        // （正文与旧版逐字相同，只是换了容器）。
        using var dialog = new AboutDialog();
        dialog.ShowDialog(this);
    }
}
