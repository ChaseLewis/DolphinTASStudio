using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using TasStudio.Core;
using TasStudio.Emulation;

namespace TasStudio.App;

/// <summary>Virtual horizontal timeline: drawing cost follows visible lanes/frames, not movie length.</summary>
public sealed partial class TimelineView : Control
{
    private const double LabelWidth = 118, LaneHeight = 38;
    private const int RenderChunkFrames = 256;
    private double RulerHeight => Tags.Count == 0 ? 64 : 94;
    private double FrameTop => RulerHeight - 32;
    private static readonly IBrush TagBrush = StudioTheme.Brush(ThemeColor.TimelineTag);
    private static readonly IBrush BackgroundBrush = StudioTheme.Brush(ThemeColor.TimelineBackground), GridBrush = StudioTheme.Brush(ThemeColor.TimelineGrid),
        TextBrush = StudioTheme.Brush(ThemeColor.Text), MutedBrush = StudioTheme.Brush(ThemeColor.Muted), ActiveBrush = StudioTheme.Brush(ThemeColor.TimelineActive), InputBrush = StudioTheme.Brush(ThemeColor.TimelineInput),
        SelectedBrush = StudioTheme.Brush(ThemeColor.TimelineSelection), CursorBrush = StudioTheme.Brush(ThemeColor.TimelineCursor), SelectionBrush = StudioTheme.Brush(ThemeColor.TimelinePreview), InvalidBrush = StudioTheme.Brush(ThemeColor.TimelineInvalid);
    private static readonly Typeface Font = new("Segoe UI");
    public int FirstFrame { get; set; }
    public int VisibleFrames { get; set; } = 300;
    public int InputCount { get; set; }
    public IReadOnlyList<ControllerState> Inputs { get; set; } = [];
    public ulong Position { get; set; }
    public bool Current { get; set; } = true;
    public bool TrialPlayback { get; set; }
    private IReadOnlyList<InputTake> _takes = [];
    private int _maximumTakeEnd;
    public IReadOnlyList<InputTake> Takes
    {
        get => _takes;
        set
        {
            if (ReferenceEquals(_takes, value)) return;
            _takes = value;
            _maximumTakeEnd = value.Count == 0 ? 0 : value.Max(t => t.Start + t.Inputs.Length);
        }
    }
    public IReadOnlyList<TimelineSection> Sections { get; set; } = [];
    public IReadOnlyList<StateMarker> Markers { get; set; } = [];
    public IReadOnlySet<string> ExperimentStateIds { get; set; } = new HashSet<string>();
    private IReadOnlyList<TimelineTag> _tags = [];
    private int _maximumTagFrame;
    public IReadOnlyList<TimelineTag> Tags
    {
        get => _tags;
        set
        {
            if (ReferenceEquals(_tags, value)) return;
            _tags = value;
            _maximumTagFrame = value.Count == 0 ? 0 : (int)Math.Min(value.Max(t => t.Position), int.MaxValue - 1UL);
        }
    }
    private IReadOnlyList<ulong> _pollBoundaries = [];
    private bool _completePollScale;
    public IReadOnlyList<ulong> PollBoundaries
    {
        get => _pollBoundaries;
        set
        {
            if (ReferenceEquals(_pollBoundaries, value)) return;
            _pollBoundaries = value;
            // Newly authored groups have no polls yet. Use group spacing until
            // replay records them, so they remain visible and selectable.
            if (value is PollBoundarySnapshot snapshot) { _completePollScale = snapshot.IsComplete; return; }
            _completePollScale = value.Count > 1;
            for (var i = 1; i < value.Count && _completePollScale; i++)
                _completePollScale = value[i] > value[i - 1];
        }
    }
    private bool HasPollScale => _completePollScale && PollBoundaries.Count == InputCount + 1;
    public TimelineTag? SelectedTag { get; private set; }
    public event Action<ulong>? TagRequested;
    public int SelectedFrame { get; private set; }
    public int SelectionEnd { get; private set; } = 1;
    public string? SelectedTake { get; private set; }
    public StateMarker? SelectedMarker { get; private set; }
    public int? ContextFrame { get; private set; }
    public string? ContextTake { get; private set; }
    public event Action? SelectionChanged;
    public event Action<int>? MoveSelectionRequested;
    public event Action? ViewportChanged;
    private int MaximumCursorFrame => Math.Max(InputCount, _maximumTagFrame);
    private int MaximumTakeEnd => _maximumTakeEnd;
    public int MaximumFirstFrame => Math.Max(0, Math.Max(Math.Max(MaximumCursorFrame, MaximumTakeEnd), (int)Math.Min(Position, int.MaxValue)) - VisibleFrames + Math.Max(1, VisibleFrames / 5));
    private int? _dragAnchor;
    private int? _moveAnchor;
    private double _moveStartX;
    private int _moveDelta;
    private bool _moveStarted;
    private IPointer? _selectionPointer;
    private Rect? _effectiveViewport;
    private readonly record struct ViewState(int First, int Visible, int Count, ulong Position, bool Current, bool Trial,
        IReadOnlyList<ControllerState> Inputs, IReadOnlyList<InputTake> Takes, IReadOnlyList<TimelineSection> Sections,
        IReadOnlyList<StateMarker> Markers, IReadOnlySet<string> Experiments, IReadOnlyList<TimelineTag> Tags,
        IReadOnlyList<ulong> Polls, InputTimingSnapshot Timing, long Offset, IReadOnlyList<MemoryEdit> MemoryEdits);
    private ViewState? _lastViewState;
    public TimelineView()
    {
        ClipToBounds = true; Focusable = true; Height = 120;
        EffectiveViewportChanged += (_, e) =>
        {
            if (_effectiveViewport == e.EffectiveViewport) return;
            _effectiveViewport = e.EffectiveViewport; RefreshSelectionTip(); InvalidateVisual();
        };
    }
    public void SetSelection(int start, int end, string? take = null)
    {
        CancelSelectionDrag();
        ChangeSelection(start, end, take);
    }
    private void ChangeSelection(int start, int end, string? take)
    {
        var candidate = Takes.FirstOrDefault(t => t.Id == take);
        var candidateEnd = candidate == null ? InputCount : candidate.Start + candidate.Inputs.Length;
        SelectedFrame = Math.Clamp(start, 0, candidate == null ? MaximumCursorFrame : candidateEnd - 1);
        SelectionEnd = Math.Clamp(end, SelectedFrame + 1, Math.Max(candidateEnd, SelectedFrame + 1));
        SelectedTake = take; RefreshSelectionTip(); InvalidateVisual();
    }
    public void Update()
    {
        FirstFrame = Math.Clamp(FirstFrame, 0, MaximumFirstFrame);
        var state = new ViewState(FirstFrame, VisibleFrames, InputCount, Position, Current, TrialPlayback,
            Inputs, Takes, Sections, Markers, ExperimentStateIds, Tags, PollBoundaries, InputTiming, TimelineOffsetMilliseconds, MemoryEdits);
        if (_lastViewState == state) return;
        _lastViewState = state;
        Height = RulerHeight + LaneHeight * (Takes.Count + 1) + 8;
        RefreshSelectionTip(); InvalidateVisual(); ViewportChanged?.Invoke();
    }
    public void PanTo(int frame) { FirstFrame = Math.Clamp(frame, 0, MaximumFirstFrame); Update(); }
    public void Zoom(double multiplier)
    {
        var center = FirstFrame + VisibleFrames / 2;
        VisibleFrames = Math.Clamp((int)(VisibleFrames * multiplier), 10, 100000);
        PanTo(center - VisibleFrames / 2);
    }
    public void MoveCursor(int direction)
    {
        var take = Takes.FirstOrDefault(t => t.Id == SelectedTake);
        var first = take?.Start ?? 0;
        var last = take == null ? MaximumCursorFrame : take.Start + take.Inputs.Length - 1;
        var frame = Math.Clamp(SelectedFrame + Math.Sign(direction), first, Math.Max(first, last));
        SetSelection(frame, frame + 1, SelectedTake);
        SelectedMarker = null; SelectedTag = null; SelectedMemoryEditPosition = null; ContextFrame = null;
        RevealFrame(frame);
        SelectionChanged?.Invoke();
    }
    public void RevealPosition() => RevealFrame((int)Math.Min(Position, int.MaxValue));
    public void RevealSelection() => RevealFrame(SelectedFrame);
    public void SelectTag(string id)
    {
        var tag = Tags.FirstOrDefault(t => t.Id == id);
        if (tag == null) return;
        SelectedTag = tag; SelectedMarker = null; SelectedMemoryEditPosition = null; ContextFrame = null; ContextTake = null;
        var frame = (int)Math.Min(tag.Position, int.MaxValue - 1UL);
        SetSelection(frame, frame + 1);
        RevealFrame(frame);
        SelectionChanged?.Invoke();
    }
    public bool CancelSelectionDrag()
    {
        var moving = _moveAnchor != null || _resizeEdge != 0;
        _resizeEdge = 0;
        _moveAnchor = null; _moveDelta = 0; _moveStarted = false;
        var pointer = _selectionPointer; _selectionPointer = null;
        pointer?.Capture(null);
        if (moving) InvalidateVisual();
        return moving;
    }
    private void RevealFrame(int frame)
    {
        var margin = Math.Max(1, VisibleFrames / 10);
        if (frame < FirstFrame + margin || frame >= FirstFrame + VisibleFrames - margin)
            PanTo(frame - VisibleFrames / 2);
    }
    public void RefreshMarker()
    {
        if (SelectedMarker != null) SelectedMarker = Markers.FirstOrDefault(m => m.Id == SelectedMarker.Id);
        if (SelectedTag != null) SelectedTag = Tags.FirstOrDefault(tag => tag.Id == SelectedTag.Id);
    }
    private (Rect Box, FormattedText Text) TagLayout(TimelineTag tag)
    {
        var text = new FormattedText(tag.Name, System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, Font, 12, StudioTheme.Brush(ThemeColor.TimelineTagText));
        var width = Math.Min(Math.Min(160, Math.Max(1, Bounds.Width - LabelWidth)), Math.Max(36, text.Width + 12));
        text.MaxTextWidth = Math.Max(1, width - 12);
        text.MaxLineCount = 1;
        text.Trimming = TextTrimming.CharacterEllipsis;
        var box = new Rect(Math.Clamp(X(tag.Position) - width / 2, LabelWidth, Math.Max(LabelWidth, Bounds.Width - width)), 33, width, 20);
        return (box, text);
    }
    private TimelineTag? TagAt(Point point) => Tags.LastOrDefault(tag => X(tag.Position) >= LabelWidth && X(tag.Position) <= Bounds.Width && TagLayout(tag).Box.Contains(point));
    private double Boundary(double group)
    {
        if (!HasPollScale) return group;
        if (group >= 0 && group <= InputCount) return PollBoundaries[(int)group];
        // Only blank viewport space is extrapolated; never label unrecorded poll positions.
        var average = Math.Max(1, (double)PollBoundaries[^1] / Math.Max(1, InputCount));
        return group < 0 ? group * average : PollBoundaries[^1] + (group - InputCount) * average;
    }
    private double X(double frame) => LabelWidth + (Boundary(frame) - Boundary(FirstFrame)) *
        Math.Max(1, Bounds.Width - LabelWidth) / Math.Max(1, Boundary(FirstFrame + VisibleFrames) - Boundary(FirstFrame));
    private int Frame(double x, bool allowBeyondEnd = false)
    {
        if (!HasPollScale) return (int)Math.Clamp(FirstFrame + Math.Floor((x - LabelWidth) * VisibleFrames / Math.Max(1, Bounds.Width - LabelWidth)), 0, allowBeyondEnd ? int.MaxValue - 1 : InputCount);
        var poll = Boundary(FirstFrame) + (x - LabelWidth) * (Boundary(FirstFrame + VisibleFrames) - Boundary(FirstFrame)) / Math.Max(1, Bounds.Width - LabelWidth);
        if (allowBeyondEnd && poll > PollBoundaries[^1])
            return (int)Math.Min(int.MaxValue - 1, InputCount + Math.Floor((poll - PollBoundaries[^1]) / Math.Max(1, (double)PollBoundaries[^1] / InputCount)));
        var low = 0; var high = InputCount;
        while (low < high) { var mid = low + (high - low + 1) / 2; if (PollBoundaries[mid] <= poll) low = mid; else high = mid - 1; }
        return low;
    }
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        StudioTheme.Changed += InvalidateVisual;
        InvalidateVisual();
    }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _effectiveViewport = null;
        StudioTheme.Changed -= InvalidateVisual;
        base.OnDetachedFromVisualTree(e);
    }
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var viewport = (_effectiveViewport ?? new Rect(Bounds.Size)).Intersect(new Rect(Bounds.Size));
        if (viewport.Width <= 0 || viewport.Height <= 0) return;
        using var visibleClip = context.PushClip(viewport);
        context.FillRectangle(BackgroundBrush, viewport);
        var firstRow = Math.Max(0, (int)Math.Floor((viewport.Top - RulerHeight) / LaneHeight));
        var lastRow = Math.Min(Takes.Count, (int)Math.Ceiling((viewport.Bottom - RulerHeight) / LaneHeight) - 1);
        void Text(string value, double x, double y, IBrush? brush = null) => context.DrawText(new FormattedText(value, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Font, 12, brush ?? TextBrush), new Point(x, y));
        if (viewport.Top < RulerHeight)
        {
            Text("Saved states", 8, 8, MutedBrush);
            if (Tags.Count > 0) Text("Tags", 8, 36, MutedBrush);
        }
        var tick = Math.Max(1, (int)Math.Pow(10, Math.Floor(Math.Log10(Math.Max(1, VisibleFrames / 6d)))));
        while (VisibleFrames / tick > 10) tick *= 2;
        using (context.PushClip(new Rect(LabelWidth, 0, Math.Max(0, Bounds.Width - LabelWidth), Bounds.Height)))
        {
            for (var f = FirstFrame / tick * tick; f <= FirstFrame + VisibleFrames; f += tick)
            { var x = X(f); context.DrawLine(new Pen(GridBrush), new(x, FrameTop), new(x, Bounds.Height)); if (viewport.Top < RulerHeight && (!HasPollScale || f <= InputCount)) Text(Boundary(f).ToString("N0"), x + 3, FrameTop + 4, MutedBrush); }
            foreach (var tag in viewport.Top < RulerHeight ? Tags : [])
            {
                var x = X(tag.Position); if (x < LabelWidth || x > Bounds.Width) continue;
                var (box, text) = TagLayout(tag);
                context.DrawRectangle(TagBrush, new Pen(TagBrush), box, 3, 3);
                using (context.PushClip(box.Deflate(1)))
                    context.DrawText(text, new Point(box.Center.X - text.Width / 2, box.Center.Y - text.Height / 2));
                context.DrawLine(new Pen(TagBrush), new(x, box.Bottom), new(x, FrameTop));
                context.DrawLine(new Pen(TagBrush), new(x - 3, FrameTop - 4), new(x, FrameTop));
                context.DrawLine(new Pen(TagBrush), new(x + 3, FrameTop - 4), new(x, FrameTop));
            }
            for (var row = firstRow; row <= lastRow; row++)
            {
                var y = RulerHeight + row * LaneHeight;
                context.DrawLine(new Pen(GridBrush), new(LabelWidth, y + LaneHeight), new(Bounds.Width, y + LaneHeight));
                void Clip(int start, int length, string name, bool active, IReadOnlyList<ControllerState> inputs, int inputStart = 0)
                {
                    var left = Math.Max(LabelWidth, X(start)); var right = Math.Min(Bounds.Width, X(start + length));
                    if (right <= left) return;
                    var rect = new Rect(left + 1, y + 5, Math.Max(0, right - left - 2), LaneHeight - 10);
                    context.DrawRectangle(active ? ActiveBrush : BackgroundBrush, new Pen(active ? CursorBrush : MutedBrush), rect, 3, 3);
                    // One logical clip/background, with independently culled shading
                    // chunks. Never inspect the offscreen duration of the active row.
                    var end = Math.Min(Math.Min(start + length, inputStart + inputs.Count), FirstFrame + VisibleFrames + 1);
                    if (rect.Width > 2)
                    using (context.PushClip(rect.Deflate(1)))
                    {
                        var first = Math.Max(Math.Max(start, inputStart), FirstFrame);
                        for (var chunk = first / RenderChunkFrames; chunk <= (end - 1) / RenderChunkFrames && first < end; chunk++)
                        {
                            var chunkEnd = (int)Math.Min(end, ((long)chunk + 1) * RenderChunkFrames);
                            for (var frame = Math.Max(first, chunk * RenderChunkFrames); frame < chunkEnd;)
                            {
                                if (inputs[frame - inputStart] == ControllerState.Neutral) { frame++; continue; }
                                var runStart = frame++;
                                while (frame < chunkEnd && inputs[frame - inputStart] != ControllerState.Neutral) frame++;
                                var x = X(runStart);
                                context.FillRectangle(InputBrush, new Rect(x, rect.Y, Math.Max(1, X(frame) - x), rect.Height));
                            }
                        }
                    }
                    using (context.PushClip(rect)) Text(name, left + 7, y + 10, active ? StudioTheme.Brush(ThemeColor.TimelineClipText) : MutedBrush);
                }
                if (row == 0)
                {
                    Clip(0, InputCount, "Active playback", true, Inputs);
                    foreach (var s in Sections) Clip(s.Start, s.Length, s.Name, true, Inputs);
                    if (TrialPlayback)
                        foreach (var take in Takes.Reverse().Where(t => t.IsActive)) Clip(take.Start, take.Inputs.Length, "Trial · " + take.Name, true, take.Inputs, take.Start);
                }
                else { var t = Takes[row - 1]; Clip(t.Start, t.Inputs.Length, t.IsActive ? "Active · " + t.Name : t.Name, t.IsActive, t.Inputs, t.Start); }
            }
            var selectedRow = 0;
            if (SelectedTake != null)
                for (var i = 0; i < Takes.Count; i++) if (Takes[i].Id == SelectedTake) { selectedRow = i + 1; break; }
            context.FillRectangle(SelectedBrush, new Rect(X(SelectedFrame), RulerHeight + selectedRow * LaneHeight, Math.Max(3, X(SelectionEnd) - X(SelectedFrame)), LaneHeight));
            if (_moveStarted)
            {
                var left = X(SelectedFrame + _moveDelta); var right = X(SelectionEnd + _moveDelta);
                context.DrawRectangle(SelectedBrush, new Pen(SelectionBrush, 2),
                    new Rect(left, RulerHeight + selectedRow * LaneHeight, Math.Max(3, right - left), LaneHeight));
            }
            foreach (var marker in viewport.Top < 30 ? Markers : [])
            {
                var x = X(marker.Position);
                if (x < LabelWidth - 5 || x > Bounds.Width + 5) continue;
                var brush = !marker.Valid ? InvalidBrush : ExperimentStateIds.Contains(marker.Id)
                    ? StudioTheme.Brush(ThemeColor.TimelineExperiment)
                    : marker.Automatic ? StudioTheme.Brush(ThemeColor.TimelineAutomatic) : SelectionBrush;
                var y = StateMarkerY(marker);
                if (marker.Automatic) context.DrawEllipse(null, new Pen(brush, 2), new(x, y), 4, 4);
                else context.DrawRectangle(brush, null, new Rect(x - 4, y - 8, 8, 15), 1, 1);
                if (!marker.Valid) context.DrawLine(new Pen(BackgroundBrush, 2), new(x - 5, y - 6), new(x + 5, y + 6));
            }
            if (viewport.Top < 31) DrawMemoryEdits(context);
            context.DrawLine(new Pen(Current ? CursorBrush : InvalidBrush, 2), new(X(Position), FrameTop), new(X(Position), Bounds.Height));
            context.DrawLine(new Pen(SelectionBrush, 2, new DashStyle([3, 3], 0)), new(X(SelectedFrame), FrameTop), new(X(SelectedFrame), Bounds.Height));
        }
        if (firstRow == 0 && lastRow >= 0) Text(TrialPlayback ? "▶ Trial" : "▶ Active", 8, RulerHeight + 10, CursorBrush);
        for (var i = Math.Max(0, firstRow - 1); i < lastRow; i++)
        {
            using (context.PushClip(new Rect(0, 0, LabelWidth - 4, Bounds.Height))) Text((Takes[i].IsActive ? "✓ " : "") + Takes[i].Name, 8, RulerHeight + (i + 1) * LaneHeight + 10, Takes[i].Id == SelectedTake ? SelectionBrush : Takes[i].IsActive ? CursorBrush : MutedBrush);
        }
        DrawSelectionReadout(context);
    }
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var properties = e.GetCurrentPoint(this).Properties;
        var point = e.GetPosition(this);
        // The readout is drawn above tags; clicks must not select a hidden tag.
        if (GetSelectionReadout() is { } readout && readout.Box.Contains(point))
        { e.Handled = true; return; }
        if (properties.IsMiddleButtonPressed && point.X >= LabelWidth)
        { TagRequested?.Invoke((ulong)Frame(point.X)); e.Handled = true; return; }
        if (!properties.IsLeftButtonPressed && !properties.IsRightButtonPressed) return;
        SelectedMemoryEditPosition = null;
        if (MemoryEditsAt(point) is { } memoryEdits)
        {
            Focus(); CancelSelectionDrag(); _dragAnchor = null;
            SelectedMemoryEditPosition = memoryEdits.Key;
            SelectedMarker = null; SelectedTag = null; ContextTake = null;
            ContextFrame = checked((int)memoryEdits.Key);
            if (properties.IsLeftButtonPressed)
            {
                SetSelection(ContextFrame.Value, ContextFrame.Value + 1);
                SelectionChanged?.Invoke();
                MemoryEditsRequested?.Invoke(memoryEdits.Key, point);
                e.Handled = true;
            }
            return;
        }
        var edge = properties.IsLeftButtonPressed && !e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? SelectionEdgeAt(point) : 0;
        if (edge != 0)
        {
            Focus(); _dragAnchor = null; CancelSelectionDrag();
            _resizeEdge = edge; _resizeFixedBoundary = edge < 0 ? SelectionEnd : SelectedFrame;
            _selectionPointer = e.Pointer; e.Pointer.Capture(this); e.Handled = true; return;
        }
        if (properties.IsRightButtonPressed) { ContextFrame = null; ContextTake = null; }
        SelectedTag = TagAt(point);
        if (SelectedTag != null)
        {
            SelectedMarker = null; ContextFrame = null; _dragAnchor = null;
            if (properties.IsLeftButtonPressed)
            {
                Focus();
                var tagFrame = (int)Math.Min(SelectedTag.Position, int.MaxValue - 1UL);
                SetSelection(tagFrame, tagFrame + 1);
                RevealFrame(tagFrame);
                SelectionChanged?.Invoke();
                e.Handled = true;
            }
            return;
        }
        var rightClick = properties.IsRightButtonPressed;
        Focus(); var row = (int)((point.Y - RulerHeight) / LaneHeight);
        SelectedMarker = point.X >= LabelWidth && point.Y < 30 ? Markers.Where(m => Math.Abs(X(m.Position) - point.X) < 9 && Math.Abs(StateMarkerY(m) - point.Y) < 9).OrderBy(m => Math.Abs(X(m.Position) - point.X)).ThenByDescending(m => m.Valid).FirstOrDefault() : null;
        var frame = SelectedMarker == null ? Frame(point.X, allowBeyondEnd: point.Y >= RulerHeight && row > 0 && row <= Takes.Count) : (int)SelectedMarker.Position;
        if (rightClick)
        {
            _dragAnchor = null;
            if (point.Y >= RulerHeight && row > 0 && row <= Takes.Count) ContextTake = Takes[row - 1].Id;
            if (point.X >= LabelWidth && (point.Y < RulerHeight || row == 0)) ContextFrame = frame;
            return;
        }
        // Ruler and saved-state clicks select active playback, just like tags.
        // Retaining a previous take here makes the next arrow snap to its bounds.
        var clickedTake = point.Y >= RulerHeight && row > 0 && row <= Takes.Count ? Takes[row - 1].Id : null;
        var takeBounds = Takes.FirstOrDefault(t => t.Id == SelectedTake);
        var selectionInsideLane = SelectedFrame >= (takeBounds?.Start ?? 0) &&
            SelectionEnd <= (takeBounds == null ? InputCount : takeBounds.Start + takeBounds.Inputs.Length);
        if (point.X >= LabelWidth && point.Y >= RulerHeight && row <= Takes.Count &&
            clickedTake == SelectedTake && selectionInsideLane && frame >= SelectedFrame && frame < SelectionEnd &&
            (SelectionEnd - SelectedFrame > 1 || e.KeyModifiers.HasFlag(KeyModifiers.Shift)))
        {
            _dragAnchor = null; _moveAnchor = frame; _moveStartX = point.X; _moveDelta = 0; _moveStarted = false;
            _selectionPointer = e.Pointer; e.Pointer.Capture(this); e.Handled = true; return;
        }
        SelectedTake = clickedTake;
        if (point.X < LabelWidth && SelectedTake != null) { var take = Takes.Single(t => t.Id == SelectedTake); SetSelection(take.Start, take.Start + take.Inputs.Length, SelectedTake); }
        else { _dragAnchor = frame; SetSelection(frame, frame + 1, SelectedTake); if (!rightClick) e.Pointer.Capture(this); }
        SelectionChanged?.Invoke(); e.Handled = !rightClick;
    }
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        _selectionHover = e.GetPosition(this);
        RefreshSelectionTip();
        if (_resizeEdge != 0)
        {
            ResizeSelection(e.GetPosition(this).X); e.Handled = true; return;
        }
        Cursor = SelectionEdgeAt(e.GetPosition(this)) != 0 && !e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? ResizeCursor : null;
        if (_moveAnchor is { } moveAnchor)
        {
            var point = e.GetPosition(this);
            if (!_moveStarted && Math.Abs(point.X - _moveStartX) < 4) return;
            _moveStarted = true;
            var take = Takes.FirstOrDefault(t => t.Id == SelectedTake);
            var first = take?.Start ?? 0;
            var end = take == null ? int.MaxValue : first + take.Inputs.Length;
            _moveDelta = Math.Clamp(Frame(point.X, allowBeyondEnd: true) - moveAnchor, first - SelectedFrame, end - SelectionEnd);
            InvalidateVisual(); e.Handled = true; return;
        }
        if (_dragAnchor is not { } anchor) return;
        var frame = Frame(e.GetPosition(this).X, allowBeyondEnd: SelectedTake != null); SetSelection(Math.Min(anchor, frame), Math.Max(anchor, frame) + 1, SelectedTake); SelectionChanged?.Invoke();
    }
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        if (_resizeEdge != 0)
        {
            ResizeSelection(e.GetPosition(this).X); CancelSelectionDrag();
            e.Handled = true; base.OnPointerReleased(e); return;
        }
        var anchor = _moveAnchor; var delta = _moveDelta; var moved = _moveStarted;
        CancelSelectionDrag(); _dragAnchor = null; e.Pointer.Capture(null);
        if (anchor != null)
        {
            if (moved) { if (delta != 0) MoveSelectionRequested?.Invoke(delta); }
            else { SetSelection(anchor.Value, anchor.Value + 1, SelectedTake); SelectionChanged?.Invoke(); }
            e.Handled = true;
        }
        base.OnPointerReleased(e);
    }
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        _dragAnchor = null; _selectionPointer = null; CancelSelectionDrag();
        base.OnPointerCaptureLost(e);
    }
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control)) Zoom(e.Delta.Y > 0 ? .8 : 1.25);
        else PanTo(FirstFrame - (int)((e.Delta.X != 0 ? e.Delta.X : e.Delta.Y) * Math.Max(1, VisibleFrames / 5)));
        e.Handled = true;
    }
}
