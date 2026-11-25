using System.Security.Cryptography;
using System.Text;
using TasStudio.Core;

namespace TasStudio.Emulation;

/// <summary>
/// Lets repeated saves of one session skip JSON, gzip and parsing for movie chunks whose content is unchanged.
/// Chunks are matched by a hash of every field, never by reference, and the asset file is still checked on disk.
/// </summary>
internal sealed class ProjectChunkCache
{
    private readonly Dictionary<string, ProjectAsset> _assets = [];
    private readonly Dictionary<string, object> _chunks = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _usedKeys = [];
    private readonly HashSet<string> _usedChunks = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Chunks serialized by the current save; unchanged chunks do not count.</summary>
    internal int Serialized { get; private set; }
    /// <summary>Chunks parsed from disk by the current save's verification.</summary>
    internal int Parsed { get; private set; }

    internal void BeginSave() { _usedKeys.Clear(); _usedChunks.Clear(); Serialized = 0; Parsed = 0; }

    /// <summary>Forget chunks the committed save no longer references, so edits do not accumulate old copies.</summary>
    internal void EndSave()
    {
        foreach (var key in _assets.Keys.Where(key => !_usedKeys.Contains(key)).ToArray()) _assets.Remove(key);
        foreach (var hash in _chunks.Keys.Where(hash => !_usedChunks.Contains(hash)).ToArray()) _chunks.Remove(hash);
    }

    internal bool TryAsset(string key, string root, out ProjectAsset asset)
    {
        if (_assets.TryGetValue(key, out asset!) && File.Exists(Path.GetFullPath(asset.Path, root))) { Use(key, asset); return true; }
        return false;
    }

    internal void Remember(string key, ProjectAsset asset, object chunk)
    {
        _assets[key] = asset; _chunks[asset.Sha256] = chunk; Serialized++; Use(key, asset);
    }

    /// <summary>The caller has already matched the file's checksum to <paramref name="sha256"/>.</summary>
    internal T? TryChunk<T>(string sha256) where T : class
    {
        if (_chunks.TryGetValue(sha256, out var chunk) && chunk is T typed) return typed;
        Parsed++; return null;
    }

    private void Use(string key, ProjectAsset asset) { _usedKeys.Add(key); _usedChunks.Add(asset.Sha256); }

    internal static string Key(ControllerState[] inputs) => Hash("inputs/v1", writer =>
    {
        writer.Write(inputs.Length);
        foreach (var input in inputs) Write(writer, input);
    });

    internal static string Key(IReadOnlyList<RecordedInputFrame> frames) => Hash("poll-frames/v1", writer =>
    {
        writer.Write(frames.Count);
        foreach (var record in frames)
        {
            writer.Write(record.Index); writer.Write(record.PrefixHash ?? "");
            Write(writer, record.Frame.Input); writer.Write(record.Frame.Fields); writer.Write(record.Frame.Ticks);
            writer.Write(record.Frame.Polls.Length);
            foreach (var poll in record.Frame.Polls)
            {
                writer.Write(poll.TickOffset); writer.Write(poll.FieldOffset); writer.Write(poll.Port); Write(writer, poll.Input);
            }
        }
    });

    private static void Write(BinaryWriter writer, ControllerState input)
    {
        writer.Write((ushort)input.Buttons); writer.Write(input.StickX); writer.Write(input.StickY);
        writer.Write(input.CStickX); writer.Write(input.CStickY); writer.Write(input.TriggerL); writer.Write(input.TriggerR);
    }

    private static string Hash(string kind, Action<BinaryWriter> encode)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, true)) { writer.Write(kind); encode(writer); }
        return Convert.ToHexString(SHA256.HashData(stream.GetBuffer().AsSpan(0, (int)stream.Length)));
    }
}
