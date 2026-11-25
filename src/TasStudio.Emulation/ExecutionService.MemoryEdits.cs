namespace TasStudio.Emulation;

// An immutable UI receipt. EventIndex preserves the order of writes at one boundary.
public sealed record MemoryEdit(int EventIndex, ulong Position, uint Address, string HexBytes);
public sealed record MemoryEditsSnapshot(long Revision, IReadOnlyList<MemoryEdit> Edits);

public sealed partial class ExecutionService
{
    private IReadOnlyList<MemoryEdit> _publishedMemoryEdits = [];
    private MemoryEditsSnapshot _publishedMemoryEditSnapshot = new(0, []);
    public MemoryEditsSnapshot MemoryEditSnapshot => Volatile.Read(ref _publishedMemoryEditSnapshot);
    public IReadOnlyList<MemoryEdit> MemoryEdits => MemoryEditSnapshot.Edits;

    public Task RemoveMemoryEditsAsync(long expectedRevision, ulong? position = null, int? eventIndex = null) => Enqueue(() =>
    {
        RequireProject(); Pause();
        if (_takingOver || _trialPlayback)
            throw new InvalidOperationException("Return to Active playback before clearing memory edits.");
        if (_revision != expectedRevision)
            throw new InvalidOperationException("The recording changed. Open the memory edits again before clearing them.");
        var indices = _events.Select((entry, index) => (entry, index))
            .Where(e => e.entry.Kind == ExecutionEventKind.MemoryWrite &&
                (position == null || e.entry.Position == position) && (eventIndex == null || e.index == eventIndex))
            .Select(e => e.index).ToArray();
        if (indices.Length == 0) return;
        var first = indices.Min(i => _events[i].Position);
        var target = Math.Min(_backend.Position, (ulong)_inputs.Count);
        RememberEdit(rebuildPreview: true);
        foreach (var index in indices.Reverse()) _events.RemoveAt(index);
        HistoryChanged(checked((int)first));
        // Restoring old bytes cannot undo downstream game logic. Replay the edited movie.
        // If replay fails, the edit remains undoable and the preview remains stale.
        _executedHistory = null;
        Seek(target);
        Notify($"Cleared {indices.Length} memory edit{(indices.Length == 1 ? "" : "s")}; Undo restores them");
    });

    private void PublishMemoryEdits()
    {
        PublishIfChanged(ref _publishedMemoryEdits, !_hasProject ? [] : _events.Select((entry, index) => (entry, index))
            .Where(e => e.entry.Kind == ExecutionEventKind.MemoryWrite)
            .Select(e => new MemoryEdit(e.index, e.entry.Position, e.entry.Address, Convert.ToHexString(e.entry.Bytes!))));
        // Publish the receipt and its revision together; the UI must never pair
        // old event indices with a revision from a newer owner-thread edit.
        Volatile.Write(ref _publishedMemoryEditSnapshot, new(_revision, _publishedMemoryEdits));
    }
}
