using System.Reflection;
using System.Text.RegularExpressions;
using LongStitch.App.UI;
using LongStitch.Core;
using LongStitch.Core.Cleanup;
using LongStitch.Core.Export;
using LongStitch.Core.Geometry;
using LongStitch.Core.Imaging;
using LongStitch.Core.Imaging.Png;
using LongStitch.Core.Layout;
using LongStitch.Core.Matching;
using LongStitch.Core.Model;
using LongStitch.Core.ProjectIO;

namespace LongStitch.Checks;

/// <summary>
/// 裁切拼缝判据（第九轮）。
///
/// ═══ 这一组格子守的是哪一句话 ═══
/// 用户报的现场是准确的：「两张图还是以完整尺寸参与布局，只是靠 offsetY 让下一张盖住上一张，
/// 所以红框里同时看得到两份内容」。后果不止难看——重复那一截<b>进了像素</b>，
/// 任何"逐段取数"的通路（差异视图、检测取数、分段导出）都会拿到两份。
///
/// 本轮把去重改成<b>真的裁两刀</b>：
/// <code>
///   b = BeforeCropBottom   上一张底部裁掉的行数（带里由下一张提供）
///   t = AfterCropTop       下一张顶部裁掉的行数（带里由上一张提供）
///   b + t == O             不变式，由 Seam 的 setter 守住（不是事后比对出来的）
///   上一张源矩形 = (0, 0, w, H1 − b)     下一张源矩形 = (0, t, w, H2 − t)
///   画布高 = ΣH − ΣO
/// </code>
/// 于是两段在画布上<b>首尾相接、不再相交</b>，"只画一次"从"靠绘制顺序"升级为结构性质
/// （判据 <c>crop/长截图里任意两段的矩形都不相交</c>）。
///
/// ═══ 判据为什么真能量到东西（两处自证，别跳过）═══
/// ① "数这一行出现几次"的判据，样本必须<b>每行唯一</b>，否则数出 1 次可能只是
///    "本来就没有第二份"。每格都先量这件事。
/// ② "被裁掉的那几行不进成品"用<b>同本</b>样本量不出来——两份副本逐字节相同，数谁都在。
///    所以另造一组<b>异本</b>样本：把重叠带在后一张里涂一列标记，两份副本因此可分辨，
///    于是"切点选哪一侧、成品里就是哪一份，另一份一行都不进成品"成为可翻红的整数判据
///    （这也正是这个功能对用户的全部意义：这几行内容要哪一份）。
///    反向对照是"切点换到另一头，两份计数互换"——不互换就说明标记根本没生效、判据在空转。
/// </summary>
public static class SeamCropTests
{
    // 需求七.1 点名的样本尺寸：80×200 两张、重叠 60 ⇒ 成品高必须 340、不是 400
    private const int Width = 80, Height = 200, Overlap = 60, Expected = Height + Height - Overlap;

    /// <summary>默认档（b=O、t=0）时两段接缝所在的画布行号，也是蓝线在 1:1 缩放下的设备纵坐标。</summary>
    private const int LineY = Height - Overlap;

    /// <summary>
    /// 异本样本涂在重叠带上的标记列颜色。
    /// 选纯红是因为 <see cref="LongScrollTests.StripedPage"/> 的 R 通道来自 y%233（永远 ≤232），
    /// 碰不到 255——标记若与正常内容同色，"被裁掉的那一份"就数不出来，整格退化成空判据。
    /// </summary>
    private static readonly RgbaColor Marker = new(255, 0, 0, 255);

    public static void Register()
    {
        Battery.Add("crop", "80×200 两张、重叠 60：成品高 340 而不是 400，重复那行只出现一次", HeightIsSumMinusOverlap);
        Battery.Add("crop", "三段链的总账：导出高 = Σ高 − Σ重叠，两缝各裁各的", ChainExportHeightIsSumMinusAllOverlaps);
        Battery.Add("crop", "整带取下一张：上一张裁掉的底部行一行都不进导出文件", BeforeBottomCropRemovesItsRows);        Battery.Add("crop", "整带取上一张：下一张裁掉的顶部行一行都不进导出文件", AfterTopCropRemovesItsRows);
        Battery.Add("crop", "切点在带中间：带里前 t 行取上一张、后 b 行取下一张", CutInsideBandSplitsTheTwoCopies);
        Battery.Add("crop", "预览合成与导出文件逐字节相同（分块=整块同样成立）", PreviewAndExportUseSameCrops);
        // 下面这几格会造 StitchCanvasControl（WinForms 控件），必须与 canvas/ 组一样走 STA 线程；
        // 注册成 lambda 而不是直接传方法组，是为了别让"控件在非 STA 线程上构造"变成偶发红。
        Battery.Add("crop", "调试取数不进出成品的那条合成通路", () => UiSmokeTests.RunOnStaThread(DebugSourceNeverEntersExport));
        Battery.Add("crop", "压着蓝线滑动下方那张图：蓝线行号不变、只一条撤销", () => UiSmokeTests.RunOnStaThread(LineDragMovesCutOnly));
        Battery.Add("crop", "滑动图片的流水账带齐需求八那批字段与线不动读数", () => UiSmokeTests.RunOnStaThread(SlideTraceCarriesRequiredLogFields));
        Battery.Add("crop", "点普通画布只平移视口：裁切参数与导出字节一字不改", () => UiSmokeTests.RunOnStaThread(BlankAreaPansOnly));
        Battery.Add("crop", "长截图里任意两段的矩形都不相交", RectsAreAlwaysDisjoint);
        Battery.Add("crop", "红框真的对应两个裁切矩形：上一张改 SourceRect.Bottom、下一张改 SourceRect.Top", BandMapsToTwoCrops);
        Battery.Add("crop", "默认切分与旧的覆盖式落位逐字节相同", DefaultCutEqualsLegacyOverwrite);
        Battery.Add("crop", "竖拼与横拼不走裁切：源矩形不被动过、位移照旧落位", OffsetModesNeverCrop);
        Battery.Add("crop", "旧项目文件没有裁切字段时按默认档补齐", OldProjectFileMigratesToDefaultCut);
        Battery.Add("align", "旧项目文件的裁切字段按任意顺序到达都归一到同一组核心值",
            LegacyAliasOrderNormalizesToSameCoreValues);
        Battery.Add("crop", "取消本次调整与恢复自动都回到自动结论那一格", () => UiSmokeTests.RunOnStaThread(CancelAndResetReturnToAutoCut));
        Battery.Add("crop", "匹配结果自带裁切建议：带起止、两侧裁切量、切点与需人工标记", MatchResultCarriesCropAdvice);
        Battery.Add("crop", "方向键一次滑动一行：蓝线画布行号不变、每次按键一条撤销", () => UiSmokeTests.RunOnStaThread(ArrowsMoveCutOneRow));
        Battery.Add("crop", "改总重叠时蓝线按带里的相对位置换算，不跳到带首带尾", OverlapChangeScalesUserCutLine);
        Battery.Add("crop", "重叠写得比图还高时裁切被夹小，但总账仍平且不裁出负尺寸", OverlapBeyondImageIsClampedVisible);
        Battery.Add("crop", "布局与导出那两条状态日志也带齐需求八那批字段", StateLinesCarryCropFields);

        // ═══ 守卫的守卫（本项目约定：每写一条"能抓 bug 的断言"就配一格故意注入缺陷、要求它必须变红）═══
        // 这三格注的都是"判据自己的失效形状"，不改产品源文件——守卫坏掉时是绿灯，
        // 那是这类工装最危险的失效模式（详见 docs/精度红线.md 第 3 节）。
        Battery.Add("guard", "自动档蓝线被写到带外时修补回到自动结论那一格", CropsBrokenByBackdoorAreDetected);
        Battery.Add("guard", "异本标记不生效时被裁的行不进成品会空转", CropMarkerIsWhatMakesThoseAssertionsBit);
        Battery.Add("guard", "位移档做出的同样去重量必被不相交判据抓到", OverlayPlacementIsCaughtByDisjointness);
    }

    // ───────────────────────── 样本与夹具 ─────────────────────────

    /// <summary>
    /// 一份样本 = 两张图 + 由它们建好的长截图项目 + 导出用的合成器与解压器。
    /// 放在一起是因为判据要拿<b>同一对图</b>去数行：另建一份"内容相同"的图，
    /// 万一工厂改了种子，判据数的就是另一张图的内容了。
    /// </summary>
    private sealed class Sample
    {
        public required RgbaImage Before { get; init; }
        public required RgbaImage After { get; init; }
        public required RgbaImage Page { get; init; }
        public required Project P { get; init; }
        public required CanvasComposer Composer { get; init; }
        public required MemoryResolver Resolver { get; init; }

        /// <summary>上一张在带内第 i 行的源内容（带 = 页面第 140..199 行）。</summary>
        public ReadOnlySpan<byte> BeforeBandRow(int i) => Before.Row(LineY + i);

        /// <summary>下一张在带内第 i 行的源内容（异本样本里它涂了标记）。</summary>
        public ReadOnlySpan<byte> AfterBandRow(int i) => After.Row(i);

        public ReadOnlySpan<byte> PageRow(int i) => Page.Row(i);
    }

    /// <summary>
    /// 建样本。<paramref name="tinted"/>=true 时把后一段的带内行涂上标记（异本），
    /// false 时保留"两份副本逐字节相同"的真实连拍形状（同本）。
    /// </summary>
    private static Sample NewSample(bool tinted)
    {
        RgbaImage page = LongScrollTests.StripedPage(Width, Expected);
        (RgbaImage before, RgbaImage after, int expectedOverlap) =
            TestProject.CutScrollPair(page, firstTop: 0, firstHeight: Height, overlap: Overlap);
        if (tinted)
            for (int row = 0; row < Overlap; row++) after.Fill(new IntRect(0, row, 1, 1), Marker);

        (Project p, MemoryResolver r, _) = TestProject.Create(StitchMode.LongScroll, before, after);

        // ── 夹具自证（这三条不成立，下面所有"数行数"的判据都是空转）──
        Battery.AssertEqual(Overlap, expectedOverlap, "夹具自证：切出来的重叠就是 60");
        Battery.AssertEqual(Height, before.Height, "夹具自证：前一段高 H1=200");
        Battery.AssertEqual(Height, after.Height, "夹具自证：后一段高 H2=200（页面够长，没被截短）");
        Battery.AssertEqual(1, LongScrollTests.CountRowsEqualTo(page, page.Row(LineY)),
            "夹具自证：带里那一行在整页里唯一");
        if (tinted)
        {
            Battery.Assert(!before.Row(Height - 1).SequenceEqual(after.Row(Overlap - 1)),
                "夹具自证：两份副本必须能分辨，否则指认不了被裁掉的是哪一份");
            Battery.AssertEqual(0, LongScrollTests.CountRowsEqualTo(before, after.Row(0)),
                "夹具自证：涂了标记的那一行在上一张里一份都没有");
        }
        else
        {
            Battery.Assert(before.Row(Height - 1).SequenceEqual(after.Row(Overlap - 1)),
                "夹具自证：同本样本里带尾那一行的两份副本应逐字节相同");
        }

        return new Sample
        {
            Before = before, After = after, Page = page, P = p,
            Composer = new CanvasComposer(r), Resolver = r,
        };
    }

    /// <summary>
    /// 走<b>产品的自动写回出口</b>摆好这条缝的去重账：先给重叠，再（可选）给自动切点。
    /// 判据不许直接摆后备字段——那样量的是夹具，不是软件。
    /// </summary>
    private static void WriteAuto(Project p, int overlap, int? autoCut = null)
    {
        p.Mutate("写回自动结论", d =>
        {
            Seam seam = d.Seams[0];
            int tight = p.Layout.TightOffset(seam).Y;
            seam.ApplyAutoResult(new IntPoint(0, tight - overlap), SeamStatus.AutoPassed,
                SeamCause.None, null, 0.98, overlap, new List<MatchCandidate>(), autoCut);
        });
    }

    /// <summary>用户改切点的唯一通路（面板数值框、方向键、拖动最后都落到这条）。</summary>
    private static CutApply MoveCut(Project p, int cut)
    {
        CutApply result = default;
        p.Mutate("移动切点", d => result = d.Seams[0].ApplyCut(cut));
        return result;
    }

    private static SeamGeometry Geometry(Project p)
    {
        SeamGeometry? g = p.Layout.DescribeSeam(p.Doc.Seams[0]);
        Battery.Assert(g is not null, "两段都在布局里，去重账必须量得出来");
        return g!;
    }

    /// <summary>用产品的导出引擎写出 PNG 字节。</summary>
    private static byte[] ExportBytes(Project p, CanvasComposer composer, MemoryResolver resolver)
    {
        string dir = Path.Combine(Path.GetTempPath(), "ls-crop-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(dir);
        try
        {
            var encoders = new EncoderRegistry();
            var settings = new ExportSettings
            {
                Format = ExportFormat.Png,
                OutputDirectory = dir,
                MaxSegmentHeight = 0,
                SegmentOverlap = 0,
                DownscaleDivisor = 1,
                FileNameTemplate = "crop_{index}.{ext}",
            };
            ExportPlan plan = ExportPlanner.Build(p.Layout, settings, encoders, dir, composer, resolver);
            Battery.Assert(!plan.IsHardBlock, "导出计划被硬拦：" + plan.BlockingReason);
            Battery.AssertEqual(p.Layout.CanvasSize.Height, plan.OriginalPixelSize.Height,
                "计划尺寸必须来自同一份裁切布局");
            ExportOutcome outcome = new ExportEngine(composer, encoders).Run(p.Layout, plan, settings, dir);
            Battery.Assert(outcome.Success, "导出失败：" + outcome.Error);
            Battery.AssertEqual(1, outcome.Files.Count, "不分段应只有一个文件");
            return File.ReadAllBytes(outcome.Files[0]);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* 临时目录清不掉不影响判据 */ }
        }
    }

    /// <summary>导出再解码回来的成品（宽必须还是源图宽，否则"锁横向"被撑开了）。</summary>
    private static RgbaImage ExportImage(Project p, CanvasComposer composer, MemoryResolver resolver)
    {
        RgbaImage image = PngDecoder.Decode(ExportBytes(p, composer, resolver)).Image;
        Battery.AssertEqual(Width, image.Width, "成品宽 = 源图宽（长截图锁横向，不该被撑宽）");
        return image;
    }

    /// <summary>裁切拖动用的画布夹具（1:1 缩放、视口原点 (0,0)，于是设备像素 == 源行号）。</summary>
    private sealed class CropRig : IDisposable
    {
        public required StitchCanvasControl Canvas { get; init; }
        public required Sample S { get; init; }

        public Project P => S.P;
        public Seam Seam => S.P.Doc.Seams[0];
        public int Revision => S.P.Revision;
        public IntPoint TopLeft => Canvas.Viewport.TopLeft;

