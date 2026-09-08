using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TasStudio.App;
using TasStudio.Emulation;
using Xunit;

namespace TasStudio.Core.Tests;

public sealed class TimelineTagTests
{
    [AvaloniaFact]
    public async Task ClickingTagSelectsExactGroupAndPreservedFutureTagsRemainReachable()
    {
        using var files = new TestWorkspace(); using var service = new ExecutionService(new FakeBackend { RecordPolls = true });
        await service.LoadGameAsync(files.GamePath, files.Options); await service.NewProjectAsync();
        service.LiveInput = () => ControllerState.Neutral with { Buttons = PadButtons.A, StickX = 200 };
        for (var i = 0; i < 100; i++) await service.StepAsync();
        var take = await service.CaptureTakeAsync("Candidate", 40, 20);
        var target = await service.AddTagAsync(50, "Target");
        var future = await service.AddTagAsync(75, "Later");
        await service.ClearLaterInputAsync(59); await service.SeekAsync(10);
        var main = new MainWindow(["--layout-file", files.FilePath("layout.json")], new AppSettings(), service);
        try
        {
            main.ShowEditor(); main.Show(); main.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            var timeline = main.GetVisualDescendants().OfType<TimelineView>().Single();
            var axis = main.GetVisualDescendants().OfType<NumericUpDown>().Single(n => n.Name == "TasAxis0");
            var a = main.GetVisualDescendants().OfType<CheckBox>().Single(c => Equals(c.Content, "A"));
            timeline.VisibleFrames = 100; timeline.FirstFrame = 0; timeline.SetSelection(42, 44, take); timeline.Update(); main.UpdateLayout();
            var revision = service.Revision;
            Point TagPoint(int frame, double labelOffset = 0) => timeline.TranslatePoint(new Point(118 + frame * (timeline.Bounds.Width - 118) / 100 + labelOffset, 43), main)!.Value;
            var point = TagPoint(50, 20); // Click the label edge, not the arrow's frame coordinate.
            main.MouseDown(point, MouseButton.Left); main.MouseUp(point, MouseButton.Left);
            Assert.Equal(50, timeline.SelectedFrame); Assert.Equal(51, timeline.SelectionEnd); Assert.Null(timeline.SelectedTake);
            Assert.Equal(target, timeline.SelectedTag?.Id); Assert.True(a.IsChecked); Assert.Equal(200m, axis.Value);
            point = TagPoint(75);
            main.MouseDown(point, MouseButton.Right); main.MouseUp(point, MouseButton.Right);
            Assert.Equal(future, timeline.SelectedTag?.Id); Assert.Equal(50, timeline.SelectedFrame);
            timeline.ContextMenu!.Close();
            main.MouseDown(point, MouseButton.Left); main.MouseUp(point, MouseButton.Left);
            Assert.Equal(75, timeline.SelectedFrame); Assert.Equal(76, timeline.SelectionEnd);
            Assert.False(a.IsChecked); Assert.Equal(128m, axis.Value);
            main.KeyPressQwerty(PhysicalKey.ArrowLeft, RawInputModifiers.None);
            main.KeyReleaseQwerty(PhysicalKey.ArrowLeft, RawInputModifiers.None);
            Assert.Equal(74, timeline.SelectedFrame);
            Assert.Equal(10UL, service.Position); Assert.Equal(60, service.Inputs.Count); Assert.Equal(revision, service.Revision);
        }
        finally { main.CloseAfterCapture(); }
    }

    [Fact]
    public async Task TagsSurviveEditsSaveRecoveryAndReopenWithoutInvalidatingStates()
    {
        using var files = new TestWorkspace(); using var service = new ExecutionService(new FakeBackend());
        await service.LoadGameAsync(files.GamePath, files.Options); await service.NewProjectAsync();
        for (var i = 0; i < 100; i++) await service.StepAsync();
        await service.SaveNamedStateAsync("End");
        var inputs = service.Inputs.ToArray(); var markers = service.StateMarkers.ToArray();
        var id = await service.AddTagAsync(80, "Boss input");
        Assert.Equal(inputs, service.Inputs); Assert.Equal(markers, service.StateMarkers);
        Assert.True(service.IsPreviewCurrent); Assert.Equal(100UL, service.Position);
        await service.SetInputAsync(0, ControllerState.Neutral with { Buttons = PadButtons.A });
        Assert.Single(service.Tags); Assert.Empty(service.StateMarkers);
        await service.RenameTagAsync(id, "Confirm");
        var project = files.FilePath("tagged.tasproj");
        await service.SaveProjectAsync(project);
        await service.SaveRecoveryAsync(files.FilePath("recovery.tasproj"));
        foreach (var path in new[] { project, files.FilePath("recovery.tasproj") })
        {
            using var reopened = new ExecutionService(new FakeBackend());
            await reopened.LoadProjectAsync(path, files.Options);
            Assert.Equal(new TimelineTag(id, 80, "Confirm"), Assert.Single(reopened.Tags));
        }
        await service.RemoveTagAsync(id);
        Assert.Empty(service.Tags); Assert.Equal(100UL, service.Position);
    }

