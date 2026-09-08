using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace TasStudio.App;

public sealed partial class MainWindow
{
    private Task ControllerSettings() => ControllerSettingsCore(null);
    private async Task ControllerSettingsCore(string? capturePath)
    {
        await _execution.PauseAsync(); _audio.Flush();
        var dialog = new Window
        {
            Title = "Controller Settings — GameCube Port 1", Width = 940, Height = 650, MinWidth = 860, MinHeight = 650,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var draft = new AppSettings
        {
            Keys = new(_settings.Keys), GamepadButtons = new(_settings.GamepadButtons), GamepadBindings = new(_settings.GamepadBindings)
        };
        var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), Margin = new Thickness(18) };
        var heading = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), RowDefinitions = new RowDefinitions("Auto,Auto"), Margin = new Thickness(0, 0, 0, 14) };
        var devicePanel = new StackPanel { Spacing = 5 };
        devicePanel.Children.Add(new TextBlock { Text = "Device", FontSize = 12, FontWeight = FontWeight.SemiBold });
        var devices = new ComboBox
        {
            Width = 290, SelectedIndex = _settings.GamepadIndex + 1,
            ItemsSource = new[] { "Keyboard", "XInput controller 1", "XInput controller 2", "XInput controller 3", "XInput controller 4" }
        };
        devicePanel.Children.Add(devices); heading.Children.Add(devicePanel);
        var title = new TextBlock { Text = "GameCube · Port 1", FontSize = 19, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(title, 2); heading.Children.Add(title);
        var deviceStatus = new TextBlock { FontSize = 12, Margin = new Thickness(0, 8, 0, 0), Foreground = StudioTheme.Brush(ThemeColor.Muted) };
        Grid.SetRow(deviceStatus, 1); Grid.SetColumnSpan(deviceStatus, 3); heading.Children.Add(deviceStatus); layout.Children.Add(heading);
        var mappingButtons = new Dictionary<PadControl, Button>();
        PadControl? listening = null;
        GamepadBindingCapture? capture = null;
        var deadline = DateTime.UtcNow;
        var message = new TextBlock { Text = "Click a binding, then press a key or move a control on the selected device.", FontSize = 12, TextWrapping = TextWrapping.Wrap };
        bool Keyboard() => devices.SelectedIndex == 0;
        string BindingName(PadControl control) => Keyboard()
            ? draft.Keys.GetValueOrDefault(control, Key.None) is var key && key != Key.None ? key.ToString() : "—"
            : GamepadBinding.For(draft, control).ToString();
        void RefreshBindings()
        {
            foreach (var (control, button) in mappingButtons)
            {
                button.Content = listening == control ? "Listening…" : BindingName(control);
                button.BorderBrush = listening == control ? StudioTheme.Brush(ThemeColor.Accent) : null;
                button.BorderThickness = listening == control ? new Thickness(2) : new Thickness(1);
            }
        }
        void EndCapture(string text) { listening = null; capture = null; message.Text = text; RefreshBindings(); }
        void ClearBinding(PadControl control)
        {
            if (Keyboard()) draft.Keys[control] = Key.None;
            else draft.GamepadBindings[control] = new();
            EndCapture($"Cleared {control}. Other device bindings are preserved.");
        }
        Border Group(string name, (string Label, PadControl Control)[] controls)
        {
            var body = new StackPanel { Spacing = 3 };
            body.Children.Add(new TextBlock { Text = name, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 5) });
            foreach (var (label, control) in controls)
            {
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("70,*") };
                row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, FontSize = 12 });
                var button = new Button { HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center, MinHeight = 26, Padding = new Thickness(6, 2), FontSize = 12 };
                ToolTip.SetTip(button, "Click to bind. Right-click to clear. Escape cancels capture.");
                button.Click += (_, _) =>
                {
                    listening = control; capture = Keyboard() ? null : new(); deadline = DateTime.UtcNow.AddSeconds(10);
                    message.Text = Keyboard() ? $"Press a key for {label}. Escape cancels; Backspace clears."
                        : $"Release all controls, then press or move the control for {label}. Escape cancels.";
                    RefreshBindings();
                };
                var clear = new MenuItem { Header = "Clear binding" }; clear.Click += (_, _) => ClearBinding(control);
                button.ContextMenu = new ContextMenu { ItemsSource = new[] { clear } };
                mappingButtons.Add(control, button); Grid.SetColumn(button, 1); row.Children.Add(button); body.Children.Add(row);
            }
            return new Border { Child = body, Padding = new Thickness(10), Margin = new Thickness(0, 0, 10, 10), BorderBrush = StudioTheme.Brush(ThemeColor.Border), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4) };
        }
        var groups = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*"), RowDefinitions = new RowDefinitions("Auto,Auto") };
        void Place(Control control, int column, int row) { Grid.SetColumn(control, column); Grid.SetRow(control, row); groups.Children.Add(control); }
        Place(Group("Buttons", [("A", PadControl.A), ("B", PadControl.B), ("X", PadControl.X), ("Y", PadControl.Y), ("Z", PadControl.Z), ("Start", PadControl.Start)]), 0, 0);
        Place(Group("Main stick", [("Up", PadControl.StickUp), ("Down", PadControl.StickDown), ("Left", PadControl.StickLeft), ("Right", PadControl.StickRight)]), 1, 0);
        Place(Group("C-stick", [("Up", PadControl.CUp), ("Down", PadControl.CDown), ("Left", PadControl.CLeft), ("Right", PadControl.CRight)]), 2, 0);
        Place(Group("D-pad", [("Up", PadControl.Up), ("Down", PadControl.Down), ("Left", PadControl.Left), ("Right", PadControl.Right)]), 0, 1);
        Place(Group("Triggers", [("L click", PadControl.L), ("R click", PadControl.R), ("L analog", PadControl.AnalogL), ("R analog", PadControl.AnalogR)]), 1, 1);
        var deadZone = new NumericUpDown { Minimum = 0, Maximum = 95, Value = _settings.DeadZonePercent, Width = 110, FormatString = "0", Padding = new Thickness(5, 2) };
        var triggerClick = new NumericUpDown { Minimum = 1, Maximum = 100, Value = _settings.TriggerClickPercent, Width = 110, FormatString = "0", Padding = new Thickness(5, 2) };
        var invertMain = new CheckBox { Content = "Invert main stick Y", IsChecked = _settings.InvertStickY, FontSize = 12 };
        var invertC = new CheckBox { Content = "Invert C-stick Y", IsChecked = _settings.InvertCStickY, FontSize = 12 };
        var calibration = new StackPanel { Spacing = 5 };
        calibration.Children.Add(new TextBlock { Text = "Gamepad range", FontWeight = FontWeight.SemiBold });
        calibration.Children.Add(Row(new TextBlock { Text = "Dead zone %", Width = 100, FontSize = 12, VerticalAlignment = VerticalAlignment.Center }, deadZone));
        calibration.Children.Add(Row(new TextBlock { Text = "Trigger click %", Width = 100, FontSize = 12, VerticalAlignment = VerticalAlignment.Center }, triggerClick));
        calibration.Children.Add(invertMain); calibration.Children.Add(invertC);
        Place(new Border { Child = calibration, Padding = new Thickness(12), Margin = new Thickness(0, 0, 10, 10), BorderBrush = StudioTheme.Brush(ThemeColor.Border), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4) }, 2, 1);
        Grid.SetRow(groups, 1); layout.Children.Add(groups);
        var footer = new StackPanel { Spacing = 8 };
        footer.Children.Add(message);
        footer.Children.Add(new TextBlock { Text = "Keyboard stays available with XInput. Shift/Ctrl halves main/C-stick range. Ports 2–4, rumble and native GameCube adapters are unavailable.", FontSize = 11, Foreground = StudioTheme.Brush(ThemeColor.Muted), TextWrapping = TextWrapping.Wrap });
        var actions = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"), Margin = new Thickness(0, 4, 0, 0) };
        var defaults = new Button { Content = "Reset device defaults" };
        defaults.Click += (_, _) =>
        {
            if (Keyboard()) draft.Keys = AppSettings.DefaultKeys();
            else { draft.GamepadButtons = AppSettings.DefaultGamepadButtons(); draft.GamepadBindings.Clear(); }
            EndCapture("Default bindings restored for the selected device. Save to apply.");
        };
        var cancel = new Button { Content = "Cancel", Margin = new Thickness(0, 0, 8, 0) }; cancel.Click += (_, _) => dialog.Close();
        var save = new Button { Content = "Save", MinWidth = 80 };
        save.Click += (_, _) =>
        {
            if (listening != null) { EndCapture("Capture cancelled. Save again to apply the completed bindings."); return; }
            _settings.Keys = new(draft.Keys); _settings.GamepadButtons = new(draft.GamepadButtons); _settings.GamepadBindings = new(draft.GamepadBindings);
            _settings.GamepadIndex = devices.SelectedIndex - 1;
            _settings.DeadZonePercent = (int)(deadZone.Value ?? AppSettings.DefaultDeadZone);
            _settings.TriggerClickPercent = (int)(triggerClick.Value ?? AppSettings.DefaultTriggerThreshold);
            _settings.InvertStickY = invertMain.IsChecked == true; _settings.InvertCStickY = invertC.IsChecked == true;
            try { _settings.Save(); _input.Configure(_settings); dialog.Close(); }
            catch (Exception ex) { message.Text = $"Could not save settings: {ex.Message}"; }
        };
        actions.Children.Add(defaults); Grid.SetColumn(cancel, 2); actions.Children.Add(cancel); Grid.SetColumn(save, 3); actions.Children.Add(save);
        footer.Children.Add(actions); Grid.SetRow(footer, 2); layout.Children.Add(footer); dialog.Content = layout;
        devices.SelectionChanged += (_, _) => EndCapture("Click a binding to capture input from the selected device.");
        dialog.AddHandler(InputElement.KeyDownEvent, (_, args) =>
        {
            if (listening is not { } control) return;
            args.Handled = true;
            if (args.Key == Key.Escape) { EndCapture("Capture cancelled; previous binding kept."); return; }
            if (args.Key == Key.Back) { ClearBinding(control); return; }
            if (Keyboard() && args.Key != Key.None) { draft.Keys[control] = args.Key; EndCapture($"Bound {control} to {args.Key}."); }
        }, RoutingStrategies.Tunnel);
        RefreshBindings();
        var poll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        poll.Tick += (_, _) =>
        {
            var index = devices.SelectedIndex - 1;
            var sample = index < 0 ? null : _input.ReadDevice(index);
            deviceStatus.Text = index < 0 ? "Keyboard selected · gamepad input disabled" : sample!.Connected
                ? $"Connected · {sample.State.Buttons} · LT {sample.State.LeftTrigger} / RT {sample.State.RightTrigger}"
                : "Disconnected · connect an XInput-compatible controller";
            calibration.IsEnabled = !Keyboard();
            if (listening == null) return;
            if (DateTime.UtcNow >= deadline) { EndCapture("Capture timed out; previous binding kept."); return; }
            if (capture != null && sample != null)
            {
                if (capture.Observe(sample) is { } binding)
                {
                    var control = listening.Value; draft.GamepadBindings[control] = binding; EndCapture($"Bound {control} to {binding}.");
                }
                else message.Text = !sample.Connected ? "Connect the selected controller, or press Escape to cancel."
                    : capture.Ready ? "Listening… press a button, move a stick, or squeeze a trigger."
                    : "Release the controller buttons, sticks and triggers before binding.";
            }
        };
        _dialogOpen = true; _input.SetAcceptInput(false); poll.Start();
        if (capturePath != null)
            dialog.Opened += async (_, _) =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500));
                using var captureImage = new RenderTargetBitmap(new PixelSize((int)dialog.Bounds.Width, (int)dialog.Bounds.Height));
                captureImage.Render(dialog);
                var path = Path.GetFullPath(capturePath); Directory.CreateDirectory(Path.GetDirectoryName(path)!); captureImage.Save(path); dialog.Close();
            };
        try { await dialog.ShowDialog(this); }
        finally { poll.Stop(); _dialogOpen = false; _input.Clear(); }
    }
}
