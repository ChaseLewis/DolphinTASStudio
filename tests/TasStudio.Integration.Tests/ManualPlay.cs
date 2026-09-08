using System.Security.Cryptography;
using System.Text.Json;
using TasStudio.Core;
using TasStudio.Dolphin;
using TasStudio.Emulation;

internal static class ManualPlay
{
    private sealed record Fixture(string State, string Project, string Ram, string Card);
    public static async Task Run(string rom, string output, bool restore)
    {
        var options = new BackendOptions(Path.Combine(AppContext.BaseDirectory, "native", "dolphin_libretro.dll"),
            Path.Combine(AppContext.BaseDirectory, "system"), Path.Combine(output, restore ? "fresh" : "play"))
        { Configuration = new EmulationConfiguration { MemoryCardSizeOverride = 0 } };
        using var service = new ExecutionService(new DolphinBackend());
        string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
        async Task<string> Ram() => Hash(await service.ReadMemoryAsync(0x80000000, 24 * 1024 * 1024));
        var manifest = Path.Combine(output, "fixtures.json");
        if (restore)
        {
            foreach (var fixture in JsonSerializer.Deserialize<Fixture[]>(File.ReadAllText(manifest))!)
            {
                await service.LoadProjectAsync(Path.Combine(output, fixture.Project), options);
                if (await Ram() != fixture.Ram) throw new InvalidOperationException("Fresh process fixture RAM mismatch.");
                var initial = await service.GetConfigurationAsync();
                if (initial!.Options.Configuration!.MemoryCardSizeOverride != 0) throw new InvalidOperationException("Fixture lost memory card size.");
                await service.StopAsync();
                if (Hash(File.ReadAllBytes(PlayMemoryCards.CardPath(initial.Options, "USA"))) != fixture.Card)
                    throw new InvalidOperationException("Fresh process fixture card mismatch.");
            }
            Console.WriteLine("Fresh-process manual fixtures verified: RAM and memory cards.");
            return;
        }

        await service.LoadPlayGameAsync(rom, options);
        var exported = Path.Combine(output, "small-card.raw");
        await service.ExportPlayMemoryCardAsync("USA", exported);
        var card = File.ReadAllBytes(exported);
        if (PlayMemoryCards.ValidateRawCard(card, "USA") != 0) throw new InvalidOperationException("Dolphin did not create a 59-block card.");
        // An unused data block lets us prove actual card-byte persistence without game-specific UI automation.
        card[0xA000] = 0x5a; File.WriteAllBytes(exported, card);
        var expectedCard = Hash(card);
        await service.ImportPlayMemoryCardAsync("USA", exported);
        var fixtures = new List<Fixture>();
        for (var i = 0; i < 3; i++)
        {
            for (var group = 0; group < 60; group++)
                await service.AdvanceFrameAsync(ControllerState.Neutral);
            var state = $"fixture-{i}.tasstate";
            await service.SaveStateAsync(Path.Combine(output, state));
            fixtures.Add(new(state, $"fixture-{i}/test.tasproj", await Ram(), expectedCard));
        }
        if (service.HasProject || service.Inputs.Count != 0) throw new InvalidOperationException("Play recorded a timeline.");
        var beforeExport = await Ram(); var position = service.Position;
        await service.ExportPlayMemoryCardAsync("USA", Path.Combine(output, "after-play.raw"));
        if (service.Position != position || await Ram() != beforeExport) throw new InvalidOperationException("Export changed the play position.");
        if (Hash(File.ReadAllBytes(Path.Combine(output, "after-play.raw"))) != expectedCard) throw new InvalidOperationException("Card data did not persist.");
        foreach (var fixture in fixtures)
        {
            // Play adopts the state's size/settings even when requested defaults differ.
            await service.LoadPlayGameAsync(rom, options with { Configuration = new() }, Path.Combine(output, fixture.State));
            if (await Ram() != fixture.Ram) throw new InvalidOperationException("Restored manual state RAM mismatch.");
            await service.CreateProjectAsync(Path.Combine(output, fixture.Project), rom, options with { Configuration = new() }, Path.Combine(output, fixture.State));
            if (service.Position != 0 || await Ram() != fixture.Ram) throw new InvalidOperationException("Project fixture baseline mismatch.");
        }
        File.WriteAllText(manifest, JsonSerializer.Serialize(fixtures));
        Console.WriteLine("Manual play, card import/export, three independent states and project baselines verified.");
    }
}
