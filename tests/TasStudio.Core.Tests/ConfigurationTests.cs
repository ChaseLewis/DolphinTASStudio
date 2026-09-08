using TasStudio.Core;
using TasStudio.Emulation;
using Xunit;

namespace TasStudio.Core.Tests;

public sealed class ConfigurationTests
{
    private static readonly ControllerState A = ControllerState.Neutral with { Buttons = PadButtons.A };
    private static async Task Start(ExecutionService service, TestWorkspace workspace, bool legacy = false)
    {
        await service.LoadGameAsync(workspace.GamePath, workspace.Options with
        {
            Configuration = legacy ? null : new EmulationConfiguration().ValidatedCopy()
        });
        await service.NewProjectAsync();
    }
    private static async Task Step(ExecutionService service, int count)
    {
        for (var i = 0; i < count; i++) await service.StepAsync();
    }
    private static string[] CacheFiles(TestWorkspace workspace) => Directory.GetFiles(workspace.DirectoryPath, "*.tasstate", SearchOption.AllDirectories)
        .Where(p => Path.GetFileName(Path.GetDirectoryName(p)) == "CheckpointCache").ToArray();
    private static ulong[] AutomaticPositions(ExecutionService service) => service.StateMarkers.Where(m => m.Automatic).Select(m => m.Position).Order().ToArray();

    [Fact]
    public async Task CheckpointsUseEmulatedSecondsAndHaveReadableFilesOnDisk()
    {
        using var w = new TestWorkspace(); using var s = new ExecutionService(new FakeBackend()); await Start(s, w);
        await s.ConfigureCheckpointsAsync(new(IntervalSeconds: 1));
        await Step(s, 59); Assert.Empty(CacheFiles(w)); Assert.Empty(AutomaticPositions(s));
        await Step(s, 1); Assert.Equal(new ulong[] { 60 }, AutomaticPositions(s));
        var state = ProjectArchive.Load(Assert.Single(CacheFiles(w)), ProjectArchive.StateKind);
        Assert.Equal(60UL, state.InitialState.Position); Assert.Equal(await s.HistoryAtAsync(60), state.Metadata.HistoryHash);
        await Step(s, 59); Assert.Single(CacheFiles(w));
        await Step(s, 1); Assert.Equal(new ulong[] { 60, 120 }, AutomaticPositions(s));
        Assert.Equal(2, CacheFiles(w).Length);
    }

    [Theory]
    [InlineData(CheckpointRetention.OldestCreated, 120UL)]
    [InlineData(CheckpointRetention.LeastRecentlyUsed, 60UL)]
    public async Task RetentionUsesActualRestoreAccessWhenConfiguredForLru(CheckpointRetention retention, ulong survivingOlderPosition)
    {
        using var w = new TestWorkspace(); var backend = new FakeBackend(); using var s = new ExecutionService(backend); await Start(s, w);
        await s.ConfigureCheckpointsAsync(new(IntervalSeconds: 1, MaximumCount: 2, Retention: retention));
        await Step(s, 120);
        var originalFiles = CacheFiles(w).ToDictionary(p => ProjectArchive.Load(p, ProjectArchive.StateKind).InitialState.Position);
        var restores = backend.Calls.Count(c => c.Operation == nameof(FakeBackend.Restore));
        await s.SeekAsync(60);
        Assert.Equal(restores + 1, backend.Calls.Count(c => c.Operation == nameof(FakeBackend.Restore)));
        // Execute through 120 without restoring it; touching 60 must affect only LRU retention.
        await Step(s, 120);
        Assert.Equal(new[] { survivingOlderPosition, 180UL }, AutomaticPositions(s));
        Assert.Equal(2, CacheFiles(w).Length);
        Assert.True(File.Exists(originalFiles[survivingOlderPosition]));
        Assert.False(File.Exists(originalFiles[survivingOlderPosition == 60 ? 120UL : 60UL]));
    }

    [Fact]
    public async Task ReducingMaximumCountImmediatelyDeletesOldestAutomaticFilesButKeepsNamedStates()
    {
        using var w = new TestWorkspace(); using var s = new ExecutionService(new FakeBackend()); await Start(s, w);
        await s.ConfigureCheckpointsAsync(new(IntervalSeconds: 1)); await Step(s, 180); await s.SaveNamedStateAsync("Keep me");
        await s.ConfigureCheckpointsAsync(new(IntervalSeconds: 1, MaximumCount: 1));
        Assert.Equal(new ulong[] { 180 }, AutomaticPositions(s)); Assert.Single(CacheFiles(w));
        Assert.Single(s.StateMarkers, m => !m.Automatic);
    }

