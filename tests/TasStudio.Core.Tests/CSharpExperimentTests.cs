using System.Security.Cryptography;
using System.Text.Json;
using TasStudio.Core;
using TasStudio.Emulation;
using TasStudio.Sdk;
using Xunit;

namespace TasStudio.Core.Tests;

public sealed class CSharpExperimentTests
{
    [Fact]
    public async Task WorkerSerializesTypedResultsIncludingNestedStructFields()
    {
        using var files = new TestWorkspace(); var path = await Source(files);
        using var execution = new ExecutionService(new FakeBackend());
        var result = await ExperimentWorker.RunAsync(Job(files, path, ExperimentStart.Boot), execution,
            CancellationToken.None, new TypedWorkerExperiment());
        Assert.Equal("completed", result.Status);
        Assert.Equal(7, result.Value!.Value.GetProperty("Details").GetProperty("Count").GetInt32());
    }

    private sealed record TypedWorkerResult(TypedWorkerDetails Details);
    private readonly struct TypedWorkerDetails(int count) { public readonly int Count = count; }
    private sealed class TypedWorkerExperiment : IExperiment<TypedWorkerResult>
    {
        public Task<TypedWorkerResult> RunAsync(ExperimentRunContext context, CancellationToken token) =>
            Task.FromResult(new TypedWorkerResult(new(7)));
    }

    [Fact]
    public async Task RollingStatesDeleteOwnedFilesAndRetainOnlyTheReplacementInResults()
    {
        using var files = new TestWorkspace(); var path = await Source(files);
        var sourceHash = SHA256.HashData(File.ReadAllBytes(path));
        using var execution = new ExecutionService(new FakeBackend());
        var job = Job(files, path, ExperimentStart.Boot);
        var result = await ExperimentWorker.RunAsync(job, execution, CancellationToken.None,
            new DelegateExperiment { Run = async (ctx, _) =>
            {
                var emulator = ctx.Emulator;
                var state = await emulator.SaveStateAsync("Rolling");
                string[] StateFiles() => Directory.GetFiles(Path.Combine(job.OutputDirectory, "profile"), "*.tasstate", SearchOption.AllDirectories);
                for (var i = 0; i < 12; i++)
                {
                    var oldFile = Assert.Single(StateFiles());
                    await emulator.LoadStateAsync(state);
                    await emulator.AdvanceAsync();
                    var next = await emulator.SaveStateAsync("Rolling");
                    Assert.Equal(2, StateFiles().Length);
                    var position = await emulator.GetPositionAsync();
                    await emulator.DeleteStateAsync(state);
                    Assert.False(File.Exists(oldFile));
                    Assert.Single(StateFiles());
                    Assert.Equal(next, Assert.Single(await emulator.GetStatesAsync()).Id);
                    Assert.Equal(position, await emulator.GetPositionAsync());
                    await Assert.ThrowsAsync<InvalidDataException>(() => emulator.LoadStateAsync(state));
                    state = next;
                }
                await emulator.LoadStateAsync(state);
                return state;
            } });
        Assert.Equal("completed", result.Status);
        Assert.Equal(12UL, result.Position);
        Assert.Single(FolderProject.Load(result.ProjectPath!).States);
        Assert.Equal(sourceHash, SHA256.HashData(File.ReadAllBytes(path)));
    }

    [Fact]
    public async Task DeleteInheritedStateOnlyRemovesWorkerReference()
    {
        using var files = new TestWorkspace();
        using var source = new ExecutionService(new FakeBackend());
        await source.LoadGameAsync(files.GamePath, files.Options); await source.NewProjectAsync();
        await source.StepAsync(); await source.SaveNamedStateAsync("Main state");
        var id = Assert.Single(source.StateMarkers).Id;
        var path = files.FilePath("source/project.tasproj"); await source.SaveProjectAsync(path);
        var stateFile = Assert.Single(FolderProject.Load(path).States).Path;
        var stateHash = SHA256.HashData(File.ReadAllBytes(stateFile));
        var projectHash = SHA256.HashData(File.ReadAllBytes(path));
        using var execution = new ExecutionService(new FakeBackend());
        var result = await ExperimentWorker.RunAsync(Job(files, path, ExperimentStart.SaveState) with { StateId = id }, execution, CancellationToken.None,
            new DelegateExperiment { Run = async (ctx, _) =>
            {
                await ctx.Emulator.DeleteStateAsync(id);
                Assert.Empty(await ctx.Emulator.GetStatesAsync());
                await Assert.ThrowsAsync<InvalidDataException>(() => ctx.Emulator.DeleteStateAsync(id));
                return null;
            } });
        Assert.Equal("completed", result.Status);
        Assert.Empty(FolderProject.Load(result.ProjectPath!).States);
        Assert.Equal(stateHash, SHA256.HashData(File.ReadAllBytes(stateFile)));
        Assert.Equal(projectHash, SHA256.HashData(File.ReadAllBytes(path)));
        await source.LoadMarkerAsync(id);
        Assert.Equal(id, Assert.Single(source.StateMarkers).Id);
    }

