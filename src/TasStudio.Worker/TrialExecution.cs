using TasStudio.Dolphin;
using TasStudio.Emulation;

namespace TasStudio.Worker;

internal static class TrialExecution
{
    public static async Task<int> RunAsync(ExperimentJob job, CancellationToken token = default, Action<VideoFrame>? showFrame = null)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        cancellation.CancelAfter(TimeSpan.FromSeconds(job.TimeoutSeconds));
        ConsoleCancelEventHandler cancelKey = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += cancelKey;
        try
        {
            using var monitor = new Timer(_ =>
            {
                try { if (File.Exists(Path.Combine(job.OutputDirectory, "cancel"))) cancellation.Cancel(); }
                catch (ObjectDisposedException) { }
            }, null, 0, 100);
            using var execution = new ExecutionService(new DolphinBackend());
            if (showFrame != null) execution.VideoReady += showFrame;
            var result = await ExperimentWorker.RunAsync(job, execution, cancellation.Token);
            return result.Status == "completed" ? 0 : result.Status == "cancelled" ? 2 : 1;
        }
        finally { Console.CancelKeyPress -= cancelKey; }
    }
}
