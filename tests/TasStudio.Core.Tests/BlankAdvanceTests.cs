using System.Diagnostics;
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

public sealed class BlankAdvanceTests
{
    [AvaloniaFact]
    public async Task ParkedCursorAlwaysShowsAndEditsItsRecordingAcrossF12AndSeek()
    {
        using var files = new TestWorkspace(); using var service = new ExecutionService(new FakeBackend { RecordPolls = true, FieldsPerStep = 2 });
        await service.LoadGameAsync(files.GamePath, files.Options); await service.NewProjectAsync();
        var recorded = ControllerState.Neutral with { Buttons = PadButtons.A, StickX = 200, TriggerR = 190 };
        service.LiveInput = () => recorded;
        for (var i = 0; i < 8; i++) await service.StepAsync();
        await service.SeekAsync(2); await service.SaveNamedStateAsync("Cursor");
        var main = new MainWindow(["--layout-file", files.FilePath("layout.json")], new AppSettings(), service);
        try
        {
            main.RestoreProjectTimelineView(); main.ShowEditor(); main.Show(); main.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            var timeline = main.GetVisualDescendants().OfType<TimelineView>().Single();
            var next = main.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "NextFrame");
            var seek = main.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "SeekSelection");
            var axis = main.GetVisualDescendants().OfType<NumericUpDown>().Single(n => n.Name == "TasAxis0");
            var trigger = main.GetVisualDescendants().OfType<NumericUpDown>().Single(n => n.Name == "TasAxis5");
            var a = main.GetVisualDescendants().OfType<CheckBox>().Single(c => Equals(c.Content, "A"));
            var preview = main.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "PreviewFrame");
            axis.GetVisualDescendants().OfType<TextBox>().First().Focus();
            for (ulong position = 3; position <= 5; position++)
            {
                Press(main, PhysicalKey.F12);
                await Until(() => service.Position == position && next.IsEnabled && preview.Text == $"Frame {position * 2}");
                Assert.Equal(2, timeline.SelectedFrame); Assert.Equal(3, timeline.SelectionEnd);
                Assert.True(a.IsChecked); Assert.Equal(200m, axis.Value); Assert.Equal(190m, trigger.Value);
                Assert.All((await service.GetPollFrameAsync((int)position - 1))!.Frame.Polls, poll => Assert.Equal(recorded, poll.Input));
            }
            Assert.Equal(recorded, service.Inputs[2]); Assert.Equal(recorded, service.Inputs[5]);
            // The parked inspector edits the cursor's recorded group, never a hidden future input.
            a.IsChecked = false; axis.Value = 210;
            await Until(() => service.Inputs[2].Buttons == PadButtons.None && service.Inputs[2].StickX == 210);
            Assert.False(service.IsPreviewCurrent);
            Assert.Equal(recorded, service.Inputs[5]);
            var beforeSeek = service.Inputs.ToArray();
            await Until(() => seek.IsEnabled);
            seek.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => service.Position == 2 && next.IsEnabled && preview.Text == "Frame 4");
            Assert.False(a.IsChecked); Assert.Equal(210m, axis.Value); Assert.Equal(190m, trigger.Value);
            Assert.Equal(beforeSeek, service.Inputs); Assert.True(service.IsPreviewCurrent);
            Assert.Equal(2, timeline.SelectedFrame);
            Press(main, PhysicalKey.F12);
            await Until(() => service.Position == 3 && next.IsEnabled && preview.Text == "Frame 6");
            Assert.Equal(210m, axis.Value); Assert.Equal(2, timeline.SelectedFrame);
            Assert.Equal(210, service.Inputs[2].StickX);
        }
        finally { main.CloseAfterCapture(); }
    }

    [AvaloniaFact]
    public async Task LiveControllerCannotReplaceTheDisplayOrInputOfAParkedHistoricalCursor()
    {
        using var files = new TestWorkspace(); using var service = new ExecutionService(new FakeBackend());
        await service.LoadGameAsync(files.GamePath, files.Options); await service.NewProjectAsync();
        await service.SetInputAsync(0, ControllerState.Neutral with { Buttons = PadButtons.A });
        var sample = new LiveInputSource.GamepadSample(true, new() { Buttons = GamepadButton.A });
        var physical = new LiveInputSource(_ => Volatile.Read(ref sample));
        var main = new MainWindow(["--layout-file", files.FilePath("layout.json")], new AppSettings { GamepadIndex = 0 }, service, physical);
        try
        {
            main.ShowEditor(); main.Show(); main.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            var use = main.GetVisualDescendants().OfType<CheckBox>().Single(c => c.Name == "UseController");
            var a = main.GetVisualDescendants().OfType<CheckBox>().Single(c => Equals(c.Content, "A"));
            var b = main.GetVisualDescendants().OfType<CheckBox>().Single(c => Equals(c.Content, "B"));
            var next = main.GetVisualDescendants().OfType<Button>().Single(c => c.Name == "NextFrame");
            var preview = main.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "PreviewFrame");
            use.IsChecked = true; await Until(() => a.IsChecked == true);
            Press(main, PhysicalKey.F12);
            await Until(() => service.Position == 1 && next.IsEnabled && preview.Text == "Frame 1");
            Volatile.Write(ref sample, new(true, new() { Buttons = GamepadButton.B }));
            await Task.Delay(80); Dispatcher.UIThread.RunJobs();
            Assert.True(use.IsChecked); Assert.True(a.IsChecked); Assert.False(b.IsChecked);
            await PressF12AtEnd(main, service);
            Assert.Equal(1UL, service.Position); Assert.Single(service.Inputs);
            Assert.Equal(PadButtons.A, service.Inputs[0].Buttons);
            Assert.True(a.IsChecked); Assert.False(b.IsChecked);
        }
        finally { main.CloseAfterCapture(); }
    }

    [AvaloniaFact]
    public async Task F10AtUnrecordedEndDoesNotInheritThePreviousDisplayedInput()
    {
        using var files = new TestWorkspace(); using var service = new ExecutionService(new FakeBackend { RecordPolls = true });
        await service.LoadGameAsync(files.GamePath, files.Options); await service.NewProjectAsync();
        var main = new MainWindow(["--layout-file", files.FilePath("layout.json")], new AppSettings(), service);
        try
        {
            main.ShowEditor(); main.Show(); main.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            var a = main.GetVisualDescendants().OfType<CheckBox>().Single(c => Equals(c.Content, "A"));
            var timeline = main.GetVisualDescendants().OfType<TimelineView>().Single();
            var next = main.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "NextFrame");
            var preview = main.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "PreviewFrame");
            a.IsChecked = true;
            Press(main, PhysicalKey.F11);
            await Until(() => service.Position == 1 && timeline.SelectedFrame == 1 && next.IsEnabled);
            Assert.True(a.IsChecked); Assert.Single(service.Inputs);
            Press(main, PhysicalKey.F10);
            await Until(() => service.Position == 2 && preview.Text == "Frame 2" && next.IsEnabled);
            Assert.Equal(2, timeline.SelectedFrame); Assert.Equal(3, timeline.SelectionEnd); Assert.Equal(PadButtons.A, service.Inputs[0].Buttons);
            Assert.Equal(ControllerState.Neutral, service.Inputs[1]); Assert.False(a.IsChecked);
            Assert.All((await service.GetPollFrameAsync(1))!.Frame.Polls, poll => Assert.Equal(ControllerState.Neutral, poll.Input));
        }
        finally { main.CloseAfterCapture(); }
    }

    [AvaloniaFact]
    public async Task F12StopsAtEndWhileF10ClearsAndF11KeepsInputForNewFrames()
    {
        using var files = new TestWorkspace(); using var service = new ExecutionService(new FakeBackend { RecordPolls = true });
        await service.LoadGameAsync(files.GamePath, files.Options); await service.NewProjectAsync();
        var recorded = ControllerState.Neutral with { Buttons = PadButtons.A | PadButtons.Start, StickX = 200, TriggerR = 220 };
        service.LiveInput = () => recorded;
        await service.StepAsync(); await service.StepAsync(); await service.SeekAsync(0);
        var main = new MainWindow(["--layout-file", files.FilePath("layout.json")], new AppSettings(), service);
        try
        {
            main.ShowEditor(); main.Show(); main.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            var timeline = main.GetVisualDescendants().OfType<TimelineView>().Single();
            var axis = main.GetVisualDescendants().OfType<NumericUpDown>().Single(n => n.Name == "TasAxis0");
            var next = main.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "NextFrame");
            var preview = main.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "PreviewFrame");
            axis.GetVisualDescendants().OfType<TextBox>().First().Focus();
            Press(main, PhysicalKey.F10);
            await Until(() => service.Position == 1 && preview.Text == "Frame 1" && next.IsEnabled);
            Assert.False(service.IsRunning); Assert.Equal(1, timeline.SelectedFrame);
            Assert.Equal(recorded, service.Inputs[0]); Assert.Equal(recorded, service.Inputs[1]);
            Assert.All((await service.GetPollFrameAsync(0))!.Frame.Polls, poll => Assert.Equal(recorded, poll.Input));
            Press(main, PhysicalKey.F12);
            await Until(() => service.Position == 2 && preview.Text == "Frame 2" && next.IsEnabled);
            Assert.Equal(recorded, service.Inputs[1]); Assert.Equal(200m, axis.Value); Assert.Equal(1, timeline.SelectedFrame);
            Press(main, PhysicalKey.F10);
            await Until(() => service.Position == 3 && preview.Text == "Frame 3" && next.IsEnabled);
            Assert.Equal(ControllerState.Neutral, service.Inputs[2]); Assert.Equal(3, timeline.SelectedFrame);
            Assert.Equal(4, timeline.SelectionEnd); Assert.Equal(128m, axis.Value);
            await PressF12AtEnd(main, service);
            Assert.Equal(3UL, service.Position); Assert.Equal(3, service.Inputs.Count);
            Assert.Equal(3, timeline.SelectedFrame);
            timeline.MoveCursor(-1); timeline.MoveCursor(-1); timeline.MoveCursor(-1);
            Assert.Equal(200m, axis.Value);
            Press(main, PhysicalKey.F11);
            await Until(() => service.Position == 4 && preview.Text == "Frame 4" && next.IsEnabled);
            Assert.Equal(recorded, service.Inputs[3]); Assert.Equal(0, timeline.SelectedFrame);
        }
        finally { main.CloseAfterCapture(); }
    }

    private static async Task PressF12AtEnd(MainWindow main, ExecutionService service)
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnStatus(string status) { if (status.StartsWith("End of recorded input")) completed.TrySetResult(); }
        service.StatusChanged += OnStatus;
        try
        {
            Press(main, PhysicalKey.F12);
            await Until(() => completed.Task.IsCompleted && main.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "NextFrame").IsEnabled);
        }
        finally { service.StatusChanged -= OnStatus; }
    }

    private static void Press(MainWindow main, PhysicalKey key)
    { main.KeyPressQwerty(key, RawInputModifiers.None); main.KeyReleaseQwerty(key, RawInputModifiers.None); }
    private static async Task Until(Func<bool> condition)
    {
        var timer = Stopwatch.StartNew();
        while (!condition() && timer.Elapsed < TimeSpan.FromSeconds(5)) { await Task.Delay(10); Dispatcher.UIThread.RunJobs(); }
        Assert.True(condition());
    }
}
