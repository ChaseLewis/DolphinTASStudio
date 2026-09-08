namespace TasStudio.Emulation;

public sealed partial class ExecutionService
{
    public Task ConfigureCheckpointsAsync(CheckpointPolicy policy) => Enqueue(() =>
    {
        policy.Validate();
        _checkpointPolicy = policy; _lastCheckpointAttempt = double.NaN; TrimCheckpoints(); Interlocked.Increment(ref _revision);
        Notify("Checkpoint settings updated");
    });

    public Task ApplyConfigurationAsync(EmulationConfiguration configuration, CheckpointPolicy checkpoints, bool forceRestart = false)
    {
        var requested = configuration.ValidatedCopy(); checkpoints.Validate();
        return Enqueue(() =>
        {
            RequireProject(); Pause();
            if (!forceRestart && _options!.Configuration?.Fingerprint == requested.Fingerprint)
            {
                _checkpointPolicy = checkpoints; TrimCheckpoints(); Interlocked.Increment(ref _revision); Notify("Project settings updated"); return;
            }
            var backup = CaptureSession()!;
            var succeeded = false;
            var options = _options! with { Configuration = requested, InternalResolution = requested.Resolution, DspHle = requested.DspHle,
                SaveDirectory = Path.Combine(_options.StorageRoot ?? _options.SaveDirectory, "ConfigurationRuns", Guid.NewGuid().ToString("N")) };
            _preserveCheckpointFiles = true;
            try
            {
                LoadGame(backup.Path, options);
                // The old baseline is a state too. Never restore it under changed settings.
                _initial = _backend.Capture() with { Position = 0 };
                _projectStart = new(ProjectStartKind.PowerOn);
                _inputs.AddRange(backup.Inputs); _events.AddRange(backup.Events); _hasProject = true;
                InitializeWorkspace(); _checkpointPolicy = checkpoints;
                // Retain the group's editable poll layout, but regenerate timing
                // under the new settings before using these records for playback.
                RestorePollRecords(backup.PollFrames.Select(record => record with { PrefixHash = "" }));
                // Keep candidates for inspection; their original root hashes correctly prevent reuse.
                _takes.AddRange(backup.Takes); _sections.AddRange(backup.Sections);
                _tags.AddRange(backup.Tags);
                ApplyEvents(0); _executedHistory = _history!.At(0);
                succeeded = true;
                Notify("Settings applied — inputs preserved, game restarted, old states removed");
            }
            catch { RestoreSession(backup); throw; }
            finally { FinishSessionReplacement(backup, succeeded); }
        });
    }

    private void RemoveCheckpoint(AutomaticCheckpoint checkpoint, bool requireFileDeletion = false)
    {
        // Only entries created by this service are ever in this cache.
        DeleteCheckpointFile(checkpoint, requireFileDeletion);
        _checkpoints.Remove(checkpoint);
    }
    private void DeleteCheckpointFile(AutomaticCheckpoint checkpoint, bool requireFileDeletion = false)
    {
        try { if (checkpoint.OwnsFile && !_preserveCheckpointFiles) File.Delete(checkpoint.State.Path); }
        catch (Exception ex) when (!requireFileDeletion && ex is (IOException or UnauthorizedAccessException))
        { StatusChanged?.Invoke("Removed checkpoint from cache, but could not delete its file: " + ex.Message); }
    }
    private void FinishSessionReplacement(SessionBackup? backup, bool succeeded)
    {
        _preserveCheckpointFiles = false;
        if (succeeded && backup != null)
        {
            // The newly opened project may contain equal references. Retire the old files,
            // never remove entries from the replacement session by record equality.
            foreach (var checkpoint in backup.AutomaticCheckpoints) DeleteCheckpointFile(checkpoint);
            foreach (var state in backup.States) DeleteNamedStateFile(state);
        }
    }
    private void RemoveNamedState(SavedStateReference state, bool requireFileDeletion = false)
    {
        DeleteNamedStateFile(state, requireFileDeletion);
        _savedStates.Remove(state);
    }
    private void DeleteNamedStateFile(SavedStateReference state, bool requireFileDeletion = false)
    {
        try { if (state.OwnsFile && !_preserveCheckpointFiles) File.Delete(state.Path); }
        catch (Exception ex) when (!requireFileDeletion && ex is (IOException or UnauthorizedAccessException))
        { StatusChanged?.Invoke("Removed state marker, but could not delete its file: " + ex.Message); }
    }
    private void ClearNamedStates()
    {
        foreach (var state in _savedStates.ToArray()) RemoveNamedState(state);
    }
    private void ClearAutomaticCheckpoints()
    {
        foreach (var checkpoint in _checkpoints.ToArray()) RemoveCheckpoint(checkpoint);
    }
    private void PruneInvalidAutomaticCheckpoints()
    {
        _lastCheckpointAttempt = double.NaN;
        foreach (var checkpoint in _checkpoints.Where(c => !Valid(c.State.Position, c.State.HistoryHash)).ToArray()) RemoveCheckpoint(checkpoint);
    }
    private void PruneInvalidStates()
    {
        PruneInvalidAutomaticCheckpoints();
        // Remove invalid named state references immediately. Exported files and files referenced
        // by the last saved manifest stay intact until that manifest is explicitly replaced.
        foreach (var state in _savedStates.Where(s => !Valid(s.Position, s.HistoryHash)).ToArray()) RemoveNamedState(state);
    }
    private void TrimCheckpoints()
    {
        while (_checkpoints.Count > _checkpointPolicy.MaximumCount || _checkpoints.Sum(c => c.Bytes) > _checkpointPolicy.DiskBudgetMiB * 1024L * 1024)
        {
            var victim = _checkpoints.OrderBy(c => _checkpointPolicy.Retention == CheckpointRetention.LeastRecentlyUsed ? c.LastUse : c.Created).First();
            RemoveCheckpoint(victim);
        }
    }
}
