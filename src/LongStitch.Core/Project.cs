using LongStitch.Core.Geometry;
using LongStitch.Core.Layout;
using LongStitch.Core.Model;
using LongStitch.Core.ProjectIO;

namespace LongStitch.Core;

/// <summary>
/// 运行时项目聚合：持有文档、维护撤销栈、缓存布局、把"结构操作"收在一处。
///
/// 用法约定（很重要，界面无脑遵守就不会出错）：
///   · 任何改动都通过 <see cref="Mutate"/> 提交，它负责记一条可撤销历史、
///     自增 <see cref="Revision"/>、置脏标记、触发 <see cref="Changed"/>。
///   · 一次拖动 = 一次 Mutate（在鼠标松开时提交），不要每移动 1 像素记一条历史，
///     否则 Ctrl+Z 要按几百次才能退回，需求 §4 的撤销语义就废了。
///   · 读布局用 <see cref="Layout"/>，它按 Revision 惰性重算，调用方不用操心缓存。
/// </summary>
public sealed class Project
{
    /// <summary>历史深度上限。快照只是几 KB 参数，80 步足够；再深对内存无感但对操作无意义。</summary>
    public const int HistoryLimit = 80;

    public ProjectDocument Doc { get; private set; }

    /// <summary>当前项目文件路径；null 表示尚未保存过。</summary>
    public string? FilePath { get; set; }

    /// <summary>每次参数变更 +1。瓦片缓存、导出计划都以它作失效依据。</summary>
    public int Revision { get; private set; }

    /// <summary>有未保存的改动。</summary>
    public bool IsDirty { get; private set; }

    /// <summary>参数或历史发生变化。界面据此刷新（一次 Mutate 只触发一次）。</summary>
    public event Action? Changed;

    /// <summary>
    /// Undo / Redo <b>换好文档之后</b>再触发这一个（顺序有意义：界面先看到新参数与新布局，
    /// 再决定屏幕要不要跟着换）。参数那一份真相走 <see cref="Changed"/>，
    /// 显示层那一份附件走这里——<see cref="HistoryDisplayState"/> 的类注释写了为什么必须分两条。
    /// </summary>
    public event Action<HistoryStep, HistoryDisplayState?>? HistoryStepped;

    private readonly List<Entry> _undo = new();
    private readonly List<Entry> _redo = new();

    /// <summary>
    /// 一条历史 = 文档快照 + 菜单文字 + 随它一起存档的显示层快照（没有就是 null）。
    /// 显示快照对 Core 完全不透明，Core 只负责把它跟着条目在两个栈之间搬来搬去。
    /// </summary>
    private readonly record struct Entry(string Json, string Label, HistoryDisplayState? Display);

    private CanvasLayout? _layout;
    private int _layoutRevision = -1;

    public Project(ProjectDocument? document = null)
    {
        Doc = document ?? new ProjectDocument();
        ProjectFile.Normalize(Doc);
    }

    /// <summary>当前布局（按 Revision 缓存）。</summary>
    public CanvasLayout Layout
    {
        get
        {
            if (_layout is null || _layoutRevision != Revision)
            {
                _layout = CanvasLayout.Build(Doc);
                _layoutRevision = Revision;
            }
            return _layout;
        }
    }

    // ───────────────────────── 变更入口 ─────────────────────────

    /// <summary>
    /// 提交一次可撤销的改动。<paramref name="label"/> 会出现在"编辑"菜单的撤销项文字里。
    /// </summary>
    /// <param name="display">
    /// 随这条历史一起存档的<b>显示层</b>快照（第十四轮需求一）。界面只在"这一趟会挪屏幕"时传它：
    /// 固定后一段拖对齐要在撤销时把纯显示补偿还回去。Core 不解读它，只在 Undo/Redo 时原样交还。
    /// 传 null（绝大多数改动）就是现在的行为：撤销只改文档，屏幕一个像素都不动。
    /// </param>
    public void Mutate(string label, Action<ProjectDocument> change, HistoryDisplayState? display = null)
    {
        ArgumentNullException.ThrowIfNull(change);

        // 先把"改之前"的状态入栈，再改。这样异常时历史不会多出一条错误的快照。
        _undo.Add(new Entry(ProjectFile.Serialize(Doc), label, display));
        if (_undo.Count > HistoryLimit) _undo.RemoveAt(0);
        _redo.Clear();

        try
        {
            change(Doc);
            ProjectFile.Normalize(Doc);
        }
        catch
        {
            _undo.RemoveAt(_undo.Count - 1);   // 改动失败：撤回刚压的快照
            throw;
        }

        Revision++;
        IsDirty = true;
        Changed?.Invoke();
    }

