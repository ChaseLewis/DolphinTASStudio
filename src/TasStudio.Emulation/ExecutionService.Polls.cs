using TasStudio.Core;

namespace TasStudio.Emulation;

public sealed partial class ExecutionService
{
    private readonly Dictionary<int, RecordedInputFrame> _pollFrames = [];
    private PollBoundarySnapshot _publishedPollBoundaries = PollBoundarySnapshot.Empty;
    private int _firstChangedPoll;
    public IReadOnlyList<ulong> PollBoundaries => Volatile.Read(ref _publishedPollBoundaries);
    public Task<RecordedInputFrame?> GetPollFrameAsync(int index) => Enqueue(() =>
        _pollFrames.TryGetValue(index, out var record) ? record with { Frame = record.Frame.Copy() } : null);
    private RecordedInputFrame[] PollRecords()
    {
        // Records are keyed by input group, so walking the movie yields them in order without sorting.
        var records = new List<RecordedInputFrame>(_pollFrames.Count);
        for (var index = 0; index <= _inputs.Count && records.Count < _pollFrames.Count; index++)
            if (_pollFrames.TryGetValue(index, out var record)) records.Add(record);
        return records.Count == _pollFrames.Count ? records.ToArray() : _pollFrames.Values.OrderBy(frame => frame.Index).ToArray();
    }
    private RecordedInputFrame[] PollRecords(int start, int end)
    {
        var records = new List<RecordedInputFrame>();
        for (var index = start; index < end; index++)
            if (_pollFrames.TryGetValue(index, out var record)) records.Add(record);
        return records.ToArray();
    }
    private void RestorePollRecords(IEnumerable<RecordedInputFrame> records)
    { InvalidateTimeline(0); _pollFrames.Clear(); foreach (var record in records) _pollFrames.Add(record.Index, record); }

    /// <summary>Inputs before <paramref name="firstChanged"/> are unchanged, so their recorded groups still agree.</summary>
    private void RewritePollInputs(int firstChanged = 0)
    {
        if (_pollFrames.Count == 0) return;
        List<int>? beyond = null;
        foreach (var index in _pollFrames.Keys) if (index >= _inputs.Count) (beyond ??= []).Add(index);
        if (beyond != null) foreach (var index in beyond) _pollFrames.Remove(index);
        for (var index = Math.Max(0, firstChanged); index < _inputs.Count; index++)
        {
            if (!_pollFrames.TryGetValue(index, out var record)) continue;
            var input = _inputs[index];
            if (record.Frame.Input == input) continue;
            _pollFrames[index] = record with { PrefixHash = "", Frame = record.Frame with
                { Input = input, Polls = record.Frame.Polls.Select(poll => poll with { Input = input }).ToArray() } };
        }
    }

    private void ExecuteInputGroup(int index, ControllerState input)
    {
        var useTimeline = _hasProject && !_takingOver;
        var prefix = useTimeline ? _history!.At((ulong)index) : "";
        _collectingPresentation = true;
        _groupVideo = null;
        InputPollFrame? played = null;
        try
        {
            if (useTimeline && _pollFrames.TryGetValue(index, out var recorded) &&
                recorded.PrefixHash == prefix && recorded.Frame.Input == input)
            {
                played = recorded.Frame;
                _backend.ReplayInputPollFrame(recorded.Frame);
            }
            else
            {
                _backend.Step(input);
                played = _backend.LastInputPollFrame;
                if (useTimeline && _backend.LastInputPollFrame is { } frame)
                {
                    frame.Validate();
                    _pollFrames[index] = new(index, prefix, frame.Copy());
                    _firstChangedPoll = Math.Min(_firstChangedPoll, index);
                    _history!.Invalidate(index);
                    if (!_trialPlayback) Interlocked.Increment(ref _revision);
                }
            }
        }
        catch { _executedHistory = null; throw; }
        finally { _collectingPresentation = false; }
        _lastPresentedInput = played == null ? input : played.Polls.Length > 0 ? played.Polls[^1].Input : _lastPresentedInput;
        if (_groupVideo != null) Present(_groupVideo, _lastPresentedInput, _backend.Position);
    }

    private void PublishPollBoundaries()
    {
        var boundaries = _publishedPollBoundaries.Update(_inputs.Count, _firstChangedPoll, _pollFrames,
            _history == null ? null : index => _history.At((ulong)index, remember: false));
        Volatile.Write(ref _publishedPollBoundaries, boundaries);
        _firstChangedPoll = int.MaxValue;
        var fps = Math.Clamp(_backend.FramesPerSecond, MinimumFramesPerSecond, MaximumFramesPerSecond);
        var tickRate = _backend.EmulatedTicksPerSecond;
        if (!ReferenceEquals(_publishedInputTiming.Boundaries, boundaries) || _publishedInputTiming.FramesPerSecond != fps ||
            _publishedInputTiming.TicksPerSecond != tickRate)
            Volatile.Write(ref _publishedInputTiming, new(boundaries, fps, tickRate));
    }
}
