using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Media;
using System.Globalization;

namespace PatchDiff;

/// <summary>Diff kind → background tint for the previous column.</summary>
public sealed class KindToBrushConverter : IValueConverter
{
    static readonly IBrush Deleted = new SolidColorBrush(Color.FromRgb(0x3a, 0x1e, 0x1e));
    static readonly IBrush Modified = new SolidColorBrush(Color.FromRgb(0x3a, 0x2f, 0x1e));
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is string s ? s switch
        {
            "Deleted" => Deleted,
            "Modified" => Modified,
            _ => null,
        } : null;
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Diff kind → background tint for the new column.</summary>
public sealed class KindToNewBrushConverter : IValueConverter
{
    static readonly IBrush Inserted = new SolidColorBrush(Color.FromRgb(0x1e, 0x3a, 0x2a));
    static readonly IBrush Modified = new SolidColorBrush(Color.FromRgb(0x3a, 0x2f, 0x1e));
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is string s ? s switch
        {
            "Inserted" => Inserted,
            "Modified" => Modified,
            _ => null,
        } : null;
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Non-empty string → true (badge visibility).</summary>
public sealed class NotEmptyToBoolConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is string s && s.Length > 0;
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
