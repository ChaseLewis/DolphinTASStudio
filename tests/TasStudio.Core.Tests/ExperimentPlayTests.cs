using System.Text.Json;
using System.Diagnostics;
using System.Security;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Data.Sqlite;
using TasStudio.App;
using TasStudio.Core;
using TasStudio.Emulation;
using TasStudio.Sdk;
using Xunit;

namespace TasStudio.Core.Tests;

public sealed class ExperimentPlayTests
{
    public sealed record ScoreResult(double Score);
    private sealed class Script(Func<ExperimentRunContext, Task<double>> run) : IExperiment<ScoreResult>
    {
        public async Task<ScoreResult> RunAsync(ExperimentRunContext context, CancellationToken token) => new(await run(context));
    }
    private static FakeBackend Backend() => new() { RecordPolls = true };
    private static async Task<(string Path, string State)> Source(TestWorkspace files, ExecutionService source)
    {
        await source.LoadGameAsync(files.GamePath, files.Options); await source.NewProjectAsync();
        for (var i = 0; i < 3; i++) await source.AdvanceFrameAsync(ControllerState.Neutral);
        await source.SaveNamedStateAsync("Start");
        var state = Assert.Single(source.StateMarkers).Id;
        var path = files.FilePath("source/project.tasproj"); await source.SaveProjectAsync(path);
        return (path, state);
    }
    private static ExperimentJob Job(TestWorkspace files, string path, string state, ExperimentTopPlays? top = null) =>
        new("Play test", path, files.Options.CorePath, files.Options.SystemDirectory, files.FilePath("trial"),
            ExperimentStart.SaveState, state, null, JsonSerializer.SerializeToElement(new { }), 30, 0, 1,
            KeepSuccessfulArtifact: false) { TopPlays = top };

    [Fact]
    public async Task ExistingCallsRemainOptionalAndRestorePreservesInputByDefault()
    {
        using var files = new TestWorkspace(); using var source = new ExecutionService(Backend());
        var (path, state) = await Source(files, source); using var worker = new ExecutionService(Backend());
        var result = await ExperimentWorker.RunAsync(Job(files, path, state), worker, default, new Script(async context =>
        {
            await context.Emulator.AdvanceAsync(ControllerState.Neutral, 4);
            await context.Emulator.LoadStateAsync(state);
            Assert.Equal(7, (await context.Emulator.GetPositionAsync()).InputCount);
            await context.SubmitPlayAsync(50); // Retention disabled, including at an empty range.
            return 50;
        }));
        Assert.Equal("completed", result.Status); Assert.Null(result.Plays); Assert.Null(result.ProjectPath);
        Assert.NotNull(typeof(IExperimentEmulator).GetMethod("LoadStateAsync", [typeof(string)]));
        Assert.NotNull(typeof(ExperimentRunContext).GetConstructor([typeof(ExperimentInitializationContext), typeof(IExperimentEmulator),
            typeof(IEnumerable<ControllerState>), typeof(Action<object>)]));
    }

