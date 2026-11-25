using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using TasStudio.Emulation;

namespace TasStudio.App;

public sealed partial class TimelineView
{
    public InputTimingSnapshot InputTiming { get; set; } = InputTimingSnapshot.Empty;
    public long TimelineOffsetMilliseconds { get; set; }
    private SelectionTimingDisplay _timingDisplay = SelectionTimingDisplay.MultipleOnly;
    public SelectionTimingDisplay TimingDisplay
    {
        get => _timingDisplay;
        set
        {
            if (_timingDisplay == value) return;
            _timingDisplay = value;
            RefreshSelectionTip(); InvalidateVisual();
        }
    }
    private int _resizeEdge, _resizeFixedBoundary;
    private Point? _selectionHover;
    private static readonly Cursor ResizeCursor = new(StandardCursorType.SizeWestEast);

    internal sealed record SelectionReadout(Rect Box, Rect Range, string Duration, string Groups, string Detail);

    private int SelectionRow()
    {
        if (SelectedTake == null) return 0;
        for (var i = 0; i < Takes.Count; i++) if (Takes[i].Id == SelectedTake) return i + 1;
        return -1;
    }

    private static FormattedText SelectionText(string value, double size, IBrush brush) =>
        new(value, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Font, size, brush);

    private Rect? GetSelectionRange()
    {
        var row = SelectionRow();
        if (row < 0 || SelectionEnd <= SelectedFrame) return null;
        var first = row == 0 ? 0 : Takes[row - 1].Start;
        var end = row == 0 ? InputCount : first + Takes[row - 1].Inputs.Length;
        if (SelectedFrame < first || SelectionEnd > end) return null;
        var viewport = (_effectiveViewport ?? new Rect(Bounds.Size)).Intersect(new Rect(Bounds.Size));
        var range = new Rect(X(SelectedFrame), RulerHeight + row * LaneHeight,
            Math.Max(3, X(SelectionEnd) - X(SelectedFrame)), LaneHeight);
        if (range.Bottom <= viewport.Top || range.Top >= viewport.Bottom ||
            range.Right <= Math.Max(LabelWidth, viewport.Left) || range.Left >= viewport.Right)
            return null;
        return range;
    }

    internal SelectionReadout? GetSelectionReadout()
    {
        if (TimingDisplay == SelectionTimingDisplay.Never ||
            (TimingDisplay == SelectionTimingDisplay.MultipleOnly && SelectionEnd - SelectedFrame < 2) ||
            GetSelectionRange() is not { } range) return null;
        var viewport = (_effectiveViewport ?? new Rect(Bounds.Size)).Intersect(new Rect(Bounds.Size));
        var left = Math.Max(LabelWidth, viewport.Left) + 4;
        var right = viewport.Right - 4;
        if (right - left < 40) return null;

        var timing = InputTiming.Measure(SelectedFrame, SelectionEnd);
        // Candidate inputs have not necessarily been executed. Their placement on
        // the active timeline supplies an estimate, not a measured take duration.
        var estimate = SelectedTake != null || timing.IsDurationEstimated;
        var duration = FormattableString.Invariant($"{(estimate ? "≈ " : "")}{timing.DurationSeconds:0.000} s");
        var groups = (SelectionEnd - SelectedFrame).ToString("N0", CultureInfo.InvariantCulture) +
            (SelectionEnd - SelectedFrame == 1 ? " group" : " groups");
        string Time(double seconds, bool estimated) => (estimated || SelectedTake != null ? "≈ " : "") +
            MainWindow.FormatTimelineTime(seconds, TimelineOffsetMilliseconds);
        var detail = $"Duration: {duration} · {groups}\n" +
            $"Start: {Time(timing.StartSeconds, timing.IsStartEstimated)}\n" +
            $"End:   {Time(timing.EndSeconds, timing.IsEndEstimated)}\n" +
            (SelectedTake != null ? "Estimate based on Active playback timing." : estimate || timing.IsEndEstimated
                ? "Estimated where timing is unrecorded or inputs have changed."
                : "Recorded timing; end is the boundary after the selection.");
        var durationWidth = SelectionText(duration, 13, TextBrush).Width;
        var groupWidth = SelectionText(groups, 11, MutedBrush).Width;
        var available = right - left;
        // Keep the useful number intact in narrow panels; omit the secondary count first.
        if (durationWidth + groupWidth + 40 > available) { groups = ""; groupWidth = 0; }
        var width = Math.Min(available, durationWidth + (groups.Length > 0 ? groupWidth + 18 : 0) + 20);
        var center = (Math.Max(left, range.Left) + Math.Min(right, range.Right)) / 2;
        // Float over the tags band (or the top header when there are no tags).
        // The selected take's row never allocates space for this readout.
        var top = Tags.Count > 0 ? 29 : 3;
        var box = new Rect(Math.Clamp(center - width / 2, left, right - width),
            Math.Max(viewport.Top + 3, top), width, 28);
        return new(box, range, duration, groups, detail);
    }

