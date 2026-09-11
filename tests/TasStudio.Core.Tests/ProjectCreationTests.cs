using TasStudio.Emulation;
using Xunit;

namespace TasStudio.Core.Tests;

public sealed class ProjectCreationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreatedAndReopenedProjectsAreReadyToAdvanceWithoutExplicitSeek(bool fromState)
    {
        using var files = new TestWorkspace(); var backend = new FakeBackend(); using var service = new ExecutionService(backend);
        string? state = null;
        if (fromState)
        {
            await service.LoadGameAsync(files.GamePath, files.Options); await service.StepAsync();
            state = files.FilePath("start.tasstate"); await service.SaveStateAsync(state);
        }
        var path = files.FilePath("ready/ready.tasproj");
        await service.CreateProjectAsync(path, files.GamePath, files.Options, state);
        foreach (var reopen in new[] { false, true })
        {
            if (reopen) await service.LoadProjectAsync(path, files.Options);
            Assert.True(service.IsPreviewCurrent);
            Assert.Equal(0UL, service.Position);
            var restores = backend.Calls.Count(call => call.Operation == "Restore");
            var steps = backend.SubmittedInputs.Count;
            Assert.True(await service.AdvanceFrameAsync(ControllerState.Neutral with { Buttons = PadButtons.A }));
            Assert.Equal(1UL, service.Position);
            Assert.Equal(steps + 1, backend.SubmittedInputs.Count);
            Assert.Equal(restores, backend.Calls.Count(call => call.Operation == "Restore"));
        }
    }

    [Fact]
    public async Task VerificationChecksRomAssetsAndBuildWithoutRunningEmulation()
    {
        using var files = new TestWorkspace(); var backend = new FakeBackend(); using var service = new ExecutionService(backend);
        var path = files.FilePath("verified/verified.tasproj");
        await service.CreateProjectAsync(path, files.GamePath, files.Options);
        var calls = backend.Calls.Count;
        var content = await ProjectVerification.VerifyAsync(path, backend.Identity, new Dictionary<string, string>());
        Assert.Equal(calls, backend.Calls.Count);
        var different = await ProjectVerification.VerifyAsync(path, "different core", new Dictionary<string, string>());
        Assert.NotNull(ProjectVerification.BuildWarning(different.Archive.Metadata, "different core"));
        var moved = files.FilePath("moved.iso"); File.Move(files.GamePath, moved);
        await ProjectVerification.VerifyAsync(path, backend.Identity, new Dictionary<string, string> { [content.Archive.Metadata.GameHash] = moved });
        await Assert.ThrowsAsync<FileNotFoundException>(() => ProjectVerification.VerifyAsync(path, backend.Identity, new Dictionary<string, string>()));
        File.WriteAllText(moved, "different revision");
        await Assert.ThrowsAsync<InvalidDataException>(() => ProjectVerification.VerifyAsync(path, backend.Identity, new Dictionary<string, string> { [content.Archive.Metadata.GameHash] = moved }));
        var state = Directory.GetFiles(files.FilePath("verified/states"), "*.tasstate").Single();
        File.WriteAllText(state, "corrupt state");
        await Assert.ThrowsAsync<InvalidDataException>(() => ProjectVerification.VerifyAsync(path, backend.Identity, new Dictionary<string, string>()));
    }

    [Fact]
    public async Task PowerOnCommitsAnEmptyProjectWithItsUtcAndOrigin()
    {
        using var files = new TestWorkspace(); using var service = new ExecutionService(new FakeBackend());
        var path = files.FilePath("power/power.tasproj");
        var config = new EmulationConfiguration { StartUtcSeconds = 1234567890 };
        await service.CreateProjectAsync(path, files.GamePath, files.Options with { Configuration = config });
        var saved = FolderProject.Load(path);
        Assert.Empty(saved.Archive.Metadata.Inputs); Assert.Equal(0UL, service.Position);
        Assert.Equal(ProjectStartKind.PowerOn, saved.Archive.Metadata.Start!.Kind);
        Assert.Equal(config.StartUtcSeconds, saved.Archive.Metadata.Configuration!.StartUtcSeconds);
    }

    [Fact]
    public async Task StateStartCopiesItsSettingsAndStateAndRebasesTimelineWithoutDependingOnSource()
    {
        using var files = new TestWorkspace(); using var service = new ExecutionService(new FakeBackend());
        var config = new EmulationConfiguration { StartUtcSeconds = 1234567890 };
        var source = files.FilePath("source.tasstate"); var project = files.FilePath("branch/branch.tasproj");
        await service.LoadGameAsync(files.GamePath, files.Options with { Configuration = config });
        await service.NewProjectAsync(); await service.StepAsync(); await service.StepAsync();
        var memory = await service.ReadMemoryAsync(0x80000000, 4);
        await service.SaveStateAsync(source);
        await service.CreateProjectAsync(project, files.GamePath, files.Options, source);
        Assert.Equal(0UL, service.Position); Assert.Empty(service.Inputs);
        Assert.Equal(memory, await service.ReadMemoryAsync(0x80000000, 4));
        var metadata = FolderProject.Load(project).Archive.Metadata;
        Assert.Equal(new ProjectStart(ProjectStartKind.SaveState, "source.tasstate", 2), metadata.Start);
        Assert.Equal(config.Fingerprint, metadata.Configuration!.Fingerprint);
        File.Delete(source);
        await service.StepAsync(); await service.SaveProjectAsync(project);
        await service.LoadProjectAsync(project, files.Options);
        Assert.Single(service.Inputs);
        await service.SeekAsync(0);
        Assert.Equal(memory, await service.ReadMemoryAsync(0x80000000, 4));
        await service.ApplyConfigurationAsync(config with { StartUtcSeconds = 1234567891 }, new());
        Assert.Single(service.Inputs);
        await service.SaveProjectAsync(project);
        Assert.Equal(ProjectStartKind.PowerOn, FolderProject.Load(project).Archive.Metadata.Start!.Kind);
    }

    [Fact]
    public async Task LoadFailureRestoresPreviousProjectAndItsInputHistory()
    {
        using var files = new TestWorkspace(); var backend = new FakeBackend(); using var service = new ExecutionService(backend);
        var original = files.FilePath("original/original.tasproj");
        await service.CreateProjectAsync(original, files.GamePath, files.Options);
        await service.StepAsync(); var memory = await service.ReadMemoryAsync(0x80000000, 4);
        backend.FailNextLoad = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateProjectAsync(files.FilePath("failed/failed.tasproj"), files.GamePath, files.Options));
        Assert.Single(service.Inputs); Assert.Equal(1UL, service.Position);
        Assert.Equal(memory, await service.ReadMemoryAsync(0x80000000, 4));
        await service.SaveProjectAsync(original);
        Assert.Equal(ProjectStartKind.PowerOn, FolderProject.Load(original).Archive.Metadata.Start!.Kind);
    }

    [Fact]
    public async Task BadStateAndExistingDestinationCannotReplaceCurrentProject()
    {
        using var files = new TestWorkspace(); using var service = new ExecutionService(new FakeBackend());
        var project = files.FilePath("project/project.tasproj");
        await service.CreateProjectAsync(project, files.GamePath, files.Options);
        await service.StepAsync();
        var state = files.FilePath("source.tasstate"); await service.SaveStateAsync(state);
        var other = files.FilePath("other.iso"); File.WriteAllText(other, "different");
        await Assert.ThrowsAsync<InvalidDataException>(() => service.CreateProjectAsync(files.FilePath("bad/bad.tasproj"), other, files.Options, state));
        await Assert.ThrowsAsync<IOException>(() => service.CreateProjectAsync(project, files.GamePath, files.Options));
        Assert.Single(service.Inputs); Assert.Equal(1UL, service.Position);
    }

    [Fact]
    public async Task SaveFailureRestoresSession()
    {
        using var files = new TestWorkspace(); using var service = new ExecutionService(new FakeBackend());
        await service.CreateProjectAsync(files.FilePath("first/first.tasproj"), files.GamePath, files.Options);
        await service.StepAsync();
        var blocker = files.FilePath("file-not-folder"); File.WriteAllText(blocker, "keep");
        await Assert.ThrowsAnyAsync<IOException>(() => service.CreateProjectAsync(Path.Combine(blocker, "bad.tasproj"), files.GamePath, files.Options));
        Assert.Single(service.Inputs); Assert.Equal(1UL, service.Position); Assert.Equal("keep", File.ReadAllText(blocker));
    }
}
