using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace BatchRename.App;

/// <summary>
/// bool → Visibility（true = Visible）。
/// 传 parameter="invert" 时反过来（true = Collapsed），给"二选一"的两个图层用。
/// </summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var flag = value is true;
        if (parameter is string s && s.Equals("invert", StringComparison.OrdinalIgnoreCase))
            flag = !flag;
        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => value is Visibility.Visible;
}
