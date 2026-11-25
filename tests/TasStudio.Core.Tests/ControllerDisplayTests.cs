using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using TasStudio.App;
using Xunit;

namespace TasStudio.Core.Tests;

public sealed class ControllerDisplayTests
{
    [AvaloniaFact]
    public void DockingFadeDoesNotExposeOverlappingControllerLayers()
    {
        var display = new ControllerDisplay { Width = 480, Height = 365 };
        // The fade originates above the controller in Dock's content presenter.
        var window = new Window { Width = 480, Height = 365, Background = Brushes.White,
            Content = new Border { Opacity = .5, Child = display } };
        RenderOptions.SetRequiresFullOpacityHandling(window, true);
        try
        {
            window.Show(); window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            using var frame = window.CaptureRenderedFrame()!;
            byte[] Pixel(int x, int y)
            {
                var point = display.TranslatePoint(new Point(x, y), window)!.Value;
                var bytes = new byte[4]; var buffer = Marshal.AllocHGlobal(4);
                try
                {
                    frame.CopyPixels(new PixelRect((int)point.X, (int)point.Y, 1, 1), buffer, 4, 4);
                    Marshal.Copy(buffer, bytes, 0, 4); return bytes;
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            // Same body color over a shoulder and over empty background.
            Assert.Equal(Pixel(240, 65), Pixel(100, 65));
            // Z crosses the shell/face boundary; its fill must be uniform on either side.
            Assert.Equal(Pixel(352, 64), Pixel(352, 71));
            if (Environment.GetEnvironmentVariable("TASSTUDIO_TEST_CAPTURE_DIR") is { Length: > 0 } output)
            {
                Directory.CreateDirectory(output); frame.Save(Path.Combine(output, "controller-fade.png"));
                ((Border)window.Content!).Opacity = 1;
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                using var solid = window.CaptureRenderedFrame()!;
                solid.Save(Path.Combine(output, "controller-solid.png"));
            }
        }
        finally { window.Close(); }
    }
}
