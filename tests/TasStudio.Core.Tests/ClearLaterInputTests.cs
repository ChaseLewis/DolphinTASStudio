using TasStudio.Emulation;
using Xunit;

namespace TasStudio.Core.Tests;

public sealed class ClearLaterInputTests
{
    [Fact]
    public async Task TruncationPreservesBoundaryAndCandidatesAndRoundTripsThroughUndoAndDisk()
    {
        using var files = new TestWorkspace();
        var backend = new FakeBackend { RecordPolls = true };
        using var service = new ExecutionService(backend);
        await service.LoadGameAsync(files.GamePath, files.Options); await service.NewProjectAsync();
        await service.ConfigureCheckpointsAsync(new(IntervalSeconds: 1));
        for (var i = 0; i < 30; i++) await service.StepAsync();
        await service.WriteMemoryAsync(0x80000020, BitConverter.GetBytes(17));
        await service.SaveNamedStateAsync("Cutoff");
        var boundaryHash = await service.HistoryAtAsync(30);
        for (var i = 30; i < 80; i++) await service.StepAsync();
        await service.WriteMemoryAsync(0x80000020, BitConverter.GetBytes(29));
        await service.SaveNamedStateAsync("Later");
        var candidate = await service.CaptureTakeAsync("Candidate", 60, 10);
        var crossing = await service.CaptureTakeAsync("Crossing", 20, 20);
        await service.UseTakeAsync(crossing, 20, 20);
        await service.AddTagAsync(75, "Retry here");
        var inputs = service.Inputs.ToArray(); var polls = service.PollBoundaries.ToArray();
        Assert.Contains(service.StateMarkers, marker => marker.Automatic);

        var retainedGroup = (await service.GetPollFrameAsync(29))!;
        await service.ClearLaterInputAsync(29);
        Assert.Equal(30, service.Inputs.Count); Assert.Equal(30UL, service.Position);
        Assert.True(service.IsPreviewCurrent); Assert.False(service.IsRunning);
        Assert.Equal(boundaryHash, await service.HistoryAtAsync(30));
        Assert.Equal(BitConverter.GetBytes(17), await service.ReadMemoryAsync(0x80000020, 4));
        Assert.Equal(polls.Take(31), service.PollBoundaries);
        Assert.Equal(inputs[29], service.Inputs[29]);
        Assert.Equal(retainedGroup.Frame.Polls, (await service.GetPollFrameAsync(29))!.Frame.Polls);
        Assert.Null(await service.GetPollFrameAsync(30));
        Assert.Equal("Cutoff", Assert.Single(service.StateMarkers).Name);
        Assert.Equal(10, Assert.Single(service.Sections).Length);
        Assert.Equal(2, service.Takes.Count); Assert.Single(service.Tags);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.AuditionTakeAsync(candidate, 65));

        await service.UndoAsync();
        Assert.Equal(inputs, service.Inputs); Assert.Equal(polls, service.PollBoundaries);
        Assert.Equal(20, Assert.Single(service.Sections).Length);
        Assert.NotNull(await service.GetPollFrameAsync(79));
        await service.SeekAsync(80);
        Assert.Equal(BitConverter.GetBytes(29), await service.ReadMemoryAsync(0x80000020, 4));
        await service.RedoAsync();
        Assert.Equal(30UL, service.Position); Assert.Equal(30, service.Inputs.Count);

        foreach (var recovery in new[] { false, true })
        {
            var path = files.FilePath(recovery ? "recovery.tasproj" : "cleared.tasproj");
            if (recovery) await service.SaveRecoveryAsync(path); else await service.SaveProjectAsync(path);
            using var reopened = new ExecutionService(new FakeBackend { RecordPolls = true });
            await reopened.LoadProjectAsync(path, files.Options);
            Assert.Equal(30, reopened.Inputs.Count); Assert.Equal(30UL, reopened.Position);
            Assert.Equal(polls.Take(31), reopened.PollBoundaries);
            Assert.Equal(2, reopened.Takes.Count); Assert.Single(reopened.Tags);
            Assert.Equal(BitConverter.GetBytes(17), await reopened.ReadMemoryAsync(0x80000020, 4));
        }
    }

    [Fact]
    public async Task ClearingFutureInputLeavesPreviewAloneAndClearingAfterZeroKeepsFirstInput()
    {
        using var files = new TestWorkspace(); using var service = new ExecutionService(new FakeBackend());
        await service.LoadGameAsync(files.GamePath, files.Options); await service.NewProjectAsync();
        await service.ClearLaterInputAsync(0); // Empty timeline is a no-op.
        var first = ControllerState.Neutral with { Buttons = PadButtons.A, StickX = 200 };
        service.LiveInput = () => first;
        for (var i = 0; i < 8; i++) await service.StepAsync();
        await service.SeekAsync(2);
        await service.ClearLaterInputAsync(5);
        Assert.Equal(6, service.Inputs.Count);
        Assert.Equal(2UL, service.Position); Assert.True(service.IsPreviewCurrent);
        await service.ClearLaterInputAsync(5); // No-op must not add an undo entry.
        await service.UndoAsync(); Assert.Equal(8, service.Inputs.Count);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.ClearLaterInputAsync(-1));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.ClearLaterInputAsync(9));
        await service.ClearLaterInputAsync(0);
        Assert.Equal(first, Assert.Single(service.Inputs)); Assert.Equal(1UL, service.Position); Assert.True(service.IsPreviewCurrent);
        var revision = service.Revision;
        await service.ClearLaterInputAsync(0); await service.ClearLaterInputAsync(1);
        Assert.Equal(revision, service.Revision);
        Assert.True(await service.AdvanceFrameAsync(ControllerState.Neutral));
        Assert.Equal(2, service.Inputs.Count); Assert.Equal(first, service.Inputs[0]); Assert.Equal(ControllerState.Neutral, service.Inputs[1]);
    }
}
