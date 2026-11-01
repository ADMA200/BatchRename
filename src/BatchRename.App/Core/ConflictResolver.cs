using System.IO;
using BatchRename.App.Models;

namespace BatchRename.App.Core;

/// <summary>
/// 改名冲突判定（规格书 §8.8）：批内互撞 → 批内循环 → 磁盘已存在 →（可选）冲突自动编号。
///
/// v1.6 从 <see cref="RenameEngine"/> 抽出。理由：
/// 这部分只关心"名字之间 / 名字与磁盘"的冲突，与"规则如何算出新名"（BuildNewName）
/// 和"名字是否合法"（Validate）是两件事，混在一个 465 行的类里时，改规则容易误伤冲突逻辑。
///
/// 契约：只读入参列表、就地写回 State/Message/NewName，不碰规则、不写磁盘。
/// 调用顺序有意义，Run() 里必须按 互撞 → 循环 → 磁盘 → 编号 执行。
/// </summary>
internal static class ConflictResolver
{
    private static void Set(RenameItem item, ItemState state, string? message)
    {
        item.State = state;
        item.Message = message;
    }

    /// <summary>规格书 §8.8 的批内互撞部分。</summary>
    internal static void MarkBatchConflicts(IReadOnlyList<RenameItem> items)
    {
        // 键必须大小写不敏感：NTFS 上 Same.txt 与 same.txt 是同一个名字，
        // 而磁盘检查那一侧用的是 OrdinalIgnoreCase，两边不能不一致（REVIEW P2-15）。
        var map = new Dictionary<string, List<RenameItem>>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            if (item.State is ItemState.Invalid or ItemState.Unchanged or ItemState.Renamed) continue;

            var key = Path.Combine(item.Directory, item.NewName);
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

    /// <summary>
    /// 批内**循环**改名检测（REVIEW P0-1，实测会丢数据）。
    ///
    /// `a.txt → b.txt` 且 `b.txt → a.txt`：`MarkDiskConflicts` 里"本批会让出的源名"
    /// 判定会把两项都当空位放行，而执行阶段 `FOF_NOCONFIRMATION` 会**静默覆盖**——
    /// 实测原 b.txt 的内容永久消失，结果还报"1 成功 1 失败"。
    ///
    /// 做法：沿"目标名 = 另一项的源名"这条边一直走，走回本轮起点即成环，整环标红阻止。
    /// 链（`a→b`、`b→c`）不是环，不会误伤；它们由执行器按依赖顺序排队保证安全。
    /// 复杂度 O(n)：每个节点最多被走一次，遇到已访问节点立即停。
    /// </summary>
    internal static void MarkBatchCycles(IReadOnlyList<RenameItem> items)
    {
        var bySource = new Dictionary<string, RenameItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            if (item.State is not (ItemState.Ok or ItemState.Warning)) continue;
            bySource[item.FullPath] = item;
        }
        if (bySource.Count < 2) return;

        var visitedRound = new Dictionary<RenameItem, int>();
        var round = 0;

        foreach (var start in bySource.Values)
        {
            round++;
            var chain = new List<RenameItem>();
            var cur = start;

            while (true)
            {
                if (visitedRound.TryGetValue(cur, out var seenRound))
                {
                    if (seenRound == round)
                    {
                        var from = chain.IndexOf(cur);
                        if (from >= 0)
                        {
                            foreach (var cycleItem in chain.Skip(from))
                            {
                                Set(cycleItem, ItemState.ConflictInBatch, "批内循环改名");
                            }
                        }
                    }
                    break;
                }

                visitedRound[cur] = round;
                chain.Add(cur);

                if (!bySource.TryGetValue(cur.NewFullPath, out var next)) break;
                if (ReferenceEquals(next, cur)) break;   // 目标就是自己（仅大小写/无变化）
                cur = next;
            }
        }
    }

    /// <summary>
    /// 规格书 §8.8 的「磁盘已存在」检测。按目录分组、每目录枚举一次，
    /// 比逐项 File.Exists 快得多，也避免了对大小写不敏感卷的误判。
    /// </summary>
    internal static void MarkDiskConflicts(IReadOnlyList<RenameItem> items)
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
    internal static void ApplyAutoNumbering(IReadOnlyList<RenameItem> items)
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
            // 必须从**目标名**切主名/扩展名，不能用 item.Ext（那是改名前的老扩展名）：
            // 旧实现在「改扩展名 + 自动编号」时会产出 "a.md (1).txt"（编号落在扩展名之后，
            // 且扩展名被打回原样）。见 docs/REVIEW-2026-11-01.md P1-5。
            var (stem, ext) = NameSplitter.Split(item.NewName, item.IsDirectory);

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