    /// <summary>只刷新缓存与界面、不记历史（用于纯显示层面的状态变化）。</summary>
    public void Touch()
    {
        Revision++;
        Changed?.Invoke();
    }

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public string? NextUndoLabel => _undo.Count > 0 ? _undo[^1].Label : null;
    public string? NextRedoLabel => _redo.Count > 0 ? _redo[^1].Label : null;

    public void Undo()
    {
        if (!CanUndo) return;
        Entry back = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        // 显示快照原样带进 redo 栈：那一条重做要还的就是同一趟手势留下的那一段屏幕偏移。
        // Json 换成"此刻的状态"（也就是那次改动做完之后的文档），Label 与 Display 都跟着这条历史走，不换。
        _redo.Add(new Entry(ProjectFile.Serialize(Doc), back.Label, back.Display));
        Doc = ProjectFile.Deserialize(back.Json);
        Revision++;
        IsDirty = true;
        Changed?.Invoke();
        // 顺序：先让界面按新参数刷完（Changed），再问它"屏幕要不要一起退回"（HistoryStepped）。
        // 反过来就会拿旧布局去算补偿该还多少，量错的是屏幕。
        HistoryStepped?.Invoke(HistoryStep.Undo, back.Display);
    }

    public void Redo()
    {
        if (!CanRedo) return;
        Entry forward = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        _undo.Add(new Entry(ProjectFile.Serialize(Doc), forward.Label, forward.Display));
        Doc = ProjectFile.Deserialize(forward.Json);
        Revision++;
        IsDirty = true;
        Changed?.Invoke();
        HistoryStepped?.Invoke(HistoryStep.Redo, forward.Display);
    }

    /// <summary>保存成功后调用，清掉未保存标记（不动历史栈）。</summary>
    public void MarkSaved(string? path = null)
    {
        FilePath = path ?? FilePath;
        IsDirty = false;
        Changed?.Invoke();
    }

    /// <summary>换一个项目（新建/打开）。历史与脏标记都清零。</summary>
    public void ReplaceDocument(ProjectDocument doc, string? path)
    {
        Doc = doc;
        FilePath = path;
        ProjectFile.Normalize(Doc);
        _undo.Clear();
        _redo.Clear();
        Revision++;
        IsDirty = false;
        Changed?.Invoke();
    }

    // ───────────────────────── 结构操作 ─────────────────────────

    /// <summary>为一张源图追加一个覆盖全图的片段。返回新片段。</summary>
    public Segment AppendSegmentForSource(SourceImage src)
    {
        var seg = new Segment
        {
            Id = NewId("seg"),
            SourceId = src.Id,
            SourceRect = new IntRect(0, 0, src.Size.Width, src.Size.Height),
        };
        Mutate("添加图片", d => d.Segments.Add(seg));
        return seg;
    }

    /// <summary>
    /// 让拼缝列表与片段顺序严格一致：为新的相邻对建 Pending 缝，删掉不再相邻的孤儿缝。
    /// 重排、删除、切分之后都要走它——需求 §4.1"排序、删除、追加后仅重算受影响的拼缝"
    /// 的实现就是"仍然相邻的那条缝原样保留参数，其余重建"。
    /// </summary>
    public void EnsureSeams()
    {
        Mutate("重排", d =>
        {
            var wanted = new List<Seam>();
            for (int i = 0; i + 1 < d.Segments.Count; i++)
            {
                string a = d.Segments[i].Id, b = d.Segments[i + 1].Id;
                Seam? existing = d.Seams.FirstOrDefault(s => s.BeforeId == a && s.AfterId == b);
                wanted.Add(existing ?? new Seam { Id = NewId("seam"), BeforeId = a, AfterId = b });
            }
            d.Seams = wanted;
        });
    }

    /// <summary>把片段从 from 移到 to（缩略图拖动排序）。</summary>
    public void MoveSegment(int from, int to)
    {
        if (from == to) return;
        if (from < 0 || from >= Doc.Segments.Count || to < 0 || to >= Doc.Segments.Count) return;
        Mutate("调整顺序", d =>
        {
            Segment moved = d.Segments[from];
            d.Segments.RemoveAt(from);
            d.Segments.Insert(Math.Min(to, d.Segments.Count), moved);
            RebuildSeamsInPlace(d);
        });
    }

    /// <summary>
    /// 按给定顺序重排全部片段（"按文件名/拍摄时间排序"菜单）。
    /// 排序与拼缝重建记在<b>同一条</b>历史里——分成两条的话，
    /// 用户要按两次 Ctrl+Z 才能完全退回排序前（审查问题 #10）。
    /// </summary>
    public void ReorderSegments(List<Segment> ordered)
    {
        ArgumentNullException.ThrowIfNull(ordered);
        Mutate("排序", d =>
        {
            d.Segments = ordered;
            RebuildSeamsInPlace(d);
        });
    }

