using System.IO;
using System.Text.RegularExpressions;
using BatchRename.App.Models;

namespace BatchRename.App.Core;

public sealed class EngineOutcome
{
    public IReadOnlyList<RenameItem> Items { get; init; } = Array.Empty<RenameItem>();

    /// <summary>非空表示正则非法，UI 应显示顶部横幅并禁用执行。</summary>
    public string? RegexError { get; init; }

    public bool HasBlockingError { get; init; }
}

/// <summary>
/// 规格书 §8.4 执行顺序：匹配替换 → 大小写转换 → 拼回 → 逐项校验 → 冲突检测。
/// M2 只做前四步 + 批内互撞；磁盘冲突留 M3。
/// </summary>
public static class RenameEngine
{
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    private static readonly char[] InvalidChars = Path.GetInvalidFileNameChars();

    /// <summary>
    /// 精确（区分大小写）判断路径是否存在。
    /// File.Exists 在 NTFS 上忽略大小写，无法验证「仅大小写改名」是否真的生效——
    /// UPPER.TXT 明明没改，File.Exists("upper.txt") 也会返回 true。
    /// </summary>
    public static bool ExistsExact(string path)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return false;

            var name = Path.GetFileName(path);
            return Directory.EnumerateFileSystemEntries(dir)
                .Any(p => string.Equals(Path.GetFileName(p), name, StringComparison.Ordinal));
        }
        catch
        {
            return false;
        }
    }

    public static RenameItem CreateItem(string fullPath, bool isDirectory, FileAttributes attributes)    {
        var trimmed = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(trimmed);
        var dir = Path.GetDirectoryName(trimmed) ?? "";
        var (stem, ext) = NameSplitter.Split(name, isDirectory);

        return new RenameItem
        {
            FullPath = fullPath,
            Directory = dir,
            OriginalName = name,
            Stem = stem,
            Ext = ext,
            IsDirectory = isDirectory,
            Attributes = attributes,
            NewName = name
        };
    }

    /// <summary>从磁盘路径批量构建（M2 预览用；5000 项也要够快）。</summary>
    public static List<RenameItem> CreateItems(IEnumerable<string> paths)
    {
        var list = new List<RenameItem>();
        foreach (var p in paths)
        {
            try
            {
                var isDir = Directory.Exists(p);
                var attrs = File.GetAttributes(p);
                list.Add(CreateItem(p, isDir, attrs));
            }
            catch
            {
                // 路径已消失/无权限：跳过，不打断整批
            }
        }
        return list;
    }

    /// <param name="autoNumber">
    /// 冲突时自动编号（§8.8 可选策略，默认关）。
    /// 编号加在 **Stem 尾部**：a.txt → a (1).txt，不是 a.txt (1)。
    /// </param>
    public static EngineOutcome Run(IReadOnlyList<RenameItem> items, RenameRule rule, bool autoNumber = false)
    {
        var regex = rule.Compile(out var regexError);
        if (regexError is not null)
        {
            return new EngineOutcome { Items = items, RegexError = regexError };
        }

        var anyTimeout = false;

        foreach (var item in items)
        {
            // 本批次已改过名的项不再参与规则计算，保持"已改名"状态
            if (item.IsRenamed)
            {
                item.NewName = item.OriginalName;
                item.State = ItemState.Renamed;
                item.Message = "已改名";
                continue;
            }

            var newName = BuildNewName(item, rule, regex, ref anyTimeout);
            item.NewName = newName;
            Validate(item);
        }

        MarkBatchConflicts(items);
        MarkDiskConflicts(items);

        if (autoNumber) ApplyAutoNumbering(items);

        var blocking = anyTimeout || items.Any(i =>
            i.IsEnabled && (i.State == ItemState.Invalid ||
                            i.State == ItemState.ConflictInBatch ||
                            i.State == ItemState.ConflictOnDisk));

        return new EngineOutcome { Items = items, HasBlockingError = blocking };
    }

    private static string BuildNewName(RenameItem item, RenameRule rule, Regex? regex, ref bool anyTimeout)
    {
        var extBody = item.Ext.Length > 1 ? item.Ext[1..] : "";
        var newStem = item.Stem;
        var newExtBody = extBody;

        switch (rule.Scope)
        {
            case RenameScope.StemOnly:
                (newStem, var e1) = rule.Apply(item.Stem, regex);
                if (e1 is not null) { item.Message = e1; anyTimeout = true; }
                break;

            case RenameScope.ExtOnly:
                (newExtBody, var e2) = rule.Apply(extBody, regex);
                if (e2 is not null) { item.Message = e2; anyTimeout = true; }
                break;

            case RenameScope.Both:
                (newStem, var e3) = rule.Apply(item.Stem, regex);
                (newExtBody, var e4) = rule.Apply(extBody, regex);
                var e = e3 ?? e4;
                if (e is not null) { item.Message = e; anyTimeout = true; }
                break;
        }

        if (item.IsDirectory) return newStem;
        if (item.Ext.Length == 0) return newStem;

        // 重建：StemOnly 时 newExtBody 未变，等价于保留原 Ext（含点）
        return newStem + "." + newExtBody;
    }

    /// <summary>规格书 §8.7，按顺序执行，首个失败即返回。</summary>
    private static void Validate(RenameItem item)
    {
        item.CaseOnly = false;
        item.Message = null;
        var name = item.NewName;

        if (string.IsNullOrEmpty(name))
        {
            Set(item, ItemState.Invalid, "新名为空");
            return;
        }

        foreach (var c in name)
        {
            if (Array.IndexOf(InvalidChars, c) >= 0 || c < 32)
            {
                Set(item, ItemState.Invalid, "含非法字符");
                return;
            }
        }

        var trimmed = name.TrimEnd(' ', '.');
        var dot = trimmed.IndexOf('.');
        var baseName = dot >= 0 ? trimmed[..dot] : trimmed;
        if (ReservedNames.Contains(baseName))
        {
            Set(item, ItemState.Invalid, "保留设备名");
            return;
        }

        if (name.EndsWith(' ') || name.EndsWith('.'))
        {
            Set(item, ItemState.Invalid, "不能以空格或点结尾");
            return;
        }

        if (name.Length > 255)
        {
            Set(item, ItemState.Invalid, "单段名超过 255 字符");
            return;
        }

        var full = item.NewFullPath;
        if (full.Length > 32767)
        {
            Set(item, ItemState.Invalid, "路径过长");
            return;
        }

        if (full.Length > 260)
        {
            Set(item, ItemState.Warning, "超出传统路径长度，依赖长路径支持");
            return;
        }

        if (name == item.OriginalName)
        {
            Set(item, ItemState.Unchanged, "未改变");
            return;
        }

        if (string.Equals(name, item.OriginalName, StringComparison.OrdinalIgnoreCase))
        {
            item.CaseOnly = true;
            Set(item, ItemState.Ok, "仅大小写变化");
            return;
        }

        if (item.Attributes.HasFlag(FileAttributes.ReadOnly) ||
            item.Attributes.HasFlag(FileAttributes.System))
        {
            Set(item, ItemState.Warning, "目标为只读/系统文件");
            return;
        }

        Set(item, ItemState.Ok, null);
    }

    /// <summary>规格书 §8.8 的批内互撞部分（磁盘检测留 M3）。</summary>
    private static void MarkBatchConflicts(IReadOnlyList<RenameItem> items)
    {
        var map = new Dictionary<(string Dir, string Name), List<RenameItem>>();
        foreach (var item in items)
        {
            if (item.State is ItemState.Invalid or ItemState.Unchanged or ItemState.Renamed) continue;

            var key = (item.Directory, item.NewName);
            if (!map.TryGetValue(key, out var bucket))
            {
                bucket = new List<RenameItem>(1);
                map[key] = bucket;
            }
            bucket.Add(item);
        }

        foreach (var bucket in map.Values)
        {
            if (bucket.Count <= 1) continue;
            foreach (var item in bucket)
            {
                Set(item, ItemState.ConflictInBatch, "批内重名");
            }
        }
    }

    private static void Set(RenameItem item, ItemState state, string? message)
    {
        item.State = state;
        item.Message = message;
    }

    /// <summary>
    /// 规格书 §8.8 的「磁盘已存在」检测。按目录分组、每目录枚举一次，
    /// 比逐项 File.Exists 快得多，也避免了对大小写不敏感卷的误判。
    /// </summary>
    private static void MarkDiskConflicts(IReadOnlyList<RenameItem> items)
    {
        foreach (var group in items.GroupBy(i => i.Directory, StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrEmpty(group.Key)) continue;

            HashSet<string> existing;
            try
            {
                existing = new HashSet<string>(
                    Directory.EnumerateFileSystemEntries(group.Key).Select(p => Path.GetFileName(p)),
                    StringComparer.OrdinalIgnoreCase);
            }
            catch
            {
                continue;   // 目录不可读：跳过，不误判为冲突
            }

            // 本批里会被改走的名字，执行后这些名字就空出来了，不算冲突
            var vacating = new HashSet<string>(
                group.Where(i => i.State is ItemState.Ok or ItemState.Warning).Select(i => i.OriginalName),
                StringComparer.OrdinalIgnoreCase);

            foreach (var item in group)
            {
                if (item.State is not (ItemState.Ok or ItemState.Warning)) continue;
                if (item.CaseOnly) continue;                        // 目标就是它自己
                if (!existing.Contains(item.NewName)) continue;      // 目标不存在
                if (vacating.Contains(item.NewName)) continue;       // 会被本批让出来

                Set(item, ItemState.ConflictOnDisk, "目标已存在");
            }
        }
    }

    /// <summary>
    /// 规格书 §8.8 的可选策略：冲突时自动编号。
    /// `a.txt` → `a (1).txt`（加在 Stem 尾部，不是 `a.txt (1)`），最多试 999 次。
    /// </summary>
    private static void ApplyAutoNumbering(IReadOnlyList<RenameItem> items)
    {
        var conflicts = items
            .Where(i => i.State is ItemState.ConflictInBatch or ItemState.ConflictOnDisk)
            .ToList();
        if (conflicts.Count == 0) return;

        var occupied = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        HashSet<string> SetFor(string dir)
        {
            if (occupied.TryGetValue(dir, out var cached)) return cached;

            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 本批会让出的源名，不算占用
            var vacating = new HashSet<string>(
                items.Where(i => string.Equals(i.Directory, dir, StringComparison.OrdinalIgnoreCase)
                                 && i.State is ItemState.Ok or ItemState.Warning)
                     .Select(i => i.OriginalName),
                StringComparer.OrdinalIgnoreCase);

            try
            {
                foreach (var p in Directory.EnumerateFileSystemEntries(dir))
                {
                    var name = Path.GetFileName(p);
                    if (!vacating.Contains(name)) set.Add(name);
                }
            }
            catch
            {
                // 目录不可读：只靠批内占用集合判断
            }

            occupied[dir] = set;
            return set;
        }

        // 先把已确定不冲突的新名占住，避免编号撞上它们
        foreach (var item in items)
        {
            if (item.State is ItemState.Ok or ItemState.Warning or ItemState.Renamed)
                SetFor(item.Directory).Add(item.NewName);
        }

        foreach (var item in conflicts)
        {
            var set = SetFor(item.Directory);
            var ext = item.IsDirectory ? "" : item.Ext;

            var stem = ext.Length > 0 && item.NewName.EndsWith(ext, StringComparison.OrdinalIgnoreCase)
                ? item.NewName[..^ext.Length]
                : item.NewName;

            var resolved = false;
            // n=0 先试原名：批内互撞时第一个项应该保留 x.txt，而不是被编成 x (1).txt
            for (var n = 0; n <= 999; n++)
            {
                var candidate = n == 0 ? item.NewName : $"{stem} ({n}){ext}";
                if (set.Contains(candidate)) continue;

                item.NewName = candidate;
                item.State = ItemState.Ok;
                item.Message = n == 0 ? null : $"自动编号 ({n})";
                item.CaseOnly = false;
                set.Add(candidate);
                resolved = true;
                break;
            }

            if (!resolved) item.Message = "自动编号失败（已尝试 999 次）";
        }
    }
}
