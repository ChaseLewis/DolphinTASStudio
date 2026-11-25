using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Dock.Avalonia.Controls;
using TasStudio.App;
using TasStudio.Core;
using TasStudio.Emulation;
using Xunit;

namespace TasStudio.Core.Tests;

public sealed class TakeOverTests
{
    private static readonly ControllerState A = ControllerState.Neutral with { Buttons = PadButtons.A };
    private static readonly ControllerState B = ControllerState.Neutral with { Buttons = PadButtons.B, StickX = 255 };

    private static async Task Record(ExecutionService service, TestWorkspace files)
    {
        await service.LoadGameAsync(files.GamePath, files.Options); await service.NewProjectAsync();
        await service.ConfigureCheckpointsAsync(new(IntervalSeconds: 1));
        for (var i = 0; i < 4; i++) await service.AdvanceFrameAsync(A);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LiveBranchIgnoresRecordedInputAndEventsAndRestoresEverythingWithoutStateFiles(bool polls)
    {
        using var files = new TestWorkspace(); var backend = new FakeBackend { RecordPolls = polls };
        using var service = new ExecutionService(backend); await Record(service, files);
        await service.SeekAsync(2); await service.WriteMemoryAsync(0, BitConverter.GetBytes(42));
        await service.SeekAsync(4); await service.CaptureTakeAsync("Candidate", 0, 3);
        await service.SaveNamedStateAsync("End"); await service.SeekAsync(1);
        var memory = await service.ReadMemoryAsync(0, 4); var presentation = service.LatestPresentation;
        var seconds = service.ElapsedSeconds; var fields = service.VideoFieldCount;
        var inputs = service.Inputs.ToArray(); var markers = service.StateMarkers.ToArray();
        var takes = JsonSerializer.Serialize(service.Takes); var boundaries = service.PollBoundaries.ToArray();
        var poll = JsonSerializer.Serialize(await service.GetPollFrameAsync(1));
        var history = await service.HistoryAtAsync(4); var revision = service.Revision;
        var disk = Directory.GetFiles(files.DirectoryPath, "*", SearchOption.AllDirectories).Order().ToArray();
        var calls = backend.Calls.Count; var submitted = backend.SubmittedInputs.Count;

        await service.BeginTakeOverAsync(); service.LiveInput = () => B;
        Assert.Equal(1UL, service.TakeOverPosition); Assert.True(service.HasProject);
        for (var i = 0; i < 130; i++) await service.StepAsync();
        Assert.Equal(131UL, service.Position); Assert.True(service.IsPreviewCurrent);
        Assert.All(backend.SubmittedInputs.Skip(submitted), input => Assert.Equal(B, input));
        Assert.DoesNotContain(backend.Calls.Skip(calls), call => call.Operation is "ReplayInputPollFrame" or "WriteMemory");
        // Even explicit transient reset/memory writes cannot become TAS events.
        await service.ResetAsync(); await service.WriteMemoryAsync(0, BitConverter.GetBytes(999));
        Assert.Equal(revision, service.Revision);
        Assert.Equal(disk, Directory.GetFiles(files.DirectoryPath, "*", SearchOption.AllDirectories).Order());
        await service.EndTakeOverAsync();

        Assert.False(service.IsTakingOver); Assert.Null(service.TakeOverPosition); Assert.False(service.IsRunning);
        Assert.Equal(1UL, service.Position); Assert.Equal(memory, await service.ReadMemoryAsync(0, 4));
        Assert.Equal(presentation, service.LatestPresentation); Assert.Equal(seconds, service.ElapsedSeconds); Assert.Equal(fields, service.VideoFieldCount);
        Assert.Equal(inputs, service.Inputs); Assert.Equal(markers, service.StateMarkers);
        Assert.Equal(takes, JsonSerializer.Serialize(service.Takes)); Assert.Equal(boundaries, service.PollBoundaries);
        Assert.Equal(poll, JsonSerializer.Serialize(await service.GetPollFrameAsync(1)));
        Assert.Equal(history, await service.HistoryAtAsync(4)); Assert.Equal(revision, service.Revision);
        await service.StepRecordedFrameAsync(); Assert.Equal(BitConverter.GetBytes(42), await service.ReadMemoryAsync(0, 4));
        // The existing undo history is still the candidate creation, not temporary gameplay.
        await service.UndoAsync(); Assert.Empty(service.Takes);
    }

    [Fact]
    public async Task ExplicitEntryRepairsStalePreviewAndLiveRunCanContinuePastMovieEnd()
    {
        using var files = new TestWorkspace(); var backend = new FakeBackend { RecordPolls = true };
        using var service = new ExecutionService(backend); await Record(service, files);
        await service.SetInputAsync(0, B);
        Assert.False(service.IsPreviewCurrent);
        await service.BeginTakeOverAsync(1);
        var memory = await service.ReadMemoryAsync(0, 4); var revision = service.Revision;
        service.LiveInput = () => B; await service.RunAsync();
        Assert.False(service.IsRecordingLive);
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            while (service.Position <= 4) await Task.Delay(5, timeout.Token);
        await service.PauseAsync(); Assert.True(service.IsTakingOver);
        await service.EndTakeOverAsync();
        Assert.Equal(1UL, service.Position); Assert.Equal(revision, service.Revision);
        Assert.Equal(memory, await service.ReadMemoryAsync(0, 4)); Assert.True(service.IsPreviewCurrent);
        await service.SeekAsync(1); Assert.Equal(memory, await service.ReadMemoryAsync(0, 4));
    }

    [Fact]
    public async Task TakeOverBlocksAuthoringAndSavingAndCanRetryFailedReturn()
    {
        using var files = new TestWorkspace(); var backend = new FakeBackend();
        using var service = new ExecutionService(backend); await Record(service, files);
        var revision = service.Revision; var memory = await service.ReadMemoryAsync(0, 4);
        await service.BeginTakeOverAsync();
        Func<Task>[] forbidden = [
            () => service.BeginTakeOverAsync(), () => service.SeekAsync(0), () => service.PlayRecordedAsync(),
            () => service.SetInputAsync(0, B), () => service.SaveNamedStateAsync("Temporary"),
            () => service.SaveStateAsync(files.FilePath("temporary.tasstate")),
            () => service.SaveProjectAsync(files.FilePath("temporary.tasproj")),
            () => service.SaveRecoveryAsync(files.FilePath("recovery.tasproj")),
            () => service.NewProjectAsync(), () => service.LoadGameAsync(files.GamePath, files.Options),
            () => service.ConfigureCheckpointsAsync(new(Enabled: false))
        ];
        foreach (var command in forbidden) await Assert.ThrowsAsync<InvalidOperationException>(command);
        Assert.Equal(revision, service.Revision); Assert.Empty(service.StateMarkers);
        backend.FailNextStep = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.StepAsync());
        Assert.True(service.IsTakingOver); Assert.False(service.IsRunning);
        await service.AdvanceFrameAsync(B);
        backend.FailNextRestore = true;
        await Assert.ThrowsAsync<InvalidDataException>(() => service.EndTakeOverAsync());
        Assert.True(service.IsTakingOver); Assert.Equal(4UL, service.TakeOverPosition);
        await service.EndTakeOverAsync();
        Assert.False(service.IsTakingOver); Assert.True(service.IsPreviewCurrent);
        Assert.Equal(memory, await service.ReadMemoryAsync(0, 4)); Assert.Equal(revision, service.Revision);
        await service.BeginTakeOverAsync(); await service.StepAsync(); await service.StopPlaybackAsync();
        Assert.False(service.IsTakingOver); Assert.Equal(4UL, service.Position);
        await service.BeginTakeOverAsync(); await service.StopAsync();
        Assert.False(service.IsTakingOver); Assert.Null(service.TakeOverPosition); Assert.False(service.HasProject);
    }