    [Fact]
    public async Task FailedStateDeletionIsReportedAndCanBeRetried()
    {
        if (!OperatingSystem.IsWindows()) return; // Windows denies deletion without FileShare.Delete.
        using var files = new TestWorkspace(); var path = await Source(files);
        using var execution = new ExecutionService(new FakeBackend());
        var job = Job(files, path, ExperimentStart.Boot);
        var result = await ExperimentWorker.RunAsync(job, execution, CancellationToken.None,
            new DelegateExperiment { Run = async (ctx, _) =>
            {
                var id = await ctx.Emulator.SaveStateAsync("Locked");
                var stateFile = Assert.Single(Directory.GetFiles(Path.Combine(job.OutputDirectory, "profile"), "*.tasstate", SearchOption.AllDirectories));
                using (var held = new FileStream(stateFile, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    await Assert.ThrowsAsync<IOException>(() => ctx.Emulator.DeleteStateAsync(id));
                    Assert.Equal(id, Assert.Single(await ctx.Emulator.GetStatesAsync()).Id);
                    Assert.True(File.Exists(stateFile));
                }
                await ctx.Emulator.DeleteStateAsync(id);
                Assert.Empty(await ctx.Emulator.GetStatesAsync());
                Assert.False(File.Exists(stateFile));
                return null;
            } });
        Assert.Equal("completed", result.Status);
        Assert.Empty(FolderProject.Load(result.ProjectPath!).States);
    }

    [Fact]
    public async Task SetCurrentInputReplacesNextGroupAndPersistsUnadvancedEndWithoutChangingOriginal()
    {
        using var files = new TestWorkspace(); var path = await Source(files);
        var sourceHash = SHA256.HashData(File.ReadAllBytes(path));
        var backend = new FakeBackend { RecordPolls = true, FieldsPerStep = 2 };
        using var execution = new ExecutionService(backend);
        var pressed = ControllerState.Neutral with { Buttons = PadButtons.A, StickX = 210 };
        var final = ControllerState.Neutral with { Buttons = PadButtons.X, TriggerR = 80 };
        var appended = ControllerState.Neutral with { Buttons = PadButtons.Y };
        var result = await ExperimentWorker.RunAsync(Job(files, path, ExperimentStart.Boot), execution, CancellationToken.None,
            new DelegateExperiment { Run = async (ctx, _) =>
            {
                var emulator = ctx.Emulator;
                await emulator.AdvanceAsync(count: 3);
                var later = await emulator.SaveStateAsync("Later");
                await emulator.SeekAsync(0);
                var here = await emulator.SaveStateAsync("Here");
                await emulator.SetCurrentInputAsync(pressed);
                await emulator.SetCurrentInputAsync(final);
                Assert.Equal(0UL, (await emulator.GetPositionAsync()).Group);
                Assert.True((await emulator.GetPositionAsync()).IsCurrent);
                Assert.Equal(3, (await emulator.GetPositionAsync()).InputCount);
                Assert.Equal(final, await emulator.GetInputAsync(0));
                Assert.Equal(ControllerState.Neutral, ctx.Movie[0]);
                Assert.DoesNotContain(await emulator.GetStatesAsync(), s => s.Id == later);
                Assert.Contains(await emulator.GetStatesAsync(), s => s.Id == here && s.Valid);
                await emulator.AdvanceAsync(pressed); // Explicitly authored recording wins over advance's fallback input.
                Assert.Equal(1UL, (await emulator.GetPositionAsync()).Group);
                Assert.All((await execution.GetPollFrameAsync(0))!.Frame.Polls, poll => Assert.Equal(final, poll.Input));
                Assert.Equal(ControllerState.Neutral, await emulator.GetInputAsync(1));
                await emulator.SeekAsync(3);
                await emulator.SetCurrentInputAsync(appended);
                Assert.Equal(3UL, (await emulator.GetPositionAsync()).Group);
                Assert.Equal(4, (await emulator.GetPositionAsync()).InputCount);
                return null; // This authored last group must persist even without an advance.
            } });
        Assert.Equal("completed", result.Status); Assert.Equal(3UL, result.Position);
        Assert.Equal(new[] { final, ControllerState.Neutral, ControllerState.Neutral, appended }, FolderProject.Load(result.ProjectPath!).Archive.Metadata.Inputs);
        Assert.Equal(sourceHash, SHA256.HashData(File.ReadAllBytes(path)));
        Assert.Single(backend.Calls.Select(c => c.Thread).Distinct());
    }

    [Fact]
    public async Task SetCurrentInputValidatesStateAndDoesNotRepairEarlierEdits()
    {
        using var files = new TestWorkspace(); var path = await Source(files);
        using var execution = new ExecutionService(new FakeBackend());
        var result = await ExperimentWorker.RunAsync(Job(files, path, ExperimentStart.Boot), execution, CancellationToken.None,
            new DelegateExperiment { Run = async (ctx, _) =>
            {
                var emulator = ctx.Emulator;
                await Assert.ThrowsAsync<ArgumentException>(() => emulator.SetCurrentInputAsync(ControllerState.Neutral with { Buttons = (PadButtons)0x8000 }));
                Assert.Equal(ControllerState.Neutral, await emulator.GetInputAsync(0));
                await emulator.AdvanceAsync();
                await emulator.SetInputAsync(0, ControllerState.Neutral with { Buttons = PadButtons.B });
                await emulator.SetCurrentInputAsync(ControllerState.Neutral with { Buttons = PadButtons.X });
                await Assert.ThrowsAsync<InvalidOperationException>(() => emulator.AdvanceAsync());
                Assert.Equal(1UL, (await emulator.GetPositionAsync()).Group);
                await emulator.SeekAsync(1); await emulator.AdvanceAsync();
                return null;
            } });
        Assert.Equal("completed", result.Status); Assert.Equal(2UL, result.Position);
    }

    [Fact]
    public async Task InitializePrecedesSingleBootAndRuntimeUsesFrozenMovieAndOwnerThread()
    {
        using var files = new TestWorkspace();
        using var source = new ExecutionService(new FakeBackend { RecordPolls = true });
        await source.LoadGameAsync(files.GamePath, files.Options with { Configuration = new() }); await source.NewProjectAsync();
        source.LiveInput = () => ControllerState.Neutral with { Buttons = PadButtons.B };
        for (var i = 0; i < 5; i++) await source.StepAsync();
        var path = files.FilePath("source/project.tasproj"); await source.SaveProjectAsync(path);
        var hash = SHA256.HashData(File.ReadAllBytes(path));
        var backend = new FakeBackend { RecordPolls = true }; using var execution = new ExecutionService(backend);
        var experiment = new DelegateExperiment
        {
            Init = ctx =>
            {
                Assert.Empty(backend.Calls); Assert.Equal(3, ctx.Index); Assert.Equal(7, ctx.Count);
                Assert.Equal(946684800, ctx.DefaultStartUtcSeconds);
                return new(ctx.DefaultStartUtcSeconds + ctx.Index);
            },
            Run = async (ctx, token) =>
            {
                Assert.Equal(2UL, (await ctx.Emulator.GetPositionAsync()).Group);
                Assert.Equal(946684803, await ctx.Emulator.GetStartUtcAsync());
                Assert.Throws<NotSupportedException>(() => ((IList<ControllerState>)ctx.Movie)[0] = ControllerState.Neutral);
                await ctx.Emulator.AdvanceAsync(ControllerState.Neutral with { Buttons = PadButtons.A });
                Assert.Equal(PadButtons.B, (await ctx.Emulator.GetInputAsync(2)).Buttons);
                await ctx.Emulator.SetInputAsync(1, ControllerState.Neutral with { Buttons = PadButtons.X });
                Assert.Equal(PadButtons.B, ctx.Movie[1].Buttons);
                await Assert.ThrowsAsync<InvalidOperationException>(() => ctx.Emulator.AdvanceAsync());
                await ctx.Emulator.SeekAsync(3);
                await ctx.Emulator.WriteU32Async(0x80000000, 0x12345678);
                Assert.Equal(0x12345678U, await ctx.Emulator.ReadU32Async(0x80000000));
                var saved = await ctx.Emulator.SaveStateAsync("Runtime state");
                await ctx.Emulator.PlayAsync(); await ctx.Emulator.LoadStateAsync(saved);
                await ctx.Emulator.AddTakeAsync("Candidate", 3, [ControllerState.Neutral with { Buttons = PadButtons.A }]);
                ctx.Log(new { ctx.Index, Message = "done" });
                return new { ctx.Index, Position = (await ctx.Emulator.GetPositionAsync()).Group };
            }
        };
        var job = Job(files, path, ExperimentStart.Boot) with { Index = 3, Count = 7, PrerollGroups = 2 };
        var result = await ExperimentWorker.RunAsync(job, execution, CancellationToken.None, experiment);
        Assert.Equal("completed", result.Status); Assert.Equal(3UL, result.Position);
        Assert.Equal(1, backend.Calls.Count(c => c.Operation == nameof(FakeBackend.LoadGame)));
        Assert.Equal(946684803, backend.LastLoadOptions!.Configuration!.StartUtcSeconds);
        Assert.Single(backend.Calls.Select(c => c.Thread).Distinct());
        Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(path)));
        var savedResult = FolderProject.Load(result.ProjectPath!);
        Assert.Single(savedResult.Takes); Assert.Single(savedResult.Archive.Metadata.Events);
        Assert.Equal(946684803, savedResult.Archive.Metadata.Configuration!.StartUtcSeconds);
        Assert.Equal(5UL, source.Position);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(4294967296)]
    public async Task InvalidInitializationUtcFailsBeforeBoot(long utc)
    {
        using var files = new TestWorkspace(); var path = await Source(files);
        var backend = new FakeBackend(); using var execution = new ExecutionService(backend);
        var result = await ExperimentWorker.RunAsync(Job(files, path, ExperimentStart.Boot), execution, CancellationToken.None,
            new DelegateExperiment { Init = _ => new(utc) });
        Assert.Equal("failed", result.Status); Assert.Empty(backend.Calls); Assert.Null(result.ProjectPath);
    }

    [Fact]
    public async Task StateStartRejectsUtcAndCancellationPreventsRun()
    {
        using var files = new TestWorkspace(); var path = await Source(files);
        var backend = new FakeBackend(); using var execution = new ExecutionService(backend);
        var result = await ExperimentWorker.RunAsync(Job(files, path, ExperimentStart.SaveState), execution, CancellationToken.None,
            new DelegateExperiment { Init = _ => new(946684801) });
        Assert.Equal("failed", result.Status); Assert.Contains("saved state", result.Error); Assert.Empty(backend.Calls);
        using var cancellation = new CancellationTokenSource();
        result = await ExperimentWorker.RunAsync(Job(files, path, ExperimentStart.Boot) with { OutputDirectory = files.FilePath("cancelled") }, execution, cancellation.Token,
            new DelegateExperiment { Init = _ => { cancellation.Cancel(); return new(); } });
        Assert.Equal("cancelled", result.Status); Assert.Empty(backend.Calls);
    }

    [Fact]
    public async Task RuntimeContextClosesAfterRunAndErrorKeepsCompletedEdits()
    {
        using var files = new TestWorkspace(); var path = await Source(files);
        using var execution = new ExecutionService(new FakeBackend()); IExperimentEmulator? retained = null;
        var result = await ExperimentWorker.RunAsync(Job(files, path, ExperimentStart.Boot), execution, CancellationToken.None,
            new DelegateExperiment { Run = async (ctx, _) =>
            {
                retained = ctx.Emulator;
                await ctx.Emulator.SetInputAsync(0, ControllerState.Neutral with { Buttons = PadButtons.A });
                throw new InvalidOperationException("intentional failure");
            } });
        Assert.Equal("failed", result.Status); Assert.Equal("intentional failure", result.Error);
        Assert.Equal(PadButtons.A, FolderProject.Load(result.ProjectPath!).Archive.Metadata.Inputs[0].Buttons);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => retained!.AdvanceAsync());
    }

    [Fact]
    public void InitializationContractDoesNotExposeEmulationCapabilities()
    {
        var properties = typeof(ExperimentInitializationContext).GetProperties().Select(p => p.Name).Order().ToArray();
        Assert.Equal(new[] { "CancellationToken", "Count", "DefaultStartUtcSeconds", "Index", "Parameters", "StartingPoint" }, properties);
    }

    private static ExperimentJob Job(TestWorkspace files, string source, ExperimentStart start) => new("C# test", source,
        files.Options.CorePath, files.Options.SystemDirectory, files.FilePath("run"), start, "missing", null,
        JsonSerializer.SerializeToElement(new { }), 10, 0, 1);
    private static async Task<string> Source(TestWorkspace files)
    {
        using var source = new ExecutionService(new FakeBackend()); await source.LoadGameAsync(files.GamePath, files.Options); await source.NewProjectAsync();
        await source.StepAsync(); var path = files.FilePath("source/project.tasproj"); await source.SaveProjectAsync(path); return path;
    }
    private sealed class DelegateExperiment : IExperiment
    {
        public Func<ExperimentInitializationContext, ExperimentBootOptions> Init { get; init; } = _ => new();
        public Func<ExperimentRunContext, CancellationToken, Task<object?>> Run { get; init; } = (_, _) => throw new Exception("Run should not execute.");
        public ExperimentBootOptions Initialize(ExperimentInitializationContext context) => Init(context);
        public Task<object?> RunAsync(ExperimentRunContext context, CancellationToken token) => Run(context, token);
    }
}
