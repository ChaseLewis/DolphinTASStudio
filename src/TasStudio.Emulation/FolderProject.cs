using System.Security.Cryptography;
using System.Text.Json;
using TasStudio.Core;

namespace TasStudio.Emulation;

public sealed record ProjectAsset(string Path, string Sha256);
public sealed record TakeAsset(string Id, string Name, int Start, string BaselineHash, string Provenance, ProjectAsset[] Inputs, string? EventsHash = null);
public sealed record StateAsset(string Id, string Name, ulong Position, string HistoryHash, ProjectAsset Data);
public sealed record MovieData(ProjectAsset[] Inputs, ExecutionEvent[] Events, TakeAsset[] Takes, TimelineSection[] Sections)
{
    public ProjectAsset[] PollFrames { get; init; } = [];
}
public sealed record ProjectManifest(int Version, string Id, ArchiveMetadata Environment, ProjectAsset InitialState,
    ProjectAsset Timeline, StateAsset[] States)
{
    public CheckpointAsset[] AutomaticCheckpoints { get; init; } = [];
}
public sealed record CheckpointAsset(StateAsset State, double Seconds, long Created, long LastUse);
public sealed record FolderProjectContent(ArchiveContent Archive, InputTake[] Takes, TimelineSection[] Sections, SavedStateReference[] States, bool Legacy)
{
    public CheckpointReference[] AutomaticCheckpoints { get; init; } = [];
}

