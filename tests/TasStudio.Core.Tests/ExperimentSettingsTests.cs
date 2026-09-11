using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using TasStudio.App;
using Xunit;

namespace TasStudio.Core.Tests;

public sealed class ExperimentSettingsTests
{
    [Fact]
    public void AutomaticEditorPrefersCodeAndExplicitSelectionNeverSilentlyFallsBack()
    {
        using var files = new TestWorkspace();
        var code = files.FilePath("Code.exe"); var visualStudio = files.FilePath("devenv.exe"); var custom = files.FilePath("custom.exe");
        foreach (var path in new[] { code, visualStudio, custom }) File.WriteAllText(path, "Editor fixture; never launched");
        var probes = 0;
        string? Probe() { probes++; return visualStudio; }
        Assert.Equal(code, CodeEditors.Select(null, [files.FilePath("missing.exe"), code], Probe)); Assert.Equal(0, probes);
        Assert.Equal(visualStudio, CodeEditors.Select(null, [], Probe)); Assert.Equal(1, probes);
        Assert.Equal(custom, CodeEditors.Select(custom, [code], Probe)); Assert.Equal(1, probes);
        Assert.Null(CodeEditors.Select(files.FilePath("removed.exe"), [code], Probe)); Assert.Equal(1, probes);
        Assert.Null(CodeEditors.Select(null, [], () => null));
    }

    [Fact]
    public void EditorLaunchPassesFolderAsOneLiteralArgumentWithoutShellExpansion()
    {
        using var files = new TestWorkspace(); var folder = files.FilePath("My experiments & helpers (draft)");
        Directory.CreateDirectory(folder);
        foreach (var executable in new[] { "Code.exe", "devenv.exe", "custom.exe" })
        {
            var path = files.FilePath(executable); File.WriteAllText(path, "Never launched");
            var start = CodeEditors.StartInfo(path, folder);
            Assert.Equal(path, start.FileName); Assert.Equal(folder, Assert.Single(start.ArgumentList));
            Assert.False(start.UseShellExecute); Assert.Equal(folder, start.WorkingDirectory);
        }
    }

    [Fact]
    public void OlderSettingsDefaultToAutomaticEditorAndNoLibraries()
    {
        using var files = new TestWorkspace(); var path = files.FilePath("settings.json");
        File.WriteAllText(path, "{\"Volume\":25}");
        var settings = AppSettings.Load(path, out var warning);
        Assert.Null(warning); Assert.Null(settings.CodeEditorPath); Assert.Empty(settings.ExperimentLibraries);
        settings.CodeEditorPath = files.FilePath("editor.exe"); settings.ExperimentLibraries = [files.FilePath("helpers.csproj")];
        settings.Save(path); var reopened = AppSettings.Load(path, out warning);
        Assert.Null(warning); Assert.Equal(settings.CodeEditorPath, reopened.CodeEditorPath);
        Assert.Equal(settings.ExperimentLibraries, reopened.ExperimentLibraries); Assert.Equal(25, reopened.Volume);
    }

    [AvaloniaFact]
    public async Task EditorAndLibrarySettingsBrowsePersistRemoveAndRollBackFailedSaves()
    {
        using var files = new TestWorkspace();
        var editor = files.FilePath("Editor.exe"); File.WriteAllText(editor, "Never launched");
        var helper = files.FilePath("Helpers.csproj"); File.WriteAllText(helper, "<Project />");
        var dll = typeof(Skies.SkiesGame).Assembly.Location;
        var path = files.FilePath("settings.json"); var settings = new AppSettings(); var fail = false;
        var page = MainWindow.BuildExperimentSettings(settings, () => { if (fail) throw new IOException("Save fixture failure"); settings.Save(path); },
            () => Task.FromResult<string?>(editor), () => Task.FromResult<IReadOnlyList<string>>([helper, dll, helper]));
        var window = new Window { Content = page };
        try
        {
            window.Show(); window.UpdateLayout();
            Button Button(string name) => page.GetVisualDescendants().OfType<Button>().Single(b => b.Name == name);
            void Click(string name) => Button(name).RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
            Click("BrowseCodeEditor");
            Assert.Equal(editor, settings.CodeEditorPath);
            Assert.Equal(editor, AppSettings.Load(path, out _).CodeEditorPath);
            Click("AddExperimentLibrary");
            Assert.Equal(new[] { helper, dll }, settings.ExperimentLibraries);
            Assert.Equal(settings.ExperimentLibraries, AppSettings.Load(path, out _).ExperimentLibraries);
            var list = page.GetVisualDescendants().OfType<ListBox>().Single(); list.SelectedItem = helper;
            fail = true; Click("RemoveExperimentLibrary");
            Assert.Equal(2, settings.ExperimentLibraries.Count);
            Assert.Contains(page.GetVisualDescendants().OfType<TextBlock>(), t => t.Text?.Contains("Save fixture failure") == true);
            fail = false; Click("RemoveExperimentLibrary"); Assert.Equal(dll, Assert.Single(settings.ExperimentLibraries));
            var text = page.GetVisualDescendants().OfType<TextBox>().Single(); text.Text = files.FilePath("missing.exe");
            Click("SaveCodeEditor"); Assert.Equal(editor, settings.CodeEditorPath);
            Click("AutomaticCodeEditor"); Assert.Null(settings.CodeEditorPath);
            Assert.Null(AppSettings.Load(path, out _).CodeEditorPath);
            await CodeEditors.ResolveAsync(null); // Let the background discovery finish before closing the fixture.
        }
        finally { window.Close(); }
    }
}
