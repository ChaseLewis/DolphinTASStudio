using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using TasStudio.Emulation;

namespace TasStudio.App;

internal sealed class ExperimentStorageWindow : Window
{
    internal ExperimentStorageWindow(string project, Func<Task<string[]>> protectedFiles)
    {
        Title = "Clean experiment storage"; Width = 740; Height = 560; MinWidth = 600; MinHeight = 430;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var summary = new TextBlock { Name = "ExperimentStorageSummary", TextWrapping = TextWrapping.Wrap, FontSize = 18 };
        var status = new TextBlock { Name = "ExperimentStorageProgressText", TextWrapping = TextWrapping.Wrap };
        var bar = new ProgressBar { Name = "ExperimentStorageProgress", Minimum = 0, Maximum = 100, Height = 8 };
        var details = new TextBox { Name = "ExperimentStorageDetails", IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap };
        var scan = new Button { Content = "Scan again", Name = "ScanExperimentStorage" };
        var clean = new Button { Content = "Clean experiments", Name = "CleanExperimentStorage", IsEnabled = false };
        var stop = new Button { Content = "Stop", Name = "StopExperimentStorage", IsEnabled = false };
        var close = new Button { Content = "Close", IsCancel = true }; close.Click += (_, _) => Close();
        var busy = false; CancellationTokenSource? cancellation = null;
        Closing += (_, e) => e.Cancel = busy;
        stop.Click += (_, _) => { cancellation?.Cancel(); stop.IsEnabled = false; };
        static string Size(long bytes) => bytes >= 1024L * 1024 * 1024 ? $"{bytes / (1024.0 * 1024 * 1024):F2} GiB" : $"{bytes / (1024.0 * 1024):F1} MiB";
        async Task Run(bool preview)
        {
            if (busy) return;
            busy = true; scan.IsEnabled = clean.IsEnabled = close.IsEnabled = false; stop.IsEnabled = true;
            using var cancel = cancellation = new CancellationTokenSource();
            summary.Text = preview ? "Scanning experiment storage..." : "Cleaning experiment storage...";
            details.Text = ""; status.Text = "Scanning..."; bar.IsIndeterminate = true; bar.Value = 0;
            long freed = 0;
            var progress = new Progress<ExperimentStorageProgress>(update =>
            {
                if (!busy || cancellation != cancel) return;
                freed = update.FreedBytes;
                var working = update.Stage is "Freeing space" or "Compressing inputs";
                bar.IsIndeterminate = !working;
                if (working) bar.Value = update.TotalFiles > 0 ? Math.Clamp(100.0 * update.ProcessedFiles / update.TotalFiles, 0, 100) : 0;
                status.Text = working
                    ? $"{update.Stage}: {update.ProcessedFiles:N0} / {update.TotalFiles:N0} steps in this experiment | Freed {Size(Math.Max(0, freed))}"
                    : $"{update.Stage}: {update.ScannedFiles:N0} files | {Size(update.ScannedBytes)} found";
                if (working && update.Path != null)
                    status.Text += "\n" + Path.GetRelativePath(Path.GetDirectoryName(Path.GetFullPath(project))!, update.Path);
            });
            try
            {
                var paths = await protectedFiles();
                var report = await Task.Run(() => ExperimentStorage.CleanProject(project, preview, paths,
                    removeScreenshots: true, compressInputs: true, progress: progress, cancellationToken: cancel.Token));
                summary.Text = preview ? $"{Size(report.Bytes)} can be removed | {report.Files:N0} files" : $"Freed {Size(report.Bytes)} | {report.Files:N0} files processed";
                if (preview && report.CompressibleFiles > 0)
                    summary.Text += $"\nPlus {Size(report.CompressibleBytes)} of inputs to compress ({report.CompressibleFiles:N0} files).";
                details.Text = $"{(preview ? "Total experiments folder" : "Before cleanup")}: {Size(report.TotalBytes)} in {report.TotalFiles:N0} files\n" +
                    string.Join("\n", report.Categories.Select(category => $"{category.Name}: {Size(category.Bytes)} ({category.Files:N0} files)")) +
                    "\n\n" + string.Join("\n\n", report.Batches.Where(batch => batch.Cleanup.Files > 0 || batch.Cleanup.InUse || batch.Cleanup.Error != null).Select(batch =>
                        Path.GetRelativePath(Path.GetDirectoryName(Path.GetFullPath(project))!, batch.Directory) + "\n" +
                        (batch.Cleanup.InUse ? "In use - skipped" : batch.Cleanup.Error ?? $"{batch.Cleanup.Files:N0} files | {Size(batch.Cleanup.Bytes)}")));
                if (report.InUse > 0 || report.Blocked > 0)
                    summary.Text += $"\nSkipped {report.InUse} active and {report.Blocked} protected/unreadable locations.";
                clean.IsEnabled = preview && (report.Files > 0 || report.CompressibleFiles > 0);
                status.Text = preview ? "Scan complete. Cleanup deletes old states and BMP screenshots, then compresses retained inputs." : "Cleanup complete.";
                bar.Value = 100;
            }
            catch (OperationCanceledException)
            { summary.Text = preview ? "Scan stopped." : $"Cleanup stopped. Freed {Size(Math.Max(0, freed))} so far."; status.Text = "Scan again to continue."; }
            catch (Exception ex) { summary.Text = $"Cleanup could not finish. Freed {Size(Math.Max(0, freed))} so far."; details.Text = ex.Message; }
            finally { busy = false; cancellation = null; bar.IsIndeterminate = false; stop.IsEnabled = false; scan.IsEnabled = close.IsEnabled = true; }
        }
        scan.Click += async (_, _) => await Run(true);
        clean.Click += async (_, _) => await Run(false);
        var root = new Grid { Margin = new Thickness(20), RowDefinitions = new("Auto,Auto,Auto,Auto,*,Auto"), RowSpacing = 12 };
        Control[] rows = [
            new TextBlock { Text = "Delete old experiment save states, worker profiles and BMP screenshots; compress retained input JSON without changing the inputs. Main-project states, experiment baselines and recipe start states are protected. Code, results, top plays and logs are kept. Removed state markers disappear; cleaned batches cannot be resumed.", TextWrapping = TextWrapping.Wrap },
            summary, status, bar, details,
            new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { scan, clean, stop, close } }
        ];
        for (var i = 0; i < rows.Length; i++) { Grid.SetRow(rows[i], i); root.Children.Add(rows[i]); }
        Content = root;
        Opened += async (_, _) => await Run(true);
    }
}
