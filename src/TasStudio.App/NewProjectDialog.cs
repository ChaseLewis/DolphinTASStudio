using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using TasStudio.Emulation;

namespace TasStudio.App;

internal sealed record NewProjectRequest(string Path, string Rom, long StartUtc, string? StatePath);

internal sealed class NewProjectDialog : Window
{
    internal readonly TextBox ProjectName = new() { Text = "New project" };
    internal readonly TextBox Location = new() { Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "TAS Projects") };
    internal readonly TextBox Rom = new();
    internal readonly ComboBox StartingPoint = new() { ItemsSource = new[] { "Power on", "Save state" }, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
    internal readonly TextBox StatePath = new();
    internal readonly TextBox Utc = new() { Text = "2000-01-01 00:00:00" };
    internal readonly TextBlock Error = new() { Foreground = Brushes.Salmon, TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _stateRow;
    private readonly TextBlock _startNote = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.LightSteelBlue, FontSize = 12 };

    public NewProjectDialog(string? lastRom = null)
    {
        Title = "New project"; Width = 600; SizeToContent = SizeToContent.Height; CanResize = false;
        Background = Brush.Parse("#20262F");
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Rom.Text = lastRom;
        var content = new StackPanel { Margin = new Thickness(24), Spacing = 12 };
        content.Children.Add(new TextBlock { Text = "Create a TAS project", FontSize = 22, FontWeight = FontWeight.SemiBold });
        content.Children.Add(Field("Project name", ProjectName));
        content.Children.Add(Field("Location", BrowseRow(Location, async () =>
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new() { Title = "Project location" });
            if (folders.Count > 0) Location.Text = folders[0].TryGetLocalPath();
        })));
        content.Children.Add(Field("Game image", BrowseRow(Rom, async () =>
        {
            var path = await Pick("Select game image", ["*.iso", "*.gcm", "*.rvz", "*.gcz", "*.ciso", "*.dol", "*.elf"]);
            if (path != null) Rom.Text = path;
        })));
        content.Children.Add(Field("Starting point", StartingPoint));
        _stateRow = Field("Save state", BrowseRow(StatePath, async () =>
        {
            var path = await Pick("Select TAS Studio save state", ["*.tasstate"]);
            if (path == null) return;
            StatePath.Text = path;
            try
            {
                var state = await Task.Run(() => ProjectArchive.Load(path, ProjectArchive.StateKind));
                if (string.IsNullOrWhiteSpace(Rom.Text)) Rom.Text = FolderProject.ResolveRomPath(path, state.Metadata.GamePath);
                Utc.Text = DateTimeOffset.FromUnixTimeSeconds(state.Metadata.Configuration?.StartUtcSeconds ?? 946684800).UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                Error.Text = null;
            }
            catch (Exception ex) { Error.Text = ex.Message; }
        }));
        content.Children.Add(_stateRow);
        content.Children.Add(Field("Start UTC · yyyy-MM-dd HH:mm:ss", Utc));
        content.Children.Add(_startNote); content.Children.Add(Error);
        var create = new Button { Content = "Create project", IsDefault = true };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        create.Click += (_, _) => { try { Close(ReadRequest()); } catch (Exception ex) { Error.Text = ex.Message; } };
        cancel.Click += (_, _) => Close();
        content.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { cancel, create } });
        Content = content;
        StartingPoint.SelectionChanged += (_, _) => RefreshStart(); RefreshStart();
    }
    private void RefreshStart()
    {
        var state = StartingPoint.SelectedIndex == 1;
        _stateRow.IsVisible = state; Utc.IsEnabled = !state;
        _startNote.Text = state ? "Exploration start · copies the state and its settings into the project. Timeline begins at frame 0." : "Starts with a fresh game session at the chosen UTC.";
    }
    internal NewProjectRequest ReadRequest()
    {
        var name = ProjectName.Text?.Trim() ?? "";
        if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name is "." or ".." || name.EndsWith('.'))
            throw new InvalidDataException("Enter a valid project name.");
        if (string.IsNullOrWhiteSpace(Location.Text) || !Path.IsPathFullyQualified(Location.Text)) throw new InvalidDataException("Choose an absolute project location.");
        if (!File.Exists(Rom.Text)) throw new InvalidDataException("Select an existing game image.");
        var folder = Path.Combine(Location.Text, name);
        if (File.Exists(folder) || Directory.Exists(folder)) throw new InvalidDataException("That project folder already exists. Choose a new name or location.");
        var state = StartingPoint.SelectedIndex == 1;
        if (state && !File.Exists(StatePath.Text)) throw new InvalidDataException("Select a TAS Studio save state (.tasstate).");
        long utc = 946684800;
        if (!state)
        {
            if (!DateTimeOffset.TryParseExact(Utc.Text, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date)) throw new InvalidDataException("Enter UTC as yyyy-MM-dd HH:mm:ss.");
            utc = date.ToUnixTimeSeconds(); new EmulationConfiguration { StartUtcSeconds = utc }.ValidatedCopy();
        }
        return new(Path.Combine(folder, name + ".tasproj"), Path.GetFullPath(Rom.Text!), utc, state ? Path.GetFullPath(StatePath.Text!) : null);
    }
    private async Task<string?> Pick(string title, string[] patterns)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new() { Title = title, FileTypeFilter = [new(title) { Patterns = patterns }] });
        return files.Count == 0 ? null : files[0].TryGetLocalPath();
    }
    private static StackPanel Field(string name, Control value) => new() { Spacing = 4, Children = { new TextBlock { Text = name, FontSize = 12 }, value } };
    private static Control BrowseRow(TextBox box, Func<Task> action)
    {
        var grid = new Grid { ColumnDefinitions = new("*,Auto") };
        var browse = new Button { Content = "Browse…", Margin = new Thickness(8, 0, 0, 0) };
        browse.Click += async (_, _) => await action();
        grid.Children.Add(box); Grid.SetColumn(browse, 1); grid.Children.Add(browse); return grid;
    }
}
