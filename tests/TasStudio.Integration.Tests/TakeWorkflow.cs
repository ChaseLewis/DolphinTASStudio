using System.Security.Cryptography;
using TasStudio.Core;
using TasStudio.Dolphin;
using TasStudio.Emulation;

internal static class TakeWorkflow
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
        var original = service.Inputs.ToArray(); var originalMemory = await Hash(service);
        var top = await service.CaptureTakeAsync("Top", 15, 30);
        var lower = await service.CaptureTakeAsync("Lower", 30, 45);
        await service.EditRangeAsync(15, 30, top, ControllerState.Neutral with { Buttons = PadButtons.A }, PadButtons.A, 0);
        await service.EditRangeAsync(30, 45, lower, ControllerState.Neutral with { Buttons = PadButtons.B }, PadButtons.B, 0);
        await service.PasteInputRangeAsync(50, await service.CopyInputRangeAsync(15, 5, top), lower);
        await service.SetTakeActiveAsync(top, true); await service.SetTakeActiveAsync(lower, true);
        await service.SaveNamedStateAsync("Original movie");
        var history = await service.HistoryAtAsync(90); var revision = service.Revision; var markers = service.StateMarkers.ToArray();
        await service.TrialActiveTakesAsync(15); var trialMemory = await Hash(service);
        if (!service.Inputs.SequenceEqual(original) || await service.HistoryAtAsync(90) != history ||
            service.Revision != revision || !service.StateMarkers.SequenceEqual(markers))
            throw new Exception("Trial changed the original movie, poll history, or saved states.");
        await service.ApplyActiveTakesAsync(); await service.SeekAsync(90);
        if (await Hash(service) != trialMemory) throw new Exception("Applied take playback differs from the trial's game state.");
        await service.UndoAsync(); await service.SeekAsync(90);
        if (await Hash(service) != originalMemory || !service.Inputs.SequenceEqual(original) || service.Takes.Any(t => !t.IsActive))
            throw new Exception("Undo did not restore the original movie and active take selection.");
        using (var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(250)))
        {
            try { await service.TrialActiveTakesAsync(15, cancellation.Token); throw new Exception("Trial did not cancel."); }
            catch (OperationCanceledException) { }
        }
        if (!service.Inputs.SequenceEqual(original) || await service.HistoryAtAsync(90) != history)
            throw new Exception("Canceling a trial changed the movie.");
        await service.SeekAsync(90);
        var path = Path.Combine(output, "takes.tasproj"); await service.SaveProjectAsync(path);
        await service.LoadProjectAsync(path, options);
        if (await Hash(service) != originalMemory || service.Takes.Any(t => !t.IsActive) || service.Takes[0].Id != top)
            throw new Exception("Save/reopen lost the movie or take priority.");
        await service.ApplyActiveTakesAsync(); await service.SeekAsync(90);
        if (await Hash(service) != trialMemory) throw new Exception("Reopened take playback differs from its trial.");
        await service.StopAsync();
        Console.WriteLine("PASS: real-core take copy/paste, trial/apply memory equality, cancellation, undo, and save/reopen.");
    }

    private static async Task<string> Hash(ExecutionService service) =>
        Convert.ToHexString(SHA256.HashData(await service.ReadMemoryAsync(0x80000000, 24 * 1024 * 1024)));
}
