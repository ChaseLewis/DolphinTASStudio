using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using TasStudio.Emulation;

namespace TasStudio.Worker;

/// <summary>Read-only view of this worker. Retains only the latest frame; never paces emulation.</summary>
internal sealed class WorkerPreviewWindow : Window
{
    private readonly Image _image = new() { Stretch = Stretch.Uniform };
    private readonly TextBlock _status = new() { Text = "Starting trial…", Margin = new Thickness(10, 6) };
    private readonly ProgressBar _loading = new() { IsIndeterminate = true, Height = 3 };
    private readonly CancellationTokenSource _cancellation = new();
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private readonly Func<Action<VideoFrame>, CancellationToken, Task<int>> _run;
    private VideoFrame? _pending;
    private WriteableBitmap? _bitmap;
    private bool _finished;
    private bool _started;
    private volatile bool _closed;
    internal event Action<int>? Completed;

    public WorkerPreviewWindow(ExperimentJob job, Func<Action<VideoFrame>, CancellationToken, Task<int>> run)
    {
        _run = run;
        Title = $"TAS Trial #{job.Index} — {job.Name} ({job.Index + 1:N0}/{job.Count:N0})";
        Width = 640; Height = 520; MinWidth = 320; MinHeight = 260;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var layout = new Grid { RowDefinitions = new("*,Auto,Auto"), Background = Brushes.Black };
        layout.Children.Add(_image);
        Grid.SetRow(_loading, 1); layout.Children.Add(_loading);
        Grid.SetRow(_status, 2); layout.Children.Add(_status);
        Content = layout;
        _refresh.Tick += (_, _) => RefreshFrame();
        Opened += (_, _) => Start();
        Closing += (_, e) =>
        {
            if (_finished) return;
            e.Cancel = true;
            _status.Text = "Cancelling trial and saving results…";
            _cancellation.Cancel();
        };
        Closed += (_, _) =>
        {
            _closed = true; _refresh.Stop();
            _image.Source = null; _bitmap?.Dispose(); _bitmap = null;
            Interlocked.Exchange(ref _pending, null);
            _cancellation.Dispose();
        };
    }

    private async void Start()
    {
        if (_started) return;
        _started = true; _refresh.Start();
        int code;
        try { code = await Task.Run(() => _run(PublishFrame, _cancellation.Token)); }
        catch (OperationCanceledException) { code = 2; }
        catch (Exception error) { Console.Error.WriteLine(error); code = 1; }
        _finished = true;
        Completed?.Invoke(code);
        Close();
    }

    private void PublishFrame(VideoFrame frame)
    {
        if (!_closed) Interlocked.Exchange(ref _pending, frame);
    }

    internal void RefreshFrame()
    {
        var frame = Interlocked.Exchange(ref _pending, null);
        if (frame == null || _closed) return;
        if (_bitmap?.PixelSize != new PixelSize(frame.Width, frame.Height))
        {
            _image.Source = null; _bitmap?.Dispose();
            _bitmap = new(new PixelSize(frame.Width, frame.Height), new Vector(96, 96), PixelFormat.Rgba8888, AlphaFormat.Opaque);
            _image.Source = _bitmap;
        }
        using (var buffer = _bitmap.Lock())
        {
            var stride = checked(frame.Width * 4);
            for (var y = 0; y < frame.Height; y++)
                Marshal.Copy(frame.Rgba, y * stride, buffer.Address + y * buffer.RowBytes, stride);
        }
        _image.InvalidateVisual();
        _loading.IsVisible = false;
        if (!_cancellation.IsCancellationRequested) _status.Text = $"Rendered frame {frame.Sequence:N0} · Close window to cancel this trial";
    }
}
