using LongStitch.Core;
using LongStitch.Core.Cleanup;
using LongStitch.Core.Export;
using LongStitch.Core.Geometry;
using LongStitch.Core.Imaging;
using LongStitch.Core.Imaging.Png;
using LongStitch.Core.Layout;
using LongStitch.Core.Model;
using LongStitch.Core.ProjectIO;

namespace LongStitch.Checks;

/// <summary>
/// 导出电池——"一像素不出错"的最终落点。
///
/// 这里的核心判据只有一句话：<b>分块写出的文件，解码回来必须与一次性合成的整张逐字节相同</b>。
/// 长图导出之所以要分块（否则内存扛不住），但分块一旦与整块有任何细微差别，
/// 用户看到的就是"预览没问题、导出的图接缝处多一行/少一行"，
/// 而且只在超长图上出现，最难复现、最容易 escapes 到发布之后。
/// </summary>
public static class ExportTests
{
    public static void Register()
    {
        Battery.Add("export", "分块导出与整块合成逐字节相同", ChunkedExportMatchesWhole);
        Battery.Add("export", "分段每片原尺寸且覆盖完整", SplitPiecesCoverCanvasExactly);
        Battery.Add("export", "分段边界落在拼缝上", SplitSnapsToSeams);
        Battery.Add("export", "吸附与重叠后每片仍不超过用户设定高度", SplitHeightIncludesOverlap);
        Battery.Add("export", "取消后不留临时文件", CancelLeavesNoPart);
        Battery.Add("export", "超限时拒绝且不写文件", HardLimitRefusesSilentCompress);
        Battery.Add("export", "整数倍降采样与独立复算一致", DownscaleMatchesIndependentMath);
        Battery.Add("export", "缩小导出全链路逐字节对得上独立复算", DownscaleFullPipelineMatchesIndependentMath);
        Battery.Add("export", "0=不分段必须兑现即使超出建议上限", ZeroMeansNoSplitEvenAboveViewerAdvice);
        Battery.Add("export", "缺失源图在计划阶段点名硬拦", MissingSourcesBlockPlanByName);
        Battery.Add("export", "显式分段解除整图尺寸硬限但单片超限仍拦", SplitExemptsWholeImageHardLimit);
        Battery.Add("export", "项目文件往返参数完全一致", ProjectFileRoundTrip);
        Battery.Add("export", "打包项目往返：原图字节进容器、换目录也能解码", ProjectPackageRoundTripCarriesSources);
        Battery.Add("export", "最近项目列表去重置顶并可清空", RecentProjectsDedupeCapAndClear);
        Battery.Add("export", "TIFF 分块写出与整块写出字节相同", TiffChunkedEqualsWholeAndDecodesBack);
        // 第五十六改②：PNG「透明」勾勾掉时，透明像素要真的被压到所选底色上（且分块路径同样生效）
        Battery.Add("export", "PNG 勾掉透明后透明像素压到所选底色且分块路径一致", PngOpaqueFlattenHonoursBackground);
    }

    /// <summary>
    /// 自研 TIFF 编码器（补齐"TIFF 未内置编码器"这条缺口）。三条判据合起来才说明它可用：
    ///   ① <b>分块 == 整块</b>：这是本项目的精度红线之一，导出长图走分块，
    ///      两种写法必须产出同一个字节序列；
    ///   ② 条带数据在文件里就是行主序的 RGBA 原样字节（未压缩），
    ///      所以能拿"文件第 8 字节起的那一段"直接与图像像素比对——这是不依赖任何解码器的布局自证；
    ///   ③ 自己写的读取器能完整读回（读取器与编码器不共享条带算术，见 TiffDecoder 的文档）。
    /// </summary>
    private static void TiffChunkedEqualsWholeAndDecodesBack()
    {
        // 高度取 130：跨过 64 行的条带边界，逼出"三条带"的布局（最后一条不满）
        var image = Fixture.ScreenshotLike(37, 130, seed: 5150);
        var region = new IntRect(0, 0, image.Width, image.Height);

        using var whole = new MemoryStream();
        LongStitch.Core.Imaging.Tiff.TiffEncoder.Encode(image, region, whole);
        byte[] wholeBytes = whole.ToArray();

        using var chunked = new MemoryStream();
        using (var writer = LongStitch.Core.Imaging.Tiff.TiffEncoder.CreateWriter(image.Size, chunked))
        {
            for (int y = 0; y < image.Height; y += 7)
            {
                int h = Math.Min(7, image.Height - y);
                writer.WriteBand(image.SubImage(new IntRect(0, y, image.Width, h)));
            }
            writer.Finish();
        }
        Battery.AssertBytesEqual(wholeBytes, chunked.ToArray(),
            "TIFF 整块写出与 7 行一条带分块写出");

        // ② 条带字节 = 行主序 RGBA 原样
        int pixelBytes = image.Width * image.Height * 4;
        var stripArea = new byte[pixelBytes];
        Array.Copy(wholeBytes, 8, stripArea, 0, pixelBytes);
        Battery.AssertBytesEqual(image.Pixels, stripArea, "TIFF 里紧接文件头的条带数据");

        // ③ 读回
        var back = LongStitch.Core.Imaging.Tiff.TiffDecoder.Decode(wholeBytes);
        Battery.AssertEqual(image.Width, back.Width, "读回的宽度");
        Battery.AssertEqual(image.Height, back.Height, "读回的高度");
        Battery.AssertEqual(4, back.SamplesPerPixel, "样本数应为 4（RGBA 直存，不合并 alpha）");
        Battery.AssertEqual(3, back.StripCount, "130 行按 64 行一条带应切成 3 条");
        Battery.AssertBytesEqual(image.Pixels, back.Image.Pixels, "TIFF 读回像素与原图");

        // GDI+ 作为外部参照：能读到尺寸就记下，读不动也只是记下来。
        // 系统解码器对"4 样本 + ExtraSamples"的取色方式各家实现不同，
        // 像素层面的真值由上面三条负责；把它升级成断言会把环境差异误读成本工程的缺陷。
        try
        {
            using var bmp = new System.Drawing.Bitmap(new MemoryStream(wholeBytes));
            Battery.Evidence($"GDI+ 也认这份文件（{bmp.Width}×{bmp.Height}）；" +
                             "像素真值由条带字节与自研读取器两头核对");
        }
        catch (Exception ex)
        {
            Battery.Evidence($"本机 GDI+ 读这份 RGBA TIFF 失败（{ex.Message}）——" +
                             "记为外部参照缺席，不作为判红；像素无损性仍由条带字节与自研读取器两头保证");
        }

        // ④ 注册表里 TIFF 现在应报"可用"，导出面板不能再显示"未内置编码器"
        var registry = new EncoderRegistry();
        Battery.Assert(registry.Probe(ExportFormat.Tiff) is null,
            $"TIFF 应已可用，实际原因：{registry.Probe(ExportFormat.Tiff)}");
        Battery.Assert(registry.IsLossless(ExportFormat.Tiff),
            "TIFF 是 RGBA 字节直存，允许承诺无损");

        // ⑤ 反例：文件被截短，读取器必须报错而不是硬拼一张短图
        var broken = (byte[])wholeBytes.Clone();
        Array.Resize(ref broken, broken.Length - 100);
        string msg = Battery.AssertThrows(
            () => LongStitch.Core.Imaging.Tiff.TiffDecoder.Decode(broken), "被截断的 TIFF");
        Battery.Assert(msg.Contains("越界") || msg.Contains("截断") || msg.Contains("IFD"),
            $"截断文件要说清楚问题出在哪一段，实际：{msg}");

        // ⑥ 导入通路也要真的吃这份文件：走 FileSourceResolver.Decode（产品用的那一个），
        // 而不是绕过它直接调读取器——否则"能写能读"可能只在测试里成立。
        string dir = MakeTempDir();
        try
        {
            string tif = Path.Combine(dir, "out.tif");
            File.WriteAllBytes(tif, wholeBytes);
            var decoded = LongStitch.App.Imaging.FileSourceResolver.Decode(tif);
            Battery.AssertBytesEqual(image.Pixels, decoded.Image.Pixels, "导入通路读回这份 TIFF");
            Battery.Assert(decoded.Warnings.Any(w => w.Contains("自研", StringComparison.Ordinal)),
                "走自研读取器时必须在导入提示里说清楚（这决定了无损承诺的口径）");
            Battery.Evidence($"导入通路核对：Decode(.tif) 与原图逐字节相同，并带出自研解码的提示");
        }
        finally { TryDeleteDir(dir); }
    }

