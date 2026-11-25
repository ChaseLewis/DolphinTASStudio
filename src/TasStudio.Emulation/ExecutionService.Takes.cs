using System.Diagnostics;
using TasStudio.Core;

namespace TasStudio.Emulation;

public sealed partial class ExecutionService
{
    private volatile bool _trialPlayback;
    public bool IsTrialPlayback => _trialPlayback;

    /// <summary>Copies controller intent, independent of the source take and its poll timing.</summary>
    public Task<ControllerState[]> CopyInputRangeAsync(int start, int length, string? takeId = null) => Enqueue(() =>
    {
        RequireProject();
        var take = takeId == null ? null : _takes.Single(t => t.Id == takeId);
        var source = take?.Inputs ?? _inputs.ToArray();
        var offset = start - (take?.Start ?? 0);
        ValidateRange(offset, length, source.Length);
        return source.AsSpan(offset, length).ToArray();
    });

    public Task PasteInputRangeAsync(int start, ControllerState[] inputs, string? takeId = null)
    {
        var copy = (ControllerState[])inputs.Clone();
        foreach (var input in copy) input.Validate();
        return Enqueue(() =>
        {
            RequireProject(); Pause();
            var index = takeId == null ? -1 : _takes.FindIndex(t => t.Id == takeId);
            if (takeId != null && index < 0) throw new InvalidOperationException("Take was removed.");
            var take = index < 0 ? null : _takes[index];
            var source = take?.Inputs ?? _inputs.ToArray();
            var offset = start - (take?.Start ?? 0);
            if (copy.Length == 0 || offset < 0 || offset > source.Length ||
                (long)start + copy.Length > FolderProject.MaximumFrames ||
                (take != null && (long)offset + copy.Length > source.Length))
                throw new ArgumentOutOfRangeException(nameof(start), "Paste must fit inside the destination take; Active playback can extend at its end.");
            var result = new ControllerState[Math.Max(source.Length, offset + copy.Length)];
            source.CopyTo(result, 0); copy.CopyTo(result, offset);
            if (source.SequenceEqual(result)) return;
            RememberEdit();
            if (take != null)
            {
                _takes[index] = take with { Inputs = result };
                Interlocked.Increment(ref _revision);
            }
            else
            {
                _inputs.Clear(); _inputs.AddRange(result); _sections.Clear(); HistoryChanged(start);
            }
            Notify("Inputs pasted; Undo restores the destination");
        });
    }

    public Task SetTakeActiveAsync(string id, bool active) => Enqueue(() =>
    {
        RequireProject(); Pause();
        var index = _takes.FindIndex(t => t.Id == id);
        if (index < 0) throw new InvalidOperationException("Take was removed.");
        if (_takes[index].IsActive == active) return;
        RememberEdit(); _takes[index] = _takes[index] with { IsActive = active };
        Interlocked.Increment(ref _revision); Notify("Take selection updated; trial or apply to use these inputs");
    });

    public Task MoveTakeAsync(string id, int direction) => Enqueue(() =>
    {
        RequireProject(); Pause();
        var index = _takes.FindIndex(t => t.Id == id);
        if (index < 0) throw new InvalidOperationException("Take was removed.");
        var target = Math.Clamp(index + Math.Sign(direction), 0, _takes.Count - 1);
        if (index == target) return;
        RememberEdit(); var take = _takes[index]; _takes.RemoveAt(index); _takes.Insert(target, take);
        Interlocked.Increment(ref _revision); Notify("Take priority updated; highest active take wins overlaps");
    });

    private sealed record TakeSpan(InputTake Take, int Start, int Length);
    private TakeSpan[] ActiveTakeSpans()
    {
        var active = _takes.Where(t => t.IsActive).ToArray();
        if (active.Length == 0) throw new InvalidOperationException("Mark at least one take active first.");
        var boundaries = active.SelectMany(t => new[] { t.Start, checked(t.Start + t.Inputs.Length) }).Distinct().Order().ToArray();
        var spans = new List<TakeSpan>();
        var end = _inputs.Count;
        for (var i = 0; i < boundaries.Length - 1; i++)
        {
            var start = boundaries[i];
            var take = active.FirstOrDefault(t => t.Start <= start && t.Start + t.Inputs.Length > start);
            if (take == null) continue;
            if (start > end || boundaries[i + 1] > FolderProject.MaximumFrames)
                throw new InvalidOperationException("Active takes leave a gap beyond Active playback. Fill the gap before trialing or applying.");
            spans.Add(new(take, start, boundaries[i + 1] - start));
            end = Math.Max(end, boundaries[i + 1]);
        }
        // Validate experiment receipts before any mutation.
        foreach (var take in active.Where(t => t.Experiment != null)) ExperimentPlayData.Decode(take.Experiment!);
        return spans.ToArray();
    }

