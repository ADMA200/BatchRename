using BatchRename.App.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace BatchRename.App;

/// <summary>
/// ItemState → 前景色。
///
/// 颜色不在这里"取"：XAML 用 `{ThemeResource SystemFillColor*}` 把它们声明成窗口资源里的
/// 5 支 `SolidColorBrush`（MainWindow.xaml 的 RootGrid.Resources），转换器只按 key 拿现成对象，
/// 系统深浅色 / 高对比度切换时已绑定的行会跟着换值。
///
/// 两条替代方案都不行：代码查 `XamlControlsResources.ThemeDictionaries` 会按启动时的主题固化
/// （深色下取 "Light" 字典拿到的仍是深色值）；缓存画刷则会让切主题后的旧值被钉死。
/// 兜底色只在资源缺失（自定义主题 / 旧 SDK）时使用。
/// </summary>
public sealed class StateToBrushConverter : IValueConverter
{
    /// <summary>
    /// 由 MainWindow 注入：按 key 从窗口资源里取已由 XAML 解析好主题的画刷。
    /// 为 null 时走兜底。
    /// </summary>
    public static Func<string, Brush?>? Resolver { get; set; }

    /// <summary>状态 → 资源 key + 兜底色（深色 Fluent 的近似值）。</summary>
    private static readonly Dictionary<ItemState, (string Key, Color Fallback)> Styles = new()
    {
        [ItemState.Unchanged] = ("RowStateSecondary", Color.FromArgb(255, 197, 197, 197)),
        [ItemState.Ok] = ("RowStateSuccess", Color.FromArgb(255, 108, 203, 95)),
        [ItemState.Warning] = ("RowStateCaution", Color.FromArgb(255, 242, 203, 122)),
        [ItemState.Invalid] = ("RowStateCritical", Color.FromArgb(255, 255, 153, 164)),
        [ItemState.ConflictInBatch] = ("RowStateCritical", Color.FromArgb(255, 255, 153, 164)),
        [ItemState.ConflictOnDisk] = ("RowStateCritical", Color.FromArgb(255, 255, 153, 164)),
        [ItemState.Renamed] = ("RowStateAccent", Color.FromArgb(255, 153, 187, 255)),
    };

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var state = value is ItemState s ? s : ItemState.Unchanged;
        var (key, fallback) = Styles.TryGetValue(state, out var style)
            ? style
            : Styles[ItemState.Unchanged];

        try
        {
            if (Resolver?.Invoke(key) is { } themed) return themed;
        }
        catch
        {
            // 资源缺失（自定义主题 / 旧版 SDK）→ 用兜底色
        }

        return new SolidColorBrush(fallback);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
