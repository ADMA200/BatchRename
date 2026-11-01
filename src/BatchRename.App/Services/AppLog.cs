using System.IO;
using System.Text;

namespace BatchRename.App.Services;

/// <summary>
/// App 进程侧的极简日志：只在异常与关键节点写，1MB 循环。
///
/// 之前 App 侧**一行日志都没有**——M3 的几个真实故障（静默覆盖、0x80070002、
/// File.Exists 误判）全靠肉眼复现。无常驻、低频的工具，日志的价值就是把
/// "用户说它没反应"从猜测变成一行时间戳。
///
/// 位置：%LOCALAPPDATA%\BatchRename\logs\app.log
/// （这里没有包容器，不像 Shell 侧会被文件虚拟化重定向）
/// </summary>
public static class AppLog
{
    private const long MaxBytes = 1024 * 1024;

    private static readonly object Gate = new();

    public static string LogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BatchRename", "logs", "app.log");

    public static void Info(string message) => Write("INFO ", message);

    public static void Error(string context, Exception? ex = null)
    {
        var text = ex is null ? context : $"{context} :: {ex.GetType().Name}: {ex.Message}";
        if (ex?.StackTrace is { Length: > 0 } st) text += Environment.NewLine + st;
        Write("ERROR", text);
    }

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                var path = LogPath;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                RollIfNeeded(path);

                var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}";
                File.AppendAllText(path, line, Encoding.UTF8);
            }
        }
        catch
        {
            // 日志写不进去绝不影响主流程
        }
    }

    private static void RollIfNeeded(string path)
    {
        var fi = new FileInfo(path);
        if (!fi.Exists || fi.Length < MaxBytes) return;

        var old = path + ".1";
        try
        {
            if (File.Exists(old)) File.Delete(old);
            File.Move(path, old);
        }
        catch
        {
            // 滚不动就继续往后写
        }
    }
}