    [Fact]
    public async Task OversizedCheckpointIsEvictedFromDiskWithoutCapturingOnEveryFrame()
    {
        using var w = new TestWorkspace(); var backend = new FakeBackend(); using var s = new ExecutionService(backend); await Start(s, w);
        await s.ConfigureCheckpointsAsync(new(IntervalSeconds: 1, DiskBudgetMiB: 1));
        backend.SnapshotPaddingBytes = 1100000;
        var captures = backend.Calls.Count(c => c.Operation == nameof(FakeBackend.Capture));
        await Step(s, 60);
        Assert.Equal(captures + 1, backend.Calls.Count(c => c.Operation == nameof(FakeBackend.Capture)));
        Assert.Empty(AutomaticPositions(s)); Assert.Empty(CacheFiles(w));
        await Step(s, 59);
        Assert.Equal(captures + 1, backend.Calls.Count(c => c.Operation == nameof(FakeBackend.Capture)));
        await Step(s, 1);
        Assert.Equal(captures + 2, backend.Calls.Count(c => c.Operation == nameof(FakeBackend.Capture)));
        Assert.Empty(CacheFiles(w));
    }

    [Fact]
    public async Task DisabledCheckpointPolicyStopsCreationWithoutDiscardingExistingAnchors()
    {
        using var w = new TestWorkspace(); using var s = new ExecutionService(new FakeBackend()); await Start(s, w);
        await s.ConfigureCheckpointsAsync(new(IntervalSeconds: 1)); await Step(s, 60);
        await s.ConfigureCheckpointsAsync(new(Enabled: false, IntervalSeconds: 1)); await Step(s, 120);
        Assert.Equal(new ulong[] { 60 }, AutomaticPositions(s)); Assert.Single(CacheFiles(w));
        await s.ConfigureCheckpointsAsync(new(IntervalSeconds: 2)); await Step(s, 1);
        Assert.Equal(new ulong[] { 60, 181 }, AutomaticPositions(s));
        await Step(s, 119); Assert.Equal(new ulong[] { 60, 181 }, AutomaticPositions(s));
        await Step(s, 1); Assert.Equal(new ulong[] { 60, 181, 301 }, AutomaticPositions(s));
    }

    [Fact]
    public async Task InputAndBoundaryChangesRemoveOnlyCheckpointsWithChangedHistory()
    {
        using var w = new TestWorkspace(); using var s = new ExecutionService(new FakeBackend()); await Start(s, w);
        await s.ConfigureCheckpointsAsync(new(IntervalSeconds: 1)); await Step(s, 180);
        var files = CacheFiles(w).ToDictionary(p => ProjectArchive.Load(p, ProjectArchive.StateKind).InitialState.Position);
        await s.SetInputAsync(120, A);
        Assert.Equal(new ulong[] { 60, 120 }, AutomaticPositions(s));
        Assert.True(File.Exists(files[120])); Assert.False(File.Exists(files[180]));
        await s.SeekAsync(120); await s.WriteMemoryAsync(0x80000000, BitConverter.GetBytes(17));
        Assert.Equal(new ulong[] { 60 }, AutomaticPositions(s)); Assert.False(File.Exists(files[120]));
        await s.UndoAsync(); Assert.Equal(new ulong[] { 60 }, AutomaticPositions(s));
        await s.UndoAsync(); Assert.Equal(new ulong[] { 60 }, AutomaticPositions(s));
    }

