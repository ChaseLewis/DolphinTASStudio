namespace TasStudio.Emulation;

public sealed partial class ExecutionService
{
    private readonly Action<string, ArchiveMetadata, EmulatorSnapshot> _writeCheckpoint;
    private sealed record PendingCheckpoint(AutomaticCheckpoint Checkpoint, ExecutionHistory History, Task<long> Write);
    private PendingCheckpoint? _pendingCheckpoint;

    private long WriteCheckpoint(string path, ArchiveMetadata metadata, EmulatorSnapshot snapshot)
    {
        _writeCheckpoint(path, metadata, snapshot);
        return new FileInfo(path).Length;
    }

    private void CommitCheckpoint(AutomaticCheckpoint checkpoint, long bytes)
    {
        _checkpoints.Add(checkpoint with { Bytes = bytes });
        TrimCheckpoints();
    }

    private void FinishCheckpointWrite(bool wait = false)
    {
        if (_pendingCheckpoint is not { } pending || (!wait && !pending.Write.IsCompleted)) return;
        _pendingCheckpoint = null;
        try
        {
            var bytes = pending.Write.GetAwaiter().GetResult();
            if (ReferenceEquals(_history, pending.History) && _hasProject &&
                Valid(pending.Checkpoint.State.Position, pending.Checkpoint.State.HistoryHash))
                CommitCheckpoint(pending.Checkpoint, bytes);
            else File.Delete(pending.Checkpoint.State.Path);
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke("Automatic checkpoint could not be saved: " + ex.Message);
        }
    }
}
