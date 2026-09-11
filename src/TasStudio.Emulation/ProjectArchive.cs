using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using TasStudio.Core;

namespace TasStudio.Emulation;

public enum ExecutionEventKind { MemoryWrite, Reset }
public enum ProjectStartKind { CapturedState, PowerOn, SaveState }
public sealed record ProjectStart(ProjectStartKind Kind, string? SourceName = null, ulong SourcePosition = 0);
public sealed record EmulatorRuntime(string BackendIdentity, string? ConfigurationIdentity);
public sealed record ExecutionEvent(ulong Position, ExecutionEventKind Kind, uint Address = 0, byte[]? Bytes = null);
public sealed record ArchiveMetadata(int Version, string Kind, string BackendIdentity, string GamePath,
    string GameHash, int InternalResolution, bool DspHle, ulong Position, ulong InitialPosition,
    string StateHash, int PreviewWidth, int PreviewHeight,
    ControllerState[] Inputs, ExecutionEvent[] Events)
{
    public string? HistoryHash { get; init; }
    public EmulationConfiguration? Configuration { get; init; }
    public string? ConfigurationIdentity { get; init; }
    // Provenance, not a claim that recordings or experiment results were reverified.
    public EmulatorRuntime[] RuntimeHistory { get; init; } = [];
    public CheckpointPolicy? Checkpoints { get; init; }
    public ProjectStart? Start { get; init; }
    public TimelineTag[] Tags { get; init; } = [];
    public RecordedInputFrame[] PollFrames { get; init; } = [];
}
public sealed record ArchiveContent(ArchiveMetadata Metadata, EmulatorSnapshot InitialState);

