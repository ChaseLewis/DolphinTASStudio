using System.Security.Cryptography;
using System.Text.Json;
using TasStudio.Core;

namespace TasStudio.Emulation;

public sealed partial class ExecutionService
{
    private const int MaximumUndoEntries = 32;
    private const long MaximumUndoBytes = 64L * 1024 * 1024;
    private long _publishedRevision = -1;
    private ExecutionHistory? _history;
    private string? _executedHistory;
    private bool _suppressMedia;
    private VideoFrame? _latestFrame;
    private long _revision;
    private volatile bool _publishedCurrent = true;
    private readonly List<InputTake> _takes = [];
    private readonly List<TimelineSection> _sections = [];
    private readonly List<SavedStateReference> _savedStates = [];
    private sealed record AutomaticCheckpoint(SavedStateReference State, double Seconds, long Created, long LastUse, long Bytes, bool OwnsFile = true);
    private readonly List<AutomaticCheckpoint> _checkpoints = [];
    private CheckpointPolicy _checkpointPolicy = new();
    private double _checkpointOrigin;
    private long _checkpointAccess;
    private double _lastCheckpointAttempt = double.NaN;
    private bool _preserveCheckpointFiles;
    private sealed record Edit(ControllerState[] Inputs, ExecutionEvent[] Events, TimelineSection[] Sections, InputTake[] Takes)
    { public RecordedInputFrame[] PollFrames { get; init; } = []; }
    private readonly List<Edit> _undo = [], _redo = [];
    private InputTake[] _publishedTakes = [];
    private TimelineSection[] _publishedSections = [];
    private StateMarker[] _publishedMarkers = [];
    public CheckpointPolicy Checkpoints => Volatile.Read(ref _checkpointPolicy);
    public bool IsPreviewCurrent => _publishedCurrent;
    public long Revision => Interlocked.Read(ref _revision);
    public IReadOnlyList<InputTake> Takes => Array.AsReadOnly(Volatile.Read(ref _publishedTakes));
    public IReadOnlyList<TimelineSection> Sections => Array.AsReadOnly(Volatile.Read(ref _publishedSections));
    public IReadOnlyList<StateMarker> StateMarkers => Array.AsReadOnly(Volatile.Read(ref _publishedMarkers));

