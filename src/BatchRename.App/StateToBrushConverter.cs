using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using BatchRename.App.Models;

namespace BatchRename.App;

/// <summary>把 ItemState 映射成前景色。M2 只区分红/黄/灰/正常。</summary>
public sealed class StateToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Invalid = new(Color.FromArgb(255, 232, 106, 106));
    private static readonly SolidColorBrush Warning = new(Color.FromArgb(255, 224, 178, 84));
    private static readonly SolidColorBrush Unchanged = new(Color.FromArgb(255, 150, 150, 150));
    private static readonly SolidColorBrush Ok = new(Color.FromArgb(255, 132, 200, 132));

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        return value is ItemState state
            ? state switch
            {
                ItemState.Invalid or ItemState.ConflictInBatch or ItemState.ConflictOnDisk => Invalid,
                ItemState.Warning => Warning,
                ItemState.Unchanged => Unchanged,
                _ => Ok
            }
            : Unchanged;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
