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

public sealed class TimelineCursorTests
{
    [AvaloniaFact]
    public void RightClickOnRowsMarkersAndTagsNeverChangesTheCursorOrSelection()
    {
        var timeline = new TimelineView
        {
            InputCount = 100, VisibleFrames = 100, Position = 50,
            Takes = [new InputTake("take", "Candidate", 10, Enumerable.Repeat(ControllerState.Neutral, 30).ToArray(), "", "test")],
            Markers = [new StateMarker("state", "Saved", 60, false, true)],
            Tags = [new TimelineTag("tag", 80, "Note")]
        };
        var window = new Window { Width = 1100, Height = 300, Content = timeline };
        try
        {
            window.Show(); window.UpdateLayout(); timeline.Update(); window.UpdateLayout();
            timeline.SetSelection(10, 14, "take"); var changes = 0;
            timeline.SelectionChanged += () => changes++;
            void RightClick(int frame, double y)
            {
                var point = timeline.TranslatePoint(new Avalonia.Point(118 + frame * (timeline.Bounds.Width - 118) / 100, y), window)!.Value;
                window.MouseDown(point, MouseButton.Right); window.MouseUp(point, MouseButton.Right);
                Assert.Equal(10, timeline.SelectedFrame); Assert.Equal(14, timeline.SelectionEnd); Assert.Equal("take", timeline.SelectedTake);
                Assert.Equal(50UL, timeline.Position); Assert.Equal(0, changes);
            }
            RightClick(70, 110); Assert.Equal(70, timeline.ContextFrame);
            RightClick(90, 148); Assert.Null(timeline.ContextFrame);
            RightClick(60, 18); Assert.Equal("state", timeline.SelectedMarker?.Id);
            RightClick(80, 43); Assert.Equal("tag", timeline.SelectedTag?.Id);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task ArrowsMoveWholeGroupsRefreshInputsAndScrollWithoutSeekingOrEditing()
    {
        using var files = new TestWorkspace(); using var service = new ExecutionService(new FakeBackend { RecordPolls = true, FieldsPerStep = 2 });
        await service.LoadGameAsync(files.GamePath, files.Options); await service.NewProjectAsync();
        for (var i = 0; i < 80; i++)
        {
            service.LiveInput = () => ControllerState.Neutral with { StickX = (byte)i };
            await service.StepAsync();
        }
        var take = await service.CaptureTakeAsync("Candidate", 20, 10);
        await service.SeekAsync(50);
        var main = new MainWindow(["--layout-file", files.FilePath("layout.json")], new AppSettings(), service);
        try
        {
            main.RestoreProjectTimelineView(); main.ShowEditor(); main.Show(); main.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            var timeline = main.GetVisualDescendants().OfType<TimelineView>().Single();
            var axis = main.GetVisualDescendants().OfType<NumericUpDown>().Single(n => n.Name == "TasAxis0");
            var scroll = main.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ScrollBar>().Single(s => s.Name == "TimelineScroll");
            var revision = service.Revision; var inputs = service.Inputs.ToArray();
            timeline.VisibleFrames = 10; timeline.FirstFrame = 0; timeline.SetSelection(10, 15); timeline.Update(); timeline.Focus();
            Press(main, PhysicalKey.ArrowRight);
            Assert.Equal(11, timeline.SelectedFrame); Assert.Equal(12, timeline.SelectionEnd);
            Assert.Equal(11m, axis.Value);
            Assert.Equal(4UL, service.PollBoundaries[11] - service.PollBoundaries[10]);
            Assert.InRange(11, timeline.FirstFrame, timeline.FirstFrame + timeline.VisibleFrames - 1);
            Assert.Equal(timeline.FirstFrame, scroll.Value);
            Press(main, PhysicalKey.ArrowLeft); Assert.Equal(10, timeline.SelectedFrame); Assert.Equal(10m, axis.Value);
            timeline.SetSelection(0, 1); Press(main, PhysicalKey.ArrowLeft); Assert.Equal(0, timeline.SelectedFrame);
            timeline.SetSelection(79, 80); Press(main, PhysicalKey.ArrowRight); Assert.Equal(80, timeline.SelectedFrame);
            Press(main, PhysicalKey.ArrowRight); Assert.Equal(80, timeline.SelectedFrame);
            Press(main, PhysicalKey.ArrowLeft); Assert.Equal(79, timeline.SelectedFrame); Assert.Equal(79m, axis.Value);
            timeline.SetSelection(20, 21, take); Press(main, PhysicalKey.ArrowLeft); Assert.Equal(20, timeline.SelectedFrame);
            Press(main, PhysicalKey.ArrowRight); Assert.Equal(21, timeline.SelectedFrame); Assert.Equal(take, timeline.SelectedTake);
            timeline.SetSelection(29, 30, take); Press(main, PhysicalKey.ArrowRight); Assert.Equal(29, timeline.SelectedFrame);
            Assert.Equal(50UL, service.Position); Assert.Equal(revision, service.Revision); Assert.Equal(inputs, service.Inputs);
            Assert.True(service.IsPreviewCurrent);

            // Shared shortcuts also work in a detached timeline window.
            var factory = (WorkspaceFactory)main.GetVisualDescendants().OfType<DockControl>().First().Factory!;
            factory.FloatDockable(factory.Panels["timeline"]); Dispatcher.UIThread.RunJobs();
            var host = Assert.IsType<HostWindow>(TopLevel.GetTopLevel(timeline)); host.UpdateLayout(); timeline.Focus();
            Press(host, PhysicalKey.ArrowLeft); Assert.Equal(28, timeline.SelectedFrame); Assert.Equal(28m, axis.Value);
            Assert.Equal(50UL, service.Position);
        }
        finally { main.CloseAfterCapture(); }
    }

    [AvaloniaFact]
    public async Task ArrowsRespectTextSlidersAndControllerInputFocus()
    {
        using var files = new TestWorkspace(); using var service = new ExecutionService(new FakeBackend());
        await service.LoadGameAsync(files.GamePath, files.Options); await service.NewProjectAsync();
        for (var i = 0; i < 3; i++) await service.StepAsync();
        await service.SeekAsync(0);
        var main = new MainWindow(["--layout-file", files.FilePath("layout.json")], new AppSettings(), service);
        try
        {
            main.ShowEditor(); main.Show(); main.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            var timeline = main.GetVisualDescendants().OfType<TimelineView>().Single();
            var axis = main.GetVisualDescendants().OfType<NumericUpDown>().Single(n => n.Name == "TasAxis0");
            var text = axis.GetVisualDescendants().OfType<TextBox>().First();
            text.Focus(); text.SelectionStart = text.SelectionEnd = text.Text!.Length;
            var caret = text.CaretIndex;
            Press(main, PhysicalKey.ArrowLeft);
            Assert.Equal(0, timeline.SelectedFrame); Assert.True(text.CaretIndex < caret);
            main.GetVisualDescendants().OfType<Slider>().First().Focus();
            Press(main, PhysicalKey.ArrowRight); Assert.Equal(0, timeline.SelectedFrame);
            var use = main.GetVisualDescendants().OfType<CheckBox>().Single(c => c.Name == "UseController");
            use.IsChecked = true;
            main.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "NextFrame").Focus();
            Press(main, PhysicalKey.ArrowRight); Assert.Equal(0, timeline.SelectedFrame);
            timeline.Focus(); Press(main, PhysicalKey.ArrowRight); Assert.Equal(1, timeline.SelectedFrame);
            Assert.Equal(0UL, service.Position);
        }
        finally { main.CloseAfterCapture(); }
    }

    private static void Press(Window window, PhysicalKey key)
    { window.KeyPressQwerty(key, RawInputModifiers.None); window.KeyReleaseQwerty(key, RawInputModifiers.None); }
}
