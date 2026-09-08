using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Dock.Avalonia.Controls;

namespace TasStudio.App;

public enum UiTheme { System, Light, Dark }

internal enum ThemeColor
{
    Window, Panel, Header, Border, Text, Muted, Icon, Accent, Primary, PrimaryText,
    Selection, SelectionBorder, Input, Guide, Error, TimelineBackground, TimelineGrid,
    TimelineActive, TimelineInput, TimelineCursor, TimelinePreview, TimelineInvalid,
    TimelineTag, TimelineTagText, TimelineSelection, TimelineAutomatic, TimelineClipText, TimelineCandidatePreview,
    ControllerDot, ControllerDotBorder, GameBackground, GamePlaceholder
}

/// <summary>Shared mutable brushes keep existing views in sync without rebuilding their controls.</summary>
internal static class StudioTheme
{
    private sealed record Palettes(Dictionary<string, string> Shared, Dictionary<string, string> Light, Dictionary<string, string> Dark);
    private static readonly Dictionary<UiTheme, Dictionary<ThemeColor, Color>> Colors = LoadPalettes();
    private static readonly Dictionary<ThemeColor, SolidColorBrush> Brushes = Colors[UiTheme.Light]
        .ToDictionary(pair => pair.Key, pair => new SolidColorBrush(pair.Value));
    internal static event Action? Changed;
    internal static IBrush Brush(ThemeColor key) => Brushes[key];
    internal static Color ColorFor(UiTheme theme, ThemeColor key) => Colors[theme][key];

    internal static FluentTheme CreateFluentTheme()
    {
        var fluent = new FluentTheme();
        foreach (var (theme, variant) in new[] { (UiTheme.Light, ThemeVariant.Light), (UiTheme.Dark, ThemeVariant.Dark) })
        {
            var colors = Colors[theme];
            fluent.Palettes[variant] = new ColorPaletteResources
            {
                Accent = colors[ThemeColor.Accent], RegionColor = colors[ThemeColor.Panel],
                BaseHigh = colors[ThemeColor.Text], BaseMedium = colors[ThemeColor.Muted],
                ChromeLow = colors[ThemeColor.Window], ChromeMedium = colors[ThemeColor.Panel],
                ChromeHigh = colors[ThemeColor.Header], ErrorText = colors[ThemeColor.Error]
            };
        }
        return fluent;
    }

    internal static void Initialize(Application app)
    {
        app.Styles.Add(new Style(selector => selector.Is<Window>())
        {
            Setters = { new Setter(Window.BackgroundProperty, Brush(ThemeColor.Window)),
                new Setter(Window.ForegroundProperty, Brush(ThemeColor.Text)) }
        });
        app.Styles.Add(new Style(selector => selector.Is<ToolChromeControl>().Class(":active"))
        {
            Setters = { new Setter(TemplatedControl.BackgroundProperty, Brush(ThemeColor.Primary)) }
        });
        // Fluent's default accent foreground assumes a saturated system accent.
        // Our dark accent is near-white, so its checked controls need dark text.
        foreach (var state in new[] { "Checked", "Indeterminate" })
        foreach (var interaction in new[] { "", "PointerOver", "Pressed", "Disabled" })
            app.Resources[$"CheckBoxCheckGlyphForeground{state}{interaction}"] = Brush(ThemeColor.PrimaryText);
        app.ActualThemeVariantChanged += (_, _) => Refresh(app.ActualThemeVariant);
        Refresh(app.ActualThemeVariant);
    }

    internal static ThemeVariant Variant(UiTheme theme) => theme switch
    {
        UiTheme.Light => ThemeVariant.Light,
        UiTheme.Dark => ThemeVariant.Dark,
        _ => ThemeVariant.Default
    };

    internal static void Apply(UiTheme theme)
    {
        if (Application.Current is not { } app) return;
        app.RequestedThemeVariant = Variant(theme);
        Refresh(app.ActualThemeVariant);
    }

    private static void Refresh(ThemeVariant variant)
    {
        var colors = Colors[variant == ThemeVariant.Dark ? UiTheme.Dark : UiTheme.Light];
        var changed = false;
        foreach (var (key, color) in colors)
        {
            if (Brushes[key].Color == color) continue;
            Brushes[key].Color = color; changed = true;
        }
        // Immediate-mode drawings must recreate cached text/geometry after a palette change.
        if (changed) Changed?.Invoke();
    }

    private static Dictionary<UiTheme, Dictionary<ThemeColor, Color>> LoadPalettes()
    {
        using var stream = typeof(StudioTheme).Assembly.GetManifestResourceStream("TasStudio.App.Themes.palettes.json")
            ?? throw new InvalidDataException("The embedded UI palette is missing.");
        var palettes = JsonSerializer.Deserialize<Palettes>(stream) ?? throw new InvalidDataException("The UI palette is empty.");
        Dictionary<ThemeColor, Color> Parse(Dictionary<string, string> values)
        {
            var result = new Dictionary<ThemeColor, Color>();
            foreach (var (name, value) in palettes.Shared.Concat(values))
            {
                if (!Enum.TryParse<ThemeColor>(name, out var key) || !Enum.IsDefined(key))
                    throw new InvalidDataException($"Unknown UI palette color: {name}.");
                if (!result.TryAdd(key, Color.Parse(value))) throw new InvalidDataException($"Duplicate UI palette color: {name}.");
            }
            foreach (var key in Enum.GetValues<ThemeColor>())
                if (!result.ContainsKey(key)) throw new InvalidDataException($"Missing UI palette color: {key}.");
            return result;
        }
        return new() { [UiTheme.Light] = Parse(palettes.Light), [UiTheme.Dark] = Parse(palettes.Dark) };
    }
}
