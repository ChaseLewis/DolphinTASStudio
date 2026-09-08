using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia.Input;
using TasStudio.Core;

namespace TasStudio.App;

public static class AppPaths
{
    public static readonly string Data = ResolveDataDirectory(AppContext.BaseDirectory,
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TasStudio"));
    internal static string ResolveDataDirectory(string executableDirectory, string defaultDirectory)
    {
        var marker = Path.Combine(executableDirectory, "tasstudio-data.path");
        if (!File.Exists(marker)) return defaultDirectory;
        var path = File.ReadAllText(marker).Trim();
        if (path.Length == 0) throw new InvalidDataException("tasstudio-data.path must name a data directory.");
        return Path.GetFullPath(path, executableDirectory);
    }
    public static readonly string Settings = Path.Combine(Data, "settings.json");
    public static readonly string States = Path.Combine(Data, "States");
    public static readonly string DolphinUser = Path.Combine(Data, "DolphinUser");
    public static readonly string PlayUser = Path.Combine(Data, "Play", "DolphinUser");
    public static readonly string Core = Path.Combine(AppContext.BaseDirectory, "native", "dolphin_libretro.dll");
    public static readonly string System = Path.Combine(AppContext.BaseDirectory, "system");
}

public enum PadControl { A, B, X, Y, Z, Start, L, R, Up, Down, Left, Right, StickUp, StickDown, StickLeft, StickRight, CUp, CDown, CLeft, CRight, AnalogL, AnalogR }
[Flags]
public enum GamepadButton : ushort
{
    None = 0, Up = 0x0001, Down = 0x0002, Left = 0x0004, Right = 0x0008,
    Start = 0x0010, Back = 0x0020, LeftStick = 0x0040, RightStick = 0x0080,
    LeftShoulder = 0x0100, RightShoulder = 0x0200, A = 0x1000, B = 0x2000, X = 0x4000, Y = 0x8000
}

public sealed class AppSettings
{
    public const int DefaultVolume = 70;
    public const int DefaultDeadZone = 15;
    public const int DefaultTriggerThreshold = 90;
    public int Volume { get; set; } = DefaultVolume;
    public bool Muted { get; set; }
    public UiTheme Theme { get; set; } = UiTheme.System;
    public int GamepadIndex { get; set; } = -1;
    public int DeadZonePercent { get; set; } = DefaultDeadZone;
    public int TriggerClickPercent { get; set; } = DefaultTriggerThreshold;
    public bool InvertStickY { get; set; }
    public bool InvertCStickY { get; set; }
    public string? LastGame { get; set; }
    public TasStudio.Emulation.EmulationConfiguration PlayConfiguration { get; set; } = new();
    public bool WatcherVisible { get; set; }
    public double InspectorWidth { get; set; } = 330;
    public double TimelineHeight { get; set; } = 320;
    public Dictionary<string, string> ResolvedRoms { get; set; } = [];
    public List<RecentProject> RecentProjects { get; set; } = [];
    public Dictionary<PadControl, Key> Keys { get; set; } = DefaultKeys();
    public Dictionary<PadControl, GamepadButton> GamepadButtons { get; set; } = DefaultGamepadButtons();
    public Dictionary<PadControl, GamepadBinding> GamepadBindings { get; set; } = [];
    public static Dictionary<PadControl, Key> DefaultKeys() => new()
    {
        [PadControl.A] = Key.X, [PadControl.B] = Key.Z, [PadControl.X] = Key.C, [PadControl.Y] = Key.S,
        [PadControl.Z] = Key.D, [PadControl.Start] = Key.Enter, [PadControl.L] = Key.Q, [PadControl.R] = Key.W,
        [PadControl.Up] = Key.T, [PadControl.Down] = Key.G, [PadControl.Left] = Key.F, [PadControl.Right] = Key.H,
        [PadControl.StickUp] = Key.Up, [PadControl.StickDown] = Key.Down, [PadControl.StickLeft] = Key.Left, [PadControl.StickRight] = Key.Right,
        [PadControl.CUp] = Key.I, [PadControl.CDown] = Key.K, [PadControl.CLeft] = Key.J, [PadControl.CRight] = Key.L,
        [PadControl.AnalogL] = Key.None, [PadControl.AnalogR] = Key.None
    };
    public static Dictionary<PadControl, GamepadButton> DefaultGamepadButtons() => new()
    {
        [PadControl.A] = GamepadButton.A, [PadControl.B] = GamepadButton.B, [PadControl.X] = GamepadButton.X,
        [PadControl.Y] = GamepadButton.Y, [PadControl.Z] = GamepadButton.RightShoulder, [PadControl.Start] = GamepadButton.Start,
        [PadControl.L] = GamepadButton.LeftShoulder, [PadControl.R] = GamepadButton.None,
        [PadControl.Up] = GamepadButton.Up, [PadControl.Down] = GamepadButton.Down, [PadControl.Left] = GamepadButton.Left, [PadControl.Right] = GamepadButton.Right
    };
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    public static AppSettings Load(out string? warning)
        => Load(AppPaths.Settings, out warning);
    internal static AppSettings Load(string path, out string? warning)
    {
        warning = null;
        if (!File.Exists(path)) return new();
        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), JsonOptions) ?? throw new InvalidDataException("Settings are empty.");
            settings.Volume = Math.Clamp(settings.Volume, 0, 100);
            if (!Enum.IsDefined(settings.Theme)) settings.Theme = UiTheme.System;
            settings.GamepadIndex = Math.Clamp(settings.GamepadIndex, -1, 3);
            settings.DeadZonePercent = Math.Clamp(settings.DeadZonePercent, 0, 95);
            settings.TriggerClickPercent = Math.Clamp(settings.TriggerClickPercent, 1, 100);
            settings.Keys ??= DefaultKeys();
            settings.GamepadButtons ??= DefaultGamepadButtons();
            settings.GamepadBindings ??= [];
            settings.ResolvedRoms ??= [];
            settings.PlayConfiguration = (settings.PlayConfiguration ?? new()).ValidatedCopy();
            settings.RecentProjects = (settings.RecentProjects ?? []).Where(p => p != null && !string.IsNullOrWhiteSpace(p.Path)).Take(50).ToList();
            settings.InspectorWidth = Math.Clamp(settings.InspectorWidth, 300, 550);
            settings.TimelineHeight = Math.Clamp(settings.TimelineHeight, 230, 600);
            return settings;
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or UnauthorizedAccessException)
        {
            warning = $"Settings could not be read; defaults are active. {ex.Message}";
            return new();
        }
    }
    public void Save()
        => Save(AppPaths.Settings);
    internal void Save(string path)
    {
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(temporary, path, true);
    }
}
