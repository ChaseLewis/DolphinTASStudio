using System.Collections;
using TasStudio.App;
using TasStudio.Core;
using Xunit;

namespace TasStudio.Core.Tests;

public sealed class InputSelectionSummaryTests
{
    [Fact]
    public void SelectingNearEndOfLongMovieReadsOnlySelectedFrames()
    {
        var inputs = new IndexedInputs(1000000, _ => ControllerState.Neutral with { Buttons = PadButtons.A });
        var summary = InputSelectionSummary.Read(inputs, 999990, 3);
        Assert.Equal(new[] { 999990, 999991, 999992 }, inputs.Reads);
        Assert.Equal(true, summary.Button(PadButtons.A));
        Assert.Equal(false, summary.Button(PadButtons.B));
        Assert.Equal(new byte?[] { 128, 128, 128, 128, 0, 0 }, summary.Axes);
    }

    [Fact]
    public void MixedSelectionPreservesEveryUnchangedControl()
    {
        var first = new ControllerState(PadButtons.A | PadButtons.B, 10, 20, 30, 40, 50, 60);
        var second = first with { Buttons = PadButtons.B | PadButtons.X, StickY = 21, CStickX = 31, TriggerR = 61 };
        var summary = InputSelectionSummary.Read(new[] { first, second, first }, 0, 3);
        Assert.Null(summary.Button(PadButtons.A)); Assert.Null(summary.Button(PadButtons.X));
        Assert.Equal(true, summary.Button(PadButtons.B)); Assert.Equal(false, summary.Button(PadButtons.Y));
        Assert.Equal(new byte?[] { 10, null, null, 40, 50, null }, summary.Axes);
    }

    [Fact]
    public void EntirelyMixedSelectionDoesNotScanRestOfMovie()
    {
        var inputs = new IndexedInputs(1000000, i => i == 0 ? ControllerState.Neutral :
            new ControllerState(ControllerState.KnownButtons, 1, 1, 1, 1, 1, 1));
        var summary = InputSelectionSummary.Read(inputs, 0, inputs.Count);
        Assert.Equal(new[] { 0, 1 }, inputs.Reads);
        Assert.All(summary.Axes, value => Assert.Null(value));
        foreach (var button in Enum.GetValues<PadButtons>().Where(b => b != PadButtons.None))
            Assert.Null(summary.Button(button));
    }

    private sealed class IndexedInputs(int count, Func<int, ControllerState> input) : IReadOnlyList<ControllerState>
    {
        public List<int> Reads { get; } = [];
        public int Count => count;
        public ControllerState this[int index] { get { Reads.Add(index); return input(index); } }
        public IEnumerator<ControllerState> GetEnumerator() => throw new InvalidOperationException("Must not scan the unselected prefix.");
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
