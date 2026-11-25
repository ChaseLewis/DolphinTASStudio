using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace TasStudio.Emulation;

internal static class ProjectAssetLease
{
    internal static IDisposable Acquire(string root, bool exclusive = false)
    {
        var directory = Path.Combine(Path.GetTempPath(), "TasStudio.AssetLocks"); Directory.CreateDirectory(directory);
        var key = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(root).ToUpperInvariant())));
        return new FileStream(Path.Combine(directory, key + ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite,
            exclusive ? FileShare.None : FileShare.ReadWrite);
    }
}

public sealed record InputCompressionResult(int Files, long BytesSaved);

/// <summary>Commit new immutable assets and manifests before retiring uncompressed copies.</summary>
public static class ProjectInputCompression
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public static InputCompressionResult Compress(string directory, Action<int, int, long>? progress = null, CancellationToken token = default)
    {
        var root = Path.GetFullPath(directory);
        using var lease = ProjectAssetLease.Acquire(root, exclusive: true);
        var manifests = Directory.GetFiles(root, "*.tasproj").Order(StringComparer.OrdinalIgnoreCase).ToArray();
        if (manifests.Length == 0) return new(0, 0);
        var originals = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var projects = new Dictionary<string, ProjectManifest>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in manifests)
        {
            CheckPath(root, path);
            if (FolderProject.IsLegacy(path)) throw new InvalidDataException("Legacy project retained: " + path);
            var bytes = CompressedJson.ReadBytes(path); originals.Add(path, bytes);
            var manifest = JsonSerializer.Deserialize<ProjectManifest>(bytes) ?? throw new InvalidDataException("Invalid project manifest.");
            if (manifest.Version is not (2 or FolderProject.Version) || manifest.Timeline == null) throw new InvalidDataException("Unknown project format.");
            projects.Add(path, manifest);
        }
        var inputDirectory = Path.Combine(root, "inputs");
        if (Directory.Exists(inputDirectory)) CheckPath(root, inputDirectory);
        var inputs = Directory.Exists(inputDirectory) ? Directory.GetFiles(inputDirectory, "*.json").Where(IsGeneratedJson).ToArray() : [];
        if (inputs.Length == 0) return new(0, 0);
        var replacements = new Dictionary<string, ProjectAsset>(StringComparer.OrdinalIgnoreCase);
        var stored = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var oldSizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var newSizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        long writtenBytes = 0; var completed = 0; var progressGate = new object();
        try
        {
            Parallel.For(0, inputs.Length, new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Min(4, Math.Max(2, Environment.ProcessorCount / 2)), CancellationToken = token
            }, i =>
            {
                token.ThrowIfCancellationRequested(); var path = inputs[i]; CheckPath(root, path);
                var bytes = CompressedJson.ReadBytes(path); var hash = Hash(bytes);
                if (!Path.GetFileNameWithoutExtension(path).Equals(hash, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Input checksum mismatch: " + path);
                var compressed = CompressedJson.Compress(bytes); var compressedHash = Hash(compressed);
                var target = Path.Combine(inputDirectory, compressedHash + ".json.gz");
                // Round-trip before committing any reference to the new asset.
                var alreadyExists = File.Exists(target);
                if (!alreadyExists) PlayMemoryCards.WriteAtomic(target, compressed);
                CheckPath(root, target);
                if (Hash(File.ReadAllBytes(target)) != compressedHash || Hash(CompressedJson.ReadBytes(target)) != hash)
                    throw new InvalidDataException("Compressed input verification failed.");
                lock (progressGate)
                {
                    replacements.Add(path, new(Path.GetRelativePath(root, target).Replace('\\', '/'), compressedHash));
                    oldSizes[path] = bytes.Length; newSizes[path] = alreadyExists ? 0 : compressed.Length;
                    writtenBytes += newSizes[path];
                    // Callbacks stay ordered and never overlap, even when files finish out of order.
                    progress?.Invoke(++completed, inputs.Length * 2, -writtenBytes);
                }
            });
        }
        // Parallel.For joins in-flight writes before throwing. Preserve the original failure
        // type so cleanup can report it and retain every original input and manifest.
        catch (AggregateException error) { ExceptionDispatchInfo.Capture(error.Flatten().InnerExceptions[0]).Throw(); }
        ProjectAsset Replace(ProjectAsset asset)
        {
            var path = Path.GetFullPath(asset.Path, root);
            if (!replacements.TryGetValue(path, out var replacement)) return asset;
            if (!Path.GetFileNameWithoutExtension(path).Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Input reference checksum mismatch; original inputs retained.");
            return replacement;
        }
        var timelineDirectory = Path.Combine(root, "timelines"); CheckPath(root, timelineDirectory);
        var timelines = Directory.GetFiles(timelineDirectory, "*.json").Where(IsGeneratedJson).ToArray();
        var timelineMap = new Dictionary<string, ProjectAsset>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in timelines)
        {
            token.ThrowIfCancellationRequested(); CheckPath(root, path);
            var bytes = CompressedJson.ReadBytes(path);
            if (!Path.GetFileNameWithoutExtension(path).Equals(Hash(bytes), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Timeline checksum mismatch.");
            var movie = JsonSerializer.Deserialize<MovieData>(bytes) ?? throw new InvalidDataException("Invalid timeline.");
            var changed = movie with { Inputs = movie.Inputs.Select(Replace).ToArray(), PollFrames = movie.PollFrames.Select(Replace).ToArray(),
                Takes = movie.Takes.Select(take => take with { Inputs = take.Inputs.Select(Replace).ToArray(), Experiment = take.Experiment == null ? null : Replace(take.Experiment) }).ToArray() };
            var updated = JsonSerializer.SerializeToUtf8Bytes(changed, Json); var hash = Hash(updated);
            var target = Path.Combine(root, "timelines", hash + ".json");
            if (!File.Exists(target)) PlayMemoryCards.WriteAtomic(target, updated);
            CheckPath(root, target);
            if (Hash(File.ReadAllBytes(target)) != hash) throw new InvalidDataException("Updated timeline checksum mismatch.");
            timelineMap[path] = new(Path.GetRelativePath(root, target).Replace('\\', '/'), hash);
        }
        foreach (var (path, manifest) in projects)
        {
            var timelinePath = Path.GetFullPath(manifest.Timeline.Path, root);
            if (!Path.GetFileNameWithoutExtension(timelinePath).Equals(manifest.Timeline.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Timeline reference checksum mismatch; original inputs retained.");
            if (!timelineMap.TryGetValue(timelinePath, out var updated)) throw new InvalidDataException("Unsupported timeline path; original inputs retained.");
            stored[path] = JsonSerializer.SerializeToUtf8Bytes(manifest with { Timeline = updated }, Json);
        }
        token.ThrowIfCancellationRequested();
        if (!manifests.SequenceEqual(Directory.GetFiles(root, "*.tasproj").Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase) ||
            originals.Any(pair => !File.ReadAllBytes(pair.Key).SequenceEqual(pair.Value)))
            throw new IOException("Project changed during compression; original inputs retained.");
        foreach (var (path, bytes) in stored) PlayMemoryCards.WriteAtomic(path, bytes);
        // Every manifest and historical timeline now has an equivalent compressed version.
        // If a commit above failed, execution never reaches deletion and both versions remain usable.
        // Retire obsolete timelines first, so cancellation while deleting input copies cannot
        // leave a historical timeline pointing at an already-removed uncompressed asset.
        var currentTimelines = timelineMap.Values.Select(asset => Path.GetFullPath(asset.Path, root)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in timelines.Where(path => !currentTimelines.Contains(path))) { CheckPath(root, path); File.Delete(path); }
        long saved = -newSizes.Values.Sum(); var count = 0;
        foreach (var path in inputs)
        {
            token.ThrowIfCancellationRequested(); CheckPath(root, path); File.Delete(path);
            saved += oldSizes[path]; count++; progress?.Invoke(inputs.Length + count, inputs.Length * 2, saved);
        }
        return new(count, saved);
    }
    internal static bool IsGeneratedJson(string path) => Path.GetFileNameWithoutExtension(path) is { Length: 64 } name && name.All(Uri.IsHexDigit);
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static void CheckPath(string root, string path)
    {
        if (!Path.GetFullPath(path).StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("Asset escapes the project.");
        for (var current = path; current != null; current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked assets are not eligible for compression.");
    }
}
