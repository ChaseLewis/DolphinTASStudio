using TasStudio.App;
using TasStudio.Core;
using Xunit;

namespace TasStudio.Core.Tests;

public sealed class TurboInputTests
{
    [Fact]
    public void AlternatesHundredsOfFramesAndKeepsPhaseAcrossSeek()
    {
        var turbo = new TurboInput();
        var input = ControllerState.Neutral with { Buttons = PadButtons.A | PadButtons.B, TriggerR = 203 };
        Assert.True(turbo.Toggle(PadButtons.A, 11));
        for (ulong frame = 11; frame < 611; frame++)
        {
            var actual = turbo.Apply(input, frame);
            Assert.Equal((frame & 1) == 1, actual.Buttons.HasFlag(PadButtons.A));
            Assert.True(actual.Buttons.HasFlag(PadButtons.B)); Assert.Equal(203, actual.TriggerR);
        }
        Assert.True(turbo.Apply(input, 11).Buttons.HasFlag(PadButtons.A));
        Assert.False(turbo.Apply(input, 10).Buttons.HasFlag(PadButtons.A));
        Assert.False(turbo.Toggle(PadButtons.A, 611));
        Assert.Equal(input, turbo.Apply(input, 612));
    }
}
