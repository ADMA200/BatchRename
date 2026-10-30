using System.Text;
using System.Text.RegularExpressions;

namespace BatchRename.App.Core;

public enum RenameScope
{
    /// <summary>仅文件名（Stem）</summary>
    StemOnly,
    /// <summary>仅扩展名（不含前导点）</summary>
    ExtOnly,
    /// <summary>两者：Stem 与扩展名主体各跑一次同一条规则，绝不拼串</summary>
    Both
}

public enum CaseMode
{
    None,
    Upper,
    Lower,
    Title
}

/// <summary>
/// 规格书 §8.5 / §8.6。执行顺序固定：先匹配替换，再大小写转换。
/// </summary>
public sealed class RenameRule
{
    // §8.5 词边界：中文与数字不算边界。
    // 注意：单引号故意不在此列 —— 规格书要求 don't 不能变 Don'T，
    // 但原文又把 ' 写进了 WordSeps，自相矛盾。这里以行为要求为准。
    private static readonly char[] WordSeps = " \t-_.[](){}+&".ToCharArray();

    public string Find { get; set; } = "";
    public string Replace { get; set; } = "";
    public bool UseRegex { get; set; }
    public bool CaseSensitive { get; set; }
    public RenameScope Scope { get; set; } = RenameScope.Both;
    public CaseMode CaseMode { get; set; } = CaseMode.None;

    /// <summary>
    /// 编译正则。返回 null + error 表示非法正则（供 UI 显示横幅，绝不能冒泡成未处理异常）。
    /// 非正则模式下返回 null 且 error 为 null。
    /// </summary>
    public Regex? Compile(out string? error)
    {
        error = null;
        if (!UseRegex || string.IsNullOrEmpty(Find)) return null;

        var opts = RegexOptions.CultureInvariant
                 | (CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase);

        // 首选非回溯引擎：天然免疫灾难性回溯
        try { return new Regex(Find, opts | RegexOptions.NonBacktracking); }
        catch (NotSupportedException) { /* 用到 lookaround / backreference，静默回退 */ }
        catch (ArgumentException ex) { error = ex.Message; return null; }

        // 回退到回溯引擎，必须带超时
        try { return new Regex(Find, opts, TimeSpan.FromMilliseconds(250)); }
        catch (ArgumentException ex) { error = ex.Message; return null; }
    }

    /// <summary>
    /// 对一段文本应用规则。返回替换+大小写后的结果；Error 非空表示该项匹配超时。
    /// </summary>
    public (string Result, string? Error) Apply(string input, Regex? regex)
    {
        if (string.IsNullOrEmpty(input)) return (input, null);

        string replaced;
        if (UseRegex)
        {
            if (regex is null) return (ConvertCase(input), null);
            try
            {
                replaced = regex.Replace(input, Replace);
            }
            catch (RegexMatchTimeoutException)
            {
                return (input, "正则匹配超时");
            }
        }
        else
        {
            replaced = ReplaceText(input, Find, Replace, CaseSensitive);
        }

        return (ConvertCase(replaced), null);
    }

    /// <summary>
    /// 规格书 §8.6。string.Replace 没有带比较器的重载，必须自己写，
    /// 否则"区分大小写"开关形同虚设。
    /// </summary>
    internal static string ReplaceText(string input, string needle, string replacement, bool caseSensitive)
    {
        if (string.IsNullOrEmpty(needle)) return input;

        var cmp = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var sb = new StringBuilder(input.Length);
        var pos = 0;

        while (true)
        {
            var hit = input.IndexOf(needle, pos, cmp);
            if (hit < 0)
            {
                sb.Append(input, pos, input.Length - pos);
                break;
            }
            sb.Append(input, pos, hit - pos).Append(replacement);
            pos = hit + needle.Length;   // ★ 按命中长度前进，避免替换词含查找词时死循环
            if (pos > input.Length) break;
        }
        return sb.ToString();
    }

    public string ConvertCase(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;

        return CaseMode switch
        {
            // ★ 必须 Invariant：用 ToUpper() 在土耳其语下 fileIndex → FİLEİNDEX
            CaseMode.Upper => s.ToUpperInvariant(),
            CaseMode.Lower => s.ToLowerInvariant(),
            CaseMode.Title => TitleCase(s),
            _ => s
        };
    }

    /// <summary>
    /// 规格书 §8.5。不要用 TextInfo.ToTitleCase：它会把 fileName 变成 Filename、don't 变成 Don'T。
    /// </summary>
    internal static string TitleCase(string s)
    {
        var chars = s.ToLowerInvariant().ToCharArray();
        var atWordStart = true;

        for (var i = 0; i < chars.Length; i++)
        {
            if (Array.IndexOf(WordSeps, chars[i]) >= 0)
            {
                atWordStart = true;
                continue;
            }

            if (atWordStart && char.IsLetterOrDigit(chars[i]))
            {
                chars[i] = char.ToUpperInvariant(chars[i]);
                atWordStart = false;
            }
            else if (char.IsLetterOrDigit(chars[i]))
            {
                atWordStart = false;
            }
            // 其它符号（如 ' ）不改变词状态：don't 的 t 不该被当成词首
        }
        return new string(chars);
    }
}
