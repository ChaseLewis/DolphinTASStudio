using TasStudio.Core;
using TasStudio.Emulation;
using Xunit;

namespace TasStudio.Core.Tests;

public sealed class RecoveryTests
{
    [Fact]
    public async Task StopPlaybackRetainsRomProjectInputsEventsAndSavedStates()
    {
        using var w = new TestWorkspace(); using var s = new ExecutionService(new FakeBackend());
        await s.LoadGameAsync(w.GamePath, w.Options); await s.NewProjectAsync();
        await s.StepAsync(); await s.ResetAsync(); await s.StepAsync(); await s.SaveNamedStateAsync("Anchor");
        var inputs = s.Inputs.ToArray(); var history = await s.HistoryAtAsync(2); var marker = Assert.Single(s.StateMarkers);
        await s.StopPlaybackAsync();
        Assert.True(s.IsLoaded); Assert.True(s.HasProject); Assert.False(s.IsRunning);
        Assert.Equal(w.GamePath, s.GamePath); Assert.Equal(0UL, s.Position);
        Assert.Equal(inputs, s.Inputs); Assert.Equal(history, await s.HistoryAtAsync(2));
        Assert.Equal(marker, Assert.Single(s.StateMarkers));
        await s.SeekAsync(2); Assert.True(s.IsPreviewCurrent);
    }

    [Fact]
    public async Task RecoverySurvivesSessionDisposalAndReopensInputsEventsAndCandidates()
    {
        using var w = new TestWorkspace();
        var path = w.FilePath("recovery/recovery.tasproj");
        ControllerState[] inputs; string root;
        using (var s = new ExecutionService(new FakeBackend()))
        {
            await s.LoadGameAsync(w.GamePath, w.Options with { Configuration = new EmulationConfiguration() }); await s.NewProjectAsync();
            await s.StepAsync(); await s.ResetAsync(); await s.StepAsync();
            await s.SetInputAsync(0, ControllerState.Neutral with { Buttons = PadButtons.A });
            await s.CaptureTakeAsync("Candidate", 0, 2); await s.SaveNamedStateAsync("Temporary state");
            inputs = s.Inputs.ToArray(); root = await s.HistoryAtAsync(0);
            var saving = s.SaveRecoveryAsync(path);
            await s.StopAsync(); // Deletes temporary state files while background recovery writing can continue.
            await saving;
        }
        var disk = FolderProject.Load(path);
        Assert.Equal(inputs, disk.Archive.Metadata.Inputs); Assert.Single(disk.Archive.Metadata.Events);
        Assert.Single(disk.Takes); Assert.Empty(disk.States);
        using var recovered = new ExecutionService(new FakeBackend());
        await recovered.LoadProjectAsync(path, w.Options);
        Assert.Equal(inputs, recovered.Inputs); Assert.Equal(root, await recovered.HistoryAtAsync(0));
        Assert.Single(recovered.Takes); Assert.True(recovered.IsPreviewCurrent);
    }

    [Fact]
    public async Task RecoveryDoesNotPausePlaybackOrOverwriteExplicitProject()
    {
        using var w = new TestWorkspace(); using var s = new ExecutionService(new FakeBackend());
        await s.LoadGameAsync(w.GamePath, w.Options); await s.NewProjectAsync(); await s.StepAsync();
        var project = w.FilePath("original/movie.tasproj"); await s.SaveProjectAsync(project);
        var original = File.ReadAllBytes(project);
        await s.RunAsync();
        var recovery = w.FilePath("recovery/recovery.tasproj"); await s.SaveRecoveryAsync(recovery);
        Assert.True(s.IsRunning); Assert.Equal(original, File.ReadAllBytes(project));
        await s.PauseAsync();
        Assert.NotEmpty(FolderProject.Load(recovery).Archive.Metadata.Inputs);
    }

    [Fact]
    public async Task FailedRecoveryWriteLeavesThePreviousRecoveryUsable()
    {
        using var w = new TestWorkspace(); using var s = new ExecutionService(new FakeBackend());
        await s.LoadGameAsync(w.GamePath, w.Options); await s.NewProjectAsync(); await s.StepAsync();
        var recovery = w.FilePath("recovery/recovery.tasproj"); await s.SaveRecoveryAsync(recovery);
        await s.StepAsync();
        using (var locked = new FileStream(recovery, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var error = await Record.ExceptionAsync(() => s.SaveRecoveryAsync(recovery));
            Assert.True(error is IOException or UnauthorizedAccessException);
        }
        Assert.Single(FolderProject.Load(recovery).Archive.Metadata.Inputs);
        await s.SaveRecoveryAsync(recovery); Assert.Equal(2, FolderProject.Load(recovery).Archive.Metadata.Inputs.Length);
    }
}
