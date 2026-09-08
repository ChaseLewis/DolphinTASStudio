using System.Security.Cryptography;
using System.Text.Json;
using TasStudio.Emulation;
using Xunit;

namespace TasStudio.Core.Tests;

public sealed class RecoveryAssetCleanupTests
{
    private static readonly byte[] State = [1, 2, 3];
    private static void Save(string path, params ControllerState[] inputs)
    {
        var metadata = new ArchiveMetadata(ProjectArchive.FormatVersion, ProjectArchive.ProjectKind, "core", "game.iso", "game", 1, true,
            (ulong)inputs.Length, 0, Convert.ToHexString(SHA256.HashData(State)), 0, 0, inputs, []);
        FolderProject.Save(path, metadata, new(0, State, null), [], [], []);
    }
    private static string[] JsonAssets(string root) => Directory.GetFiles(root, "*.json", SearchOption.AllDirectories);

    [Fact]
    public void ChangedTailCollectsOnlyPreviousUnreferencedJsonAndPreservesBaselineAndUnrelatedFile()
    {
        using var w = new TestWorkspace(); var path = w.FilePath("recovery.tasproj");
        Save(path, ControllerState.Neutral); var old = JsonAssets(w.DirectoryPath);
        var previous = RecoveryAssetCleanup.Capture(path);
        var unrelated = w.FilePath("inputs/unrelated.json"); File.WriteAllText(unrelated, "leave me");
        Save(path, ControllerState.Neutral, ControllerState.Neutral);
        var result = RecoveryAssetCleanup.Collect(previous);
        Assert.Null(result.SkippedReason); Assert.Equal(2, result.Deleted);
        Assert.All(old, file => Assert.False(File.Exists(file)));
        Assert.True(File.Exists(unrelated)); Assert.Single(Directory.GetFiles(w.FilePath("states")));
        Assert.Equal(2, FolderProject.Load(path).Archive.Metadata.Inputs.Length);
    }

    [Fact]
    public void SiblingManifestProtectsOldTailAndTimeline()
    {
        using var w = new TestWorkspace(); var path = w.FilePath("recovery.tasproj");
        Save(path, ControllerState.Neutral); var old = JsonAssets(w.DirectoryPath);
        File.Copy(path, w.FilePath("sibling.tasproj")); var previous = RecoveryAssetCleanup.Capture(path);
        Save(path, ControllerState.Neutral, ControllerState.Neutral);
        var result = RecoveryAssetCleanup.Collect(previous);
        Assert.Null(result.SkippedReason); Assert.Equal(0, result.Deleted);
        Assert.All(old, file => Assert.True(File.Exists(file)));
        Assert.Single(FolderProject.Load(w.FilePath("sibling.tasproj")).Archive.Metadata.Inputs);
    }

    [Theory]
    [InlineData("malformed-sibling")]
    [InlineData("changed-old-asset")]
    [InlineData("pending-manifest")]
    [InlineData("path-escape")]
    public void AmbiguityOrChecksumFailureStopsCollection(string failure)
    {
        using var w = new TestWorkspace(); var path = w.FilePath("recovery.tasproj");
        Save(path, ControllerState.Neutral); var old = JsonAssets(w.DirectoryPath);
        var previous = RecoveryAssetCleanup.Capture(path);
        Save(path, ControllerState.Neutral, ControllerState.Neutral);
        switch (failure)
        {
            case "malformed-sibling": File.WriteAllText(w.FilePath("sibling.tasproj"), "{"); break;
            case "changed-old-asset": File.AppendAllText(old[0], " "); break;
            case "pending-manifest": File.WriteAllText(w.FilePath("sibling.tasproj.pending.tmp"), "{}"); break;
            case "path-escape":
                var current = JsonSerializer.Deserialize<ProjectManifest>(File.ReadAllBytes(path))!;
                File.WriteAllText(w.FilePath("sibling.tasproj"), JsonSerializer.Serialize(current with { Timeline = current.Timeline with { Path = "../outside.json" } })); break;
        }
        var result = RecoveryAssetCleanup.Collect(previous);
        Assert.NotNull(result.SkippedReason); Assert.Equal(0, result.Deleted);
        Assert.All(old, file => Assert.True(File.Exists(file)));
    }

    [Fact]
    public void UnchangedSaveAndNoPriorManifestDeleteNothing()
    {
        using var w = new TestWorkspace(); var path = w.FilePath("recovery.tasproj");
        Assert.Null(RecoveryAssetCleanup.Capture(path)); Assert.Equal(0, RecoveryAssetCleanup.Collect(null).Deleted);
        Save(path, ControllerState.Neutral); var previous = RecoveryAssetCleanup.Capture(path); Save(path, ControllerState.Neutral);
        Assert.Equal(new RecoveryAssetCleanup.Result(0), RecoveryAssetCleanup.Collect(previous));
        Assert.Single(FolderProject.Load(path).Archive.Metadata.Inputs);
    }

    [Fact]
    public void DifferentProjectIdentityDoesNotDeleteAssets()
    {
        using var w = new TestWorkspace(); var path = w.FilePath("recovery.tasproj");
        Save(path, ControllerState.Neutral); var old = JsonAssets(w.DirectoryPath); var previous = RecoveryAssetCleanup.Capture(path);
        Save(path, ControllerState.Neutral, ControllerState.Neutral);
        var manifest = JsonSerializer.Deserialize<ProjectManifest>(File.ReadAllBytes(path))!;
        File.WriteAllText(path, JsonSerializer.Serialize(manifest with { Id = Guid.NewGuid().ToString() }));
        var result = RecoveryAssetCleanup.Collect(previous);
        Assert.NotNull(result.SkippedReason); Assert.Equal(0, result.Deleted); Assert.All(old, file => Assert.True(File.Exists(file)));
    }

    [Fact]
    public void ReparseAssetIsNeverDeletedOrFollowed()
    {
        using var w = new TestWorkspace(); var path = w.FilePath("recovery.tasproj");
        Save(path, ControllerState.Neutral); var old = JsonAssets(w.DirectoryPath); var previous = RecoveryAssetCleanup.Capture(path);
        Save(path, ControllerState.Neutral, ControllerState.Neutral);
        var inputs = w.FilePath("inputs"); var target = w.FilePath("unrelated-inputs");
        Assert.StartsWith(w.DirectoryPath + Path.DirectorySeparatorChar, Path.GetFullPath(inputs));
        Assert.StartsWith(w.DirectoryPath + Path.DirectorySeparatorChar, Path.GetFullPath(target));
        Directory.Move(inputs, target);
        if (OperatingSystem.IsWindows())
        {
            var start = new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { "/c", "mklink", "/J", inputs, target }) start.ArgumentList.Add(arg);
            using var process = System.Diagnostics.Process.Start(start)!; process.WaitForExit();
            Assert.Equal(0, process.ExitCode);
        }
        else Directory.CreateSymbolicLink(inputs, target);
        try
        {
            var result = RecoveryAssetCleanup.Collect(previous);
            Assert.NotNull(result.SkippedReason); Assert.Equal(0, result.Deleted);
            Assert.True(Directory.Exists(target)); Assert.True((File.GetAttributes(inputs) & FileAttributes.ReparsePoint) != 0);
        }
        finally { Directory.Delete(inputs); } // Unlink the test junction itself before workspace disposal.
    }
}
