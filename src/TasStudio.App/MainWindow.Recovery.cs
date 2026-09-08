using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using TasStudio.Emulation;

namespace TasStudio.App;

public sealed partial class MainWindow
{
    private readonly DispatcherTimer _recoveryTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private static string RecoveryRoot => Path.Combine(AppPaths.Data, "Recovery");
    private string _recoveryPath = NewRecoveryPath();
    private long _recoveryRevision = -1;
    private Task? _recoveryWrite;
    private readonly SemaphoreSlim _recoveryGate = new(1, 1);
    private FileStream? _recoveryMarker;
    private DateTimeOffset? _recoverySaved;
    private string? _recoveryFailure;
    private readonly TextBlock _recoveryStatus = new() { FontSize = 11, TextWrapping = Avalonia.Media.TextWrapping.Wrap };

    private static string NewRecoveryPath() => Path.Combine(RecoveryRoot, Guid.NewGuid().ToString("N"), "recovery.tasproj");
    private void InitializeRecovery()
    {
        _recoveryTimer.Tick += async (_, _) =>
        {
            if (!_busy && !_dialogOpen && _execution.HasProject && _execution.Revision != _recoveryRevision)
                await SaveRecoveryNow();
        };
        Opened += (_, _) => _recoveryTimer.Start();
        Closed += (_, _) => { _recoveryTimer.Stop(); CloseRecoveryMarker(); };
    }
    private async Task BeginRecoverySession()
    {
        await _recoveryGate.WaitAsync();
        try
        {
            CloseRecoveryMarker();
            _recoveryPath = NewRecoveryPath(); _recoveryRevision = -1; _recoverySaved = null; _recoveryFailure = null;
            Directory.CreateDirectory(Path.GetDirectoryName(_recoveryPath)!);
            _recoveryMarker = new FileStream(Path.Combine(Path.GetDirectoryName(_recoveryPath)!, "session.open"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            _recoveryMarker.Flush(true);
            await SaveRecoveryCore();
        }
        finally { _recoveryGate.Release(); }
    }
    private void CloseRecoveryMarker()
    {
        if (_recoveryMarker == null) return;
        var path = _recoveryMarker.Name; _recoveryMarker.Dispose(); _recoveryMarker = null;
        try { File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
    private async Task SaveRecoveryNow()
    {
        await _recoveryGate.WaitAsync();
        try { await SaveRecoveryCore(); }
        finally { _recoveryGate.Release(); }
    }
    private async Task SaveRecoveryCore()
    {
        try { await FlushInputEditsAsync(); }
        catch (Exception ex) { _recoveryFailure = ex.Message; RefreshRecoveryStatus(); return; }
        if (_recoveryWrite != null) await _recoveryWrite;
        if (!_execution.HasProject || _execution.Revision == _recoveryRevision) return;
        var path = _recoveryPath;
        async Task Write()
        {
            try
            {
                var revision = await _execution.SaveRecoveryAsync(path);
                if (!_watchSaveBlocked) _watchDocument.Save(path + ".watches.json");
                if (path == _recoveryPath) { _recoveryRevision = revision; _recoverySaved = DateTimeOffset.Now; _recoveryFailure = null; }
            }
            catch (Exception ex) { if (path == _recoveryPath) _recoveryFailure = ex.Message; }
            RefreshRecoveryStatus();
        }
        var pending = _recoveryWrite = Write();
        await pending;
        if (_recoveryWrite == pending) _recoveryWrite = null;
    }
    private void RefreshRecoveryStatus()
    {
        _recoveryStatus.Text = !_execution.HasProject ? "" : _recoveryFailure != null ? "Recovery backup failed: " + _recoveryFailure :
            _recoverySaved is { } saved ? $"Recovery saved {saved:HH:mm:ss} · every 5 seconds" : "Preparing recovery backup…";
        ToolTip.SetTip(_projectStatus, _recoveryStatus.Text);
        if (_recoveryFailure != null) _status.Text = _recoveryStatus.Text;
    }
    private static (string Path, string Label)[] RecoveryEntries()
    {
        if (!Directory.Exists(RecoveryRoot)) return [];
        var entries = new List<(string Path, string Label)>();
        foreach (var path in Directory.EnumerateFiles(RecoveryRoot, "recovery.tasproj", SearchOption.AllDirectories).OrderByDescending(File.GetLastWriteTimeUtc))
        {
            try
            {
                var manifest = JsonSerializer.Deserialize<ProjectManifest>(File.ReadAllText(path));
                if (manifest?.Environment == null) continue;
                entries.Add((path, $"{Path.GetFileNameWithoutExtension(manifest.Environment.GamePath)} · {File.GetLastWriteTime(path):g}"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        }
        return entries.ToArray();
    }
    private async Task OfferRecovery()
    {
        bool Unclean(string path)
        {
            var marker = Path.Combine(Path.GetDirectoryName(path)!, "session.open");
            if (!File.Exists(marker)) return false;
            try { using var file = new FileStream(marker, FileMode.Open, FileAccess.Read, FileShare.None); return true; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
        }
        if (!RecoveryEntries().Any(e => Unclean(e.Path))) return;
        if (await Choice("Input recovery available", "Local input backups are available. Review them to recover a timeline, or continue normally.", "Review backups", "Continue") == "Review backups")
            await RecoverInputs();
    }
    private async Task RecoverInputs()
    {
        await _execution.PauseAsync(); _audio.Flush();
        var entries = RecoveryEntries();
        if (entries.Length == 0) { await ShowMessage("Recover inputs", "No input recovery backups have been saved yet."); return; }
        var dialog = new Window { Title = "Recover inputs", Width = 650, Height = 440, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var list = new ListBox { ItemsSource = entries.Select(e => e.Label).ToArray(), SelectedIndex = 0 };
        var open = new Button { Content = "Recover as unsaved project" };
        open.Click += (_, _) => { if (list.SelectedIndex >= 0) dialog.Close(entries[list.SelectedIndex].Path); };
        var cancel = new Button { Content = "Cancel" }; cancel.Click += (_, _) => dialog.Close(null);
        var body = new DockPanel { Margin = new Thickness(18) };
        var note = new TextBlock { Text = "Backups include inputs, events, candidate takes and their starting state. Recovery preserves your saved project; Save As chooses a destination.", TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new Thickness(0,0,0,12) };
        DockPanel.SetDock(note, Avalonia.Controls.Dock.Top); body.Children.Add(note);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Margin = new Thickness(0,12,0,0), Children = { cancel, open } };
        DockPanel.SetDock(actions, Avalonia.Controls.Dock.Bottom); body.Children.Add(actions); body.Children.Add(list); dialog.Content = body;
        string? selected;
        _dialogOpen = true;
        try { selected = await dialog.ShowDialog<string?>(this); }
        finally { _dialogOpen = false; }
        if (selected == null) return;
        await OpenProjectPath(selected);
        if (_projectPath == selected)
        {
            _projectPath = null; _dirty = true;
            DetachWatchDocument();
            try { File.Delete(Path.Combine(Path.GetDirectoryName(selected)!, "session.open")); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            _status.Text = "Inputs recovered. Save inputs to choose a project destination.";
        }
    }
}