public static class ProjectArchive
{
    public const int FormatVersion = 2;
    public const string ProjectKind = "tas-project";
    public const string StateKind = "tas-state";
    private const string MetadataEntry = "metadata.json";
    private const string StateEntry = "state.bin";
    private const string PreviewEntry = "preview.rgba";
    private const int MaximumMetadataBytes = 64 * 1024 * 1024;
    private const int MaximumStateBytes = 256 * 1024 * 1024;
    private const int MaximumPreviewDimension = 4096;
    private const int BytesPerPixel = 4;
    private const int MaximumPreviewBytes = MaximumPreviewDimension * MaximumPreviewDimension * BytesPerPixel;
    private const int MinimumInternalResolution = 1;
    private const int MaximumInternalResolution = 4;
    private const PadButtons KnownButtons = PadButtons.Left | PadButtons.Right | PadButtons.Down | PadButtons.Up |
        PadButtons.Z | PadButtons.R | PadButtons.L | PadButtons.A | PadButtons.B | PadButtons.X | PadButtons.Y | PadButtons.Start;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    public static void Save(string path, ArchiveMetadata metadata, EmulatorSnapshot initial)
    {
        ValidateMetadata(metadata, metadata.Kind);
        if (initial.Data.Length is 0 or > MaximumStateBytes || initial.Position != metadata.InitialPosition)
            throw new InvalidDataException("Invalid initial state.");
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                using (var zip = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true))
                {
                    using (var entry = zip.CreateEntry(MetadataEntry).Open()) JsonSerializer.Serialize(entry, metadata, JsonOptions);
                    using (var entry = zip.CreateEntry(StateEntry, CompressionLevel.Fastest).Open()) entry.Write(initial.Data);
                    if (initial.Preview is { } preview)
                        using (var entry = zip.CreateEntry(PreviewEntry, CompressionLevel.Fastest).Open()) entry.Write(preview.Rgba);
                }
                file.Flush(flushToDisk: true);
            }
            // Validate the completed container before it can replace a known-good save.
            Load(temporary, metadata.Kind);
            File.Move(temporary, fullPath, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static ArchiveContent Load(string path, string expectedKind)
    {
        using var zip = ZipFile.OpenRead(path);
        var metadata = JsonSerializer.Deserialize<ArchiveMetadata>(ReadEntry(zip, MetadataEntry, MaximumMetadataBytes), JsonOptions)
            ?? throw new InvalidDataException("Missing state metadata.");
        ValidateMetadata(metadata, expectedKind);
        var state = ReadEntry(zip, StateEntry, MaximumStateBytes);
        if (!Convert.ToHexString(SHA256.HashData(state)).Equals(metadata.StateHash, StringComparison.Ordinal))
            throw new InvalidDataException("Savestate checksum mismatch.");
        VideoFrame? preview = null;
        if (metadata.PreviewWidth > 0 || metadata.PreviewHeight > 0)
        {
            if (metadata.PreviewWidth is <= 0 or > MaximumPreviewDimension || metadata.PreviewHeight is <= 0 or > MaximumPreviewDimension)
                throw new InvalidDataException("Invalid preview dimensions.");
            var pixels = ReadEntry(zip, PreviewEntry, MaximumPreviewBytes);
            if (pixels.Length != checked(metadata.PreviewWidth * metadata.PreviewHeight * BytesPerPixel))
                throw new InvalidDataException("Invalid preview size.");
            preview = new VideoFrame(metadata.PreviewWidth, metadata.PreviewHeight, pixels, 0);
        }
        return new ArchiveContent(metadata, new EmulatorSnapshot(metadata.InitialPosition, state, preview));
    }

    internal static void ValidateMetadata(ArchiveMetadata metadata, string expectedKind)
    {
        if (metadata.RuntimeHistory == null || metadata.RuntimeHistory.Length > 10000 ||
            metadata.RuntimeHistory.Any(runtime => runtime == null || string.IsNullOrWhiteSpace(runtime.BackendIdentity)))
            throw new InvalidDataException("Invalid emulator runtime history.");
        if (metadata.PollFrames == null || metadata.PollFrames.Length > FolderProject.MaximumFrames)
            throw new InvalidDataException("Invalid poll frame collection.");
        var previous = -1;
        foreach (var record in metadata.PollFrames)
        {
            if (record == null || record.Frame == null || record.PrefixHash == null || record.Index <= previous ||
                metadata.Inputs == null || record.Index >= metadata.Inputs.Length || record.Frame.Input != metadata.Inputs[record.Index])
                throw new InvalidDataException("Invalid poll frame mapping.");
            record.Frame.Validate(); previous = record.Index;
        }
        if (metadata.Start is { } start && !Enum.IsDefined(start.Kind)) throw new InvalidDataException("Unknown project starting point.");
        metadata.Checkpoints?.Validate();
        if (metadata.Configuration is { } configuration)
        {
            configuration.ValidatedCopy();
            if (configuration.Resolution != metadata.InternalResolution || configuration.DspHle != metadata.DspHle)
                throw new InvalidDataException("Conflicting project settings.");
        }
        if (expectedKind is not (ProjectKind or StateKind) || metadata.Version is not (1 or FormatVersion) || metadata.Kind != expectedKind)
            throw new InvalidDataException("Unsupported TAS Studio file type or version.");
        if (metadata.Inputs is null || metadata.Events is null || string.IsNullOrWhiteSpace(metadata.BackendIdentity)
            || string.IsNullOrWhiteSpace(metadata.GameHash) || string.IsNullOrWhiteSpace(metadata.GamePath)
            || string.IsNullOrWhiteSpace(metadata.StateHash))
            throw new InvalidDataException("Incomplete project metadata.");
        if (metadata.InternalResolution is < MinimumInternalResolution or > MaximumInternalResolution)
            throw new InvalidDataException("Unsupported internal resolution.");
        if (metadata.Tags == null || metadata.Tags.Length > 10000 ||
            metadata.Tags.Any(tag => tag == null || string.IsNullOrWhiteSpace(tag.Id) || tag.Position > int.MaxValue ||
                string.IsNullOrWhiteSpace(tag.Name) || tag.Name.Length > 40 || tag.Name.Any(char.IsControl)) ||
            metadata.Tags.Select(tag => tag.Id).Distinct().Count() != metadata.Tags.Length)
            throw new InvalidDataException("Invalid timeline tags.");
        if (metadata.Position > long.MaxValue || metadata.InitialPosition > long.MaxValue ||
            (expectedKind == ProjectKind && (metadata.Position > (ulong)metadata.Inputs.Length || metadata.InitialPosition != 0)) ||
            (expectedKind == StateKind && (metadata.InitialPosition != metadata.Position || metadata.Inputs.Length != 0 || metadata.Events.Length != 0)))
            throw new InvalidDataException("Invalid archive position or timeline.");
        if (metadata.PreviewWidth < 0 || metadata.PreviewHeight < 0 ||
            metadata.PreviewWidth > MaximumPreviewDimension || metadata.PreviewHeight > MaximumPreviewDimension ||
            (metadata.PreviewWidth == 0) != (metadata.PreviewHeight == 0))
            throw new InvalidDataException("Invalid preview dimensions.");
        if (metadata.Inputs.Any(input => (input.Buttons & ~KnownButtons) != PadButtons.None))
            throw new InvalidDataException("Unknown controller button bits.");
        if (metadata.Events.Any(e => e is null || e.Position > (ulong)metadata.Inputs.Length ||
            !Enum.IsDefined(e.Kind) || (e.Kind == ExecutionEventKind.MemoryWrite && (e.Bytes is null || e.Bytes.Length == 0 || e.Bytes.Length > MaximumStateBytes))))
            throw new InvalidDataException("Invalid execution event.");
    }

    private static byte[] ReadEntry(ZipArchive zip, string name, int maximum)
    {
        var entries = zip.Entries.Where(e => e.FullName == name).ToArray();
        if (entries.Length != 1 || entries[0].Length is <= 0 || entries[0].Length > maximum)
            throw new InvalidDataException("Missing, duplicate, or oversized archive entry: " + name);
        var bytes = new byte[(int)entries[0].Length];
        using var stream = entries[0].Open();
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1) throw new InvalidDataException("Archive entry exceeded its declared size.");
        return bytes;
    }
}
