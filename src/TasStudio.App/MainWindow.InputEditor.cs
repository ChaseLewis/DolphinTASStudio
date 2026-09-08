using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using TasStudio.Core;

namespace TasStudio.App;

public sealed partial class MainWindow
{
    private readonly StickPad[] _stickPads = [new(), new()];
    private readonly Slider[] _triggerSliders = [new(), new()];
    private readonly TextBlock _executionTargetLabel = new() { Name = "InputPosition", FontSize = 11, TextWrapping = TextWrapping.Wrap, Foreground = StudioTheme.Brush(ThemeColor.Muted) };
    private readonly TextBlock _inputPreviewLabel = new() { Name = "PreviewFrame", FontWeight = FontWeight.SemiBold };
    private readonly CheckBox _useController = new() { Name = "UseController", Content = "Use controller", FontSize = 12, MinHeight = 24, Height = 24 };
    private Control _manualInputControls = null!;
    private bool EditingAtPreview => _timeline.SelectedTake == null && (ulong)_timeline.SelectedFrame == _execution.Position && _timeline.SelectionEnd == _timeline.SelectedFrame + 1;
    private bool AuthoringNewInput => EditingAtPreview && _execution.Position >= (ulong)_execution.Inputs.Count;
    private bool UsesControllerInput => _useController.IsChecked == true && AuthoringNewInput;
    private readonly TurboInput _turboInput = new();
    private Task _pendingInputEdits = Task.CompletedTask;
    private Exception? _inputEditFailure;
    private ControllerState _pendingTasInput = ControllerState.Neutral;
    private long _inspectorRevision = -1;
    private long _inspectorSelectionVersion;
    private bool _inspectorReloadPending;
    private Control BuildInspector()
    {
        _useController.Resources["CheckBoxMinHeight"] = 24d;
        var heading = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 8 };
        _inputPreviewLabel.VerticalAlignment = VerticalAlignment.Center;
        heading.Children.Add(_inputPreviewLabel); Grid.SetColumn(_useController, 1); heading.Children.Add(_useController);
        _availability.Add((_useController, () => _execution.IsLoaded && _timeline.SelectedTake == null));
        _useController.IsCheckedChanged += (_, _) =>
        {
            if (_useController.IsChecked != true && AuthoringNewInput) _pendingTasInput = ShownInput(_pendingTasInput);
            UpdateInputAcceptance(); LoadSelectedInput(); RefreshControllerInput();
        };
        var editor = new StackPanel { Margin = new Thickness(12), Spacing = 8, Children =
        {
            heading, _selectionLabel
        } };
        var manual = new StackPanel { Spacing = 8 };
        _manualInputControls = manual; editor.Children.Add(manual);
        var buttons = new WrapPanel();
        foreach (var (_, button) in LiveInputSource.ButtonMap)
        {
            var check = new CheckBox { Content = button.ToString(), Margin = new Thickness(0, 0, 4, 0), FontSize = 12 };
            TurboCheckBox.Configure(check);
            ToolTip.SetTip(check, "Right-click to toggle turbo (one frame pressed, one frame released)");
            check.AddHandler(PointerPressedEvent, (_, e) =>
            {
                if (!e.GetCurrentPoint(check).Properties.IsRightButtonPressed || _timeline.SelectedTake != null) return;
                var enabled = _turboInput.Toggle(button, _execution.Position);
                ToolTip.SetTip(check, enabled ? "Turbo: + presses this frame; − releases it. Right-click to turn off." : "Right-click to toggle turbo (one frame pressed, one frame released)");
                _loadingInspector = true; check.IsChecked = enabled; _loadingInspector = false;
                if (!enabled)
                {
                    _pendingTasInput = _pendingTasInput with { Buttons = _pendingTasInput.Buttons & ~button };
                    if (_heldManualInput is { } held) _heldManualInput = held with { Buttons = held.Buttons & ~button };
                }
                RefreshControllerInput(); EditorChanged(button, 0); e.Handled = true;
            }, RoutingStrategies.Tunnel);
            check.IsCheckedChanged += (_, _) => { if (!_loadingInspector && check.IsChecked != null) EditorChanged(button, 0); };
            _buttons[button] = check; buttons.Children.Add(check);
        }
        _loadingInspector = true;
        for (var i = 0; i < _axes.Length; i++)
        {
            var index = i;
            ConfigureCompactAxis(_axes[i]);
            _axes[i].Name = "TasAxis" + i;
            _axes[i].Value = i < 4 ? ControllerState.NeutralAxis : 0;
            _axes[i].ValueChanged += (_, _) =>
            {
                if (_loadingInspector) return;
                if (index < 4 && NormalizeStickEditor(index / 2))
                {
                    EditorChanged(PadButtons.None, 3 << (index / 2 * 2)); return;
                }
                RefreshStickGraphics();
                if (_axes[index].Value != null) EditorChanged(PadButtons.None, 1 << index);
            };
        }
        _loadingInspector = false;
        var sticks = new Grid { ColumnDefinitions = new("*,*"), ColumnSpacing = 12 };
        for (var i = 0; i < 2; i++)
        {
            var index = i; var pad = _stickPads[i];
            pad.HorizontalAlignment = HorizontalAlignment.Center;
            (int Start, int Length, string? Take, long Version)? dragTarget = null;
            Avalonia.Automation.AutomationProperties.SetName(pad, i == 0 ? "Main stick drag control" : "C-stick drag control");
            pad.DragStarted += () =>
            {
                dragTarget = (_timeline.SelectedFrame, _timeline.SelectionEnd - _timeline.SelectedFrame, _timeline.SelectedTake, _inspectorSelectionVersion);
                // Stop playback once per gesture, before further input frames can be recorded.
                _pendingInputEdits = ObserveInputEdit(_execution.PauseAsync(), _inspectorSelectionVersion, _pendingInputEdits);
            };
            pad.PositionChanged += (x, y) =>
            {
                _loadingInspector = true; _axes[index * 2].Value = x; _axes[index * 2 + 1].Value = y; _loadingInspector = false;
            };
            pad.PositionCommitted += () =>
            {
                EditorChanged(PadButtons.None, 3 << (index * 2), dragTarget);
                dragTarget = null;
            };
            var center = new Button { Content = "Center", FontSize = 11, Padding = new Thickness(5, 2), HorizontalAlignment = HorizontalAlignment.Center };
            center.Click += (_, _) =>
            {
                _loadingInspector = true; _axes[index * 2].Value = 128; _axes[index * 2 + 1].Value = 128; _loadingInspector = false;
                RefreshStickGraphics(); EditorChanged(PadButtons.None, 3 << (index * 2));
            };
            var normalize = new CheckBox { Name = "NormalizeStick" + i, Content = "Normalize", FontSize = 11, MinHeight = 26, Height = 26 };
            var radius = new NumericUpDown { Name = "StickRadius" + i, Minimum = 0, Maximum = 1, Value = 1, Increment = .05m, FormatString = "0.00", ShowButtonSpinner = false };
            ConfigureCompactAxis(radius); radius.Width = double.NaN; radius.MinWidth = 46; radius.IsEnabled = false;
            Avalonia.Automation.AutomationProperties.SetName(radius, i == 0 ? "Main stick normalized radius" : "C-stick normalized radius");
            ToolTip.SetTip(normalize, "Constrain stick edits to this radius: 1 = full, 0.1 = 10%. Rounded to 8-bit values. Center stays neutral; mixed axes wait for a direction.");
            ToolTip.SetTip(radius, "Radius from center (0–1)");
            void UpdateNormalization()
            {
                radius.IsEnabled = normalize.IsChecked == true;
                pad.NormalizedRadius = normalize.IsChecked == true ? (double)(radius.Value ?? 1) : null;
                if (!_loadingInspector && NormalizeStickEditor(index)) EditorChanged(PadButtons.None, 3 << (index * 2));
            }
            normalize.IsCheckedChanged += (_, _) => UpdateNormalization();
            radius.ValueChanged += (_, _) => { if (radius.Value != null) UpdateNormalization(); };
            var normalization = new Grid { ColumnDefinitions = new("Auto,*"), ColumnSpacing = 4 };
            normalization.Children.Add(normalize); Grid.SetColumn(radius, 1); normalization.Children.Add(radius);
            Control AxisRow(string name, NumericUpDown axis)
            {
                axis.Width = double.NaN;
                var row = new Grid { ColumnDefinitions = new("Auto,*"), ColumnSpacing = 6 };
                row.Children.Add(Label(name)); Grid.SetColumn(axis, 1); row.Children.Add(axis);
                return row;
            }
            var column = new StackPanel { Spacing = 3, Children =
            {
                Row(new TextBlock { Text = i == 0 ? "Main stick" : "C-stick", FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center }, center),
                pad, AxisRow("X", _axes[i * 2]), AxisRow("Y", _axes[i * 2 + 1]), normalization
            } };
            Grid.SetColumn(column, i); sticks.Children.Add(column);
        }
        manual.Children.Add(sticks);
        for (var i = 0; i < 2; i++)
        {
            // Fluent's slider track and thumb need more height than the compact numeric field.
            var index = i; var slider = _triggerSliders[i]; slider.Height = 40; slider.MinHeight = 40; slider.VerticalAlignment = VerticalAlignment.Center; slider.Minimum = 0; slider.Maximum = 255; slider.TickFrequency = 1; slider.IsSnapToTickEnabled = true;
            Avalonia.Automation.AutomationProperties.SetName(slider, i == 0 ? "Left trigger pressure" : "Right trigger pressure");
            slider.ValueChanged += (_, _) => { if (!_loadingInspector) _axes[index + 4].Value = (decimal)Math.Round(slider.Value); };
            var row = new Grid { ColumnDefinitions = new("Auto,*,Auto"), ColumnSpacing = 8 };
            row.Children.Add(Label(i == 0 ? "L pressure" : "R pressure")); Grid.SetColumn(slider, 1); row.Children.Add(slider);
            Grid.SetColumn(_axes[i + 4], 2); row.Children.Add(_axes[i + 4]); manual.Children.Add(row);
        }
        editor.Children.Add(buttons);
        var next = ActionButton("Next Frame", Step, () => _execution.IsLoaded && _execution.IsPreviewCurrent);
        next.Name = "NextFrame"; next.Height = 38; next.Margin = new Thickness(0, 2, 0, 0);
        ToolTip.SetTip(next, "Recorded inputs always play unchanged. F11: frame advance & keep input. F10: frame advance & clear (neutral new input), moving the cursor to the preview. F12: play one recorded frame with the cursor parked; stop at the end. Shift+F11 advances while held.");
        next.HorizontalAlignment = HorizontalAlignment.Stretch; next.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        next.Background = StudioTheme.Brush(ThemeColor.Primary); next.Foreground = StudioTheme.Brush(ThemeColor.PrimaryText);
        var nextContent = new Grid { ColumnDefinitions = new("Auto,*,Auto"), ColumnSpacing = 10 };
        nextContent.Children.Add(new PathIcon { Data = Geometry.Parse(ToolIcons.Paths["step"]), Width = 20, Height = 20, Foreground = StudioTheme.Brush(ThemeColor.PrimaryText) });
        var nextLabel = new TextBlock { Text = "Advance & keep input", FontSize = 15, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(nextLabel, 1); nextContent.Children.Add(nextLabel);
        var shortcut = new TextBlock { Text = "F11", VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(shortcut, 2); nextContent.Children.Add(shortcut); next.Content = nextContent;
        Avalonia.Automation.AutomationProperties.SetName(next, "Next Frame");
        editor.Children.Add(next); editor.Children.Add(_executionTargetLabel);
        _availability.Add((editor, () => _execution.IsLoaded));
        LoadSelectedInput();
        var border = new Border { Background = PanelBrush, MinWidth = 320, Child = editor };
        // Squares follow their half of the panel width, bounded by the height left for controls.
        void FitStickPads()
        {
            var extraButtonHeight = Math.Max(0, buttons.Bounds.Height - 64);
            foreach (var pad in _stickPads) pad.MaxHeight = Math.Max(96, border.Bounds.Height - 421 - extraButtonHeight);
        }
        border.SizeChanged += (_, _) => FitStickPads();
        buttons.SizeChanged += (_, _) => FitStickPads();
        return border;
    }
    private static void ConfigureCompactAxis(NumericUpDown axis)
    {
        axis.Width = 88; axis.Height = 26; axis.MinHeight = 0;
        axis.FontSize = 12; axis.Padding = new Thickness(5, 2);
        axis.VerticalAlignment = VerticalAlignment.Center;
        axis.TextAlignment = TextAlignment.Center;
        axis.VerticalContentAlignment = VerticalAlignment.Center;
        axis.TemplateApplied += (_, template) =>
        {
            if (template.NameScope.Find<ButtonSpinner>("PART_Spinner") is not { } spinner) return;
            spinner.MinHeight = 0;
            spinner.TemplateApplied += (_, parts) =>
            {
                // Fluent assigns a local 34px minimum to these parts; a style cannot override it.
                foreach (var name in new[] { "PART_IncreaseButton", "PART_DecreaseButton" })
                    if (parts.NameScope.Find<RepeatButton>(name) is { } button)
                    { button.MinWidth = 0; button.Width = 20; button.Padding = new Thickness(2); }
            };
            spinner.ApplyTemplate();
        };
    }
    private bool NormalizeStickEditor(int index)
    {
        if (_stickPads[index].NormalizedRadius is not { } radius ||
            _axes[index * 2].Value is not { } x || _axes[index * 2 + 1].Value is not { } y) return false;
        var normalized = StickPad.Normalize((byte)x, (byte)y, radius);
        _loadingInspector = true;
        _axes[index * 2].Value = normalized.X; _axes[index * 2 + 1].Value = normalized.Y;
        _loadingInspector = false; RefreshStickGraphics(); return true;
    }
    private ControllerState ShownInput(ControllerState fallback) => TasInputExecution.Resolve(fallback,
        _buttons.Select(p => new KeyValuePair<PadButtons, bool?>(p.Key, p.Value.IsChecked)), _axes.Select(a => a.Value).ToArray());
    private static byte[] AxisValues(ControllerState s) => [s.StickX, s.StickY, s.CStickX, s.CStickY, s.TriggerL, s.TriggerR];
    private void RefreshStickGraphics()
    {
        var loading = _loadingInspector; _loadingInspector = true;
        for (var i = 0; i < 2; i++)
        {
            _stickPads[i].SetPosition(_axes[i * 2].Value is { } x ? (byte)x : null, _axes[i * 2 + 1].Value is { } y ? (byte)y : null);
            _triggerSliders[i].Value = (double)(_axes[i + 4].Value ?? 0);
            _triggerSliders[i].Opacity = _axes[i + 4].Value == null ? .45 : 1;
        }
        _loadingInspector = loading;
    }
    private void EditorChanged(PadButtons buttons, int axes, (int Start, int Length, string? Take, long Version)? capturedTarget = null)
    {
        if (_loadingInspector || !_execution.IsLoaded) return;
        if (UsesControllerInput || _advanceHeld) { RefreshControllerInput(); return; }
        var target = capturedTarget ?? (_timeline.SelectedFrame, _timeline.SelectionEnd - _timeline.SelectedFrame, _timeline.SelectedTake, _inspectorSelectionVersion);
        var value = ShownInput(_pendingTasInput);
        if (target.Take == null) _pendingTasInput = value;
        if (!_execution.HasProject || target.Start < 0 || (target.Take == null && target.Start > _execution.Inputs.Count)) return;
        var task = _execution.EditRangeAsync(target.Start, target.Length, target.Take, value, buttons, axes);
        _pendingInputEdits = ObserveInputEdit(task, target.Version, _pendingInputEdits);
    }
    private async Task ObserveInputEdit(Task edit, long selectionVersion, Task previous)
    {
        try
        {
            await Task.WhenAll(previous, edit); _audio.Flush();
            // A new selection may have read the old publication while this edit was queued.
            // Refresh it after completion instead of declaring its stale controls current.
            if (selectionVersion != _inspectorSelectionVersion || (_selectedInput < 0 && _timeline.SelectedFrame < _execution.Inputs.Count)) _inspectorReloadPending = true;
            _inspectorRevision = _execution.Revision;
        }
        catch (Exception ex) { _inputEditFailure = ex; _status.Text = "Input edit failed: " + ex.Message; }
    }
    private async Task FlushInputEditsAsync()
    {
        await _pendingInputEdits;
        if (_inputEditFailure is { } error) { _inputEditFailure = null; throw new InvalidOperationException("A TAS input edit failed. Review the selected inputs before advancing.", error); }
    }
    private void LoadSelectedInput()
    {
        UpdateInputAcceptance();
        ++_inspectorSelectionVersion;
        if (_stickPads.Any(p => p.IsDragging)) { _inspectorReloadPending = true; return; }
        _inspectorReloadPending = false;
        var start = _timeline.SelectedFrame; var end = _timeline.SelectionEnd;
        var take = _execution.Takes.FirstOrDefault(t => t.Id == _timeline.SelectedTake);
        var source = take?.Inputs ?? _execution.Inputs.ToArray(); var offset = take?.Start ?? 0;
        _selectedInput = start >= offset && start < end && end <= offset + source.Length ? start : -1;
        _selectionLabel.Text = start > source.Length + offset ? $"No recorded input · group {start}" : _selectedInput < 0 ? "Pending next frame group" : $"{take?.Name ?? "Active playback"} · groups [{start}, {end})";
        if (_timeline.SelectedTake != null && _selectedInput < 0) _selectionLabel.Text = "Select inputs inside this candidate to edit it.";
        if ((_advanceHeld || UsesControllerInput) && AuthoringNewInput)
        {
            // Keep live/held controls visible instead of briefly loading the next
            // recorded input before the next UI refresh replaces it again.
            _inspectorRevision = _execution.Revision;
            RefreshControllerInput(); UpdateExecutionTargetLabel();
            return;
        }
        var selected = _selectedInput < 0 ? new[] { start > source.Length + offset ? ControllerState.Neutral : _pendingTasInput } : source.Skip(start - offset).Take(end - start).ToArray();
        _loadingInspector = true;
        foreach (var (button, check) in _buttons)
        {
            var values = selected.Select(s => s.Buttons.HasFlag(button)).Distinct().ToArray();
            check.IsChecked = values.Length == 1 ? values[0] : null;
        }
        for (var i = 0; i < _axes.Length; i++)
        { var index = i; var values = selected.Select(s => AxisValues(s)[index]).Distinct().ToArray(); _axes[i].Value = values.Length == 1 ? values[0] : null; }
        _loadingInspector = false; RefreshStickGraphics(); _inspectorRevision = _execution.Revision;
        UpdateExecutionTargetLabel();
    }
    private void UpdateExecutionTargetLabel()
    {
        _inputPreviewLabel.Text = $"Frame {_execution.VideoFieldCount:N0}";
        _selectionLabel.IsVisible = _timeline.SelectedTake != null || _timeline.SelectedFrame > _execution.Inputs.Count || (_selectedInput >= 0 && (ulong)_selectedInput != _execution.Position) || _timeline.SelectionEnd - _timeline.SelectedFrame > 1;
        _executionTargetLabel.Text = _execution.HasProject && !_execution.IsPreviewCurrent
            ? "Earlier inputs changed — use Seek before advancing."
            : _timeline.SelectedTake != null
            ? $"Active frame group {_execution.Position:N0} · Poll {CurrentPollPosition:N0}"
            : $"{(UsesControllerInput ? "Controller · " : "")}Frame group {_execution.Position:N0} · Poll {CurrentPollPosition:N0}";
        ToolTip.SetTip(_executionTargetLabel, _timeline.SelectedTake != null
            ? "Candidate edits stay separate. Next Frame plays the active timeline; use Audition to preview this candidate."
            : "Next Frame plays recorded input unchanged, or uses displayed controls for a new frame. Editing changes the selected input. Selection does not move the preview; use Seek to selection.");
    }
    private void RefreshInputEditorStatus()
    {
        UpdateExecutionTargetLabel();
        if (!_stickPads.Any(p => p.IsDragging) && _pendingInputEdits.IsCompleted &&
            (_inspectorReloadPending || _inspectorRevision != _execution.Revision)) LoadSelectedInput();
        RefreshControllerInput();
    }
    private void RefreshControllerInput()
    {
        var turboDown = _turboInput.Apply(ControllerState.Neutral, _execution.Position).Buttons;
        foreach (var (button, check) in _buttons)
            TurboCheckBox.SetPhase(check, EditingAtPreview && _turboInput.Buttons.HasFlag(button), AuthoringNewInput ? turboDown.HasFlag(button) : check.IsChecked == true);
        var editable = _timeline.SelectedFrame <= _execution.Inputs.Count;
        _manualInputControls.IsEnabled = editable && !UsesControllerInput && !_advanceHeld;
        foreach (var check in _buttons.Values) check.IsEnabled = editable;
        ToolTip.SetTip(_useController, $"{_input.DeviceStatus}. Mirror mapped input and sample it on Next Frame; configure the device in Controllers.");
        if (!_execution.IsLoaded || !AuthoringNewInput) return;
        var liveDisplay = UsesControllerInput || _advanceHeld;
        var state = _turboInput.Apply(UsesControllerInput ? _input.Read() : _heldManualInput ?? ShownInput(_pendingTasInput), _execution.Position);
        _loadingInspector = true;
        try
        {
            foreach (var (button, check) in _buttons)
                if (liveDisplay || _turboInput.Buttons.HasFlag(button)) check.IsChecked = state.Buttons.HasFlag(button);
            if (liveDisplay)
            {
                var axes = AxisValues(state);
                for (var i = 0; i < axes.Length; i++) _axes[i].Value = axes[i];
            }
        }
        finally { _loadingInspector = false; }
        RefreshStickGraphics();
    }
    private async Task StepWithEditorInputAsync(bool blankInput = false)
    {
        if (!_execution.IsLoaded) return;
        if (blankInput && _timeline.SelectedTake != null) return;
        await FlushInputEditsAsync();
        await _execution.PauseAsync();
        var position = _execution.Position;
        var fallback = position < (ulong)_execution.Inputs.Count ? _execution.Inputs[(int)position] : _pendingTasInput;
        var shown = UsesControllerInput ? _input.Read() : _heldManualInput ?? ShownInput(fallback);
        if (EditingAtPreview || _advanceHeld) shown = _turboInput.Apply(shown, _execution.Position);
        var candidate = _timeline.SelectedTake != null;
        // Supplied controls only fill a missing group; execution preserves every existing group.
        if (blankInput) shown = ControllerState.Neutral;
        var atEnd = _timeline.SelectedFrame == _execution.Inputs.Count;
        var followsPreview = (ulong)_timeline.SelectedFrame == position && (ulong)_timeline.SelectionEnd == position + 1;
        _audio.Flush();
        if (!await TasInputExecution.StepAsync(_execution, shown, candidate))
        {
            StopHeldAdvance();
            _status.Text = "Earlier inputs changed — use Seek before advancing.";
            return;
        }
        if (!candidate)
        {
            _pendingTasInput = shown;
            if (blankInput || atEnd || followsPreview)
            {
                _timeline.InputCount = _execution.Inputs.Count;
                var next = checked((int)_execution.Position); _timeline.SetSelection(next, next + 1);
            }
            LoadSelectedInput();
        }
    }
}
