namespace TasStudio.Emulation;

internal static class ExperimentArtifactCleanup
{
    internal static bool RetainArtifacts(string status) => status is "failed" or "timed out";

    // Called only after the coordinator has committed the result and the owned
    // worker has exited. Never use a path supplied by an experiment's result.
    internal static void RemoveTrial(string batchDirectory, int index, Action<string>? progress)
    {
        try
        {
            if (index < 0) throw new InvalidDataException("Invalid trial index for cleanup.");
            var batch = Path.GetFullPath(batchDirectory);
            var target = Path.GetFullPath(Path.Combine(batch, $"run-{index + 1:00000}"));
            if (!string.Equals(Path.GetDirectoryName(target), batch.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                throw new IOException("Trial cleanup target is outside the batch.");
            if (!Directory.Exists(target)) return;
            RejectLink(batch);
            var pending = new Stack<string>(); pending.Push(target);
            while (pending.TryPop(out var directory))
            {
                RejectLink(directory);
                foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
                {
                    RejectLink(entry);
                    if (Directory.Exists(entry)) pending.Push(entry);
                }
            }
            Directory.Delete(target, recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            progress?.Invoke($"Trial {index} result is saved, but artifact cleanup will be retried on resume: {error.Message}");
        }
    }

    private static void RejectLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Trial cleanup refuses filesystem links: " + path);
    }
}