    [AvaloniaFact]
    public async Task MiddleClickAddsVisualTagWithoutMovingPreviewOrSelection()
    {
        using var files = new TestWorkspace(); using var service = new ExecutionService(new FakeBackend());
        await service.LoadGameAsync(files.GamePath, files.Options); await service.NewProjectAsync();
        for (var i = 0; i < 100; i++) await service.StepAsync();
        var main = new MainWindow(["--layout-file", files.FilePath("layout.json")], new AppSettings(), service) { Width = 1060, Height = 720 };
        try
        {
            main.ShowEditor(); main.Show(); main.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            var timeline = main.GetVisualDescendants().OfType<TimelineView>().Single();
            timeline.VisibleFrames = 100; timeline.SetSelection(2, 4); timeline.Update();
            var x = 118 + 40.5 * (timeline.Bounds.Width - 118) / 100;
            var point = timeline.TranslatePoint(new Point(x, 75), main)!.Value;
            main.MouseDown(point, MouseButton.Middle); main.MouseUp(point, MouseButton.Middle);
            await Until(() => main.OwnedWindows.Any(w => w.Title == "Add timeline tag"));
            var dialog = main.OwnedWindows.Single(w => w.Title == "Add timeline tag");
            dialog.GetVisualDescendants().OfType<TextBox>().Single(b => b.Name == "TagName").Text = "Boss input";
            dialog.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "SaveTag").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => timeline.Tags.Count == 1);
            Assert.Equal(40UL, Assert.Single(service.Tags).Position);
            Assert.Equal(100UL, service.Position);
            Assert.Equal(2, timeline.SelectedFrame); Assert.Equal(4, timeline.SelectionEnd);
            main.UpdateLayout();
            point = timeline.TranslatePoint(new Point(x, 43), main)!.Value;
            main.MouseDown(point, MouseButton.Left); main.MouseUp(point, MouseButton.Left);
            Assert.Equal(100UL, service.Position); Assert.Equal(40, timeline.SelectedFrame); Assert.Equal(41, timeline.SelectionEnd);
            if (Environment.GetEnvironmentVariable("TASSTUDIO_TEST_CAPTURE_DIR") is { Length: > 0 } output)
            {
                Directory.CreateDirectory(output); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                main.CaptureRenderedFrame()!.Save(Path.Combine(output, "timeline-tag.png"));
            }
            main.MouseDown(point, MouseButton.Right); main.MouseUp(point, MouseButton.Right);
            var remove = timeline.ContextMenu!.Items.OfType<MenuItem>().Single(m => Equals(m.Header, "Remove tag"));
            await Until(() => remove.IsEnabled);
            remove.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await Until(() => service.Tags.Count == 0);
            Assert.Equal(100UL, service.Position); Assert.Equal(40, timeline.SelectedFrame);
        }
        finally { main.CloseAfterCapture(); }
    }
    [AvaloniaFact]
    public async Task F11AdvancesFromSavedStatePastVisualTagWithoutStoppingOrMovingTag()
    {
        using var files = new TestWorkspace(); using var service = new ExecutionService(new FakeBackend());
        await service.LoadGameAsync(files.GamePath, files.Options); await service.NewProjectAsync();
        for (var i = 0; i < 39; i++) await service.StepAsync();
        await service.SaveNamedStateAsync("Before input");
        for (var i = 0; i < 5; i++) await service.StepAsync();
        await service.AddTagAsync(40, "Press A here");
        var main = new MainWindow(["--layout-file", files.FilePath("layout.json")], new AppSettings(), service);
        try
        {
            main.ShowEditor(); main.Show(); main.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            var previous = main.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "PreviousState");
            previous.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => service.Position == 39);
            var timeline = main.GetVisualDescendants().OfType<TimelineView>().Single();
            timeline.Focus();
            foreach (var frame in new[] { 40UL, 41UL })
            {
                main.KeyPressQwerty(PhysicalKey.F11, RawInputModifiers.None);
                main.KeyReleaseQwerty(PhysicalKey.F11, RawInputModifiers.None);
                await Until(() => service.Position == frame && timeline.Position == frame);
                Assert.Equal(40UL, Assert.Single(service.Tags).Position);
            }
        }
        finally { main.CloseAfterCapture(); }
    }

    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++) { await Task.Delay(10); Dispatcher.UIThread.RunJobs(); }
        Assert.True(condition());
    }
}