        public static CropRig New(bool tinted = false)
        {
            Sample s = NewSample(tinted);
            WriteAuto(s.P, Overlap);
            var canvas = new StitchCanvasControl(s.P, s.Composer) { Size = new Size(320, 420) };
            canvas.Viewport.SetOneToOne(new Point(0, 0));
            canvas.SelectSeam(s.P.Doc.Seams[0].Id);
            return new CropRig { Canvas = canvas, S = s };
        }

        public byte[] Export() => ExportBytes(P, S.Composer, S.Resolver);

        public void Dispose() => Canvas.Dispose();
    }

    // ═════════════════ ① 总账：成品高度与"只出现一次"（需求七.1） ═════════════════

    /// <summary>
    /// 需求七.1 逐字核对：80×200 两张、重叠 60 ⇒ 成品高 <b>340</b>，不是 400；
    /// 重叠区里那一行在成品里<b>只出现一次</b>。
    /// 三处一起量（布局参数、布局落位、真正的导出文件），因为这三处历史上各算过一次：
    /// 只有导出那一路也量到了，才不会出现"面板说去掉 60 行、文件却高出 60 行"。
    /// 反例（O=0）必须真能数出两次，否则上面的"只出现一次"是空判据。
    /// </summary>
    private static void HeightIsSumMinusOverlap()
    {
        Sample s = NewSample(tinted: false);
        Project p = s.P;
        WriteAuto(p, Overlap);

        Battery.AssertEqual(340, p.Layout.CanvasSize.Height, "裁切档的画布高");
        Battery.Assert(p.Layout.CanvasSize.Height != Height + Height,
            "绝不能等于两段之和（那意味着一行都没去掉）");

        SeamGeometry g = Geometry(p);
        Battery.AssertEqual(Overlap, g.Overlap, "布局真裁掉的行数");
        Battery.AssertEqual(Overlap, g.ReportedOverlap, "与分析声称的重叠一致");
        Battery.Assert(g.CropInvariantHolds, "两侧裁切之和不等于重叠：" + g);
        Battery.Assert(g.RectsDisjoint, "两段矩形仍在交叠（还在靠绘制顺序去重）：" + g);
        Battery.Assert(g.FormulaHolds, "去重公式不成立：" + g);

        DedupAccount account = p.Layout.MeasureDedup();
        Battery.AssertEqual<long>(400, account.SumExtents, "各段未裁切长度之和 = 200 + 200");
        Battery.AssertEqual<long>(60, account.SumOverlaps, "各重叠之和");
        Battery.AssertEqual<long>(340, account.Expected, "ΣH − Σ重叠");
        Battery.AssertEqual<long>(340, account.CanvasExtent, "画布实际高");
        Battery.Assert(account.Holds && account.Unexplained == 0, "总账不平：" + account);

        RgbaImage file = ExportImage(p, s.Composer, s.Resolver);
        Battery.AssertEqual(340, file.Height, "导出文件的高度必须 = ΣH − Σ重叠");

        const int markerRow = 150;          // 落在重叠带（画布 140..199）内
        Battery.AssertEqual(1, LongScrollTests.CountRowsEqualTo(s.Page, s.PageRow(markerRow)),
            "夹具自证：被数的那一行在整页里唯一");
        Battery.AssertEqual(1, LongScrollTests.CountRowsEqualTo(file, s.PageRow(markerRow)),
            "重叠区里那一行在成品里应当只有一份");
        Battery.AssertEqual(1, LongScrollTests.CountRowsEqualTo(file, s.PageRow(0)),
            "上一张独有的首行不许被顺手吃掉");
        Battery.AssertEqual(1, LongScrollTests.CountRowsEqualTo(file, s.PageRow(Expected - 1)),
            "下一张独有的末行必须还在成品里");

        // 反例：重叠写成 0（等于不去重）→ 同一行出现两次、画布高出整整一段重叠
        WriteAuto(p, 0);
        RgbaImage cheat = ExportImage(p, s.Composer, s.Resolver);
        Battery.AssertEqual(400, cheat.Height, "不去重时画布确实高出整段重叠");
        Battery.AssertEqual(2, LongScrollTests.CountRowsEqualTo(cheat, s.PageRow(markerRow)),
            "反例必须真能数出两次，否则上面的「只出现一次」是空判据");

        Battery.Evidence($"O=60 → 文件 {file.Width}×{file.Height}、该行出现 1 次；O=0 → {cheat.Height} 行、出现 2 次");
    }

    /// <summary>
    /// 验收条件第一条的<b>多段</b>版本：三段链、两条缝各有各的重叠，
    /// 成品高必须 = ΣH − ΣO，而不是只核对第一缝。
    /// 只测两段的实现最容易在"中间那段同时被上一条缝裁顶、被下一条缝裁底"上出错，
    /// 所以中间那段的两侧裁切量单独量出来。
    /// </summary>
    private static void ChainExportHeightIsSumMinusAllOverlaps()
    {
        RgbaImage a = Fixture.OpaqueLike(Width, Height, seed: 201);
        RgbaImage b = Fixture.OpaqueLike(Width, Height, seed: 202);
        RgbaImage c = Fixture.OpaqueLike(Width, Height, seed: 203);
        (Project p, MemoryResolver r, _) = TestProject.Create(StitchMode.LongScroll, a, b, c);
        var composer = new CanvasComposer(r);

        p.Mutate("两条缝各给重叠", d =>
        {
            // 走产品出口：自动建议的位移 = 紧接位移 − 重叠（裁切档里位移只是记录）
            d.Seams[0].ApplyAutoResult(new IntPoint(0, Height - 60), SeamStatus.AutoPassed,
                SeamCause.None, null, 0.9, 60, new List<MatchCandidate>());
            d.Seams[1].ApplyAutoResult(new IntPoint(0, Height - 40), SeamStatus.AutoPassed,
                SeamCause.None, null, 0.9, 40, new List<MatchCandidate>());
        });

        Battery.AssertEqual(500, p.Layout.CanvasSize.Height, "三段链的画布高 = 600 − (60+40)");
        DedupAccount account = p.Layout.MeasureDedup();
        Battery.AssertEqual<long>(600, account.SumExtents, "三段未裁切之和");
        Battery.AssertEqual<long>(100, account.SumOverlaps, "两条缝重叠之和");
        Battery.Assert(account.Holds, "多段总账不平：" + account);

        SeamGeometry first = p.Layout.DescribeSeam(p.Doc.Seams[0])!;
        SeamGeometry second = p.Layout.DescribeSeam(p.Doc.Seams[1])!;
        Battery.AssertEqual(Height, first.BeforeBaseExtent, "第一缝前段（段 #0）未裁切长");
        Battery.AssertEqual(Height, second.BeforeBaseExtent, "第二缝前段（段 #1）未裁切长");
        Battery.AssertEqual(0, second.BeforeTopTrim, "段 #1 的头顶没被裁（第一缝是默认档 t=0）");
        Battery.AssertEqual(40, second.AppliedBeforeCrop, "段 #1 的底被第二缝裁掉 40");
        Battery.AssertEqual(Height - 40, p.Layout.Placed[1].Rect.Height, "段 #1 可见高 = 200 − 40");
        Battery.AssertEqual(p.Layout.Placed[0].Rect.Bottom, p.Layout.Placed[1].Rect.Y,
            "段 #0 与段 #1 首尾相接（既不交叠也不留空洞）");
        Battery.AssertEqual(p.Layout.Placed[1].Rect.Bottom, p.Layout.Placed[2].Rect.Y,
            "段 #1 与段 #2 也首尾相接");
        Battery.Assert(p.Layout.Placed[2].Rect.Bottom == 500, "链末底边即画布底边");

        RgbaImage file = PngDecoder.Decode(ExportBytes(p, composer, r)).Image;
        Battery.AssertEqual(500, file.Height, "多段链的导出高度");
        Battery.Evidence($"三段共 600 行、两条缝去掉 60+40 → 画布与文件都 500 行；" +
                         $"段 #1 两侧裁切 (顶 {second.BeforeTopTrim}, 底 {second.AppliedBeforeCrop})");
    }

    // ═════════════════ ② 两侧裁切各自进导出（需求七.2 / 七.3 / 带中间） ═════════════════

    /// <summary>
    /// 需求七.2：<b>上一张</b>的 bottom crop。默认档 b=O、t=0 时
    /// 上一张源矩形必须是 (0, 0, w, H1 − b)（也就是它的 <c>SourceRect.Bottom</c> 被推上去了），
    /// 而且它被裁掉的那 O 行<b>一行都不进导出文件</b>；同一份内容只出现一次，且来自下一张。
    /// 反向对照：切点换到带尾后两份计数必须互换——不互换就说明标记没生效。
    /// </summary>
    private static void BeforeBottomCropRemovesItsRows()
    {
        Sample s = NewSample(tinted: true);
        Project p = s.P;
        WriteAuto(p, Overlap);
        Seam seam = p.Doc.Seams[0];

        Battery.AssertEqual(Overlap, seam.BeforeCropBottom, "默认档：上一张底部裁掉整段重叠");
        Battery.AssertEqual(0, seam.AfterCropTop, "默认档：下一张顶部不裁");
        Battery.AssertEqual(Overlap, seam.CropTotal, "两侧之和 = 重叠");

        SeamGeometry g = Geometry(p);
        Battery.AssertEqual(new IntRect(0, 0, Width, Height - Overlap), g.BeforeSourceRect,
            "上一张真正进像素的矩形 = (0, 0, w, H1 − BeforeCropBottom)");
        Battery.AssertEqual(Height - Overlap, g.BeforeSourceRect.Bottom,
            "上一张的 SourceRect.Bottom 被推到了带首（需求结尾点名的就是这一个数）");
        Battery.AssertEqual(new IntRect(0, Height - Overlap, Width, Overlap), g.BeforeDroppedRect,
            "被裁掉的那一条源矩形（源图坐标）");
        Battery.AssertEqual(g.BeforeSourceRect.Bottom, g.BeforeDroppedRect.Y,
            "被裁掉的行必须紧跟在裁切矩形底边之后（中间不许漏行也不许重叠）");

        RgbaImage file = ExportImage(p, s.Composer, s.Resolver);
        for (int i = 0; i < Overlap; i += 15)
        {
            Battery.AssertEqual(0, LongScrollTests.CountRowsEqualTo(file, s.BeforeBandRow(i)),
                $"上一张带内第 {i} 行（源行 {LineY + i}）被裁掉了，不该出现在成品里");
            Battery.AssertEqual(1, LongScrollTests.CountRowsEqualTo(file, s.AfterBandRow(i)),
                $"同一份内容改由下一张提供，应恰好出现一次（带内第 {i} 行）");
        }
        Battery.AssertEqual(0, LongScrollTests.CountRowsEqualTo(file, s.BeforeBandRow(Overlap - 1)),
            "上一张的最后一行同样不进成品");

        MoveCut(p, Overlap);              // 整带取上一张
        RgbaImage flipped = ExportImage(p, s.Composer, s.Resolver);
        Battery.AssertEqual(1, LongScrollTests.CountRowsEqualTo(flipped, s.BeforeBandRow(0)),
            "反向对照：切点换到带尾后，上一张那份回到成品（0 → 1）");
        Battery.AssertEqual(0, LongScrollTests.CountRowsEqualTo(flipped, s.AfterBandRow(0)),
            "反向对照：下一张那份被裁掉（1 → 0）");
        Battery.AssertEqual(Expected, flipped.Height, "换提供者不改总高");
        Battery.Evidence($"b=60,t=0：上一张矩形 (0,0,{Width},{Height - Overlap})、其带内行在成品 0 次；" +
                         "切点换到带尾后两份计数互换、总高仍 340");
    }

    /// <summary>
    /// 需求七.3：<b>下一张</b>的 top crop。切点切到带尾（t=O）时
    /// 下一张源矩形起点必须是第 O 行（<c>SourceRect.Top</c> 被推下来了）、长度 H2 − O，
    /// 它被裁掉的顶部 O 行一行都不进成品。
    /// </summary>
    private static void AfterTopCropRemovesItsRows()
    {
        Sample s = NewSample(tinted: true);
        Project p = s.P;
        WriteAuto(p, Overlap);
        MoveCut(p, Overlap);              // 整带取上一张 ⇒ t = O

        Seam seam = p.Doc.Seams[0];
        Battery.AssertEqual(Overlap, seam.AfterCropTop, "切点在带尾：下一张顶部裁掉整段重叠");
        Battery.AssertEqual(0, seam.BeforeCropBottom, "上一张此时一行都不裁");
        Battery.AssertEqual(Overlap, seam.CropTotal, "不变式仍成立：0 + O = O");

        SeamGeometry g = Geometry(p);
        Battery.AssertEqual(new IntRect(0, Overlap, Width, Height - Overlap), g.AfterSourceRect,
            "下一张真正进像素的矩形 = (0, AfterCropTop, w, H2 − AfterCropTop)");
        Battery.AssertEqual(Overlap, g.AfterSourceRect.Y,
            "下一张的 SourceRect.Top = AfterCropTop（需求结尾点名的另一个数）");
        Battery.AssertEqual(new IntRect(0, 0, Width, Overlap), g.AfterDroppedRect, "被裁掉的顶部那一条");
        Battery.AssertEqual(g.AfterDroppedRect.Bottom, g.AfterSourceRect.Y,
            "被裁掉的行必须正好顶在裁切矩形起点之前");

        RgbaImage file = ExportImage(p, s.Composer, s.Resolver);
        for (int i = 0; i < Overlap; i += 15)
        {
            Battery.AssertEqual(0, LongScrollTests.CountRowsEqualTo(file, s.AfterBandRow(i)),
                $"下一张带内第 {i} 行被裁掉了，不该出现在成品里");
            Battery.AssertEqual(1, LongScrollTests.CountRowsEqualTo(file, s.BeforeBandRow(i)),
                $"同一份内容改由上一张提供，应恰好一次（带内第 {i} 行）");
        }

        MoveCut(p, 0);                    // 反向对照：切回带首
        RgbaImage back = ExportImage(p, s.Composer, s.Resolver);
        Battery.AssertEqual(1, LongScrollTests.CountRowsEqualTo(back, s.AfterBandRow(0)),
            "反向对照：t=0 时下一张那份回到成品");
        Battery.AssertEqual(0, LongScrollTests.CountRowsEqualTo(back, s.BeforeBandRow(0)),
            "反向对照：t=0 时上一张那份被裁掉");
        Battery.Evidence($"t=60：下一张矩形起点 {g.AfterSourceRect.Y}、其顶部行在成品 0 次；t=0 后两份互换");
    }

