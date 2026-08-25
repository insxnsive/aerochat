using System.Globalization;
using System.Windows.Data;

namespace Aerochat.Windows;

public sealed class TimeFormatConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is DateTimeOffset dateTime)
            return dateTime.ToString("h:mm tt", culture);
        if (value is DateTime date)
            return date.ToString("h:mm tt", culture);
        return value;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