    /// <summary>
    /// 最近项目列表（模拟 UI 需求 §6：允许清空）。
    /// 四条都要量：去重置顶、条数上限、已消失的路径不显示、清空后真为空。
    /// 判据用临时文件当存储，绝不碰用户 %APPDATA% 里那份真的列表。
    /// </summary>
    private static void RecentProjectsDedupeCapAndClear()
    {
        string dir = MakeTempDir();
        try
        {
            string store = Path.Combine(dir, "recent.txt");
            var files = new List<string>();
            for (int i = 0; i < RecentProjects.Limit + 4; i++)
            {
                string f = Path.Combine(dir, $"p{i}.lstick");
                File.WriteAllText(f, "{}");
                files.Add(f);
            }

            foreach (string f in files) RecentProjects.Add(f, store);
            var list = RecentProjects.Load(store);
            Battery.AssertEqual(RecentProjects.Limit, list.Count,
                $"条数应被截到上限 {RecentProjects.Limit}，实际 {list.Count}");
            Battery.AssertTextEqual(files[^1], list[0], "最后加入的一条应在最前面");

            // 重新打开最早那条：必须移到最前而不是又插一份（重复行会让菜单越用越长）
            RecentProjects.Add(files[0], store);
            list = RecentProjects.Load(store);
            Battery.AssertTextEqual(files[0], list[0], "再次打开要置顶");
            Battery.AssertEqual(1, list.Count(p =>
                string.Equals(p, files[0], StringComparison.OrdinalIgnoreCase)), "不得出现重复行");

            // 文件被删掉后不应继续显示（点了只会收到一句"打不开"）
            File.Delete(files[0]);
            list = RecentProjects.Load(store);
            Battery.Assert(!list.Contains(files[0]), "磁盘上已消失的路径应被过滤掉");
            Battery.AssertTextEqual(files[^1], list[0], "过滤后其余顺序不变");

            RecentProjects.Clear(store);
            Battery.AssertEqual(0, RecentProjects.Load(store).Count, "清空后列表应为空");
            Battery.Assert(File.Exists(store), "清空应留一个空文件，便于用户核对确实清了");

            Battery.Evidence($"上限 {RecentProjects.Limit} 条、置顶去重、消失即过滤、清空归零（全程只碰临时目录）");
        }
        finally { TryDeleteDir(dir); }
    }

    /// <summary>
    /// 打包项目（.lstickx）的往返判据。需求 §9 的原文是"项目文件仅引用本地源图，
    /// 不将原图嵌入项目，除非用户执行明确的『打包项目』"——所以打包这一条通路必须单独量：
    ///   ① 容器里的原图必须与磁盘上的**逐字节相同**（打包不许顺手重编码）；
    ///   ② 打开到别的目录后，路径要指向解包出来的文件并且**真的能解码**；
    ///   ③ 源文件已丢失的那张要如实报"跳过"，不能假装打进去了。
    /// </summary>
    private static void ProjectPackageRoundTripCarriesSources()
    {
        string dir = MakeTempDir();
        try
        {
            var imgA = Fixture.OpaqueLike(64, 90, seed: 3);
            var imgB = Fixture.OpaqueLike(64, 90, seed: 4);
            string fileA = Path.Combine(dir, "a.png"), fileB = Path.Combine(dir, "b.png");
            byte[] bytesA = PngEncoder.EncodeToBytes(imgA), bytesB = PngEncoder.EncodeToBytes(imgB);
            File.WriteAllBytes(fileA, bytesA);
            File.WriteAllBytes(fileB, bytesB);

            var doc = new ProjectDocument { Mode = StitchMode.Vertical, Title = "打包测试" };
            var sources = new List<SourceImage>();
            foreach (var (path, bytes, name) in new[]
                     { (fileA, bytesA, "a.png"), (fileB, bytesB, "b.png") })
            {
                var src = new SourceImage
                {
                    Id = Project.NewId("src"), Path = path, FileName = name,
                    Size = new IntSize(64, 90), Format = "png",
                    FileSizeBytes = bytes.Length,
                };
                doc.Sources.Add(src);
                sources.Add(src);
                doc.Segments.Add(new Segment
                {
                    Id = Project.NewId("seg"), SourceId = src.Id,
                    SourceRect = new IntRect(0, 0, 64, 90),
                });
            }

            // 第三张：磁盘上不存在，打包必须点名跳过
            var ghost = new SourceImage
            {
                Id = Project.NewId("src"), Path = Path.Combine(dir, "gone.png"),
                FileName = "gone.png", Size = new IntSize(64, 90), Format = "png",
            };
            doc.Sources.Add(ghost);
            doc.Segments.Add(new Segment
            {
                Id = Project.NewId("seg"), SourceId = ghost.Id,
                SourceRect = new IntRect(0, 0, 64, 90),
            });

            string package = Path.Combine(dir, "pack" + ProjectPackage.Extension);
            ProjectPackage.PackReport report = ProjectPackage.Save(doc, package);
            Battery.AssertEqual(2, report.PackedSources, "应打进 2 张存在的原图");
            Battery.AssertEqual(1, report.Skipped.Count, "丢失的那张应被点名跳过");
            Battery.Assert(report.Skipped[0].Contains("gone.png", StringComparison.Ordinal),
                $"跳过清单要能指认是哪张，实际这句话是：{report.Skipped[0]}");
            Battery.Assert(File.Exists(package), "容器文件应已写出");

            // ① 逐字节核对容器里的原图
            using (var zip = System.IO.Compression.ZipFile.OpenRead(package))
            {
                Battery.Assert(zip.GetEntry(ProjectPackage.ManifestEntry) is not null,
                    "容器里应有 project.json");
                foreach (var (src, expected) in new[] { (sources[0], bytesA), (sources[1], bytesB) })
                {
                    var entry = zip.GetEntry($"{ProjectPackage.ImagesPrefix}{src.Id}/{src.FileName}");
                    Battery.Assert(entry is not null, $"容器里应有一张 {src.FileName}");
                    using var ms = new MemoryStream();
                    entry!.Open().CopyTo(ms);
                    Battery.AssertBytesEqual(expected, ms.ToArray(), $"{src.FileName} 在容器内的字节");
                }
                Battery.AssertEqual(report.PackedSources, zip.Entries.Count(x =>
                        x.FullName.StartsWith(ProjectPackage.ImagesPrefix, StringComparison.Ordinal)
                        && !x.FullName.EndsWith("/")),
                    "容器内条目数应等于打进去的原图数（丢失的那张不该有空壳）");
            }

            // ② 把原图目录整个搬走，证明容器自己能独立打开
            string movedAway = Path.Combine(dir, "moved");
            Directory.CreateDirectory(movedAway);
            File.Move(fileA, Path.Combine(movedAway, "a.png"));
            File.Move(fileB, Path.Combine(movedAway, "b.png"));

            string extract = Path.Combine(dir, "unpacked");
            ProjectDocument reopened = ProjectPackage.Load(package, extract);
            Battery.AssertEqual(doc.Segments.Count, reopened.Segments.Count, "片段数应一致");
            Battery.AssertEqual(2, reopened.Sources.Count(s => !s.SourceMissing),
                "两张原图都应从容器里解出来");

            var resolver = new LongStitch.App.Imaging.FileSourceResolver();
            foreach (SourceImage src in reopened.Sources)
            {
                if (src.SourceMissing) continue;
                Battery.Assert(File.Exists(src.Path), $"解包路径应真实存在：{src.Path}");
                Battery.Assert(src.Path.StartsWith(extract, StringComparison.Ordinal),
                    $"路径必须指向解包目录而不是原来的绝对路径：{src.Path}");
                resolver.Register(src);
                RgbaImage decoded = resolver.Resolve(src.Id);
                Battery.AssertEqual(new IntSize(64, 90), decoded.Size, "解出来的图尺寸应与记录一致");
                byte[] original = src.FileName == "a.png" ? bytesA : bytesB;
                Battery.AssertBytesEqual(PngEncoder.EncodeToBytes(decoded), original,
                    $"{src.FileName} 解包后重新编码应与打包前逐字节相同");
            }
            Battery.Assert(reopened.Sources.Any(s => s.SourceMissing),
                "丢失的那张在打开后仍应标为缺失，不能悄悄变出一张");

            Battery.Evidence($"容器 {report.TotalBytes} B：2 张原图逐字节一致、搬走原图后仍可打开并解码；" +
                             "丢失那张被点名跳过并保持缺失标记");
        }
        finally { TryDeleteDir(dir); }
    }

