using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using TasStudio.Emulation;

namespace TasStudio.App;

public sealed partial class MainWindow
{
    private Control _manualWorkspace = null!;
    private readonly Image _manualViewport = new() { Stretch = Stretch.Fill };
    private readonly TextBlock _manualPosition = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _manualPlayLabel = new() { Text = "Play" };

    private Control BuildPlayWorkspace()
    {
        var root = new Grid { Name = "PlayWorkspace", RowDefinitions = new("Auto,*,Auto"), IsVisible = false };
        bool Playing() => _execution.IsManualPlay;
        var actions = new WrapPanel { Margin = new Thickness(12, 4) };
        var play = ActionButton("Play", ToggleRun, Playing); play.Name = "ManualPlay"; play.Content = _manualPlayLabel;
        actions.Children.Add(play);
        actions.Children.Add(ActionButton("Save state…  Ctrl+S", SaveStateFile, Playing));
        actions.Children.Add(ActionButton("Load state…", LoadStateFile, Playing));
        actions.Children.Add(ActionButton("Memory cards…", ManagePlayMemoryCards, Playing));
        actions.Children.Add(ActionButton("Stop game", StopPlay, Playing));
        actions.Children.Add(_manualPosition); root.Children.Add(actions);
        var display = new Viewbox { Stretch = Stretch.Uniform, Child = new Border { Width = GameCubeDisplayWidth, Height = GameCubeDisplayHeight, Child = _manualViewport } };
        var game = new Border { Background = StudioTheme.Brush(ThemeColor.GameBackground), Child = display };
        Grid.SetRow(game, 1); root.Children.Add(game);
        var help = new TextBlock { Margin = new Thickness(12, 8), TextWrapping = TextWrapping.Wrap,
            Text = "PLAY MODE  ·  Ctrl+P play/pause  ·  F11 advance  ·  Shift+F1–F8 save slot / F1–F8 load  ·  Alt+Enter fullscreen\nUse Controllers to map your keyboard or gamepad. Save in-game to Slot A; save named states for your automation fixtures." };
        Grid.SetRow(help, 2); root.Children.Add(help);
        return root;
    }

    private async Task StopPlay()
    {
        await _execution.StopAsync(); _audio.Flush();
        _manualViewport.Source = null;
        await ShowProjects();
    }

    private async Task RememberPlayConfiguration()
    {
        _settings.PlayConfiguration = (await _execution.GetConfigurationAsync())!.Options.Configuration ?? new();
        _settings.Save();
    }

    private async Task ManagePlayMemoryCards()
    {
        await PausePlayback();
        var options = (await _execution.GetConfigurationAsync())!.Options;
        var dialog = new Window { Title = "Play memory cards · Slot A", Width = 680, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var region = new ComboBox { ItemsSource = PlayMemoryCards.Regions, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        var location = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap };
        var message = new TextBlock { TextWrapping = TextWrapping.Wrap };
        void UpdatePath() => location.Text = PlayMemoryCards.CardPath(options, (string)region.SelectedItem!);
        region.SelectionChanged += (_, _) => UpdatePath(); UpdatePath();
        var import = new Button { Content = "Import card copy & restart…" };
        var export = new Button { Content = "Export card…" };
        var close = new Button { Content = "Close" }; close.Click += (_, _) => dialog.Close();
        var working = false;
        dialog.Closing += (_, e) => e.Cancel = working;
        var type = new FilePickerFileType("GameCube raw memory card") { Patterns = ["*.raw"] };
        async Task Transfer(bool importing)
        {
            if (working) return;
            working = true; import.IsEnabled = export.IsEnabled = close.IsEnabled = region.IsEnabled = false;
            try
            {
                var selectedRegion = (string)region.SelectedItem!;
                if (importing)
                {
                    var files = await dialog.StorageProvider.OpenFilePickerAsync(new() { Title = "Import a raw card copy and restart the game", AllowMultiple = false, FileTypeFilter = [type] });
                    if (files.FirstOrDefault()?.TryGetLocalPath() is not { } path) return;
                    await WithGameLoading(() => _execution.ImportPlayMemoryCardAsync(selectedRegion, path), dialog);
                    await RememberPlayConfiguration();
                    options = (await _execution.GetConfigurationAsync())!.Options; UpdatePath();
                    message.Text = "Imported a copy and restarted the game. Play is paused.";
                }
                else
                {
                    var file = await dialog.StorageProvider.SaveFilePickerAsync(new() { Title = "Export raw memory card", FileTypeChoices = [type], SuggestedFileName = $"MemoryCardA.{selectedRegion}.raw", DefaultExtension = "raw", ShowOverwritePrompt = true });
                    if (file?.TryGetLocalPath() is not { } path) return;
                    await WithGameLoading(() => _execution.ExportPlayMemoryCardAsync(selectedRegion, path), dialog);
                    message.Text = "Card exported. Play remains paused at the same position.";
                }
            }
            catch (Exception ex) { message.Text = ex.Message; }
            finally { working = false; import.IsEnabled = export.IsEnabled = close.IsEnabled = region.IsEnabled = true; }
        }
        import.Click += async (_, _) => await Transfer(true);
        export.Click += async (_, _) => await Transfer(false);
        dialog.Content = new StackPanel { Margin = new Thickness(22), Spacing = 14, Children =
        {
            new TextBlock { Text = "Persistent memory cards", FontSize = 22 },
            new TextBlock { Text = "Choose your game's region. In-game saves persist in Slot A. Import copies a 59–2043 block .raw card and restarts the game; any replaced card is kept as a .bak file. Export flushes pending writes and restores your play position.", TextWrapping = TextWrapping.Wrap },
            region, location, Row(import, export),
            new TextBlock { Text = "Loading a save state also rewinds the memory card. Export a card before restoring older progress if you want to keep both versions. Create a TAS project from a .tasstate to use it in experiments.", TextWrapping = TextWrapping.Wrap },
            message, close
        } };
        _dialogOpen = true;
        try { await dialog.ShowDialog(this); }
        finally { _dialogOpen = false; }
    }
}
