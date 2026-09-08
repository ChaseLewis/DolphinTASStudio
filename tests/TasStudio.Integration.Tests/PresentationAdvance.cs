using System.Security.Cryptography;
using System.Text.Json;
using TasStudio.Core;
using TasStudio.Dolphin;
using TasStudio.Emulation;

internal static class PresentationAdvance
{
    public static void Run(string rom, string output)
    {
        using var backend = new DolphinBackend();
        VideoFrame? video = null;
        backend.VideoReady += frame => video = frame;
        var options = new BackendOptions(Path.Combine(AppContext.BaseDirectory, "native", "dolphin_libretro.dll"),
            Path.Combine(AppContext.BaseDirectory, "system"), Path.Combine(output, "profile")) { Configuration = new EmulationConfiguration() };
        backend.LoadGame(rom, options);
        var histogram = new Dictionary<ulong, int>();
        for (var i = 0; i < 900; i++)
        {
            backend.Step(ControllerState.Neutral);
            histogram[backend.LastStepFields] = histogram.GetValueOrDefault(backend.LastStepFields) + 1;
            if (i % 100 == 0) Console.WriteLine($"Presented {backend.Position}: {backend.LastStepFields} fields, {backend.EmulatedSeconds:F3}s");
        }
        if (!histogram.Keys.Any(fields => fields > 1)) throw new Exception("Did not exercise a multi-field frame.");
        var saved = backend.Capture();
        string[] Replay()
        {
            var observations = new List<string>();
            for (var i = 0; i < 60; i++)
            {
                backend.Step(ControllerState.Neutral with { Buttons = i % 2 == 0 ? PadButtons.A : PadButtons.None });
                if (backend.Position != saved.Position + (ulong)i + 1 || video == null) throw new Exception("Incorrect presentation position.");
                observations.Add($"{backend.LastStepFields}:{backend.EmulatedSeconds:R}:{Convert.ToHexString(SHA256.HashData(video.Rgba))}:{Convert.ToHexString(SHA256.HashData(backend.ReadMemory(0x80000000, 24 * 1024 * 1024)))}");
            }
            return observations.ToArray();
        }
        var first = Replay();
        backend.Restore(saved);
        if (!first.SequenceEqual(Replay())) throw new Exception("Restored presentation steps diverged in field timing, video, or RAM.");
        File.WriteAllText(Path.Combine(output, "results.json"), JsonSerializer.Serialize(new { FieldsPerAdvance = histogram, ReplayFrames = first.Length, Passed = true }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("PASS: presentation stepping, multi-field frames, and exact state replay.");
    }
}
