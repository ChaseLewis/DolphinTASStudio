using TasStudio.Emulation;
using Xunit;

namespace TasStudio.Core.Tests;

public sealed class MoveInputTests
{
    private static ControllerState Input(int index) => ControllerState.Neutral with
    { Buttons = index % 2 == 0 ? PadButtons.A : PadButtons.B, StickX = (byte)(index + 1), TriggerL = (byte)(index * 7) };

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task OverlappingMovesPreserveAllControlsAndUndoRedo(int destination)
    {
        using var files = new TestWorkspace(); using var service = new ExecutionService(new FakeBackend { RecordPolls = true });
        await Record(service, files, 7);
        var before = service.Inputs.ToArray(); var revision = service.Revision;
        await service.MoveInputRangeAsync(2, 3, null, destination);
        Assert.Equal(revision + 1, service.Revision);
        Assert.Equal(before.AsSpan(2, 3).ToArray(), service.Inputs.Skip(destination).Take(3));
        Assert.Equal(ControllerState.Neutral, service.Inputs[destination == 1 ? 4 : 2]);
        Assert.Equal(before[0], service.Inputs[0]); Assert.Equal(before[6], service.Inputs[6]);
        Assert.Equal(7, service.Inputs.Count); Assert.Equal(7UL, service.Position); Assert.False(service.IsPreviewCurrent);
        var after = service.Inputs.ToArray();
        await service.UndoAsync(); Assert.Equal(before, service.Inputs); Assert.True(service.IsPreviewCurrent);
        await service.RedoAsync(); Assert.Equal(after, service.Inputs); Assert.False(service.IsPreviewCurrent);
    }

    [Fact]
    public async Task ExtensionNeutralizesGapsPreservesTimingAndPersists()
    {
        using var files = new TestWorkspace(); using var service = new ExecutionService(new FakeBackend { RecordPolls = true });
        await Record(service, files, 6);
        await service.SaveNamedStateAsync("After changed inputs");
        await service.SeekAsync(1); await service.SaveNamedStateAsync("Before changed inputs");
        await service.SeekAsync(6);
        var before = service.Inputs.ToArray(); var polls = service.PollBoundaries.ToArray();
        var first = (await service.GetPollFrameAsync(0))!;
        var source = (await service.GetPollFrameAsync(1))!;
        await service.MoveInputRangeAsync(1, 2, null, 8);
        Assert.Equal(10, service.Inputs.Count);
        Assert.Equal(before.AsSpan(1, 2).ToArray(), service.Inputs.Skip(8));
        foreach (var index in new[] { 1, 2, 6, 7 }) Assert.Equal(ControllerState.Neutral, service.Inputs[index]);
        Assert.Equal(before[3], service.Inputs[3]); Assert.Equal(before[5], service.Inputs[5]);
        Assert.Equal(6UL, service.Position); Assert.False(service.IsPreviewCurrent);
        Assert.Equal("Before changed inputs", Assert.Single(service.StateMarkers).Name);
        Assert.Equal(first.PrefixHash, (await service.GetPollFrameAsync(0))!.PrefixHash);
        var cleared = (await service.GetPollFrameAsync(1))!;
        Assert.Empty(cleared.PrefixHash);
        Assert.Equal(source.Frame.Polls.Select(p => p.TickOffset), cleared.Frame.Polls.Select(p => p.TickOffset));
        Assert.All(cleared.Frame.Polls, p => Assert.Equal(ControllerState.Neutral, p.Input));
        Assert.Null(await service.GetPollFrameAsync(8)); // No source timing copied into the gap.
        await service.UndoAsync(); Assert.Equal(before, service.Inputs); Assert.Equal(polls, service.PollBoundaries);
        await service.RedoAsync(); await service.SeekAsync(10);
        Assert.True(service.IsPreviewCurrent);
        Assert.All((await service.GetPollFrameAsync(8))!.Frame.Polls, p => Assert.Equal(before[1], p.Input));
        var expected = service.Inputs.ToArray(); var memory = await service.ReadMemoryAsync(0x80000000, 4);
        foreach (var recovery in new[] { false, true })
        {
            var path = files.FilePath(recovery ? "recovery.tasproj" : "moved.tasproj");
            if (recovery) await service.SaveRecoveryAsync(path); else await service.SaveProjectAsync(path);
            using var reopened = new ExecutionService(new FakeBackend { RecordPolls = true });
            await reopened.LoadProjectAsync(path, files.Options);
            Assert.Equal(expected, reopened.Inputs); Assert.Equal(memory, await reopened.ReadMemoryAsync(0x80000000, 4));
        }
    }

    [Fact]
    public async Task CandidateMovesStayWithinTheirLaneAndDoNotChangePlayback()
    {
        using var files = new TestWorkspace(); using var service = new ExecutionService(new FakeBackend());
        await Record(service, files, 8);
        var id = await service.CaptureTakeAsync("Candidate", 2, 5);
        var playback = service.Inputs.ToArray(); var original = service.Takes[0].Inputs.ToArray();
        await service.MoveInputRangeAsync(3, 2, id, 5);
        var moved = Assert.Single(service.Takes);
        Assert.Equal(original[0], moved.Inputs[0]);
        Assert.Equal(new[] { ControllerState.Neutral, ControllerState.Neutral }, moved.Inputs.Skip(1).Take(2));
        Assert.Equal(original.Skip(1).Take(2), moved.Inputs.Skip(3));
        Assert.Equal(playback, service.Inputs); Assert.True(service.IsPreviewCurrent); Assert.Equal(8UL, service.Position);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.MoveInputRangeAsync(5, 2, id, 6));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.MoveInputRangeAsync(5, 2, id, 1));
        await service.UndoAsync(); Assert.Equal(original, service.Takes[0].Inputs);
    }

    [Fact]
    public async Task FutureEditsKeepPreviewCurrentAndNoOpsOrInvalidMovesLeaveUndoIntact()
    {
        using var files = new TestWorkspace(); using var service = new ExecutionService(new FakeBackend());
        await Record(service, files, 6); await service.SeekAsync(1);
        var before = service.Inputs.ToArray();
        await service.MoveInputRangeAsync(2, 2, null, 3);
        Assert.True(service.IsPreviewCurrent); Assert.Equal(1UL, service.Position);
        var revision = service.Revision;
        await service.MoveInputRangeAsync(3, 2, null, 3);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.MoveInputRangeAsync(3, 2, null, -1));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.MoveInputRangeAsync(3, 2, null, int.MaxValue));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.MoveInputRangeAsync(5, 2, null, 0));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.MoveInputRangeAsync(3, 2, "missing", 0));
        Assert.Equal(revision, service.Revision);
        await service.UndoAsync(); Assert.Equal(before, service.Inputs);
        await service.EditRangeAsync(0, 6, null, ControllerState.Neutral, ControllerState.KnownButtons, 0b111111);
        revision = service.Revision;
        await service.MoveInputRangeAsync(0, 2, null, 3);
        Assert.Equal(revision, service.Revision);
        await service.UndoAsync(); Assert.Equal(before, service.Inputs);
    }

    private static async Task Record(ExecutionService service, TestWorkspace files, int count)
    {
        await service.LoadGameAsync(files.GamePath, files.Options); await service.NewProjectAsync();
        for (var i = 0; i < count; i++) { var input = Input(i); service.LiveInput = () => input; await service.StepAsync(); }
    }
}
