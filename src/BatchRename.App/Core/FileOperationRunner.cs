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
    /// <summary>因取消而没执行的项（用户/上层请求取消）。</summary>
    public bool Cancelled { get; init; }
    public int Succeeded => Entries.Count(e => e.Ok);
    public int Failed => Entries.Count(e => !e.Ok);
    public bool AllOk => Failed == 0 && Entries.Count > 0;
}

/// <summary>
/// 一次执行的可选参数。
/// </summary>
/// <param name="OwnerHwnd">
/// 主窗口句柄。Shell 的错误框 / UAC 提示框会以它作为 owner；
/// 不给的话对话框可能弹在窗口后面，用户会以为"点了没反应"。
/// </param>
/// <param name="AddUndoRecord">
/// 是否把这一批登记到<b>资源管理器</b>的撤销栈（<c>FOFX_ADDUNDORECORD</c>）。
/// 正向改名 = true：用户关掉本程序后仍能在资源管理器里 Ctrl+Z 撤销；
/// journal 驱动的反向执行 = false：否则撤销本身也会多出一条撤销记录。
/// </param>
public readonly record struct RenameRunOptions(IntPtr OwnerHwnd, bool AddUndoRecord);

/// <summary>
/// 规格书 §8.9。IFileOperation 必须跑在自建的 STA 线程上（不能用 UI 线程，长操作会冻结界面）。
///
/// 2026-11-01 修订记录：
///   ① 删掉「CaseOnly 走 File.Move 两步」：实测 IFileOperation::RenameItem 对文件和目录的
///      仅大小写改名都直接成功；File.Move 反而动不了目录。现在所有项统一进一个队列。
///   ② 加 FOFX_ADDUNDORECORD：整批登记到资源管理器撤销栈，关掉本程序后 Ctrl+Z 仍有效。
///   ③ 加 SetOwnerWindow：UAC 提示框与 Shell 错误框归属主窗口。
///   ④ 实现 IFileOperationProgressSink：逐项成败与 HRESULT 由 Shell 直接给，
///      不再"执行后按名字扫目录"（旧实现是 O(n²)，且链式改名会把成功的项误报为失败）。
///   ⑤ 入队前按**依赖顺序**排序：链式改名 a→b、b→c 若先入队 a→b，Shell 会静默覆盖
///      b 的原有内容（FOF_NOCONFIRMATION）。有依赖时按拓扑序排，无依赖时保持原顺序。
/// </summary>
public sealed class FileOperationRunner
{
    private const uint COINIT_APARTMENTTHREADED = 0x2;
    private static readonly Guid CLSID_FileOperation = new("3AD05575-8857-4850-9277-11B85BDB8E09");

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr pvReserved, uint dwCoInit);

    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();

    public Task<RenameBatchResult> RunAsync(
        IReadOnlyList<RenameItem> items,
        RenameRunOptions options = default,
        CancellationToken ct = default,
        Action<int, int>? onProgress = null)
    {
        var tcs = new TaskCompletionSource<RenameBatchResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        var thread = new Thread(() =>
        {
            try { tcs.TrySetResult(RunCore(items, options, ct, onProgress)); }
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

    private static unsafe RenameBatchResult RunCore(
        IReadOnlyList<RenameItem> items, RenameRunOptions options, CancellationToken ct,
        Action<int, int>? onProgress)
    {
        CoInitializeEx(IntPtr.Zero, COINIT_APARTMENTTHREADED);

        var planned = new List<(RenameItem Item, string Target)>();

        try
        {
            // 只有真正会改名的项参与执行：已勾选 + 状态 Ok/Warning
            foreach (var item in items)
            {
                if (!item.IsEnabled) continue;
                if (item.State is not (ItemState.Ok or ItemState.Warning)) continue;
                planned.Add((item, Path.Combine(item.Directory, item.NewName)));
            }

            var aborted = false;
            var cancelled = false;
            IReadOnlyDictionary<string, (int Hr, string? NewName)>? sinkResults = null;

            if (planned.Count > 0 && !ct.IsCancellationRequested)
            {
                var ordered = OrderByDependency(planned);
                var sink = new RenameProgressSink(ct, planned.Count, onProgress);

                IFileOperation? op = null;
                try
                {
                    var clsidType = Type.GetTypeFromCLSID(CLSID_FileOperation)
                                    ?? throw new InvalidOperationException("CLSID_FileOperation 未注册");
                    op = (IFileOperation)Activator.CreateInstance(clsidType)!;

                    // 不加 FOF_RENAMEONCOLLISION：冲突在预览阶段已解决，静默改名比报错更坏
                    var flags = FILEOPERATION_FLAGS.FOF_SILENT
                              | FILEOPERATION_FLAGS.FOF_NOCONFIRMATION
                              | FILEOPERATION_FLAGS.FOF_NOCONFIRMMKDIR
                              | FILEOPERATION_FLAGS.FOF_NOERRORUI
                              | FILEOPERATION_FLAGS.FOFX_SHOWELEVATIONPROMPT;

                    if (options.AddUndoRecord) flags |= FILEOPERATION_FLAGS.FOFX_ADDUNDORECORD;
                    op.SetOperationFlags(flags);

                    if (options.OwnerHwnd != IntPtr.Zero) op.SetOwnerWindow((HWND)options.OwnerHwnd);

                    // sink 是托管对象，CLR 会为非托管侧建 CCW；每项 RenameItem 都挂上同一个 sink
                    foreach (var (item, _) in ordered)
                    {
                        if (ct.IsCancellationRequested) break;

                        var src = CreateShellItem(item.FullPath);
                        fixed (char* pName = item.NewName)
                        {
                            op.RenameItem(src, new PCWSTR(pName), sink);
                        }
                    }

                    try
                    {
                        op.PerformOperations();
                    }
                    catch (OperationCanceledException)
                    {
                        // sink 在 PreRenameItem 里抛异常 → CLR 把失败 HRESULT 带回 PerformOperations。
                        // 这是"按用户要求提前停下"，不是失败：已完成项的结果仍在 sink 里。
                    }

                    op.GetAnyOperationsAborted(out var abortedRaw);   // CsWin32 的 out 重载只吃 BOOL
                    aborted = abortedRaw;
                    cancelled = sink.CancelRequested;
                    sinkResults = sink.Results;
                }
                finally
                {
                    if (op is not null) Marshal.ReleaseComObject(op);
                }
            }

            var results = planned.Select(p =>
            {
                if (sinkResults is not null && sinkResults.TryGetValue(p.Item.FullPath, out var r))
                {
                    var ok = r.Hr >= 0;
                    // 「仅大小写改名」单独复核：Shell 报成功但文件名没变的情况历史上真出现过
                    if (ok && p.Item.CaseOnly) ok = RenameEngine.ExistsExact(p.Target);
                    return new RenameEntryResult(p.Item.FullPath, p.Target, ok,
                                                 ok ? null : DescribeHr(r.Hr));
                }

                return new RenameEntryResult(p.Item.FullPath, p.Target, false,
                                             cancelled ? "已取消" : "未执行");
            }).ToList();

            return new RenameBatchResult { Entries = results, AnyAborted = aborted, Cancelled = cancelled };
        }
        finally
        {
            CoUninitialize();
        }
    }

    /// <summary>
    /// 按依赖顺序排列入队项：若 A 的目标名正是 B 的源名，B 必须排在 A 前面。
    ///
    /// 不排的后果不是"顺序不好看"，而是**丢数据**：a→b 先执行时 b 的内容会被
    /// 静默覆盖（FOF_NOCONFIRMATION 不问），随后 b→c 把覆盖后的文件挪走。
    /// 无依赖时（99.9% 的情况）直接返回原列表，不付出排序成本。
    /// </summary>
    private static List<(RenameItem Item, string Target)> OrderByDependency(
        List<(RenameItem Item, string Target)> planned)
    {
        if (planned.Count < 2) return planned;

        var byTarget = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < planned.Count; i++)
        {
            var t = planned[i].Target;
            if (!byTarget.TryGetValue(t, out var list)) byTarget[t] = list = new List<int>(1);
            list.Add(i);
        }

        var indegree = new int[planned.Count];
        var after = new List<int>?[planned.Count];   // after[j] = 必须排在 j 之后的项
        var hasDependency = false;

        for (var j = 0; j < planned.Count; j++)
        {
            if (!byTarget.TryGetValue(planned[j].Item.FullPath, out var waiters)) continue;
            foreach (var i in waiters)
            {
                if (i == j) continue;
                (after[j] ??= new List<int>(1)).Add(i);
                indegree[i]++;
                hasDependency = true;
            }
        }

        if (!hasDependency) return planned;

        var order = new List<(RenameItem Item, string Target)>(planned.Count);
        var queue = new Queue<int>();
        for (var i = 0; i < planned.Count; i++) if (indegree[i] == 0) queue.Enqueue(i);

        var emitted = new bool[planned.Count];
        while (queue.Count > 0)
        {
            var j = queue.Dequeue();
            if (emitted[j]) continue;
            emitted[j] = true;
            order.Add(planned[j]);

            if (after[j] is null) continue;
            foreach (var i in after[j]!)
                if (--indegree[i] == 0) queue.Enqueue(i);
        }

        // 环由引擎在预览阶段拦掉；这里只是兜底，把剩下的按原顺序补上
        if (order.Count < planned.Count)
        {
            for (var i = 0; i < planned.Count; i++)
                if (!emitted[i]) order.Add(planned[i]);
        }

        return order;
    }

    /// <summary>把 HRESULT 翻成人话，别再让用户看 0x80070020。</summary>
    internal static string DescribeHr(int hr)
    {
        if (hr >= 0) return "改名失败";   // 理论上不会走到这里

        // 常规 Win32 错误：FACILITY_WIN32
        if ((hr & unchecked((int)0xFFFF0000)) == unchecked((int)0x80070000))
        {
            return (hr & 0xFFFF) switch
            {
                2 => "源文件不存在",
                3 => "路径不存在",
                5 => "拒绝访问（权限不足或被占用）",
                32 => "文件正被其它程序使用",
                33 => "文件被锁定，无法改名",
                1223 => "操作已取消",
                183 => "目标名已存在",
                206 => "路径过长",
                _ => $"改名失败 (0x{hr:X8})"
            };
        }

        // Shell 复制引擎错误（COPYENGINE_E_*，0x8027xxxx）：改名被占用时实测走这一族
        if ((hr & unchecked((int)0xFFFF0000)) == unchecked((int)0x80270000))
        {
            return (hr & 0xFFFF) switch
            {
                0x27 => "源文件正被其它程序使用",
                _ => $"改名失败（Shell 复制引擎 0x{hr:X8}，多为文件被占用或权限不足）"
            };
        }

        return $"改名失败 (0x{hr:X8})";
    }

    private static IShellItem CreateShellItem(string path)
    {
        PInvoke.SHCreateItemFromParsingName<IShellItem>(path, null, out var item).ThrowOnFailure();
        return item;
    }
}
