using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;

namespace TasStudio.App;

public sealed partial class MainWindow
{
    private readonly Button _updateNotice = new() { Content = "Update ready…", IsVisible = false, Padding = new Thickness(6, 1), FontSize = 11 };

    private void InitializeUpdates()
    {
        _updateNotice.Click += async (_, _) => await Perform(UpdateSettings);
        if (Program.Updates is not { } updates) return;
        void RefreshUpdateNotice() => Dispatcher.UIThread.Post(() => _updateNotice.IsVisible = updates.Enabled && updates.PendingVersion != null);
        updates.Changed += RefreshUpdateNotice;
        RefreshUpdateNotice();
        Opened += async (_, _) => { if (updates.Automatic) await updates.CheckAsync(); };
        Closed += (_, _) => { updates.Changed -= RefreshUpdateNotice; updates.Dispose(); };
    }

    private Task UpdateSettings() => UpdateSettingsCore(null);
    internal async Task UpdateSettingsCore(string? capturePath)
    {
        var updates = Program.Updates;
        var dialog = new Window { Title = "Updates — Dolphin TAS Studio", Width = 660, Height = 450,
            MinWidth = 540, MinHeight = 380, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = StudioTheme.Brush(ThemeColor.Window) };
        var status = SettingsNote(updates?.Status ?? "Updates are available in installed releases and release portable ZIPs.");
        var channels = new ComboBox { ItemsSource = Enum.GetValues<ReleaseChannel>(), SelectedItem = updates?.Channel ?? ReleaseChannel.Stable,
            HorizontalAlignment = HorizontalAlignment.Stretch };
        var automatic = new CheckBox { Content = "Automatically download updates and install when Studio closes", IsChecked = updates?.Automatic ?? true };
        var check = new Button { Content = "Check for updates" };
        var restart = new Button { Content = "Restart to update" };
        var close = new Button { Content = "Close" };
        var notes = new Button { Content = "Release notes" };
        notes.Click += (_, _) => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(AppUpdates.Repository + "/releases") { UseShellExecute = true });
        var page = new StackPanel { Margin = new Thickness(24), Spacing = 16, Children =
        {
            new TextBlock { Text = "Studio updates", FontSize = 25 },
            SettingsNote($"Current version: {updates?.CurrentVersion ?? "Development build"}"),
            SettingsRow("Release channel", channels), automatic,
            SettingsNote("Each channel receives its own newer releases. Switching channels never downgrades your installation."),
            status,
            SettingsNote("Settings, memory cards and saved files are kept. Before 1.0, updates may affect replay or save-state compatibility; check the release notes for known changes."),
            new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 10,
                Children = { notes, check, restart, close } }
        } };
        dialog.Content = new ScrollViewer { Content = page };
        void RefreshControls()
        {
            status.Text = updates?.Status ?? status.Text;
            channels.IsEnabled = automatic.IsEnabled = check.IsEnabled = updates is { Enabled: true, Busy: false };
            restart.IsEnabled = updates is { Enabled: true, Busy: false, PendingVersion: not null };
        }
        void Changed() => Dispatcher.UIThread.Post(RefreshControls);
        void Configure()
        {
            if (updates == null || channels.SelectedItem is not ReleaseChannel channel) return;
            try { updates.Configure(channel, automatic.IsChecked == true); }
            catch (Exception error) { status.Text = $"Update preferences could not be saved: {error.Message}"; }
        }
        channels.SelectionChanged += (_, _) => Configure();
        automatic.IsCheckedChanged += (_, _) => Configure();
        check.Click += async (_, _) => { if (updates != null) await updates.CheckAsync(); };
        restart.Click += (_, _) => dialog.Close(true);
        close.Click += (_, _) => dialog.Close(false);
        if (updates != null) updates.Changed += Changed;
        RefreshControls();
        if (capturePath != null) dialog.Opened += async (_, _) =>
        {
            await Task.Delay(300);
            using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize((int)dialog.Bounds.Width, (int)dialog.Bounds.Height));
            bitmap.Render(dialog); bitmap.Save(capturePath); dialog.Close(false);
        };
        _dialogOpen = true;
        bool restartRequested;
        try { restartRequested = await dialog.ShowDialog<bool>(this); }
        finally { _dialogOpen = false; if (updates != null) updates.Changed -= Changed; }
        if (restartRequested)
        {
            // Let Perform finish before entering the regular save-and-close path.
            Dispatcher.UIThread.Post(() => { Program.RestartForUpdate = true; Close(); }, DispatcherPriority.Background);
        }
    }
}
