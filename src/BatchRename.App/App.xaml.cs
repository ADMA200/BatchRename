using Microsoft.UI.Xaml;

namespace BatchRename.App;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // 规格书 §4.2：先解析 stdin，再建窗口，避免闪一个空窗
        var payload = ShellPayload.ReadFromStdIn();

        _window = new MainWindow(payload);
        _window.Activate();
    }
}