    [Fact]
    public async Task RetryTrimsAbandonedSuffixAndSubmissionFreezesInputsPollsAndEvents()
    {
        using var files = new TestWorkspace(); using var source = new ExecutionService(Backend());
        var (path, state) = await Source(files, source); using var worker = new ExecutionService(Backend());
        var a = ControllerState.Neutral with { Buttons = PadButtons.A };
        var b = ControllerState.Neutral with { Buttons = PadButtons.B, TriggerL = 42 };
        byte[]? winningMemory = null;
        var result = await ExperimentWorker.RunAsync(Job(files, path, state, new(Keep: 2)), worker, default, new Script(async context =>
        {
            var emulator = context.Emulator;
            await emulator.AdvanceAsync(a, 2); // Shared successful prefix, groups 3 and 4.
            await emulator.WriteMemoryAsync(0x80000020, BitConverter.GetBytes(11)); // Included in checkpoint.
            var checkpoint = await emulator.SaveStateAsync("Branch");
            await emulator.AdvanceAsync(a, 4);
            await emulator.WriteMemoryAsync(0x80000020, BitConverter.GetBytes(99)); // Abandoned event.
            await emulator.LoadStateAsync(checkpoint, clearLaterInput: true);
            Assert.Equal(5, (await emulator.GetPositionAsync()).InputCount);
            Assert.Equal(BitConverter.GetBytes(11), await emulator.ReadMemoryAsync(0x80000020, 4));
            await emulator.AdvanceAsync(b, 2);
            winningMemory = await emulator.ReadMemoryAsync(0x80000020, 4);
            await context.SubmitPlayAsync(100, "Winner");
            await emulator.LoadStateAsync(state, clearLaterInput: true);
            Assert.Equal(3, (await emulator.GetPositionAsync()).InputCount);
            await emulator.AdvanceAsync(a);
            await context.SubmitPlayAsync(80, "Second");
            await emulator.LoadStateAsync(state, clearLaterInput: true);
            await emulator.AdvanceAsync(b);
            await context.SubmitPlayAsync(10, "Discard");
            return 1000; // Explicit submissions suppress automatic final capture.
        }));
        Assert.Equal("completed", result.Status); Assert.Null(result.PlayWarning);
        Assert.Equal(new[] { 100d, 80d }, result.Plays!.Select(p => p.Score));
        var play = result.Plays![0]; var data = ExperimentPlayData.Decode(play);
        Assert.Equal(3, play.Start); Assert.Equal(4, play.Length);
        Assert.Equal(new[] { a, a, b, b }, data.Inputs);
        Assert.Equal(4, data.PollFrames.Length);
        Assert.Equal(5UL, Assert.Single(data.Events).Position);
        Assert.Equal(BitConverter.GetBytes(11), data.Events[0].Bytes);

        // Applying replaces only the play's range, with a single undo entry.
        for (var i = 0; i < 7; i++) await source.AdvanceFrameAsync(ControllerState.Neutral);
        var before = source.Inputs.ToArray();
        await source.ApplyExperimentPlayAsync(play);
        Assert.Equal(before.Take(3).Concat(data.Inputs).Concat(before.Skip(7)), source.Inputs);
        Assert.False(source.IsPreviewCurrent);
        await source.SeekAsync(7);
        Assert.Equal(winningMemory, await source.ReadMemoryAsync(0x80000020, 4));
        await source.UndoAsync(); Assert.Equal(before, source.Inputs);
        await source.RedoAsync(); Assert.Equal(data.Inputs, source.Inputs.Skip(3).Take(4));
        await source.SetInputAsync(0, a);
        var changed = source.Inputs.ToArray();
        await Assert.ThrowsAsync<ExperimentPlayHistoryMismatchException>(() => source.ApplyExperimentPlayAsync(play));
        Assert.Equal(changed, source.Inputs);
        await source.ApplyExperimentPlayAsync(play, allowHistoryMismatch: true);
        Assert.Equal(changed.Take(3).Concat(data.Inputs).Concat(changed.Skip(7)), source.Inputs);
        Assert.Null(await source.GetPollFrameAsync(3)); // old-baseline timing must not be replayed
        await source.SeekAsync(7);
        Assert.NotNull(await source.GetPollFrameAsync(3)); // recreated by current playback
        Assert.Equal(winningMemory, await source.ReadMemoryAsync(0x80000020, 4));
        await source.UndoAsync();
        Assert.Equal(changed, source.Inputs);
    }

