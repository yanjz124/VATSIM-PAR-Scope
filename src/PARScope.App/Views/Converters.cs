using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using PARScope.App.ViewModels;
using PARScope.Core.Par;
using PARScope.Core.Weather;

namespace PARScope.App.Views;

/// <summary>Maps status enums to the accent brushes defined in Theme.xaml.</summary>
public sealed class StatusBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        string key = value switch
        {
            LinkHealth.Fast => "StatusGoodBrush",
            LinkHealth.Slow => "StatusCautionBrush",
            LinkHealth.Error => "StatusBadBrush",
            LinkHealth.None => "StatusIdleBrush",
            AdvisoryLevel.Warning => "StatusBadBrush",
            AdvisoryLevel.Caution => "StatusCautionBrush",
            AdvisoryLevel.Normal => "StatusNeutralBrush",
            FlightCategory.Vfr => "CategoryVfrBrush",
            FlightCategory.Mvfr => "CategoryMvfrBrush",
            FlightCategory.Ifr => "CategoryIfrBrush",
            FlightCategory.Lifr => "CategoryLifrBrush",
            _ => "StatusIdleBrush",
        };
        return Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class RangeLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is double d ? $"{d:0} NM" : "";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class CategoryLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is FlightCategory c && c != FlightCategory.Unknown ? c.ToString().ToUpperInvariant() : "—";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class NotNullToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is null or "" ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Radio log line color: warnings (not understood, no reply) orange, approach results and grades green.</summary>
public sealed class RadioLogBrushConverter : IValueConverter
{
    private static readonly System.Windows.Media.Brush Warning = Frozen(0xFF, 0xB0, 0x20);
    private static readonly System.Windows.Media.Brush Result = Frozen(0x5C, 0xD6, 0x7A);

    private static System.Windows.Media.Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        // Binding.DoNothing keeps the theme's (inherited) text color for ordinary lines.
        value is not string s ? Binding.DoNothing
        : s.Contains('⚠') ? Warning
        : s.Contains(" RESULT: ") ? Result
        : Binding.DoNothing;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
}
