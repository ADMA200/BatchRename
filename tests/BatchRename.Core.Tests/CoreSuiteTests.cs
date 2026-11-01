using Xunit;

namespace BatchRename.Core.Tests;

/// <summary>
/// M2 引擎断言集的 xUnit 入口。<see cref="CoreSuite"/> 是唯一实现，
/// console 入口（BatchRename.ShellTest）跑的是同一份代码，避免两边断言漂移。
/// </summary>
public class CoreSuiteTests
{
    /// <summary>断言条数守卫：任何人增删断言都要在这里显式改数字，防止"悄悄少跑几条"。</summary>
    private const int ExpectedChecks = 124;

    [Fact]
    public async Task 全部断言通过()
    {
        var failed = new List<string>();

        var result = await CoreSuite.RunAsync((name, ok) =>
        {
            if (!ok) failed.Add(name);
        });

        Assert.True(result.Failures == 0,
            $"{result.Failures} 条断言失败：{Environment.NewLine}{string.Join(Environment.NewLine, failed)}");

        Assert.Equal(ExpectedChecks, result.Checks);
    }
}
