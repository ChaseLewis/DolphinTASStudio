namespace TasStudio.Emulation;

public sealed partial class ExecutionService
{
    // Commit a self-contained project before replacing the old session.
    public Task<ProjectSummary> CreateProjectAsync(string projectPath, string gamePath, BackendOptions options,
        string? statePath = null, bool fromCurrentPlay = false) => Enqueue(() =>
    {
        RequireNoTakeOver();
        Pause();
        if (File.Exists(projectPath)) throw new IOException("A project already exists at this location.");
        ArchiveContent? source = statePath == null ? null : ProjectArchive.Load(statePath, ProjectArchive.StateKind);
        if (fromCurrentPlay)
        {
            RequireManualPlay();
            if (statePath != null) throw new ArgumentException("Choose current Play or a save-state file, not both.");
            var state = _backend.Capture();
            source = new(Metadata(ProjectArchive.StateKind, state), state);
        }
        if (source != null)
        {
            if (source.Metadata.GameHash != HashFile(gamePath)) throw new InvalidDataException("The save state belongs to a different game image.");
            if (source.Metadata.BackendIdentity != _backend.InspectIdentity(options)) throw new InvalidDataException("The save state requires a different emulator build.");
            // A state must start with the settings under which it was made, including its RTC.
            options = options with { Configuration = source.Metadata.Configuration?.ValidatedCopy(),
                InternalResolution = source.Metadata.InternalResolution, DspHle = source.Metadata.DspHle };
        }
        else if (options.Configuration is { } config)
            options = options with { Configuration = config.ValidatedCopy(), InternalResolution = config.Resolution, DspHle = config.DspHle };
        options = options with { StorageRoot = options.StorageRoot ?? options.SaveDirectory,
            SaveDirectory = Path.Combine(options.SaveDirectory, "Projects", Guid.NewGuid().ToString("N")),
            GciDirectory = ProjectMemoryCards.DirectoryFor(projectPath) };
        var backup = CaptureSession();
        var succeeded = false;
        ProjectSummary summary;
        _preserveCheckpointFiles = true;
        try
        {
            LoadGame(gamePath, options);
            if (source != null) { ValidateCompatibility(source.Metadata); RestoreBackend(source.InitialState); }
            _initial = _backend.Capture() with { Position = 0 };
            RestoreBackend(_initial);
            _hasProject = true;
            _projectStart = fromCurrentPlay ? new(ProjectStartKind.CapturedState, "Play", source!.InitialState.Position) :
                source == null ? new(ProjectStartKind.PowerOn) :
                new(ProjectStartKind.SaveState, Path.GetFileName(statePath), source.InitialState.Position);
            InitializeWorkspace();
            // The backend is already at this exact baseline. Make interactive
            // advance available immediately, without a redundant seek/restore.
            _executedHistory = _history!.At(0);
            var metadata = Metadata(ProjectArchive.ProjectKind, _initial);
            summary = ProjectSummary.From(metadata, _takes);
            FolderProject.Save(projectPath, metadata, _initial, [], [], [], null, _projectStateHashes);
            succeeded = true;
        }
        catch
        {
            if (backup != null) RestoreSession(backup);
            else { _backend.Stop(); _loaded = false; ClearProject(); GamePath = null; }
            Publish();
            throw;
        }
        finally { FinishSessionReplacement(backup, succeeded); }
        Notify("Project created: " + Path.GetFileNameWithoutExtension(projectPath));
        return summary;
    });

    public Task<string> InspectBackendIdentityAsync(BackendOptions options) => Enqueue(() => _backend.InspectIdentity(options));
}
