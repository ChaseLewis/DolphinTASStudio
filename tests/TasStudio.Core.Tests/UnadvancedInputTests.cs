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

public sealed class UnadvancedInputTests
{
    [AvaloniaFact]
    public async Task LastFrameEditsPersistToProjectAndRecoveryBeforeAnyAdvance()
    {
        using var files = new TestWorkspace();
        var project = files.FilePath("edited.tasproj"); var recovery = files.FilePath("recovery.tasproj");
        var expected = ControllerState.Neutral with { Buttons = PadButtons.A | PadButtons.Start, StickX = 213, TriggerR = 177 };
        using (var service = new ExecutionService(new FakeBackend { RecordPolls = true }))
        {
            await service.LoadGameAsync(files.GamePath, files.Options); await service.NewProjectAsync();
            for (var i = 0; i < 10; i++) await service.StepAsync();
            await service.SaveNamedStateAsync("Before draft");
            var main = new MainWindow(["--layout-file", files.FilePath("layout.json")], new AppSettings(), service);
            try
            {
                main.RestoreProjectTimelineView(); main.ShowEditor(); main.Show(); main.UpdateLayout(); Dispatcher.UIThread.RunJobs();
                var checks = main.GetVisualDescendants().OfType<CheckBox>().ToArray();
                var axes = main.GetVisualDescendants().OfType<NumericUpDown>().ToArray();
                checks.Single(c => Equals(c.Content, "A")).IsChecked = true;
                axes.Single(n => n.Name == "TasAxis0").Value = 213;
                checks.Single(c => Equals(c.Content, "Start")).IsChecked = true;
                axes.Single(n => n.Name == "TasAxis5").Value = 177;
                // Save immediately, while the edit commands may still be queued.
                await service.SaveProjectAsync(project); await service.SaveRecoveryAsync(recovery);
                Assert.Equal(10UL, service.Position); Assert.Equal(11, service.Inputs.Count);
                Assert.Equal(expected, service.Inputs[10]); Assert.True(service.IsPreviewCurrent);
                Assert.Null(await service.GetPollFrameAsync(10)); // No poll timing is invented before execution.
                Assert.True(Assert.Single(service.StateMarkers).Valid);
                var timeline = main.GetVisualDescendants().OfType<TimelineView>().Single();
                await Until(() => timeline.InputCount == 11);
                Assert.Equal(10, timeline.SelectedFrame);
                timeline.Focus(); Press(main, PhysicalKey.ArrowLeft); Press(main, PhysicalKey.ArrowRight);
                Assert.True(checks.Single(c => Equals(c.Content, "A")).IsChecked);
                Assert.Equal(213m, axes.Single(n => n.Name == "TasAxis0").Value);
            }
            finally { main.CloseAfterCapture(); }
        }
        foreach (var path in new[] { project, recovery })
        {
            using var reopened = new ExecutionService(new FakeBackend { RecordPolls = true });
            await reopened.LoadProjectAsync(path, files.Options);
            Assert.Equal(10UL, reopened.Position); Assert.Equal(11, reopened.Inputs.Count);
            Assert.Equal(expected, reopened.Inputs[10]); Assert.Null(await reopened.GetPollFrameAsync(10));
            var main = new MainWindow(["--layout-file", files.FilePath(path == project ? "reopen.json" : "recover.json")], new AppSettings(), reopened);
            try
            {
                main.RestoreProjectTimelineView(); main.ShowEditor(); main.Show(); main.UpdateLayout(); Dispatcher.UIThread.RunJobs();
                Assert.True(main.GetVisualDescendants().OfType<CheckBox>().Single(c => Equals(c.Content, "Start")).IsChecked);
                Assert.Equal(177m, main.GetVisualDescendants().OfType<NumericUpDown>().Single(n => n.Name == "TasAxis5").Value);
                var timeline = main.GetVisualDescendants().OfType<TimelineView>().Single(); timeline.Focus();
                Press(main, PhysicalKey.F11); await Until(() => reopened.Position == 11 && timeline.SelectedFrame == 11);
                Assert.Equal(11, reopened.Inputs.Count);
                Assert.All((await reopened.GetPollFrameAsync(10))!.Frame.Polls, poll => Assert.Equal(expected, poll.Input));
            }
            finally { main.CloseAfterCapture(); }
        }
    }

    [AvaloniaFact]
    public async Task StickGestureCreatesAnUndoableFirstInputWithoutAdvancing()
    {
        using var files = new TestWorkspace(); using var service = new ExecutionService(new FakeBackend());
        await service.LoadGameAsync(files.GamePath, files.Options); await service.NewProjectAsync();
        var main = new MainWindow(["--layout-file", files.FilePath("layout.json")], new AppSettings(), service);
        try
        {
            main.ShowEditor(); main.Show(); main.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            var pad = main.GetVisualDescendants().OfType<StickPad>().First();
            var target = new Point(pad.Bounds.Width * .75, pad.Bounds.Height * .3);
            var (x, y) = StickPad.Coordinates(target, pad.Bounds.Size);
            var point = pad.TranslatePoint(target, main)!.Value;
            main.MouseDown(point, MouseButton.Left); main.MouseUp(point, MouseButton.Left);
            await Until(() => service.Inputs.Count == 1);
            Assert.Equal(x, service.Inputs[0].StickX); Assert.Equal(y, service.Inputs[0].StickY);
            Assert.Equal(0UL, service.Position); Assert.True(service.IsPreviewCurrent);
            await service.UndoAsync(); Assert.Empty(service.Inputs);
            await service.RedoAsync(); Assert.Equal(x, Assert.Single(service.Inputs).StickX);
            var path = files.FilePath("first.tasproj"); await service.SaveProjectAsync(path);
            Assert.Equal(service.Inputs[0], Assert.Single(FolderProject.Load(path).Archive.Metadata.Inputs));
        }
        finally { main.CloseAfterCapture(); }
    }

    private static void Press(Window window, PhysicalKey key)
    { window.KeyPressQwerty(key, RawInputModifiers.None); window.KeyReleaseQwerty(key, RawInputModifiers.None); }
    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++) { await Task.Delay(10); Dispatcher.UIThread.RunJobs(); }
        Assert.True(condition());
    }
}