    /// <summary>
    /// 切点落在带<b>中间</b>时，带里的每一行都必须各归各的：前 t 行取上一张、后 b 行取下一张，
    /// 每行仍然只有一份、总高不变。这是"裁切真的把重叠分成两刀"最直接的像素级证据，
    /// 也正是"这几行内容我要上面那份还是下面那份"这个功能的验收面。
    /// </summary>
    private static void CutInsideBandSplitsTheTwoCopies()
    {
        const int cut = 20;
        Sample s = NewSample(tinted: true);
        Project p = s.P;
        WriteAuto(p, Overlap);
        CutApply applied = MoveCut(p, cut);

        Battery.Assert(applied.Applied && !applied.Clamped, "切点 20 在带内，不该被夹");
        Battery.AssertEqual(cut, p.Doc.Seams[0].AfterCropTop, "后一段顶部裁切 = 切点");
        Battery.AssertEqual(Overlap - cut, p.Doc.Seams[0].BeforeCropBottom, "前一段底部裁切 = O − 切点");

        RgbaImage file = ExportImage(p, s.Composer, s.Resolver);
        Battery.AssertEqual(Expected, file.Height, "切点在带中间也不改总高");

        int fromBefore = 0, fromAfter = 0;
        for (int i = 0; i < Overlap; i++)
        {
            int plain = LongScrollTests.CountRowsEqualTo(file, s.BeforeBandRow(i));
            int tinted = LongScrollTests.CountRowsEqualTo(file, s.AfterBandRow(i));
            int wantFromBefore = i < cut ? 1 : 0;
            // 判据的"为什么"先算成变量再插值：把带引号的中文直接写在插值洞里，
            // 编译器的报错位置会跑到几百行之外（本项目已踩过两次）。
            string why = i < cut ? "切点之前，应取上一张那份" : "切点之后，上一张那份应已被裁掉";
            Battery.AssertEqual(wantFromBefore, plain, $"带内第 {i} 行：{why}");
            Battery.AssertEqual(1 - wantFromBefore, tinted,
                $"带内第 {i} 行：下一张那份的出现次数应与上一张互补（合计恰好一份）");
            fromBefore += plain;
            fromAfter += tinted;
        }
        Battery.AssertEqual(cut, fromBefore, $"带里由上一张提供的行数应 = 切点 {cut}");
        Battery.AssertEqual(Overlap - cut, fromAfter, "带里由下一张提供的行数应 = O − 切点");

        SeamGeometry g = Geometry(p);
        Battery.AssertEqual(g.BeforeRect.Bottom - cut, g.BandRect.Y,
            "带顶 = 上一张可见底边 − 切点（带里上面这 t 行确实归上一张）");
        Battery.AssertEqual(Expected, g.PairExtent, "两段的总跨度不变");
        Battery.Evidence($"切点 {cut}：带里 {cut} 行取上一张、{Overlap - cut} 行取下一张，逐行互补、总高仍 {file.Height}");
    }

    // ═════════════════ ③ 预览 = 导出（需求三、七.4） ═════════════════

    /// <summary>
    /// 需求七.4：预览与导出<b>逐字节</b>一致，而且必须用的是同一对裁切矩形。
    /// 三路一起比：整张合成、分块合成（分块=整块那条红线）、真正的导出文件。
    /// 只在切点 0 比不够——"预览裁了、导出没裁"这类缺陷只在切点挪过之后才露出来。
    /// </summary>
    private static void PreviewAndExportUseSameCrops()
    {
        foreach (int cut in new[] { 0, 20, 60 })
        {
            Sample s = NewSample(tinted: true);
            WriteAuto(s.P, Overlap);
            MoveCut(s.P, cut);
            CanvasLayout layout = s.P.Layout;
            var fullRegion = new IntRect(0, 0, layout.CanvasSize.Width, layout.CanvasSize.Height);

            byte[] whole = s.Composer.RenderWhole(layout).RegionSha256(layout.CanvasBounds);
            byte[] banded = s.Composer.RenderBand(layout, layout.CanvasBounds).RegionSha256(fullRegion);
            byte[] file = PngDecoder.Decode(ExportBytes(s.P, s.Composer, s.Resolver)).Image.RegionSha256(fullRegion);

            Battery.AssertBytesEqual(whole, banded, $"切点 {cut}：整张合成与分块合成");
            Battery.AssertBytesEqual(whole, file, $"切点 {cut}：预览合成与导出文件");

            SeamGeometry g = Geometry(s.P);
            Battery.AssertEqual(Expected, layout.CanvasSize.Height, "切点移动不改总高");
            Battery.Assert(g.CropsAppliedAsRequested, "裁切被布局夹小了：" + g);
            Battery.Assert(g.RectsDisjoint, "预览与导出读的是同一对不相交的裁切矩形");
            Battery.AssertEqual(g.BeforeSourceRect.Height + g.AfterSourceRect.Height, Expected,
                "两个裁切矩形的高度之和 = 画布高（没有第三路在画完整原图）");
        }
        Battery.Evidence("切点 0 / 20 / 60 三档：整张合成、分块合成、导出文件三路 sha 全等");
    }

    /// <summary>
    /// 需求三.5/三.6 + 需求七.5：重影与"被丢掉的那一份"只能是<b>调试</b>通路。
    /// 量三件事：① 调试取数确实能单独拿到被裁掉那一份（否则重影层是假的、"能核对"是空话）；
    /// ② 开/关重影层都不改参数、不改布局、不改导出字节；
    /// ③ 开着调试层导出的成品里，带里那一行仍只有一份。
    /// </summary>
    private static void DebugSourceNeverEntersExport()
    {
        // 全程一个项目：判据要问的是"这一层的开关改动会不会漏进这一份成品"，
        // 拿两个项目各量一半就成了"看起来对得上"的假判据。
        using CropRig rig = CropRig.New(tinted: true);
        Project p = rig.P;
        Sample s = rig.S;
        SeamGeometry g = Geometry(p);
        Battery.AssertEqual(Overlap, g.BeforeDroppedRect.Height, "默认档里被丢掉的是上一张的最后 O 行");

        PlacedSegment before = p.Layout.Placed[0];
        RgbaImage? discarded = s.Composer.RenderDebugSource(before, g.BeforeDroppedRect);
        Battery.Assert(discarded is not null,
            "调试取数必须能单独取到被裁掉的那一条，实际 null（矩形 " + g.BeforeDroppedRect + "）");
        Battery.AssertEqual(Overlap, discarded!.Height, "调试取数拿到的行数 = 被裁掉的行数");
        for (int i = 0; i < Overlap; i += 15)
        {
            Battery.Assert(discarded.Row(i).SequenceEqual(s.BeforeBandRow(i)),
                $"调试取数取的就是上一张源图第 {g.BeforeDroppedRect.Y + i} 行的原样，不是别处的内容");
        }
        // 这几行在<b>画布</b>上没有归属：上一张的画布矩形只到带首为止
        Battery.Assert(!before.Rect.Intersects(new IntRect(0, LineY, Width, Overlap)),
            "上一张的画布矩形不含带内那几行（正常合成通路根本拿不到它们）");

        int revision = p.Revision;
        CanvasLayout sameLayout = p.Layout;
        byte[] overlayOff = rig.Export();

        rig.Canvas.ShowCompareOverlay = false;
        rig.Canvas.ShowCompareOverlay = true;
        rig.Canvas.OverlapOpacityPercent = 15;
        rig.Canvas.Invalidate();

        Battery.AssertEqual(revision, p.Revision, "开关重影层不许改动项目参数");
        Battery.Assert(ReferenceEquals(sameLayout, p.Layout), "开关重影层不许让布局重算");
        byte[] overlayOn = rig.Export();
        Battery.AssertBytesEqual(overlayOff, overlayOn, "重影层开与关两次导出的字节");

        RgbaImage file = PngDecoder.Decode(overlayOn).Image;
        Battery.AssertEqual(1, LongScrollTests.CountRowsEqualTo(file, s.AfterBandRow(0)),
            "开着调试层导出的成品里，带里那一行仍只有一份");
        Battery.AssertEqual(0, LongScrollTests.CountRowsEqualTo(file, s.BeforeBandRow(0)),
            "被调试层画在屏幕上的那一份，一行都没进成品");
        Battery.Evidence($"调试取数拿到 {discarded.Height} 行被丢掉的内容；重影开关两次导出字节全等、Revision {revision} 未变");
    }

    // ═════════════════ ④ 蓝色拼缝线的交互（需求四、五、七.6） ═════════════════

    /// <summary>
    /// 需求七.6：拖蓝线<b>只</b>改裁切参数。逐条量：切点跟着手指走、两侧裁切量重摆、
    /// 总重叠 O 与画布高一字不动、视口一动不动、X 恒为 0（横着拖只回话不落库）、
    /// 参数只在松手时写一次、一次撤销退回整趟。
    ///
    /// 关于需求结尾提到的 <c>Segment.Origin.Y</c>：裁切档里两段的原点<b>确实</b>会随切点挪
    /// （前一段可见高度变了），但那是裁出来的结果而不是"把整张图挪了"——
    /// 所以这里同时量"总跨度与总高不变"，用它把两种实现分开。
    /// </summary>
    private static void LineDragMovesCutOnly()
    {
        using CropRig rig = CropRig.New();
        // 起点必须把蓝线摆在带中间：带内行号为 0 时"抓下方那张往下滑"一步都滑不动
        // （重叠与带内行号同减，减到负数就出带了），那一档只会报"滑到头"。
        rig.P.Mutate("预设蓝线", _ => rig.Seam.ApplyCut(30));
        int revision = rig.Revision;
        IntPoint topLeftBefore = rig.TopLeft;
        int heightBefore = rig.P.Layout.CanvasSize.Height;
        int originBefore = rig.P.Layout.Placed[1].Origin.Y;
        int lineBefore = Geometry(rig.P).CutYCanvas;
        int rect2Before = Geometry(rig.P).Rect2.Y;
        Battery.AssertEqual(170, lineBefore, "夹具自证：蓝线在画布第 170 行（200 − 60 + 30）");
        Battery.AssertEqual(140, rect2Before, "夹具自证：图2 的完整矩形顶边在画布第 140 行（200 − 60）");

        // 「调整」把手骑在蓝线正中：要量线那一档就得避开它，否则按下去只是开关微调模式。
        Rectangle handle = rig.Canvas.AdjustHandleDeviceRect(rig.Seam.Id);
        var press = new Point(handle.Right + 4, lineBefore);
        Battery.Assert(rig.Canvas.HitTestSeamEditRegion(press) is
            { Kind: StitchCanvasControl.SeamHitKind.CutLine }, "夹具自证：这一点命中的是蓝线本身");

        rig.Canvas.MousePress(MouseButtons.Left, Keys.None, press);
        rig.Canvas.MouseMoveTo(new Point(press.X, press.Y + 10));
        rig.Canvas.MouseMoveTo(new Point(press.X, press.Y + 20));

        // 预览期：参数一个字不写，但屏幕已经按预览那一组 (O, Cut) 画了
        Battery.AssertEqual(30, rig.Seam.CutPosition, "拖动过程中不许写切点参数（松手才提交，需求四.5）");
        Battery.AssertEqual(Overlap, rig.Seam.OverlapLength, "拖动过程中也不许写重叠");
        Battery.AssertEqual(revision, rig.Revision, "没松手就不该有修订号变化");
        Battery.AssertEqual(originBefore, rig.P.Layout.Placed[1].Origin.Y, "项目布局在松手前保持原样");
        Battery.Assert(rig.Canvas.UsingPreviewLayoutForTest,
            "预览布局必须真的挂着——它要是本来就是 false，上面那几句「参数没动」就成了恒真判据");
        Battery.Assert(rig.Canvas.HitTestSeam(new Point(press.X, lineBefore)) == rig.Seam.Id,
            "预览期间蓝线仍在原行（这一档的观感就是线不动、图动）");

        rig.Canvas.MouseRelease(new Point(press.X, press.Y + 20));

        Battery.AssertEqual(10, rig.Seam.CutPosition, "松手后带内行号 = 30 − 20（抓下方那张往下滑 20 行）");
        Battery.AssertEqual(Overlap - 20, rig.Seam.OverlapLength, "重叠同减 20：这一对必须一起动，线才钉得住");
        Battery.AssertEqual(Overlap - 20 - 10, rig.Seam.BeforeCropBottom, "上一张底部裁切 = O − 切点");
        Battery.AssertEqual(10, rig.Seam.AfterCropTop, "下一张顶部裁切 = 切点");
        Battery.Assert(rig.Seam.CropInvariantHolds, "不变式 b + t = O");
        Battery.AssertEqual(lineBefore, Geometry(rig.P).CutYCanvas,
            $"蓝线的画布行号必须一字不动（{lineBefore}）");
        Battery.AssertEqual(SeamCropMode.Manual, rig.Seam.CropMode, "切点来源要记成手动");
        Battery.Assert(rig.Seam.IsManualCut, "滑过必须留痕（重新分析不许抹掉他选的那一份）");
        Battery.Assert(rig.Seam.IsManualAlignment, "重叠也被这趟改了，同样要留痕");
        Battery.Assert(rig.Seam.ManualOffset is null,
            "长截图里滑动图片不该写位移（需求一：不要把裁切实现成移动整个 Segment）");
        Battery.AssertEqual(heightBefore + 20, rig.P.Layout.CanvasSize.Height,
            "重叠少 20 行 ⇒ 画布总高多 20 行（这是这一档的物理结果，不是偷偷改了别的量）");
        Battery.AssertEqual(topLeftBefore, rig.TopLeft, "抓下方那张时视口一动不动（画布坐标里线本来就没动）");
        Battery.AssertEqual(revision + 1, rig.Revision, "整趟拖动只记一条改动");
        Battery.AssertEqual("滑动图片", rig.P.NextUndoLabel, "撤销栈顶就是这次滑动");
        Battery.AssertEqual(FixedSide.Before, rig.Seam.Fixed, "抓下方那张 ⇒ 屏上钉住的是上方那张");
        Battery.AssertEqual(0, rig.P.Layout.Placed[1].Origin.X, "后一段 X 恒为 0");
        Battery.AssertEqual(0, rig.P.Layout.EffectiveOffset(rig.Seam).X, "生效位移 X 恒为 0");
        Battery.AssertEqual(0, rig.Seam.ManualOffset?.X ?? 0, "参数里也没有横向值");
        Battery.AssertEqual(originBefore, rig.P.Layout.Placed[1].Origin.Y,
            $"图2 的<b>可见</b>顶边就是蓝线那一行（{lineBefore}）：线不动 ⇒ 它也不动。" +
            "动的是它下面露出来的内容——完整矩形那一头才真的被推下了 20 行");
        Battery.AssertEqual(rect2Before + 20, Geometry(rig.P).Rect2.Y,
            $"图2 的<b>完整</b>矩形被推下 20 行（{rect2Before}→{Geometry(rig.P).Rect2.Y}）：" +
            "这一头才是「图片在动」，可见顶边被蓝线钉住所以看不出位移");

        // 横着拖：只回话，不落任何参数
        int locks = 0;
        rig.Canvas.HorizontalShiftRejected += _ => locks++;
        var sidePress = new Point(press.X, Geometry(rig.P).CutYCanvas);
        rig.Canvas.MousePress(MouseButtons.Left, Keys.None, sidePress);
        rig.Canvas.MouseMoveTo(new Point(sidePress.X + 30, sidePress.Y));
        rig.Canvas.MouseRelease(new Point(sidePress.X + 30, sidePress.Y));
        Battery.Assert(locks >= 1, "横向意图必须回话，不能悄悄吞掉");
        Battery.AssertEqual(10, rig.Seam.CutPosition, "纯横向拖动不该改动切点");
        Battery.AssertEqual(topLeftBefore, rig.TopLeft, "在编辑区里的横向拖也不该顺手平移画布");

        rig.P.Undo();
        Battery.AssertEqual(30, rig.Seam.CutPosition, "一次撤销退回整趟滑动（带内行号回到 30）");
        Battery.AssertEqual(Overlap, rig.Seam.OverlapLength, "重叠也一起退回来");
        Battery.AssertEqual(heightBefore, rig.P.Layout.CanvasSize.Height, "撤销把总高也还回去");
        Battery.AssertEqual(lineBefore, Geometry(rig.P).CutYCanvas, "撤销后蓝线仍在原行");
        Battery.Evidence($"压线拖 20 设备像素：Cut 30→10、O 60→40、蓝线画布行号恒为 {lineBefore}、" +
                         $"总高 {heightBefore}→{heightBefore + 20}、TopLeft {topLeftBefore} 未动；一条撤销整趟退回");
    }

