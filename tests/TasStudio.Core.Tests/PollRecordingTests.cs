using System.Runtime.InteropServices;
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

public sealed class PollRecordingTests
{
    [Fact]
    public async Task ZeroPollGroupsAndExportedReplaysRetainTheirBoundaries()
    {
        using var files = new TestWorkspace(); var backend = new FakeBackend { RecordPolls = true, PollsPerStep = 0 };
        using var service = new ExecutionService(backend);
        await service.LoadGameAsync(files.GamePath, files.Options); await service.NewProjectAsync();
        await service.StepAsync();
        Assert.Empty((await service.GetPollFrameAsync(0))!.Frame.Polls);
        Assert.Equal(new ulong[] { 0, 0 }, service.PollBoundaries);
        var path = files.FilePath("zero.tasreplay"); await service.ExportReplayAsync(path);
        await service.LoadProjectAsync(path, files.Options);
        await service.SeekAsync(1);
        Assert.True(service.IsPreviewCurrent); Assert.Equal(1UL, service.Position);
        Assert.Empty((await service.GetPollFrameAsync(0))!.Frame.Polls);
    }

    [Fact]
    public async Task PollsPersistAndWholeGroupEditsInvalidateStatesAndRebuildTiming()
    {
        Assert.Equal(32, Marshal.SizeOf<InputPoll>());
        using var files = new TestWorkspace(); var backend = new FakeBackend { RecordPolls = true, FieldsPerStep = 2 };
        using var service = new ExecutionService(backend);
        await service.CreateProjectAsync(files.FilePath("start.tasproj"), files.GamePath, files.Options);
        for (var i = 0; i < 3; i++) await service.StepAsync();
        Assert.Equal(new ulong[] { 0, 4, 8, 12 }, service.PollBoundaries);
        var before = await service.ReadMemoryAsync(0x80000000, 4);
        await service.SaveNamedStateAsync("End");
        await service.SeekAsync(0); await service.SeekAsync(2); await service.SeekAsync(3);
        Assert.Equal(before, await service.ReadMemoryAsync(0x80000000, 4));
        Assert.Contains(backend.Calls, call => call.Operation == nameof(FakeBackend.ReplayInputPollFrame));
        var a = ControllerState.Neutral with { Buttons = PadButtons.A };
        await service.SetInputAsync(1, a);
        var edited = (await service.GetPollFrameAsync(1))!;
        Assert.Equal(4, edited.Frame.Polls.Length); Assert.All(edited.Frame.Polls, poll => Assert.Equal(a, poll.Input));
        Assert.Empty(edited.PrefixHash); Assert.False(service.IsPreviewCurrent); Assert.Empty(service.StateMarkers);
        Assert.All((await service.GetPollFrameAsync(0))!.Frame.Polls, poll => Assert.Equal(ControllerState.Neutral, poll.Input));
        await service.UndoAsync(); Assert.True(service.IsPreviewCurrent);
        await service.RedoAsync(); Assert.False(service.IsPreviewCurrent);
        // Regenerate all affected groups against their new history, then validate replay.
        await service.SeekAsync(3);
        Assert.NotEmpty((await service.GetPollFrameAsync(1))!.PrefixHash);
        var expected = await service.ReadMemoryAsync(0x80000000, 4);
        var project = files.FilePath("edited.tasproj"); var recovery = files.FilePath("recovery.tasproj");
        await service.SaveProjectAsync(project); await service.SaveRecoveryAsync(recovery);
        foreach (var path in new[] { project, recovery })
        {
            Assert.Equal(3, FolderProject.Load(path).Archive.Metadata.PollFrames.Length);
            using var reopened = new ExecutionService(new FakeBackend { RecordPolls = true, FieldsPerStep = 2 });
            await reopened.LoadProjectAsync(path, files.Options);
            Assert.Equal(expected, await reopened.ReadMemoryAsync(0x80000000, 4));
            Assert.Equal(new ulong[] { 0, 4, 8, 12 }, reopened.PollBoundaries);
        }
        await service.SeekAsync(0); backend.PollsPerStep = 3;
        await Assert.ThrowsAsync<InvalidDataException>(() => service.SeekAsync(3));
        Assert.False(service.IsPreviewCurrent); Assert.False(service.IsRunning);
    }

    [AvaloniaFact]
    public async Task PollTimelineSnapsScrubbingAndEditsToWholeFrameGroup()
    {
        using var files = new TestWorkspace(); using var service = new ExecutionService(new FakeBackend { RecordPolls = true, FieldsPerStep = 2 });
        await service.LoadGameAsync(files.GamePath, files.Options); await service.NewProjectAsync();
        for (var i = 0; i < 10; i++) await service.StepAsync();
        await service.SeekAsync(2);
        var main = new MainWindow(["--layout-file", files.FilePath("layout.json")], new AppSettings(), service) { Width = 1100, Height = 760 };
        try
        {
            main.RestoreProjectTimelineView(); main.ShowEditor(); main.Show(); main.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            var timeline = main.GetVisualDescendants().OfType<TimelineView>().Single();
            timeline.VisibleFrames = 10; timeline.FirstFrame = 0; timeline.Update(); main.UpdateLayout();
            var label = main.GetVisualDescendants().OfType<TextBlock>().Single(b => b.Name == "TimelinePosition");
            Assert.Equal("Time: 00:00:00 | Frame: 4 | Poll: 8", label.Text);
            timeline.Focus(); main.KeyPressQwerty(PhysicalKey.F11, RawInputModifiers.None);
            main.KeyReleaseQwerty(PhysicalKey.F11, RawInputModifiers.None);
            for (var i = 0; i < 200 && label.Text != "Time: 00:00:00 | Frame: 6 | Poll: 12"; i++) { await Task.Delay(10); Dispatcher.UIThread.RunJobs(); }
            Assert.Equal("Time: 00:00:00 | Frame: 6 | Poll: 12", label.Text);
            // Poll 5.5 is inside group 1 (polls 4..7), so select and edit all four polls.
            var x = 118 + 5.5 / 40 * (timeline.Bounds.Width - 118);
            var point = timeline.TranslatePoint(new Point(x, 76), main)!.Value;
            main.MouseDown(point, MouseButton.Left); main.MouseUp(point, MouseButton.Left);
            Assert.Equal(1, timeline.SelectedFrame); Assert.Equal(2, timeline.SelectionEnd);
            var a = main.GetVisualDescendants().OfType<CheckBox>().Single(c => Equals(c.Content, "A"));
            a.IsChecked = true;
            for (var i = 0; i < 200 && !service.Inputs[1].Buttons.HasFlag(PadButtons.A); i++) { await Task.Delay(10); Dispatcher.UIThread.RunJobs(); }
            Assert.All((await service.GetPollFrameAsync(1))!.Frame.Polls, poll => Assert.True(poll.Input.Buttons.HasFlag(PadButtons.A)));
            if (Environment.GetEnvironmentVariable("TASSTUDIO_TEST_CAPTURE_DIR") is { Length: > 0 } output)
            {
                Directory.CreateDirectory(output); main.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                main.CaptureRenderedFrame()!.Save(Path.Combine(output, "poll-timeline.png"));
            }
        }
        finally { main.CloseAfterCapture(); }
    }
}
