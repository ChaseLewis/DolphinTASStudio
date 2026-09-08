namespace TasStudio.Emulation;

internal static class ExperimentScheduler
{
    /// <summary>Only Parallelism callbacks are in flight, regardless of the total trial count.</summary>
    public static async Task RunAsync(int count, int parallelism, Func<int, Task> runTrial, CancellationToken token = default)
    {
        try { await Parallel.ForEachAsync(Enumerable.Range(0, count), new ParallelOptions
        {
            MaxDegreeOfParallelism = parallelism, CancellationToken = token
        }, async (index, _) => { if (!token.IsCancellationRequested) await runTrial(index); }); }
        // Parallel.ForEachAsync waits for in-flight callbacks (including result commits)
        // before throwing. Unstarted indices remain absent and therefore resumable.
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
}
