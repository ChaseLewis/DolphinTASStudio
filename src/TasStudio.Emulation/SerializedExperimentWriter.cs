using TasStudio.Sdk;

namespace TasStudio.Emulation;

/// <summary>Owns the writer lifetime; even asynchronous implementations cannot overlap calls.</summary>
internal sealed class SerializedExperimentWriter(IExperimentResultWriter writer) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _closed;

    public Task InitializeAsync(ExperimentOutputContext context, CancellationToken token) =>
        CallAsync(() => writer.InitializeAsync(context, token));

    // Completed results are persisted even after worker cancellation.
    public Task WriteAsync(ExperimentResult result) => CallAsync(() => writer.WriteAsync(new(
        result.Index, result.Name, result.Status, result.Error, result.Started, result.WallSeconds,
        result.Position, result.Frame, result.EmulatedSeconds, result.Value, result.ProjectPath), CancellationToken.None));

    private async Task CallAsync(Func<Task> action)
    {
        await _gate.WaitAsync();
        try { ObjectDisposedException.ThrowIf(_closed, this); await action(); }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_closed) return;
            _closed = true;
            await writer.DisposeAsync();
        }
        finally { _gate.Release(); }
    }
}
