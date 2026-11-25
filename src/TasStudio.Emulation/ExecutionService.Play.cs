namespace TasStudio.Emulation;

public sealed partial class ExecutionService
{
    public Task LoadPlayGameAsync(string path, BackendOptions options, string? statePath = null) => Enqueue(() =>
    {
        RequireNoTakeOver();
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
            if (source != null) { ValidateCompatibility(source.Metadata); RestoreBackend(source.InitialState); }
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
        if (_options!.Configuration?.UseGciFolder == true) throw new InvalidOperationException("This session uses a GCI folder. Its individual save files are already available in " + _options.GciDirectory);
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
    public Task<int> ImportPlayMemoryCardFolderAsync(string region, string folder) => Enqueue(() =>
    {
        RequireManualPlay(); Pause();
        if (!PlayMemoryCards.Regions.Contains(region)) throw new ArgumentException("Select USA, EUR or JAP.", nameof(region));
        if (_backend is not IGameCubeMemoryCardBackend importer) throw new NotSupportedException("This backend does not support GCI folder import.");
        var saves = Directory.EnumerateFiles(Path.GetFullPath(folder)).Where(path => Path.GetExtension(path).Equals(".gci", StringComparison.OrdinalIgnoreCase))
            .Take(128).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        if (saves.Length == 0) throw new InvalidDataException("This folder contains no .gci saves. Select the Card A folder for your game's region.");
        if (saves.Length > 127) throw new InvalidDataException("A GameCube card can contain at most 127 saves. Choose a folder with fewer saves.");
        var temporary = Path.Combine(_options!.SaveDirectory, "Imports", Guid.NewGuid().ToString("N") + ".raw");
        Directory.CreateDirectory(Path.GetDirectoryName(temporary)!);
        try
        {
            importer.CreateMemoryCard(saves, temporary, region == "JAP");
            ImportPlayMemoryCard(region, temporary);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        Notify($"Imported {saves.Length} GCI save copies and restarted Play — paused");
        return saves.Length;
    });

    public Task ImportPlayMemoryCardAsync(string region, string source) => Enqueue(() => ImportPlayMemoryCard(region, source));
    private void ImportPlayMemoryCard(string region, string source)
    {
        RequireManualPlay(); Pause();
        if (new FileInfo(source).Length > 16 * 1024 * 1024) throw new InvalidDataException("Memory card is too large.");
        var bytes = File.ReadAllBytes(source);
        var size = PlayMemoryCards.ValidateRawCard(bytes, region);
        var options = _options! with { Configuration = (_options!.Configuration ?? EmulationConfiguration.FromLegacy(_options.InternalResolution, _options.DspHle)) with { MemoryCardSizeOverride = size, UseGciFolder = false } };
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
    }
}