    /// <summary>删除一个片段（"删段"）。相邻拼缝随之重建。</summary>
    public void RemoveSegment(string segmentId) =>
        Mutate("删除片段", d =>
        {
            d.Segments.RemoveAll(s => s.Id == segmentId);
            RebuildSeamsInPlace(d);
        });

    /// <summary>
    /// 在画布纵坐标 canvasY 处把该段切成两段（需求 §6"切分"）。
    /// 返回 false 表示这个位置切不动：不在任何片段内、或切完会留下零高度的空段。
    /// 切分不复制像素、只把源矩形一分为二，因此它天然是无损且可撤销的。
    ///
    /// 注意坐标换算：用户点的是<b>有效区域</b>（已裁边）里的位置，而片段存的是<b>未裁边</b>的源矩形，
    /// 所以切点要先换算回源矩形坐标；两半各自继承裁边时也要拆开——
    /// 上半保留上边裁切、下半保留下边裁切，否则会把同一层裁切重复算两次。
    /// </summary>
    public bool SplitSegmentAtCanvas(int canvasY)
    {
        CanvasLayout layout = Layout;
        PlacedSegment? target = layout.Placed.FirstOrDefault(p =>
            !p.IsEmpty && canvasY >= p.Rect.Y && canvasY < p.Rect.Bottom);
        if (target is null) return false;

        int localY = canvasY - target.Rect.Y;              // 有效区域内的行
        if (localY <= 0 || localY >= target.Rect.Height) return false;   // 贴边切会留空段

        Segment originalSnapshot = target.Segment;
        int cut = target.SourceRect.Y + localY - originalSnapshot.SourceRect.Y;  // 源矩形里的切点
        if (cut <= 0 || cut >= originalSnapshot.SourceRect.Height) return false;

        string id = originalSnapshot.Id;
        Mutate("切分片段", d =>
        {
            int i = d.Segments.FindIndex(s => s.Id == id);
            if (i < 0) return;
            Segment original = d.Segments[i];
            IntRect r = original.SourceRect;
            Insets crop = original.EdgeCrop;

            var first = new Segment
            {
                Id = original.Id,
                SourceId = original.SourceId,
                SourceRect = new IntRect(r.X, r.Y, r.Width, cut),
                // 上半只保留上边与左右裁切；下边裁切已经"用掉"在下半的归属里
                EdgeCrop = new Insets(crop.Left, crop.Top, crop.Right, 0),
                Patches = original.Patches
                    .Where(p => p.Target.Bottom <= localY).Select(p => CopyPatch(p)).ToList(),
            };
            var second = new Segment
            {
                Id = NewId("seg"),
                SourceId = original.SourceId,
                SourceRect = new IntRect(r.X, r.Y + cut, r.Width, r.Height - cut),
                EdgeCrop = new Insets(crop.Left, 0, crop.Right, crop.Bottom),
                // 下半的补丁坐标要跟着平移，否则滚动条那一块会盖到错误的位置上
                Patches = original.Patches
                    .Where(p => p.Target.Y >= localY)
                    .Select(p => CopyPatch(p, p.Target.Offset(0, -localY)))
                    .ToList(),
            };

            d.Segments[i] = first;
            d.Segments.Insert(i + 1, second);
            RebuildSeamsInPlace(d);
        });
        return true;
    }

    /// <summary>
    /// 就地重建拼缝（在 <see cref="Mutate"/> 的委托内调用，避免多记一条历史）。
    /// 与 <see cref="EnsureSeams"/> 同规则，只是不额外压历史。
    /// </summary>
    private static void RebuildSeamsInPlace(ProjectDocument d)
    {
        var wanted = new List<Seam>();
        for (int i = 0; i + 1 < d.Segments.Count; i++)
        {
            string a = d.Segments[i].Id, b = d.Segments[i + 1].Id;
            Seam? existing = d.Seams.FirstOrDefault(s => s.BeforeId == a && s.AfterId == b);
            wanted.Add(existing ?? new Seam { Id = NewId("seam"), BeforeId = a, AfterId = b });
        }
        d.Seams = wanted;
    }

    private static PixelPatch CopyPatch(PixelPatch p, IntRect? overrideTarget = null) => new()
    {
        Id = p.Id,
        Kind = p.Kind,
        Target = overrideTarget ?? p.Target,
        ExtendColumnX = p.ExtendColumnX,
        Enabled = p.Enabled,
        Reason = p.Reason,
        Confidence = p.Confidence,
    };

    /// <summary>短 Id。项目文件里可读比紧凑重要，所以用"前缀 + 8 位十六进制"。</summary>
    public static string NewId(string prefix) =>
        $"{prefix}_{Guid.NewGuid().ToString("N")[..8]}";
}
