using TasStudio.Core;

namespace TasStudio.Emulation;

public sealed partial class ExecutionService
{
    private readonly Dictionary<int, RecordedInputFrame> _pollFrames = [];
    private ulong[] _publishedPollBoundaries = [0];
    public IReadOnlyList<ulong> PollBoundaries => Array.AsReadOnly(Volatile.Read(ref _publishedPollBoundaries));
    public Task<RecordedInputFrame?> GetPollFrameAsync(int index) => Enqueue(() =>
        _pollFrames.TryGetValue(index, out var record) ? record with { Frame = record.Frame.Copy() } : null);
    private RecordedInputFrame[] PollRecords() => _pollFrames.Values.OrderBy(frame => frame.Index).ToArray();
    private void RestorePollRecords(IEnumerable<RecordedInputFrame> records)
    { _pollFrames.Clear(); foreach (var record in records) _pollFrames.Add(record.Index, record); }

    private void RewritePollInputs()
    {
        foreach (var index in _pollFrames.Keys.ToArray())
        {
            if (index >= _inputs.Count) { _pollFrames.Remove(index); continue; }
            var record = _pollFrames[index]; var input = _inputs[index];
            if (record.Frame.Input == input) continue;
            _pollFrames[index] = record with { PrefixHash = "", Frame = record.Frame with
                { Input = input, Polls = record.Frame.Polls.Select(poll => poll with { Input = input }).ToArray() } };
        }
    }

    private void ExecuteInputGroup(int index, ControllerState input)
    {
        var prefix = _hasProject ? _history!.At((ulong)index) : "";
        try
        {
            if (_hasProject && _pollFrames.TryGetValue(index, out var recorded) &&
                recorded.PrefixHash == prefix && recorded.Frame.Input == input)
                _backend.ReplayInputPollFrame(recorded.Frame);
            else
            {
                _backend.Step(input);
                if (_hasProject && _backend.LastInputPollFrame is { } frame)
                {
                    frame.Validate();
                    _pollFrames[index] = new(index, prefix, frame.Copy());
                    _history!.Invalidate(index);
                    Interlocked.Increment(ref _revision);
                }
            }
        }
        catch { _executedHistory = null; throw; }
    }

    private void PublishPollBoundaries()
    {
        var boundaries = new ulong[_inputs.Count + 1];
        for (var index = 0; index < _inputs.Count; index++)
            boundaries[index + 1] = boundaries[index] + (ulong)(_pollFrames.TryGetValue(index, out var frame) ? frame.Frame.Polls.Length : 0);
        Volatile.Write(ref _publishedPollBoundaries, boundaries);
    }
}
