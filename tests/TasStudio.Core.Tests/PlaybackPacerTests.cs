using System.Diagnostics;
using TasStudio.Emulation;
using Xunit;

namespace TasStudio.Core.Tests;

public sealed class PlaybackPacerTests
{
    [Fact]
    public void ShortStallKeepsDeadlineUntilPlaybackCatchesUp()
    {
        var pacing = new PlaybackPacer();
        pacing.Restart(TimeSpan.Zero);
        pacing.Advance(TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(80));
        Assert.Equal(TimeSpan.FromMilliseconds(20), pacing.Deadline);
        pacing.Advance(TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(85));
        Assert.Equal(TimeSpan.FromMilliseconds(40), pacing.Deadline);
    }

    [Fact]
    public void LongStallCapsCatchUpAndRestartDiscardsOldDeadline()
    {
        var pacing = new PlaybackPacer();
        pacing.Advance(TimeSpan.FromMilliseconds(20), TimeSpan.FromSeconds(3));
        Assert.Equal(TimeSpan.FromMilliseconds(2900), pacing.Deadline);
        pacing.Restart(TimeSpan.FromSeconds(10));
        Assert.Equal(TimeSpan.FromSeconds(10), pacing.Deadline);
    }

    [Fact]
    public void LoadingGroupWaitsForItsEntireEmulatedDuration()
    {
        var pacing = new PlaybackPacer();
        pacing.Advance(TimeSpan.FromMilliseconds(900), TimeSpan.FromMilliseconds(225));
        Assert.Equal(TimeSpan.FromMilliseconds(900), pacing.Deadline);
    }

    [Fact]
    public async Task ReadOnlyCommandsDoNotAcceleratePlayback()
    {
        using var files = new TestWorkspace();
        using var service = new ExecutionService(new FakeBackend());
        await service.LoadGameAsync(files.GamePath, files.Options);
        await service.RunAsync();
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromMilliseconds(350))
        {
            await service.GetConfigurationAsync();
            await Task.Delay(1);
        }
        await service.PauseAsync();
        // Leave scheduling tolerance, but reject the previous one-frame-per-query behavior.
        Assert.InRange(service.Position, 1UL, (ulong)(timer.Elapsed.TotalSeconds * 90 + 3));
    }
}
