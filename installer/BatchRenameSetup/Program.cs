using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace BatchRename.Setup;

/// <summary>
/// 自解压安装器：把内嵌的 payload.zip 解到 %LOCALAPPDATA%\BatchRename\app，
/// 再调用包里的 Install-Sparse.ps1 完成稀疏包注册。
///
/// 为什么不做成 MSIX：真 MSIX 需要签名证书，自签证书在目标机必须被信任，
/// 否则报 0x800B0109；对"拷一台机器过去双击就装好"的场景，那步比现在更麻烦。
/// </summary>
internal static class Program
{
    private const string AppName = "批量重命名";
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\BatchRename";
    private const string DevModeKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock";

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    private const uint MB_OK = 0x0;
    private const uint MB_YESNO = 0x4;
    private const uint MB_ICONERROR = 0x10;
    private const uint MB_ICONQUESTION = 0x20;
    private const uint MB_ICONINFORMATION = 0x40;

    [STAThread]
    private static int Main(string[] args)
    {
        // 文件夹选择对话框需要视觉样式，否则是老式外观
        System.Windows.Forms.Application.EnableVisualStyles();
        System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);

        // 静默模式：不弹任何对话框，日志写 %LOCALAPPDATA%\BatchRename\setup.log，用退出码表达成败。
        // 用于自动化验证与批处理部署。路径可用 --dir 指定。
        var silent = args.Any(a => a.Equals("/S", StringComparison.OrdinalIgnoreCase)
                                || a.Equals("--silent", StringComparison.OrdinalIgnoreCase));
        var logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BatchRename", "setup.log");

