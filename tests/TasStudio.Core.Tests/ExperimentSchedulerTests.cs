using System.Text.Json;
using TasStudio.Emulation;
using Xunit;

namespace TasStudio.Core.Tests;

public sealed class ExperimentSchedulerTests
{
    [Fact]
    public async Task EightWorkersAreAcceptedAndRunConcurrently()
    {
        var definition = new ExperimentDefinition(1, "Eight workers", ExperimentStart.Boot,
            null, null, 8, 300, [new("Trial", JsonSerializer.SerializeToElement(new { }))]);
        definition.Validate();
        Assert.Throws<InvalidDataException>(() => (definition with { Parallelism = 0 }).Validate());
        Assert.Throws<InvalidDataException>(() => (definition with { Parallelism = 9 }).Validate());
        var allEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        var visits = new int[16];
        var run = ExperimentScheduler.RunAsync(visits.Length, definition.Parallelism, async index =>
        {
            var inFlight = Interlocked.Increment(ref active);
            try
            {
                Assert.InRange(inFlight, 1, 8);
                if (inFlight == 8) allEntered.TrySetResult();
                await release.Task;
                Interlocked.Increment(ref visits[index]);
            }
            finally { Interlocked.Decrement(ref active); }
        });
        try { await allEntered.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
        finally { release.TrySetResult(); await run; }
        Assert.All(visits, count => Assert.Equal(1, count));
        Assert.Equal(0, active);
    }

    [Fact]
    public async Task CancellationStopsSchedulingAndWaitsForStartedTrialsToFinishSaving()
    {
        using var cancellation = new CancellationTokenSource();
        var entered = 0; var saved = 0;
        await ExperimentScheduler.RunAsync(50001, 4, async _ =>
        {
            Interlocked.Increment(ref entered);
            cancellation.Cancel();
            await Task.Yield(); // In-flight work must finish despite scheduler cancellation.
            Interlocked.Increment(ref saved);
        }, cancellation.Token);
        Assert.InRange(entered, 1, 4);
        Assert.Equal(entered, saved);
        await ExperimentScheduler.RunAsync(50001, 4, _ => throw new Exception("Must not start"), cancellation.Token);
    }

    [Fact]
    public async Task FiftyThousandPlusTrialsAreAcceptedAndScheduledWithBoundedConcurrency()
    {
        const int count = 50001;
        var parameters = JsonSerializer.SerializeToElement(new { });
        var trials = Enumerable.Range(0, count).Select(i => new ExperimentTrial("Trial " + i, parameters)).ToArray();
        new ExperimentDefinition(1, "Large batch", ExperimentStart.Boot, null, null,
            4, 300, trials).Validate();
        var visits = new int[count];
        var active = 0;
        await ExperimentScheduler.RunAsync(count, 4, async index =>
        {
            var inFlight = Interlocked.Increment(ref active);
            try
            {
                Assert.InRange(inFlight, 1, 4);
                await Task.Yield();
                Interlocked.Increment(ref visits[index]);
            }
            finally { Interlocked.Decrement(ref active); }
        });
        Assert.All(visits, visitsForTrial => Assert.Equal(1, visitsForTrial));
        Assert.Equal(0, active);
    }
}
