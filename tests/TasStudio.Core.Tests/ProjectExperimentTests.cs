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
    [Fact]
    public void PreviousBatchesFindsBothLayoutsAndSkipsFoldersWithoutResults()
    {
        using var files = new TestWorkspace();
        var config = files.FilePath(ProjectExperiments.ConfigFileName);
        Assert.Empty(ProjectExperiments.PreviousBatches(config));
        var direct = Path.GetFullPath(files.FilePath(".runs/capture-entry-269-01"));
        var nested = Path.GetFullPath(files.FilePath(".runs/studio-run/batch"));
        var both = Path.GetFullPath(files.FilePath(".runs/both-layouts"));
        var batchPaths = new[] { direct, nested, both, Path.Combine(both, "batch") };
        var timestamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (var index = 0; index < batchPaths.Length; index++)
        {
            var batch = batchPaths[index];
            Directory.CreateDirectory(batch);
            File.WriteAllText(Path.Combine(batch, "results.sqlite"), "Discovery must not modify the database");
            File.SetLastWriteTimeUtc(Path.Combine(batch, "results.sqlite"), timestamp.AddHours(index));
        }
        Directory.CreateDirectory(files.FilePath(".runs/unfinished/batch"));
        var found = ProjectExperiments.PreviousBatches(config);
        Assert.Equal(new[] { Path.Combine(both, "batch"), both, nested, direct }, found);
        Assert.All(found, batch => Assert.Equal("Discovery must not modify the database", File.ReadAllText(Path.Combine(batch, "results.sqlite"))));
        // A resumed batch's WAL can be newer than its main database and folder name.
        var wal = Path.Combine(direct, "results.sqlite-wal"); File.WriteAllText(wal, "active results");
        File.SetLastWriteTimeUtc(wal, timestamp.AddDays(1));
        Assert.Equal(timestamp.AddDays(1), ProjectExperiments.BatchLastUpdatedUtc(direct));
        Assert.Equal(direct, ProjectExperiments.PreviousBatches(config)[0]);
        Assert.Equal("active results", File.ReadAllText(wal));
    }

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
    public void IncompleteRunUsesFrozenRequestNameAndCorruptMetadataDoesNotHideIt()
    {
        using var files = new TestWorkspace(); var batch = files.FilePath("exports/unique-run/batch");
        Assert.Null(ProjectExperiments.ReadBatchName(batch));
        ExperimentFiles.Write(Path.Combine(batch, "request.json"), new { Name = "Boss RNG" });
        Assert.Equal("Boss RNG", ProjectExperiments.ReadBatchName(batch));
        File.WriteAllText(Path.Combine(batch, "experiment.json"), "{unfinished");
        Assert.Equal("Boss RNG", ProjectExperiments.ReadBatchName(batch));
        ExperimentFiles.Write(Path.Combine(batch, "experiment.json"), new { Name = "Final name" });
        Assert.Equal("Final name", ProjectExperiments.ReadBatchName(batch));
        Assert.Null(ProjectExperiments.ReadBatchExperimentName(batch));
        ExperimentFiles.Write(Path.Combine(batch, "request.json"), new { Name = "Original batch", DisplayName = "Stable experiment" });
        Assert.Equal("Final name", ProjectExperiments.ReadBatchName(batch));
        Assert.Null(ProjectExperiments.ReadBatchExperimentName(batch));
        ProjectExperiments.WriteDisplayName(batch, "Stable experiment");
        Assert.Equal("Stable experiment", ProjectExperiments.ReadBatchExperimentName(batch));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReplacingRunConfigurationAndCompletingNewBatchCannotRenameExperiment(bool nested)
    {
        using var files = new TestWorkspace();
        var folder = files.FilePath("experiments/encounter-search");
        var config = Path.Combine(folder, ProjectExperiments.ConfigFileName);
        var batch = Path.Combine(folder, nested ? ".runs/latest/batch" : ".runs/latest");
        ExperimentFiles.Write(config, new { Name = "winning batch", DisplayName = "winning batch" });
        ExperimentFiles.Write(Path.Combine(batch, "experiment.json"), new { Name = "winning batch", DisplayName = "winning batch" });
        Assert.Equal("encounter-search", ProjectExperiments.DisplayName(config));
        Assert.Equal("encounter-search", ProjectExperiments.ReadBatchExperimentName(batch));
        ProjectExperiments.WriteDisplayName(folder, "Encounter search");
        foreach (var batchName in new[] { "shrine-run-out", "newest successful batch" })
        {
            ExperimentFiles.Write(config, new { Name = batchName, DisplayName = batchName });
            ExperimentFiles.Write(Path.Combine(batch, "request.json"), new { Name = batchName, DisplayName = batchName });
            ExperimentFiles.Write(Path.Combine(batch, "experiment.json"), new { Name = batchName });
            Assert.Equal(batchName, ProjectExperiments.ReadBatchName(batch));
            Assert.Equal("Encounter search", ProjectExperiments.DisplayName(config));
            Assert.Equal("Encounter search", ProjectExperiments.DisplayName(Path.Combine(batch, "request.json")));
            Assert.Equal("Encounter search", ProjectExperiments.ReadBatchExperimentName(batch));
        }
    }

    [Fact]
    public async Task CreateUsesSelectedStateAndPortablePathsWithoutOverwritingAnExistingRecipe()
    {
        using var files = new TestWorkspace(); using var execution = new ExecutionService(new FakeBackend());
        var (project, state) = await Source(files, execution);
        await execution.StepAsync(); // Current preview is deliberately not the selected state.
        var path = ProjectExperiments.Create(project, state.Id, state.Name);
        var config = ExperimentFiles.Read<CSharpExperimentFile>(path);
        Assert.Equal(state.Name, ProjectExperiments.DisplayName(path));
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
    public async Task LegacyRecipeUsesFolderUntilNamedAndRunDescriptionsNeverChangeItsLabel()
    {
        using var files = new TestWorkspace(); using var execution = new ExecutionService(new FakeBackend());
        var (project, state) = await Source(files, execution);
        var path = ProjectExperiments.Create(project, state.Id, "RunWithoutEncounter");
        File.Delete(Path.Combine(Path.GetDirectoryName(path)!, ProjectExperiments.IdentityFileName));
        var config = JsonNode.Parse(File.ReadAllText(path))!;
        config.AsObject().Remove("DisplayName");
        config["Name"] = "RunWithoutEncounter: zero-delay winner";
        File.WriteAllText(path, config.ToJsonString());
        var original = File.ReadAllBytes(path);
        var folderName = Path.GetFileName(Path.GetDirectoryName(path)!);
        Assert.Equal(folderName, Assert.Single(ProjectExperiments.Discover(project)).Name);
        Assert.Equal(original, File.ReadAllBytes(path));
        var run = ProjectExperiments.PrepareRun(path, project, state.Id);
        Assert.Equal(folderName, run.Name);
        Assert.Equal(folderName, ProjectExperiments.ReadBatchExperimentName(run.BatchDirectory));
        Assert.Equal("RunWithoutEncounter: zero-delay winner", ProjectExperiments.ReadBatchName(run.BatchDirectory));
        ExperimentFiles.Write(Path.Combine(run.BatchDirectory, "experiment.json"), new { Name = "Last successful run" });
        Assert.Equal(folderName, ProjectExperiments.ReadBatchExperimentName(run.BatchDirectory));
        Assert.Equal(original, File.ReadAllBytes(path));

        ProjectExperiments.Rename(project, path, "  Encounter search  ");
        config = JsonNode.Parse(File.ReadAllText(path))!;
        Assert.Equal("RunWithoutEncounter: zero-delay winner", config["Name"]!.GetValue<string>());
        config["Name"] = "Next sweep: 200 trials";
        config["DisplayName"] = "Next sweep: 200 trials";
        File.WriteAllText(path, config.ToJsonString());
        Assert.Equal("Encounter search", Assert.Single(ProjectExperiments.Discover(project)).Name);
        Assert.Equal("Encounter search", ProjectExperiments.PrepareRun(path, project, state.Id).Name);
        Assert.Equal("Encounter search", ProjectExperiments.ReadBatchExperimentName(run.BatchDirectory));

        File.Delete(Path.Combine(Path.GetDirectoryName(path)!, ProjectExperiments.IdentityFileName));
        config["DisplayName"] = "   "; File.WriteAllText(path, config.ToJsonString());
        var movedFolder = Path.Combine(ProjectExperiments.Root(project), "Renamed encounter folder");
        Directory.Move(Path.GetDirectoryName(path)!, movedFolder);
        Assert.Equal("Renamed encounter folder", Assert.Single(ProjectExperiments.Discover(project)).Name);
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
        Assert.Equal(original.Name, oldRun.Name);
        Assert.Equal(original.Name, ProjectExperiments.ReadBatchExperimentName(oldRun.BatchDirectory));
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
        Assert.Equal(oldRun.Name, newRun.Name);
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
    public async Task RenameAndDetachPreserveProjectRecipeCodeCustomFieldsAndOldRunSnapshots()
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

        ProjectExperiments.Detach(project, path, state.Id);
        Assert.True(File.Exists(path));
        var expected = JsonNode.Parse(beforeInvalidRename)!; expected["StateId"] = null;
        Assert.True(JsonNode.DeepEquals(expected, JsonNode.Parse(File.ReadAllText(path))));
        var recipes = ProjectExperiments.Discover(project);
        Assert.Equal(2, recipes.Count);
        var detached = recipes.Single(e => e.ConfigPath == path);
        Assert.Null(detached.StateId); Assert.Null(detached.Error); Assert.Equal("Fastest boss fight", detached.Name);
        Assert.Equal(state.Id, recipes.Single(e => e.ConfigPath == other).StateId);
        var detachedConfig = ExperimentFiles.Read<CSharpExperimentFile>(path);
        Assert.Equal(ExperimentStart.SaveState, detachedConfig.Start);
        var definition = new ExperimentDefinition(1, detachedConfig.Name, detachedConfig.Start, detachedConfig.StateId, null, 1, 30,
            [new("Trial", JsonSerializer.SerializeToElement(new { }))]);
        Assert.Throws<InvalidDataException>(definition.Validate);
        Assert.Throws<InvalidDataException>(() => ProjectExperiments.PrepareRun(path, project, state.Id));
        Assert.Equal(request, File.ReadAllBytes(run.ConfigPath)); Assert.Equal(codeBytes, File.ReadAllBytes(code));
        var frozen = ExperimentFiles.Read<CSharpExperimentFile>(run.ConfigPath);
        using var worker = new ExecutionService(new FakeBackend());
        await worker.LoadProjectAsync(Path.GetFullPath(frozen.SourceProject, run.Directory), files.Options, positionOverride: 0);
        await worker.LoadMarkerAsync(state.Id); Assert.Equal(state.Position, worker.Position);
        ProjectExperiments.Attach(path, project, state.Id);
        Assert.Equal(2, ProjectExperiments.Discover(project).Count);
        Assert.Equal(state.Id, ProjectExperiments.Discover(project).Single(e => e.ConfigPath == path).StateId);
        Assert.Equal("Fastest boss fight", ProjectExperiments.PrepareRun(path, project, state.Id).Name);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.removed-*"));
    }

    [Fact]
    public async Task ManagementRejectsOtherFoldersAndLeavesBrokenRecipesIntact()
    {
        using var files = new TestWorkspace(); using var execution = new ExecutionService(new FakeBackend());
        var (project, state) = await Source(files, execution);
        var foreignFolder = files.FilePath("Other project/experiments/search"); Directory.CreateDirectory(foreignFolder);
        var foreign = Path.Combine(foreignFolder, ProjectExperiments.ConfigFileName); File.WriteAllText(foreign, "{untouched}");
        Assert.Throws<InvalidDataException>(() => ProjectExperiments.Detach(project, foreign, state.Id));
        Assert.Throws<InvalidDataException>(() => ProjectExperiments.Rename(project, foreign, "New name"));
        Assert.Equal("{untouched}", File.ReadAllText(foreign));
        var path = ProjectExperiments.Create(project, state.Id, "Broken search"); File.WriteAllText(path, "{broken");
        Assert.ThrowsAny<JsonException>(() => ProjectExperiments.Detach(project, path, state.Id));
        Assert.NotNull(Assert.Single(ProjectExperiments.Discover(project)).Error);
        Assert.Equal("{broken", File.ReadAllText(path));
    }

    [Fact]
    public async Task DetachRejectsStaleStateSelectionAndDifferentSourceProjectWithoutChangingConfiguration()
    {
        using var files = new TestWorkspace(); using var execution = new ExecutionService(new FakeBackend());
        var (project, state) = await Source(files, execution);
        var path = ProjectExperiments.Create(project, state.Id, "Search");
        await execution.StepAsync(); await execution.SaveNamedStateAsync("Replacement"); await execution.SaveProjectAsync(project);
        var replacement = execution.StateMarkers.Single(marker => marker.Id != state.Id);
        ProjectExperiments.Attach(path, project, replacement.Id);
        var bytes = File.ReadAllBytes(path);
        Assert.Throws<InvalidDataException>(() => ProjectExperiments.Detach(project, path, state.Id));
        Assert.Equal(bytes, File.ReadAllBytes(path));
        var config = JsonNode.Parse(bytes)!; config["SourceProject"] = "../../OtherProject.tasproj";
        File.WriteAllText(path, config.ToJsonString()); bytes = File.ReadAllBytes(path);
        Assert.Throws<InvalidDataException>(() => ProjectExperiments.Detach(project, path, replacement.Id));
        Assert.Equal(bytes, File.ReadAllBytes(path));
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
            Assert.Contains(menu.Items.OfType<MenuItem>(), m => Equals(m.Header, "Batch settings — Before fight"));
            // Legacy configs often contain a one-off result description in Name.
            var legacy = JsonNode.Parse(File.ReadAllText(path))!;
            File.Delete(Path.Combine(Path.GetDirectoryName(path)!, ProjectExperiments.IdentityFileName));
            legacy.AsObject().Remove("DisplayName"); legacy["Name"] = "Last successful run: zero-delay winner";
            File.WriteAllText(path, legacy.ToJsonString());
            menu = OpenAt(state.Position);
            foreach (var action in new[] { "Run ", "Top plays settings — ", "View top plays — ", "Open Folder — " })
                Assert.Contains(menu.Items.OfType<MenuItem>(), m => Equals(m.Header, action + "experiment-before-fight"));
            Assert.DoesNotContain(menu.Items.OfType<MenuItem>(), m => m.Header?.ToString()?.Contains("zero-delay winner") == true);
            ProjectExperiments.Rename(project, path, state.Name);
            menu = OpenAt(state.Position);
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
            var remove = menu.Items.OfType<MenuItem>().Single(m => Equals(m.Header, "Remove experiment from state"));
            Assert.False(remove.IsEnabled); Assert.Empty(remove.Items);
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
            remove = menu.Items.OfType<MenuItem>().Single(m => Equals(m.Header, "Remove experiment from state"));
            Assert.Equal(2, remove.ItemCount);
            remove.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Boss drop search"))
                .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await Until(() => ProjectExperiments.Discover(project).Single(e => e.ConfigPath == created).StateId == null);
            timeline.ContextMenu!.Close();
            Assert.Equal(2, ProjectExperiments.Discover(project).Count);
            menu = OpenAt(replacement.Position);
            Assert.DoesNotContain(menu.Items.OfType<MenuItem>(), m => Equals(m.Header, "Run Boss drop search"));
            Assert.Contains(menu.Items.OfType<MenuItem>().Single(m => Equals(m.Header, "Attach existing experiment")).Items.OfType<MenuItem>(),
                m => Equals(m.Header, "Boss drop search"));
            Assert.Single(menu.Items.OfType<MenuItem>().Single(m => Equals(m.Header, "Remove experiment from state")).Items);
            await Until(() => timeline.ExperimentStateIds.Contains(replacement.Id));
            timeline.ContextMenu!.Close();
            await main.RemoveStateExperiment(replacement, ProjectExperiments.Discover(project).Single(e => e.ConfigPath == path));
            await Until(() => !timeline.ExperimentStateIds.Contains(replacement.Id));
            menu = OpenAt(replacement.Position);
            Assert.DoesNotContain(menu.Items.OfType<MenuItem>(), m => m.Header?.ToString()?.StartsWith("Run ") == true);
            Assert.False(menu.Items.OfType<MenuItem>().Single(m => Equals(m.Header, "Remove experiment from state")).IsEnabled);
            Assert.Equal(2, menu.Items.OfType<MenuItem>().Single(m => Equals(m.Header, "Attach existing experiment")).ItemCount);
            Assert.All(ProjectExperiments.Discover(project), experiment => Assert.Null(experiment.StateId));
            timeline.ContextMenu!.Close();
            var cancelled = main.CreateStateExperiment(replacement);
            await Until(() => main.OwnedWindows.Any(w => w.Title == "Create experiment"));
            main.OwnedWindows.Single(w => w.Title == "Create experiment").Close();
            Assert.Null(await cancelled); Assert.Equal(2, ProjectExperiments.Discover(project).Count);

            ProjectExperiments.SetHidden(project, path, true);
            ProjectExperiments.DeleteFromProject(project, created);
            menu = OpenAt(replacement.Position);
            Assert.Empty(menu.Items.OfType<MenuItem>().Single(m => Equals(m.Header, "Attach existing experiment")).Items);
            Assert.Contains(menu.Items.OfType<MenuItem>(), m => Equals(m.Header, "All project experiments…"));
            timeline.ContextMenu!.Close();
            await main.ShowProjectExperiments();
            await Until(() => main.OwnedWindows.OfType<ProjectExperimentsWindow>().Any(w =>
                w.GetVisualDescendants().OfType<ListBox>().Any(l => l.Name == "ProjectExperimentList" && l.ItemCount == 2)));
            main.OwnedWindows.OfType<ProjectExperimentsWindow>().Single().Close();
            ProjectExperiments.Restore(project, path); ProjectExperiments.Restore(project, created);
            menu = OpenAt(replacement.Position);
            Assert.Equal(2, menu.Items.OfType<MenuItem>().Single(m => Equals(m.Header, "Attach existing experiment")).ItemCount);
            timeline.ContextMenu!.Close();
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
