using System.Security.Cryptography;
using TasStudio.Core;
using TasStudio.Dolphin;
using TasStudio.Emulation;

internal static class RecoveryRoundtrip
{
    public static async Task Run(string rom, string output, bool reopen, bool waitForCrash = false)
    {
        var options = new BackendOptions(Path.Combine(AppContext.BaseDirectory, "native", "dolphin_libretro.dll"),
            Path.Combine(AppContext.BaseDirectory, "system"), Path.Combine(output, reopen ? "fresh-profile" : "profile"))
        { Configuration = new EmulationConfiguration() };
        var recovery = Path.Combine(output, "backup", "recovery.tasproj");
        var evidence = Path.Combine(output, "memory.sha256");
        using var service = new ExecutionService(new DolphinBackend());
        if (!reopen)
        {
            await service.LoadGameAsync(rom, options); await service.NewProjectAsync();
            for (var i = 0; i < 120; i++)
            {
                var input = Input(i); service.LiveInput = () => input; await service.StepAsync();
            }
            await service.CaptureTakeAsync("Recovery candidate", 10, 5);
            var hash = Convert.ToHexString(SHA256.HashData(await service.ReadMemoryAsync(0x80000000, 24 * 1024 * 1024)));
            File.WriteAllText(evidence, hash);
            await service.SaveRecoveryAsync(recovery);
            if (waitForCrash)
            {
                // The parent test terminates this process after the durable recovery write.
                // Keep an additional unsaved edit so recovery must return the committed snapshot.
                await service.SetInputAsync(0, ControllerState.Neutral with { Buttons = PadButtons.B });
                File.WriteAllText(Path.Combine(output, "crash-ready"), Environment.ProcessId.ToString());
                await Task.Delay(Timeout.InfiniteTimeSpan);
            }
            await service.StopPlaybackAsync();
            if (!service.HasProject || service.GamePath != rom || service.Inputs.Count != 120 || service.Position != 0)
                throw new Exception("Stop lost the project's ROM or inputs.");
        }
        else
        {
            await service.LoadProjectAsync(recovery, options);
            if (service.Position != 120 || !service.Inputs.SequenceEqual(Enumerable.Range(0, 120).Select(Input)) || service.Takes.Count != 1)
                throw new Exception("Recovery lost inputs, position or candidate.");
            var hash = Convert.ToHexString(SHA256.HashData(await service.ReadMemoryAsync(0x80000000, 24 * 1024 * 1024)));
            if (hash != File.ReadAllText(evidence)) throw new Exception("Recovered native memory differs from the saved input replay.");
        }
        Console.WriteLine(reopen ? "PASS: recovery reopened in a fresh process; inputs, candidate and terminal MEM1 match."
            : "PASS: native input recovery saved; Stop retains ROM, project and all 120 inputs.");
    }
    private static ControllerState Input(int i) => ControllerState.Neutral with { Buttons = i is >= 80 and < 84 ? PadButtons.Start : PadButtons.None };
}
