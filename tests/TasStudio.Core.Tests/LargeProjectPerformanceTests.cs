using System.Security.Cryptography;
using System.Text.Json;
using TasStudio.Core;
using TasStudio.Emulation;
using Xunit;

namespace TasStudio.Core.Tests;

/// <summary>Work that must not grow with the size of the author's project. Counts work done, never wall time.</summary>
public sealed class LargeProjectPerformanceTests
{
    private static readonly ControllerState A = ControllerState.Neutral with { Buttons = PadButtons.A };

    private static async Task<(string Project, StateMarker[] States)> ProjectWithStates(TestWorkspace files, ExecutionService execution, int states)
    {
        await execution.LoadGameAsync(files.GamePath, files.Options);
        await execution.NewProjectAsync();
        for (var i = 0; i < states; i++) { await execution.StepAsync(); await execution.SaveNamedStateAsync("State " + i); }
        var project = Path.GetFullPath(files.FilePath("My TAS/movie.tasproj"));
        await execution.SaveProjectAsync(project);
        return (project, execution.StateMarkers.Where(s => !s.Automatic).OrderBy(s => s.Position).ToArray());
    }

    private static Dictionary<string, (long Length, DateTime Written, string Hash)> Fingerprint(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(file => !file.Contains(Path.DirectorySeparatorChar + ".runs" + Path.DirectorySeparatorChar))
            .ToDictionary(file => file, file => (new FileInfo(file).Length, File.GetLastWriteTimeUtc(file),
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)))), StringComparer.OrdinalIgnoreCase);

    [Fact]
    public async Task RunSnapshotCopiesOnlyItsStartStateAndNeverChangesTheProject()
    {
        using var files = new TestWorkspace(); using var execution = new ExecutionService(new FakeBackend());
        var (project, states) = await ProjectWithStates(files, execution, 3);
        var config = ProjectExperiments.Create(project, states[1].Id, "Search");
        var root = Path.GetDirectoryName(project)!;
        var before = Fingerprint(root);

        var run = ProjectExperiments.PrepareRun(config, project, states[1].Id);

        var snapshot = Path.Combine(run.Directory, "source", "project.tasproj");
        var content = FolderProject.Load(snapshot);
        Assert.Equal(states[1].Id, Assert.Single(content.States).Id);
        Assert.Empty(content.AutomaticCheckpoints);
        Assert.Equal(FolderProject.Load(project).Archive.Metadata.Inputs, content.Archive.Metadata.Inputs);
        // The baseline plus the one start state; the project's other states are not copied.
        Assert.Equal(2, Directory.GetFiles(Path.Combine(run.Directory, "source", "states")).Length);
        Assert.Equal(3, FolderProject.Load(project).States.Length);
        Assert.Equal(before, Fingerprint(root));

        // The copy is self-contained: a trial can restore its state with the author's assets gone.
        foreach (var folder in new[] { "states", "inputs", "timelines" }) Directory.Move(Path.Combine(root, folder), Path.Combine(root, folder + ".moved"));
        File.Move(project, project + ".moved");
        using var worker = new ExecutionService(new FakeBackend());
        await worker.LoadProjectAsync(snapshot, files.Options, positionOverride: 0, isolatedMemoryCard: true);
        await worker.LoadMarkerAsync(states[1].Id);
        Assert.Equal(states[1].Position, worker.Position);
    }

    [Fact]
    public async Task LoadingForOneStateDoesNotReadTheOthers()
    {
        using var files = new TestWorkspace(); using var execution = new ExecutionService(new FakeBackend());
        var (project, states) = await ProjectWithStates(files, execution, 3);
        var manifest = JsonSerializer.Deserialize<ProjectManifest>(File.ReadAllBytes(project))!;
        var other = Path.GetFullPath(manifest.States.Single(s => s.Id == states[2].Id).Data.Path, Path.GetDirectoryName(project)!);
        File.Delete(other);

        Assert.ThrowsAny<IOException>(() => FolderProject.Load(project));
        Assert.Equal(states[0].Id, Assert.Single(FolderProject.LoadForState(project, states[0].Id).States).Id);
        Assert.Empty(FolderProject.LoadForState(project, null).States);
        Assert.Equal(FolderProject.LoadForState(project, null).Archive.Metadata.Configuration?.Fingerprint,
            FolderProject.LoadEnvironment(project).Configuration?.Fingerprint);
    }

    [Fact]
    public async Task OpeningReusesRememberedStateChecksumsAndRevalidationRereadsEverything()
    {
        using var files = new TestWorkspace(); using var execution = new ExecutionService(new FakeBackend());
        var (project, states) = await ProjectWithStates(files, execution, 3);
        var store = files.FilePath("app-data/file-hashes.json");

        var first = new KnownFileHashes(store);
        Assert.Equal(3, FolderProject.Load(project, first).States.Length);
        Assert.Equal(4, first.Reads); // The baseline and three states.

        // A later session rereads nothing while sizes and timestamps match.
        var second = new KnownFileHashes(store);
        Assert.Equal(3, FolderProject.Load(project, second).States.Length);
        Assert.Equal(0, second.Reads);

        var manifest = JsonSerializer.Deserialize<ProjectManifest>(File.ReadAllBytes(project))!;
        var state = Path.GetFullPath(manifest.States.Single(s => s.Id == states[1].Id).Data.Path, Path.GetDirectoryName(project)!);
        var written = File.GetLastWriteTimeUtc(state); var bytes = File.ReadAllBytes(state);

        // A changed timestamp alone is enough to reread that one file.
        File.SetLastWriteTimeUtc(state, written.AddSeconds(5));
        FolderProject.Load(project, second); Assert.Equal(1, second.Reads);

        // Damage hidden behind unchanged stamps is what revalidation exists to find.
        var stamp = File.GetLastWriteTimeUtc(state);
        bytes[0] ^= 0xff; File.WriteAllBytes(state, bytes); File.SetLastWriteTimeUtc(state, stamp);
        FolderProject.Load(project, second);
        Assert.Throws<InvalidDataException>(() => FolderProject.Load(project));
        Assert.Throws<InvalidDataException>(() => FolderProject.Load(project, second, revalidate: true));
        // It rereads files regardless of their stamps, stopping at the first one that is damaged.
        Assert.InRange(second.Reads, 1 + 2, 1 + 4);
        // Revalidation corrected the record, so ordinary opens now report the damage too.
        Assert.Throws<InvalidDataException>(() => FolderProject.Load(project, new KnownFileHashes(store)));
    }

    [Fact]
    public void AnUnreadableChecksumStoreOnlyCostsARead()
    {
        using var files = new TestWorkspace();
        var store = files.FilePath("file-hashes.json"); File.WriteAllText(store, "{ not json");
        var file = files.FilePath("state.bin"); File.WriteAllBytes(file, [1, 2, 3]);
        var known = new KnownFileHashes(store);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(new byte[] { 1, 2, 3 })), known.HashFile(file));
        known.Save();
        var reopened = new KnownFileHashes(store);
        reopened.HashFile(file); Assert.Equal(0, reopened.Reads);
    }

    [Fact]
    public async Task ResavingAfterASmallEditSerializesOnlyTheChangedChunks()
    {
        using var files = new TestWorkspace(); using var execution = new ExecutionService(new FakeBackend());
        await execution.LoadGameAsync(files.GamePath, files.Options); await execution.NewProjectAsync();
        var groups = FolderProject.FramesPerChunk * 2 + 10;
        for (var i = 0; i < groups; i++) await execution.StepAsync();
        var project = files.FilePath("project/movie.tasproj");

        await execution.SaveProjectAsync(project);
        // Identical chunks share one asset, so only distinct content is counted.
        Assert.True(execution.ProjectChunks.Serialized >= 1);
        Assert.Equal(0, execution.ProjectChunks.Parsed);
        var assets = Directory.GetFiles(files.FilePath("project/inputs")).Length;

        await execution.SaveProjectAsync(project);
        Assert.Equal(0, execution.ProjectChunks.Serialized);
        Assert.Equal(0, execution.ProjectChunks.Parsed);

        await execution.SetInputAsync(FolderProject.FramesPerChunk + 5, A);
        await execution.SaveProjectAsync(project);
        // One input chunk and at most one poll chunk hold the edited group.
        Assert.InRange(execution.ProjectChunks.Serialized, 1, 2);
        Assert.Equal(0, execution.ProjectChunks.Parsed);
        Assert.InRange(Directory.GetFiles(files.FilePath("project/inputs")).Length - assets, 1, 2);

        // A save that reuses chunks commits the same movie as one that serializes everything.
        var saved = FolderProject.Load(project);
        Assert.Equal(A, saved.Archive.Metadata.Inputs[FolderProject.FramesPerChunk + 5]);
        var copy = files.FilePath("copy/movie.tasproj");
        FolderProject.Save(copy, saved.Archive.Metadata, saved.Archive.InitialState, saved.Takes, saved.Sections, saved.States, saved.AutomaticCheckpoints);
        static string Timeline(string path) => JsonSerializer.Deserialize<ProjectManifest>(File.ReadAllBytes(path))!.Timeline.Sha256;
        Assert.Equal(Timeline(copy), Timeline(project));
    }

    [Fact]
    public async Task ResaveDetectsAChunkRemovedFromDisk()
    {
        using var files = new TestWorkspace(); using var execution = new ExecutionService(new FakeBackend());
        await execution.LoadGameAsync(files.GamePath, files.Options); await execution.NewProjectAsync();
        for (var i = 0; i < 20; i++) await execution.StepAsync();
        var project = files.FilePath("project/movie.tasproj");
        await execution.SaveProjectAsync(project);
        foreach (var file in Directory.GetFiles(files.FilePath("project/inputs"))) File.Delete(file);

        await execution.SaveProjectAsync(project);

        Assert.True(execution.ProjectChunks.Serialized >= 1);
        Assert.Equal(20, FolderProject.Load(project).Archive.Metadata.Inputs.Length);
    }

    [Fact]
    public void RememberedHistoryMatchesAFreshChainAfterEveryKindOfChange()
    {
        var inputs = Enumerable.Range(0, 200).Select(i => i % 3 == 0 ? A : ControllerState.Neutral).ToList();
        var events = new List<ExecutionEvent> { new(5, ExecutionEventKind.MemoryWrite, 0x80000000, [1, 2]) };
        var polls = new Dictionary<int, RecordedInputFrame>();
        var history = new ExecutionHistory("root", inputs, events, polls);
        void Check()
        {
            var fresh = new ExecutionHistory("root", inputs, events, polls);
            for (var twice = 0; twice < 2; twice++)
                for (ulong position = 0; position <= (ulong)inputs.Count; position++)
                    Assert.Equal(fresh.At(position), history.At(position));
        }
        Check();
        inputs[50] = A with { StickX = 1 }; history.Invalidate(50); Check();
        // An event changes its own boundary at once, even before the service invalidates the chain after it.
        events.Add(new(120, ExecutionEventKind.MemoryWrite, 0x80000004, [9]));
        Assert.Equal(new ExecutionHistory("root", inputs, events, polls).At(120), history.At(120));
        history.Invalidate(120); Check();
        events.Add(new(5, ExecutionEventKind.Reset, 0, null)); history.Invalidate(5); Check();
        events.RemoveAt(0); events.Add(new(7, ExecutionEventKind.Reset, 0, null)); history.Invalidate(5); Check();
        polls[10] = new(10, "", new(inputs[10], 1, 1, [new(0, 0, 0, inputs[10])])); history.Invalidate(10); Check();
        inputs.RemoveRange(150, 50); history.Invalidate(150); Check();
        inputs.Add(A); Check();
    }
}
