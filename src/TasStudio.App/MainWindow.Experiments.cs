using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using TasStudio.Emulation;

namespace TasStudio.App;

public sealed partial class MainWindow
{
    private string? _experimentMarkerProject;
    private long _nextExperimentMarkerRefresh;
    private bool _readingExperimentMarkers;
    private static readonly TimeSpan ExperimentMarkerRefreshInterval = TimeSpan.FromSeconds(2);

    private async Task RefreshExperimentMarkerColors(bool force = false)
    {
        var project = _execution.HasProject && _projectPath != null ? Path.GetFullPath(_projectPath) : null;
        if (!string.Equals(project, _experimentMarkerProject, StringComparison.OrdinalIgnoreCase))
        {
            _experimentMarkerProject = project;
            _timeline.ExperimentStateIds = new HashSet<string>();
            _timeline.InvalidateVisual();
            _nextExperimentMarkerRefresh = 0;
        }
        if (force) _nextExperimentMarkerRefresh = 0;
        if (project == null || _readingExperimentMarkers || Environment.TickCount64 < _nextExperimentMarkerRefresh) return;
        _readingExperimentMarkers = true;
        _nextExperimentMarkerRefresh = Environment.TickCount64 + (long)ExperimentMarkerRefreshInterval.TotalMilliseconds;
        try
        {
            var experiments = await Task.Run(() => ProjectExperiments.Discover(project));
            if (!string.Equals(project, _experimentMarkerProject, StringComparison.OrdinalIgnoreCase)) return;
            var states = experiments.Where(e => e.IsVisible && e.Error == null && e.StateId != null &&
                string.Equals(e.SourceProject, project, StringComparison.OrdinalIgnoreCase)).Select(e => e.StateId!).ToHashSet();
            if (!_timeline.ExperimentStateIds.SetEquals(states))
            {
                _timeline.ExperimentStateIds = states;
                _timeline.InvalidateVisual();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (string.Equals(project, _experimentMarkerProject, StringComparison.OrdinalIgnoreCase))
            {
                _timeline.ExperimentStateIds = new HashSet<string>();
                _timeline.InvalidateVisual();
            }
        }
        finally { _readingExperimentMarkers = false; }
    }

    private void PopulateStateExperiments(MenuItem menu)
    {
        var state = _timeline.SelectedMarker;
        menu.IsVisible = state != null;
        menu.IsEnabled = !_busy && state is { Valid: true } && _execution.HasProject;
        if (state == null) { menu.ItemsSource = null; return; }
        MenuItem Action(string label, Func<Task> action)
        {
            var item = new MenuItem { Header = label };
            item.Click += async (_, e) => { e.Handled = true; await Perform(action); };
            return item;
        }
        var items = new List<Control> { Action("Create experiment…", () => CreateStateExperiment(state)),
            Action("All project experiments…", ShowProjectExperiments) };
        var editorItems = new List<MenuItem>();
        try
        {
            var experiments = _projectPath == null ? [] : ProjectExperiments.Discover(_projectPath).Where(e => e.IsVisible).ToArray();
            var attached = experiments.Where(e => e.Error == null && e.StateId == state.Id &&
                _projectPath != null && string.Equals(e.SourceProject, Path.GetFullPath(_projectPath), StringComparison.OrdinalIgnoreCase)).ToArray();
            var attach = new MenuItem { Header = "Attach existing experiment", IsEnabled = experiments.Length > 0 };
            attach.ItemsSource = experiments.Select(experiment =>
            {
                var item = Action(experiment.Name, () => AttachStateExperiment(state, experiment.ConfigPath));
                item.IsEnabled = experiment.Error == null;
                ToolTip.SetTip(item, experiment.Error ?? experiment.ConfigPath);
                return item;
            }).ToArray();
            items.Add(attach);
            var rename = new MenuItem { Header = "Rename experiment…", IsEnabled = experiments.Length > 0 };
            rename.ItemsSource = experiments.Select(experiment =>
            {
                var item = Action(experiment.Name, () => RenameStateExperiment(experiment));
                item.IsEnabled = experiment.Error == null;
                ToolTip.SetTip(item, experiment.Error ?? experiment.ConfigPath);
                return item;
            }).ToArray();
            items.Add(rename);
            var remove = new MenuItem { Header = "Remove experiment from state", IsEnabled = attached.Length > 0 };
            remove.ItemsSource = attached.Select(experiment =>
            {
                var item = Action(experiment.Name, () => RemoveStateExperiment(state, experiment));
                ToolTip.SetTip(item, "Detach from this state; keep the experiment available in Attach existing experiment.\n" + experiment.ConfigPath);
                return item;
            }).ToArray();
            items.Add(remove);
            foreach (var experiment in attached)
            {
                items.Add(new Separator());
                items.Add(Action("Run " + experiment.Name, () => RunStateExperiment(state, experiment.ConfigPath)));
                items.Add(Action("Batch settings — " + experiment.Name, () => ConfigureExperimentBatch(experiment.ConfigPath)));
                items.Add(Action("Top plays settings — " + experiment.Name, () => ConfigureExperimentTopPlays(experiment.ConfigPath)));
                items.Add(Action("View top plays — " + experiment.Name, () =>
                {
                    ShowExperimentPlays(ProjectExperiments.PreviousBatches(experiment.ConfigPath), experiment.Name,
                        () => ProjectExperiments.PreviousBatches(experiment.ConfigPath));
                    return Task.CompletedTask;
                }));
                items.Add(Action("Open Folder — " + experiment.Name, () =>
                {
                    OpenExperimentFolder(Path.GetDirectoryName(experiment.ConfigPath)!);
                    return Task.CompletedTask;
                }));
                var edit = Action("Open in Editor — " + experiment.Name, async () =>
                {
                    var editor = await CodeEditors.ResolveAsync(_settings.CodeEditorPath)
                        ?? throw new InvalidOperationException("Choose a code editor in Configuration → Interface.");
                    CodeEditors.Open(editor, Path.GetDirectoryName(experiment.ConfigPath)!);
                });
                edit.IsVisible = false; editorItems.Add(edit); items.Add(edit);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { items.Add(new MenuItem { Header = "Cannot read experiments: " + ex.Message, IsEnabled = false }); }
        menu.ItemsSource = items;
        _ = RefreshEditorItems(editorItems);
    }

    private async Task RefreshEditorItems(IEnumerable<MenuItem> items)
    {
        var editor = await CodeEditors.ResolveAsync(_settings.CodeEditorPath);
        foreach (var item in items) { item.IsVisible = editor != null; ToolTip.SetTip(item, editor); }
    }

    internal Task ShowProjectExperiments()
    {
        if (_projectPath == null) return Task.CompletedTask;
        var project = _projectPath;
        var window = new ProjectExperimentsWindow(project, () => _execution.StateMarkers,
            async (state, path) =>
            {
                if (_projectPath != project) throw new InvalidOperationException("Reopen the experiment list for the current project.");
                await Perform(() => AttachStateExperiment(state, path));
            }, async experiment =>
            {
                if (_projectPath != project) throw new InvalidOperationException("Reopen the experiment list for the current project.");
                await Perform(() => RenameStateExperiment(experiment));
            },
            experiment => ShowExperimentPlays(ProjectExperiments.PreviousBatches(experiment.ConfigPath), experiment.Name,
                () => ProjectExperiments.PreviousBatches(experiment.ConfigPath)),
            () => RefreshExperimentMarkerColors(force: true), async experiment =>
            {
                if (_projectPath != project) throw new InvalidOperationException("Reopen the experiment list for the current project.");
                await Perform(() => ConfigureExperimentBatch(experiment.ConfigPath));
            }, CleanExperimentStates);
        window.Show(this);
        return Task.CompletedTask;
    }

    private async Task CleanExperimentStates()
    {
        if (_projectPath == null) return;
        var project = _projectPath;
        var window = new ExperimentStorageWindow(project, async () =>
        {
            if (_projectPath != project) throw new InvalidOperationException("Reopen cleanup for the current project.");
            return await _execution.GetProtectedStateFilesAsync();
        });
        await window.ShowDialog(this);
    }

    private async Task<bool> SaveExperimentSource(StateMarker state)
    {
        await FlushInputEditsAsync();
        if (!_execution.StateMarkers.Any(s => s.Id == state.Id && s.Valid))
            throw new InvalidOperationException("The selected state is no longer valid. Select a replacement state.");
        await SaveProject();
        // An untitled project's save picker may have been cancelled.
        if (_projectPath == null) return false;
        if (!_execution.StateMarkers.Any(s => s.Id == state.Id && s.Valid))
            throw new InvalidOperationException("The selected state is no longer valid. Select a replacement state.");
        return true;
    }

    internal async Task<string?> CreateStateExperiment(StateMarker state, string? name = null)
    {
        name ??= await PromptExperimentName("Create experiment", null);
        if (name == null) return null;
        if (!await SaveExperimentSource(state)) return null;
        var libraries = _settings.ExperimentLibraries.ToArray();
        var path = await Task.Run(() => ProjectExperiments.Create(_projectPath!, state.Id, name, libraries));
        await RefreshExperimentMarkerColors(force: true);
        _status.Text = "Created experiment: " + name;
        return path;
    }

    internal async Task RenameStateExperiment(ProjectExperiment experiment)
    {
        if (_projectPath == null) return;
        var name = await PromptExperimentName("Rename experiment", experiment.Name);
        if (name == null) return;
        await Task.Run(() => ProjectExperiments.Rename(_projectPath, experiment.ConfigPath, name));
        _status.Text = "Renamed experiment to " + name;
    }

    internal async Task RemoveStateExperiment(StateMarker state, ProjectExperiment experiment)
    {
        if (_projectPath == null) return;
        await Task.Run(() => ProjectExperiments.Detach(_projectPath, experiment.ConfigPath, state.Id));
        await RefreshExperimentMarkerColors(force: true);
        _status.Text = $"Detached {experiment.Name} from {state.Name}. It remains available in Attach existing experiment.";
    }

    private async Task<string?> PromptExperimentName(string title, string? current)
    {
        var dialog = new Window { Title = title, Width = 430, SizeToContent = SizeToContent.Height,
            CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var name = new TextBox { Name = "ExperimentName", Text = current ?? "", Watermark = "e.g. First boss — fastest win", MaxLength = ProjectExperiments.MaximumNameLength };
        var save = new Button { Name = "SaveExperimentName", Content = current == null ? "Create" : "Rename", IsDefault = true };
        var cancel = new Button { Name = "CancelExperimentName", Content = "Cancel", IsCancel = true };
        void Validate()
        {
            try { ProjectExperiments.ValidateName(name.Text); save.IsEnabled = true; }
            catch (InvalidDataException) { save.IsEnabled = false; }
        }
        name.TextChanged += (_, _) => Validate(); Validate();
        save.Click += (_, _) => dialog.Close(ProjectExperiments.ValidateName(name.Text));
        cancel.Click += (_, _) => dialog.Close();
        dialog.Content = new StackPanel { Margin = new Thickness(18), Spacing = 12, Children =
        {
            new TextBlock { Text = "Experiment name" }, name,
            new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { cancel, save } }
        } };
        dialog.Opened += (_, _) => { name.Focus(); name.SelectAll(); };
        _dialogOpen = true;
        try { return await dialog.ShowDialog<string?>(this); }
        finally { _dialogOpen = false; }
    }

    internal async Task AttachStateExperiment(StateMarker state, string configPath)
    {
        if (!await SaveExperimentSource(state)) return;
        await Task.Run(() => ProjectExperiments.Attach(configPath, _projectPath!, state.Id));
        await RefreshExperimentMarkerColors(force: true);
        _status.Text = $"Experiment attached to {state.Name}. Edit its code/config or run it from this state's Experiment menu.";
    }

    private async Task RunStateExperiment(StateMarker state, string configPath)
    {
        var worker = Path.Combine(AppContext.BaseDirectory, "TasStudio.Worker.exe");
        if (!File.Exists(worker)) throw new FileNotFoundException("Experiment worker is missing. Use a complete published TAS Studio build.", worker);
        if (!await SaveExperimentSource(state)) return;
        var run = await Task.Run(() => ProjectExperiments.PrepareRun(configPath, _projectPath!, state.Id));
        var window = new ExperimentRunWindow(worker, run, () => ShowExperimentPlays([run.BatchDirectory], run.Name));
        window.Show(this);
        _status.Text = "Experiment started: " + run.Name;
    }

    private void ShowExperimentPlays(IEnumerable<string> batches, string? experimentName = null, Func<IEnumerable<string>>? refreshBatches = null)
    {
        ExperimentPlaysWindow? window = null;
        window = new ExperimentPlaysWindow(batches, (batch, summary, asTake) => ApplyExperimentPlay(batch, summary, window!, asTake), experimentName, refreshBatches);
        window.Show(this);
    }

    private async Task<bool> ApplyExperimentPlay(string batch, ExperimentPlaySummary summary, Window owner, bool asTake)
    {
        if (_busy) throw new InvalidOperationException("Wait for the current project operation to finish.");
        _busy = true;
        try
        {
            await FlushInputEditsAsync();
            var play = await Task.Run(() => ExperimentPlayResults.Load(batch, summary.TrialIndex, summary.SubmissionIndex));
            play = play with { ExperimentName = summary.ExperimentName ?? play.ExperimentName };
            if (!asTake && !await ConfirmInputOverwrite(play.Start, null, owner)) return false;
            string? takeId = null;
            async Task Apply(bool allowHistoryMismatch = false)
            {
                if (asTake) takeId = await _execution.ApplyExperimentPlayAsTakeAsync(play, allowHistoryMismatch);
                else await _execution.ApplyExperimentPlayAsync(play, allowHistoryMismatch);
            }
            try { await Apply(); }
            catch (ExperimentPlayHistoryMismatchException)
            {
                var warning = new ExperimentPlayMismatchWindow(play.DisplayName, play.Start, play.Length, asTake);
                if (!await warning.ShowDialog<bool>(owner)) return false;
                await Apply(allowHistoryMismatch: true);
            }
            _audio.Flush();
            _timeline.InputCount = _execution.Inputs.Count;
            _timeline.Takes = _execution.Takes;
            _timeline.SetSelection(play.Start, play.Start + play.Length, takeId);
            _timeline.RevealSelection();
            LoadSelectedInput();
            _status.Text = asTake ? $"Added {play.DisplayName} as a take. Active playback unchanged; Undo removes the take."
                : $"Applied {play.DisplayName} to Active playback. Seek to preview; Undo restores previous inputs.";
            return true;
        }
        finally { _busy = false; Refresh(); }
    }

    internal async Task ConfigureExperimentBatch(string configPath)
    {
        if (_projectPath == null) return;
        var project = _projectPath;
        var current = ExperimentFiles.Read<CSharpExperimentFile>(configPath);
        var name = ProjectExperiments.DisplayName(configPath);
        var count = new NumericUpDown { Name = "ExperimentTrialCount", Minimum = 1, Maximum = int.MaxValue,
            Increment = 1, FormatString = "0", Value = current.Count };
        var workers = new NumericUpDown { Name = "ExperimentWorkerCount", Minimum = 1, Maximum = 8,
            Increment = 1, FormatString = "0", Value = current.Parallelism };
        var note = new TextBlock { Name = "ExperimentWorkerSummary", TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        var save = new Button { Name = "SaveExperimentBatchSettings", Content = "Save", IsDefault = true };
        var cancel = new Button { Name = "CancelExperimentBatchSettings", Content = "Cancel", IsCancel = true };
        var dialog = new Window { Title = name + " — Batch settings", Width = 460, SizeToContent = SizeToContent.Height,
            CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        void Validate()
        {
            save.IsEnabled = count.Value is >= 1 and <= int.MaxValue && workers.Value is >= 1 and <= 8 &&
                count.Value == decimal.Truncate(count.Value.Value) && workers.Value == decimal.Truncate(workers.Value.Value);
            var concurrent = save.IsEnabled ? Math.Min((int)count.Value!, (int)workers.Value!) : 0;
            note.Text = save.IsEnabled ? $"Up to {concurrent} {(concurrent == 1 ? "trial" : "trials")} will run at once. Changes apply to new batches."
                : "Enter a whole-number trial count and 1–8 workers.";
        }
        count.ValueChanged += (_, _) => Validate(); workers.ValueChanged += (_, _) => Validate(); Validate();
        save.Click += (_, _) => dialog.Close(true); cancel.Click += (_, _) => dialog.Close(false);
        dialog.Content = new StackPanel { Margin = new Thickness(18), Spacing = 10, Children =
        {
            new TextBlock { Text = "Total trials" }, count,
            new TextBlock { Text = "Concurrent workers" }, workers, note,
            new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
                Spacing = 8, Children = { cancel, save } }
        } };
        _dialogOpen = true;
        try
        {
            if (await dialog.ShowDialog<bool>(this))
            {
                var trials = (int)count.Value!; var parallelism = (int)workers.Value!;
                await Task.Run(() => ProjectExperiments.SetBatchScheduling(project, configPath, trials, parallelism));
                _status.Text = $"{name}: {trials} trials, up to {parallelism} workers for new batches.";
            }
        }
        finally { _dialogOpen = false; }
    }

    private async Task ConfigureExperimentTopPlays(string configPath)
    {
        var current = ExperimentFiles.Read<CSharpExperimentFile>(configPath).TopPlays;
        var enabled = new CheckBox { Content = "Retain top plays", IsChecked = current != null };
        var field = new TextBox { Text = current?.ScoreField ?? "Score", Watermark = "Result score field" };
        var higher = new CheckBox { Content = "Higher scores are better", IsChecked = current?.HigherIsBetter ?? true };
        var keep = new NumericUpDown { Minimum = 1, Maximum = 1000, Increment = 1, FormatString = "0", Value = current?.Keep ?? 10 };
        var save = new Button { Content = "Save", IsDefault = true };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        var dialog = new Window { Title = "Top plays settings", Width = 460, SizeToContent = SizeToContent.Height,
            CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        void Validate()
        {
            field.IsEnabled = higher.IsEnabled = keep.IsEnabled = enabled.IsChecked == true;
            save.IsEnabled = enabled.IsChecked != true || (!string.IsNullOrWhiteSpace(field.Text) && keep.Value is >= 1 and <= 1000 && keep.Value == decimal.Truncate(keep.Value.Value));
        }
        enabled.IsCheckedChanged += (_, _) => Validate(); field.TextChanged += (_, _) => Validate(); keep.ValueChanged += (_, _) => Validate();
        Validate(); save.Click += (_, _) => dialog.Close(true); cancel.Click += (_, _) => dialog.Close(false);
        dialog.Content = new StackPanel { Margin = new Thickness(18), Spacing = 10, Children =
        {
            enabled, new TextBlock { Text = "Score field in the returned result (used for automatic capture)" }, field, higher,
            new TextBlock { Text = "Number of plays to keep across all trials" }, keep,
            new TextBlock { Text = "Explicit SubmitPlayAsync calls use their supplied score. Changes apply to new batches.", TextWrapping = Avalonia.Media.TextWrapping.Wrap },
            new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { cancel, save } }
        } };
        _dialogOpen = true;
        try
        {
            if (await dialog.ShowDialog<bool>(this))
            {
                ExperimentTopPlays? options = enabled.IsChecked == true ? new(field.Text!.Trim(), higher.IsChecked == true, (int)keep.Value!) : null;
                ProjectExperiments.SetTopPlays(_projectPath!, configPath, options);
            }
        }
        finally { _dialogOpen = false; }
    }

    internal static void OpenExperimentFolder(string directory) =>
        Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
}
