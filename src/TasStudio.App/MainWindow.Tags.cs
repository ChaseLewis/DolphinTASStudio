using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using TasStudio.Emulation;

namespace TasStudio.App;

public sealed partial class MainWindow
{
    private async Task EditTimelineTag(TimelineTag? tag, ulong position)
    {
        if (!_execution.HasProject) return;
        var dialog = new Window
        {
            Title = tag == null ? "Add timeline tag" : "Rename timeline tag", Width = 330,
            SizeToContent = SizeToContent.Height, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var name = new TextBox { Name = "TagName", Text = tag?.Name ?? "", Watermark = "Short name", MaxLength = 40 };
        var save = new Button { Name = "SaveTag", Content = tag == null ? "Add" : "Rename", IsDefault = true };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        save.IsEnabled = !string.IsNullOrWhiteSpace(name.Text);
        name.TextChanged += (_, _) => save.IsEnabled = !string.IsNullOrWhiteSpace(name.Text);
        save.Click += (_, _) => dialog.Close(name.Text!.Trim());
        cancel.Click += (_, _) => dialog.Close();
        dialog.Content = new StackPanel { Margin = new Thickness(16), Spacing = 10, Children =
        {
            new TextBlock { Text = $"Frame group {position:N0}" }, name,
            new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { cancel, save } }
        } };
        dialog.Opened += (_, _) => { name.Focus(); name.SelectAll(); };
        _dialogOpen = true;
        string? result;
        try { result = await dialog.ShowDialog<string?>(this); }
        finally { _dialogOpen = false; }
        if (result == null) return;
        if (tag == null) await _execution.AddTagAsync(position, result);
        else await _execution.RenameTagAsync(tag.Id, result);
    }
}
