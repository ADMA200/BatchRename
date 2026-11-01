using System.Runtime.InteropServices;
using BatchRename.App.Services;
using Microsoft.UI.Xaml;

namespace BatchRename.App;

public partial class App : Application
{
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    private Window? _window;
    private SingleInstanceGate? _gate;

    public App()
    {
        InitializeComponent();

        // 🔧 P0-3：WinUI 3 没有内建崩溃对话框，不挂这三个钩子的话，
        // 任何未处理异常都表现为"进程静默消失"，连一行记录都没有。
        UnhandledException += (_, e) =>
            AppLog.Error("Application.UnhandledException", e.Exception);

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            AppLog.Error("AppDomain.UnhandledException", e.ExceptionObject as Exception);

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            AppLog.Error("TaskScheduler.UnobservedTaskException", e.Exception);
            e.SetObserved();
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // 规格书 §4.2：先解析 stdin，再建窗口，避免闪一个空窗
        var sw = System.Diagnostics.Stopwatch.StartNew();
        // 两条入口：右键菜单（stdin）与 SendTo（命令行参数），stdin 优先
        var payload = ShellPayload.Read();
        AppLog.Info($"[startup] paths={payload.Paths.Count} stdin={sw.ElapsedMilliseconds}ms pid={Environment.ProcessId}");

        _gate = new SingleInstanceGate();

        // 抢不到锁 = 已有窗口开着。把路径转发过去（无参启动则只是置前），然后自己退出
        if (!_gate.IsFirstInstance && _gate.TrySendToFirstInstance(payload.Paths))
        {
            Environment.Exit(0);
            return;
        }

        if (_gate.IsFirstInstance)
        {
            // Application 没有 DispatcherQueue 属性，从当前线程取
            var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            _gate.StartServer(OnActivatedFromSecondInstance, dispatcher);
        }

        _window = new MainWindow(payload);

        // 🔧 P2-14：关窗时清理 active.pid 并释放 mutex（以前 Dispose 没有任何调用点，
        // pid 文件永远残留）。Closed 在 UI 线程上触发，与创建 mutex 的线程一致，
        // 所以 ReleaseMutex 不会抛"不拥有互斥体"。
        _window.Closed += (_, _) => _gate?.Dispose();

        _window.Activate();
    }

    private void OnActivatedFromSecondInstance(IReadOnlyList<string> paths)
    {
        if (_window is not MainWindow w) return;

        // 空列表 = 第二实例只是无参启动，语义为"把已有窗口拉到前台"，不要清空列表
        if (paths.Count > 0) w.UpdateFromPaths(paths);

        w.Activate();
        BringToForeground(w);
    }

    private static void BringToForeground(Window window)
    {
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            SetForegroundWindow(hwnd);
        }
        catch
        {
            // 拿不到 hwnd 时 Activate() 已经尽力了
        }
    }
}