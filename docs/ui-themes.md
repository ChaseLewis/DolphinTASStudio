# UI themes

Open **Config → Interface → UI theme** (or **Options → Configuration → Interface**).
Choose **Light**, **Dark**, or **System**. System is the default for both new settings
and existing settings files that have no theme preference.

Changes apply immediately to the main window, dialogs and floating panels and are
saved as the application's `Theme` preference in `settings.json`. The Interface tab
also works before opening a project. Its Close button does not undo the preference;
project emulation tabs retain their separate Apply/Cancel behavior.

System delegates appearance selection to Avalonia's operating-system theme support
and follows changes while the application is running. Explicit Light or Dark stays
selected regardless of the system preference. This uses Avalonia's
[theme-variant mechanism](https://docs.avaloniaui.net/docs/styling/theme-variants).

Theme changes do not alter project input, emulator configuration, game rendering,
save states or checkpoint validity. The game viewport remains black where no game
image is drawn. Only application UI is themed.

## Editing the embedded palette

Edit [palettes.json](../src/TasStudio.App/Themes/palettes.json), then rebuild Studio.
The `.csproj` embeds this file as `TasStudio.App.Themes.palettes.json`; deployment
does not need a loose theme file beside the executable. Editing a file next to an
already built app does not hot-reload it.

The file has three sections:

- **Light:** white surfaces, cool grey borders/backdrops, light blue accents.
- **Dark:** neutral charcoal/black surfaces, grey borders, white accents.
- **Shared:** stable timeline/data colors and game/position indicators.

Colors use `#RRGGBB` or `#AARRGGBB` notation. Each theme combines its own section
with Shared. Every `ThemeColor` key must appear exactly once in that combination;
missing, duplicate or unknown keys fail palette loading explicitly. Keep Shared
keys out of Light/Dark to preserve the same data meaning in both themes.

The most useful appearance keys are `Window`, `Panel`, `Header`, `Border`, `Text`,
`Muted`, `Icon`, `Accent`, `Primary`, `PrimaryText`, `Selection`, `Input`, and `Guide`.
`Accent` feeds Fluent's checkbox/slider/focus palette. `Primary` and `PrimaryText`
style the advance button and active Dock headers.
The bundled palettes use the same blue for both. Header and surface colors also feed the Fluent palette,
which supplies standard controls' hover, pressed and disabled states. Maintain
contrast when changing any foreground/background pair.

`TimelineBackground` and `TimelineGrid` change with the theme. Blue active input,
purple non-neutral input, cyan cursor, gold preview/state markers, lavender tags,
automatic-state markers and invalid-state colors stay in Shared. Clip text has a
separate shared foreground so it remains readable over colored input blocks.

[StudioTheme.cs](../src/TasStudio.App/StudioTheme.cs) loads/validates the embedded
palette and bridges it to Fluent and custom controls. Existing controls share
mutable brushes; changing theme does not rebuild the workspace or replace input
controls. Custom timeline/stick drawings request a new render when colors change
and unsubscribe when detached.

## Verification

`ThemeTests` covers preference migration/persistence, save failure, configuration
access without a project, changing existing and floating windows, readable checked
glyphs, stable timeline colors, and retaining inputs/valid states. To capture the
real UI with a test emulator:

```powershell
$env:TASSTUDIO_THEME_CAPTURE_DIR = Join-Path (Get-Location) 'artifacts/themes'
dotnet test tests/TasStudio.Core.Tests -c Release --filter FullyQualifiedName~ThemeTests
```

These captures use a fake emulator, not a running game. They verify UI appearance;
native operating-system appearance notifications still depend on Avalonia and the
host platform. System is represented by `ThemeVariant.Default`, not a saved copy of
the OS theme at launch.
