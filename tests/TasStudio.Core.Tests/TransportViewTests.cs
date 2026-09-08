using System.Diagnostics;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Dock.Avalonia.Controls;
using TasStudio.App;
using TasStudio.Emulation;
using Xunit;

namespace TasStudio.Core.Tests;

public sealed class TransportViewTests
{
    [AvaloniaFact]
    public async Task RightClickClearLaterInputTargetsClickedGroupWithoutChangingSelectionFirst()
    {
        using var files = new TestWorkspace(); using var service = new ExecutionService(new FakeBackend { RecordPolls = true });
        await service.LoadGameAsync(files.GamePath, files.Options); await service.NewProjectAsync();
        service.LiveInput = () => TasStudio.Core.ControllerState.Neutral with { Buttons = TasStudio.Core.PadButtons.A, StickX = 200 };
        for (var i = 0; i < 10; i++) await service.StepAsync();
        await service.SeekAsync(4);
        var main = new MainWindow(["--layout-file", files.FilePath("layout.json")], new AppSettings(), service);
        try
        {
            main.ShowEditor(); main.Show(); main.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            var timeline = main.GetVisualDescendants().OfType<TimelineView>().Single();
            timeline.VisibleFrames = 10; timeline.FirstFrame = 0; timeline.SetSelection(1, 3); timeline.Update(); main.UpdateLayout();
            var point = timeline.TranslatePoint(new Point(118 + 4.5 / 10 * (timeline.Bounds.Width - 118), 76), main)!.Value;
            main.MouseDown(point, MouseButton.Right); main.MouseUp(point, MouseButton.Right);
            Assert.Equal(1, timeline.SelectedFrame); Assert.Equal(3, timeline.SelectionEnd); Assert.Equal(4, timeline.ContextFrame);
            var clear = timeline.ContextMenu!.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Clear later input"));
            await Until(() => clear.IsEnabled);
            clear.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await Until(() => service.Inputs.Count == 5 && timeline.SelectedFrame == 4);
            Assert.Equal(4UL, service.Position); Assert.True(service.IsPreviewCurrent);
            Assert.Equal(TasStudio.Core.PadButtons.A, service.Inputs[4].Buttons); Assert.Equal(200, service.Inputs[4].StickX);
            Assert.Equal(4, (await service.GetPollFrameAsync(4))!.Frame.Polls.Length);
            Assert.Null(await service.GetPollFrameAsync(5));
            await Until(() => !clear.IsEnabled); // No later input remains after the selected group.
            await service.UndoAsync(); Assert.Equal(10, service.Inputs.Count);
        }
        finally { main.CloseAfterCapture(); }
    }

