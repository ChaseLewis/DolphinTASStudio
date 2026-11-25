using System.Security.Cryptography;
using TasStudio.Core;
using TasStudio.Dolphin;
using TasStudio.Emulation;

internal static class LiveGciFolder
{
    public static async Task Run(string rom, string output, string source)
    {
        var original = Directory.GetFiles(source, "*.gci").ToDictionary(Path.GetFullPath, File.ReadAllBytes);
        var fixture = original.First().Value;
        var project = Path.Combine(output, "project", "live.tasproj");
        var folder = ProjectMemoryCards.DirectoryFor(project);
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "current.gci"), fixture);
        var other = fixture.ToArray(); other[0] = (byte)'Z'; other[1] = (byte)'Z';
        File.WriteAllBytes(Path.Combine(folder, "other-game.gci"), other);
        var options = new BackendOptions(Path.Combine(AppContext.BaseDirectory, "native", "dolphin_libretro.dll"),
            Path.Combine(AppContext.BaseDirectory, "system"), Path.Combine(output, "profile"),
            Configuration: new EmulationConfiguration { UseGciFolder = true });
        using var backend = new DolphinBackend(); using var service = new ExecutionService(backend);
        await service.CreateProjectAsync(project, rom, options);
        await service.PrepareProjectMemoryCardAsync(project);
        var loaded = (await service.GetConfigurationAsync())!.Options;
        if (!File.ReadAllText(Path.Combine(loaded.SaveDirectory, "User", "Config", "Dolphin.ini")).Contains("SlotA = 8")) throw new Exception("Folder device is not active.");
        for (var i = 0; i < 30; i++) await service.AdvanceFrameAsync(ControllerState.Neutral);
        var expectedRam = Hash(await service.ReadMemoryAsync(0x80000000, 24 * 1024 * 1024));
        var state = Path.Combine(output, "card.tasstate"); await service.SaveStateAsync(state);
        await service.SaveProjectAsync(project);
        await service.StopAsync();

        // Reopening a moved project must recover both games from its embedded state.
        // Remove the copied card files entirely, so a stale absolute path cannot pass.
        var movedRoot = Path.Combine(output, "moved");
        Directory.Move(Path.GetDirectoryName(project)!, movedRoot);
        project = Path.Combine(movedRoot, "live.tasproj"); folder = ProjectMemoryCards.DirectoryFor(project);
        foreach (var file in Directory.GetFiles(folder, "*.gci")) File.Delete(file);
        await service.LoadProjectAsync(project, options);
        await service.PrepareProjectMemoryCardAsync(project);
        if (Hash(await service.ReadMemoryAsync(0x80000000, 24 * 1024 * 1024)) != expectedRam) throw new Exception("Moved project RAM mismatch.");
        Verify(folder, "current.gci", fixture); Verify(folder, "other-game.gci", other);
        if (Directory.Exists(Path.Combine(output, "project", "gci"))) throw new Exception("Restore recreated the old card destination.");
        await service.StopAsync();

        // Boot sees newly dropped files; restoring an older state retires only files
        // that had been loaded by that instance, leaving unrelated external additions alone.
        var future = fixture.ToArray(); future[8] = (byte)'X';
        File.WriteAllBytes(Path.Combine(folder, "future.gci"), future);
        backend.LoadGame(rom, options with { GciDirectory = folder });
        backend.Restore(ProjectArchive.Load(state, ProjectArchive.StateKind).InitialState);
        backend.FlushGciFolder(); backend.Stop();
        if (File.Exists(Path.Combine(folder, "future.gci"))) throw new Exception("Future save survived rewind.");
        if (!File.Exists(Path.Combine(folder, "future.gci.deleted"))) throw new Exception("Discarded future save was not retained as a backup.");
        var cardHashes = Directory.GetFiles(folder, "*.gci").ToDictionary(file => Path.GetFileName(file), file => Hash(File.ReadAllBytes(file)));

        // Both state workers and fresh-boot/UTC workers must write exclusively to their profiles.
        foreach (var boot in new[] { false, true })
        {
            await service.LoadProjectAsync(project, options, positionOverride: 0, isolatedMemoryCard: true,
                powerOnConfiguration: boot ? options.Configuration! with { StartUtcSeconds = 1234567890 } : null);
            var worker = (await service.GetConfigurationAsync())!.Options;
            if (worker.GciDirectory == folder || !worker.GciDirectory!.StartsWith(worker.SaveDirectory)) throw new Exception("Worker shares the live card.");
            await service.StopAsync();
            Verify(worker.GciDirectory, "current.gci", fixture); Verify(worker.GciDirectory, "other-game.gci", other);
            foreach (var (name, hash) in cardHashes)
                if (Hash(File.ReadAllBytes(Path.Combine(folder, name!))) != hash) throw new Exception("Worker changed the project card.");
        }

        // Edits made while stopped enter the new baseline only after the explicit restart.
        await service.LoadProjectAsync(project, options);
        await service.PrepareProjectMemoryCardAsync(project);
        File.WriteAllBytes(Path.Combine(folder, "new-save.gci"), future);
        await service.RestartFromProjectMemoryCardAsync(project);
        await service.SaveProjectAsync(project);
        var copy = Path.Combine(output, "copy", "live.tasproj"); await service.SaveProjectAsync(copy);
        await service.StopAsync();
        Verify(ProjectMemoryCards.DirectoryFor(copy), "new-save.gci", future);
        await service.LoadProjectAsync(copy, options, isolatedMemoryCard: true);
        var copied = (await service.GetConfigurationAsync())!.Options;
        await service.StopAsync(); Verify(copied.GciDirectory!, "new-save.gci", future);
        foreach (var (file, bytes) in original)
            if (!File.ReadAllBytes(file).SequenceEqual(bytes)) throw new Exception("Source Dolphin save changed.");
        Console.WriteLine("Live project GCI: card writes, all-game snapshots, relocation, rewind deletion, isolated state/boot workers, restart and Save As verified; source saves unchanged.");
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static void Verify(string folder, string name, byte[] expected)
    {
        var actual = File.ReadAllBytes(Path.Combine(folder, name));
        // Dolphin allocates virtual card blocks and updates first-block/identity metadata.
        if (!actual.AsSpan(0, 0x36).SequenceEqual(expected.AsSpan(0, 0x36)) || !actual.AsSpan(64).SequenceEqual(expected.AsSpan(64)))
            throw new Exception("GCI payload differs: " + name);
    }
}
