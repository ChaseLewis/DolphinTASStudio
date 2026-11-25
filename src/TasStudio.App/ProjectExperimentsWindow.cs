using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using TasStudio.Emulation;

namespace TasStudio.App;

internal sealed class ProjectExperimentsWindow : Window
{
    internal ProjectExperimentsWindow(string project, Func<IReadOnlyList<StateMarker>> states,
        Func<StateMarker, string, Task> attach, Func<ProjectExperiment, Task> rename,
        Action<ProjectExperiment> showPlays, Func<Task> changed, Func<ProjectExperiment, Task>? configureBatch = null, Func<Task>? cleanStorage = null)
    {
        project = Path.GetFullPath(project);
        Title = "All project experiments"; Width = 900; Height = 640; MinWidth = 760; MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        string Binding(ProjectExperiment experiment)
        {
            if (experiment.Error != null) return "Configuration needs repair";
            if (experiment.WasRemoved) return "Detached by an older version — restore or attach to a state";
            if (experiment.StateId == null) return "No state attachment";
            if (!string.Equals(experiment.SourceProject, project, StringComparison.OrdinalIgnoreCase)) return "Attached to another project";
            var state = states().FirstOrDefault(s => s.Id == experiment.StateId);
            return state == null ? "Original state is no longer in this project"
                : $"{state.Name} · group {state.Position}" + (state.Valid ? "" : " · state is invalid");
        }
        var list = new ListBox { Name = "ProjectExperimentList", ItemTemplate = new FuncDataTemplate<ProjectExperiment>((item, _) =>
            item == null ? null : new StackPanel { Spacing = 3, Margin = new Thickness(4), Children =
            {
                new TextBlock { Text = item.Name, FontWeight = FontWeight.SemiBold },
                new TextBlock { Text = (item.IsDeleted ? "Deleted · " : item.IsHidden ? "Hidden · " : "") + Binding(item),
                    Foreground = StudioTheme.Brush(ThemeColor.Muted), TextWrapping = TextWrapping.Wrap }
            } }) };
        var target = new ComboBox { Name = "ProjectExperimentState", PlaceholderText = "Choose a state to attach to",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemTemplate = new FuncDataTemplate<StateMarker>((state, _) => new TextBlock { Text = state == null ? "" : $"{state.Name} · group {state.Position}" }) };
        var refresh = new Button { Content = "Refresh", Name = "RefreshProjectExperiments" };
        var attachButton = new Button { Content = "Attach to state", Name = "AttachProjectExperiment" };
        var renameButton = new Button { Content = "Rename…", Name = "RenameProjectExperiment" };
        var batchSettings = new Button { Content = "Batch settings…", Name = "ConfigureProjectExperimentBatch", IsVisible = configureBatch != null };
        var plays = new Button { Content = "View top plays", Name = "ViewProjectExperimentBatches" };
        var folder = new Button { Content = "Open folder", Name = "OpenProjectExperimentFolder" };
        var hide = new Button { Content = "Hide", Name = "HideProjectExperiment" };
        var delete = new Button { Content = "Delete from project…", Name = "DeleteProjectExperiment" };
        var restore = new Button { Content = "Restore", Name = "RestoreProjectExperiment" };
        var clean = new Button { Content = "Clean experiment storage…", Name = "CleanProjectExperimentStates", IsVisible = cleanStorage != null };
        var detail = new TextBlock { Name = "ProjectExperimentDetail", TextWrapping = TextWrapping.Wrap, FontSize = 11 };
        var status = new TextBlock { Name = "ProjectExperimentStatus", TextWrapping = TextWrapping.Wrap };
        var busy = false;
        void Update()
        {
            var selected = list.SelectedItem as ProjectExperiment;
            refresh.IsEnabled = list.IsEnabled = target.IsEnabled = !busy;
            clean.IsEnabled = !busy;
            folder.IsEnabled = plays.IsEnabled = !busy && selected != null;
            hide.IsEnabled = delete.IsEnabled = !busy && selected is { IsDeleted: false };
            renameButton.IsEnabled = !busy && selected is { IsDeleted: false };
            batchSettings.IsEnabled = !busy && selected is { IsDeleted: false, WasRemoved: false, Error: null };
            attachButton.IsEnabled = !busy && selected is { IsVisible: true, Error: null } && target.SelectedItem is StateMarker;
            restore.IsEnabled = !busy && selected != null && (selected.IsDeleted || selected.WasRemoved);
            hide.Content = selected?.IsHidden == true ? "Unhide" : "Hide";
            detail.Text = selected == null ? "Select an experiment to view its runs or manage it."
                : selected.ConfigPath + (selected.Error == null ? "" : "\n" + selected.Error);
        }
        async Task Read()
        {
            var path = (list.SelectedItem as ProjectExperiment)?.ConfigPath;
            var stateId = (target.SelectedItem as StateMarker)?.Id;
            var experiments = await Task.Run(() => ProjectExperiments.Discover(project));
            list.ItemsSource = experiments;
            list.SelectedItem = experiments.FirstOrDefault(e => e.ConfigPath == path) ?? experiments.FirstOrDefault();
            var choices = states().Where(s => s.Valid).OrderBy(s => s.Position).ToArray();
            target.ItemsSource = choices;
            target.SelectedItem = choices.FirstOrDefault(s => s.Id == stateId);
            status.Text = $"{experiments.Count} experiments · {experiments.Count(e => e.IsHidden && !e.IsDeleted)} hidden · {experiments.Count(e => e.IsDeleted)} deleted. All are listed above.";
        }
        async Task Perform(Func<Task> action)
        {
            if (busy) return;
            busy = true; Update();
            try { await action(); await Read(); await changed(); }
            catch (Exception ex) { status.Text = ex.Message; }
            finally { busy = false; Update(); }
        }
        Task Manage(Action<ProjectExperiment> action) => Perform(() =>
        {
            if (list.SelectedItem is ProjectExperiment selected) action(selected);
            return Task.CompletedTask;
        });
        list.SelectionChanged += (_, _) => Update();
        target.SelectionChanged += (_, _) => Update();
        refresh.Click += async (_, _) => await Perform(() => Task.CompletedTask);
        clean.Click += async (_, _) => { if (cleanStorage != null) await Perform(cleanStorage); };
        attachButton.Click += async (_, _) => await Perform(async () =>
        {
            if (list.SelectedItem is ProjectExperiment selected && target.SelectedItem is StateMarker state)
                await attach(state, selected.ConfigPath);
        });
        renameButton.Click += async (_, _) => await Perform(async () =>
        {
            if (list.SelectedItem is ProjectExperiment selected) await rename(selected);
        });
        batchSettings.Click += async (_, _) => await Perform(async () =>
        {
            if (list.SelectedItem is ProjectExperiment selected && configureBatch != null) await configureBatch(selected);
        });
        plays.Click += async (_, _) => await Manage(showPlays);
        folder.Click += async (_, _) => await Manage(e => MainWindow.OpenExperimentFolder(Path.GetDirectoryName(e.ConfigPath)!));
        hide.Click += async (_, _) => await Manage(e => ProjectExperiments.SetHidden(project, e.ConfigPath, !e.IsHidden));
        restore.Click += async (_, _) => await Manage(e => ProjectExperiments.Restore(project, e.ConfigPath));
        delete.Click += async (_, _) => await Perform(async () =>
        {
            if (list.SelectedItem is ProjectExperiment selected && await ConfirmDelete(selected.Name))
                ProjectExperiments.DeleteFromProject(project, selected.ConfigPath);
        });
        var binding = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 10 };
        binding.Children.Add(target); Grid.SetColumn(attachButton, 1); binding.Children.Add(attachButton);
        var actions = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var button in new[] { refresh, renameButton, batchSettings, plays, folder, hide, delete, restore, clean })
        { button.Margin = new Thickness(0, 0, 8, 8); actions.Children.Add(button); }
        var root = new Grid { Margin = new Thickness(16), RowDefinitions = new("Auto,*,Auto,Auto,Auto,Auto"), RowSpacing = 12 };
        Control[] rows = [new TextBlock { Text = "Every project experiment is listed here, including detached, hidden and deleted experiments. Hide and Delete remove experiments from state menus; Restore brings them back. Code and run history are kept.", TextWrapping = TextWrapping.Wrap },
            list, detail, binding, actions, status];
        for (var i = 0; i < rows.Length; i++) { Grid.SetRow(rows[i], i); root.Children.Add(rows[i]); }
        Content = root; Update();
        Opened += async (_, _) => await Perform(() => Task.CompletedTask);
    }

    private Task<bool> ConfirmDelete(string name)
    {
        var dialog = new Window { Title = "Delete experiment from project", Width = 470, SizeToContent = SizeToContent.Height,
            CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var cancel = new Button { Content = "Cancel", Name = "CancelDeleteExperiment", IsCancel = true, IsDefault = true };
        var delete = new Button { Content = "Delete from project", Name = "ConfirmDeleteExperiment" };
        cancel.Click += (_, _) => dialog.Close(false); delete.Click += (_, _) => dialog.Close(true);
        dialog.Content = new StackPanel { Margin = new Thickness(20), Spacing = 16, Children =
        {
            new TextBlock { Text = $"Delete {name} from the project? It will leave the state menus. Its code and run history will be kept, and you can restore it from All project experiments.", TextWrapping = TextWrapping.Wrap },
            new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
                Spacing = 10, Children = { cancel, delete } }
        } };
        return dialog.ShowDialog<bool>(this);
    }
}
