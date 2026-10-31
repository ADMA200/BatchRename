using System.IO;
using System.Runtime.InteropServices;
using BatchRename.App.Models;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Shell;

namespace BatchRename.App.Core;

public sealed record RenameEntryResult(string From, string To, bool Ok, string? Error);

public sealed class RenameBatchResult
{
    public IReadOnlyList<RenameEntryResult> Entries { get; init; } = Array.Empty<RenameEntryResult>();
    public bool AnyAborted { get; init; }
    public int Succeeded => Entries.Count(e => e.Ok);
    public int Failed => Entries.Count(e => !e.Ok);
    public bool AllOk => Failed == 0 && Entries.Count > 0;
}

/// <summary>
/// 规格书 §8.9。IFileOperation 必须跑在自建的 STA 线程上（不能用 UI 线程，长操作会冻结界面）。
///
/// 不用 IFileOperationProgressSink：逐项结果在执行后用文件系统校验即可，
/// 省掉实现 15 个回调，也不会因 sink 的 vtable 细节踩坑。
///
/// 🔧 与规格书的偏差（实现时证伪）：§8.9.2 建议在同一 IFileOperation 里对 CaseOnly 项
///    连续入队两次 RenameItem，第二次按临时名重新创建 IShellItem。这行不通——
///    PerformOperations 之前临时文件根本不存在，SHCreateItemFromParsingName 返回
///    0x80070002。IFileOperation 的队列无法引用前一步的结果。
///    因此 CaseOnly 项改用 File.Move 走两步，普通项仍走 IFileOperation。
/// </summary>
public sealed class FileOperationRunner
{
    private const uint COINIT_APARTMENTTHREADED = 0x2;
    private static readonly Guid CLSID_FileOperation = new("3AD05575-8857-4850-9277-11B85BDB8E09");

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr pvReserved, uint dwCoInit);

    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();

    public Task<RenameBatchResult> RunAsync(IReadOnlyList<RenameItem> items, CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<RenameBatchResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        var thread = new Thread(() =>
        {
            try { tcs.TrySetResult(RunCore(items, ct)); }
            catch (Exception ex) { tcs.TrySetException(ex); }
        })
        {
            IsBackground = true,
            Name = "RenameSta"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        return tcs.Task;
    }

    private static unsafe RenameBatchResult RunCore(IReadOnlyList<RenameItem> items, CancellationToken ct)
    {
        CoInitializeEx(IntPtr.Zero, COINIT_APARTMENTTHREADED);

        var planned = new List<(RenameItem Item, string Target)>();
        var failures = new Dictionary<string, string>();

        try
        {
            var normal = new List<RenameItem>();

            // ---- 先分类：CaseOnly 单独走两步 File.Move ----
            foreach (var item in items)
            {
                if (ct.IsCancellationRequested) break;
                if (!item.IsEnabled) continue;
                if (item.State is not (ItemState.Ok or ItemState.Warning)) continue;

                var target = Path.Combine(item.Directory, item.NewName);
                planned.Add((item, target));

                if (item.CaseOnly)
                {
                    var err = RenameCaseOnly(item, target);
                    if (err is not null) failures[item.FullPath] = err;
                }
                else
                {
                    normal.Add(item);
                }
            }

            // ---- 普通项走 IFileOperation 批量 ----
            if (normal.Count > 0 && !ct.IsCancellationRequested)
            {
                IFileOperation? op = null;
                try
                {
                    var clsidType = Type.GetTypeFromCLSID(CLSID_FileOperation);
                    op = (IFileOperation)Activator.CreateInstance(clsidType)!;

                    // 有意不加 FOF_ALLOWUNDO（撤销走我们自己的 journal）、
                    // 不加 FOF_RENAMEONCOLLISION（冲突在预览阶段已解决，静默改名比报错更坏）
                    op.SetOperationFlags(
                        FILEOPERATION_FLAGS.FOF_SILENT |
                        FILEOPERATION_FLAGS.FOF_NOCONFIRMATION |
                        FILEOPERATION_FLAGS.FOF_NOCONFIRMMKDIR |
                        FILEOPERATION_FLAGS.FOF_NOERRORUI |
                        FILEOPERATION_FLAGS.FOFX_SHOWELEVATIONPROMPT);

                    foreach (var item in normal)
                    {
                        if (ct.IsCancellationRequested) break;

                        var src = CreateShellItem(item.FullPath);
                        fixed (char* pName = item.NewName)
                        {
                            op.RenameItem(src, new PCWSTR(pName), null!);
                        }
                    }

                    op.PerformOperations();
                    op.GetAnyOperationsAborted(out var b);
                }
                finally
                {
                    if (op is not null) Marshal.ReleaseComObject(op);
                }
            }

            var results = planned
                .Select(p =>
                {
                    // 用区分大小写的校验：File.Exists 在 NTFS 上忽略大小写，
                    // 会把「仅大小写改名没生效」误判为成功
                    var ok = RenameEngine.ExistsExact(p.Target);
                    var err = ok ? null : (failures.TryGetValue(p.Item.FullPath, out var e) ? e : "改名后目标不存在");
                    return new RenameEntryResult(p.Item.FullPath, p.Target, ok, err);
                })
                .ToList();

            return new RenameBatchResult { Entries = results };
        }
        finally
        {
            CoUninitialize();
        }
    }

    /// <summary>
    /// 仅大小写变化：NTFS 不区分大小写，a.txt → A.txt 直接改会 ERROR_ALREADY_EXISTS，
    /// 必须经一个临时名中转。失败时把临时名挪回原名，避免留下半成品。
    /// </summary>
    private static string? RenameCaseOnly(RenameItem item, string target)
    {
        var temp = Path.Combine(item.Directory, "$br$" + Guid.NewGuid().ToString("N") + "$");
        try
        {
            File.Move(item.FullPath, temp);
        }
        catch (Exception ex)
        {
            return "改名失败：" + ex.Message;
        }

        try
        {
            File.Move(temp, target);
            return null;
        }
        catch (Exception ex)
        {
            // 第二步失败 → 回滚第一步，别把文件留在临时名下
            try { File.Move(temp, item.FullPath); } catch { }
            return "改名失败：" + ex.Message;
        }
    }

    private static IShellItem CreateShellItem(string path)
    {
        PInvoke.SHCreateItemFromParsingName<IShellItem>(path, null, out var item).ThrowOnFailure();
        return item;
    }
}
