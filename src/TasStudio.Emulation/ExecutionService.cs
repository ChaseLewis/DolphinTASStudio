using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using TasStudio.Core;

namespace TasStudio.Emulation;

/// <summary>Serializes all native work on one persistent thread. No UI thread calls the core.</summary>
public sealed partial class ExecutionService : IDisposable
{
    private const int IdleWaitMilliseconds = 100;
    private const double MinimumFramesPerSecond = 1;
    private const double MaximumFramesPerSecond = 240;
    private readonly IEmulatorBackend _backend;
    private readonly BlockingCollection<Action> _commands = new();
    private readonly Thread _thread;
    private readonly List<ControllerState> _inputs = [];
    private readonly List<ExecutionEvent> _events = [];
    private EmulatorSnapshot? _initial;
    private ProjectStart? _projectStart;
    private BackendOptions? _options;
    private string _gameHash = "";
    private volatile bool _running, _loaded, _hasProject;
    private volatile bool _playbackOnly;
    public bool IsRecordingLive => _running && !_playbackOnly;
    private int _disposed;
    private long _position;
    private long _videoFieldCount;
    private double _elapsedSeconds;
    private long _seekInterruptVersion;
    private ControllerState[] _publishedInputs = [];
    public bool IsLoaded => _loaded;
    public bool IsRunning => _running;
    public bool HasProject => _hasProject;
    public ulong Position => (ulong)Interlocked.Read(ref _position);
    public ulong VideoFieldCount => (ulong)Interlocked.Read(ref _videoFieldCount);
    public double ElapsedSeconds => Volatile.Read(ref _elapsedSeconds);
    public string? GamePath { get; private set; }
    public IReadOnlyList<ControllerState> Inputs => Array.AsReadOnly(Volatile.Read(ref _publishedInputs));
    public Func<ControllerState> LiveInput { get; set; } = () => ControllerState.Neutral;
    public event Action<VideoFrame>? VideoReady;
    public event Action<short[], int>? AudioReady;
    public event Action<string>? StatusChanged;
    public event Action? Changed;

    public ExecutionService(IEmulatorBackend backend)
    {
        _backend = backend;
        _backend.VideoReady += frame => { _latestFrame = frame; if (!_suppressMedia) VideoReady?.Invoke(frame); };
        _backend.AudioReady += (samples, rate) => { if (!_suppressMedia) AudioReady?.Invoke(samples, rate); };
        _thread = new Thread(Loop) { IsBackground = true, Name = "TAS emulation" };
        _thread.Start();
    }

    public Task LoadGameAsync(string path, BackendOptions options) => Enqueue(() => LoadGame(path, options));
    public Task<SessionConfiguration?> GetConfigurationAsync() => Enqueue(() =>
        _loaded && _options != null ? new SessionConfiguration(_options with { Configuration = _options.Configuration?.ValidatedCopy() }, _backend.Identity) : null);
    public Task StopAsync() { InterruptSeek(); return Enqueue(() => { Pause(); _backend.Stop(); ClearProject(); _loaded = false; GamePath = null; Notify("Stopped"); }); }
    public Task RunAsync() => Enqueue(() => { RequireLoaded(); EnsureCurrent(); _playbackOnly = false; _running = true; Notify("Recording live input at timeline end"); });
    public Task PlayRecordedAsync() => Enqueue(() =>
    {
        RequireProject(); EnsureCurrent(); _playbackOnly = true;
        _running = _backend.Position < (ulong)_inputs.Count;
        Notify(_running ? "Playing active timeline" : "End of timeline — use Next Frame to add inputs");
    });
    public Task PauseAsync() { InterruptSeek(); return Enqueue(() => { Pause(); Notify("Paused"); }); }
    public Task StepAsync() => Enqueue(() => { RequireLoaded(); Pause(); Step(); Notify("Paused"); });
    /// <summary>Play one recorded group without creating input, moving selection, or repairing a stale preview.</summary>
    public Task<bool> StepRecordedFrameAsync() => Enqueue(() =>
    {
        RequireProject(); Pause();
        if (!Current()) { Notify("Preview predates an input edit — use Seek before advancing"); return false; }
        if (_backend.Position >= (ulong)_inputs.Count) { Notify("End of recorded input — use F10 or F11 to add a frame"); return false; }
        Step(repairPreview: false); Notify("Paused"); return true;
    });
    /// <summary>Advance recorded input unchanged; supplied input is used only at the unrecorded end. Never rebuilds an invalid preview.</summary>
    public Task<bool> AdvanceFrameAsync(ControllerState? input = null) => Enqueue(() =>
    {
        RequireLoaded(); Pause();
        if (!Current()) { Notify("Preview predates an input edit — use Seek before advancing"); return false; }
        if (input is { } shown) shown.Validate();
        Step(input, repairPreview: false); Notify("Paused"); return true;
    });
    public Task ResetAsync() => Enqueue(() => { RequireLoaded(); Pause(); AddEvent(new ExecutionEvent(_backend.Position, ExecutionEventKind.Reset)); Notify("Reset button pressed"); });
    public Task NewProjectAsync(ProjectStartKind startKind = ProjectStartKind.CapturedState) => Enqueue(() =>
    {
        RequireLoaded(); Pause();
        var initial = _backend.Capture() with { Position = 0 };
        _backend.Restore(initial);
        _initial = initial;
        _projectStart = new(startKind);
        _inputs.Clear(); _events.Clear(); _hasProject = true;
        InitializeWorkspace();
        _executedHistory = _history!.At(0);
        Notify("Project created from the current state");
    });

