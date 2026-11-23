using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace DailyDocket.App.ViewModels;

public static class Converters
{
    /// <summary>Visible when the value is a non-empty string.</summary>
    public static readonly IValueConverter NonEmptyToVisibility = new NonEmpty(whenEmpty: false);

    /// <summary>Visible when the value is null or an empty string: the reader's "select a thread" hint.</summary>
    public static readonly IValueConverter EmptyToVisibility = new NonEmpty(whenEmpty: true);

    /// <summary>Visible when the value is false: the reading view of a task that is not being edited.</summary>
    public static readonly IValueConverter FalseToVisible = new FalseVisible();

    private sealed class FalseVisible : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            value is true ? Visibility.Collapsed : Visibility.Visible;

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }

    private sealed class NonEmpty(bool whenEmpty) : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            (value is string { Length: > 0 }) != whenEmpty ? Visibility.Visible : Visibility.Collapsed;

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
