using System.Buffers.Binary;
using System.Security.Cryptography;
using TasStudio.Core;
using TasStudio.Dolphin;
using TasStudio.Emulation;

internal static class GciFolderImport
{
    public static async Task Run(string rom, string output, string source)
    {
        var saves = Directory.GetFiles(source, "*.gci").ToDictionary(Path.GetFullPath, File.ReadAllBytes);
        if (saves.Count == 0) throw new InvalidDataException("Expected a GCI fixture folder.");
        var options = new BackendOptions(Path.Combine(AppContext.BaseDirectory, "native", "dolphin_libretro.dll"),
            Path.Combine(AppContext.BaseDirectory, "system"), Path.Combine(output, "play"))
            { Configuration = new EmulationConfiguration { MemoryCardSizeOverride = 0 } };
        using var service = new ExecutionService(new DolphinBackend());
        string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
        async Task<string> Ram() => Hash(await service.ReadMemoryAsync(0x80000000, 24 * 1024 * 1024));
        await service.LoadPlayGameAsync(rom, options);
        if (await service.ImportPlayMemoryCardFolderAsync("USA", source) != saves.Count)
            throw new InvalidOperationException("Wrong imported save count.");
        var exported = Path.Combine(output, "imported.raw");
        await service.ExportPlayMemoryCardAsync("USA", exported);
        var card = File.ReadAllBytes(exported);
        PlayMemoryCards.ValidateRawCard(card, "USA");
        VerifyContents(card, saves.Values);
        for (var i = 0; i < 60; i++) await service.AdvanceFrameAsync(ControllerState.Neutral);
        var expectedRam = await Ram(); var position = service.Position;
        foreach (var invalid in new[] { "corrupt", "duplicate" })
        {
            var folder = Path.Combine(output, invalid); Directory.CreateDirectory(folder);
            File.WriteAllBytes(Path.Combine(folder, "one.gci"), invalid == "corrupt" ? [1, 2, 3] : saves.First().Value);
            if (invalid == "duplicate") File.WriteAllBytes(Path.Combine(folder, "two.gci"), saves.First().Value);
            try { await service.ImportPlayMemoryCardFolderAsync("USA", folder); throw new Exception("Invalid import was accepted."); }
            catch (InvalidOperationException) { }
            if (service.Position != position || await Ram() != expectedRam) throw new Exception("Failed import changed the game.");
        }
        var playOptions = (await service.GetConfigurationAsync())!.Options;
        var project = Path.Combine(output, "project", "from-play.tasproj");
        await service.CreateProjectAsync(project, rom, options, fromCurrentPlay: true);
        if (service.Position != 0 || !service.HasProject || await Ram() != expectedRam)
            throw new Exception("Start TAS here changed the captured game state.");
        var projectOptions = (await service.GetConfigurationAsync())!.Options;
        if (projectOptions.SaveDirectory == playOptions.SaveDirectory) throw new Exception("Project shares the Play card profile.");
        await service.SaveNamedStateAsync("Experiment start");
        if (!service.StateMarkers.Any(m => m.Name == "Experiment start" && m.Valid)) throw new Exception("Experiment anchor is missing.");
        await service.StopAsync();
        VerifyContents(File.ReadAllBytes(PlayMemoryCards.CardPath(projectOptions, "USA")), saves.Values);
        var playCardHash = Hash(File.ReadAllBytes(PlayMemoryCards.CardPath(playOptions, "USA")));
        await service.LoadProjectAsync(project, options);
        if (await Ram() != expectedRam) throw new Exception("Reopened project RAM mismatch.");
        var reopened = (await service.GetConfigurationAsync())!.Options;
        await service.StopAsync();
        VerifyContents(File.ReadAllBytes(PlayMemoryCards.CardPath(reopened, "USA")), saves.Values);
        if (Hash(File.ReadAllBytes(PlayMemoryCards.CardPath(playOptions, "USA"))) != playCardHash) throw new Exception("TAS changed the Play card.");
        foreach (var (path, bytes) in saves)
            if (!File.ReadAllBytes(path).SequenceEqual(bytes)) throw new Exception("Source GCI changed.");
        if (Directory.EnumerateFiles(Path.Combine(playOptions.SaveDirectory, "Imports")).Any()) throw new Exception("Temporary import card remains.");
        Console.WriteLine($"Imported {saves.Count} GCI copies; raw card data, failed-import recovery, Play-to-TAS RAM/card isolation, experiment anchor and project reopen verified. Source folder unchanged.");
    }

    private static void VerifyContents(byte[] card, IEnumerable<byte[]> saves)
    {
        int Word(int offset) => BinaryPrimitives.ReadUInt16BigEndian(card.AsSpan(offset, 2));
        var directory = Word(0x2000 + 0x1ffa) > Word(0x4000 + 0x1ffa) ? 0x2000 : 0x4000;
        var bat = Word(0x6000 + 4) > Word(0x8000 + 4) ? 0x6000 : 0x8000;
        foreach (var save in saves)
        {
            var entry = Enumerable.Range(0, 127).Select(i => directory + i * 64).FirstOrDefault(offset =>
                card.AsSpan(offset, 6).SequenceEqual(save.AsSpan(0, 6)) && card.AsSpan(offset + 8, 32).SequenceEqual(save.AsSpan(8, 32)), -1);
            if (entry < 0) throw new Exception("Imported save is absent from the raw card directory.");
            var block = Word(entry + 0x36);
            var count = Word(entry + 0x38);
            if (save.Length != 64 + count * 8192) throw new Exception("Wrong save length.");
            for (var i = 0; i < count; i++)
            {
                if (block < 5 || block * 8192 + 8192 > card.Length ||
                    !card.AsSpan(block * 8192, 8192).SequenceEqual(save.AsSpan(64 + i * 8192, 8192)))
                    throw new Exception("Imported save payload differs from its source.");
                block = Word(bat + 10 + (block - 5) * 2);
            }
            if (block != 0xffff) throw new Exception("Invalid imported block chain.");
        }
    }
}
