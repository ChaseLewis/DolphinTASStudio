using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using TasStudio.Emulation;

namespace TasStudio.App;

public sealed partial class MainWindow
{
    private Task ApplicationSettings() => ApplicationSettingsCore(null);
    private Task CheckpointSettings() => ApplicationSettingsCore(null, 4);
    internal async Task ApplicationSettingsCore(string? capturePath, int selectedTab = 0)
    {
        await _execution.PauseAsync(); _audio.Flush();
        var session = await _execution.GetConfigurationAsync();
        var options = session?.Options ?? BackendOptions();
        var original = (options.Configuration ?? EmulationConfiguration.FromLegacy(options.InternalResolution, options.DspHle)).ValidatedCopy();
        var policy = _execution.Checkpoints;
        var dialog = new Window { Title = "Configuration — Dolphin TAS Studio", Width = 830, Height = 720,
            MinWidth = 720, MinHeight = 500, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = StudioTheme.Brush(ThemeColor.Window) };
        var layout = new Grid { RowDefinitions = new("Auto,*,Auto,Auto"), Margin = new Thickness(22) };
        layout.Children.Add(new StackPanel { Spacing = 6, Margin = new Thickness(0,0,0,16), Children =
        {
            new TextBlock { Text = "Configuration", FontSize = 25, FontWeight = FontWeight.SemiBold },
            SettingsNote(_execution.HasProject ? $"{Path.GetFileName(_projectPath ?? "Untitled project")} · emulation and checkpoints are saved with this project" : "Open or create a project to edit emulation settings.")
        } });
        var editors = new Dictionary<string, ComboBox>();
        var pages = new Dictionary<string, StackPanel>();
        foreach (var group in new[] { "General", "Graphics", "Compatibility", "Audio" })
        {
            var page = pages[group] = SettingsPage();
            foreach (var setting in EmulationConfiguration.Settings.Where(s => s.Group == group))
            {
                var input = new ComboBox { ItemsSource = setting.Labels, SelectedIndex = Array.IndexOf(setting.Values, original.Value(setting.Key)), HorizontalAlignment = HorizontalAlignment.Stretch };
                editors.Add(setting.Key, input); page.Children.Add(SettingsRow(setting.Label, input));
            }
        }
        var utc = new TextBox { Text = DateTimeOffset.FromUnixTimeSeconds(original.StartUtcSeconds).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), Watermark = "yyyy-MM-dd HH:mm:ss" };
        pages["General"].Children.Insert(0, SettingsRow("Start date and time (UTC)", utc));
        pages["General"].Children.Insert(1, SettingsNote("UTC changes keep your recorded inputs. Apply restarts from boot with the new clock; optionally replay to the selected input."));
        pages["General"].Children.Add(SettingsRow("CPU execution", new TextBlock { Text = "Single core · required", VerticalAlignment = VerticalAlignment.Center }));
        pages["Graphics"].Children.Insert(0, SettingsRow("Backend", new TextBlock { Text = "Direct3D 11", VerticalAlignment = VerticalAlignment.Center }));
        pages["Compatibility"].Children.Insert(0, SettingsNote("These are project defaults. Dolphin's game compatibility overrides always take precedence. Changing these values restarts the project and invalidates existing states."));
        pages["Audio"].Children.Add(SettingsNote("Listening volume and mute are available on the toolbar. DSP settings affect emulation and are saved with this project."));
        var checkpointPage = SettingsPage();
        var enabled = new CheckBox { Content = "Create automatic checkpoints", IsChecked = policy.Enabled };
        var interval = new NumericUpDown { Minimum = 1, Maximum = 86400, Value = policy.IntervalSeconds, FormatString = "0" };
        var count = new NumericUpDown { Minimum = 1, Maximum = 10000, Value = policy.MaximumCount, FormatString = "0" };
        var budget = new NumericUpDown { Minimum = 1, Maximum = 1048576, Value = policy.DiskBudgetMiB, FormatString = "0" };
        var retention = new ComboBox { ItemsSource = new[] { "Remove oldest created", "Remove least recently used (LRU)" }, SelectedIndex = (int)policy.Retention, HorizontalAlignment = HorizontalAlignment.Stretch };
        checkpointPage.Children.Add(enabled);
        checkpointPage.Children.Add(SettingsRow("Interval (emulated seconds)", interval));
        checkpointPage.Children.Add(SettingsRow("Maximum checkpoint count", count));
        checkpointPage.Children.Add(SettingsRow("Disk budget (MiB)", budget));
        checkpointPage.Children.Add(SettingsRow("When a limit is reached", retention));
        checkpointPage.Children.Add(SettingsNote("Default: every 60 seconds, up to 300 checkpoints within 4096 MiB. Use 300 seconds for five-minute spacing. The project baseline is always retained."));
        checkpointPage.Children.Add(SettingsNote("Invalid automatic checkpoints are deleted after earlier edits. Invalid named states disappear from the timeline. Undo restores inputs, but does not resurrect removed states."));
        checkpointPage.Children.Add(SettingsNote("Checkpoints and named states are stored on disk and included when you save the project. Reducing a limit trims the active cache immediately."));
        void EnableCheckpointControls() { foreach (var control in new Control[] { interval, count, budget, retention }) control.IsEnabled = enabled.IsChecked == true; }
        enabled.IsCheckedChanged += (_, _) => EnableCheckpointControls(); EnableCheckpointControls();
        var projectTabs = pages.Select(p => SettingsTab(p.Key, p.Value)).Append(SettingsTab("Checkpoints", checkpointPage)).ToArray();
        foreach (var tab in projectTabs) tab.IsEnabled = _execution.HasProject;
        var interfaceTab = SettingsTab("Interface", BuildInterfaceSettings(_settings, _settings.Save));
        var tabItems = projectTabs.Append(interfaceTab).ToArray();
        var tabs = new TabControl { Name = "ConfigurationTabs", ItemsSource = tabItems, SelectedIndex = _execution.HasProject ? selectedTab : projectTabs.Length };
        Grid.SetRow(tabs, 1); layout.Children.Add(tabs);
        var replay = new CheckBox { Content = "After an emulation change, replay to selected input", IsChecked = true, IsEnabled = _execution.HasProject };
        var note = SettingsNote("Emulation changes restart from boot with a new baseline, preserve inputs/events, and remove existing state markers. Checkpoint settings apply without a restart.");
        var footer = new StackPanel { Spacing = 8, Margin = new Thickness(0,12,0,0), Children = { replay, note } };
        Grid.SetRow(footer, 2); layout.Children.Add(footer);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 10, Margin = new Thickness(0,12,0,0) };
        var cancel = new Button { Content = "Cancel" }; cancel.Click += (_, _) => dialog.Close(false);
        var apply = new Button { Content = "Apply to project", IsEnabled = _execution.HasProject };
        bool replayAfterApply = false, applying = false;
        dialog.Closing += (_, e) => { if (applying) e.Cancel = true; };
        apply.Click += async (_, _) =>
        {
            try
            {
                if (!DateTimeOffset.TryParseExact(utc.Text?.Trim(), "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var start))
                    throw new InvalidDataException("Enter UTC as yyyy-MM-dd HH:mm:ss.");
                var values = new SortedDictionary<string,string>(StringComparer.Ordinal);
                foreach (var setting in EmulationConfiguration.Settings) values.Add(setting.Key, setting.Values[editors[setting.Key].SelectedIndex]);
                var next = new EmulationConfiguration { StartUtcSeconds = start.ToUnixTimeSeconds(), Options = values }.ValidatedCopy();
                var nextPolicy = new CheckpointPolicy(enabled.IsChecked == true, (int)interval.Value!, (int)count.Value!, (CheckpointRetention)retention.SelectedIndex, (int)budget.Value!);
                nextPolicy.Validate();
                applying = true;
                apply.IsEnabled = cancel.IsEnabled = tabs.IsEnabled = false; note.Text = "Applying project settings…";
                var emulationChanged = next.Fingerprint != original.Fingerprint;
                if (emulationChanged) await WithGameLoading(() => _execution.ApplyConfigurationAsync(next, nextPolicy), dialog);
                else await _execution.ConfigureCheckpointsAsync(nextPolicy);
                replayAfterApply = emulationChanged && replay.IsChecked == true;
                applying = false;
                dialog.Close(true);
            }
            catch (Exception ex) { applying = false; note.Text = ex.Message; apply.IsEnabled = cancel.IsEnabled = tabs.IsEnabled = true; }
        };
        actions.Children.Add(cancel); actions.Children.Add(apply); Grid.SetRow(actions,3); layout.Children.Add(actions); dialog.Content = layout;
        void RefreshScope()
        {
            var interfaceSelected = tabs.SelectedItem == interfaceTab;
            footer.IsVisible = apply.IsVisible = !interfaceSelected;
            cancel.Content = interfaceSelected ? "Close" : "Cancel";
        }
        tabs.SelectionChanged += (_, _) => RefreshScope(); RefreshScope();
        if (capturePath != null) dialog.Opened += async (_, _) =>
        {
            var path = Path.GetFullPath(capturePath); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            for (var i = 0; i < tabItems.Length; i++)
            {
                tabs.SelectedIndex = i; await Task.Delay(300);
                using var bitmap = new RenderTargetBitmap(new PixelSize((int)dialog.Bounds.Width, (int)dialog.Bounds.Height)); bitmap.Render(dialog);
                bitmap.Save(i == 0 ? path : Path.Combine(Path.GetDirectoryName(path)!, $"{Path.GetFileNameWithoutExtension(path)}-tab{i}.png"));
            }
            dialog.Close(false);
        };
        _dialogOpen = true;
        try { await dialog.ShowDialog<bool>(this); }
        finally { _dialogOpen = false; }
        if (replayAfterApply) await SeekTo((ulong)Math.Min(_execution.Inputs.Count, _timeline.SelectedFrame));
    }
    private static TabItem SettingsTab(string title, Control content) => new() { Header = title, Content = new ScrollViewer { Content = content } };
    internal static Control BuildInterfaceSettings(AppSettings settings, Action save)
    {
        var page = SettingsPage();
        var theme = new ComboBox { Name = "UiTheme", ItemsSource = new[] { UiTheme.Light, UiTheme.Dark, UiTheme.System },
            SelectedItem = settings.Theme, HorizontalAlignment = HorizontalAlignment.Stretch };
        var note = SettingsNote("Applies immediately to all Studio windows. System follows your operating system's appearance.");
        page.Children.Add(SettingsRow("UI theme", theme));
        page.Children.Add(note);
        theme.SelectionChanged += (_, _) =>
        {
            if (theme.SelectedItem is not UiTheme selected || selected == settings.Theme) return;
            var previous = settings.Theme;
            try
            {
                settings.Theme = selected;
                save();
                StudioTheme.Apply(selected);
                note.Text = "Applies immediately to all Studio windows. System follows your operating system's appearance.";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                settings.Theme = previous; theme.SelectedItem = previous;
                note.Text = $"Theme could not be saved: {ex.Message}";
            }
        };
        return page;
    }
    private static StackPanel SettingsPage() => new() { Margin = new Thickness(0,18,12,10), Spacing = 13 };
    private static TextBlock SettingsNote(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = StudioTheme.Brush(ThemeColor.Muted), FontSize = 13 };
    private static Control SettingsRow(string label, Control editor)
    {
        var row = new Grid { ColumnDefinitions = new("240,*") };
        row.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
        Avalonia.Automation.AutomationProperties.SetName(editor, label);
        Grid.SetColumn(editor,1); row.Children.Add(editor); return row;
    }
}
