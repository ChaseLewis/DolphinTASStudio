using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TasStudio.Emulation;

/// <summary>
/// Collects only obsolete generated JSON assets named by a previous recovery manifest.
/// Call Capture, FolderProject.Save and Collect in one serialized recovery-writer operation.
/// Recovery directories must remain application-owned; this is not a general folder collector.
/// </summary>
public static class RecoveryAssetCleanup
{
    private const long MaximumJsonBytes = 64 * 1024 * 1024;
    private static readonly StringComparer Paths = StringComparer.OrdinalIgnoreCase;
    private static readonly Regex Generated = new(@"^(inputs|timelines)/([A-Fa-f0-9]{64})\.json$", RegexOptions.CultureInvariant);
    public sealed class Previous
    {
        internal string Path { get; }
        internal byte[] Bytes { get; }
        internal Previous(string path, byte[] bytes) { Path = path; Bytes = bytes; }
    }
    public sealed record Result(int Deleted, string? SkippedReason = null);

    public static Previous? Capture(string path)
    {
        path = Path.GetFullPath(path);
        if (!string.Equals(Path.GetFileName(path), "recovery.tasproj", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Cleanup is restricted to recovery.tasproj.", nameof(path));
        if (!File.Exists(path)) return null;
        CheckOrdinaryPath(path);
        return new(path, ReadBytes(path));
    }

    public static Result Collect(Previous? previous)
    {
        if (previous == null) return new(0);
        var deleted = 0;
        try
        {
            var root = Path.GetDirectoryName(previous.Path)!;
            CheckOrdinaryPath(root);
            var oldManifest = Parse<ProjectManifest>(previous.Bytes);
            var oldAssets = Assets(root, oldManifest);
            // Hold manifest handles without write/delete sharing throughout collection. This
            // also prevents replacing a sibling manifest to reference an about-to-be-deleted asset.
            var streams = new List<FileStream>();
            try
            {
                var files = ManifestFiles(root);
                var protectedPaths = new HashSet<string>(Paths);
                ProjectManifest? current = null;
                foreach (var file in files)
                {
                    CheckOrdinaryPath(file);
                    var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
                    streams.Add(stream);
                    if (stream.Length > MaximumJsonBytes) throw new InvalidDataException("Oversized sibling manifest.");
                    using var bytes = new MemoryStream(); stream.CopyTo(bytes);
                    var manifest = Parse<ProjectManifest>(bytes.ToArray());
                    foreach (var asset in Assets(root, manifest)) protectedPaths.Add(asset.Path);
                    // Protect even unusual non-JSON references: never delete something a sibling names.
                    foreach (var asset in new[] { manifest.InitialState }.Concat(manifest.States.Select(s => s.Data))
                        .Concat(manifest.AutomaticCheckpoints.Select(c => c.State.Data)))
                        protectedPaths.Add(Resolve(root, asset));
                    if (Paths.Equals(file, previous.Path)) current = manifest;
                }
                if (current == null || current.Id != oldManifest.Id)
                    throw new InvalidDataException("Recovery manifest identity changed or disappeared.");
                foreach (var candidate in oldAssets.DistinctBy(a => a.Path, Paths))
                {
                    if (protectedPaths.Contains(candidate.Path)) continue;
                    if (!files.SequenceEqual(ManifestFiles(root), Paths))
                        throw new InvalidDataException("Sibling manifest set changed during cleanup.");
                    CheckOrdinaryPath(candidate.Path);
                    if (Hash(ReadBytes(candidate.Path)) != candidate.Hash)
                        throw new InvalidDataException("Obsolete asset changed since validation.");
                    File.Delete(candidate.Path); deleted++;
                }
                return new(deleted);
            }
            finally { foreach (var stream in streams) stream.Dispose(); }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        { return new(deleted, ex.Message); }
    }

    private sealed record Asset(string Path, string Hash);
    private static List<Asset> Assets(string root, ProjectManifest manifest)
    {
        if (manifest.Version is not (2 or 3) || string.IsNullOrWhiteSpace(manifest.Id) || manifest.Environment == null ||
            manifest.InitialState == null || manifest.Timeline == null || manifest.States == null || manifest.AutomaticCheckpoints == null ||
            manifest.States.Any(s => s == null || s.Data == null) || manifest.AutomaticCheckpoints.Any(c => c?.State?.Data == null))
            throw new InvalidDataException("Ambiguous recovery or sibling manifest.");
        var assets = new List<Asset>();
        byte[] Add(ProjectAsset asset, string directory)
        {
            if (asset == null) throw new InvalidDataException("Missing JSON asset reference.");
            var match = Generated.Match(asset.Path ?? "");
            if (!match.Success || match.Groups[1].Value != directory || !string.Equals(match.Groups[2].Value, asset.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("JSON asset is not a generated content-addressed file.");
            var path = Resolve(root, asset); var bytes = ReadBytes(path); var hash = Hash(bytes);
            if (!string.Equals(hash, asset.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("JSON asset checksum mismatch.");
            assets.Add(new(path, hash)); return bytes;
        }
        var movie = Parse<MovieData>(Add(manifest.Timeline, "timelines"));
        if (movie.Inputs == null || movie.PollFrames == null || movie.Takes == null || movie.Events == null || movie.Sections == null || movie.Takes.Any(t => t?.Inputs == null))
            throw new InvalidDataException("Ambiguous timeline references.");
        foreach (var input in movie.Inputs.Concat(movie.Takes.SelectMany(t => t.Inputs))) Add(input, "inputs");
        foreach (var polls in movie.PollFrames) Add(polls, "inputs");
        return assets;
    }

    private static string[] ManifestFiles(string root)
    {
        // No directory traversal: folder assets cannot escape their own manifest root, so
        // nested manifests cannot legally reference these top-level inputs/timelines.
        var entries = Directory.GetFileSystemEntries(root);
        foreach (var entry in entries) CheckOrdinaryPath(entry);
        if (entries.Any(p => !Directory.Exists(p) && (p.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".json", StringComparison.OrdinalIgnoreCase))))
            throw new InvalidDataException("Ambiguous or in-flight sibling manifest.");
        return entries.Where(p => !Directory.Exists(p) && p.EndsWith(".tasproj", StringComparison.OrdinalIgnoreCase)).OrderBy(p => p, Paths).ToArray();
    }
    private static string Resolve(string root, ProjectAsset asset)
    {
        if (asset == null || string.IsNullOrWhiteSpace(asset.Path) || Path.IsPathRooted(asset.Path)) throw new InvalidDataException("Invalid asset path.");
        var full = Path.GetFullPath(asset.Path, root);
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Asset escapes recovery root.");
        CheckOrdinaryPath(full); return full;
    }
    private static void CheckOrdinaryPath(string path)
    {
        for (var current = Path.GetFullPath(path); current != null; current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Reparse paths are not eligible for cleanup.");
    }
    private static byte[] ReadBytes(string path)
    {
        if (new FileInfo(path).Length > MaximumJsonBytes) throw new InvalidDataException("Oversized JSON asset.");
        return File.ReadAllBytes(path);
    }
    private static T Parse<T>(byte[] bytes) => JsonSerializer.Deserialize<T>(bytes) ?? throw new InvalidDataException("Missing JSON object.");
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
}
