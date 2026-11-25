using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
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

public sealed class MemoryEditTests
{
    private const uint Address = 0x80000020;

    [Fact]
    public async Task RemovalPreservesResetAndInputsReplaysAndSupportsUndoRedoAndReopen()
    {
        using var files = new TestWorkspace(); using var service = new ExecutionService(new FakeBackend());
        await service.LoadGameAsync(files.GamePath, files.Options); await service.NewProjectAsync();
        await service.ResetAsync();
        for (var i = 0; i < 3; i++) await service.StepAsync();
        var cleanHash = await service.HistoryAtAsync(3);
        var cleanBytes = await service.ReadMemoryAsync(Address, 4);
        var inputs = service.Inputs.ToArray();
        await service.SeekAsync(0);
        await service.WriteMemoryAsync(Address, BitConverter.GetBytes(111));
        await service.WriteMemoryAsync(Address, BitConverter.GetBytes(222));
        await service.SeekAsync(2);
        await service.WriteMemoryAsync(Address, BitConverter.GetBytes(333));
        await service.SeekAsync(3); await service.SaveNamedStateAsync("After writes");
        var editedBytes = await service.ReadMemoryAsync(Address, 4);
        var original = service.MemoryEdits;
        Assert.Equal(3, original.Count);
        Assert.Equal(1, original[0].EventIndex); // Reset remains the first event.
        await service.RemoveMemoryEditsAsync(service.Revision, 0, original[0].EventIndex);
        Assert.Equal(2, service.MemoryEdits.Count); Assert.Equal(3, original.Count);
        Assert.Equal(3UL, service.Position); Assert.True(service.IsPreviewCurrent);
        Assert.Empty(service.StateMarkers);
        await service.RemoveMemoryEditsAsync(service.Revision, 0);
        Assert.Equal(2UL, Assert.Single(service.MemoryEdits).Position);
        Assert.Equal(editedBytes, await service.ReadMemoryAsync(Address, 4));
        await service.RemoveMemoryEditsAsync(service.Revision);
        Assert.Empty(service.MemoryEdits); Assert.Equal(inputs, service.Inputs);
        Assert.Equal(cleanHash, await service.HistoryAtAsync(3));
        Assert.Equal(cleanBytes, await service.ReadMemoryAsync(Address, 4));
        await service.UndoAsync();
        Assert.Single(service.MemoryEdits); Assert.True(service.IsPreviewCurrent);
        Assert.Equal(editedBytes, await service.ReadMemoryAsync(Address, 4));
        var path = files.FilePath("edits.tasproj");
        await service.SaveProjectAsync(path);
        using (var reopened = new ExecutionService(new FakeBackend()))
        {
            await reopened.LoadProjectAsync(path, files.Options);
            Assert.Equal(service.MemoryEdits, reopened.MemoryEdits);
            await reopened.SeekAsync(3);
            Assert.Equal(editedBytes, await reopened.ReadMemoryAsync(Address, 4));
        }
        await service.RedoAsync();
        Assert.Empty(service.MemoryEdits); Assert.True(service.IsPreviewCurrent);
        Assert.Equal(cleanBytes, await service.ReadMemoryAsync(Address, 4));
        await service.SaveProjectAsync(path);
        using var cleared = new ExecutionService(new FakeBackend());
        await cleared.LoadProjectAsync(path, files.Options);
        Assert.Empty(cleared.MemoryEdits); Assert.Equal(cleanHash, await cleared.HistoryAtAsync(3));
    }