    public Task SaveStateAsync(string path) => Enqueue(() =>
    {
        RequireLoaded(); Pause(); EnsureCurrent(); var state = _backend.Capture();
        ProjectArchive.Save(path, Metadata(ProjectArchive.StateKind, state), state);
        if (_hasProject) RegisterSavedState(path, state.Position);
        Notify("Saved state: " + Path.GetFileName(path));
    });

    public Task LoadStateAsync(string path) => Enqueue(() =>
    {
        RequireLoaded(); Pause(); var archive = ProjectArchive.Load(path, ProjectArchive.StateKind);
        ValidateCompatibility(archive.Metadata);
        if (_hasProject)
        {
            if (archive.Metadata.HistoryHash == null || archive.InitialState.Position > (ulong)_inputs.Count || archive.Metadata.HistoryHash != _history!.At(archive.InitialState.Position))
                throw new InvalidDataException("This saved state is invalid for the project's input/event history. Seek to regenerate it, or open it outside the project as a new baseline.");
        }
        _backend.Restore(archive.InitialState);
        if (_hasProject) _executedHistory = _history!.At(_backend.Position);
        Notify("Loaded state: " + Path.GetFileName(path));
    });

    public Task SaveProjectAsync(string path) => Enqueue(() =>
    {
        RequireProject(); Pause();
        FolderProject.Save(path, Metadata(ProjectArchive.ProjectKind, _initial!), _initial!, _takes, _sections, _savedStates,
            _checkpoints.Select(c => new CheckpointReference(c.State, c.Seconds, c.Created, c.LastUse)).ToArray());
        Notify("Saved project: " + Path.GetFileName(path));
    });

