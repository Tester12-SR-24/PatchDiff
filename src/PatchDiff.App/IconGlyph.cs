using System.Windows;

namespace PatchDiff;

/// <summary>
/// Attached property for button icons: set the icon geometry on any Button and the
/// implicit button template (Themes/Dark.xaml) renders it before the text.
///   &lt;Button ctl:IconGlyph.Geometry="{StaticResource IconAdd}" Content="Add DLL"/&gt;
/// Geometry resources live in Themes/Icons.xaml (24x24 grid, stroke design — the
/// template strokes them 1.8px with round caps and binds their color to the
/// button's Foreground, so hover/accent states color the icon for free).
/// Unset = no icon, button renders exactly as before.
/// </summary>
public static class IconGlyph
{
    public static readonly DependencyProperty GeometryProperty =
        DependencyProperty.RegisterAttached(
            "Geometry",
            typeof(System.Windows.Media.Geometry),
            typeof(IconGlyph),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static void SetGeometry(DependencyObject d, System.Windows.Media.Geometry? value) =>
        d.SetValue(GeometryProperty, value);

    public static System.Windows.Media.Geometry? GetGeometry(DependencyObject d) =>
        (System.Windows.Media.Geometry?)d.GetValue(GeometryProperty);
}
