using Avalonia.Controls;
using Avalonia.Headless.XUnit;
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
            var loading = main.WithGameLoading(() => pending.Task);
            var dialog = Assert.Single(main.OwnedWindows);
            Assert.True(dialog.GetVisualDescendants().OfType<ProgressBar>().Single().IsIndeterminate);
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
