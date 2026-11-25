using System.Text.Json;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TasStudio.App;
using TasStudio.Emulation;
using Xunit;

namespace TasStudio.Core.Tests;

public sealed class ProjectMemoryCardTests
{
    [AvaloniaFact]
    public async Task ProjectCardDialogOpensTheProjectFolderAndCommitsAnExplicitRestart()
    {
        using var files = new TestWorkspace(); using var service = new ExecutionService(new FakeBackend());
        var project = files.FilePath("project/project.tasproj");
        await service.CreateProjectAsync(project, files.GamePath, files.Options);
        await service.StepAsync(); await service.SaveNamedStateAsync("before restart");
        var main = new MainWindow(["--layout-file", files.FilePath("layout.json")], new AppSettings(), service);
        typeof(MainWindow).GetField("_projectPath", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(main, project);
        try
        {
            main.Show(); main.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            var opening = main.ManageProjectMemoryCard();
            await Until(() => main.OwnedWindows.Any());
            var dialog = main.OwnedWindows.Single();
            Assert.Contains(dialog.GetVisualDescendants().OfType<TextBox>(), box => box.Text == ProjectMemoryCards.DirectoryFor(project));
            if (Environment.GetEnvironmentVariable("TASSTUDIO_TEST_CAPTURE_DIR") is { Length: > 0 } capture)
            {
                Directory.CreateDirectory(capture); dialog.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                dialog.CaptureRenderedFrame()!.Save(Path.Combine(capture, "project-memory-card.png"));
            }
            var restart = dialog.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "RestartProjectGciFolder");
            restart.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => restart.IsEnabled);
            Assert.True(FolderProject.Load(project).Archive.Metadata.Configuration!.UseGciFolder);
            Assert.Single(service.Inputs); Assert.Equal(0UL, service.Position);
            Assert.DoesNotContain(service.StateMarkers, state => state.Name == "before restart");
            dialog.Close(); await opening;
        }
        finally { foreach (var dialog in main.OwnedWindows.ToArray()) dialog.Close(); main.CloseAfterCapture(); }
    }

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition()) { if (DateTime.UtcNow > deadline) throw new TimeoutException(); Dispatcher.UIThread.RunJobs(); await Task.Delay(10); }
    }

    [Fact]
    public async Task ProjectFolderFollowsSaveAsAndRelocationWhileWorkersGetPrivateFolders()
    {
        using var files = new TestWorkspace(); using var service = new ExecutionService(new FakeBackend());
        var options = files.Options with { Configuration = new EmulationConfiguration { UseGciFolder = true } };
        var path = files.FilePath("original/project.tasproj");
        await service.CreateProjectAsync(path, files.GamePath, options);
        var card = await service.PrepareProjectMemoryCardAsync(path);
        Assert.Equal(ProjectMemoryCards.DirectoryFor(path), (await service.GetConfigurationAsync())!.Options.GciDirectory);
        File.WriteAllText(Path.Combine(card, "save.gci"), "card data");
        await service.StepAsync(); var memory = await service.ReadMemoryAsync(0x80000000, 4);
        await service.SaveNamedStateAsync("before copy");
        var copy = files.FilePath("copy/project.tasproj");
        await service.SaveProjectAsync(copy);
        Assert.Equal("card data", File.ReadAllText(Path.Combine(ProjectMemoryCards.DirectoryFor(copy), "save.gci")));
        Assert.Equal(ProjectMemoryCards.DirectoryFor(copy), (await service.GetConfigurationAsync())!.Options.GciDirectory);
        Assert.Equal(memory, await service.ReadMemoryAsync(0x80000000, 4)); Assert.Single(service.Inputs);
        Assert.Contains(service.StateMarkers, state => state.Name == "before copy" && state.Valid);
        await service.LoadProjectAsync(copy, files.Options);
        Assert.Equal(ProjectMemoryCards.DirectoryFor(copy), (await service.GetConfigurationAsync())!.Options.GciDirectory);
        await service.LoadProjectAsync(copy, files.Options, isolatedMemoryCard: true);
        var worker = (await service.GetConfigurationAsync())!.Options;
        Assert.StartsWith(worker.SaveDirectory, worker.GciDirectory);
        Assert.NotEqual(card, worker.GciDirectory);
        await service.LoadProjectAsync(copy, files.Options, powerOnConfiguration: options.Configuration! with { StartUtcSeconds = 123 }, isolatedMemoryCard: true);
        Assert.StartsWith((await service.GetConfigurationAsync())!.Options.SaveDirectory, (await service.GetConfigurationAsync())!.Options.GciDirectory);
    }

    [Fact]
    public async Task RestartSwitchesLegacyProjectToFolderAndKeepsInputsButInvalidatesStates()
    {
        using var files = new TestWorkspace(); var backend = new FakeBackend(); using var service = new ExecutionService(backend);
        var path = files.FilePath("project/project.tasproj");
        await service.CreateProjectAsync(path, files.GamePath, files.Options);
        await service.StepAsync(); await service.SaveNamedStateAsync("old baseline");
        backend.FailNextLoad = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RestartFromProjectMemoryCardAsync(path));
        Assert.Equal(1UL, service.Position); Assert.Contains(service.StateMarkers, state => state.Name == "old baseline" && state.Valid);
        await service.RestartFromProjectMemoryCardAsync(path);
        Assert.Equal(0UL, service.Position); Assert.Single(service.Inputs);
        Assert.DoesNotContain(service.StateMarkers, state => state.Name == "old baseline");
        var options = (await service.GetConfigurationAsync())!.Options;
        Assert.True(options.Configuration!.UseGciFolder); Assert.Equal(ProjectMemoryCards.DirectoryFor(path), options.GciDirectory);
        await service.SaveProjectAsync(path);
        Assert.True(FolderProject.Load(path).Archive.Metadata.Configuration!.UseGciFolder);
    }

    [Fact]
    public void ExistingRawConfigurationSerializationDoesNotChange()
    {
        var options = new JsonSerializerOptions { IgnoreReadOnlyProperties = true };
        Assert.DoesNotContain("UseGciFolder", JsonSerializer.Serialize(new EmulationConfiguration().ValidatedCopy(), options));
        Assert.NotEqual(new EmulationConfiguration().Fingerprint, new EmulationConfiguration { UseGciFolder = true }.Fingerprint);
    }
}
