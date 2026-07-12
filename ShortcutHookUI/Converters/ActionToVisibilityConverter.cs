using System;
using System.Windows;
using System.Windows.Data;
using ShortcutHookUI.Models;

namespace ShortcutHookUI.Converters;

public class ActionToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
    {
        if (value is ActionKind action && parameter is string param)
        {
            if (param == "OpenFileOrFolder")
                return (action == ActionKind.OpenFile || action == ActionKind.OpenFolder) ? Visibility.Visible : Visibility.Collapsed;

            if (Enum.TryParse<ActionKind>(param, out var targetAction))
                return action == targetAction ? Visibility.Visible : Visibility.Collapsed;
        }
        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => throw new NotImplementedException();
}
