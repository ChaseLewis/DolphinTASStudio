using Avalonia;
using TasStudio.App;
using TasStudio.Emulation;
using Xunit;

namespace TasStudio.Core.Tests;

public sealed class TasInputExecutionTests
{
    [Fact]
    public async Task RecordedStepStopsAtEndAndNeverRepairsAStalePreview()
    {
        using var files = new TestWorkspace(); var backend = new FakeBackend { RecordPolls = true, FieldsPerStep = 2 };
        using var service = new ExecutionService(backend);
        await service.LoadGameAsync(files.GamePath, files.Options); await service.NewProjectAsync();
        service.LiveInput = () => throw new InvalidOperationException("Playback must not sample live input");
        var revision = service.Revision;
        Assert.False(await service.StepRecordedFrameAsync());
        Assert.Empty(service.Inputs); Assert.Empty(backend.SubmittedInputs); Assert.Equal(revision, service.Revision);

        var recorded = ControllerState.Neutral with { Buttons = PadButtons.A, StickX = 200 };
        await service.SetInputAsync(0, recorded);
        Assert.True(await service.StepRecordedFrameAsync());
        Assert.Equal(1UL, service.Position); Assert.Single(service.Inputs);
        var polls = (await service.GetPollFrameAsync(0))!.Frame.Polls;
        Assert.All(polls, poll => Assert.Equal(recorded, poll.Input));
        await service.SaveNamedStateAsync("End");
        var hash = await service.HistoryAtAsync(1); revision = service.Revision;
        var submitted = backend.SubmittedInputs.Count;
        Assert.False(await service.StepRecordedFrameAsync());
        Assert.Equal(1UL, service.Position); Assert.Equal(submitted, backend.SubmittedInputs.Count);
        Assert.Equal(hash, await service.HistoryAtAsync(1)); Assert.Equal(revision, service.Revision);
        Assert.True(Assert.Single(service.StateMarkers).Valid);

        await service.SeekAsync(0);
        Assert.True(await service.StepRecordedFrameAsync());
        Assert.Equal(polls, (await service.GetPollFrameAsync(0))!.Frame.Polls);
        await service.SetInputAsync(0, ControllerState.Neutral);
        Assert.False(service.IsPreviewCurrent);
        var restores = backend.Calls.Count(c => c.Operation == "Restore");
        submitted = backend.SubmittedInputs.Count;
        Assert.False(await service.StepRecordedFrameAsync());
        Assert.Equal(1UL, service.Position); Assert.Equal(submitted, backend.SubmittedInputs.Count);
        Assert.Equal(restores, backend.Calls.Count(c => c.Operation == "Restore"));
    }

    [Fact]
    public void MixedControlsPreservePreviewValuesWhenNextFrameResolvesInput()
    {
        var original = ControllerState.Neutral with { Buttons = PadButtons.A | PadButtons.B, StickX = 77, TriggerL = 201 };
        var actual = TasInputExecution.Resolve(original,
            new Dictionary<PadButtons, bool?> { [PadButtons.A] = null, [PadButtons.B] = false, [PadButtons.X] = true },
            [null, 255, null, null, null, 19]);
        Assert.Equal(PadButtons.A | PadButtons.X, actual.Buttons);
        Assert.Equal(77, actual.StickX); Assert.Equal(255, actual.StickY);
        Assert.Equal(201, actual.TriggerL); Assert.Equal(19, actual.TriggerR);
    }

    [Fact]
    public async Task NextFrameRejectsStalePreviewWithoutReplayingOrChangingInputs()
    {
        using var workspace = new TestWorkspace(); var backend = new FakeBackend();
        using var service = new ExecutionService(backend);
        await service.LoadGameAsync(workspace.GamePath, workspace.Options); await service.NewProjectAsync();
        for (var i = 0; i < 6; i++) await service.StepAsync();
        await service.SeekAsync(3);
        await service.EditRangeAsync(0, 2, null, ControllerState.Neutral with { Buttons = PadButtons.A }, PadButtons.A, 0);
        Assert.False(service.IsPreviewCurrent);
        var shown = ControllerState.Neutral with { Buttons = PadButtons.B, StickX = 199 };
        var before = backend.SubmittedInputs.Count;
        var restores = backend.Calls.Count(c => c.Operation == "Restore");
        Assert.False(await TasInputExecution.StepAsync(service, shown, false));
        Assert.Equal(3UL, service.Position);
        Assert.Equal(before, backend.SubmittedInputs.Count);
        Assert.Equal(restores, backend.Calls.Count(c => c.Operation == "Restore"));
        Assert.Equal(ControllerState.Neutral, service.Inputs[3]);
        await service.SeekAsync(3);
        before = backend.SubmittedInputs.Count;
        Assert.True(await TasInputExecution.StepAsync(service, shown, false));
        Assert.Equal(before + 1, backend.SubmittedInputs.Count);
        Assert.Equal(4UL, service.Position);
        Assert.Equal(ControllerState.Neutral, backend.SubmittedInputs.Last()); Assert.Equal(ControllerState.Neutral, service.Inputs[3]);
        Assert.Equal(PadButtons.A, service.Inputs[0].Buttons); Assert.Equal(PadButtons.A, service.Inputs[1].Buttons);
        Assert.Equal(ControllerState.Neutral, service.Inputs[4]);
    }