    public Task LoadProjectAsync(string path, BackendOptions options, string? relocatedRom = null, ulong? positionOverride = null,
        EmulationConfiguration? powerOnConfiguration = null) => Enqueue(() =>
    {
        Pause();
        // Validate the container and content identity before replacing the live session.
        var content = FolderProject.Load(path);
        var archive = content.Archive;
        var metadata = archive.Metadata;
        var gamePath = relocatedRom ?? FolderProject.ResolveRomPath(path, metadata.GamePath);
        if (!File.Exists(gamePath)) throw new FileNotFoundException("Project game image is missing: " + gamePath);
        if (HashFile(gamePath) != metadata.GameHash) throw new InvalidDataException("Project game image hash does not match.");
        var projectOptions = options with { InternalResolution = metadata.InternalResolution, DspHle = metadata.DspHle, Configuration = metadata.Configuration?.ValidatedCopy() };
        if (powerOnConfiguration is { } boot)
            projectOptions = projectOptions with { Configuration = boot.ValidatedCopy(), InternalResolution = boot.Resolution, DspHle = boot.DspHle };
        var coreIdentity = _backend.InspectIdentity(options);
        if (coreIdentity != metadata.BackendIdentity) throw new InvalidDataException("Project requires a different emulator build.");
        // Reopening always gets a new writable profile; raw memory-card contents come from the state.
        projectOptions = projectOptions with { SaveDirectory = Path.Combine(options.SaveDirectory, "Projects", Guid.NewGuid().ToString("N")), StorageRoot = options.StorageRoot ?? options.SaveDirectory };
        var rollback = CaptureSession();
        var succeeded = false;
        _preserveCheckpointFiles = true;
        try
        {
            LoadGame(gamePath, projectOptions);
            if (powerOnConfiguration != null)
            {
                // A power-on experiment never restores a state made under another UTC/configuration.
                _initial = _backend.Capture() with { Position = 0 };
                _projectStart = new(ProjectStartKind.PowerOn);
                _inputs.AddRange(metadata.Inputs); _events.AddRange(metadata.Events); _hasProject = true;
                InitializeWorkspace(); _checkpointPolicy = new(Enabled: false);
                RestorePollRecords(metadata.PollFrames.Select(record => record with { PrefixHash = "" }));
                _takes.AddRange(content.Takes); _sections.AddRange(content.Sections); _tags.AddRange(metadata.Tags);
                ApplyEvents(0); _executedHistory = _history!.At(0);
                if (positionOverride is > 0) Seek(positionOverride.Value);
            }
            else
            {
            ValidateCompatibility(metadata);
            _backend.Restore(archive.InitialState);
            _initial = archive.InitialState; _inputs.AddRange(metadata.Inputs); _events.AddRange(metadata.Events); _hasProject = true;
            _projectStart = metadata.Start;
            InitializeWorkspace();
            RestorePollRecords(metadata.PollFrames);
            _checkpointPolicy = metadata.Checkpoints ?? new();
            _takes.AddRange(content.Takes); _sections.AddRange(content.Sections); _savedStates.AddRange(content.States);
            _tags.AddRange(metadata.Tags);
            _checkpoints.AddRange(content.AutomaticCheckpoints.Select(c => new AutomaticCheckpoint(c.State, c.Seconds, c.Created, c.LastUse, new FileInfo(c.State.Path).Length, false)));
            _checkpointAccess = _checkpoints.Count == 0 ? 0 : _checkpoints.Max(c => Math.Max(c.Created, c.LastUse));
            PruneInvalidStates();
            TrimCheckpoints();
            Seek(positionOverride ?? metadata.Position);
            }
            succeeded = true;
        }
        catch
        {
            if (rollback != null) RestoreSession(rollback);
            else { _backend.Stop(); _loaded = false; ClearProject(); GamePath = null; }
            throw;
        }
        finally { FinishSessionReplacement(rollback, succeeded); }
        Notify("Opened project: " + Path.GetFileName(path));
    });

    public Task SeekAsync(ulong target, CancellationToken cancellationToken = default)
    {
        var interruptVersion = Volatile.Read(ref _seekInterruptVersion);
        return Enqueue(() => { RequireProject(); Pause(); Seek(target, cancellationToken, interruptVersion); Notify("Seek complete"); });
    }

    public Task SetInputAsync(int index, ControllerState input) => Enqueue(() => SetInput(index, input));

    // Resolve the execution boundary and edit it in the same owner-thread operation.
    public Task SetCurrentInputAsync(ControllerState state) => Enqueue(() => SetInput(checked((int)_backend.Position), state));

    private void SetInput(int index, ControllerState input)
    {
        RequireProject(); Pause();
        if (index < 0 || index > _inputs.Count) throw new ArgumentOutOfRangeException(nameof(index));
        input.Validate();
        if (index < _inputs.Count && _inputs[index] == input) return;
        RememberEdit();
        if (index == _inputs.Count) _inputs.Add(input); else _inputs[index] = input;
        _sections.Clear(); HistoryChanged(index);
        Notify("Input updated");
    }

    public Task<byte[]> ReadMemoryAsync(uint address, int count) => Enqueue(() => { RequireLoaded(); return _backend.ReadMemory(address, count); });
    public Task WriteMemoryAsync(uint address, byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        var capturedBytes = (byte[])bytes.Clone();
        return Enqueue(() =>
        {
            RequireLoaded(); Pause();
            AddEvent(new ExecutionEvent(_backend.Position, ExecutionEventKind.MemoryWrite, address, capturedBytes));
            Notify("Memory updated");
        });
    }

    private void LoadGame(string path, BackendOptions options)
    {
        options = options with { StorageRoot = options.StorageRoot ?? options.SaveDirectory, Configuration = options.Configuration?.ValidatedCopy() };
        Pause();
        if (!File.Exists(path)) throw new FileNotFoundException("Game image does not exist.", path);
        var hash = HashFile(path);
        _backend.Stop(); _loaded = false; GamePath = null; ClearProject();
        _backend.LoadGame(path, options);
        _loaded = true; _options = options; GamePath = Path.GetFullPath(path); _gameHash = hash;
        Notify("Game loaded — paused");
    }

