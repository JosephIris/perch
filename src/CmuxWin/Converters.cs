using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;

namespace CmuxWin;

/// True → Visible, false/null → Collapsed. Used by sidebar rows that should
/// only appear when a session has data to show (e.g. a notification).
internal sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => (value is bool b && b) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is Visibility v && v == Visibility.Visible;
}

/// Resolves a `{DynamicResource}` brush key (passed as a string) at binding
/// time. Lets a view-model expose a theme-keyed brush by name without
/// holding a strong reference to the Brush itself — handy for the
/// notification dot, whose color depends on level but should still pick up
/// theme changes.
internal sealed class ResourceKeyToBrushConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string key || key.Length == 0) return Brushes.Transparent;
        try
        {
            var found = System.Windows.Application.Current?.TryFindResource(key);
            return found as Brush ?? Brushes.Transparent;
        }
        catch { return Brushes.Transparent; }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
