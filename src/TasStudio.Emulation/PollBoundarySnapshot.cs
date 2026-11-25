using System.Collections;
using System.Collections.Immutable;

namespace TasStudio.Emulation;

/// <summary>Immutable poll and field prefixes shared across timeline publications.</summary>
public sealed class PollBoundarySnapshot : IReadOnlyList<ulong>
{
    private readonly record struct Boundary(ulong Polls, double Fields, bool Complete, int Unverified = 0,
        double Ticks = 0, int Unrecorded = 0);
    private readonly ImmutableList<Boundary> _boundaries;
    internal static PollBoundarySnapshot Empty { get; } = new(ImmutableList.Create(new Boundary(0, 0, true)));
    private PollBoundarySnapshot(ImmutableList<Boundary> boundaries) => _boundaries = boundaries;
    public int Count => _boundaries.Count;
    public ulong this[int index] => _boundaries[index].Polls;
    public bool IsComplete => Count > 1 && _boundaries[^1].Complete;
    internal double FieldsAt(int index) => _boundaries[index].Fields;
    internal int UnverifiedAt(int index) => _boundaries[index].Unverified;
    internal double TicksAt(int index) => _boundaries[index].Ticks;
    internal int UnrecordedAt(int index) => _boundaries[index].Unrecorded;

    internal PollBoundarySnapshot Update(int inputCount, int firstChanged,
        IReadOnlyDictionary<int, RecordedInputFrame> frames, Func<int, string>? historyAt = null)
    {
        var start = Math.Min(firstChanged, Math.Min(inputCount, Count - 1));
        if (start == inputCount && Count == inputCount + 1) return this;
        var updated = _boundaries.RemoveRange(start + 1, Count - start - 1).ToBuilder();
        var previous = updated[^1];
        for (var index = start; index < inputCount; index++)
        {
            var recorded = frames.TryGetValue(index, out var frame);
            var polls = recorded ? (ulong)frame!.Frame.Polls.Length : 0;
            var verified = recorded && (historyAt == null || frame!.PrefixHash == historyAt(index));
            previous = new(previous.Polls + polls, previous.Fields + (recorded ? frame!.Frame.Fields : 1),
                previous.Complete && polls > 0, previous.Unverified + (verified ? 0 : 1),
                previous.Ticks + (recorded ? frame!.Frame.Ticks : 0), previous.Unrecorded + (recorded ? 0 : 1));
            updated.Add(previous);
        }
        return new(updated.ToImmutable());
    }

    public IEnumerator<ulong> GetEnumerator()
    {
        foreach (var boundary in _boundaries) yield return boundary.Polls;
    }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