    [AvaloniaFact]
    public async Task DisablingTurboDoesNotHideTheFollowingGroupsRecordedInput()
    {
        using var files = new TestWorkspace();
        var backend = new FakeBackend { RecordPolls = true, FieldsPerStep = 2 };
        using var service = new ExecutionService(backend);
        await service.LoadGameAsync(files.GamePath, files.Options); await service.NewProjectAsync();
        for (var i = 0; i < 12; i++)
        {
            service.LiveInput = () => TasStudio.Core.ControllerState.Neutral with { Buttons = i % 2 == 0 ? TasStudio.Core.PadButtons.Start : TasStudio.Core.PadButtons.None };
            await service.StepAsync();
        }
        await service.SeekAsync(0);
        var main = new MainWindow(["--layout-file", files.FilePath("layout.json")], new AppSettings(), service);
        try
        {
            main.ShowEditor(); main.Show(); main.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            var start = main.GetVisualDescendants().OfType<CheckBox>().Single(c => Equals(c.Content, "Start"));
            var next = main.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "NextFrame");
            var timeline = main.GetVisualDescendants().OfType<TimelineView>().Single();
            var point = start.TranslatePoint(new Point(8, 8), main)!.Value;
            main.MouseDown(point, MouseButton.Right); main.MouseUp(point, MouseButton.Right);
            Assert.Contains("turbo", start.Classes);
            main.KeyPressQwerty(PhysicalKey.F11, RawInputModifiers.None);
            main.KeyReleaseQwerty(PhysicalKey.F11, RawInputModifiers.None);
            await Until(() => service.Position == 1 && next.IsEnabled);
            main.MouseDown(point, MouseButton.Right); main.MouseUp(point, MouseButton.Right);
            Assert.DoesNotContain("turbo", start.Classes);
            Assert.False(start.IsChecked);
            for (var i = 1; i < 5; i++)
            {
                main.KeyPressQwerty(PhysicalKey.F11, RawInputModifiers.None);
                main.KeyReleaseQwerty(PhysicalKey.F11, RawInputModifiers.None);
                await Until(() => service.Position == (ulong)i + 1 && next.IsEnabled && timeline.SelectedFrame == i + 1);
                Assert.Equal((i + 1) % 2 == 0, start.IsChecked);
                Assert.All((await service.GetPollFrameAsync(i))!.Frame.Polls, poll => Assert.Equal(i % 2 == 0, poll.Input.Buttons.HasFlag(TasStudio.Core.PadButtons.Start)));
            }
            // Future recording remains intact until stepped over. An explicit
            // selection still shows its recorded value, even at the preview.
            Assert.True(service.Inputs[6].Buttons.HasFlag(TasStudio.Core.PadButtons.Start));
            timeline.FirstFrame = 0; timeline.VisibleFrames = 12; timeline.Update(); main.UpdateLayout();
            var historical = timeline.TranslatePoint(new Point(118 + 6.5 / 12 * (timeline.Bounds.Width - 118), 76), main)!.Value;
            main.MouseDown(historical, MouseButton.Left); main.MouseUp(historical, MouseButton.Left);
            Assert.Equal(6, timeline.SelectedFrame); Assert.True(start.IsChecked);
            Assert.DoesNotContain("turbo", start.Classes);
            Assert.Equal(5UL, service.Position);
        }
        finally { main.CloseAfterCapture(); }
    }

    [AvaloniaFact]
    public async Task DisablingTurboAfterHeldAdvanceKeepsStartReleasedWhenHoldingAgain()
    {
        using var files = new TestWorkspace(); var backend = new FakeBackend(); using var service = new ExecutionService(backend);
        await service.LoadGameAsync(files.GamePath, files.Options); await service.NewProjectAsync();
        var main = new MainWindow(["--layout-file", files.FilePath("layout.json")], new AppSettings(), service);
        try
        {
            main.ShowEditor(); main.Show(); main.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            var start = main.GetVisualDescendants().OfType<CheckBox>().Single(c => Equals(c.Content, "Start"));
            var next = main.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "NextFrame");
            var point = start.TranslatePoint(new Point(8, 8), main)!.Value;
            main.MouseDown(point, MouseButton.Right); main.MouseUp(point, MouseButton.Right);
            Assert.True(start.IsChecked); // The held snapshot initially contains Start.
            backend.BeforeStep = () => Thread.Sleep(45);
            main.KeyPressQwerty(PhysicalKey.F11, RawInputModifiers.Shift);
            await Until(() => service.Position >= 2);
            main.KeyReleaseQwerty(PhysicalKey.F11, RawInputModifiers.Shift);
            await Until(() => next.IsEnabled);
            main.MouseDown(point, MouseButton.Right); main.MouseUp(point, MouseButton.Right);
            Assert.DoesNotContain("turbo", start.Classes);
            var disabledAt = service.Position;
            main.KeyPressQwerty(PhysicalKey.F11, RawInputModifiers.Shift);
            await Until(() => service.Position >= disabledAt + 4);
            Assert.False(start.IsChecked); Assert.DoesNotContain("turbo", start.Classes);
            main.KeyReleaseQwerty(PhysicalKey.F11, RawInputModifiers.Shift);
            await Until(() => next.IsEnabled);
            Assert.All(service.Inputs.Skip((int)disabledAt + 1), input => Assert.False(input.Buttons.HasFlag(TasStudio.Core.PadButtons.Start)));
            Assert.False(start.IsChecked);
        }
        finally { main.CloseAfterCapture(); }
    }

    [AvaloniaFact]
    public async Task HeldAdvanceKeepsCommandAvailabilityAndHeldAxesSteady()
    {
        using var files = new TestWorkspace(); var backend = new FakeBackend(); using var service = new ExecutionService(backend);
        await service.LoadGameAsync(files.GamePath, files.Options); await service.NewProjectAsync();
        for (var i = 0; i < 30; i++) await service.StepAsync();
        await service.SeekAsync(0);
        var main = new MainWindow(["--layout-file", files.FilePath("layout.json")], new AppSettings(), service);
        try
        {
            main.ShowEditor(); main.Show(); main.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            var axis = main.GetVisualDescendants().OfType<NumericUpDown>().Single(n => n.Name == "TasAxis0");
            var next = main.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "NextFrame");
            axis.Value = 200;
            await Until(() => service.Inputs[0].StickX == 200);
            backend.BeforeStep = () => Thread.Sleep(45); // Allow UI refreshes during each native step.
            var availability = new List<bool>(); var values = new List<decimal?>(); var holding = true;
            next.PropertyChanged += (_, change) => { if (change.Property == InputElement.IsEnabledProperty) availability.Add(next.IsEnabled); };
            axis.PropertyChanged += (_, change) => { if (holding && change.Property == NumericUpDown.ValueProperty) values.Add(axis.Value); };
            main.KeyPressQwerty(PhysicalKey.F11, RawInputModifiers.Shift);
            await Until(() => service.Position >= 5);
            Assert.Equal(new[] { false }, availability);
            Assert.Equal(new decimal?[] { 128m }, values); // One update to the next recorded value, without held-value flashes.
            holding = false;
            main.KeyReleaseQwerty(PhysicalKey.F11, RawInputModifiers.Shift);
            await Until(() => next.IsEnabled);
            Assert.Equal(new[] { false, true }, availability);
            var stopped = service.Position;
            await Task.Delay(100); Dispatcher.UIThread.RunJobs();
            Assert.Equal(stopped, service.Position);
            main.KeyPressQwerty(PhysicalKey.F11, RawInputModifiers.Shift);
            await Until(() => service.Position > stopped);
            main.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            await Until(() => next.IsEnabled);
            stopped = service.Position;
            await Task.Delay(100); Dispatcher.UIThread.RunJobs();
            Assert.Equal(stopped, service.Position);
        }
        finally { main.CloseAfterCapture(); }
    }

    [AvaloniaFact]
    public async Task WizardProjectEnablesNextFrameAndF11Immediately()
    {
        using var files = new TestWorkspace(); using var service = new ExecutionService(new FakeBackend());
        var path = files.FilePath("ready/ready.tasproj");
        await service.CreateProjectAsync(path, files.GamePath, files.Options);
        var main = new MainWindow(["--layout-file", files.FilePath("layout.json")], new AppSettings(), service);
        try
        {
            main.RestoreProjectTimelineView(); main.ShowEditor(); main.Show(); main.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            var next = main.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "NextFrame");
            var timeline = main.GetVisualDescendants().OfType<TimelineView>().Single();
            Assert.True(next.IsEnabled);
            timeline.Focus();
            main.KeyPressQwerty(PhysicalKey.F11, RawInputModifiers.None);
            main.KeyReleaseQwerty(PhysicalKey.F11, RawInputModifiers.None);
            await Until(() => service.Position == 1 && timeline.SelectedFrame == 1);
            Assert.True(service.IsPreviewCurrent);
        }
        finally { main.CloseAfterCapture(); }
    }

    [AvaloniaFact]
    public async Task ReopenedProjectEditsAndAdvancesRestoredFrameWithoutReplayingFromStart()
    {
        using var files = new TestWorkspace();
        var path = files.FilePath("resume.tasproj");
        using (var writer = new ExecutionService(new FakeBackend()))
        {
            await writer.LoadGameAsync(files.GamePath, files.Options); await writer.NewProjectAsync();
            for (var i = 0; i < 500; i++) await writer.StepAsync();
            await writer.SeekAsync(300); await writer.SaveProjectAsync(path);
        }
        var backend = new FakeBackend(); using var service = new ExecutionService(backend);
        await service.LoadProjectAsync(path, files.Options);
        var main = new MainWindow(["--layout-file", files.FilePath("layout.json")], new AppSettings(), service);
        try
        {
            main.RestoreProjectTimelineView(); main.ShowEditor(); main.Show(); main.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            var timeline = main.GetVisualDescendants().OfType<TimelineView>().Single();
            Assert.Equal(300, timeline.SelectedFrame); Assert.Equal(301, timeline.SelectionEnd);
            var a = main.GetVisualDescendants().OfType<CheckBox>().Single(c => Equals(c.Content, "A"));
            a.IsChecked = true;
            await Until(() => service.Inputs[300].Buttons.HasFlag(TasStudio.Core.PadButtons.A));
            Assert.True(service.IsPreviewCurrent);
            Assert.Equal(TasStudio.Core.ControllerState.Neutral, service.Inputs[0]);
            var steps = backend.SubmittedInputs.Count;
            var restores = backend.Calls.Count(c => c.Operation == "Restore");
            var next = main.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "NextFrame");
            next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => service.Position == 301 && timeline.SelectedFrame == 301);
            Assert.Equal(steps + 1, backend.SubmittedInputs.Count);
            Assert.Equal(restores, backend.Calls.Count(c => c.Operation == "Restore"));
            Assert.Equal(500, service.Inputs.Count);

            // Editing an earlier frame must require an explicit seek, including keyboard advance.
            await service.SetInputAsync(0, TasStudio.Core.ControllerState.Neutral with { Buttons = TasStudio.Core.PadButtons.B });
            await Until(() => !next.IsEnabled);
            steps = backend.SubmittedInputs.Count;
            main.KeyPressQwerty(PhysicalKey.F11, RawInputModifiers.None);
            await Task.Delay(100); Dispatcher.UIThread.RunJobs();
            Assert.Equal(301UL, service.Position); Assert.Equal(steps, backend.SubmittedInputs.Count);
            Assert.Equal(restores, backend.Calls.Count(c => c.Operation == "Restore"));
            Assert.False(service.IsRunning);
        }
        finally { main.CloseAfterCapture(); }
    }

    [AvaloniaFact]
    public async Task HeldAdvanceKeepsManualInputAndTurboPhaseAndStopsOnEitherKeyRelease()
    {
        using var files = new TestWorkspace(); using var service = new ExecutionService(new FakeBackend());
        await service.LoadGameAsync(files.GamePath, files.Options); await service.NewProjectAsync();
        for (var i = 0; i < 20; i++) await service.StepAsync();
        await service.SeekAsync(0);
        var main = new MainWindow(["--layout-file", files.FilePath("layout.json")], new AppSettings(), service);
        try
        {
            main.ShowEditor(); main.Show(); main.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            var axis = main.GetVisualDescendants().OfType<NumericUpDown>().Single(n => n.Name == "TasAxis0");
            axis.Value = 200;
            var a = main.GetVisualDescendants().OfType<CheckBox>().Single(b => Equals(b.Content, "A"));
            var originalButtonSize = a.Bounds.Size;
            var point = a.TranslatePoint(new Point(8, 8), main)!.Value;
            main.MouseDown(point, MouseButton.Right); main.MouseUp(point, MouseButton.Right);
            Assert.Equal("A", a.Content);
            Assert.Contains("turbo-down", a.Classes);
            main.UpdateLayout();
            Assert.Equal(originalButtonSize, a.Bounds.Size);
            if (Environment.GetEnvironmentVariable("TASSTUDIO_TEST_CAPTURE_DIR") is { Length: > 0 } capture)
            {
                Directory.CreateDirectory(capture); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                main.CaptureRenderedFrame()!.Save(Path.Combine(capture, "turbo-plus.png"));
            }
            var count = service.Inputs.Count;
            main.KeyPressQwerty(PhysicalKey.F11, RawInputModifiers.Shift);
            await Until(() => service.Position >= 4);
            main.KeyReleaseQwerty(PhysicalKey.ShiftLeft, RawInputModifiers.None);
            await Task.Delay(70); Dispatcher.UIThread.RunJobs();
            var stopped = service.Position;
            main.KeyPressQwerty(PhysicalKey.F11, RawInputModifiers.None); // F11 still held after Shift released.
            await Task.Delay(70); Dispatcher.UIThread.RunJobs();
            Assert.Equal(stopped, service.Position);
            for (var i = 0; i < (int)stopped; i++)
            {
                Assert.Equal(i == 0 ? 200 : 128, service.Inputs[i].StickX);
                Assert.Equal(i == 0, service.Inputs[i].Buttons.HasFlag(TasStudio.Core.PadButtons.A));
            }
            main.KeyReleaseQwerty(PhysicalKey.F11, RawInputModifiers.None);
            main.KeyPressQwerty(PhysicalKey.F11, RawInputModifiers.Shift);
            await Until(() => service.Position >= stopped + 2);
            main.KeyReleaseQwerty(PhysicalKey.F11, RawInputModifiers.Shift);
            await Task.Delay(70); Dispatcher.UIThread.RunJobs();
            stopped = service.Position;
            await Task.Delay(70); Dispatcher.UIThread.RunJobs();
            Assert.Equal(stopped, service.Position);
            Assert.Equal(count, service.Inputs.Count);
            Assert.Equal(service.Inputs[(int)service.Position].Buttons.HasFlag(TasStudio.Core.PadButtons.A), a.Classes.Contains("turbo-down"));
            if (Environment.GetEnvironmentVariable("TASSTUDIO_TEST_CAPTURE_DIR") is { Length: > 0 } captureMinus)
            {
                if ((service.Position & 1) == 0)
                {
                    var before = service.Position;
                    main.KeyPressQwerty(PhysicalKey.F11, RawInputModifiers.None);
                    await Until(() => service.Position == before + 1);
                }
                main.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                main.CaptureRenderedFrame()!.Save(Path.Combine(captureMinus, "turbo-minus.png"));
            }
            main.UpdateLayout();
            point = a.TranslatePoint(new Point(8, 8), main)!.Value;
            main.MouseDown(point, MouseButton.Right); main.MouseUp(point, MouseButton.Right);
            Assert.Equal("A", a.Content);
        }
        finally { main.CloseAfterCapture(); }
    }

    [AvaloniaFact]
    public async Task UseControllerPreviewsWithoutEditingAndRecordsOnlyOnNextFrame()
    {
        using var files = new TestWorkspace(); using var service = new ExecutionService(new FakeBackend());
        await service.LoadGameAsync(files.GamePath, files.Options); await service.NewProjectAsync();
        await service.StepAsync(); await service.StepAsync();
        var sample = new LiveInputSource.GamepadSample(true, new() { Buttons = GamepadButton.A, LeftX = short.MaxValue, RightTrigger = 255 });
        var physical = new LiveInputSource(_ => Volatile.Read(ref sample));
        var main = new MainWindow(["--layout-file", files.FilePath("layout.json")], new AppSettings { GamepadIndex = 0 }, service, physical);
        try
        {
            main.RestoreProjectTimelineView(); main.ShowEditor(); main.Show(); main.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            var use = main.GetVisualDescendants().OfType<CheckBox>().Single(b => b.Name == "UseController");
            var axis = main.GetVisualDescendants().OfType<NumericUpDown>().Single(n => n.Name == "TasAxis0");
            var checkA = main.GetVisualDescendants().OfType<CheckBox>().Single(b => Equals(b.Content, "A"));
            var timeline = main.GetVisualDescendants().OfType<TimelineView>().Single();
            Assert.NotEqual(true, use.IsChecked);
            var original = service.Inputs.ToArray(); var revision = service.Revision;
            use.IsChecked = true;
            await Until(() => axis.Value == 255 && checkA.IsChecked == true);
            main.Hide(); // Headless platform posts the normal deactivation callback.
            await Until(() => axis.Value == 128 && checkA.IsChecked == false);
            main.Show(); main.Activate();
            await Until(() => axis.Value == 255 && checkA.IsChecked == true);
            Assert.Equal(original, service.Inputs); Assert.Equal(revision, service.Revision);
            Assert.False(axis.IsEffectivelyEnabled);
            main.KeyPressQwerty(PhysicalKey.F11, RawInputModifiers.None);
            await Until(() => service.Position == 3 && timeline.SelectedFrame == 3);
            Assert.Equal(TasStudio.Core.PadButtons.A | TasStudio.Core.PadButtons.R, service.Inputs[2].Buttons);
            Assert.Equal(255, service.Inputs[2].StickX); Assert.Equal(255, service.Inputs[2].TriggerR);
            Assert.Equal(original[1], service.Inputs[1]);
            Volatile.Write(ref sample, new(false, default));
            await Until(() => checkA.IsChecked == false && axis.Value == 128);
            Assert.Equal(3, service.Inputs.Count); Assert.Equal(3UL, service.Position);
            use.IsChecked = false;
            Assert.True(axis.IsEffectivelyEnabled);
            axis.Value = 200;
            var next = main.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "NextFrame");
            next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => service.Position == 4);
            Assert.Equal(200, service.Inputs[3].StickX);
            Assert.Equal(TasStudio.Core.PadButtons.None, service.Inputs[3].Buttons);
        }
        finally { main.CloseAfterCapture(); }
    }

    [AvaloniaFact]
    public async Task F11WorksWithGameViewFocusedInMainAndFloatingWindows()
    {
        using var files = new TestWorkspace(); using var service = new ExecutionService(new FakeBackend());
        await service.LoadGameAsync(files.GamePath, files.Options); await service.NewProjectAsync();
        var main = new MainWindow(["--layout-file", files.FilePath("layout.json")], new AppSettings(), service);
        try
        {
            main.ShowEditor(); main.Show(); main.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            var factory = (WorkspaceFactory)main.GetVisualDescendants().OfType<DockControl>().First().Factory!;
            var game = factory.Panels["game"].View!;
            var point = game.TranslatePoint(new Point(game.Bounds.Width / 2, game.Bounds.Height / 2), main)!.Value;
            main.MouseDown(point, MouseButton.Left); main.MouseUp(point, MouseButton.Left);
            main.KeyPressQwerty(PhysicalKey.F11, RawInputModifiers.None);
            await Until(() => service.Position == 1);
            factory.FloatDockable(factory.Panels["game"]); Dispatcher.UIThread.RunJobs();
            var host = Assert.IsType<HostWindow>(TopLevel.GetTopLevel(game));
            host.UpdateLayout();
            point = game.TranslatePoint(new Point(game.Bounds.Width / 2, game.Bounds.Height / 2), host)!.Value;
            host.MouseDown(point, MouseButton.Left); host.MouseUp(point, MouseButton.Left);
            host.KeyPressQwerty(PhysicalKey.F11, RawInputModifiers.None);
            await Until(() => service.Position == 2);
            var input = factory.Panels["input"].View!;
            var check = input.GetVisualDescendants().OfType<CheckBox>().First(b => Equals(b.Content, "Start"));
            point = check.TranslatePoint(new Point(8, 8), main)!.Value;
            main.MouseDown(point, MouseButton.Left); main.MouseUp(point, MouseButton.Left);
            main.KeyPressQwerty(PhysicalKey.F11, RawInputModifiers.None);
            await Until(() => service.Position == 3);
            Assert.True(service.Inputs[2].Buttons.HasFlag(TasStudio.Core.PadButtons.Start));
        }
        finally { main.CloseAfterCapture(); }
    }

    [AvaloniaFact]
    public async Task TimelineScrollsFreelyWhilePausedAndFollowsNextFrame()
    {
        using var files = new TestWorkspace(); using var service = new ExecutionService(new FakeBackend());
        await service.LoadGameAsync(files.GamePath, files.Options); await service.NewProjectAsync();
        for (var i = 0; i < 500; i++) await service.StepAsync();
        var main = new MainWindow(["--layout-file", files.FilePath("layout.json")], new AppSettings(), service);
        try
        {
            main.ShowEditor(); main.Show(); main.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            var timeline = main.GetVisualDescendants().OfType<TimelineView>().Single();
            var scroll = main.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ScrollBar>().Single(s => s.Name == "TimelineScroll");
            await Until(() => timeline.Position == 500);
            timeline.VisibleFrames = 60; timeline.Update();
            timeline.SetSelection(15, 20);
            scroll.Value = 120;
            await Task.Delay(100); Dispatcher.UIThread.RunJobs();
            Assert.Equal(120, timeline.FirstFrame);
            Assert.Equal(500UL, service.Position);
            Assert.Equal(15, timeline.SelectedFrame); Assert.Equal(20, timeline.SelectionEnd);
            var wheelPoint = timeline.TranslatePoint(new Point(200, 70), main)!.Value;
            main.MouseWheel(wheelPoint, new Vector(-1, 0));
            Assert.Equal(132, timeline.FirstFrame);
            Assert.Equal(132, scroll.Value);
            var next = main.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "NextFrame");
            next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => timeline.Position == 501);
            Assert.InRange(501, timeline.FirstFrame + 1, timeline.FirstFrame + timeline.VisibleFrames - 1);
            Assert.Equal(timeline.FirstFrame, scroll.Value);
            Assert.Equal(15, timeline.SelectedFrame); Assert.Equal(20, timeline.SelectionEnd);
            scroll.Value = scroll.Maximum;
            timeline.Zoom(1.5);
            Assert.Equal(timeline.VisibleFrames, scroll.ViewportSize);
            Assert.InRange(scroll.Value, 0, scroll.Maximum);
            Assert.Equal(501UL, service.Position);
            if (Environment.GetEnvironmentVariable("TASSTUDIO_TEST_CAPTURE_DIR") is { Length: > 0 } output)
            {
                Directory.CreateDirectory(output); main.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                main.CaptureRenderedFrame()!.Save(Path.Combine(output, "timeline-scroll.png"));
            }
        }
        finally { main.CloseAfterCapture(); }
    }

    [AvaloniaFact]
    public async Task RightClickMarkerCanClearItWithoutMovingPreview()
    {
        using var files = new TestWorkspace(); using var service = new ExecutionService(new FakeBackend());
        await service.LoadGameAsync(files.GamePath, files.Options); await service.NewProjectAsync();
        await service.SaveNamedStateAsync("Start"); await service.StepAsync();
        var main = new MainWindow(["--layout-file", files.FilePath("layout.json")], new AppSettings(), service);
        try
        {
            main.ShowEditor(); main.Show(); main.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            var timeline = main.GetVisualDescendants().OfType<TimelineView>().Single();
            await Until(() => timeline.Markers.Count == 1);
            var point = timeline.TranslatePoint(new Point(119, 18), main)!.Value;
            main.MouseDown(point, MouseButton.Right); main.MouseUp(point, MouseButton.Right);
            Assert.Equal("Start", timeline.SelectedMarker?.Name);
            var clear = timeline.ContextMenu!.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Clear selected state"));
            await Until(() => clear.IsEnabled);
            clear.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await Until(() => service.StateMarkers.Count == 0 && timeline.SelectedMarker == null);
            Assert.Equal(1UL, service.Position); Assert.Single(service.Inputs);
        }
        finally { main.CloseAfterCapture(); }
    }

    [AvaloniaFact]
    public async Task PreviousStateButtonAndF11InNumericFieldOperateOnPreview()
    {
        using var files = new TestWorkspace(); using var service = new ExecutionService(new FakeBackend());
        await service.LoadGameAsync(files.GamePath, files.Options); await service.NewProjectAsync();
        await service.StepAsync(); await service.StepAsync(); await service.SaveNamedStateAsync("Two");
        for (var i = 0; i < 3; i++) await service.StepAsync();
        var main = new MainWindow(["--layout-file", files.FilePath("layout.json")], new AppSettings(), service) { Width = 1060, Height = 720 };
        try
        {
            main.ShowEditor(); main.Show(); main.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            var factory = (WorkspaceFactory)main.GetVisualDescendants().OfType<DockControl>().First().Factory!;
            var timeline = factory.Panels["timeline"].View!; var input = factory.Panels["input"].View!;
            var previous = timeline.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "PreviousState");
            previous.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var selection = timeline.GetVisualDescendants().OfType<TimelineView>().Single();
            await Until(() => service.Position == 2 && selection.SelectedFrame == 2);
            var axis = input.GetVisualDescendants().OfType<NumericUpDown>().Single(n => n.Name == "TasAxis0");
            axis.Value = 200;
            axis.GetVisualDescendants().OfType<TextBox>().First().Focus();
            main.KeyPressQwerty(PhysicalKey.F11, Avalonia.Input.RawInputModifiers.None);
            await Until(() => service.Position == 3 && selection.SelectedFrame == 3);
            Assert.Equal(200, service.Inputs[2].StickX); Assert.Equal(5, service.Inputs.Count);
            Assert.Equal(128, axis.Value); Assert.Equal(128, service.Inputs[3].StickX);
            Assert.Empty(timeline.GetVisualDescendants().OfType<NumericUpDown>());
            Assert.Single(input.GetVisualDescendants().OfType<Button>(), b => b.Name == "NextFrame");
            Assert.DoesNotContain(timeline.GetVisualDescendants().OfType<Button>(), b => AutomationProperties.GetName(b) == "Next Frame");
            main.UpdateLayout();
            foreach (var button in input.GetVisualDescendants().OfType<Button>().Where(b => b.Name == "NextFrame"))
                Assert.InRange(button.TranslatePoint(default, input)!.Value.Y + button.Bounds.Height, 1, input.Bounds.Height);
            foreach (var button in timeline.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible))
                Assert.InRange(button.TranslatePoint(default, timeline)!.Value.Y + button.Bounds.Height, 1, timeline.Bounds.Height + 1);
            if (Environment.GetEnvironmentVariable("TASSTUDIO_TEST_CAPTURE_DIR") is { Length: > 0 } output)
            {
                Directory.CreateDirectory(output); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                main.CaptureRenderedFrame()!.Save(Path.Combine(output, "transport-loaded.png"));
            }
            var seek = timeline.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "SeekSelection");
            var inputs = service.Inputs.ToArray();
            selection.SetSelection(1, 3);
            await Until(() => seek.IsEnabled);
            Assert.Equal(3UL, service.Position);
            seek.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => service.Position == 1);
            Assert.Equal(1, selection.SelectedFrame); Assert.Equal(3, selection.SelectionEnd);
            Assert.Equal(inputs, service.Inputs);
        }
        finally { main.CloseAfterCapture(); }
    }
    [AvaloniaFact]
    public async Task RightClickClearSelectedInputNeutralizesWholeRangeWithoutMovingOrTruncating()
    {
        using var files = new TestWorkspace(); using var service = new ExecutionService(new FakeBackend { RecordPolls = true });
        await service.LoadGameAsync(files.GamePath, files.Options); await service.NewProjectAsync();
        var pressed = TasStudio.Core.ControllerState.Neutral with { Buttons = TasStudio.Core.PadButtons.A, StickX = 200, CStickY = 17, TriggerR = 220 };
        service.LiveInput = () => pressed;
        for (var i = 0; i < 8; i++) await service.StepAsync();
        await service.SaveNamedStateAsync("End");
        var candidate = await service.CaptureTakeAsync("Alternative", 1, 3);
        var main = new MainWindow(["--layout-file", files.FilePath("layout.json")], new AppSettings(), service);
        try
        {
            main.ShowEditor(); main.Show(); main.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            var timeline = main.GetVisualDescendants().OfType<TimelineView>().Single();
            timeline.VisibleFrames = 10; timeline.FirstFrame = 0; timeline.SetSelection(1, 4); timeline.Update(); main.UpdateLayout();
            var point = timeline.TranslatePoint(new Point(118 + 6.5 / 10 * (timeline.Bounds.Width - 118), 76), main)!.Value;
            main.MouseDown(point, MouseButton.Right); main.MouseUp(point, MouseButton.Right);
            Assert.Equal(1, timeline.SelectedFrame); Assert.Equal(4, timeline.SelectionEnd);
            var clear = timeline.ContextMenu!.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Clear selected input"));
            await Until(() => clear.IsEnabled);
            clear.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await Until(() => service.Inputs[1] == TasStudio.Core.ControllerState.Neutral);
            Assert.Equal(8UL, service.Position); Assert.Equal(8, service.Inputs.Count);
            Assert.Equal(1, timeline.SelectedFrame); Assert.Equal(4, timeline.SelectionEnd);
            Assert.False(service.IsPreviewCurrent); Assert.Empty(service.StateMarkers);
            for (var i = 0; i < 8; i++)
            {
                var expected = i >= 1 && i < 4 ? TasStudio.Core.ControllerState.Neutral : pressed;
                Assert.Equal(expected, service.Inputs[i]);
                Assert.All((await service.GetPollFrameAsync(i))!.Frame.Polls, poll => Assert.Equal(expected, poll.Input));
            }
            Assert.All(Assert.Single(service.Takes).Inputs, input => Assert.Equal(pressed, input));
            await service.UndoAsync(); Assert.All(service.Inputs, input => Assert.Equal(pressed, input));
            timeline.ContextMenu.Close(); timeline.SetSelection(1, 4, candidate);
            await Until(() => clear.IsEnabled);
            clear.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await Until(() => service.Takes[0].Inputs.All(input => input == TasStudio.Core.ControllerState.Neutral));
            Assert.All(service.Inputs, input => Assert.Equal(pressed, input));
            Assert.Equal(candidate, timeline.SelectedTake); Assert.Equal(8UL, service.Position);
        }
        finally { main.CloseAfterCapture(); }
    }

    [AvaloniaFact]
    public async Task ApplyingTakeRemovesItSelectsActiveAndUndoesAsOneEdit()
    {
        using var files = new TestWorkspace(); using var service = new ExecutionService(new FakeBackend { RecordPolls = true });
        await service.LoadGameAsync(files.GamePath, files.Options); await service.NewProjectAsync();
        for (var i = 0; i < 8; i++) await service.StepAsync();
        var id = await service.CaptureTakeAsync("Apply me", 1, 4);
        var other = await service.CaptureTakeAsync("Keep me", 0, 2);
        var pressed = TasStudio.Core.ControllerState.Neutral with { Buttons = TasStudio.Core.PadButtons.A, StickX = 201 };
        await service.SetTakeInputAsync(id, 2, pressed);
        var main = new MainWindow(["--layout-file", files.FilePath("layout.json")], new AppSettings(), service);
        try
        {
            main.ShowEditor(); main.Show(); main.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            var timeline = main.GetVisualDescendants().OfType<TimelineView>().Single();
            timeline.SetSelection(2, 4, id);
            var apply = timeline.ContextMenu!.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Use selected section"));
            await Until(() => apply.IsEnabled);
            apply.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await Until(() => service.Takes.Count == 1 && timeline.SelectedTake == null);
            Assert.Equal(other, Assert.Single(service.Takes).Id);
            Assert.Equal(2, timeline.SelectedFrame); Assert.Equal(4, timeline.SelectionEnd);
            Assert.Equal(8UL, service.Position); Assert.Equal(8, service.Inputs.Count);
            Assert.Equal(pressed, service.Inputs[2]); Assert.False(service.IsPreviewCurrent);
            Assert.Equal("Apply me", Assert.Single(service.Sections).Name);
            await service.UndoAsync();
            Assert.Equal(2, service.Takes.Count);
            Assert.Equal(pressed, service.Takes.Single(t => t.Id == id).Inputs[1]);
            Assert.All(service.Inputs, input => Assert.Equal(TasStudio.Core.ControllerState.Neutral, input));
            await service.RedoAsync(); Assert.Equal(other, Assert.Single(service.Takes).Id);
            Assert.Equal(pressed, service.Inputs[2]);
            var path = files.FilePath("applied.tasproj"); await service.SaveProjectAsync(path);
            Assert.Equal(other, Assert.Single(FolderProject.Load(path).Takes).Id);
        }
        finally { main.CloseAfterCapture(); }
    }

    [AvaloniaFact]
    public async Task ApplyTakeAtCursorUsesCursorRatherThanRightClickOrOriginalTakePosition()
    {
        using var files = new TestWorkspace(); using var service = new ExecutionService(new FakeBackend { RecordPolls = true });
        await service.LoadGameAsync(files.GamePath, files.Options); await service.NewProjectAsync();
        for (var i = 0; i < 10; i++) await service.StepAsync();
        var id = await service.CaptureTakeAsync("Move here", 0, 2);
        var first = TasStudio.Core.ControllerState.Neutral with { Buttons = TasStudio.Core.PadButtons.A };
        var second = TasStudio.Core.ControllerState.Neutral with { Buttons = TasStudio.Core.PadButtons.B };
        await service.SetTakeInputAsync(id, 0, first); await service.SetTakeInputAsync(id, 1, second);
        var main = new MainWindow(["--layout-file", files.FilePath("layout.json")], new AppSettings(), service);
        try
        {
            main.ShowEditor(); main.Show(); main.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            var timeline = main.GetVisualDescendants().OfType<TimelineView>().Single();
            timeline.VisibleFrames = 10; timeline.FirstFrame = 0; timeline.SetSelection(3, 4); timeline.Update(); main.UpdateLayout();
            var point = timeline.TranslatePoint(new Point(118 + 6.5 / 10 * (timeline.Bounds.Width - 118), 76), main)!.Value;
            main.MouseDown(point, MouseButton.Right); main.MouseUp(point, MouseButton.Right);
            Assert.Equal(3, timeline.SelectedFrame);
            var parent = timeline.ContextMenu!.Items.OfType<MenuItem>().Single(item => item.Name == "ApplyTakeAtCursor");
            await Until(() => parent.IsEnabled && parent.Items.Count == 1);
            parent.Items.OfType<MenuItem>().Single().RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await Until(() => service.Takes.Count == 0 && service.Inputs[3] == first);
            Assert.Equal(first, service.Inputs[3]); Assert.Equal(second, service.Inputs[4]);
            Assert.Equal(TasStudio.Core.ControllerState.Neutral, service.Inputs[2]);
            Assert.Equal(TasStudio.Core.ControllerState.Neutral, service.Inputs[5]);
            Assert.Equal(3, timeline.SelectedFrame); Assert.Equal(4, timeline.SelectionEnd); Assert.Null(timeline.SelectedTake);
            Assert.Equal(10UL, service.Position); Assert.Equal(10, service.Inputs.Count);
            await Until(() => main.GetVisualDescendants().OfType<CheckBox>().Single(c => Equals(c.Content, "A")).IsChecked == true);
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
