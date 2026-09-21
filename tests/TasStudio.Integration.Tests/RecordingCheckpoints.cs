using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using TasStudio.Dolphin;
using TasStudio.Emulation;

internal static class RecordingCheckpoints
{
    public static async Task Run(string rom, string output, bool captureWhileRecording)
    {
        using var service = new ExecutionService(new DolphinBackend());
        using var audio = new MixerConsumer(service);
        await service.LoadGameAsync(rom, new BackendOptions(Path.Combine(AppContext.BaseDirectory, "native/dolphin_libretro.dll"),
            Path.Combine(AppContext.BaseDirectory, "system"), Path.Combine(output, "profile")));
        await service.NewProjectAsync();
        await service.ConfigureCheckpointsAsync(new(IntervalSeconds: 2, CaptureWhileRecording: captureWhileRecording));
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var watch = Stopwatch.StartNew();
        var rows = new List<(ulong Position, double Milliseconds)>();
        var lastWall = watch.Elapsed.TotalMilliseconds;
        ulong lastPosition = 0;
        service.Changed += () =>
        {
            if (!service.IsRecordingLive || service.Position == lastPosition) return;
            var now = watch.Elapsed.TotalMilliseconds;
            rows.Add((service.Position, now - lastWall)); lastWall = now; lastPosition = service.Position;
            if (service.ElapsedSeconds >= 6) finished.TrySetResult();
        };
        service.StatusChanged += message =>
        {
            if (message.StartsWith("Emulation stopped:") || message.StartsWith("Automatic checkpoint could not"))
                finished.TrySetException(new InvalidOperationException(message));
        };
        await service.RunAsync();
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(60));
        if (!captureWhileRecording && service.StateMarkers.Any(marker => marker.Automatic))
            throw new Exception("Default live recording captured an automatic checkpoint before pausing.");
        await service.PauseAsync();
        var end = service.Position;
        var input = service.Inputs.ToArray();
        var checkpointCount = service.StateMarkers.Count(marker => marker.Automatic && marker.Valid);
        if (checkpointCount < (captureWhileRecording ? 2 : 1)) throw new Exception("Recording did not publish completed checkpoints.");
        var expected = SHA256.HashData(await service.ReadMemoryAsync(0x80000000, 24 * 1024 * 1024));
        var saved = Path.Combine(output, "recording.tasproj");
        await service.SaveProjectAsync(saved);
        var content = FolderProject.Load(saved);
        if (content.AutomaticCheckpoints.Length != checkpointCount)
            throw new Exception("Pause/save did not include completed checkpoint files.");
        await service.ConfigureCheckpointsAsync(new(Enabled: false));
        await service.SeekAsync(0); await service.SeekAsync(end);
        var actual = SHA256.HashData(await service.ReadMemoryAsync(0x80000000, 24 * 1024 * 1024));
        if (!actual.SequenceEqual(expected) || !service.Inputs.SequenceEqual(input))
            throw new Exception("Restoring a recorded checkpoint changed RAM or controller input.");
        var ordered = rows.Skip(1).Select(row => row.Milliseconds).Order().ToArray();
        double Percentile(double fraction) => ordered[Math.Min(ordered.Length - 1, (int)(ordered.Length * fraction))];
        File.WriteAllText(Path.Combine(output, "timings.json"), JsonSerializer.Serialize(new
        {
            Groups = end, Checkpoints = checkpointCount,
            MedianMilliseconds = Percentile(.5), P95Milliseconds = Percentile(.95), MaximumMilliseconds = ordered[^1],
            Frames = rows.Select(row => new { row.Position, row.Milliseconds })
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"PASS: {end} recorded groups, {checkpointCount} readable saved checkpoints, identical RAM and inputs after checkpoint restore.");
        Console.WriteLine($"Recording intervals: median {Percentile(.5):F2} ms, p95 {Percentile(.95):F2} ms, max {ordered[^1]:F2} ms. Includes game loading and native state capture.");
    }

    private sealed class MixerConsumer : IDisposable
    {
        private readonly ExecutionService _service;
        private readonly ManualResetEventSlim _stop = new();
        private readonly Thread _thread;
        private IAudioSource? _source;
        public MixerConsumer(ExecutionService service)
        {
            _service = service; service.AudioSourceChanged += SetSource;
            _thread = new Thread(() =>
            {
                short[] samples = [];
                while (!_stop.IsSet)
                {
                    if (Volatile.Read(ref _source) is { } source)
                    {
                        var count = source.SampleRate / 100 * 2;
                        if (samples.Length != count) samples = new short[count];
                        source.Read(samples, count);
                    }
                    _stop.Wait(10);
                }
            }) { IsBackground = true };
            _thread.Start();
        }
        private void SetSource(IAudioSource? source) => Volatile.Write(ref _source, source);
        public void Dispose() { _service.AudioSourceChanged -= SetSource; _stop.Set(); _thread.Join(); _stop.Dispose(); }
    }
}
