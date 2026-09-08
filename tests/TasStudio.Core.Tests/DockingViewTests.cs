using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Dock.Avalonia.Controls;
using Dock.Model.Controls;
using Dock.Model.Core;
using TasStudio.App;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(TasStudio.Core.Tests.DockingTestApp))]

namespace TasStudio.Core.Tests;

public static class DockingTestApp
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<StudioApp>()
        .UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

public sealed class DockingViewTests
{
    [AvaloniaFact]
    public async Task ProjectHomeHidesFloatingPanelsAndResumeRestoresThem()
    {
        using var files = new TestWorkspace();
        var main = new MainWindow(["--layout-file", files.FilePath("layout.json")], new AppSettings());
        try
        {
            main.ShowEditor(); main.Show(); main.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            var dock = main.GetVisualDescendants().OfType<DockControl>().First();
            var factory = (WorkspaceFactory)dock.Factory!; var panel = factory.Panels["input"];
            factory.FloatDockable(panel); Dispatcher.UIThread.RunJobs();
            var host = Assert.IsType<HostWindow>(TopLevel.GetTopLevel(panel.View!));
            await main.ShowProjects(); Dispatcher.UIThread.RunJobs();
            Assert.False(host.IsVisible);
            main.ShowEditor(); main.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            Assert.True(host.IsVisible); Assert.Same(host, TopLevel.GetTopLevel(panel.View!));
        }
        finally { main.CloseAfterCapture(); }
    }
    [AvaloniaFact]
    public void DefaultMinimumWindowShowsAllInputControlsAndCompactSpinners()
    {
        using var files = new TestWorkspace();
        var main = new MainWindow(["--layout-file", files.FilePath("layout.json")]) { Width = 1060, Height = 720 };
        try
        {
            main.ShowEditor(); main.Show(); main.UpdateLayout(); Dispatcher.UIThread.RunJobs(); main.UpdateLayout();
            var dock = main.GetVisualDescendants().OfType<DockControl>().First();
            var input = ((WorkspaceFactory)dock.Factory!).Panels["input"].View!;
            var use = input.GetVisualDescendants().OfType<CheckBox>().Single(c => c.Name == "UseController");
            var indicator = use.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "NormalRectangle");
            Assert.InRange(indicator.TranslatePoint(default, use)!.Value.Y, 0, use.Bounds.Height - indicator.Bounds.Height);
            Assert.DoesNotContain(input.GetVisualDescendants().OfType<ScrollViewer>(), s => s.Content is StackPanel);
            var axes = input.GetVisualDescendants().OfType<NumericUpDown>().Where(a => a.Name?.StartsWith("TasAxis") == true).ToArray();
            Assert.Equal(6, axes.Length);
            foreach (var axis in axes)
            {
                var point = axis.TranslatePoint(default, input)!.Value;
                Assert.InRange(point.Y + axis.Bounds.Height, 1, input.Bounds.Height);
                Assert.InRange(axis.Bounds.Height, 20, 28);
                Assert.Equal(Avalonia.Media.TextAlignment.Center, axis.TextAlignment);
                Assert.All(axis.GetVisualDescendants().OfType<TextBox>(), box => Assert.Equal(Avalonia.Media.TextAlignment.Center, box.TextAlignment));
                var spin = axis.GetVisualDescendants().OfType<Button>().First(b => b.Name == "PART_IncreaseButton");
                Assert.True(spin.Bounds.Width <= 22, string.Join(" > ", spin.GetVisualAncestors().Prepend(spin).Select(v => v.GetType().Name + ":" + (v as Control)?.Name)));
            }
            var pads = input.GetVisualDescendants().OfType<StickPad>().ToArray();
            Assert.Equal(2, pads.Length);
            Assert.InRange(pads[0].Bounds.Width, 130, 200);
            Assert.Equal(pads[0].Bounds.Width, pads[1].Bounds.Width, 1);
            Assert.Equal(pads[0].Bounds.Width, pads[0].Bounds.Height, 1);
            foreach (var radius in input.GetVisualDescendants().OfType<NumericUpDown>().Where(a => a.Name?.StartsWith("StickRadius") == true))
            {
                var point = radius.TranslatePoint(default, input)!.Value;
                Assert.InRange(point.Y + radius.Bounds.Height, 1, input.Bounds.Height);
                Assert.True(radius.Bounds.Width >= 46);
            }
            if (Environment.GetEnvironmentVariable("TASSTUDIO_TEST_CAPTURE_DIR") is { Length: > 0 } output)
            {
                Directory.CreateDirectory(output);
                main.CaptureRenderedFrame()!.Save(Path.Combine(output, "minimum-layout.png"));
            }
        }
        finally { main.CloseAfterCapture(); }
    }

    [AvaloniaFact]
    public void FloatingTabbingClosingAndReopeningRetainLiveControls()
    {
        using var files = new TestWorkspace();
        var main = new MainWindow(["--layout-file", files.FilePath("layout.json")]);
        try
        {
            main.ShowEditor(); main.Show(); main.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            var dock = main.GetVisualDescendants().OfType<DockControl>().First();
            var factory = (WorkspaceFactory)dock.Factory!;
            var panel = factory.Panels["input"];
            var view = panel.View!;
            var axes = view.GetVisualDescendants().OfType<NumericUpDown>().Where(a => a.Name?.StartsWith("TasAxis") == true).ToArray();
            axes[0].Value = 203;
            factory.FloatDockable(panel); Dispatcher.UIThread.RunJobs();
            Assert.NotSame(main, TopLevel.GetTopLevel(view));
            Assert.IsType<HostWindow>(TopLevel.GetTopLevel(view));
            Assert.Same(axes[0], view.GetVisualDescendants().OfType<NumericUpDown>().Where(a => a.Name?.StartsWith("TasAxis") == true).First());
            Assert.Equal(203, axes[0].Value);
            var gameGroup = (IDock)factory.Panels["game"].Owner!;
            factory.MoveDockable((IDock)panel.Owner!, gameGroup, panel, null);
            factory.SetActiveDockable(panel); Dispatcher.UIThread.RunJobs(); main.UpdateLayout();
            Assert.Same(main, TopLevel.GetTopLevel(view));
            Assert.Equal(203, axes[0].Value);
            factory.SetActiveDockable(factory.Panels["game"]); Dispatcher.UIThread.RunJobs();
            factory.SetActiveDockable(panel); Dispatcher.UIThread.RunJobs();
            Assert.Same(main, TopLevel.GetTopLevel(view));
            Assert.Equal(203, axes[0].Value);
            factory.CloseDockable(panel); Dispatcher.UIThread.RunJobs();
            Assert.DoesNotContain(panel, WorkspaceFactory.Walk(dock.Layout!));
            Assert.Equal(203, axes[0].Value);
            main.ShowWorkspacePanel("input"); Dispatcher.UIThread.RunJobs();
            Assert.IsType<HostWindow>(TopLevel.GetTopLevel(view));
            Assert.Equal(203, axes[0].Value);
            var host = (Window)TopLevel.GetTopLevel(view)!;
            host.Width = 600; host.Height = 720; host.UpdateLayout(); Dispatcher.UIThread.RunJobs(); host.UpdateLayout();
            Assert.All(view.GetVisualDescendants().OfType<StickPad>(), pad => Assert.InRange(pad.Bounds.Width, 260, 300));
            main.ResetDockWorkspace(); Dispatcher.UIThread.RunJobs(); main.UpdateLayout();
            Assert.Same(main, TopLevel.GetTopLevel(view));
            Assert.Equal(203, axes[0].Value);
            Assert.Empty(factory.HostWindows);
        }
        finally { main.CloseAfterCapture(); }
    }

    [AvaloniaFact]
    public void FloatingLayoutSurvivesMainWindowCloseAndRestart()
    {
        using var files = new TestWorkspace();
        var path = files.FilePath("layout.json");
        var main = new MainWindow(["--layout-file", path]);
        main.ShowEditor(); main.Show(); main.UpdateLayout(); Dispatcher.UIThread.RunJobs();
        var dock = main.GetVisualDescendants().OfType<DockControl>().First();
        var factory = (WorkspaceFactory)dock.Factory!;
        factory.FloatDockable(factory.Panels["input"]); Dispatcher.UIThread.RunJobs();
        main.CloseAfterCapture();
        var saved = WorkspaceLayout.Parse(File.ReadAllText(path));
        Assert.Single(saved.Windows);
        var restored = new MainWindow(["--layout-file", path]);
        try
        {
            restored.Show(); restored.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            restored.ShowEditor(); restored.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            var restoredDock = restored.GetVisualDescendants().OfType<DockControl>().First();
            var input = ((WorkspaceFactory)restoredDock.Factory!).Panels["input"].View!;
            Assert.IsType<HostWindow>(TopLevel.GetTopLevel(input));
            Assert.Equal(6, input.GetVisualDescendants().OfType<NumericUpDown>().Where(a => a.Name?.StartsWith("TasAxis") == true).Count());
        }
        finally { restored.CloseAfterCapture(); }
    }
}
