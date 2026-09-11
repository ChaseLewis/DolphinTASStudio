using TasStudio.Core;

namespace TasStudio.Emulation;

public sealed record VideoFrame(int Width, int Height, byte[] Rgba, long Sequence);
public sealed record BackendOptions(string CorePath, string SystemDirectory, string SaveDirectory,
    int InternalResolution = 1, bool DspHle = true, EmulationConfiguration? Configuration = null, string? StorageRoot = null);
/// <summary>Studio's requested settings, not a readback of Dolphin's layered effective config.</summary>
public sealed record SessionConfiguration(BackendOptions Options, string BackendIdentity);
public sealed record EmulatorSnapshot(ulong Position, byte[] Data, VideoFrame? Preview = null);
public sealed record GameCubeMovieCard(string FileName, byte[] Bytes);
public sealed record GameCubeMovieBoot(GameCubeMovieCard[] Cards, InputPollFrame Inputs);

/// <summary>Recover original cards and measured startup inputs, verifying boot timing/RAM and restoring the paused session.</summary>
public interface IGameCubeMovieBootBackend
{
    GameCubeMovieBoot ExportMovieBoot(EmulatorSnapshot initial, string gamePath, BackendOptions options);
}

/// <summary>All members are invoked exclusively on the execution service's owner thread.</summary>
public interface IEmulatorBackend : IDisposable
{
    string Identity { get; }
    string? ConfigurationIdentity => null;
    double EmulatedSeconds => Position / FramesPerSecond;
    /// <summary>Inspects the requested build without loading native code or modifying the current session.</summary>
    string InspectIdentity(BackendOptions options) => Identity;
    bool IsLoaded { get; }
    ulong Position { get; }
    /// <summary>Emulated video fields since boot, restored with the emulator state; distinct from timeline inputs.</summary>
    ulong VideoFieldCount => Position;
    /// <summary>Exact CoreTiming ticks since boot, when supported by the backend.</summary>
    ulong? EmulatedTicks => null;
    InputPollFrame? LastInputPollFrame => null;
    double FramesPerSecond { get; }
    event Action<VideoFrame>? VideoReady;
    event Action<short[], int>? AudioReady;
    void LoadGame(string path, BackendOptions options);
    void Stop();
    /// <summary>Hold input until the next nonduplicate presentation and its field boundary. Position advances by one.</summary>
    void Step(ControllerState input);
    /// <summary>Consume recorded polls and reject count/order/timing divergence.</summary>
    void ReplayInputPollFrame(InputPollFrame frame) => throw new NotSupportedException("Backend does not support poll playback.");
    void Reset();
    EmulatorSnapshot Capture();
    void Restore(EmulatorSnapshot snapshot);
    byte[] ReadMemory(uint address, int count);
    void WriteMemory(uint address, byte[] bytes);
}
