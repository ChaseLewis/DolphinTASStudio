using TasStudio.Emulation;
using Xunit;

namespace TasStudio.Core.Tests;

public sealed class ExecutionServiceTests
{
    private const uint MemoryAddress = 0x80000000;
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);
    private static readonly ControllerState PressA = ControllerState.Neutral with { Buttons = PadButtons.A };

    [Fact]
    public async Task AllBackendOperationsUseOneDedicatedOwnerThread()
    {
        using var workspace = new TestWorkspace();
        var backend = new FakeBackend();
        var service = new ExecutionService(backend);
        var callerThread = Environment.CurrentManagedThreadId;
        await service.LoadGameAsync(workspace.GamePath, workspace.Options);
        await service.NewProjectAsync();
        await service.StepAsync();
        await service.SaveStateAsync(workspace.FilePath("state.tasstate"));
        await service.LoadStateAsync(workspace.FilePath("state.tasstate"));
        await service.ReadMemoryAsync(MemoryAddress, sizeof(int));
        service.Dispose();
        var owner = Assert.Single(backend.Calls.Select(c => c.Thread).Distinct());
        Assert.NotEqual(callerThread, owner);
        Assert.Equal(nameof(FakeBackend.Dispose), backend.Calls.Last().Operation);
    }

    [Fact]
    public async Task RecordingAndPlaybackRespectStateBeforeInputConvention()
    {
        using var workspace = new TestWorkspace();
        var backend = new FakeBackend();
        using var service = new ExecutionService(backend) { LiveInput = () => PressA };
        await service.LoadGameAsync(workspace.GamePath, workspace.Options);
        await service.NewProjectAsync();
        await service.StepAsync();
        service.LiveInput = () => ControllerState.Neutral;
        await service.StepAsync();
        var expected = await service.ReadMemoryAsync(MemoryAddress, sizeof(int));
        Assert.Equal(new[] { PressA, ControllerState.Neutral }, service.Inputs);
        Assert.Equal(2UL, service.Position);
        Assert.False(service.IsRunning);
        await service.SeekAsync(0);
        Assert.Equal(0UL, service.Position);
        service.LiveInput = () => throw new InvalidOperationException("Playback consulted live input");
        await service.StepAsync();
        Assert.Equal(PressA, backend.SubmittedInputs.Last());
        await service.StepAsync();
        Assert.Equal(expected, await service.ReadMemoryAsync(MemoryAddress, sizeof(int)));
        Assert.Equal(2, service.Inputs.Count);
    }

    [Fact]
    public async Task EditingPastInputLeavesPreviewUntilExplicitSeek()
    {
        using var workspace = new TestWorkspace();
        using var service = new ExecutionService(new FakeBackend());
        await service.LoadGameAsync(workspace.GamePath, workspace.Options);
        await service.NewProjectAsync();
        await service.StepAsync();
        await service.StepAsync();
        var before = await service.ReadMemoryAsync(MemoryAddress, sizeof(int));
        await service.SetInputAsync(0, PressA);
        Assert.Equal(2UL, service.Position);
        Assert.Equal(2, service.Inputs.Count);
        Assert.False(service.IsPreviewCurrent);
        Assert.Equal(before, await service.ReadMemoryAsync(MemoryAddress, sizeof(int)));
        await service.SeekAsync(2);
        var after = await service.ReadMemoryAsync(MemoryAddress, sizeof(int));
        Assert.NotEqual(before, after);
        await service.SeekAsync(0);
        await service.SeekAsync(2);
        Assert.Equal(after, await service.ReadMemoryAsync(MemoryAddress, sizeof(int)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.SeekAsync(3));
        Assert.Equal(2UL, service.Position);
    }

    [Fact]
    public async Task OrderedBoundaryEventsReplayAtZeroAndAtLaterPositions()
    {
        using var workspace = new TestWorkspace();
        using var service = new ExecutionService(new FakeBackend());
        await service.LoadGameAsync(workspace.GamePath, workspace.Options);
        await service.NewProjectAsync();
        await service.WriteMemoryAsync(MemoryAddress, BitConverter.GetBytes(11));
        await service.StepAsync();
        await service.ResetAsync();
        await service.WriteMemoryAsync(MemoryAddress, BitConverter.GetBytes(47));
        await service.StepAsync();
        var expected = await service.ReadMemoryAsync(MemoryAddress, sizeof(int));
        await service.SeekAsync(0);
        Assert.Equal(11, BitConverter.ToInt32(await service.ReadMemoryAsync(MemoryAddress, sizeof(int))));
        await service.SeekAsync(1);
        Assert.Equal(47, BitConverter.ToInt32(await service.ReadMemoryAsync(MemoryAddress, sizeof(int))));
        await service.SeekAsync(2);
        Assert.Equal(expected, await service.ReadMemoryAsync(MemoryAddress, sizeof(int)));
    }

    [Fact]
    public async Task ProjectReopensAcrossServiceInstancesWithExactInputsAndEvents()
    {
        using var workspace = new TestWorkspace();
        const string identity = "fake-core/roundtrip";
        var path = workspace.FilePath("project.tasproj");
        byte[] expected;
        using (var first = new ExecutionService(new FakeBackend { Identity = identity }) { LiveInput = () => PressA })
        {
            await first.LoadGameAsync(workspace.GamePath, workspace.Options with { InternalResolution = 2, DspHle = false });
            await first.NewProjectAsync();
            await first.StepAsync();
            await first.WriteMemoryAsync(MemoryAddress, BitConverter.GetBytes(321));
            await first.StepAsync();
            expected = await first.ReadMemoryAsync(MemoryAddress, sizeof(int));
            await first.SaveProjectAsync(path);
        }
        using var reopened = new ExecutionService(new FakeBackend { Identity = identity });
        await reopened.LoadProjectAsync(path, workspace.Options);
        Assert.True(reopened.HasProject);
        var configuration = await reopened.GetConfigurationAsync();
        Assert.NotNull(configuration);
        Assert.Equal(2, configuration.Options.InternalResolution);
        Assert.False(configuration.Options.DspHle);
        Assert.Equal(identity, configuration.BackendIdentity);
        Assert.Equal(2UL, reopened.Position);
        Assert.Equal(new[] { PressA, PressA }, reopened.Inputs);
        Assert.Equal(expected, await reopened.ReadMemoryAsync(MemoryAddress, sizeof(int)));
        await reopened.SeekAsync(0);
        await reopened.SeekAsync(2);
        Assert.Equal(expected, await reopened.ReadMemoryAsync(MemoryAddress, sizeof(int)));
    }

    [Theory]
    [InlineData("backend")]
    [InlineData("game")]
    [InlineData("configuration")]
    public async Task IncompatibleStateIsRejectedBeforeRestoreAndProjectIsPreserved(string mismatch)
    {
        using var workspace = new TestWorkspace();
        var backend = new FakeBackend();
        using var service = new ExecutionService(backend);
        await service.LoadGameAsync(workspace.GamePath, workspace.Options);
        await service.NewProjectAsync();
        await service.StepAsync();
        var path = workspace.FilePath("state.tasstate");
        await service.SaveStateAsync(path);
        var archive = ProjectArchive.Load(path, ProjectArchive.StateKind);
        var metadata = mismatch switch
        {
            "backend" => archive.Metadata with { BackendIdentity = "wrong-core" },
            "game" => archive.Metadata with { GameHash = "wrong-game" },
            "configuration" => archive.Metadata with { InternalResolution = 2 },
            _ => throw new ArgumentException(nameof(mismatch))
        };
        ProjectArchive.Save(path, metadata, archive.InitialState);
        var restoresBefore = backend.Calls.Count(c => c.Operation == nameof(FakeBackend.Restore));
        await Assert.ThrowsAsync<InvalidDataException>(() => service.LoadStateAsync(path));
        Assert.Equal(restoresBefore, backend.Calls.Count(c => c.Operation == nameof(FakeBackend.Restore)));
        Assert.True(service.HasProject);
        Assert.Equal(1UL, service.Position);
    }

    [Fact]
    public async Task FailedNewProjectKeepsExistingInitialStateAndTimeline()
    {
        using var workspace = new TestWorkspace();
        var backend = new FakeBackend();
        using var service = new ExecutionService(backend);
        await service.LoadGameAsync(workspace.GamePath, workspace.Options);
        await service.NewProjectAsync();
        await service.StepAsync();
        backend.FailNextRestore = true;
        await Assert.ThrowsAsync<InvalidDataException>(() => service.NewProjectAsync());
        Assert.True(service.HasProject);
        Assert.Single(service.Inputs);
        await service.SeekAsync(0);
        Assert.Equal(0, BitConverter.ToInt32(await service.ReadMemoryAsync(MemoryAddress, sizeof(int))));
    }

    [Fact]
    public async Task ProjectBuildPreflightRejectsMismatchWithoutReplacingLiveProject()
    {
        using var workspace = new TestWorkspace();
        var backend = new FakeBackend();
        using var service = new ExecutionService(backend) { LiveInput = () => PressA };
        await service.LoadGameAsync(workspace.GamePath, workspace.Options);
        await service.NewProjectAsync();
        await service.StepAsync();
        var expectedMemory = await service.ReadMemoryAsync(MemoryAddress, sizeof(int));
        var path = workspace.FilePath("different-build.tasproj");
        await service.SaveProjectAsync(path);
        var archive = FolderProject.Load(path).Archive;
        FolderProject.Save(path, archive.Metadata with { BackendIdentity = "another-core-build" }, archive.InitialState, [], [], []);
        var callsBeforeLoad = backend.Calls.Count;
        await Assert.ThrowsAsync<InvalidDataException>(() => service.LoadProjectAsync(path, workspace.Options));
        Assert.DoesNotContain(backend.Calls.Skip(callsBeforeLoad), call =>
            call.Operation is nameof(FakeBackend.Stop) or nameof(FakeBackend.LoadGame) or nameof(FakeBackend.Restore));
        Assert.True(service.IsLoaded);
        Assert.True(service.HasProject);
        Assert.Equal(workspace.GamePath, service.GamePath);
        Assert.Equal(1UL, service.Position);
        Assert.Equal(PressA, Assert.Single(service.Inputs));
        Assert.Equal(expectedMemory, await service.ReadMemoryAsync(MemoryAddress, sizeof(int)));
    }

    [Fact]
    public async Task RunThenPauseAcknowledgesAStableBackendBoundary()
    {
        using var workspace = new TestWorkspace();
        var backend = new FakeBackend();
        using var service = new ExecutionService(backend);
        await service.LoadGameAsync(workspace.GamePath, workspace.Options);
        var progressed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.VideoReady += frame => { if (frame.Sequence >= 2) progressed.TrySetResult(); };
        await service.RunAsync();
        await progressed.Task.WaitAsync(TestTimeout);
        await service.PauseAsync();
        var pausedAt = service.Position;
        Assert.True(pausedAt >= 2);
        Assert.False(service.IsRunning);
        // Multiple queued reads complete without another interval after pause acknowledged.
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => service.ReadMemoryAsync(MemoryAddress, sizeof(int))));
        Assert.Equal(pausedAt, service.Position);
    }

    [Fact]
    public async Task UntrackedStateCannotBypassProjectHistoryValidation()
    {
        using var workspace = new TestWorkspace();
        using var service = new ExecutionService(new FakeBackend());
        await service.LoadGameAsync(workspace.GamePath, workspace.Options);
        await service.StepAsync();
        var expected = await service.ReadMemoryAsync(MemoryAddress, sizeof(int));
        var path = workspace.FilePath("state.tasstate");
        await service.SaveStateAsync(path);
        await service.NewProjectAsync();
        await service.StepAsync();
        await service.StepAsync();
        await Assert.ThrowsAsync<InvalidDataException>(() => service.LoadStateAsync(path));
        Assert.True(service.HasProject);
        await service.LoadGameAsync(workspace.GamePath, workspace.Options);
        await service.LoadStateAsync(path);
        Assert.Equal(1UL, service.Position);
        Assert.False(service.HasProject);
        Assert.Empty(service.Inputs);
        Assert.Equal(expected, await service.ReadMemoryAsync(MemoryAddress, sizeof(int)));
    }

    [Fact]
    public async Task FailedStepDoesNotRecordInputOrAdvanceAndNextCommandWorks()
    {
        using var workspace = new TestWorkspace();
        var backend = new FakeBackend();
        using var service = new ExecutionService(backend);
        await service.LoadGameAsync(workspace.GamePath, workspace.Options);
        await service.NewProjectAsync();
        backend.FailNextStep = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.StepAsync());
        Assert.Empty(service.Inputs);
        Assert.Equal(0UL, service.Position);
        Assert.False(service.IsRunning);
        await service.StepAsync();
        Assert.Single(service.Inputs);
    }

    [Fact]
    public async Task PauseInterruptsLongSeekBetweenBackendSteps()
    {
        using var workspace = new TestWorkspace();
        var backend = new FakeBackend();
        using var service = new ExecutionService(backend);
        await service.LoadGameAsync(workspace.GamePath, workspace.Options);
        await service.NewProjectAsync();
        for (var i = 0; i < 20; i++) await service.SetInputAsync(i, ControllerState.Neutral);
        using var stepEntered = new ManualResetEventSlim();
        using var allowStep = new ManualResetEventSlim();
        backend.BeforeStep = () => { stepEntered.Set(); Assert.True(allowStep.Wait(TestTimeout)); };
        var seek = service.SeekAsync(20);
        Assert.True(stepEntered.Wait(TestTimeout));
        var pause = service.PauseAsync();
        allowStep.Set();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => seek.WaitAsync(TestTimeout));
        await pause.WaitAsync(TestTimeout);
        Assert.Equal(1UL, service.Position);
        Assert.False(service.IsRunning);
        backend.BeforeStep = null;
        await service.SeekAsync(20);
        Assert.Equal(20UL, service.Position);
    }

    [Fact]
    public async Task CanceledSeekDoesNotRestoreOrMoveTheSession()
    {
        using var workspace = new TestWorkspace();
        var backend = new FakeBackend();
        using var service = new ExecutionService(backend);
        await service.LoadGameAsync(workspace.GamePath, workspace.Options);
        await service.NewProjectAsync();
        await service.StepAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var restoreCount = backend.Calls.Count(c => c.Operation == nameof(FakeBackend.Restore));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.SeekAsync(0, cancellation.Token));
        Assert.Equal(1UL, service.Position);
        Assert.Equal(restoreCount, backend.Calls.Count(c => c.Operation == nameof(FakeBackend.Restore)));
    }
}