    [AvaloniaFact]
    public async Task TakeOverAcceptsMappedKeysAndEscapeRestoresEditorAndTimeline()
    {
        using var files = new TestWorkspace(); var backend = new FakeBackend();
        using var service = new ExecutionService(backend); await Record(service, files); await service.SeekAsync(1);
        var inputs = service.Inputs.ToArray(); var revision = service.Revision;
        using var input = new LiveInputSource(_ => new(false, default));
        var settings = new AppSettings { GamepadIndex = -1, Keys = new() { [PadControl.A] = Key.Space, [PadControl.StickRight] = Key.Right } };
        var main = new MainWindow(["--layout-file", files.FilePath("layout.json")], settings, service, input);
        try
        {
            main.ShowEditor(); main.Show(); main.Activate(); main.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            var dock = main.GetVisualDescendants().OfType<DockControl>().First();
            var factory = (WorkspaceFactory)dock.Factory!;
            var timeline = factory.Panels["timeline"].View!.GetVisualDescendants().OfType<TimelineView>().Single();
            timeline.SetSelection(2, 3);
            var layout = WorkspaceFactory.Capture((Dock.Model.Controls.IRootDock)dock.Layout!).ToJson();
            await main.EnterTakeOverModeAsync(); await service.PauseAsync(); Dispatcher.UIThread.RunJobs();
            Assert.True(service.IsTakingOver); Assert.False(factory.Panels["input"].View!.IsEnabled);
            Assert.True(main.GetVisualDescendants().OfType<Control>().Single(c => c.Name == "TakeOverToolbar").IsVisible);
            main.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Space });
            main.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Right });
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                while (input.Read().Buttons != PadButtons.A || input.Read().StickX != 255) await Task.Delay(5, timeout.Token);
            await service.StepAsync();
            Assert.Equal(PadButtons.A, backend.SubmittedInputs.Last().Buttons); Assert.Equal(255, backend.SubmittedInputs.Last().StickX);
            Assert.False(service.IsRunning); // Space controls the game, not playback.
            main.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.F11 });
            main.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Z, KeyModifiers = KeyModifiers.Control });
            main.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                while (main.IsMovieMode) { Dispatcher.UIThread.RunJobs(); await Task.Delay(5, timeout.Token); }
            Assert.False(service.IsTakingOver); Assert.False(service.IsRunning); Assert.Equal(1UL, service.Position);
            Assert.True(factory.Panels["input"].View!.IsEnabled); Assert.Equal(2, timeline.SelectedFrame);
            Assert.Equal(layout, WorkspaceFactory.Capture((Dock.Model.Controls.IRootDock)dock.Layout!).ToJson());
            Assert.Equal(inputs, service.Inputs); Assert.Equal(revision, service.Revision);
            // Timeline entry seeks to the requested boundary, which becomes the return point.
            await main.EnterTakeOverModeAsync(3); await service.PauseAsync(); await main.ExitMovieModeAsync();
            Assert.Equal(3UL, service.Position);
        }
        finally { main.CloseAfterCapture(); }
    }
}
