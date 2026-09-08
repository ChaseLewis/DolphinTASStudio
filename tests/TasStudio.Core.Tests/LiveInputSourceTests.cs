using Avalonia.Input;
using TasStudio.App;
using TasStudio.Core;
using Xunit;

namespace TasStudio.Core.Tests;

public sealed class LiveInputSourceTests
{
    private static readonly LiveInputSource.GamepadSample Disconnected = new(false, default);
    private static void Eventually(Func<bool> condition) => Assert.True(SpinWait.SpinUntil(condition, TimeSpan.FromSeconds(5)));

    [Fact]
    public void ControllerChangesAreSampledWithoutUiTicksAndStopOnDispose()
    {
        var caller = Environment.CurrentManagedThreadId;
        var pollingThread = 0;
        var calls = 0;
        var sample = Disconnected;
        var input = new LiveInputSource(_ =>
        {
            Interlocked.Exchange(ref pollingThread, Environment.CurrentManagedThreadId);
            Interlocked.Increment(ref calls);
            return Volatile.Read(ref sample);
        });
        try
        {
            input.Configure(new AppSettings { GamepadIndex = 0 });
            input.SetAcceptInput(true);
            Volatile.Write(ref sample, new(true, new() { Buttons = GamepadButton.A, LeftX = short.MaxValue, LeftTrigger = 255 }));
            Eventually(() => input.Read().Buttons.HasFlag(PadButtons.A | PadButtons.L));
            Assert.NotEqual(caller, Volatile.Read(ref pollingThread));
            Assert.Equal(255, input.Read().StickX);
            input.KeyDown(Key.Down);
            Eventually(() => input.Read().StickY == 0);
            input.SetAcceptInput(true, acceptKeyboard: false);
            Eventually(() => input.Read().StickY == 128 && input.Read().Buttons.HasFlag(PadButtons.A));
            input.KeyDown(Key.Down);
            Assert.Equal(128, input.Read().StickY);
            Volatile.Write(ref sample, Disconnected);
            Eventually(() => input.Read() == ControllerState.Neutral && input.DeviceStatus.Contains("disconnected"));
            input.Dispose();
            var stoppedCalls = Volatile.Read(ref calls);
            Assert.Equal(ControllerState.Neutral, input.Read());
            input.SetAcceptInput(true); input.KeyDown(Key.X);
            Assert.Equal(ControllerState.Neutral, input.Read());
            Assert.Equal(stoppedCalls, Volatile.Read(ref calls));
        }
        finally { input.Dispose(); }
    }

    [Fact]
    public void ConfigurationIsCopiedAndKeysAreClearedWhenInputIsBlocked()
    {
        using var input = new LiveInputSource(_ => Disconnected);
        var settings = new AppSettings();
        input.Configure(settings);
        settings.Keys[PadControl.A] = Key.B;
        input.SetAcceptInput(true); input.KeyDown(Key.X);
        Eventually(() => input.Read().Buttons == PadButtons.A);
        input.SetAcceptInput(false);
        Assert.Equal(ControllerState.Neutral, input.Read());
        input.KeyDown(Key.X); // Typing in a text box or another application must not latch a key.
        input.SetAcceptInput(true);
        input.KeyDown(Key.Right);
        Eventually(() => input.Read().StickX == 255);
        Assert.Equal(PadButtons.None, input.Read().Buttons);
        input.KeyUp(Key.Right);
        Eventually(() => input.Read() == ControllerState.Neutral);
        input.Configure(settings); input.KeyDown(Key.B);
        Eventually(() => input.Read().Buttons == PadButtons.A);
    }

    [Fact]
    public void FocusLossDuringDeviceReadCannotRepublishHeldInput()
    {
        using var entered = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        var block = 0;
        var completedReads = 0;
        using var input = new LiveInputSource(_ =>
        {
            if (Volatile.Read(ref block) == 1) { entered.Set(); resume.Wait(TimeSpan.FromSeconds(5)); }
            Interlocked.Increment(ref completedReads);
            return new(true, new() { Buttons = GamepadButton.A });
        });
        input.Configure(new AppSettings { GamepadIndex = 0 }); input.SetAcceptInput(true);
        Eventually(() => input.Read().Buttons == PadButtons.A);
        Volatile.Write(ref block, 1);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        try
        {
            input.SetAcceptInput(false);
            Assert.Equal(ControllerState.Neutral, input.Read());
        }
        finally { Volatile.Write(ref block, 0); resume.Set(); }
        // Allow several complete polls after focus loss, with the hardware button still held.
        var readsAfterRelease = Volatile.Read(ref completedReads);
        Eventually(() => Volatile.Read(ref completedReads) >= readsAfterRelease + 12);
        Assert.Equal(ControllerState.Neutral, input.Read());
    }

    [Fact]
    public void ControllerDiagnosticsRemainAvailableWhileModalInputIsBlocked()
    {
        using var input = new LiveInputSource(index => index == 2
            ? new(true, new() { Buttons = GamepadButton.B }) : Disconnected);
        input.Configure(new AppSettings { GamepadIndex = 2 });
        Eventually(() => input.ReadDevice(2).Connected && input.DeviceStatus.Contains("connected"));
        Assert.Equal(GamepadButton.B, input.ReadDevice(2).State.Buttons);
        Assert.Equal(ControllerState.Neutral, input.Read());
        input.SetAcceptInput(true);
        Eventually(() => input.Read().Buttons == PadButtons.B);
    }
}
