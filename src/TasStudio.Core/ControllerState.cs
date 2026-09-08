using System.Runtime.InteropServices;

namespace TasStudio.Core;

[Flags]
public enum PadButtons : ushort
{
    None = 0, Left = 0x0001, Right = 0x0002, Down = 0x0004, Up = 0x0008,
    Z = 0x0010, R = 0x0020, L = 0x0040,
    A = 0x0100, B = 0x0200, X = 0x0400, Y = 0x0800, Start = 0x1000
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly record struct ControllerState(PadButtons Buttons, byte StickX, byte StickY,
    byte CStickX, byte CStickY, byte TriggerL, byte TriggerR)
{
    public const byte NeutralAxis = 128;
    public const byte MinimumAxis = 0;
    public const byte MaximumAxis = 255;
    public const PadButtons KnownButtons = PadButtons.Left | PadButtons.Right | PadButtons.Down | PadButtons.Up |
        PadButtons.Z | PadButtons.R | PadButtons.L | PadButtons.A | PadButtons.B | PadButtons.X | PadButtons.Y | PadButtons.Start;
    public void Validate() { if ((Buttons & ~KnownButtons) != PadButtons.None) throw new ArgumentException("Unknown GameCube controller button bits."); }
    public static ControllerState Neutral => new(PadButtons.None, NeutralAxis, NeutralAxis,
        NeutralAxis, NeutralAxis, 0, 0);
}
