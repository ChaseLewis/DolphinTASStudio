using System.Text.Json;
using TasStudio.Core;

namespace TasStudio.Sdk;

public enum ExperimentStartingPoint { PowerOn, SaveState }

/// <summary>Available before boot. Contains no emulator, memory or mutable timeline APIs.</summary>
public sealed class ExperimentInitializationContext
{
    public int Index { get; }
    public int Count { get; }
    public JsonElement Parameters { get; }
    public ExperimentStartingPoint StartingPoint { get; }
    public long DefaultStartUtcSeconds { get; }
    public CancellationToken CancellationToken { get; }
    public ExperimentInitializationContext(int index, int count, JsonElement parameters,
        ExperimentStartingPoint startingPoint, long defaultStartUtcSeconds, CancellationToken cancellationToken)
    {
        if (count < 1 || index < 0 || index >= count) throw new ArgumentOutOfRangeException(nameof(index));
        Index = index; Count = count; Parameters = parameters.Clone(); StartingPoint = startingPoint;
        DefaultStartUtcSeconds = defaultStartUtcSeconds; CancellationToken = cancellationToken;
    }
    public T GetParameters<T>() => Parameters.Deserialize<T>() ?? throw new InvalidDataException("Missing experiment parameters.");
}

/// <summary>Null UTC preserves the configured default. UTC overrides are allowed only for power-on starts.</summary>
public sealed record ExperimentBootOptions(long? StartUtcSeconds = null);

public interface IExperiment
{
    /// <summary>Called once in each isolated trial before loading the emulator. The same instance subsequently runs.</summary>
    ExperimentBootOptions Initialize(ExperimentInitializationContext context) => new();
    /// <summary>Called after boot/state restoration and movie preroll. Return a JSON-serializable result.</summary>
    Task<object?> RunAsync(ExperimentRunContext context, CancellationToken cancellationToken);
}

/// <summary>Declares this experiment's result schema once, before any trials launch.</summary>
public interface IExperiment<TResult> : IExperiment where TResult : notnull
{
    new Task<TResult> RunAsync(ExperimentRunContext context, CancellationToken cancellationToken);

    async Task<object?> IExperiment.RunAsync(ExperimentRunContext context, CancellationToken cancellationToken)
        => await RunAsync(context, cancellationToken)
            ?? throw new InvalidDataException("A completed typed experiment must return a result.");
}

/// <summary>Runtime capabilities available only after boot. Frame positions are zero-based input groups, not polls or video fields.</summary>
public sealed class ExperimentRunContext
{
    private readonly Action<object?> _log;
    public int Index => Initialization.Index;
    public int Count => Initialization.Count;
    public JsonElement Parameters => Initialization.Parameters;
    public ExperimentInitializationContext Initialization { get; }
    public IExperimentEmulator Emulator { get; }
    /// <summary>Frozen original input intent before preroll or edits; structs are returned by value.</summary>
    public IReadOnlyList<ControllerState> Movie { get; }
    public ExperimentRunContext(ExperimentInitializationContext initialization, IExperimentEmulator emulator,
        IEnumerable<ControllerState> movie, Action<object?> log)
    { Initialization = initialization; Emulator = emulator; Movie = Array.AsReadOnly(movie.ToArray()); _log = log; }
    public T GetParameters<T>() => Initialization.GetParameters<T>();
    public void Log(object? value) => _log(value);
}

public sealed record EmulatorPosition(ulong Group, ulong VideoField, double Seconds, int InputCount, bool IsCurrent);
public sealed record ExperimentState(string Id, string Name, ulong Group, bool Valid);

/// <summary>All calls marshal to the emulator owner thread. Await calls in order; existing recordings always win during advance.</summary>
public interface IExperimentEmulator
{
    Task<EmulatorPosition> GetPositionAsync();
    Task<long> GetStartUtcAsync();
    Task<ControllerState> GetInputAsync(int group);
    Task SetInputAsync(int group, ControllerState input);
    /// <summary>
    /// Replace the worker's current frame-group input, or append it at the unrecorded end,
    /// without advancing. The next advance executes this input. Earlier edits still require Seek.
    /// </summary>
    Task SetCurrentInputAsync(ControllerState state);
    /// <summary>Supplied input fills only absent groups. Stale preview requires explicit Seek.</summary>
    Task AdvanceAsync(ControllerState? input = null, int count = 1);
    /// <summary>Play one recorded group. False at end; never appends input.</summary>
    Task<bool> PlayAsync();
    Task SeekAsync(ulong group);
    Task<string> SaveStateAsync(string name);
    Task LoadStateAsync(string id);
    /// <summary>Remove a state/checkpoint from this worker. Deletes worker-owned files;
    /// source-project files remain untouched. Unknown IDs or file deletion failures throw.</summary>
    Task DeleteStateAsync(string id);
    Task<IReadOnlyList<ExperimentState>> GetStatesAsync();
    Task<string> AddTakeAsync(string name, int startGroup, IEnumerable<ControllerState> inputs);
    Task<byte[]> ReadMemoryAsync(uint address, int count);
    /// <summary>Writes are recorded as ordered execution events in the result project.</summary>
    Task WriteMemoryAsync(uint address, byte[] bytes);
}