    [Fact]
    public async Task ProjectRoundtripPersistsCheckpointPolicyAndDiskStates()
    {
        using var w = new TestWorkspace(); var path = w.FilePath("movie.tasproj");
        var policy = new CheckpointPolicy(IntervalSeconds: 1, MaximumCount: 2, Retention: CheckpointRetention.LeastRecentlyUsed, DiskBudgetMiB: 32);
        using (var s = new ExecutionService(new FakeBackend()))
        {
            await Start(s, w); await s.ConfigureCheckpointsAsync(policy); await Step(s, 120); await s.SaveProjectAsync(path);
        }
        var content = FolderProject.Load(path);
        Assert.Equal(policy, content.Archive.Metadata.Checkpoints); Assert.Equal(2, content.AutomaticCheckpoints.Length);
        Assert.All(content.AutomaticCheckpoints, c => Assert.True(File.Exists(c.State.Path)));
        var backend = new FakeBackend(); using var reopened = new ExecutionService(backend);
        await reopened.LoadProjectAsync(path, w.Options);
        Assert.Equal(policy, reopened.Checkpoints); Assert.Equal(new ulong[] { 60, 120 }, AutomaticPositions(reopened));
        Assert.Empty(backend.SubmittedInputs); // Opening at 120 restores the on-disk checkpoint directly.
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CheckpointOnlyApplyDoesNotRestartOrChangeHistory(bool legacy)
    {
        using var w = new TestWorkspace(); var backend = new FakeBackend(); using var s = new ExecutionService(backend); await Start(s, w, legacy);
        await Step(s, 3); await s.SaveNamedStateAsync("Before policy change");
        var history = await s.HistoryAtAsync(3); var marker = Assert.Single(s.StateMarkers);
        var loads = backend.Calls.Count(c => c.Operation == nameof(FakeBackend.LoadGame));
        var current = (await s.GetConfigurationAsync())!.Options;
        var settings = current.Configuration ?? EmulationConfiguration.FromLegacy(current.InternalResolution, current.DspHle);
        var policy = new CheckpointPolicy(IntervalSeconds: 300, MaximumCount: 25);
        if (legacy) await s.ConfigureCheckpointsAsync(policy);
        else await s.ApplyConfigurationAsync(settings, policy);
        Assert.Equal(loads, backend.Calls.Count(c => c.Operation == nameof(FakeBackend.LoadGame)));
        Assert.Equal(3UL, s.Position); Assert.Equal(history, await s.HistoryAtAsync(3));
        Assert.Equal(marker, Assert.Single(s.StateMarkers)); Assert.Equal(policy, s.Checkpoints);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChangingUtcOrResolutionPreservesInputsButChangesRootAndRejectsOldStates(bool resolution)
    {
        using var w = new TestWorkspace(); var backend = new FakeBackend(); using var s = new ExecutionService(backend); await Start(s, w);
        await s.ConfigureCheckpointsAsync(new(IntervalSeconds: 1)); await Step(s, 60);
        await s.SetInputAsync(3, A); await s.SeekAsync(60); await s.SaveNamedStateAsync("Old settings");
        var oldState = w.FilePath("export.tasstate"); await s.SaveStateAsync(oldState);
        var originalInputs = s.Inputs.ToArray(); var oldRoot = await s.HistoryAtAsync(0); var oldFiles = CacheFiles(w);
        var settings = (await s.GetConfigurationAsync())!.Options.Configuration!.ValidatedCopy();
        if (resolution) settings.Options["dolphin_efb_scale"] = "2";
        else settings = settings with { StartUtcSeconds = settings.StartUtcSeconds + 1234 };
        await s.ApplyConfigurationAsync(settings, new(IntervalSeconds: 1));
        Assert.Equal(originalInputs, s.Inputs); Assert.Equal(0UL, s.Position);
        Assert.NotEqual(oldRoot, await s.HistoryAtAsync(0)); Assert.Empty(s.StateMarkers);
        Assert.All(oldFiles, file => Assert.False(File.Exists(file)));
        var restores = backend.Calls.Count(c => c.Operation == nameof(FakeBackend.Restore));
        await Assert.ThrowsAsync<InvalidDataException>(() => s.LoadStateAsync(oldState));
        Assert.Equal(restores, backend.Calls.Count(c => c.Operation == nameof(FakeBackend.Restore)));
        var project = w.FilePath("changed.tasproj"); await s.SaveProjectAsync(project);
        Assert.Equal(settings.Fingerprint, FolderProject.Load(project).Archive.Metadata.Configuration!.Fingerprint);
        await s.LoadProjectAsync(project, w.Options); Assert.Equal(originalInputs, s.Inputs);
        Assert.Equal(settings.Fingerprint, (await s.GetConfigurationAsync())!.Options.Configuration!.Fingerprint);
    }

    [Fact]
    public async Task FailedConfigurationApplyRestoresPositionInputsPolicyAndAutomaticCheckpoints()
    {
        using var w = new TestWorkspace(); var backend = new FakeBackend(); using var s = new ExecutionService(backend); await Start(s, w);
        var policy = new CheckpointPolicy(IntervalSeconds: 1, MaximumCount: 5);
        await s.ConfigureCheckpointsAsync(policy); await Step(s, 60); await s.SaveNamedStateAsync("Before failed apply");
        var markers = s.StateMarkers.ToArray(); var inputs = s.Inputs.ToArray(); var files = CacheFiles(w);
        var root = await s.HistoryAtAsync(0); var revision = s.Revision;
        var configuration = (await s.GetConfigurationAsync())!.Options.Configuration!;
        backend.FailNextLoad = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.ApplyConfigurationAsync(configuration with { StartUtcSeconds = configuration.StartUtcSeconds + 1 }, new()));
        Assert.Equal(60UL, s.Position); Assert.Equal(inputs, s.Inputs); Assert.Equal(policy, s.Checkpoints);
        Assert.Equal(root, await s.HistoryAtAsync(0)); Assert.Equal(revision, s.Revision); Assert.True(s.IsPreviewCurrent);
        Assert.Equal(markers, s.StateMarkers); Assert.All(files, file => Assert.True(File.Exists(file)));
    }

    [Fact]
    public async Task InvalidNamedStatesDeleteOwnedFilesButPreserveExportsAndSavedProjectUntilSave()
    {
        using var w = new TestWorkspace(); using var s = new ExecutionService(new FakeBackend()); await Start(s, w);
        await Step(s, 3); await s.SaveNamedStateAsync("Owned state");
        var owned = Assert.Single(Directory.GetFiles(w.DirectoryPath, "*.tasstate", SearchOption.AllDirectories));
        var exported = w.FilePath("export.tasstate"); await s.SaveStateAsync(exported);
        var project = w.FilePath("saved.tasproj"); await s.SaveProjectAsync(project);
        await s.SetInputAsync(0, A);
        Assert.False(File.Exists(owned)); Assert.True(File.Exists(exported)); Assert.Empty(s.StateMarkers);
        Assert.Equal(2, FolderProject.Load(project).States.Length);
    }

    [Fact]
    public async Task RepeatedUtcRetriesUseSiblingProfilesAndRollbackKeepsNamedFiles()
    {
        using var w = new TestWorkspace(); var backend = new FakeBackend(); using var s = new ExecutionService(backend); await Start(s, w);
        var settings = (await s.GetConfigurationAsync())!.Options.Configuration!;
        var directories = new List<string>();
        for (var i = 1; i <= 3; i++)
        {
            await s.ApplyConfigurationAsync(settings with { StartUtcSeconds = settings.StartUtcSeconds + i }, new());
            directories.Add((await s.GetConfigurationAsync())!.Options.SaveDirectory);
        }
        Assert.Single(directories.Select(Path.GetDirectoryName).Distinct());
        await Step(s, 1); await s.SaveNamedStateAsync("Rollback");
        var owned = Assert.Single(Directory.GetFiles(w.DirectoryPath, "*.tasstate", SearchOption.AllDirectories));
        var marker = Assert.Single(s.StateMarkers);
        backend.FailNextLoad = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.ApplyConfigurationAsync(settings, new()));
        Assert.True(File.Exists(owned)); await s.LoadMarkerAsync(marker.Id);
    }

