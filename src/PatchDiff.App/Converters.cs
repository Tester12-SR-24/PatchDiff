using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace PatchDiff;

/// <summary>
/// True (collection HasItems) → Collapsed; false → Visible. Used for empty-state hints.
/// </summary>
public sealed class CollapsedWhenHasItemsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Maps diff line kinds (Unchanged/Deleted/Inserted/Modified) to background brushes.
/// </summary>
public sealed class KindToBrushConverter : IValueConverter
{
    static readonly Brush Transparent = Brushes.Transparent;
    static readonly Brush Deleted = new SolidColorBrush(Color.FromRgb(0x3A, 0x21, 0x26));
    static readonly Brush Inserted = new SolidColorBrush(Color.FromRgb(0x22, 0x37, 0x1F));
    static readonly Brush Modified = new SolidColorBrush(Color.FromRgb(0x39, 0x30, 0x1A));

    static KindToBrushConverter()
    {
        Deleted.Freeze(); Inserted.Freeze(); Modified.Freeze();
    }

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value switch
        {
            "Deleted" or "Removed" => Deleted,
            "Inserted" or "Added" => Inserted,
            "Modified" => Modified,
            _ => Transparent,
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
