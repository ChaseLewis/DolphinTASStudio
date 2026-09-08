using System.Runtime.InteropServices;
using Avalonia.Input;
using TasStudio.Core;

namespace TasStudio.App;

public sealed class LiveInputSource : IDisposable
{
    public const int GamepadCount = 4;
    private const uint XInputSuccess = 0;
    private const double PercentScale = 100.0;
    private const double KeyboardModifierScale = 0.5;
    private sealed record Snapshot(ControllerState Value, string DeviceStatus);
    public sealed record GamepadSample(bool Connected, XInputGamepad State);
    private Snapshot _snapshot = new(ControllerState.Neutral, "Keyboard");
    private GamepadSample[] _devices = Enumerable.Range(0, GamepadCount).Select(_ => new GamepadSample(false, default)).ToArray();
    private readonly object _gate = new();
    private readonly HashSet<Key> _keys = [];
    private readonly ManualResetEventSlim _stop = new();
    private readonly Thread _worker;
    private readonly Func<int, GamepadSample> _readDevice;
    private AppSettings _configuration = new();
    private bool _acceptInput, _acceptKeyboard, _disposed;
    public string DeviceStatus => Volatile.Read(ref _snapshot).DeviceStatus;
    public ControllerState Read() => Volatile.Read(ref _snapshot).Value;
    public GamepadSample ReadDevice(int index) => Volatile.Read(ref _devices)[index];

    public LiveInputSource(Func<int, GamepadSample>? readDevice = null)
    {
        _readDevice = readDevice ?? (index => new(IsConnected(index, out var pad), pad));
        _worker = new Thread(PollLoop) { IsBackground = true, Name = "TAS controller input" };
        _worker.Start();
    }

