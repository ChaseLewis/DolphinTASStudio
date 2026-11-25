using System.Diagnostics;

namespace TasStudio.Emulation;

/// <summary>Opt-in timings. Set TASSTUDIO_PERF_LOG to a file path outside any project; unset costs nothing.</summary>
public static class PerfLog
{
    private static readonly string? Target = Environment.GetEnvironmentVariable("TASSTUDIO_PERF_LOG") is { Length: > 0 } path ? Path.GetFullPath(path) : null;
    private static readonly object Gate = new();
    public static bool Enabled => Target != null;

    /// <summary>Entries faster than <paramref name="minimumMilliseconds"/> are dropped, so per-frame scopes stay quiet.</summary>
    public static Scope Time(string name, long size = 0, double minimumMilliseconds = 0) =>
        new(name, size, minimumMilliseconds, Target == null ? 0 : Stopwatch.GetTimestamp());

    public readonly struct Scope(string name, long size, double minimumMilliseconds, long started) : IDisposable
    {
        public void Dispose()
        {
            if (Target == null) return;
            var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (elapsed < minimumMilliseconds) return;
            var line = $"{DateTime.UtcNow:O}\t{Environment.ProcessId}\t{name}\t{elapsed:F1}\t{size}";
            try
            {
                // Workers share the file; append under a short-lived handle.
                lock (Gate)
                {
                    using var file = new FileStream(Target, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                    using var writer = new StreamWriter(file);
                    writer.WriteLine(line);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Timing must never fail the operation. */ }
        }
    }
}
