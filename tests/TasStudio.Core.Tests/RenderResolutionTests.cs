using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TasStudio.App;
using TasStudio.Emulation;
using Xunit;

namespace TasStudio.Core.Tests;

public sealed class RenderResolutionTests
{
    private sealed class ResolutionBackend : FakeBackend, IRenderResolutionBackend
    {
        public int? Override { get; private set; }
        public bool FailNextChange { get; set; }
        public void SetRenderResolution(int? scale)
        {
            Calls.Enqueue((nameof(SetRenderResolution), Environment.CurrentManagedThreadId));
            if (FailNextChange) { FailNextChange = false; throw new InvalidOperationException("Resolution change failed"); }
            Override = scale;
        }
    }

    [Fact]
    public async Task OverridePreservesProjectAndStatesAndSurvivesSeeking()
    {
        using var files = new TestWorkspace();
        var backend = new ResolutionBackend { RecordPolls = true };
        using var service = new ExecutionService(backend);
        await service.LoadGameAsync(files.GamePath, files.Options); await service.NewProjectAsync();
        await service.ConfigureCheckpointsAsync(new(IntervalSeconds: 1));
        for (var i = 0; i < 10; i++) await service.StepAsync();
        await service.SaveNamedStateAsync("Before override");
        var markers = service.StateMarkers.ToArray();
        var inputs = service.Inputs.ToArray();
        var revision = service.Revision;
        var config = await service.GetConfigurationAsync();
        var loads = backend.Calls.Count(c => c.Operation == "LoadGame");
        await service.SetRenderResolutionAsync(4);
        Assert.Equal(10UL, service.Position);
        Assert.Equal(revision, service.Revision);
        Assert.Equal(config, await service.GetConfigurationAsync());
        Assert.Equal(markers, service.StateMarkers);
        await service.SeekAsync(0); await service.SeekAsync(10);
        Assert.Equal(4, backend.Override);
        Assert.Equal(inputs, service.Inputs);
        Assert.Equal(loads, backend.Calls.Count(c => c.Operation == "LoadGame"));
        // Override playback must not populate the project's checkpoint cache.
        for (var i = 0; i < 60; i++) await service.StepAsync();
        Assert.Equal(markers, service.StateMarkers);
        backend.FailNextChange = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SetRenderResolutionAsync(2));
        Assert.Equal(4, service.RenderResolutionOverride);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.SetRenderResolutionAsync(5));
        await service.SetRenderResolutionAsync(null);
        Assert.Null(backend.Override); Assert.Null(service.RenderResolutionOverride);
        Assert.Single(backend.Calls.Select(c => c.Thread).Distinct());
        await service.SetRenderResolutionAsync(2); await service.StopAsync();
        Assert.Null(service.RenderResolutionOverride);
    }

    [AvaloniaFact]
    public async Task MovieSelectorIsExperimentalAndExitRestoresProjectDefault()
    {
        using var files = new TestWorkspace();
        var backend = new ResolutionBackend();
        using var service = new ExecutionService(backend);
        await service.LoadGameAsync(files.GamePath, files.Options); await service.NewProjectAsync();
        await service.StepAsync();
        var main = new MainWindow(["--layout-file", files.FilePath("layout.json")], new AppSettings(), service);
        try
        {
            main.ShowEditor(); main.Show();
            await main.EnterMovieModeAsync(); await service.PauseAsync();
            Dispatcher.UIThread.RunJobs();
            var selector = main.GetVisualDescendants().OfType<ComboBox>().Single(c => c.Name == "MovieRenderResolution");
            Assert.Contains("experimental", AutomationProperties.GetName(selector));
            Assert.Equal(0, selector.SelectedIndex);
            selector.SelectedIndex = 3;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (service.RenderResolutionOverride != 3 || !selector.IsEnabled)
            { Dispatcher.UIThread.RunJobs(); await Task.Delay(5, timeout.Token); }
            Assert.Equal(3, backend.Override);
            await main.ExitMovieModeAsync();
            Assert.Null(backend.Override);
            await main.EnterMovieModeAsync(); await service.PauseAsync(); Dispatcher.UIThread.RunJobs();
            Assert.Equal(0, selector.SelectedIndex);
            await main.ExitMovieModeAsync();
        }
        finally { main.CloseAfterCapture(); }
    }
}
