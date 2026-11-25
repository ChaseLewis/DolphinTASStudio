using System.Security.Cryptography;
using System.Text.Json;
using TasStudio.Core;
using TasStudio.Emulation;
using Xunit;

namespace TasStudio.Core.Tests;

public sealed class InputCompressionTests
{
    internal static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    internal static ProjectAsset Asset(string root, string folder, byte[] bytes)
    {
        var relative = folder + "/" + Hash(bytes) + ".json";
        Directory.CreateDirectory(Path.Combine(root, folder)); File.WriteAllBytes(Path.Combine(root, relative), bytes);
        return new(relative, Hash(bytes));
    }
    internal static string LegacyProject(string path)
    {
        var root = Path.GetDirectoryName(path)!; Directory.CreateDirectory(root);
        byte[] state = [1, 2, 3];
        var a = ControllerState.Neutral with { Buttons = PadButtons.A };
        var inputs = Enumerable.Repeat(a, 2048).ToArray();
        RecordedInputFrame[] polls = [new(0, "prefix", new(a, 2, 100, [new(1, 0, 0, a)]))];
        var metadata = new ArchiveMetadata(ProjectArchive.FormatVersion, ProjectArchive.ProjectKind, "core", "game.iso", "game", 1, true,
            (ulong)inputs.Length, 0, Hash(state), 0, 0, [], []);
        var baseline = Path.Combine(root, "states", "initial-" + Hash(state) + ".tasstate");
        ProjectArchive.Save(baseline, metadata with { Kind = ProjectArchive.StateKind, Position = 0 }, new(0, state, null));
        var input = Asset(root, "inputs", JsonSerializer.SerializeToUtf8Bytes(inputs));
        var poll = Asset(root, "inputs", JsonSerializer.SerializeToUtf8Bytes(polls));
        var play = new ExperimentPlay(0, "winner", 10, 0, inputs.Length, new ExperimentPlayData(Hash(state), inputs, [], polls).Encode());
        var playAsset = Asset(root, "inputs", JsonSerializer.SerializeToUtf8Bytes(play));
        var movie = new MovieData([input], [], [new("take", "Take", 0, Hash(state), "test", [input]) { Experiment = playAsset }], []) { PollFrames = [poll] };
        var timeline = Asset(root, "timelines", JsonSerializer.SerializeToUtf8Bytes(movie));
        var manifest = new ProjectManifest(FolderProject.Version, "fixture", metadata,
            new(Path.GetRelativePath(root, baseline), Hash(File.ReadAllBytes(baseline))), timeline, []);
        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(manifest));
        return path;
    }
    private static void Same(FolderProjectContent before, FolderProjectContent after)
    {
        Assert.Equal(before.Archive.InitialState.Data, after.Archive.InitialState.Data);
        Assert.Equal(JsonSerializer.Serialize(before.Archive.Metadata), JsonSerializer.Serialize(after.Archive.Metadata));
        Assert.Equal(JsonSerializer.Serialize(before.Takes), JsonSerializer.Serialize(after.Takes));
    }

    [Fact]
    public void NewProjectsCompressInputsPollsAndTakeMetadataByDefaultAndCheckStoredHash()
    {
        using var files = new TestWorkspace();
        var before = FolderProject.Load(LegacyProject(files.FilePath("old/project.tasproj")));
        var path = files.FilePath("new/project.tasproj"); var root = Path.GetDirectoryName(path)!;
        FolderProject.Save(path, before.Archive.Metadata, before.Archive.InitialState, before.Takes, before.Sections, before.States);
        Same(before, FolderProject.Load(path));
        var assets = Directory.GetFiles(Path.Combine(root, "inputs"));
        Assert.Equal(3, assets.Length); Assert.All(assets, asset => Assert.EndsWith(".json.gz", asset));
        File.AppendAllText(assets[0], "corrupt");
        Assert.Throws<InvalidDataException>(() => FolderProject.Load(path));
    }

    [Fact]
    public void ExistingInputsCompressLosslesslyWithSiblingAndHistoricalTimelines()
    {
        using var files = new TestWorkspace(); var path = LegacyProject(files.FilePath("project/project.tasproj")); var root = Path.GetDirectoryName(path)!;
        File.Copy(path, Path.Combine(root, "sibling.tasproj"));
        var before = FolderProject.Load(path);
        var old = Directory.GetFiles(Path.Combine(root, "inputs")); var size = old.Sum(file => new FileInfo(file).Length);
        var result = ProjectInputCompression.Compress(root);
        Assert.Equal(old.Length, result.Files); Assert.True(result.BytesSaved > size * .8);
        Assert.All(old, file => Assert.False(File.Exists(file)));
        Same(before, FolderProject.Load(path)); Same(before, FolderProject.Load(Path.Combine(root, "sibling.tasproj")));
        Assert.Equal(new InputCompressionResult(0, 0), ProjectInputCompression.Compress(root));
    }

    [Fact]
    public void ThousandsOfInputsRoundTripWithOrderedProgressAndNoEarlyManifestCommit()
    {
        using var files = new TestWorkspace(); var path = LegacyProject(files.FilePath("project/project.tasproj")); var root = Path.GetDirectoryName(path)!;
        for (var i = 0; i < 1024; i++)
        {
            var input = ControllerState.Neutral with { StickX = (byte)i, StickY = (byte)(i >> 8) };
            Asset(root, "inputs", JsonSerializer.SerializeToUtf8Bytes(Enumerable.Repeat(input, 128).ToArray()));
        }
        var before = FolderProject.Load(path); var manifest = File.ReadAllBytes(path);
        var originals = Directory.GetFiles(Path.Combine(root, "inputs"), "*.json");
        var hashes = originals.Select(file => Hash(File.ReadAllBytes(file))).ToHashSet();
        var oldBytes = originals.Sum(file => new FileInfo(file).Length);
        var completed = 0; var inCallback = 0; long reportedSaved = 0;
        var result = ProjectInputCompression.Compress(root, (done, total, saved) =>
        {
            Assert.Equal(1, Interlocked.Increment(ref inCallback));
            try
            {
                Assert.Equal(++completed, done); Assert.Equal(2 * originals.Length, total); reportedSaved = saved;
                if (done <= originals.Length) Assert.Equal(manifest, File.ReadAllBytes(path));
                if (done == originals.Length) Assert.All(originals, file => Assert.True(File.Exists(file)));
            }
            finally { Interlocked.Decrement(ref inCallback); }
        });
        Assert.Equal(2 * originals.Length, completed); Assert.Equal(originals.Length, result.Files);
        Assert.Equal(result.BytesSaved, reportedSaved);
        var compressed = Directory.GetFiles(Path.Combine(root, "inputs"), "*.json.gz");
        Assert.Equal(originals.Length, compressed.Length);
        Assert.Equal(oldBytes - compressed.Sum(file => new FileInfo(file).Length), result.BytesSaved);
        Assert.All(compressed, file =>
        {
            Assert.Equal(Path.GetFileName(file)[..64], Hash(File.ReadAllBytes(file)));
            Assert.True(hashes.Remove(Hash(CompressedJson.ReadBytes(file))));
        });
        Assert.Empty(hashes); Same(before, FolderProject.Load(path));
    }

    [Fact]
    public void ParallelWorkerFailurePreservesOriginalsAndReportsTheUnderlyingError()
    {
        using var files = new TestWorkspace(); var path = LegacyProject(files.FilePath("project/project.tasproj")); var root = Path.GetDirectoryName(path)!;
        var manifest = File.ReadAllBytes(path); var before = FolderProject.Load(path);
        var invalid = Path.Combine(root, "inputs", new string('0', 64) + ".json"); File.WriteAllText(invalid, "[]");
        var originals = Directory.GetFiles(Path.Combine(root, "inputs"), "*.json");
        var error = Assert.Throws<InvalidDataException>(() => ProjectInputCompression.Compress(root));
        Assert.Contains("Input checksum mismatch", error.Message);
        Assert.Equal(manifest, File.ReadAllBytes(path)); Assert.All(originals, file => Assert.True(File.Exists(file)));
        Same(before, FolderProject.Load(path));
        File.Delete(invalid); // All parallel workers have joined, so retry can safely reuse verified gzip files.
        Assert.Equal(3, ProjectInputCompression.Compress(root).Files); Same(before, FolderProject.Load(path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancellationBeforeOrAfterCommitKeepsProjectsLoadableAndConversionCanBeRetried(bool afterCommit)
    {
        using var files = new TestWorkspace(); var path = LegacyProject(files.FilePath("project/project.tasproj")); var root = Path.GetDirectoryName(path)!;
        var original = File.ReadAllBytes(path); var before = FolderProject.Load(path);
        using var cancel = new CancellationTokenSource();
        Assert.Throws<OperationCanceledException>(() => ProjectInputCompression.Compress(root, (done, total, _) =>
        { if (afterCommit ? done > total / 2 : done == 1) cancel.Cancel(); }, cancel.Token));
        if (!afterCommit) Assert.Equal(original, File.ReadAllBytes(path));
        Same(before, FolderProject.Load(path));
        Assert.Equal(afterCommit ? 2 : 3, ProjectInputCompression.Compress(root).Files); Same(before, FolderProject.Load(path));
        foreach (var timeline in Directory.GetFiles(Path.Combine(root, "timelines"), "*.json"))
        {
            var movie = JsonSerializer.Deserialize<MovieData>(File.ReadAllBytes(timeline))!;
            Assert.All(movie.Inputs.Concat(movie.PollFrames).Concat(movie.Takes.SelectMany(take => take.Inputs)),
                asset => Assert.True(File.Exists(Path.GetFullPath(asset.Path, root))));
        }
    }

    [Fact]
    public void InvalidReferenceAndActiveProjectPreventReplacingOriginalInputs()
    {
        using var files = new TestWorkspace(); var path = LegacyProject(files.FilePath("project/project.tasproj")); var root = Path.GetDirectoryName(path)!;
        using (ProjectAssetLease.Acquire(root)) Assert.Throws<IOException>(() => ProjectInputCompression.Compress(root));
        var manifest = JsonSerializer.Deserialize<ProjectManifest>(File.ReadAllBytes(path))!;
        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(manifest with { Timeline = manifest.Timeline with { Sha256 = new string('0', 64) } }));
        var original = File.ReadAllBytes(path); var inputs = Directory.GetFiles(Path.Combine(root, "inputs"));
        Assert.Throws<InvalidDataException>(() => ProjectInputCompression.Compress(root));
        Assert.Equal(original, File.ReadAllBytes(path)); Assert.All(inputs, file => Assert.True(File.Exists(file)));
    }

    [Fact]
    public void ResultReceiptsDefaultToGzipAndReadOldAndNewFormats()
    {
        using var files = new TestWorkspace(); var path = files.FilePath("results.json");
        File.WriteAllText(path, "[1,2]"); Assert.Equal(new[] { 1, 2 }, ExperimentFiles.Read<int[]>(path));
        ExperimentFiles.Write(path, new[] { 3, 4 });
        Assert.False(File.Exists(path)); Assert.True(File.Exists(path + ".gz"));
        Assert.Equal(new[] { 3, 4 }, ExperimentFiles.Read<int[]>(path));
        Assert.Equal(new[] { 3, 4 }, ExperimentFiles.Read<int[]>(path + ".gz"));
        ExperimentFiles.Write(files.FilePath("request.json"), new { Name = "Editable" });
        Assert.Contains("Editable", File.ReadAllText(files.FilePath("request.json")));
    }
}
