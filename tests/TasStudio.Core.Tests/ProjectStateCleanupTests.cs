using System.Security.Cryptography;
using System.Diagnostics;
using System.Text.Json;
using TasStudio.Core;
using TasStudio.Emulation;
using Xunit;

namespace TasStudio.Core.Tests;

public sealed class ProjectStateCleanupTests
{
    private static ProjectManifest Read(string path) => JsonSerializer.Deserialize<ProjectManifest>(File.ReadAllBytes(path))!;
    private static void Write(string path, ProjectManifest manifest) => File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(manifest));
    private static string Resolve(TestWorkspace workspace, ProjectAsset asset) => Path.GetFullPath(asset.Path, workspace.DirectoryPath);
    private static async Task<string> SaveWithStates(ExecutionService service, TestWorkspace workspace)
    {
        await service.LoadGameAsync(workspace.GamePath, workspace.Options); await service.NewProjectAsync();
        await service.ConfigureCheckpointsAsync(new(IntervalSeconds: 1));
        for (var i = 0; i < 60; i++) await service.StepAsync();
        await service.SaveNamedStateAsync("Named");
        var path = workspace.FilePath("movie.tasproj"); await service.SaveProjectAsync(path); return path;
    }
    private static Task Invalidate(ExecutionService service) => service.SetInputAsync(0, ControllerState.Neutral with { Buttons = PadButtons.A });

    [Fact]
    public async Task ClearedMarkersArePersistedAndOwnedAssetsRetiredWithoutDeletingExports()
    {
        using var w = new TestWorkspace(); using var s = new ExecutionService(new FakeBackend());
        var path = await SaveWithStates(s, w); var before = Read(path);
        var named = Resolve(w, Assert.Single(before.States).Data);
        var checkpoint = Resolve(w, Assert.Single(before.AutomaticCheckpoints).State.Data);
        var export = w.FilePath("keep-export.tasstate"); await s.SaveStateAsync(export);
        var cleared = s.StateMarkers.ToArray();
        foreach (var marker in cleared) await s.ClearMarkerAsync(marker.Id);
        Assert.Empty(s.StateMarkers);
        Assert.True(File.Exists(named)); Assert.True(File.Exists(checkpoint)); Assert.True(File.Exists(export));
        await s.SaveProjectAsync(path);
        Assert.False(File.Exists(named)); Assert.False(File.Exists(checkpoint)); Assert.True(File.Exists(export));
        var saved = FolderProject.Load(path);
        Assert.Empty(saved.States); Assert.Empty(saved.AutomaticCheckpoints);
        Assert.True(File.Exists(Resolve(w, Read(path).InitialState)));
        await s.LoadProjectAsync(path, w.Options);
        // Replaying to the saved position may generate a new automatic checkpoint.
        Assert.DoesNotContain(s.StateMarkers, marker => !marker.Automatic || cleared.Any(old => old.Id == marker.Id));
        Assert.Equal(60, s.Inputs.Count);
    }

    [Fact]
    public async Task SuccessfulCommitDeletesDroppedStatesButPreservesOriginalExportsAndUnreferencedFiles()
    {
        using var w = new TestWorkspace(); using var s = new ExecutionService(new FakeBackend()); var path = await SaveWithStates(s, w);
        var before = Read(path); var named = Resolve(w, Assert.Single(before.States).Data);
        var checkpoint = Resolve(w, Assert.Single(before.AutomaticCheckpoints).State.Data);
        var export = w.FilePath("keep-export.tasstate"); await s.SaveStateAsync(export);
        var unrelated = w.FilePath("states/" + new string('A', 64) + ".tasstate"); File.Copy(named, unrelated);
        var baseline = Resolve(w, before.InitialState);
        await Invalidate(s);
        Assert.True(File.Exists(named)); Assert.True(File.Exists(checkpoint)); // The old manifest is still usable.
        await s.SaveProjectAsync(path);
        Assert.False(File.Exists(named)); Assert.False(File.Exists(checkpoint));
        Assert.True(File.Exists(baseline)); Assert.True(File.Exists(export)); Assert.True(File.Exists(unrelated));
        Assert.Empty(FolderProject.Load(path).States); Assert.Empty(FolderProject.Load(path).AutomaticCheckpoints);
    }

    [Fact]
    public async Task OtherManifestInSameDirectoryProtectsSharedNamedAndAutomaticStates()
    {
        using var w = new TestWorkspace(); using var s = new ExecutionService(new FakeBackend()); var path = await SaveWithStates(s, w);
        var before = Read(path); var sibling = w.FilePath("alternate.TASPROJ"); File.Copy(path, sibling);
        await Invalidate(s); await s.SaveProjectAsync(path);
        Assert.True(File.Exists(Resolve(w, Assert.Single(before.States).Data)));
        Assert.True(File.Exists(Resolve(w, Assert.Single(before.AutomaticCheckpoints).State.Data)));
        Assert.Single(FolderProject.Load(sibling).States); Assert.Single(FolderProject.Load(sibling).AutomaticCheckpoints);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    public async Task UnreadableNeighborFailsClosedWithoutFailingTheSave(string contents)
    {
        using var w = new TestWorkspace(); using var s = new ExecutionService(new FakeBackend()); var path = await SaveWithStates(s, w);
        var before = Read(path); File.WriteAllText(w.FilePath("unreadable.tasproj"), contents);
        await Invalidate(s); await s.SaveProjectAsync(path);
        Assert.True(File.Exists(Resolve(w, Assert.Single(before.States).Data)));
        Assert.True(File.Exists(Resolve(w, Assert.Single(before.AutomaticCheckpoints).State.Data)));
        Assert.Empty(FolderProject.Load(path).States);
    }

    [Fact]
    public async Task ChangedStateBytesAreNotDeletedUnderAnOldManifestHash()
    {
        using var w = new TestWorkspace(); using var s = new ExecutionService(new FakeBackend()); var path = await SaveWithStates(s, w);
        var target = Resolve(w, Assert.Single(Read(path).States).Data);
        await Invalidate(s); File.WriteAllText(target, "Someone replaced this asset"); await s.SaveProjectAsync(path);
        Assert.Equal("Someone replaced this asset", File.ReadAllText(target));
    }

    [Theory]
    [InlineData("export.tasstate")]
    [InlineData("states/export.tasstate")]
    public async Task HandReferencedUserNamedExportsArePreserved(string exportName)
    {
        using var w = new TestWorkspace(); using var s = new ExecutionService(new FakeBackend()); var path = await SaveWithStates(s, w);
        var previous = Read(path); var state = Assert.Single(previous.States); var original = Resolve(w, state.Data);
        var export = w.FilePath(exportName); File.Copy(original, export);
        Write(path, previous with { States = [state with { Data = state.Data with { Path = exportName } }] });
        await Invalidate(s); await s.SaveProjectAsync(path);
        Assert.True(File.Exists(export));
    }

    [Fact]
    public async Task ReplacingBaselineDeletesOnlyTheOldUnreferencedBaselineAfterCommit()
    {
        using var w = new TestWorkspace(); using var s = new ExecutionService(new FakeBackend()); var path = await SaveWithStates(s, w);
        var before = Read(path); var content = FolderProject.Load(path); var oldBaseline = Resolve(w, before.InitialState);
        var bytes = (byte[])content.Archive.InitialState.Data.Clone(); bytes[8] ^= 1;
        var initial = content.Archive.InitialState with { Data = bytes };
        var metadata = content.Archive.Metadata with { StateHash = Convert.ToHexString(SHA256.HashData(bytes)) };
        FolderProject.Save(path, metadata, initial, [], [], []);
        Assert.False(File.Exists(oldBaseline));
        Assert.True(File.Exists(Resolve(w, Read(path).InitialState))); Assert.Equal(bytes, FolderProject.Load(path).Archive.InitialState.Data);
    }

    [Fact]
    public async Task FailedCommitDoesNotDeleteAnyPreviousStateAsset()
    {
        using var w = new TestWorkspace(); using var s = new ExecutionService(new FakeBackend()); var path = await SaveWithStates(s, w);
        var before = Read(path); var manifestBytes = File.ReadAllBytes(path); var content = FolderProject.Load(path);
        Assert.Throws<InvalidDataException>(() => FolderProject.Save(path,
            content.Archive.Metadata with { StateHash = "invalid" }, content.Archive.InitialState, [], [], []));
        Assert.Equal(manifestBytes, File.ReadAllBytes(path));
        Assert.True(File.Exists(Resolve(w, before.InitialState))); Assert.True(File.Exists(Resolve(w, Assert.Single(before.States).Data)));
        Assert.True(File.Exists(Resolve(w, Assert.Single(before.AutomaticCheckpoints).State.Data)));
        FolderProject.Load(path);
    }

    [Fact]
    public async Task PreviousManifestPathEscapingStatesDirectoryCannotDeleteExport()
    {
        using var w = new TestWorkspace(); using var s = new ExecutionService(new FakeBackend()); var path = await SaveWithStates(s, w);
        var previous = Read(path); var state = Assert.Single(previous.States);
        var export = w.FilePath(Path.GetFileName(state.Data.Path)); File.Copy(Resolve(w, state.Data), export);
        Write(path, previous with { States = [state with { Data = state.Data with { Path = "states/../" + Path.GetFileName(export) } }] });
        await Invalidate(s); await s.SaveProjectAsync(path);
        Assert.True(File.Exists(export));
    }

    [Fact]
    public async Task StatesDirectoryJunctionPreventsDeletionThroughItsTarget()
    {
        if (!OperatingSystem.IsWindows()) return; // The application and this junction regression target Windows.
        using var w = new TestWorkspace(); using var s = new ExecutionService(new FakeBackend()); var path = await SaveWithStates(s, w);
        var before = Read(path); var states = w.FilePath("states"); var relocated = w.FilePath("relocated-states");
        Directory.Move(states, relocated);
        var junctionCreated = false;
        try
        {
            // Creating a junction requires no symlink privilege. All paths are generated inside this test's private directory.
            var start = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{states}\" \"{relocated}\"")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using var process = Process.Start(start)!; await process.WaitForExitAsync();
            Assert.True(process.ExitCode == 0, await process.StandardError.ReadToEndAsync()); junctionCreated = true;
            Assert.True((File.GetAttributes(states) & FileAttributes.ReparsePoint) != 0);
            await Invalidate(s); await s.SaveProjectAsync(path);
            Assert.True(File.Exists(Path.Combine(relocated, Path.GetFileName(Assert.Single(before.States).Data.Path))));
            Assert.True(File.Exists(Path.Combine(relocated, Path.GetFileName(Assert.Single(before.AutomaticCheckpoints).State.Data.Path))));
        }
        finally
        {
            if (junctionCreated) Directory.Delete(states); // Remove only the junction itself, without recursion.
            Directory.Move(relocated, states);
        }
    }
}
