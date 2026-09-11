using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using TasStudio.Emulation;

namespace TasStudio.App;

public sealed partial class MainWindow
{
    internal static Control BuildExperimentSettings(AppSettings settings, Action save,
        Func<Task<string?>>? pickEditor = null, Func<Task<IReadOnlyList<string>>>? pickLibraries = null)
    {
        var page = new StackPanel { Spacing = 12, Margin = new Thickness(0, 12, 0, 0) };
        page.Children.Add(new TextBlock { Text = "Experiments", FontSize = 17, FontWeight = FontWeight.SemiBold });
        var editor = new TextBox { Name = "CodeEditorPath", Text = settings.CodeEditorPath, Watermark = "Automatic: VS Code → Visual Studio" };
        var editorNote = SettingsNote("Looking for a code editor…");
        editorNote.Name = "CodeEditorStatus";
        page.Children.Add(SettingsRow("Code editor", editor));
        var editorButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var browse = new Button { Name = "BrowseCodeEditor", Content = "Browse…" };
        var saveEditor = new Button { Name = "SaveCodeEditor", Content = "Save editor" };
        var automatic = new Button { Name = "AutomaticCodeEditor", Content = "Use automatic" };
        editorButtons.Children.Add(browse); editorButtons.Children.Add(saveEditor); editorButtons.Children.Add(automatic);
        page.Children.Add(editorButtons); page.Children.Add(editorNote);

        async Task DescribeEditor(bool refresh = false)
        {
            var configured = settings.CodeEditorPath;
            var resolved = await CodeEditors.ResolveAsync(configured, refresh);
            if (configured != settings.CodeEditorPath) return;
            editorNote.Text = resolved != null
                ? (configured == null ? "Automatically selected: " : "Selected editor: ") + resolved
                : configured == null ? "No editor found. Browse to an editor executable to enable Open in Editor."
                : "The configured editor could not be found. Choose its executable or use automatic detection.";
        }
        async Task SaveEditor(string? path, bool refresh = false)
        {
            var previous = settings.CodeEditorPath;
            try
            {
                settings.CodeEditorPath = string.IsNullOrWhiteSpace(path) ? null : CodeEditors.Validate(path);
                save(); editor.Text = settings.CodeEditorPath;
            }
            catch (Exception ex) { settings.CodeEditorPath = previous; editorNote.Text = "Editor could not be saved: " + ex.Message; return; }
            await DescribeEditor(refresh);
        }
        saveEditor.Click += async (_, _) => await SaveEditor(editor.Text);
        automatic.Click += async (_, _) => await SaveEditor(null, refresh: true);
        browse.Click += async (_, _) =>
        {
            try
            {
                string? path;
                if (pickEditor != null) path = await pickEditor();
                else
                {
                    var files = await TopLevel.GetTopLevel(page)!.StorageProvider.OpenFilePickerAsync(new()
                    {
                        Title = "Choose code editor", AllowMultiple = false,
                        FileTypeFilter = [new("Editor executable") { Patterns = ["*.exe"] }]
                    });
                    path = files.FirstOrDefault()?.TryGetLocalPath();
                }
                if (path != null) await SaveEditor(path);
            }
            catch (Exception ex) { editorNote.Text = ex.Message; }
        };
        _ = DescribeEditor();

        var libraries = new ListBox { Name = "ExperimentLibraries", Height = 120, ItemsSource = settings.ExperimentLibraries.ToArray() };
        page.Children.Add(SettingsRow("Default libraries", libraries));
        page.Children.Add(SettingsNote("Included in new experiments created in Studio. Choose .csproj to rebuild a library with the experiment, or .dll to use a compiled .NET library."));
        var libraryNote = SettingsNote(""); libraryNote.Name = "ExperimentLibrariesStatus";
        var libraryButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var add = new Button { Name = "AddExperimentLibrary", Content = "Add libraries…" };
        var remove = new Button { Name = "RemoveExperimentLibrary", Content = "Remove selected", IsEnabled = false };
        libraries.SelectionChanged += (_, _) => remove.IsEnabled = libraries.SelectedItem is string;
        libraryButtons.Children.Add(add); libraryButtons.Children.Add(remove); page.Children.Add(libraryButtons); page.Children.Add(libraryNote);
        void SaveLibraries(List<string> next)
        {
            var previous = settings.ExperimentLibraries;
            try
            {
                settings.ExperimentLibraries = next; save(); libraries.ItemsSource = next.ToArray();
                libraryNote.Text = "Default libraries saved. Existing experiment references are unchanged.";
            }
            catch (Exception ex) { settings.ExperimentLibraries = previous; libraryNote.Text = "Libraries could not be saved: " + ex.Message; }
        }
        add.Click += async (_, _) =>
        {
            try
            {
                IReadOnlyList<string> selected;
                if (pickLibraries != null) selected = await pickLibraries();
                else
                {
                    var files = await TopLevel.GetTopLevel(page)!.StorageProvider.OpenFilePickerAsync(new()
                    {
                        Title = "Choose experiment libraries", AllowMultiple = true,
                        FileTypeFilter = [new("C# projects and .NET libraries") { Patterns = ["*.csproj", "*.dll"] }]
                    });
                    selected = files.Select(f => f.TryGetLocalPath()).OfType<string>().ToArray();
                }
                if (selected.Count == 0) return;
                var validated = ExperimentLibraryReferences.Validate(selected);
                SaveLibraries(settings.ExperimentLibraries.Concat(validated).Distinct(StringComparer.OrdinalIgnoreCase).ToList());
            }
            catch (Exception ex) { libraryNote.Text = ex.Message; }
        };
        remove.Click += (_, _) =>
        {
            if (libraries.SelectedItem is string selected)
                SaveLibraries(settings.ExperimentLibraries.Where(p => !p.Equals(selected, StringComparison.OrdinalIgnoreCase)).ToList());
        };
        return page;
    }
}
