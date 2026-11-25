using System.Security.Cryptography;
using TasStudio.Core;
using TasStudio.Dolphin;
using TasStudio.Emulation;

internal static class TakeOver
{
    public static async Task Run(string rom, string output)
    {
        using var service = new ExecutionService(new DolphinBackend());
        var options = new BackendOptions(Path.Combine(AppContext.BaseDirectory, "native", "dolphin_libretro.dll"),
            Path.Combine(AppContext.BaseDirectory, "system"), Path.Combine(output, "profile"))
            { Configuration = new EmulationConfiguration() };
        await service.LoadGameAsync(rom, options);
        for (var i = 0; i < 120; i++) await service.StepAsync();
        await service.NewProjectAsync(); await service.ConfigureCheckpointsAsync(new(Enabled: false));
        for (var i = 0; i < 90; i++) await service.AdvanceFrameAsync(ControllerState.Neutral);
        var endMemory = await Hash(service);
        await service.SeekAsync(30);
        var originMemory = await Hash(service); var originFields = service.VideoFieldCount; var originSeconds = service.ElapsedSeconds;
        var inputs = service.Inputs.ToArray(); var history = await service.HistoryAtAsync(90); var revision = service.Revision;
        var presentation = service.LatestPresentation;
        var diskStates = Directory.GetFiles(output, "*.tasstate", SearchOption.AllDirectories);
        await service.BeginTakeOverAsync();
        for (var i = 0; i < 120; i++)
            await service.AdvanceFrameAsync(ControllerState.Neutral with { Buttons = i % 2 == 0 ? PadButtons.Start : PadButtons.A, StickX = 255 });
        if (service.Position != 150 || service.IsRecordingLive || !service.Inputs.SequenceEqual(inputs) || service.Revision != revision)
            throw new Exception("Take Over recorded live input or stopped at the movie end.");
        await service.EndTakeOverAsync();
        if (service.Position != 30 || service.IsRunning || service.IsTakingOver || !service.IsPreviewCurrent ||
            await Hash(service) != originMemory || service.VideoFieldCount != originFields || service.ElapsedSeconds != originSeconds ||
            service.LatestPresentation != presentation)
            throw new Exception("Take Over did not restore the exact memory, timing, and presentation at its origin.");
        while (service.Position < 90) await service.StepRecordedFrameAsync();
        if (await Hash(service) != endMemory || await service.HistoryAtAsync(90) != history || service.Revision != revision ||
            !diskStates.SequenceEqual(Directory.GetFiles(output, "*.tasstate", SearchOption.AllDirectories)))
            throw new Exception("Take Over changed recorded replay, history, or save-state files.");
        await service.StopAsync();
        Console.WriteLine("PASS: real-core Take Over, exact RAM/timing/presentation return, unchanged recorded replay, no temporary state files.");
    }

    private static async Task<string> Hash(ExecutionService service) =>
        Convert.ToHexString(SHA256.HashData(await service.ReadMemoryAsync(0x80000000, 24 * 1024 * 1024)));
}
