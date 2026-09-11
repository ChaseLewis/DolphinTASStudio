using System.Buffers.Binary;
using System.Text.Json;
using TasStudio.Core;
using TasStudio.Dolphin;
using TasStudio.Emulation;

internal static class DolphinMovieExportCheck
{
    public static async Task Run(string rom, string output, string? source)
    {
        using var backend = new DolphinBackend();
        using var service = new ExecutionService(backend);
        var options = new BackendOptions(Path.Combine(AppContext.BaseDirectory, "native", "dolphin_libretro.dll"),
            Path.Combine(AppContext.BaseDirectory, "system"), Path.Combine(output, "profiles"), Configuration: new());
        if (source != null)
            await service.LoadProjectAsync(source, options, rom, positionOverride: 0);
        else
        {
            await service.CreateProjectAsync(Path.Combine(output, "smoke.tasproj"), rom, options);
            for (var group = 0; group < 180; group++)
                await service.AdvanceFrameAsync(ControllerState.Neutral with { Buttons = group is >= 90 and < 100 ? PadButtons.Start : PadButtons.None });
            await service.SaveProjectAsync(Path.Combine(output, "smoke.tasproj"));
            await service.SeekAsync(60);
        }
        var position = service.Position;
        var before = await service.ReadMemoryAsync(0x80000000, 24 * 1024 * 1024);
        var result = await service.ExportDolphinMovieAsync(Path.Combine(output, "movie.dtm"));
        var after = await service.ReadMemoryAsync(0x80000000, before.Length);
        if (service.Position != position || !before.SequenceEqual(after))
            throw new Exception("DTM export changed the paused emulator state.");
        var dtm = File.ReadAllBytes(result.MoviePath);
        if (dtm.Length != 256 + (long)result.InputCount * 8 || dtm[12] != 0 || dtm[11] != 1)
            throw new Exception("DTM length, power-on flag or controller topology is invalid.");
        var companion = Path.GetDirectoryName(result.InstructionsPath)!;
        using var report = JsonDocument.Parse(File.ReadAllText(Path.Combine(companion, "export.json")));
        if (!report.RootElement.GetProperty("BootPollsIncluded").GetBoolean())
            throw new Exception("DTM omitted startup controller polls.");
        var bootPollCount = report.RootElement.GetProperty("BootPollCount").GetInt32();
        var cards = Directory.GetFiles(Path.Combine(companion, "InitialCards"), "*.raw");
        if (cards.Length == 0) throw new Exception("Original memory card was not exported.");
        foreach (var card in cards)
            if (!File.ReadAllBytes(card).SequenceEqual(File.ReadAllBytes(Path.Combine(companion, "User", "GC", Path.GetFileName(card)))))
                throw new Exception("Playback card does not match the original copy.");
        Console.WriteLine(JsonSerializer.Serialize(new { result.MoviePath, result.InstructionsPath, result.InputCount,
            BootPollCount = bootPollCount, InitialCards = cards.Length, LastInputTick = BinaryPrimitives.ReadUInt64LittleEndian(dtm.AsSpan(0xED)),
            RestoredPosition = position, RamPreserved = true }));
        // Quick reuses the baked polls, but still recovers boot inputs and restores the session.
        var repeated = await service.ExportDolphinMovieAsync(Path.Combine(output, "movie-quick.dtm"), mode: DolphinMovieExportMode.Quick);
        var repeatedRam = await service.ReadMemoryAsync(0x80000000, before.Length);
        if (!dtm.SequenceEqual(File.ReadAllBytes(repeated.MoviePath)) || service.Position != position ||
            !before.SequenceEqual(repeatedRam))
            throw new Exception("Repeated export changed the DTM or paused state.");
        Console.WriteLine("Quick and Full exports are byte-identical; original RAM and position preserved.");
        // Exercise continued polling after the export's card flush/reboot and state restoration.
        if (position < (ulong)service.Inputs.Count) await service.StepRecordedFrameAsync();
    }
}
