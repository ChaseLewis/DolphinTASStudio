using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Dock.Model.Core;
using TasStudio.Emulation;

namespace TasStudio.App;

public sealed partial class MainWindow
{
    private bool _movieMode, _refreshingMovieSeek, _movieSeekPending;
    private WorkspaceLayout? _editingLayout;
    private Control _movieToolbar = null!;
    private readonly ControllerDisplay _controllerDisplay = new() { Name = "ControllerDisplay" };
    private readonly TextBlock _controllerDetails = new() { Name = "ControllerDetails", Margin = new Thickness(10), TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private readonly Slider _movieSeek = new() { Name = "MovieSeek", Minimum = 0, TickFrequency = 1, IsSnapToTickEnabled = true, MinWidth = 180 };
    private readonly TextBlock _moviePosition = new() { Name = "MoviePosition", VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _moviePlayLabel = new() { Text = "Play", VerticalAlignment = VerticalAlignment.Center };
    private readonly PathIcon _moviePlayIcon = new() { Width = 17, Height = 17, Foreground = StudioTheme.Brush(ThemeColor.Icon) };
    private Button _moviePlayButton = null!;
    private readonly Button _movieCancelSeek = new() { IsVisible = false };
    private readonly ComboBox _movieResolution = new()
    {
        Name = "MovieRenderResolution", ItemsSource = new[] { "Project default", "1× native", "2× native", "3× native", "4× native" },
        SelectedIndex = 0, MinWidth = 145, VerticalAlignment = VerticalAlignment.Center
    };
    private bool _refreshingMovieResolution;
    private readonly List<Control> _movieDisabledMenus = [];
    private string MovieLayoutPath => Path.ChangeExtension(_layoutPath, "movie.json");
    internal bool IsMovieMode => _movieMode;

    private Control BuildControllerDisplay()
    {
        var panel = new Grid { Name = "ControllerDisplayPanel", RowDefinitions = new("*,Auto") };
        _controllerDisplay.Margin = new Thickness(10);
        panel.Children.Add(_controllerDisplay); Grid.SetRow(_controllerDetails, 1); panel.Children.Add(_controllerDetails);
        void Appearance()
        {
            panel.Background = _settings.ControllerDisplayBackground switch
            {
                "Black" => Brushes.Black, "White" => Brushes.White,
                "Chroma key" => Brushes.Magenta, _ => StudioTheme.Brush(ThemeColor.Panel)
            };
            _controllerDetails.Foreground = _settings.ControllerDisplayBackground == "Black" ? Brushes.White :
                _settings.ControllerDisplayBackground is "White" or "Chroma key" ? Brushes.Black : StudioTheme.Brush(ThemeColor.Text);
            _controllerDetails.IsVisible = !_settings.ControllerDisplayClean;
        }
        var clean = new MenuItem { Header = "Show input values" };
        void UpdateClean() { clean.Header = _settings.ControllerDisplayClean ? "Show input values" : "Hide input values"; Appearance(); }
        clean.Click += (_, _) => { _settings.ControllerDisplayClean = !_settings.ControllerDisplayClean; UpdateClean(); };
        var backgrounds = new[] { "Theme", "Black", "White", "Chroma key" }.Select(name =>
        {
            var item = new MenuItem { Header = name };
            item.Click += (_, _) => { _settings.ControllerDisplayBackground = name; Appearance(); };
            return item;
        }).ToArray();
        panel.ContextMenu = new ContextMenu { ItemsSource = new Control[] { clean, new MenuItem { Header = "Background", ItemsSource = backgrounds } } };
        UpdateClean();
        return panel;
    }

    private Control BuildMovieToolbar()
    {
        var bar = new StackPanel { Margin = new Thickness(10, 6), Spacing = 6, IsVisible = false };
        var buttons = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        buttons.Children.Add(TransportButton("Restart", "restart", () => SeekMovie(0), () => _movieMode));
        _moviePlayButton = TransportButton("Play", "play-only", MoviePlayPause, () => _movieMode && _execution.HasProject);
        _moviePlayIcon.Data = Geometry.Parse(ToolIcons.Paths["play-only"]);
        _moviePlayButton.Name = "MoviePlay"; _moviePlayButton.Content = Row(_moviePlayIcon, _moviePlayLabel);
        ToolTip.SetTip(_moviePlayButton, "Play (Space)"); buttons.Children.Add(_moviePlayButton);
        var step = TransportButton("Frame advance", "step", MovieStep, CanStepMovie);
        ToolTip.SetTip(step, "Play one recorded frame group (F12)");
        step.Name = "MovieStep"; buttons.Children.Add(step);
        StyleTransportButton(_movieCancelSeek, "Cancel seek", "stop");
        _movieCancelSeek.Click += async (_, _) => await _execution.PauseAsync(); buttons.Children.Add(_movieCancelSeek);
        buttons.Children.Add(ToolSeparator());
        Avalonia.Automation.AutomationProperties.SetName(_movieResolution, "Render resolution (experimental)");
        ToolTip.SetTip(_movieResolution, "Experimental: changes on the next frame. May affect playback in some games. Project settings and existing save states are preserved.");
        _movieResolution.SelectionChanged += async (_, _) =>
        {
            if (_refreshingMovieResolution || !_movieMode) return;
            var scale = _movieResolution.SelectedIndex;
            if (scale < 0) return;
            await Perform(() => _execution.SetRenderResolutionAsync(scale == 0 ? null : scale));
            RefreshMovieMode();
        };
        buttons.Children.Add(Row(Label("Render resolution (experimental)"), _movieResolution));
        buttons.Children.Add(ToolSeparator());
        buttons.Children.Add(TransportButton("Game", "window", () => { ShowWorkspacePanel("game"); return Task.CompletedTask; }, () => _movieMode));
        buttons.Children.Add(TransportButton("Controller Inputs", "pad", () => { ShowWorkspacePanel("controller"); return Task.CompletedTask; }, () => _movieMode));
        var fullscreen = TransportButton("Fullscreen", "window", () => { ToggleGameFullscreen(); return Task.CompletedTask; }, () => _movieMode);
        ToolTip.SetTip(fullscreen, "Game fullscreen (Alt+Enter)"); buttons.Children.Add(fullscreen);
        buttons.Children.Add(ToolSeparator());
        var exit = TransportButton("Return to editor", "watch", ExitMovieModeAsync, () => _movieMode);
        exit.Name = "ExitMovieMode"; ToolTip.SetTip(exit, "Return to editor (Escape)"); buttons.Children.Add(exit);
        var toolbarRow = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 10 };
        toolbarRow.Children.Add(buttons);
        var audio = Row(ToolSeparator(), BuildAudioControls(compact: true));
        audio.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetColumn(audio, 1); toolbarRow.Children.Add(audio);
        bar.Children.Add(toolbarRow);
        var seekRow = new Grid { ColumnDefinitions = new("*,Auto,Auto"), ColumnSpacing = 10 };
        _movieSeek.ValueChanged += (_, _) => { if (!_refreshingMovieSeek) _movieSeekPending = true; };
        seekRow.Children.Add(_movieSeek);
        var seek = TransportButton("Seek", "seek", () => SeekMovie((ulong)_movieSeek.Value), () => _movieMode && _movieSeekPending);
        seek.Name = "MovieSeekButton"; Grid.SetColumn(seek, 1); seekRow.Children.Add(seek);
        Grid.SetColumn(_moviePosition, 2); seekRow.Children.Add(_moviePosition); bar.Children.Add(seekRow);
        return bar;
    }

    internal async Task EnterMovieModeAsync(bool fromStart = true)
    {
        if (!_execution.HasProject || _movieMode) return;
        StopHeldAdvance();
        await FlushInputEditsAsync(); await PausePlayback();
        EnterViewingLayout();
        if (await SeekMovie(fromStart ? 0 : _execution.Position)) await _execution.PlayRecordedAsync();
        RefreshMovieMode();
    }

    private void EnterViewingLayout()
    {
        ShowEditor();
        SaveDockWorkspace(); _editingLayout = WorkspaceFactory.Capture(_dockRoot);
        var layout = _dockFactory.CreateMovieLayout();
        try { if (File.Exists(MovieLayoutPath)) layout = WorkspaceLayout.Parse(File.ReadAllText(MovieLayoutPath)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException)
        { _status.Text = "Movie layout could not be restored; using the default layout."; }
        _movieMode = true;
        ReplaceDockWorkspace(layout);
        SetMovieEditingAvailability();
        ShowEditor();
    }

    internal async Task ExitMovieModeAsync()
    {
        if (!_movieMode) return;
        await PausePlayback();
        await _execution.EndTakeOverAsync();
        _input.Clear();
        await _execution.SetRenderResolutionAsync(null);
        SaveDockWorkspace();
        _movieMode = false;
        ReplaceDockWorkspace(_editingLayout!);
        _editingLayout = null;
        // Playback moved the preview, but returning to authoring must not scroll its saved viewport.
        _timeline.Position = _execution.Position;
        SetMovieEditingAvailability(); ShowEditor(); Refresh();
    }

    private void SetMovieEditingAvailability()
    {
        foreach (var id in new[] { "timeline", "input", "watcher" }) _dockFactory.Panels[id].View!.IsEnabled = !_movieMode;
        foreach (var menu in _movieDisabledMenus) menu.IsEnabled = !_movieMode;
        UpdateInputAcceptance();
    }

    private async Task<bool> SeekMovie(ulong position)
    {
        _movieSeekPending = false;
        _seeking = true; _movieCancelSeek.IsVisible = true;
        try
        {
            await PausePlayback();
            await _execution.SeekAsync(position);
            return true;
        }
        catch (OperationCanceledException) { _status.Text = $"Seek canceled at group {_execution.Position:N0}."; return false; }
        finally { _seeking = false; _movieCancelSeek.IsVisible = false; _audio.Flush(); RefreshMovieMode(); }
    }

    private async Task MoviePlayPause()
    {
        if (_execution.IsRunning) { await PausePlayback(); return; }
        if (_execution.Position >= (ulong)_execution.Inputs.Count && !await SeekMovie(0)) return;
        await _execution.PlayRecordedAsync();
    }
    private bool CanStepMovie() => _movieMode && !_execution.IsRunning && !_seeking &&
        _execution.Position < (ulong)_execution.Inputs.Count;

    private async Task MovieStep()
    {
        if (!CanStepMovie()) return;
        await PausePlayback(); await _execution.StepRecordedFrameAsync();
    }
    private void RefreshMovieMode()
    {
        if (!_movieMode) return;
        if (_execution.IsTakingOver) { RefreshTakeOver(); return; }
        _refreshingMovieResolution = true;
        _movieResolution.SelectedIndex = _execution.RenderResolutionOverride ?? 0;
        _refreshingMovieResolution = false;
        _movieResolution.IsEnabled = !_busy && !_seeking && _execution.SupportsRenderResolution;
        var playing = _execution.IsRunning;
        var playLabel = playing ? "Pause" : "Play";
        if (_moviePlayLabel.Text != playLabel)
        {
            _moviePlayLabel.Text = playLabel;
            _moviePlayIcon.Data = Geometry.Parse(ToolIcons.Paths[playing ? "pause" : "play-only"]);
            ToolTip.SetTip(_moviePlayButton, playLabel + " (Space)");
            Avalonia.Automation.AutomationProperties.SetName(_moviePlayButton, playLabel);
        }
        _refreshingMovieSeek = true;
        _movieSeek.Maximum = _execution.Inputs.Count;
        if (!_movieSeekPending) _movieSeek.Value = _execution.Position;
        _refreshingMovieSeek = false;
        _movieSeek.IsEnabled = !_seeking && !_busy;
        _moviePosition.Text = _seeking ? $"Seeking… group {_execution.Position:N0}" :
            _movieSeekPending ? $"Seek to group {_movieSeek.Value:N0}" :
            $"{(_execution.Position == (ulong)_execution.Inputs.Count ? "Finished · " : "")}Group {_execution.Position:N0} / {_execution.Inputs.Count:N0}";
    }
    private async Task HandleMovieKey(KeyEventArgs e)
    {
        if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.Alt) { e.Handled = true; ToggleGameFullscreen(); return; }
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            if (_seeking) { await _execution.PauseAsync(); return; }
            if (GameHostWindow is { WindowState: WindowState.FullScreen } host) { host.WindowState = _gameWindowRestoreState; return; }
            await Perform(ExitMovieModeAsync); return;
        }
        if (e.Key == Key.Space || (e.Key == Key.P && e.KeyModifiers == KeyModifiers.Control))
        { e.Handled = true; await Perform(MoviePlayPause); }
        else if (e.Key == Key.F12 && e.KeyModifiers == KeyModifiers.None)
        { e.Handled = true; await Perform(MovieStep); }
    }

    private void PresentController(PlaybackPresentation presentation)
    {
        _controllerDisplay.SetInput(presentation.Input);
        _controllerDetails.Text = presentation.Input is { } input ?
            $"Port 1 · last poll · group {presentation.Position:N0}\n{input.Buttons}   Stick {input.StickX},{input.StickY}   C {input.CStickX},{input.CStickY}   L/R {input.TriggerL}/{input.TriggerR}" :
            "Port 1 · no recorded input at this position";
    }
}
