using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TasStudio.App;
using TasStudio.Emulation;
using Xunit;

namespace TasStudio.Core.Tests;

public sealed class ProjectCompatibilityTests
{
    private static FakeBackend Backend(string identity, string? environment = null) =>
        new() { Identity = identity, ConfigurationIdentity = environment, RecordPolls = true, FieldsPerStep = 2 };

    private static async Task<string> Create(TestWorkspace files, bool saveState = false)
    {
        using var source = new ExecutionService(Backend("old", "old-host"));
        await source.LoadGameAsync(files.GamePath, files.Options with { Configuration = new() });
        await source.NewProjectAsync();
        for (var i = 0; i < 3; i++) await source.StepAsync();
        if (saveState) await source.SaveNamedStateAsync("Progress");
        var path = files.FilePath("original.tasproj");
        await source.SaveProjectAsync(path);
        return path;
    }

    [Fact]
    public async Task DifferentRuntimeOpensWithProgressAndPreservesBaselineAndBuildHistoryOnSaveAndRecovery()
    {
        using var files = new TestWorkspace();
        var path = await Create(files, saveState: true);
        var originalBytes = File.ReadAllBytes(path);
        var original = FolderProject.Load(path);
        var backend = Backend("new", "new-host");
        using var service = new ExecutionService(backend);
        await service.LoadProjectAsync(path, files.Options);
        Assert.NotNull(service.CompatibilityWarning);
        Assert.Equal(3UL, service.Position);
        Assert.Equal(original.Archive.Metadata.Inputs, service.Inputs);
        Assert.Equal("Progress", Assert.Single(service.StateMarkers).Name);
        Assert.Equal(originalBytes, File.ReadAllBytes(path));
        await service.SeekAsync(1); // must replay old polls, not silently regenerate them
        Assert.Contains(backend.Calls, c => c.Operation == nameof(FakeBackend.ReplayInputPollFrame));
        var replayed = (await service.GetPollFrameAsync(0))!;
        Assert.Equal(original.Archive.Metadata.PollFrames[0].PrefixHash, replayed.PrefixHash);
        Assert.Equal(original.Archive.Metadata.PollFrames[0].Frame.Polls, replayed.Frame.Polls);
        await service.SeekAsync(3);
        await service.StepAsync();
        await service.SaveNamedStateAsync("New progress");
        var recovery = files.FilePath("recovery.tasproj");
        await service.SaveRecoveryAsync(recovery);
        await service.SaveProjectAsync(path);
        foreach (var saved in new[] { path, recovery })
        {
            var content = FolderProject.Load(saved);
            Assert.Equal("old", content.Archive.Metadata.BackendIdentity);
            Assert.Equal("old-host", content.Archive.Metadata.ConfigurationIdentity);
            Assert.Equal(original.Archive.InitialState.Data, content.Archive.InitialState.Data);
            Assert.Contains(new EmulatorRuntime("old", "old-host"), content.Archive.Metadata.RuntimeHistory);
            Assert.Contains(new EmulatorRuntime("new", "new-host"), content.Archive.Metadata.RuntimeHistory);
            using var reopened = new ExecutionService(Backend("new", "new-host"));
            await reopened.LoadProjectAsync(saved, files.Options);
            Assert.Equal(4UL, reopened.Position);
            Assert.NotNull(reopened.CompatibilityWarning);
        }
        var state = FolderProject.Load(path).States.Single(s => s.Name == "New progress");
        Assert.Equal("new", ProjectArchive.Load(state.Path, ProjectArchive.StateKind).Metadata.BackendIdentity);
        Assert.Equal(2, service.StateMarkers.Count);
    }

    [Fact]
    public async Task WarningDoesNotBypassRecordedPollDesyncAndFailedOpenRestoresTheCurrentSession()
    {
        using var files = new TestWorkspace();
        var path = await Create(files);
        var backend = Backend("new", "new-host");
        using var service = new ExecutionService(backend);
        await service.LoadProjectAsync(path, files.Options);
        var memory = await service.ReadMemoryAsync(0x80000000, 4);
        var warning = service.CompatibilityWarning;
        backend.PollsPerStep = 3;
        await Assert.ThrowsAsync<InvalidDataException>(() => service.LoadProjectAsync(path, files.Options));
        Assert.Equal(3UL, service.Position);
        Assert.Equal(memory, await service.ReadMemoryAsync(0x80000000, 4));
        Assert.Equal(warning, service.CompatibilityWarning);
        Assert.True(service.IsPreviewCurrent);
    }

    [Fact]
    public async Task UnreadableStateStillFailsAndLeavesProjectFilesUntouched()
    {
        using var files = new TestWorkspace();
        var path = await Create(files);
        var bytes = File.ReadAllBytes(path);
        var backend = Backend("new", "new-host");
        using var service = new ExecutionService(backend);
        backend.FailNextRestore = true;
        await Assert.ThrowsAsync<InvalidDataException>(() => service.LoadProjectAsync(path, files.Options));
        Assert.False(service.HasProject);
        Assert.Null(service.CompatibilityWarning);
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task ExplicitStrictModeStillRejectsBuildAndHostMismatches()
    {
        using var files = new TestWorkspace();
        var path = await Create(files);
        foreach (var backend in new[] { Backend("new", "old-host"), Backend("old", "new-host") })
        {
            using var service = new ExecutionService(backend);
            await Assert.ThrowsAsync<InvalidDataException>(() => service.LoadProjectAsync(path, files.Options, requireExactRuntime: true));
        }
    }

    [AvaloniaFact]
    public async Task WarningStaysVisibleInTheEditorAfterStatusUpdates()
    {
        using var files = new TestWorkspace();
        var path = await Create(files);
        using var service = new ExecutionService(Backend("new", "new-host"));
        await service.LoadProjectAsync(path, files.Options);
        var main = new MainWindow(["--layout-file", files.FilePath("layout.json")], new AppSettings(), service);
        try
        {
            main.Show(); main.ShowEditor();
            await service.PauseAsync();
            var notice = main.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "CompatibilityNotice");
            for (var i = 0; i < 100 && !notice.IsEffectivelyVisible; i++) { await Task.Delay(10); Dispatcher.UIThread.RunJobs(); }
            Assert.True(notice.IsEffectivelyVisible);
            Assert.Contains("different emulator build", notice.Text);
        }
        finally { main.CloseAfterCapture(); }
    }
}
