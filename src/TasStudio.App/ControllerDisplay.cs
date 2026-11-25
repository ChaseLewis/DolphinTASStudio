using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using TasStudio.Core;

namespace TasStudio.App;

/// <summary>Read-only, resolution-independent GameCube input display for playback and capture.</summary>
public sealed class ControllerDisplay : Control
{
    private ControllerState? _input;
    public ControllerState? Input => _input;
    public void SetInput(ControllerState? input)
    {
        if (_input == input) return;
        _input = input; InvalidateVisual();
    }

    private static IBrush Ink(string color) => new SolidColorBrush(Color.Parse(color));
    private static readonly IBrush Shell = Ink("#7e88cb"), Face = Ink("#5d6abe"), Silver = Ink("#d8dce5"),
        Gate = Ink("#c3c7d1"), Yellow = Ink("#ffdb59"), Red = Ink("#d52b36"), TextInk = Ink("#263352"),
        Guide = Ink("#9b91b8"), Pressed = Ink("#a9e3ff");
    private static readonly Geometry Body = Geometry.Parse("M63 91 C96 50 165 30 240 30 C315 30 384 50 417 91 C447 128 460 187 460 242 C460 307 449 350 422 350 C399 350 393 337 388 310 L368 227 C353 240 354 253 347 266 C334 296 300 306 279 292 C249 280 241 255 252 226 C262 205 277 200 292 191 C262 185 218 185 188 191 C203 204 214 209 225 235 C239 269 214 303 181 300 C150 296 132 276 129 249 C127 240 123 235 112 228 L93 308 C88 336 81 350 58 350 C30 350 20 307 20 242 C20 187 33 128 63 91 Z");
    private static readonly Geometry LeftFace = Geometry.Parse("M110 68 C65 68 30 104 30 148 C30 189 60 223 103 227 C114 228 119 235 120 248 C123 279 146 299 174 300 C205 301 230 276 230 247 C230 222 214 205 196 194 C185 187 185 179 187 162 C189 157 190 151 190 148 C190 104 154 68 110 68 Z");
    private static readonly Geometry MainGate = Geometry.Parse("M110 102 L142 116 L156 148 L142 180 L110 194 L78 180 L64 148 L78 116 Z");
    private static readonly Geometry CGate = Geometry.Parse("M306 212 L329 222 L339 245 L329 268 L306 278 L283 268 L273 245 L283 222 Z");
    private static readonly Geometry DPad = Geometry.Parse("M170 220 H181 Q186 220 186 225 V240 H201 Q206 240 206 245 V256 Q206 261 201 261 H186 V276 Q186 281 181 281 H170 Q165 281 165 276 V261 H150 Q145 261 145 256 V245 Q145 240 150 240 H165 V225 Q165 220 170 220 Z");
    private static readonly (PadButtons Button, Geometry Shape)[] Shapes =
    [
        (PadButtons.L, Geometry.Parse("M61 88 C57 55 75 30 101 30 C123 30 138 47 140 66 Z")),
        (PadButtons.R, Geometry.Parse("M419 88 C423 55 405 30 379 30 C357 30 342 47 340 66 Z")),
        (PadButtons.Up, Geometry.Parse("M170 220 H181 Q186 220 186 225 V241 H165 V225 Q165 220 170 220 Z")),
        (PadButtons.Down, Geometry.Parse("M165 260 H186 V276 Q186 281 181 281 H170 Q165 281 165 276 Z")),
        (PadButtons.Left, Geometry.Parse("M150 240 H166 V261 H150 Q145 261 145 256 V245 Q145 240 150 240 Z")),
        (PadButtons.Right, Geometry.Parse("M185 240 H201 Q206 240 206 245 V256 Q206 261 201 261 H185 Z")),
        (PadButtons.X, Geometry.Parse("M414 139 C410 120 432 120 434 136 C436 146 436 157 433 164 C429 173 413 169 414 160 C416 151 416 148 414 139 Z")),
        (PadButtons.Y, Geometry.Parse("M356 85 C364 82 377 82 384 86 C394 92 390 105 381 105 C373 103 366 103 358 105 C347 108 346 91 356 85 Z"))
    ];
    private static readonly IBrush Green = Ink("#49b4aa"), GreenDown = Ink("#83ffd0"),
        Pink = Ink("#e37683"), PinkDown = Ink("#ffabb4"), Purple = Ink("#b4a6e2"), PurpleDown = Ink("#e0c9ff"),
        StickFace = Ink("#edf0f5"), CFace = Ink("#ffe996");
    private static readonly Pen Outline = new(TextInk, 4), TargetPen = new(Guide, 1.5), RedPen = new(Red, 2);