        void Say(string msg)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
                File.AppendAllText(logPath, $"[{DateTime.Now:HH:mm:ss}] {msg}{Environment.NewLine}");
            }
            catch { }
        }

        try
        {
            if (!DevModeEnabled())
            {
                Say("FAIL: 开发者模式未开启");
                if (!silent)
                {
                    MessageBoxW(IntPtr.Zero,
                        "需要先开启「开发者模式」才能安装。\n\n" +
                        "设置 > 系统 > 开发者选项 > 开发人员模式\n\n" +
                        "开启后再运行本安装程序。",
                        AppName + " 安装程序", MB_OK | MB_ICONERROR);
                }
                return 1;
            }

            var appRoot = GetTargetDirFromArgs(args) ?? DefaultRoot;

            if (!silent)
            {
                // 先让用户选目录（默认 %LOCALAPPDATA%\BatchRename\app）
                var picked = PickFolder(appRoot);
                if (picked is null) return 0;   // 用户取消
                appRoot = picked;

                var answer = MessageBoxW(IntPtr.Zero,
                    $"将安装到：\n{appRoot}\n\n" +
                    "安装后不要移动该目录（稀疏包按绝对路径注册）。\n\n继续？",
                    AppName + " 安装程序", MB_YESNO | MB_ICONQUESTION);

                if (answer != 6 /* IDYES */) return 0;
            }

            Say($"解压到 {appRoot}");
            var stage = Extract(appRoot);
            var installedRoot = FindPackageRoot(stage);

            var script = Path.Combine(installedRoot, "tools", "Install-Sparse.ps1");
            if (!File.Exists(script))
            {
                Say("FAIL: 安装包不完整，找不到 Install-Sparse.ps1");
                if (!silent)
                {
                    MessageBoxW(IntPtr.Zero, "安装包不完整：找不到 tools\\Install-Sparse.ps1",
                        AppName + " 安装程序", MB_OK | MB_ICONERROR);
                }
                return 1;
            }

            Say($"调用注册脚本 {script}");
            var (code, output) = RunPowerShell($"-NoProfile -ExecutionPolicy Bypass -File \"{script}\"");
            Say($"注册脚本退出码 {code}");
            if (code != 0) Say(Tail(output, 2000));

            if (code != 0)
            {
                if (!silent)
                {
                    MessageBoxW(IntPtr.Zero,
                        "安装失败（退出码 " + code + "）。\n\n" + Tail(output, 800),
                        AppName + " 安装程序", MB_OK | MB_ICONERROR);
                }
                return code;
            }

            WriteUninstallEntry(installedRoot);
            WriteUninstallCmd(installedRoot);
            Say("安装完成");

            if (!silent)
            {
                MessageBoxW(IntPtr.Zero,
                    "安装完成。\n\n" +
                    "用法：选中文件 > 右键 > 批量重命名\n" +
                    "若没看到：右键菜单最底部有「自定义菜单」按钮，可把扩展提到主菜单。\n\n" +
                    "兜底入口：右键 > 发送到 > 批量重命名",
                    AppName, MB_OK | MB_ICONINFORMATION);
            }

            return 0;
        }
        catch (Exception ex)
        {
            Say("EXCEPTION: " + ex);
            if (!silent)
            {
                MessageBoxW(IntPtr.Zero, "安装过程出错：\n\n" + ex.Message,
                    AppName + " 安装程序", MB_OK | MB_ICONERROR);
            }
            return 1;
        }
    }

    private static string DefaultRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BatchRename", "app");

    /// <summary>静默模式支持 --dir &lt;路径&gt;，便于装到指定位置。</summary>
    private static string? GetTargetDirFromArgs(string[] args)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals("--dir", StringComparison.OrdinalIgnoreCase))
            {
                var v = args[i + 1].Trim('"');
                return string.IsNullOrWhiteSpace(v) ? null : Path.GetFullPath(v);
            }
        }
        return null;
    }

    private static string? PickFolder(string initial)
    {
        try
        {
            using var dlg = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "选择安装位置（安装后请不要移动该目录）",
                UseDescriptionForTitle = true,
                ShowNewFolderButton = true
            };

            // InitialDirectory 为空时交给系统默认
            var start = Directory.Exists(initial)
                ? initial
                : Path.GetDirectoryName(initial) is { } parent && Directory.Exists(parent) ? parent : null;
            if (start is not null) dlg.InitialDirectory = start;

            return dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK ? dlg.SelectedPath : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool DevModeEnabled()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(DevModeKey);
            return key?.GetValue("AllowDevelopmentWithoutDevLicense") is int v && v == 1;
        }
        catch
        {
            // 读不到就不拦，让 Add-AppxPackage 自己报错更准确
            return true;
        }
    }

    private static string Extract(string appRoot)
    {
        if (Directory.Exists(appRoot)) Directory.Delete(appRoot, recursive: true);
        Directory.CreateDirectory(appRoot);

        using var res = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip")
                        ?? throw new InvalidOperationException("安装包损坏：找不到内嵌 payload");

        using var zip = new ZipArchive(res, ZipArchiveMode.Read);
        zip.ExtractToDirectory(appRoot, overwriteFiles: true);
        return appRoot;
    }

    /// <summary>payload 里有一层 BatchRename-yyyymmdd 目录，取出来当根。</summary>
    private static string FindPackageRoot(string stage)
    {
        var dirs = Directory.GetDirectories(stage);
        var withTools = dirs.FirstOrDefault(d => Directory.Exists(Path.Combine(d, "tools")));
        return withTools ?? stage;
    }

    private static (int Code, string Output) RunPowerShell(string args)
    {
        var psi = new ProcessStartInfo("powershell.exe", args)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        using var p = Process.Start(psi)!;
        var sb = new StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data is not null) sb.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data is not null) sb.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        p.WaitForExit(180_000);

        return (p.HasExited ? p.ExitCode : -1, sb.ToString());
    }

    private static void WriteUninstallEntry(string installRoot)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(UninstallKey);
            if (key is null) return;

            var uninstallCmd = Path.Combine(installRoot, "Uninstall.cmd");
            key.SetValue("DisplayName", AppName);
            key.SetValue("DisplayVersion", "1.0.0");
            key.SetValue("Publisher", "Local");
            key.SetValue("InstallLocation", installRoot);
            key.SetValue("UninstallString", $"\"{uninstallCmd}\"");
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        }
        catch
        {
            // 写不进去不影响安装本身
        }
    }

    private static void WriteUninstallCmd(string installRoot)
    {
        var path = Path.Combine(installRoot, "Uninstall.cmd");
        var body = "@echo off\r\n" +
                   "chcp 65001 >nul\r\n" +
                   "powershell -NoProfile -ExecutionPolicy Bypass -File \"%~dp0tools\\Uninstall.ps1\"\r\n" +
                   "reg delete \"HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\BatchRename\" /f >nul 2>&1\r\n" +
                   "echo.\r\n" +
                   "echo 已卸载。若要彻底清理，请手动删除本目录：%~dp0\r\n" +
                   "pause\r\n";
        File.WriteAllText(path, body, new UTF8Encoding(false));
    }

    private static string Tail(string s, int max)
    {
        s = s.Trim();
        return s.Length <= max ? s : "..." + s[^max..];
    }
}
