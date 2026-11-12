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

/// <summary>Diff row kind → text brush: changed lines bright, unchanged dimmed
/// so modified/inserted/deleted lines pop against the surrounding code.</summary>
public sealed class KindToTextBrushConverter : IValueConverter
{
    static readonly IBrush Bright = new SolidColorBrush(Color.FromRgb(0xe2, 0xe9, 0xf2));
    static readonly IBrush Dim = new SolidColorBrush(Color.FromRgb(0x9a, 0xa6, 0xb5));
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is string s && s != "Unchanged" ? Bright : Dim;
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Section change kind → accent stripe brush for the diff section header.</summary>
public sealed class SectionKindToBrushConverter : IValueConverter
{
    static readonly IBrush Added = new SolidColorBrush(Color.FromRgb(0x3d, 0xdc, 0x8a));
    static readonly IBrush Removed = new SolidColorBrush(Color.FromRgb(0xf2, 0x6d, 0x7a));
    static readonly IBrush Modified = new SolidColorBrush(Color.FromRgb(0xe5, 0xb5, 0x67));
    static readonly IBrush Unchanged = new SolidColorBrush(Color.FromRgb(0x2c, 0x36, 0x44));
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is string s ? s switch
        {
            "Added" => Added,
            "Removed" => Removed,
            "Modified" => Modified,
            _ => Unchanged,
        } : Unchanged;
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Watch pill kind → status dot brush (green/amber/red/grey).
/// Keeps the rail dot honest for ERROR/MISSING rows.</summary>
public sealed class PillKindToBrushConverter : IValueConverter
{
    static readonly IBrush Green = new SolidColorBrush(Color.FromRgb(0x62, 0xd9, 0x9d));
    static readonly IBrush Amber = new SolidColorBrush(Color.FromRgb(0xe5, 0xb5, 0x67));
    static readonly IBrush Red = new SolidColorBrush(Color.FromRgb(0xf0, 0x8e, 0x97));
    static readonly IBrush Grey = new SolidColorBrush(Color.FromRgb(0x8d, 0x99, 0xac));
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is string s ? s switch
        {
            "ok" => Green,
            "baseline" => Green,
            "updated" => Amber,
            "error" => Red,
            "busy" => Grey,
            _ => Grey,
        } : Grey;
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