    // Called on the UI thread; the worker never sees mutable UI settings or dictionaries.
    public void Configure(AppSettings settings)
    {
        var copy = new AppSettings
        {
            GamepadIndex = Math.Clamp(settings.GamepadIndex, -1, GamepadCount - 1),
            DeadZonePercent = Math.Clamp(settings.DeadZonePercent, 0, 95),
            TriggerClickPercent = Math.Clamp(settings.TriggerClickPercent, 1, 100),
            InvertStickY = settings.InvertStickY, InvertCStickY = settings.InvertCStickY,
            Keys = new(settings.Keys), GamepadButtons = new(settings.GamepadButtons), GamepadBindings = new(settings.GamepadBindings)
        };
        lock (_gate) { _configuration = copy; ClearLocked(); }
    }
    public void SetAcceptInput(bool accept, bool acceptKeyboard = true)
    {
        lock (_gate)
        {
            _acceptInput = accept && !_disposed;
            var keyboard = _acceptInput && acceptKeyboard;
            if (!_acceptInput || (_acceptKeyboard && !keyboard)) ClearLocked();
            _acceptKeyboard = keyboard;
        }
    }
    public void KeyDown(Key key) { lock (_gate) { if (_acceptKeyboard) _keys.Add(key); } }
    public void KeyUp(Key key) { lock (_gate) _keys.Remove(key); }
    public void Clear() { lock (_gate) ClearLocked(); }
    private void ClearLocked()
    {
        _keys.Clear();
        Volatile.Write(ref _snapshot, new(ControllerState.Neutral, _snapshot.DeviceStatus));
    }
    private void PollLoop()
    {
        while (!_stop.IsSet)
        {
            var devices = new GamepadSample[GamepadCount];
            for (var i = 0; i < devices.Length; i++) devices[i] = _readDevice(i);
            Volatile.Write(ref _devices, devices);
            lock (_gate)
            {
                var settings = _configuration;
                var pad = settings.GamepadIndex < 0 ? new GamepadSample(false, default) : devices[settings.GamepadIndex];
                var status = settings.GamepadIndex < 0 ? "Keyboard" : $"XInput {settings.GamepadIndex + 1} • {(pad.Connected ? "connected" : "disconnected")}";
                Volatile.Write(ref _snapshot, new(_acceptInput ? Compose(settings, pad) : ControllerState.Neutral, status));
            }
            _stop.Wait(TimeSpan.FromMilliseconds(8));
        }
    }
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true; _acceptInput = false; ClearLocked();
        }
        _stop.Set(); _worker.Join(); _stop.Dispose();
    }
    private ControllerState Compose(AppSettings settings, GamepadSample sample)
    {
        bool Pressed(PadControl control) => settings.Keys.TryGetValue(control, out var key) && key != Key.None && _keys.Contains(key);
        var buttons = PadButtons.None;
        foreach (var (control, button) in ButtonMap)
            if (Pressed(control)) buttons |= button;
        double Axis(PadControl negative, PadControl positive, bool modifier) => ((Pressed(positive) ? 1 : 0) - (Pressed(negative) ? 1 : 0)) * (modifier ? KeyboardModifierScale : 1);
        var stickModifier = _keys.Contains(Key.LeftShift) || _keys.Contains(Key.RightShift);
        var cModifier = _keys.Contains(Key.LeftCtrl) || _keys.Contains(Key.RightCtrl);
        double sx = Axis(PadControl.StickLeft, PadControl.StickRight, stickModifier);
        double sy = Axis(PadControl.StickDown, PadControl.StickUp, stickModifier);
        double cx = Axis(PadControl.CLeft, PadControl.CRight, cModifier);
        double cy = Axis(PadControl.CDown, PadControl.CUp, cModifier);
        byte left = Pressed(PadControl.AnalogL) ? byte.MaxValue : byte.MinValue;
        byte right = Pressed(PadControl.AnalogR) ? byte.MaxValue : byte.MinValue;
        if (sample.Connected)
        {
            var pad = sample.State;
            var deadZone = settings.DeadZonePercent / PercentScale;
            double Amount(PadControl control) => GamepadBinding.For(settings, control).Amount(pad, deadZone);
            foreach (var (control, button) in ButtonMap)
                if (Amount(control) >= 0.5) buttons |= button;
            static double Strongest(double keyboard, double gamepad) => Math.Abs(keyboard) >= Math.Abs(gamepad) ? keyboard : gamepad;
            sx = Strongest(sx, Amount(PadControl.StickRight) - Amount(PadControl.StickLeft));
            sy = Strongest(sy, (Amount(PadControl.StickUp) - Amount(PadControl.StickDown)) * (settings.InvertStickY ? -1 : 1));
            cx = Strongest(cx, Amount(PadControl.CRight) - Amount(PadControl.CLeft));
            cy = Strongest(cy, (Amount(PadControl.CUp) - Amount(PadControl.CDown)) * (settings.InvertCStickY ? -1 : 1));
            left = Math.Max(left, (byte)Math.Round(Amount(PadControl.AnalogL) * byte.MaxValue));
            right = Math.Max(right, (byte)Math.Round(Amount(PadControl.AnalogR) * byte.MaxValue));
            var threshold = settings.TriggerClickPercent / PercentScale * byte.MaxValue;
            if (left >= threshold) buttons |= PadButtons.L;
            if (right >= threshold) buttons |= PadButtons.R;
        }
        return new(buttons, ToAxis(sx), ToAxis(sy), ToAxis(cx), ToAxis(cy), left, right);
    }

    private static byte ToAxis(double value) => (byte)Math.Clamp(Math.Round(ControllerState.NeutralAxis + value * (value < 0 ? ControllerState.NeutralAxis : ControllerState.MaximumAxis - ControllerState.NeutralAxis)), byte.MinValue, byte.MaxValue);
    public static bool IsConnected(int index, out XInputGamepad gamepad)
    {
        gamepad = default;
        if (!OperatingSystem.IsWindows()) return false;
        if (XInputGetState((uint)index, out var state) != XInputSuccess) return false;
        gamepad = state.Gamepad;
        return true;
    }
    public static readonly (PadControl Control, PadButtons Button)[] ButtonMap =
    [
        (PadControl.A,PadButtons.A),(PadControl.B,PadButtons.B),(PadControl.X,PadButtons.X),(PadControl.Y,PadButtons.Y),
        (PadControl.Z,PadButtons.Z),(PadControl.Start,PadButtons.Start),(PadControl.L,PadButtons.L),(PadControl.R,PadButtons.R),
        (PadControl.Up,PadButtons.Up),(PadControl.Down,PadButtons.Down),(PadControl.Left,PadButtons.Left),(PadControl.Right,PadButtons.Right)
    ];
    [StructLayout(LayoutKind.Sequential)]
    public struct XInputGamepad
    {
        public GamepadButton Buttons;
        public byte LeftTrigger, RightTrigger;
        public short LeftX, LeftY, RightX, RightY;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct XInputState { public uint Packet; public XInputGamepad Gamepad; }
    [DllImport("xinput1_4.dll", ExactSpelling = true)]
    private static extern uint XInputGetState(uint userIndex, out XInputState state);
}
