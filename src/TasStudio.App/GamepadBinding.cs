namespace TasStudio.App;

public enum GamepadAxis
{
    None, LeftUp, LeftDown, LeftLeft, LeftRight, RightUp, RightDown, RightLeft, RightRight, LeftTrigger, RightTrigger
}

/// <summary>An optional override; absent entries retain legacy button and fixed axis mappings.</summary>
public sealed record GamepadBinding(GamepadButton Button = GamepadButton.None, GamepadAxis Axis = GamepadAxis.None)
{
    public static GamepadBinding For(AppSettings settings, PadControl control)
    {
        if (settings.GamepadBindings.TryGetValue(control, out var binding)) return binding ?? new();
        return control switch
        {
            PadControl.StickUp => new(Axis: GamepadAxis.LeftUp), PadControl.StickDown => new(Axis: GamepadAxis.LeftDown),
            PadControl.StickLeft => new(Axis: GamepadAxis.LeftLeft), PadControl.StickRight => new(Axis: GamepadAxis.LeftRight),
            PadControl.CUp => new(Axis: GamepadAxis.RightUp), PadControl.CDown => new(Axis: GamepadAxis.RightDown),
            PadControl.CLeft => new(Axis: GamepadAxis.RightLeft), PadControl.CRight => new(Axis: GamepadAxis.RightRight),
            PadControl.AnalogL => new(Axis: GamepadAxis.LeftTrigger), PadControl.AnalogR => new(Axis: GamepadAxis.RightTrigger),
            _ => new(settings.GamepadButtons.GetValueOrDefault(control, GamepadButton.None))
        };
    }

    public double Amount(LiveInputSource.XInputGamepad pad, double deadZone = 0)
    {
        var digital = Button != GamepadButton.None && (pad.Buttons & Button) != 0 ? 1.0 : 0;
        static double Signed(short raw) => raw < 0 ? raw / -(double)short.MinValue : raw / (double)short.MaxValue;
        var axis = Axis switch
        {
            GamepadAxis.LeftUp => Signed(pad.LeftY), GamepadAxis.LeftDown => -Signed(pad.LeftY),
            GamepadAxis.LeftLeft => -Signed(pad.LeftX), GamepadAxis.LeftRight => Signed(pad.LeftX),
            GamepadAxis.RightUp => Signed(pad.RightY), GamepadAxis.RightDown => -Signed(pad.RightY),
            GamepadAxis.RightLeft => -Signed(pad.RightX), GamepadAxis.RightRight => Signed(pad.RightX),
            GamepadAxis.LeftTrigger => pad.LeftTrigger / 255.0, GamepadAxis.RightTrigger => pad.RightTrigger / 255.0,
            _ => 0
        };
        axis = Math.Max(0, axis);
        if (Axis is not (GamepadAxis.LeftTrigger or GamepadAxis.RightTrigger))
            axis = axis <= deadZone ? 0 : (axis - deadZone) / (1 - deadZone);
        return Math.Max(digital, Math.Clamp(axis, 0, 1));
    }

    public override string ToString()
    {
        var axis = Axis switch
        {
            GamepadAxis.LeftUp => "Left stick ↑", GamepadAxis.LeftDown => "Left stick ↓",
            GamepadAxis.LeftLeft => "Left stick ←", GamepadAxis.LeftRight => "Left stick →",
            GamepadAxis.RightUp => "Right stick ↑", GamepadAxis.RightDown => "Right stick ↓",
            GamepadAxis.RightLeft => "Right stick ←", GamepadAxis.RightRight => "Right stick →",
            GamepadAxis.LeftTrigger => "Left trigger", GamepadAxis.RightTrigger => "Right trigger", _ => ""
        };
        var button = Button switch
        {
            GamepadButton.LeftShoulder => "LB", GamepadButton.RightShoulder => "RB",
            GamepadButton.LeftStick => "Left stick click", GamepadButton.RightStick => "Right stick click",
            GamepadButton.Up => "D-pad ↑", GamepadButton.Down => "D-pad ↓", GamepadButton.Left => "D-pad ←", GamepadButton.Right => "D-pad →",
            GamepadButton.None => "", _ => Button.ToString()
        };
        return button.Length == 0 ? axis.Length == 0 ? "—" : axis : axis.Length == 0 ? button : button + " / " + axis;
    }
}

/// <summary>Waits for release before detecting a fresh physical action; drift cannot bind an axis.</summary>
public sealed class GamepadBindingCapture
{
    public bool Ready { get; private set; }
    public GamepadBinding? Observe(LiveInputSource.GamepadSample sample)
    {
        if (!sample.Connected) { Ready = false; return null; }
        var axes = Enum.GetValues<GamepadAxis>().Where(a => a != GamepadAxis.None)
            .Select(a => new GamepadBinding(Axis: a)).ToArray();
        if (!Ready)
        {
            Ready = sample.State.Buttons == GamepadButton.None && axes.All(a => a.Amount(sample.State) < 0.2);
            return null;
        }
        var button = Enum.GetValues<GamepadButton>().FirstOrDefault(b => b != GamepadButton.None && (sample.State.Buttons & b) != 0);
        if (button != GamepadButton.None) return new(button);
        return axes.OrderByDescending(a => a.Amount(sample.State)).FirstOrDefault(a => a.Amount(sample.State) >= 0.55);
    }
}
