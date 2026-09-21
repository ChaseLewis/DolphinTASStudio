using System.Diagnostics;
using Avalonia.Automation;
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

public sealed class LiveRecordingViewTests
{
    [AvaloniaFact]
    public async Task RecordButtonPreviewsAndAppendsControllerInputAndStopsWithoutLosingRecording()
    {
        using var files = new TestWorkspace(); var backend = new FakeBackend { RecordPolls = true };
        using var service = new ExecutionService(backend);
        await service.LoadGameAsync(files.GamePath, files.Options); await service.NewProjectAsync();
        var original = ControllerState.Neutral with { Buttons = PadButtons.Start };
        for (var i = 0; i < 3; i++) await service.AdvanceFrameAsync(original);
        var take = await service.CaptureTakeAsync("Candidate", 0, 3);
        await service.SeekAsync(0);
        var sample = new LiveInputSource.GamepadSample(true, new() { Buttons = GamepadButton.A, LeftX = short.MaxValue, RightTrigger = 255 });
        var physical = new LiveInputSource(_ => Volatile.Read(ref sample));
        var main = new MainWindow(["--layout-file", files.FilePath("layout.json")], new AppSettings { GamepadIndex = 0 }, service, physical);
        try
        {
            main.ShowEditor(); main.Show(); main.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            var record = main.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "TimelineRecord");
            var use = main.GetVisualDescendants().OfType<CheckBox>().Single(c => c.Name == "UseController");
            var axis = main.GetVisualDescendants().OfType<NumericUpDown>().Single(n => n.Name == "TasAxis0");
            var timeline = main.GetVisualDescendants().OfType<TimelineView>().Single();
            var position = main.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "TimelinePosition");
            timeline.SetSelection(0, 2, take);
            Assert.NotEqual(true, use.IsChecked);
            Assert.True(record.IsEffectivelyVisible); Assert.True(record.IsEnabled);
            Assert.Equal("Record", AutomationProperties.GetName(record));
            record.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var held = ControllerState.Neutral with { Buttons = PadButtons.A | PadButtons.R, StickX = 255, TriggerR = 255 };
            await Until(() => service.Inputs.Count >= 6 && service.Inputs.Last() == held && axis.Value == 255 && record.IsEnabled && position.Text!.StartsWith("REC"));
            Assert.True(use.IsChecked); Assert.False(use.IsEffectivelyEnabled);
            Assert.Null(timeline.SelectedTake); Assert.True(timeline.SelectedFrame > 0);
            Assert.Equal("Stop recording", AutomationProperties.GetName(record));
            Assert.All(service.Inputs.Take(3), input => Assert.Equal(original, input));
            Assert.All(service.Takes.Single().Inputs, input => Assert.Equal(original, input));

            Volatile.Write(ref sample, new(true, new() { Buttons = GamepadButton.B, LeftX = short.MinValue }));
            var changed = ControllerState.Neutral with { Buttons = PadButtons.B, StickX = 0 };
            await Until(() => service.Inputs.Last() == changed && axis.Value == 0);
            record.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => !service.IsRunning && record.IsEnabled && AutomationProperties.GetName(record) == "Record");
            Assert.Equal((int)service.Position, timeline.SelectedFrame);
            Assert.True(use.IsEffectivelyEnabled);
            var recorded = service.Inputs.ToArray();
            await Task.Delay(80); Dispatcher.UIThread.RunJobs();
            Assert.Equal(recorded, service.Inputs);
            Assert.All((await service.GetPollFrameAsync(recorded.Length - 1))!.Frame.Polls, poll => Assert.Equal(changed, poll.Input));

            record.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => service.Inputs.Count > recorded.Length && record.IsEnabled);
            main.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            main.KeyReleaseQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            await Until(() => !service.IsRunning && AutomationProperties.GetName(record) == "Record");
            Assert.Equal(recorded, service.Inputs.Take(recorded.Length));
            Assert.Equal((int)service.Position, timeline.SelectedFrame);
        }
        finally { main.CloseAfterCapture(); }
    }

    [AvaloniaFact]
    public async Task RecordingAcceptsMappedArrowKeysEvenWhenTimelineHasFocus()
    {
        using var files = new TestWorkspace(); using var service = new ExecutionService(new FakeBackend());
        await service.LoadGameAsync(files.GamePath, files.Options); await service.NewProjectAsync();
        var main = new MainWindow(["--layout-file", files.FilePath("layout.json")], new AppSettings { GamepadIndex = -1 }, service);
        try
        {
            main.ShowEditor(); main.Show(); main.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            var record = main.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "TimelineRecord");
            var timeline = main.GetVisualDescendants().OfType<TimelineView>().Single();
            record.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => service.IsRecordingLive && record.IsEnabled);
            timeline.Focus();
            main.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None);
            await Until(() => service.Inputs.Any(input => input.StickX == 255));
            main.KeyReleaseQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None);
            await Until(() => service.Inputs.Last().StickX == 128);
            record.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => !service.IsRunning && record.IsEnabled && AutomationProperties.GetName(record) == "Record");
            Assert.Equal((int)service.Position, timeline.SelectedFrame);
        }
        finally { main.CloseAfterCapture(); }
    }

    private static async Task Until(Func<bool> condition)
    {
        var timer = Stopwatch.StartNew();
        while (!condition() && timer.Elapsed < TimeSpan.FromSeconds(5)) { await Task.Delay(10); Dispatcher.UIThread.RunJobs(); }
        Assert.True(condition());
    }
}
