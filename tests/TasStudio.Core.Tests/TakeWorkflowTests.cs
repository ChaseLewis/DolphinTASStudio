using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Dock.Avalonia.Controls;
using TasStudio.App;
using TasStudio.Core;
using TasStudio.Emulation;
using Xunit;

namespace TasStudio.Core.Tests;

public sealed class TakeWorkflowTests
{
    private static readonly ControllerState A = ControllerState.Neutral with { Buttons = PadButtons.A };
    private static readonly ControllerState B = ControllerState.Neutral with { Buttons = PadButtons.B };

    private static async Task Record(ExecutionService service, TestWorkspace files, int length = 10)
    {
        await service.LoadGameAsync(files.GamePath, files.Options); await service.NewProjectAsync();
        await service.ConfigureCheckpointsAsync(new(Enabled: false));
        for (var i = 0; i < length; i++) await service.AdvanceFrameAsync(ControllerState.Neutral);
    }

    [Fact]
    public async Task CopyReadsSelectedTakeAndPastePreservesSourceAndMovieWithUndoAndBoundsValidation()
    {
        using var files = new TestWorkspace(); using var service = new ExecutionService(new FakeBackend { RecordPolls = true });
        await Record(service, files);
        var source = await service.CaptureTakeAsync("Source", 2, 4);
        var destination = await service.CaptureTakeAsync("Destination", 4, 6);
        await service.SetTakeInputAsync(source, 2, A); await service.SetTakeInputAsync(source, 3, B);
        var before = service.Inputs.ToArray(); var history = await service.HistoryAtAsync(10);
        var copy = await service.CopyInputRangeAsync(2, 3, source);
        Assert.Equal(new[] { A, B, ControllerState.Neutral }, copy);
        await service.PasteInputRangeAsync(5, copy, destination);
        Assert.Equal(copy, service.Takes.Single(t => t.Id == destination).Inputs.Skip(1).Take(3));
        Assert.Equal(A, service.Takes.Single(t => t.Id == source).Inputs[0]);
        Assert.Equal(before, service.Inputs); Assert.Equal(history, await service.HistoryAtAsync(10));
        await service.UndoAsync(); Assert.All(service.Takes.Single(t => t.Id == destination).Inputs, input => Assert.Equal(ControllerState.Neutral, input));
        await service.RedoAsync(); Assert.Equal(A, service.Takes.Single(t => t.Id == destination).Inputs[1]);
        var revision = service.Revision;
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.PasteInputRangeAsync(9, copy, destination));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.PasteInputRangeAsync(4, copy, "missing"));
        Assert.Equal(revision, service.Revision);
        var captured = await service.CaptureTakeAsync("Copy from take", 2, 3, source);
        Assert.Equal(copy, service.Takes.Single(t => t.Id == captured).Inputs);
        await service.RemoveTakeAsync(source); await service.PasteInputRangeAsync(9, copy);
        copy[0] = B; // The clipboard and source must not alias the pasted recording.
        Assert.Equal(12, service.Inputs.Count); Assert.Equal(A, service.Inputs[9]);
        Assert.False(service.IsPreviewCurrent);
        await service.UndoAsync(); Assert.Equal(before, service.Inputs);
    }

    [Fact]
    public async Task HighestActiveTakeWinsIncludingNeutralInputsAndTrialMatchesOneUndoableApply()
    {
        using var files = new TestWorkspace(); var backend = new FakeBackend { RecordPolls = true };
        using var service = new ExecutionService(backend); await Record(service, files);
        var top = await service.CaptureTakeAsync("Top", 2, 4);
        var lower = await service.CaptureTakeAsync("Lower", 4, 4);
        await service.EditRangeAsync(2, 2, top, A, ControllerState.KnownButtons, 0);
        await service.EditRangeAsync(4, 4, lower, B, ControllerState.KnownButtons, 0);
        await service.SetTakeActiveAsync(top, true); await service.SetTakeActiveAsync(lower, true);
        await service.SaveNamedStateAsync("Original end");
        var original = service.Inputs.ToArray(); var history = await service.HistoryAtAsync(10);
        var revision = service.Revision; var markers = service.StateMarkers.ToArray(); var polls = service.PollBoundaries.ToArray();
        var submitted = backend.SubmittedInputs.Count;
        await service.TrialActiveTakesAsync(0);
        var expected = new[] { ControllerState.Neutral, ControllerState.Neutral, A, A, ControllerState.Neutral, ControllerState.Neutral, B, B, ControllerState.Neutral, ControllerState.Neutral };
        Assert.Equal(expected, backend.SubmittedInputs.Skip(submitted));
        var trialResult = await service.ReadMemoryAsync(0, 4);
        Assert.Equal(original, service.Inputs); Assert.Equal(history, await service.HistoryAtAsync(10));
        Assert.Equal(revision, service.Revision); Assert.Equal(polls, service.PollBoundaries);
        Assert.Equal(markers, service.StateMarkers); Assert.False(service.IsTrialPlayback); Assert.False(service.IsRunning);
        Assert.False(service.IsPreviewCurrent);
        await service.ApplyActiveTakesAsync(); Assert.Equal(expected, service.Inputs);
        Assert.All(service.Takes, take => Assert.False(take.IsActive)); Assert.Empty(service.StateMarkers);
        await service.SeekAsync(10); Assert.Equal(trialResult, await service.ReadMemoryAsync(0, 4));
        await service.UndoAsync(); Assert.Equal(original, service.Inputs); Assert.All(service.Takes, take => Assert.True(take.IsActive));
        await service.RedoAsync(); Assert.Equal(expected, service.Inputs); Assert.All(service.Takes, take => Assert.False(take.IsActive));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TrialCancellationOrBackendFailureRestoresRecordingPollsStatesAndRevision(bool fail)
    {
        using var files = new TestWorkspace(); var backend = new FakeBackend { RecordPolls = true };
        using var service = new ExecutionService(backend); await Record(service, files);
        var take = await service.CaptureTakeAsync("Candidate", 0, 10);
        await service.SetTakeInputAsync(take, 0, A); await service.SetTakeActiveAsync(take, true);
        await service.SaveNamedStateAsync("Keep");
        var original = service.Inputs.ToArray(); var polls = service.PollBoundaries.ToArray(); var markers = service.StateMarkers.ToArray();
        var revision = service.Revision; var history = await service.HistoryAtAsync(10);
        using var cancellation = new CancellationTokenSource();
        backend.BeforeStep = () =>
        {
            Assert.True(service.IsTrialPlayback); Assert.Equal(original, service.Inputs); Assert.Equal(revision, service.Revision);
            if (fail) backend.FailNextStep = true; else cancellation.Cancel();
        };
        if (fail) await Assert.ThrowsAsync<InvalidOperationException>(() => service.TrialActiveTakesAsync(0, cancellation.Token));
        else await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.TrialActiveTakesAsync(0, cancellation.Token));
        backend.BeforeStep = null;
        Assert.Equal(original, service.Inputs); Assert.Equal(polls, service.PollBoundaries); Assert.Equal(markers, service.StateMarkers);
        Assert.Equal(revision, service.Revision); Assert.Equal(history, await service.HistoryAtAsync(10));
        Assert.False(service.IsTrialPlayback); Assert.False(service.IsRunning);
        await service.SeekAsync(10); Assert.True(service.IsPreviewCurrent);
    }

    [Fact]
    public async Task TakePriorityAndActiveFlagsRoundtripAndUndoWhileOlderProjectsDefaultToInactive()
    {
        using var files = new TestWorkspace(); using var service = new ExecutionService(new FakeBackend()); await Record(service, files);
        var first = await service.CaptureTakeAsync("First", 0, 3); var second = await service.CaptureTakeAsync("Second", 0, 3);
        await service.SetTakeInputAsync(first, 0, A); await service.SetTakeInputAsync(second, 0, B);
        Assert.All(service.Takes, take => Assert.False(take.IsActive));
        await service.SetTakeActiveAsync(first, true); await service.SetTakeActiveAsync(second, true);
        await service.MoveTakeAsync(second, -1); Assert.Equal(second, service.Takes[0].Id);
        await service.UndoAsync(); Assert.Equal(first, service.Takes[0].Id);
        await service.RedoAsync(); Assert.Equal(second, service.Takes[0].Id);
        var path = files.FilePath("takes.tasproj"); await service.SaveProjectAsync(path);
        using var reopened = new ExecutionService(new FakeBackend()); await reopened.LoadProjectAsync(path, files.Options);
        Assert.Equal(second, reopened.Takes[0].Id); Assert.All(reopened.Takes, take => Assert.True(take.IsActive));
        await reopened.ApplyActiveTakesAsync(); Assert.Equal(B, reopened.Inputs[0]);
        var old = System.Text.Json.JsonSerializer.Deserialize<TakeAsset>("{\"Id\":\"old\",\"Name\":\"Old\",\"Start\":0,\"BaselineHash\":\"\",\"Provenance\":\"\",\"Inputs\":[]}")!;
        Assert.False(old.IsActive);
    }

    [Fact]
    public async Task OverwriteAgeUsesVideoFieldsInsteadOfGroupOrPollCount()
    {
        using var files = new TestWorkspace(); var backend = new FakeBackend { RecordPolls = true, FieldsPerStep = 120, PollsPerStep = 1 };
        using var service = new ExecutionService(backend); await Record(service, files, 31);
        Assert.Equal(62, service.InputAgeSeconds(0));
        Assert.Equal(60, service.InputAgeSeconds(1));
        Assert.Equal(0, service.InputAgeSeconds(31));
        await service.SeekAsync(30); Assert.Equal(60, service.InputAgeSeconds(0));
    }

    [AvaloniaFact]
    public async Task TimelineCopyPasteWorksBetweenTakesAndInFloatingTimelineWithoutChangingSource()
    {
        using var files = new TestWorkspace(); using var service = new ExecutionService(new FakeBackend()); await Record(service, files);
        var source = await service.CaptureTakeAsync("Source", 1, 3); var destination = await service.CaptureTakeAsync("Destination", 5, 4);
        await service.SetTakeInputAsync(source, 1, A); await service.SetTakeInputAsync(source, 2, B);
        var main = new MainWindow(["--layout-file", files.FilePath("layout.json")], new AppSettings(), service);
        try
        {
            var timeline = Show(main); timeline.SetSelection(1, 3, source); timeline.Focus();
            Press(timeline, Key.C); await Until(() => timeline.ContextMenu!.Items.OfType<MenuItem>().Single(m => m.Header?.ToString()?.StartsWith("Paste inputs") == true).IsEnabled);
            // Moving the timeline to another host must preserve the clipboard and shortcuts.
            var factory = (WorkspaceFactory)main.GetVisualDescendants().OfType<DockControl>().First().Factory!;
            factory.FloatDockable(factory.Panels["timeline"]); Dispatcher.UIThread.RunJobs();
            timeline.SetSelection(6, 7, destination); timeline.Focus(); Press(timeline, Key.V);
            await Until(() => service.Takes.Single(t => t.Id == destination).Inputs[1] == A && timeline.SelectionEnd == 8);
            Assert.Equal(B, service.Takes.Single(t => t.Id == destination).Inputs[2]);
            Assert.Equal(source, service.Takes[0].Id); Assert.Equal(A, service.Takes[0].Inputs[0]);
            Assert.Equal(6, timeline.SelectedFrame); Assert.Equal(8, timeline.SelectionEnd);
            Assert.All(service.Inputs, input => Assert.Equal(ControllerState.Neutral, input));
        }
        finally { main.CloseAfterCapture(); }
    }

    [AvaloniaFact]
    public async Task OldPasteAsksBeforeMutationCancelPreservesHistoryAndAcceptSupportsUndo()
    {
        using var files = new TestWorkspace(); using var service = new ExecutionService(new FakeBackend { RecordPolls = true, FieldsPerStep = 120 });
        await Record(service, files, 31); var source = await service.CaptureTakeAsync("Source", 0, 2); await service.SetTakeInputAsync(source, 0, A);
        var main = new MainWindow(["--layout-file", files.FilePath("layout.json")], new AppSettings(), service);
        try
        {
            var timeline = Show(main); timeline.SetSelection(0, 1, source); timeline.Focus(); Press(timeline, Key.C);
            await Until(() => timeline.ContextMenu!.Items.OfType<MenuItem>().Single(m => m.Header?.ToString()?.StartsWith("Paste inputs") == true).IsEnabled);
            timeline.SetSelection(0, 1); timeline.Focus(); var before = service.Inputs.ToArray(); var revision = service.Revision;
            Press(timeline, Key.V); var dialog = await Confirmation(main);
            Assert.Equal(before, service.Inputs); Assert.Equal(revision, service.Revision);
            Click(dialog, "CancelInputOverwrite"); await Until(() => !main.OwnedWindows.Any());
            await Until(() => main.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "TrialActiveTakes").IsEnabled == false);
            Assert.Equal(before, service.Inputs); Assert.Equal(revision, service.Revision);
            // Let Perform finish after the dialog has closed.
            await Until(() => main.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "+ Take")).IsEnabled);
            Press(timeline, Key.V); dialog = await Confirmation(main); Click(dialog, "AcceptInputOverwrite");
            await Until(() => service.Inputs[0] == A); await service.UndoAsync(); Assert.Equal(before, service.Inputs);
        }
        finally { foreach (var dialog in main.OwnedWindows.ToArray()) dialog.Close(); main.CloseAfterCapture(); }
    }

    [Fact]
    public async Task ExtendedExperimentTrialPreservesEventsAndCanSaveBeforeApplying()
    {
        using var files = new TestWorkspace(); using var service = new ExecutionService(new FakeBackend { RecordPolls = true });
        await Record(service, files, 3); var original = service.Inputs.ToArray();
        var data = new ExperimentPlayData(await service.HistoryAtAsync(3), [A, B, A],
            [new(4, ExecutionEventKind.MemoryWrite, 0, BitConverter.GetBytes(123))], []);
        var take = await service.ApplyExperimentPlayAsTakeAsync(new(0, "Extended", 0, 3, 3, data.Encode()));
        await service.SetTakeActiveAsync(take, true); var revision = service.Revision;
        await service.TrialActiveTakesAsync(3); var result = await service.ReadMemoryAsync(0, 4);
        Assert.Equal(6UL, service.Position); Assert.Equal(original, service.Inputs); Assert.Equal(revision, service.Revision);
        var path = files.FilePath("after-trial.tasproj"); await service.SaveProjectAsync(path);
        var saved = FolderProject.Load(path); Assert.Equal(original, saved.Archive.Metadata.Inputs); Assert.Empty(saved.Archive.Metadata.Events);
        Assert.Equal(3UL, saved.Archive.Metadata.Position); Assert.True(Assert.Single(saved.Takes).IsActive);
        await service.ApplyActiveTakesAsync(); await service.SeekAsync(6);
        Assert.Equal(result, await service.ReadMemoryAsync(0, 4));
        await service.SaveProjectAsync(path); saved = FolderProject.Load(path);
        Assert.Equal(4UL, Assert.Single(saved.Archive.Metadata.Events).Position);
        await service.UndoAsync(); Assert.Equal(original, service.Inputs); Assert.True(Assert.Single(service.Takes).IsActive);
    }

    [AvaloniaFact]
    public async Task InspectorCancelRestoresControlsAndExactlyOneMinuteEditsWithoutPrompt()
    {
        using var files = new TestWorkspace(); using var service = new ExecutionService(new FakeBackend { RecordPolls = true, FieldsPerStep = 120 });
        await Record(service, files, 31);
        var main = new MainWindow(["--layout-file", files.FilePath("layout.json")], new AppSettings(), service);
        try
        {
            var timeline = Show(main); timeline.SetSelection(0, 1); timeline.MoveCursor(0);
            var axis = main.GetVisualDescendants().OfType<NumericUpDown>().Single(n => n.Name == "TasAxis0");
            var revision = service.Revision; axis.Value = 220;
            var dialog = await Confirmation(main); Assert.Equal(ControllerState.Neutral, service.Inputs[0]);
            Click(dialog, "CancelInputOverwrite"); await Until(() => axis.Value == 128 && !main.OwnedWindows.Any());
            Assert.Equal(revision, service.Revision);
            timeline.SetSelection(1, 2); timeline.MoveCursor(0); axis.Value = 201;
            await Until(() => service.Inputs[1].StickX == 201); Assert.Empty(main.OwnedWindows);
            Assert.Equal(ControllerState.Neutral, service.Inputs[0]);
        }
        finally { foreach (var dialog in main.OwnedWindows.ToArray()) dialog.Close(); main.CloseAfterCapture(); }
    }

    [AvaloniaFact]
    public async Task MarkTrialEscapeAndApplyButtonsPreserveMovieUntilApplying()
    {
        using var files = new TestWorkspace(); using var service = new ExecutionService(new FakeBackend { RecordPolls = true });
        await Record(service, files, 200); var original = service.Inputs.ToArray();
        var take = await service.CaptureTakeAsync("Boss route", 0, 180); await service.SetTakeInputAsync(take, 0, A);
        var main = new MainWindow(["--layout-file", files.FilePath("layout.json")], new AppSettings(), service);
        try
        {
            var timeline = Show(main); timeline.SetSelection(0, 180, take); timeline.MoveCursor(0);
            var mark = main.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "ToggleTakeActive");
            var trial = main.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "TrialActiveTakes");
            var apply = main.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "ApplyActiveTakes");
            await Until(() => mark.IsEnabled); Click(main, "ToggleTakeActive");
            await Until(() => trial.IsEnabled && Equals(mark.Content, "Unmark active"));
            if (Environment.GetEnvironmentVariable("TASSTUDIO_TEST_CAPTURE_DIR") is { Length: > 0 } output)
            {
                Directory.CreateDirectory(output); main.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                main.CaptureRenderedFrame()!.Save(Path.Combine(output, "active-takes.png"));
            }
            Click(main, "TrialActiveTakes"); await Until(() => service.IsTrialPlayback && service.IsRunning);
            Assert.Equal(original, service.Inputs);
            main.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
            await Until(() => !service.IsTrialPlayback && !service.IsRunning && trial.IsEnabled);
            Assert.Equal(original, service.Inputs); Assert.True(service.Takes[0].IsActive);
            Click(main, "ApplyActiveTakes"); await Until(() => service.Inputs[0] == A && !apply.IsEnabled);
            Assert.False(service.Takes[0].IsActive); await service.UndoAsync(); Assert.Equal(original, service.Inputs);
        }
        finally { main.CloseAfterCapture(); }
    }

    private static TimelineView Show(MainWindow main)
    {
        main.ShowEditor(); main.Show(); main.UpdateLayout(); Dispatcher.UIThread.RunJobs();
        return main.GetVisualDescendants().OfType<TimelineView>().Single();
    }
    private static void Press(Control control, Key key) => control.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = KeyModifiers.Control });
    private static void Click(Window window, string name) => window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static async Task<Window> Confirmation(MainWindow main)
    {
        await Until(() => main.OwnedWindows.Any(w => w.Name == "ConfirmInputOverwrite"));
        return main.OwnedWindows.Single(w => w.Name == "ConfirmInputOverwrite");
    }
    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) { Dispatcher.UIThread.RunJobs(); await Task.Delay(5, timeout.Token); }
    }
}
