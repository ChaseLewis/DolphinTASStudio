using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TasStudio.Emulation;
using TasStudio.Experiment.Fixtures;
using Xunit;

namespace TasStudio.Core.Tests;

public sealed class ExperimentStorageTests
{
    private static void Write(string path, string contents = "temporary state")
    { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, contents); }

    private static ExperimentDefinition Definition() => new(1, "Batch", ExperimentStart.Boot, null, null, 1, 30,
        [new("Trial", JsonSerializer.SerializeToElement(new { }))], AssemblyPath: typeof(TypedExperiment).Assembly.Location,
        TypeName: typeof(TypedExperiment).FullName);

    [Fact]
    public async Task CleanupRemovesOnlyThisBatchsSnapshotsAndKeepsResultsPlaysLogsAndOtherBatches()
    {
        using var files = new TestWorkspace();
        var run = files.FilePath("experiments/search/.runs/current"); var batch = Path.Combine(run, "batch");
        ExperimentFiles.Write(Path.Combine(run, "request.tascsharp.json"), new { SourceProject = "source/project.tasproj" });
        ExperimentFiles.Write(Path.Combine(batch, "experiment.json"), Definition() with { TopPlays = new() });
        var trial = Path.Combine(batch, "run-00001"); var project = Path.Combine(trial, "result.tasproj");
        Write(project); Write(Path.Combine(trial, "states", "state.tasstate"));
        Write(Path.Combine(trial, "profile", "StudioStates", "state.tasstate"));
        Write(Path.Combine(trial, "inputs", "input.json")); Write(Path.Combine(trial, "timelines", "timeline.json"));
        Write(Path.Combine(trial, "attempts", Guid.NewGuid().ToString("N"), "states", "state.tasstate"));
        Write(Path.Combine(batch, "source", "project.tasproj")); Write(Path.Combine(batch, "source", "states", "initial.tasstate"));
        Write(Path.Combine(run, "source", "project.tasproj")); Write(Path.Combine(run, "source", "states", "initial.tasstate"));
        var sibling = files.FilePath("experiments/search/.runs/other/source/states/keep.tasstate"); Write(sibling);
        var userProject = files.FilePath("states/keep.tasstate"); Write(userProject);
        var export = Path.Combine(batch, "export", "keep.tasstate"); Write(export);
        var log = Path.Combine(trial, "error.log"); Write(log, "diagnostics");
        var receipt = new ExperimentResult("Trial", "completed", null, DateTimeOffset.UtcNow, 1, 10, 10, 1,
            JsonSerializer.SerializeToElement(new TypedResult(42, new([], "result"))), project, 0)
            { Plays = [new(0, "Winning path", 42, 0, 1, [1, 2, 3])] };
        ExperimentFiles.Write(Path.Combine(trial, "result.json"), receipt);
        ExperimentFiles.Write(Path.Combine(batch, "results.json"), new[] { receipt });
        await using (var writer = new SerializedExperimentWriter(new SqliteExperimentWriter(typeof(TypedResult), topPlays: new())))
        {
            await writer.InitializeAsync(new(batch, "Batch", 1), default); await writer.WriteAsync(receipt);
        }
        var preview = ExperimentStorage.CleanBatch(batch, preview: true);
        Assert.True(preview.Files >= 10); Assert.True(File.Exists(project));
        Assert.False(File.Exists(Path.Combine(batch, "storage-cleanup.json")));
        var cleaned = ExperimentStorage.CleanBatch(batch);
        Assert.Null(cleaned.Error); Assert.False(cleaned.InUse); Assert.True(cleaned.Files >= 10); Assert.True(cleaned.Bytes > 0);
        Assert.False(Directory.Exists(Path.Combine(batch, "source"))); Assert.False(Directory.Exists(Path.Combine(run, "source")));
        Assert.False(File.Exists(project)); Assert.False(Directory.Exists(Path.Combine(trial, "profile")));
        Assert.Equal("diagnostics", File.ReadAllText(log)); Assert.True(File.Exists(sibling)); Assert.True(File.Exists(userProject)); Assert.True(File.Exists(export));
        Assert.Null(ExperimentFiles.Read<ExperimentResult>(Path.Combine(trial, "result.json")).ProjectPath);
        Assert.Null(Assert.Single(ExperimentFiles.Read<ExperimentResult[]>(Path.Combine(batch, "results.json"))).ProjectPath);
        await using (var reader = new SqliteExperimentWriter(typeof(TypedResult), resume: true))
        {
            await reader.InitializeAsync(new(batch, "Batch", 1), default);
            var stored = reader.ReadTrials()[0]; Assert.Null(stored.ProjectPath); Assert.Equal(42, stored.Value!.Value.GetProperty("Score").GetInt32());
        }
        Assert.Single(ExperimentPlayResults.Read(batch)); Assert.Equal(new byte[] { 1, 2, 3 }, ExperimentPlayResults.Load(batch, 0, 0).Data);
        Assert.Equal(0, ExperimentStorage.CleanBatch(batch).Files);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => ExperimentRunner.ResumeAsync(batch, "unused", "unused", "unused", null, default));
        Assert.Contains("cleaned up", error.Message);
    }

    [Fact]
    public void RunningCoordinatorAndIndependentWorkerLeasesPreventCleanup()
    {
        using var files = new TestWorkspace(); var batch = files.FilePath("batch");
        ExperimentFiles.Write(Path.Combine(batch, "experiment.json"), Definition());
        var state = Path.Combine(batch, "source", "states", "initial.tasstate"); Write(state);
        using (var coordinator = ExperimentStorage.HoldBatch(batch))
        {
            using var worker = ExperimentStorage.HoldBatch(batch);
            Assert.True(ExperimentStorage.CleanBatch(batch).InUse);
            coordinator.Dispose(); // An orphaned child still protects its batch.
            Assert.True(ExperimentStorage.CleanBatch(batch).InUse); Assert.True(File.Exists(state));
        }
        using (var owner = new FileStream(Path.Combine(batch, ".runner.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        { Assert.True(ExperimentStorage.CleanBatch(batch).InUse); Assert.True(File.Exists(state)); }
        Assert.Equal(1, ExperimentStorage.CleanBatch(batch).Files); Assert.False(File.Exists(state));
    }

    [Theory]
    [InlineData("bad receipt")]
    [InlineData("writer failure")]
    public void UnreadableOrUncommittedResultsPreserveSnapshots(string problem)
    {
        using var files = new TestWorkspace(); var batch = files.FilePath("batch");
        ExperimentFiles.Write(Path.Combine(batch, "experiment.json"), Definition());
        var state = Path.Combine(batch, "source", "states", "initial.tasstate"); Write(state);
        Write(Path.Combine(batch, problem == "bad receipt" ? "results.json" : "writer-errors.json"), "invalid");
        Assert.NotNull(ExperimentStorage.CleanBatch(batch).Error); Assert.True(File.Exists(state));
        Assert.False(File.Exists(Path.Combine(batch, "storage-cleanup.json")));
    }

    [Fact]
    public async Task CancelledBatchCleansSnapshotsAfterCommitAndNeverCleansExternalSource()
    {
        using var files = new TestWorkspace(); var batch = files.FilePath("batch");
        var source = files.FilePath("original/project.tasproj"); Write(source, "original");
        Write(Path.Combine(batch, "source", "project.tasproj"));
        using var cancel = new CancellationTokenSource();
        var result = await ExperimentRunner.RunAsync(Definition(), source, batch, Environment.ProcessPath!, "unused", "unused", message =>
        {
            if (!message.StartsWith("Starting")) return;
            Write(Path.Combine(batch, "run-00001", "profile", "StudioStates", "working.tasstate")); cancel.Cancel();
        }, cancel.Token);
        Assert.Equal("cancelled", Assert.Single(result).Status);
        Assert.Equal("original", File.ReadAllText(source)); Assert.False(Directory.Exists(Path.Combine(batch, "source")));
        Assert.False(Directory.Exists(Path.Combine(batch, "run-00001")));
        Assert.True(File.Exists(Path.Combine(batch, "results.sqlite"))); Assert.True(File.Exists(Path.Combine(batch, "storage-cleanup.json")));
    }

    [Fact]
    public async Task PreflightFailurePreservesSnapshotsForAValidRetry()
    {
        using var files = new TestWorkspace(); var batch = files.FilePath("batch");
        ExperimentFiles.Write(Path.Combine(batch, "experiment.json"), Definition());
        var source = Path.Combine(batch, "source", "project.tasproj"); Write(source);
        await Assert.ThrowsAsync<FileNotFoundException>(() => ExperimentRunner.RunAsync(Definition(), source, batch, files.FilePath("missing-worker.exe"), "unused", "unused", null, default));
        Assert.True(File.Exists(source)); Assert.False(File.Exists(Path.Combine(batch, "storage-cleanup.json")));
    }

    [Fact]
    public async Task JunctionInsideGeneratedSourcePreventsAllDeletion()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var files = new TestWorkspace(); var batch = files.FilePath("batch");
        ExperimentFiles.Write(Path.Combine(batch, "experiment.json"), Definition());
        var external = files.FilePath("external"); Write(Path.Combine(external, "keep.tasstate"));
        var link = Path.Combine(batch, "source");
        var start = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{external}\"")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using var process = Process.Start(start)!; await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, await process.StandardError.ReadToEndAsync());
        try
        {
            Assert.NotNull(ExperimentStorage.CleanBatch(batch).Error);
            Assert.True(File.Exists(Path.Combine(external, "keep.tasstate")));
        }
        finally { Directory.Delete(link); }
    }
}
