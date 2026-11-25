namespace TasStudio.Emulation;

/// <summary>Immutable timeline timing. Measuring a range only reads its two boundaries.</summary>
public sealed record InputTimingSnapshot(PollBoundarySnapshot Boundaries, double FramesPerSecond, double? TicksPerSecond = null)
{
    public static InputTimingSnapshot Empty { get; } = new(PollBoundarySnapshot.Empty, 60);

    public InputRangeTiming Measure(int start, int end)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfLessThan(end, start);
        var last = Boundaries.Count - 1;
        double Seconds(int index)
        {
            var boundary = Math.Min(index, last);
            var extra = Math.Max(0, index - last);
            // Dolphin's transport uses CoreTiming ticks. Use the same clock so
            // start/end match playback even when a group spans uneven fields.
            return TicksPerSecond is > 0
                ? Boundaries.TicksAt(boundary) / TicksPerSecond.Value + (Boundaries.UnrecordedAt(boundary) + extra) / FramesPerSecond
                : (Boundaries.FieldsAt(boundary) + extra) / FramesPerSecond;
        }
        int Unverified(int index) => Boundaries.UnverifiedAt(Math.Min(index, last)) + Math.Max(0, index - last);
        return new(Seconds(start), Seconds(end),
            Unverified(end) != Unverified(start), Unverified(start) != 0, Unverified(end) != 0);
    }
}

public readonly record struct InputRangeTiming(double StartSeconds, double EndSeconds,
    bool IsDurationEstimated, bool IsStartEstimated, bool IsEndEstimated)
{
    public double DurationSeconds => EndSeconds - StartSeconds;
}
