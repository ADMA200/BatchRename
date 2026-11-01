using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Shell;

namespace BatchRename.App.Core;

/// <summary>
/// <c>IFileOperationProgressSink</c> 的托管实现——由我们实现、Shell 回调。
///
/// 这里直接实现 CsWin32 生成的那份接口（生成结果是托管 <c>[ComImport]</c> 接口，
/// 不是 vtable 结构体），所以把本类实例当参数传给 <c>IFileOperation::RenameItem</c> 时
/// CLR 会自动建 CCW，不需要手工 <c>Marshal.GetComInterfaceForObject</c>，也不用碰 vtable。
///
/// 行为约定：
/// ① <c>PostRenameItem</c> 只记录、绝不抛 —— 单条失败不该让后面几百项都不执行；
/// ② 取消只在 <c>PreRenameItem</c> 里做：抛异常会被 CLR 翻成失败 HRESULT，
///    Shell 收到失败即停止后续操作，已完成的项保持已完成；
/// ③ 每个回调都包 try/catch —— 没接住的异常同样会变成失败 HRESULT 并中止整批。
/// </summary>
internal sealed class RenameProgressSink : IFileOperationProgressSink
{
    private readonly CancellationToken _ct;
    private readonly int _total;
    private readonly int _reportEvery;
    private readonly Action<int, int>? _onProgress;
    private int _done;

    private readonly Dictionary<string, (int Hr, string? NewName)> _results =
        new(StringComparer.OrdinalIgnoreCase);

    /// <param name="ct">取消令牌：在 <c>PreRenameItem</c> 里检查，命中即中止后续操作。</param>
    /// <param name="total">本批入队项数，用于进度分母。</param>
    /// <param name="onProgress">
    /// 进度回调（已完成, 总数）。**在 STA 工作线程上调用**，调用方自行切线程。
    /// 按 ~100 次/批 节流，几千项也不会把 UI 打满。
    /// </param>
    public RenameProgressSink(CancellationToken ct, int total, Action<int, int>? onProgress = null)
    {
        _ct = ct;
        _total = total;
        _onProgress = onProgress;
        _reportEvery = Math.Max(1, total / 100);
    }

    /// <summary>源路径 → (HRESULT, Shell 实际给的最终名)。仅本次执行期间有效。</summary>
    public IReadOnlyDictionary<string, (int Hr, string? NewName)> Results => _results;

    public bool CancelRequested { get; private set; }

    public void StartOperations() { }

    public void FinishOperations(HRESULT hrResult) { }

    public void PreRenameItem(uint dwFlags, IShellItem psiItem, PCWSTR pszNewName)
    {
        if (!_ct.IsCancellationRequested) return;

        // 故意抛出：CLR 把它翻成失败 HRESULT，Shell 据此停止后续操作
        CancelRequested = true;
        throw new OperationCanceledException("用户取消了批量改名");
    }

    public void PostRenameItem(uint dwFlags, IShellItem psiItem, PCWSTR pszNewName, HRESULT hrRename,
                               IShellItem psiNewlyCreated)
    {
        try
        {
            var from = GetPath(psiItem);
            if (!string.IsNullOrEmpty(from))
            {
                _results[from] = (hrRename.Value, pszNewName.ToString());
            }

            var done = Interlocked.Increment(ref _done);
            if (_onProgress is not null && (done % _reportEvery == 0 || done == _total))
            {
                _onProgress(done, _total);
            }
        }
        catch
        {
            // 记录结果失败不影响 Shell 继续执行
        }
    }

    // 本工具只做 Rename，其余回调一律放行
    public void PreMoveItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, PCWSTR pszNewName) { }
    public void PostMoveItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, PCWSTR pszNewName, HRESULT hrMove, IShellItem psiNewlyCreated) { }
    public void PreCopyItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, PCWSTR pszNewName) { }
    public void PostCopyItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, PCWSTR pszNewName, HRESULT hrCopy, IShellItem psiNewlyCreated) { }
    public void PreDeleteItem(uint dwFlags, IShellItem psiItem) { }
    public void PostDeleteItem(uint dwFlags, IShellItem psiItem, HRESULT hrDelete, IShellItem psiNewlyCreated) { }
    public void PreNewItem(uint dwFlags, IShellItem psiDestinationFolder, PCWSTR pszNewName) { }
    public void PostNewItem(uint dwFlags, IShellItem psiDestinationFolder, PCWSTR pszNewName, PCWSTR pszTemplateName, uint dwFileAttributes, HRESULT hrNew, IShellItem psiNewItem) { }
    public void UpdateProgress(uint iWorkTotal, uint iWorkSoFar) { }
    public void ResetTimer() { }
    public void PauseTimer() { }
    public void ResumeTimer() { }

    /// <summary>
    /// 取回调项的物理路径。失败时退回解析路径（SIGDN_DESKTOPABSOLUTEPARSING），
    /// 再不行就当这条没有结果（执行器会按"未执行"上报，不会误判成功）。
    /// </summary>
    private static unsafe string? GetPath(IShellItem? psi)
    {
        if (psi is null) return null;

        var sigdns = new[] { SIGDN.SIGDN_FILESYSPATH, SIGDN.SIGDN_DESKTOPABSOLUTEPARSING };
        foreach (var sigdn in sigdns)
        {
            try
            {
                psi.GetDisplayName(sigdn, out var pw);
                if (pw.Value is null) continue;

                var text = pw.ToString();
                Marshal.FreeCoTaskMem((IntPtr)pw.Value);
                if (!string.IsNullOrEmpty(text)) return text;
            }
            catch
            {
                // 换下一种 SIGDN 再试
            }
        }
        return null;
    }
}
