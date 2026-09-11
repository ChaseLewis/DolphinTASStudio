using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using TasStudio.Emulation;

namespace TasStudio.App;

/// <summary>Nonmodal batch progress; the worker owns scheduling and durable results.</summary>
internal sealed class ExperimentRunWindow : Window
{
    private readonly TextBlock _status = new() { Text = "Starting experiment…", TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private readonly TextBox _output = new() { IsReadOnly = true, AcceptsReturn = true, FontFamily = new("Consolas"), TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private readonly Button _cancel = new() { Content = "Cancel run" };
    private readonly PreparedExperimentRun _run;
    private bool _finished;
    private bool _cancelRequested;
    private readonly Queue<string> _lines = new();
    private const int MaximumDisplayLines = 150;

    public ExperimentRunWindow(string worker, PreparedExperimentRun run)
    {
        _run = run;
        Title = "Experiment run"; Width = 760; Height = 460;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new Grid { RowDefinitions = new("Auto,*,Auto"), Margin = new Thickness(16), RowSpacing = 12 };
        root.Children.Add(_status);
        var scroll = new ScrollViewer { Content = _output };
        Grid.SetRow(scroll, 1); root.Children.Add(scroll);
        var open = new Button { Content = "Open run folder" };
        open.Click += (_, _) =>
        {
            try { MainWindow.OpenExperimentFolder(run.Directory); }
            catch (Exception ex) { _status.Text = ex.Message; }
        };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { _cancel, open } };
        Grid.SetRow(actions, 2); root.Children.Add(actions); Content = root;
        _cancel.Click += (_, _) => Cancel();
        Closing += (_, _) => { if (!_finished) Cancel(); };
        Opened += async (_, _) => await Execute(worker);
    }

    private void Cancel()
    {
        if (_finished || _cancelRequested) return;
        try
        {
            // The batch directory is owned by run-csharp; do not create it before the worker does.
            // Execute also checks this request until the directory exists (including during builds).
            _cancelRequested = true;
            SignalCancellation();
            _cancel.IsEnabled = false;
            _status.Text = "Cancelling experiment…";
        }
        catch (Exception ex) { _cancelRequested = false; _status.Text = "Could not cancel: " + ex.Message; }
    }

    private void SignalCancellation()
    {
        if (_cancelRequested && Directory.Exists(_run.BatchDirectory))
            File.WriteAllText(Path.Combine(_run.BatchDirectory, "cancel"), "cancel");
    }

    private async Task Execute(string worker)
    {
        try
        {
            using var log = new StreamWriter(Path.Combine(_run.Directory, "worker.log")) { AutoFlush = true };
            var logGate = new object();
            using var process = new Process { StartInfo = new(worker)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(_run.ConfigPath)!
            } };
            foreach (var arg in new[] { "run-csharp", _run.ConfigPath, _run.BatchDirectory }) process.StartInfo.ArgumentList.Add(arg);
            async Task ReadLines(StreamReader reader)
            {
                while (await reader.ReadLineAsync() is { } line)
                {
                    lock (logGate) log.WriteLine(line);
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        _lines.Enqueue(line.Length > 2000 ? line[..2000] : line);
                        while (_lines.Count > MaximumDisplayLines) _lines.Dequeue();
                        _output.Text = string.Join(Environment.NewLine, _lines);
                        _output.CaretIndex = _output.Text.Length;
                    });
                }
            }
            process.Start();
            _status.Text = _cancelRequested ? "Cancelling experiment…" : "Running experiment…";
            var stdout = ReadLines(process.StandardOutput);
            var stderr = ReadLines(process.StandardError);
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            timer.Tick += (_, _) =>
            {
                try { SignalCancellation(); }
                catch (Exception ex) { _status.Text = "Could not signal cancellation: " + ex.Message; }
            };
            timer.Start();
            try { await process.WaitForExitAsync(); await Task.WhenAll(stdout, stderr); }
            finally { timer.Stop(); }
            _status.Text = _cancelRequested ? "Run stopped. See the run folder for output and any completed results." :
                process.ExitCode == 0 ? "Experiment completed. Results are in batch/results.sqlite." :
                "Experiment failed or some trials did not complete. See the output and worker.log.";
        }
        catch (Exception ex) { _status.Text = "Experiment could not run: " + ex.Message; }
        finally { _finished = true; _cancel.IsEnabled = false; }
    }
}
