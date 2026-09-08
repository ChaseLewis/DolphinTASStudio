using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Dock.Avalonia.Controls;
using TasStudio.App;
using Xunit;

namespace TasStudio.Core.Tests;

public sealed class StickNormalizationViewTests
{
    [AvaloniaFact]
    public void RadiusAndAxisChangesNormalizeOnlyTheirOwnStickAndPreserveMixedAxes()
    {
        using var files = new TestWorkspace();
        var main = new MainWindow(["--layout-file", files.FilePath("layout.json")], new AppSettings());
        try
        {
            main.ShowEditor(); main.Show(); main.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            var dock = main.GetVisualDescendants().OfType<DockControl>().First();
            var input = ((WorkspaceFactory)dock.Factory!).Panels["input"].View!;
            NumericUpDown Number(string name) => input.GetVisualDescendants().OfType<NumericUpDown>().Single(c => c.Name == name);
            var x = Number("TasAxis2"); var y = Number("TasAxis3");
            x.Value = 255; y.Value = 255;
            var normalize = input.GetVisualDescendants().OfType<CheckBox>().Single(c => c.Name == "NormalizeStick1");
            var radius = Number("StickRadius1");
            normalize.IsChecked = true;
            Assert.True(radius.IsEnabled); Assert.Equal(218, x.Value); Assert.Equal(218, y.Value);
            radius.Value = .1m;
            Assert.Equal(137, x.Value); Assert.Equal(137, y.Value);
            x.Value = 128;
            Assert.Equal(128, x.Value); Assert.Equal(141, y.Value);
            Assert.Equal(128, Number("TasAxis0").Value); Assert.Equal(128, Number("TasAxis1").Value);
            normalize.IsChecked = false;
            Assert.False(radius.IsEnabled); x.Value = 17; Assert.Equal(17, x.Value); Assert.Equal(141, y.Value);
            // Mixed ranges are not resolved to an arbitrary direction by enabling the constraint.
            x.Value = null; normalize.IsChecked = true;
            Assert.Null(x.Value); Assert.Equal(141, y.Value);
            normalize.IsChecked = false; x.Value = 255; y.Value = 255; normalize.IsChecked = true;
            if (Environment.GetEnvironmentVariable("TASSTUDIO_TEST_CAPTURE_DIR") is { Length: > 0 } output)
            {
                Directory.CreateDirectory(output); main.UpdateLayout(); Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                main.CaptureRenderedFrame()!.Save(Path.Combine(output, "stick-normalization.png"));
            }
        }
        finally { main.CloseAfterCapture(); }
    }

    [AvaloniaFact]
    public void NormalizedPointerGesturePublishesExactBytesAndCommitsOnce()
    {
        var pad = new StickPad { NormalizedRadius = .1 };
        var window = new Window { Width = 240, Height = 240, Content = pad };
        var starts = 0; var commits = 0; (byte X, byte Y) last = (128, 128);
        pad.DragStarted += () => starts++;
        pad.PositionChanged += (x, y) => last = (x, y);
        pad.PositionCommitted += () => commits++;
        try
        {
            window.Show(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            var point = pad.TranslatePoint(new Avalonia.Point(pad.Bounds.Width - 10, pad.Bounds.Height / 2), window)!.Value;
            window.MouseDown(point, Avalonia.Input.MouseButton.Left);
            window.MouseUp(point, Avalonia.Input.MouseButton.Left);
            Assert.Equal(((byte)141, (byte)128), last);
            Assert.Equal(1, starts); Assert.Equal(1, commits); Assert.False(pad.IsDragging);
        }
        finally { window.Close(); }
    }
}
