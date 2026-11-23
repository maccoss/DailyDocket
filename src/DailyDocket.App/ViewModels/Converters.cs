using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace DailyDocket.App.ViewModels;

public static class Converters
{
    /// <summary>Visible when the value is a non-empty string.</summary>
    public static readonly IValueConverter NonEmptyToVisibility = new NonEmpty();

    private sealed class NonEmpty : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            value is string { Length: > 0 } ? Visibility.Visible : Visibility.Collapsed;

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