    [Fact]
    public async Task AllAdvancesPreserveExistingPollsHistoryAndStatesRegardlessOfSuppliedInput()
    {
        using var files = new TestWorkspace(); var backend = new FakeBackend { RecordPolls = true };
        using var service = new ExecutionService(backend);
        await service.LoadGameAsync(files.GamePath, files.Options); await service.NewProjectAsync();
        var recorded = ControllerState.Neutral with { Buttons = PadButtons.A, StickX = 200 };
        service.LiveInput = () => recorded;
        for (var i = 0; i < 3; i++) await service.StepAsync();
        await service.SaveNamedStateAsync("Keep");
        var endHash = await service.HistoryAtAsync(3);
        var firstPolls = (await service.GetPollFrameAsync(0))!.Frame.Polls;
        var revision = service.Revision;
        await service.SeekAsync(0);
        foreach (var proposed in new[] { ControllerState.Neutral, ControllerState.Neutral with { Buttons = PadButtons.B }, recorded })
            Assert.True(await service.AdvanceFrameAsync(proposed));
        Assert.All(service.Inputs, input => Assert.Equal(recorded, input));
        Assert.Equal(firstPolls, (await service.GetPollFrameAsync(0))!.Frame.Polls);
        Assert.Equal(endHash, await service.HistoryAtAsync(3)); Assert.Equal(revision, service.Revision);
        Assert.True(Assert.Single(service.StateMarkers).Valid);
        Assert.True(await service.AdvanceFrameAsync(ControllerState.Neutral));
        Assert.Equal(ControllerState.Neutral, service.Inputs[3]);
    }

    [Fact]
    public async Task CandidateControlsNeverOverwriteActivePlaybackOnNextFrame()
    {
        using var workspace = new TestWorkspace(); var backend = new FakeBackend();
        using var service = new ExecutionService(backend);
        await service.LoadGameAsync(workspace.GamePath, workspace.Options); await service.NewProjectAsync();
        for (var i = 0; i < 3; i++) await service.StepAsync();
        var candidate = await service.CaptureTakeAsync("Alternative", 0, 3);
        var shown = ControllerState.Neutral with { Buttons = PadButtons.A };
        await service.EditRangeAsync(0, 3, candidate, shown, PadButtons.A, 0);
        await service.SeekAsync(0); await TasInputExecution.StepAsync(service, shown, true);
        Assert.Equal(ControllerState.Neutral, backend.SubmittedInputs.Last());
        Assert.All(service.Inputs, input => Assert.Equal(ControllerState.Neutral, input));
        Assert.All(Assert.Single(service.Takes).Inputs, input => Assert.Equal(PadButtons.A, input.Buttons));
    }

    [Fact]
    public async Task HeldTasControlsRecordAtEndAndRestorePhysicalInputSource()
    {
        using var workspace = new TestWorkspace(); var backend = new FakeBackend();
        using var service = new ExecutionService(backend);
        await service.LoadGameAsync(workspace.GamePath, workspace.Options); await service.NewProjectAsync();
        Func<ControllerState> physical = () => ControllerState.Neutral;
        service.LiveInput = physical;
        var shown = ControllerState.Neutral with { CStickX = 17, TriggerR = 225, Buttons = PadButtons.R };
        Assert.True(service.IsPreviewCurrent);
        var restores = backend.Calls.Count(c => c.Operation == "Restore");
        Assert.True(await TasInputExecution.StepAsync(service, shown, false));
        Assert.True(await TasInputExecution.StepAsync(service, shown, false));
        Assert.Equal(restores, backend.Calls.Count(c => c.Operation == "Restore"));
        Assert.Equal(new[] { shown, shown }, service.Inputs); Assert.Same(physical, service.LiveInput);
    }

    [Fact]
    public void StickPointerCoordinatesMatchByteAxesAndClampOutsidePad()
    {
        Assert.Equal(((byte)128, (byte)128), StickPad.Coordinates(new Point(56, 56), new Size(112, 112)));
        Assert.Equal(((byte)0, (byte)255), StickPad.Coordinates(new Point(-20, -20), new Size(112, 112)));
        Assert.Equal(((byte)255, (byte)0), StickPad.Coordinates(new Point(200, 200), new Size(112, 112)));
    }

    [Theory]
    [InlineData(255, 128, 1, 255, 128)]
    [InlineData(0, 128, 1, 0, 128)]
    [InlineData(128, 255, 0.1, 128, 141)]
    [InlineData(128, 0, 0.1, 128, 115)]
    [InlineData(255, 255, 1, 218, 218)]
    [InlineData(255, 255, 0.1, 137, 137)]
    [InlineData(255, 0, 0, 128, 128)]
    [InlineData(128, 128, 1, 128, 128)]
    public void NormalizedStickUsesRequestedRadiusAndPreservesNeutral(byte x, byte y, double radius, byte expectedX, byte expectedY)
    {
        Assert.Equal((expectedX, expectedY), StickPad.Normalize(x, y, radius));
    }

    [Fact]
    public void NormalizedPointerKeepsDirectionOutsidePadAndRadiusWithinBytePrecision()
    {
        var result = StickPad.Coordinates(new Point(256, -44), new Size(112, 112), .5);
        // Direction 2:1 must survive dragging beyond both edges, without first clipping to a square.
        var x = (result.X - 128) / 127d; var y = (result.Y - 128) / 127d;
        Assert.InRange(x / y, 1.95, 2.05);
        Assert.InRange(Math.Sqrt(x * x + y * y), .494, .506);
        Assert.Throws<ArgumentOutOfRangeException>(() => StickPad.Normalize(255, 255, 1.1));
        Assert.Throws<ArgumentOutOfRangeException>(() => StickPad.Normalize(255, 255, double.NaN));
    }
}
