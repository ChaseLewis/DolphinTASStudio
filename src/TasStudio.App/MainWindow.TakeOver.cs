using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;

namespace TasStudio.App;

public sealed partial class MainWindow
{
    private Control _takeOverToolbar = null!;
    private Button _takeOverPlay = null!;
    private readonly TextBlock _takeOverPlayLabel = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _takeOverStatus = new() { Name = "TakeOverStatus", VerticalAlignment = VerticalAlignment.Center };

    private Control BuildTakeOverToolbar()
    {
        var bar = new WrapPanel { Name = "TakeOverToolbar", Margin = new Thickness(10, 6), IsVisible = false };
        bar.Children.Add(_takeOverStatus);
        bar.Children.Add(ToolSeparator());
        _takeOverPlay = TransportButton("Pause", "play", TakeOverPlayPause, () => _execution.IsTakingOver);
        _takeOverPlay.Name = "TakeOverPlay";
        _takeOverPlay.Content = _takeOverPlayLabel;
        bar.Children.Add(_takeOverPlay);
        var fullscreen = TransportButton("Fullscreen", "window", () => { ToggleGameFullscreen(); return Task.CompletedTask; }, () => _execution.IsTakingOver);
        ToolTip.SetTip(fullscreen, "Game fullscreen (Alt+Enter)"); bar.Children.Add(fullscreen);
        var exit = TransportButton("Return to TAS", "seek", ExitMovieModeAsync, () => _execution.IsTakingOver);
        exit.Name = "ExitTakeOver";
        ToolTip.SetTip(exit, "Discard live play and return to the takeover point (Escape)"); bar.Children.Add(exit);
        bar.Children.Add(ToolSeparator()); bar.Children.Add(BuildAudioControls(compact: true));
        return bar;
    }

    internal async Task EnterTakeOverModeAsync(ulong? position = null)
    {
        if (!_execution.HasProject || _movieMode) return;
        StopHeldAdvance();
        await FlushInputEditsAsync(); await PausePlayback(); await SaveRecoveryNow();
        await _execution.BeginTakeOverAsync(position);
        try
        {
            EnterViewingLayout();
            _input.Clear();
            _takeOverPlay.Focus(); UpdateInputAcceptance();
            await _execution.RunAsync();
            Refresh();
        }
        catch
        {
            if (_movieMode) await ExitMovieModeAsync();
            else await _execution.EndTakeOverAsync();
            throw;
        }
    }

    private async Task TakeOverPlayPause()
    {
        if (!_execution.IsTakingOver) return;
        if (_execution.IsRunning) await PausePlayback();
        else await _execution.RunAsync();
        RefreshTakeOver();
    }

    private void RefreshTakeOver()
    {
        var label = _execution.IsRunning ? "Pause" : "Play";
        _takeOverPlayLabel.Text = label;
        ToolTip.SetTip(_takeOverPlay, label + " (Ctrl+P)");
        Avalonia.Automation.AutomationProperties.SetName(_takeOverPlay, label);
        _takeOverStatus.Text = $"Take Over · Not recording · Return to group {_execution.TakeOverPosition:N0}";
    }

    private async Task HandleTakeOverKey(KeyEventArgs e)
    {
        if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.Alt)
        { e.Handled = true; ToggleGameFullscreen(); return; }
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            if (GameHostWindow is { WindowState: WindowState.FullScreen } host) host.WindowState = _gameWindowRestoreState;
            await Perform(ExitMovieModeAsync); return;
        }
        if (e.Key == Key.P && e.KeyModifiers == KeyModifiers.Control)
        { e.Handled = true; await Perform(TakeOverPlayPause); return; }
        // Mapped keys (including arrows and Space) belong to the game, never the editor.
        if (_settings.Keys.Values.Contains(e.Key) || e.Key is Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl)
        { e.Handled = true; _input.KeyDown(e.Key); }
    }
}
