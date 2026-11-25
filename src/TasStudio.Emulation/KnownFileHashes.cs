using System.Security.Cryptography;
using System.Text.Json;

namespace TasStudio.Emulation;

/// <summary>
/// Checksums of large files (saved states, game images) remembered between sessions, so opening a project
/// does not reread gigabytes that have not changed. A checksum is reused only while the file's length,
/// write time and creation time all match; revalidation rereads the file regardless and corrects the record.
/// The store lives in application data, never inside a project.
/// </summary>
public sealed class KnownFileHashes(string storePath)
{
    private const int MaximumEntries = 65536;
    private sealed record Entry(long Length, long WrittenUtcTicks, long CreatedUtcTicks, string Sha256);
    private readonly object _gate = new();
    private Dictionary<string, Entry>? _entries;
    private bool _changed;

    /// <summary>Files whose contents were actually read since this instance was created.</summary>
    public int Reads { get; private set; }

    public string HashFile(string path, bool revalidate = false) => HashFile(path, revalidate, CancellationToken.None);

    public string HashFile(string path, bool revalidate, CancellationToken cancellationToken)
    {
        path = Path.GetFullPath(path);
        // Hold the file against writers while its stamp is read and, on a miss, while it is hashed.
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        var stamp = (file.Length, File.GetLastWriteTimeUtc(path).Ticks, File.GetCreationTimeUtc(path).Ticks);
        lock (_gate)
        {
            if (!revalidate && Entries().TryGetValue(path, out var known) &&
                (known.Length, known.WrittenUtcTicks, known.CreatedUtcTicks) == stamp) return known.Sha256;
        }
        var hash = Convert.ToHexString(SHA256.HashDataAsync(file, cancellationToken).AsTask().GetAwaiter().GetResult());
        lock (_gate)
        {
            Reads++;
            var entries = Entries();
            if (entries.Count >= MaximumEntries) entries.Clear();
            entries[path] = new(stamp.Length, stamp.Item2, stamp.Item3, hash); _changed = true;
        }
        return hash;
    }

    /// <summary>Persist new checksums. A cache that cannot be written only costs time on the next open.</summary>
    public void Save()
    {
        byte[] bytes;
        lock (_gate)
        {
            if (!_changed || _entries == null) return;
            bytes = JsonSerializer.SerializeToUtf8Bytes(_entries); _changed = false;
        }
        var temp = storePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(storePath))!);
            File.WriteAllBytes(temp, bytes); File.Move(temp, storePath, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { lock (_gate) _changed = true; }
        finally { try { if (File.Exists(temp)) File.Delete(temp); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
    }

    private Dictionary<string, Entry> Entries()
    {
        if (_entries != null) return _entries;
        try
        {
            if (File.Exists(storePath))
                _entries = new(JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllBytes(storePath)) ?? [], StringComparer.OrdinalIgnoreCase);
        }
        // An unreadable cache is discarded: every file is simply hashed again.
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        return _entries ??= new(StringComparer.OrdinalIgnoreCase);
    }
}