    private void DrawSelectionReadout(DrawingContext context)
    {
        if (GetSelectionRange() is not { } range) return;
        var readout = GetSelectionReadout();
        var viewport = _effectiveViewport ?? new Rect(Bounds.Size);
        using var clip = context.PushClip(new Rect(LabelWidth, 0, Math.Max(0, Bounds.Width - LabelWidth), Bounds.Height));
        var left = Math.Max(Math.Max(LabelWidth, viewport.Left), range.Left);
        var right = Math.Min(Math.Min(Bounds.Width, viewport.Right), range.Right);
        var pen = new Pen(SelectionBrush);
        var bracketY = readout == null ? range.Top : range.Top - 4;
        if (readout != null) context.DrawLine(pen, new(left, bracketY), new(right, bracketY));
        foreach (var x in new[] { range.Left, range.Right })
        {
            context.DrawLine(pen, new(x, bracketY), new(x, range.Bottom));
            if (SelectionEnd - SelectedFrame > 1)
                context.DrawRectangle(StudioTheme.Brush(ThemeColor.Panel), pen,
                    new Rect(x - 2, range.Center.Y - 7, 4, 14), 2, 2);
        }
        if (readout == null) return;
        context.DrawRectangle(StudioTheme.Brush(ThemeColor.Panel), new Pen(StudioTheme.Brush(ThemeColor.Border)), readout.Box, 5, 5);
        using (context.PushClip(readout.Box.Deflate(4)))
        {
            var duration = SelectionText(readout.Duration, 13, TextBrush);
            context.DrawText(duration, new(readout.Box.Left + 10, readout.Box.Center.Y - duration.Height / 2));
            if (readout.Groups.Length > 0)
            {
                var divider = readout.Box.Left + duration.Width + 19;
                context.DrawLine(new Pen(GridBrush), new(divider, readout.Box.Top + 7), new(divider, readout.Box.Bottom - 7));
                var groups = SelectionText(readout.Groups, 11, MutedBrush);
                context.DrawText(groups, new(divider + 9, readout.Box.Center.Y - groups.Height / 2));
            }
        }
    }

    private int SelectionEdgeAt(Point point)
    {
        if (SelectionEnd - SelectedFrame <= 1 || GetSelectionRange() is not { } range ||
            point.X < LabelWidth || point.Y < range.Top || point.Y > range.Bottom) return 0;
        var start = Math.Abs(point.X - range.Left);
        var end = Math.Abs(point.X - range.Right);
        return Math.Min(start, end) > 4 ? 0 : start <= end ? -1 : 1;
    }

    private void ResizeSelection(double x)
    {
        var frame = Frame(x, allowBeyondEnd: SelectedTake != null);
        if (x > (X(frame) + X(frame + 1d)) / 2) frame++;
        var row = SelectionRow();
        if (row < 0) { CancelSelectionDrag(); return; }
        var first = row == 0 ? 0 : Takes[row - 1].Start;
        var last = row == 0 ? InputCount : first + Takes[row - 1].Inputs.Length;
        if (_resizeEdge < 0) ChangeSelection(Math.Clamp(frame, first, _resizeFixedBoundary - 1), _resizeFixedBoundary, SelectedTake);
        else ChangeSelection(_resizeFixedBoundary, Math.Clamp(frame, _resizeFixedBoundary + 1, last), SelectedTake);
        SelectionChanged?.Invoke();
    }

    private void RefreshSelectionTip()
    {
        var readout = GetSelectionReadout();
        var tip = _selectionHover is { } point ?
            readout != null && readout.Box.Contains(point) ? readout.Detail : MemoryEditTip(point) : null;
        if (!Equals(ToolTip.GetTip(this), tip)) ToolTip.SetTip(this, tip);
        // The pointer is already inside this custom-drawn control when it reaches
        // the badge, so there is no child PointerEntered event to open the tip.
        ToolTip.SetIsOpen(this, tip != null);
        var help = readout?.Detail ?? "";
        if (AutomationProperties.GetHelpText(this) != help) AutomationProperties.SetHelpText(this, help);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        _selectionHover = null; RefreshSelectionTip(); Cursor = null;
        base.OnPointerExited(e);
    }
}
