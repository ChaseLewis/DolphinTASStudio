using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using TasStudio.Core;

namespace TasStudio.App;

public sealed partial class MainWindow
{
    private ControllerState[]? _copiedInputs;
    private Button? _takeActiveButton;
    private bool CanPasteInput()
    {
        if (!_execution.HasProject || _copiedInputs is not { Length: > 0 } copy) return false;
        var start = _timeline.SelectedFrame;
        var take = _execution.Takes.FirstOrDefault(t => t.Id == _timeline.SelectedTake);
        return _timeline.SelectedTake == null
            ? start >= 0 && start <= _execution.Inputs.Count && (long)start + copy.Length <= Emulation.FolderProject.MaximumFrames
            : take != null && start >= take.Start && (long)start + copy.Length <= (long)take.Start + take.Inputs.Length;
    }
    private async Task CopySelectedInputs()
    {
        if (!CanClearSelectedInput()) return;
        var start = _timeline.SelectedFrame; var length = _timeline.SelectionEnd - start; var take = _timeline.SelectedTake;
        await FlushInputEditsAsync();
        _copiedInputs = await _execution.CopyInputRangeAsync(start, length, take);
        _status.Text = $"Copied {length:N0} input groups. Select a destination in any take or Active playback, then paste.";
    }
    private async Task PasteSelectedInputs()
    {
        if (!CanPasteInput()) return;
        var start = _timeline.SelectedFrame; var take = _timeline.SelectedTake; var copy = _copiedInputs!;
        await FlushInputEditsAsync();
        if (!await ConfirmInputOverwrite(start, take)) return;
        await _execution.PasteInputRangeAsync(start, copy, take);
        RefreshTimeline(); _timeline.SetSelection(start, start + copy.Length, take); LoadSelectedInput();
    }
    private async Task ToggleTakeActive(string? id)
    {
        var take = _execution.Takes.FirstOrDefault(t => t.Id == id);
        if (take == null) return;
        await FlushInputEditsAsync(); await _execution.SetTakeActiveAsync(id!, !take.IsActive);
    }
    private bool HasActiveTakes() => _execution.HasProject && _execution.Takes.Any(t => t.IsActive);
    private async Task ApplyActiveTakes()
    {
        if (!HasActiveTakes()) return;
        await FlushInputEditsAsync();
        var start = _execution.Takes.Where(t => t.IsActive).Min(t => t.Start);
        if (!await ConfirmInputOverwrite(start, null)) return;
        await _execution.ApplyActiveTakesAsync(); RefreshTimeline(); LoadSelectedInput();
    }
    private async Task TrialActiveTakes()
    {
        if (!HasActiveTakes()) return;
        await FlushInputEditsAsync();
        var start = _execution.Takes.Where(t => t.IsActive).Min(t => t.Start);
        _seeking = true; _cancelSeek.Content = "Stop trial"; _cancelSeek.IsVisible = true; _cancelSeek.IsEnabled = true;
        Refresh();
        try { await _execution.TrialActiveTakesAsync(start); }
        catch (OperationCanceledException) { _status.Text = "Trial stopped; Active playback preserved."; }
        finally { _seeking = false; _cancelSeek.Content = "Cancel seek"; _cancelSeek.IsVisible = false; _audio.Flush(); }
    }

    private async Task<bool> ConfirmInputOverwrite(int start, string? takeId, Window? owner = null)
    {
        var seconds = _execution.InputAgeSeconds(start);
        if (seconds <= 60) return true;
        await _execution.PauseAsync(); _audio.Flush();
        var destination = takeId == null ? "Active playback" : _execution.Takes.FirstOrDefault(t => t.Id == takeId)?.Name ?? "this take";
        var dialog = new Window { Name = "ConfirmInputOverwrite", Title = "Overwrite earlier inputs?", Width = 440,
            SizeToContent = SizeToContent.Height, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var cancel = new Button { Name = "CancelInputOverwrite", Content = "Cancel", IsCancel = true, IsDefault = true };
        var overwrite = new Button { Name = "AcceptInputOverwrite", Content = "Overwrite inputs" };
        cancel.Click += (_, _) => dialog.Close(false); overwrite.Click += (_, _) => dialog.Close(true);
        dialog.Content = new StackPanel { Margin = new Thickness(20), Spacing = 16, Children =
        {
            new TextBlock { Text = $"These inputs in {destination} are {TimeSpan.FromSeconds(seconds):m\\:ss} behind the game preview. Are you sure you want to overwrite them?\n\nYou can undo this change with Ctrl+Z.", TextWrapping = TextWrapping.Wrap },
            new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { cancel, overwrite } }
        } };
        var wasDialogOpen = _dialogOpen; _dialogOpen = true;
        try { return await dialog.ShowDialog<bool>(owner ?? TopLevel.GetTopLevel(_timeline) as Window ?? this); }
        finally { _dialogOpen = wasDialogOpen; }
    }
}
