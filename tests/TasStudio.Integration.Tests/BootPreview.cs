using System.Text.Json;
using TasStudio.Core;
using TasStudio.Dolphin;
using TasStudio.Emulation;

internal static class BootPreview
{
    public static async Task Run(string rom, string output, bool restoreBaseline, bool legacy = false)
    {
        using var service = new ExecutionService(new DolphinBackend());
        VideoFrame? frame = null; service.VideoReady += video => frame = video;
        var options = new BackendOptions(Path.Combine(AppContext.BaseDirectory, "native", "dolphin_libretro.dll"),
            Path.Combine(AppContext.BaseDirectory, "system"), Path.Combine(output, "profile")) { Configuration = legacy ? null : new EmulationConfiguration() };
        await service.LoadGameAsync(rom, options);
        if (restoreBaseline) await service.NewProjectAsync(ProjectStartKind.PowerOn);
        byte[]? reference = null;
        var renderedStartup = false;
        for (var i = 1; i <= 2400; i++)
        {
            await service.StepAsync();
            if (i % 300 != 0 || frame == null) continue;
            var nonblack = 0;
            for (var p = 0; p < frame.Rgba.Length; p += 4)
                if (frame.Rgba[p] != 0 || frame.Rgba[p + 1] != 0 || frame.Rgba[p + 2] != 0) nonblack++;
            Console.WriteLine(JsonSerializer.Serialize(new { Input = i, frame.Width, frame.Height, NonBlack = nonblack }));
            if (i <= 600 && nonblack > 1000) renderedStartup = true;
            if (i == 600) reference = frame.Rgba.ToArray();
            File.WriteAllBytes(Path.Combine(output, "frame.rgba"), frame.Rgba);
            File.WriteAllText(Path.Combine(output, "frame.json"), JsonSerializer.Serialize(new { frame.Width, frame.Height }));
        }
        if (!renderedStartup) throw new InvalidOperationException("Fresh boot produced no visible video in the first 600 inputs.");
        if (restoreBaseline)
        {
            await service.StopPlaybackAsync();
            await service.SeekAsync(600);
            if (frame == null || reference == null || !frame.Rgba.SequenceEqual(reference))
                throw new InvalidOperationException("Restarted boot did not reproduce the frame at input 600.");
        }
        Console.WriteLine("Boot rendering verified.");
    }
}
