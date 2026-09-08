using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using TasStudio.Core;
using TasStudio.Emulation;

namespace TasStudio.Dolphin;

public sealed class DolphinBackend : IEmulatorBackend
{
    private const string IdentityPrefix = "dolphin-tas-abi3/";
    private const int MaximumStateBytes = 256 * 1024 * 1024;
    private const int AudioBufferSamples = 96000;
    private const uint InitialRtcEpoch = 946684800;
    private const int RawMemoryCardDevice = 1;
    private const int NoExiDevice = 255;
    private nint _host;
    private ulong _position;
    private ulong _lastVideoSequence;
    private VideoFrame? _preview;
    private readonly short[] _audio = new short[AudioBufferSamples];
    public string Identity { get; private set; } = "Dolphin TAS ABI 3";
    public ulong LastStepFields { get; private set; }
    public InputPollFrame? LastInputPollFrame { get; private set; }
    public string? ConfigurationIdentity { get; private set; }
    public double EmulatedSeconds => IsLoaded ? Native.tas_ticks(_host) / 486000000.0 : 0;
    public bool IsLoaded => _host != 0;
    public ulong Position => _position;
    public ulong VideoFieldCount => IsLoaded ? Native.tas_fields(_host) : 0;
    public double FramesPerSecond => IsLoaded ? Native.tas_fps(_host) : 60;
    public event Action<VideoFrame>? VideoReady;
    public event Action<short[], int>? AudioReady;

    public string InspectIdentity(BackendOptions options)
    {
        using var stream = File.OpenRead(options.CorePath);
        return IdentityPrefix + Convert.ToHexString(SHA256.HashData(stream));
    }