    private static string MakeTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "longstitch-checks-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static (Project P, MemoryResolver R, CanvasComposer C, EncoderRegistry E) MakeProject(
        int pieces = 3, int width = 48, int height = 160)
    {
        var images = new RgbaImage[pieces];
        for (int i = 0; i < pieces; i++)
            images[i] = Fixture.OpaqueLike(width, height, seed: 1000 + i * 37);
        (Project p, MemoryResolver r, _) = TestProject.Create(StitchMode.LongScroll, images);
        // 给中间那条缝一个真实重叠，让布局不是简单首尾相接
        if (pieces >= 2)
            p.Mutate("设定重叠", _ => p.Doc.Seams[0].ManualOffset = new IntPoint(0, height - 30));
        return (p, r, new CanvasComposer(r), new EncoderRegistry());
    }

    private static void ChunkedExportMatchesWhole()
    {
        (Project p, _, CanvasComposer composer, EncoderRegistry encoders) = MakeProject();
        string dir = MakeTempDir();
        try
        {
            var settings = new ExportSettings { Format = ExportFormat.Png, FileNameTemplate = "whole.{ext}" };
            ExportPlan plan = ExportPlanner.Build(p.Layout, settings, encoders, dir, composer);
            Battery.Assert(!plan.NeedsSplit, "默认不该分段");

            var engine = new ExportEngine(composer, encoders);
            ExportOutcome outcome = engine.Run(p.Layout, plan, settings, dir);
            Battery.Assert(outcome.Success, "导出应成功：" + outcome.Error);

            byte[] file = File.ReadAllBytes(Path.Combine(dir, "whole.png"));
            RgbaImage decoded = PngDecoder.Decode(file).Image;

            // 判据一：解码回来与整块合成逐字节相同
            RgbaImage whole = composer.RenderWhole(p.Layout);
            Battery.AssertBytesEqual(whole.PixelsRo, decoded.PixelsRo, "导出解码 vs 整块合成");

            // 判据二：逐条带各算一次哈希，防止"整图恰好相同但某条带被重复写了一次"。
            // 注意坐标系：RenderBand 返回的是<b>局部瓦片</b>（原点在 0,0），
            // 所以哈希要拿它自己的 Bounds；只有解码回来的整图才用画布坐标的 band 区域。
            // 这条测试一开始就是把画布坐标传给了局部瓦片，得到的是"空区域哈希"，假绿。
            for (int y = 0; y < whole.Height; y += ExportEngine.BandHeight)
            {
                int h = Math.Min(ExportEngine.BandHeight, whole.Height - y);
                var band = new IntRect(0, y, whole.Width, h);
                RgbaImage bandTile = composer.RenderBand(p.Layout, band);
                Battery.AssertEqual(new IntSize(whole.Width, h), bandTile.Size, $"第 {y} 行带的瓦片尺寸");
                Battery.AssertBytesEqual(
                    bandTile.RegionSha256(bandTile.Bounds),
                    decoded.RegionSha256(band),
                    $"第 {y}..{y + h} 行");
            }
            Battery.Evidence($"{whole} → {file.Length} 字节 PNG，逐带哈希与整块一致（走真实分块路径）");
        }
        finally { TryDeleteDir(dir); }
    }

    private static void SplitPiecesCoverCanvasExactly()
    {
        (Project p, _, CanvasComposer composer, EncoderRegistry encoders) = MakeProject(pieces: 3, height: 160);
        string dir = MakeTempDir();
        try
        {
            // 画布总高 = 160 + (160-30) + 160 = 450；每片 200 → 会切成 2 片
            var settings = new ExportSettings
            {
                Format = ExportFormat.Png,
                MaxSegmentHeight = 200,
                SegmentOverlap = 20,
                FileNameTemplate = "part_{index}.{ext}",
            };
            ExportPlan plan = ExportPlanner.Build(p.Layout, settings, encoders, dir, composer);
            Battery.Assert(plan.NeedsSplit, "总高 450 > 每片 200，应分段");
            Battery.Assert(plan.Pieces.Count >= 2, $"至少 2 片，实际 {plan.Pieces.Count}");

            var engine = new ExportEngine(composer, encoders);
            Battery.Assert(engine.Run(p.Layout, plan, settings, dir).Success, "分段导出应成功");

            int covered = plan.Pieces.Sum(x => x.Height - (x.Index > 1 ? settings.SegmentOverlap : 0));
            Battery.Assert(covered >= p.Layout.CanvasSize.Height,
                $"分段必须覆盖全部 {p.Layout.CanvasSize.Height} 行，实际覆盖 {covered}");

            // 每片都必须保持原宽度（需求 §7"每片保持原宽度与像素比例"）
            foreach (ExportPiece piece in plan.Pieces)
                Battery.AssertEqual(p.Layout.CanvasSize.Width, piece.Width, $"第 {piece.Index} 片宽度");

            // 每片解码回来必须与画布对应区域逐字节相同
            RgbaImage whole = composer.RenderWhole(p.Layout);
            foreach (ExportPiece piece in plan.Pieces)
            {
                string path = Path.Combine(dir, piece.FileName);
                Battery.Assert(File.Exists(path), $"应写出 {piece.FileName}");
                RgbaImage slice = PngDecoder.Decode(File.ReadAllBytes(path)).Image;
                IntRect region = piece.CanvasRegion.Intersect(whole.Bounds);
                Battery.AssertBytesEqual(whole.RegionSha256(region), slice.RegionSha256(slice.Bounds),
                    $"第 {piece.Index} 片内容");
            }
            Battery.Evidence($"{plan.Pieces.Count} 片，覆盖 {covered} 行 ≥ 画布 {p.Layout.CanvasSize.Height} 行，逐片字节一致");
        }
        finally { TryDeleteDir(dir); }
    }

    /// <summary>分段要"尽量避开拼缝"：容差内必须正好落在拼缝行上。</summary>
    private static void SplitSnapsToSeams()
    {
        (Project p, _, CanvasComposer composer, EncoderRegistry encoders) = MakeProject(pieces: 3, height: 160);
        int[] seams = ExportPlanner.SeamRows(p.Layout, p.Layout.CanvasSize.Height, 1);
        Battery.Assert(seams.Length == 2, $"3 段应有 2 条拼缝行，实际 {seams.Length}");

        // 让 max 恰好落在第一条拼缝附近（容差 = max/4），验证吸附生效
        int max = seams[0] + 5;
        var settings = new ExportSettings { Format = ExportFormat.Png, MaxSegmentHeight = max, SegmentOverlap = 0 };
        ExportPlan plan = ExportPlanner.Build(p.Layout, settings, encoders, null, composer);

        Battery.Assert(plan.Pieces.Count >= 2, "应被分段");
        bool snapped = plan.Pieces.Skip(1).Any(x => seams.Contains(x.CanvasRegion.Y));
        Battery.Assert(snapped,
            $"分段起点应吸附到拼缝行 [{string.Join(",", seams)}]，实际 [{string.Join(",", plan.Pieces.Skip(1).Select(x => x.CanvasRegion.Y))}]");
        _ = composer;
        Battery.Evidence($"max={max} → 片起点 {string.Join(", ", plan.Pieces.Select(x => x.CanvasRegion.Y))}");
    }

    private static void SplitHeightIncludesOverlap()
    {
        List<(int Start, int End)> ranges = ExportPlanner.ComputeRanges(
            totalHeight: 500, maxPieceHeight: 200, overlap: 30, seamRows: new[] { 210, 340 });
        Battery.Assert(ranges.Count >= 3, "应生成多片");
        Battery.AssertEqual(0, ranges[0].Start, "第一片应从 0 开始");
        Battery.AssertEqual(500, ranges[^1].End, "最后一片应到达画布底部");
        for (int i = 0; i < ranges.Count; i++)
        {
            Battery.Assert(ranges[i].End - ranges[i].Start <= 200,
                $"第 {i + 1} 片连同重叠不能超过 200 行，实际 {ranges[i].End - ranges[i].Start}");
            if (i > 0)
                Battery.Assert(ranges[i].Start <= ranges[i - 1].End &&
                    ranges[i].End > ranges[i - 1].End, "相邻片不能有缺口，也必须向前推进");
        }
        Battery.Evidence($"max=200、overlap=30、附近拼缝 210/340 → " +
            string.Join(", ", ranges.Select(r => $"[{r.Start},{r.End})")));
    }

    private static void CancelLeavesNoPart()
    {
        (Project p, _, CanvasComposer composer, EncoderRegistry encoders) = MakeProject(pieces: 4, width: 64, height: 400);
        string dir = MakeTempDir();
        try
        {
            var settings = new ExportSettings { Format = ExportFormat.Png, FileNameTemplate = "cancel.{ext}" };
            ExportPlan plan = ExportPlanner.Build(p.Layout, settings, encoders, dir, composer);

            using var cts = new CancellationTokenSource();
            // 在第 3 次进度回报时取消：此时 .part 已经创建并写了若干行，必须被清掉
            var probe = new CancelOnThirdReport(cts);
            var engine = new ExportEngine(composer, encoders);
            ExportOutcome outcome = engine.Run(p.Layout, plan, settings, dir, probe, cts.Token);

            Battery.Assert(outcome.Cancelled,
                $"应报告为已取消；实际 Success={outcome.Success} Cancelled={outcome.Cancelled} " +
                $"Error={outcome.Error ?? "(无)"} 已写文件={outcome.Files.Count}");
            string[] leftovers = Directory.GetFiles(dir);
            Battery.Assert(leftovers.Length == 0,
                $"取消后目录里不该有残留，实际有：{string.Join(", ", leftovers.Select(Path.GetFileName))}");
            Battery.Evidence("写到中途取消 → 0 个残留文件（含 .part）");
        }
        finally { TryDeleteDir(dir); }
    }

    /// <summary>在指定次数的进度回报后触发取消。</summary>
    private sealed class CancelOnThirdReport : IProgress<ExportProgress>
    {
        private readonly CancellationTokenSource _cts;
        private int _count;
        public CancelOnThirdReport(CancellationTokenSource cts) => _cts = cts;
        public void Report(ExportProgress value)
        {
            if (++_count == 3) _cts.Cancel();
        }
    }

    private static void HardLimitRefusesSilentCompress()
    {
        (Project p, _, CanvasComposer composer, EncoderRegistry encoders) = MakeProject(pieces: 2, height: 100);

        // 直接构造一个"硬性超限"的计划：真实场景是 JPEG 单边 > 65535，
        // 这里不真的去造 7 万像素高的图（那会白占几百 MB），而是量判据本身。
        FormatLimits.Verdict jpegTooTall = FormatLimits.Check(ExportFormat.Jpeg, 1000, 70000, -1);
        Battery.Assert(!jpegTooTall.Fits && jpegTooTall.Hard, "JPEG 70000 高必须是硬性拒绝");
        Battery.Assert(jpegTooTall.Reason!.Contains("65535"), $"原因要给出真实上限，实际：{jpegTooTall.Reason}");

        var settings = new ExportSettings { Format = ExportFormat.Png, FileNameTemplate = "blocked.{ext}" };
        ExportPlan normal = ExportPlanner.Build(p.Layout, settings, encoders, null, composer);
        var blocked = normal with { Verdict = jpegTooTall };

        string dir = MakeTempDir();
        try
        {
            var engine = new ExportEngine(composer, encoders);
            ExportOutcome outcome = engine.Run(p.Layout, blocked, settings, dir);
            Battery.Assert(!outcome.Success, "超限必须失败");
            Battery.Assert(Directory.GetFiles(dir).Length == 0, "超限不许留下任何文件");
            Battery.Assert(blocked.DownscaleDivisor == 1,
                "关键：超限绝不能被自动降尺寸绕过——降尺寸只能是用户主动选的");
            Battery.Evidence("硬性超限时：0 文件、无降尺寸、原因可展示");
        }
        finally { TryDeleteDir(dir); }
    }

    private static void DownscaleMatchesIndependentMath()
    {
        var src = Fixture.OpaqueLike(8, 5, seed: 555);
        var collect = new CollectingWriter(4, 3);   // 8/2 = 4 宽，ceil(5/2) = 3 高
        var sampler = new RowDownsampler(8, 2, collect);

        for (int y = 0; y < 5; y += 2)
        {
            int h = Math.Min(2, 5 - y);
            sampler.WriteBand(src.SubImage(new IntRect(0, y, 8, h)));
        }
        sampler.Finish();

        RgbaImage got = collect.Joined();
        Battery.AssertEqual(new IntSize(4, 3), got.Size, "8×5 缩小 2 倍应得 4×3");

        // 独立复算：同一套盒式平均公式在测试里重写一遍，不调用被测实现
        for (int oy = 0; oy < 3; oy++)
        {
            for (int ox = 0; ox < 4; ox++)
            {
                long r = 0, g = 0, b = 0, a = 0; int rows = 0, cols = 0;
                for (int sy = oy * 2; sy < Math.Min(5, oy * 2 + 2); sy++)
                {
                    for (int sx = ox * 2; sx < Math.Min(8, ox * 2 + 2); sx++)
                    {
                        RgbaColor c = src.GetPixel(sx, sy);
                        r += c.R; g += c.G; b += c.B; a += c.A;
                    }
                    cols = Math.Min(8, ox * 2 + 2) - ox * 2;
                    rows++;
                }
                int n = rows * cols;
                var want = new RgbaColor(
                    (byte)((r + n / 2) / n), (byte)((g + n / 2) / n),
                    (byte)((b + n / 2) / n), (byte)((a + n / 2) / n));
                Battery.AssertEqual(want, got.GetPixel(ox, oy), $"缩小后 ({ox},{oy})");
            }
        }
        Battery.Evidence("最后一组只有 1 行也按实际行数平均，内容不丢");
    }

    /// <summary>
    /// 缩小导出的<b>全链路</b>判据：走真实的 ExportPlanner → ExportEngine → PngEncoder → PngDecoder，
    /// 解码回来必须与"独立复算的盒式平均"逐字节相同。
    ///
    /// 为什么必须测全链路：`DownscaleMatchesIndependentMath` 只测 RowDownsampler 本身。
    /// 曾经的真缺陷是引擎把计划里的**输出坐标**当**源坐标**用（漏乘缩小倍数），
    /// 只读源图顶部一半的行——降采样器单元测试完全看不到这一层。
    /// </summary>
    private static void DownscaleFullPipelineMatchesIndependentMath()
    {
        // ── 场景一：不缩小 2 倍单文件（画布 450 行 → 输出 225 行）──
        RunOneDownscaleCase(divisor: 2, maxSegmentHeight: 0, overlap: 0, template: "ds2_{index}.{ext}");
        // ── 场景二：缩小 3 倍 + 分段（输出坐标分段，源起点必须乘回 3 且落在分组边界上）──
        RunOneDownscaleCase(divisor: 3, maxSegmentHeight: 60, overlap: 6, template: "ds3_{index}.{ext}");
    }

    private static void RunOneDownscaleCase(int divisor, int maxSegmentHeight, int overlap, string template)
    {
        (Project p, _, CanvasComposer composer, EncoderRegistry encoders) = MakeProject();
        string dir = MakeTempDir();
        try
        {
            var settings = new ExportSettings
            {
                Format = ExportFormat.Png,
                DownscaleDivisor = divisor,
                MaxSegmentHeight = maxSegmentHeight,
                SegmentOverlap = overlap,
                FileNameTemplate = template,
            };
            ExportPlan plan = ExportPlanner.Build(p.Layout, settings, encoders, dir, composer);
            Battery.AssertEqual(divisor, plan.DownscaleDivisor, "计划应带上用户选的缩小倍数");

            var engine = new ExportEngine(composer, encoders);
            ExportOutcome outcome = engine.Run(p.Layout, plan, settings, dir);
            Battery.Assert(outcome.Success, $"缩小 {divisor} 倍导出应成功：{outcome.Error}");

            RgbaImage whole = composer.RenderWhole(p.Layout);
            RgbaImage expected = BoxDownscale(whole, divisor);
            Battery.AssertEqual(plan.OutputPixelSize, expected.Size, "计划输出尺寸 vs 独立复算尺寸");

            foreach (ExportPiece piece in plan.Pieces)
            {
                string path = Path.Combine(dir, piece.FileName);
                Battery.Assert(File.Exists(path), $"应写出 {piece.FileName}");
                RgbaImage decoded = PngDecoder.Decode(File.ReadAllBytes(path)).Image;
                Battery.AssertEqual(new IntSize(piece.Width, piece.Height), decoded.Size,
                    $"第 {piece.Index} 片尺寸（输出坐标系）");

                // 独立复算这一片应有的内容：源区间 = 输出区间 × divisor（末端夹到画布底）
                int srcY = piece.CanvasRegion.Y * divisor;
                int srcRows = Math.Min(piece.Height * divisor, whole.Height - srcY);
                RgbaImage want = BoxDownscale(
                    whole.SubImage(new IntRect(0, srcY, whole.Width, srcRows)), divisor);
                Battery.AssertBytesEqual(want.PixelsRo, decoded.PixelsRo,
                    $"缩小 {divisor} 倍第 {piece.Index} 片内容（源行 {srcY}..{srcY + srcRows}）");
            }
            Battery.Evidence($"divisor={divisor}：{plan.Pieces.Count} 片、输出 {plan.OutputPixelSize}，" +
                             $"逐片与独立复算逐字节一致");
        }
        finally { TryDeleteDir(dir); }
    }

    /// <summary>
    /// 测试侧独立复算的盒式缩小：不调用 RowDownsampler，公式按定义重写一遍。
    /// 输出 (ox,oy) = 源 [oy*d, oy*d+d) × [ox*d, ox*d+d) 的平均（先乘后除、加半个除数四舍五入）。
    /// </summary>
    internal static RgbaImage BoxDownscale(RgbaImage src, int divisor)
    {
        int ow = (src.Width + divisor - 1) / divisor;
        int oh = (src.Height + divisor - 1) / divisor;
        var dst = new RgbaImage(ow, oh);
        for (int oy = 0; oy < oh; oy++)
        {
            for (int ox = 0; ox < ow; ox++)
            {
                long r = 0, g = 0, b = 0, a = 0;
                int n = 0;
                for (int sy = oy * divisor; sy < Math.Min(src.Height, oy * divisor + divisor); sy++)
                {
                    for (int sx = ox * divisor; sx < Math.Min(src.Width, ox * divisor + divisor); sx++)
                    {
                        RgbaColor c = src.GetPixel(sx, sy);
                        r += c.R; g += c.G; b += c.B; a += c.A;
                        n++;
                    }
                }
                dst.SetPixel(ox, oy, new RgbaColor(
                    (byte)((r + n / 2) / n), (byte)((g + n / 2) / n),
                    (byte)((b + n / 2) / n), (byte)((a + n / 2) / n)));
            }
        }
        return dst;
    }

    /// <summary>
    /// 界面承诺"0 = 不分段"，计划就必须兑现（审查第三轮 #2）。
    /// 曾用规则 `requestedMax &gt; 0 ? requestedMax : suggestedMax` 会在用户选 0 时
    /// 拿"查看器建议上限"偷偷自动分段——这里用超出建议上限的真实尺寸对账。
    /// </summary>
    private static void ZeroMeansNoSplitEvenAboveViewerAdvice()
    {
        // 两张 4×16500 → 画布 4×33000（> 32767 查看器建议上限，但 PNG 写得出来）
        (Project p, MemoryResolver r, _) = TestProject.Create(StitchMode.LongScroll,
            Fixture.OpaqueLike(4, 16500, seed: 91), Fixture.OpaqueLike(4, 16500, seed: 92));
        var encoders = new EncoderRegistry();
        Battery.Assert(p.Layout.CanvasSize.Height == 33000, $"夹具画布应为 33000 行，实际 {p.Layout.CanvasSize.Height}");

        var settings = new ExportSettings { Format = ExportFormat.Png, MaxSegmentHeight = 0 };
        ExportPlan plan = ExportPlanner.Build(p.Layout, settings, encoders, null, null);

        // 前提自证：这组尺寸确实"超出建议上限"——旧规则在这里一定会偷偷分段
        Battery.Assert(plan.Verdict.SuggestedMaxHeight > 0 && plan.OutputPixelSize.Height > plan.Verdict.SuggestedMaxHeight,
            $"夹具要落在「超出建议上限」区间才有杀伤力，实际 suggested={plan.Verdict.SuggestedMaxHeight}");
        int oldRuleEffective = plan.Verdict.SuggestedMaxHeight;   // 旧规则会用的值
        Battery.Assert(plan.OutputPixelSize.Height > oldRuleEffective, "旧规则判据复现：它确实会切");

        Battery.Assert(!plan.NeedsSplit, "用户选 0 = 不分段，计划不许自己分段");
        Battery.AssertEqual(1, plan.Pieces.Count, "不分段必须只有一个文件");
        Battery.AssertEqual(33000, plan.Pieces[0].Height, "整片必须覆盖全部行");
        Battery.Assert(!plan.IsHardBlock, "超建议上限只是警告，不是硬阻断");
        Battery.Assert(plan.Verdict.Reason is not null && plan.Verdict.Reason.Contains("32767"),
            $"提示文字要给出真实建议上限，实际：{plan.Verdict.Reason}");

        // 对照：用户显式选分段时照常分
        var splitSettings = new ExportSettings { Format = ExportFormat.Png, MaxSegmentHeight = 20000 };
        ExportPlan split = ExportPlanner.Build(p.Layout, splitSettings, encoders, null, null);
        Battery.Assert(split.NeedsSplit && split.Pieces.Count >= 2, "显式设 20000 应当分段");
        Battery.Evidence($"0=不分段兑现：33000 行单片（建议上限 {plan.Verdict.SuggestedMaxHeight} 只进提示）；" +
                         $"显式 20000 → {split.Pieces.Count} 片");
    }

    /// <summary>
    /// 缺失源图必须在**计划阶段**点名硬拦（审查第三轮 #3）。
    /// 不拦的话缺失片段在布局里是 0 尺寸占位，导出会"安静地少一段"。
    /// 三层都要量：SourceMissing 标记 / 解析器读不到 / 尺寸与记录不符。
    /// </summary>
    private static void MissingSourcesBlockPlanByName()
    {
        var encoders = new EncoderRegistry();

        // ── 情况一：文档里标了 SourceMissing ──
        {
            (Project p, MemoryResolver r, SourceImage[] srcs) = TestProject.Create(StitchMode.LongScroll,
                Fixture.OpaqueLike(24, 90, seed: 101), Fixture.OpaqueLike(24, 90, seed: 102));
            srcs[1].SourceMissing = true;
            ExportPlan plan = ExportPlanner.Build(p.Layout, new ExportSettings { Format = ExportFormat.Png },
                encoders, null, null);
            Battery.Assert(plan.IsHardBlock, "SourceMissing 必须硬阻断");
            Battery.Assert(plan.BlockingReason!.Contains(srcs[1].FileName),
                $"阻断原因必须点名具体文件，实际：{plan.BlockingReason}");
            Battery.Assert(!plan.BlockingReason!.Contains(srcs[0].FileName),
                "好端端的文件不许被连带点名");
        }

        // ── 情况二：标记没跟上，但给了 probe，真实解析失败 ──
        {
            (Project p, MemoryResolver r, SourceImage[] srcs) = TestProject.Create(StitchMode.LongScroll,
                Fixture.OpaqueLike(24, 90, seed: 111), Fixture.OpaqueLike(24, 90, seed: 112));
            r.Invalidate(srcs[1].Id);   // 模拟"会话中途文件被删"：标记还是 false
            ExportPlan plan = ExportPlanner.Build(p.Layout, new ExportSettings { Format = ExportFormat.Png },
                encoders, null, null, probe: r);
            Battery.Assert(plan.IsHardBlock && plan.BlockingReason!.Contains(srcs[1].FileName),
                $"probe 读不到必须点名阻断，实际：{plan.BlockingReason ?? "(没阻断)"}");
        }

        // ── 情况三：能解析，但尺寸与项目记录不符（文件被外部替换）──
        {
            (Project p, MemoryResolver r, SourceImage[] srcs) = TestProject.Create(StitchMode.LongScroll,
                Fixture.OpaqueLike(24, 90, seed: 121), Fixture.OpaqueLike(24, 90, seed: 122));
            r.Replace(srcs[1].Id, Fixture.OpaqueLike(24, 60, seed: 122));
            ExportPlan plan = ExportPlanner.Build(p.Layout, new ExportSettings { Format = ExportFormat.Png },
                encoders, null, null, probe: r);
            Battery.Assert(plan.IsHardBlock && plan.BlockingReason!.Contains("不符"),
                $"尺寸与记录不符必须点名阻断，实际：{plan.BlockingReason ?? "(没阻断)"}");

            // ── 收尾：引擎在硬阻断下一个文件都不许写 ──
            string dir = MakeTempDir();
            try
            {
                var engine = new ExportEngine(new CanvasComposer(r), encoders);
                ExportOutcome outcome = engine.Run(p.Layout, plan,
                    new ExportSettings { Format = ExportFormat.Png }, dir);
                Battery.Assert(!outcome.Success, "硬阻断时导出必须失败");
                Battery.Assert(Directory.GetFiles(dir).Length == 0, "硬阻断不许留下任何文件");
            }
            finally { TryDeleteDir(dir); }
        }

        Battery.Evidence("三层拦截各自点名：SourceMissing / probe 读不到 / 尺寸与记录不符；阻断时 0 文件");
    }

    /// <summary>
    /// 审查第四轮 P1：显式分段必须解除"整图单边超限"的硬拦，
    /// 否则"长图分段导出"恰好在最需要它的场景（总高 > 65535 的 JPEG）里失效。
    /// 同时证明两件事：不分段时仍按整图拦（不许悄悄放行），单片仍超限时照拦。
    /// </summary>
    private static void SplitExemptsWholeImageHardLimit()
    {
        // 两张 4×35000 → 总高 70000，超 JPEG 单边 65535
        (Project p, MemoryResolver r, SourceImage[] srcs) = TestProject.Create(StitchMode.LongScroll,
            Fixture.OpaqueLike(4, 35000, seed: 201), Fixture.OpaqueLike(4, 35000, seed: 202));
        var encoders = new EncoderRegistry();
        Battery.Assert(p.Layout.CanvasSize.Height == 70000, $"夹具总高应为 70000，实际 {p.Layout.CanvasSize.Height}");

        // ① 不分段：必须仍硬拦，原因点名 65535（不许被本轮豁免逻辑放水）
        ExportPlan alone = ExportPlanner.Build(p.Layout,
            new ExportSettings { Format = ExportFormat.Jpeg, MaxSegmentHeight = 0 }, encoders, null, null);
        Battery.Assert(alone.IsHardBlock, "不选分段时整图超限必须硬拦");
        Battery.Assert(alone.BlockingReason!.Contains("65535"), $"原因要给真实上限，实际：{alone.BlockingReason}");

        // ② 显式分段每片 30000：豁免生效，不再硬拦
        ExportPlan split = ExportPlanner.Build(p.Layout,
            new ExportSettings { Format = ExportFormat.Jpeg, MaxSegmentHeight = 30000 }, encoders, null, null);
        Battery.Assert(split.NeedsSplit && split.Pieces.Count >= 3, $"应切成 ≥3 片，实际 {split.Pieces.Count}");
        Battery.Assert(!split.IsHardBlock, $"显式分段后不得再拦（审查 P1 的核心诉求），实际：{split.BlockingReason}");
        // 注意：分段边界会吸附到拼缝行，单片可能略超"每片上限"（35000 > 30000），
        // 真正的判据是**每片都在单文件硬限之内**——豁免逻辑检查的也正是最高的那片。
        Battery.Assert(split.Pieces.All(x => x.Height <= 65535),
            $"每片必须在单文件限制内，实际 [{string.Join(",", split.Pieces.Select(x => x.Height))}]");

        // ③ 单片仍超限：每片 80000 → 单片 70000 行仍超 65535 → 照拦，并把原因指向"每片"
        ExportPlan tooTall = ExportPlanner.Build(p.Layout,
            new ExportSettings { Format = ExportFormat.Jpeg, MaxSegmentHeight = 80000 }, encoders, null, null);
        Battery.Assert(!tooTall.NeedsSplit && tooTall.IsHardBlock,
            "每片上限大到等于不分段时，整图超限依然要拦");

        // ④ 缺失源图不可被分段豁免
        srcs[1].SourceMissing = true;
        ExportPlan withMissing = ExportPlanner.Build(p.Layout,
            new ExportSettings { Format = ExportFormat.Jpeg, MaxSegmentHeight = 30000 }, encoders, null, null);
        Battery.Assert(withMissing.IsHardBlock && withMissing.BlockingReason!.Contains(srcs[1].FileName),
            "显式分段不得豁免缺失源图的硬拦");
        srcs[1].SourceMissing = false;

        // ⑤ 真走一遍引擎：豁免不是纸面放行——注册 App 的 GDI+ JPEG 编码器，
        //    把这套分段计划真的写成文件，再逐片用独立解码器（GDI+）读回尺寸。
        encoders.Register(new LongStitch.App.Imaging.GdiplusJpegEncoder());
        string dir = MakeTempDir();
        try
        {
            var jpegSettings = new ExportSettings
            {
                Format = ExportFormat.Jpeg,
                MaxSegmentHeight = 30000,
                FileNameTemplate = "jparts_{index}.{ext}",
            };
            ExportPlan runPlan = ExportPlanner.Build(p.Layout, jpegSettings, encoders, dir, null);
            var engine = new ExportEngine(new CanvasComposer(r), encoders);
            ExportOutcome outcome = engine.Run(p.Layout, runPlan, jpegSettings, dir);
            Battery.Assert(outcome.Success, $"分段 JPEG 导出应成功（曾被硬拦的场景）：{outcome.Error}");
            Battery.AssertEqual(runPlan.Pieces.Count, outcome.Files.Count, "落盘文件数应等于片数");
            foreach (ExportPiece piece in runPlan.Pieces)
            {
                string path = Path.Combine(dir, piece.FileName);
                using var bmp = new System.Drawing.Bitmap(path);   // 独立解码器读回
                Battery.AssertEqual(piece.Width, bmp.Width, $"第 {piece.Index} 片宽");
                Battery.AssertEqual(piece.Height, bmp.Height, $"第 {piece.Index} 片高");
            }
        }
        finally { TryDeleteDir(dir); }

        Battery.Evidence($"总高 70000 的 JPEG：不分段拦（65535）；每片 30000 → {split.Pieces.Count} 片放行，" +
                         "引擎真写出全部片且逐片读回尺寸一致；每片 80000 仍拦；缺失源图不因分段放行");
    }

    private static void ProjectFileRoundTrip()
    {
        (Project p, _, _, _) = MakeProject(pieces: 3, height: 120);
        string segId = p.Doc.Segments[1].Id;
        p.Mutate("加补丁", _ => p.Doc.Segments[0].Patches.Add(new PixelPatch
        {
            Id = Project.NewId("patch"),
            Kind = PatchKind.ExtendColumn,
            Target = new IntRect(40, 10, 8, 60),
            ExtendColumnX = 37,
            Reason = CleanupPlanner.AutoReasonPrefix + "滚动条：右侧厚 8px",
            Confidence = 0.83,
        }));
        p.Mutate("确认拼缝", _ => p.Doc.Seams[0].Confirm());

        string dir = MakeTempDir();
        try
        {
            string path = Path.Combine(dir, "a" + ProjectFile.Extension);
            ProjectFile.Save(p.Doc, path);

            // 判据一：参数保真。这里用 Deserialize 而不是 Load——
            // Load 还会顺带做"源文件在不在"的运行时检查，那是另一件事，混在一起
            // 一旦失败就分不清是"参数丢了"还是"路径本来就不存在"。
            ProjectDocument back = ProjectFile.Deserialize(File.ReadAllText(path));
            Battery.AssertTextEqual(
                ProjectFile.Serialize(p.Doc), ProjectFile.Serialize(back), "项目文件往返后的完整参数");
            Battery.AssertEqual(new IntPoint(0, 90), back.FindSeam(back.Segments[0].Id, segId)!.ManualOffset!.Value,
                "手动偏移");
            Battery.AssertEqual(1, back.Segments[0].Patches.Count, "清理补丁");
            Battery.Assert(back.Seams[0].Confirmed, "确认状态");

            // 判据二：Load 要把找不到的源图明确标成"缺失"，供界面列出与重新定位，
            // 且这个标记不许影响任何编辑参数。
            ProjectDocument loaded = ProjectFile.Load(path);
            Battery.Assert(loaded.Sources.All(s => s.SourceMissing),
                "memory:\\ 开头的假路径应当全部被标为源文件缺失，而不是静默当作正常");
            Battery.AssertEqual(new IntPoint(0, 90),
                loaded.FindSeam(loaded.Segments[0].Id, loaded.Segments[1].Id)!.ManualOffset!.Value,
                "缺失标记不该改动拼缝参数");
            Battery.Evidence("往返一致：偏移、补丁、确认状态、模式全部保住（验收场景 8）");
        }
        finally { TryDeleteDir(dir); }
    }

    /// <summary>
    /// 第五十六改②（用户图2蓝框的另一半）：「透明」勾勾掉后，PNG 导出必须把透明像素
    /// 真的压到所选底色上。旧通路只有 JPEG 编码器压底色，PNG 一律透明原样进文件、
    /// 色卡在 PNG 档整个被禁用——勾都没有，自然没人验过这条通路。
    ///
    /// ═══ 为什么夹具要 1200 行高 ═══
    /// ExportEngine 的分块路径（CreateWriter→WriteBand）与一次性路径（Encode）都要被走过：
    /// 压底逻辑在 PngRegionWriter.WriteBand 里也抄了一份，两条路任何一条漏抄都会在这里红。
    /// 夹具高过 BandHeight 才会真的切成多条带。压底是逐像素独立运算，"每条带各自压"与
    /// "整图先压再分块"逐字节一致——精度红线不受影响，这一格同时就是它的常驻证人。
    /// </summary>
    private static void PngOpaqueFlattenHonoursBackground()
    {
        // 上半全透明红（alpha=0），下半不透明蓝——透明处该变底色、不透明处一像素不许动
        var img = new RgbaImage(40, 1200);
        img.Fill(new IntRect(0, 0, 40, 600), new RgbaColor(255, 0, 0, 0));
        img.Fill(new IntRect(0, 600, 40, 600), new RgbaColor(0, 0, 255, 255));
        (Project p, MemoryResolver r, _) = TestProject.Create(StitchMode.LongScroll, img);
        var composer = new CanvasComposer(r);
        var encoders = new EncoderRegistry();
        string dir = MakeTempDir();
        try
        {
            // ── 勾掉透明：透明处必须变成所选底色，不透明处原样 ──
            var settings = new ExportSettings
            {
                Format = ExportFormat.Png,
                PngTransparent = false,
                Background = new RgbaColor(255, 220, 0, 255),
                FileNameTemplate = "flat.{ext}",
            };
            ExportPlan plan = ExportPlanner.Build(p.Layout, settings, encoders, dir, composer);
            var engine = new ExportEngine(composer, encoders);
            ExportOutcome outcome = engine.Run(p.Layout, plan, settings, dir);
            Battery.Assert(outcome.Success, "勾掉透明的导出应成功：" + outcome.Error);

            RgbaImage flat = PngDecoder.Decode(File.ReadAllBytes(Path.Combine(dir, "flat.png"))).Image;
            Span<byte> transparentRow = flat.Row(5);
            Battery.AssertEqual((byte)255, transparentRow[5 * 4 + 3],
                "透明处 alpha 必须被压成 255（压底后不许再留透明）");
            Battery.Assert(transparentRow[5 * 4] == 255 && transparentRow[5 * 4 + 1] == 220 && transparentRow[5 * 4 + 2] == 0,
                $"透明处应合成到所选底色 (255,220,0)，实际 ({transparentRow[5 * 4]},{transparentRow[5 * 4 + 1]},{transparentRow[5 * 4 + 2]})");
            Span<byte> opaqueRow = flat.Row(1150);
            Battery.Assert(opaqueRow[5 * 4] == 0 && opaqueRow[5 * 4 + 1] == 0 && opaqueRow[5 * 4 + 2] == 255 && opaqueRow[5 * 4 + 3] == 255,
                "不透明处一像素都不许被压底改动");

            // ── 对照组：勾上透明（默认）→ alpha=0 原样保留，历史行为不许被顺手改掉 ──
            var keep = new ExportSettings
            {
                Format = ExportFormat.Png,
                PngTransparent = true,
                Background = new RgbaColor(255, 220, 0, 255),
                FileNameTemplate = "keep.{ext}",
            };
            ExportPlan keepPlan = ExportPlanner.Build(p.Layout, keep, encoders, dir, composer);
            Battery.Assert(engine.Run(p.Layout, keepPlan, keep, dir).Success, "对照组导出应成功");
            RgbaImage kept = PngDecoder.Decode(File.ReadAllBytes(Path.Combine(dir, "keep.png"))).Image;
            Span<byte> keptRow = kept.Row(5);
            Battery.AssertEqual((byte)0, keptRow[5 * 4 + 3],
                "勾上透明（默认）时 alpha=0 必须原样保留——历史行为不许被这次改动带走");
            Battery.Evidence($"勾掉透明：透明处→(255,220,0,255)、不透明处原样；对照：alpha=0 保留；夹具 1200 行跨 {ExportEngine.BandHeight} 行带走分块路径");
        }
        finally { TryDeleteDir(dir); }
    }

    /// <summary>把收集到的行带拼成一张图，便于整体比对。</summary>
    private sealed class CollectingWriter : IRegionWriter
    {
        private readonly List<RgbaImage> _bands = new();
        public CollectingWriter(int width, int expectedRows) { Width = width; ExpectedRows = expectedRows; }
        public int Width { get; }
        public int ExpectedRows { get; }
        public int RowsWritten { get; private set; }

        public void WriteBand(RgbaImage band)
        {
            if (band.Width != Width)
                throw new ArgumentException($"带宽 {band.Width} ≠ 期望 {Width}");
            _bands.Add(band.Clone());
            RowsWritten += band.Height;
        }

        public void Finish()
        {
            if (RowsWritten != ExpectedRows)
                throw new Exception($"行数不足：写了 {RowsWritten}，期望 {ExpectedRows}");
        }

        public void Dispose() { }

        public RgbaImage Joined()
        {
            var all = new RgbaImage(Width, RowsWritten);
            int y = 0;
            foreach (RgbaImage band in _bands)
            {
                all.Blit(band, band.Bounds, 0, y);
                y += band.Height;
            }
            return all;
        }
    }

    private static void TryDeleteDir(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* 临时目录留给系统回收，不影响判据 */ }
    }
}
