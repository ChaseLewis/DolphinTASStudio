using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace TasStudio.App;

/// <summary>A byte-coordinate stick editor. Positive Y points upward, matching GameCube inputs.</summary>
public sealed class StickPad : Control
{
    private byte? _x = 128, _y = 128;
    private bool _dragging;
    private double? _normalizedRadius;
    public double? NormalizedRadius
    {
        get => _normalizedRadius;
        set
        {
            if (value is { } radius && (!double.IsFinite(radius) || radius < 0 || radius > 1)) throw new ArgumentOutOfRangeException(nameof(value));
            _normalizedRadius = value; InvalidateVisual();
        }
    }
    public bool IsDragging => _dragging;
    public event Action? DragStarted;
    public event Action<byte, byte>? PositionChanged;
    public event Action? PositionCommitted;
    public StickPad() { MinHeight = 96; Focusable = true; }
    protected override Size MeasureOverride(Size availableSize)
    {
        var side = double.IsFinite(availableSize.Width) ? availableSize.Width : 160;
        side = Math.Min(side, Math.Min(availableSize.Height, MaxHeight));
        return new Size(side, side);
    }
    public void SetPosition(byte? x, byte? y) { _x = x; _y = y; InvalidateVisual(); }
    public static (byte X, byte Y) Coordinates(Point point, Size size, double? radius = null)
    {
        var extent = Math.Max(1, Math.Min(size.Width, size.Height) - 14);
        var left = (size.Width - extent) / 2; var top = (size.Height - extent) / 2;
        if (radius is { } locked)
            return NormalizeVector((point.X - size.Width / 2) / (extent / 2), (size.Height / 2 - point.Y) / (extent / 2), locked);
        return ((byte)Math.Round(Math.Clamp((point.X - left) / extent, 0, 1) * 255),
            (byte)Math.Round((1 - Math.Clamp((point.Y - top) / extent, 0, 1)) * 255));
    }
    // Neutral is exactly 128. Each signed half maps to its full byte range (0..128..255).
    private static double Unit(byte value) => (value - 128d) / (value < 128 ? 128 : 127);
    public static (byte X, byte Y) Normalize(byte x, byte y, double radius) => NormalizeVector(Unit(x), Unit(y), radius);
    private static (byte X, byte Y) NormalizeVector(double x, double y, double radius)
    {
        if (!double.IsFinite(radius) || radius < 0 || radius > 1) throw new ArgumentOutOfRangeException(nameof(radius));
        var length = Math.Sqrt(x * x + y * y);
        // A centered stick has no direction. Center and radius zero are always neutral.
        if (length < 1e-12 || radius == 0) return (128, 128);
        static byte Encode(double value) => (byte)Math.Clamp(Math.Round(128 + value * (value < 0 ? 128 : 127)), 0, 255);
        return (Encode(x / length * radius), Encode(y / length * radius));
    }
    public override void Render(DrawingContext context)
    {
        var extent = Math.Max(1, Math.Min(Bounds.Width, Bounds.Height) - 14);
        var rect = new Rect((Bounds.Width - extent) / 2, (Bounds.Height - extent) / 2, extent, extent);
        var line = new Pen(Brush.Parse("#536374"));
        context.DrawRectangle(Brush.Parse("#141B22"), line, rect, 8, 8);
        context.DrawEllipse(null, new Pen(Brush.Parse("#364653")), rect.Center, extent / 2, extent / 2);
        if (NormalizedRadius is { } radius)
            context.DrawEllipse(null, new Pen(Brush.Parse("#5AC8FA")), rect.Center, extent / 2 * radius, extent / 2 * radius);
        context.DrawLine(line, new(rect.Left, rect.Center.Y), new(rect.Right, rect.Center.Y));
        context.DrawLine(line, new(rect.Center.X, rect.Top), new(rect.Center.X, rect.Bottom));
        if (_x is { } x && _y is { } y)
            context.DrawEllipse(Brush.Parse("#F36369"), new Pen(Brushes.White, 1),
                new(rect.Center.X + Unit(x) * extent / 2, rect.Center.Y - Unit(y) * extent / 2), 5, 5);
    }
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _dragging = true; e.Pointer.Capture(this); Focus(); DragStarted?.Invoke(); Move(e); e.Handled = true;
    }
    protected override void OnPointerMoved(PointerEventArgs e) { base.OnPointerMoved(e); if (_dragging) Move(e); }
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    { base.OnPointerReleased(e); if (!_dragging) return; Move(e); _dragging = false; e.Pointer.Capture(null); PositionCommitted?.Invoke(); e.Handled = true; }
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    { if (_dragging) { _dragging = false; PositionCommitted?.Invoke(); } base.OnPointerCaptureLost(e); }
    private void Move(PointerEventArgs e)
    {
        var (x, y) = Coordinates(e.GetPosition(this), Bounds.Size, NormalizedRadius);
        if (_x == x && _y == y) return;
        SetPosition(x, y); PositionChanged?.Invoke(x, y);
    }
}
