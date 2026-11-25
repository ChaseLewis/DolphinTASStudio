using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace TasStudio.App;

public sealed partial class MainWindow
{
    private async Task CopyTake()
    {
        var start = _timeline.SelectedFrame;
        var length = _timeline.SelectionEnd - start;
        var take = _timeline.SelectedTake;
        await FlushInputEditsAsync();
        var name = await PromptTakeName("Name take", "Take " + (_execution.Takes.Count + 1), "Create");
        if (name != null) await _execution.CaptureTakeAsync(name, start, length, take);
    }

    private async Task RenameTake(string? id)
    {
        var take = _execution.Takes.FirstOrDefault(t => t.Id == id);
        if (take == null) return;
        var name = await PromptTakeName("Rename take", take.Name, "Rename");
        if (name != null) await _execution.RenameTakeAsync(take.Id, name);
    }

    private async Task<string?> PromptTakeName(string title, string initial, string action)
    {
        var dialog = new Window { Title = title, Width = 350, SizeToContent = SizeToContent.Height,
            CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var name = new TextBox { Name = "TakeName", Text = initial, Watermark = "Take name", MaxLength = 80 };
        var save = new Button { Name = "SaveTakeName", Content = action, IsDefault = true };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        void Validate() => save.IsEnabled = name.Text?.Trim() is { Length: > 0 and <= 80 } value && !value.Any(char.IsControl);
        name.TextChanged += (_, _) => Validate(); Validate();
        save.Click += (_, _) => dialog.Close(name.Text!.Trim());
        cancel.Click += (_, _) => dialog.Close();
        dialog.Content = new StackPanel { Margin = new Thickness(16), Spacing = 10, Children =
        {
            new TextBlock { Text = "Take name" }, name,
            new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
                Spacing = 8, Children = { cancel, save } }
        } };
        dialog.Opened += (_, _) => { name.Focus(); name.SelectAll(); };
        _dialogOpen = true;
        try { return await dialog.ShowDialog<string?>(TopLevel.GetTopLevel(_timeline) as Window ?? this); }
        finally { _dialogOpen = false; }
    }
}
