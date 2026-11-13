using System.IO.Compression;
using LongStitch.Core.Model;

namespace LongStitch.Core.ProjectIO;

/// <summary>
/// 打包项目：把"项目参数 + 全部原图"存进一个 zip 容器（扩展名 <see cref="Extension"/>）。
///
/// ═══ 为什么默认不打包 ═══
/// 产品需求明确："项目文件仅引用本地源图，不将原图嵌入项目，除非用户执行明确的『打包项目』。"
/// 原因是截图里是聊天内容，复制一份就多一份泄露面；而且一张 5 万像素长图的原图可能有几百 MB，
/// 静默嵌进项目文件会让"保存"变成一件又慢又占盘的事。所以这是**单独一条显式命令**。
///
/// ═══ 为什么用 zip 而不是自定义容器 ═══
/// <see cref="ZipArchive"/> 在 .NET 8 的共享框架里，零 NuGet 依赖就能用；
/// 同时它本身就是"用户能用资源管理器直接打开看"的格式，出了问题不至于变成黑盒。
///
/// ═══ 内部结构 ═══
/// <code>
/// project.json                 ← 与 .lstick 完全同一份 manifest（复用 ProjectFile 的序列化）
/// images/&lt;sourceId&gt;/&lt;原文件名&gt;   ← 按源图 Id 分目录，天然避免同名冲突
/// </code>
/// 打开时把 images/ 解到指定目录，并把每条 SourceImage.Path 改写为解包后的绝对路径；
/// 解包目录固定在容器旁边的 <c>&lt;容器名&gt;.unpacked/</c>，看得见、也删得掉。
/// </summary>
public static class ProjectPackage
{
    public const string Extension = ".lstickx";
    public const string ManifestEntry = "project.json";
    public const string ImagesPrefix = "images/";

    /// <summary>一次打包的结果，界面据此如实报告"进去几张、为什么少了几张"。</summary>
    /// <param name="PackedSources">真正写进容器的源图数量。</param>
    /// <param name="Skipped">跳过的文件名与原因（源文件已丢失、读不出来等）。</param>
    /// <param name="TotalBytes">容器字节数。</param>
    public sealed record PackReport(int PackedSources, IReadOnlyList<string> Skipped, long TotalBytes);

    /// <summary>
    /// 写入容器。原图**按字节原样拷贝**，不解码、不重编码——
    /// 打包不能成为第二个可能改变像素的地方，这一点与导出同源。
    /// </summary>
    public static PackReport Save(ProjectDocument doc, string packagePath)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);

        string? directory = Path.GetDirectoryName(Path.GetFullPath(packagePath));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        // 先写临时文件再替换：打包中途失败不能把用户原来的容器变成半截 zip
        string temp = packagePath + ".tmp";
        var skipped = new List<string>();
        int packed = 0;

        try
        {
            using (var zip = new ZipArchive(new FileStream(temp, FileMode.Create, FileAccess.Write),
                       ZipArchiveMode.Create))
            {
                ZipArchiveEntry manifest = zip.CreateEntry(ManifestEntry, CompressionLevel.Optimal);
                using (Stream writer = manifest.Open())
                using (var text = new StreamWriter(writer, new System.Text.UTF8Encoding(false)))
                    text.Write(ProjectFile.Serialize(doc));

                foreach (SourceImage src in doc.Sources)
                {
                    string entryName = $"{ImagesPrefix}{src.Id}/{src.FileName}";
                    if (!File.Exists(src.Path))
                    {
                        skipped.Add($"{src.FileName}：源文件不在（{src.Path}）");
                        continue;
                    }
                    try
                    {
                        ZipArchiveEntry entry = zip.CreateEntry(entryName, CompressionLevel.NoCompression);
                        using Stream outStream = entry.Open();
                        using var inStream = new FileStream(src.Path, FileMode.Open, FileAccess.Read,
                            FileShare.Read, 1 << 20);
                        inStream.CopyTo(outStream);
                        packed++;
                    }
                    catch (Exception ex)
                    {
                        skipped.Add($"{src.FileName}：{ex.Message}");
                    }
                }
            }

            if (File.Exists(packagePath)) File.Delete(packagePath);
            File.Move(temp, packagePath);
        }
        catch
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* 临时文件删不掉不影响报错 */ }
            throw;
        }

        return new PackReport(packed, skipped, new FileInfo(packagePath).Length);
    }

    /// <summary>
    /// 读容器并把原图解到 <paramref name="extractDirectory"/>。
    /// 返回的文档里每条 <see cref="SourceImage.Path"/> 都已指向解包后的文件，
    /// 调用方（界面）拿去直接就能注册进解析器。
    /// </summary>
    /// <param name="packagePath">.lstickx 文件。</param>
    /// <param name="extractDirectory">解包目录；由调用方决定放哪，本类不猜。</param>
    public static ProjectDocument Load(string packagePath, string extractDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(extractDirectory);
        if (!File.Exists(packagePath)) throw new FileNotFoundException("找不到打包文件", packagePath);

        string manifestJson;
        using (var zip = ZipFile.OpenRead(packagePath))
        {
            ZipArchiveEntry? manifest = zip.GetEntry(ManifestEntry);
            if (manifest is null)
                throw new InvalidDataException("这个容器里没有 project.json，不是 LongStitch 的打包文件。");

            using var reader = new StreamReader(manifest.Open(), System.Text.Encoding.UTF8);
            manifestJson = reader.ReadToEnd();

            foreach (ZipArchiveEntry entry in zip.Entries)
            {
                if (!entry.FullName.StartsWith(ImagesPrefix, StringComparison.Ordinal)) continue;
                if (entry.FullName.Length == ImagesPrefix.Length) continue;   // 目录项本身
                if (Path.GetExtension(entry.FullName).Length == 0 && entry.FullName.EndsWith('/')) continue;

                // 解包路径完全按容器内的相对路径来，不做任何"智能"改名：
                // 打开后文件在哪，用户对着容器看一眼就能对上。
                string target = Path.Combine(extractDirectory,
                    entry.FullName[ImagesPrefix.Length..].Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, overwrite: true);
            }
        }

        ProjectDocument doc = ProjectFile.Deserialize(manifestJson);

        // 路径改写：manifest 里记的是"打包那台机器上的原始路径"，本机的解包路径才是可用的。
        foreach (SourceImage src in doc.Sources)
        {
            string dir = Path.Combine(extractDirectory, src.Id);
            if (!Directory.Exists(dir))
            {
                src.SourceMissing = true;
                continue;
            }
            string? file = Directory.EnumerateFiles(dir).FirstOrDefault();
            if (file is null) { src.SourceMissing = true; continue; }
            src.Path = file;
            src.FileName = Path.GetFileName(file);
            src.SourceMissing = false;
        }
        return doc;
    }

    /// <summary>容器旁边默认的解包目录（可见、可删，不用隐藏缓存）。</summary>
    public static string DefaultExtractDirectory(string packagePath) =>
        Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(packagePath)) ?? ".",
            Path.GetFileNameWithoutExtension(packagePath) + ".unpacked");
}
