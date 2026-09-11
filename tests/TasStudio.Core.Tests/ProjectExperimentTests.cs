using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Reflection;
using System.Runtime.Loader;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TasStudio.App;
using TasStudio.Emulation;
using TasStudio.Sdk;
using Xunit;

namespace TasStudio.Core.Tests;

public sealed class ProjectExperimentTests
{
    private static async Task<(string Project, StateMarker State)> Source(TestWorkspace files, ExecutionService execution)
    {
        await execution.LoadGameAsync(files.GamePath, files.Options);
        await execution.NewProjectAsync();
        for (var i = 0; i < 3; i++) await execution.StepAsync();
        await execution.SaveNamedStateAsync("Before fight");
        var state = Assert.Single(execution.StateMarkers);
        var path = Path.GetFullPath(files.FilePath("My TAS/movie.tasproj"));
        await execution.SaveProjectAsync(path);
        return (path, state);
    }

    [Fact]
    public async Task CreateUsesSelectedStateAndPortablePathsWithoutOverwritingAnExistingRecipe()
    {
        using var files = new TestWorkspace(); using var execution = new ExecutionService(new FakeBackend());
        var (project, state) = await Source(files, execution);
        await execution.StepAsync(); // Current preview is deliberately not the selected state.
        var path = ProjectExperiments.Create(project, state.Id, state.Name);
        var config = ExperimentFiles.Read<CSharpExperimentFile>(path);
        Assert.Equal(ExperimentStart.SaveState, config.Start); Assert.Equal(state.Id, config.StateId);
        Assert.Null(config.StartUtcSeconds); Assert.Equal(0, config.PrerollGroups); Assert.Equal(1, config.Count);
        Assert.False(Path.IsPathRooted(config.SourceProject));
        Assert.Equal(project, Path.GetFullPath(config.SourceProject, Path.GetDirectoryName(path)!));
        Assert.Equal(state.Id, Assert.Single(ProjectExperiments.Discover(project)).StateId);
        var code = Path.Combine(Path.GetDirectoryName(path)!, "Experiment.cs");
        File.AppendAllText(code, "\n// Keep my custom strategy\n");
        var second = ProjectExperiments.Create(project, state.Id, state.Name);
        Assert.NotEqual(path, second); Assert.Contains("Keep my custom strategy", File.ReadAllText(code));
        Assert.Equal(2, ProjectExperiments.Discover(project).Count);

        var moved = files.FilePath("Moved TAS");
        Directory.Move(Path.GetDirectoryName(project)!, moved);
        var movedProject = Path.Combine(moved, "movie.tasproj");
        Assert.All(ProjectExperiments.Discover(movedProject), e => Assert.Equal(movedProject, e.SourceProject));
    }