    private void InitializeWorkspace()
    {
        ClearWorkspace();
        var identity = JsonSerializer.Serialize(new { Game = _gameHash, Core = BaselineRuntime.BackendIdentity,
            _options!.InternalResolution, _options.DspHle, Controller = "gamecube/port1/raw8/v1",
            Initial = Convert.ToHexString(SHA256.HashData(_initial!.Data)) });
        // Retain legacy history identities until an explicit configuration restart.
        if (_options.Configuration != null) identity = JsonSerializer.Serialize(new { Settings = _options.Configuration.Fingerprint, Environment = BaselineRuntime.ConfigurationIdentity, LegacyIdentity = identity });
        _history = new ExecutionHistory(identity, _inputs, _events, _pollFrames);
        _checkpointOrigin = _backend.EmulatedSeconds;
        _lastCheckpointAttempt = double.NaN;
        Interlocked.Increment(ref _revision);
    }
    private void ClearWorkspace()
    {
        _history = null; _executedHistory = null; _takes.Clear(); _sections.Clear(); _tags.Clear(); ClearNamedStates();
        _pollFrames.Clear(); Volatile.Write(ref _publishedPollBoundaries, [0]);
        ClearAutomaticCheckpoints(); _checkpointPolicy = new(); _undo.Clear(); _redo.Clear(); _publishedCurrent = true;
        Interlocked.Increment(ref _revision);
    }
    private bool Current() => !_hasProject || (_backend.Position <= (ulong)_inputs.Count && _executedHistory == _history!.At(_backend.Position));
    private void EnsureCurrent() { if (!Current()) Seek(_backend.Position); }
    private void HistoryChanged(int index) { RewritePollInputs(); _history!.Invalidate(index); PruneInvalidStates(); Interlocked.Increment(ref _revision); }
    private void RememberEdit()
    {
        _undo.Add(new(_inputs.ToArray(), _events.ToArray(), _sections.ToArray(), _takes.ToArray()) { PollFrames = PollRecords() }); _redo.Clear();
        while (_undo.Count > MaximumUndoEntries || (_undo.Count > 1 && _undo.Sum(e => (long)e.Inputs.Length * 8) > MaximumUndoBytes)) _undo.RemoveAt(0);
    }
    public Task UndoAsync() => Enqueue(() => ChangeHistory(_undo, _redo));
    public Task RedoAsync() => Enqueue(() => ChangeHistory(_redo, _undo));
    /// <summary>Keep the selected input group and remove only the groups after it.</summary>
    public Task ClearLaterInputAsync(int frame) => Enqueue(() =>
    {
        RequireProject(); Pause();
        if (frame < 0 || frame > _inputs.Count) throw new ArgumentOutOfRangeException(nameof(frame));
        if (frame >= _inputs.Count - 1) return;
        var start = frame + 1;
        // Restore before editing if the preview lies in the part being removed.
        // A failed restore must leave the recording intact.
        if (_backend.Position > (ulong)start) Seek((ulong)start);
        RememberEdit();
        _inputs.RemoveRange(start, _inputs.Count - start);
        _events.RemoveAll(e => e.Position > (ulong)start);
        var sections = _sections.Where(s => s.Start < start)
            .Select(s => s with { Length = Math.Min(s.Length, start - s.Start) }).ToArray();
        _sections.Clear(); _sections.AddRange(sections);
        HistoryChanged(start);
        Notify("Later input cleared; undo restores the recording");
    });
    private void ChangeHistory(List<Edit> from,
        List<Edit> to)
    {
        RequireProject(); Pause(); if (from.Count == 0) return;
        var edit = from[^1]; from.RemoveAt(from.Count - 1);
        to.Add(new(_inputs.ToArray(), _events.ToArray(), _sections.ToArray(), _takes.ToArray()) { PollFrames = PollRecords() });
        _inputs.Clear(); _inputs.AddRange(edit.Inputs); _events.Clear(); _events.AddRange(edit.Events);
        RestorePollRecords(edit.PollFrames);
        _sections.Clear(); _sections.AddRange(edit.Sections); _takes.Clear(); _takes.AddRange(edit.Takes); HistoryChanged(0);
        if (_backend.Position > (ulong)_inputs.Count) Seek((ulong)_inputs.Count);
        Notify("Timeline history updated");
    }
    public Task<string> CaptureTakeAsync(string name, int start, int length) => Enqueue(() =>
    {
        RequireProject(); ValidateRange(start, length, _inputs.Count);
        return AddTake(name, start, _inputs.Skip(start).Take(length).ToArray(), "Manual copy");
    });
    /// <summary>Result producers submit exact inputs against a verified baseline. Submission never changes playback.</summary>
    public Task<string> AddCandidateAsync(string name, int start, ControllerState[] inputs, string baselineHash, string eventsHash, string provenance)
    {
        var copy = (ControllerState[])inputs.Clone();
        foreach (var input in copy) input.Validate();
        return Enqueue(() =>
        {
            RequireProject(); ValidateRange(start, copy.Length, _inputs.Count);
            if (_history!.At((ulong)start) != baselineHash) throw new InvalidDataException("Candidate baseline no longer matches the active movie.");
            if (EventsHash(start, copy.Length) != eventsHash) throw new InvalidDataException("Candidate boundary events changed.");
            return AddTake(name, start, copy, provenance);
        });
    }
    public Task<string> HistoryAtAsync(ulong position) => Enqueue(() => { RequireProject(); return _history!.At(position); });
    public Task<string> EventsHashAsync(int start, int length) => Enqueue(() => { RequireProject(); ValidateRange(start, length, _inputs.Count); return EventsHash(start, length); });
    private string EventsHash(int start, int length) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(_events.Where(e => e.Position >= (ulong)start && e.Position <= (ulong)(start + length)).ToArray())));
    private bool CandidateValid(InputTake take) => take.Start >= 0 && (long)take.Start + take.Inputs.Length <= _inputs.Count &&
        _history!.At((ulong)take.Start) == take.BaselineHash && take.EventsHash == EventsHash(take.Start, take.Inputs.Length);
    private string AddTake(string name, int start, ControllerState[] inputs, string provenance)
    {
        RememberEdit(); var id = Guid.NewGuid().ToString("N");
        _takes.Add(new(id, string.IsNullOrWhiteSpace(name) ? "Take " + (_takes.Count + 1) : name.Trim(), start, inputs, _history!.At((ulong)start), provenance) { EventsHash = EventsHash(start, inputs.Length) });
        Interlocked.Increment(ref _revision); Notify("Candidate added; active playback unchanged"); return id;
    }
    public Task SetTakeInputAsync(string id, int frame, ControllerState input) => Enqueue(() =>
    {
        var index = _takes.FindIndex(t => t.Id == id); if (index < 0) throw new InvalidOperationException("Take was removed.");
        var take = _takes[index]; var offset = frame - take.Start;
        input.Validate();
        if (offset < 0 || offset >= take.Inputs.Length) throw new ArgumentOutOfRangeException(nameof(frame));
        RememberEdit(); var copy = (ControllerState[])take.Inputs.Clone(); copy[offset] = input;
        _takes[index] = take with { Inputs = copy }; Interlocked.Increment(ref _revision); Notify("Candidate input updated; active playback unchanged");
    });
    public Task EditRangeAsync(int start, int length, string? takeId, ControllerState value, PadButtons changedButtons, int changedAxes) => Enqueue(() =>
    {
        RequireProject(); Pause();
        var takeIndex = takeId == null ? -1 : _takes.FindIndex(t => t.Id == takeId);
        value.Validate();
        if (takeId != null && takeIndex < 0) throw new InvalidOperationException("Take was removed.");
        var take = takeIndex < 0 ? null : _takes[takeIndex];
        if (take == null && start == _inputs.Count && length == 1)
        {
            if (changedButtons == PadButtons.None && changedAxes == 0) return;
            // Authoring the next group is already a project edit. Actual poll
            // timings are captured later when that group is first executed.
            RememberEdit(); _inputs.Add(value); HistoryChanged(start);
            Notify("Next input saved to timeline; preview unchanged");
            return;
        }
        var offset = take?.Start ?? 0; var source = take?.Inputs ?? _inputs.ToArray();
        ValidateRange(start - offset, length, source.Length);
        if (changedButtons == PadButtons.None && changedAxes == 0) return;
        var copy = (ControllerState[])source.Clone();
        for (var i = start - offset; i < start - offset + length; i++)
        {
            var original = copy[i]; byte Axis(int bit, byte oldValue, byte newValue) => (changedAxes & (1 << bit)) != 0 ? newValue : oldValue;
            copy[i] = new((original.Buttons & ~changedButtons) | (value.Buttons & changedButtons), Axis(0, original.StickX, value.StickX), Axis(1, original.StickY, value.StickY),
                Axis(2, original.CStickX, value.CStickX), Axis(3, original.CStickY, value.CStickY), Axis(4, original.TriggerL, value.TriggerL), Axis(5, original.TriggerR, value.TriggerR));
        }
        if (copy.SequenceEqual(source)) return;
        if (take != null) { RememberEdit(); _takes[takeIndex] = take with { Inputs = copy }; Interlocked.Increment(ref _revision); }
        else { RememberEdit(); _inputs.Clear(); _inputs.AddRange(copy); _sections.Clear(); HistoryChanged(start); }
        Notify(take == null ? "Input range edited; seek to update preview" : "Candidate edited; active playback unchanged");
    });
    public Task ExportReplayAsync(string path) => Enqueue(() =>
    {
        RequireProject(); Pause();
        var position = _backend.Position;
        // Materialize edited/unexecuted groups into actual polls before baking.
        Seek((ulong)_inputs.Count);
        Seek(Math.Min(position, (ulong)_inputs.Count));
        ProjectArchive.Save(path, Metadata(ProjectArchive.ProjectKind, _initial!) with { Position = 0 }, _initial!);
        Notify("Baked replay exported — open it with Open Project / Replay");
    });
    public Task UseTakeAsync(string id, int start, int length, bool removeTake = false) => Enqueue(() =>
    {
        RequireProject(); Pause(); var take = _takes.Single(t => t.Id == id);
        ValidateRange(start, length, _inputs.Count);
        if (start < take.Start || (long)start + length > (long)take.Start + take.Inputs.Length) throw new InvalidOperationException("Select a range inside the candidate take.");
        if (!CandidateValid(take)) throw new InvalidDataException("Candidate baseline or boundary events changed. Generate or copy a new take from the current movie.");
        ApplyTakeSection(take, start - take.Start, start, length, removeTake);
    });
    public Task ApplyTakeAtCursorAsync(string id, int target) => Enqueue(() =>
    {
        RequireProject(); Pause();
        var take = _takes.Single(t => t.Id == id);
        if (target < 0 || target > _inputs.Count || (long)target + take.Inputs.Length > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(target));
        // Relocation copies input intent only. Poll timing is validated/regenerated at the destination.
        ApplyTakeSection(take, 0, target, take.Inputs.Length, removeTake: true);
    });
    private void ApplyTakeSection(InputTake take, int sourceOffset, int start, int length, bool removeTake)
    {
        RememberEdit();
        for (var i = 0; i < length; i++)
        {
            var input = take.Inputs[sourceOffset + i];
            if (start + i < _inputs.Count) _inputs[start + i] = input;
            else _inputs.Add(input);
        }
        // Sections describe provenance; flattened exact inputs remain canonical.
        var remaining = new List<TimelineSection>();
        foreach (var s in _sections)
        {
            if (s.Start + s.Length <= start || s.Start >= start + length) remaining.Add(s);
            else { if (s.Start < start) remaining.Add(s with { Length = start - s.Start }); if (s.Start + s.Length > start + length) remaining.Add(s with { Start = start + length, Length = s.Start + s.Length - start - length }); }
        }
        _sections.Clear(); _sections.AddRange(remaining); _sections.Add(new(start, length, take.Name));
        if (removeTake) _takes.Remove(take);
        HistoryChanged(start); Notify(removeTake ? "Section applied and take removed; seek to update the game preview" : "Section applied; seek to update the game preview");
    }
    public Task AuditionTakeAsync(string id, int target) => Enqueue(() =>
    {
        RequireProject(); Pause(); var take = _takes.Single(t => t.Id == id);
        if (target < take.Start || target > take.Start + take.Inputs.Length || !CandidateValid(take))
            throw new InvalidDataException("Audition target or baseline is invalid.");
        var original = _inputs.ToArray(); var events = _events.ToArray();
        try
        {
            for (var i = 0; i < take.Inputs.Length; i++) _inputs[take.Start + i] = take.Inputs[i];
            _history!.Invalidate(take.Start); Seek((ulong)target);
        }
        finally { _inputs.Clear(); _inputs.AddRange(original); _events.Clear(); _events.AddRange(events); _history!.Invalidate(take.Start); PruneInvalidAutomaticCheckpoints(); Publish(); }
        Notify("Audition preview — active playback unchanged; Seek returns to active history");
    });
    private static void ValidateRange(int start, int length, int count)
    { if (start < 0 || length <= 0 || (long)start + length > count) throw new ArgumentOutOfRangeException(nameof(length), "Select a nonempty range inside the recording."); }

    public Task SaveNamedStateAsync(string name) => Enqueue(() =>
    {
        RequireProject(); Pause(); EnsureCurrent();
        var path = Path.Combine(_options!.SaveDirectory, "StudioStates", Guid.NewGuid().ToString("N") + ".tasstate");
        var snapshot = _backend.Capture(); ProjectArchive.Save(path, Metadata(ProjectArchive.StateKind, snapshot), snapshot);
        RegisterSavedState(path, snapshot.Position, name, ownsFile: true); Notify("Named state saved");
    });
    private void RegisterSavedState(string path, ulong position, string? name = null, bool ownsFile = false)
    {
        _savedStates.RemoveAll(s => Path.GetFullPath(s.Path).Equals(Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase));
        _savedStates.Add(new(Guid.NewGuid().ToString("N"), name ?? Path.GetFileNameWithoutExtension(path), position, _history!.At(position), Path.GetFullPath(path)) { OwnsFile = ownsFile });
        Interlocked.Increment(ref _revision);
    }
    public Task LoadMarkerAsync(string id) => LoadMarkerAsync(id, false);
    public Task LoadMarkerAsync(string id, bool clearLaterInput) => Enqueue(() =>
    {
        RequireProject(); Pause(); var saved = _savedStates.SingleOrDefault(s => s.Id == id) ?? _checkpoints.SingleOrDefault(c => c.State.Id == id)?.State ?? throw new InvalidDataException("Saved state was removed because its history changed.");
        if (!Valid(saved.Position, saved.HistoryHash)) throw new InvalidDataException("Saved state invalid: preceding input/events changed.");
        RestoreSaved(saved); TouchCheckpoint(saved.Id); Notify("Saved state restored");
        if (clearLaterInput) ClearInputFromCurrentPosition();
    });
    public Task ClearMarkerAsync(string id, bool requireFileDeletion = false) => Enqueue(() =>
    {
        RequireProject();
        var saved = _savedStates.SingleOrDefault(s => s.Id == id);
        var checkpoint = _checkpoints.SingleOrDefault(c => c.State.Id == id);
        if (saved != null) RemoveNamedState(saved, requireFileDeletion);
        else if (checkpoint != null) RemoveCheckpoint(checkpoint, requireFileDeletion);
        else throw new InvalidDataException("The selected state has already been removed.");
        Interlocked.Increment(ref _revision);
        Notify(checkpoint != null ? "Checkpoint cleared" : "Saved state cleared");
    });
    public Task PreviousStateAsync() => Enqueue(() =>
    {
        RequireProject(); Pause();
        var saved = _savedStates.Concat(_checkpoints.Select(c => c.State))
            .Where(s => s.Position < _backend.Position && Valid(s.Position, s.HistoryHash))
            .OrderByDescending(s => s.Position).FirstOrDefault();
        if (saved != null) { RestoreSaved(saved); TouchCheckpoint(saved.Id); }
        else { _backend.Restore(_initial!); ApplyEvents(0); _executedHistory = _history!.At(0); }
        Notify(saved == null ? "Returned to project start" : "Previous state: " + saved.Name);
    });
    private void TouchCheckpoint(string id)
    {
        var index = _checkpoints.FindIndex(c => c.State.Id == id);
        if (index >= 0) _checkpoints[index] = _checkpoints[index] with { LastUse = ++_checkpointAccess };
    }
    private bool Valid(ulong position, string history) => position <= (ulong)_inputs.Count && history == _history!.At(position);
    private void RestoreSaved(SavedStateReference saved)
    {
        var content = ProjectArchive.Load(saved.Path, ProjectArchive.StateKind); ValidateCompatibility(content.Metadata);
        if (content.Metadata.HistoryHash != saved.HistoryHash || content.InitialState.Position != saved.Position) throw new InvalidDataException("Saved state history metadata does not match its project reference.");
        _backend.Restore(content.InitialState); _executedHistory = saved.HistoryHash;
    }
    private void MaybeCheckpoint()
    {
        if (!_checkpointPolicy.Enabled || _backend.Position == 0) return;
        var seconds = _backend.EmulatedSeconds;
        var previous = _checkpoints.Where(c => c.State.Position <= _backend.Position && Valid(c.State.Position, c.State.HistoryHash)).OrderByDescending(c => c.State.Position).FirstOrDefault();
        if (seconds - (previous?.Seconds ?? _checkpointOrigin) + 0.000001 < _checkpointPolicy.IntervalSeconds) return;
        if (seconds >= _lastCheckpointAttempt && seconds - _lastCheckpointAttempt + 0.000001 < _checkpointPolicy.IntervalSeconds) return;
        var hash = _history!.At(_backend.Position);
        if (_checkpoints.Any(c => c.State.Position == _backend.Position && c.State.HistoryHash == hash)) return;
        _lastCheckpointAttempt = seconds;
        var snapshot = _backend.Capture();
        var id = Guid.NewGuid().ToString("N");
        var path = Path.Combine(_options!.SaveDirectory, "CheckpointCache", id + ".tasstate");
        ProjectArchive.Save(path, Metadata(ProjectArchive.StateKind, snapshot), snapshot);
        var access = ++_checkpointAccess;
        _checkpoints.Add(new(new(id, "Checkpoint", snapshot.Position, hash, path), seconds, access, access, new FileInfo(path).Length));
        TrimCheckpoints();
    }
    private void RestoreNearest(ulong target)
    {
        // A seek always begins from a saved baseline, even if the live preview's hash matches.
        var checkpoint = _checkpoints.Where(c => c.State.Position <= target && Valid(c.State.Position, c.State.HistoryHash)).OrderByDescending(c => c.State.Position).FirstOrDefault();
        var saved = _savedStates.Where(s => s.Position <= target && Valid(s.Position, s.HistoryHash)).OrderByDescending(s => s.Position).FirstOrDefault();
        if (saved != null && (checkpoint == null || saved.Position >= checkpoint.State.Position)) { RestoreSaved(saved); return; }
        if (checkpoint != null)
        {
            RestoreSaved(checkpoint.State);
            _checkpoints[_checkpoints.IndexOf(checkpoint)] = checkpoint with { LastUse = ++_checkpointAccess };
            return;
        }
        _backend.Restore(_initial!); ApplyEvents(0); _executedHistory = _history!.At(0);
    }
    private void PublishWorkspace()
    {
        _publishedCurrent = Current();
        if (_publishedRevision != _revision)
        {
            PublishPollBoundaries();
            Volatile.Write(ref _publishedTakes, _takes.Select(t => t with { Inputs = (ControllerState[])t.Inputs.Clone() }).ToArray());
            Volatile.Write(ref _publishedSections, _sections.ToArray());
            Volatile.Write(ref _publishedTags, _tags.ToArray());
            _publishedRevision = _revision;
        }
        Volatile.Write(ref _publishedMarkers, !_hasProject ? [] : _savedStates.Select(s => new StateMarker(s.Id, s.Name, s.Position, false, Valid(s.Position, s.HistoryHash)))
            .Concat(_checkpoints.Select(c => new StateMarker(c.State.Id, "Checkpoint", c.State.Position, true, Valid(c.State.Position, c.State.HistoryHash)))).ToArray());
    }

    private sealed record SessionBackup(string Path, BackendOptions Options, EmulatorSnapshot State, EmulatorSnapshot? Initial,
        ControllerState[] Inputs, ExecutionEvent[] Events, InputTake[] Takes, TimelineSection[] Sections, SavedStateReference[] States,
        string? ExecutedHistory, Edit[] Undo, Edit[] Redo, long Revision, CheckpointPolicy Checkpoints, double CheckpointOrigin,
        AutomaticCheckpoint[] AutomaticCheckpoints, long CheckpointAccess, ProjectStart? Start)
    {
        public TimelineTag[] Tags { get; init; } = []; public RecordedInputFrame[] PollFrames { get; init; } = [];
        public EmulatorRuntime? BaselineRuntime { get; init; }
        public EmulatorRuntime[] RuntimeHistory { get; init; } = [];
        public bool AllowRuntimeMismatch { get; init; }
        public string? CompatibilityWarning { get; init; }
    }
    private SessionBackup? CaptureSession() => !_loaded ? null : new(GamePath!, _options!, _backend.Capture(), _initial,
        _inputs.ToArray(), _events.ToArray(), _takes.ToArray(), _sections.ToArray(), _savedStates.ToArray(), _executedHistory, _undo.ToArray(), _redo.ToArray(), _revision, _checkpointPolicy, _checkpointOrigin, _checkpoints.ToArray(), _checkpointAccess, _projectStart)
        { Tags = _tags.ToArray(), PollFrames = PollRecords(), BaselineRuntime = _hasProject ? BaselineRuntime : null,
          RuntimeHistory = _runtimeHistory.ToArray(), AllowRuntimeMismatch = _allowRuntimeMismatch, CompatibilityWarning = CompatibilityWarning };
    private void RestoreSession(SessionBackup saved)
    {
        LoadGame(saved.Path, saved.Options); _backend.Restore(saved.State);
        _initial = saved.Initial;
        _projectStart = saved.Start;
        _baselineRuntime = saved.BaselineRuntime;
        _runtimeHistory = saved.RuntimeHistory;
        _allowRuntimeMismatch = saved.AllowRuntimeMismatch;
        Volatile.Write(ref _compatibilityWarning, saved.CompatibilityWarning);
        if (_initial != null)
        {
            _inputs.AddRange(saved.Inputs); _events.AddRange(saved.Events); _hasProject = true; InitializeWorkspace();
            _takes.AddRange(saved.Takes); _sections.AddRange(saved.Sections); _savedStates.AddRange(saved.States);
            _tags.AddRange(saved.Tags);
            RestorePollRecords(saved.PollFrames);
            _undo.AddRange(saved.Undo); _redo.AddRange(saved.Redo); _executedHistory = saved.ExecutedHistory;
            _checkpointPolicy = saved.Checkpoints;
            _checkpointOrigin = saved.CheckpointOrigin;
            _checkpoints.AddRange(saved.AutomaticCheckpoints); _checkpointAccess = saved.CheckpointAccess;
        }
        _revision = saved.Revision; _publishedRevision = -1;
    }
}
