using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using TasStudio.Emulation;
using Xunit;

namespace TasStudio.Core.Tests;

public sealed class ProjectArchiveTests
{
    private static readonly byte[] StateBytes = [1, 2, 3, 4, 5];
    private static ArchiveMetadata Metadata => new(ProjectArchive.FormatVersion, ProjectArchive.ProjectKind, "fake-core", "test.iso",
        "game-hash", 1, true, 1, 0, Convert.ToHexString(SHA256.HashData(StateBytes)), 1, 1,
        [ControllerState.Neutral with { Buttons = PadButtons.L, TriggerL = 23, StickX = 17 }],
        [new ExecutionEvent(0, ExecutionEventKind.MemoryWrite, 0x80000000, [7, 9])]);
    private static EmulatorSnapshot State => new(0, StateBytes, new VideoFrame(1, 1, [23, 42, 81, 255], 9));

    [Fact]
    public void RoundTripPreservesExactAnalogValuesIndependentOfDigitalClicks()
    {
        using var workspace = new TestWorkspace();
        var path = workspace.FilePath("roundtrip.tasproj");
        ProjectArchive.Save(path, Metadata, State);
        var loaded = ProjectArchive.Load(path, ProjectArchive.ProjectKind);
        Assert.Equal(Metadata.Inputs, loaded.Metadata.Inputs);
        Assert.Equal(Metadata.Events[0].Bytes, loaded.Metadata.Events[0].Bytes);
        Assert.Equal(StateBytes, loaded.InitialState.Data);
        Assert.Equal(State.Preview!.Rgba, loaded.InitialState.Preview!.Rgba);
    }

    [Fact]
    public void ChecksumFailureCannotReplaceAKnownGoodSave()
    {
        using var workspace = new TestWorkspace();
        var path = workspace.FilePath("atomic.tasproj");
        ProjectArchive.Save(path, Metadata, State);
        var original = File.ReadAllBytes(path);
        Assert.Throws<InvalidDataException>(() => ProjectArchive.Save(path, Metadata with { StateHash = "wrong" }, State));
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Empty(Directory.GetFiles(workspace.DirectoryPath, "*.tmp"));
    }

    [Theory]
    [InlineData("version")]
    [InlineData("position")]
    [InlineData("buttons")]
    [InlineData("preview")]
    [InlineData("events")]
    [InlineData("resolution")]
    public void InvalidMetadataIsRejected(string invalidField)
    {
        using var workspace = new TestWorkspace();
        var path = workspace.FilePath("invalid.tasproj");
        var bad = invalidField switch
        {
            "version" => Metadata with { Version = ProjectArchive.FormatVersion + 1 },
            "position" => Metadata with { Position = 2 },
            "buttons" => Metadata with { Inputs = [ControllerState.Neutral with { Buttons = (PadButtons)0x8000 }] },
            "preview" => Metadata with { PreviewWidth = -1 },
            "events" => Metadata with { Events = [new ExecutionEvent(0, ExecutionEventKind.MemoryWrite)] },
            "resolution" => Metadata with { InternalResolution = 0 },
            _ => throw new ArgumentException(nameof(invalidField))
        };
        WriteArchive(path, bad);
        Assert.Throws<InvalidDataException>(() => ProjectArchive.Load(path, ProjectArchive.ProjectKind));
    }

    [Fact]
    public void ModifiedStateBytesAndDuplicateEntriesAreRejected()
    {
        using var workspace = new TestWorkspace();
        var path = workspace.FilePath("corrupt.tasproj");
        WriteArchive(path, Metadata, [9, 9, 9]);
        Assert.Throws<InvalidDataException>(() => ProjectArchive.Load(path, ProjectArchive.ProjectKind));
        WriteArchive(path, Metadata, duplicateState: true);
        Assert.Throws<InvalidDataException>(() => ProjectArchive.Load(path, ProjectArchive.ProjectKind));
    }

    [Fact]
    public void StateCannotCarryAnUnrelatedInputHistory()
    {
        using var workspace = new TestWorkspace();
        var path = workspace.FilePath("ambiguous.tasstate");
        WriteArchive(path, Metadata with { Kind = ProjectArchive.StateKind, InitialPosition = 1 });
        Assert.Throws<InvalidDataException>(() => ProjectArchive.Load(path, ProjectArchive.StateKind));
    }

    private static void WriteArchive(string path, ArchiveMetadata metadata, byte[]? state = null, bool duplicateState = false)
    {
        using var file = File.Create(path);
        using var archive = new ZipArchive(file, ZipArchiveMode.Create);
        using (var stream = archive.CreateEntry("metadata.json").Open()) JsonSerializer.Serialize(stream, metadata);
        using (var stream = archive.CreateEntry("state.bin").Open()) stream.Write(state ?? StateBytes);
        using (var stream = archive.CreateEntry("preview.rgba").Open()) stream.Write(State.Preview!.Rgba);
        if (duplicateState) using (var stream = archive.CreateEntry("state.bin").Open()) stream.Write(StateBytes);
    }
}
