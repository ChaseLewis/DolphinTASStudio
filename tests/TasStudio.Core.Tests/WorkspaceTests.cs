using System.Security.Cryptography;
using System.Text.Json;
using TasStudio.Core;
using TasStudio.Emulation;
using Xunit;

namespace TasStudio.Core.Tests;

public sealed class WorkspaceTests
{
    [Fact]
    public async Task ElapsedTimeFollowsEmulatedTimeAcrossSeekAndProjectReopen()
    {
        using var w = new TestWorkspace(); var b = new FakeBackend { RecordPolls = true, FieldsPerStep = 2 };
        using var s = new ExecutionService(b); await Record(s, w, 120);
        Assert.Equal(4, s.ElapsedSeconds); // 120 presented frames at 30 FPS, regardless of poll count.
        await s.SeekAsync(30); Assert.Equal(1, s.ElapsedSeconds);
        await s.SaveNamedStateAsync("One second");
        await s.SeekAsync(120); Assert.Equal(4, s.ElapsedSeconds);
        await s.LoadMarkerAsync(Assert.Single(s.StateMarkers).Id); Assert.Equal(1, s.ElapsedSeconds);
        var path = w.FilePath("timed.tasproj");
        await s.SaveProjectAsync(path); await s.LoadProjectAsync(path, w.Options);
        Assert.Equal(1, s.ElapsedSeconds);
        await s.SeekAsync(0); Assert.Equal(0, s.ElapsedSeconds);
    }

