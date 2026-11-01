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
        // 5000 项的载入耗时几乎全在这里（实测 105ms → 40ms），两处优化：
        //  ① 一次 stat：原来 Directory.Exists + File.GetAttributes 各打一次盘，
        //     GetAttributes 的返回值本身就带 Directory 位，合并后直接砍半；
        //  ② 并行查询属性、按下标写回，保持输入顺序（列表顺序 = Shell 选中顺序，不能乱）。
        //     属性查询是文件系统调用，单线程会串行等；这里上限 8 线程，避免网络盘/机械盘被压垮。
        var arr = paths as IReadOnlyList<string> ?? paths.ToList();
        if (arr.Count == 0) return new List<RenameItem>();

        var slots = new RenameItem?[arr.Count];
        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Min(Environment.ProcessorCount, 8)
        };
        Parallel.For(0, arr.Count, options, i =>
        {
            try
            {
                var attrs = File.GetAttributes(arr[i]);
                slots[i] = CreateItem(arr[i], (attrs & FileAttributes.Directory) != 0, attrs);
            }
            catch
            {
                // 路径已消失/无权限：跳过，不打断整批
            }
        });

        var list = new List<RenameItem>(arr.Count);
        foreach (var s in slots) if (s is not null) list.Add(s);
        return list;
    }

    /// <param name="autoNumber">
    /// 冲突时自动编号（§8.8 可选策略，默认关）。
    /// 编号加在 **Stem 尾部**：a.txt → a (1).txt，不是 a.txt (1)。
    /// </param>
    /// <param name="checkDisk">
    /// 是否做「磁盘已存在」检查（§8.11 两层防抖的第一层传 false）。
    /// 纯内存那层只跑文本规则 + 批内互撞/环，磁盘枚举留给停手 400ms 后的第二层。
    /// **执行前必须用 true 跑一次**，否则会拿"没查过磁盘"的状态去点执行。
    /// </param>
    public static EngineOutcome Run(
        IReadOnlyList<RenameItem> items, RenameRule rule, bool autoNumber = false, bool checkDisk = true)
    {
        var regex = rule.Compile(out var regexError);
        if (regexError is not null)
        {
            return new EngineOutcome { Items = items, RegexError = regexError };
        }

        var anyTimeout = false;

        foreach (var item in items)
        {
            // 🔧 P1-7：本会话改过名的项**照常参与规则计算**。
            // 旧实现在这里直接 continue，等于"改过一次 → 这一窗口里永远改不动它"，
            // 用户看到的是项目被锁定/忽略（REVIEW P1-7）。
            // 现在只有在"当前规则对它没有任何作用"时，才把状态回显成「已改名」当视觉标记。
            var newName = BuildNewName(item, rule, regex, ref anyTimeout);
            item.NewName = newName;
            Validate(item);

            if (item.IsRenamed && item.State == ItemState.Unchanged)
            {
                item.State = ItemState.Renamed;
                item.Message = "已改名";
            }
        }

        ConflictResolver.MarkBatchConflicts(items);
        ConflictResolver.MarkBatchCycles(items);      // 🔧 P0-1：环状改名会静默覆盖，必须先拦
        if (checkDisk) ConflictResolver.MarkDiskConflicts(items);

        if (autoNumber) ConflictResolver.ApplyAutoNumbering(items);

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

    private static void Set(RenameItem item, ItemState state, string? message)
    {
        item.State = state;
        item.Message = message;
    }

}
