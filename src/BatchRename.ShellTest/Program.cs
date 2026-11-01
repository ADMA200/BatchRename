using System.Text;
using BatchRename.Core.Tests;

// 控制台默认 GBK，中文断言名会乱码
try { Console.OutputEncoding = Encoding.UTF8; } catch { }

// 断言体在 tests/BatchRename.Core.Tests/CoreSuite.cs（与 xUnit 共用同一份实现）。
// 这个 console 入口只负责：逐条打印、汇总、用退出码表达成败（0 = 全过）。
var result = await CoreSuite.RunAsync((name, ok) =>
{
    Console.WriteLine(ok ? $"  [ok]   {name}" : $"  [FAIL] {name}");
});

Console.WriteLine();
Console.WriteLine(new string('-', 56));
Console.WriteLine($"检查 {result.Checks} 项，失败 {result.Failures} 项");
return result.Failures == 0 ? 0 : 1;