    /// <summary>
    /// 需求八：seam.slide.* 每一行都要带齐那批字段。
    /// 判据读的就是<b>日志自己那行字符串</b>（<c>LastSeamEditTraceForTest.Fields()</c>），
    /// 所以"界面上动了、日志里没记"或"记了另一套名字"都会当场红。
    /// 「线不动、图动」这一轮又添了六个读数（蓝线的画布行号与设备行号各一对、动没动、抓的哪一张）：
    /// 只给 O 与 Cut 让人自己推"线有没有跑"，等于把判据留给读日志的人。
    /// 最后再交叉核对需求五的第三件事：平移那一趟里裁切一字未动。
    /// </summary>
    private static void SlideTraceCarriesRequiredLogFields()
    {
        using CropRig rig = CropRig.New();
        // 起点蓝线必须挪进带里：Cut=0 时"抓下方那张往下拖"已经滑到头（滑动量被 min(O,Cut) 夹成 0），
        // 那一趟会诚实地报"滑到头"而不改任何参数 ⇒ 没有流水账，这一格就红成"工装没量到"。
        MoveCut(rig.P, 30);
        Battery.AssertEqual(30, rig.Seam.CutPosition, "夹具自证：起点蓝线在带里第 30 行");

        // 起手要避开那枚「调整」把手：按在把手上只会开关微调档、<b>不开拖动手势</b>
        //（判据 `align/命中四档各归其位：把手开关微调、线上与带里都滑动那张图`），
        // 于是 LastSeamEditTraceForTest 会是 null，这一格红得莫名其妙。
        // 纵向取<b>当下</b>的蓝线画布行号，不能用常量 LineY：那是 Cut=0 时线的位置（带顶），
        // 蓝线挪到 30 之后线在 170，按 140 只按到带顶那条红箭头上。
        int lineRow = Geometry(rig.P).CutYCanvas;
        Battery.AssertEqual(170, lineRow, "夹具自证：预设之后蓝线在画布第 170 行");
        Point start = new(rig.Canvas.AdjustHandleDeviceRect(rig.Seam.Id).Right + 4, lineRow);
        Battery.AssertEqual(StitchCanvasControl.SeamHitKind.CutLine,
            rig.Canvas.HitTestSeamEditRegion(start)?.Kind ?? StitchCanvasControl.SeamHitKind.AdjustHandle,
            "夹具自证：起手点必须命中蓝线本身，不是把手、也不是带里");
        rig.Canvas.MousePress(MouseButtons.Left, Keys.None, start);
        rig.Canvas.MouseMoveTo(new Point(start.X, lineRow + 10));
        rig.Canvas.MouseMoveTo(new Point(start.X, lineRow + 20));     // 压着蓝线往下拖 20 = 挪下方那张图
        rig.Canvas.MouseRelease(new Point(start.X, lineRow + 20));

        StitchCanvasControl.SeamEditTrace? trace = rig.Canvas.LastSeamEditTraceForTest;
        Battery.Assert(trace is not null, "压着蓝线滑过一趟必须留下流水账（日志拼的就是这一份数）");
        Battery.Assert(trace!.CropMode, "长截图里的这次手势要标成裁切档，事件前缀才是 seam.slide");
        Battery.AssertEqual("seam.slide", trace.EventPrefix, "事件前缀按档位分（位移档用 seam.edit）");

        string line = trace.Fields();
        // 字段名按需求十一逐字对（viewportBefore/viewportAfter、canvasHeightBefore/After、undoPushed）：
        // 同义但不同名的写法，等于给"按需求列的名字去 grep"的人一条空结果 —— 那读不出任何东西。
        string[] required =
        {
            "seam=", "sourceRect1=", "sourceRect2=",
            "before_crop_bottom=", "after_crop_top=", "overlap=",
            "cutYBefore=", "cutYAfter=", "canvasHeightBefore=", "canvasHeightAfter=",
            "viewportBefore=", "viewportAfter=", "mutated=", "undoPushed=",
            "alignmentOffsetBefore=", "alignmentOffsetAfter=", "rect1=", "rect2=",
            // 第十三轮需求七那批 before/after：带、两块矩形在<b>屏幕上</b>的位置、蓝线比例、固定侧。
            // 名字逐字取需求原文（fixedSide 不是 fixed_side）——按需求列的名字去 grep 的人要能命中。
            "overlapRectBefore=", "overlapRectAfter=",
            "rect1DeviceBefore=", "rect1DeviceAfter=", "rect2DeviceBefore=", "rect2DeviceAfter=",
            "cutRatioBefore=", "cutRatioAfter=", "fixedSide=",
            // 「线不动、图动」这一轮新加的六个：蓝线的画布行号与设备行号（各要 before/after）、
            // 设备行号到底动没动、以及这一趟抓的是上方还是下方那张。
            "lineCanvasRowBefore=", "lineCanvasRowAfter=",
            "lineDeviceBefore=", "lineDeviceAfter=", "line_device_moved=", "grabbed=",
        };
        foreach (string key in required)
        {
            Battery.Assert(line.Contains(key, StringComparison.Ordinal),
                "需求十一要求的字段 " + key + " 没出现在这一行里，实际：" + line);
            // 每个名字在这一行里只许出现一次：两处同值就是契约，迟早漂移
            //（第十二轮之前那句 `canvas_height_before=` 在 Fields() 与 commit 行尾各写了一次）。
            Battery.AssertEqual(1, Regex.Matches(line, Regex.Escape(key)).Count,
                "字段 " + key + " 在一行里出现了不止一次，实际：" + line);
        }

        Battery.Assert(line.Contains("crop_mode=true", StringComparison.Ordinal), "裁切档要明写 crop_mode=true");
        Battery.Assert(line.Contains("cutYBefore=30", StringComparison.Ordinal), "起点蓝线要记进日志");
        Battery.Assert(line.Contains("cutYAfter=10", StringComparison.Ordinal),
            "抓下方那张往下拖 20 行 ⇒ 带里行号同步走 20（30→10），画布行号才不动");
        Battery.Assert(line.Contains("before_crop_bottom=30", StringComparison.Ordinal),
            "前段裁切量 = O − 切点：60−30 与 40−10 都是 30，这一头本来就不该动");
        Battery.Assert(line.Contains("after_crop_top=10", StringComparison.Ordinal), "后段裁切量 = 切点");
        Battery.Assert(line.Contains("overlap=40", StringComparison.Ordinal), "总重叠压掉 20 行后要单独写出来");
        Battery.Assert(line.Contains("height_changed=true", StringComparison.Ordinal),
            "滑动这张图<b>就是</b>在改总高（旧口径那句「总高不许变」属于"
            + "「线动图不动」那一套，已经作废）——不变才说明这一趟什么都没滑");
        Battery.Assert(line.Contains("mutated=true", StringComparison.Ordinal), "提交要报 mutated=true");
        Battery.Assert(line.Contains("undoPushed=true", StringComparison.Ordinal), "真压进撤销栈才算 undoPushed");
        Battery.Assert(Regex.Matches(line, "mutated=").Count == 1,
            "一行里不许出现两次 mutated=（两处同值迟早漂移，日志也一样）");
        Battery.Assert(line.Contains("grabbed=After", StringComparison.Ordinal),
            "压着蓝线按下抓的就是下方那张图，日志要写出来（否则查不出用户到底挪了哪一张）");

        SeamGeometry g = Geometry(rig.P);
        string rect = $"sourceRect1=({g.BeforeSourceRect.X},{g.BeforeSourceRect.Y}," +
                      $"{g.BeforeSourceRect.Width},{g.BeforeSourceRect.Height})";
        Battery.Assert(line.Contains(rect, StringComparison.Ordinal),
            "日志里的上一张矩形要等于布局实际用的那对 " + g.BeforeSourceRect + "，实际行：" + line);
        Battery.Assert(line.Contains("viewportAfter=(0,0)", StringComparison.Ordinal),
            "抓下方那张时视口原点必须还是 (0,0)：这一档不需要补偿，补偿是抓上方那张才有的事");

        // ═══ 「线不动、图动」在<b>屏幕坐标</b>上的投影 ═══
        // 抓下方那张 ⇒ 屏上钉住的是上方那张：图1 的设备矩形一字不动，
        // 而蓝线自己的画布行号与设备行号都不动（用户眼里那条参照线没被拖走），
        // 动的是图2（它跟着手往下走了 20 设备像素）与带（重叠压掉 20 行）。
        Battery.AssertEqual(trace.Rect1DeviceBefore, trace.Rect1DeviceAfter,
            "抓下方那张时图1 在屏幕上一个像素都不许动（它就是被钉住的那一张）");
        Battery.Assert(trace.Rect2DeviceAfter.Y == trace.Rect2DeviceBefore.Y + 20,
            $"图2 必须跟着手往下走 20 设备像素（它才是被挪的那一张）："
            + $"{trace.Rect2DeviceBefore.Y}→{trace.Rect2DeviceAfter.Y}");
        Battery.AssertEqual(170, trace.LineCanvasRowBefore, "夹具自证：起点蓝线在画布第 170 行（200−60+30）");
        Battery.AssertEqual(trace.LineCanvasRowBefore, trace.LineCanvasRowAfter,
            $"蓝线的画布行号必须一动不动：{trace.LineCanvasRowBefore}→{trace.LineCanvasRowAfter}");
        Battery.AssertEqual(trace.LineDeviceBefore, trace.LineDeviceAfter,
            $"蓝线的设备行号也必须一动不动：{trace.LineDeviceBefore}→{trace.LineDeviceAfter}");
        Battery.Assert(!trace.LineDeviceMoved, "line_device_moved 那一格必须读成 false：这就是「线不动」的读数");
        Battery.Assert(line.Contains("line_device_moved=false", StringComparison.Ordinal),
            "「线没动」要能在日志里直接 grep 出来，不许只给两个行号让人自己比");
        Battery.AssertEqual(60, trace.OverlapRectBefore.Height, "夹具自证：起点带高 60 行");
        Battery.AssertEqual(40, trace.OverlapRect.Height, "带被压到 40 行（带区就是 Rect1∩Rect2）");
        Battery.Assert(trace.AlignmentChanged, "滑动这张图靠的就是改对齐位移，不变才说明没滑");
        // 蓝线在带里的<b>相对位置</b>这一轮不再守恒（守恒的是画布行号）：
        // 30/60=0.5 → 10/40=0.25。这两个比例仍然要写进日志，事后才查得出带被压缩了多少。
        Battery.Assert(trace.CutRatioAfter < trace.CutRatioBefore,
            $"抓下方那张往下滑，带被压缩 ⇒ 蓝线在带里的相对位置变小（{trace.CutRatioBefore:F4}→{trace.CutRatioAfter:F4}）");
        Battery.Assert(line.Contains("cutRatioBefore=0.5000", StringComparison.Ordinal) &&
                       line.Contains("cutRatioAfter=0.2500", StringComparison.Ordinal),
            "比例这一对要写进日志：30/60=0.5 → 10/40=0.25，事后能一眼看出带被压掉了三分之一");

        // 需求五交叉核对：平移那一趟不许碰裁切。
        // 必须先退出选中（SelectSeam(null) 顺手关掉微调模式）：压过蓝线之后模式是开着的，
        // 那一下点空白会被读成"点选上方那张图"而不是平移，这一格就会红在工装自己身上。
        rig.Canvas.SelectSeam(null);
        Battery.Assert(!rig.Canvas.AdjustModeActive, "夹具自证：退出选中之后微调模式必须已经关掉");
        int cutNow = rig.Seam.CutPosition, overlapNow = rig.Seam.OverlapLength;
        rig.Canvas.MousePress(MouseButtons.Left, Keys.None, new Point(40, 40));
        rig.Canvas.MouseMoveTo(new Point(40, 10));
        rig.Canvas.MouseRelease(new Point(40, 10));
        Battery.AssertEqual(cutNow, rig.Seam.CutPosition, "平移视口不许改动切点");
        Battery.AssertEqual(overlapNow, rig.Seam.OverlapLength, "平移视口不许改动总重叠");
        Battery.Assert(rig.TopLeft != IntPoint.Zero, "夹具自证：这一趟确实平移了");
        Battery.Evidence($"seam.slide.* 字段齐备（含那对源矩形、grabbed=After 与 line_device_moved=false）；" +
                         $"蓝线画布行号 {trace.LineCanvasRowBefore} 全程未动；随后平移视口 {rig.TopLeft} 未碰裁切");
    }

