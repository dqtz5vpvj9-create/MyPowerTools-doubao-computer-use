using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace DoubaoComputerUse.Desktop.Converters;

public sealed class StateBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool boolean)
        {
            return boolean
                ? new SolidColorBrush(Color.FromRgb(220, 38, 38))
                : new SolidColorBrush(Color.FromRgb(22, 163, 74));
        }

        var state = value?.ToString()?.ToLowerInvariant();
        return state switch
        {
            "online" or "ready" => new SolidColorBrush(Color.FromRgb(22, 163, 74)),
            "starting" or "degraded" => new SolidColorBrush(Color.FromRgb(202, 138, 4)),
            "offline" or "error" or "failed" or "unavailable" => new SolidColorBrush(Color.FromRgb(220, 38, 38)),
            _ => new SolidColorBrush(Color.FromRgb(100, 116, 139))
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
