using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TasStudio.App;
using TasStudio.Emulation;
using Xunit;

namespace TasStudio.Core.Tests;

public sealed class LoadingViewTests
{
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartupIndicatorStaysOpenUntilCompletionAndClosesOnFailure(bool fail)
    {
        using var files = new TestWorkspace();
        using var service = new ExecutionService(new FakeBackend());
        var main = new MainWindow(["--layout-file", files.FilePath("layout.json")], new AppSettings(), service);
        main.Show();
        var pending = new TaskCompletionSource();
        try
        {
            IProgress<MainWindow.LoadingProgress>? progress = null;
            var loading = main.WithGameLoading(reporter => { progress = reporter; return pending.Task; });
            var dialog = Assert.Single(main.OwnedWindows);
            var bar = dialog.GetVisualDescendants().OfType<ProgressBar>().Single();
            Assert.True(bar.IsIndeterminate);
            progress!.Report(new("Baking inputs: 42 / 100 groups (42%)", 0.42));
            Dispatcher.UIThread.RunJobs();
            Assert.False(bar.IsIndeterminate);
            Assert.Equal(42, bar.Value);
            Assert.Contains(dialog.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "Baking inputs: 42 / 100 groups (42%)");
            progress.Report(new("Writing the Dolphin movie and playback files…"));
            Dispatcher.UIThread.RunJobs();
            Assert.True(bar.IsIndeterminate);
            dialog.Close();
            Assert.True(dialog.IsVisible);
            if (fail) pending.SetException(new InvalidOperationException("Boot failed"));
            else pending.SetResult();
            if (fail) await Assert.ThrowsAsync<InvalidOperationException>(() => loading);
            else await loading;
            Assert.Empty(main.OwnedWindows);
        }
        finally { pending.TrySetResult(); main.CloseAfterCapture(); }
    }
}
