using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace TasStudio.App;

public sealed partial class MainWindow
{
    private readonly HashSet<Window> _workspaceWindows = [];
    private WindowState _gameWindowRestoreState;
    private bool _modalOpen;
    private bool _dialogOpen
    {
        get => _modalOpen;
        set { _modalOpen = value; if (value) StopHeldAdvance(); UpdateInputAcceptance(); }
    }

    private Window? GameHostWindow => _execution.IsManualPlay ? this : TopLevel.GetTopLevel(_dockFactory.Panels["game"].View!) as Window;
    private static bool IsEditingText(Window window)
    {
        var focused = window.FocusManager?.GetFocusedElement();
        if (focused is not Visual visual || TopLevel.GetTopLevel(visual) != window) return false;
        return focused is TextBox or NumericUpDown or ComboBox or ListBox or ListBoxItem ||
            visual.GetVisualAncestors().Any(parent => parent is TextBox or NumericUpDown or ComboBox or ListBox or ListBoxItem);
    }
    private void UpdateInputAcceptance()
    {
        var active = _workspaceWindows.FirstOrDefault(window => window.IsActive);
        var keyboard = active != null && !IsEditingText(active);
        _input.SetAcceptInput(!_homeVisible && !_dialogOpen && !_closingApproved && active != null &&
            (keyboard || UsesControllerInput || _execution.IsManualPlay), acceptKeyboard: keyboard);
    }

    private bool TryMoveTimelineCursor(Window window, KeyEventArgs e)
    {
        if (!_execution.HasProject || e.KeyModifiers != KeyModifiers.None || e.Key is not (Key.Left or Key.Right)) return false;
        var focused = window.FocusManager?.GetFocusedElement();
        if (IsEditingText(window)) return false;
        if (focused is Visual visual && visual.GetVisualAncestors().Prepend(visual)
            .Any(control => control is Slider or MenuItem or TabItem or Avalonia.Controls.Primitives.ScrollBar)) return false;
        // Reserve mapped arrows only while authoring live controller input.
        // Historical edits still navigate, and timeline focus always navigates.
        if (UsesControllerInput && focused is not TimelineView) return false;
        e.Handled = true;
        if (!_busy) _timeline.MoveCursor(e.Key == Key.Left ? -1 : 1);
        return true;
    }

    private void AttachInputEvents(Window window)
    {
        if (!_workspaceWindows.Add(window)) return;
        window.AddHandler(KeyDownEvent, HandleKeyDown, RoutingStrategies.Tunnel);
        window.AddHandler(KeyUpEvent, (_, e) =>
        {
            if (e.Key is Key.F11 or Key.LeftShift or Key.RightShift) StopHeldAdvance();
            if (e.Key == Key.F11) _suppressF11UntilRelease = false;
            _input.KeyUp(e.Key);
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        window.AddHandler(GotFocusEvent, (_, _) => UpdateInputAcceptance(), RoutingStrategies.Bubble);
        window.AddHandler(LostFocusEvent, (_, _) => UpdateInputAcceptance(), RoutingStrategies.Bubble);
        // Activated is raised before IsActive becomes true; observe the settled property instead.
        window.PropertyChanged += (_, change) => { if (change.Property == IsActiveProperty) UpdateInputAcceptance(); };
        window.Deactivated += (_, _) => { StopHeldAdvance(); _suppressF11UntilRelease = false; UpdateInputAcceptance(); };
        window.Closed += (_, _) => { StopHeldAdvance(); _workspaceWindows.Remove(window); UpdateInputAcceptance(); };
    }

    private void ToggleGameWindow()
    {
        if (_execution.IsManualPlay) return;
        if (_dialogOpen || _closingApproved) return;
        ShowWorkspacePanel("game");
        var panel = _dockFactory.Panels["game"];
        if (_dockFactory.FindRoot(panel) != _dockRoot)
        {
            if (GameHostWindow is { } host) host.WindowState = WindowState.Normal;
            var target = WorkspaceFactory.Walk(_dockRoot).OfType<Dock.Model.Controls.IToolDock>()
                .FirstOrDefault(d => _dockFactory.FindRoot(d) == _dockRoot);
            if (target is not null && panel.Owner is Dock.Model.Core.IDock source)
                _dockFactory.MoveDockable(source, target, panel, null);
        }
        else _dockFactory.FloatDockable(panel);
    }
    private void ToggleGameFullscreen()
    {
        if (_dialogOpen || _closingApproved) return;
        if (_execution.IsManualPlay)
        {
            if (WindowState == WindowState.FullScreen) WindowState = _gameWindowRestoreState;
            else { _gameWindowRestoreState = WindowState; WindowState = WindowState.FullScreen; }
            return;
        }
        ShowWorkspacePanel("game");
        if (_dockFactory.FindRoot(_dockFactory.Panels["game"]) == _dockRoot)
            _dockFactory.FloatDockable(_dockFactory.Panels["game"]);
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (GameHostWindow is not { } game || game == this) return;
            if (game.WindowState == WindowState.FullScreen) game.WindowState = _gameWindowRestoreState;
            else { _gameWindowRestoreState = game.WindowState; game.WindowState = WindowState.FullScreen; }
            game.Activate();
        });
    }
}
