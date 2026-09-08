using TasStudio.App;
using Xunit;

namespace TasStudio.Core.Tests;

public sealed class DevWorkspaceTests
{
    [Fact]
    public void DefaultKeepsExistingDataWhilePortableWorkspaceIsIsolated()
    {
        using var workspace = new TestWorkspace();
        var app = Path.Combine(workspace.DirectoryPath, "app");
        Directory.CreateDirectory(app);
        var existing = Path.Combine(workspace.DirectoryPath, "existing-data");
        Assert.Equal(existing, AppPaths.ResolveDataDirectory(app, existing));
        File.WriteAllText(Path.Combine(app, "tasstudio-data.path"), "../data\n");
        Assert.Equal(Path.Combine(workspace.DirectoryPath, "data"), AppPaths.ResolveDataDirectory(app, existing));
        // Restarting resolves the same directory, regardless of the caller's working directory.
        Assert.Equal(AppPaths.ResolveDataDirectory(app, existing), AppPaths.ResolveDataDirectory(app, "different-default"));
    }

    [Fact]
    public void EmptyPortableMarkerCannotSilentlyFallBackToSharedData()
    {
        using var workspace = new TestWorkspace();
        File.WriteAllText(workspace.FilePath("tasstudio-data.path"), " \n");
        Assert.Throws<InvalidDataException>(() => AppPaths.ResolveDataDirectory(workspace.DirectoryPath, "shared-data"));
    }
}
