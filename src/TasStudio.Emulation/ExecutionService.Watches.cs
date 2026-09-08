using TasStudio.Core;

namespace TasStudio.Emulation;

public sealed record WatchSample(long Generation, ulong Position, bool Current, WatchValue[] Values);
public sealed partial class ExecutionService
{
    private long _sampleGeneration;
    public long SampleGeneration => Interlocked.Read(ref _sampleGeneration);

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
