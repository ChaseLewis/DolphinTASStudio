using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace TasStudio.App;

internal sealed class ExperimentPlayMismatchWindow : Window
{
    internal ExperimentPlayMismatchWindow(string playName, int start, int length, bool asTake = false)
    {
        Title = "Apply play with different history?";
        Width = 520; SizeToContent = SizeToContent.Height; CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var apply = new Button { Name = "ConfirmMismatchedPlay", Content = "Apply anyway" };
        var cancel = new Button { Name = "CancelMismatchedPlay", Content = "Cancel", IsDefault = true, IsCancel = true };
        apply.Click += (_, _) => Close(true);
        cancel.Click += (_, _) => Close(false);
        Content = new StackPanel { Margin = new Thickness(20), Spacing = 14, Children =
        {
            new TextBlock { Text = playName, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
            new TextBlock { Text = "This play was recorded with different starting history. This can include the initial savestate, earlier inputs, or settings. The result may differ even if you are at the same point in the movie.", TextWrapping = Avalonia.Media.TextWrapping.Wrap },
            new TextBlock { Text = asTake
                ? $"Add a candidate take for groups [{start}, {start + length}) using the current starting history? Active playback stays unchanged. You can audition or apply the take later, or Undo to remove it."
                : $"Apply its inputs to groups [{start}, {start + length})? Existing inputs in that range will be replaced; inputs beyond the end of the movie will be appended. Earlier inputs and project settings stay unchanged. You can Undo this edit.", TextWrapping = Avalonia.Media.TextWrapping.Wrap },
            new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 10, Children = { cancel, apply } }
        }};
    }
}
