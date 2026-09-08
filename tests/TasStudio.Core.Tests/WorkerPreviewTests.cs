using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using TasStudio.Emulation;
using TasStudio.Worker;
using Xunit;

namespace TasStudio.Core.Tests;

public sealed class WorkerPreviewTests
{
    [Fact]
    public void MissingHeadlessOptionPreservesExistingBackgroundBehavior()
    {
        var config = new CSharpExperimentFile("Test", "source", "project", "assembly", "type");
        Assert.True(config.Headless);
        Assert.True(Job().Headless);
        var json = JsonSerializer.SerializeToElement(Job());
        var withoutOption = json.EnumerateObject().Where(p => p.Name != "Headless").ToDictionary(p => p.Name, p => p.Value);
        Assert.True(JsonSerializer.Deserialize<ExperimentJob>(JsonSerializer.Serialize(withoutOption))!.Headless);
        Assert.False(JsonSerializer.Deserialize<ExperimentJob>(JsonSerializer.Serialize(Job() with { Headless = false }))!.Headless);
    }

    [AvaloniaFact]
    public async Task PreviewShowsLatestFrameAndClosingWaitsForOnlyItsTrialCleanup()
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var unrelatedTrial = new CancellationTokenSource();
        var window = new WorkerPreviewWindow(Job() with { Index = 1, Headless = false }, async (publish, token) =>
        {
            using var registration = token.Register(() => cancelled.TrySetResult());
            // Publishing many frames from a worker keeps only the latest one for the UI.
            for (var i = 1; i <= 1000; i++) publish(new(4, 2, Enumerable.Repeat((byte)255, 32).ToArray(), i));
            ready.TrySetResult();
            return await finish.Task;
        });
        window.Completed += code => completed.TrySetResult(code);
        window.Closed += (_, _) => closed.TrySetResult();
        try
        {
            window.Show();
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
            window.RefreshFrame();
            Assert.Contains("#1", window.Title);
            var image = window.GetVisualDescendants().OfType<Image>().Single();
            Assert.Equal(new PixelSize(4, 2), Assert.IsType<WriteableBitmap>(image.Source).PixelSize);
            Assert.Contains("1,000", window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text?.Contains("Rendered frame") == true).Text);
            window.Close();
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(window.IsVisible);
            Assert.False(unrelatedTrial.IsCancellationRequested);
            finish.TrySetResult(2);
            Assert.Equal(2, await completed.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(window.IsVisible);
            Assert.Null(image.Source);
        }
        finally
        {
            finish.TrySetResult(2);
            window.Close();
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [AvaloniaFact]
    public async Task CompletedTrialClosesItsWindowAutomatically()
    {
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var window = new WorkerPreviewWindow(Job(), (_, _) => Task.FromResult(0));
        window.Completed += code => completed.TrySetResult(code);
        window.Closed += (_, _) => closed.TrySetResult();
        window.Show();
        Assert.Equal(0, await completed.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(window.IsVisible);
    }

    private static ExperimentJob Job() => new("Preview test", "source", "core", "system", "output", ExperimentStart.Boot,
        null, null, JsonSerializer.SerializeToElement(new { }), 30, 0, 2);
}
