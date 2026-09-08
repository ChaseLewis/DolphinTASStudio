using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using TasStudio.Core;
using TasStudio.Dolphin;
using TasStudio.Emulation;

namespace TasStudio.App;

public sealed partial class MainWindow : Window
{
    private const string ApplicationTitle = "Dolphin TAS Studio";
    private const int BytesPerPixel = 4;
    private const int GameCubeDisplayWidth = 640;
    private const int GameCubeDisplayHeight = 480;
    private static readonly TimeSpan UiRefreshInterval = TimeSpan.FromMilliseconds(33);
    private readonly ExecutionService _execution;
    private readonly LiveInputSource _input;
    private readonly AudioOutput _audio = new();
    private readonly AppSettings _settings;
    private readonly DispatcherTimer _timer = new() { Interval = UiRefreshInterval };
    private readonly Image _viewport = new() { Stretch = Stretch.Fill };
    private readonly TextBlock _empty = new() { Text = "Open a GameCube game to begin", FontSize = 22, Foreground = Brushes.Gray, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _status = new() { Text = "Ready", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _position = new() { FontFamily = FontFamily.Parse("Consolas"), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _inputStatus = new() { FontFamily = FontFamily.Parse("Consolas"), FontSize = 11, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _projectStatus = new() { Text = "No project • create a project to record inputs", TextWrapping = TextWrapping.Wrap };
    private readonly TimelineView _timeline = new() { VerticalAlignment = VerticalAlignment.Top };
    private readonly Button _cancelSeek = new() { Content = "Cancel seek", IsVisible = false };
    private readonly Dictionary<PadButtons, CheckBox> _buttons = [];
    private readonly NumericUpDown[] _axes = Enumerable.Range(0, 6).Select(_ => new NumericUpDown { Minimum = byte.MinValue, Maximum = byte.MaxValue, Width = 110, FormatString = "0" }).ToArray();
    private readonly List<(Control Control, Func<bool> Enabled)> _availability = [];
    private VideoFrame? _pendingVideo;
    private WriteableBitmap? _bitmap;
    private string? _projectPath;
    private bool _busy, _dirty, _closingApproved, _seeking;
    private int _selectedInput = -1;
    private long _savedRevision;

    public MainWindow(string[] args, AppSettings? settings = null, ExecutionService? execution = null, LiveInputSource? input = null)
    {
        _input = input ?? new();
        _execution = execution ?? new(new DolphinBackend());
        string? warning = null;
        _settings = settings ?? AppSettings.Load(out warning);
        Title = ApplicationTitle;
        Width = 1360; Height = 920; MinWidth = 1060; MinHeight = 720;
        Background = Brush.Parse("#15191F");
        _layoutPath = LayoutPathFromArgs(args);
        Content = BuildLayout();
        InitializeRecovery();
        _input.Configure(_settings);
        _execution.LiveInput = () => _turboInput.Apply(_input.Read(), _execution.Position);
        _execution.VideoReady += frame => Interlocked.Exchange(ref _pendingVideo, frame);
        _execution.AudioReady += _audio.Add;
        _execution.StatusChanged += message => Dispatcher.UIThread.Post(() => _status.Text = message);
        _audio.Failed += message => Dispatcher.UIThread.Post(() => _status.Text = message);
        _audio.SetVolume(_settings.Volume, _settings.Muted);
        _timer.Tick += (_, _) => Refresh();
        AttachInputEvents(this);
        Closing += OnClosing;
        Closed += (_, _) => { _timer.Stop(); CloseDockWorkspace(); _input.Dispose(); _execution.Dispose(); _audio.Dispose(); _bitmap?.Dispose(); };
        Closed += (_, _) => { foreach (var banner in _banners.Values) banner.Dispose(); };
        Opened += async (_, _) =>
        {
            _timer.Start();
            if (_homeVisible) SetProjectHome(true);
            await LoadProjectBanners();
            if (warning != null) _status.Text = warning;
            if (args.Length == 0) await Perform(OfferRecovery);
            var romIndex = Array.IndexOf(args, "--rom");
            if (romIndex >= 0 && romIndex + 1 < args.Length) await Perform(() => OpenGamePath(args[romIndex + 1]));
            var projectIndex = Array.IndexOf(args, "--project");
            if (projectIndex >= 0 && projectIndex + 1 < args.Length) await Perform(() => OpenProjectPath(args[projectIndex + 1]));
            if (args.Contains("--autoplay") && _execution.IsLoaded) await Perform(() => _execution.RunAsync());
            var settingsCaptureIndex = Array.IndexOf(args, "--settings-screenshot");
            if (settingsCaptureIndex >= 0 && settingsCaptureIndex + 1 < args.Length)
            {
                await Perform(() => ApplicationSettingsCore(args[settingsCaptureIndex + 1]));
                if (args.Contains("--exit-after-capture")) { _closingApproved = true; Close(); return; }
            }
            var controllerCaptureIndex = Array.IndexOf(args, "--controller-screenshot");
            if (controllerCaptureIndex >= 0 && controllerCaptureIndex + 1 < args.Length)
            {
                await Perform(() => ControllerSettingsCore(args[controllerCaptureIndex + 1]));
                if (args.Contains("--exit-after-capture")) { _closingApproved = true; Close(); return; }
            }
            var captureIndex = Array.IndexOf(args, "--screenshot");
            if (captureIndex >= 0 && captureIndex + 1 < args.Length)
            {
                await Task.Delay(TimeSpan.FromSeconds(5));
                await Perform(async () =>
                {
                    await _execution.PauseAsync(); _audio.Flush();
                });
                var path = Path.GetFullPath(args[captureIndex + 1]);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using var screenshot = new RenderTargetBitmap(new PixelSize((int)Bounds.Width, (int)Bounds.Height));
                screenshot.Render(this); screenshot.Save(path);
                if (args.Contains("--exit-after-capture")) { _closingApproved = true; Close(); }
            }
        };
    }

    private BackendOptions BackendOptions() => new(AppPaths.Core, AppPaths.System, AppPaths.DolphinUser)
        { Configuration = new EmulationConfiguration() };


    private void Refresh()
    {
        var frame = Interlocked.Exchange(ref _pendingVideo, null);
        if (frame != null) ShowFrame(frame);
        _empty.IsVisible = !_execution.IsLoaded;
        _viewport.IsVisible = _execution.IsLoaded;
        _position.Text = $"{(_execution.IsRunning ? "RUNNING" : _execution.IsLoaded ? "PAUSED" : "STOPPED")}   •   {PlaybackPositionText}";
        var input = _input.Read();
        _inputStatus.Text = $"Port 1 · {_input.DeviceStatus}\n{input.Buttons}   Stick {input.StickX},{input.StickY}   C {input.CStickX},{input.CStickY}   L/R {input.TriggerL}/{input.TriggerR}";
        if (_execution.HasProject && _execution.Revision != _savedRevision) _dirty = true;
        _projectStatus.Text = _execution.HasProject ? $"{(_projectPath == null ? "Untitled project" : Path.GetFileName(_projectPath))}{(_dirty ? " • unsaved" : "")}   |   {_execution.PollBoundaries[^1]:N0} polls · {_execution.Inputs.Count:N0} frame groups" : "No project • create a project to record inputs";
        Title = $"{ApplicationTitle}{(_execution.GamePath is { } game ? " — " + Path.GetFileNameWithoutExtension(game) : "")}{(_dirty ? " *" : "")}";
        foreach (var (control, enabled) in _availability) control.IsEnabled = !_busy && enabled();
        RefreshTimeline();
        RefreshInputEditorStatus();
        RefreshRecoveryStatus();
        RefreshWatcherStatus();
        _position.Text += _execution.IsPreviewCurrent ? "" : "  • Preview predates edit — seek to update";
    }

    private unsafe void ShowFrame(VideoFrame frame)
    {
        if (_bitmap == null || _bitmap.PixelSize.Width != frame.Width || _bitmap.PixelSize.Height != frame.Height)
        {
            _viewport.Source = null;
            _bitmap?.Dispose();
            _bitmap = new(new PixelSize(frame.Width, frame.Height), new Vector(96, 96), PixelFormat.Rgba8888, AlphaFormat.Opaque);
            _viewport.Source = _bitmap;
        }
        using (var buffer = _bitmap.Lock())
        {
            var sourceStride = checked(frame.Width * BytesPerPixel);
            for (var y = 0; y < frame.Height; y++) Marshal.Copy(frame.Rgba, y * sourceStride, buffer.Address + y * buffer.RowBytes, sourceStride);
        }
        _viewport.InvalidateVisual();
    }

    private async Task Perform(Func<Task> action)
    {
        if (_busy) return;
        _busy = true;
        try { await action(); }
        catch (Exception ex) { _status.Text = ex.Message; await ShowMessage("Operation failed", ex.Message); }
        finally { _busy = false; Refresh(); }
    }

    private async void HandleKeyDown(object? sender, KeyEventArgs e)
    {
        if (_homeVisible || _dialogOpen || e.Handled) return;
        if (e.Key == Key.Escape && _advanceHeld)
        {
            e.Handled = true; StopHeldAdvance();
            await _execution.PauseAsync();
            return;
        }
        if (e.Key == Key.F11 && e.KeyModifiers == KeyModifiers.Shift && _execution.IsLoaded)
        { e.Handled = true; StartHeldAdvance(); return; }
        if (e.Key == Key.F11 && e.KeyModifiers == KeyModifiers.None && _execution.IsLoaded)
        { e.Handled = true; if (!_suppressF11UntilRelease) await Perform(Step); return; }
        if (e.Key == Key.F12 && e.KeyModifiers == KeyModifiers.None && _execution.IsLoaded)
        { e.Handled = true; await Perform(StepWithoutMovingSelection); return; }
        if (e.Key == Key.F10 && e.KeyModifiers == KeyModifiers.None && _execution.IsLoaded)
        { e.Handled = true; await Perform(StepNeutral); return; }
        if (IsEditingText(sender as Window ?? this)) return;
        if (TryMoveTimelineCursor(sender as Window ?? this, e)) return;
        if (e.Key == Key.D && e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift))
        { e.Handled = true; ToggleGameWindow(); return; }
        if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.Alt)
        { e.Handled = true; ToggleGameFullscreen(); return; }
        if (e.Key == Key.Escape && GameHostWindow is { WindowState: WindowState.FullScreen })
        { e.Handled = true; GameHostWindow!.WindowState = _gameWindowRestoreState; return; }
        Func<Task>? command = null;
        if (e.Key == Key.O && e.KeyModifiers == KeyModifiers.Control) command = OpenGame;
        else if (e.Key == Key.S && e.KeyModifiers == KeyModifiers.Control && _execution.HasProject) command = SaveProject;
        else if (e.Key == Key.Z && e.KeyModifiers == KeyModifiers.Control && _execution.HasProject) command = _execution.UndoAsync;
        else if (e.Key == Key.Y && e.KeyModifiers == KeyModifiers.Control && _execution.HasProject) command = _execution.RedoAsync;
        else if (e.Key == Key.F11 && e.KeyModifiers == KeyModifiers.None) command = Step;
        else if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None && _execution.IsLoaded) command = PausePlayback;
        else if (e.Key >= Key.F1 && e.Key <= Key.F8 && _execution.IsLoaded && e.KeyModifiers is KeyModifiers.None or KeyModifiers.Shift)
        {
            var slot = (int)e.Key - (int)Key.F1 + 1;
            command = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? () => SaveSlot(slot) : () => LoadSlot(slot);
        }
        if (command != null) { e.Handled = true; await Perform(command); }
        else if (e.KeyModifiers == KeyModifiers.None || e.Key is Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl) _input.KeyDown(e.Key);
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        StopHeldAdvance();
        if (_closingApproved) { SaveDockWorkspace(); return; }
        e.Cancel = true;
        if (_busy) return;
        await Perform(async () =>
        {
            await _execution.PauseAsync();
            _audio.Flush();
            if (!await ResolveUnsaved()) return;
            _settings.Save();
            _closingApproved = true;
            Close();
        });
    }

    private Button ActionButton(string label, Func<Task> action, Func<bool>? enabled = null)
    {
        var button = new Button { Content = label, Margin = new Thickness(0, 0, 6, 0), IsEnabled = enabled?.Invoke() ?? true };
        button.Click += async (_, _) => await Perform(action);
        _availability.Add((button, enabled ?? (() => true)));
        return button;
    }
    private MenuItem ActionMenu(string label, Func<Task> action, Func<bool>? enabled = null)
    {
        var item = new MenuItem { Header = label };
        item.Click += async (_, _) => await Perform(action);
        _availability.Add((item, enabled ?? (() => true)));
        return item;
    }
    private static TextBlock Label(string text) => new() { Text = text, VerticalAlignment = VerticalAlignment.Center };
    private static StackPanel Row(params Control[] controls) => new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 }.WithChildren(controls);
}

internal static class PanelExtensions
{
    public static T WithChildren<T>(this T panel, params Control[] children) where T : Panel
    {
        foreach (var child in children) panel.Children.Add(child);
        return panel;
    }
}
