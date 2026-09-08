using TasStudio.Core;

namespace TasStudio.App;

/// <summary>Immutable phase masks shared by the UI and emulation worker.</summary>
public sealed class TurboInput
{
    private sealed record Pattern(PadButtons Even, PadButtons Odd);
    private Pattern _pattern = new(PadButtons.None, PadButtons.None);
    public PadButtons Buttons { get { var pattern = Volatile.Read(ref _pattern); return pattern.Even | pattern.Odd; } }
    public bool Toggle(PadButtons button, ulong position)
    {
        var pattern = Volatile.Read(ref _pattern);
        var enabled = ((pattern.Even | pattern.Odd) & button) == 0;
        var even = pattern.Even & ~button; var odd = pattern.Odd & ~button;
        if (enabled) { if ((position & 1) == 0) even |= button; else odd |= button; }
        Volatile.Write(ref _pattern, new(even, odd));
        return enabled;
    }
    public ControllerState Apply(ControllerState input, ulong position)
    {
        var pattern = Volatile.Read(ref _pattern);
        var pressed = (position & 1) == 0 ? pattern.Even : pattern.Odd;
        return input with { Buttons = (input.Buttons & ~(pattern.Even | pattern.Odd)) | pressed };
    }
}
