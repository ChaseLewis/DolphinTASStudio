using System.Text.Json.Nodes;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TasStudio.App;
using TasStudio.Emulation;
using Xunit;

namespace TasStudio.Core.Tests;

public sealed class ProjectExperimentManagementTests
{
    private static async Task<(string Project, StateMarker State)> Source(TestWorkspace files, ExecutionService execution)
    {
        await execution.LoadGameAsync(files.GamePath, files.Options);
        await execution.NewProjectAsync();
        await execution.StepAsync(); await execution.SaveNamedStateAsync("Start search");
        var project = Path.GetFullPath(files.FilePath("My TAS/movie.tasproj"));
        await execution.SaveProjectAsync(project);
        return (project, Assert.Single(execution.StateMarkers));
    }

    [Fact]
    public async Task LegacyRemovalIsDiscoveredWithoutWritesAndRestoresDetachedWithBackupsIntact()
    {
        using var files = new TestWorkspace(); using var execution = new ExecutionService(new FakeBackend());
        var (project, state) = await Source(files, execution);
        var path = ProjectExperiments.Create(project, state.Id, "Try to improve time");
        var old = path + ".removed-old"; var latest = path + ".removed-latest";
        File.Copy(path, old); File.Move(path, latest);
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-2));
        var original = File.ReadAllBytes(latest);
        var recipe = Assert.Single(ProjectExperiments.Discover(project));
        Assert.True(recipe.WasRemoved); Assert.True(recipe.IsVisible); Assert.Null(recipe.StateId); Assert.Null(recipe.Error);
        Assert.Equal("Try to improve time", recipe.Name); Assert.Equal(path, recipe.ConfigPath);
        Assert.False(File.Exists(path)); Assert.Equal(original, File.ReadAllBytes(latest));

        ProjectExperiments.Restore(project, path);
        recipe = Assert.Single(ProjectExperiments.Discover(project));
        Assert.False(recipe.WasRemoved); Assert.True(recipe.IsVisible); Assert.Null(recipe.StateId);
        var expected = JsonNode.Parse(original)!; expected["StateId"] = null;
        Assert.True(JsonNode.DeepEquals(expected, JsonNode.Parse(File.ReadAllText(path))));
        Assert.Equal(original, File.ReadAllBytes(old)); Assert.Equal(original, File.ReadAllBytes(latest));
        Assert.Throws<InvalidDataException>(() => ProjectExperiments.PrepareRun(path, project, state.Id));
        ProjectExperiments.Attach(path, project, state.Id);
        Assert.Equal(state.Id, Assert.Single(ProjectExperiments.Discover(project)).StateId);
    }

    [Fact]
    public async Task LegacyRemovalCanAttachDirectlyAndForeignRecipesCannotBeModified()
    {
        using var files = new TestWorkspace(); using var execution = new ExecutionService(new FakeBackend());
        var (project, state) = await Source(files, execution);
        var path = ProjectExperiments.Create(project, state.Id, "Search");
        var backup = path + ".removed-old"; File.Move(path, backup);
        var original = File.ReadAllBytes(backup);
        ProjectExperiments.Attach(path, project, state.Id);
        Assert.Equal(state.Id, Assert.Single(ProjectExperiments.Discover(project)).StateId);
        Assert.Equal(original, File.ReadAllBytes(backup));

        var otherProject = files.FilePath("Other TAS/movie.tasproj");
        Assert.Throws<InvalidDataException>(() => ProjectExperiments.SetHidden(otherProject, path, true));
        Assert.Throws<InvalidDataException>(() => ProjectExperiments.DeleteFromProject(otherProject, path));
        Assert.Throws<InvalidDataException>(() => ProjectExperiments.Restore(otherProject, path));
        Assert.True(Assert.Single(ProjectExperiments.Discover(project)).IsVisible);
    }

    [Fact]
    public async Task HideDeleteAndRenameKeepBindingsConfigAndRunHistoryAndCannotBeResetByBatchConfig()
    {
        using var files = new TestWorkspace(); using var execution = new ExecutionService(new FakeBackend());
        var (project, state) = await Source(files, execution);
        var path = ProjectExperiments.Create(project, state.Id, "Search");
        var run = ProjectExperiments.PrepareRun(path, project, state.Id);
        var config = File.ReadAllBytes(path); var snapshot = File.ReadAllBytes(run.ConfigPath);
        ProjectExperiments.SetHidden(project, path, true);
        var recipe = Assert.Single(ProjectExperiments.Discover(project));
        Assert.True(recipe.IsHidden); Assert.False(recipe.IsVisible); Assert.False(recipe.IsDeleted);
        Assert.Equal(state.Id, recipe.StateId);
        ProjectExperiments.DeleteFromProject(project, path);
        ProjectExperiments.Rename(project, path, "Stable experiment");
        Assert.Equal(config, File.ReadAllBytes(path));
        var batchConfig = JsonNode.Parse(config)!; batchConfig["Name"] = "Last successful batch"; batchConfig["DisplayName"] = "Last successful batch";
        File.WriteAllText(path, batchConfig.ToJsonString());
        recipe = Assert.Single(ProjectExperiments.Discover(project));
        Assert.True(recipe.IsHidden); Assert.True(recipe.IsDeleted); Assert.False(recipe.IsVisible); Assert.Equal("Stable experiment", recipe.Name);
        ProjectExperiments.Restore(project, path);
        recipe = Assert.Single(ProjectExperiments.Discover(project));
        Assert.True(recipe.IsVisible); Assert.False(recipe.IsHidden); Assert.False(recipe.IsDeleted);
        Assert.Equal(state.Id, recipe.StateId); Assert.Equal("Stable experiment", recipe.Name);
        Assert.Equal(snapshot, File.ReadAllBytes(run.ConfigPath));
        Assert.True(JsonNode.DeepEquals(batchConfig, JsonNode.Parse(File.ReadAllText(path))));
    }

    [AvaloniaFact]
    public async Task ManagerListsEveryRecipeAndKeepsHiddenDeletedAndLegacyEntriesAccessible()
    {
        using var files = new TestWorkspace(); using var execution = new ExecutionService(new FakeBackend());
        var (project, state) = await Source(files, execution);
        var attached = ProjectExperiments.Create(project, state.Id, "Attached");
        var detached = ProjectExperiments.Create(project, state.Id, "Detached"); ProjectExperiments.Detach(project, detached, state.Id);
        var missing = ProjectExperiments.Create(project, state.Id, "Missing state");
        var config = JsonNode.Parse(File.ReadAllText(missing))!; config["StateId"] = "old-state"; File.WriteAllText(missing, config.ToJsonString());
        var broken = ProjectExperiments.Create(project, state.Id, "Broken"); File.WriteAllText(broken, "{invalid");
        var hidden = ProjectExperiments.Create(project, state.Id, "Hidden"); ProjectExperiments.SetHidden(project, hidden, true);
        var deleted = ProjectExperiments.Create(project, state.Id, "Deleted"); ProjectExperiments.DeleteFromProject(project, deleted);
        var legacy = ProjectExperiments.Create(project, state.Id, "Legacy"); File.Move(legacy, legacy + ".removed-old");
        var changed = 0;
        var window = new ProjectExperimentsWindow(project, () => execution.StateMarkers,
            (marker, path) => { ProjectExperiments.Attach(path, project, marker.Id); return Task.CompletedTask; },
            _ => Task.CompletedTask, _ => { }, () => { changed++; return Task.CompletedTask; });
        try
        {
            window.Show(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            T Control<T>(string name) where T : Control => window.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);
            var list = Control<ListBox>("ProjectExperimentList"); var refresh = Control<Button>("RefreshProjectExperiments");
            await Until(() => list.ItemCount == 7 && refresh.IsEnabled);
            ProjectExperiment Entry(string path) => list.Items.Cast<ProjectExperiment>().Single(e => e.ConfigPath == path);
            void Click(string name) => Control<Button>(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Null(Entry(detached).StateId); Assert.Equal("old-state", Entry(missing).StateId);
            Assert.NotNull(Entry(broken).Error); Assert.True(Entry(legacy).WasRemoved);
            list.SelectedItem = Entry(attached); Click("HideProjectExperiment");
            await Until(() => Entry(attached).IsHidden && refresh.IsEnabled);
            Assert.Equal(7, list.ItemCount); Assert.Equal("Unhide", Control<Button>("HideProjectExperiment").Content);
            Click("HideProjectExperiment"); await Until(() => Entry(attached).IsVisible && refresh.IsEnabled);
            Click("DeleteProjectExperiment");
            await Until(() => window.OwnedWindows.Any());
            var dialog = window.OwnedWindows.Single();
            dialog.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "CancelDeleteExperiment").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => refresh.IsEnabled); Assert.False(Entry(attached).IsDeleted);
            Click("DeleteProjectExperiment"); await Until(() => window.OwnedWindows.Any());
            dialog = window.OwnedWindows.Single();
            dialog.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "ConfirmDeleteExperiment").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => Entry(attached).IsDeleted && refresh.IsEnabled);
            Assert.Equal(7, list.ItemCount); Assert.True(File.Exists(attached)); Assert.False(Control<Button>("AttachProjectExperiment").IsEnabled);
            Click("RestoreProjectExperiment"); await Until(() => Entry(attached).IsVisible && refresh.IsEnabled);
            Assert.Equal(state.Id, Entry(attached).StateId);
            list.SelectedItem = Entry(legacy); Click("RestoreProjectExperiment");
            await Until(() => !Entry(legacy).WasRemoved && refresh.IsEnabled); Assert.Null(Entry(legacy).StateId);
            Control<ComboBox>("ProjectExperimentState").SelectedIndex = 0;
            Assert.True(Control<Button>("AttachProjectExperiment").IsEnabled);
            Click("AttachProjectExperiment"); await Until(() => Entry(legacy).StateId == state.Id && refresh.IsEnabled);
            Assert.Equal(7, list.ItemCount); Assert.True(changed >= 7);
        }
        finally { foreach (var dialog in window.OwnedWindows.ToArray()) dialog.Close(); window.Close(); }
    }

    [Fact]
    public async Task BatchSchedulingAcceptsEightWorkersAndPreservesIdentityParametersAndFrozenRuns()
    {
        using var files = new TestWorkspace(); using var execution = new ExecutionService(new FakeBackend());
        var (project, state) = await Source(files, execution);
        var path = ProjectExperiments.Create(project, state.Id, "Stable experiment");
        var config = JsonNode.Parse(File.ReadAllText(path))!; config["FutureField"] = "keep";
        File.WriteAllText(path, config.ToJsonString());
        var oldRun = ProjectExperiments.PrepareRun(path, project, state.Id);
        var frozen = File.ReadAllBytes(oldRun.ConfigPath);
        ProjectExperiments.SetBatchScheduling(project, path, 24, 8);
        config["Count"] = 24; config["Parallelism"] = 8;
        Assert.True(JsonNode.DeepEquals(config, JsonNode.Parse(File.ReadAllText(path))));
        Assert.Equal("Stable experiment", ProjectExperiments.DisplayName(path));
        Assert.Equal(frozen, File.ReadAllBytes(oldRun.ConfigPath));
        var next = ExperimentFiles.Read<CSharpExperimentFile>(ProjectExperiments.PrepareRun(path, project, state.Id).ConfigPath);
        Assert.Equal(24, next.Count); Assert.Equal(8, next.Parallelism);
        var bytes = File.ReadAllBytes(path);
        foreach (var invalid in new[] { (Count: 0, Workers: 8), (Count: 1, Workers: 0), (Count: 8, Workers: 9) })
            Assert.Throws<InvalidDataException>(() => ProjectExperiments.SetBatchScheduling(project, path, invalid.Count, invalid.Workers));
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [AvaloniaFact]
    public async Task BatchSettingsFromProjectListSaveEightWorkersAndCancelLeavesConfigIntact()
    {
        using var files = new TestWorkspace(); using var execution = new ExecutionService(new FakeBackend());
        var (project, state) = await Source(files, execution);
        var path = ProjectExperiments.Create(project, state.Id, "Search");
        var main = new MainWindow(["--layout-file", files.FilePath("layout.json")], new AppSettings(), execution);
        typeof(MainWindow).GetField("_projectPath", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(main, project);
        try
        {
            main.ShowEditor(); main.Show(); main.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            await main.ShowProjectExperiments();
            var manager = main.OwnedWindows.OfType<ProjectExperimentsWindow>().Single();
            await Until(() => manager.GetVisualDescendants().OfType<Button>().Any(b => b.Name == "ConfigureProjectExperimentBatch" && b.IsEnabled));
            var settings = manager.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "ConfigureProjectExperimentBatch");
            settings.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => main.OwnedWindows.Any(w => w.Title == "Search — Batch settings"));
            var dialog = main.OwnedWindows.Single(w => w.Title == "Search — Batch settings");
            var workers = dialog.GetVisualDescendants().OfType<NumericUpDown>().Single(n => n.Name == "ExperimentWorkerCount");
            var count = dialog.GetVisualDescendants().OfType<NumericUpDown>().Single(n => n.Name == "ExperimentTrialCount");
            var save = dialog.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "SaveExperimentBatchSettings");
            Assert.Equal(8m, workers.Maximum); workers.Value = 8; count.Value = 1;
            Assert.StartsWith("Up to 1 trial will", dialog.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "ExperimentWorkerSummary").Text);
            count.Value = 1.5m; Assert.False(save.IsEnabled);
            count.Value = 16; Assert.True(save.IsEnabled); save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => settings.IsEnabled && ExperimentFiles.Read<CSharpExperimentFile>(path).Parallelism == 8);
            Assert.Equal(16, ExperimentFiles.Read<CSharpExperimentFile>(path).Count);
            var bytes = File.ReadAllBytes(path);
            settings.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => main.OwnedWindows.Any(w => w.Title == "Search — Batch settings"));
            dialog = main.OwnedWindows.Single(w => w.Title == "Search — Batch settings");
            dialog.GetVisualDescendants().OfType<NumericUpDown>().Single(n => n.Name == "ExperimentWorkerCount").Value = 2;
            dialog.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "CancelExperimentBatchSettings").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => settings.IsEnabled); Assert.Equal(bytes, File.ReadAllBytes(path));
            manager.Close();
        }
        finally { foreach (var window in main.OwnedWindows.ToArray()) window.Close(); main.CloseAfterCapture(); }
    }

    private static async Task Until(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 300 && !condition(); attempt++)
        { await Task.Delay(10); Dispatcher.UIThread.RunJobs(); }
        Assert.True(condition());
    }
}
