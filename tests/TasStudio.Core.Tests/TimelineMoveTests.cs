using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Dock.Avalonia.Controls;
using TasStudio.App;
using TasStudio.Emulation;
using Xunit;

namespace TasStudio.Core.Tests;

public sealed class TimelineMoveTests
{
    [AvaloniaFact]
    public async Task DragPreviewsThenCommitsOneUndoableMoveAndEscapeCancels()
    {
        using var files = new TestWorkspace(); using var service = new ExecutionService(new FakeBackend { RecordPolls = true });
        await Record(service, files);
        var main = new MainWindow(["--layout-file", files.FilePath("layout.json")], new AppSettings(), service);
        try
        {
            var timeline = Show(main);
            var inputs = service.Inputs.ToArray(); var revision = service.Revision;
            Point At(double frame) => timeline.TranslatePoint(new Point(118 + frame * (timeline.Bounds.Width - 118) / 20, 76), main)!.Value;
            main.MouseDown(At(2.5), MouseButton.Left); main.MouseMove(At(4.5)); main.MouseUp(At(4.5), MouseButton.Left);
            Assert.Equal(2, timeline.SelectedFrame); Assert.Equal(5, timeline.SelectionEnd);
            main.MouseDown(At(3.5), MouseButton.Left);
            main.MouseMove(At(5.5)); main.MouseMove(At(7.5));
            Assert.Equal(inputs, service.Inputs); Assert.Equal(2, timeline.SelectedFrame);
            main.MouseUp(At(7.5), MouseButton.Left);
            await Until(() => timeline.SelectedFrame == 6);
            Assert.Equal(9, timeline.SelectionEnd); Assert.Equal(revision + 1, service.Revision);
            Assert.Equal(inputs.Skip(2).Take(3), service.Inputs.Skip(6).Take(3));
            Assert.All(service.Inputs.Skip(2).Take(3), input => Assert.Equal(ControllerState.Neutral, input));
            Assert.Equal(12UL, service.Position); Assert.False(service.IsPreviewCurrent);
            var moved = service.Inputs.ToArray();
            main.MouseDown(At(7.5), MouseButton.Left); main.MouseMove(At(10.5));
            Press(main, PhysicalKey.Escape);
            main.MouseUp(At(10.5), MouseButton.Left);
            Assert.Equal(moved, service.Inputs); Assert.Equal(6, timeline.SelectedFrame); Assert.Equal(9, timeline.SelectionEnd);
            // Clicking inside a range still collapses it; a new range can be drawn on the ruler.
            main.MouseDown(At(7.5), MouseButton.Left); main.MouseUp(At(7.5), MouseButton.Left);
            Assert.Equal(7, timeline.SelectedFrame); Assert.Equal(8, timeline.SelectionEnd);
            await service.UndoAsync(); Assert.Equal(inputs, service.Inputs);
        }
        finally { main.CloseAfterCapture(); }
    }

    [AvaloniaFact]
    public async Task ShiftArrowsKeepRangeClampAtStartExtendAtEndAndWorkInDetachedTimeline()
    {
        using var files = new TestWorkspace(); using var service = new ExecutionService(new FakeBackend { RecordPolls = true });
        await Record(service, files);
        var main = new MainWindow(["--layout-file", files.FilePath("layout.json")], new AppSettings(), service);
        try
        {
            var timeline = Show(main); var inputs = service.Inputs.ToArray();
            timeline.SetSelection(0, 2); timeline.Focus(); var revision = service.Revision;
            Press(main, PhysicalKey.ArrowLeft, RawInputModifiers.Shift);
            Assert.Equal(0, timeline.SelectedFrame); Assert.Equal(revision, service.Revision);
            Press(main, PhysicalKey.ArrowRight, RawInputModifiers.Shift);
            await Until(() => timeline.SelectedFrame == 1);
            Assert.Equal(3, timeline.SelectionEnd); Assert.Equal(inputs.Take(2), service.Inputs.Skip(1).Take(2));
            var axis = main.GetVisualDescendants().OfType<NumericUpDown>().Single(n => n.Name == "TasAxis0");
            Assert.Null(axis.Value); // Inspector still represents both selected inputs.
            var text = axis.GetVisualDescendants().OfType<TextBox>().First();
            text.Focus(); revision = service.Revision;
            Press(main, PhysicalKey.ArrowRight, RawInputModifiers.Shift);
            Assert.Equal(revision, service.Revision); Assert.Equal(1, timeline.SelectedFrame);
            main.GetVisualDescendants().OfType<Slider>().First().Focus();
            Press(main, PhysicalKey.ArrowLeft, RawInputModifiers.Shift); Assert.Equal(1, timeline.SelectedFrame);

            var factory = (WorkspaceFactory)main.GetVisualDescendants().OfType<DockControl>().First().Factory!;
            factory.FloatDockable(factory.Panels["timeline"]); Dispatcher.UIThread.RunJobs();
            var host = Assert.IsType<HostWindow>(TopLevel.GetTopLevel(timeline)); host.UpdateLayout();
            timeline.SetSelection(10, 12); timeline.Focus();
            Press(host, PhysicalKey.ArrowRight, RawInputModifiers.Shift);
            await Until(() => timeline.SelectedFrame == 11);
            Assert.Equal(13, timeline.SelectionEnd); Assert.Equal(13, service.Inputs.Count);
            Assert.Equal(inputs.Skip(10), service.Inputs.Skip(11));
            Assert.Equal(12UL, service.Position);
            // The new group has no polls, but must remain visible and selectable.
            timeline.FirstFrame = 0; timeline.Update(); host.UpdateLayout();
            var point = timeline.TranslatePoint(new Point(118 + 12.5 * (timeline.Bounds.Width - 118) / 20, 76), host)!.Value;
            host.MouseDown(point, MouseButton.Left); host.MouseUp(point, MouseButton.Left);
            Assert.Equal(12, timeline.SelectedFrame);
        }
        finally { main.CloseAfterCapture(); }
    }