    /// <summary>
    /// 需求七.7：点普通画布区域只平移视口，裁切参数与<b>导出字节</b>一字不改。
    /// 与上一格是一对：那一格证"拖裁切不动视口"，这一格证"动视口不动裁切"。
    /// </summary>
    private static void BlankAreaPansOnly()
    {
        using CropRig rig = CropRig.New();
        MoveCut(rig.P, 25);               // 先有一个非默认切点，才量得出"没被抹回默认档"
        int revision = rig.Revision;
        byte[] before = rig.Export();

        rig.Canvas.MousePress(MouseButtons.Left, Keys.None, new Point(40, 40));
        rig.Canvas.MouseMoveTo(new Point(60, 20));
        rig.Canvas.MouseMoveTo(new Point(80, 0));
        Battery.AssertEqual(revision, rig.Revision, "拖动过程中不该有修订号变化");
        rig.Canvas.MouseRelease(new Point(80, 0));

        Battery.AssertEqual(new IntPoint(-40, 40), rig.TopLeft, "画布跟着鼠标走");
        Battery.AssertEqual(revision, rig.Revision, "平移一个字节的参数都不许改");
        Battery.AssertEqual(25, rig.Seam.CutPosition, "切点保持用户选的那一格");
        Battery.AssertEqual(Overlap, rig.Seam.OverlapLength, "总重叠不变");
        Battery.AssertEqual(Overlap - 25, rig.Seam.BeforeCropBottom, "两侧裁切量不变");
        Battery.Assert(rig.Seam.ManualOffset is null, "平移不许留下手动位移");
        // 必须逐字节比较：EqualityComparer<byte[]>.Default 比的是引用，两次导出永远拿到两个不同的数组。
        Battery.AssertBytesEqual(before, rig.Export(), "视口平移前后两次导出的字节（需求五.1：平移不改导出内容）");
        Battery.Assert(rig.Canvas.LastSeamEditTraceForTest is null,
            "带外按下不该开裁切编辑的流水账（开了就说明命中区又管太宽了）");
        Battery.Evidence($"点空白拖 (40,40)→(80,0)：TopLeft→{rig.TopLeft}，切点仍 25、导出字节全等");
    }

    // ═════════════════ ⑤ 结构性质：不相交 + 红框=两个裁切矩形 ═════════════════

    /// <summary>
    /// 需求一结尾那句"不能只依靠两个完整 Segment 的坐标重叠来看起来去重"的结构证明：
    /// 无论切点在哪、重叠多大，任意两段的画布矩形都不相交，
    /// 于是"每个画布点至多来自一段"成立，<see cref="CanvasLayout.CanvasToSource"/> 不再有歧义，
    /// 差异视图/检测取数这类"逐段拿像素"的通路也不可能拿到两份。
    /// </summary>
    private static void RectsAreAlwaysDisjoint()
    {
        foreach (int overlap in new[] { 0, 20, 60 })
        {
            foreach (int wantedCut in new[] { 0, 7, 30, 59, 60 })
            {
                int cut = Math.Min(wantedCut, overlap);
                Sample s = NewSample(tinted: false);
                WriteAuto(s.P, overlap);
                if (cut > 0) MoveCut(s.P, cut);
                SeamGeometry g = Geometry(s.P);

                Battery.Assert(g.RectsDisjoint,
                    $"切点 {cut}、重叠 {overlap} 时两段仍在交叠：" + g);
                Battery.AssertEqual(s.P.Layout.Placed[0].Rect.Bottom, s.P.Layout.Placed[1].Rect.Y,
                    "两段首尾相接（既不交叠也不留空洞）");
                Battery.AssertEqual(Height + Height - overlap, s.P.Layout.CanvasSize.Height,
                    "总高只由 O 决定，与切点无关");

                // 画布上任意一点至多落在一段里：这条直接否定"重叠区画了两份"
                for (int y = 0; y < s.P.Layout.CanvasSize.Height; y += 7)
                {
                    int hits = 0;
                    foreach (PlacedSegment pl in s.P.Layout.Placed)
                        if (!pl.IsEmpty && pl.Rect.Contains(new IntPoint(30, y))) hits++;
                    Battery.AssertEqual(1, hits, $"画布行 {y} 被几段覆盖（应恰好 1 段）");
                }
                Battery.AssertEqual(0, g.CrossOffset, "锁横向：交叉轴位移恒为 0");
            }
        }
        Battery.Evidence("三档重叠 × 五档切点共 15 组：矩形全不相交、每行恰好一段覆盖、总高只随 O 变");
    }

    /// <summary>
    /// 用户特别叮嘱的那一条：<b>OverlapBand 不能只是一个可拖的显示区域</b>，
    /// 它必须真的对应两个裁切矩形——带的上边界由"上一张的 SourceRect.Bottom 往上 t 行"定，
    /// 带的下边界由"下一张的 SourceRect.Top 往下 b 行"定，
    /// 而带的<b>分界线</b>就是这两个源矩形各自的接缝（切点）。
    /// 这里把带的四边与两个裁切矩形、两个被裁矩形逐一对齐；任一边画歪都说明命中区与裁切分家了。
    /// </summary>
    private static void BandMapsToTwoCrops()
    {
        foreach (int cut in new[] { 0, 12, 30, 60 })
        {
            Sample s = NewSample(tinted: false);
            WriteAuto(s.P, Overlap);
            if (cut > 0) MoveCut(s.P, cut);
            SeamGeometry g = Geometry(s.P);
            int t = cut, b = Overlap - cut;

            Battery.Assert(g.HasBand, $"重叠 {Overlap} 行就该有带（切点 {cut}）");
            Battery.AssertEqual(Overlap, g.BandRect.Height, "带高 = 两侧裁切之和 = 重叠");
            Battery.Assert(g.BandMatchesDedup, "带高与去重量对不上：" + g);
            Battery.AssertEqual(g.BeforeRect.Bottom - t, g.BandRect.Y,
                "带顶 = 上一张可见底边 − t（带里上面 t 行由上一张提供）");
            Battery.AssertEqual(g.BeforeRect.Bottom + b, g.BandRect.Bottom,
                "带底 = 上一张可见底边 + b（下面 b 行由下一张提供）");
            Battery.AssertEqual(g.AfterRect.Y - t, g.BandRect.Y, "带顶同样等于下一张顶边往上 t 行");
            Battery.AssertEqual(g.BandRect.Y + t, g.AfterRect.Y,
                "带的分界线 = 两个源矩形在画布上的接缝（切点就在这条线上）");

            // 需求结尾点名的那两个数：上图的 SourceRect.Bottom、下图的 SourceRect.Top
            Battery.AssertEqual(Height - b, g.BeforeSourceRect.Bottom,
                "上一张 SourceRect.Bottom 随切点移动（= H1 − BeforeCropBottom）");
            Battery.AssertEqual(t, g.AfterSourceRect.Y,
                "下一张 SourceRect.Top 随切点移动（= AfterCropTop）");

            // 被裁掉的两条源矩形必须正好贴在两个裁切矩形的外侧。
            // 某一侧裁 0 行时"被裁矩形"是空的（default），那条相邻判据就没得量——
            // 分两种情况写，否则切点=0 或 =O 的那两档会拿 0 去比非 0，红得莫名其妙。
            if (b > 0)
            {
                Battery.AssertEqual(g.BeforeSourceRect.Bottom, g.BeforeDroppedRect.Y,
                    "上一张被裁那一条的起点 = 它的 SourceRect.Bottom");
            }
            else
            {
                Battery.AssertEqual(0, g.BeforeDroppedRect.Height, "上一张一行都没裁时不该有被裁矩形");
            }
            if (t > 0)
            {
                Battery.AssertEqual(g.AfterDroppedRect.Bottom, g.AfterSourceRect.Y,
                    "下一张被裁那一条的终点 = 它的 SourceRect.Top");
            }
            else
            {
                Battery.AssertEqual(0, g.AfterDroppedRect.Height, "下一张一行都没裁时不该有被裁矩形");
            }
            Battery.AssertEqual(b, g.BeforeDroppedRect.Height, "上一张被裁行数 = BeforeCropBottom");
            Battery.AssertEqual(t, g.AfterDroppedRect.Height, "下一张被裁行数 = AfterCropTop");
        }
        Battery.Evidence("四档切点：带四边都落在两个源矩形上，被裁矩形紧贴在裁切矩形外侧、行数各等于 b 与 t");
    }

    /// <summary>
    /// 本轮改造必须<b>不改成品</b>：默认档（t=0、整带取下一张）与第八轮那种
    /// "后一段整张画在下面盖住前一段"的写法逐字节相同。
    /// 参照实现故意写在测试里（不复用产品代码）——它的价值是证明"这次重构是行为保持的"，
    /// 老写法的形状在这里是独立复现的。
    /// </summary>
    private static void DefaultCutEqualsLegacyOverwrite()
    {
        Sample s = NewSample(tinted: false);
        WriteAuto(s.P, Overlap);

        // 参照实现：老写法 = 两段完整尺寸 + offsetY = H1 − O + 后画的盖先画的
        int offsetY = Height - Overlap;
        var legacy = new RgbaImage(Width, offsetY + s.After.Height);
        legacy.Fill(RgbaColor.Transparent);
        legacy.ReplaceRegion(s.Before, s.Before.Bounds, new IntRect(0, 0, Width, Height));
        legacy.ReplaceRegion(s.After, s.After.Bounds, new IntRect(0, offsetY, Width, s.After.Height));

        RgbaImage modern = s.Composer.RenderWhole(s.P.Layout);
        Battery.AssertEqual(legacy.Height, modern.Height, "裁切档与老写法的画布高");
        var region = new IntRect(0, 0, Width, legacy.Height);
        Battery.AssertBytesEqual(legacy.RegionSha256(region), modern.RegionSha256(region),
            "默认切分（t=0）与旧的覆盖式落位");

        // 但两者的<b>成因</b>不同：老写法里前一段那 O 行进了像素再被盖住，裁切档里根本没进
        SeamGeometry g = Geometry(s.P);
        Battery.Assert(g.RectsDisjoint, "裁切档里两段不再相交（这就是与老写法的唯一区别）");
        Battery.AssertEqual(Overlap, g.AppliedBeforeCrop, "前一段被真裁掉了 O 行");
        Battery.AssertEqual(0, g.AppliedAfterCrop, "后一段一行都不裁");
        Battery.AssertEqual(Height - Overlap, g.BeforeSourceRect.Bottom,
            "前一段的源矩形底边就在带首（那 O 行不在矩形里，而不是被盖住）");
        Battery.Evidence($"成品像素与老写法全等（{legacy.Height} 行），差别只在两段不再相交");
    }

    /// <summary>
    /// 需求五.2：位移档（竖拼/横拼）里位移就是落位，<b>不走裁切</b>。
    /// 反证"长截图那两条规则不许外溢"：即使参数里存着裁切量，布局也不裁源矩形；
    /// 但参数要原样留着（用户切回长截图时他的切点还在）。
    /// </summary>
    private static void OffsetModesNeverCrop()
    {
        foreach (StitchMode mode in new[] { StitchMode.Vertical, StitchMode.Horizontal })
        {
            bool horizontal = mode == StitchMode.Horizontal;
            RgbaImage a = Fixture.OpaqueLike(Height, Height, seed: 301);
            RgbaImage b = Fixture.OpaqueLike(Height, Height, seed: 302);
            (Project p, MemoryResolver _, _) = TestProject.Create(mode, a, b);

            p.Mutate("位移档塞进裁切参数", d =>
            {
                d.Seams[0].SetOverlap(Overlap);
                d.Seams[0].ApplyCut(20);
                // 主轴上留 150 行的重叠（横拼写 X，竖拼写 Y）
                d.Seams[0].ManualOffset = horizontal ? new IntPoint(50, 0) : new IntPoint(0, 50);
            });

            Battery.Assert(!p.Layout.UsesCropPlacement, mode + " 不该走裁切落位");
            SeamGeometry g = Geometry(p);
            Battery.Assert(!g.CropPlacement, "去重账要标明这不是裁切档");
            Battery.AssertEqual(0, g.AppliedBeforeCrop, mode + " 里布局不裁上一张");
            Battery.AssertEqual(0, g.AppliedAfterCrop, mode + " 里布局不裁下一张");
            Battery.AssertEqual(new IntRect(0, 0, Height, Height), g.BeforeSourceRect,
                mode + " 里源矩形保持完整（位移档裁的是画布上的落位，不是像素）");
            Battery.AssertEqual(Overlap - 20, p.Doc.Seams[0].BeforeCropBottom,
                "参数原样存着（切回长截图要用他的切点），只是这一档不生效");
            Battery.Assert(!g.RectsDisjoint, "夹具自证：位移档里两段确实还相交（这才是它该长的样子）");

            int main = horizontal ? p.Layout.CanvasSize.Width : p.Layout.CanvasSize.Height;
            Battery.AssertEqual(250, main, mode + " 按位移落位：主轴长 = 位移 + 后段主轴长");
        }
        Battery.Evidence("竖拼/横拼：裁切参数原样存着但源矩形与落位一律不裁；两段仍可交叠");
    }

    // ═════════════════ ⑥ 存档与迁移（不变式的历史面） ═════════════════

    /// <summary>
    /// 裁切字段是新加的，<b>v1 项目文件里没有</b>。反序列化得到 b=0、t=0 而 O=60，
    /// 那不代表"用户选择不裁"，只代表"那个版本还没有裁切概念"。
    /// 这里删掉这些键模拟旧文件，要求读档后补成默认档（b=O、t=0）且不变式成立——
    /// 于是老文件打开后的成品与它当年导出的逐字节相同。
    /// 顺带量两种坏形状（手工编辑过的文件）：
    /// 自动档 (10,10) 修回默认档；手动档保住用户的切点、只重摆另一侧。
    /// </summary>
    private static void OldProjectFileMigratesToDefaultCut()
    {
        Sample s = NewSample(tinted: false);
        WriteAuto(s.P, Overlap);
        string json = ProjectFile.Serialize(s.P.Doc);
        Battery.Assert(json.Contains(@"""BeforeCropBottom""", StringComparison.Ordinal),
            "夹具自证：当前写法确实把裁切字段写进了项目文件");

        // 用 DOM 删键而不是正则：正则只认得"标量 + 逗号"，而 AlignmentOffset 是个对象，
        // 漏删一个键就会让这格测的是"新键没删干净"而不是"旧文件能不能迁移"。
        string v1 = WithoutSeamKeys(json,
            "BeforeCropBottom", "AfterCropTop", "CropMode", "IsManualCut",
            "AutoAfterCropTop", "AutoCutY", "CutY", "AlignmentOffset", "AutoOverlapLength");
        Battery.Assert(v1.Length < json.Length, "夹具自证：删字段这一步真删掉了（不是拿原文件自欺欺人）");
        Battery.Assert(!v1.Contains(@"""AfterCropTop""", StringComparison.Ordinal),
            "夹具自证：裁切键已从这份 JSON 里消失");

        Seam migrated = ProjectFile.Deserialize(v1).Seams[0];
        Battery.AssertEqual(Overlap, migrated.OverlapLength, "旧文件里的重叠照旧读出 60");
        Battery.AssertEqual(Overlap, migrated.BeforeCropBottom, "缺字段补成默认档：上一张裁底 = O");
        Battery.AssertEqual(0, migrated.AfterCropTop, "缺字段补成默认档：下一张不裁顶");
        Battery.Assert(migrated.CropInvariantHolds, "补完之后不变式成立");
        Battery.AssertEqual(SeamCropMode.Auto, migrated.CropMode, "旧文件没有手动切点这一说，来源记自动");

        // 半套参数（手工编辑过）：两侧都写成 10 而 O=60
        string brokenAuto = WithSeamKeys(json, ("AfterCropTop", 10), ("BeforeCropBottom", 10));
        Seam repairedAuto = ProjectFile.Deserialize(brokenAuto).Seams[0];
        Battery.Assert(repairedAuto.CropInvariantHolds, "自动档的坏参数要修回不变式");
        Battery.AssertEqual(Overlap, repairedAuto.BeforeCropBottom,
            "自动档：b 补回 O、t 归 0（回到旧行为，成品不变）");

        // 用户真切过 ⇒ 保住他的切点，只重摆另一侧
        MoveCut(s.P, 30);
        string brokenManual = WithSeamKeys(ProjectFile.Serialize(s.P.Doc), ("BeforeCropBottom", 10));
        Seam kept = ProjectFile.Deserialize(brokenManual).Seams[0];
        Battery.AssertEqual(30, kept.CutPosition, "手动档：用户切的第 30 行不许被抹回带首");
        Battery.AssertEqual(Overlap - 30, kept.BeforeCropBottom, "另一侧补齐，b + t = O");
        Battery.Assert(kept.IsManualCut, "手动来源要保住（读档不该改他的意图）");

        // 正常往返：切点原样回来
        Seam round = ProjectFile.Deserialize(ProjectFile.Serialize(s.P.Doc)).Seams[0];
        Battery.AssertEqual(30, round.CutPosition, "存盘再读回，切点原样");
        Battery.Assert(round.CropInvariantHolds, "往返之后不变式仍成立");
        Battery.Evidence("v1（无字段）→ (60,0)；自动坏参数 (10,10) → (60,0)；手动坏参数 (10,30) → (30,30) 保住切点");
    }

