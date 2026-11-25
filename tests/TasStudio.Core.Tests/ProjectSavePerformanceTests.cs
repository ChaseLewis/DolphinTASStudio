using System.Security.Cryptography;
using System.Text.Json;
using TasStudio.App;
using TasStudio.Core;
using TasStudio.Emulation;
using Xunit;

namespace TasStudio.Core.Tests;

public sealed class ProjectSavePerformanceTests
{
    [Theory]
    [InlineData("write-time")]
    [InlineData("length")]
    [InlineData("creation-time")]
    public void StateChecksumIsReusedOnlyWhileFileMetadataMatches(string change)
    {
        using var files = new TestWorkspace();
        var path = files.FilePath("state.tasstate");
        File.WriteAllBytes(path, [1, 2, 3, 4]);
        var reads = 0;
        var cache = new ProjectStateHashCache(stream => { reads++; return Convert.ToHexString(SHA256.HashData(stream)); });
        var first = cache.HashFile(path);
        Assert.Equal(first, cache.HashFile(path)); Assert.Equal(1, reads);
        var write = File.GetLastWriteTimeUtc(path);
        var created = File.GetCreationTimeUtc(path);
        File.WriteAllBytes(path, change == "length" ? [5, 6, 7, 8, 9] : [5, 6, 7, 8]);
        File.SetLastWriteTimeUtc(path, change == "write-time" ? write.AddSeconds(5) : write);
        if (change == "creation-time") File.SetCreationTimeUtc(path, created.AddSeconds(5));
        Assert.NotEqual(first, cache.HashFile(path)); Assert.Equal(2, reads);
        File.Delete(path);
        Assert.Throws<FileNotFoundException>(() => cache.HashFile(path));
    }

    [Fact]
    public async Task RepeatedSaveReusesStateHashesIncludingValidationAndDetectsChangedDestination()
    {
        using var files = new TestWorkspace(); using var service = new ExecutionService(new FakeBackend());
        await service.LoadGameAsync(files.GamePath, files.Options); await service.NewProjectAsync();
        await service.ConfigureCheckpointsAsync(new(IntervalSeconds: 1));
        for (var i = 0; i < 60; i++) await service.StepAsync();
        var external = files.FilePath("external.tasstate"); await service.SaveStateAsync(external);
        var path = files.FilePath("project/movie.tasproj"); await service.SaveProjectAsync(path);
        var content = FolderProject.Load(path);
        var states = content.States.Select(s => s with { Path = external }).ToArray();
        Assert.Single(states); Assert.Single(content.AutomaticCheckpoints);
        var reads = new List<string>();
        var cache = new ProjectStateHashCache(stream =>
        {
            reads.Add(((FileStream)stream).Name);
            return Convert.ToHexString(SHA256.HashData(stream));
        });
        void Save(string destination) => FolderProject.Save(destination, content.Archive.Metadata, content.Archive.InitialState,
            content.Takes, content.Sections, states, content.AutomaticCheckpoints, cache);
        Save(path); Assert.Contains(external, reads);
        reads.Clear(); Save(path); Assert.Empty(reads);

        // Save As must validate the copied destination as well as the cached source.
        var copy = files.FilePath("copy/movie.tasproj"); Save(copy);
        Assert.Contains(reads, file => file.StartsWith(Path.GetDirectoryName(copy)!, StringComparison.OrdinalIgnoreCase));
        Assert.Single(FolderProject.Load(copy).States);

        var manifestBefore = File.ReadAllBytes(path);
        var manifest = JsonSerializer.Deserialize<ProjectManifest>(manifestBefore)!;
        var state = Path.GetFullPath(manifest.States.Single().Data.Path, Path.GetDirectoryName(path)!);
        var bytes = File.ReadAllBytes(state); var timestamp = File.GetLastWriteTimeUtc(state);
        bytes[0] ^= 0xff; File.WriteAllBytes(state, bytes); File.SetLastWriteTimeUtc(state, timestamp.AddSeconds(5));
        Assert.Throws<InvalidDataException>(() => Save(path));
        Assert.Equal(manifestBefore, File.ReadAllBytes(path));
        Assert.Contains(state, reads);
    }

    [Fact]
    public async Task NormalLoadStillVerifiesStateContentsEvenWhenTimestampsArePreserved()
    {
        using var files = new TestWorkspace(); using var service = new ExecutionService(new FakeBackend());
        await service.LoadGameAsync(files.GamePath, files.Options); await service.NewProjectAsync(); await service.StepAsync();
        await service.SaveNamedStateAsync("Keep");
        var path = files.FilePath("movie.tasproj"); await service.SaveProjectAsync(path); await service.SaveProjectAsync(path);
        var manifest = JsonSerializer.Deserialize<ProjectManifest>(File.ReadAllBytes(path))!;
        var state = Path.GetFullPath(manifest.States.Single().Data.Path, files.DirectoryPath);
        var timestamp = File.GetLastWriteTimeUtc(state); var bytes = File.ReadAllBytes(state);
        bytes[0] ^= 0xff; File.WriteAllBytes(state, bytes); File.SetLastWriteTimeUtc(state, timestamp);
        Assert.Throws<InvalidDataException>(() => FolderProject.Load(path));
    }

    [Fact]
    public async Task SaveReturnsCommittedRecentProjectSummaryWithoutNeedingFilesAgain()
    {
        using var files = new TestWorkspace(); using var service = new ExecutionService(new FakeBackend());
        var path = files.FilePath("movie.tasproj");
        var created = await service.CreateProjectAsync(path, files.GamePath, files.Options);
        Assert.Equal(0, created.InputCount); Assert.Equal(ProjectStartKind.PowerOn, created.Start!.Kind);
        for (var i = 0; i < 5; i++) await service.StepAsync();
        for (var i = 0; i < 5; i++) await service.CaptureTakeAsync("Take " + i, i, 1);
        var saved = await service.SaveProjectAsync(path);
        var expected = RecentProject.From(path, FolderProject.Load(path));
        await service.StepAsync(); // Later session changes cannot alter the committed summary.
        File.Delete(path);
        var actual = RecentProject.From(path, saved);
        Assert.Equal(expected with { OpenedUtc = actual.OpenedUtc, TakeSpans = actual.TakeSpans }, actual);
        Assert.Equal(expected.TakeSpans, actual.TakeSpans);
        Assert.Equal(5, actual.InputCount); Assert.Equal(5, actual.TakeCount); Assert.Equal(4, actual.TakeSpans.Length);
    }
}
