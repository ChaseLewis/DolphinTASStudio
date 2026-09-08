using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using TasStudio.Core;
using TasStudio.Dolphin;
using TasStudio.Emulation;

internal static class ConfigurationRoundtrip
{
    private const int InputCount = 360;
    private const long InitialUtc = 946684800;
    private const long RetryUtc = InitialUtc + 86400;
    private sealed record Evidence(ulong BootClock, int Width, int Height, string HistoryRoot, string ProfileHash);

    public static async Task Run(string rom, string output, bool reopen)
    {
        using var service = new ExecutionService(new DolphinBackend());
        service.StatusChanged += message => { if (message != "Paused") Console.WriteLine(message); };
        VideoFrame? latest = null;
        service.VideoReady += frame => latest = frame;
        var options = new BackendOptions(Path.Combine(AppContext.BaseDirectory, "native", "dolphin_libretro.dll"),
            Path.Combine(AppContext.BaseDirectory, "system"), Path.Combine(output, "profiles"))
        { Configuration = new EmulationConfiguration { StartUtcSeconds = InitialUtc } };
        var projectPath = Path.Combine(output, "configuration.tasproj");
        var evidencePath = Path.Combine(output, "configuration-evidence.json");
        var changed = new EmulationConfiguration
        {
            StartUtcSeconds = RetryUtc,
            Options = new(StringComparer.Ordinal) { ["dolphin_efb_scale"] = "2" }
        }.ValidatedCopy();
        var checkpoints = new CheckpointPolicy(IntervalSeconds: 2, MaximumCount: 3,
            Retention: CheckpointRetention.LeastRecentlyUsed, DiskBudgetMiB: 1024);

        if (reopen)
        {
            var expected = JsonSerializer.Deserialize<Evidence>(File.ReadAllText(evidencePath))!;
            await service.LoadProjectAsync(projectPath, options);
            Check(service.Position == InputCount, "Fresh process restored wrong position.");
            CheckInputs(service);
            Check((await service.GetConfigurationAsync())!.Options.Configuration!.Fingerprint == changed.Fingerprint,
                "Fresh process did not restore project settings.");
            Check(service.Checkpoints == checkpoints, "Fresh process lost checkpoint policy.");
            Check(await service.HistoryAtAsync(0) == expected.HistoryRoot, "Fresh process changed settings/history identity.");
            Check(await BootClock(service) == expected.BootClock, "Fresh process changed emulated UTC.");
            Check(latest?.Width == expected.Width && latest.Height == expected.Height, "Fresh process lost 2x rendering.");
            await service.SeekAsync(0);
            await service.SeekAsync(InputCount);
            Check(latest?.Width == expected.Width && latest.Height == expected.Height, "Fresh process seek lost 2x rendering.");
        }
        else
        {
            await service.LoadGameAsync(rom, options);
            await service.NewProjectAsync();
            await service.ConfigureCheckpointsAsync(checkpoints);
            var initialClock = await BootClock(service);
            var initialRoot = await service.HistoryAtAsync(0);
            for (var i = 0; i < InputCount; i++)
            {
                var input = Input(i);
                service.LiveInput = () => input;
                await service.StepAsync();
            }
            Check(latest != null, "No native image at 1x.");
            var nativeWidth = latest!.Width;
            var nativeHeight = latest.Height;
            await service.SaveNamedStateAsync("Before settings change");
            var oldState = Path.Combine(output, "old-settings.tasstate");
            await service.SaveStateAsync(oldState);
            Check(service.StateMarkers.Any(m => m.Position > 0), "No states/checkpoints before settings change.");
            await service.ApplyConfigurationAsync(changed, checkpoints);
            Check(service.Position == 0, "Changing settings did not restart at baseline.");
            CheckInputs(service);
            Check(!service.StateMarkers.Any(m => m.Position > 0), "Old checkpoints/states survived settings change.");
            var retryClock = await BootClock(service);
            Check(retryClock - initialClock == (ulong)(RetryUtc - InitialUtc) * 40500000UL,
                $"Native boot UTC did not change by one day: {initialClock} -> {retryClock}.");
            Check(await service.HistoryAtAsync(0) != initialRoot, "Settings failed to change history root.");
            try
            {
                await service.LoadStateAsync(oldState);
                throw new Exception("State from previous settings was accepted.");
            }
            catch (InvalidDataException) { }
            await service.SeekAsync(InputCount);
            Check(latest?.Width == nativeWidth * 2 && latest.Height == nativeHeight * 2,
                $"Native resolution did not double: {nativeWidth}x{nativeHeight} -> {latest?.Width}x{latest?.Height}.");
            await service.SaveProjectAsync(projectPath);
            var content = FolderProject.Load(projectPath);
            Check(content.Archive.Metadata.Configuration!.Fingerprint == changed.Fingerprint, "Saved project lost emulation settings.");
            Check(content.Archive.Metadata.Checkpoints == checkpoints, "Saved project lost checkpoint settings.");
            Check(content.AutomaticCheckpoints.Length > 0, "No automatic checkpoints persisted to disk.");
            var expected = new Evidence(await BootClock(service), latest!.Width, latest.Height,
                await service.HistoryAtAsync(0), content.Archive.Metadata.ConfigurationIdentity!);
            File.WriteAllText(evidencePath, JsonSerializer.Serialize(expected, new JsonSerializerOptions { WriteIndented = true }));
            await service.LoadProjectAsync(projectPath, options);
            CheckInputs(service);
            Check(await service.HistoryAtAsync(0) == expected.HistoryRoot, "Same process reopen changed settings/history identity.");
            Check(await BootClock(service) == expected.BootClock, "Same process reopen lost changed UTC.");
            Check(latest?.Width == expected.Width && latest.Height == expected.Height, "Same process reopen lost 2x rendering.");
            Console.WriteLine($"Native clock delta: {retryClock - initialClock}; resolution {nativeWidth}x{nativeHeight} -> {expected.Width}x{expected.Height}.");
        }
        await service.StopAsync();
        if (!reopen)
        {
            // Override only the test's copied resources. The bundled Sys tree is read-only.
            // A contradictory game compatibility setting must win over the requested profile.
            var compatibilitySystem = Path.Combine(output, "compatibility-system");
            foreach (var source in Directory.EnumerateFiles(options.SystemDirectory, "*", SearchOption.AllDirectories))
            {
                var destination = Path.Combine(compatibilitySystem, Path.GetRelativePath(options.SystemDirectory, source));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(source, destination, true);
            }
            using var image = File.OpenRead(rom);
            var gameIdBytes = new byte[6]; image.ReadExactly(gameIdBytes);
            var gameId = Encoding.ASCII.GetString(gameIdBytes);
            Check(gameId.All(char.IsAsciiLetterOrDigit), "Test ROM game identifier is invalid.");
            var overridePath = Path.Combine(compatibilitySystem, "dolphin-emu", "Sys", "GameSettings", gameId + ".ini");
            File.AppendAllText(overridePath, $"\n[Core]\nCustomRTCValue = {RetryUtc + 86400}\n");
            var overrideOptions = options with { Configuration = changed, InternalResolution = 2,
                SystemDirectory = compatibilitySystem, SaveDirectory = Path.Combine(output, "compatibility-profile") };
            latest = null;
            await service.LoadGameAsync(rom, overrideOptions);
            await service.NewProjectAsync();
            var compatibilityClock = await BootClock(service);
            for (var i = 0; i < InputCount; i++) await service.StepAsync();
            var expected = JsonSerializer.Deserialize<Evidence>(File.ReadAllText(evidencePath))!;
            Check(compatibilityClock - expected.BootClock == 86400UL * 40500000UL,
                $"Game compatibility override did not take precedence over project UTC: {expected.BootClock} -> {compatibilityClock}.");
            var overrideProject = Path.Combine(output, "compatibility-project", "override.tasproj");
            await service.SaveProjectAsync(overrideProject);
            var overrideMetadata = FolderProject.Load(overrideProject).Archive.Metadata;
            Check(overrideMetadata.Configuration!.Fingerprint == changed.Fingerprint, "Compatibility override rewrote the user's requested settings.");
            Check(overrideMetadata.ConfigurationIdentity != expected.ProfileHash, "Compatibility resource change did not change configuration identity.");
            await service.StopAsync();
            Console.WriteLine("PASS: game compatibility override wins; changed compatibility resources change profile identity.");
        }
        Console.WriteLine("PASS: native per-project UTC/resolution, input preservation, state invalidation and project settings roundtrip.");
    }

    private static ControllerState Input(int i) => ControllerState.Neutral with
    { Buttons = i is >= 50 and < 60 ? PadButtons.Start : i is >= 100 and < 105 ? PadButtons.A : PadButtons.None };
    private static void CheckInputs(ExecutionService service) => Check(
        service.Inputs.SequenceEqual(Enumerable.Range(0, InputCount).Select(Input)), "Recorded inputs changed or were discarded.");
    private static async Task<ulong> BootClock(ExecutionService service) =>
        BinaryPrimitives.ReadUInt64BigEndian(await service.ReadMemoryAsync(0x800030D8, 8));
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