    public ControllerDisplay()
    {
        MinWidth = 180; MinHeight = 140;
        Avalonia.Automation.AutomationProperties.SetName(this, "Recorded GameCube controller inputs");
    }
    protected override Size MeasureOverride(Size availableSize) => new(
        double.IsFinite(availableSize.Width) ? availableSize.Width : 400,
        double.IsFinite(availableSize.Height) ? availableSize.Height : 304);

    public override void Render(DrawingContext context)
    {
        var scale = Math.Min(Bounds.Width / 480, Bounds.Height / 365);
        if (scale <= 0) return;
        using var transform = context.PushTransform(Matrix.CreateScale(scale, scale) *
            Matrix.CreateTranslation((Bounds.Width - 480 * scale) / 2, (Bounds.Height - 365 * scale) / 2));
        var input = _input ?? ControllerState.Neutral;
        bool Down(PadButtons key) => (input.Buttons & key) != 0;
        void Shape((PadButtons Button, Geometry Shape) key) => context.DrawGeometry(Down(key.Button) ? Pressed : Silver,
            Down(key.Button) ? Outline : null, key.Shape);
        Shape(Shapes[0]); Shape(Shapes[1]);
        context.DrawGeometry(Shell, null, Body);
        context.DrawGeometry(Face, null, LeftFace);
        using (context.PushTransform(Matrix.CreateScale(-1, 1) * Matrix.CreateTranslation(480, 0)))
            context.DrawGeometry(Face, null, LeftFace);
        context.DrawGeometry(Gate, null, MainGate); context.DrawGeometry(Yellow, null, CGate);
        DrawStick(context, new(110, 148), 40, 42, 6, input.StickX, input.StickY, StickFace);
        DrawStick(context, new(306, 245), 28, 30, 5, input.CStickX, input.CStickY, CFace);
        context.DrawGeometry(Silver, null, DPad);
        foreach (var shape in Shapes.Skip(2)) Shape(shape);
        void Round(PadButtons key, Point center, double radius, IBrush rest, IBrush pressed) =>
            context.DrawEllipse(Down(key) ? pressed : rest, Down(key) ? Outline : null, center, radius, radius);
        Round(PadButtons.A, new(370, 148), 30, Green, GreenDown);
        Round(PadButtons.B, new(325, 183), 15, Pink, PinkDown);
        Round(PadButtons.Start, new(240, 148), 10, Silver, Pressed);
        context.DrawRectangle(Down(PadButtons.Z) ? PurpleDown : Purple, Down(PadButtons.Z) ? Outline : null, new(340, 58, 58, 18), 9, 9);
        Trigger(context, 65, input.TriggerL); Trigger(context, 337, input.TriggerR);
        // Optical label centers account for the letter shapes and exposed shoulder areas.
        foreach (var (label, x, y) in new[] { ("L",95,46), ("R",385,46), ("Start",240,172), ("A",370,146),
                     ("B",325,181), ("X",426,147), ("Y",369,94), ("Z",369,65) })
        {
            var text = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), label == "A" ? 25 : 20, TextInk);
            context.DrawText(text, new(x - text.Width / 2, y - text.Height / 2));
        }
    }
    internal static Point StickPoint(Point center, double radius, byte x, byte y)
    {
        static double Unit(byte value) => (value - 128d) / (value < 128 ? 128 : 127);
        return new(center.X + Unit(x) * radius, center.Y - Unit(y) * radius);
    }
    private static void DrawStick(DrawingContext context, Point center, double radius, double faceRadius, double dotRadius,
        byte x, byte y, IBrush fill)
    {
        context.DrawEllipse(fill, null, center, faceRadius, faceRadius);
        context.DrawEllipse(null, TargetPen, center, radius, radius);
        context.DrawLine(TargetPen, new(center.X - faceRadius, center.Y), new(center.X + faceRadius, center.Y));
        context.DrawLine(TargetPen, new(center.X, center.Y - faceRadius), new(center.X, center.Y + faceRadius));
        var point = StickPoint(center, radius, x, y);
        context.DrawLine(RedPen, center, point);
        context.DrawEllipse(Red, new Pen(Brushes.White, 1.5), point, dotRadius, dotRadius);
    }
    private static void Trigger(DrawingContext context, double x, byte pressure)
    {
        context.DrawRectangle(Silver, null, new(x, 15, 78, 5), 2, 2);
        if (pressure > 0) context.DrawRectangle(Pressed, null, new(x, 15, 78d * pressure / 255, 5), 2, 2);
    }
}
