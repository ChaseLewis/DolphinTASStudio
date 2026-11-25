using TasStudio.Core;

namespace TasStudio.Emulation;

public static class ProjectVerification
{
    /// <summary>
    /// Checks persisted assets and identities. Does not boot the core or claim replay/pixel verification.
    /// Every file is reread; with <paramref name="known"/>, the checksums remembered for fast opening are corrected too.
    /// </summary>
    public static async Task<FolderProjectContent> VerifyAsync(string path, string backendIdentity,
        IReadOnlyDictionary<string, string> relocatedRoms, IDictionary<string, string>? hashes = null,
        CancellationToken cancellationToken = default, KnownFileHashes? known = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var content = await Task.Run(() => known == null ? FolderProject.Load(path)
            : FolderProject.Load(path, known, revalidate: true, cancellationToken), cancellationToken);
        var metadata = content.Archive.Metadata;
        var rom = relocatedRoms.GetValueOrDefault(metadata.GameHash) ?? FolderProject.ResolveRomPath(path, metadata.GamePath);
        hashes ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!hashes.TryGetValue(rom, out var hash))
        {
            hashes[rom] = hash = known == null ? await FolderProject.HashGameAsync(rom, cancellationToken)
                : await Task.Run(() => known.HashFile(rom, revalidate: true, cancellationToken), cancellationToken);
            known?.Save();
        }
        if (hash != metadata.GameHash) throw new InvalidDataException("ROM identity differs.");
        if (File.Exists(path + ".watches.json")) WatchDocument.Load(path + ".watches.json");
        return content;
    }
    public static string? BuildWarning(ArchiveMetadata metadata, string backendIdentity) =>
        metadata.BackendIdentity != backendIdentity || metadata.RuntimeHistory.Length > 1 ||
        metadata.RuntimeHistory.Any(runtime => runtime.BackendIdentity != backendIdentity)
            ? "Different emulator build or resources — opens with a compatibility warning" : null;
}