    public void LoadGame(string path, BackendOptions options)
    {
        var configuration = options.Configuration?.ValidatedCopy();
        if (configuration != null && (configuration.Resolution != options.InternalResolution || configuration.DspHle != options.DspHle))
            throw new InvalidDataException("Project settings disagree with backend options.");
        if (!File.Exists(path)) throw new FileNotFoundException("Game image does not exist.", path);
        if (!File.Exists(options.CorePath)) throw new FileNotFoundException("Dolphin core is missing. Run scripts/build.ps1.", options.CorePath);
        if (!Directory.Exists(Path.Combine(options.SystemDirectory, "dolphin-emu", "Sys")))
            throw new DirectoryNotFoundException("Dolphin Sys resources are missing. Run scripts/build.ps1.");
        if (options.InternalResolution is < 1 or > 4) throw new ArgumentOutOfRangeException(nameof(options.InternalResolution));
        Stop();
        Directory.CreateDirectory(options.SaveDirectory);
        var profileDirectory = Path.GetFullPath(options.SaveDirectory);
        var configDirectory = Path.Combine(profileDirectory, "User", "Config");
        if (configuration != null && Directory.Exists(configDirectory))
        {
            // Project settings define the profile; preserve residual INIs outside Dolphin's lookup paths.
            // GameSettings compatibility overrides and memory-card storage remain in place.
            if ((File.GetAttributes(configDirectory) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("The app-owned configuration directory cannot be a link.");
            var archiveDirectory = Path.Combine(profileDirectory, "ArchivedConfig");
            Directory.CreateDirectory(archiveDirectory);
            Directory.Move(configDirectory, Path.Combine(archiveDirectory, Guid.NewGuid().ToString("N")));
        }
        Directory.CreateDirectory(configDirectory);
        // This is exclusively the application's isolated profile; never the user's Dolphin profile.
        File.WriteAllText(Path.Combine(configDirectory, "Dolphin.ini"),
            $"[Core]\nCPUThread = False\nEnableCustomRTC = True\nCustomRTCValue = {configuration?.StartUtcSeconds ?? InitialRtcEpoch}\nSlotA = {RawMemoryCardDevice}\nSlotB = {NoExiDevice}\n[Interface]\nConfirmStop = False\n");
        Identity = InspectIdentity(options);
        ConfigurationIdentity = configuration == null ? null : ConfigurationHash(configuration, options);
        _host = Native.tas_create(options.CorePath, options.SystemDirectory, options.SaveDirectory,
            options.InternalResolution, options.DspHle ? 1 : 0);
        if (_host == 0) throw new InvalidOperationException(Error());
        try
        {
            if (configuration != null)
            {
                foreach (var (key, value) in configuration.Options) Check(Native.tas_set_option(_host, key, value));
                // These requirements are not user options. All enter Dolphin at the base layer.
                foreach (var key in new[] { "dolphin_main_cpu_thread", "dolphin_vi_skip", "dolphin_cheats_enabled", "dolphin_cheats_import", "dolphin_load_custom_textures", "dolphin_mods_enabled", "dolphin_osd_enabled" })
                    Check(Native.tas_set_option(_host, key, "disabled"));
            }
            Check(Native.tas_load(_host, path));
            _position = 0;
            PublishMedia();
        }
        catch { Stop(); throw; }
    }

    private static string ConfigurationHash(EmulationConfiguration configuration, BackendOptions options)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Add(string value) { hash.AppendData(Encoding.UTF8.GetBytes(value)); hash.AppendData([0]); }
        Add("studio-project-config/v1"); Add(configuration.Fingerprint);
        using (var host = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "native", "TasStudio.LibretroHost.dll"))) Add(Convert.ToHexString(SHA256.HashData(host)));
        var roots = new[] { options.SystemDirectory, Path.Combine(options.SaveDirectory, "User", "GameSettings") };
        for (var index = 0; index < roots.Length; index++)
        {
            Add(index == 0 ? "system-resources" : "local-compatibility");
            var root = roots[index];
            if (!Directory.Exists(root)) continue;
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).OrderBy(p => Path.GetRelativePath(root, p), StringComparer.Ordinal))
            {
                Add(Path.GetRelativePath(root, file).Replace('\\', '/'));
                using var stream = File.OpenRead(file); Add(Convert.ToHexString(SHA256.HashData(stream)));
            }
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    public void Step(ControllerState input)
        => StepCore(input, null);

    public void ReplayInputPollFrame(InputPollFrame frame)
    {
        frame.Validate();
        StepCore(frame.Input, frame);
    }

    private void StepCore(ControllerState input, InputPollFrame? replay)
    {
        RequireLoaded();
        var before = Native.tas_fields(_host);
        var ticksBefore = Native.tas_ticks(_host);
        var presentedBefore = Native.tas_presentations(_host);
        if (replay == null) Check(Native.tas_step(_host, in input));
        else Check(Native.tas_replay_step(_host, in input, replay.Polls, (nuint)replay.Polls.Length));
        var after = Native.tas_fields(_host);
        if (after <= before || Native.tas_presentations(_host) <= presentedBefore)
            throw new InvalidOperationException("Dolphin returned before presenting the next frame.");
        LastStepFields = after - before;
        var ticks = Native.tas_ticks(_host) - ticksBefore;
        if (replay != null && (replay.Fields != LastStepFields || replay.Ticks != ticks))
            throw new InvalidDataException("Playback desync: presentation boundary timing changed.");
        var count = Native.tas_polls(_host, null, 0);
        if (count > 100000) throw new InvalidDataException("Too many controller polls in one advance.");
        var polls = new InputPoll[(int)count];
        if (Native.tas_polls(_host, polls, count) != count) throw new InvalidDataException("Controller poll capture changed.");
        LastInputPollFrame = new(input, LastStepFields, ticks, polls);
        ++_position;
        PublishMedia();
    }

    public void Reset() { RequireLoaded(); Check(Native.tas_reset(_host)); }

    public EmulatorSnapshot Capture()
    {
        RequireLoaded();
        var size = Native.tas_state_size(_host);
        if (size is 0 or > MaximumStateBytes) throw new InvalidOperationException("Invalid Dolphin state size: " + size);
        var bytes = new byte[(int)size];
        Check(Native.tas_save(_host, bytes, size));
        return new EmulatorSnapshot(Position, bytes, _preview);
    }

    public void Restore(EmulatorSnapshot snapshot)
    {
        RequireLoaded();
        if (snapshot.Data.Length is 0 or > MaximumStateBytes) throw new InvalidDataException("Invalid state payload size.");
        // The core may partially change state on a malformed payload. Restore the known-good backup.
        var backup = Capture();
        if (Native.tas_restore(_host, snapshot.Data, (nuint)snapshot.Data.Length) == 0)
        {
            var error = Error();
            if (Native.tas_restore(_host, backup.Data, (nuint)backup.Data.Length) == 0)
            { Stop(); throw new InvalidOperationException("State restore and rollback failed; emulation stopped. " + error); }
            throw new InvalidDataException(error);
        }
        _position = snapshot.Position;
        _preview = snapshot.Preview;
        if (_preview is not null) VideoReady?.Invoke(_preview);
        AudioReady?.Invoke([], 0);
    }

    public byte[] ReadMemory(uint address, int count)
    {
        RequireLoaded();
        if (count is < 1 or > MaximumStateBytes) throw new ArgumentOutOfRangeException(nameof(count));
        var bytes = new byte[count]; Check(Native.tas_memory(_host, address, bytes, (nuint)count, 0)); return bytes;
    }

    public void WriteMemory(uint address, byte[] bytes)
    { RequireLoaded(); Check(Native.tas_memory(_host, address, bytes, (nuint)bytes.Length, 1)); }

    public void Stop()
    {
        if (_host != 0) Native.tas_destroy(_host);
        _host = 0; _position = 0; _lastVideoSequence = 0; _preview = null;
        AudioReady?.Invoke([], 0);
    }
    public void Dispose() => Stop();
    private void RequireLoaded() { if (!IsLoaded) throw new InvalidOperationException("Open a game first."); }
    private string Error() => Marshal.PtrToStringUTF8(Native.tas_error(_host)) ?? "Unknown native error.";
    private void Check(int success) { if (success == 0) throw new InvalidOperationException(Error()); }

    private void PublishMedia()
    {
        var size = Native.tas_video(_host, null, 0, out var width, out var height, out var sequence);
        if (size > 0 && sequence != _lastVideoSequence)
        {
            var pixels = new byte[checked((int)size)];
            Native.tas_video(_host, pixels, size, out width, out height, out sequence);
            _lastVideoSequence = sequence;
            _preview = new VideoFrame((int)width, (int)height, pixels, (long)sequence);
            VideoReady?.Invoke(_preview);
        }
        var count = Native.tas_audio(_host, _audio, (nuint)_audio.Length, out var rate);
        if (count > 0) AudioReady?.Invoke(_audio[..(int)count], (int)rate);
    }

    private static class Native
    {
        private const string Library = "TasStudio.LibretroHost";
        static Native() => NativeLibrary.SetDllImportResolver(typeof(Native).Assembly, Resolve);
        private static nint Resolve(string name, Assembly assembly, DllImportSearchPath? path) =>
            name == Library ? NativeLibrary.Load(Path.Combine(AppContext.BaseDirectory, "native", Library + ".dll")) : 0;
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] public static extern nint tas_create([MarshalAs(UnmanagedType.LPUTF8Str)] string core, [MarshalAs(UnmanagedType.LPUTF8Str)] string system, [MarshalAs(UnmanagedType.LPUTF8Str)] string saves, int resolution, int dspHle);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] public static extern nint tas_error(nint host);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] public static extern int tas_set_option(nint host, [MarshalAs(UnmanagedType.LPUTF8Str)] string key, [MarshalAs(UnmanagedType.LPUTF8Str)] string value);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] public static extern void tas_destroy(nint host);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] public static extern int tas_load(nint host, [MarshalAs(UnmanagedType.LPUTF8Str)] string game);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] public static extern int tas_step(nint host, in ControllerState input);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] public static extern int tas_replay_step(nint host, in ControllerState input, [In] InputPoll[] polls, nuint count);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] public static extern nuint tas_polls(nint host, [Out] InputPoll[]? polls, nuint capacity);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] public static extern int tas_reset(nint host);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] public static extern ulong tas_fields(nint host);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] public static extern ulong tas_presentations(nint host);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] public static extern ulong tas_ticks(nint host);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] public static extern double tas_fps(nint host);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] public static extern nuint tas_state_size(nint host);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] public static extern int tas_save(nint host, [Out] byte[] bytes, nuint count);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] public static extern int tas_restore(nint host, byte[] bytes, nuint count);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] public static extern int tas_memory(nint host, uint address, [In, Out] byte[] bytes, nuint count, int write);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] public static extern nuint tas_video(nint host, [Out] byte[]? bytes, nuint capacity, out uint width, out uint height, out ulong sequence);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] public static extern nuint tas_audio(nint host, [Out] short[] samples, nuint capacity, out uint rate);
    }
}
