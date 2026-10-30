using System.Diagnostics;
using System.Text;
using BatchRename.App.Core;
using BatchRename.App.Models;

// 控制台默认 GBK，中文断言名会乱码
try { Console.OutputEncoding = Encoding.UTF8; } catch { }

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

Console.WriteLine();
Console.WriteLine(new string('-', 56));
Console.WriteLine($"检查 {checks} 项，失败 {failures} 项");
return failures == 0 ? 0 : 1;
