using TasStudio.Emulation;
using TasStudio.Worker;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 1)
            {
                var job = ExperimentFiles.Read<ExperimentJob>(Path.GetFullPath(args[0]));
                return job.Headless ? TrialExecution.RunAsync(job).GetAwaiter().GetResult() : WorkerPreviewApp.Run(job);
            }
            return RunCommandAsync(args).GetAwaiter().GetResult();
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static async Task<int> RunCommandAsync(string[] args)
    {
        if (args.Length == 2 && args[0] == "clean-csharp")
        {
            var count = await ExperimentRunner.CleanAsync(args[1], Console.WriteLine, CancellationToken.None);
            Console.WriteLine($"Processed cleanup for {count} completed trials or never-started placeholders; other trial artifacts were retained.");
            return 0;
        }
        if (args.Length == 3 && args[0] == "new-csharp")
        {
            CSharpWorkspace.Create(args[1], args[2]);
            return 0;
        }
        var resume = args.Length == 2 && args[0] == "resume-csharp";
        if (resume || (args.Length == 3 && args[0] == "run-csharp"))
        {
            using var cancel = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancel.Cancel(); };
            var cancelPath = Path.Combine(Path.GetFullPath(args[resume ? 1 : 2]), "cancel");
            // Resume removes the previous signal only after acquiring the batch lock.
            // Ignore that old signal until removal; a new one still cancels this invocation.
            var oldSignal = resume && File.Exists(cancelPath) ? File.GetLastWriteTimeUtc(cancelPath) : (DateTime?)null;
            using var monitor = new Timer(_ =>
            {
                try
                {
                    if (!File.Exists(cancelPath)) oldSignal = null;
                    else if (oldSignal == null || File.GetLastWriteTimeUtc(cancelPath) != oldSignal) cancel.Cancel();
                }
                catch (ObjectDisposedException) { }
            }, null, 0, 100);
            try { return resume ? await CSharpCommand.ResumeAsync(args[1], cancel.Token) : await CSharpCommand.RunAsync(args[1], args[2], cancel.Token); }
            catch (OperationCanceledException) { Console.Error.WriteLine("Batch cancelled."); return 2; }
            catch (Exception error) { Console.Error.WriteLine(error.Message); return 1; }
        }
        Console.Error.WriteLine("Usage: TasStudio.Worker <job.json> | run-csharp <experiment.tascsharp.json> <new-output-directory> | resume-csharp <batch-directory> | clean-csharp <batch-directory>");
        return 2;
    }
}
