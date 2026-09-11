using System.Text.Json;
using TasStudio.Emulation;
using TasStudio.Sdk;
using Xunit;

namespace TasStudio.Core.Tests;

public sealed class ExperimentCompatibilityTests
{
    private static FakeBackend Backend(string identity = "current", string environment = "current-host") =>
        new() { Identity = identity, ConfigurationIdentity = environment, RecordPolls = true, FieldsPerStep = 2 };

    private static async Task<(string Path, string State)> Source(TestWorkspace files, bool mixed = true)
    {
        using var execution = new ExecutionService(Backend());
        await execution.LoadGameAsync(files.GamePath, files.Options with { Configuration = new() });
        await execution.NewProjectAsync();
        for (var i = 0; i < 3; i++) await execution.StepAsync();
        await execution.SaveNamedStateAsync("Attached state");
        var state = Assert.Single(execution.StateMarkers).Id;
        for (var i = 0; i < 2; i++) await execution.StepAsync();
        var path = files.FilePath("source/project.tasproj");
        await execution.SaveProjectAsync(path);
        if (mixed)
        {
            var source = FolderProject.Load(path);
            FolderProject.Save(path, source.Archive.Metadata with
            {
                RuntimeHistory = [new("older", "older-host"), new("current", "current-host")]
            }, source.Archive.InitialState, source.Takes, source.Sections, source.States, source.AutomaticCheckpoints);
        }
        return (path, state);
    }

    private static ExperimentJob Job(TestWorkspace files, string path, string state, ExperimentStart start) =>
        new("Compatibility", path, files.Options.CorePath, files.Options.SystemDirectory, files.FilePath("trial"), start, state,
            null, JsonSerializer.SerializeToElement(new { }), 30, 0, 1);

    private sealed record ProbeResult(ulong StartGroup, ulong EndGroup);
    private sealed class Probe : IExperiment<ProbeResult>
    {
        public bool Ran { get; private set; }
        public async Task<ProbeResult> RunAsync(ExperimentRunContext context, CancellationToken token)
        {
            Ran = true;
            var start = await context.Emulator.GetPositionAsync();
            await context.Emulator.SetCurrentInputAsync(ControllerState.Neutral);
            await context.Emulator.AdvanceAsync();
            return new(start.Group, (await context.Emulator.GetPositionAsync()).Group);
        }
    }

    [Theory]
    [InlineData(ExperimentStart.SaveState, "current", "current-host")]
    [InlineData(ExperimentStart.Boot, "current", "current-host")]
    [InlineData(ExperimentStart.SaveState, "different", "current-host")]
    [InlineData(ExperimentStart.SaveState, "current", "different-host")]
    [InlineData(ExperimentStart.Boot, "different", "different-host")]
    public async Task MixedRuntimeHistoryWarnsAndRunsUserCode(ExperimentStart start, string identity, string environment)
    {
        using var files = new TestWorkspace(); var (path, state) = await Source(files);
        var original = File.ReadAllBytes(path);
        using var worker = new ExecutionService(Backend(identity, environment));
        var probe = new Probe(); var job = Job(files, path, state, start);
        var result = await ExperimentWorker.RunAsync(job, worker, CancellationToken.None, probe);
        Assert.True(probe.Ran); Assert.Equal("completed", result.Status); Assert.Null(result.Error);
        Assert.Contains("different emulator build", result.CompatibilityWarning);
        Assert.Equal(start == ExperimentStart.SaveState ? 3UL : 0UL, result.Value!.Value.GetProperty("StartGroup").GetUInt64());
        var receipt = ExperimentFiles.Read<ExperimentResult>(Path.Combine(job.OutputDirectory, "result.json"));
        Assert.Equal(result.CompatibilityWarning, receipt.CompatibilityWarning);
        Assert.Equal(original, File.ReadAllBytes(path));
        var log = File.ReadAllLines(Path.Combine(job.OutputDirectory, "output.jsonl")).Select(line => JsonSerializer.Deserialize<JsonElement>(line)).ToArray();
        var warningIndex = Array.FindIndex(log, line => line.GetProperty("kind").GetString() == "warning");
        var startIndex = Array.FindIndex(log, line => line.GetProperty("kind").GetString() == "script-start");
        Assert.True(warningIndex >= 0 && warningIndex < startIndex);
        Assert.Equal("runtime-compatibility", log[warningIndex].GetProperty("data").GetProperty("code").GetString());
    }

    [Fact]
    public async Task MatchingRuntimeHasNoWarning()
    {
        using var files = new TestWorkspace(); var (path, state) = await Source(files, mixed: false);
        using var worker = new ExecutionService(Backend());
        var result = await ExperimentWorker.RunAsync(Job(files, path, state, ExperimentStart.SaveState), worker, CancellationToken.None, new Probe());
        Assert.Equal("completed", result.Status); Assert.Null(result.CompatibilityWarning);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ActualRestoreAndRomFailuresStillStopTheTrial(bool restoreFailure)
    {
        using var files = new TestWorkspace(); var (path, state) = await Source(files);
        var backend = Backend(); backend.FailNextRestore = restoreFailure;
        if (!restoreFailure) File.WriteAllText(files.GamePath, "Different ROM bytes");
        using var worker = new ExecutionService(backend); var probe = new Probe();
        var result = await ExperimentWorker.RunAsync(Job(files, path, state, ExperimentStart.SaveState), worker, CancellationToken.None, probe);
        Assert.False(probe.Ran); Assert.Equal("failed", result.Status);
        Assert.Contains(restoreFailure ? "Injected restore failure" : "game image hash", result.Error);
    }
}
