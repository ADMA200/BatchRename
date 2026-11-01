using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using BatchRename.App.Models;

namespace BatchRename.App.Core;

public sealed class JournalEntry
{
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public bool CaseOnly { get; set; }
    public bool Ok { get; set; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum JournalState
{
    Pending,
    Applied,
    Undone
}

public sealed class UndoOutcome
{
    public int Undone { get; init; }
    /// <summary>目标已消失、源名被占用、卷不可用等，本次跳过。</summary>
    public int Invalid { get; init; }
    /// <summary>已经是原名了（上一次部分撤销留下的），不算错误。</summary>
    public int AlreadyUndone { get; init; }
    public int Failed { get; init; }
    public string? RejectReason { get; init; }
    public bool Rejected => RejectReason is not null;
    /// <summary>全部回滚到位：这条记录可以丢弃了。</summary>
    public bool Completed => !Rejected && Failed == 0;
}

/// <summary>
/// 规格书 §8.10（v1.5 起简化为"单次操作事务日志"）。
///
/// 只有一个文件：%LOCALAPPDATA%\BatchRename\journal\last-batch.json，覆盖写。
/// 不再有 7 天有效期、不再保留 20 个历史批次——那不是需求，只是一台没有入口的状态机
/// （见 docs/REVIEW-2026-11-01.md §7.6）。
///
/// 留下的两个不可替代职能：
///   ① write-ahead 崩溃标记：执行前先落盘 Pending，执行完再回写 Applied。
///      进程在中间死掉，下次启动才能知道"上次只做了一半"。
///   ② 软件内的「撤销」按钮：本次会话与下一次启动都能撤最近一批。
///      （资源管理器里的 Ctrl+Z 由 IFileOperation 的 FOFX_ADDUNDORECORD 负责，与这份记录互不干扰）
///
/// 状态机：Pending --执行成功--> Applied --撤销成功--> Undone
///                                  ↑______部分失败则留在原状态，允许重试
/// </summary>
public sealed class UndoJournal
{
    /// <summary>固定文件名：一次只记一批。</summary>
    public const string FileName = "last-batch.json";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private static string? _rootOverride;

    public static string DefaultRootDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BatchRename", "journal");

    public static string RootDir => _rootOverride ?? DefaultRootDir;

    /// <summary>
    /// 测试专用：把 journal 目录指到临时路径。
    /// 没有这个钩子的话，断言测试会覆盖用户真实的 last-batch.json。
    /// </summary>
    public static void UseRootDirForTesting(string dir) => _rootOverride = dir;

    public int Schema { get; set; } = 2;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public JournalState State { get; set; } = JournalState.Pending;

    /// <summary>规则摘要，仅用于事后人工排查（没有列表 UI 展示它）。</summary>
    public string RuleSummary { get; set; } = "";

    /// <summary>
    /// 执行被异常中断时写入的原因（正常完成为 null）。
    /// 与 Pending 状态配合：下次启动既能提示"上次没做完"，也能说清为什么。
    /// </summary>
    public string? Error { get; set; }

    public List<JournalEntry> Entries { get; set; } = new();

    public string FilePath => Path.Combine(RootDir, FileName);

    public static UndoJournal Create(string ruleSummary, IEnumerable<JournalEntry> entries)
    {
        return new UndoJournal
        {
            CreatedUtc = DateTime.UtcNow,
            State = JournalState.Pending,
            RuleSummary = ruleSummary,
            Entries = entries.ToList()
        };
    }

    /// <summary>落盘。临时文件 + Move 保证原子，Flush(true) 强制刷到物理磁盘。</summary>
    public void Save()
    {
        Directory.CreateDirectory(RootDir);
        var tmp = FilePath + ".tmp";

        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(fs, this, JsonOpts);
            fs.Flush(true);
        }

        File.Move(tmp, FilePath, overwrite: true);
    }

    /// <summary>最近一次读取失败的异常，便于排查（不参与序列化）。</summary>
    public static Exception? LastLoadError { get; private set; }

    /// <summary>读最近一批。文件不存在或损坏都返回 null（损坏就当没记录，不阻塞启动）。</summary>
    public static UndoJournal? LoadLast()
    {
        try
        {
            // 注意：File.Exists 对目录返回 false，这里必须用 Directory.Exists
            if (!Directory.Exists(RootDir)) return null;
            var path = Path.Combine(RootDir, FileName);
            if (!File.Exists(path)) return null;

            var j = JsonSerializer.Deserialize<UndoJournal>(File.ReadAllText(path), JsonOpts);
            return string.IsNullOrEmpty(j?.RuleSummary) && j?.Entries.Count == 0 ? null : j;
        }
        catch (Exception ex)
        {
            LastLoadError = ex;
            return null;
        }
    }

    /// <summary>崩溃恢复：上次执行没有回写 Applied 的批次。</summary>
    public static UndoJournal? FindPending() =>
        LoadLast() is { State: JournalState.Pending } j ? j : null;

    /// <summary>丢弃这条记录（用户选择「忽略」上次未完成的操作）。</summary>
    public void Discard()
    {
        try { if (File.Exists(FilePath)) File.Delete(FilePath); } catch { }
    }

    /// <summary>
    /// 执行中途抛异常时，按**磁盘现状**回写逐项结果。
    ///
    /// 没有这一步，journal 里每项都还是创建时的 Ok=true，回滚会去"撤销"那些
    /// 根本没发生过的改名（把好好的文件当成已改名项处理）。判定方式与执行器一致：
    /// 目标名在、源名不在 → 这一步确实做过了。
    /// </summary>
    public void ReconcileFromDisk()
    {
        foreach (var e in Entries)
        {
            try
            {
                e.Ok = RenameEngine.ExistsExact(e.To) && !RenameEngine.ExistsExact(e.From);
            }
            catch
            {
                e.Ok = false;
            }
        }
    }

    /// <summary>整批层面的可撤销判定（逐项失效在 ReverseAsync 里做）。</summary>
    public bool IsUndoable(out string? reason)
    {
        reason = null;

        if (State == JournalState.Undone)
        {
            reason = "该批次已撤销过";
            return false;
        }
        if (State == JournalState.Pending)
        {
            reason = "该批次未正常完成，请用「回滚未完成」";
            return false;
        }
        return true;
    }

    /// <summary>撤销最近一批（要求状态为 Applied）。</summary>
    public async Task<UndoOutcome> UndoAsync(CancellationToken ct = default)
    {
        if (!IsUndoable(out var reason)) return new UndoOutcome { RejectReason = reason };
        return await ReverseAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 回滚"上次未正常完成"的批次（状态 Pending）。
    /// 旧版没有这个入口：崩溃恢复对话框调的是 UndoAsync，而 UndoAsync 明确拒绝 Pending，
    /// 于是那个按钮 100% 失败（REVIEW P0-2）。
    /// </summary>
    public async Task<UndoOutcome> RollbackPendingAsync(CancellationToken ct = default)
    {
        if (State != JournalState.Pending)
        {
            return new UndoOutcome { RejectReason = "没有未完成的批次" };
        }
        return await ReverseAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 反向执行。逐项判定失效（§8.10.3），其余正常回滚。
    /// 反向执行不加 FOFX_ADDUNDORECORD：否则资源管理器里会多一条"撤销的撤销"记录。
    /// </summary>
    private async Task<UndoOutcome> ReverseAsync(CancellationToken ct)
    {
        var undoable = new List<JournalEntry>();
        var invalid = 0;
        var alreadyUndone = 0;

        foreach (var e in Entries.Where(x => x.Ok))
        {
            // 两处都必须用精确大小写比较：CaseOnly 项的 from/to 只差大小写，
            // File.Exists 会同时命中两个，把整批判成"已失效"。
            var toExists = RenameEngine.ExistsExact(e.To);
            var fromExists = RenameEngine.ExistsExact(e.From);

            if (!toExists)
            {
                // 目标没了：要么上次已经回滚过（源名在），要么被人挪走了（源名也不在）
                if (fromExists) alreadyUndone++;
                else invalid++;
                continue;
            }

            if (fromExists) { invalid++; continue; }   // 源名被别的文件占了，不覆盖

            undoable.Add(e);
        }

        if (undoable.Count == 0)
        {
            // 没有可做的了：全部失效或全部已回滚，直接把记录定格，避免下次启动反复提示
            State = JournalState.Undone;
            Save();
            return new UndoOutcome
            {
                Undone = 0,
                Invalid = invalid,
                AlreadyUndone = alreadyUndone,
                RejectReason = invalid > 0 ? "所有项均已失效" : null
            };
        }

        var items = new List<RenameItem>();
        foreach (var e in undoable)
        {
            try
            {
                var isDir = Directory.Exists(e.To);
                var attrs = isDir
                    ? new DirectoryInfo(e.To).Attributes
                    : File.GetAttributes(e.To);

                var item = RenameEngine.CreateItem(e.To, isDir, attrs);
                item.NewName = Path.GetFileName(e.From);
                item.State = ItemState.Ok;
                item.CaseOnly = e.CaseOnly;
                items.Add(item);
            }
            catch
            {
                invalid++;
            }
        }

        var runner = new FileOperationRunner();
        // AddUndoRecord: false —— 撤销动作本身不登记到资源管理器撤销栈
        var result = await runner
            .RunAsync(items, new RenameRunOptions(IntPtr.Zero, AddUndoRecord: false), ct)
            .ConfigureAwait(false);

        // 只有全部成功才定格为 Undone；部分失败保留原状态，让用户能再点一次（已回滚的项会被跳过）
        if (result.Failed == 0) State = JournalState.Undone;
        Save();

        return new UndoOutcome
        {
            Undone = result.Succeeded,
            Failed = result.Failed,
            Invalid = invalid,
            AlreadyUndone = alreadyUndone
        };
    }

    /// <summary>把规则压成一句话，写进 journal 便于事后排查（不再用于列表展示）。</summary>
    public static string Summarize(RenameRule rule)
    {
        var scope = rule.Scope switch
        {
            RenameScope.StemOnly => "Stem",
            RenameScope.ExtOnly => "Ext",
            _ => "Both"
        };
        var caseMode = rule.CaseMode switch
        {
            CaseMode.Upper => "Upper",
            CaseMode.Lower => "Lower",
            CaseMode.Title => "Title",
            _ => "None"
        };

        var find = rule.UseRegex ? $"re:{rule.Find}" : rule.Find;
        return $"find='{find}' → '{rule.Replace}' | scope={scope} | case={caseMode} | sensitive={rule.CaseSensitive}";
    }
}