    [Fact]
    public async Task ClearIncludesFutureWritesRejectsStaleReceiptsAndNoOpDoesNotAddUndo()
    {
        using var files = new TestWorkspace(); using var service = new ExecutionService(new FakeBackend());
        await service.LoadGameAsync(files.GamePath, files.Options); await service.NewProjectAsync();
        await service.StepAsync(); await service.WriteMemoryAsync(Address, BitConverter.GetBytes(12));
        var stale = service.Revision;
        await service.WriteMemoryAsync(Address, BitConverter.GetBytes(24));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RemoveMemoryEditsAsync(stale));
        Assert.Equal(2, service.MemoryEdits.Count);
        await service.SeekAsync(0); await service.SaveNamedStateAsync("Before writes");
        await service.RemoveMemoryEditsAsync(service.Revision);
        Assert.Empty(service.MemoryEdits); Assert.Equal(0UL, service.Position);
        Assert.True(Assert.Single(service.StateMarkers).Valid);
        var revision = service.Revision;
        await service.RemoveMemoryEditsAsync(revision); Assert.Equal(revision, service.Revision);
        await service.UndoAsync(); Assert.Equal(2, service.MemoryEdits.Count);
    }

    [Fact]
    public async Task FailedReplayLeavesRemovalUndoableAndPreviewStale()
    {
        using var files = new TestWorkspace(); var backend = new FakeBackend(); using var service = new ExecutionService(backend);
        await service.LoadGameAsync(files.GamePath, files.Options); await service.NewProjectAsync();
        await service.WriteMemoryAsync(Address, BitConverter.GetBytes(123));
        backend.FailNextRestore = true;
        await Assert.ThrowsAsync<InvalidDataException>(() => service.RemoveMemoryEditsAsync(service.Revision));
        Assert.Empty(service.MemoryEdits); Assert.False(service.IsPreviewCurrent);
        await service.UndoAsync();
        Assert.Single(service.MemoryEdits); Assert.True(service.IsPreviewCurrent);
        Assert.Equal(BitConverter.GetBytes(123), await service.ReadMemoryAsync(Address, 4));
    }

    [AvaloniaFact]
    public void DiamondsAndOverlappingStatesAreIndependentlyClickable()
    {
        var timeline = new TimelineView
        {
            InputCount = 100, VisibleFrames = 100, Position = 70,
            Markers = [new StateMarker("state", "Saved", 40, false, true)],
            MemoryEdits = [new(0, 40, Address, "00000063"), new(1, 40, Address, "00000001"), new(2, 80, Address, "00000002")]
        };
        var window = new Window { Width = 1000, Height = 170, Content = timeline };
        ulong? requested = null; timeline.MemoryEditsRequested += (position, _) => requested = position;
        try
        {
            window.Show(); window.UpdateLayout(); timeline.Update(); window.UpdateLayout();
            void Click(int frame, double y, MouseButton button = MouseButton.Left)
            {
                var point = timeline.TranslatePoint(new Point(118 + frame * (timeline.Bounds.Width - 118) / 100, y), window)!.Value;
                window.MouseDown(point, button); window.MouseUp(point, button);
            }
            timeline.SetSelection(10, 12);
            Click(40, 24, MouseButton.Right);
            Assert.Equal(10, timeline.SelectedFrame); Assert.Equal(12, timeline.SelectionEnd);
            Assert.Equal(40UL, timeline.SelectedMemoryEditPosition); Assert.Null(timeline.SelectedMarker);
            Click(40, 24);
            Assert.Equal(40UL, requested); Assert.Equal(40, timeline.SelectedFrame); Assert.Equal(70UL, timeline.Position);
            Click(40, 9);
            Assert.Equal("state", timeline.SelectedMarker?.Id); Assert.Null(timeline.SelectedMemoryEditPosition);
            Click(80, 18); Assert.Equal(80UL, requested);
            if (Environment.GetEnvironmentVariable("TASSTUDIO_TEST_CAPTURE_DIR") is { Length: > 0 } output)
            {
                Directory.CreateDirectory(output); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                window.CaptureRenderedFrame()!.Save(Path.Combine(output, "memory-edit-markers.png"));
            }
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task DiamondPopoverRemovesOneWriteAndClearAllShowsCount()
    {
        using var files = new TestWorkspace(); using var service = new ExecutionService(new FakeBackend());
        await service.LoadGameAsync(files.GamePath, files.Options); await service.NewProjectAsync();
        for (var i = 0; i < 40; i++) await service.StepAsync();
        await service.WriteMemoryAsync(Address, BitConverter.GetBytes(1));
        await service.WriteMemoryAsync(Address, BitConverter.GetBytes(2));
        var main = new MainWindow(["--layout-file", files.FilePath("layout.json")], new AppSettings(), service);
        try
        {
            main.ShowEditor(); main.Show(); main.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            var timeline = main.GetVisualDescendants().OfType<TimelineView>().Single();
            timeline.VisibleFrames = 100; timeline.FirstFrame = 0; timeline.Update(); main.UpdateLayout();
            var point = timeline.TranslatePoint(new Point(118 + 40 * (timeline.Bounds.Width - 118) / 100, 18), main)!.Value;
            main.MouseDown(point, MouseButton.Left); main.MouseUp(point, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            var flyout = Assert.IsType<Flyout>(FlyoutBase.GetAttachedFlyout(timeline));
            Assert.True(flyout.IsOpen);
            main.UpdateLayout();
            var content = Assert.IsType<StackPanel>(flyout.Content);
            var presenter = content.GetVisualAncestors().OfType<FlyoutPresenter>().Single();
            var origin = content.TranslatePoint(default, presenter)!.Value;
            Assert.True(origin.X >= 0 && origin.X + content.Bounds.Width <= presenter.Bounds.Width);
            if (Environment.GetEnvironmentVariable("TASSTUDIO_TEST_CAPTURE_DIR") is { Length: > 0 } output)
            {
                Directory.CreateDirectory(output); main.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                main.CaptureRenderedFrame()!.Save(Path.Combine(output, "memory-edit-popover.png"));
                StudioTheme.Apply(UiTheme.Dark); main.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                main.CaptureRenderedFrame()!.Save(Path.Combine(output, "memory-edit-popover-dark.png"));
                StudioTheme.Apply(UiTheme.System);
            }
            var remove = Assert.IsType<StackPanel>(flyout.Content).GetVisualDescendants().OfType<Button>().First(b => b.Name == "RemoveMemoryEdit");
            remove.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => service.MemoryEdits.Count == 1 && timeline.MemoryEdits.Count == 1);
            Assert.True(service.IsPreviewCurrent);
            timeline.ContextMenu!.Open(timeline); Dispatcher.UIThread.RunJobs();
            var clear = timeline.ContextMenu.Items.OfType<MenuItem>().Single(m => Equals(m.Header, "Clear all memory edits…"));
            clear.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); timeline.ContextMenu.Close();
            await Until(() => main.OwnedWindows.Any(w => w.Title == "Clear all memory edits?"));
            var dialog = main.OwnedWindows.Single(w => w.Title == "Clear all memory edits?");
            var confirm = dialog.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "ConfirmClearMemoryEdits");
            Assert.Equal("Clear 1 edit", confirm.Content);
            confirm.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => service.MemoryEdits.Count == 0 && timeline.MemoryEdits.Count == 0);
            Assert.True(service.IsPreviewCurrent);
        }
        finally { main.CloseAfterCapture(); }
    }

    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); }
        Assert.True(condition());
    }
}
