using TasStudio.Core;
using TasStudio.Emulation;

namespace TasStudio.App;

/// <summary>Next Frame preserves recorded inputs and uses shown TAS controls only for an unrecorded frame.</summary>
public static class TasInputExecution
{
    public static ControllerState Resolve(ControllerState fallback, IEnumerable<KeyValuePair<PadButtons, bool?>> buttons, decimal?[] axes)
    {
        var bits = fallback.Buttons;
        foreach (var (button, pressed) in buttons)
            if (pressed is { } value) bits = value ? bits | button : bits & ~button;
        byte Axis(int index, byte original) => axes[index] is { } value ? (byte)Math.Clamp(value, 0, 255) : original;
        return new(bits, Axis(0, fallback.StickX), Axis(1, fallback.StickY), Axis(2, fallback.CStickX),
            Axis(3, fallback.CStickY), Axis(4, fallback.TriggerL), Axis(5, fallback.TriggerR));
    }

    public static Task<bool> StepAsync(ExecutionService execution, ControllerState shown, bool candidateSelected) =>
        execution.AdvanceFrameAsync(candidateSelected ? null : shown);
}
