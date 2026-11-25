using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
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

public sealed class MovieModeTests
{
    [Fact]
    public async Task PresentationUsesCompletedGroupsLastPollAndSurvivesReplaySeekAndEmptyPolls()
    {
        using var files = new TestWorkspace(); var backend = new PolledBackend { RecordPolls = true };
        using var service = new ExecutionService(backend);
        await service.LoadGameAsync(files.GamePath, files.Options); await service.NewProjectAsync();
        await service.ConfigureCheckpointsAsync(new(Enabled: false));
        var a = ControllerState.Neutral with { Buttons = PadButtons.A };
        var b = ControllerState.Neutral with { Buttons = PadButtons.B, StickX = 255, TriggerL = 180 };
        backend.LastPoll = b;
        var shown = new List<PlaybackPresentation>(); service.PresentationReady += shown.Add;
        await service.AdvanceFrameAsync(a);
        Assert.Equal(b, service.LatestPresentation.Input); Assert.Equal(1UL, service.LatestPresentation.Position);
        Assert.Equal(1, service.LatestPresentation.Video!.Sequence);
        Assert.Single(shown); // Backend emits before it finishes exposing its final poll data.
        backend.PollsPerStep = 0;
        await service.AdvanceFrameAsync(ControllerState.Neutral);
        Assert.Equal(b, service.LatestPresentation.Input); // No poll does not release held buttons.
        await service.SaveNamedStateAsync("Empty poll boundary");
        var marker = Assert.Single(service.StateMarkers);
        shown.Clear(); await service.SeekAsync(0);
        Assert.Null(service.LatestPresentation.Input); Assert.Null(service.LatestPresentation.Video);
        Assert.Single(shown);
        await service.LoadMarkerAsync(marker.Id);
        Assert.Equal(b, service.LatestPresentation.Input); Assert.Equal(2UL, service.LatestPresentation.Position);
        backend.PollsPerStep = 4;
        await service.SeekAsync(0); shown.Clear(); await service.StepRecordedFrameAsync();
        Assert.Equal(b, service.LatestPresentation.Input); Assert.Single(shown);
        await service.SetInputAsync(0, ControllerState.Neutral);
        Assert.Equal(b, service.LatestPresentation.Input); // Editing the timeline does not rewrite the visible image's input.
        await service.StopAsync(); Assert.Null(service.LatestPresentation.Input); Assert.Null(service.LatestPresentation.Video);
    }

    private sealed class PolledBackend : FakeBackend, IEmulatorBackend
    {
        public ControllerState LastPoll { get; set; }
        public new InputPollFrame? LastInputPollFrame { get; private set; }
        public new void Step(ControllerState input)
        {
            base.Step(input);
            var frame = base.LastInputPollFrame!;
            LastInputPollFrame = frame with { Polls = frame.Polls.Select((poll, index) =>
                index == frame.Polls.Length - 1 ? poll with { Input = LastPoll } : poll).ToArray() };
        }
        public new void ReplayInputPollFrame(InputPollFrame frame)
        { base.ReplayInputPollFrame(frame); LastInputPollFrame = frame.Copy(); }
    }

