using System.Collections;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using TasStudio.App;
using TasStudio.Core;
using TasStudio.Emulation;
using Xunit;

namespace TasStudio.Core.Tests;

public sealed class TimelinePerformanceTests
{
    [AvaloniaFact]
    public void UnchangedRefreshDoesNotRescanPollsOrUpdateTheViewport()
    {
        var polls = new CountingList<ulong>(100001, i => (ulong)i * 4);
        var timeline = new TimelineView { InputCount = 100000, PollBoundaries = polls };
        var updates = 0;
        timeline.ViewportChanged += () => updates++;
        timeline.Update(); polls.Reads.Clear();
        for (var i = 0; i < 100; i++) { timeline.PollBoundaries = polls; timeline.Update(); }
        Assert.Empty(polls.Reads);
        Assert.Equal(1, updates);
        timeline.PanTo(50000);
        Assert.Equal(2, updates);
        timeline.Position = 50010; timeline.Update();
        Assert.Equal(3, updates);
    }

    [AvaloniaFact]
    public void MillionFrameActiveRowOnlyReadsVisibleShadingChunks()
    {
        var inputs = new CountingList<ControllerState>(1000000,
            i => ControllerState.Neutral with { Buttons = i % 7 == 0 ? PadButtons.None : PadButtons.A });
        var timeline = new TimelineView { Inputs = inputs, InputCount = inputs.Count, FirstFrame = 456700, VisibleFrames = 600 };
        timeline.Update();
        timeline.Measure(new Size(1000, 150)); timeline.Arrange(new Rect(0, 0, 1000, 150));
        Draw(timeline);
        Assert.NotEmpty(inputs.Reads);
        Assert.All(inputs.Reads, index => Assert.InRange(index, 456700, 457300));
        Assert.Contains(456959, inputs.Reads); Assert.Contains(456960, inputs.Reads); // Across a chunk boundary.
        Assert.True(inputs.Reads.Count <= 601);
        Assert.Empty(timeline.Takes); // Rendering chunks never become logical takes.
        inputs.Reads.Clear(); timeline.PanTo(600000); Draw(timeline);
        Assert.NotEmpty(inputs.Reads);
        Assert.All(inputs.Reads, index => Assert.InRange(index, 600000, 600600));
    }

