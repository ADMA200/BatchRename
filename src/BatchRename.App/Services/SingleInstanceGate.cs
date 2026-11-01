using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using Microsoft.UI.Dispatching;

namespace BatchRename.App.Services;

/// <summary>
/// 规格书 §7.6 单实例与激活协议。
///
/// 只解决"连点两次出两个窗口"，不提供热复用——窗口一关进程就没了。
/// 抢到锁 → 开窗 + 启动管道服务；抢不到 → 转发路径后立刻退出。
///
/// 与规格书的偏差：Mutex 名没用 {PackageFamilyName}。
/// 本应用由 Shell 通过 CreateProcess 启动，exe 在外部位置（bin\publish），
/// 进程没有包身份，Package.Current 不可用，因此用固定名。
/// </summary>
public sealed class SingleInstanceGate : IDisposable
{
    private const string MutexName = @"Local\BatchRename.SingleInstance";
    private const int ConnectTimeoutMs = 1500;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int dwProcessId);

    private Mutex? _mutex;
    private readonly string _pipeName;

    public bool IsFirstInstance { get; }
    public int ProcessId { get; }

    public static string RootDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BatchRename");

    private static string ActivePidPath => Path.Combine(RootDir, "active.pid");

    public SingleInstanceGate()
    {
        ProcessId = Environment.ProcessId;
        _pipeName = $"BatchRename.Activate.{ProcessId}";

        try
        {
            _mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
            IsFirstInstance = createdNew;
            if (!createdNew)
            {
                // 没抢到锁，不要再持有 mutex，交给第一个实例
                _mutex.Dispose();
                _mutex = null;
            }
        }
        catch
        {
            // Mutex 创建失败（权限/命名冲突）时按"首实例"处理，宁可多开一个窗口也别打不开
            IsFirstInstance = true;
        }
    }

    /// <summary>首实例：写 pid 文件并开始监听激活请求。</summary>
    public void StartServer(Action<IReadOnlyList<string>> onActivate, DispatcherQueue dispatcher)
    {
        try
        {
            Directory.CreateDirectory(RootDir);
            File.WriteAllText(ActivePidPath, ProcessId.ToString());
        }
        catch
        {
            // pid 文件写不了不影响主流程，只是第二个实例无法转发
        }

        _ = Task.Run(async () =>
        {
            var consecutiveFailures = 0;

            while (true)
            {
                NamedPipeServerStream? server = null;
                try
                {
                    server = new NamedPipeServerStream(
                        _pipeName, PipeDirection.In, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

                    await server.WaitForConnectionAsync().ConfigureAwait(false);
                    consecutiveFailures = 0;

                    using (server)
                    using (var reader = new BinaryReader(server, Encoding.UTF8, leaveOpen: true))
                    {
                        var count = reader.ReadInt32();
                        if (count < 0 || count > 100_000) continue;

                        var paths = new List<string>(count);
                        for (var i = 0; i < count; i++) paths.Add(reader.ReadString());

                        dispatcher.TryEnqueue(() => onActivate(paths));
                    }
                }
                catch (Exception ex)
                {
                    // 管道断开/读失败：继续等下一条连接，不退出监听
                    try { server?.Dispose(); } catch { }

                    // 🔧 P2-12：必须退避。管道名被占或权限不足时，while(true) 会空转吃满一个核
                    consecutiveFailures++;
                    AppLog.Error($"激活管道第 {consecutiveFailures} 次失败（退避 {Math.Min(250 * consecutiveFailures, 4000)}ms）", ex);
                    await Task.Delay(Math.Min(250 * consecutiveFailures, 4000)).ConfigureAwait(false);
                }
            }
        });
    }

    /// <summary>
    /// 第二实例：把路径转发给已开着的窗口。成功返回 true，调用方应随即退出。
    /// 允许空列表——那表示"无参启动"，语义只是把已有窗口拉到前台。
    /// </summary>
    public bool TrySendToFirstInstance(IReadOnlyList<string> paths)
    {
        try
        {
            if (!File.Exists(ActivePidPath)) return false;
            if (!int.TryParse(File.ReadAllText(ActivePidPath).Trim(), out var firstPid)) return false;
            if (firstPid == ProcessId) return false;

            using var client = new NamedPipeClientStream(
                ".", $"BatchRename.Activate.{firstPid}", PipeDirection.Out, PipeOptions.Asynchronous);

            client.Connect(ConnectTimeoutMs);

            // 授权首实例把窗口拉到前台（否则 SetForegroundWindow 会被前台锁拒绝）
            try { AllowSetForegroundWindow(firstPid); } catch { }

            using var writer = new BinaryWriter(client, Encoding.UTF8, leaveOpen: true);
            writer.Write(paths.Count);
            foreach (var p in paths) writer.Write(p);
            writer.Flush();

            return true;
        }
        catch
        {
            // 超时/管道不存在 → 退化为"新开窗口"，不当作失败
            return false;
        }
    }

    public void Dispose()
    {
        try
        {
            if (IsFirstInstance && File.Exists(ActivePidPath) &&
                File.ReadAllText(ActivePidPath).Trim() == ProcessId.ToString())
            {
                File.Delete(ActivePidPath);
            }
        }
        catch { }

        _mutex?.ReleaseMutex();
        _mutex?.Dispose();
    }
}