    private void OverlayTakes(TakeSpan[] spans)
    {
        foreach (var span in spans)
            ApplyTakeRecording(span.Take, span.Start - span.Take.Start, span.Start, span.Length);
        RewritePollInputs(); _history!.Invalidate(spans[0].Start);
    }

    public Task ApplyActiveTakesAsync() => Enqueue(() =>
    {
        RequireProject(); Pause(); var spans = ActiveTakeSpans();
        RememberEdit(); OverlayTakes(spans);
        foreach (var span in spans)
        {
            var end = span.Start + span.Length;
            var remaining = _sections.SelectMany(s =>
                s.Start >= end || s.Start + s.Length <= span.Start ? new[] { s } :
                new[] { s with { Length = span.Start - s.Start }, s with { Start = end, Length = s.Start + s.Length - end } }.Where(s => s.Length > 0)).ToArray();
            _sections.Clear(); _sections.AddRange(remaining); _sections.Add(new(span.Start, span.Length, span.Take.Name));
        }
        for (var i = 0; i < _takes.Count; i++) _takes[i] = _takes[i] with { IsActive = false };
        HistoryChanged(spans[0].Start); Notify("Active takes applied; Undo restores the movie and active take selection");
    });

    /// <summary>Play the chosen stack on the owner thread, restoring the movie even on stop or failure.</summary>
    public Task TrialActiveTakesAsync(int start, CancellationToken cancellationToken = default)
    {
        var interruptVersion = Volatile.Read(ref _seekInterruptVersion);
        return Enqueue(() =>
        {
            RequireProject(); Pause(); var spans = ActiveTakeSpans();
            var end = Math.Max(_inputs.Count, spans.Max(s => s.Start + s.Length));
            if (start < 0 || start >= end) throw new ArgumentOutOfRangeException(nameof(start));
            var original = _inputs.ToArray(); var events = _events.ToArray(); var polls = PollRecords();
            var revision = _revision;
            void CheckCancellation()
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (interruptVersion != Volatile.Read(ref _seekInterruptVersion)) throw new OperationCanceledException("Trial stopped.");
            }
            try
            {
                CheckCancellation(); _trialPlayback = true;
                OverlayTakes(spans);
                Seek((ulong)start, cancellationToken, interruptVersion);
                _playbackOnly = true; _running = true;
                Notify("Trial playback — highest active take wins; Escape stops; Active playback is preserved");
                var timer = Stopwatch.StartNew(); var pacing = new PlaybackPacer(); pacing.Restart(timer.Elapsed);
                while (_backend.Position < (ulong)end)
                {
                    CheckCancellation();
                    var remaining = pacing.Deadline - timer.Elapsed;
                    if (remaining > TimeSpan.Zero) { Thread.Sleep(Math.Clamp((int)Math.Ceiling(remaining.TotalMilliseconds), 1, 10)); continue; }
                    if (!_realtimePlayback && _backend is IRealtimeAudioBackend realtime)
                    { var source = realtime.StartRealtimePlayback(); _realtimePlayback = true; AudioSourceChanged?.Invoke(source); }
                    var before = _backend.EmulatedSeconds;
                    Step(repairPreview: false);
                    var elapsed = _backend.EmulatedSeconds - before;
                    pacing.Advance(TimeSpan.FromSeconds(double.IsFinite(elapsed) && elapsed > 0 ? elapsed :
                        1 / Math.Clamp(_backend.FramesPerSecond, MinimumFramesPerSecond, MaximumFramesPerSecond)), timer.Elapsed);
                }
            }
            finally
            {
                try { Pause(); }
                finally
                {
                    _inputs.Clear(); _inputs.AddRange(original); _events.Clear(); _events.AddRange(events); RestorePollRecords(polls);
                    _history!.Invalidate(0); _revision = revision; _trialPlayback = false;
                    _publishedRevision = -1; Publish();
                }
            }
            Notify("Trial finished; Active playback preserved. Apply active takes to keep the chosen inputs, or Seek to return.");
        });
    }

    private InputTimingSnapshot _publishedInputTiming = InputTimingSnapshot.Empty;
    public InputTimingSnapshot InputTiming => Volatile.Read(ref _publishedInputTiming);
    public double InputAgeSeconds(int start)
    {
        var timing = Volatile.Read(ref _publishedInputTiming);
        var end = (int)Math.Min(Position, (ulong)(timing.Boundaries.Count - 1));
        if (start < 0 || start >= end) return 0d;
        // Groups can span several video fields; poll count is not a clock.
        return (timing.Boundaries.FieldsAt(end) - timing.Boundaries.FieldsAt(start)) / timing.FramesPerSecond;
    }
}
