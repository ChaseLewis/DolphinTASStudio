using TasStudio.Core;

namespace TasStudio.Emulation;

public static class ProjectVerification
{
    /// <summary>Checks persisted assets and identities. Does not boot the core or claim replay/pixel verification.</summary>
    public static async Task<FolderProjectContent> VerifyAsync(string path, string backendIdentity,
        IReadOnlyDictionary<string, string> relocatedRoms, IDictionary<string, string>? hashes = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var content = await Task.Run(() => FolderProject.Load(path), cancellationToken);
        var metadata = content.Archive.Metadata;
        var rom = relocatedRoms.GetValueOrDefault(metadata.GameHash) ?? FolderProject.ResolveRomPath(path, metadata.GamePath);
        hashes ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!hashes.TryGetValue(rom, out var hash)) hashes[rom] = hash = await FolderProject.HashGameAsync(rom, cancellationToken);
        if (hash != metadata.GameHash) throw new InvalidDataException("ROM identity differs.");
        if (backendIdentity != metadata.BackendIdentity) throw new InvalidDataException("Different emulator build required.");
        if (File.Exists(path + ".watches.json")) WatchDocument.Load(path + ".watches.json");
        return content;
    }
}