    [AvaloniaFact]
    public async Task MovieLayoutFloatsHidesAndRestoresEditorWithoutAuthoringInput()
    {
        using var files = new TestWorkspace(); var backend = new FakeBackend();
        using var service = new ExecutionService(backend);
        await service.LoadGameAsync(files.GamePath, files.Options); await service.NewProjectAsync();
        await service.ConfigureCheckpointsAsync(new(Enabled: false));
        for (var i = 0; i < 180; i++) await service.AdvanceFrameAsync(ControllerState.Neutral with { Buttons = PadButtons.A, StickX = 235 });
        var before = service.Inputs.ToArray();
        var layoutPath = files.FilePath("layout.json");
        var main = new MainWindow(["--layout-file", layoutPath], new AppSettings(), service);
        try
        {
            main.ShowEditor(); main.Show(); main.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            var dock = main.GetVisualDescendants().OfType<DockControl>().First();
            var factory = (WorkspaceFactory)dock.Factory!;
            var timeline = factory.Panels["timeline"].View!.GetVisualDescendants().OfType<TimelineView>().Single();
            timeline.VisibleFrames = 60; timeline.SetSelection(7, 8); timeline.FirstFrame = 3;
            factory.FloatDockable(factory.Panels["input"]); Dispatcher.UIThread.RunJobs();
            var saved = WorkspaceFactory.Capture((Dock.Model.Controls.IRootDock)dock.Layout!).ToJson();
            await main.EnterMovieModeAsync(); Dispatcher.UIThread.RunJobs();
            var step = main.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "MovieStep");
            Assert.True(service.IsRunning); Assert.False(step.IsEnabled);
            main.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.F12 });
            Assert.True(service.IsRunning); // A disabled step shortcut must not pause playback.
            await service.PauseAsync();
            main.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                while (!step.IsEnabled) { Dispatcher.UIThread.RunJobs(); await Task.Delay(5, timeout.Token); }
            Assert.True(step.IsEnabled);
            Assert.True(main.IsMovieMode);
            Assert.True(Avalonia.Media.RenderOptions.GetRequiresFullOpacityHandling(main));
            Assert.False(factory.Panels["input"].View!.IsEnabled);
            Assert.Equal(new[] { "game", "controller" }, WorkspaceFactory.Walk(dock.Layout!).OfType<StudioPanel>().Select(p => p.Id));
            main.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.F11 });
            main.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Z, KeyModifiers = KeyModifiers.Control });
            Assert.Equal(before, service.Inputs);
            if (Environment.GetEnvironmentVariable("TASSTUDIO_TEST_CAPTURE_DIR") is { Length: > 0 } output)
            {
                Directory.CreateDirectory(output); main.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                main.CaptureRenderedFrame()!.Save(Path.Combine(output, "movie-mode.png"));
            }
            factory.FloatDockable(factory.Panels["controller"]); factory.FloatDockable(factory.Panels["game"]);
            Dispatcher.UIThread.RunJobs();
            var inputsHost = Assert.IsType<HostWindow>(TopLevel.GetTopLevel(factory.Panels["controller"].View!));
            var gameHost = Assert.IsType<HostWindow>(TopLevel.GetTopLevel(factory.Panels["game"].View!));
            Assert.True(Avalonia.Media.RenderOptions.GetRequiresFullOpacityHandling(inputsHost));
            Assert.NotSame(inputsHost, gameHost);
            // Frame presentation keeps working on the floating host even with the main window minimized.
            main.WindowState = WindowState.Minimized;
            await service.SeekAsync(1);
            var display = factory.Panels["controller"].View!.GetVisualDescendants().OfType<ControllerDisplay>().Single();
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            {
                while (display.Input != before[0])
                {
                    AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs();
                    await Task.Delay(5, timeout.Token);
                }
            }
            main.WindowState = WindowState.Normal;
            await service.SeekAsync(0); await service.PlayRecordedAsync();
            factory.CloseDockable(factory.Panels["controller"]); Dispatcher.UIThread.RunJobs();
            Assert.True(service.IsRunning);
            main.ShowWorkspacePanel("controller"); Dispatcher.UIThread.RunJobs();
            Assert.IsType<HostWindow>(TopLevel.GetTopLevel(factory.Panels["controller"].View!));
            await main.ExitMovieModeAsync(); Dispatcher.UIThread.RunJobs();
            Assert.False(main.IsMovieMode); Assert.False(service.IsRunning);
            Assert.Equal(saved, WorkspaceFactory.Capture((Dock.Model.Controls.IRootDock)dock.Layout!).ToJson());
            Assert.True(factory.Panels["input"].View!.IsEnabled);
            Assert.Equal(7, timeline.SelectedFrame); Assert.Equal(3, timeline.FirstFrame);
            Assert.Equal(before, service.Inputs);
            Assert.Equal(2, WorkspaceLayout.Parse(File.ReadAllText(Path.ChangeExtension(layoutPath, "movie.json"))).Windows.Length);
            await main.EnterMovieModeAsync(false); await service.PauseAsync(); Dispatcher.UIThread.RunJobs();
            Assert.Equal(2, WorkspaceFactory.Capture((Dock.Model.Controls.IRootDock)dock.Layout!).Windows.Length);
            await service.SeekAsync((ulong)before.Length); await service.PlayRecordedAsync();
            Assert.False(service.IsRunning); Assert.Equal(before, service.Inputs);
            await main.ExitMovieModeAsync();
        }
        finally { main.CloseAfterCapture(); }
    }

    [Fact]
    public void StickDisplayUsesEntireRangeWithPositiveYUp()
    {
        Assert.Equal(new Point(110, 148), ControllerDisplay.StickPoint(new(110, 148), 40, 128, 128));
        Assert.Equal(new Point(70, 108), ControllerDisplay.StickPoint(new(110, 148), 40, 0, 255));
        Assert.Equal(new Point(150, 188), ControllerDisplay.StickPoint(new(110, 148), 40, 255, 0));
    }
}
