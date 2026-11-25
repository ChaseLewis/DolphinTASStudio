using System.Security.Cryptography;

namespace TasStudio.Emulation;

/// <summary>Save-writer-local checksums. Normal project loads never trust this cache.</summary>
internal sealed class ProjectStateHashCache(Func<Stream, string>? hash = null)
{
    private const int MaximumEntries = 32768;
    /// <summary>Movie chunks already written by this session's saves.</summary>
    internal ProjectChunkCache Chunks { get; } = new();
    private readonly record struct Stamp(long Length, DateTime LastWriteUtc, DateTime CreatedUtc);
    private readonly Dictionary<string, (Stamp Stamp, string Hash)> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<Stream, string> _hash = hash ?? (stream => Convert.ToHexString(SHA256.HashData(stream)));

    internal string HashFile(string path)
    {
        path = Path.GetFullPath(path);
        // Exclude writers and replacements while checking metadata and, on a miss,
        // hashing. Missing/inaccessible files must still fail even after a cache hit.
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var stamp = new Stamp(file.Length, File.GetLastWriteTimeUtc(path), File.GetCreationTimeUtc(path));
        if (_entries.TryGetValue(path, out var cached) && cached.Stamp == stamp) return cached.Hash;
        var checksum = _hash(file);
        if (_entries.Count >= MaximumEntries) _entries.Clear();
        _entries[path] = (stamp, checksum);
        return checksum;
    }
}
