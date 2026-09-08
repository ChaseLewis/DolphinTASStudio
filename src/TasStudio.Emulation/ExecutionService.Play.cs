namespace TasStudio.Emulation;

public sealed partial class ExecutionService
{
    public Task LoadPlayGameAsync(string path, BackendOptions options, string? statePath = null) => Enqueue(() =>
    {
        Pause();
        var source = statePath == null ? null : ProjectArchive.Load(statePath, ProjectArchive.StateKind);
        if (source != null)
        {
            if (source.Metadata.GameHash != HashFile(path)) throw new InvalidDataException("State belongs to a different game image.");
            if (source.Metadata.BackendIdentity != _backend.InspectIdentity(options)) throw new InvalidDataException("State requires a different Dolphin build.");
            options = options with { Configuration = source.Metadata.Configuration?.ValidatedCopy(),
                InternalResolution = source.Metadata.InternalResolution, DspHle = source.Metadata.DspHle };
        }
        var backup = CaptureSession();
        var succeeded = false;
        _preserveCheckpointFiles = true;
        try
        {
            LoadGame(path, options);
            if (source != null) { ValidateCompatibility(source.Metadata); _backend.Restore(source.InitialState); }
            succeeded = true;
        }
        catch
        {
            if (backup != null) RestoreSession(backup);
            else { _backend.Stop(); _loaded = false; ClearProject(); GamePath = null; }
            throw;
        }
        finally { FinishSessionReplacement(backup, succeeded); }
        Notify("Play mode — paused");
    });

    private void RequireManualPlay()
    {
        RequireLoaded();
        if (_hasProject) throw new InvalidOperationException("Memory card files are managed in Play mode.");
    }

    /// <summary>Shutdown joins Dolphin's card writer; restore the exact play position after copying.</summary>
    public Task ExportPlayMemoryCardAsync(string region, string destination) => Enqueue(() =>
    {
        RequireManualPlay(); Pause();
        var source = PlayMemoryCards.CardPath(_options!, region);
        var target = Path.GetFullPath(destination);
        if (Path.GetFullPath(source).Equals(target, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Export to a file outside the active memory card location.");
        var backup = CaptureSession()!;
        _backend.Stop(); _loaded = false;
        try { PlayMemoryCards.WriteAtomic(target, File.ReadAllBytes(source)); }
        finally { RestoreSession(backup); }
        Notify("Exported memory card; Play is paused at the same position");
    });

    /// <summary>Import a copy and reboot. Keep the replaced card as a backup; never edit the source file.</summary>
    public Task ImportPlayMemoryCardAsync(string region, string source) => Enqueue(() =>
    {
        RequireManualPlay(); Pause();
        if (new FileInfo(source).Length > 16 * 1024 * 1024) throw new InvalidDataException("Memory card is too large.");
        var bytes = File.ReadAllBytes(source);
        var size = PlayMemoryCards.ValidateRawCard(bytes, region);
        var options = _options! with { Configuration = (_options!.Configuration ?? EmulationConfiguration.FromLegacy(_options.InternalResolution, _options.DspHle)) with { MemoryCardSizeOverride = size } };
        var target = PlayMemoryCards.CardPath(options, region);
        if (Path.GetFullPath(source).Equals(Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
            throw new IOException("Choose a card outside the active memory card location.");
        var backup = CaptureSession()!;
        _backend.Stop(); _loaded = false;
        byte[]? previous = null;
        var replaced = false;
        try
        {
            if (File.Exists(target))
            {
                previous = File.ReadAllBytes(target);
                File.Copy(target, target + "." + Guid.NewGuid().ToString("N") + ".bak");
            }
            PlayMemoryCards.WriteAtomic(target, bytes); replaced = true;
            LoadGame(backup.Path, options);
        }
        catch
        {
            _backend.Stop(); _loaded = false;
            if (replaced)
            {
                if (previous != null) PlayMemoryCards.WriteAtomic(target, previous);
                else File.Delete(target);
            }
            RestoreSession(backup);
            throw;
        }
        Notify("Imported memory card copy and restarted Play — paused");
    });
}
