using TasStudio.Core;

namespace TasStudio.Emulation;

public sealed partial class ExecutionService
{
    private readonly SemaphoreSlim _recoveryWriter = new(1, 1);
    /// <summary>Take an owner-thread view, then persist it without pausing playback or calling the core from the writer.</summary>
    public async Task<long> SaveRecoveryAsync(string path)
    {
        await _recoveryWriter.WaitAsync();
        try { return await WriteRecoveryAsync(path); }
        finally { _recoveryWriter.Release(); }
    }
    private async Task<long> WriteRecoveryAsync(string path)
    {
        var captured = await Enqueue(() =>
        {
            RequireProject();
            return (Metadata: Metadata(ProjectArchive.ProjectKind, _initial!), Initial: _initial!,
                Takes: _takes.Select(t => t with { Inputs = (ControllerState[])t.Inputs.Clone() }).ToArray(),
                Sections: _sections.ToArray(), Revision: _revision);
        });
        // The baseline is immutable for the lifetime of this capture. Temporary checkpoint files
        // are deliberately not referenced: later edits/disposal may delete them while this writes.
        await Task.Run(() =>
        {
            RecoveryAssetCleanup.Previous? previous = null;
            try { previous = RecoveryAssetCleanup.Capture(path); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException) { }
            FolderProject.Save(path, captured.Metadata, captured.Initial, captured.Takes, captured.Sections, []);
            RecoveryAssetCleanup.Collect(previous);
        });
        return captured.Revision;
    }

    public Task StopPlaybackAsync()
    {
        InterruptSeek();
        return Enqueue(() =>
        {
            RequireLoaded(); Pause();
            if (_hasProject) Seek(0);
            Notify(_hasProject ? "Playback stopped at project start — ROM and inputs retained" : "Playback stopped — game retained");
        });
    }
}
