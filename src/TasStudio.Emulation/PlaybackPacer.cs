namespace TasStudio.Emulation;

/// <summary>Wall-clock pacing only; never changes emulated timing or recorded inputs.</summary>
internal sealed class PlaybackPacer
{
    private static readonly TimeSpan MaximumCatchUp = TimeSpan.FromMilliseconds(100);
    public TimeSpan Deadline { get; private set; }
    public void Restart(TimeSpan now) => Deadline = now;
    public void Advance(TimeSpan emulatedDuration, TimeSpan now)
    {
        Deadline += emulatedDuration;
        // Recover short stalls (and the audio they consumed), while bounding
        // catch-up work after a long shader compile or suspended application.
        if (Deadline < now - MaximumCatchUp) Deadline = now - MaximumCatchUp;
    }
}
