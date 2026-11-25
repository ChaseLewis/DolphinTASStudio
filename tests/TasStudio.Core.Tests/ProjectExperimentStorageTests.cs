using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TasStudio.App;
using TasStudio.Emulation;
using Xunit;

namespace TasStudio.Core.Tests;

public sealed class ProjectExperimentStorageTests
{
    private static string Write(string path, string text = "temporary state")
    { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, text); return path; }

    private static string Batch(string root, string name)
    {
        var batch = Path.Combine(root, "experiments", name, ".runs", "old-run");
        Write(Path.Combine(batch, "experiment.json"), "{}");
        Write(Path.Combine(batch, "source", "states", "initial.tasstate"));
        Write(Path.Combine(batch, "run-00000", "profile", "StudioStates", "state.tasstate"));
        Write(Path.Combine(batch, "run-00000", "gci", "save.gci"));
        return batch;
    }

    [Fact]
    public async Task ProjectScanIncludesOrphanedAndNeverStartedRunsAndSkipsLiveRuns()
    {
        using var files = new TestWorkspace(); using var service = new ExecutionService(new FakeBackend());
        var project = files.FilePath("project/project.tasproj"); var root = Path.GetDirectoryName(project)!;
        await service.CreateProjectAsync(project, files.GamePath, files.Options);
        await service.SaveNamedStateAsync("main state"); await service.SaveProjectAsync(project);
        var protectedState = FolderProject.Load(project).States.Single().Path; var bytes = File.ReadAllBytes(protectedState);
        var orphan = Batch(root, "deleted-recipe"); var active = Batch(root, "active");
        var run = Path.Combine(root, "experiments", "compile-failed", ".runs", "prepared");
        Write(Path.Combine(run, "request.tascsharp.json"), "{\"SourceProject\":\"source/project.tasproj\"}");
        var neverStarted = Write(Path.Combine(run, "source", "states", "initial.tasstate"));
        var code = Write(Path.Combine(root, "experiments", "deleted-recipe", "Experiment.cs"), "keep code");
        var log = Write(Path.Combine(orphan, "worker.log"), "keep log");
        var exported = Write(Path.Combine(orphan, "export", "keep.tasstate"));
        using var running = ExperimentStorage.HoldBatch(active);
        var preview = ExperimentStorage.CleanProject(project, preview: true);
        Assert.Equal(3, preview.Batches.Count); Assert.Equal(1, preview.InUse); Assert.Equal(4, preview.Files);
        Assert.True(File.Exists(neverStarted)); Assert.False(File.Exists(Path.Combine(orphan, "storage-cleanup.json")));
        var cleanup = ExperimentStorage.CleanProject(project);
        Assert.Equal(preview.Files, cleanup.Files); Assert.Equal(preview.Bytes, cleanup.Bytes); Assert.Equal(1, cleanup.InUse);
        Assert.False(File.Exists(neverStarted)); Assert.False(Directory.Exists(Path.Combine(orphan, "run-00000", "gci")));
        Assert.True(File.Exists(Path.Combine(active, "source", "states", "initial.tasstate")));
        Assert.Equal(bytes, File.ReadAllBytes(protectedState)); Assert.True(File.Exists(exported)); Assert.True(File.Exists(code)); Assert.True(File.Exists(log));
        Assert.Single(FolderProject.Load(project).States);
    }

    [Fact]
    public async Task MainManifestAndUnsavedSessionReferencesProtectEvenBatchOwnedPaths()
    {
        using var files = new TestWorkspace(); using var service = new ExecutionService(new FakeBackend());
        var project = files.FilePath("project/project.tasproj"); var root = Path.GetDirectoryName(project)!;
        await service.CreateProjectAsync(project, files.GamePath, files.Options);
        await service.SaveNamedStateAsync("main state"); await service.SaveProjectAsync(project);
        var mainBatch = Batch(root, "main-reference"); var unsavedBatch = Batch(root, "unsaved-reference");
        var manifest = ExperimentFiles.Read<ProjectManifest>(project);
        var state = manifest.States.Single();
        var referenced = Path.Combine(mainBatch, "source", "states", "initial.tasstate");
        File.Copy(Path.GetFullPath(state.Data.Path, root), referenced, true);
        File.WriteAllText(project, System.Text.Json.JsonSerializer.Serialize(manifest with { States = [state with { Data = state.Data with { Path = Path.GetRelativePath(root, referenced) } }] }));
        var unsaved = Path.Combine(unsavedBatch, "source", "states", "initial.tasstate");
        var cleanup = ExperimentStorage.CleanProject(project, additionalProtectedFiles: [unsaved]);
        Assert.Equal(0, cleanup.Files); Assert.Equal(2, cleanup.Blocked);
        Assert.True(File.Exists(referenced)); Assert.True(File.Exists(unsaved)); Assert.Single(FolderProject.Load(project).States);
    }

    [AvaloniaFact]
    public async Task CleanupWindowPreviewsBeforeRemovingFilesAndReportsFreedSpace()
    {
        using var files = new TestWorkspace(); using var service = new ExecutionService(new FakeBackend());
        var project = files.FilePath("project/project.tasproj");
        await service.CreateProjectAsync(project, files.GamePath, files.Options);
        var batch = Batch(Path.GetDirectoryName(project)!, "old");
        var state = Path.Combine(batch, "source", "states", "initial.tasstate");
        var window = new ExperimentStorageWindow(project, service.GetProtectedStateFilesAsync);
        try
        {
            window.Show(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            T Control<T>(string name) where T : Control => window.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
            await Until(() => Control<Button>("CleanExperimentStorage").IsEnabled);
            Assert.True(File.Exists(state)); Assert.Contains("can be removed", Control<TextBlock>("ExperimentStorageSummary").Text);
            if (Environment.GetEnvironmentVariable("TASSTUDIO_TEST_CAPTURE_DIR") is { Length: > 0 } capture)
            {
                Directory.CreateDirectory(capture); window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                window.CaptureRenderedFrame()!.Save(Path.Combine(capture, "experiment-state-cleanup.png"));
            }
            Control<Button>("CleanExperimentStorage").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => Control<Button>("ScanExperimentStorage").IsEnabled);
            Assert.False(File.Exists(state)); Assert.StartsWith("Freed", Control<TextBlock>("ExperimentStorageSummary").Text);
            Assert.False(Control<Button>("CleanExperimentStorage").IsEnabled);
        }
        finally { window.Close(); }
    }

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition()) { if (DateTime.UtcNow > deadline) throw new TimeoutException(); Dispatcher.UIThread.RunJobs(); await Task.Delay(10); }
    }
}
