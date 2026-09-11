using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using TasStudio.Core;
using TasStudio.Dolphin;
using TasStudio.Emulation;

const uint RamBase = 0x80000000;
const int RamBytes = 24 * 1024 * 1024;
const int WarmupFrames = 1800;
const int ReplayFrames = 600;
if (args.Length < 2) throw new ArgumentException("Usage: integration <rom> <output-folder> [restore] [--diagnostic-input <index>]");
var rom = Path.GetFullPath(args[0]);
var output = Path.GetFullPath(args[1]);
Directory.CreateDirectory(output);
if (args.Contains("dtm-export"))
{
    var sourceIndex = Array.IndexOf(args, "--movie");
    await DolphinMovieExportCheck.Run(rom, output, sourceIndex >= 0 ? args[sourceIndex + 1] : null);
    return;
}
if (Array.IndexOf(args, "project-build-warning") is var warningIndex && warningIndex >= 0)
{
    await ProjectBuildWarning.Run(rom, output, args[warningIndex + 1]);
    return;
}
if (args.Contains("realtime-playback"))
{
    string? Option(string name) => Array.IndexOf(args, name) is var index && index >= 0 ? args[index + 1] : null;
    RealtimePlayback.Run(rom, output, Option("--movie"), int.Parse(Option("--start") ?? "0"), int.Parse(Option("--groups") ?? "600"));
    return;
}
if (args.Contains("manual-play-create") || args.Contains("manual-play-restore"))
{
    await ManualPlay.Run(rom, output, args.Contains("manual-play-restore"));
    return;
}
if (args.Contains("poll-create") || args.Contains("poll-restore"))
{
    await PollPlayback.Run(rom, output, args.Contains("poll-restore"));
    return;
}
if (args.Contains("presentation-advance"))
{
    PresentationAdvance.Run(rom, output);
    return;
}
if (args.Contains("boot-preview"))
{
    await BootPreview.Run(rom, output, args.Contains("restore-baseline"), args.Contains("legacy"));
    return;
}
if (args.Contains("recovery-create") || args.Contains("recovery-restore") || args.Contains("recovery-crash"))
{
    await RecoveryRoundtrip.Run(rom, output, args.Contains("recovery-restore"), args.Contains("recovery-crash"));
    return;
}
if (args.Contains("configuration-clean"))
{
    ResidualConfiguration.Run(rom, output);
    return;
}
if (args.Contains("configuration-create") || args.Contains("configuration-restore"))
{
    await ConfigurationRoundtrip.Run(rom, output, args.Contains("configuration-restore"));
    return;
}
if (args.Contains("project-create") || args.Contains("project-restore"))
{
    await ProjectRoundtrip.Run(rom, output, args.Contains("project-restore"));
    return;
}
using var backend = new DolphinBackend();
var restoring = args.Contains("restore");
var diagnosticIndex = Array.IndexOf(args, "--diagnostic-input");
var diagnosticInput = diagnosticIndex >= 0 && diagnosticIndex + 1 < args.Length ? int.Parse(args[diagnosticIndex + 1]) : -1;
var options = new BackendOptions(Path.Combine(AppContext.BaseDirectory, "native", "dolphin_libretro.dll"),
    Path.Combine(AppContext.BaseDirectory, "system"), Path.Combine(output, restoring ? "fresh-profile" : "profile"));
