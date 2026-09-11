using System.Collections.Concurrent;
using TasStudio.Core;
using TasStudio.Emulation;

namespace TasStudio.Core.Tests;

// Deliberately stateful: replay must actually restore and execute the intended inputs.
internal class FakeBackend : IEmulatorBackend
{
    private const int SnapshotBytes = sizeof(ulong) + sizeof(int);
    private int _accumulator;
    public string Identity { get; set; } = "fake-core/1";
    public string? ConfigurationIdentity { get; set; }
    public bool IsLoaded { get; private set; }
    public ulong Position { get; private set; }
    public ulong FieldsPerStep { get; set; } = 1;
    public ulong VideoFieldCount => Position * FieldsPerStep;
    public ulong? EmulatedTicks => VideoFieldCount * 1000;
    public bool RecordPolls { get; set; }
    public int PollsPerStep { get; set; } = 4;
    public InputPollFrame? LastInputPollFrame { get; private set; }
    public double FramesPerSecond => 60;
    public double EmulatedSeconds => VideoFieldCount / FramesPerSecond;
    public ConcurrentQueue<(string Operation, int Thread)> Calls { get; } = new();
    public ConcurrentQueue<ControllerState> SubmittedInputs { get; } = new();
    public bool FailNextStep { get; set; }
    public bool FailNextRestore { get; set; }
    public bool FailNextLoad { get; set; }
    public BackendOptions? LastLoadOptions { get; private set; }
    public int SnapshotPaddingBytes { get; set; }
    public Action? BeforeStep { get; set; }
    public event Action<VideoFrame>? VideoReady;
    public event Action<short[], int>? AudioReady;
    private void Record(string operation) => Calls.Enqueue((operation, Environment.CurrentManagedThreadId));
    public void LoadGame(string path, BackendOptions options)
    {
        Record(nameof(LoadGame));
        LastLoadOptions = options;
        if (FailNextLoad) { FailNextLoad = false; throw new InvalidOperationException("Injected load failure"); }
        IsLoaded = true; Position = 0; _accumulator = 0;
    }
    public void Stop() { Record(nameof(Stop)); IsLoaded = false; Position = 0; }
    public void Step(ControllerState input)
    {
        Record(nameof(Step));
        BeforeStep?.Invoke();
        if (FailNextStep) { FailNextStep = false; throw new InvalidOperationException("Injected step failure"); }
        SubmittedInputs.Enqueue(input);
        if (RecordPolls)
        {
            var ticks = FieldsPerStep * 1000;
            LastInputPollFrame = new(input, FieldsPerStep, ticks, Enumerable.Range(0, PollsPerStep)
                .Select(i => new InputPoll((ulong)i * ticks / (ulong)PollsPerStep, (ulong)i * FieldsPerStep / (ulong)PollsPerStep, 0, input)).ToArray());
            foreach (var poll in LastInputPollFrame.Polls) Accumulate(poll.Input);
        }
        else Accumulate(input);
        Position++;
        VideoReady?.Invoke(new VideoFrame(1, 1, [0, 0, 0, byte.MaxValue], (long)Position));
        AudioReady?.Invoke([], 0);
    }
    private void Accumulate(ControllerState input) => _accumulator = unchecked(_accumulator * 31 + (int)input.Buttons + input.StickX + input.TriggerL * 7);
    public void ReplayInputPollFrame(InputPollFrame frame)
    {
        Record(nameof(ReplayInputPollFrame)); frame.Validate();
        if (frame.Polls.Length != PollsPerStep || frame.Fields != FieldsPerStep || frame.Ticks != FieldsPerStep * 1000 ||
            frame.Polls.Where((p, i) => p.TickOffset != (ulong)i * frame.Ticks / (ulong)PollsPerStep || p.FieldOffset != (ulong)i * FieldsPerStep / (ulong)PollsPerStep).Any())
            throw new InvalidDataException("Playback desync: poll timing or count changed.");
        var before = _accumulator;
        Step(frame.Input);
        _accumulator = before;
        foreach (var poll in frame.Polls) Accumulate(poll.Input);
        LastInputPollFrame = frame.Copy();
    }
    public void Reset() { Record(nameof(Reset)); _accumulator = 0; }
    public EmulatorSnapshot Capture()
    {
        Record(nameof(Capture));
        var bytes = new byte[SnapshotBytes + SnapshotPaddingBytes];
        // Incompressible payload lets storage-budget tests exercise real archive bytes.
        if (SnapshotPaddingBytes > 0) new Random(17).NextBytes(bytes);
        BitConverter.TryWriteBytes(bytes.AsSpan(), Position);
        BitConverter.TryWriteBytes(bytes.AsSpan(sizeof(ulong)), _accumulator);
        return new EmulatorSnapshot(Position, bytes);
    }
    public void Restore(EmulatorSnapshot snapshot)
    {
        Record(nameof(Restore));
        if (FailNextRestore) { FailNextRestore = false; throw new InvalidDataException("Injected restore failure"); }
        if (snapshot.Data.Length < SnapshotBytes) throw new InvalidDataException("Bad fake snapshot");
        Position = snapshot.Position;
        _accumulator = BitConverter.ToInt32(snapshot.Data, sizeof(ulong));
    }
    public virtual byte[] ReadMemory(uint address, int count)
    {
        Record(nameof(ReadMemory));
        if (count != sizeof(int)) throw new ArgumentOutOfRangeException(nameof(count));
        return BitConverter.GetBytes(_accumulator);
    }
    public void WriteMemory(uint address, byte[] bytes) { Record(nameof(WriteMemory)); _accumulator = BitConverter.ToInt32(bytes); }
    public void Dispose() { Record(nameof(Dispose)); IsLoaded = false; }
}

internal sealed class TestWorkspace : IDisposable
{
    public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "TasStudio.Tests", Guid.NewGuid().ToString("N"));
    public string GamePath => Path.Combine(DirectoryPath, "test.iso");
    public BackendOptions Options => new(Path.Combine(DirectoryPath, "core.dll"), Path.Combine(DirectoryPath, "system"), Path.Combine(DirectoryPath, "saves"));
    public TestWorkspace()
    {
        Directory.CreateDirectory(DirectoryPath);
        File.WriteAllText(GamePath, "Known fake game bytes");
        File.WriteAllText(Options.CorePath, "Known fake core bytes");
        Directory.CreateDirectory(Options.SystemDirectory);
        Directory.CreateDirectory(Options.SaveDirectory);
    }
    public string FilePath(string name) => Path.Combine(DirectoryPath, name);
    public void Dispose() => Directory.Delete(DirectoryPath, recursive: true);
}
