using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace BatchRename.App;

/// <summary>
/// Shell 扩展通过 stdin 传来的路径集合。
/// C++ 侧写的是 UTF-16LE 的 wchar_t 原始字节，每条路径后跟一个 L'\0'，
/// 所以这里必须用 Encoding.Unicode 读、用 \0 切分（不是按换行切）。
/// </summary>
public sealed class ShellPayload
{
    public IReadOnlyList<string> Paths { get; }

    /// <summary>是否由 Shell 右键菜单拉起（false = 直接双击 exe / 拖放模式）</summary>
    public bool FromShell => Paths.Count > 0;

    private ShellPayload(IReadOnlyList<string> paths) => Paths = paths;

    public static ShellPayload Empty { get; } = new(Array.Empty<string>());

    /// <summary>
    /// 两条入口按优先级取：
    /// ① Shell 扩展（右键菜单）：stdin 传 UTF-16LE + \0 分隔；
    /// ② SendTo 快捷方式 / 直接拖到 exe：命令行参数。
    /// 都没有 → 无参启动（拖放模式，M5 后续）。
    /// </summary>
    public static ShellPayload Read(string[]? args = null)
    {
        var fromStdin = ReadFromStdIn();
        if (fromStdin.Paths.Count > 0) return fromStdin;

        // GetCommandLineArgs()[0] 是 exe 自身路径，跳过；
        // "--xxx" 开头的留给未来的开关（如 --undo-last），不当路径
        var argv = args ?? Environment.GetCommandLineArgs().Skip(1).ToArray();

        var list = new List<string>();
        foreach (var a in argv)
        {
            if (string.IsNullOrWhiteSpace(a)) continue;
            if (a.StartsWith("--", StringComparison.Ordinal)) continue;

            // SendTo 传的是裸路径；去掉可能的引号
            var p = a.Trim('"');
            if (File.Exists(p) || Directory.Exists(p)) list.Add(p);
        }

        return list.Count > 0 ? new ShellPayload(list) : Empty;
    }

    public static ShellPayload ReadFromStdIn()
    {
        try
        {
            using var stdin = Console.OpenStandardInput();
            using var reader = new StreamReader(stdin, Encoding.Unicode);

            var text = reader.ReadToEnd();
            if (string.IsNullOrEmpty(text)) return Empty;

            var list = new List<string>();
            foreach (var part in text.Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                var p = part.Trim('\r', '\n');
                if (p.Length > 0) list.Add(p);
            }
            return new ShellPayload(list);
        }
        catch
        {
            // stdin 读失败不致命：退化成"无参启动"
            return Empty;
        }
    }
}
