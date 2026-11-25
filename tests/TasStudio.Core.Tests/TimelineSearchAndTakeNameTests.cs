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

public sealed class TimelineSearchAndTakeNameTests
{
    [AvaloniaFact]
    public async Task CompactTagSearchCyclesDuplicatesAndSelectsFutureTagsWithoutSeeking()
    {
        using var files = new TestWorkspace(); var backend = new FakeBackend();
        using var service = new ExecutionService(backend);
        await service.LoadGameAsync(files.GamePath, files.Options); await service.NewProjectAsync();
        for (var i = 0; i < 100; i++) await service.StepAsync();
        var first = await service.AddTagAsync(20, "Boss entry");
        var second = await service.AddTagAsync(95, "Boss entry");
        await service.AddTagAsync(40, "Town");
        await service.ClearLaterInputAsync(60); await service.SeekAsync(10);
        var main = new MainWindow(["--layout-file", files.FilePath("layout.json")], new AppSettings(), service);
        try
        {
            main.ShowEditor(); main.Show(); main.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            var timeline = main.GetVisualDescendants().OfType<TimelineView>().Single();
            timeline.VisibleFrames = 20; timeline.Update();
            var search = main.GetVisualDescendants().OfType<TextBox>().Single(c => c.Name == "TagSearch");
            var popup = ((Grid)search.Parent!).Children.OfType<Popup>().Single();
            var calls = backend.Calls.Count;
            search.Focus(); search.Text = "bOsS"; main.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            Assert.True(search.Bounds.Width <= 170); Assert.True(popup.IsOpen);
            void Enter(bool reverse = false) => search.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent,
                Key = Key.Enter, KeyModifiers = reverse ? KeyModifiers.Shift : KeyModifiers.None });
            Enter(); Assert.Equal(first, timeline.SelectedTag!.Id); Assert.Equal(20, timeline.SelectedFrame);
            Enter(); Assert.Equal(second, timeline.SelectedTag!.Id); Assert.Equal(95, timeline.SelectedFrame);
            Assert.True(timeline.FirstFrame > 0);
            Enter(); Assert.Equal(first, timeline.SelectedTag!.Id);
            Enter(true); Assert.Equal(second, timeline.SelectedTag!.Id);
            Assert.Equal(10UL, service.Position); Assert.Equal(calls, backend.Calls.Count);
            // The dropdown shows the name and frame, and clicking uses the exact tag identity.
            var result = popup.Child!.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Tag, first));
            result.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(first, timeline.SelectedTag!.Id); Assert.False(popup.IsOpen);
            search.Focus(); search.Text = "missing"; Dispatcher.UIThread.RunJobs();
            Assert.Empty(popup.Child!.GetVisualDescendants().OfType<Button>());
            Enter(); Assert.Equal(first, timeline.SelectedTag!.Id);
            search.Text = "boss"; Dispatcher.UIThread.RunJobs();
            await service.RenameTagAsync(first, "Harbor"); await service.RemoveTagAsync(second);
            await Until(() => !popup.Child!.GetVisualDescendants().OfType<Button>().Any());
            search.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
            Assert.True(string.IsNullOrEmpty(search.Text)); Assert.False(popup.IsOpen);
            search.Text = "Harbor"; main.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            if (Environment.GetEnvironmentVariable("TASSTUDIO_TEST_CAPTURE_DIR") is { Length: > 0 } output)
            {
                Directory.CreateDirectory(output); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                main.CaptureRenderedFrame()!.Save(Path.Combine(output, "timeline-search.png"));
            }
        }
        finally { main.CloseAfterCapture(); }
    }

    [Fact]
    public async Task RenameTakeIsUndoableAndPersistsWithoutChangingInputsOrStates()
    {
        using var files = new TestWorkspace(); using var service = new ExecutionService(new FakeBackend());
        await service.LoadGameAsync(files.GamePath, files.Options); await service.NewProjectAsync();
        for (var i = 0; i < 10; i++) await service.StepAsync();
        var id = await service.CaptureTakeAsync("Original", 2, 4);
        await service.SaveNamedStateAsync("Keep state");
        var original = Assert.Single(service.Takes); var markers = service.StateMarkers.ToArray();
        await service.RenameTakeAsync(id, "  Boss route  ");
        var renamed = Assert.Single(service.Takes);
        Assert.Equal("Boss route", renamed.Name); Assert.Equal(original.Inputs, renamed.Inputs);
        Assert.Equal(original.BaselineHash, renamed.BaselineHash); Assert.Equal(original.Start, renamed.Start);
        Assert.Equal(10UL, service.Position); Assert.Equal(markers, service.StateMarkers);
        await Assert.ThrowsAsync<ArgumentException>(() => service.RenameTakeAsync(id, "  "));
        await Assert.ThrowsAsync<ArgumentException>(() => service.RenameTakeAsync(id, "two\nlines"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RenameTakeAsync("removed", "Name"));
        await service.UndoAsync(); Assert.Equal("Original", Assert.Single(service.Takes).Name);
        await service.RedoAsync(); Assert.Equal("Boss route", Assert.Single(service.Takes).Name);
        var path = files.FilePath("takes.tasproj"); await service.SaveProjectAsync(path);
        Assert.Equal("Boss route", Assert.Single(FolderProject.Load(path).Takes).Name);
    }

    [AvaloniaFact]
    public async Task CreateTakePromptsAndF2AndContextMenuRenameTheCorrectTake()
    {
        using var files = new TestWorkspace(); using var service = new ExecutionService(new FakeBackend());
        await service.LoadGameAsync(files.GamePath, files.Options); await service.NewProjectAsync();
        for (var i = 0; i < 10; i++) await service.StepAsync();
        var main = new MainWindow(["--layout-file", files.FilePath("layout.json")], new AppSettings(), service);
        try
        {
            main.ShowEditor(); main.Show(); main.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            var timeline = main.GetVisualDescendants().OfType<TimelineView>().Single();
            timeline.SetSelection(1, 4);
            var create = main.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "+ Take"));
            create.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => main.OwnedWindows.Any(w => w.Title == "Name take"));
            main.OwnedWindows.Single(w => w.Title == "Name take").Close();
            await Until(() => create.IsEnabled); Assert.Empty(service.Takes);
            create.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await SaveName("Name take", "Boss route");
            await Until(() => service.Takes.Count == 1 && timeline.Takes.Count == 1);
            var first = Assert.Single(service.Takes); Assert.Equal("Boss route", first.Name);
            Assert.Equal(1, first.Start); Assert.Equal(3, first.Inputs.Length);
            timeline.SetSelection(1, 4, first.Id); timeline.Focus();
            timeline.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.F2 });
            await SaveName("Rename take", "Fast route");
            await Until(() => service.Takes[0].Name == "Fast route");
            var other = await service.CaptureTakeAsync("Other", 2, 4);
            await Until(() => timeline.Takes.Count == 2);
            main.UpdateLayout();
            var point = timeline.TranslatePoint(new Point(30, 64 + 2 * 38 + 15), main)!.Value;
            main.MouseDown(point, MouseButton.Right); main.MouseUp(point, MouseButton.Right);
            Assert.Equal(other, timeline.ContextTake); Assert.Equal(first.Id, timeline.SelectedTake);
            timeline.ContextMenu!.Items.OfType<MenuItem>().Single(m => m.Header?.ToString()?.StartsWith("Rename take") == true)
                .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await SaveName("Rename take", "Safer route");
            await Until(() => service.Takes.Single(t => t.Id == other).Name == "Safer route");
            Assert.Equal("Fast route", service.Takes.Single(t => t.Id == first.Id).Name);

            async Task SaveName(string title, string value)
            {
                await Until(() => main.OwnedWindows.Any(w => w.Title == title));
                var dialog = main.OwnedWindows.Single(w => w.Title == title);
                var input = dialog.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "TakeName");
                var save = dialog.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "SaveTakeName");
                input.Text = " "; await Until(() => !save.IsEnabled);
                input.Text = value; await Until(() => save.IsEnabled);
                save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Until(() => !main.OwnedWindows.Any(w => w.Title == title) && create.IsEnabled);
            }
        }
        finally { foreach (var dialog in main.OwnedWindows.ToArray()) dialog.Close(); main.CloseAfterCapture(); }
    }

    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) { Dispatcher.UIThread.RunJobs(); await Task.Delay(5, timeout.Token); }
    }
}
