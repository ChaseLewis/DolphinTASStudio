using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TasStudio.App;
using TasStudio.Emulation;
using Xunit;

namespace TasStudio.Core.Tests;

public sealed class TimelineSelectionTimingTests
{
    [Fact]
    public void MeasuresRecordedClockBoundariesAndMarksOnlyUnknownOrStaleTimingAsEstimated()
    {
        var records = new Dictionary<int, RecordedInputFrame>
        {
            [0] = Record(0, 2, 2100, 4),
            [1] = Record(1, 3, 2900, 0), // No polls, but the elapsed time is still recorded.
            [3] = Record(3, 1, 1050, 7),
            [4] = Record(4, 2, 2000, 2) with { PrefixHash = "old" }
        };
        var snapshot = PollBoundarySnapshot.Empty.Update(5, 0, records, i => i.ToString());
        var clock = new InputTimingSnapshot(snapshot, 60, 60000);
        var range = clock.Measure(0, 2);
        Assert.Equal(5000d / 60000, range.DurationSeconds, 9);
        Assert.False(range.IsDurationEstimated); Assert.False(range.IsEndEstimated);
        Assert.False(snapshot.IsComplete); // Poll scale completeness is independent of timing.
        var later = clock.Measure(3, 4);
        Assert.Equal(1050d / 60000, later.DurationSeconds, 9);
        Assert.False(later.IsDurationEstimated); Assert.True(later.IsStartEstimated);
        Assert.True(clock.Measure(2, 3).IsDurationEstimated);
        Assert.True(clock.Measure(4, 5).IsDurationEstimated);
        Assert.True(clock.Measure(5, 8).IsDurationEstimated);
        Assert.Equal(3d / 60, clock.Measure(5, 8).DurationSeconds, 9);
        var fields = new InputTimingSnapshot(snapshot, 50);
        Assert.Equal(5d / 50, fields.Measure(0, 2).DurationSeconds, 9);
        records[2] = Record(2, 4, 4000, 2);
        records[4] = Record(4, 2, 2000, 2);
        var replayed = new InputTimingSnapshot(snapshot.Update(5, 2, records, i => i.ToString()), 60, 60000);
        Assert.False(replayed.Measure(0, 5).IsDurationEstimated);
        Assert.True(clock.Measure(0, 5).IsDurationEstimated); // Previous publications stay immutable.
    }

    [Fact]
    public async Task EditingEarlierInputMarksDownstreamTimingAsEstimatedAndUndoRestoresIt()
    {
        using var files = new TestWorkspace();
        var backend = new FakeBackend { RecordPolls = true, FieldsPerStep = 2 };
        using var service = new ExecutionService(backend);
        await service.LoadGameAsync(files.GamePath, files.Options); await service.NewProjectAsync();
        for (var i = 0; i < 8; i++) await service.StepAsync();
        var recorded = service.InputTiming;
        Assert.False(recorded.Measure(2, 6).IsDurationEstimated);
        await service.SetInputAsync(1, ControllerState.Neutral with { StickX = 20 });
        Assert.True(service.InputTiming.Measure(2, 6).IsDurationEstimated);
        Assert.False(service.InputTiming.Measure(0, 1).IsDurationEstimated);
        await service.UndoAsync();
        Assert.False(service.InputTiming.Measure(2, 6).IsDurationEstimated);
        Assert.Equal(8UL, service.Position);
        Assert.False(recorded.Measure(2, 6).IsDurationEstimated);
    }

    [AvaloniaFact]
    public async Task SelectingAndResizingShowsLiveDurationWithoutSeekingOrEditing()
    {
        using var files = new TestWorkspace();
        var backend = new FakeBackend { RecordPolls = true, FieldsPerStep = 2 };
        using var service = new ExecutionService(backend);
        await service.LoadGameAsync(files.GamePath, files.Options); await service.NewProjectAsync();
        for (var i = 0; i < 12; i++) await service.StepAsync();
        await service.SetTimelineOffsetAsync(1000);
        var main = new MainWindow(["--layout-file", files.FilePath("layout.json")], new AppSettings(), service);
        try
        {
            main.ShowEditor(); main.Show(); main.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            var timeline = main.GetVisualDescendants().OfType<TimelineView>().Single();
            timeline.VisibleFrames = 12; timeline.FirstFrame = 0; timeline.Update(); main.UpdateLayout();
            var revision = service.Revision; var inputs = service.Inputs; var calls = backend.Calls.ToArray();
            Point At(double group) => timeline.TranslatePoint(new Point(118 + group * (timeline.Bounds.Width - 118) / 12, 76), main)!.Value;
            main.MouseDown(At(2.5), MouseButton.Left); main.MouseMove(At(5.5));
            Assert.Equal("0.133 s", timeline.GetSelectionReadout()!.Duration);
            main.MouseUp(At(5.5), MouseButton.Left);
            Assert.Equal(2, timeline.SelectedFrame); Assert.Equal(6, timeline.SelectionEnd);
            var readout = timeline.GetSelectionReadout()!;
            Assert.Contains("Start: -00:00:00.933", readout.Detail);
            Assert.Contains("End:   -00:00:00.800", readout.Detail);
            main.MouseMove(timeline.TranslatePoint(readout.Box.Center, main)!.Value);
            Assert.Equal(readout.Detail, ToolTip.GetTip(timeline));
            Assert.True(ToolTip.GetIsOpen(timeline));
            main.MouseDown(At(6), MouseButton.Left); main.MouseMove(At(9));
            Assert.False(ToolTip.GetIsOpen(timeline));
            Assert.Equal(9, timeline.SelectionEnd); Assert.Equal("0.233 s", timeline.GetSelectionReadout()!.Duration);
            main.MouseUp(At(9), MouseButton.Left);
            main.MouseDown(At(2), MouseButton.Left); main.MouseMove(At(1)); main.MouseUp(At(1), MouseButton.Left);
            Assert.Equal(1, timeline.SelectedFrame); Assert.Equal(9, timeline.SelectionEnd);
            Assert.Equal("0.267 s", timeline.GetSelectionReadout()!.Duration);
            Assert.Equal(12UL, service.Position); Assert.Equal(revision, service.Revision); Assert.Same(inputs, service.Inputs);
            Assert.Equal(calls, backend.Calls.ToArray());
        }
        finally { main.CloseAfterCapture(); }
    }

