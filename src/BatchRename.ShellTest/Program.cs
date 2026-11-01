using System.Diagnostics;
using System.Text;
using BatchRename.App.Core;
using BatchRename.App.Models;

// 控制台默认 GBK，中文断言名会乱码
try { Console.OutputEncoding = Encoding.UTF8; } catch { }

// journal 现在只有一个固定文件（last-batch.json），测试必须把它指到临时目录，
// 否则会覆盖用户真实的 last-batch.json。
var testJournalRoot = Path.Combine(Path.GetTempPath(), "BatchRenameTestJournal", Guid.NewGuid().ToString("N")[..8]);
UndoJournal.UseRootDirForTesting(testJournalRoot);

// ============================================================
// M2 引擎断言测试。不依赖 UI，可独立跑。
// 用法: BatchRename.ShellTest.exe
// ============================================================

var failures = 0;
var checks = 0;

void Eq<T>(string name, T expected, T actual)
{
    checks++;
    var ok = EqualityComparer<T>.Default.Equals(expected, actual);
    if (!ok)
    {
        failures++;
        Console.WriteLine($"  [FAIL] {name}\n         期望: {expected}\n         实际: {actual}");
    }
    else
    {
        Console.WriteLine($"  [ok]   {name}");
    }
}

void True(string name, bool cond, string? detail = null)
{
    checks++;
    if (!cond)
    {
        failures++;
        Console.WriteLine($"  [FAIL] {name}" + (detail is null ? "" : $"  ({detail})"));
    }
    else
    {
        Console.WriteLine($"  [ok]   {name}");
    }
}

RenameItem Item(string path, bool isDir = false) =>
    RenameEngine.CreateItem(path, isDir, FileAttributes.Normal);

void RunOne(RenameItem item, RenameRule rule) =>
    RenameEngine.Run(new[] { item }, rule);

Console.WriteLine("== 1. NameSplitter (§8.2) ==");
Eq("report.txt", ("report", ".txt"), NameSplitter.Split("report.txt", false));
Eq("archive.tar.gz 只切最后一层", ("archive.tar", ".gz"), NameSplitter.Split("archive.tar.gz", false));
Eq(".gitignore 是 dotfile", (".gitignore", ""), NameSplitter.Split(".gitignore", false));
Eq("a. 结尾点", ("a", "."), NameSplitter.Split("a.", false));
Eq("noext 无扩展名", ("noext", ""), NameSplitter.Split("noext", false));
Eq("目录永不切", ("文件夹.v2", ""), NameSplitter.Split("文件夹.v2", true));

Console.WriteLine("\n== 2. 作用域语义 (§8.3) ==");
{
    var it = Item(@"C:\t\a.txt");
    RunOne(it, new RenameRule { Find = "txt", Replace = "md", Scope = RenameScope.ExtOnly });
    Eq("ExtOnly 查找 txt 命中扩展名(不带点)", "a.md", it.NewName);

    var it2 = Item(@"C:\t\a.txt");
    RunOne(it2, new RenameRule { Find = "a.t", Replace = "x", Scope = RenameScope.StemOnly });
    Eq("StemOnly 不跨点", "a.txt", it2.NewName);

    var it3 = Item(@"C:\t\a.txt");
    RunOne(it3, new RenameRule { Find = "a.t", Replace = "x", Scope = RenameScope.Both });
    Eq("Both 也绝不拼串", "a.txt", it3.NewName);
}

Console.WriteLine("\n== 3. 大小写 (§8.5) ==");
{
    var it = Item(@"C:\t\fileIndex.txt");
    RunOne(it, new RenameRule { CaseMode = CaseMode.Upper, Scope = RenameScope.StemOnly });
    Eq("全大写 Invariant（土耳其语 i 不炸）", "FILEINDEX.txt", it.NewName);

    var it2 = Item(@"C:\t\My File-Name.txt");
    RunOne(it2, new RenameRule { CaseMode = CaseMode.Title, Scope = RenameScope.StemOnly });
    Eq("每词首字母大写", "My File-Name.txt", it2.NewName);

    var it3 = Item(@"C:\t\don't stop.txt");
    RunOne(it3, new RenameRule { CaseMode = CaseMode.Title, Scope = RenameScope.StemOnly });
    Eq("don't 不变 Don'T", "Don't Stop.txt", it3.NewName);
}