    [Fact]
    public async Task ApplyAsTakePreservesActiveMovieAndRoundtripsEventsPollsAndUndo()
    {
        using var files = new TestWorkspace(); var backend = Backend(); using var source = new ExecutionService(backend);
        var (path, state) = await Source(files, source); using var worker = new ExecutionService(Backend());
        byte[]? winningMemory = null;
        var result = await ExperimentWorker.RunAsync(Job(files, path, state, new()), worker, default, new Script(async context =>
        {
            await context.Emulator.AdvanceAsync(ControllerState.Neutral with { Buttons = PadButtons.A }, 2);
            await context.Emulator.WriteMemoryAsync(0x80000020, BitConverter.GetBytes(47));
            await context.Emulator.AdvanceAsync(ControllerState.Neutral with { Buttons = PadButtons.B }, 2);
            winningMemory = await context.Emulator.ReadMemoryAsync(0x80000020, 4);
            return 100;
        }));
        var play = Assert.Single(result.Plays!);
        var before = source.Inputs.ToArray(); var memory = await source.ReadMemoryAsync(0x80000020, 4);
        var hash = await source.HistoryAtAsync(3); var markers = source.StateMarkers.ToArray();
        var calls = backend.Calls.ToArray();
        var id = await source.ApplyExperimentPlayAsTakeAsync(play);
        Assert.Equal(calls, backend.Calls.ToArray()); Assert.Equal(before, source.Inputs);
        Assert.Equal(3UL, source.Position); Assert.True(source.IsPreviewCurrent);
        Assert.Equal(markers, source.StateMarkers); Assert.Equal(hash, await source.HistoryAtAsync(3));
        var take = Assert.Single(source.Takes); Assert.Equal(play.Name, take.Name); Assert.Equal(4, take.Inputs.Length);
        await source.UndoAsync(); Assert.Empty(source.Takes); Assert.Equal(before, source.Inputs);
        await source.RedoAsync(); Assert.Equal(id, Assert.Single(source.Takes).Id);

        foreach (var recovery in new[] { false, true })
        {
            var saved = files.FilePath(recovery ? "recovery/recovery.tasproj" : "saved/project.tasproj");
            if (recovery) { await source.SaveRecoveryAsync(saved); await source.SaveRecoveryAsync(saved); }
            else await source.SaveProjectAsync(saved);
            using var reopened = new ExecutionService(Backend()); await reopened.LoadProjectAsync(saved, files.Options);
            Assert.Equal(id, Assert.Single(reopened.Takes).Id);
            await reopened.AuditionTakeAsync(id, 7);
            Assert.Equal(winningMemory, await reopened.ReadMemoryAsync(0x80000020, 4));
            Assert.Equal(before, reopened.Inputs); Assert.Null(await reopened.GetPollFrameAsync(3));
            Assert.Equal(new ulong[] { 0, 4, 8, 12 }, reopened.PollBoundaries);
            await reopened.SeekAsync(3); Assert.Equal(memory, await reopened.ReadMemoryAsync(0x80000020, 4));
            await reopened.UseTakeAsync(id, 3, 4, removeTake: true);
            Assert.Empty(reopened.Takes); Assert.Equal(before.Concat(take.Inputs), reopened.Inputs);
            Assert.NotNull(await reopened.GetPollFrameAsync(3));
            await reopened.SeekAsync(7); Assert.Equal(winningMemory, await reopened.ReadMemoryAsync(0x80000020, 4));
            await reopened.UndoAsync(); Assert.Equal(before, reopened.Inputs); Assert.Equal(id, Assert.Single(reopened.Takes).Id);
            await reopened.RedoAsync(); Assert.Empty(reopened.Takes); Assert.Equal(7, reopened.Inputs.Count);
        }
    }

