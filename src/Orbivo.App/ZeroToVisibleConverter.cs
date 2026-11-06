using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Orbivo.App;

/// <summary>Visible for 0 (an empty list shows its hint), collapsed otherwise.</summary>
public sealed class ZeroToVisibleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
