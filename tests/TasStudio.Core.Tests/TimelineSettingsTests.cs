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

public sealed class TimelineSettingsTests
{
    [Theory]
    [InlineData(0, 0, "00:00:00.000")]
    [InlineData(3661.123, 0, "01:01:01.123")]
    [InlineData(59.9996, 0, "00:01:00.000")]
    [InlineData(90000.125, 0, "25:00:00.125")]
    [InlineData(90.5, 90500, "00:00:00.000")]
    [InlineData(90.499, 90500, "-00:00:00.001")]
    public void ClockIncludesMillisecondsAndSignedRunTime(double seconds, long offset, string expected) =>
        Assert.Equal(expected, MainWindow.FormatTimelineTime(seconds, offset));

    [Fact]
    public async Task OffsetPersistsWithoutChangingEmulatorHistoryOrStates()
    {
        using var files = new TestWorkspace(); var backend = new FakeBackend();
        using var service = new ExecutionService(backend);
        await service.LoadGameAsync(files.GamePath, files.Options with { Configuration = new EmulationConfiguration() });
        await service.NewProjectAsync(); await service.StepAsync(); await service.SaveNamedStateAsync("Keep");
        var history = await service.HistoryAtAsync(1); var states = service.StateMarkers.ToArray();
        var calls = backend.Calls.ToArray(); var revision = service.Revision;
        await service.SetTimelineOffsetAsync(90500);
        Assert.Equal(calls, backend.Calls.ToArray());
        Assert.Equal(1UL, service.Position); Assert.Equal(1d / 60, service.ElapsedSeconds);
        Assert.Equal(history, await service.HistoryAtAsync(1)); Assert.Equal(states, service.StateMarkers);
        Assert.True(service.IsPreviewCurrent); Assert.Equal(revision + 1, service.Revision);
        await service.SetTimelineOffsetAsync(90500); Assert.Equal(revision + 1, service.Revision);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.SetTimelineOffsetAsync(-1));
        foreach (var recovery in new[] { false, true })
        {
            var path = files.FilePath(recovery ? "recovery.tasproj" : "saved.tasproj");
            if (recovery) await service.SaveRecoveryAsync(path); else await service.SaveProjectAsync(path);
            using var reopened = new ExecutionService(new FakeBackend());
            await reopened.LoadProjectAsync(path, files.Options);
            Assert.Equal(90500, reopened.TimelineOffsetMilliseconds);
        }
        await service.ApplyConfigurationAsync(new EmulationConfiguration { StartUtcSeconds = 1000000000 }, service.Checkpoints);
        Assert.Equal(90500, service.TimelineOffsetMilliseconds);
        backend.FailNextLoad = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApplyConfigurationAsync(
            new EmulationConfiguration { StartUtcSeconds = 1000000001 }, service.Checkpoints));
        Assert.Equal(90500, service.TimelineOffsetMilliseconds);
        await service.LoadGameAsync(files.GamePath, files.Options); await service.NewProjectAsync();
        Assert.Equal(0, service.TimelineOffsetMilliseconds);
    }

    [AvaloniaFact]
    public async Task TimelineTabAppliesOnlyDisplayOffset()
    {
        using var files = new TestWorkspace(); var backend = new FakeBackend();
        using var service = new ExecutionService(backend);
        await service.LoadGameAsync(files.GamePath, files.Options); await service.NewProjectAsync(); await service.StepAsync();
        var main = new MainWindow(["--layout-file", files.FilePath("layout.json")], new AppSettings(), service);
        try
        {
            main.ShowEditor(); main.Show(); main.UpdateLayout();
            var opening = main.ApplicationSettingsCore(null, 5);
            for (var i = 0; i < 100 && !main.OwnedWindows.Any(); i++) { await Task.Delay(10); Dispatcher.UIThread.RunJobs(); }
            var dialog = Assert.Single(main.OwnedWindows); dialog.UpdateLayout();
            var offset = dialog.GetVisualDescendants().OfType<NumericUpDown>().Single(c => c.Name == "TimelineTimeOffset");
            offset.Value = 90.500m;
            var calls = backend.Calls.ToArray(); var history = await service.HistoryAtAsync(1);
            dialog.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Apply timeline settings"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await opening;
            Assert.Equal(90500, service.TimelineOffsetMilliseconds); Assert.Equal(calls, backend.Calls.ToArray());
            Assert.Equal(history, await service.HistoryAtAsync(1)); Assert.Equal(1UL, service.Position);
            for (var i = 0; i < 20; i++) { await Task.Delay(10); Dispatcher.UIThread.RunJobs(); }
            Assert.StartsWith("Time: -00:01:30.483 |", main.GetVisualDescendants().OfType<TextBlock>().Single(c => c.Name == "TimelinePosition").Text);
        }
        finally { foreach (var dialog in main.OwnedWindows.ToArray()) dialog.Close(false); main.CloseAfterCapture(); }
    }

    [AvaloniaFact]
    public async Task ContextDeleteRemovesClickedTakeAndSupportsUndoAndDeleteKey()
    {
        using var files = new TestWorkspace(); using var service = new ExecutionService(new FakeBackend());
        await service.LoadGameAsync(files.GamePath, files.Options); await service.NewProjectAsync();
        for (var i = 0; i < 10; i++) await service.StepAsync();
        var selected = await service.CaptureTakeAsync("Selected", 0, 5);
        var clicked = await service.CaptureTakeAsync("Clicked", 2, 5);
        var main = new MainWindow(["--layout-file", files.FilePath("layout.json")], new AppSettings(), service);
        try
        {
            main.ShowEditor(); main.Show(); main.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            var timeline = main.GetVisualDescendants().OfType<TimelineView>().Single();
            timeline.SetSelection(0, 5, selected); timeline.Update(); main.UpdateLayout();
            var point = timeline.TranslatePoint(new Point(30, 64 + 2 * 38 + 15), main)!.Value;
            main.MouseDown(point, MouseButton.Right); main.MouseUp(point, MouseButton.Right);
            Assert.Equal(clicked, timeline.ContextTake); Assert.Equal(selected, timeline.SelectedTake);
            timeline.ContextMenu!.Items.OfType<MenuItem>().Single(m => Equals(m.Header, "Delete take"))
                .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            for (var i = 0; i < 100 && service.Takes.Count != 1; i++) { await Task.Delay(10); Dispatcher.UIThread.RunJobs(); }
            Assert.Equal(selected, Assert.Single(service.Takes).Id); Assert.Equal(selected, timeline.SelectedTake);
            Assert.Equal(10, service.Inputs.Count); Assert.Equal(10UL, service.Position);
            await service.UndoAsync(); Assert.Equal(2, service.Takes.Count);
            timeline.ContextMenu.Close(); timeline.Focus();
            for (var i = 0; i < 10; i++) { await Task.Delay(10); Dispatcher.UIThread.RunJobs(); }
            main.KeyPressQwerty(PhysicalKey.Delete, RawInputModifiers.None);
            main.KeyReleaseQwerty(PhysicalKey.Delete, RawInputModifiers.None);
            for (var i = 0; i < 100 && service.Takes.Count != 1; i++) { await Task.Delay(10); Dispatcher.UIThread.RunJobs(); }
            Assert.Equal(clicked, Assert.Single(service.Takes).Id); Assert.Null(timeline.SelectedTake);
        }
        finally { main.CloseAfterCapture(); }
    }
}
