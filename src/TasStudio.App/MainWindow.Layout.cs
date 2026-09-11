using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using TasStudio.Core;
using TasStudio.Emulation;

namespace TasStudio.App;

public sealed partial class MainWindow
{
    private readonly TextBlock _selectionLabel = new() { Text = "Select an input", TextWrapping = TextWrapping.Wrap };
    private bool _loadingInspector;
    private readonly TextBlock _timelinePreview = new() { Name = "TimelinePosition", Text = "Frame: 0 · Poll: 0", VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
    private readonly TextBlock _playLabel = new() { Text = "Play", VerticalAlignment = VerticalAlignment.Center };
    private readonly PathIcon _playIcon = new() { Width = 17, Height = 17, Foreground = StudioTheme.Brush(ThemeColor.Icon) };
    private Button _transportPlay = null!;
    private readonly TextBlock _compatibilityNotice = new()
    {
        Name = "CompatibilityNotice", IsVisible = false, TextWrapping = TextWrapping.Wrap,
        FontSize = 12, Margin = new Thickness(10, 6), Foreground = StudioTheme.Brush(ThemeColor.Text)
    };
    private static readonly IBrush PanelBrush = StudioTheme.Brush(ThemeColor.Panel), LineBrush = StudioTheme.Brush(ThemeColor.Border);
    private Control BuildLayout()
    {
        bool Loaded() => !_homeVisible && _execution.IsLoaded;
        bool Project() => _execution.HasProject;
        var root = new Grid { RowDefinitions = new("Auto,Auto,*,Auto") };
        root.Children.Add(new Menu { ItemsSource = new[]
        {
            new MenuItem { Header = "_File", ItemsSource = new Control[] { ActionMenu("Play Game…    Ctrl+O", OpenGame), ActionMenu("Projects", ShowProjects), ActionMenu("New Project", NewProject), ActionMenu("Open Project / Replay…", OpenProject), ActionMenu("Recover inputs…", RecoverInputs), ActionMenu("Save Project    Ctrl+S", SaveProject, Project), ActionMenu("Save Project As…", SaveProjectAs, Project), ActionMenu("Export Replay…", ExportReplay, Project) } },
            new MenuItem { Header = "_Emulation", ItemsSource = new Control[] { ActionMenu("Play / Pause", ToggleRun, Loaded), ActionMenu("Frame advance & keep input    F11", Step, Loaded), ActionMenu("Frame advance & clear    F10", StepNeutral, () => Loaded() && _execution.IsPreviewCurrent && _timeline.SelectedTake == null), ActionMenu("Play one recorded frame    F12", StepWithoutMovingSelection, () => Loaded() && _execution.HasProject && _execution.IsPreviewCurrent && _execution.Position < (ulong)_execution.Inputs.Count), ActionMenu("Pause    Escape", PausePlayback, Loaded), ActionMenu("Restart from start", RestartPlayback, Project), ActionMenu("Previous save state", PreviousState, Project), ActionMenu("Reset", Reset, Loaded), new Separator(), ActionMenu("Save State to File…", SaveStateFile, Loaded), ActionMenu("Load State from File…", LoadStateFile, Loaded), ActionMenu("Save Slot 1    Shift+F1", () => SaveSlot(1), Loaded), ActionMenu("Load Slot 1    F1", () => LoadSlot(1), Loaded) } },
            new MenuItem { Header = "_Movie", ItemsSource = new Control[] { ActionMenu("Record live controller input", RecordLive, Project), new Separator(), ActionMenu("Undo    Ctrl+Z", _execution.UndoAsync, Project), ActionMenu("Redo    Ctrl+Y", _execution.RedoAsync, Project), ActionMenu("Copy Selection as Take", CopyTake, Project), ActionMenu("Save Named State", SaveNamedState, Project) } },
            new MenuItem { Header = "_Options", ItemsSource = new[] { ActionMenu("Controllers…", ControllerSettings), ActionMenu("Configuration…", ApplicationSettings), ActionMenu("Updates…", UpdateSettings) } },
            new MenuItem { Header = "_View", ItemsSource = BuildViewMenu() }
        } });
        var toolbar = new WrapPanel { Margin = new Thickness(8, 4, 8, 7) };
        toolbar.Children.Add(IconButton("Projects", "folder", ShowProjects));
        toolbar.Children.Add(IconButton("Save", "save", SaveProject, Project));
        toolbar.Children.Add(ToolSeparator());
        toolbar.Children.Add(IconButton("Controllers", "pad", ControllerSettings)); toolbar.Children.Add(IconButton("Config", "config", ApplicationSettings));
        toolbar.Children.Add(IconButton("Watches", "watch", () => { ToggleWatcher(); return Task.CompletedTask; }, () => !_execution.IsManualPlay)); toolbar.Children.Add(ToolSeparator());
        var soundIcon = new Grid { Width = 24, Height = 24 };
        soundIcon.Children.Add(new Avalonia.Controls.Shapes.Path
        {
            Data = Geometry.Parse("M2,8 L7,8 L13,3 L13,21 L7,16 L2,16 Z"), Fill = StudioTheme.Brush(ThemeColor.Icon)
        });
        var soundWaves = new Avalonia.Controls.Shapes.Path
        {
            Data = Geometry.Parse("M16,8 C19,10 19,14 16,16 M19,4 C25,8 25,16 19,20"),
            Stroke = StudioTheme.Brush(ThemeColor.Icon), StrokeThickness = 2
        };
        soundIcon.Children.Add(soundWaves);
        var cancelSymbol = new Border
        {
            Width = 16, Height = 16, CornerRadius = new CornerRadius(8), Background = Background,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
            Child = new Avalonia.Controls.Shapes.Path
            {
                Data = Geometry.Parse("M8,1 A7,7 0 1 1 8,15 A7,7 0 1 1 8,1 M3,3 L13,13"),
                Stroke = StudioTheme.Brush(ThemeColor.Error), StrokeThickness = 2
            }
        };
        soundIcon.Children.Add(cancelSymbol);
        var mute = new Button
        {
            Content = soundIcon, Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Padding = new Thickness(9), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0)
        };
        toolbar.Children.Add(mute);
        var volume = new Slider { Minimum = 0, Maximum = 100, Value = _settings.Volume, Width = 90, VerticalAlignment = VerticalAlignment.Center };
        Avalonia.Automation.AutomationProperties.SetName(volume, "Volume");
        void RefreshAudioControls()
        {
            soundWaves.IsVisible = !_settings.Muted;
            cancelSymbol.IsVisible = _settings.Muted;
            volume.IsEnabled = !_settings.Muted;
            var action = _settings.Muted ? "Unmute audio" : "Mute audio";
            ToolTip.SetTip(mute, action);
            Avalonia.Automation.AutomationProperties.SetName(mute, action);
        }
        mute.Click += (_, _) =>
        {
            _settings.Muted = !_settings.Muted;
            _audio.SetVolume(_settings.Volume, _settings.Muted);
            RefreshAudioControls();
        };
        RefreshAudioControls();
        volume.ValueChanged += (_, _) => { _settings.Volume = (int)volume.Value; _audio.SetVolume(_settings.Volume, _settings.Muted); }; toolbar.Children.Add(volume);
        Grid.SetRow(toolbar, 1); root.Children.Add(toolbar);
        var workspace = BuildDockWorkspace();
        Grid.SetRow(workspace, 2); root.Children.Add(workspace);
        _editorToolbar = toolbar; _editorWorkspace = workspace;
        _manualWorkspace = BuildPlayWorkspace(); Grid.SetRow(_manualWorkspace, 2); root.Children.Add(_manualWorkspace);
        var homeTools = new WrapPanel { Margin = new Thickness(8, 4, 8, 7) };
        homeTools.Children.Add(IconButton("New project", "state", NewProject));
        homeTools.Children.Add(IconButton("Open project", "folder", OpenProject));
        homeTools.Children.Add(IconButton("Verify projects", "watch", () => VerifyProjects(_settings.RecentProjects)));
        _cancelVerification.Click += (_, _) => _verificationCancellation?.Cancel();
        homeTools.Children.Add(_cancelVerification);
        homeTools.Children.Add(ToolSeparator());
        homeTools.Children.Add(IconButton("Play a game", "pad", OpenGame));
        homeTools.Children.Add(IconButton("Resume game / editor", "play", () => { ShowEditor(); return Task.CompletedTask; }, () => _execution.IsLoaded));
        _homeToolbar = homeTools; Grid.SetRow(homeTools, 1); root.Children.Add(homeTools);
        _projectHome = BuildProjectHome(); Grid.SetRow(_projectHome, 2); root.Children.Add(_projectHome);
        toolbar.IsVisible = workspace.IsVisible = false;
        var status = new Grid { ColumnDefinitions = new("*,Auto,Auto"), ColumnSpacing = 16, Margin = new Thickness(10, 5) };
        _projectStatus.FontSize = 11; _projectStatus.TextWrapping = TextWrapping.NoWrap; _projectStatus.TextTrimming = TextTrimming.CharacterEllipsis;
        _status.FontSize = 11; _status.TextWrapping = TextWrapping.NoWrap; _status.TextTrimming = TextTrimming.CharacterEllipsis; _status.MaxWidth = 450;
        status.Children.Add(_projectStatus); Grid.SetColumn(_status, 1); status.Children.Add(_status);
        Grid.SetColumn(_updateNotice, 2); status.Children.Add(_updateNotice);
        var footer = new StackPanel(); footer.Children.Add(_compatibilityNotice); footer.Children.Add(status);
        Grid.SetRow(footer, 3); root.Children.Add(footer); return root;
    }
    private static Control ToolSeparator() => new Border { Width = 1, Background = LineBrush, Margin = new Thickness(7, 7) };
    private Button IconButton(string name, string icon, Func<Task> action, Func<bool>? enabled = null)
    {
        var panel = new StackPanel { Spacing = 8, Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        panel.Children.Add(new PathIcon { Data = Geometry.Parse(ToolIcons.Paths[icon]), Width = 23, Height = 23, Foreground = StudioTheme.Brush(ThemeColor.Icon) });
        panel.Children.Add(new TextBlock { Text = name, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        var button = ActionButton(name, action, enabled); button.Content = panel; button.Background = Brushes.Transparent; button.BorderThickness = new Thickness(0); button.Padding = new Thickness(10, 7);
        ToolTip.SetTip(button, name); Avalonia.Automation.AutomationProperties.SetName(button, name); return button;
    }
    private Control BuildTimeline()
    {
        var panel = new Grid { RowDefinitions = new("Auto,*,Auto"), Background = PanelBrush };
        var header = new Grid { ColumnDefinitions = new("*,Auto"), Margin = new Thickness(6, 5) };
        var transport = new WrapPanel();
        transport.Children.Add(TransportButton("Restart", "restart", RestartPlayback, () => _execution.HasProject));
        var previous = TransportButton("Previous state", "previous-state", PreviousState, () => _execution.HasProject && _execution.Position > 0);
        previous.Name = "PreviousState"; ToolTip.SetTip(previous, "Previous valid save state or checkpoint; project start if none remains"); transport.Children.Add(previous);
        _transportPlay = TransportButton("Play", "play-only", ToggleRun, () => _execution.HasProject && (_execution.IsRunning || _execution.Position < (ulong)_execution.Inputs.Count));
        _transportPlay.Name = "TimelinePlay"; _playIcon.Data = Geometry.Parse(ToolIcons.Paths["play-only"]); _transportPlay.Content = Row(_playIcon, _playLabel); transport.Children.Add(_transportPlay);
        var seek = TransportButton("Seek", "seek", () => SeekTo((ulong)_timeline.SelectedFrame),
            () => _execution.HasProject && _timeline.SelectedFrame <= _execution.Inputs.Count && (!_execution.IsPreviewCurrent || _execution.Position != (ulong)_timeline.SelectedFrame));
        seek.Name = "SeekSelection"; ToolTip.SetTip(seek, "Seek the game preview to the start of the timeline selection");
        transport.Children.Add(seek);
        transport.Children.Add(TransportButton("Save state", "state", SaveNamedState, () => _execution.HasProject));
        transport.Children.Add(_cancelSeek);
        header.Children.Add(transport); Grid.SetColumn(_timelinePreview, 1); header.Children.Add(_timelinePreview); panel.Children.Add(header);
        _cancelSeek.Click += async (_, _) => { if (_seeking) { _cancelSeek.IsEnabled = false; await _execution.PauseAsync(); } };
        _timeline.SelectionChanged += LoadSelectedInput;
        _timeline.TagRequested += async position => await Perform(() => EditTimelineTag(null, position));
        var scroll = new ScrollViewer { Content = _timeline, VerticalContentAlignment = VerticalAlignment.Top, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        Grid.SetRow(scroll, 1); panel.Children.Add(scroll);
        int ClearFrom(bool context) => context ? _timeline.ContextFrame ?? -1 : _timeline.SelectedFrame;
        Control[] EditMenu(bool context = false) => [
            ActionMenu("Undo    Ctrl+Z", _execution.UndoAsync, () => _execution.HasProject),
            ActionMenu("Redo    Ctrl+Y", _execution.RedoAsync, () => _execution.HasProject), new Separator(),
            ActionMenu("Copy selection as take", CopyTake, HasInputSelection),
            ActionMenu("Clear selected input", ClearSelectedInput, CanClearSelectedInput),
            ActionMenu("Clear later input", () => ClearLaterInput(ClearFrom(context)),
                () => _execution.HasProject && (context || _timeline.SelectedTake == null) && ClearFrom(context) >= 0 && ClearFrom(context) < _execution.Inputs.Count - 1),
            ActionMenu("Use selected section", UseTake, () => HasInputSelection() && _timeline.SelectedTake != null),
            TakeAtCursorMenu(),
            ActionMenu("Audition take", AuditionTake, () => HasInputSelection() && _timeline.SelectedTake != null), new Separator(),
            ActionMenu("Load selected state", LoadSelectedMarker, () => _timeline.SelectedMarker is { Valid: true }),
            ActionMenu("Clear selected state", ClearSelectedMarker, () => _timeline.SelectedMarker != null),
            new MenuItem { Header = "Experiment", Name = "StateExperimentMenu", IsVisible = false },
            new Separator(),
            ActionMenu("Rename tag…", () => EditTimelineTag(_timeline.SelectedTag, _timeline.SelectedTag!.Position), () => _timeline.SelectedTag != null),
            ActionMenu("Remove tag", () => _execution.RemoveTagAsync(_timeline.SelectedTag!.Id), () => _timeline.SelectedTag != null),
            ActionMenu("Checkpoint settings…", CheckpointSettings, () => _execution.HasProject)
        ];
        ContextMenu CreateEditMenu(bool context = false)
        {
            var editMenu = new ContextMenu { ItemsSource = EditMenu(context) };
            editMenu.Opened += (_, _) =>
            {
                PopulateStateExperiments(editMenu.Items.OfType<MenuItem>().Single(item => item.Name == "StateExperimentMenu"));
                var parent = editMenu.Items.OfType<MenuItem>().Single(item => item.Name == "ApplyTakeAtCursor");
                parent.ItemsSource = _execution.Takes.Select(take =>
                {
                    var item = new MenuItem { Header = take.Name };
                    item.Click += async (_, e) => { e.Handled = true; await Perform(() => ApplyTakeAtCursor(take.Id)); };
                    return item;
                }).ToArray();
            };
            return editMenu;
        }
        _timeline.ContextMenu = CreateEditMenu(true);
        var actions = new Grid { ColumnDefinitions = new("Auto,*,Auto"), ColumnSpacing = 8, Margin = new Thickness(10, 6) };
        var left = new WrapPanel(); left.Children.Add(ActionButton("+ Take", CopyTake, HasInputSelection));
        var more = new Button { Content = "⋯", Padding = new Thickness(9, 2) };
        var menu = CreateEditMenu(); more.Click += (_, _) => menu.Open(more);
        ToolTip.SetTip(more, "Timeline editing and checkpoint settings"); left.Children.Add(more); actions.Children.Add(left);
        var horizontal = new Avalonia.Controls.Primitives.ScrollBar
        {
            Name = "TimelineScroll", Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center, MinHeight = 16,
            AllowAutoHide = false
        };
        Avalonia.Automation.AutomationProperties.SetName(horizontal, "Timeline scroll");
        var syncingScroll = false;
        _timeline.ViewportChanged += () =>
        {
            syncingScroll = true;
            try
            {
                horizontal.Maximum = _timeline.MaximumFirstFrame;
                horizontal.ViewportSize = _timeline.VisibleFrames;
                horizontal.SmallChange = Math.Max(1, _timeline.VisibleFrames / 20);
                horizontal.LargeChange = Math.Max(1, _timeline.VisibleFrames * .8);
                horizontal.Value = _timeline.FirstFrame;
            }
            finally { syncingScroll = false; }
        };
        horizontal.ValueChanged += (_, _) => { if (!syncingScroll) _timeline.PanTo((int)horizontal.Value); };
        Grid.SetColumn(horizontal, 1); actions.Children.Add(horizontal);
        var zoom = new StackPanel { Orientation = Orientation.Horizontal };
        zoom.Children.Add(ActionButton("−", () => ZoomTimeline(1.5))); zoom.Children.Add(ActionButton("+", () => ZoomTimeline(.67)));
        Grid.SetColumn(zoom, 2); actions.Children.Add(zoom);
        ToolTip.SetTip(scroll, "Click selects inputs. Drag selects a range. Middle-click adds a visual tag. Wheel pans; Ctrl+wheel zooms. Right-click a tag to rename or remove it.");
        Grid.SetRow(actions, 2); panel.Children.Add(actions); return panel;
    }
    private bool HasInputSelection() => _execution.HasProject && _timeline.SelectedFrame < _execution.Inputs.Count;
    private bool CanClearSelectedInput()
    {
        if (!_execution.HasProject) return false;
        var take = _execution.Takes.FirstOrDefault(t => t.Id == _timeline.SelectedTake);
        if (_timeline.SelectedTake != null && take == null) return false;
        var start = take?.Start ?? 0;
        var end = take == null ? _execution.Inputs.Count : start + take.Inputs.Length;
        return _timeline.SelectedFrame >= start && _timeline.SelectionEnd > _timeline.SelectedFrame && _timeline.SelectionEnd <= end;
    }
    private async Task ClearSelectedInput()
    {
        if (!CanClearSelectedInput()) return;
        var start = _timeline.SelectedFrame; var length = _timeline.SelectionEnd - start; var take = _timeline.SelectedTake;
        await FlushInputEditsAsync(); await PausePlayback();
        await _execution.EditRangeAsync(start, length, take, ControllerState.Neutral, ControllerState.KnownButtons, 0b111111);
        LoadSelectedInput();
    }
    private async Task ClearLaterInput(int start)
    {
        await FlushInputEditsAsync(); await PausePlayback();
        await _execution.ClearLaterInputAsync(start);
        _timeline.InputCount = _execution.Inputs.Count;
        _timeline.SetSelection(start, start + 1);
        LoadSelectedInput();
    }
    private async Task LoadSelectedMarker()
    {
        if (_timeline.SelectedMarker is not { Valid: true } marker) return;
        await FlushInputEditsAsync(); await PausePlayback(); await _execution.LoadMarkerAsync(marker.Id); SelectPreviewInput();
    }
    private async Task ClearSelectedMarker()
    {
        if (_timeline.SelectedMarker is not { } marker) return;
        await _execution.ClearMarkerAsync(marker.Id);
    }
    private Button TransportButton(string label, string icon, Func<Task> action, Func<bool> enabled)
    {
        var button = ActionButton(label, action, enabled); button.FontSize = 12; button.Padding = new Thickness(6, 5);
        button.Background = Brushes.Transparent; button.BorderThickness = new Thickness(0);
        button.Content = Row(new PathIcon { Data = Geometry.Parse(ToolIcons.Paths[icon]), Width = 17, Height = 17, Foreground = StudioTheme.Brush(ThemeColor.Icon) }, Label(label));
        Avalonia.Automation.AutomationProperties.SetName(button, label); return button;
    }
    private Task ZoomTimeline(double multiplier) { _timeline.Zoom(multiplier); return Task.CompletedTask; }
    private void RefreshTimeline()
    {
        _ = RefreshExperimentMarkerColors();
        _timelinePreview.Text = $"{(_execution.IsRecordingLive ? "REC · " : "")}{PlaybackPositionText}";
        _timelinePreview.Foreground = _execution.IsRecordingLive ? StudioTheme.Brush(ThemeColor.Error) : StudioTheme.Brush(ThemeColor.Text);
        var playing = _execution.IsRunning;
        if (_playLabel.Text != (playing ? "Pause" : "Play"))
        {
            _playLabel.Text = playing ? "Pause" : "Play";
            _playIcon.Data = Geometry.Parse(ToolIcons.Paths[playing ? "pause" : "play-only"]);
            Avalonia.Automation.AutomationProperties.SetName(_transportPlay, _playLabel.Text);
        }
        var previewMoved = _timeline.Position != _execution.Position;
        _timeline.Inputs = _execution.Inputs;
        _timeline.InputCount = _timeline.Inputs.Count; _timeline.Position = _execution.Position; _timeline.Current = _execution.IsPreviewCurrent;
        _timeline.PollBoundaries = _execution.PollBoundaries;
        if (previewMoved) _timeline.RevealPosition();
        _timeline.Takes = _execution.Takes; _timeline.Sections = _execution.Sections; _timeline.Markers = _execution.StateMarkers;
        _timeline.Tags = _execution.Tags;
        _timeline.RefreshMarker(); _timeline.Update();
    }
    private async Task CopyTake() { await _execution.CaptureTakeAsync("Take " + (_execution.Takes.Count + 1), _timeline.SelectedFrame, _timeline.SelectionEnd - _timeline.SelectedFrame); }
    private MenuItem TakeAtCursorMenu()
    {
        var item = new MenuItem { Header = "Apply take at cursor", Name = "ApplyTakeAtCursor" };
        _availability.Add((item, () => _execution.HasProject && _execution.Takes.Count > 0 && _timeline.SelectedFrame <= _execution.Inputs.Count));
        return item;
    }
    private async Task ApplyTakeAtCursor(string id)
    {
        var target = _timeline.SelectedFrame;
        await FlushInputEditsAsync();
        await _execution.ApplyTakeAtCursorAsync(id, target);
        _timeline.InputCount = _execution.Inputs.Count;
        _timeline.SetSelection(target, target + 1);
        LoadSelectedInput();
    }
    private async Task UseTake()
    {
        var take = _timeline.SelectedTake!; var start = _timeline.SelectedFrame; var end = _timeline.SelectionEnd;
        await FlushInputEditsAsync();
        await _execution.UseTakeAsync(take, start, end - start, removeTake: true);
        _timeline.SetSelection(start, end);
        LoadSelectedInput();
    }
    private async Task AuditionTake()
    {
        _seeking = true; _cancelSeek.IsVisible = true; _cancelSeek.IsEnabled = true;
        try { await _execution.AuditionTakeAsync(_timeline.SelectedTake!, _timeline.SelectionEnd); }
        catch (OperationCanceledException) { _status.Text = "Audition canceled; active movie preserved."; }
        finally { _seeking = false; _cancelSeek.IsVisible = false; }
    }
    private async Task SaveNamedState() { await FlushInputEditsAsync(); await _execution.SaveNamedStateAsync("State " + _execution.Position); }
    private async Task<bool> SeekTo(ulong position)
    {
        _seeking = true; _cancelSeek.IsVisible = true; _cancelSeek.IsEnabled = true;
        try
        {
            await FlushInputEditsAsync(); await _execution.PauseAsync(); _audio.Flush();
            await _execution.SeekAsync(position);
            // A seek returns to inspecting the selected recording, even when
            // F12 left the cursor parked and no selection-change event fires.
            LoadSelectedInput();
            return true;
        }
        catch (OperationCanceledException) { _status.Text = $"Seek canceled at state {_execution.Position}."; return false; }
        finally { _seeking = false; _cancelSeek.IsVisible = false; _audio.Flush(); }
    }
}

internal static class ToolIcons
{
    public static readonly IReadOnlyDictionary<string, string> Paths = new Dictionary<string, string>
    {
        ["play-only"] = "M5,2 L22,12 5,22 Z", ["pause"] = "M5,3 L10,3 10,21 5,21 Z M14,3 L19,3 19,21 14,21 Z",
        ["restart"] = "M2,3 L5,3 5,21 2,21 Z M14,3 L5,12 14,21 Z M23,3 L14,12 23,21 Z",
        ["previous-state"] = "M10,3 L10,9 20,9 20,20 17,20 17,12 10,12 10,18 1,10 Z",
        ["seek"] = "M2,3 L14,12 2,21 Z M17,3 L20,3 20,21 17,21 Z",
        ["window"] = "M2,8 L10,8 10,10 4,10 4,20 14,20 14,14 16,14 16,22 2,22 Z M12,2 L22,2 22,12 20,12 20,5 10,15 8,13 18,4 12,4 Z",
        ["folder"] = "M2,4 L9,4 12,7 22,7 22,20 2,20 Z M4,9 L4,18 20,18 20,9 Z",
        ["save"] = "M3,2 L19,2 22,5 22,22 2,22 2,2 Z M6,3 L6,10 17,10 17,3 Z M6,14 L6,21 18,21 18,14 Z",
        ["play"] = "M3,2 L16,12 3,22 Z M18,3 L21,3 21,21 18,21 Z", ["stop"] = "M4,4 L20,4 20,20 4,20 Z",
        ["step"] = "M3,3 L17,12 3,21 Z M19,3 L22,3 22,21 19,21 Z", ["state"] = "M5,2 L19,2 19,23 12,18 5,23 Z M8,5 L8,14 16,14 16,5 Z",
        ["pad"] = "M5,5 L19,5 23,18 20,21 15,16 9,16 4,21 1,18 Z M5,8 L5,10 3,10 3,12 5,12 5,14 7,14 7,12 9,12 9,10 7,10 7,8 Z M16,9 L16,12 19,12 19,9 Z",
        ["config"] = "M2,5 L22,5 22,7 2,7 Z M2,11 L22,11 22,13 2,13 Z M2,17 L22,17 22,19 2,19 Z M6,3 L9,3 9,9 6,9 Z M15,9 L18,9 18,15 15,15 Z M7,15 L10,15 10,21 7,21 Z",
        ["watch"] = "M2,3 L6,3 6,7 2,7 Z M9,4 L22,4 22,6 9,6 Z M2,10 L6,10 6,14 2,14 Z M9,11 L22,11 22,13 9,13 Z M2,17 L6,17 6,21 2,21 Z M9,18 L22,18 22,20 9,20 Z"
    };
}
