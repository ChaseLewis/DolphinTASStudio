namespace TasStudio.Emulation;

public sealed partial class ExecutionService
{
    private EmulatorRuntime? _baselineRuntime;
    private EmulatorRuntime[] _runtimeHistory = [];
    private bool _allowRuntimeMismatch;
    private string? _compatibilityWarning;
    public string? CompatibilityWarning => Volatile.Read(ref _compatibilityWarning);
    private EmulatorRuntime CurrentRuntime => new(_backend.Identity, _backend.ConfigurationIdentity);
    private EmulatorRuntime BaselineRuntime => _baselineRuntime ?? CurrentRuntime;

    private void RememberRuntime(ArchiveMetadata metadata)
        => RememberRuntime(new(metadata.BackendIdentity, metadata.ConfigurationIdentity), metadata.RuntimeHistory);

    private void RememberRuntime(EmulatorRuntime baseline, IEnumerable<EmulatorRuntime> history)
    {
        var runtimes = _runtimeHistory.Concat(history).Append(baseline).Append(CurrentRuntime).Distinct().ToArray();
        _runtimeHistory = runtimes.Length > 1 ? runtimes : [];
        Volatile.Write(ref _compatibilityWarning, _runtimeHistory.Length > 1 ?
            "Warning: this TAS was created or used with a different emulator build or compatibility resources. Playback and experiment results may differ." : null);
    }

    private void ClearCompatibility()
    {
        _baselineRuntime = null;
        _runtimeHistory = [];
        _allowRuntimeMismatch = false;
        Volatile.Write(ref _compatibilityWarning, null);
    }
}
