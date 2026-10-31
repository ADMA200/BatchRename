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
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // 规格书 §4.2：先解析 stdin，再建窗口，避免闪一个空窗
        var payload = ShellPayload.ReadFromStdIn();

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