    [Fact]
    public async Task ReopeningTheSameProjectPreservesIdenticalPersistedMarkers()
    {
        using var w = new TestWorkspace(); using var s = new ExecutionService(new FakeBackend()); await Start(s, w);
        await s.ConfigureCheckpointsAsync(new(IntervalSeconds: 1)); await Step(s, 60); await s.SaveNamedStateAsync("Anchor");
        var project = w.FilePath("same/project.tasproj"); await s.SaveProjectAsync(project);
        await s.LoadProjectAsync(project, w.Options);
        var markers = s.StateMarkers.ToArray(); Assert.Equal(2, markers.Length);
        await s.LoadProjectAsync(project, w.Options);
        Assert.Equal(markers, s.StateMarkers);
        await s.LoadMarkerAsync(Assert.Single(s.StateMarkers, m => !m.Automatic).Id);
    }

    [Fact]
    public void SettingsFingerprintIsCanonicalAndForbiddenCpuThreadOptionIsRejected()
    {
        var sparse = new EmulationConfiguration(); var expanded = sparse.ValidatedCopy();
        Assert.Equal(sparse.Fingerprint, expanded.Fingerprint);
        var reordered = expanded with { Options = new(StringComparer.Ordinal) };
        foreach (var pair in expanded.Options.Reverse()) reordered.Options.Add(pair.Key, pair.Value);
        Assert.Equal(expanded.Fingerprint, reordered.Fingerprint);
        var forbidden = new EmulationConfiguration { Options = new(StringComparer.Ordinal) { ["dolphin_main_cpu_thread"] = "enabled" } };
        Assert.Throws<InvalidDataException>(() => forbidden.ValidatedCopy());
    }
}
