using Avalonia;
using Avalonia.Media;
using TasStudio.Emulation;

namespace TasStudio.App;

public sealed partial class TimelineView
{
    private static readonly IBrush MemoryEditBrush = new SolidColorBrush(Color.Parse("#E65353"));
    private IReadOnlyList<MemoryEdit> _memoryEdits = [];
    private IGrouping<ulong, MemoryEdit>[] _memoryEditGroups = [];
    public IReadOnlyList<MemoryEdit> MemoryEdits
    {
        get => _memoryEdits;
        set
        {
            if (ReferenceEquals(_memoryEdits, value)) return;
            _memoryEdits = value;
            _memoryEditGroups = value.GroupBy(e => e.Position).OrderBy(g => g.Key).ToArray();
            if (!_memoryEditGroups.Any(g => g.Key == SelectedMemoryEditPosition)) SelectedMemoryEditPosition = null;
        }
    }
    public ulong? SelectedMemoryEditPosition { get; private set; }
    public event Action<ulong, Point>? MemoryEditsRequested;

    private double StateMarkerY(StateMarker marker) =>
        _memoryEditGroups.Any(g => Math.Abs(X(g.Key) - X(marker.Position)) < 12) ? 9 : 18;
    private Point MemoryEditPoint(ulong position) => new(X(position),
        Markers.Any(m => Math.Abs(X(m.Position) - X(position)) < 12) ? 24 : 18);

    private IGrouping<ulong, MemoryEdit>? MemoryEditsAt(Point point) =>
        point.X < LabelWidth || point.Y < 0 || point.Y >= 31 ? null : _memoryEditGroups
            .Where(g => { var center = MemoryEditPoint(g.Key); return Math.Abs(center.X - point.X) + Math.Abs(center.Y - point.Y) <= 8; })
            .OrderBy(g => Math.Abs(X(g.Key) - point.X)).FirstOrDefault();

    private string? MemoryEditTip(Point point)
    {
        if (MemoryEditsAt(point) is not { } group) return null;
        return $"{group.Count()} memory edit{(group.Count() == 1 ? "" : "s")} at group {group.Key:N0}\n" +
            string.Join("\n", group.Take(8).Select(e => $"0x{e.Address:X8}: {PreviewBytes(e.HexBytes)}")) +
            (group.Count() > 8 ? "\n…" : "") + "\nClick to inspect or clear.";
    }

    internal static string PreviewBytes(string hex) => string.Join(" ",
        Enumerable.Range(0, Math.Min(hex.Length / 2, 16)).Select(i => hex.Substring(i * 2, 2))) + (hex.Length > 32 ? " …" : "");

    private void DrawMemoryEdits(DrawingContext context)
    {
        foreach (var group in _memoryEditGroups)
        {
            var p = MemoryEditPoint(group.Key);
            if (p.X < LabelWidth - 6 || p.X > Bounds.Width + 6) continue;
            var diamond = new StreamGeometry();
            using (var shape = diamond.Open())
            {
                shape.BeginFigure(new Point(p.X, p.Y - 5), true);
                shape.LineTo(new Point(p.X + 5, p.Y)); shape.LineTo(new Point(p.X, p.Y + 5));
                shape.LineTo(new Point(p.X - 5, p.Y)); shape.EndFigure(true);
            }
            context.DrawGeometry(MemoryEditBrush, new Pen(BackgroundBrush), diamond);
            if (group.Count() > 1)
                context.DrawText(new FormattedText(group.Count().ToString(), System.Globalization.CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, Font, 10, MemoryEditBrush), new Point(p.X + 7, p.Y - 7));
        }
    }
}
