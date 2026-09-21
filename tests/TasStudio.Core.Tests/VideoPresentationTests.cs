using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Dock.Avalonia.Controls;
using TasStudio.App;
using TasStudio.Emulation;
using Xunit;

namespace TasStudio.Core.Tests;

public sealed class VideoPresentationTests
{
    [AvaloniaFact]
    public async Task RenderTicksPresentLatestVideoIndependentlyOfInspectorTimerAndAcrossDocking()
    {
        using var files = new TestWorkspace(); var backend = new VideoBackend();
        using var service = new ExecutionService(backend);
        await service.LoadGameAsync(files.GamePath, files.Options); await service.NewProjectAsync();
        var main = new MainWindow(["--layout-file", files.FilePath("layout.json")], new AppSettings(), service);
        try
        {
            main.ShowEditor(); main.Show(); main.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            // A stopped inspector timer makes the old 33 ms presentation dependency fail deterministically.
            ((DispatcherTimer)typeof(MainWindow).GetField("_timer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!).Stop();
            var factory = (WorkspaceFactory)main.GetVisualDescendants().OfType<DockControl>().First().Factory!;
            var viewport = factory.Panels["game"].View!.GetVisualDescendants().OfType<Image>().Single();
            byte Red()
            {
                using var buffer = Assert.IsType<WriteableBitmap>(viewport.Source).Lock();
                return Marshal.ReadByte(buffer.Address);
            }
            async Task Render(byte expected)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                do
                {
                    AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs();
                    if (viewport.Source != null && Red() == expected) return;
                    await Task.Delay(5, timeout.Token);
                } while (true);
            }
            backend.Emit(new(1, 1, [10, 0, 0, 255], 1)); await Render(10);
            var bitmap = viewport.Source;
            backend.Emit(new(1, 1, [20, 0, 0, 255], 2));
            backend.Emit(new(1, 1, [30, 0, 0, 255], 3));
            await Render(30); Assert.Same(bitmap, viewport.Source);
            factory.FloatDockable(factory.Panels["game"]); Dispatcher.UIThread.RunJobs();
            backend.Emit(new(1, 1, [40, 0, 0, 255], 4)); await Render(40);
            main.Hide();
            backend.Emit(new(1, 1, [50, 0, 0, 255], 5)); await Render(50);
            main.Show();
        }
        finally { main.CloseAfterCapture(); }
        backend.Emit(new(1, 1, [60, 0, 0, 255], 6));
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs();
    }

    private sealed class VideoBackend : FakeBackend, IEmulatorBackend
    {
        public new event Action<VideoFrame>? VideoReady;
        public void Emit(VideoFrame frame) => VideoReady?.Invoke(frame);
    }
}
