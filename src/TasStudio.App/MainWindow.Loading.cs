using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace TasStudio.App;

public sealed partial class MainWindow
{
    internal sealed record LoadingProgress(string Detail, double? Fraction = null);

    internal Task WithGameLoading(Func<Task> operation, Window? owner = null,
        string title = "Loading game", string detail = "Initializing the emulator and compiling shaders…")
        => WithGameLoading(_ => operation(), owner, title, detail);

    internal async Task WithGameLoading(Func<IProgress<LoadingProgress>, Task> operation, Window? owner = null,
        string title = "Loading game", string detail = "Initializing the emulator and compiling shaders…")
    {
        var finished = false;
        var previousDialogOpen = _dialogOpen;
        var status = new TextBlock { Name = "GameLoadingDetail", Text = detail, TextWrapping = TextWrapping.Wrap };
        var bar = new ProgressBar { Name = "GameLoadingProgress", IsIndeterminate = true,
            Minimum = 0, Maximum = 100, Height = 5, HorizontalAlignment = HorizontalAlignment.Stretch };
        var progress = new Progress<LoadingProgress>(update =>
        {
            if (finished) return;
            status.Text = update.Detail;
            bar.IsIndeterminate = update.Fraction == null;
            if (update.Fraction is { } fraction) bar.Value = Math.Clamp(fraction, 0, 1) * 100;
        });
        var loading = new Window
        {
            Title = title, Width = 420, SizeToContent = SizeToContent.Height,
            CanResize = false, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(24), Spacing = 14,
                Children =
                {
                    new TextBlock { Text = title + "…", FontSize = 18, FontWeight = FontWeight.SemiBold },
                    status,
                    bar
                }
            }
        };
        loading.Closing += (_, e) => e.Cancel = !finished;
        _dialogOpen = true;
        var shown = loading.ShowDialog(owner ?? this);
        try { await operation(progress); }
        finally
        {
            finished = true;
            loading.Close();
            await shown;
            _dialogOpen = previousDialogOpen;
        }
    }
}
