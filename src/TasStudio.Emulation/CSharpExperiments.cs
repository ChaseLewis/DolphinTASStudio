using System.Reflection;
using System.Runtime.Loader;
using TasStudio.Core;
using TasStudio.Sdk;

namespace TasStudio.Emulation;

internal sealed class ExperimentAssembly : AssemblyLoadContext, IDisposable
{
    private readonly AssemblyDependencyResolver _resolver;
    private readonly string _path;
    private readonly bool _loadInMemory;
    private Assembly? _assembly;
    public ExperimentAssembly(string path, bool loadInMemory = false) : base(isCollectible: true)
    { _path = Path.GetFullPath(path); _resolver = new(_path); _loadInMemory = loadInMemory; }
    public IExperiment Create(string? typeName) => (IExperiment)(Activator.CreateInstance(FindExperimentType(typeName))
        ?? throw new InvalidDataException("Experiment needs a public parameterless constructor."));

    public Type? GetResultType(string? typeName)
    {
        var results = FindExperimentType(typeName).GetInterfaces()
            .Where(t => t.IsGenericType && t.GetGenericTypeDefinition() == typeof(IExperiment<>)).ToArray();
        if (results.Length > 1) throw new InvalidDataException("Declare one result type per experiment.");
        return results.SingleOrDefault()?.GenericTypeArguments[0];
    }

    private Type FindExperimentType(string? typeName)
    {
        var assembly = _assembly ??= LoadManagedAssembly(_path);
        var candidates = assembly.GetExportedTypes().Where(t => !t.IsAbstract && !t.ContainsGenericParameters && typeof(IExperiment).IsAssignableFrom(t)
            && (typeName == null || t.FullName == typeName)).ToArray();
        if (candidates.Length != 1) throw new InvalidDataException($"Select one public IExperiment class by its full type name; found {candidates.Length}.");
        return candidates[0];
    }
    protected override Assembly? Load(AssemblyName name)
    {
        // Share the contract types with the worker even when publish copied their DLLs.
        foreach (var shared in new[] { typeof(IExperiment).Assembly, typeof(ControllerState).Assembly })
            if (name.Name == shared.GetName().Name)
            {
                if (name.Version != shared.GetName().Version) throw new InvalidDataException("Experiment SDK version differs from this Studio build.");
                return shared;
            }
        var dependency = _resolver.ResolveAssemblyToPath(name);
        return dependency == null ? null : LoadManagedAssembly(dependency);
    }
    private Assembly LoadManagedAssembly(string path)
    {
        if (!_loadInMemory) return LoadFromAssemblyPath(path);
        // Schema readers can outlive disposal through reflection/serializer caches.
        // Avoid mapping batch DLLs into memory, which locks them on Windows until GC.
        using var stream = File.OpenRead(path);
        return LoadFromStream(stream);
    }
    protected override IntPtr LoadUnmanagedDll(string name)
    { var path = _resolver.ResolveUnmanagedDllToPath(name); return path == null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path); }
    public void Dispose() => Unload();
}

internal sealed class ExperimentEmulator(ExecutionService execution, CancellationToken token) : IExperimentEmulator, IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1);
    private volatile bool _closed;
    private async Task<T> Call<T>(Func<Task<T>> operation)
    {
        await _gate.WaitAsync(token);
        try { ObjectDisposedException.ThrowIf(_closed, this); token.ThrowIfCancellationRequested(); return await operation(); }
        finally { _gate.Release(); }
    }
    private Task Call(Func<Task> operation) => Call(async () => { await operation(); return true; });
    public Task<EmulatorPosition> GetPositionAsync() => Call(() => Task.FromResult(new EmulatorPosition(execution.Position, execution.VideoFieldCount, execution.ElapsedSeconds, execution.Inputs.Count, execution.IsPreviewCurrent)));
    public Task<long> GetStartUtcAsync() => Call(async () => (await execution.GetConfigurationAsync())!.Options.Configuration?.StartUtcSeconds ?? 946684800);
    public Task<ControllerState> GetInputAsync(int group) => Call(() =>
    {
        if (group < 0 || group > execution.Inputs.Count) throw new ArgumentOutOfRangeException(nameof(group));
        return Task.FromResult(group == execution.Inputs.Count ? ControllerState.Neutral : execution.Inputs[group]);
    });
    public Task SetInputAsync(int group, ControllerState input) => Call(() => execution.SetInputAsync(group, input));
    public Task SetCurrentInputAsync(ControllerState state) => Call(() => execution.SetCurrentInputAsync(state));
    public Task AdvanceAsync(ControllerState? input = null, int count = 1) => Call(async () =>
    {
        if (count is < 1 or > 100000) throw new ArgumentOutOfRangeException(nameof(count));
        for (var i = 0; i < count; i++)
        {
            token.ThrowIfCancellationRequested();
            if (!await execution.AdvanceFrameAsync(input ?? ControllerState.Neutral)) throw new InvalidOperationException("Preview predates edits; seek before advancing.");
        }
    });
    public Task<bool> PlayAsync() => Call(() => execution.StepRecordedFrameAsync());
    public Task SeekAsync(ulong group) => Call(() => execution.SeekAsync(group, token));
    public Task<string> SaveStateAsync(string name) => Call(async () =>
    {
        var existing = execution.StateMarkers.Select(s => s.Id).ToHashSet();
        await execution.SaveNamedStateAsync(name);
        return execution.StateMarkers.Single(s => !existing.Contains(s.Id) && !s.Automatic).Id;
    });
    public Task LoadStateAsync(string id) => Call(() => execution.LoadMarkerAsync(id));
    public Task LoadStateAsync(string id, bool clearLaterInput) => Call(() => execution.LoadMarkerAsync(id, clearLaterInput));
    internal Task SubmitPlayAsync(Func<Task> submit) => Call(submit);
    public Task DeleteStateAsync(string id) => Call(() => execution.ClearMarkerAsync(id, requireFileDeletion: true));
    public Task<IReadOnlyList<ExperimentState>> GetStatesAsync() => Call(() => Task.FromResult<IReadOnlyList<ExperimentState>>(
        Array.AsReadOnly(execution.StateMarkers.Select(s => new ExperimentState(s.Id, s.Name, s.Position, s.Valid)).ToArray())));
    public Task<string> AddTakeAsync(string name, int startGroup, IEnumerable<ControllerState> inputs)
    {
        var copy = inputs.Take(100001).ToArray();
        return Call(() => execution.ImportInputTakeAsync(name, startGroup, copy, "C# experiment"));
    }
    public Task<byte[]> ReadMemoryAsync(uint address, int count) => Call(() =>
    {
        if (count is < 1 or > 1048576) throw new ArgumentOutOfRangeException(nameof(count));
        return execution.ReadMemoryAsync(address, count);
    });
    public Task WriteMemoryAsync(uint address, byte[] bytes)
    {
        if (bytes.Length is < 1 or > 1048576) throw new ArgumentOutOfRangeException(nameof(bytes));
        var copy = bytes.ToArray(); return Call(() => execution.WriteMemoryAsync(address, copy));
    }
    public async ValueTask DisposeAsync()
    {
        _closed = true;
        // Drain the current SDK call before saving; late/background calls cannot mutate results.
        await _gate.WaitAsync(); _gate.Release();
    }
}
