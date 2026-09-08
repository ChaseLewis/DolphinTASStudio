using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using TasStudio.Core;
using Velopack;
using Velopack.Locators;

namespace TasStudio.App;

internal enum ReleaseChannel { Stable, Beta, Alpha }

internal static class ReleaseChannels
{
    public static string Feed(this ReleaseChannel channel) => $"win-x64-{channel.ToString().ToLowerInvariant()}";
    public static ReleaseChannel? FromFeed(string? feed) => Enum.GetValues<ReleaseChannel>()
        .Cast<ReleaseChannel?>().FirstOrDefault(c => c!.Value.Feed() == feed);
    public static bool Matches(this ReleaseChannel channel, string version)
    {
        var suffix = version.Split('+')[0].Split('-', 2);
        return channel == ReleaseChannel.Stable ? suffix.Length == 1 :
            suffix.Length == 2 && suffix[1].StartsWith(channel.ToString().ToLowerInvariant() + ".", StringComparison.Ordinal);
    }
}

internal sealed class UpdatePreferences
{
    public bool Automatic { get; set; } = true;
    public ReleaseChannel Channel { get; set; }
    public VelopackAsset? Pending { get; set; }
}

internal sealed class AppUpdates : IDisposable
{
    public const string Repository = "https://github.com/ChaseLewis/DolphinTASStudio";
    public const string PackageId = "DolphinTASStudio";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter(), new VersionConverter() } };
    private readonly Func<ReleaseChannel, UpdateManager> _manager;
    private readonly string _preferencesPath, _packagesDirectory;
    private readonly CancellationTokenSource _cancel = new();
    private readonly UpdatePreferences _preferences;
    private bool _disposed;
    public bool Enabled { get; }
    public string CurrentVersion { get; }
    public bool Busy { get; private set; }
    public string Status { get; private set; }
    public string? PendingVersion => _preferences.Pending?.Version.ToString();
    public ReleaseChannel Channel => _preferences.Channel;
    public bool Automatic => _preferences.Automatic;
    public event Action? Changed;

    public static AppUpdates Create()
    {
        var locator = VelopackLocator.Current;
        var channel = ReleaseChannels.FromFeed(locator.Channel);
        var enabled = locator.CurrentlyInstalledVersion != null && channel != null && locator.AppId == PackageId;
        // A developer data override inside current/ would be replaced along with the application.
        var relativeData = Path.GetRelativePath(AppContext.BaseDirectory, AppPaths.Data);
        if (!Path.IsPathRooted(relativeData) && relativeData != ".." && !relativeData.StartsWith(".." + Path.DirectorySeparatorChar)) enabled = false;
        var directory = RuntimeActivity.UpdateDirectory(AppContext.BaseDirectory);
        return new(c => new UpdateManager(new ChannelGithubSource(c),
                new UpdateOptions { ExplicitChannel = c.Feed(), AllowVersionDowngrade = false }),
            Path.Combine(directory, "preferences.json"), locator.PackagesDir ?? directory,
            locator.CurrentlyInstalledVersion?.ToString() ?? "Development build", channel ?? ReleaseChannel.Stable, enabled);
    }

    internal AppUpdates(Func<ReleaseChannel, UpdateManager> manager, string preferencesPath, string packagesDirectory,
        string currentVersion, ReleaseChannel packagedChannel, bool enabled = true)
    {
        _manager = manager; _preferencesPath = preferencesPath; _packagesDirectory = packagesDirectory;
        CurrentVersion = currentVersion; Enabled = enabled;
        _preferences = new() { Channel = packagedChannel };
        try
        {
            if (File.Exists(preferencesPath)) _preferences = JsonSerializer.Deserialize<UpdatePreferences>(File.ReadAllText(preferencesPath), JsonOptions) ?? _preferences;
            if (!Enum.IsDefined(_preferences.Channel)) _preferences.Channel = packagedChannel;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { }
        if (_preferences.Pending is { } pending && (!Accepts(pending) || !File.Exists(PackagePath(pending)))) _preferences.Pending = null;
        Status = !Enabled ? "Updates are available in installed releases and release portable ZIPs." :
            PendingVersion is { } version ? $"Version {version} is ready. It will install when Studio closes and all workers have finished." : "Updates have not been checked yet.";
    }

    public void Configure(ReleaseChannel channel, bool automatic)
    {
        if (Busy) throw new InvalidOperationException("Wait for the update check or download to finish.");
        if (!Enum.IsDefined(channel)) throw new ArgumentOutOfRangeException(nameof(channel));
        var previous = (_preferences.Channel, _preferences.Automatic, _preferences.Pending);
        _preferences.Channel = channel; _preferences.Automatic = automatic;
        if (channel != previous.Channel) _preferences.Pending = null;
        try { Save(); }
        catch { (_preferences.Channel, _preferences.Automatic, _preferences.Pending) = previous; throw; }
        SetStatus(PendingVersion == null ? $"Following {channel.ToString().ToLowerInvariant()} releases. Only newer versions will be installed." : $"Version {PendingVersion} is ready to install.");
    }

    public async Task CheckAsync()
    {
        if (!Enabled || Busy || _disposed) return;
        Busy = true; SetStatus("Checking GitHub for updates…");
        try
        {
            var manager = _manager(Channel);
            var update = await manager.CheckForUpdatesAsync();
            _cancel.Token.ThrowIfCancellationRequested();
            if (update == null)
            {
                SetStatus(PendingVersion == null ? $"No newer {Channel.ToString().ToLowerInvariant()} release is available." : $"Version {PendingVersion} is ready to install.");
                return;
            }
            if (!Accepts(update.TargetFullRelease)) throw new InvalidDataException("The release does not match this application's channel or package identity.");
            // Velopack reuses cached full packages; remove a damaged cache entry before asking it to download.
            if (File.Exists(PackagePath(update.TargetFullRelease)))
            {
                try { await VerifyPackage(update.TargetFullRelease, _cancel.Token); }
                catch (InvalidDataException) { File.Delete(PackagePath(update.TargetFullRelease)); }
            }
            SetStatus($"Downloading {update.TargetFullRelease.Version}…");
            await manager.DownloadUpdatesAsync(update, progress => SetStatus($"Downloading {update.TargetFullRelease.Version}… {progress}%"), _cancel.Token);
            await VerifyPackage(update.TargetFullRelease, _cancel.Token);
            _cancel.Token.ThrowIfCancellationRequested();
            var previous = _preferences.Pending;
            _preferences.Pending = update.TargetFullRelease with { };
            try { Save(); }
            catch { _preferences.Pending = previous; throw; }
            SetStatus($"Version {PendingVersion} is ready. It will install when Studio closes and all workers have finished.");
        }
        catch (OperationCanceledException) when (_cancel.IsCancellationRequested) { }
        catch (Exception error) { SetStatus($"Update check failed: {error.Message}" + (PendingVersion != null ? $" Downloaded version {PendingVersion} is still ready." : "")); }
        finally { Busy = false; if (!_disposed) Changed?.Invoke(); }
    }

    public bool ApplyAfterShutdown(RuntimeActivity activity, bool restart)
    {
        if (!Enabled || Busy || _preferences.Pending is not { } pending || (!Automatic && !restart)) return false;
        try
        {
            if (!Accepts(pending)) return false;
            VerifyPackage(pending, CancellationToken.None).GetAwaiter().GetResult();
            return activity.TryStartUpdate(() =>
            {
                // Another Studio instance may have changed channels or disabled automatic updates.
                // Read again under the startup gate, after all other runtime leases are gone.
                var saved = JsonSerializer.Deserialize<UpdatePreferences>(File.ReadAllText(_preferencesPath), JsonOptions);
                if (saved == null || saved.Channel != Channel || (!saved.Automatic && !restart) ||
                    saved.Pending?.FileName != pending.FileName || saved.Pending.SHA256 != pending.SHA256)
                    throw new InvalidOperationException("Update preferences changed in another Studio window. Open Studio to review the selected update.");
                _manager(Channel).WaitExitThenApplyUpdates(pending, silent: !restart, restart: restart);
            });
        }
        catch (Exception error)
        {
            SetStatus($"Update installation deferred: {error.Message}");
            try { File.WriteAllText(Path.Combine(Path.GetDirectoryName(_preferencesPath)!, "last-error.txt"), Status); }
            catch (Exception logError) when (logError is IOException or UnauthorizedAccessException) { }
            return false;
        }
    }

    private bool Accepts(VelopackAsset asset) => asset.PackageId == PackageId && asset.Type == VelopackAssetType.Full &&
        asset.Version != null && Channel.Matches(asset.Version.ToString()) &&
        SemanticVersion.TryParse(CurrentVersion, out var current) && asset.Version > current &&
        !string.IsNullOrEmpty(asset.FileName) && Path.GetFileName(asset.FileName) == asset.FileName &&
        asset.FileName.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase) && asset.Size > 0 &&
        asset.SHA256 is { Length: 64 } && asset.SHA256.All(Uri.IsHexDigit);

    private string PackagePath(VelopackAsset asset) => Path.Combine(_packagesDirectory, asset.FileName);
    private async Task VerifyPackage(VelopackAsset asset, CancellationToken token)
    {
        using var file = File.OpenRead(PackagePath(asset));
        if (file.Length != asset.Size || !Convert.ToHexString(await SHA256.HashDataAsync(file, token).ConfigureAwait(false)).Equals(asset.SHA256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The downloaded update failed its checksum. Check for updates again to download a fresh copy.");
    }
    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_preferencesPath)!);
        var temporary = _preferencesPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(_preferences, JsonOptions)); File.Move(temporary, _preferencesPath, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private void SetStatus(string status) { Status = status; if (!_disposed) Changed?.Invoke(); }
    public void Dispose() { _disposed = true; _cancel.Cancel(); }

    private sealed class VersionConverter : JsonConverter<SemanticVersion>
    {
        public override SemanticVersion Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) =>
            SemanticVersion.TryParse(reader.GetString(), out var version) ? version : throw new JsonException("Invalid update version.");
        public override void Write(Utf8JsonWriter writer, SemanticVersion value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString());
    }
}