    [AvaloniaFact]
    public void VerticalScrollOnlyDrawsVisibleTakeRows()
    {
        var takes = new CountingList<InputTake>(2000, i => new InputTake(i.ToString(), "Take " + i, 0,
            [ControllerState.Neutral], "", "test"));
        var inputs = new CountingList<ControllerState>(1000000, _ => ControllerState.Neutral);
        var timeline = new TimelineView { Inputs = inputs, InputCount = inputs.Count, Takes = takes, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top };
        timeline.Update();
        var scroll = new ScrollViewer { Content = timeline };
        var window = new Window { Width = 1000, Height = 250, Content = scroll };
        try
        {
            window.Show(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            scroll.Offset = new Vector(0, 64 + 500 * 38);
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            takes.Reads.Clear(); inputs.Reads.Clear(); Draw(timeline);
            Assert.NotEmpty(takes.Reads);
            Assert.All(takes.Reads, index => Assert.InRange(index, 498, 510));
            Assert.True(takes.Reads.Count <= 12);
            Assert.Empty(inputs.Reads);
        }
        finally { window.Close(); }
    }

    [Fact]
    public async Task RecordingReusesUnchangedTakesAndKeepsPublishedSnapshotsImmutable()
    {
        using var files = new TestWorkspace();
        using var service = new ExecutionService(new FakeBackend { RecordPolls = true });
        await service.LoadGameAsync(files.GamePath, files.Options); await service.NewProjectAsync();
        await service.StepAsync();
        await service.CaptureTakeAsync("Keep", 0, 1);
        var inputs = service.Inputs; var polls = service.PollBoundaries; var takes = service.Takes;
        var tags = service.Tags; var sections = service.Sections; var markers = service.StateMarkers;
        Assert.Same(inputs, service.Inputs); Assert.Same(polls, service.PollBoundaries);
        await service.SetTimelineOffsetAsync(123);
        Assert.Same(inputs, service.Inputs); Assert.Same(polls, service.PollBoundaries);
        await service.StepAsync();
        Assert.Single(inputs); Assert.Equal(new ulong[] { 0, 4 }, polls);
        Assert.Equal(2, service.Inputs.Count); Assert.Equal(new ulong[] { 0, 4, 8 }, service.PollBoundaries);
        Assert.Same(takes, service.Takes); Assert.Same(takes[0].Inputs, service.Takes[0].Inputs);
        Assert.Same(tags, service.Tags); Assert.Same(sections, service.Sections); Assert.Same(markers, service.StateMarkers);
        var recorded = service.Inputs;
        var edited = ControllerState.Neutral with { Buttons = PadButtons.A };
        await service.SetInputAsync(0, edited);
        Assert.Equal(ControllerState.Neutral, recorded[0]); Assert.Equal(edited, service.Inputs[0]);
        await service.UndoAsync(); Assert.Equal(recorded, service.Inputs);
        await service.RedoAsync(); Assert.Equal(edited, service.Inputs[0]);
        await service.ClearLaterInputAsync(0);
        Assert.Single(service.Inputs); Assert.Equal(new ulong[] { 0, 4 }, service.PollBoundaries);
        Assert.Equal(new ulong[] { 0, 4 }, polls);
    }

    [Fact]
    public void PollPrefixesAppendWithoutReadingTheExistingMovieAndRegenerateChangedSuffixes()
    {
        var input = ControllerState.Neutral;
        var record = new RecordedInputFrame(0, "", new(input, 2, 1000, [new(0, 0, 0, input)]));
        var frames = new CountingFrames(record);
        var first = PollBoundarySnapshot.Empty.Update(100000, 0, frames);
        Assert.True(first.IsComplete); Assert.Equal(100000UL, first[^1]);
        frames.Reads = 0;
        var next = first.Update(100001, int.MaxValue, frames);
        Assert.Equal(1, frames.Reads);
        Assert.Equal(100001, first.Count); Assert.Equal(100000UL, first[^1]);
        Assert.Equal(100001UL, next[^1]); Assert.Equal(200002d, next.FieldsAt(100001));
        Assert.Same(next, next.Update(100001, int.MaxValue, frames));
        frames.Missing = 99999; frames.Reads = 0;
        var edited = next.Update(100001, 99999, frames);
        Assert.Equal(2, frames.Reads); Assert.False(edited.IsComplete);
        Assert.Equal(100000UL, edited[^1]); Assert.Equal(200001d, edited.FieldsAt(100001));
        Assert.True(next.IsComplete);
        var truncated = edited.Update(99999, 99999, frames);
        Assert.True(truncated.IsComplete); Assert.Equal(100000, truncated.Count);
    }

    private static void Draw(TimelineView timeline)
    {
        using var bitmap = new RenderTargetBitmap(new PixelSize(1000, 250));
        using var context = bitmap.CreateDrawingContext();
        timeline.Render(context);
    }

    private sealed class CountingList<T>(int count, Func<int, T> value) : IReadOnlyList<T>
    {
        public HashSet<int> Reads { get; } = [];
        public int Count => count;
        public T this[int index] { get { Reads.Add(index); return value(index); } }
        public IEnumerator<T> GetEnumerator() { for (var i = 0; i < Count; i++) yield return this[i]; }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class CountingFrames(RecordedInputFrame frame) : IReadOnlyDictionary<int, RecordedInputFrame>
    {
        public int Reads { get; set; }
        public int Missing { get; set; } = -1;
        public bool TryGetValue(int key, out RecordedInputFrame value) { Reads++; value = frame; return key != Missing; }
        public int Count => 100001;
        public RecordedInputFrame this[int key] => throw new NotSupportedException();
        public IEnumerable<int> Keys => throw new NotSupportedException();
        public IEnumerable<RecordedInputFrame> Values => throw new NotSupportedException();
        public bool ContainsKey(int key) => key != Missing;
        public IEnumerator<KeyValuePair<int, RecordedInputFrame>> GetEnumerator() => throw new NotSupportedException();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
