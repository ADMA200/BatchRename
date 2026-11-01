using BatchRename.App.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace BatchRename.App;

/// <summary>
/// ItemState → 前景色。
///
/// 旧实现是一组硬编码的 RGB（深色主题下还行，浅色主题下绿/黄对比度不足）——
/// 现在优先取系统主题资源，取不到才退回固定色。缓存按 (状态, 是否深色) 分组，
/// 所以运行中切换系统主题需要重开窗口才会重算（旧实现连重算都不会）。
/// </summary>
public sealed class StateToBrushConverter : IValueConverter
{
    private static readonly Dictionary<(ItemState, bool), Brush> Cache = new();

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var state = value is ItemState s ? s : ItemState.Unchanged;
        var dark = Application.Current?.RequestedTheme == ApplicationTheme.Dark;

        if (Cache.TryGetValue((state, dark), out var cached)) return cached;

        var brush = Resolve(state, dark);
        Cache[(state, dark)] = brush;
        return brush;
    }

    private static Brush Resolve(ItemState state, bool dark)
    {
        var key = state switch
        {
            ItemState.Invalid or ItemState.ConflictInBatch or ItemState.ConflictOnDisk
                => "SystemFillColorCriticalBrush",
            ItemState.Warning => "SystemFillColorCautionBrush",
            ItemState.Unchanged => "TextFillColorSecondaryBrush",
            ItemState.Renamed => "AccentTextFillColorPrimaryBrush",
            _ => "SystemFillColorSuccessBrush"
        };

        try
        {
            if (Application.Current?.Resources is { } res &&
                res.TryGetValue(key, out var v) && v is Brush themed)
            {
                return themed;
            }
        }
        catch
        {
            // 主题资源缺失（自定义主题/旧版 SDK）时走兜底
        }

        return new SolidColorBrush(state switch
        {
            ItemState.Invalid or ItemState.ConflictInBatch or ItemState.ConflictOnDisk
                => dark ? Color.FromArgb(255, 232, 106, 106) : Color.FromArgb(255, 196, 43, 28),
            ItemState.Warning => dark ? Color.FromArgb(255, 224, 178, 84) : Color.FromArgb(255, 157, 93, 0),
            ItemState.Unchanged => dark ? Color.FromArgb(255, 150, 150, 150) : Color.FromArgb(255, 96, 96, 96),
            ItemState.Renamed => dark ? Color.FromArgb(255, 120, 175, 225) : Color.FromArgb(255, 0, 95, 184),
            _ => dark ? Color.FromArgb(255, 132, 200, 132) : Color.FromArgb(255, 15, 123, 15)
        });
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
