using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using LongStitch.Core.Model;

namespace LongStitch.Core.ProjectIO;

/// <summary>
/// 项目文件读写（JSON）。
///
/// 为什么用 JSON 而不是二进制：项目文件只存参数、体积通常几 KB，可读性远比紧凑重要——
/// 出问题时用户/协作 AI 能直接打开看"这条缝到底存了什么"，这对一个要上架 GitHub 的
/// 开源工具是实打实的价值。
///
/// 另外，撤销栈也复用这里的序列化：快照 = 项目文件内容。
/// 于是"撤销 N 步之后的状态"和"存档再读档"走的是同一份序列化实现，
/// 不会出现"撤销正常、存盘丢参数"这种两套代码才有的分裂 bug。
/// </summary>
public static class ProjectFile
{
    /// <summary>项目文件扩展名（需求 §11 待定值之一，这里先定死，改一处常量即可）。</summary>
    public const string Extension = ".lstick";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        // 枚举写成 "LongScroll" 而不是 0，便于人读与手工核对
        Converters = { new JsonStringEnumConverter() },
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,  // 中文标题不被转义成 \uXXXX
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Serialize(ProjectDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        doc.SchemaVersion = ProjectDocument.CurrentSchemaVersion;
        return JsonSerializer.Serialize(doc, Options);
    }

    /// <summary>
    /// 反序列化。版本高于本程序支持的版本时抛错并说明，绝不"尽力读"——
    /// 读一半丢掉手动拼缝参数，比拒绝打开糟糕得多。
    /// </summary>
    public static ProjectDocument Deserialize(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        ProjectDocument? doc;
        try
        {
            doc = JsonSerializer.Deserialize<ProjectDocument>(json, Options);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("项目文件不是合法的 JSON，可能已损坏：" + ex.Message, ex);
        }

        if (doc is null) throw new InvalidDataException("项目文件内容为空");
        if (doc.SchemaVersion > ProjectDocument.CurrentSchemaVersion)
            throw new InvalidDataException(
                $"项目文件版本 v{doc.SchemaVersion} 高于本程序支持的 v{ProjectDocument.CurrentSchemaVersion}，" +
                "请升级 LongStitch 后再打开（本程序不会猜测未知字段）");

        Normalize(doc);
        return doc;
    }

    public static void Save(ProjectDocument doc, string path) => File.WriteAllText(path, Serialize(doc));

    /// <summary>读档。同时把"源文件是否还在"标出来，供界面列出缺失项与重新定位入口。</summary>
    public static ProjectDocument Load(string path)
    {
        string json = File.ReadAllText(path);
        ProjectDocument doc = Deserialize(json);
        foreach (SourceImage src in doc.Sources)
            src.SourceMissing = !File.Exists(src.Path);
        return doc;
    }

    /// <summary>
    /// 读档后的结构自检与最小修复：丢掉指向不存在的片段/源图的悬空引用。
    /// 这一步让"手工编辑过的项目文件"或"被旧版本写坏的文件"不至于让界面崩溃，
    /// 同时把丢掉的条目数量回报给调用方，由界面显示"已忽略 N 条无效拼缝"。
    /// </summary>
    public static int Normalize(ProjectDocument doc)
    {
        int dropped = 0;
        doc.Sources ??= new();
        doc.Segments ??= new();
        doc.Seams ??= new();
        doc.Crop ??= new();
        doc.Cleanup ??= new();
        doc.Spacing ??= new();
        doc.Export ??= new();

        var sourceIds = doc.Sources.Select(s => s.Id).ToHashSet();
        for (int i = doc.Segments.Count - 1; i >= 0; i--)
        {
            if (!sourceIds.Contains(doc.Segments[i].SourceId))
            {
                doc.Segments.RemoveAt(i);
                dropped++;
            }
        }

        var segmentIds = doc.Segments.Select(s => s.Id).ToHashSet();
        for (int i = doc.Seams.Count - 1; i >= 0; i--)
        {
            Seam s = doc.Seams[i];
            if (!segmentIds.Contains(s.BeforeId) || !segmentIds.Contains(s.AfterId))
            {
                doc.Seams.RemoveAt(i);
                dropped++;
            }
        }

        foreach (Segment seg in doc.Segments)
        {
            seg.Patches ??= new();
            seg.Patches.RemoveAll(p => p.Target.IsEmpty);
        }

        // 第九轮：裁切参数的迁移与修补点。
        // v1 项目文件里根本没有 beforeCropBottom / afterCropTop，反序列化得到 0/0，
        // 而重叠记着 60——那不代表"用户选择不裁"，只代表"那个版本还没有裁切概念"。
        // 这里按当时唯一的合法语义补成默认档（底裁=重叠、顶裁=0），
        // 于是老文件打开后的成品像素与它当年导出的逐字节相同（判据见 crop 组）。
        // 手工编辑过的文件里出现"两侧之和 ≠ 重叠"也在这里一并夹回不变式。
        foreach (Seam seam in doc.Seams) seam.NormalizeAlignment();

        return dropped;
    }
}
