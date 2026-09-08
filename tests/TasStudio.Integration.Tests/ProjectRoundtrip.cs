using System.Security.Cryptography;
using TasStudio.Core;
using TasStudio.Dolphin;
using TasStudio.Emulation;

internal static class ProjectRoundtrip
{
    private const uint Mem1Base = 0x80000000;
    private const int Mem1Size = 24 * 1024 * 1024;
    private const int InputCount = 360;
    public static async Task Run(string rom, string output, bool reopen)
    {
        using var service = new ExecutionService(new DolphinBackend());
        service.StatusChanged += message => { if (message != "Paused") Console.WriteLine(message); };
        var options = new BackendOptions(Path.Combine(AppContext.BaseDirectory, "native", "dolphin_libretro.dll"),
            Path.Combine(AppContext.BaseDirectory, "system"), Path.Combine(output, "project-profiles"))
        { Configuration = new EmulationConfiguration() };
        var projectPath = Path.Combine(output, "integration.tasproj");
        var statePath = Path.Combine(output, "manual.tasstate");
        var expectedPath = Path.Combine(output, "project-hash.txt");
        if (reopen)
        {
            await service.LoadProjectAsync(projectPath, options);
            if (service.Position != InputCount || service.Inputs.Count != InputCount)
                throw new Exception("Reopened project has the wrong timeline or position.");
            if (service.Takes.Count != 1 || service.Takes[0].Inputs[5].Buttons != PadButtons.A || service.Inputs[15].Buttons != PadButtons.A)
                throw new Exception("Reopening lost candidate edits or the applied section.");
            var expected = File.ReadAllText(expectedPath);
            if (await Hash(service) != expected) throw new Exception("Fresh-process project replay diverged.");
            await service.SeekAsync(0);
            await service.SeekAsync(InputCount);
            if (await Hash(service) != expected) throw new Exception("Fresh-process seeking diverged.");
        }
        else
        {
            await service.LoadGameAsync(rom, options);
            for (int i = 0; i < 1200; i++) await service.StepAsync();
            await service.NewProjectAsync();
            await service.ConfigureCheckpointsAsync(new(IntervalSeconds: 2, MaximumCount: 3));
            for (int i = 0; i < InputCount; i++)
            {
                var buttons = i is >= 5 and < 10 ? PadButtons.Start : PadButtons.None;
                service.LiveInput = () => ControllerState.Neutral with { Buttons = buttons };
                await service.StepAsync();
            }
            await service.SaveNamedStateAsync("Before edit");
            var takeId = await service.CaptureTakeAsync("Alternative", 10, 30);
            await service.SetTakeInputAsync(takeId, 15, ControllerState.Neutral with { Buttons = PadButtons.A });
            await service.UseTakeAsync(takeId, 10, 30);
            if (service.IsPreviewCurrent || service.StateMarkers.Any(m => m.Position > 15 && m.Valid)) throw new Exception("Edit failed to invalidate saved states/checkpoints.");
            await service.SeekAsync(InputCount);
            await service.SaveNamedStateAsync("After edit");
            var expected = await Hash(service);
            await service.SeekAsync(0);
            await service.SeekAsync(InputCount);
            if (await Hash(service) != expected) throw new Exception("Edited project replay diverged.");
            await service.SaveProjectAsync(projectPath);
            File.WriteAllText(expectedPath, expected);
            await service.SaveStateAsync(statePath);
            await service.StepAsync();
            await service.LoadStateAsync(statePath);
            if (!service.HasProject || service.Position != InputCount || await Hash(service) != expected)
                throw new Exception("Manual savestate file roundtrip failed.");
            await service.LoadProjectAsync(projectPath, options);
            if (await Hash(service) != expected) throw new Exception("Project reopen in same process diverged.");
        }
        var checkpoint = service.StateMarkers.FirstOrDefault(marker => marker.Automatic && marker.Position < InputCount)
            ?? throw new Exception("No persisted checkpoint available before the current preview.");
        var terminal = await Hash(service); var inputs = service.Inputs.ToArray();
        await service.LoadMarkerAsync(checkpoint.Id);
        if (service.Position != checkpoint.Position) throw new Exception("Checkpoint did not restore its boundary.");
        await service.SeekAsync(InputCount);
        if (await Hash(service) != terminal || !service.Inputs.SequenceEqual(inputs))
            throw new Exception("Checkpoint replay changed memory or inputs.");
        await service.ClearMarkerAsync(checkpoint.Id);
        if (service.Position != InputCount || service.StateMarkers.Any(marker => marker.Id == checkpoint.Id))
            throw new Exception("Clearing a checkpoint changed preview position or retained the marker.");
        await service.StopAsync();
        Console.WriteLine("PASS: real-core project recording, edit, seek, save/reopen, and state file workflows.");
    }
    private static async Task<string> Hash(ExecutionService service) =>
        Convert.ToHexString(SHA256.HashData(await service.ReadMemoryAsync(Mem1Base, Mem1Size)));
}