Console.WriteLine("\n== 4. 文本替换 (§8.6) ==");
{
    var it = Item(@"C:\t\abcABC.txt");
    RunOne(it, new RenameRule { Find = "abc", Replace = "x", Scope = RenameScope.StemOnly, CaseSensitive = false });
    Eq("不区分大小写全命中", "xx.txt", it.NewName);

    var it2 = Item(@"C:\t\abcABC.txt");
    RunOne(it2, new RenameRule { Find = "abc", Replace = "x", Scope = RenameScope.StemOnly, CaseSensitive = true });
    Eq("区分大小写只命中原文", "xABC.txt", it2.NewName);

    var it3 = Item(@"C:\t\aaa.txt");
    RunOne(it3, new RenameRule { Find = "a", Replace = "aa", Scope = RenameScope.StemOnly });
    Eq("替换词含查找词不死循环", "aaaaaa.txt", it3.NewName);
}

Console.WriteLine("\n== 5. 正则 (§8.6) ==");
{
    var it = Item(@"C:\t\file12.txt");
    RunOne(it, new RenameRule { UseRegex = true, Find = @"(\d+)", Replace = "#$1", Scope = RenameScope.StemOnly });
    Eq("捕获组 $1", "file#12.txt", it.NewName);

    var bad = Item(@"C:\t\a.txt");
    var outcome = RenameEngine.Run(new[] { bad }, new RenameRule { UseRegex = true, Find = "(" });
    True("非法正则返回 RegexError 而非抛异常", outcome.RegexError is not null, outcome.RegexError);
}

Console.WriteLine("\n== 6. 逐项校验 (§8.7) ==");
{
    var it = Item(@"C:\t\abc.txt");
    RunOne(it, new RenameRule { Find = "a", Replace = "?", Scope = RenameScope.StemOnly });
    Eq("非法字符 ?", ItemState.Invalid, it.State);
    Eq("非法字符文案", "含非法字符", it.Message);

    var it2 = Item(@"C:\t\a.txt");
    RunOne(it2, new RenameRule { Find = "a", Replace = "CON", Scope = RenameScope.StemOnly });
    Eq("保留设备名 CON", ItemState.Invalid, it2.State);

    var it3 = Item(@"C:\t\a.txt");
    RunOne(it3, new RenameRule { Find = "txt", Replace = "", Scope = RenameScope.ExtOnly });
    Eq("扩展名被清空 → 结尾点", ItemState.Invalid, it3.State);

    var it4 = Item(@"C:\t\a.txt");
    RunOne(it4, new RenameRule { Find = "zzz", Replace = "y", Scope = RenameScope.StemOnly });
    Eq("无变化 → Unchanged", ItemState.Unchanged, it4.State);

    var it5 = Item(@"C:\t\a.txt");
    RunOne(it5, new RenameRule { CaseMode = CaseMode.Upper, Scope = RenameScope.StemOnly });
    Eq("仅大小写 → Ok", ItemState.Ok, it5.State);
    True("仅大小写 CaseOnly=true", it5.CaseOnly);
    Eq("仅大小写文案", "仅大小写变化", it5.Message);
}

Console.WriteLine("\n== 7. 批内互撞 (§8.8) ==");
{
    var a = Item(@"C:\t\a1.txt");
    var b = Item(@"C:\t\a2.txt");
    var outcome = RenameEngine.Run(new[] { a, b },
        new RenameRule { UseRegex = true, Find = @"^a\d", Replace = "x", Scope = RenameScope.StemOnly });
    Eq("第一个撞", ItemState.ConflictInBatch, a.State);
    Eq("第二个撞", ItemState.ConflictInBatch, b.State);
    True("阻塞执行", outcome.HasBlockingError);
}

Console.WriteLine("\n== 8. 200 混合样本 ==");
{
    string[] exts = { ".txt", ".tar.gz", ".md", "", ".jpeg" };
    var samples = new List<RenameItem>();
    for (var i = 0; i < 200; i++)
    {
        var ext = exts[i % exts.Length];
        var stem = (i % 7) switch
        {
            0 => $"中文文件{i}",
            1 => $"with space {i}",
            2 => $".dotfile{i}",
            3 => $"UPPER{i}",
            4 => $"第{i}章",
            5 => $"a&b{i}",
            _ => $"file{i}"
        };
        samples.Add(Item($@"C:\t\{stem}{ext}", i % 11 == 0));
    }

    var sw = Stopwatch.StartNew();
    var outcome = RenameEngine.Run(samples,
        new RenameRule { Find = "file", Replace = "doc", Scope = RenameScope.Both, CaseSensitive = false });
    sw.Stop();

    True("200 项无异常完成", outcome.RegexError is null);
    True("全部产出 NewName", samples.All(s => !string.IsNullOrEmpty(s.NewName)));
    True($"200 项耗时 {sw.ElapsedMilliseconds}ms < 200ms", sw.ElapsedMilliseconds < 200);
    var invalid = samples.Count(s => s.State == ItemState.Invalid);
    Eq("合法样本不应被判非法", 0, invalid);
}

