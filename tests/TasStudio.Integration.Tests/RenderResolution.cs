using System.Security.Cryptography;
using System.Text.Json;
using TasStudio.Core;
using TasStudio.Dolphin;
using TasStudio.Emulation;

internal static class RenderResolution
{
    public static void Run(string rom, string output)
    {
        using var backend = new DolphinBackend();
        VideoFrame? video = null;
        backend.VideoReady += frame => video = frame;
        backend.LoadGame(rom, new BackendOptions(
            Path.Combine(AppContext.BaseDirectory, "native", "dolphin_libretro.dll"),
            Path.Combine(AppContext.BaseDirectory, "system"), Path.Combine(output, "profile"))
            { Configuration = new EmulationConfiguration() });
        for (var i = 0; i < 180; i++) backend.Step(ControllerState.Neutral);
        var baseline = backend.Capture();
        var identity = backend.ConfigurationIdentity;
        var records = new List<InputPollFrame>();
        for (var i = 0; i < 60; i++)
        {
            backend.Step(ControllerState.Neutral);
            records.Add(backend.LastInputPollFrame!.Copy());
        }
        var nativeWidth = video!.Width; var nativeHeight = video.Height;
        var expectedRam = SHA256.HashData(backend.ReadMemory(0x80000000, 24 * 1024 * 1024));
        var results = new List<object>();
        foreach (int? scale in new int?[] { 2, 3, 4, null })
        {
            backend.Restore(baseline);
            var ticks = backend.EmulatedTicks;
            backend.SetRenderResolution(scale);
            if (backend.Position != baseline.Position || backend.EmulatedTicks != ticks || backend.ConfigurationIdentity != identity)
                throw new Exception("Resolution changed the position or project configuration.");
            foreach (var record in records) backend.ReplayInputPollFrame(record);
            var effective = scale ?? 1;
            if (video!.Width != nativeWidth * effective || video.Height != nativeHeight * effective)
                throw new Exception($"Expected {nativeWidth * effective}x{nativeHeight * effective}, got {video.Width}x{video.Height} at {effective}x.");
            if (!expectedRam.SequenceEqual(SHA256.HashData(backend.ReadMemory(0x80000000, 24 * 1024 * 1024))))
                throw new Exception($"RAM differed after replay at {effective}x.");
            // Seeking after the change must retain the active rendering scale.
            backend.Restore(baseline);
            foreach (var record in records) backend.ReplayInputPollFrame(record);
            if (video.Width != nativeWidth * effective || video.Height != nativeHeight * effective)
                throw new Exception("State restore lost the rendering override.");
            results.Add(new { Scale = effective, video.Width, video.Height, ReplayedGroups = records.Count, RamMatches = true });
            Console.WriteLine($"PASS: {effective}x, {video.Width}x{video.Height}, recorded poll replay and seek, identical RAM.");
        }
        File.WriteAllText(Path.Combine(output, "results.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
    }
}
