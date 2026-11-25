using System.Text.Json;
using TasStudio.Emulation;
using Xunit;

namespace TasStudio.Core.Tests;

public sealed class ExperimentStorageMaintenanceTests
{
    private sealed class Observer : IProgress<ExperimentStorageProgress>
    { public List<ExperimentStorageProgress> Updates { get; } = []; public void Report(ExperimentStorageProgress value) => Updates.Add(value); }
    private static string Write(string path, string text = "temporary")
    { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, text); return path; }
    private static string AddState(string project, string id)
    {
        var root = Path.GetDirectoryName(project)!; var bytes = System.Text.Encoding.UTF8.GetBytes(id);
        var asset = new ProjectAsset("states/" + InputCompressionTests.Hash(bytes) + ".tasstate", InputCompressionTests.Hash(bytes));
        File.WriteAllBytes(Path.Combine(root, asset.Path), bytes);
        var manifest = JsonSerializer.Deserialize<ProjectManifest>(File.ReadAllBytes(project))!;
        File.WriteAllBytes(project, JsonSerializer.SerializeToUtf8Bytes(manifest with { States = [.. manifest.States, new(id, id, 0, "history", asset)] }));
        return Path.Combine(root, asset.Path);
    }

    [Fact]
    public void DefaultCleanupFindsArchivedRunsDeletesScreenshotsAndPrunesOldStatesWhileCompressingInputs()
    {
        using var files = new TestWorkspace(); var main = InputCompressionTests.LegacyProject(files.FilePath("project/project.tasproj"));
        var mainState = AddState(main, "main"); var mainBytes = File.ReadAllBytes(main);
        var experiment = files.FilePath("project/experiments/search");
        var retained = InputCompressionTests.LegacyProject(Path.Combine(experiment, "optimized", "project.tasproj"));
        var obsolete = AddState(retained, "obsolete"); var anchor = AddState(retained, "anchor");
        ExperimentFiles.Write(Path.Combine(experiment, "experiment.tascsharp.json"), new { SourceProject = "optimized/project.tasproj", StateId = "anchor" });
        var screenshot = Write(Path.Combine(experiment, "artifacts", "trial-000", "screen.bmp"));
        var export = Write(Path.Combine(experiment, "export", "keep.tasstate"));
        var batch = Path.Combine(experiment, "batch-archive", "older", "nested");
        Write(Path.Combine(batch, "experiment.json"), "{}");
        var batchState = Write(Path.Combine(batch, "source", "states", "initial.tasstate"));
        var log = Write(Path.Combine(batch, "worker.log"));
        var before = FolderProject.Load(retained); var observer = new Observer();
        var preview = ExperimentStorage.CleanProject(main, preview: true, progress: observer);
        Assert.Equal(3, preview.Files); Assert.Equal(3, preview.CompressibleFiles);
        Assert.True(preview.TotalBytes > preview.Bytes); Assert.True(File.Exists(screenshot)); Assert.True(File.Exists(obsolete));
        Assert.Contains(preview.Categories, c => c.Name == "BMP screenshots");
        Assert.Equal("Scanning", observer.Updates[0].Stage);
        Assert.Equal(preview.TotalFiles, observer.Updates[^1].ScannedFiles);
        Assert.True(observer.Updates.Zip(observer.Updates.Skip(1)).All(pair => pair.First.ScannedFiles <= pair.Second.ScannedFiles));
        observer.Updates.Clear();
        var cleaned = ExperimentStorage.CleanProject(main, progress: observer);
        Assert.Equal(0, cleaned.Blocked); Assert.Equal(6, cleaned.Files); Assert.True(cleaned.Bytes > preview.Bytes);
        Assert.False(File.Exists(obsolete)); Assert.False(File.Exists(screenshot)); Assert.False(File.Exists(batchState));
        Assert.True(File.Exists(anchor)); Assert.True(File.Exists(mainState)); Assert.True(File.Exists(log)); Assert.True(File.Exists(export));
        Assert.Equal(mainBytes, File.ReadAllBytes(main)); Assert.Single(FolderProject.Load(main).States);
        var loaded = FolderProject.Load(retained); Assert.Equal("anchor", Assert.Single(loaded.States).Id);
        Assert.Equal(before.Archive.Metadata.Inputs, loaded.Archive.Metadata.Inputs); Assert.Equal(before.Archive.InitialState.Data, loaded.Archive.InitialState.Data);
        Assert.Contains(observer.Updates, p => p.Stage == "Freeing space" && p.TotalFiles > 0);
        Assert.Equal(cleaned.Bytes, observer.Updates[^1].FreedBytes);
        Assert.Equal(0, ExperimentStorage.CleanProject(main).Files);
    }

    [Fact]
    public void MainTimelineReferencesProtectInputsInsideAnExperimentProject()
    {
        using var files = new TestWorkspace(); var main = InputCompressionTests.LegacyProject(files.FilePath("project/project.tasproj"));
        var root = Path.GetDirectoryName(main)!;
        var experiment = InputCompressionTests.LegacyProject(files.FilePath("project/experiments/search/fixture/project.tasproj"));
        var fixtureRoot = Path.GetDirectoryName(experiment)!;
        var fixture = JsonSerializer.Deserialize<ProjectManifest>(File.ReadAllBytes(experiment))!;
        var fixtureMovie = JsonSerializer.Deserialize<MovieData>(File.ReadAllBytes(Path.GetFullPath(fixture.Timeline.Path, fixtureRoot)))!;
        var input = fixtureMovie.Inputs[0]; var inputPath = Path.GetFullPath(input.Path, fixtureRoot);
        var manifest = JsonSerializer.Deserialize<ProjectManifest>(File.ReadAllBytes(main))!;
        var movie = JsonSerializer.Deserialize<MovieData>(File.ReadAllBytes(Path.GetFullPath(manifest.Timeline.Path, root)))!;
        var updated = movie with { Inputs = [input with { Path = Path.GetRelativePath(root, inputPath) }] };
        var timeline = InputCompressionTests.Asset(root, "timelines", JsonSerializer.SerializeToUtf8Bytes(updated));
        File.WriteAllBytes(main, JsonSerializer.SerializeToUtf8Bytes(manifest with { Timeline = timeline }));
        var before = File.ReadAllBytes(main); var expected = FolderProject.Load(main).Archive.Metadata.Inputs;
        var result = ExperimentStorage.CleanProject(main);
        Assert.Equal(0, result.CompressibleFiles); Assert.True(File.Exists(inputPath));
        Assert.Equal(before, File.ReadAllBytes(main)); Assert.Equal(expected, FolderProject.Load(main).Archive.Metadata.Inputs);
        Assert.NotEmpty(FolderProject.Load(experiment).Archive.Metadata.Inputs);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LiveExperimentAndFailedWriterRetainScreenshotsStatesAndPlainInputs(bool writerFailure)
    {
        using var files = new TestWorkspace(); var main = InputCompressionTests.LegacyProject(files.FilePath("project/project.tasproj"));
        var experiment = files.FilePath("project/experiments/search"); var batch = Path.Combine(experiment, "archive", "batch");
        Write(Path.Combine(batch, "experiment.json"), "{}");
        var source = InputCompressionTests.LegacyProject(Path.Combine(batch, "source", "project.tasproj"));
        var obsolete = AddState(source, "old"); var screenshot = Write(Path.Combine(batch, "screen.bmp"));
        if (writerFailure) Write(Path.Combine(batch, "writer-errors.json"), "[]");
        using var active = writerFailure ? null : ExperimentStorage.HoldBatch(batch);
        var result = ExperimentStorage.CleanProject(main);
        Assert.Equal(0, result.Files); Assert.Equal(0, result.CompressibleFiles);
        Assert.Equal(1, writerFailure ? result.Blocked : result.InUse);
        Assert.True(File.Exists(obsolete)); Assert.True(File.Exists(screenshot));
        Assert.Equal(3, Directory.GetFiles(Path.Combine(Path.GetDirectoryName(source)!, "inputs"), "*.json").Length);
        Assert.Single(FolderProject.Load(source).States);
    }
}
