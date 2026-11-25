namespace TasStudio.Emulation;

public sealed partial class ExecutionService
{
    public Task<string> PrepareProjectMemoryCardAsync(string projectPath) => Enqueue(() =>
    {
        RequireProject(); Pause();
        if (_options!.Configuration?.UseGciFolder == true && _backend is IGciFolderBackend card) card.FlushGciFolder();
        var directory = ProjectMemoryCards.DirectoryFor(projectPath);
        Directory.CreateDirectory(directory);
        return directory;
    });

    public async Task RestartFromProjectMemoryCardAsync(string projectPath)
    {
        var current = (await GetConfigurationAsync())?.Options ?? throw new InvalidOperationException("Open a project first.");
        var configuration = current.Configuration ?? EmulationConfiguration.FromLegacy(current.InternalResolution, current.DspHle);
        await ApplyConfigurationAsync(configuration with { UseGciFolder = true, MemoryCardSizeOverride = null }, Checkpoints,
            forceRestart: true, gciDirectory: ProjectMemoryCards.DirectoryFor(projectPath));
    }
}