    private const uint Address = 0x80000000;
    private static readonly ControllerState A = ControllerState.Neutral with { Buttons = PadButtons.A };
    private static async Task Record(ExecutionService service, TestWorkspace workspace, int count = 4)
    {
        await service.LoadGameAsync(workspace.GamePath, workspace.Options); await service.NewProjectAsync();
        for (var i = 0; i < count; i++) await service.StepAsync();
    }
    [Fact]
    public async Task ManualStateIsRemovedOnlyAfterChangedPrefixAndUndoDoesNotResurrectIt()
    {
        using var w = new TestWorkspace(); var backend = new FakeBackend(); using var s = new ExecutionService(backend);
        await Record(s, w); await s.SeekAsync(2); await s.SaveNamedStateAsync("At two");
        var marker = Assert.Single(s.StateMarkers); var hash = await s.HistoryAtAsync(2);
        await s.SetInputAsync(2, A); Assert.True(Assert.Single(s.StateMarkers).Valid);
        await s.SetInputAsync(1, A); Assert.Empty(s.StateMarkers);
        var restores = backend.Calls.Count(c => c.Operation == nameof(FakeBackend.Restore));
        await Assert.ThrowsAsync<InvalidDataException>(() => s.LoadMarkerAsync(marker.Id));
        Assert.Equal(restores, backend.Calls.Count(c => c.Operation == nameof(FakeBackend.Restore)));
        await s.UndoAsync(); Assert.Equal(hash, await s.HistoryAtAsync(2)); Assert.Empty(s.StateMarkers);
    }
    [Fact]
    public async Task BoundaryEventsInvalidateSamePositionStateAndDoNotReplayTwice()
    {
        using var w = new TestWorkspace(); using var s = new ExecutionService(new FakeBackend()); await Record(s, w);
        await s.SeekAsync(2); await s.SaveNamedStateAsync("Before write"); var before = await s.HistoryAtAsync(2);
        await s.WriteMemoryAsync(Address, BitConverter.GetBytes(77));
        Assert.Empty(s.StateMarkers); Assert.NotEqual(before, await s.HistoryAtAsync(2));
        await s.SaveNamedStateAsync("After write"); var after = s.StateMarkers.Last();
        await s.SeekAsync(4); await s.LoadMarkerAsync(after.Id);
        Assert.Equal(77, BitConverter.ToInt32(await s.ReadMemoryAsync(Address, 4)));
        await s.UndoAsync(); Assert.Empty(s.StateMarkers);
    }
    [Fact]
    public async Task CheckpointInvalidationAndNearestManualAnchorAvoidReplayingPrefix()
    {
        using var w = new TestWorkspace(); var b = new FakeBackend(); using var s = new ExecutionService(b); await Record(s, w, 0);
        await s.ConfigureCheckpointsAsync(new(IntervalSeconds: 5));
        for (var i = 0; i < 310; i++) await s.StepAsync();
        Assert.Contains(s.StateMarkers, m => m.Automatic && m.Position == 300 && m.Valid);
        await s.SeekAsync(200); await s.SaveNamedStateAsync("Anchor"); await s.SetInputAsync(299, A);
        Assert.DoesNotContain(s.StateMarkers, m => m.Automatic && m.Position == 300);
        await s.SeekAsync(0); var count = b.SubmittedInputs.Count; await s.SeekAsync(305);
        Assert.Equal(105, b.SubmittedInputs.Count - count);
        Assert.Contains(s.StateMarkers, m => m.Automatic && m.Position == 300 && m.Valid);
        var expected = await s.ReadMemoryAsync(Address, 4); await s.SeekAsync(0); await s.SeekAsync(305);
        Assert.Equal(expected, await s.ReadMemoryAsync(Address, 4));
    }
    [Theory]
    [InlineData("initial", 0)]
    [InlineData("manual", 2)]
    [InlineData("automatic", 300)]
    public async Task EverySeekRestoresNearestSavedBaselineEvenWhenPreviewIsCurrent(string kind, int anchor)
    {
        using var w = new TestWorkspace(); var b = new FakeBackend { RecordPolls = true };
        using var s = new ExecutionService(b); await Record(s, w, 0);
        await s.ConfigureCheckpointsAsync(new(IntervalSeconds: 5));
        for (var i = 0; i < anchor + 8; i++) await s.StepAsync();
        if (kind == "manual") { await s.SeekAsync((ulong)anchor); await s.SaveNamedStateAsync("Anchor"); }
        if (kind == "automatic") Assert.Contains(s.StateMarkers, m => m.Automatic && m.Position == (ulong)anchor);
        await s.SeekAsync((ulong)anchor + 3);
        foreach (var target in new[] { anchor + 6, anchor + 6, anchor })
        {
            var restores = b.Calls.Count(c => c.Operation == "Restore"); var steps = b.SubmittedInputs.Count;
            await s.SeekAsync((ulong)target);
            Assert.Equal(restores + 1, b.Calls.Count(c => c.Operation == "Restore"));
            Assert.Equal(target - anchor, b.SubmittedInputs.Count - steps);
            Assert.Equal((ulong)target, s.Position); Assert.True(s.IsPreviewCurrent);
        }
    }

    [Fact]
    public async Task SeekingAfterSuccessiveEditsReplaysFromLastValidState()
    {
        using var w = new TestWorkspace(); var b = new FakeBackend { RecordPolls = true };
        using var s = new ExecutionService(b); await Record(s, w, 8);
        await s.SeekAsync(2); await s.SaveNamedStateAsync("Keep");
        await s.SeekAsync(6); await s.SaveNamedStateAsync("Invalidate");
        await s.SeekAsync(3); await s.SetInputAsync(3, A);
        await s.SeekAsync(5); await s.SetInputAsync(5, A with { StickX = 217 });
        Assert.Equal("Keep", Assert.Single(s.StateMarkers).Name);
        var restores = b.Calls.Count(c => c.Operation == "Restore"); var steps = b.SubmittedInputs.Count;
        await s.SeekAsync(8);
        Assert.Equal(restores + 1, b.Calls.Count(c => c.Operation == "Restore"));
        Assert.Equal(6, b.SubmittedInputs.Count - steps);
        var expected = await s.ReadMemoryAsync(Address, 4);
        await s.SetInputAsync(1, A); Assert.Empty(s.StateMarkers);
        steps = b.SubmittedInputs.Count;
        await s.SeekAsync(8); Assert.Equal(8, b.SubmittedInputs.Count - steps);
        Assert.NotEqual(expected, await s.ReadMemoryAsync(Address, 4));
        expected = await s.ReadMemoryAsync(Address, 4);
        await s.SeekAsync(8); Assert.Equal(expected, await s.ReadMemoryAsync(Address, 4));
    }