    [Fact]
    public async Task ExperimentTakeRejectsMismatchUnlessOverriddenAndMovesItsEventsWithEditedInputs()
    {
        using var files = new TestWorkspace(); using var source = new ExecutionService(Backend());
        await Source(files, source);
        var a = ControllerState.Neutral with { Buttons = PadButtons.A };
        var play = new ExperimentPlay(0, "Different history", 10, 3, 2, new ExperimentPlayData("different", [a, a],
            [new(4, ExecutionEventKind.MemoryWrite, 0x80000020, BitConverter.GetBytes(21))], []).Encode());
        await Assert.ThrowsAsync<ExperimentPlayHistoryMismatchException>(() => source.ApplyExperimentPlayAsTakeAsync(play));
        Assert.Empty(source.Takes);
        await Assert.ThrowsAsync<InvalidDataException>(() => source.ApplyExperimentPlayAsTakeAsync(play with { Length = 3 }, true));
        Assert.Empty(source.Takes);
        var id = await source.ApplyExperimentPlayAsTakeAsync(play, true);
        await source.SetTakeInputAsync(id, 3, ControllerState.Neutral);
        await source.ApplyTakeAtCursorAsync(id, 1);
        Assert.Empty(source.Takes); Assert.Equal(ControllerState.Neutral, source.Inputs[1]); Assert.Equal(a, source.Inputs[2]);
        var path = files.FilePath("moved/project.tasproj"); await source.SaveProjectAsync(path);
        var metadata = FolderProject.Load(path).Archive.Metadata;
        Assert.Equal(2UL, Assert.Single(metadata.Events).Position);
        Assert.Equal(BitConverter.GetBytes(21), metadata.Events[0].Bytes);
        await source.UndoAsync(); Assert.Equal(id, Assert.Single(source.Takes).Id);
    }

    [AvaloniaFact]
    public void ExperimentTakeBeyondActiveEndCanBeSelectedAndNavigated()
    {
        var timeline = new TimelineView { InputCount = 3, VisibleFrames = 10,
            PollBoundaries = [0, 4, 8, 12],
            Takes = [new("take", "Long experiment", 3, Enumerable.Repeat(ControllerState.Neutral, 100).ToArray(), "", "Experiment")] };
        timeline.SetSelection(3, 103, "take");
        Assert.Equal(103, timeline.SelectionEnd);
        timeline.SetSelection(99, 100, "take"); timeline.MoveCursor(1);
        Assert.Equal(100, timeline.SelectedFrame); Assert.True(timeline.FirstFrame > 3);
        timeline.SetSelection(102, 103, "take"); timeline.MoveCursor(1); Assert.Equal(102, timeline.SelectedFrame);
        timeline.SetSelection(3, 4); timeline.MoveCursor(1); Assert.Equal(3, timeline.SelectedFrame);
    }