Console.WriteLine("\n== 9. 5000 项性能 (§8.11 预算) ==");
{
    var big = new List<RenameItem>(5000);
    for (var i = 0; i < 5000; i++) big.Add(Item($@"C:\t\file{i}.txt"));

    var sw = Stopwatch.StartNew();
    RenameEngine.Run(big, new RenameRule { Find = "file", Replace = "doc", Scope = RenameScope.Both });
    sw.Stop();
    True($"5000 项 {sw.ElapsedMilliseconds}ms < 1500ms", sw.ElapsedMilliseconds < 1500);
}

Console.WriteLine("\n== 10. 执行器 + 撤销 journal（真实文件，临时目录） ==");
{
    var root = Path.Combine(Path.GetTempPath(), "BatchRenameTest", Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(root);
    UndoJournal? journal = null;

    try
    {
        for (var i = 0; i < 10; i++)
            File.WriteAllText(Path.Combine(root, $"file{i}.txt"), "x");
        File.WriteAllText(Path.Combine(root, "CaseTest.txt"), "x");

        var items = RenameEngine.CreateItems(Directory.GetFiles(root));
        var rule = new RenameRule { Find = "file", Replace = "doc", Scope = RenameScope.Both };
        var run = RenameEngine.Run(items, rule);
        True("预览阶段无阻塞错误", !run.HasBlockingError);

        // --- write-ahead：先落盘 Pending，再执行 ---
        journal = UndoJournal.Create(UndoJournal.Summarize(rule),
            items.Where(i => i.State is ItemState.Ok or ItemState.Warning)
                 .Select(i => new JournalEntry
                 {
                     From = i.FullPath,
                     To = i.NewFullPath,
                     CaseOnly = i.CaseOnly,
                     Ok = true
                 }));
        journal.Save();

        True("journal 已落盘", File.Exists(journal.FilePath));
        Eq("初始状态 Pending", JournalState.Pending, journal.State);
        True("LoadLast 能读回且为 Pending", UndoJournal.LoadLast() is { State: JournalState.Pending });

        // --- 执行 ---
        var runner = new FileOperationRunner();
        var result = await runner.RunAsync(items);

        Eq("10 项文件名全部改动", 10, result.Succeeded);
        True("doc0.txt 存在", File.Exists(Path.Combine(root, "doc0.txt")));
        True("doc9.txt 存在", File.Exists(Path.Combine(root, "doc9.txt")));
        True("原 file0.txt 已不存在", !File.Exists(Path.Combine(root, "file0.txt")));

        journal.State = JournalState.Applied;
        journal.Save();
        Eq("回写状态 Applied", JournalState.Applied, journal.State);

        // --- 仅大小写变化走两步改名 ---
        var caseItem = RenameEngine.CreateItem(Path.Combine(root, "CaseTest.txt"), false, FileAttributes.Normal);
        RenameEngine.Run(new[] { caseItem },
            new RenameRule { CaseMode = CaseMode.Upper, Scope = RenameScope.StemOnly });
        True("引擎标记为 CaseOnly", caseItem.CaseOnly);

        var caseResult = await new FileOperationRunner().RunAsync(new[] { caseItem });
        True("仅大小写改名成功", caseResult.Succeeded == 1, $"失败 {caseResult.Failed} 项");
        True("CASETEST.txt 存在", File.Exists(Path.Combine(root, "CASETEST.txt")));

        // --- 撤销 ---
        var outcome = await journal.UndoAsync();
        Eq("撤销失败 0 项", 0, outcome.Failed);
        True("撤销了 10 项", outcome.Undone == 10, $"实际 {outcome.Undone}");
        True("file0.txt 已恢复", File.Exists(Path.Combine(root, "file0.txt")));
        True("doc0.txt 已不存在", !File.Exists(Path.Combine(root, "doc0.txt")));
        Eq("撤销后状态 Undone", JournalState.Undone, journal.State);

        // --- 重复撤销被拒绝 ---
        var again = await journal.UndoAsync();
        True("二次撤销被拒绝", again.Rejected, again.RejectReason);
    }
    finally
    {
        try { if (journal is not null && File.Exists(journal.FilePath)) File.Delete(journal.FilePath); } catch { }
        try { Directory.Delete(root, true); } catch { }
    }
}

Console.WriteLine("\n== 11. journal 失效判定（§8.10.3） ==");
{
    var root = Path.Combine(Path.GetTempPath(), "BatchRenameTest", Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(root);
    UndoJournal? journal = null;

    try
    {
        var a = Path.Combine(root, "a.txt");
        var b = Path.Combine(root, "b.txt");
        File.WriteAllText(a, "x");
        File.WriteAllText(b, "x");

        var items = RenameEngine.CreateItems(new[] { a, b });
        RenameEngine.Run(items, new RenameRule { Find = "txt", Replace = "md", Scope = RenameScope.ExtOnly });

        journal = UndoJournal.Create("test", items.Select(i => new JournalEntry
        {
            From = i.FullPath, To = i.NewFullPath, CaseOnly = i.CaseOnly, Ok = true
        }));
        journal.Save();
        journal.State = JournalState.Applied;
        journal.Save();

        await new FileOperationRunner().RunAsync(items);
        True("a.md 存在", File.Exists(Path.Combine(root, "a.md")));

        // 手动删掉一个目标文件，模拟"用户自己挪走了"
        File.Delete(Path.Combine(root, "b.md"));

        var outcome = await journal.UndoAsync();
        Eq("失效 1 项", 1, outcome.Invalid);
        Eq("回滚 1 项", 1, outcome.Undone);
        True("a.txt 已恢复", File.Exists(Path.Combine(root, "a.txt")));
    }
    finally
    {
        try { if (journal is not null && File.Exists(journal.FilePath)) File.Delete(journal.FilePath); } catch { }
        try { Directory.Delete(root, true); } catch { }
    }
}

Console.WriteLine("\n== 12. 磁盘冲突检测（§8.8） ==");
{
    var root = Path.Combine(Path.GetTempPath(), "BatchRenameTest", Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(root);

    try
    {
        File.WriteAllText(Path.Combine(root, "a.txt"), "x");
        File.WriteAllText(Path.Combine(root, "b.txt"), "x");

        // a.txt → b.txt，而 b.txt 已存在于磁盘
        var items = RenameEngine.CreateItems(new[] { Path.Combine(root, "a.txt") });
        var outcome = RenameEngine.Run(items,
            new RenameRule { Find = "a", Replace = "b", Scope = RenameScope.StemOnly });

        Eq("目标已存在 → ConflictOnDisk", ItemState.ConflictOnDisk, items[0].State);
        Eq("冲突文案", "目标已存在", items[0].Message);
        True("阻塞执行", outcome.HasBlockingError);

        // 仅大小写变化的目标就是自己，不能误判为磁盘冲突
        var caseItem = RenameEngine.CreateItems(new[] { Path.Combine(root, "a.txt") });
        RenameEngine.Run(caseItem,
            new RenameRule { CaseMode = CaseMode.Upper, Scope = RenameScope.StemOnly });
        True("CaseOnly 不误判为冲突", caseItem[0].State != ItemState.ConflictOnDisk, caseItem[0].State.ToString());
    }
    finally
    {
        try { Directory.Delete(root, true); } catch { }
    }
}

Console.WriteLine("\n== 13. CaseOnly 端到端撤销 ==");
{
    var root = Path.Combine(Path.GetTempPath(), "BatchRenameTest", Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(root);
    UndoJournal? journal = null;

    try
    {
        File.WriteAllText(Path.Combine(root, "Mixed.txt"), "x");

        var items = RenameEngine.CreateItems(new[] { Path.Combine(root, "Mixed.txt") });
        RenameEngine.Run(items, new RenameRule { CaseMode = CaseMode.Upper, Scope = RenameScope.StemOnly });

        var upperPath = Path.Combine(root, "MIXED.txt");
        True("引擎标记 CaseOnly", items[0].CaseOnly);
        Eq("新名是全大写", "MIXED.txt", items[0].NewName);

        journal = UndoJournal.Create("case-test", items.Select(i => new JournalEntry
        {
            From = i.FullPath, To = i.NewFullPath, CaseOnly = i.CaseOnly, Ok = true
        }));
        journal.Save();

        var runResult = await new FileOperationRunner().RunAsync(items);
        Eq("执行成功 1 项", 1, runResult.Succeeded);
        True("磁盘上确实变成 MIXED.txt", RenameEngine.ExistsExact(upperPath));
        True("原 Mixed.txt 已不存在", !RenameEngine.ExistsExact(Path.Combine(root, "Mixed.txt")));

        journal.State = JournalState.Applied;
        journal.Save();

        var undo = await journal.UndoAsync();
        True("撤销未被拒绝", !undo.Rejected, undo.RejectReason);
        Eq("撤销 1 项", 1, undo.Undone);
        True("Mixed.txt 已恢复", RenameEngine.ExistsExact(Path.Combine(root, "Mixed.txt")));
    }
    finally
    {
        try { if (journal is not null && File.Exists(journal.FilePath)) File.Delete(journal.FilePath); } catch { }
        try { Directory.Delete(root, true); } catch { }
    }
}

Console.WriteLine("\n== 14. 自动编号（§8.8 可选策略） ==");
{
    var root = Path.Combine(Path.GetTempPath(), "BatchRenameTest", Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(root);

    try
    {
        File.WriteAllText(Path.Combine(root, "a.txt"), "x");
        File.WriteAllText(Path.Combine(root, "b.txt"), "x");

        // 磁盘冲突：a.txt → b.txt（b 已存在），开自动编号 → b (1).txt
        var items = RenameEngine.CreateItems(new[] { Path.Combine(root, "a.txt") });
        var rule = new RenameRule { Find = "a", Replace = "b", Scope = RenameScope.StemOnly };

        RenameEngine.Run(items, rule, autoNumber: false);
        Eq("不开编号 → 磁盘冲突", ItemState.ConflictOnDisk, items[0].State);

        RenameEngine.Run(items, rule, autoNumber: true);
        Eq("开编号后状态 Ok", ItemState.Ok, items[0].State);
        Eq("编号加在 Stem 尾部", "b (1).txt", items[0].NewName);
        Eq("编号提示文案", "自动编号 (1)", items[0].Message);

        // 🔧 P1-5 回归：改扩展名时，编号必须加在**新**扩展名之前，且扩展名不能被老扩展名顶回去
        //   旧实现用 item.Ext（改名前的老扩展名）反推 stem，会产出 "e.md (1).txt"
        File.WriteAllText(Path.Combine(root, "e.txt"), "x");
        File.WriteAllText(Path.Combine(root, "e.md"), "x");     // 目标已存在 → 触发编号
        var extChange = RenameEngine.CreateItems(new[] { Path.Combine(root, "e.txt") });
        RenameEngine.Run(extChange,
            new RenameRule { Find = "txt", Replace = "md", Scope = RenameScope.ExtOnly }, autoNumber: true);
        Eq("改扩展名 + 编号：编号在新扩展名之前", "e (1).md", extChange[0].NewName);
        Eq("改扩展名 + 编号：状态 Ok", ItemState.Ok, extChange[0].State);

        // 批内互撞：两个都改成 x.txt → 第一个保留原名，第二个 x (1).txt
        File.WriteAllText(Path.Combine(root, "c.txt"), "x");
        File.WriteAllText(Path.Combine(root, "d.txt"), "x");
        var both = RenameEngine.CreateItems(new[]
        {
            Path.Combine(root, "c.txt"),
            Path.Combine(root, "d.txt")
        });
        RenameEngine.Run(both,
            new RenameRule { Find = "c", Replace = "x", Scope = RenameScope.StemOnly },
            autoNumber: true);
        // 只有 c.txt 命中 → x.txt；d.txt 不变
        Eq("命中项改成 x.txt", "x.txt", both[0].NewName);
        Eq("命中项状态 Ok", ItemState.Ok, both[0].State);

        // 真正的批内同名：两条规则都指向同名的场景用正则造
        File.WriteAllText(Path.Combine(root, "p1.txt"), "x");
        File.WriteAllText(Path.Combine(root, "p2.txt"), "x");
        var dup = RenameEngine.CreateItems(new[]
        {
            Path.Combine(root, "p1.txt"),
            Path.Combine(root, "p2.txt")
        });
        RenameEngine.Run(dup,
            new RenameRule { UseRegex = true, Find = @"^p\d$", Replace = "same", Scope = RenameScope.StemOnly },
            autoNumber: true);
        Eq("第一个保留 same.txt", "same.txt", dup[0].NewName);
        Eq("第二个自动编号", "same (1).txt", dup[1].NewName);
        True("两项都可执行", dup.All(d => d.State == ItemState.Ok));
    }
    finally
    {
        try { Directory.Delete(root, true); } catch { }
    }
}

Console.WriteLine("\n== 15. 崩溃恢复：Pending journal（§8.10.2） ==");
{
    var root = Path.Combine(Path.GetTempPath(), "BatchRenameTest", Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(root);
    UndoJournal? pending = null;

    try
    {
        // 造一个"改名已生效、但没来得及回写 Applied"的现场
        var from = Path.Combine(root, "before.txt");
        var to = Path.Combine(root, "after.txt");
        File.WriteAllText(from, "x");
        File.Move(from, to);

        pending = UndoJournal.Create("pending-test", new[]
        {
            new JournalEntry { From = from, To = to, Ok = true }
        });
        pending.Save();
        Eq("初始为 Pending", JournalState.Pending, pending.State);

        var found = UndoJournal.FindPending();
        True("FindPending 能定位到它", found is not null);
        True("FindPending 读回的状态是 Pending", found?.State == JournalState.Pending);

        var refused = await pending.UndoAsync();
        True("普通撤销拒绝 Pending（语义正确）", refused.Rejected, refused.RejectReason);

        // 🔧 旧版就是死在这里：崩溃恢复对话框调的是 UndoAsync，于是永远失败
        var rollback = await pending.RollbackPendingAsync();
        True("回滚未完成批次成功", !rollback.Rejected && rollback.Failed == 0, rollback.RejectReason);
        Eq("回滚 1 项", 1, rollback.Undone);
        True("原名已恢复", RenameEngine.ExistsExact(from));
        True("新名已不存在", !RenameEngine.ExistsExact(to));
        Eq("回滚后状态 Undone", JournalState.Undone, pending.State);
        True("不再有 Pending 记录", UndoJournal.FindPending() is null);
    }
    finally
    {
        try { pending?.Discard(); } catch { }
        try { Directory.Delete(root, true); } catch { }
    }
}

Console.WriteLine("\n== 16. 执行器：仅大小写改名（文件 + 目录，统一走 IFileOperation） ==");
{
    var root = Path.Combine(Path.GetTempPath(), "BatchRenameTest", Guid.NewGuid().ToString("N")[..8]);
    var sub = Path.Combine(root, "lowerdir");
    Directory.CreateDirectory(sub);
    File.WriteAllText(Path.Combine(sub, "inside.txt"), "x");
    var file = Path.Combine(root, "mixed.txt");
    File.WriteAllText(file, "x");

    try
    {
        var dirItem = RenameEngine.CreateItem(sub, true, FileAttributes.Directory);
        var fileItem = RenameEngine.CreateItem(file, false, FileAttributes.Normal);
        RenameEngine.Run(new[] { dirItem }, new RenameRule { CaseMode = CaseMode.Upper, Scope = RenameScope.StemOnly });
        RenameEngine.Run(new[] { fileItem }, new RenameRule { CaseMode = CaseMode.Upper, Scope = RenameScope.StemOnly });

        True("目录被标记 CaseOnly", dirItem.CaseOnly);
        True("文件被标记 CaseOnly", fileItem.CaseOnly);

        // 测试不登记到资源管理器撤销栈（AddUndoRecord: false），免得往用户 Ctrl+Z 里塞临时文件
        var res = await new FileOperationRunner()
            .RunAsync(new[] { dirItem, fileItem }, new RenameRunOptions(IntPtr.Zero, AddUndoRecord: false));

        Eq("两项都成功（旧版目录那项必失败）", 2, res.Succeeded);
        True("目录名已变成 LOWERDIR", RenameEngine.ExistsExact(Path.Combine(root, "LOWERDIR")));
        True("目录里的文件完好", File.Exists(Path.Combine(root, "LOWERDIR", "inside.txt")));
        True("文件名已变成 MIXED.txt", RenameEngine.ExistsExact(Path.Combine(root, "MIXED.txt")));
    }
    finally
    {
        try { Directory.Delete(root, true); } catch { }
    }
}

Console.WriteLine("\n== 17. 异常中断：按磁盘现状回写 journal（P0-3） ==");
{
    var root = Path.Combine(Path.GetTempPath(), "BatchRenameTest", Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(root);

    try
    {
        // 造"执行到一半崩了"的现场：第一项真的改名了，第二项没来得及
        var doneFrom = Path.Combine(root, "one.txt");
        var doneTo = Path.Combine(root, "ONE.txt");
        File.WriteAllText(doneFrom, "x");
        File.Move(doneFrom, doneTo);

        var todoFrom = Path.Combine(root, "two.txt");
        File.WriteAllText(todoFrom, "x");

        var journal = UndoJournal.Create("crash-test", new[]
        {
            new JournalEntry { From = doneFrom, To = doneTo, Ok = true },
            new JournalEntry { From = todoFrom, To = Path.Combine(root, "TWO.txt"), Ok = true }
        });
        journal.Save();
        Eq("中断前逐项都是 Ok=true（还不可信）", 2, journal.Entries.Count(e => e.Ok));

        journal.ReconcileFromDisk();

        True("做过的那项仍是 Ok", journal.Entries[0].Ok);
        True("没做的那项被改成 Ok=false", !journal.Entries[1].Ok);
        Eq("状态仍为 Pending（下次启动提示回滚）", JournalState.Pending, journal.State);
        journal.Discard();
    }
    finally
    {
        try { Directory.Delete(root, true); } catch { }
    }
}

Console.WriteLine("\n== 18. 二次改名：已改名项照常参与规则计算（P1-7） ==");
{
    // 模拟"执行成功后被 RemapItems 标记过"的项：FullPath 已是改名后的路径
    var item = Item(@"C:\t\OMEGA.txt");
    item.IsRenamed = true;

    // 规则为空时：不该冒出一个假的新名字，只回显「已改名」
    RenameEngine.Run(new[] { item }, new RenameRule { Scope = RenameScope.StemOnly });
    Eq("无规则时回显为已改名", ItemState.Renamed, item.State);
    Eq("已改名文案", "已改名", item.Message);

    // 有规则时：必须真的能再改一次（旧实现会一直卡在 Renamed）
    RenameEngine.Run(new[] { item },
        new RenameRule { Find = "OMEGA", Replace = "final", Scope = RenameScope.StemOnly, CaseMode = CaseMode.Upper });
    Eq("二次改名产出新名字", "FINAL.txt", item.NewName);
    Eq("二次改名状态 Ok（可执行）", ItemState.Ok, item.State);
}

Console.WriteLine("\n== 19. 链式改名：按依赖顺序入队，不许覆盖（P0-1 / P2-10） ==");
{
    var root = Path.Combine(Path.GetTempPath(), "BatchRenameTest", Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(root);

    try
    {
        var a = Path.Combine(root, "a.txt");
        var b = Path.Combine(root, "b.txt");
        File.WriteAllText(a, "AAA");
        File.WriteAllText(b, "BBB");

        // 故意按"错序"放进列表：a→b 在 b→c 前面。
        // 若执行器不排序，Shell 会先 a→b 静默覆盖 b.txt（BBB 消失），再 b→c 把覆盖后的文件挪走。
        var toB = RenameEngine.CreateItem(a, false, FileAttributes.Normal);
        toB.NewName = "b.txt";
        toB.State = ItemState.Ok;

        var toC = RenameEngine.CreateItem(b, false, FileAttributes.Normal);
        toC.NewName = "c.txt";
        toC.State = ItemState.Ok;

        var res = await new FileOperationRunner()
            .RunAsync(new[] { toB, toC }, new RenameRunOptions(IntPtr.Zero, AddUndoRecord: false));

        Eq("链式两项都成功", 2, res.Succeeded);
        True("a.txt 已不存在", !RenameEngine.ExistsExact(a));
        True("b.txt 的内容来自 a（AAA）", File.Exists(b) && File.ReadAllText(b) == "AAA");
        True("c.txt 的内容来自 b（BBB，没被覆盖）", File.Exists(Path.Combine(root, "c.txt")) &&
             File.ReadAllText(Path.Combine(root, "c.txt")) == "BBB");
    }
    finally
    {
        try { Directory.Delete(root, true); } catch { }
    }
}

Console.WriteLine("\n== 20. 批内循环改名：预览阶段拦掉（P0-1） ==");
{
    var root = Path.Combine(Path.GetTempPath(), "BatchRenameTest", Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(root);

    try
    {
        var a = Path.Combine(root, "a.txt");
        var b = Path.Combine(root, "b.txt");
        File.WriteAllText(a, "AAA");
        File.WriteAllText(b, "BBB");

        // a↔b 互换：一条规则造不出来，直接构造出来喂给引擎的环检测
        var swapA = RenameEngine.CreateItem(a, false, FileAttributes.Normal);
        swapA.NewName = "b.txt";
        swapA.State = ItemState.Ok;

        var swapB = RenameEngine.CreateItem(b, false, FileAttributes.Normal);
        swapB.NewName = "a.txt";
        swapB.State = ItemState.Ok;

        RenameEngine.MarkBatchCycles(new[] { swapA, swapB });
        Eq("互换的第一项被判冲突", ItemState.ConflictInBatch, swapA.State);
        Eq("互换的第二项被判冲突", ItemState.ConflictInBatch, swapB.State);
        Eq("冲突文案", "批内循环改名", swapA.Message);

        // 链不是环，不能误伤
        var chainA = RenameEngine.CreateItem(a, false, FileAttributes.Normal);
        chainA.NewName = "b.txt";
        chainA.State = ItemState.Ok;
        var chainB = RenameEngine.CreateItem(b, false, FileAttributes.Normal);
        chainB.NewName = "c.txt";
        chainB.State = ItemState.Ok;

        RenameEngine.MarkBatchCycles(new[] { chainA, chainB });
        Eq("链式改名第一项不算环", ItemState.Ok, chainA.State);
        Eq("链式改名第二项不算环", ItemState.Ok, chainB.State);
    }
    finally
    {
        try { Directory.Delete(root, true); } catch { }
    }
}

Console.WriteLine("\n== 21. 单项失败不中止整批（P2-10：逐项 HRESULT） ==");
{
    var root = Path.Combine(Path.GetTempPath(), "BatchRenameTest", Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(root);

    try
    {
        var locked = Path.Combine(root, "locked.txt");
        var free = Path.Combine(root, "free.txt");
        File.WriteAllText(locked, "x");
        File.WriteAllText(free, "x");

        var lockedItem = RenameEngine.CreateItem(locked, false, FileAttributes.Normal);
        lockedItem.NewName = "LOCKED.txt";
        lockedItem.State = ItemState.Ok;

        var freeItem = RenameEngine.CreateItem(free, false, FileAttributes.Normal);
        freeItem.NewName = "FREE.txt";
        freeItem.State = ItemState.Ok;

        // 独占打开：改名必然失败（共享冲突），用来验证"逐项失败、其余继续"
        using (var hold = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var res = await new FileOperationRunner()
                .RunAsync(new[] { lockedItem, freeItem }, new RenameRunOptions(IntPtr.Zero, AddUndoRecord: false));

            Eq("被占用那项失败", 1, res.Failed);
            Eq("另一项仍然成功", 1, res.Succeeded);
            var err = res.Entries.First(e => !e.Ok).Error ?? "";
            True($"失败原因已翻译成中文（{err}）", err.Contains("使用") || err.Contains("占用") || err.Contains("拒绝"));
            True("成功项真的改了名", RenameEngine.ExistsExact(Path.Combine(root, "FREE.txt")));
        }
    }
    finally
    {
        try { Directory.Delete(root, true); } catch { }
    }
}

Console.WriteLine("\n== 22. 两层防抖：第一层跳过磁盘检查（§8.11） ==");
{
    var root = Path.Combine(Path.GetTempPath(), "BatchRenameTest", Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(root);

    try
    {
        File.WriteAllText(Path.Combine(root, "a.txt"), "x");
        File.WriteAllText(Path.Combine(root, "b.txt"), "x");     // 目标已存在

        var items = RenameEngine.CreateItems(new[] { Path.Combine(root, "a.txt") });
        var rule = new RenameRule { Find = "a", Replace = "b", Scope = RenameScope.StemOnly };

        var fast = RenameEngine.Run(items, rule, checkDisk: false);
        True("第一层（不查磁盘）不标冲突", items[0].State != ItemState.ConflictOnDisk, items[0].State.ToString());
        True("第一层不阻塞（按钮可点，但执行前会再查一遍）", !fast.HasBlockingError);

        RenameEngine.Run(items, rule, checkDisk: true);
        Eq("第二层（查磁盘）如实标出冲突", ItemState.ConflictOnDisk, items[0].State);
    }
    finally
    {
        try { Directory.Delete(root, true); } catch { }
    }
}

try { if (Directory.Exists(testJournalRoot)) Directory.Delete(testJournalRoot, true); } catch { }

Console.WriteLine();
Console.WriteLine(new string('-', 56));
Console.WriteLine($"检查 {checks} 项，失败 {failures} 项");
return failures == 0 ? 0 : 1;
