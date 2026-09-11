using TasStudio.Core;

namespace TasStudio.Emulation;

public sealed partial class ExecutionService
{
    private void ClearInputFromCurrentPosition()
    {
        var start = checked((int)_backend.Position);
        if (start == _inputs.Count && !_events.Any(e => e.Position > (ulong)start)) return;
        RememberEdit();
        _inputs.RemoveRange(start, _inputs.Count - start);
        _events.RemoveAll(e => e.Position > (ulong)start);
        var sections = _sections.Where(s => s.Start < start)
            .Select(s => s with { Length = Math.Min(s.Length, start - s.Start) }).ToArray();
        _sections.Clear(); _sections.AddRange(sections);
        HistoryChanged(start);
        Notify("Saved state restored; later input cleared");
    }

    internal Task<ExperimentPlayAnchor> CapturePlayAnchorAsync() => Enqueue(() =>
    {
        RequireProject();
        if (!Current()) throw new InvalidOperationException("Seek before starting play capture.");
        var start = checked((int)_backend.Position);
        return new ExperimentPlayAnchor(start, _history!.BeforeEventsAt((ulong)start), _history.At((ulong)start));
    });

    internal Task<ExperimentPlay> CapturePlayAsync(ExperimentPlayAnchor anchor, int index, string name, double score) => Enqueue(() =>
    {
        RequireProject();
        var end = checked((int)_backend.Position);
        if (!Current()) throw new InvalidOperationException("Seek before submitting: the preview predates input edits.");
        if (end <= anchor.Start || end - anchor.Start > ExperimentPlayData.MaximumGroups)
            throw new InvalidOperationException("Submit between 1 and 1,000,000 executed groups after the experiment's starting group.");
        if (_history!.BeforeEventsAt((ulong)anchor.Start) != anchor.PrefixHash)
            throw new InvalidDataException("Cannot submit a play after changing history before the experiment's starting group.");
        var data = new ExperimentPlayData(anchor.BaselineHash, _inputs.Skip(anchor.Start).Take(end - anchor.Start).ToArray(),
            _events.Where(e => e.Position >= (ulong)anchor.Start && e.Position <= (ulong)end).ToArray(),
            PollRecords().Where(f => f.Index >= anchor.Start && f.Index < end).ToArray());
        return new ExperimentPlay(index, name, score, anchor.Start, end - anchor.Start, data.Encode());
    });

    public Task ApplyExperimentPlayAsync(ExperimentPlay play)
    {
        // Decode and validate all data before entering the mutation.
        var data = ExperimentPlayData.Decode(play);
        return Enqueue(() =>
        {
            RequireProject(); Pause();
            if (play.Start > _inputs.Count || _history!.At((ulong)play.Start) != data.BaselineHash)
                throw new InvalidDataException("This play's starting history does not match Active playback. Open its original project and restore the preceding inputs/settings.");
            RememberEdit();
            for (var i = 0; i < play.Length; i++)
                if (play.Start + i < _inputs.Count) _inputs[play.Start + i] = data.Inputs[i]; else _inputs.Add(data.Inputs[i]);
            var end = play.Start + play.Length;
            _events.RemoveAll(e => e.Position >= (ulong)play.Start && e.Position <= (ulong)end);
            _events.AddRange(data.Events);
            foreach (var index in _pollFrames.Keys.Where(i => i >= play.Start).ToArray()) _pollFrames.Remove(index);
            foreach (var frame in data.PollFrames) _pollFrames.Add(frame.Index, frame);
            var sections = new List<TimelineSection>();
            foreach (var section in _sections)
            {
                if (section.Start + section.Length <= play.Start || section.Start >= end) sections.Add(section);
                else
                {
                    if (section.Start < play.Start) sections.Add(section with { Length = play.Start - section.Start });
                    if (section.Start + section.Length > end) sections.Add(section with { Start = end, Length = section.Start + section.Length - end });
                }
            }
            _sections.Clear(); _sections.AddRange(sections); _sections.Add(new(play.Start, play.Length, play.Name));
            HistoryChanged(play.Start);
            Notify("Experiment play applied to Active playback; seek to preview. Undo restores previous inputs.");
        });
    }

    public Task<string> ImportInputTakeAsync(string name, int start, ControllerState[] inputs, string provenance)
    {
        var copy = (ControllerState[])inputs.Clone(); foreach (var input in copy) input.Validate();
        return Enqueue(() =>
        {
            RequireProject();
            if (start < 0 || start > _inputs.Count || copy.Length is < 1 or > 100000 || (long)start + copy.Length > int.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(start));
            var id = AddTake(name, start, copy, provenance); Notify("Experiment inputs imported as a take"); return id;
        });
    }
}