/// <summary>Immutable assets are committed before the small manifest; an interrupted save leaves the old root usable.</summary>
public static class FolderProject
{
    public const int Version = 3;
    public const int FramesPerChunk = 4096;
    public const int MaximumFrames = 5_000_000;
    private const int MaximumJsonBytes = 64 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public static bool IsLegacy(string path)
    {
        using var stream = File.OpenRead(path); return stream.ReadByte() == 'P' && stream.ReadByte() == 'K';
    }
    public static FolderProjectContent Load(string path)
    {
        if (IsLegacy(path)) return new(ProjectArchive.Load(path, ProjectArchive.ProjectKind), [], [], [], true);
        var manifest = ReadJson<ProjectManifest>(path);
        if (manifest.Version is not (2 or Version)) throw new InvalidDataException("Unsupported folder project version.");
        if (manifest.Environment == null || manifest.InitialState == null || manifest.Timeline == null || manifest.States == null) throw new InvalidDataException("Incomplete project manifest.");
        var root = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var movie = ReadJson<MovieData>(Resolve(root, manifest.Timeline));
        if (movie.Inputs == null || movie.Takes == null || movie.Events == null || movie.Sections == null) throw new InvalidDataException("Incomplete movie metadata.");
        if (movie.Takes.Length > 1000 || manifest.States.Length > 10000) throw new InvalidDataException("Project asset count exceeds supported limits.");
        var inputs = ReadInputs(root, movie.Inputs);
        var initialContent = ProjectArchive.Load(Resolve(root, manifest.InitialState), ProjectArchive.StateKind);
        var initial = initialContent.InitialState;
        if (movie.PollFrames == null || movie.PollFrames.Length > MaximumFrames)
            throw new InvalidDataException("Invalid poll assets.");
        var pollFrames = new List<RecordedInputFrame>();
        foreach (var asset in movie.PollFrames)
        {
            var chunk = ReadJson<RecordedInputFrame[]>(Resolve(root, asset));
            if (chunk.Length > FramesPerChunk || pollFrames.Count + chunk.Length > MaximumFrames)
                throw new InvalidDataException("Too many poll groups.");
            pollFrames.AddRange(chunk);
        }
        var metadata = manifest.Environment with { Inputs = inputs, Events = movie.Events, PollFrames = pollFrames.ToArray() };
        ProjectArchive.ValidateMetadata(metadata, ProjectArchive.ProjectKind);
        if (metadata.Configuration?.Fingerprint != initialContent.Metadata.Configuration?.Fingerprint || metadata.ConfigurationIdentity != initialContent.Metadata.ConfigurationIdentity)
            throw new InvalidDataException("Baseline settings do not match the project.");
        if (initial.Position != 0 || metadata.StateHash != Convert.ToHexString(SHA256.HashData(initial.Data)))
            throw new InvalidDataException("Project initial state identity does not match.");
        var takes = movie.Takes.Select(t => new InputTake(t.Id, t.Name, t.Start, ReadInputs(root, t.Inputs), t.BaselineHash, t.Provenance) { EventsHash = t.EventsHash }).ToArray();
        if (takes.Any(t => t.Start < 0 || t.Inputs.Length == 0 || (long)t.Start + t.Inputs.Length > MaximumFrames || string.IsNullOrWhiteSpace(t.Id)) ||
            takes.Select(t => t.Id).Distinct().Count() != takes.Length ||
            movie.Sections.Any(s => s.Start < 0 || s.Length <= 0 || (long)s.Start + s.Length > inputs.Length))
            throw new InvalidDataException("Invalid take or timeline section.");
        var states = manifest.States.Select(s => new SavedStateReference(s.Id, s.Name, s.Position, s.HistoryHash, Resolve(root, s.Data))).ToArray();
        if (manifest.AutomaticCheckpoints == null || manifest.AutomaticCheckpoints.Length > 10000) throw new InvalidDataException("Invalid automatic checkpoint list.");
        var automatic = manifest.AutomaticCheckpoints.Select(c =>
        {
            if (c.State == null || !double.IsFinite(c.Seconds) || c.Seconds < 0 || c.State.Position > (ulong)inputs.Length)
                throw new InvalidDataException("Invalid automatic checkpoint metadata.");
            var s = c.State;
            return new CheckpointReference(new(s.Id, s.Name, s.Position, s.HistoryHash, Resolve(root, s.Data)), c.Seconds, c.Created, c.LastUse);
        }).ToArray();
        return new(new(metadata, initial), takes, movie.Sections, states, false) { AutomaticCheckpoints = automatic };
    }
    public static void Save(string path, ArchiveMetadata metadata, EmulatorSnapshot initial,
        IReadOnlyList<InputTake> takes, IReadOnlyList<TimelineSection> sections, IReadOnlyList<SavedStateReference> states,
        IReadOnlyList<CheckpointReference>? automatic = null)
    {
        if (File.Exists(path) && IsLegacy(path)) throw new InvalidOperationException("Import the legacy ZIP project with Save As into a new folder; the original is preserved.");
        ProjectArchive.ValidateMetadata(metadata, ProjectArchive.ProjectKind);
        var full = Path.GetFullPath(path); var root = Path.GetDirectoryName(full)!; Directory.CreateDirectory(root);
        var previous = File.Exists(full) ? ReadJson<ProjectManifest>(full) : null;
        var baselinePath = Path.Combine(root, "states", "initial-" + metadata.StateHash + (metadata.Configuration == null ? "" : "-" + (metadata.ConfigurationIdentity ?? metadata.Configuration.Fingerprint)) + ".tasstate");
        if (!File.Exists(baselinePath)) ProjectArchive.Save(baselinePath,
            metadata with { Kind = ProjectArchive.StateKind, Position = 0, InitialPosition = 0, Inputs = [], Events = [], PollFrames = [], HistoryHash = null }, initial);
        var stateAssets = states.Select(s => new StateAsset(s.Id, s.Name, s.Position, s.HistoryHash, CopyState(root, s.Path))).ToArray();
        var takeAssets = takes.Select(t => new TakeAsset(t.Id, t.Name, t.Start, t.BaselineHash, t.Provenance, WriteInputs(root, t.Inputs), t.EventsHash)).ToArray();
        var movie = new MovieData(WriteInputs(root, metadata.Inputs), metadata.Events, takeAssets, sections.ToArray())
        { PollFrames = WritePollFrames(root, metadata.PollFrames) };
        var id = previous?.Id ?? Guid.NewGuid().ToString("D");
        var manifest = new ProjectManifest(Version, id, metadata with { Inputs = [], Events = [], PollFrames = [] },
            Asset(root, baselinePath), WriteJson(root, "timelines", movie), stateAssets)
        {
            AutomaticCheckpoints = (automatic ?? []).Select(c => new CheckpointAsset(
                new(c.State.Id, c.State.Name, c.State.Position, c.State.HistoryHash, CopyState(root, c.State.Path)), c.Seconds, c.Created, c.LastUse)).ToArray()
        };
        var temp = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            WriteFlushed(temp, JsonSerializer.SerializeToUtf8Bytes(manifest, Json));
            Load(temp); // Verify all references before the commit point.
            File.Move(temp, full, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
        if (previous != null) RemoveDroppedStateAssets(root, full, previous, manifest);
    }

    // A successful manifest replacement is the ownership boundary. Only state assets dropped
    // by that replacement are candidates; unrelated files and input/timeline history are untouched.
    private static void RemoveDroppedStateAssets(string root, string currentPath, ProjectManifest previous, ProjectManifest current)
    {
        try
        {
            var retained = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Protect(ProjectManifest manifest)
            {
                foreach (var asset in StateAssets(manifest))
                    retained.Add(Path.GetFullPath(asset.Path, root));
            }
            Protect(current);
            foreach (var path in Directory.EnumerateFiles(root))
            {
                if (!Path.GetExtension(path).Equals(".tasproj", StringComparison.OrdinalIgnoreCase) ||
                    Path.GetFullPath(path).Equals(currentPath, StringComparison.OrdinalIgnoreCase)) continue;
                // An unknown, legacy or unreadable neighbor makes ownership uncertain: retain all.
                if (IsLegacy(path)) return;
                Protect(ReadJson<ProjectManifest>(path));
            }
            var candidates = StateAssets(previous).ToArray();
            foreach (var asset in candidates)
            {
                try
                {
                    var target = Path.GetFullPath(asset.Path, root);
                    if (retained.Contains(target) || !OwnedStatePath(root, asset, target)) continue;
                    if (HashFile(target) != asset.Sha256) continue;
                    // Recheck immediately before deletion; never traverse a reparse point.
                    if (HasReparsePoint(target)) continue;
                    File.Delete(target);
                }
                catch (Exception ex) when (CleanupFailure(ex)) { /* A retained orphan is safer than a failed save. */ }
            }
        }
        catch (Exception ex) when (CleanupFailure(ex)) { /* Fail closed before deleting if references cannot be read. */ }
    }
    private static IEnumerable<ProjectAsset> StateAssets(ProjectManifest manifest)
    {
        if (manifest.Version is not (2 or Version) || manifest.Environment == null || manifest.InitialState == null ||
            manifest.Timeline == null || manifest.States == null || manifest.AutomaticCheckpoints == null ||
            manifest.States.Any(s => s?.Data == null) || manifest.AutomaticCheckpoints.Any(c => c?.State?.Data == null))
            throw new InvalidDataException("Cannot determine project state ownership.");
        var assets = new[] { manifest.InitialState }.Concat(manifest.States.Select(s => s.Data))
            .Concat(manifest.AutomaticCheckpoints.Select(c => c.State.Data)).ToArray();
        if (assets.Any(a => string.IsNullOrWhiteSpace(a.Path) || string.IsNullOrWhiteSpace(a.Sha256)))
            throw new InvalidDataException("Incomplete state reference.");
        return assets;
    }
    private static bool OwnedStatePath(string root, ProjectAsset asset, string target)
    {
        var states = Path.GetFullPath(Path.Combine(root, "states"));
        if (Path.IsPathRooted(asset.Path) || !Path.GetDirectoryName(target)!.Equals(states, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetExtension(target).Equals(".tasstate", StringComparison.OrdinalIgnoreCase) || HasReparsePoint(target)) return false;
        // CopyState and the baseline writer own only these generated names. A user-named export
        // remains external even if someone explicitly references it from a hand-edited manifest.
        var name = Path.GetFileNameWithoutExtension(target);
        static bool HashName(string text) => text.Length == 64 && text.All(Uri.IsHexDigit);
        if (HashName(name)) return name.Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase);
        if (!name.StartsWith("initial-", StringComparison.Ordinal)) return false;
        var hashes = name[8..].Split('-');
        return hashes.Length is 1 or 2 && hashes.All(HashName);
    }
    private static bool HasReparsePoint(string path)
    {
        for (var cursor = path; cursor != null; cursor = Path.GetDirectoryName(cursor))
            if ((File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0) return true;
        return false;
    }
    private static bool CleanupFailure(Exception ex) => ex is IOException or InvalidDataException or UnauthorizedAccessException or
        JsonException or ArgumentException or NotSupportedException or System.Security.SecurityException;
    public static string ResolveRomPath(string projectPath, string hint) => Path.GetFullPath(hint, Path.GetDirectoryName(Path.GetFullPath(projectPath))!);
    public static async Task<string> HashGameAsync(string path, CancellationToken token = default)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, token));
    }
    private static ProjectAsset[] WriteInputs(string root, ControllerState[] inputs) => inputs.Chunk(FramesPerChunk).Select(c => WriteJson(root, "inputs", c)).ToArray();
    private static ProjectAsset[] WritePollFrames(string root, RecordedInputFrame[] frames)
    {
        var assets = new List<ProjectAsset>(); var chunk = new List<RecordedInputFrame>(); var polls = 0;
        foreach (var frame in frames)
        {
            if (chunk.Count > 0 && (chunk.Count >= 128 || polls + frame.Frame.Polls.Length > 16384))
            { assets.Add(WriteJson(root, "inputs", chunk)); chunk.Clear(); polls = 0; }
            chunk.Add(frame); polls += frame.Frame.Polls.Length;
        }
        if (chunk.Count > 0) assets.Add(WriteJson(root, "inputs", chunk));
        return assets.ToArray();
    }
    private static ControllerState[] ReadInputs(string root, ProjectAsset[] assets)
    {
        if (assets.Length > (MaximumFrames + FramesPerChunk - 1) / FramesPerChunk) throw new InvalidDataException("Movie is too long.");
        var list = new List<ControllerState>();
        foreach (var asset in assets)
        {
            var chunk = ReadJson<ControllerState[]>(Resolve(root, asset));
            if (chunk.Length is 0 or > FramesPerChunk || list.Count + chunk.Length > MaximumFrames) throw new InvalidDataException("Invalid input chunk.");
            if (chunk.Any(p => (p.Buttons & ~ControllerState.KnownButtons) != PadButtons.None)) throw new InvalidDataException("Unknown controller bits in input chunk.");
            list.AddRange(chunk);
        }
        return list.ToArray();
    }
    private static ProjectAsset WriteJson<T>(string root, string folder, T value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json); var hash = Convert.ToHexString(SHA256.HashData(bytes));
        var path = Path.Combine(root, folder, hash + ".json");
        if (!File.Exists(path)) WriteFlushed(path, bytes);
        return new(Path.GetRelativePath(root, path).Replace('\\', '/'), hash);
    }
    private static ProjectAsset CopyState(string root, string source)
    {
        var hash = HashFile(source); var destination = Path.Combine(root, "states", hash + ".tasstate");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (!File.Exists(destination))
        {
            var temp = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { File.Copy(source, temp); using (var file = new FileStream(temp, FileMode.Open, FileAccess.ReadWrite)) file.Flush(true); File.Move(temp, destination); }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        return new(Path.GetRelativePath(root, destination).Replace('\\', '/'), hash);
    }
    private static ProjectAsset Asset(string root, string path) => new(Path.GetRelativePath(root, path).Replace('\\', '/'), HashFile(path));
    private static string HashFile(string path) { using var file = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(file)); }
    private static string Resolve(string root, ProjectAsset asset)
    {
        var full = Path.GetFullPath(asset.Path, root);
        if (Path.IsPathRooted(asset.Path) || !full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Project asset escapes its directory.");
        if (HashFile(full) != asset.Sha256) throw new InvalidDataException("Project asset checksum mismatch: " + asset.Path);
        return full;
    }
    private static T ReadJson<T>(string path)
    {
        if (new FileInfo(path).Length > MaximumJsonBytes) throw new InvalidDataException("Project metadata is too large.");
        return JsonSerializer.Deserialize<T>(File.ReadAllBytes(path), Json) ?? throw new InvalidDataException("Missing project metadata.");
    }
    private static void WriteFlushed(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None); file.Write(bytes); file.Flush(true);
    }
}