    [Fact]
    public async Task ReattachPreservesCodeCustomConfigurationAndFrozenRunsAfterOldStateIsDeleted()
    {
        using var files = new TestWorkspace(); using var execution = new ExecutionService(new FakeBackend());
        var (project, original) = await Source(files, execution);
        var path = ProjectExperiments.Create(project, original.Id, original.Name);
        var node = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        node["Parameters"] = JsonSerializer.SerializeToNode(new { WaitBudget = 120, Variants = new[] { "neutral", "left" } });
        node["Count"] = 242; node["Headless"] = false; node["FutureOption"] = "preserve";
        File.WriteAllText(path, node.ToJsonString());
        var code = Path.Combine(Path.GetDirectoryName(path)!, "Experiment.cs");
        var originalCode = File.ReadAllText(code);
        var oldRun = ProjectExperiments.PrepareRun(path, project, original.Id);
        var receipt = File.ReadAllBytes(oldRun.ConfigPath);
        await execution.ClearMarkerAsync(original.Id);
        await execution.StepAsync(); await execution.SaveNamedStateAsync("Revised approach");
        var replacement = Assert.Single(execution.StateMarkers);
        await execution.SaveProjectAsync(project);
        Assert.Throws<InvalidDataException>(() => ProjectExperiments.PrepareRun(path, project, original.Id));
        node["StartUtcSeconds"] = 946684801; node["PrerollGroups"] = 50;
        File.WriteAllText(path, node.ToJsonString());
        ProjectExperiments.Attach(path, project, replacement.Id);
        var attached = ExperimentFiles.Read<CSharpExperimentFile>(path);
        Assert.Equal(replacement.Id, attached.StateId); Assert.Null(attached.StartUtcSeconds); Assert.Equal(0, attached.PrerollGroups);
        Assert.Equal(242, attached.Count); Assert.False(attached.Headless);
        Assert.Equal(120, attached.Parameters.GetProperty("WaitBudget").GetInt32());
        Assert.Equal("preserve", JsonNode.Parse(File.ReadAllText(path))!["FutureOption"]!.GetValue<string>());
        Assert.Equal(originalCode, File.ReadAllText(code)); Assert.Equal(receipt, File.ReadAllBytes(oldRun.ConfigPath));
        Assert.Throws<InvalidDataException>(() => ProjectExperiments.PrepareRun(path, project, original.Id));

        var newRun = ProjectExperiments.PrepareRun(path, project, replacement.Id);
        Assert.NotEqual(oldRun.Directory, newRun.Directory);
        foreach (var (run, expected) in new[] { (oldRun, original), (newRun, replacement) })
        {
            var request = ExperimentFiles.Read<CSharpExperimentFile>(run.ConfigPath);
            var snapshot = Path.GetFullPath(request.SourceProject, run.Directory);
            using var worker = new ExecutionService(new FakeBackend());
            await worker.LoadProjectAsync(snapshot, files.Options, positionOverride: 0);
            await worker.LoadMarkerAsync(request.StateId!);
            Assert.Equal(expected.Position, worker.Position);
        }
    }

    [Fact]
    public async Task InvalidAttachmentLeavesConfigUntouchedAndCorruptRecipeIsVisible()
    {
        using var files = new TestWorkspace(); using var execution = new ExecutionService(new FakeBackend());
        var (project, state) = await Source(files, execution);
        var path = ProjectExperiments.Create(project, state.Id, state.Name);
        var bytes = File.ReadAllBytes(path);
        Assert.Throws<InvalidDataException>(() => ProjectExperiments.Attach(path, project, "removed"));
        Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Throws<InvalidDataException>(() => ProjectExperiments.Create(project, "removed", "bad"));
        Assert.Throws<FileNotFoundException>(() => ProjectExperiments.Create(project, state.Id, "Missing library", [files.FilePath("missing.dll")]));
        Assert.Single(Directory.GetDirectories(ProjectExperiments.Root(project)));
        foreach (var invalid in new[] { "{broken", "null", "{}" })
        {
            File.WriteAllText(path, invalid);
            Assert.NotNull(Assert.Single(ProjectExperiments.Discover(project)).Error);
        }
    }

