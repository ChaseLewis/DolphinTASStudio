using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace TasStudio.App;

public sealed partial class MainWindow
{
    private Flyout? _memoryEditsFlyout;
    private bool CanClearMemoryEdits() => _execution.HasProject && !_execution.IsTakingOver &&
        !_execution.IsTrialPlayback && _execution.MemoryEdits.Count > 0;

    private void ShowMemoryEdits(ulong position, Point point)
    {
        _memoryEditsFlyout?.Hide();
        var snapshot = _execution.MemoryEditSnapshot;
        var revision = snapshot.Revision;
        var edits = snapshot.Edits.Where(e => e.Position == position).ToArray();
        if (edits.Length == 0) return;
        var flyout = new Flyout(); _memoryEditsFlyout = flyout;
        var rows = new StackPanel { Spacing = 6 };
        foreach (var edit in edits)
        {
            var remove = new Button { Name = "RemoveMemoryEdit", Content = "Remove", IsEnabled = CanClearMemoryEdits() };
            remove.Click += async (_, _) =>
            {
                flyout.Hide();
                await Perform(() => _execution.RemoveMemoryEditsAsync(revision, position, edit.EventIndex));
            };
            var row = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 12 };
            var bytes = new TextBlock
            {
                Text = $"0x{edit.Address:X8}   {TimelineView.PreviewBytes(edit.HexBytes)}",
                FontFamily = FontFamily.Parse("Consolas"), FontSize = 12,
                TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center
            };
            ToolTip.SetTip(bytes, $"{edit.HexBytes.Length / 2:N0} bytes\n{edit.HexBytes}");
            row.Children.Add(bytes); Grid.SetColumn(remove, 1); row.Children.Add(remove); rows.Children.Add(row);
        }
        var clearHere = new Button { Name = "ClearMemoryEditsHere", Content = $"Clear {edits.Length} edit{(edits.Length == 1 ? "" : "s")} here", IsEnabled = CanClearMemoryEdits() };
        clearHere.Click += async (_, _) =>
        {
            flyout.Hide(); await Perform(() => _execution.RemoveMemoryEditsAsync(revision, position));
        };
        var clearAll = new Button { Name = "ClearAllMemoryEdits", Content = "Clear all memory edits…", IsEnabled = CanClearMemoryEdits(), Margin = new Thickness(8, 0, 0, 0) };
        clearAll.Click += async (_, _) => { flyout.Hide(); await Perform(ClearAllMemoryEdits); };
        flyout.Content = new StackPanel { Width = 420, Spacing = 12, Children =
        {
            new TextBlock { Text = $"Memory edits · group {position:N0}", FontWeight = FontWeight.SemiBold },
            new TextBlock { Text = "Active playback · written bytes in replay order", FontSize = 12 },
            new ScrollViewer { Content = rows, MaxHeight = 260, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled },
            new WrapPanel { Orientation = Orientation.Horizontal, Children = { clearHere, clearAll } }
        } };
        ToolTip.SetIsOpen(_timeline, false);
        Avalonia.Controls.Primitives.FlyoutBase.SetAttachedFlyout(_timeline, flyout);
        flyout.ShowAt(_timeline, showAtPointer: true);
    }

    private Task ClearMemoryEditsHere() => _timeline.SelectedMemoryEditPosition is { } position
        ? _execution.RemoveMemoryEditsAsync(_execution.MemoryEditSnapshot.Revision, position) : Task.CompletedTask;

    private async Task ClearAllMemoryEdits()
    {
        if (!CanClearMemoryEdits()) return;
        await _execution.PauseAsync(); _audio.Flush();
        var snapshot = _execution.MemoryEditSnapshot;
        var revision = snapshot.Revision;
        var count = snapshot.Edits.Count;
        var dialog = new Window { Title = "Clear all memory edits?", Width = 440, SizeToContent = SizeToContent.Height,
            CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var cancel = new Button { Content = "Cancel", IsCancel = true, IsDefault = true };
        var clear = new Button { Name = "ConfirmClearMemoryEdits", Content = $"Clear {count} edit{(count == 1 ? "" : "s")}" };
        cancel.Click += (_, _) => dialog.Close(false); clear.Click += (_, _) => dialog.Close(true);
        dialog.Content = new StackPanel { Margin = new Thickness(20), Spacing = 16, Children =
        {
            new TextBlock { Text = $"Remove all {count} recorded memory edits from Active playback, including edits outside the visible timeline?\n\nThe game preview will be rebuilt. Ctrl+Z restores the edits.", TextWrapping = TextWrapping.Wrap },
            new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { cancel, clear } }
        } };
        var wasDialogOpen = _dialogOpen; _dialogOpen = true;
        bool confirmed;
        try { confirmed = await dialog.ShowDialog<bool>(TopLevel.GetTopLevel(_timeline) as Window ?? this); }
        finally { _dialogOpen = wasDialogOpen; }
        if (confirmed) await _execution.RemoveMemoryEditsAsync(revision);
    }
}
