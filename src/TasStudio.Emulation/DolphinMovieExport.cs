using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TasStudio.Core;

namespace TasStudio.Emulation;

public sealed record DolphinMovieOrigin(string GameId, ulong Ticks, ulong Fields);
public sealed record DolphinMovieExportResult(string MoviePath, string InstructionsPath, ulong InputCount)
{
    public string LauncherPath => Path.Combine(Path.GetDirectoryName(InstructionsPath)!, "Play in Dolphin.cmd");
}

/// <summary>Experimental power-on GameCube DTM export. No Studio state is embedded or converted.</summary>
public static class DolphinMovieExport
{
    private static readonly PadButtons[] DtmButtons = [PadButtons.Start, PadButtons.A, PadButtons.B, PadButtons.X,
        PadButtons.Y, PadButtons.Z, PadButtons.Up, PadButtons.Down, PadButtons.Left, PadButtons.Right, PadButtons.L, PadButtons.R];
    public static DolphinMovieExportResult Save(string path, DolphinMovieOrigin origin, InputPollFrame[] frames,
        EmulationConfiguration configuration, string gamePath, string gameHash, string backendIdentity,
        GameCubeMovieCard[]? initialCards = null, DolphinMovieSettings? movieSettings = null,
        InputPollFrame? bootInputs = null, DolphinMovieExportMode mode = DolphinMovieExportMode.Full)
    {
        var config = configuration.ValidatedCopy();
        initialCards ??= [];
        foreach (var card in initialCards)
            if (Path.GetFileName(card.FileName) != card.FileName || !card.FileName.StartsWith("MemoryCardA.", StringComparison.Ordinal) ||
                !card.FileName.EndsWith(".raw", StringComparison.Ordinal) || card.Bytes.Length is 0 or > 16 * 1024 * 1024)
                throw new InvalidDataException("Invalid initial memory card export.");
        if (origin.GameId.Length != 6 || origin.GameId.Any(c => !char.IsAsciiLetterOrDigit(c)))
            throw new InvalidDataException("Invalid GameCube game ID.");
        if (bootInputs != null)
        {
            bootInputs.Validate();
            if (bootInputs.Ticks != origin.Ticks || bootInputs.Fields != origin.Fields)
                throw new InvalidDataException("Recovered startup inputs do not match the movie's initial boundary.");
        }
        else if (origin.Ticks != 0 || origin.Fields != 0)
            throw new NotSupportedException("Startup controller polls must be recovered before exporting this power-on movie.");
        var allFrames = bootInputs == null ? frames : new[] { bootInputs }.Concat(frames).ToArray();
        ulong ticks = 0, fields = 0, inputs = 0, lastInputTick = 0;
        ulong polledFields = 0;
        ulong? lastPolledField = null;
        foreach (var frame in allFrames)
        {
            frame.Validate();
            foreach (var poll in frame.Polls)
            {
                var field = checked(fields + poll.FieldOffset);
                if (lastPolledField != field) { polledFields++; lastPolledField = field; }
                lastInputTick = checked(ticks + poll.TickOffset);
                inputs++;
            }
            ticks = checked(ticks + frame.Ticks);
            fields = checked(fields + frame.Fields);
        }
        if (inputs == 0) throw new InvalidOperationException("The movie contains no controller polls to export.");
        // A poll after the final field callback does not make that completed VI non-lagged.
        if (lastPolledField == fields) polledFields--;

        var header = new byte[256];
        "DTM\u001a"u8.CopyTo(header);
        Encoding.ASCII.GetBytes(origin.GameId).CopyTo(header, 4);
        header[0x0B] = 1; // GC port 1; bWii and bFromSaveState remain false.
        U64(header, 0x0D, fields);
        U64(header, 0x15, inputs);
        U64(header, 0x1D, fields - polledFields);
        Encoding.UTF8.GetBytes("Dolphin TAS Studio").CopyTo(header, 0x31);
        using (var game = File.OpenRead(gamePath)) MD5.HashData(game).CopyTo(header, 0x71);
        U64(header, 0x81, checked((ulong)config.StartUtcSeconds));
        movieSettings ??= DolphinMovieSettings.Create(config, origin.GameId);
        movieSettings.WriteHeader(header);
        U64(header, 0xED, lastInputTick);

        var destination = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var token = Guid.NewGuid().ToString("N");
        var temporary = destination + "." + token + ".tmp";
        // Every export gets a fresh profile so an earlier test's card writes cannot contaminate it.
        var companion = destination + ".dolphin-" + token[..8];
        var user = Path.Combine(companion, "User");
        var instructions = Path.Combine(companion, "README.txt");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(header);
                Span<byte> record = stackalloc byte[8];
                foreach (var frame in allFrames)
                    foreach (var poll in frame.Polls)
                    {
                        EncodeController(poll.Input, record);
                        stream.Write(record);
                    }
                stream.Flush(flushToDisk: true);
            }
            Directory.CreateDirectory(Path.Combine(user, "Config"));
            foreach (var card in initialCards)
            {
                Directory.CreateDirectory(Path.Combine(companion, "InitialCards"));
                Directory.CreateDirectory(Path.Combine(user, "GC"));
                File.WriteAllBytes(Path.Combine(companion, "InitialCards", card.FileName), card.Bytes);
                File.WriteAllBytes(Path.Combine(user, "GC", card.FileName), card.Bytes);
            }
            File.WriteAllText(Path.Combine(user, "Config", "Dolphin.ini"), DolphinConfig(config));
            File.WriteAllText(Path.Combine(user, "Config", "GFX.ini"), GraphicsConfig(config));
            File.WriteAllText(Path.Combine(companion, "export.json"), JsonSerializer.Serialize(new
            {
                Format = "experimental-dtm-v3", ExportMode = mode.ToString(), origin.GameId, GameSha256 = gameHash, BackendIdentity = backendIdentity,
                MovieSettings = movieSettings,
                Configuration = config, InitialTicks = origin.Ticks, InitialFields = origin.Fields,
                InputCount = inputs, FrameCount = fields, LastInputTick = lastInputTick,
                BootPollsIncluded = true, BootPollCount = bootInputs?.Polls.Length ?? 0,
                InitialMemoryCardIncluded = initialCards.Length > 0,
                MemoryCards = initialCards.Select(card => new { card.FileName, Sha256 = Convert.ToHexString(SHA256.HashData(card.Bytes)) })
            }, new JsonSerializerOptions { WriteIndented = true }));
            File.WriteAllText(instructions, $"""
                Experimental Dolphin movie export

                Movie: {destination}
                Game: {Path.GetFullPath(gamePath)}
                Game ID: {origin.GameId}
                Export: {mode} — {(mode == DolphinMovieExportMode.Quick ? "used current cached controller polls" : "baked the full active timeline")}.
                Inputs: {inputs} controller polls; {fields} video fields including boot.

                Double-click "Play in Dolphin.cmd" in this folder. Select your standalone
                Dolphin.exe the first time. The launcher uses the supplied profile and restores
                its original card copies before each test. Close that Dolphin session before retrying.
                Opening the DTM from your normal Dolphin profile does not apply all settings or these cards.

                Or launch standalone Dolphin manually from PowerShell using this isolated profile:
                & 'C:\path\to\Dolphin.exe' -u {Quote(user)} -e {Quote(Path.GetFullPath(gamePath))} -m {Quote(destination)}

                Replace only the Dolphin.exe path. The profile carries Studio's requested settings.
                Alternatively launch Dolphin with -u pointing to the User folder above, then use
                Movie > Play Input Recording and select the DTM with the same game available.
                The DTM uses the project's UTC epoch during movie playback. No savestate is used.

                First comparison target for the currently pinned libretro core:
                upstream Dolphin commit 430138f468effe6bf396adf3cd4d46df4cbd9050 (2606 + 282 commits).
                Stock 2606 may also be tried, but is an earlier upstream version.

                Known limitations of this first test:
                - Includes {bootInputs?.Polls.Length ?? 0} startup controller polls recovered by rebooting
                  with the initial card/settings and verifying the original boundary and RAM.
                  The original boundary is tick {origin.Ticks}, field {origin.Fields}.
                - {(initialCards.Length > 0 ? "Original Slot A card bytes are included in InitialCards and copied into User/GC." : "No original memory card was available; Dolphin will format a new Slot A card.")}
                  {(initialCards.Length > 0 ? "Before repeating the test, close Dolphin and copy InitialCards back over User/GC." : "A new card can differ in formatting timestamps, serials and save contents.")}
                - DTM-supported settings include system/local game INI overrides. Other local
                  overrides, BIOS and DSP ROM files are not bundled; match these separately if used.
                - Inputs retain poll order and exact pad values, but DTM has no per-poll timestamps
                  or desync checks. A movie opening successfully does not prove synchronization.
                - DTM ends at its last input; trailing groups without polls are not held afterward.

                Compare gameplay at recognizable points and report the first divergence.
                Start each repeat with the same initial card; Dolphin writes cards during playback.
                """);
            DolphinMovieLauncher.Save(destination, companion, gamePath);
            File.Move(temporary, destination, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return new(destination, instructions, inputs);
    }

