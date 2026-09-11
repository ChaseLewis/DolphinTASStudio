using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using TasStudio.Emulation;

namespace TasStudio.App;

internal sealed class ExperimentPlaysWindow : Window
{
    private sealed record Batch(string Path)
    {
        public override string ToString()
        {
            var directory = new DirectoryInfo(Path);
            return directory.Name.Equals("batch", StringComparison.OrdinalIgnoreCase) && directory.Parent is { Name: not ".runs" } parent
                ? parent.Name + " / " + directory.Name : directory.Name;
        }
    }

    internal ExperimentPlaysWindow(IEnumerable<string> batches, Func<string, ExperimentPlaySummary, Task> apply)
    {
        Title = "Top experiment plays"; Width = 820; Height = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var batch = new ComboBox { Name = "PlayBatch", ItemsSource = batches.Select(p => new Batch(p)).ToArray(), HorizontalAlignment = HorizontalAlignment.Stretch };
        var results = new ListBox { Name = "RetainedPlays" };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var refresh = new Button { Content = "Refresh", Name = "RefreshPlays" };
        var use = new Button { Content = "Apply to Active playback", Name = "ApplyExperimentPlay", IsEnabled = false };
        var reading = 0; var applying = false;
        void UpdateApply() => use.IsEnabled = !applying && results.SelectedItem is ExperimentPlaySummary;
        async Task Read()
        {
            var revision = ++reading;
            results.ItemsSource = null; UpdateApply();
            if (batch.SelectedItem is not Batch selected) { status.Text = "No previous runs. Enable Top plays before running an experiment."; return; }
            try
            {
                var plays = await Task.Run(() => ExperimentPlayResults.Read(selected.Path));
                if (revision != reading) return;
                results.ItemsSource = plays;
                status.Text = plays.Count == 0 ? "No retained plays. Enable Top plays and complete a trial. Older runs contain scores only."
                    : $"{plays.Count} retained plays, best score first. Select one to replace its input range in Active playback. Undo is available.";
            }
            catch (Exception ex) { if (revision == reading) status.Text = ex.Message; }
        }
        results.SelectionChanged += (_, _) => UpdateApply();
        batch.SelectionChanged += async (_, _) => await Read();
        refresh.Click += async (_, _) => await Read();
        use.Click += async (_, _) =>
        {
            if (batch.SelectedItem is not Batch selected || results.SelectedItem is not ExperimentPlaySummary play) return;
            applying = true; UpdateApply();
            try { await apply(selected.Path, play); status.Text = $"Applied {play.Name}. Seek in the main window to preview it; Undo restores previous inputs."; }
            catch (Exception ex) { status.Text = ex.Message; }
            finally { applying = false; UpdateApply(); }
        };
        var root = new Grid { Margin = new Thickness(16), RowDefinitions = new("Auto,*,Auto,Auto"), RowSpacing = 12 };
        root.Children.Add(batch); Grid.SetRow(results, 1); root.Children.Add(results);
        Grid.SetRow(status, 2); root.Children.Add(status);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { refresh, use } };
        Grid.SetRow(actions, 3); root.Children.Add(actions); Content = root;
        Opened += async (_, _) => { if (batch.ItemCount > 0) batch.SelectedIndex = 0; else await Read(); };
    }
}
