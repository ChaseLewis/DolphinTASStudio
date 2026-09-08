using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Dock.Avalonia.Controls;
using TasStudio.App;
using TasStudio.Core;
using TasStudio.Emulation;
using Xunit;

namespace TasStudio.Core.Tests;

public sealed class ThemeTests
{
    [Fact]
    public void OldSettingsDefaultToSystemAndThemeRoundTripsWithoutLosingOtherPreferences()
    {
        using var files = new TestWorkspace();
        var path = files.FilePath("settings.json");
        File.WriteAllText(path, "{\"Volume\":37,\"LastGame\":\"game.iso\"}");
        var settings = AppSettings.Load(path, out var warning);
        Assert.Null(warning); Assert.Equal(UiTheme.System, settings.Theme);
        settings.Theme = UiTheme.Dark; settings.Save(path);
        var loaded = AppSettings.Load(path, out warning);
        Assert.Null(warning); Assert.Equal(UiTheme.Dark, loaded.Theme);
        Assert.Equal(37, loaded.Volume); Assert.Equal("game.iso", loaded.LastGame);
        Assert.Contains("\"Theme\": \"Dark\"", File.ReadAllText(path));
    }

    [AvaloniaFact]
    public void InterfaceSettingWorksWithoutAProjectAndSaveFailurePreservesPreviousTheme()
    {
        var settings = new AppSettings();
        var saves = 0;
        StudioTheme.Apply(UiTheme.System);
        var page = MainWindow.BuildInterfaceSettings(settings, () => { if (++saves == 2) throw new IOException("Test write failure"); });
        var window = new Window { Content = page };
        try
        {
            window.Show(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            var combo = page.GetVisualDescendants().OfType<ComboBox>().Single();
            Assert.Equal(UiTheme.System, combo.SelectedItem);
            combo.SelectedItem = UiTheme.Dark;
            Assert.Equal(UiTheme.Dark, settings.Theme);
            Assert.Equal(ThemeVariant.Dark, Application.Current!.RequestedThemeVariant);
            combo.SelectedItem = UiTheme.Light;
            Assert.Equal(UiTheme.Dark, combo.SelectedItem);
            Assert.Equal(UiTheme.Dark, settings.Theme);
            Assert.Equal(ThemeVariant.Dark, Application.Current.RequestedThemeVariant);
            Assert.Contains(page.GetVisualDescendants().OfType<TextBlock>(), t => t.Text?.Contains("Test write failure") == true);
            combo.SelectedItem = UiTheme.System;
            Assert.Equal(ThemeVariant.Default, Application.Current.RequestedThemeVariant);
            Assert.Equal(UiTheme.System, settings.Theme);
        }
        finally { window.Close(); StudioTheme.Apply(UiTheme.System); }
    }

    [AvaloniaFact]
    public async Task ConfigurationExposesInterfaceWithoutAProject()
    {
        using var files = new TestWorkspace();
        var settings = new AppSettings { Theme = UiTheme.Light };
        var main = new MainWindow(["--layout-file", files.FilePath("layout.json")], settings);
        try
        {
            main.Show(); Render(main);
            var opening = main.ApplicationSettingsCore(null);
            for (var attempt = 0; attempt < 100 && !main.OwnedWindows.Any(); attempt++)
            { await Task.Delay(10); Dispatcher.UIThread.RunJobs(); }
            var dialog = Assert.Single(main.OwnedWindows);
            Render(dialog);
            var tabs = dialog.GetVisualDescendants().OfType<TabControl>().Single();
            Assert.True(tabs.IsEnabled);
            Assert.Equal("Interface", ((TabItem)tabs.SelectedItem!).Header);
            Assert.True(dialog.GetVisualDescendants().OfType<ComboBox>().Single(c => c.Name == "UiTheme").IsEffectivelyEnabled);
            Assert.All(tabs.Items.OfType<TabItem>().Where(t => !Equals(t.Header, "Interface")), t => Assert.False(t.IsEnabled));
            Assert.DoesNotContain(dialog.GetVisualDescendants().OfType<Button>(), b => b.IsVisible && Equals(b.Content, "Apply to project"));
            Capture(dialog, "configuration-light");
            // Match the simulated preference without writing the real user's settings.
            settings.Theme = UiTheme.Dark;
            dialog.GetVisualDescendants().OfType<ComboBox>().Single(c => c.Name == "UiTheme").SelectedItem = UiTheme.Dark;
            StudioTheme.Apply(UiTheme.Dark); Render(dialog); Capture(dialog, "configuration-dark");
            dialog.Close(false); await opening;
        }
        finally { foreach (var dialog in main.OwnedWindows.ToArray()) dialog.Close(false); main.CloseAfterCapture(); StudioTheme.Apply(UiTheme.System); }
    }

    [AvaloniaFact]
    public async Task SwitchingUpdatesExistingAndFloatingViewsAndKeepsTimelineDataColors()
    {
        using var files = new TestWorkspace();
        using var execution = new ExecutionService(new FakeBackend { RecordPolls = true, FieldsPerStep = 2 });
        await execution.LoadGameAsync(files.GamePath, files.Options); await execution.NewProjectAsync();
        for (var i = 0; i < 90; i++)
        {
            var input = i is >= 20 and < 35 or >= 60 and < 70 ? ControllerState.Neutral with { Buttons = PadButtons.A } : ControllerState.Neutral;
            execution.LiveInput = () => input; await execution.StepAsync();
        }
        await execution.SeekAsync(55);
        await execution.SaveNamedStateAsync("Before input");
        await execution.AddTagAsync(45, "Action select");
        var settings = new AppSettings { Theme = UiTheme.Light };
        var main = new MainWindow(["--layout-file", files.FilePath("layout.json")], settings, execution) { Width = 1360, Height = 920 };
        Window? dialog = null;
        try
        {
            main.ShowEditor(); main.Show(); Render(main);
            var timeline = main.GetVisualDescendants().OfType<TimelineView>().Single();
            timeline.VisibleFrames = 100; timeline.FirstFrame = 0; timeline.SetSelection(25, 26); timeline.Update();
            var factory = (WorkspaceFactory)main.GetVisualDescendants().OfType<DockControl>().First().Factory!;
            var originalInput = factory.Panels["input"].View!;
            foreach (var theme in new[] { UiTheme.Light, UiTheme.Dark })
            {
                StudioTheme.Apply(theme); Render(main);
                Assert.Equal(StudioTheme.ColorFor(theme, ThemeColor.Window), ((ISolidColorBrush)main.Background!).Color);
                Assert.Equal(StudioTheme.ColorFor(theme, ThemeColor.Text), ((ISolidColorBrush)main.Foreground!).Color);
                Assert.Same(originalInput, factory.Panels["input"].View);
                var title = main.GetVisualDescendants().OfType<TextBlock>().First(text => text.Text == "TAS Input · Port 1");
                var activeHeader = title.GetVisualAncestors().OfType<ToolChromeControl>().Single();
                activeHeader.IsActive = true; Render(main);
                Assert.Equal(StudioTheme.ColorFor(theme, ThemeColor.Primary), ((ISolidColorBrush)activeHeader.Background!).Color);
                var next = originalInput.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "NextFrame");
                Assert.Same(activeHeader.Background, next.Background);
                Capture(main, theme.ToString().ToLowerInvariant());
                Assert.Equal(Color.Parse("#294F69"), ((ISolidColorBrush)StudioTheme.Brush(ThemeColor.TimelineActive)).Color);
                Assert.Equal(Color.Parse("#65468C"), ((ISolidColorBrush)StudioTheme.Brush(ThemeColor.TimelineInput)).Color);
                Assert.Equal(Color.Parse("#5AC8FA"), ((ISolidColorBrush)StudioTheme.Brush(ThemeColor.TimelineCursor)).Color);
                Assert.Equal(Color.Parse("#E7AF61"), ((ISolidColorBrush)StudioTheme.Brush(ThemeColor.TimelinePreview)).Color);
            }
            factory.FloatDockable(factory.Panels["input"]); Dispatcher.UIThread.RunJobs();
            var host = Assert.IsType<HostWindow>(TopLevel.GetTopLevel(originalInput));
            var check = new CheckBox { Content = "Checked", IsChecked = true };
            dialog = new Window { Content = new StackPanel { Children = { MainWindow.BuildInterfaceSettings(settings, () => { }), check } }, Width = 600, Height = 280 };
            dialog.Show();
            StudioTheme.Apply(UiTheme.Light); Render(main); Render(host); Render(dialog);
            Assert.Equal(ThemeVariant.Light, host.ActualThemeVariant);
            Assert.Equal(ThemeVariant.Light, dialog.ActualThemeVariant);
            Assert.Equal(StudioTheme.ColorFor(UiTheme.Light, ThemeColor.Window), ((ISolidColorBrush)host.Background!).Color);
            Assert.Equal(55UL, execution.Position); Assert.Equal(90, execution.Inputs.Count);
            Assert.True(Assert.Single(execution.StateMarkers).Valid);
            var glyph = check.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>().Single(p => p.Name == "CheckGlyph");
            Assert.Equal(StudioTheme.ColorFor(UiTheme.Light, ThemeColor.PrimaryText), ((ISolidColorBrush)glyph.Fill!).Color);
            StudioTheme.Apply(UiTheme.Dark); Render(dialog);
            Assert.Equal(StudioTheme.ColorFor(UiTheme.Dark, ThemeColor.PrimaryText), ((ISolidColorBrush)glyph.Fill!).Color);
            Capture(dialog, "interface");
        }
        finally { dialog?.Close(); main.CloseAfterCapture(); StudioTheme.Apply(UiTheme.System); }
    }

    private static void Render(Window window)
    {
        window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
    }
    private static void Capture(Window window, string name)
    {
        if (Environment.GetEnvironmentVariable("TASSTUDIO_THEME_CAPTURE_DIR") is not { Length: > 0 } directory) return;
        Directory.CreateDirectory(directory);
        using var frame = window.CaptureRenderedFrame();
        frame!.Save(Path.Combine(directory, name + ".png"));
    }
}
