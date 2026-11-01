using BatchRename.App.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace BatchRename.App;

/// <summary>
/// ItemState → 前景色。
///
/// v1.6 重写要点：
/// ① 颜色不在这里"取"：由 XAML 用 `{ThemeResource SystemFillColor*}` 声明成 5 个
///    `SolidColorBrush`（MainWindow.xaml 的 RootGrid.Resources），转换器只按 key 拿现成对象。
/// ② 不能靠代码查主题字典：WinUI 3 的 `XamlControlsResources.ThemeDictionaries` 按启动时主题
///    固化，实测深色下取 "Light" 字典拿到的仍是深色值（TextFillColorSecondary 两个键都是
///    #C5FFFFFF）。而 XAML 侧 `ThemeResource` 会随系统深浅色/高对比度实时换值，
///    且换的是同一个 brush 实例的 Color，已绑定的行自动跟着变。
/// ③ 因此这里**不缓存**——缓存会在主题切换后把旧 brush 钉死，正是旧实现"切主题不生效"的根因之一。
/// ④ 兜底 RGB 只在 Resolver 未注入或资源缺失时使用，正常路径不会走到。
/// </summary>
public sealed class StateToBrushConverter : IValueConverter
{
    /// <summary>
    /// 由 MainWindow 注入：按 key 从窗口资源里取已由 XAML 解析好主题的画刷。
    /// 为 null 时走兜底。
    /// </summary>
    public static Func<string, Brush?>? Resolver { get; set; }

    /// <summary>状态 → 窗口资源里的 key。</summary>
    public static string KeyFor(ItemState state) => state switch
    {
        ItemState.Invalid or ItemState.ConflictInBatch or ItemState.ConflictOnDisk => "RowStateCritical",
        ItemState.Warning => "RowStateCaution",
        ItemState.Unchanged => "RowStateSecondary",
        ItemState.Renamed => "RowStateAccent",
        _ => "RowStateSuccess"
    };

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var state = value is ItemState s ? s : ItemState.Unchanged;

        try
        {
            if (Resolver?.Invoke(KeyFor(state)) is { } themed) return themed;
        }
        catch
        {
            // 资源缺失（自定义主题/旧版 SDK）→ 走兜底
        }

        return Fallback(state);
    }

    /// <summary>最后兜底：仅当主题画刷不可用时使用（深色 Fluent 的近似值）。</summary>
    private static Brush Fallback(ItemState state)
        => new SolidColorBrush(state switch
        {
            ItemState.Invalid or ItemState.ConflictInBatch or ItemState.ConflictOnDisk
                => Color.FromArgb(255, 255, 153, 164),
            ItemState.Warning => Color.FromArgb(255, 242, 203, 122),
            ItemState.Unchanged => Color.FromArgb(255, 197, 197, 197),
            ItemState.Renamed => Color.FromArgb(255, 153, 187, 255),
            _ => Color.FromArgb(255, 108, 203, 95)
        });

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
