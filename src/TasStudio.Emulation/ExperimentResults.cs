using System.Text.Json;

namespace TasStudio.Emulation;

/// <summary>One instance lives in the batch coordinator, never in emulator workers.
/// Initialize defines the experiment's own schema. All calls are awaited and serialized by the runner.</summary>
internal interface IExperimentResultWriter : IAsyncDisposable
{
    Task InitializeAsync(ExperimentOutputContext context, CancellationToken cancellationToken);
    Task WriteAsync(ExperimentOutputResult result, CancellationToken cancellationToken);
}

internal sealed record ExperimentOutputContext(string BatchDirectory, string Name, int Count);

/// <summary>Value contains the object returned by this trial's RunAsync, or null on failure.</summary>
internal sealed record ExperimentOutputResult(int Index, string Name, string Status, string? Error,
    DateTimeOffset Started, double WallSeconds, ulong Position, ulong Frame, double EmulatedSeconds,
    JsonElement? Value, string? ProjectPath)
{
    public T? GetValue<T>() => Value is { ValueKind: not JsonValueKind.Null } value ? value.Deserialize<T>() : default;
}