    /// <summary>
    /// 需求 §七 最后那句「兼容旧字段必须在布局层明确转换，不能让多个字段互相覆盖」的可机器核对形式：
    /// 读档时别名写的是<b>不同</b>的核心字段（<c>OverlapLength</c>→<c>AlignmentOffset</c>、
    /// <c>AfterCropTop</c>→<c>CutY</c>、<c>BeforeCropBottom</c> 已无写入口），
    /// 所以 JSON 里谁先到达都不许改变最终那一组核心值。
    ///
    /// 第十一轮的缺陷正是"两个别名都往一个字段写"：那时 <c>BeforeCropBottom=10</c> 会把蓝线顶成 50、
    /// 随后 <c>AfterCropTop</c> 再写一遍，落库的是<b>到达顺序的产物</b>。
    /// 所以这一格除了三种顺序，还专门量"只读别名带一个矛盾值、排前排后都不许改结论"。
    /// 正向对照（只留 <c>AfterCropTop</c> 时蓝线必须跟着走）是必需的：
    /// 没有它，三趟同数可能只是"这些键根本没被读进来"。
    /// </summary>
    private static void LegacyAliasOrderNormalizesToSameCoreValues()
    {
        Sample s = NewSample(tinted: false);
        WriteAuto(s.P, Overlap);
        MoveCut(s.P, 20);           // 手动档 + 蓝线不在带首/带尾：三趟同数才不是恒真
        string json = ProjectFile.Serialize(s.P.Doc);

        string[] orders =
        {
            ReorderSeamKeys(json, "OverlapLength", "AfterCropTop", "BeforeCropBottom", "CutY"),
            ReorderSeamKeys(json, "CutY", "BeforeCropBottom", "AfterCropTop", "OverlapLength"),
            ReorderSeamKeys(json, "BeforeCropBottom", "CutY", "OverlapLength", "AfterCropTop"),
        };
        Battery.Assert(orders[0] != orders[1] && orders[1] != orders[2],
            "夹具自证：重排键位没生效（三份文件一模一样），那这三趟读的是同一份 JSON");

        string[] labels = { "重叠别名在前", "核心字段在前", "别名与核心字段交错" };
        for (int i = 0; i < orders.Length; i++)
        {
            Seam seam = ProjectFile.Deserialize(orders[i]).Seams[0];
            Battery.AssertEqual(Overlap, seam.OverlapLength, $"{labels[i]}到达：总重叠仍 60");
            Battery.AssertEqual(20, seam.CutPosition, $"{labels[i]}到达：蓝线仍在带内第 20 行");
            Battery.AssertEqual(Overlap - 20, seam.BeforeCropBottom,
                $"{labels[i]}到达：另一侧 40 行由蓝线派生，而不是被那个键自己决定");
            Battery.Assert(seam.IsManualCut, $"{labels[i]}到达：手动来源不许被顺序吃掉");
        }

        // 只读别名带一个与蓝线矛盾的值（O=60、b 写成 50 ⇒ 若它还能写就等于把蓝线搬到 10）
        string conflict = WithSeamKeys(json, ("BeforeCropBottom", 50));
        Battery.AssertEqual(20, ProjectFile.Deserialize(
            ReorderSeamKeys(conflict, "BeforeCropBottom", "CutY", "OverlapLength", "AfterCropTop")).Seams[0].CutPosition,
            "BeforeCropBottom 排在最前也不许把蓝线顶到第 10 行");
        Battery.AssertEqual(20, ProjectFile.Deserialize(
            ReorderSeamKeys(conflict, "CutY", "OverlapLength", "AfterCropTop", "BeforeCropBottom")).Seams[0].CutPosition,
            "同一份冲突文件换个顺序，结论必须一字不变（顺序决定结果就是 §七 禁止的形状）");

        // 正向对照：别名那条写入通路是活的，不是"整份文件没人读"
        Seam viaAlias = ProjectFile.Deserialize(
            WithSeamKeys(WithoutSeamKeys(json, "CutY"), ("AfterCropTop", 45))).Seams[0];
        Battery.AssertEqual(45, viaAlias.CutPosition,
            "正向对照：删掉 CutY、只留 AfterCropTop=45 时蓝线必须跟着走，否则上面那些同值只是没读进来");
        Battery.Evidence("三种到达顺序都得 (O=60, 蓝线 20, b=40)；冲突值 b=50 两趟都不改结论；" +
                         "只留 AfterCropTop=45 时蓝线真跟着走");
    }

    /// <summary>
    /// 需求一.4/四.6 的收口：用户滑过图片之后，"取消本次调整"与"恢复自动"都要回到
    /// <b>自动结论那一格</b>（不是硬回到带首），而且没滑过时不该产生空改动
    /// （空记录会让下一次 Ctrl+Z 退掉一个他没做的改动）。
    ///
    /// 「线不动、图动」那一轮之后这一格多担一件事：一次滑动<b>同时</b>改了重叠与蓝线，
    /// 所以"取消"必须把两半都还回去——只钉蓝线不还重叠，图片就还停在被挪走的地方。
    /// </summary>
    private static void CancelAndResetReturnToAutoCut()
    {
        using CropRig rig = CropRig.New();
        WriteAuto(rig.P, Overlap, autoCut: 25);       // 自动结论给的切点
        Battery.AssertEqual(25, rig.Seam.CutPosition, "自动切点要落进参数");
        Battery.AssertEqual(25, rig.Seam.AutoAfterCropTop, "自动结论那一格要单独记住");
        Battery.Assert(!rig.Seam.IsManualCut, "自动切点不算用户改过");
        Battery.Assert(!rig.Seam.IsManualAlignment, "自动重叠也不算用户改过");

        int revision = rig.Revision;
        // 自动切点在 25 ⇒ 线画在 140+25=165，按下要点那里；
        // 但横向要避开骑在线正中的那枚「调整」把手（按它只开关微调档、不开拖动手势）。
        var press = new Point(rig.Canvas.AdjustHandleDeviceRect(rig.Seam.Id).Right + 4, LineY + 25);
        Battery.Assert(rig.Canvas.HitTestSeamEditRegion(press) is
            { Kind: StitchCanvasControl.SeamHitKind.CutLine }, "夹具自证：这一点命中的是蓝线本身");
        rig.Canvas.MousePress(MouseButtons.Left, Keys.None, press);
        rig.Canvas.MouseMoveTo(new Point(press.X, press.Y + 10));
        rig.Canvas.MouseMoveTo(new Point(press.X, press.Y + 20));   // 抓下方那张往下滑 20 行
        rig.Canvas.MouseRelease(new Point(press.X, press.Y + 20));
        Battery.AssertEqual(5, rig.Seam.CutPosition, "滑 +20 ⇒ 带内行号 25→5（画布行号才是守恒的那一个）");
        Battery.AssertEqual(Overlap - 20, rig.Seam.OverlapLength, "同一趟里重叠 60→40：两半是一起动的");
        Battery.AssertEqual(rig.Seam.OverlapLength - 5, rig.Seam.BeforeCropBottom, "另一侧补齐（b=35）");
        Battery.Assert(rig.Seam.IsManualCut, "滑过之后算手动");
        Battery.Assert(rig.Seam.IsManualAlignment, "重叠也被这趟改了，同样要留痕");

        rig.Canvas.CancelAdjustment();
        Battery.AssertEqual(25, rig.Seam.CutPosition, "取消本次调整回到自动结论那一格（不是回到带首 0）");
        Battery.AssertEqual(Overlap, rig.Seam.OverlapLength,
            "取消必须把重叠也还回去：只钉蓝线不还重叠，图片就还停在被挪走的地方，那等于没取消");
        Battery.Assert(!rig.Seam.IsManualCut, "取消之后回到自动档");
        Battery.Assert(!rig.Seam.IsManualAlignment, "取消之后对齐那一半也要回到自动档");
        Battery.Assert(rig.Seam.ManualOffset is null, "取消之后不该有手动位移");
        Battery.AssertEqual(165, Geometry(rig.P).CutYCanvas, "蓝线的画布行号也回到自动那一趟的 165");
        Battery.AssertEqual(revision + 2, rig.Revision, "滑一次 + 取消一次 = 两条记录（各自都可撤销）");

        // 程序/面板那条路也改切点，然后"恢复自动"同样回到自动那一格
        MoveCut(rig.P, 40);
        Battery.AssertEqual(40, rig.Seam.CutPosition, "面板/程序路径也改切点");
        rig.P.Mutate("恢复自动", d => d.Seams[0].ResetToAuto());
        Battery.AssertEqual(25, rig.Seam.CutPosition, "恢复自动也回到自动那一格");
        Battery.AssertEqual(Overlap, rig.Seam.OverlapLength, "恢复自动同样把重叠还回去");

        // 反向对照：没切过时取消必须报"什么都没做"
        Battery.Assert(!rig.Seam.CancelCutAdjustment(),
            "没滑过切点时不该报成取消成功（那会给界面一条空改动）");
        Battery.Assert(!rig.Seam.CancelAlignmentAdjustment(),
            "没滑过重叠时同理：不许报成取消成功");
        Battery.AssertEqual(25, rig.Seam.CutPosition, "报 false 的同时参数不该被动过");
        Battery.Evidence("自动 (O=60, 蓝线 25) → 滑到 (O=40, 蓝线 5) → 取消回到 (60, 25) → " +
                         "程序切 40 → 恢复自动回 25；未滑过时两个取消都返回 false");
    }

    // ═════════════════ ⑦ 匹配与自动写回（需求六） ═════════════════

    /// <summary>
    /// 需求六：匹配结果不能只给 Offset 与重叠，还要给带起止、两侧裁切量、切点、
    /// 检测到的横向偏移、置信度与"要不要人工过一眼"。
    /// 同时量失败那一支：失败时裁切建议仍要自洽（b + t = O），
    /// 且写回之后布局依旧两段不相交——匹配失败也不继续叠两张完整图。
    /// </summary>
    private static void MatchResultCarriesCropAdvice()
    {
        // 成功支：64×760 的页面切两屏、重叠 300（第七/八轮已标定过的样本）
        RgbaImage page = LongScrollTests.StripedPage(64, 760);
        (RgbaImage before, RgbaImage after, int expected) =
            TestProject.CutScrollPair(page, firstTop: 0, firstHeight: 400, overlap: 300);
        MatchResult good = OverlapMatcher.Match(before, after, MatchOptions.For(StitchMode.LongScroll));

        Battery.Assert(!good.NeedsReview, "这对同页样本应自动通过，实际 " + good.Status);
        Battery.AssertEqual(SeamStatus.AutoPassed, good.Status, "自动通过才有绿色「自动匹配」");
        Battery.AssertEqual(400, good.HeightBefore, "结果要带前一段高度（带起止都由它算）");
        Battery.AssertEqual(expected, good.OverlapLength, "量到的重叠 = 构造时的真值 300");
        Battery.AssertEqual(400 - expected, good.OverlapStartY, "带在前一段里的起点");
        Battery.AssertEqual(400, good.OverlapEndY, "带在前一段里的终点（不含）= 前一段底边");
        Battery.AssertEqual(good.SeamCutPosition, good.CropAfterTop, "后一段裁切量 = 切点");
        Battery.AssertEqual(good.OverlapLength - good.SeamCutPosition, good.CropBeforeBottom,
            "前一段裁切量 = O − 切点");
        Battery.Assert(good.CropInvariantHolds, "匹配给的裁切建议自洽：b + t = O");
        Battery.Assert(good.Confidence > 0 && good.Confidence <= 1,
            "置信度应在 (0,1]，实际 " + good.Confidence);
        Battery.AssertEqual(0, good.Offset.X, "长截图采纳的位移 X 恒 0");

        // 写回之后：两个源矩形必须按建议裁开
        (Project p, MemoryResolver r, _) = TestProject.Create(StitchMode.LongScroll, before, after);
        p.Mutate("写回匹配结果", d => d.Seams[0].ApplyAutoResult(
            good.HasSuggestion ? good.Offset : null, good.Status, good.Cause, good.CauseText,
            good.Confidence, good.OverlapLength, good.Candidates.ToList(), good.SeamCutPosition));
        SeamGeometry g = Geometry(p);
        Battery.AssertEqual(new IntRect(0, 0, 64, 400 - good.CropBeforeBottom), g.BeforeSourceRect,
            "上一张的矩形按建议裁掉了底部");
        Battery.AssertEqual(new IntRect(0, good.CropAfterTop, 64, 400 - good.CropAfterTop), g.AfterSourceRect,
            "下一张的矩形按建议从切点开始");
        Battery.Assert(g.RectsDisjoint, "写回后两段不再相交：" + g);
        Battery.Assert(g.CropInvariantHolds, "写回后不变式成立：" + g);

        // 失败支：两张毫不相干的图
        RgbaImage unrelated = Fixture.OpaqueLike(64, 400, seed: 777);
        MatchResult bad = OverlapMatcher.Match(before, unrelated, MatchOptions.For(StitchMode.LongScroll));
        Battery.Assert(bad.NeedsReview, "无重叠的两张图必须降级为需人工，实际 " + bad.Status);
        Battery.Assert(bad.CropInvariantHolds, "失败时给的裁切建议也要自洽（不能半套）");

        (Project q, _, _) = TestProject.Create(StitchMode.LongScroll, before, unrelated);
        q.Mutate("写回失败结果", d => d.Seams[0].ApplyAutoResult(
            bad.HasSuggestion ? bad.Offset : null, bad.Status, bad.Cause, bad.CauseText,
            bad.Confidence, bad.OverlapLength, bad.Candidates.ToList(), bad.SeamCutPosition));
        SeamGeometry failed = q.Layout.DescribeSeam(q.Doc.Seams[0])!;
        Battery.Assert(failed.RectsDisjoint, "匹配失败也不许把两张完整图叠在一起");
        Battery.Assert(failed.CropInvariantHolds, "失败写回之后不变式照样成立");
        Battery.Assert(q.Doc.Seams[0].Status != SeamStatus.AutoPassed,
            "失败要在界面上出感叹号（状态不能是自动通过）");
        Battery.Evidence($"成功：带 [{good.OverlapStartY},{good.OverlapEndY})、裁切 " +
                         $"({good.CropBeforeBottom},{good.CropAfterTop})、强度 {good.Confidence:F3}；" +
                         "失败：仍需人工，且两段不相交");
    }