    [AvaloniaFact]
    public async Task CandidateShortcutsAndDragsClampToTheTakeWithoutChangingPlayback()
    {
        using var files = new TestWorkspace(); using var service = new ExecutionService(new FakeBackend());
        await Record(service, files);
        var id = await service.CaptureTakeAsync("Candidate", 2, 5);
        var main = new MainWindow(["--layout-file", files.FilePath("layout.json")], new AppSettings(), service);
        try
        {
            var timeline = Show(main); var inputs = service.Inputs.ToArray();
            timeline.SetSelection(2, 4, id); timeline.Focus();
            Press(main, PhysicalKey.ArrowLeft, RawInputModifiers.Shift);
            Assert.Equal(2, timeline.SelectedFrame);
            Press(main, PhysicalKey.ArrowRight, RawInputModifiers.Shift);
            await Until(() => timeline.SelectedFrame == 3);
            Assert.Equal(5, timeline.SelectionEnd); Assert.Equal(id, timeline.SelectedTake);
            Point At(double frame) => timeline.TranslatePoint(new Point(118 + frame * (timeline.Bounds.Width - 118) / 20, 114), main)!.Value;
            main.MouseDown(At(3.5), MouseButton.Left); main.MouseMove(At(0.5)); main.MouseUp(At(0.5), MouseButton.Left);
            await Until(() => timeline.SelectedFrame == 2);
            Assert.Equal(4, timeline.SelectionEnd); Assert.Equal(id, timeline.SelectedTake);
            Assert.Equal(inputs, service.Inputs); Assert.True(service.IsPreviewCurrent);
            Assert.Equal(inputs.Skip(2).Take(2), service.Takes[0].Inputs.Take(2));
        }
        finally { main.CloseAfterCapture(); }
    }

    [AvaloniaFact]
    public void DragUsesWholeGroupsOnANonuniformPollRuler()
    {
        var timeline = new TimelineView
        { InputCount = 6, VisibleFrames = 6, PollBoundaries = new ulong[] { 0, 2, 7, 8, 12, 15, 20 } };
        var window = new Window { Width = 1100, Height = 220, Content = timeline };
        try
        {
            window.Show(); window.UpdateLayout(); timeline.SetSelection(1, 3);
            Point At(double poll) => timeline.TranslatePoint(new Point(118 + poll * (timeline.Bounds.Width - 118) / 20, 76), window)!.Value;
            var requests = new List<int>(); timeline.MoveSelectionRequested += requests.Add;
            window.MouseDown(At(4), MouseButton.Left); // Group 1.
            window.MouseMove(At(13)); // Group 4, three groups later.
            window.MouseUp(At(13), MouseButton.Left);
            Assert.Equal(new[] { 3 }, requests);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void ShiftDragMovesSingleGroupAndCaptureLossCancelsWithoutEditing()
    {
        var timeline = new TimelineView { InputCount = 12, VisibleFrames = 20 };
        var window = new Window { Width = 1100, Height = 220, Content = timeline };
        try
        {
            window.Show(); window.UpdateLayout();
            Point At(double frame) => timeline.TranslatePoint(new Point(118 + frame * (timeline.Bounds.Width - 118) / 20, 76), window)!.Value;
            timeline.SetSelection(3, 4); var requests = new List<int>();
            timeline.MoveSelectionRequested += requests.Add;
            window.MouseDown(At(3.5), MouseButton.Left, RawInputModifiers.Shift);
            window.MouseMove(At(15.5), RawInputModifiers.Shift);
            window.MouseUp(At(15.5), MouseButton.Left, RawInputModifiers.Shift);
            Assert.Equal(new[] { 12 }, requests); // Drag may extend past the recorded end.
            window.MouseDown(At(3.5), MouseButton.Left, RawInputModifiers.Shift);
            window.MouseMove(At(1.5), RawInputModifiers.Shift);
            window.Close();
            Assert.Single(requests);
        }
        finally { window.Close(); }
    }

    private static TimelineView Show(MainWindow main)
    {
        main.ShowEditor(); main.Show(); main.UpdateLayout(); Dispatcher.UIThread.RunJobs();
        var timeline = main.GetVisualDescendants().OfType<TimelineView>().Single();
        timeline.VisibleFrames = 20; timeline.FirstFrame = 0; timeline.Update(); main.UpdateLayout(); return timeline;
    }
    private static async Task Record(ExecutionService service, TestWorkspace files)
    {
        await service.LoadGameAsync(files.GamePath, files.Options); await service.NewProjectAsync();
        for (var i = 0; i < 12; i++)
        {
            var input = ControllerState.Neutral with { StickX = (byte)(i + 10) };
            service.LiveInput = () => input; await service.StepAsync();
        }
    }
    private static void Press(Window window, PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None)
    { window.KeyPressQwerty(key, modifiers); window.KeyReleaseQwerty(key, modifiers); }
    private static async Task Until(Func<bool> condition)
    {
        var timer = Stopwatch.StartNew();
        while (!condition() && timer.Elapsed < TimeSpan.FromSeconds(5)) { await Task.Delay(10); Dispatcher.UIThread.RunJobs(); }
        Assert.True(condition());
    }
}
