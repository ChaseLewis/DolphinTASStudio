using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace TasStudio.App;

public sealed partial class MainWindow
{
    internal async Task WithGameLoading(Func<Task> operation, Window? owner = null)
    {
        var finished = false;
        var previousDialogOpen = _dialogOpen;
        var loading = new Window
        {
            Title = "Loading game", Width = 420, SizeToContent = SizeToContent.Height,
            CanResize = false, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(24), Spacing = 14,
                Children =
                {
                    new TextBlock { Text = "Loading game…", FontSize = 18, FontWeight = FontWeight.SemiBold },
                    new TextBlock { Text = "Initializing the emulator and compiling shaders…", TextWrapping = TextWrapping.Wrap },
                    new ProgressBar { Name = "GameLoadingProgress", IsIndeterminate = true, Height = 5, HorizontalAlignment = HorizontalAlignment.Stretch }
                }
            }
        };
        loading.Closing += (_, e) => e.Cancel = !finished;
        _dialogOpen = true;
        var shown = loading.ShowDialog(owner ?? this);
        try { await operation(); }
        finally
        {
            finished = true;
            loading.Close();
            await shown;
            _dialogOpen = previousDialogOpen;
        }
    }
}
