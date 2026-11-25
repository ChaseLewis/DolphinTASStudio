namespace TasStudio.Emulation;

public sealed partial class ExecutionService
{
    private int _renderResolutionOverride;
    public bool SupportsRenderResolution => _backend is IRenderResolutionBackend;
    public int? RenderResolutionOverride => Volatile.Read(ref _renderResolutionOverride) is var scale && scale != 0 ? scale : null;

    /// <summary>Session-only experimental presentation setting; leaves project configuration and state history intact.</summary>
    public Task SetRenderResolutionAsync(int? scale) => Enqueue(() =>
    {
        RequireProject();
        if (scale is < 1 or > 4) throw new ArgumentOutOfRangeException(nameof(scale));
        if (RenderResolutionOverride == scale) return;
        if (_backend is not IRenderResolutionBackend renderer)
            throw new NotSupportedException("This backend does not support experimental render resolution.");
        renderer.SetRenderResolution(scale);
        Volatile.Write(ref _renderResolutionOverride, scale ?? 0);
        Notify(scale is { } value ? $"Experimental render resolution: {value}× native (next frame)" : "Project render resolution restored (next frame)");
    });
}
