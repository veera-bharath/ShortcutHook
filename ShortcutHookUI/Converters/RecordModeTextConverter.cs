using System;
using System.Windows.Data;

namespace ShortcutHookUI.Converters;

public class RecordModeTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
    {
        return (value is bool b && b) ? "Aa" : "⌨";
    }

    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => throw new NotImplementedException();
}