    [AvaloniaFact]
    public void ResizingOnAnUnevenPollRulerSnapsToBoundariesAndClampsAtTheRecordedEnd()
    {
        var timeline = new TimelineView { InputCount = 6, VisibleFrames = 6, PollBoundaries = new ulong[] { 0, 2, 7, 8, 12, 15, 20 } };
        var window = new Window { Width = 1100, Height = 220, Content = timeline };
        try
        {
            window.Show(); window.UpdateLayout(); timeline.SetSelection(1, 3);
            Point At(double poll) => timeline.TranslatePoint(new Point(118 + poll * (timeline.Bounds.Width - 118) / 20, 76), window)!.Value;
            var moves = 0; timeline.MoveSelectionRequested += _ => moves++;
            window.MouseDown(At(8), MouseButton.Left); window.MouseMove(At(14.8)); window.MouseUp(At(14.8), MouseButton.Left);
            Assert.Equal(1, timeline.SelectedFrame); Assert.Equal(5, timeline.SelectionEnd);
            window.MouseDown(At(15), MouseButton.Left); window.MouseMove(At(40)); window.MouseUp(At(40), MouseButton.Left);
            Assert.Equal(6, timeline.SelectionEnd); Assert.Equal(0, moves);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void BadgeStaysInsideViewportAndHidesForOffscreenOrEmptySelections()
    {
        var records = Enumerable.Range(0, 300).ToDictionary(i => i, i => Record(i, 2, 2000, 4));
        var snapshot = PollBoundarySnapshot.Empty.Update(300, 0, records);
        var timeline = new TimelineView
        {
            InputCount = 300, Inputs = Enumerable.Repeat(ControllerState.Neutral, 300).ToArray(),
            InputTiming = new(snapshot, 60, 60000), PollBoundaries = snapshot, VisibleFrames = 300,
            Position = 120, Markers = [new("one", "Saved state", 30, false, true)],
            Tags = [new("before", 45, "Before"), new("middle", 140, "Inside selection"), new("after", 235, "After")]
        };
        var window = new Window { Width = 1000, Height = 170, Content = timeline };
        try
        {
            window.Show(); timeline.Update(); window.UpdateLayout();
            timeline.SetSelection(76, 204);
            Assert.Equal("4.267 s", timeline.GetSelectionReadout()!.Duration);
            foreach (var theme in new[] { UiTheme.Light, UiTheme.Dark })
            {
                StudioTheme.Apply(theme); Capture(window, "selection-duration-" + theme.ToString().ToLowerInvariant());
            }
            window.Width = 320; Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            foreach (var (start, end) in new[] { (0, 2), (76, 204), (298, 300) })
            {
                timeline.SetSelection(start, end);
                var box = timeline.GetSelectionReadout()!.Box;
                Assert.InRange(box.Left, 118, timeline.Bounds.Width); Assert.True(box.Right <= timeline.Bounds.Width);
                Assert.True(box.Bottom <= 64);
            }
            Capture(window, "selection-duration-narrow");
            timeline.VisibleFrames = 30; timeline.FirstFrame = 100; timeline.Update();
            Assert.Null(timeline.GetSelectionReadout());
            timeline.FirstFrame = 270; timeline.Update(); timeline.SetSelection(300, 301);
            Assert.Null(timeline.GetSelectionReadout()); // Cursor at the empty append position.
            timeline.Takes = [new("take", "Candidate", 280, Enumerable.Repeat(ControllerState.Neutral, 20).ToArray(), "", "test")];
            timeline.Update(); window.UpdateLayout(); timeline.SetSelection(281, 290, "take");
            Assert.StartsWith("≈ ", timeline.GetSelectionReadout()!.Duration);
        }
        finally { window.Close(); StudioTheme.Apply(UiTheme.System); }
    }

    private static RecordedInputFrame Record(int index, ulong fields, ulong ticks, int polls) =>
        new(index, index.ToString(), new(ControllerState.Neutral, fields, ticks,
            Enumerable.Range(0, polls).Select(_ => new InputPoll(0, 0, 0, ControllerState.Neutral)).ToArray()));

    private static void Capture(Window window, string name)
    {
        if (Environment.GetEnvironmentVariable("TASSTUDIO_TEST_CAPTURE_DIR") is not { Length: > 0 } output) return;
        Directory.CreateDirectory(output); Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        window.CaptureRenderedFrame()!.Save(Path.Combine(output, name + ".png"));
    }
}