    private static string Quote(string value) => "'" + value.Replace("'", "''") + "'";
    private static void U64(byte[] header, int offset, ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(offset), value);

    private static void EncodeController(ControllerState pad, Span<byte> bytes)
    {
        // Movie::ControllerState bit order differs from GCPadStatus / Studio PadButtons.
        ushort packed = 1 << 14; // Controller connected. Disc/reset/get-origin bits are unset.
        for (var bit = 0; bit < DtmButtons.Length; bit++)
            if ((pad.Buttons & DtmButtons[bit]) != 0) packed |= (ushort)(1 << bit);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, packed);
        bytes[2] = pad.TriggerL; bytes[3] = pad.TriggerR;
        bytes[4] = pad.StickX; bytes[5] = pad.StickY;
        bytes[6] = pad.CStickX; bytes[7] = pad.CStickY;
    }

    private static string DolphinConfig(EmulationConfiguration config) => $"""
        [Core]
        CPUThread = False
        CPUCore = 1
        GFXBackend = D3D
        DSPHLE = {config.DspHle}
        SkipIPL = {Enabled(config, "skip_gc_bios")}
        AccurateCPUCache = {Enabled(config, "main_accurate_cpu_cache")}
        SelectedLanguage = {Math.Max(0, Number(config, "language") - 1)}
        EnableCustomRTC = True
        CustomRTCValue = {config.StartUtcSeconds}
        SlotA = 1
        SlotB = 255
        SerialPort1 = 255
        MemoryCardSize = {config.MemoryCardSizeOverride ?? -1}
        SIDevice0 = 6
        SIDevice1 = 0
        SIDevice2 = 0
        SIDevice3 = 0
        EnableCheats = False
        OverclockEnable = False
        FastDiscSpeed = False
        SyncGPU = False
        PrecisionFrameTiming = False
        RushFramePresentation = False
        SmoothEarlyPresentation = False
        [DSP]
        EnableJIT = {Enabled(config, "dsp_jit")}
        """;

    private static string GraphicsConfig(EmulationConfiguration config)
    {
        var aa = Number(config, "anti_aliasing");
        return $"""
            [Settings]
            InternalResolution = {config.Resolution}
            AspectRatio = {Number(config, "aspect_ratio")}
            SafeTextureCacheColorSamples = {Number(config, "texture_cache_accuracy")}
            MSAA = {(aa == 0 ? 1 : 1 << ((aa - 1) % 3 + 1))}
            SSAA = {aa >= 4}
            ShaderCompilationMode = {Number(config, "shader_compilation_mode")}
            WaitForShadersBeforeStarting = {Enabled(config, "wait_for_shaders")}
            BackendMultithreading = False
            FastDepthCalc = {Enabled(config, "fast_depth_calculation")}
            EnableGPUTextureDecoding = {Enabled(config, "gpu_texture_decoding")}
            [Enhancements]
            ForceTextureFiltering = {Number(config, "force_texture_filtering_mode")}
            MaxAnisotropy = {Number(config, "max_anisotropy")}
            DisableCopyFilter = {Enabled(config, "disable_copy_filter")}
            ForceTrueColor = {Enabled(config, "force_true_color")}
            [Hacks]
            EFBAccessEnable = {Enabled(config, "efb_access_enable")}
            EFBEmulateFormatChanges = {Enabled(config, "efb_emulate_format_changes")}
            EFBToTextureEnable = {Enabled(config, "efb_to_texture")}
            DeferEFBCopies = {Enabled(config, "defer_efb_copies")}
            XFBToTextureEnable = {Enabled(config, "xfb_to_texture_enable")}
            EFBScaledCopy = {Enabled(config, "efb_scaled_copy")}
            BBoxEnable = {Enabled(config, "bbox_enabled")}
            ForceProgressive = True
            ImmediateXFBEnable = False
            SkipDuplicateXFBs = True
            EarlyXFBOutput = True
            VISkip = False
            """;
    }

    private static bool Enabled(EmulationConfiguration config, string key) => config.Value("dolphin_" + key) == "enabled";
    private static int Number(EmulationConfiguration config, string key) => int.Parse(config.Value("dolphin_" + key), CultureInfo.InvariantCulture);
}
