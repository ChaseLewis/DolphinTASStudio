using TasStudio.Core;

namespace TasStudio.Emulation;

public sealed record WatchSample(long Generation, ulong Position, bool Current, WatchValue[] Values);
public sealed partial class ExecutionService
{
    private long _sampleGeneration;
    public long SampleGeneration => Interlocked.Read(ref _sampleGeneration);

    public Task WriteWatchAsync(WatchDefinition watch, string text, long expectedGeneration)
    {
        var captured = watch with { Offsets = watch.Offsets?.ToArray() };
        var bytes = WatchMemory.ParseValue(captured, text);
        return Enqueue(() =>
        {
            RequireLoaded();
            if (_running || SampleGeneration != expectedGeneration || !Current())
                throw new InvalidOperationException("Memory state changed. Cancel and edit the value again after pausing or seeking.");
            // Resolve and write on the owner thread at the same boundary; replay uses this concrete address.
            var value = WatchMemory.Read(Guid.Empty, captured, _backend.ReadMemory);
            if (value.Error is { } error) throw new InvalidDataException(error);
            AddEvent(new ExecutionEvent(_backend.Position, ExecutionEventKind.MemoryWrite, value.Address!.Value, bytes));
            Notify("Memory updated");
        });
    }

    public Task<WatchSample> SampleWatchesAsync(IEnumerable<(Guid Id, WatchDefinition Watch)> watches)
    {
        // Own the request before enqueueing: callers may edit their pointer arrays meanwhile.
        var request = watches.Take(1025).Select(w => (w.Id, Watch: w.Watch with { Offsets = w.Watch.Offsets?.ToArray() })).ToArray();
        if (request.Length > 1024) throw new ArgumentException("At most 1024 watches can be sampled together.");
        foreach (var entry in request) entry.Watch.Validate();
        return Enqueue(() =>
        {
            RequireLoaded();
            var values = request.Select(w => WatchMemory.Read(w.Id, w.Watch, _backend.ReadMemory)).ToArray();
            return new WatchSample(SampleGeneration, _backend.Position, Current(), values);
        });
    }
}
