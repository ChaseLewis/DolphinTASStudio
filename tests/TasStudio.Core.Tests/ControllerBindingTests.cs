using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia.Input;
using TasStudio.App;
using TasStudio.Core;
using Xunit;

namespace TasStudio.Core.Tests;

public sealed class ControllerBindingTests
{
    private static void Eventually(Func<bool> condition) => Assert.True(SpinWait.SpinUntil(condition, TimeSpan.FromSeconds(5)));

    [Fact]
    public void ExistingMappingsAndFixedAxesRemainActiveWithoutOverrides()
    {
        var settings = new AppSettings(); settings.GamepadButtons[PadControl.A] = GamepadButton.Back;
        Assert.Equal(new(GamepadButton.Back), GamepadBinding.For(settings, PadControl.A));
        Assert.Equal(new(Axis: GamepadAxis.LeftUp), GamepadBinding.For(settings, PadControl.StickUp));
        Assert.Equal(new(Axis: GamepadAxis.RightLeft), GamepadBinding.For(settings, PadControl.CLeft));
        Assert.Equal(new(Axis: GamepadAxis.LeftTrigger), GamepadBinding.For(settings, PadControl.AnalogL));
        settings.GamepadBindings[PadControl.StickUp] = new();
        Assert.Equal(0, GamepadBinding.For(settings, PadControl.StickUp).Amount(new() { LeftY = short.MaxValue }));
    }

    [Fact]
    public void CaptureRequiresReleaseAndAFreshActionAndResetsWhenDisconnected()
    {
        var capture = new GamepadBindingCapture();
        var held = new LiveInputSource.GamepadSample(true, new() { Buttons = GamepadButton.A });
        Assert.Null(capture.Observe(held)); Assert.False(capture.Ready);
        Assert.Null(capture.Observe(new(true, default))); Assert.True(capture.Ready);
        Assert.Equal(new(GamepadButton.A), capture.Observe(held));
        Assert.Null(capture.Observe(new(false, default))); Assert.False(capture.Ready);
        Assert.Null(capture.Observe(held)); // Reconnecting while a button is held must not capture it.
    }

    [Fact]
    public void CaptureIgnoresDriftAndSelectsStrongestSignedAxis()
    {
        var capture = new GamepadBindingCapture();
        Assert.Null(capture.Observe(new(true, new() { LeftX = 2000 }))); Assert.True(capture.Ready);
        Assert.Null(capture.Observe(new(true, new() { LeftX = 10000, RightTrigger = 64 })));
        Assert.Equal(new(Axis: GamepadAxis.RightDown), capture.Observe(new(true, new() { RightY = short.MinValue, LeftX = 20000 })));
        var trigger = new GamepadBindingCapture(); trigger.Observe(new(true, default));
        Assert.Equal(new(Axis: GamepadAxis.LeftTrigger), trigger.Observe(new(true, new() { LeftTrigger = 200 })));
    }

    [Fact]
    public void AxisBindingsPreserveRangeDirectionAndDeadZone()
    {
        var right = new GamepadBinding(Axis: GamepadAxis.LeftRight);
        Assert.Equal(0, right.Amount(new() { LeftX = 2000 }, 0.15));
        Assert.Equal(0, right.Amount(new() { LeftX = short.MinValue }, 0.15));
        Assert.Equal(1, right.Amount(new() { LeftX = short.MaxValue }, 0.15));
        Assert.InRange(right.Amount(new() { LeftX = 16384 }, 0.15), 0.41, 0.42);
        Assert.Equal(1, new GamepadBinding(Axis: GamepadAxis.LeftLeft).Amount(new() { LeftX = short.MinValue }));
        Assert.Equal(64 / 255.0, new GamepadBinding(Axis: GamepadAxis.LeftTrigger).Amount(new() { LeftTrigger = 64 }, 0.95));
    }

    [Fact]
    public void MappedAxesAndTriggersDriveTheirNewTargetsAndConfigurationIsCopied()
    {
        var sample = new LiveInputSource.GamepadSample(true, default);
        using var input = new LiveInputSource(_ => Volatile.Read(ref sample));
        var settings = new AppSettings { GamepadIndex = 0 };
        settings.GamepadBindings[PadControl.StickRight] = new(Axis: GamepadAxis.RightUp);
        settings.GamepadBindings[PadControl.StickLeft] = new();
        settings.GamepadBindings[PadControl.AnalogL] = new(Axis: GamepadAxis.RightTrigger);
        settings.GamepadBindings[PadControl.AnalogR] = new();
        settings.GamepadBindings[PadControl.A] = new(GamepadButton.Back);
        input.Configure(settings); input.SetAcceptInput(true);
        settings.GamepadBindings[PadControl.StickRight] = new();
        Volatile.Write(ref sample, new(true, new() { RightY = short.MaxValue, RightTrigger = 128, LeftX = short.MinValue, Buttons = GamepadButton.Back }));
        Eventually(() => input.Read().StickX == 255 && input.Read().TriggerL == 128 && input.Read().Buttons == PadButtons.A);
        Assert.Equal(0, input.Read().TriggerR);
        Volatile.Write(ref sample, new(true, new() { LeftX = short.MaxValue }));
        Eventually(() => input.Read() == ControllerState.Neutral);
    }

    [Fact]
    public void DigitalButtonCanDriveAnalogAndClearedGamepadDirectionPreservesKeyboard()
    {
        var sample = new LiveInputSource.GamepadSample(true, new() { Buttons = GamepadButton.Back });
        using var input = new LiveInputSource(_ => Volatile.Read(ref sample));
        var settings = new AppSettings { GamepadIndex = 0 };
        settings.GamepadBindings[PadControl.AnalogR] = new(GamepadButton.Back);
        settings.GamepadBindings[PadControl.StickRight] = new();
        input.Configure(settings); input.SetAcceptInput(true); input.KeyDown(Key.Right);
        Eventually(() => input.Read().TriggerR == 255 && input.Read().StickX == 255);
        Assert.True(input.Read().Buttons.HasFlag(PadButtons.R));
        input.KeyUp(Key.Right); Volatile.Write(ref sample, new(true, new() { LeftX = short.MaxValue }));
        Eventually(() => input.Read() == ControllerState.Neutral);
    }

    [Fact]
    public void BindingOverridesRoundtripAndOldSettingsStillUseLegacyMappings()
    {
        var options = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } };
        var settings = new AppSettings(); settings.GamepadBindings[PadControl.AnalogL] = new(Axis: GamepadAxis.RightTrigger);
        settings.GamepadBindings[PadControl.CLeft] = new();
        var reloaded = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings, options), options)!;
        Assert.Equal(settings.GamepadBindings[PadControl.AnalogL], reloaded.GamepadBindings[PadControl.AnalogL]);
        Assert.Equal(new(), GamepadBinding.For(reloaded, PadControl.CLeft));
        var old = JsonSerializer.Deserialize<AppSettings>("{\"GamepadButtons\":{\"A\":\"Back\"}}", options)!;
        Assert.Empty(old.GamepadBindings); Assert.Equal(new(GamepadButton.Back), GamepadBinding.For(old, PadControl.A));
        Assert.Equal(new(Axis: GamepadAxis.LeftRight), GamepadBinding.For(old, PadControl.StickRight));
    }
}
