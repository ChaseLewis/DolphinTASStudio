using System.Globalization;
using System.Runtime.Intrinsics.X86;
using System.Text;

namespace TasStudio.Emulation;

/// <summary>The subset of effective GameCube settings represented by Dolphin's DTM header.</summary>
public sealed record DolphinMovieSettings(bool DualCore, bool DspHle, byte CpuCore, bool FastDiscSpeed,
    bool SyncGpu, bool Progressive, bool Pal60, byte Language, bool EfbAccess, bool SkipEfbCopyToRam,
    bool EfbFormatChanges, bool ImmediateXfb, bool SkipXfbCopyToRam, bool FollowBranch, bool UseFma,
    bool Widescreen)
{
    public static DolphinMovieSettings Create(EmulationConfiguration configuration, string gameId,
        byte discRevision = 0, string? systemGameSettings = null, string? localGameSettings = null)
    {
        var config = configuration.ValidatedCopy();
        bool Option(string key) => config.Value("dolphin_" + key) == "enabled";
        var overrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var names = new[] { gameId[..1], gameId[..3], gameId, gameId + "r" + discRevision };
        // Same precedence as GameConfigLoader: system then local, broad ID then disc revision.
        foreach (var directory in new[] { systemGameSettings, localGameSettings })
        {
            if (directory == null) continue;
            foreach (var name in names)
            {
                var path = Path.Combine(directory, name + ".ini");
                if (!File.Exists(path)) continue;
                var section = "";
                foreach (var raw in File.ReadLines(path))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line[0] is '#' or ';') continue;
                    if (line[0] == '[' && line.Contains(']')) { section = line[1..line.IndexOf(']')]; continue; }
                    var equal = line.IndexOf('=');
                    if (equal < 0) continue;
                    var key = section + "/" + line[..equal].Trim();
                    // Dolphin's legacy aliases for GC language and progressive/PAL60 settings.
                    key = key.Replace("Dolphin.Core/", "Core/", StringComparison.OrdinalIgnoreCase)
                        .Replace("Graphics.Hacks/", "Video_Hacks/", StringComparison.OrdinalIgnoreCase);
                    if (key.Equals("Core/GameCubeLanguage", StringComparison.OrdinalIgnoreCase)) key = "Core/SelectedLanguage";
                    overrides[key] = line[(equal + 1)..].Split('#', ';')[0].Trim();
                }
            }
        }
        bool Flag(string key, bool fallback)
        {
            if (!overrides.TryGetValue(key, out var value)) return fallback;
            if (bool.TryParse(value, out var flag)) return flag;
            return value switch { "1" => true, "0" => false, _ => fallback };
        }
        byte Number(string key, byte fallback) => overrides.TryGetValue(key, out var value) &&
            byte.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : fallback;
        var language = (byte)Math.Max(0, int.Parse(config.Value("dolphin_language"), CultureInfo.InvariantCulture) - 1);
        return new(Flag("Core/CPUThread", false), Flag("Core/DSPHLE", config.DspHle), Number("Core/CPUCore", 1),
            Flag("Core/FastDiscSpeed", false), Flag("Core/SyncGPU", false), Flag("Core/ProgressiveScan", true),
            Flag("Core/PAL60", true), Number("Core/SelectedLanguage", language),
            Flag("Video_Hacks/EFBAccessEnable", Option("efb_access_enable")),
            Flag("Video_Hacks/EFBToTextureEnable", Option("efb_to_texture")),
            Flag("Video_Hacks/EFBEmulateFormatChanges", Option("efb_emulate_format_changes")),
            Flag("Video_Hacks/ImmediateXFBEnable", false), Flag("Video_Hacks/XFBToTextureEnable", Option("xfb_to_texture_enable")),
            Flag("Core/JITFollowBranch", true), Fma.IsSupported, Flag("Wii/Widescreen", true));
    }

    public void WriteHeader(Span<byte> header)
    {
        if (header.Length < 256 || !header[..4].SequenceEqual("DTM\u001a"u8))
            throw new InvalidDataException("Expected a Dolphin DTM header.");
        header.Slice(0x51, 16).Clear();
        Encoding.ASCII.GetBytes("D3D").CopyTo(header[0x51..]);
        header.Slice(0x89, 0x1B).Clear();
        // A complete settings block avoids Movie::GetSettings before game metadata is initialized.
        header[0x89] = 1;
        header[0x8A] = 1; // Historical SkipIdle setting.
        header[0x8B] = Bit(DualCore);
        header[0x8C] = Bit(Progressive);
        header[0x8D] = Bit(DspHle);
        header[0x8E] = Bit(FastDiscSpeed);
        header[0x8F] = CpuCore;
        header[0x90] = Bit(EfbAccess);
        header[0x91] = 1; // Historical EFBCopyEnable setting.
        header[0x92] = Bit(SkipEfbCopyToRam);
        header[0x94] = Bit(EfbFormatChanges);
        header[0x95] = Bit(ImmediateXfb);
        header[0x96] = Bit(SkipXfbCopyToRam);
        header[0x97] = 1; // Raw Slot A card. ClearSave stays false to retain the supplied card.
        header[0x9A] = Bit(SyncGpu);
        header[0x9C] = Bit(Pal60);
        header[0x9D] = Language;
        header[0x9F] = Bit(FollowBranch);
        header[0xA0] = Bit(UseFma);
        header[0xA2] = Bit(Widescreen);
    }

    private static byte Bit(bool value) => value ? (byte)1 : (byte)0;
}