    [Fact]
    public async Task CandidateEditingDoesNotChangeActiveHistoryAndUseSectionSupportsUndo()
    {
        using var w = new TestWorkspace(); using var s = new ExecutionService(new FakeBackend()); await Record(s, w);
        var baseline = await s.HistoryAtAsync(4); var id = await s.CaptureTakeAsync("Alternative", 1, 2);
        await s.SetTakeInputAsync(id, 1, A); Assert.Equal(baseline, await s.HistoryAtAsync(4)); Assert.True(s.IsPreviewCurrent);
        await s.UseTakeAsync(id, 1, 1); Assert.Equal(A, s.Inputs[1]); Assert.False(s.IsPreviewCurrent);
        Assert.Equal(ControllerState.Neutral, s.Inputs[2]); Assert.Single(s.Takes);
        await s.UndoAsync(); Assert.Equal(baseline, await s.HistoryAtAsync(4)); Assert.True(s.IsPreviewCurrent);
        await s.RedoAsync(); await s.SeekAsync(4); Assert.True(s.IsPreviewCurrent);
        await s.SetInputAsync(0, A); await Assert.ThrowsAsync<InvalidDataException>(() => s.UseTakeAsync(id, 1, 1));
    }
    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(6)]
    [InlineData(8)]
    public async Task ApplyingTakeAtCursorStartsExactlyThereWithoutShiftingLaterInputs(int target)
    {
        using var w = new TestWorkspace(); using var s = new ExecutionService(new FakeBackend { RecordPolls = true });
        await Record(s, w, 8);
        await s.SetInputAsync(7, A with { Buttons = PadButtons.Z });
        var before = s.Inputs.ToArray();
        var id = await s.CaptureTakeAsync("Relocate", 1, 3);
        var source = new[] { A, A with { Buttons = PadButtons.B }, A with { Buttons = PadButtons.X } };
        for (var i = 0; i < source.Length; i++) await s.SetTakeInputAsync(id, 1 + i, source[i]);
        await s.AddTagAsync(7, "Keep position");
        await s.ApplyTakeAtCursorAsync(id, target);
        Assert.Equal(8UL, s.Position); Assert.Empty(s.Takes);
        Assert.Equal(Math.Max(8, target + 3), s.Inputs.Count);
        for (var i = 0; i < s.Inputs.Count; i++)
            Assert.Equal(i >= target && i < target + 3 ? source[i - target] : before[i], s.Inputs[i]);
        Assert.Equal(7UL, Assert.Single(s.Tags).Position);
        Assert.Equal(target, Assert.Single(s.Sections).Start);
        await s.SeekAsync((ulong)target + 1);
        Assert.All((await s.GetPollFrameAsync(target))!.Frame.Polls, poll => Assert.Equal(source[0], poll.Input));
        await s.UndoAsync(); Assert.Equal(before, s.Inputs); Assert.Equal(id, Assert.Single(s.Takes).Id);
        await s.RedoAsync(); Assert.Equal(source[0], s.Inputs[target]); Assert.Empty(s.Takes);
    }

    [Fact]
    public async Task AuditionLeavesActiveMovieAndSavedStatesIntact()
    {
        using var w = new TestWorkspace(); using var s = new ExecutionService(new FakeBackend()); await Record(s, w);
        await s.SaveNamedStateAsync("Original end"); var expected = await s.ReadMemoryAsync(Address, 4);
        var id = await s.CaptureTakeAsync("Audition", 0, 4); await s.SetTakeInputAsync(id, 0, A);
        await s.AuditionTakeAsync(id, 4); Assert.All(s.Inputs, p => Assert.Equal(ControllerState.Neutral, p));
        Assert.False(s.IsPreviewCurrent); Assert.True(Assert.Single(s.StateMarkers).Valid);
        Assert.NotEqual(expected, await s.ReadMemoryAsync(Address, 4)); await s.SeekAsync(4);
        Assert.Equal(expected, await s.ReadMemoryAsync(Address, 4));
    }
    [Fact]
    public async Task RangeEditingPreservesUnchangedAxesAndButtons()
    {
        using var w = new TestWorkspace(); using var s = new ExecutionService(new FakeBackend()); await Record(s, w);
        await s.SetInputAsync(1, A with { StickX = 7 });
        await s.EditRangeAsync(0, 3, null, ControllerState.Neutral with { Buttons = PadButtons.B }, PadButtons.B, 0);
        Assert.Equal(PadButtons.A | PadButtons.B, s.Inputs[1].Buttons); Assert.Equal(7, s.Inputs[1].StickX);
        Assert.Equal(ControllerState.Neutral, s.Inputs[3]); await s.UndoAsync(); Assert.Equal(A with { StickX = 7 }, s.Inputs[1]);
    }
    [Fact]
    public async Task FolderProjectPreservesTakesStatesAndRelocatesIdenticalRom()
    {
        using var w = new TestWorkspace(); using var s = new ExecutionService(new FakeBackend()); await Record(s, w);
        var id = await s.CaptureTakeAsync("Script 03", 1, 2); await s.SetTakeInputAsync(id, 1, A); await s.UseTakeAsync(id, 1, 2);
        await s.SeekAsync(4); await s.SaveNamedStateAsync("End");
        var path = w.FilePath("movie.tasproj"); await s.SaveProjectAsync(path);
        Assert.False(FolderProject.IsLegacy(path)); Assert.True(Directory.Exists(w.FilePath("inputs"))); Assert.True(Directory.Exists(w.FilePath("states")));
        var content = FolderProject.Load(path); Assert.Single(content.Takes); Assert.Single(content.States); Assert.Single(content.Sections);
        var expected = await s.ReadMemoryAsync(Address, 4); var newRom = w.FilePath("relocated.iso"); File.Move(w.GamePath, newRom);
        await Assert.ThrowsAsync<FileNotFoundException>(() => s.LoadProjectAsync(path, w.Options));
        Assert.True(s.HasProject); Assert.Equal(expected, await s.ReadMemoryAsync(Address, 4));
        await s.LoadProjectAsync(path, w.Options, newRom); Assert.Equal(expected, await s.ReadMemoryAsync(Address, 4));
        Assert.True(Assert.Single(s.StateMarkers).Valid); Assert.Single(s.Takes);
        await s.SetInputAsync(0, A); Assert.Empty(s.StateMarkers); await s.SaveProjectAsync(path);
        await s.LoadProjectAsync(path, w.Options, newRom); Assert.Empty(s.StateMarkers);
    }
    [Fact]
    public async Task FailedFolderCommitPreservesManifestAndLegacyImportPreservesSource()
    {
        using var w = new TestWorkspace(); using var s = new ExecutionService(new FakeBackend()); await Record(s, w);
        var path = w.FilePath("movie.tasproj"); await s.SaveProjectAsync(path); var previous = File.ReadAllBytes(path); var content = FolderProject.Load(path);
        Assert.Throws<InvalidDataException>(() => FolderProject.Save(path, content.Archive.Metadata with { StateHash = "bad" }, content.Archive.InitialState, [], [], []));
        Assert.Equal(previous, File.ReadAllBytes(path));
        var legacy = w.FilePath("legacy.tasproj"); ProjectArchive.Save(legacy, content.Archive.Metadata, content.Archive.InitialState); var original = File.ReadAllBytes(legacy);
        await s.LoadProjectAsync(legacy, w.Options); await Assert.ThrowsAsync<InvalidOperationException>(() => s.SaveProjectAsync(legacy));
        Assert.Equal(original, File.ReadAllBytes(legacy));
    }
    [Fact]
    public async Task ExportBakesActiveInputsAndEventsWithoutCandidates()
    {
        using var w = new TestWorkspace(); using var s = new ExecutionService(new FakeBackend()); await Record(s, w);
        var id = await s.CaptureTakeAsync("Candidate", 0, 4); await s.SetTakeInputAsync(id, 1, A); await s.UseTakeAsync(id, 0, 4);
        await s.SeekAsync(4); var expected = await s.ReadMemoryAsync(Address, 4);
        var path = w.FilePath("baked.tasreplay"); await s.ExportReplayAsync(path); await s.LoadProjectAsync(path, w.Options);
        Assert.Empty(s.Takes); await s.SeekAsync(4); Assert.Equal(expected, await s.ReadMemoryAsync(Address, 4));
    }
    [Fact]
    public void IncrementalHashMatchesFreshComputationAndTracksEventsAndRoot()
    {
        var inputs = Enumerable.Repeat(ControllerState.Neutral, 700).ToList(); var events = new List<ExecutionEvent>();
        var history = new ExecutionHistory("root", inputs, events); var old = history.At(600); var prefix = history.At(500);
        inputs[500] = A; history.Invalidate(500); Assert.Equal(prefix, history.At(500)); Assert.NotEqual(old, history.At(600));
        Assert.Equal(new ExecutionHistory("root", inputs, events).At(600), history.At(600));
        events.Add(new(500, ExecutionEventKind.Reset)); history.Invalidate(500);
        Assert.NotEqual(prefix, history.At(500)); Assert.Equal(new ExecutionHistory("root", inputs, events).At(600), history.At(600));
        Assert.NotEqual(new ExecutionHistory("other root", inputs, events).At(600), history.At(600));
    }
    [Fact]
    public async Task FailedNativeProjectRestoreRecoversPreviousSessionAndTimeline()
    {
        using var w = new TestWorkspace(); var b = new FakeBackend(); using var s = new ExecutionService(b); await Record(s, w);
        await s.SetInputAsync(1, A); await s.SeekAsync(4); var expected = await s.ReadMemoryAsync(Address, 4);
        var path = w.FilePath("rollback.tasproj"); await s.SaveProjectAsync(path); var revision = s.Revision;
        b.FailNextRestore = true;
        await Assert.ThrowsAsync<InvalidDataException>(() => s.LoadProjectAsync(path, w.Options));
        Assert.True(s.HasProject); Assert.True(s.IsPreviewCurrent); Assert.Equal(4UL, s.Position); Assert.Equal(revision, s.Revision);
        Assert.Equal(A, s.Inputs[1]); Assert.Equal(expected, await s.ReadMemoryAsync(Address, 4));
        await s.UndoAsync(); Assert.Equal(ControllerState.Neutral, s.Inputs[1]);
    }
    [Fact]
    public async Task CandidateEventsAreValidatedAndCandidateEditsAreUndoable()
    {
        using var w = new TestWorkspace(); using var s = new ExecutionService(new FakeBackend()); await Record(s, w);
        var id = await s.CaptureTakeAsync("Candidate", 0, 4); await s.SetTakeInputAsync(id, 1, A);
        await s.UndoAsync(); Assert.Equal(ControllerState.Neutral, s.Takes[0].Inputs[1]);
        await s.RedoAsync(); Assert.Equal(A, s.Takes[0].Inputs[1]);
        await s.SeekAsync(2); await s.ResetAsync();
        await Assert.ThrowsAsync<InvalidDataException>(() => s.UseTakeAsync(id, 0, 4));
    }
}