    private void Step(ControllerState? suppliedInput = null, bool repairPreview = true)
    {
        if (repairPreview) EnsureCurrent();
        else if (!Current()) throw new InvalidOperationException("Preview predates an input edit — use Seek before advancing");
        var index = checked((int)_backend.Position);
        var input = _hasProject && index < _inputs.Count ? _inputs[index] : suppliedInput ?? LiveInput();
        var recording = _hasProject && index == _inputs.Count;
        ExecuteInputGroup(index, input);
        if (_hasProject)
        {
            if (index == _inputs.Count) _inputs.Add(input);
            ApplyEvents(_backend.Position);
            _executedHistory = _history!.At(_backend.Position);
            if (recording) { _redo.Clear(); Interlocked.Increment(ref _revision); }
            MaybeCheckpoint();
        }
        Publish();
    }

    private void Seek(ulong target, CancellationToken cancellationToken = default, long? interruptVersion = null)
    {
        var expectedVersion = interruptVersion ?? Volatile.Read(ref _seekInterruptVersion);
        void CheckCancellation()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (expectedVersion != Volatile.Read(ref _seekInterruptVersion)) throw new OperationCanceledException("Seek interrupted.");
        }
        CheckCancellation();
        if (target > (ulong)_inputs.Count) throw new ArgumentOutOfRangeException(nameof(target));
        _suppressMedia = true;
        try
        {
            RestoreNearest(target);
            for (ulong i = _backend.Position; i < target; ++i)
            {
                CheckCancellation();
                ExecuteInputGroup((int)i, _inputs[(int)i]);
                ApplyEvents(i + 1);
                _executedHistory = _history!.At(i + 1);
                MaybeCheckpoint();
                Interlocked.Exchange(ref _position, checked((long)_backend.Position));
                Interlocked.Exchange(ref _videoFieldCount, checked((long)_backend.VideoFieldCount));
            }
        }
        finally { _suppressMedia = false; if (_latestFrame != null) VideoReady?.Invoke(_latestFrame); Publish(); AudioReady?.Invoke([], 0); }
    }

    // Boundary events execute in insertion order after reaching N, before input N.
    private void AddEvent(ExecutionEvent entry)
    {
        EnsureCurrent();
        ApplyEvent(entry);
        if (_hasProject) { RememberEdit(); _events.Add(entry); HistoryChanged((int)entry.Position); _executedHistory = _history!.At(_backend.Position); }
    }
    private void ApplyEvents(ulong position) { foreach (var entry in _events.Where(e => e.Position == position)) ApplyEvent(entry); }
    private void ApplyEvent(ExecutionEvent entry)
    {
        switch (entry.Kind)
        {
            case ExecutionEventKind.Reset: _backend.Reset(); break;
            case ExecutionEventKind.MemoryWrite: _backend.WriteMemory(entry.Address, entry.Bytes!); break;
            default: throw new InvalidDataException("Unknown execution event.");
        }
    }

    private ArchiveMetadata Metadata(string kind, EmulatorSnapshot initial) => new(
        ProjectArchive.FormatVersion, kind, _backend.Identity, GamePath!, _gameHash,
        _options!.InternalResolution, _options.DspHle, _backend.Position, initial.Position,
        Convert.ToHexString(SHA256.HashData(initial.Data)), initial.Preview?.Width ?? 0, initial.Preview?.Height ?? 0,
        kind == ProjectArchive.ProjectKind ? _inputs.ToArray() : [], kind == ProjectArchive.ProjectKind ? _events.ToArray() : [])
        { HistoryHash = _hasProject && kind == ProjectArchive.StateKind ? _history!.At(initial.Position) : null,
          Configuration = _options!.Configuration?.ValidatedCopy(), ConfigurationIdentity = _backend.ConfigurationIdentity, Checkpoints = _checkpointPolicy, Start = _projectStart,
          Tags = kind == ProjectArchive.ProjectKind ? _tags.ToArray() : [],
          PollFrames = kind == ProjectArchive.ProjectKind ? PollRecords() : [] };

    private void ValidateCompatibility(ArchiveMetadata metadata)
    {
        if (metadata.Configuration?.Fingerprint != _options!.Configuration?.Fingerprint || metadata.ConfigurationIdentity != _backend.ConfigurationIdentity)
            throw new InvalidDataException("State requires different project settings or compatibility resources. Restart with the desired settings to create a new baseline.");
        if (metadata.BackendIdentity != _backend.Identity) throw new InvalidDataException("State requires a different Dolphin build.");
        if (metadata.GameHash != _gameHash) throw new InvalidDataException("State belongs to a different game image.");
        if (metadata.InternalResolution != _options!.InternalResolution || metadata.DspHle != _options.DspHle)
            throw new InvalidDataException("State requires different graphics/DSP settings.");
    }

    private static string HashFile(string path) { using var file = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(file)); }
    private void RequireLoaded() { if (!_loaded) throw new InvalidOperationException("Open a game first."); }
    private void RequireProject() { RequireLoaded(); if (!_hasProject) throw new InvalidOperationException("Create or open a TAS project first."); }
    private void ClearProject() { _initial = null; _projectStart = null; _inputs.Clear(); _events.Clear(); _hasProject = false; ClearWorkspace(); }
    private void InterruptSeek() => Interlocked.Increment(ref _seekInterruptVersion);
    private void Pause() { _running = false; AudioReady?.Invoke([], 0); }
    private void Notify(string message) { Publish(); StatusChanged?.Invoke(message); }
    private void Publish()
    {
        Interlocked.Increment(ref _sampleGeneration);
        Interlocked.Exchange(ref _position, checked((long)_backend.Position));
        Interlocked.Exchange(ref _videoFieldCount, checked((long)_backend.VideoFieldCount));
        if (_publishedRevision != _revision) Volatile.Write(ref _publishedInputs, _inputs.ToArray());
        Volatile.Write(ref _elapsedSeconds, _loaded ? Math.Max(0, _backend.EmulatedSeconds - (_hasProject ? _checkpointOrigin : 0)) : 0);
        PublishWorkspace();
        Changed?.Invoke();
    }

    private Task Enqueue(Action action) => Enqueue(() => { action(); return true; });
    private Task<T> Enqueue<T>(Func<T> action)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _commands.Add(() =>
        {
            try { completion.SetResult(action()); }
            catch (OperationCanceledException error) { Pause(); Publish(); completion.SetCanceled(error.CancellationToken); }
            catch (Exception error) { Pause(); Publish(); completion.SetException(error); }
        });
        return completion.Task;
    }

    private void Loop()
    {
        var timer = Stopwatch.StartNew();
        var nextFrame = TimeSpan.Zero;
        try
        {
            while (!_commands.IsCompleted)
            {
                if (_commands.TryTake(out var command, _running ? 0 : IdleWaitMilliseconds))
                { command(); nextFrame = timer.Elapsed; }
                if (!_running) continue;
                var remaining = nextFrame - timer.Elapsed;
                if (remaining > TimeSpan.Zero)
                {
                    if (_commands.TryTake(out command, Math.Max(1, (int)Math.Ceiling(remaining.TotalMilliseconds)))) command();
                    continue;
                }
                var emulatedBefore = _backend.EmulatedSeconds;
                try
                {
                    if (!_playbackOnly || _backend.Position < (ulong)_inputs.Count) Step();
                    if (_playbackOnly && _backend.Position >= (ulong)_inputs.Count) { Pause(); Notify("End of timeline"); }
                }
                catch (Exception error) { Pause(); Notify("Emulation stopped: " + error.Message); }
                var elapsed = _backend.EmulatedSeconds - emulatedBefore;
                var frameDuration = TimeSpan.FromSeconds(double.IsFinite(elapsed) && elapsed > 0 ? elapsed :
                    1 / Math.Clamp(_backend.FramesPerSecond, MinimumFramesPerSecond, MaximumFramesPerSecond));
                nextFrame += frameDuration;
                // Retain the normal deadline through work; avoid a catch-up burst after a stall.
                if (nextFrame < timer.Elapsed - frameDuration) nextFrame = timer.Elapsed;
            }
        }
        finally { ClearAutomaticCheckpoints(); ClearNamedStates(); _backend.Dispose(); }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        InterruptSeek(); _running = false; _commands.CompleteAdding();
        if (Thread.CurrentThread != _thread) _thread.Join();
        _commands.Dispose();
    }
}
