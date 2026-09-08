using TasStudio.Emulation;
using Xunit;

namespace TasStudio.Core.Tests;

public sealed class TransportTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClearingOneMarkerPreservesPreviewInputsAndOtherMarkers(bool automatic)
    {
        using var files = new TestWorkspace(); var backend = new FakeBackend(); using var service = new ExecutionService(backend);
        await service.LoadGameAsync(files.GamePath, files.Options); await service.NewProjectAsync();
        await service.ConfigureCheckpointsAsync(new(IntervalSeconds: 1));
        for (var i = 0; i < 60; i++) await service.StepAsync();
        await service.SaveNamedStateAsync("Named 60");
        for (var i = 0; i < 15; i++) await service.StepAsync();
        var target = service.StateMarkers.Single(m => m.Automatic == automatic);
        var other = service.StateMarkers.Single(m => m.Automatic != automatic);
        var inputs = service.Inputs.ToArray(); var revision = service.Revision;
        var submitted = backend.SubmittedInputs.Count;
        await service.ClearMarkerAsync(target.Id);
        Assert.Equal(other, Assert.Single(service.StateMarkers));
        Assert.Equal(75UL, service.Position); Assert.True(service.IsPreviewCurrent);
        Assert.Equal(inputs, service.Inputs); Assert.Equal(submitted, backend.SubmittedInputs.Count);
        Assert.Equal(revision + 1, service.Revision); Assert.True(service.HasProject);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.ClearMarkerAsync(target.Id));
        Assert.Equal(revision + 1, service.Revision);
        await service.LoadMarkerAsync(other.Id); Assert.Equal(60UL, service.Position);
    }

    [Fact]
    public async Task PlaybackStopsAtEndWithoutReadingLiveInputOrChangingMovie()
    {
        using var files = new TestWorkspace(); var backend = new FakeBackend(); using var service = new ExecutionService(backend);
        await service.LoadGameAsync(files.GamePath, files.Options); await service.NewProjectAsync();
        for (var i = 0; i < 3; i++) await service.StepAsync();
        var inputs = service.Inputs.ToArray(); var revision = service.Revision;
        await service.SeekAsync(0);
        service.LiveInput = () => throw new InvalidOperationException("Playback must not record live input");
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.Changed += () => { if (!service.IsRunning && service.Position == 3) finished.TrySetResult(); };
        await service.PlayRecordedAsync(); await finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(service.IsRunning); Assert.Equal(inputs, service.Inputs); Assert.Equal(revision, service.Revision);
        var calls = backend.SubmittedInputs.Count;
        await service.PlayRecordedAsync();
        Assert.False(service.IsRunning); Assert.Equal(calls, backend.SubmittedInputs.Count);
    }

    [Fact]
    public async Task ExplicitLiveRecordingStillAppendsAtEnd()
    {
        using var files = new TestWorkspace(); using var service = new ExecutionService(new FakeBackend());
        await service.LoadGameAsync(files.GamePath, files.Options); await service.NewProjectAsync();
        var held = ControllerState.Neutral with { Buttons = PadButtons.A };
        service.LiveInput = () => held;
        var recorded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.Changed += () => { if (service.Inputs.Count > 0) recorded.TrySetResult(); };
        await service.RunAsync(); await recorded.Task.WaitAsync(TimeSpan.FromSeconds(5)); await service.PauseAsync();
        Assert.NotEmpty(service.Inputs); Assert.All(service.Inputs, input => Assert.Equal(held, input));
    }

    [Fact]
    public async Task PreviousStateStepsBackwardThroughNamedStatesAndAutomaticCheckpoints()
    {
        using var files = new TestWorkspace(); var backend = new FakeBackend(); using var service = new ExecutionService(backend);
        await service.LoadGameAsync(files.GamePath, files.Options); await service.NewProjectAsync();
        await service.ConfigureCheckpointsAsync(new(IntervalSeconds: 1));
        for (var i = 0; i < 150; i++)
        {
            await service.StepAsync(); if (service.Position == 90) await service.SaveNamedStateAsync("Named 90");
        }
        var inputs = service.Inputs.ToArray(); var revision = service.Revision;
        var steps = backend.SubmittedInputs.Count;
        foreach (var position in new ulong[] { 120, 90, 60, 0, 0 })
        {
            await service.PreviousStateAsync(); Assert.Equal(position, service.Position); Assert.True(service.IsPreviewCurrent);
        }
        Assert.Equal(steps, backend.SubmittedInputs.Count); Assert.Equal(inputs, service.Inputs); Assert.Equal(revision, service.Revision);
        Assert.Equal(files.GamePath, service.GamePath); Assert.True(service.HasProject);
        var checkpoint = service.StateMarkers.First(m => m.Automatic && m.Position == 120);
        await service.LoadMarkerAsync(checkpoint.Id); Assert.Equal(120UL, service.Position);
        await service.PreviousStateAsync(); Assert.Equal(90UL, service.Position);
    }

    [Fact]
    public async Task PreviousStateSkipsHistoryInvalidatedByEdits()
    {
        using var files = new TestWorkspace(); using var service = new ExecutionService(new FakeBackend());
        await service.LoadGameAsync(files.GamePath, files.Options); await service.NewProjectAsync();
        await service.ConfigureCheckpointsAsync(new(IntervalSeconds: 1));
        for (var i = 0; i < 130; i++) await service.StepAsync();
        await service.SaveNamedStateAsync("Invalidated later");
        await service.SetInputAsync(70, ControllerState.Neutral with { Buttons = PadButtons.A });
        Assert.False(service.IsPreviewCurrent);
        await service.PreviousStateAsync(); Assert.Equal(60UL, service.Position); Assert.True(service.IsPreviewCurrent);
        await service.SetInputAsync(0, ControllerState.Neutral with { Buttons = PadButtons.B });
        await service.PreviousStateAsync(); Assert.Equal(0UL, service.Position); Assert.True(service.IsPreviewCurrent);
        Assert.Equal(PadButtons.A, service.Inputs[70].Buttons);
    }
}
