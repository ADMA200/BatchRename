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
    public int Invalid { get; init; }
    public int Failed { get; init; }
    public string? RejectReason { get; init; }
    public bool Rejected => RejectReason is not null;
}

/// <summary>
/// 规格书 §8.10。write-ahead：先落盘 Pending → 执行 → 回写 Applied → 撤销后 Undone。
/// 位置：%LOCALAPPDATA%\BatchRename\journal\{yyyyMMdd-HHmmss}-{hex8}.json
/// </summary>
public sealed class UndoJournal
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly TimeSpan MaxUndoAge = TimeSpan.FromDays(7);

    public int Schema { get; set; } = 1;
    public string Id { get; set; } = "";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public JournalState State { get; set; } = JournalState.Pending;
    public string RuleSummary { get; set; } = "";
    public List<JournalEntry> Entries { get; set; } = new();

    public static string RootDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BatchRename", "journal");

    public string FilePath => Path.Combine(RootDir, Id + ".json");

    public static UndoJournal Create(string ruleSummary, IEnumerable<JournalEntry> entries)
    {
        var now = DateTime.UtcNow;
        return new UndoJournal
        {
            Id = $"{now.ToLocalTime():yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..8]}",
            CreatedUtc = now,
            State = JournalState.Pending,
            RuleSummary = ruleSummary,
            Entries = entries.ToList()
        };
    }

    /// <summary>落盘。用临时文件 + Move 保证原子，Flush(true) 强制刷到物理磁盘。</summary>
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

    public static IReadOnlyList<UndoJournal> LoadAll()
    {
        if (!Directory.Exists(RootDir)) return Array.Empty<UndoJournal>();

        var list = new List<UndoJournal>();
        foreach (var f in Directory.EnumerateFiles(RootDir, "*.json"))
        {
            try
            {
                var j = JsonSerializer.Deserialize<UndoJournal>(File.ReadAllText(f), JsonOpts);
                if (j is not null && !string.IsNullOrEmpty(j.Id)) list.Add(j);
            }
            catch
            {
                // 损坏的 journal 跳过，不影响其他批次
            }
        }
        return list.OrderByDescending(j => j.CreatedUtc).ToList();
    }

    /// <summary>保留最近 N 个，超出按时间删。</summary>
    public static void Prune(int keep = 20)
    {
        var all = LoadAll();
        foreach (var old in all.Skip(keep))
        {
            try { File.Delete(old.FilePath); } catch { }
        }
    }

    /// <summary>整批层面的可撤销判定（逐项失效在 UndoAsync 里做）。</summary>
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
        if (DateTime.UtcNow - CreatedUtc > MaxUndoAge)
        {
            reason = "超过撤销期限（7 天）";
            return false;
        }
        return true;
    }

    /// <summary>崩溃恢复：找出上次留在 Pending 的批次。</summary>
    public static UndoJournal? FindPending() =>
        LoadAll().FirstOrDefault(j => j.State == JournalState.Pending);

    /// <summary>
    /// 反向执行。逐项判定失效（§8.10.3），其余正常回滚。
    /// CaseOnly 项反向同样需要两步改名，不能假设 IFileOperation 会替我们处理大小写。
    /// </summary>
    public async Task<UndoOutcome> UndoAsync(CancellationToken ct = default)
    {
        if (!IsUndoable(out var reason))
        {
            return new UndoOutcome { RejectReason = reason };
        }

        var undoable = new List<JournalEntry>();
        var invalid = 0;

        foreach (var e in Entries.Where(x => x.Ok))
        {
            // 精确大小写校验：仅大小写改名的项，File.Exists 会误判为"还在"
            if (!RenameEngine.ExistsExact(e.To)) { invalid++; continue; }

            // from 已被别的文件占用 → 跳过，不覆盖。
            // 这里也必须用精确比较：CaseOnly 项的 from 与 to 只差大小写，
            // File.Exists(e.From) 会因为忽略大小写而永远为 true，把整批判成"已失效"。
            if (RenameEngine.ExistsExact(e.From)) { invalid++; continue; }

            undoable.Add(e);
        }

        if (undoable.Count == 0)
        {
            return new UndoOutcome { Undone = 0, Invalid = invalid, RejectReason = invalid > 0 ? "所有项均已失效" : null };
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
        var result = await runner.RunAsync(items, ct).ConfigureAwait(false);

        State = JournalState.Undone;
        Save();

        return new UndoOutcome
        {
            Undone = result.Succeeded,
            Failed = result.Failed,
            Invalid = invalid
        };
    }

    /// <summary>把规则压成一句话，便于日后在列表里识别是哪次操作。</summary>
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