    [Fact]
    public async Task ExplicitOverrideAppendsButStillRejectsMissingPrefixAndMalformedPayload()
    {
        using var files = new TestWorkspace(); using var source = new ExecutionService(Backend());
        await Source(files, source);
        var before = source.Inputs.ToArray();
        var a = ControllerState.Neutral with { Buttons = PadButtons.A };
        var play = new ExperimentPlay(0, "Other baseline", 10, 3, 2,
            new ExperimentPlayData("different", [a, a], [], []).Encode());
        await Assert.ThrowsAsync<ExperimentPlayHistoryMismatchException>(() => source.ApplyExperimentPlayAsync(play));
        Assert.Equal(before, source.Inputs);
        await Assert.ThrowsAsync<InvalidDataException>(() => source.ApplyExperimentPlayAsync(play with { Start = 4 }, true));
        await Assert.ThrowsAsync<InvalidDataException>(() => source.ApplyExperimentPlayAsync(play with { Length = 3 }, true));
        Assert.Equal(before, source.Inputs);
        await source.ApplyExperimentPlayAsync(play, true);
        Assert.Equal(before.Concat(new[] { a, a }), source.Inputs);
        await source.UndoAsync();
        Assert.Equal(before, source.Inputs);
        await source.RedoAsync();
        Assert.Equal(before.Concat(new[] { a, a }), source.Inputs);
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    [InlineData(null)]
    public async Task MismatchWarningRequiresExplicitApplyAndClosingCancels(bool? choice)
    {
        var owner = new Window();
        var warning = new ExperimentPlayMismatchWindow("Three berries", 7095, 1268);
        try
        {
            owner.Show();
            var result = warning.ShowDialog<bool>(owner);
            warning.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            var buttons = warning.GetVisualDescendants().OfType<Button>().ToArray();
            var apply = buttons.Single(b => b.Name == "ConfirmMismatchedPlay");
            var cancel = buttons.Single(b => b.Name == "CancelMismatchedPlay");
            Assert.False(apply.IsDefault);
            Assert.True(cancel.IsDefault);
            Assert.True(cancel.IsCancel);
            Assert.Contains(warning.GetVisualDescendants().OfType<TextBlock>(), b => b.Text!.Contains("7095, 8363"));
            if (choice is null) warning.Close();
            else (choice.Value ? apply : cancel).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(choice == true, await result);
        }
        finally { warning.Close(); owner.Close(); }
    }

    [Fact]
    public async Task AutomaticCaptureNeedsNoSubmissionAndReportsMissingScoreWithoutLosingResults()
    {
        using var files = new TestWorkspace(); using var source = new ExecutionService(Backend());
        var (path, state) = await Source(files, source);
        foreach (var field in new[] { "Score", "Missing" })
        {
            using var worker = new ExecutionService(Backend());
            var result = await ExperimentWorker.RunAsync(Job(files, path, state, new(ScoreField: field)), worker, default,
                new Script(async context => { await context.Emulator.AdvanceAsync(count: 2); return 12; }));
            Assert.Equal("completed", result.Status); Assert.Equal(12, result.Value!.Value.GetProperty("Score").GetDouble());
            if (field == "Score") { Assert.Equal(2, Assert.Single(result.Plays!).Length); Assert.Null(result.PlayWarning); }
            else { Assert.Null(result.Plays); Assert.Contains("Missing", result.PlayWarning); }
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SqliteKeepsGlobalTopKDeterministicallyAcrossCleanupAndResume(bool higher)
    {
        using var files = new TestWorkspace();
        var options = new ExperimentTopPlays(HigherIsBetter: higher, Keep: 2);
        ExperimentFiles.Write(files.FilePath("experiment.json"), new ExperimentDefinition(1, "Test", ExperimentStart.Boot, null, null, 1, 30,
            Enumerable.Range(0, 4).Select(i => new ExperimentTrial("Trial " + i, JsonSerializer.SerializeToElement(new { }))).ToArray()) { TopPlays = options });
        ExperimentResult Result(int index, double score) => new("Test", "completed", null, DateTimeOffset.UtcNow, 1, 1, 1, 1,
            JsonSerializer.SerializeToElement(new ScoreResult(score)), null, index)
        {
            Plays = [new(0, "Play " + index, score, 0, 1,
                new ExperimentPlayData("baseline", [ControllerState.Neutral], [], []).Encode())]
        };
        await using (var writer = new SerializedExperimentWriter(new SqliteExperimentWriter(typeof(ScoreResult), topPlays: options)))
        {
            await writer.InitializeAsync(new(files.DirectoryPath, "Test", 4), default);
            foreach (var index in new[] { 2, 1, 0 }) await writer.WriteAsync(Result(index, index == 2 ? 5 : 10));
        }
        var expected = higher ? new[] { 0, 1 } : new[] { 2, 0 };
        Assert.Equal(expected, ExperimentPlayResults.Read(files.DirectoryPath).Select(p => p.TrialIndex));
        var trial = files.FilePath("run-00001"); Directory.CreateDirectory(trial); File.WriteAllText(Path.Combine(trial, "temporary"), "delete me");
        ExperimentArtifactCleanup.RemoveTrial(files.DirectoryPath, 0, null);
        Assert.False(Directory.Exists(trial));
        await using (var sqlite = new SqliteExperimentWriter(typeof(ScoreResult), resume: true, topPlays: options))
        {
            await sqlite.InitializeAsync(new(files.DirectoryPath, "Test", 4), default);
            Assert.Equal(3, sqlite.ReadTrials().Count);
            Assert.All(sqlite.ReadTrials().Values, result => Assert.Null(result.Plays));
            await using var writer = new SerializedExperimentWriter(sqlite);
            await writer.WriteAsync(Result(0, 10) with { Plays = null }); // Legacy cleanup must preserve retained plays.
            await writer.WriteAsync(Result(3, higher ? 20 : 1));
        }
        var retained = ExperimentPlayResults.Read(files.DirectoryPath);
        Assert.Equal(new[] { 3, higher ? 0 : 2 }, retained.Select(p => p.TrialIndex));
        var loaded = ExperimentPlayResults.Load(files.DirectoryPath, 3, 0);
        Assert.Equal(ControllerState.Neutral, Assert.Single(ExperimentPlayData.Decode(loaded).Inputs));
        using var db = new SqliteConnection($"Data Source={files.FilePath("results.sqlite")};Pooling=False"); db.Open();
        using var query = db.CreateCommand(); query.CommandText = "SELECT COUNT(*) FROM trials";
        Assert.Equal(4L, query.ExecuteScalar());
    }

    [Fact]
    public async Task PlayCommitFailureRollsBackScoreAndReceiptTogether()
    {
        using var files = new TestWorkspace();
        await using var writer = new SerializedExperimentWriter(new SqliteExperimentWriter(typeof(ScoreResult), topPlays: new()));
        await writer.InitializeAsync(new(files.DirectoryPath, "Test", 1), default);
        using var db = new SqliteConnection($"Data Source={files.FilePath("results.sqlite")};Pooling=False"); db.Open();
        using var query = db.CreateCommand();
        query.CommandText = "CREATE TRIGGER reject_play BEFORE INSERT ON plays BEGIN SELECT RAISE(ABORT, 'play write failure'); END";
        query.ExecuteNonQuery();
        await Assert.ThrowsAsync<SqliteException>(() => writer.WriteAsync(new("Test", "completed", null, DateTimeOffset.UtcNow, 1, 1, 1, 1,
            JsonSerializer.SerializeToElement(new ScoreResult(1)), null, 0) { Plays = [new(0, "Test", 1, 0, 1, [0])] }));
        query.CommandText = "SELECT COUNT(*) FROM trials"; Assert.Equal(0L, query.ExecuteScalar());
    }

    [Fact]
    public async Task ExperimentCompiledAgainstOldSdkLoadsWithoutRecompilation()
    {
        using var files = new TestWorkspace();
        var legacy = files.FilePath("legacy"); Directory.CreateDirectory(legacy);
        // Compile against an independent pre-feature contract, not the current SDK.
        File.WriteAllText(Path.Combine(legacy, "TasStudio.Sdk.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework>
            <ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup></Project>
            """);
        File.WriteAllText(Path.Combine(legacy, "Contract.cs"), """
            namespace TasStudio.Sdk;
            public interface IExperiment { Task<object?> RunAsync(ExperimentRunContext context, CancellationToken cancellationToken); }
            public interface IExperimentEmulator { Task LoadStateAsync(string id); Task<string> SaveStateAsync(string name); }
            public sealed class ExperimentRunContext { public IExperimentEmulator Emulator => throw new Exception("Stub must never run"); }
            """);
        var experiment = files.FilePath("compiled"); Directory.CreateDirectory(experiment);
        File.WriteAllText(Path.Combine(experiment, "LegacyExperiment.csproj"), $"""
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework>
            <ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
            <ItemGroup><ProjectReference Include="{SecurityElement.Escape(Path.Combine(legacy, "TasStudio.Sdk.csproj"))}" /></ItemGroup></Project>
            """);
        File.WriteAllText(Path.Combine(experiment, "Experiment.cs"), """
            using TasStudio.Sdk;
            public sealed class LegacyExperiment : IExperiment
            {
                public async Task<object?> RunAsync(ExperimentRunContext context, CancellationToken token)
                {
                    var id = await context.Emulator.SaveStateAsync("Legacy checkpoint");
                    await context.Emulator.LoadStateAsync(id);
                    return new { Score = 42 };
                }
            }
            """);
        using var build = new Process { StartInfo = new("dotnet") { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true } };
        foreach (var arg in new[] { "build", Path.Combine(experiment, "LegacyExperiment.csproj"), "--nologo" }) build.StartInfo.ArgumentList.Add(arg);
        build.Start(); var stdout = build.StandardOutput.ReadToEndAsync(); var stderr = build.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        try { await build.WaitForExitAsync(timeout.Token); }
        finally { if (!build.HasExited) { build.Kill(true); await build.WaitForExitAsync(); } }
        Assert.True(build.ExitCode == 0, await stdout + await stderr);
        using var source = new ExecutionService(Backend()); var (path, state) = await Source(files, source);
        using var assembly = new ExperimentAssembly(Path.Combine(experiment, "bin/Debug/net10.0/LegacyExperiment.dll"), loadInMemory: true);
        using var worker = new ExecutionService(Backend());
        var result = await ExperimentWorker.RunAsync(Job(files, path, state), worker, default, assembly.Create("LegacyExperiment"));
        Assert.Equal("completed", result.Status); Assert.Equal(42, result.Value!.Value.GetProperty("Score").GetInt32());
    }

    [AvaloniaTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task PickerAppliesSelectedPlayAndReportsCallbackErrors(bool nested, bool asTake)
    {
        using var files = new TestWorkspace();
        var batchDirectory = Path.GetFullPath(files.FilePath(nested ? ".runs/capture-entry-269-01/batch" : ".runs/capture-entry-269-01"));
        ExperimentFiles.Write(Path.Combine(batchDirectory, "experiment.json"), new ExperimentDefinition(1, "Test", ExperimentStart.Boot, null, null, 1, 30,
            [new("Test", JsonSerializer.SerializeToElement(new { }))]) { TopPlays = new() });
        await using (var writer = new SerializedExperimentWriter(new SqliteExperimentWriter(typeof(ScoreResult), topPlays: new())))
        {
            await writer.InitializeAsync(new(batchDirectory, "Test", 1), default);
            await writer.WriteAsync(new("Test", "completed", null, DateTimeOffset.UtcNow, 1, 1, 1, 1,
                JsonSerializer.SerializeToElement(new ScoreResult(10)), null, 0)
            { Plays = [new(0, "Winner", 10, 0, 1, new ExperimentPlayData("baseline", [ControllerState.Neutral], [], []).Encode())] });
        }
        var applied = 0;
        var window = new ExperimentPlaysWindow(ProjectExperiments.PreviousBatches(files.FilePath(ProjectExperiments.ConfigFileName)), (batch, play, requestedTake) =>
        {
            Assert.Equal(asTake, requestedTake);
            Assert.Equal(batchDirectory, batch); Assert.Equal("Winner", play.Name);
            if (++applied == 2) return Task.FromResult(false);
            if (applied == 3) throw new InvalidDataException("Starting history changed");
            return Task.FromResult(true);
        });
        try
        {
            window.Show(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            var batches = window.GetVisualDescendants().OfType<ComboBox>().Single();
            Assert.Equal(nested ? "capture-entry-269-01 / batch" : "capture-entry-269-01", batches.SelectedItem!.ToString());
            var list = window.GetVisualDescendants().OfType<ListBox>().Single();
            var use = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == (asTake ? "ApplyExperimentPlayAsTake" : "ApplyExperimentPlay"));
            for (var i = 0; list.ItemCount == 0 && i < 100; i++) { await Task.Delay(10); Dispatcher.UIThread.RunJobs(); }
            Assert.Equal(1, list.ItemCount); Assert.False(use.IsEnabled);
            list.SelectedIndex = 0; Assert.True(use.IsEnabled);
            use.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert.Equal(1, applied);
            use.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert.Equal(2, applied);
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), block => block.Text == "Play was not applied.");
            use.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert.Equal(3, applied);
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), block => block.Text == "Starting history changed");
        }
        finally { window.Close(); }
    }
}
