using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using TasStudio.Emulation;

namespace TasStudio.App;

public sealed partial class MainWindow
{
    private static readonly FilePickerFileType GameFiles = new("GameCube images") { Patterns = ["*.iso", "*.gcm", "*.rvz", "*.ciso", "*.gcz", "*.dol", "*.elf"] };
    private static readonly FilePickerFileType StateFiles = new("TAS Studio state") { Patterns = ["*.tasstate"] };
    private static readonly FilePickerFileType ProjectFiles = new("TAS Studio project / replay") { Patterns = ["*.tasproj", "*.tasreplay"] };
    private static readonly FilePickerFileType ReplayFiles = new("Baked TAS replay") { Patterns = ["*.tasreplay"] };
    private async Task<string?> PickOpen(string title, FilePickerFileType type)
    {
        _dialogOpen = true;
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new() { Title = title, AllowMultiple = false, FileTypeFilter = [type, FilePickerFileTypes.All] });
            return files.Count == 0 ? null : files[0].TryGetLocalPath();
        }
        finally { _dialogOpen = false; }
    }
    private async Task<string?> PickSave(string title, FilePickerFileType type, string name, string extension)
    {
        _dialogOpen = true;
        try
        {
            var file = await StorageProvider.SaveFilePickerAsync(new() { Title = title, FileTypeChoices = [type], SuggestedFileName = name, DefaultExtension = extension, ShowOverwritePrompt = true });
            return file?.TryGetLocalPath();
        }
        finally { _dialogOpen = false; }
    }
    private async Task OpenGame()
    {
        var path = await PickOpen("Open GameCube game", GameFiles);
        if (path == null) return;
        await _execution.PauseAsync(); _audio.Flush();
        if (await ResolveUnsaved()) await OpenGamePath(path);
    }
    private async Task OpenGamePath(string path)
    {
        _status.Text = "Loading game…";
        await WithGameLoading(async () =>
        {
            await _execution.LoadGameAsync(path, BackendOptions());
            await _execution.NewProjectAsync(ProjectStartKind.PowerOn);
        });
        _audio.Flush();
        _settings.LastGame = path; _settings.Save();
        _projectPath = null; _dirty = true;
        DetachWatchDocument();
        await BeginRecoverySession();
        ShowEditor();
    }
    private async Task ToggleRun()
    {
        if (!_execution.IsLoaded) return;
        await FlushInputEditsAsync();
        if (_execution.IsRunning) { await _execution.PauseAsync(); _audio.Flush(); }
        else await _execution.PlayRecordedAsync();
    }
    private async Task PausePlayback() { await _execution.PauseAsync(); _audio.Flush(); }
    private async Task RecordLive() { await FlushInputEditsAsync(); await _execution.RunAsync(); }
    private async Task PreviousState()
    {
        await FlushInputEditsAsync(); await _execution.PauseAsync(); _audio.Flush();
        await _execution.PreviousStateAsync(); SelectPreviewInput();
    }
    private void SelectPreviewInput()
    {
        _timeline.InputCount = _execution.Inputs.Count;
        var frame = checked((int)_execution.Position);
        _timeline.SetSelection(frame, frame + 1); LoadSelectedInput();
    }
    private async Task Step()
    {
        await StepWithEditorInputAsync();
    }
    private async Task StepWithoutMovingSelection()
    {
        if (!_execution.HasProject) return;
        await FlushInputEditsAsync(); _audio.Flush();
        await _execution.StepRecordedFrameAsync();
        LoadSelectedInput();
    }
    private Task StepNeutral() => StepWithEditorInputAsync(blankInput: true);
    private async Task RestartPlayback()
    {
        await FlushInputEditsAsync();
        await _execution.PauseAsync(); _audio.Flush();
        await _execution.StopPlaybackAsync();
        SelectPreviewInput();
        await SaveRecoveryNow();
    }
    private async Task Reset()
    {
        await _execution.PauseAsync(); _audio.Flush();
        await _execution.ResetAsync();
        _dirty = _execution.HasProject;
        _status.Text = "Reset complete. In a project, reset is recorded as an ordered execution event.";
    }
    private async Task SaveStateFile()
    {
        await _execution.PauseAsync(); _audio.Flush();
        var path = await PickSave("Save TAS Studio state", StateFiles, $"state-{_execution.Position}.tasstate", "tasstate");
        if (path != null) { await _execution.SaveStateAsync(path); _status.Text = $"Saved state to {path}"; }
    }
    private async Task LoadStateFile()
    {
        var path = await PickOpen("Load compatible TAS Studio state", StateFiles);
        if (path != null) await RestoreState(path);
    }
    private string SlotPath(int slot)
    {
        var gameName = Path.GetFileNameWithoutExtension(_execution.GamePath ?? "game");
        var identity = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(_execution.GamePath ?? "game").ToUpperInvariant())));
        return Path.Combine(AppPaths.States, identity, $"{gameName}.slot{slot}.tasstate");
    }
    private async Task SaveSlot(int slot)
    {
        await _execution.PauseAsync(); _audio.Flush();
        var path = SlotPath(slot);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await _execution.SaveStateAsync(path);
        _status.Text = $"Saved state in slot {slot}.";
    }
    private async Task LoadSlot(int slot)
    {
        var path = SlotPath(slot);
        if (!File.Exists(path)) { _status.Text = $"Slot {slot} is empty for this game."; return; }
        await RestoreState(path);
    }
    private async Task RestoreState(string path)
    {
        await _execution.PauseAsync(); _audio.Flush();
        await _execution.LoadStateAsync(path);
        if (!_execution.HasProject) { _projectPath = null; _dirty = false; }
        _status.Text = "State restored after compatibility and history validation.";
    }
    private async Task NewProject()
    {
        await _execution.PauseAsync(); _audio.Flush();
        NewProjectRequest? request;
        _dialogOpen = true;
        try { request = await new NewProjectDialog(_execution.GamePath ?? _settings.LastGame).ShowDialog<NewProjectRequest?>(this); }
        finally { _dialogOpen = false; }
        if (request == null) return;
        if (!await ResolveUnsaved()) return;
        _status.Text = "Creating project…";
        var options = BackendOptions() with { Configuration = new EmulationConfiguration { StartUtcSeconds = request.StartUtc } };
        await WithGameLoading(() => _execution.CreateProjectAsync(request.Path, request.Rom, options, request.StatePath));
        _projectPath = request.Path; _savedRevision = _execution.Revision; _dirty = false;
        _settings.LastGame = request.Rom;
        DetachWatchDocument();
        StoreWatchesForProject(request.Path);
        await RememberProject(request.Path);
        await BeginRecoverySession();
        _timeline.SetSelection(0, 1); _timeline.FirstFrame = 0; LoadSelectedInput();
        ShowEditor();
    }
    private async Task OpenProject()
    {
        var path = await PickOpen("Open TAS Studio project", ProjectFiles);
        if (path == null) return;
        await OpenProjectPath(path);
    }
    private async Task OpenProjectPath(string path)
    {
        await _execution.PauseAsync(); _audio.Flush();
        if (!await ResolveUnsaved()) return;
        var content = await Task.Run(() => FolderProject.Load(path));
        var metadata = content.Archive.Metadata;
        var rom = _settings.ResolvedRoms.GetValueOrDefault(metadata.GameHash) ?? FolderProject.ResolveRomPath(path, metadata.GamePath);
        while (true)
        {
            string? hash = null; string? reason = null;
            if (File.Exists(rom))
            {
                try { hash = await HashRomWithProgress(rom); if (hash == null) return; }
                catch (IOException error) { reason = error.Message; }
                catch (UnauthorizedAccessException error) { reason = error.Message; }
                if (hash == metadata.GameHash) break;
                reason ??= "The selected image has a different content hash.";
            }
            else reason = "The referenced ROM could not be found.";
            if (await Choice("Locate project ROM", $"{reason}\n\nExpected: {Path.GetFileName(metadata.GamePath)}\nPrevious path: {rom}\n\nSelect the same game image/revision. Your current session will be preserved if you cancel.", "Browse", "Cancel") != "Browse") return;
            var selected = await PickOpen("Locate matching project ROM", GameFiles); if (selected == null) return; rom = selected;
        }
        await WithGameLoading(() => _execution.LoadProjectAsync(path, BackendOptions(), rom));
        _settings.ResolvedRoms[metadata.GameHash] = rom; _settings.Save();
        _projectPath = content.Legacy ? null : path; _savedRevision = _execution.Revision;
        _dirty = content.Legacy;
        LoadWatchDocument(path + ".watches.json");
        if (content.Legacy) DetachWatchDocument();
        await BeginRecoverySession();
        RestoreProjectTimelineView();
        if (content.Legacy) _status.Text = "Legacy project / replay imported. Save As creates a folder project and preserves the source.";
        await RememberProject(path, content);
        ShowEditor();
    }
    internal void RestoreProjectTimelineView()
    {
        SelectPreviewInput();
        _timeline.VisibleFrames = Math.Clamp(_execution.Inputs.Count + Math.Max(10, _execution.Inputs.Count / 10), 60, 600);
        _timeline.FirstFrame = _execution.Position < (ulong)_timeline.VisibleFrames ? 0 : Math.Max(0, (int)_execution.Position - _timeline.VisibleFrames / 2);
    }
    private async Task SaveProject()
    {
        if (!_execution.HasProject) return;
        await FlushInputEditsAsync();
        if (_projectPath == null) { await SaveProjectAs(); return; }
        await _execution.PauseAsync(); _audio.Flush();
        await _execution.SaveProjectAsync(_projectPath);
        StoreWatchesForProject(_projectPath);
        _dirty = false;
        _savedRevision = _execution.Revision;
        _status.Text = $"Saved project to {_projectPath}";
        await RememberProject(_projectPath);
        await SaveRecoveryNow();
    }
    private async Task SaveProjectAs()
    {
        await FlushInputEditsAsync();
        await _execution.PauseAsync(); _audio.Flush();
        var path = await PickSave("Save TAS Studio project", ProjectFiles, "project.tasproj", "tasproj");
        if (path == null) return;
        if (!File.Exists(path)) path = Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path), Path.GetFileName(path));
        await _execution.SaveProjectAsync(path);
        StoreWatchesForProject(path);
        _projectPath = path; _dirty = false;
        _savedRevision = _execution.Revision;
        _status.Text = $"Saved project to {path}";
        await RememberProject(path);
        await SaveRecoveryNow();
    }
    private async Task<bool> ResolveUnsaved()
    {
        await FlushInputEditsAsync();
        if (!_execution.HasProject || (!_dirty && _execution.Revision == _savedRevision)) return true;
        await SaveRecoveryNow();
        var result = await Choice("Unsaved project", "Save your input timeline before replacing this session?", "Save", "Discard", "Cancel");
        if (result == "Discard") return true;
        if (result != "Save") return false;
        await SaveProject();
        return !_dirty;
    }
    private async Task ShowMessage(string title, string message) => await Choice(title, message, "OK");
    private async Task<string?> Choice(string title, string message, params string[] choices)
    {
        var window = new Window { Title = title, Width = 510, SizeToContent = SizeToContent.Height, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var content = new StackPanel { Margin = new Thickness(24), Spacing = 20 };
        content.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var choice in choices)
        {
            var button = new Button { Content = choice };
            button.Click += (_, _) => window.Close(choice);
            buttons.Children.Add(button);
        }
        content.Children.Add(buttons); window.Content = content;
        _dialogOpen = true;
        try { return await window.ShowDialog<string?>(this); }
        finally { _dialogOpen = false; }
    }
    private async Task ExportReplay()
    {
        var path = await PickSave("Export baked active playback", ReplayFiles, "movie.tasreplay", "tasreplay");
        if (path != null) await _execution.ExportReplayAsync(path);
    }
    private async Task<string?> HashRomWithProgress(string path)
    {
        using var cancellation = new CancellationTokenSource();
        var window = new Window { Title = "Checking ROM identity", Width = 480, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var cancel = new Button { Content = "Cancel" }; cancel.Click += (_, _) => cancellation.Cancel();
        window.Content = new StackPanel { Margin = new Thickness(20), Spacing = 15, Children = { new TextBlock { Text = "Verifying " + Path.GetFileName(path), TextWrapping = TextWrapping.Wrap }, new ProgressBar { IsIndeterminate = true }, cancel } };
        Exception? failure = null;
        window.Opened += async (_, _) =>
        {
            try { var hash = await FolderProject.HashGameAsync(path, cancellation.Token); window.Close(hash); }
            catch (OperationCanceledException) { window.Close(null); }
            catch (Exception error) { failure = error; window.Close(null); }
        };
        window.Closing += (_, _) => cancellation.Cancel(); _dialogOpen = true;
        try { var result = await window.ShowDialog<string?>(this); if (failure != null) throw failure; return result; }
        finally { _dialogOpen = false; }
    }
}