    // ═════════════════ ⑧ 面板与键盘（需求四的其余入口） ═════════════════

    /// <summary>
    /// 需求四.2/四.3 的键盘版本：↑↓ 一次走一行<b>切点</b>，总重叠与画布高不变，
    /// ←→ 完全不动（只回话），每次按键一条撤销；顶到带边时要把夹掉的行数报出来
    /// （<c>CutClamped</c>），不许"拖不动了也不知道为什么"。
    /// </summary>
    private static void ArrowsMoveCutOneRow()
    {
        using CropRig rig = CropRig.New();
        // 起点把蓝线摆在带中间：带内行号为 0 时"抓下方那张往下滑"一步都滑不动，
        // 那一档只会报"滑到头"，量不出"每次按键走 1 个源像素"这件事。
        rig.P.Mutate("预设蓝线", _ => rig.Seam.ApplyCut(30));
        int clamps = 0, locks = 0;
        rig.Canvas.CutClamped += _ => clamps++;
        rig.Canvas.HorizontalShiftRejected += _ => locks++;

        int revision = rig.Revision;
        int lineBefore = Geometry(rig.P).CutYCanvas;
        Battery.AssertEqual(30, rig.Seam.CutPosition, "夹具自证：起点蓝线在带内第 30 行");

        // 没有点过任何一侧时，方向键默认挪下方那张（长截图里压倒性多数的意图）
        rig.Canvas.NudgeSelected(new IntPoint(0, 1));
        Battery.AssertEqual(29, rig.Seam.CutPosition, "↓ 一次：带内行号走一行（30→29）");
        Battery.AssertEqual(Overlap - 1, rig.Seam.OverlapLength, "重叠同减一行：两个量必须一起动");
        Battery.AssertEqual(lineBefore, Geometry(rig.P).CutYCanvas, "蓝线的画布行号不动（线不动、图动）");
        Battery.AssertEqual(revision + 1, rig.Revision, "一次按键一条撤销");
        Battery.AssertEqual(341, rig.P.Layout.CanvasSize.Height, "重叠少 1 行 ⇒ 总高多 1 行");

        rig.Canvas.NudgeSelected(new IntPoint(0, -1));
        Battery.AssertEqual(30, rig.Seam.CutPosition, "↑ 一次回到 30");
        Battery.AssertEqual(revision + 2, rig.Revision, "反向按键同样记一条（不合并）");

        rig.Canvas.NudgeSelected(new IntPoint(-1, 0));
        Battery.AssertEqual(30, rig.Seam.CutPosition, "← 在长截图里不动任何参数");
        Battery.AssertEqual(revision + 2, rig.Revision, "← 不该产生改动记录");
        Battery.Assert(locks >= 1, "按了没反应必须回话，否则用户以为卡住了");

        // 往带尾猛按：滑到头要出声，参数停在边界上
        rig.Canvas.NudgeSelected(new IntPoint(0, 100));
        Battery.AssertEqual(0, rig.Seam.CutPosition, "带内行号顶到 0（整带都用图2）");
        Battery.AssertEqual(30, rig.Seam.OverlapLength, "重叠也只减得动 30 行：多要的那些不许静默生效");
        Battery.Assert(clamps >= 1, "滑到头必须报被夹掉的行数，实际 clamps=" + clamps);
        Battery.AssertEqual(rig.Seam.OverlapLength, rig.Seam.BeforeCropBottom,
            "整带都由图2 提供 ⇒ 上一张让出整带（b = O = 30，不是负数）");
        Battery.AssertEqual(0, rig.Seam.AfterCropTop, "图2 顶部一行都不用让（t = 0）");
        Battery.Assert(rig.Seam.CropInvariantHolds, "夹完仍要守住不变式");
        Battery.AssertEqual(lineBefore, Geometry(rig.P).CutYCanvas, "滑到头蓝线也不许跑");
        Battery.AssertEqual(0, rig.P.Layout.Placed[1].Origin.X, "全程 X 恒为 0");

        // 已在边界再按 ↓：空操作不该留第二条撤销
        int before = rig.Revision;
        rig.Canvas.NudgeSelected(new IntPoint(0, 1));
        Battery.AssertEqual(before, rig.Revision, "滑到头再按一次不该留空撤销记录");
        Battery.Evidence($"↓→Cut 30→29→30、O 60→59→60、蓝线画布行号恒为 {lineBefore}；" +
                         $"← 不动但回话 {locks} 次；+100 只滑得动 30 行并报夹掉 {clamps} 次");
    }

    /// <summary>
    /// 改总重叠（O）与改蓝线是两件事，但必须互相尊重。
    ///
    /// ═══ 这条判据换过三次口径，每一次都是被需求否掉的（如实记着，别再当成"实现漂了"）═══
    /// ① 第九轮：按比例跟着走；② 第十二轮：只夹不换（"蓝线是用户选定的内容来源分界，
    /// 不该被新的重叠再摊一次"）；③ <b>第十三轮需求三/四又把它翻回按比例</b>——
    /// 需求要的是"普通情况下 CutY 的<b>相对位置</b>保持不变，只有越界时才被夹紧"，
    /// 并且点名"不会因为 OverlapRect 变化而把蓝线突然跳到带首或带尾"。
    /// ②那个口径下带变高 20 行时，图1 的可见底边自己长 20 行、图2 一行没变，
    /// 用户看到的是"我调对齐，图1 多吃了一段"——那正是需求三要修的语义漂移。
    /// 所以这一格现在量的是<b>比例守恒</b>，同时把②那件事（默认档 0 仍然 0）留在反向对照里。
    /// </summary>
    private static void OverlapChangeScalesUserCutLine()
    {
        Sample s = NewSample(tinted: false);
        Project p = s.P;
        WriteAuto(p, Overlap);
        p.Mutate("用户切到带中间", d => d.Seams[0].ApplyCut(30));
        Battery.AssertEqual(30, p.Doc.Seams[0].CutPosition, "起点：带内第 30 行（带 60 行，正好一半）");

        p.Mutate("重叠改成 50", d => d.Seams[0].SetOverlap(50));
        Seam seam = p.Doc.Seams[0];
        Battery.AssertEqual(25, seam.CutPosition, "带里相对位置不变：一半仍然是一半（30/60 → 25/50）");
        Battery.Assert(25 != 0 && 25 != 50, "蓝线既没跳到带首也没跳到带尾（需求四那句）");
        Battery.AssertEqual(25, seam.BeforeCropBottom, "另一侧补齐 = O − 蓝线");
        Battery.AssertEqual(0.5, Math.Round(seam.CutRatio, 4), "比例这一个数才是用户选的东西，改 O 不许动它");
        Battery.Assert(seam.CropInvariantHolds, "改 O 之后不变式仍成立");
        Battery.AssertEqual(SeamCropMode.Manual, seam.CropMode, "来源仍是手动");
        Battery.AssertEqual(50, seam.OverlapLength, "总重叠按新值");
        Battery.AssertEqual(Height + Height - 50, p.Layout.CanvasSize.Height, "总高跟着 O 走");

        // 反向对照一：变高也按同一比例（带高换算与方向无关）
        p.Mutate("重叠改回 70", d => d.Seams[0].SetOverlap(70));
        Battery.AssertEqual(35, seam.CutPosition, "50→70：25/50 → 35/70，仍是同一半");
        Battery.AssertEqual(0.5, Math.Round(seam.CutRatio, 4), "比例仍然守恒");
        Battery.Assert(seam.CropInvariantHolds, "换算完仍守住 b + t = O");

        // 反向对照二：没切过时改 O 应回默认档（整带取下一张）——0 按比例换算还是 0
        Sample t = NewSample(tinted: false);
        WriteAuto(t.P, Overlap);
        t.P.Mutate("直接改重叠", d => d.Seams[0].SetOverlap(50));
        Battery.AssertEqual(0, t.P.Doc.Seams[0].CutPosition, "未手动切过时回到带首（默认档）");
        Battery.AssertEqual(50, t.P.Doc.Seams[0].BeforeCropBottom, "默认档 b=O");
        Battery.Assert(t.P.Doc.Seams[0].CropInvariantHolds, "两支都要守不变式");
        Battery.Evidence("蓝线 30/60 → O 改 50 → 25/50（比例仍 0.5）、b 25；再改 70 → 35/70；" +
                         "未切过的同一路径 → (50,0)");
    }

    /// <summary>
    /// 坏参数（重叠比图本身还高，手工编辑的项目文件真能造出来）：
    /// 布局要<b>夹小裁切量</b>而不是裁出负尺寸，并且把"参数与落位不一致"这件事
    /// 明写在账上（<c>CropsAppliedAsRequested</c>），让界面能出声。
    /// 夹取顺序也要量：先夹上一张的底、再按剩余量夹下一张的顶，总账必须仍然平。
    /// </summary>
    private static void OverlapBeyondImageIsClampedVisible()
    {
        RgbaImage tall = Fixture.OpaqueLike(Width, Height, seed: 401);
        RgbaImage shortOne = Fixture.OpaqueLike(Width, 50, seed: 402);
        (Project p, MemoryResolver r, _) = TestProject.Create(StitchMode.LongScroll, tall, shortOne);

        // 前一段 200、后一段 50，把 O 写成 220（超过后一段本身）
        p.Mutate("写一个裁不动的重叠", d => d.Seams[0].SetOverlap(220));
        Seam seam = p.Doc.Seams[0];
        Battery.AssertEqual(220, seam.OverlapLength, "参数原样记着用户输入的值");
        Battery.Assert(seam.CropInvariantHolds, "参数自己仍是自洽的（b + t = O）");

        SeamGeometry g = Geometry(p);
        Battery.Assert(!g.CropsAppliedAsRequested,
            "布局必须报出「按请求裁不下」——不许悄悄吞掉：" + g);
        Battery.Assert(g.CropInvariantHolds, "落位后的两侧之和仍等于落位后的去重量：" + g);
        Battery.Assert(g.BeforeRect.Height >= 0 && g.AfterRect.Height >= 0, "夹取不许裁出负尺寸");
        Battery.AssertEqual(g.AppliedBeforeCrop + g.AppliedAfterCrop, g.Overlap,
            "量到的去重量 = 实际裁掉的行数");

        DedupAccount account = p.Layout.MeasureDedup();
        Battery.Assert(account.Holds, "坏参数下总账必须仍然平：" + account);
        Battery.AssertEqual(account.Expected, p.Layout.CanvasSize.Height, "画布高等于公式值");
        Battery.Assert(p.Layout.CanvasSize.Height <= 250, "裁不动时最多就是两段首尾相接，不会凭空变高");

        // 反向对照：把夹下来的量再写回参数，此时应当"按请求"了
        p.Mutate("按落位重写", d => d.Seams[0].SetOverlap(g.Overlap));
        Battery.Assert(Geometry(p).CropsAppliedAsRequested, "夹过之后再对账，参数与落位应一致");
        Battery.Evidence($"O=220 而两段共 250 行：夹到 ({g.AppliedBeforeCrop},{g.AppliedAfterCrop})、" +
                         $"画布 {p.Layout.CanvasSize.Height} 行、cropsAsRequested={g.CropsAppliedAsRequested}");
    }

