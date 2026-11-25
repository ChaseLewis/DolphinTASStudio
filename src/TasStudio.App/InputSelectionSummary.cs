using TasStudio.Core;

namespace TasStudio.App;

/// <summary>Inspector values for a range; null axes and buttons represent mixed input.</summary>
internal sealed class InputSelectionSummary
{
    private PadButtons _all, _any;
    public byte?[] Axes { get; }

    public InputSelectionSummary(ControllerState input)
    {
        _all = _any = input.Buttons;
        Axes = [input.StickX, input.StickY, input.CStickX, input.CStickY, input.TriggerL, input.TriggerR];
    }

    public bool? Button(PadButtons button) => (_all & button) == button ? true :
        (_any & button) == 0 ? false : null;

    public static InputSelectionSummary Read(IReadOnlyList<ControllerState> inputs, int start, int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfLessThan(length, 1);
        if (start > inputs.Count - length) throw new ArgumentOutOfRangeException(nameof(length));
        var result = new InputSelectionSummary(inputs[start]);
        var mixedAxes = 0;
        for (var i = start + 1; i < start + length; i++)
        {
            var input = inputs[i];
            result._all &= input.Buttons;
            result._any |= input.Buttons;
            Merge(0, input.StickX); Merge(1, input.StickY);
            Merge(2, input.CStickX); Merge(3, input.CStickY);
            Merge(4, input.TriggerL); Merge(5, input.TriggerR);
            // Once every control is mixed, later frames cannot change the display.
            if (mixedAxes == 6 && (result._all ^ result._any) == ControllerState.KnownButtons) break;
        }
        return result;

        void Merge(int axis, byte value)
        {
            if (result.Axes[axis] is { } previous && previous != value)
            { result.Axes[axis] = null; mixedAxes++; }
        }
    }
}
