using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using TasStudio.Core;
using TasStudio.Dolphin;
using TasStudio.Emulation;

internal static class RealtimePlayback
{
    public static void Run(string rom, string output, string? movie = null, int start = 0, int groups = 600)
    {
        var metadata = movie == null ? null : FolderProject.Load(movie).Archive.Metadata;
        if (start < 0 || groups < 1 || (metadata != null && start + groups > metadata.Inputs.Length))
            throw new ArgumentOutOfRangeException(nameof(start));
        var polls = metadata?.PollFrames.ToDictionary(p => p.Index, p => p.Frame);
        using var backend = new DolphinBackend();
        backend.LoadGame(rom, new BackendOptions(Path.Combine(AppContext.BaseDirectory, "native/dolphin_libretro.dll"),
            Path.Combine(AppContext.BaseDirectory, "system"), Path.Combine(output, "profile"),
            InternalResolution: metadata?.InternalResolution ?? 1, DspHle: metadata?.DspHle ?? true,
            Configuration: metadata?.Configuration));
        void Advance(int index)
        {
            if (polls != null && polls.TryGetValue(index, out var frame)) backend.ReplayInputPollFrame(frame);
            else backend.Step(metadata?.Inputs[index] ?? ControllerState.Neutral);
        }
        for (var i = 0; i < start; i++) Advance(i);
        var baseline = backend.Capture();
        var recorded = new List<InputPollFrame>();
        var video = new List<string>();
        string pixelHash = "";
        backend.VideoReady += frame => pixelHash = Convert.ToHexString(SHA256.HashData(frame.Rgba));
        var packetSignal = 0;
        backend.AudioReady += (samples, _) => { if (samples.Any(s => s != 0)) packetSignal++; };
        string Ram() => Convert.ToHexString(SHA256.HashData(backend.ReadMemory(0x80000000, 24 * 1024 * 1024)));
        for (var i = 0; i < groups; i++)
        {
            Advance(start + i);
            recorded.Add(backend.LastInputPollFrame!);
            video.Add(pixelHash);
        }
        var expectedRam = Ram();
        Console.WriteLine($"Unpaced baseline: {groups} groups, {packetSignal} packets with audio signal.");
        backend.Restore(baseline);
        var source = backend.StartRealtimePlayback();
        IAudioSource currentSource = source;
        using var cancellation = new CancellationTokenSource();
        var reads = 0;
        var signal = 0;
        var consumer = Task.Run(() =>
        {
            var samples = new short[source.SampleRate / 100 * 2];
            while (!cancellation.IsCancellationRequested)
            {
                Volatile.Read(ref currentSource).Read(samples, samples.Length);
                Interlocked.Increment(ref reads);
                if (samples.Any(s => s != 0)) Interlocked.Increment(ref signal);
                Thread.Sleep(10);
            }
        });
        var rows = new List<object>();
        try
        {
            var total = Stopwatch.StartNew();
            var emulatedStart = backend.EmulatedSeconds;
            for (var i = 0; i < groups; i++)
            {
                var before = reads;
                var watch = Stopwatch.StartNew();
                backend.ReplayInputPollFrame(recorded[i]);
                var wallSeconds = watch.Elapsed.TotalSeconds;
                rows.Add(new { Group = start + i, WallSeconds = watch.Elapsed.TotalSeconds,
                    EmulatedSeconds = recorded[i].Ticks / 486000000.0, AudioReads = reads - before });
                if (recorded[i].Ticks > 243000000 && wallSeconds < recorded[i].Ticks / 486000000.0 * 0.8)
                    throw new Exception("Long presentation was paced only after returning.");
                if (pixelHash != video[i]) throw new Exception($"Timed playback video diverged at group {i}.");
                if (i == 0 && recorded[i].Ticks > 48600000 && reads - before < 2)
                    throw new Exception("Audio did not drain inside a long presentation advance.");
                // State serialization must exclude the audio callback without stopping playback.
                if (i == 60) backend.Capture();
            }
            var emulated = backend.EmulatedSeconds - emulatedStart;
            if (total.Elapsed.TotalSeconds < emulated * 0.85 || total.Elapsed.TotalSeconds > emulated + 3)
                throw new Exception($"Internal pacing failed: {total.Elapsed.TotalSeconds:F3}s wall for {emulated:F3}s emulated.");
            if (Ram() != expectedRam) throw new Exception("Timed playback changed RAM.");
            if (signal == 0) throw new Exception("No non-silent audio reached the concurrent consumer.");
            Console.WriteLine($"PASS: {groups} groups, identical RAM/video/poll timing; {reads} audio reads, {signal} with signal; {total.Elapsed.TotalSeconds:F3}s wall for {emulated:F3}s emulated.");
            backend.StopRealtimePlayback();
            var stale = new short[640]; Array.Fill(stale, (short)123);
            source.Read(stale, stale.Length);
            if (stale.Any(s => s != 0)) throw new Exception("Retired source returned stale audio.");
            backend.Restore(baseline);
            backend.ReplayInputPollFrame(recorded[0]);
            if (pixelHash != video[0]) throw new Exception("Unpaced playback after mode switch diverged.");
            backend.Restore(baseline);
            Thread.Sleep(250);
            var resumed = backend.StartRealtimePlayback();
            Volatile.Write(ref currentSource, resumed);
            var resumeWatch = Stopwatch.StartNew();
            backend.ReplayInputPollFrame(recorded[0]);
            if (resumeWatch.Elapsed.TotalSeconds < recorded[0].Ticks / 486000000.0 * 0.8)
                throw new Exception("Resume reused the pre-pause timing deadline.");
            if (pixelHash != video[0]) throw new Exception("Resumed playback diverged.");
            source.Read(stale, stale.Length);
            if (stale.Any(s => s != 0)) throw new Exception("Old source attached to resumed playback.");
            // A still-running consumer must never dereference a destroyed native host.
            backend.Stop();
            resumed.Read(stale, stale.Length);
            if (stale.Any(s => s != 0)) throw new Exception("Destroyed source returned audio.");
            Console.WriteLine("PASS: save, mode switch, pause/resume and teardown with a concurrent consumer.");
        }
        finally
        {
            backend.StopRealtimePlayback();
            cancellation.Cancel(); consumer.GetAwaiter().GetResult();
            File.WriteAllText(Path.Combine(output, "timings.json"), JsonSerializer.Serialize(rows));
        }
    }
}