    /// <summary>
    /// 需求十点了六个事件名。手势那四条（seam.cut.begin/preview/commit/cancel）
    /// 由 <c>crop/滑动图片的流水账带齐需求八那批字段与线不动读数</c> 逐字盯；另外两条是<b>状态行</b>：
    /// <c>layout.seam.cut</c>（每次改版参数）与 <c>export.seam.cut</c>（每次导出）都拼在
    /// <c>SeamGeometry.ToString()</c> 上。以前这两条只量了"有没有写日志"，
    /// 字段齐不齐没人管——真出事时才发现日志里读不出"这两刀裁在源图哪一块、整张画布因此多高"。
    ///
    /// 这格补三段：
    /// ① 状态行必须带齐需求十点名的那批键（含 canvasHeight——只有裁切量看不出总账）；
    /// ② 切点移动后两侧裁切量互换、canvasHeight 一字不动（需求八"不许移动图片"在状态行上的投影）；
    /// ③ 竖拼那一行必须自报 crop=false 且裁切量为 0——模式隔离要能在日志里读出来，
    ///    不然下一轮有人把裁切漏进位移档，只有像素能发现。
    /// </summary>
    private static void StateLinesCarryCropFields()
    {
        Sample s = NewSample(tinted: false);
        WriteAuto(s.P, Overlap);

        string line = Geometry(s.P).ToString();
        string[] required =
        {
            "seam=", "cutY=", "cut=", "beforeCropBottom=", "afterCropTop=",
            "rect1=", "rect2=", "overlapRect=", "alignmentOffset=", "cutYInBand=",
            "canvasHeight=", "overlapStartY=", "overlapEndY=", "afterCropTop=", "overlapLength=",
            "beforeSourceRect=", "afterSourceRect=", "canvasHeight=",
        };
        foreach (string key in required)
            Battery.Assert(line.Contains(key, StringComparison.Ordinal),
                "状态行缺需求十点名的字段 " + key + "，实际：" + line);

        Battery.Assert(line.Contains("crop=true", StringComparison.Ordinal),
            "长截图的状态行要明写 crop=true，实际：" + line);
        Battery.Assert(line.Contains("rectsDisjoint=true", StringComparison.Ordinal),
            "状态行要能直接读出两段不相交（布尔一律小写，True 是 C# 的写法不是日志的）");
        string heightToken = $"canvasHeight={Expected}";
        Battery.Assert(line.Contains(heightToken, StringComparison.Ordinal),
            $"状态行里的画布高必须就是这张布局的高（{Expected}），实际：" + line);

        // ② 切点从带首挪到带内第 20 行：两侧互换，总账与画布高都不许动
        const int cut = 20;
        MoveCut(s.P, cut);
        string moved = Geometry(s.P).ToString();
        Battery.Assert(moved.Contains($"beforeCropBottom={Overlap - cut}", StringComparison.Ordinal),
            "切点挪到第 20 行后，上一张应只裁 " + (Overlap - cut) + " 行，实际：" + moved);
        Battery.Assert(moved.Contains($"afterCropTop={cut}", StringComparison.Ordinal),
            "下一张应裁顶 " + cut + " 行，实际：" + moved);
        Battery.Assert(moved.Contains(heightToken, StringComparison.Ordinal),
            "拖切点之后状态行的画布高必须一字不变——变了就说明这条链把切点实现成了移动图片");
        Battery.Assert(!moved.Contains($"beforeCropBottom={Overlap}", StringComparison.Ordinal),
            "上一张的裁切量还写着整带 ⇒ 状态行没跟着切点走，日志与落位是两份账");

        // ③ 位移档的反向对照：同一对图在竖拼里必须自报"没走裁切"
        RgbaImage a = Fixture.OpaqueLike(Width, Height, seed: 771);
        RgbaImage b = Fixture.OpaqueLike(Width, Height, seed: 772);
        (Project vertical, MemoryResolver _, _) = TestProject.Create(StitchMode.Vertical, a, b);
        vertical.Mutate("竖拼里记重叠与位移", d =>
        {
            d.Seams[0].SetOverlap(Overlap);
            d.Seams[0].AutoOffset = new IntPoint(0, Height - Overlap);
        });
        string vline = Geometry(vertical).ToString();
        Battery.Assert(vline.Contains("crop=false", StringComparison.Ordinal),
            "竖拼的状态行必须自报不走裁切，实际：" + vline);
        Battery.Assert(vline.Contains("beforeCropBottom=0", StringComparison.Ordinal) &&
                       vline.Contains("afterCropTop=0", StringComparison.Ordinal),
            "竖拼里两侧裁切量必须恒为 0（裁切矩形不许渗进位移档），实际：" + vline);
        Battery.Evidence($"默认档状态行带全字段；切到第 {cut} 行后两侧互换为 " +
                         $"{Overlap - cut}/{cut} 而 canvasHeight 仍是 {Expected}；" +
                         $"竖拼那行自报 crop=false 且两侧裁切量恒 0");
    }

    // ═════════════════ ⑨ 守卫的守卫：证明上面那些断言真的有牙齿 ═════════════════

    /// <summary>
    /// 第十二轮起，"绕开 setter 写坏两侧裁切量"这个形状<b>已经写不出来了</b>：
    /// b 与 t 不再是存储字段，而是从公共坐标里读出来的派生值。
    /// 所以这一格的守卫换了靶子——它现在守的是"派生与夹取是不是真的在起作用"：
    /// ① 直接裸写核心字段 <see cref="Seam.CutY"/> 到带外（模拟手工编辑过的项目文件、
    ///    或将来某次重构里新增的一处裸写），要求<b>读出口必须夹</b>；
    /// ② 要求布局那本账必须<b>看得见</b>这次不一致（CropsAppliedAsRequested 翻红），
    ///    而不是悄悄吞掉——"红不了"本身就是这一格的判据：
    ///    哪天有人把夹取改成"写进去什么就读出来什么"，账上就会重新出现
    ///    "声称去掉 60 行、实际一行都没裁"那个坏形状，这一格先红。
    /// 收尾要求 <c>NormalizeAlignment()</c> 把存储值本身修回带内：证明"迁移/修补"那一环也不是装饰。
    /// </summary>
    private static void CropsBrokenByBackdoorAreDetected()
    {
        Sample s = NewSample(tinted: false);
        WriteAuto(s.P, Overlap);
        Seam seam = s.P.Doc.Seams[0];
        Battery.Assert(seam.CropInvariantHolds, "夹具自证：走产品出口摆好的账应先是通过的");

        // 故意把蓝线写到带外 500 行（= "切点声称在带尾之外"这个坏形状）
        seam.CutY = Overlap + 500;
        Battery.AssertEqual(Overlap, seam.OverlapLength, "重叠记录没动（坏的是蓝线位置）");
        Battery.AssertEqual(Overlap, seam.CutPosition, "读出口必须夹回带尾——不许把带外的数原样端给界面");
        Battery.Assert(seam.CropInvariantHolds,
            "两侧之和不再等于重叠：派生读法被改坏了，b+t=O 从此又要靠人守");

        // 布局侧同一条坏参数必须也被量出来：这次直接 Build，不走项目里那份缓存布局
        SeamGeometry mutated = CanvasLayout.Build(s.P.Doc).DescribeSeam(seam)!;
        Battery.Assert(!mutated.CropsAppliedAsRequested,
            "参数与落位不一致却没被这本账抓到 —— 说明布局自己也不看夹取了多少");
        Battery.AssertEqual(Overlap, mutated.Overlap,
            "带高不许因为蓝线写坏而跟着变（去重量是蓝线两侧之和，不是那个坏数）");
        Battery.Assert(mutated.CutInsideBand, "生效蓝线必须落在带里：出带的像素等于凭空删掉一段内容");
        Battery.Assert(mutated.FormulaHolds, "夹取之后去重公式仍要自洽（坏的是参数，不是像素）");
        Battery.AssertEqual(Expected, CanvasLayout.Build(s.P.Doc).CanvasSize.Height,
            "画布高仍是 340：蓝线写坏不该让画布长出 500 行");

        seam.NormalizeAlignment();
        // 这一格注的是<b>自动档</b>的坏值：修补回到的是"自动结论那一格"（AutoCutY=0），
        // 而不是"把坏值夹到带边"——带外的裸写不是用户意图，没有替他保留的义务。
        Battery.AssertEqual(0, seam.CutY, "修补之后存储值回到自动结论那一格（带首）");
        Battery.Assert(seam.CropInvariantHolds, "修补之后不变式必须重新成立");
        Battery.Assert(CanvasLayout.Build(s.P.Doc).DescribeSeam(seam)!.CropsAppliedAsRequested,
            "修补之后账上那格「参数与落位一致」要重新绿（否则 NormalizeAlignment 只是改数字改给测试看）");
        Battery.Evidence("裸写 CutY 到带外：读出口夹、布局夹、账上标出「参数与落位不一致」、画布高不变；" +
                         "NormalizeAlignment 之后连存储值都回到带内");
    }

    /// <summary>
    /// "被裁掉的那几行不进成品"这组断言完全依赖**异本样本**（两份副本可分辨）。
    /// 这格把同一处数法同时跑在同本与异本上：同本必须数到 <b>1</b>、异本必须数到 <b>0</b>。
    /// 哪天标记与样本自己的颜色撞上（涂了等于没涂），两边会读成同一个数，这格先红——
    /// 而不是让前面那几格悄悄退化成"数谁都在"的空判据。
    /// </summary>
    private static void CropMarkerIsWhatMakesThoseAssertionsBit()
    {
        Sample plain = NewSample(tinted: false);
        WriteAuto(plain.P, Overlap);
        RgbaImage plainFile = ExportImage(plain.P, plain.Composer, plain.Resolver);
        int plainCount = LongScrollTests.CountRowsEqualTo(plainFile, plain.BeforeBandRow(0));

        Sample tinted = NewSample(tinted: true);
        WriteAuto(tinted.P, Overlap);
        RgbaImage tintedFile = ExportImage(tinted.P, tinted.Composer, tinted.Resolver);
        int tintedCount = LongScrollTests.CountRowsEqualTo(tintedFile, tinted.BeforeBandRow(0));

        Battery.AssertEqual(1, plainCount,
            "同本样本里「上一张被裁的那一行」应当仍能数到 1（它就是同一份内容）——" +
            "读到 0 说明数法本身在骗人");
        Battery.AssertEqual(0, tintedCount,
            "涂了标记之后同一处应当数到 0；还是 1 就说明标记没生效，前面那几格是空判据");
        Battery.Assert(plainCount != tintedCount,
            "两份样本读出同一个数 ⇒ 标记形同虚设，「被裁的行不进成品」这类断言全部失去意义");
        Battery.Evidence($"同一处数法：同本 {plainCount} 次、异本 {tintedCount} 次 ⇒ 判据的杀伤力来自可分辨的标记");
    }

    /// <summary>
    /// 「两段矩形不相交」这一半判据会不会只是"长截图里刚好成立"的巧合？
    /// 这格用**位移档**做出同样去掉 60 行的形状（旧写法就是这样的）：
    /// 账面上的重叠与声称值都对得上（400−340=60），但两段仍在交叠，
    /// 而且两段加起来画的像素是 400 行 ≠ 画布 340 行——那 60 行进过像素两次。
    /// 于是"只靠参数与总高"是抓不住旧写法的，必须靠不相交这一半：这格证明它没白写。
    /// </summary>
    private static void OverlayPlacementIsCaughtByDisjointness()
    {
        RgbaImage a = Fixture.OpaqueLike(Width, Height, seed: 611);
        RgbaImage b = Fixture.OpaqueLike(Width, Height, seed: 612);
        (Project p, MemoryResolver _, _) = TestProject.Create(StitchMode.Vertical, a, b);
        p.Mutate("位移档做出同样的去重量", d =>
        {
            d.Seams[0].SetOverlap(Overlap);
            d.Seams[0].AutoOffset = new IntPoint(0, Height - Overlap);   // offsetY = 140 ⇒ 去掉 60 行
        });

        SeamGeometry g = Geometry(p);
        Battery.Assert(!g.CropPlacement, "夹具自证：这一格跑在位移档");
        Battery.AssertEqual(Overlap, g.Overlap, "从落位反量出来的重叠也是 60");
        Battery.AssertEqual(Overlap, g.ReportedOverlap, "与声称的一致（旧口径这里全绿）");
        Battery.Assert(g.OverlapMatchesReport, "夹具自证：旧口径的账是平的，所以抓不住旧写法");
        Battery.AssertEqual(Height + Height - Overlap, p.Layout.CanvasSize.Height, "旧写法的高也确实 = ΣH − ΣO");

        // 同一条"去重"在裁切档的判据下必须翻红：两段仍相交、而且画进像素的行数比画布多 60
        Battery.Assert(!g.RectsDisjoint,
            "两段相交却报出不相交 ⇒ crop/长截图里任意两段的矩形都不相交 这条判据失效");
        Battery.AssertEqual(Height + Height, g.BeforeRect.Height + g.AfterRect.Height,
            "两段各自画了完整高度 ⇒ 有 60 行进过像素两次，这正是本轮要消灭的形状");
        Battery.Evidence($"旧形状：高 {(Height + Height - Overlap)}、账也平，但两段相交且共画 " +
                         $"{g.BeforeRect.Height + g.AfterRect.Height} 行 ⇒ 不相交那一半判据不可替代");
    }

    // ───────────────────────── 存档夹具：用 DOM 增删拼缝上的键 ─────────────────────────
    //
    // 为什么不用正则：v1 迁移那格要"删掉新字段模拟旧文件"，而新字段里
    // <c>AlignmentOffset</c> 是个对象（<c>{"X":0,"Y":-60}</c>），
    // 只认标量的正则会把"没删干净"当成"旧文件能迁移"来庆祝。
    // 走 JsonNode 还能顺手断言"这些键确实存在过"，夹具自证才是真的。

    /// <summary>删掉第一条拼缝上的这几个键，返回改过的 JSON。</summary>
    private static string WithoutSeamKeys(string json, params string[] keys)
    {
        System.Text.Json.Nodes.JsonNode root = System.Text.Json.Nodes.JsonNode.Parse(json)!;
        var seam = SeamObject(root);
        foreach (string key in keys)
        {
            Battery.Assert(seam.ContainsKey(key), $"夹具自证：当前写法本该把 {key} 写进项目文件");
            seam.Remove(key);
        }
        return Pretty(root);
    }

    /// <summary>改第一条拼缝上的这几个键的值，返回改过的 JSON（模拟手工编辑过的文件）。</summary>
    private static string WithSeamKeys(string json, params (string Key, int Value)[] edits)
    {
        System.Text.Json.Nodes.JsonNode root = System.Text.Json.Nodes.JsonNode.Parse(json)!;
        var seam = SeamObject(root);
        foreach ((string key, int value) in edits)
        {
            Battery.Assert(seam.ContainsKey(key), $"夹具自证：当前写法本该把 {key} 写进项目文件");
            seam[key] = value;
        }
        return Pretty(root);
    }

    /// <summary>取第一条拼缝那个 JSON 对象（键名大小写按 ProjectFile 的写法走，不猜）。</summary>
    private static System.Text.Json.Nodes.JsonObject SeamObject(System.Text.Json.Nodes.JsonNode root)
    {
        var doc = root.AsObject();
        System.Text.Json.Nodes.JsonNode? seams = doc["Seams"] ?? doc["seams"];
        Battery.Assert(seams is not null, "夹具自证：项目文件里找不到 Seams 数组，键名与预期不符：" + doc);
        // 上面那句 Assert 不产生编译期的非空收窄，这里显式抛一次：
        // 夹具本身坏了要立刻炸在明处，而不是让整格读到一个 null 引用的怪错。
        if (seams is null) throw new InvalidOperationException("夹具自证：项目文件里没有 Seams 数组");
        return seams[0]!.AsObject();
    }

    /// <summary>
    /// 把第一条拼缝上的这几个键<b>按给定顺序重排</b>（先删再补到末尾 ⇒ 反序列化真按这个顺序走 setter）。
    /// 只改值的那格（<see cref="WithSeamKeys"/>）做不到这件事：JsonObject 赋已知键是原地替换、不动位置，
    /// 而"到达顺序不许改变结论"这句话必须由顺序本身来量。
    /// </summary>
    private static string ReorderSeamKeys(string json, params string[] order)
    {
        System.Text.Json.Nodes.JsonNode root = System.Text.Json.Nodes.JsonNode.Parse(json)!;
        var seam = SeamObject(root);
        foreach (string key in order)
        {
            Battery.Assert(seam.ContainsKey(key), $"夹具自证：当前写法本该把 {key} 写进项目文件");
            System.Text.Json.Nodes.JsonNode? value = seam[key]?.DeepClone();   // 脱离父节点才能再挂回去
            seam.Remove(key);
            seam[key] = value;
        }
        return Pretty(root);
    }

    private static string Pretty(System.Text.Json.Nodes.JsonNode root) =>
        root.ToJsonString(new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
}
