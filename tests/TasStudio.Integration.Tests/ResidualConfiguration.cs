using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using TasStudio.Core;
using TasStudio.Dolphin;
using TasStudio.Emulation;

internal static class ResidualConfiguration
{
    public static void Run(string rom, string output)
    {
        using var backend = new DolphinBackend();
        const long ProjectUtc = 946684800;
        const long CompatibilityUtc = ProjectUtc + 12345;
        var profile = Path.Combine(output, "profile");
        var options = new BackendOptions(Path.Combine(AppContext.BaseDirectory, "native", "dolphin_libretro.dll"),
            Path.Combine(AppContext.BaseDirectory, "system"), profile)
        { Configuration = new EmulationConfiguration { StartUtcSeconds = ProjectUtc } };
        var gameSettings = Path.Combine(profile, "User", "GameSettings");
        Directory.CreateDirectory(gameSettings);
        using var image = File.OpenRead(rom);
        var id = new byte[6]; image.ReadExactly(id);
        var gameId = Encoding.ASCII.GetString(id);
        Check(gameId.All(char.IsAsciiLetterOrDigit), "Invalid GameCube game identifier.");
        var overridePath = Path.Combine(gameSettings, gameId + ".ini");
        var overrideText = $"[Core]\nCustomRTCValue = {CompatibilityUtc}\n";
        File.WriteAllText(overridePath, overrideText);
        var saveSentinel = Path.Combine(profile, "User", "GC", "storage-preservation-test.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(saveSentinel)!);
        byte[] sentinel = [42, 19, 8, 3]; File.WriteAllBytes(saveSentinel, sentinel);
        VideoFrame? latest = null;
        backend.VideoReady += frame => latest = frame;

        (string Identity, ulong Clock, string Pixels) Boot()
        {
            latest = null;
            backend.LoadGame(rom, options);
            var clock = BinaryPrimitives.ReadUInt64BigEndian(backend.ReadMemory(0x800030D8, 8));
            Check(clock == (ulong)(CompatibilityUtc - ProjectUtc) * 40500000UL, "Local compatibility RTC override was lost.");
            for (var i = 0; i < 360; i++) backend.Step(ControllerState.Neutral);
            Check(latest != null, "No native video.");
            var evidence = (backend.ConfigurationIdentity!, clock,
                $"{latest!.Width}x{latest.Height}:" + Convert.ToHexString(SHA256.HashData(latest.Rgba)));
            backend.Stop();
            return evidence;
        }

        var first = Boot();
        var configDirectory = Path.Combine(profile, "User", "Config");
        var gfx = Path.Combine(configDirectory, "GFX.ini");
        File.AppendAllText(gfx, "\n[Settings]\nShowFPS = True\nShowFrameCount = True\n");
        var seededGfx = File.ReadAllText(gfx);
        var marker = "test-residual.ini";
        File.WriteAllText(Path.Combine(configDirectory, marker), "must be archived");
        var second = Boot();
        Check(first == second, $"Residual config changed native identity/clock/pixels: {first} -> {second}.");
        Check(!File.Exists(Path.Combine(configDirectory, marker)), "Residual Config was still active.");
        var archivedMarker = Directory.EnumerateFiles(Path.Combine(profile, "ArchivedConfig"), marker, SearchOption.AllDirectories).Single();
        Check(File.ReadAllText(Path.Combine(Path.GetDirectoryName(archivedMarker)!, "GFX.ini")) == seededGfx,
            "Original configuration was not preserved in the archive.");
        Check(File.ReadAllText(overridePath) == overrideText, "Local GameSettings was modified.");
        Check(File.ReadAllBytes(saveSentinel).SequenceEqual(sentinel), "Memory-card storage was modified.");
        File.WriteAllText(Path.Combine(output, "evidence.txt"), $"{first.Identity}\n{first.Clock}\n{first.Pixels}\n");
        Console.WriteLine("PASS: seeded residual graphics config archived; repeated boot identity/UTC/pixels match; local compatibility and save storage preserved.");
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