    [Fact]
    public async Task RenameAndRemovePreserveCodeCustomFieldsAndOldRunSnapshots()
    {
        using var files = new TestWorkspace(); using var execution = new ExecutionService(new FakeBackend());
        var (project, state) = await Source(files, execution);
        var path = ProjectExperiments.Create(project, state.Id, "  Boss RNG  ");
        var other = ProjectExperiments.Create(project, state.Id, "Other search");
        Assert.Equal("Boss RNG", ExperimentFiles.Read<CSharpExperimentFile>(path).Name);
        var config = JsonNode.Parse(File.ReadAllText(path))!;
        config["FutureField"] = "keep";
        File.WriteAllText(path, config.ToJsonString());
        var code = Path.Combine(Path.GetDirectoryName(path)!, "Experiment.cs");
        var codeBytes = File.ReadAllBytes(code);
        var run = ProjectExperiments.PrepareRun(path, project, state.Id);
        var request = File.ReadAllBytes(run.ConfigPath);
        ProjectExperiments.Rename(project, path, "  Fastest boss fight  ");
        var renamed = ProjectExperiments.Discover(project).Single(e => e.ConfigPath == path);
        Assert.Equal("Fastest boss fight", renamed.Name); Assert.Equal(state.Id, renamed.StateId);
        Assert.Equal("keep", JsonNode.Parse(File.ReadAllText(path))!["FutureField"]!.GetValue<string>());
        Assert.Equal(request, File.ReadAllBytes(run.ConfigPath)); Assert.Equal(codeBytes, File.ReadAllBytes(code));
        var beforeInvalidRename = File.ReadAllBytes(path);
        Assert.Throws<InvalidDataException>(() => ProjectExperiments.Rename(project, path, "   "));
        Assert.Equal(beforeInvalidRename, File.ReadAllBytes(path));

        var retired = ProjectExperiments.Remove(project, path);
        Assert.False(File.Exists(path)); Assert.Equal(beforeInvalidRename, File.ReadAllBytes(retired));
        Assert.Equal(other, Assert.Single(ProjectExperiments.Discover(project)).ConfigPath);
        Assert.Equal(request, File.ReadAllBytes(run.ConfigPath)); Assert.Equal(codeBytes, File.ReadAllBytes(code));
        var frozen = ExperimentFiles.Read<CSharpExperimentFile>(run.ConfigPath);
        using var worker = new ExecutionService(new FakeBackend());
        await worker.LoadProjectAsync(Path.GetFullPath(frozen.SourceProject, run.Directory), files.Options, positionOverride: 0);
        await worker.LoadMarkerAsync(state.Id); Assert.Equal(state.Position, worker.Position);
        File.Move(retired, path); // Recovery restores the recipe without rebuilding or relocating its workspace.
        Assert.Equal(2, ProjectExperiments.Discover(project).Count);
    }

    [Fact]
    public async Task ManagementRejectsOtherFoldersAndCanRemoveABrokenRecipe()
    {
        using var files = new TestWorkspace(); using var execution = new ExecutionService(new FakeBackend());
        var (project, state) = await Source(files, execution);
        var foreignFolder = files.FilePath("Other project/experiments/search"); Directory.CreateDirectory(foreignFolder);
        var foreign = Path.Combine(foreignFolder, ProjectExperiments.ConfigFileName); File.WriteAllText(foreign, "{untouched}");
        Assert.Throws<InvalidDataException>(() => ProjectExperiments.Remove(project, foreign));
        Assert.Throws<InvalidDataException>(() => ProjectExperiments.Rename(project, foreign, "New name"));
        Assert.Equal("{untouched}", File.ReadAllText(foreign));
        var path = ProjectExperiments.Create(project, state.Id, "Broken search"); File.WriteAllText(path, "{broken");
        var retired = ProjectExperiments.Remove(project, path);
        Assert.Empty(ProjectExperiments.Discover(project)); Assert.Equal("{broken", File.ReadAllText(retired));
    }

