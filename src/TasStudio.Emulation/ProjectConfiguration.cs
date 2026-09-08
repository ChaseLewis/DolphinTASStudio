using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace TasStudio.Emulation;

public sealed record CoreSetting(string Key, string Label, string Group, string Default, string[] Values, string[] Labels);

public sealed record EmulationConfiguration
{
    public long StartUtcSeconds { get; init; } = 946684800;
    public SortedDictionary<string, string> Options { get; init; } = new(StringComparer.Ordinal);
    private static CoreSetting Toggle(string key, string label, string group, bool value) =>
        new("dolphin_" + key, label, group, value ? "enabled" : "disabled", ["disabled", "enabled"], ["Off", "On"]);
    private static CoreSetting Choice(string key, string label, string group, string value, string[] values, string[] labels) =>
        new("dolphin_" + key, label, group, value, values, labels);
    public static IReadOnlyList<CoreSetting> Settings { get; } = new CoreSetting[]
    {
        Choice("language", "System language", "General", "1", ["0","1","2","3","4","5"], ["Japanese","English","German","French","Spanish","Italian"]),
        Toggle("main_accurate_cpu_cache", "Accurate CPU cache", "General", false),
        Toggle("skip_gc_bios", "Skip GameCube BIOS", "General", true),
        Choice("efb_scale", "Internal resolution", "Graphics", "1", ["1","2","3","4"], ["1× native","2× native","3× native","4× native"]),
        Choice("aspect_ratio", "Aspect ratio", "Graphics", "3", ["0","1","2","3","6"], ["Auto","16:9","4:3","Stretch","Raw pixels"]),
        Choice("anti_aliasing", "Anti-aliasing", "Graphics", "0", ["0","1","2","3","4","5","6"], ["None","2× MSAA","4× MSAA","8× MSAA","2× SSAA","4× SSAA","8× SSAA"]),
        Choice("shader_compilation_mode", "Shader compilation", "Graphics", "0", ["0","1"], ["Synchronous","Synchronous ubershaders"]),
        Toggle("wait_for_shaders", "Compile shaders before starting", "Graphics", true),
        Choice("force_texture_filtering_mode", "Texture filtering", "Graphics", "0", ["0","1","2"], ["Game default","Nearest","Linear"]),
        Choice("max_anisotropy", "Anisotropic filtering", "Graphics", "0", ["0","1","2","3","4"], ["1×","2×","4×","8×","16×"]),
        Toggle("disable_copy_filter", "Disable copy filter", "Graphics", true),
        Toggle("force_true_color", "Force 24-bit color", "Graphics", true),
        Toggle("efb_scaled_copy", "Scaled EFB copies", "Graphics", true),
        Choice("texture_cache_accuracy", "Texture cache accuracy", "Compatibility", "128", ["0","512","128"], ["Safe","Medium","Fast"]),
        Toggle("efb_access_enable", "Allow CPU access to EFB", "Compatibility", false),
        Toggle("efb_emulate_format_changes", "Emulate EFB format changes", "Compatibility", false),
        Toggle("efb_to_texture", "Store EFB copies to texture only", "Compatibility", true),
        Toggle("defer_efb_copies", "Defer EFB copies to RAM", "Compatibility", true),
        Toggle("xfb_to_texture_enable", "Store XFB copies to texture only", "Compatibility", true),
        Toggle("bbox_enabled", "Emulate bounding box", "Compatibility", false),
        Toggle("fast_depth_calculation", "Fast depth calculation", "Compatibility", true),
        Toggle("gpu_texture_decoding", "GPU texture decoding", "Compatibility", false),
        Toggle("dsp_hle", "DSP HLE (off uses LLE)", "Audio", true),
        Toggle("dsp_jit", "DSP JIT", "Audio", true)
    };
    public string Value(string key) => Options.GetValueOrDefault(key) ?? Settings.Single(s => s.Key == key).Default;
    public EmulationConfiguration ValidatedCopy()
    {
        if (StartUtcSeconds is < 0 or > uint.MaxValue) throw new InvalidDataException("Start UTC must be between 1970 and February 2106.");
        if (Options == null || Options.Keys.Any(k => !Settings.Any(s => s.Key == k))) throw new InvalidDataException("Unknown project emulation setting.");
        var values = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var setting in Settings)
        {
            var value = Value(setting.Key);
            if (!setting.Values.Contains(value)) throw new InvalidDataException("Unsupported value for " + setting.Label);
            values.Add(setting.Key, value);
        }
        return this with { Options = values };
    }
    public string Fingerprint => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(ValidatedCopy(), new JsonSerializerOptions { IgnoreReadOnlyProperties = true })));
    public int Resolution => int.Parse(Value("dolphin_efb_scale"), CultureInfo.InvariantCulture);
    public bool DspHle => Value("dolphin_dsp_hle") == "enabled";
    public static EmulationConfiguration FromLegacy(int resolution, bool dsp) => new()
    {
        Options = new(StringComparer.Ordinal) { ["dolphin_efb_scale"] = resolution.ToString(CultureInfo.InvariantCulture), ["dolphin_dsp_hle"] = dsp ? "enabled" : "disabled" }
    };
}

public enum CheckpointRetention { OldestCreated, LeastRecentlyUsed }
public sealed record CheckpointPolicy(bool Enabled = true, int IntervalSeconds = 60, int MaximumCount = 300,
    CheckpointRetention Retention = CheckpointRetention.OldestCreated, int DiskBudgetMiB = 4096)
{
    public void Validate()
    {
        if (IntervalSeconds is < 1 or > 86400 || MaximumCount is < 1 or > 10000 || DiskBudgetMiB is < 1 or > 1048576 || !Enum.IsDefined(Retention))
            throw new InvalidDataException("Invalid checkpoint interval, count, budget or retention policy.");
    }
}
