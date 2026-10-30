namespace BatchRename.App.Core;

/// <summary>
/// 规格书 §8.2。只切最后一层扩展名；目录永不切。
/// </summary>
public static class NameSplitter
{
    public static (string Stem, string Ext) Split(string name, bool isDirectory)
    {
        if (isDirectory) return (name, "");

        var i = name.LastIndexOf('.');
        if (i <= 0) return (name, "");   // 无点，或 i==0 是 dotfile（.gitignore）

        return (name[..i], name[i..]);   // Ext 含前导点
    }
}