    [Fact]
    public async Task GeneratedStateStarterCompilesAndTakesOverAtStateInsteadOfPlayingRemainingMovie()
    {
        using var files = new TestWorkspace(); using var execution = new ExecutionService(new FakeBackend());
        var (project, state) = await Source(files, execution);
        execution.LiveInput = () => ControllerState.Neutral with { Buttons = PadButtons.B };
        for (var i = 0; i < 5; i++) await execution.StepAsync();
        await execution.SaveProjectAsync(project);
        var helperRoot = files.FilePath("Shared Helpers & Utilities"); Directory.CreateDirectory(helperRoot);
        var helper = Path.Combine(helperRoot, "Helpers.csproj");
        File.WriteAllText(helper, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        File.WriteAllText(Path.Combine(helperRoot, "Marker.cs"), "namespace HelperLibrary; public sealed class Marker {}");
        var library = typeof(Skies.SkiesGame).Assembly.Location;
        var path = ProjectExperiments.Create(project, state.Id, state.Name, [helper, library, helper]);
        var root = Path.GetDirectoryName(path)!;
        File.AppendAllText(Path.Combine(root, "Experiment.cs"), "\npublic sealed class LibraryReferencesCompile { public Skies.SkiesGame? Game; public HelperLibrary.Marker? Marker; }\n");
        using var build = new Process { StartInfo = new("dotnet") { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true } };
        foreach (var arg in new[] { "build", Path.Combine(root, "Experiments.csproj"), "-c", "Release", "--nologo" }) build.StartInfo.ArgumentList.Add(arg);
        build.Start(); var stdout = build.StandardOutput.ReadToEndAsync(); var stderr = build.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        try { await build.WaitForExitAsync(timeout.Token); }
        finally { if (!build.HasExited) { build.Kill(true); await build.WaitForExitAsync(); } }
        Assert.True(build.ExitCode == 0, await stdout + await stderr);
        Assert.True(File.Exists(Path.Combine(root, "bin/Release/net10.0/Skies.dll")));
        Assert.True(File.Exists(Path.Combine(root, "bin/Release/net10.0/Helpers.dll")));
        using var worker = new ExecutionService(new FakeBackend());
        var job = new ExperimentJob("Starter", project, files.Options.CorePath, files.Options.SystemDirectory,
            files.FilePath("worker-run"), ExperimentStart.SaveState, state.Id, null, JsonSerializer.SerializeToElement(new { }), 30, 0, 1);
        // Load bytes so the collectible assembly doesn't hold the temporary Windows DLL open during cleanup.
        var load = new AssemblyLoadContext("State starter", isCollectible: true);
        using var bytes = new MemoryStream(File.ReadAllBytes(Path.Combine(root, "bin/Release/net10.0/Experiments.dll")));
        var assembly = load.LoadFromStream(bytes);
        ExperimentResult result;
        try { result = await ExperimentWorker.RunAsync(job, worker, CancellationToken.None, (IExperiment)Activator.CreateInstance(assembly.GetType("Experiment")!)!); }
        finally { load.Unload(); }
        Assert.Equal("completed", result.Status); Assert.Equal(4UL, result.Position);
        Assert.Equal(3UL, result.Value!.Value.GetProperty("StartGroup").GetUInt64());
        Assert.Equal(PadButtons.None, FolderProject.Load(result.ProjectPath!).Archive.Metadata.Inputs[3].Buttons);
        Assert.Equal(PadButtons.B, FolderProject.Load(project).Archive.Metadata.Inputs[3].Buttons);
    }

    [AvaloniaFact]
    public async Task StateMenuDiscoversAttachmentAfterReopenAndReattachesToReplacementState()
    {
        using var files = new TestWorkspace(); using var service = new ExecutionService(new FakeBackend());
        var (project, state) = await Source(files, service);
        var path = ProjectExperiments.Create(project, state.Id, state.Name);
        await service.StepAsync(); await service.SaveNamedStateAsync("Replacement"); await service.SaveProjectAsync(project);
        var replacement = service.StateMarkers.Single(s => s.Id != state.Id);
        var settings = new AppSettings { CodeEditorPath = files.FilePath("missing.exe"), ExperimentLibraries = [typeof(Skies.SkiesGame).Assembly.Location] };
        var main = new MainWindow(["--layout-file", files.FilePath("layout.json")], settings, service);
        // Simulate an opened saved project without invoking native file pickers or a real ROM.
        typeof(MainWindow).GetField("_projectPath", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(main, project);
        try
        {
            main.ShowEditor(); main.Show(); main.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            var timeline = main.GetVisualDescendants().OfType<TimelineView>().Single();
            timeline.VisibleFrames = 12; timeline.Update(); main.UpdateLayout();
            MenuItem OpenAt(ulong position)
            {
                timeline.ContextMenu!.Close();
                var point = timeline.TranslatePoint(new Point(118 + position * (timeline.Bounds.Width - 118) / 12, 18), main)!.Value;
                main.MouseDown(point, MouseButton.Right); main.MouseUp(point, MouseButton.Right);
                Dispatcher.UIThread.RunJobs();
                return timeline.ContextMenu.Items.OfType<MenuItem>().Single(m => m.Name == "StateExperimentMenu");
            }
            var menu = OpenAt(state.Position);
            Assert.True(menu.IsVisible); Assert.True(menu.IsEnabled);
            Assert.Contains(menu.Items.OfType<MenuItem>(), m => Equals(m.Header, "Run Before fight"));
            Assert.DoesNotContain(menu.Items.OfType<MenuItem>(), m => m.IsVisible && m.Header?.ToString()?.StartsWith("Open in Editor") == true);
            settings.CodeEditorPath = files.FilePath("Editor.exe"); File.WriteAllText(settings.CodeEditorPath, "Never launched");
            menu = OpenAt(state.Position);
            Assert.Contains(menu.Items.OfType<MenuItem>(), m => m.IsVisible && m.Header?.ToString()?.StartsWith("Open in Editor") == true);
            timeline.ContextMenu!.Close();
            await main.AttachStateExperiment(replacement, path);
            menu = OpenAt(replacement.Position);
            Assert.Contains(menu.Items.OfType<MenuItem>(), m => Equals(m.Header, "Run Before fight"));
            menu = OpenAt(state.Position);
            Assert.DoesNotContain(menu.Items.OfType<MenuItem>(), m => Equals(m.Header, "Run Before fight"));
            timeline.ContextMenu!.Close();
            var creating = main.CreateStateExperiment(replacement);
            await Until(() => main.OwnedWindows.Any(w => w.Title == "Create experiment"));
            var dialog = main.OwnedWindows.Single(w => w.Title == "Create experiment");
            var name = dialog.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "ExperimentName");
            var save = dialog.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "SaveExperimentName");
            Assert.Equal("", name.Text); Assert.False(save.IsEnabled);
            name.Text = "Replacement boss search";
            save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var created = await creating;
            Assert.NotNull(created);
            Assert.Equal(replacement.Id, ExperimentFiles.Read<CSharpExperimentFile>(created).StateId);
            Assert.Equal("Replacement boss search", ExperimentFiles.Read<CSharpExperimentFile>(created).Name);
            var references = System.Xml.Linq.XDocument.Load(Path.Combine(Path.GetDirectoryName(created)!, "Experiments.csproj")).Descendants("Reference");
            Assert.Contains(references, r => (string?)r.Attribute("Include") == "Skies");
            var recipe = ProjectExperiments.Discover(project).Single(e => e.ConfigPath == created);
            var renaming = main.RenameStateExperiment(recipe);
            await Until(() => main.OwnedWindows.Any(w => w.Title == "Rename experiment"));
            dialog = main.OwnedWindows.Single(w => w.Title == "Rename experiment");
            name = dialog.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "ExperimentName");
            Assert.Equal(recipe.Name, name.Text); name.Text = "Boss drop search";
            dialog.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "SaveExperimentName").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await renaming;
            menu = OpenAt(replacement.Position);
            Assert.Contains(menu.Items.OfType<MenuItem>(), m => Equals(m.Header, "Run Boss drop search"));
            Assert.Contains(menu.Items.OfType<MenuItem>(), m => Equals(m.Header, "Rename experiment…"));
            Assert.Contains(menu.Items.OfType<MenuItem>(), m => Equals(m.Header, "Remove experiment"));
            timeline.ContextMenu!.Close();
            await main.RemoveStateExperiment(recipe);
            Assert.Single(ProjectExperiments.Discover(project));
            await main.RemoveStateExperiment(Assert.Single(ProjectExperiments.Discover(project)));
            await Until(() => !timeline.ExperimentStateIds.Contains(replacement.Id));
            menu = OpenAt(replacement.Position);
            Assert.DoesNotContain(menu.Items.OfType<MenuItem>(), m => m.Header?.ToString()?.StartsWith("Run ") == true);
            timeline.ContextMenu!.Close();
            var cancelled = main.CreateStateExperiment(replacement);
            await Until(() => main.OwnedWindows.Any(w => w.Title == "Create experiment"));
            main.OwnedWindows.Single(w => w.Title == "Create experiment").Close();
            Assert.Null(await cancelled); Assert.Empty(ProjectExperiments.Discover(project));
        }
        finally { main.CloseAfterCapture(); }
    }

    private static async Task Until(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 300 && !condition(); attempt++)
        { await Task.Delay(10); Dispatcher.UIThread.RunJobs(); }
        Assert.True(condition());
    }
}