VideoFrame? latest = null;
List<string>? stepVideo = null;
long audioSamples = 0;
bool hasAudioSignal = false;
backend.VideoReady += frame =>
{
    latest = frame;
    // Sequence counters belong to the host lifetime, so compare dimensions/pixels instead.
    // Capture only Step callbacks: Restore's cached preview is not a newly rendered frame.
    stepVideo?.Add($"{frame.Width}x{frame.Height}:{Convert.ToHexString(SHA256.HashData(frame.Rgba))}");
};
backend.AudioReady += (samples, _) =>
{
    audioSamples += samples.Length;
    hasAudioSignal |= samples.Any(sample => sample != 0);
};
var watch = Stopwatch.StartNew();
Console.WriteLine("Loading " + rom);
backend.LoadGame(rom, options);
Console.WriteLine("Loaded. FPS=" + backend.FramesPerSecond);
EmulatorSnapshot baseline;
if (restoring)
{
    var preview = JsonSerializer.Deserialize<VideoFrame>(File.ReadAllText(Path.Combine(output, "baseline-preview.json")))
        ?? throw new InvalidDataException("Missing baseline preview.");
    baseline = new EmulatorSnapshot(WarmupFrames, File.ReadAllBytes(Path.Combine(output, "baseline.bin")), preview);
    backend.Restore(baseline);
}
else
{
    for (int i = 0; i < WarmupFrames; i++) backend.Step(ControllerState.Neutral);
    baseline = backend.Capture();
    File.WriteAllBytes(Path.Combine(output, "baseline.bin"), baseline.Data);
    if (baseline.Preview is null) throw new Exception("No baseline image.");
    File.WriteAllText(Path.Combine(output, "baseline-preview.json"), JsonSerializer.Serialize(baseline.Preview));
    Console.WriteLine($"Captured {baseline.Data.Length} bytes at {baseline.Position}");
    // Exercise a safe RAM roundtrip without changing the continuation under test.
    var original = backend.ReadMemory(RamBase, 4);
    byte[] probe = [0x12, 0x34, 0x56, 0x78];
    backend.WriteMemory(RamBase, probe);
    if (!backend.ReadMemory(RamBase, 4).SequenceEqual(probe)) throw new Exception("Memory write/read mismatch.");
    if (System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(backend.ReadMemory(RamBase, 4)) != 0x12345678)
        throw new Exception("Big-endian memory interpretation failed.");
    backend.WriteMemory(RamBase, original);
    try { backend.ReadMemory(0, 4); throw new Exception("Invalid address unexpectedly accepted."); }
    catch (InvalidOperationException) { }
}
(string Ram, string[] Video) RunReplay(string name)
{
    var video = new List<string>();
    var callbacks = new List<string>();
    for (int i = 0; i < ReplayFrames; i++)
    {
        stepVideo = [];
        backend.Step(ControllerState.Neutral with { Buttons = i is >= 10 and < 20 ? PadButtons.Start :
            i is >= 120 and < 130 ? PadButtons.A : PadButtons.None });
        // A duplicate/no-output VI holds the prior image. Restore may re-present that image;
        // callback counts are diagnostic, while displayed pixels at every VI must match.
        callbacks.Add($"{i}:{(stepVideo.Count == 0 ? "no-new-frame" : string.Join(";", stepVideo))}");
        if (latest is null) throw new Exception($"No image at replay input {i}.");
        video.Add($"{i}:{latest.Width}x{latest.Height}:{Convert.ToHexString(SHA256.HashData(latest.Rgba))}");
        if (i == diagnosticInput)
        {
            File.WriteAllBytes(Path.Combine(output, name + "-diagnostic.rgba"), latest.Rgba);
            File.WriteAllText(Path.Combine(output, name + "-diagnostic.json"), JsonSerializer.Serialize(new { Input = i, latest.Width, latest.Height }));
        }
        stepVideo = null;
    }
    File.WriteAllLines(Path.Combine(output, name + "-video.txt"), video);
    File.WriteAllLines(Path.Combine(output, name + "-callbacks.txt"), callbacks);
    return (Convert.ToHexString(SHA256.HashData(backend.ReadMemory(RamBase, RamBytes))), video.ToArray());
}
void CompareVideo(string[] expected, string[] actual, string context)
{
    if (expected.Length != actual.Length) throw new Exception($"{context}: video trace length differs.");
    for (var i = 0; i < expected.Length; i++)
        if (expected[i] != actual[i])
            throw new Exception($"{context}: rendering diverged at replay input {i}, state {WarmupFrames + i + 1}. Expected {expected[i]}; actual {actual[i]}. See video traces in {output}.");
}
var first = RunReplay(restoring ? "fresh-first" : "first");
backend.Restore(baseline);
var second = RunReplay(restoring ? "fresh-second" : "second");
if (!restoring) File.WriteAllText(Path.Combine(output, "expected-hash.txt"), first.Ram);
if (first.Ram != second.Ram) throw new Exception($"RAM replay diverged: {first.Ram} / {second.Ram}");
Console.WriteLine("Same-process terminal RAM matched: " + first.Ram);
if (restoring)
{
    var expected = File.ReadAllText(Path.Combine(output, "expected-hash.txt"));
    if (first.Ram != expected) throw new Exception($"Fresh process replay diverged: {first.Ram} / {expected}");
    Console.WriteLine("Fresh-process terminal RAM matched: " + first.Ram);
    CompareVideo(File.ReadAllLines(Path.Combine(output, "first-video.txt")), first.Video, "Fresh-process restore");
}
CompareVideo(first.Video, second.Video, "Same-process restore");
if (latest is null || latest.Rgba.All(b => b == 0)) throw new Exception("No rendered image.");
if (audioSamples == 0 || !hasAudioSignal) throw new Exception("No non-silent audio samples produced.");
File.WriteAllBytes(Path.Combine(output, "frame.rgba"), latest.Rgba);
File.WriteAllText(Path.Combine(output, "frame.json"), JsonSerializer.Serialize(new { latest.Width, latest.Height }));
Console.WriteLine($"RAM and {ReplayFrames} presentation-boundary video observations matched. Image {latest.Width}x{latest.Height}; audio samples {audioSamples}; {watch.Elapsed}.");
backend.Stop();
Console.WriteLine("Stopped; reloading.");
backend.LoadGame(rom, options);
backend.Step(ControllerState.Neutral);
backend.Stop();
var invalidGame = Path.Combine(output, "invalid.iso");
File.WriteAllBytes(invalidGame, new byte[32]);
try
{
    backend.LoadGame(invalidGame, options);
    throw new Exception("Malformed ROM unexpectedly loaded.");
}
catch (InvalidOperationException error) { Console.WriteLine("Malformed ROM rejected: " + error.Message); }
backend.LoadGame(rom, options);
backend.Step(ControllerState.Neutral);
backend.Stop();
Console.WriteLine("PASS: boot, video, stepping, memory, state replay, shutdown/reload.");
