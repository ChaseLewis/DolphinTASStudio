using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Path = Avalonia.Controls.Shapes.Path;

namespace TasStudio.App;

internal static class TurboCheckBox
{
    private static readonly IBrush Accent = StudioTheme.Brush(ThemeColor.Accent);
    private static readonly Geometry Plus = Geometry.Parse("M4,0 H6 V4 H10 V6 H6 V10 H4 V6 H0 V4 H4 Z");
    private static readonly Geometry Minus = Geometry.Parse("M0,4 H10 V6 H0 Z");

    public static void Configure(CheckBox check)
    {
        // Replace only Fluent's indicator glyph; preserve the checkbox's hit target and label width.
        check.Styles.Add(new Style(s => s.OfType<CheckBox>().Class("turbo").Template().OfType<Path>().Name("CheckGlyph"))
        {
            Setters =
            {
                new Setter(Path.DataProperty, Minus), new Setter(Visual.OpacityProperty, 1d),
                new Setter(Path.FillProperty, Accent), new Setter(Path.StretchProperty, Stretch.Uniform),
                new Setter(Layoutable.WidthProperty, 10d), new Setter(Layoutable.HeightProperty, 2d)
            }
        });
        check.Styles.Add(new Style(s => s.OfType<CheckBox>().Class("turbo-down").Template().OfType<Path>().Name("CheckGlyph"))
        {
            Setters = { new Setter(Path.DataProperty, Plus), new Setter(Layoutable.HeightProperty, 10d) }
        });
        check.Styles.Add(new Style(s => s.OfType<CheckBox>().Class("turbo").Template().OfType<Border>().Name("NormalRectangle"))
        {
            Setters = { new Setter(Border.BorderBrushProperty, Accent), new Setter(Border.BackgroundProperty, Brushes.Transparent) }
        });
    }

    public static void SetPhase(CheckBox check, bool enabled, bool down)
    {
        check.Classes.Set("turbo", enabled);
        check.Classes.Set("turbo-down", enabled && down);
    }
}
