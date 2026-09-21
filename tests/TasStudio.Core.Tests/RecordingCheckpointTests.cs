using TasStudio.Emulation;
using Xunit;

namespace TasStudio.Core.Tests;

public sealed class RecordingCheckpointTests
{
    [Fact]
    public async Task SlowCheckpointWriterDoesNotBlockRecordingOrQueueMoreSnapshots()
    {
        using var files = new TestWorkspace(); using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new RealtimeBackend(); var writerThread = 0;
        using var service = new ExecutionService(backend, (path, metadata, snapshot) =>
        {
            writerThread = Environment.CurrentManagedThreadId; entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Writer was not released");
            ProjectArchive.Save(path, metadata, snapshot);
        });
        await service.LoadGameAsync(files.GamePath, files.Options); await service.NewProjectAsync();
        await service.ConfigureCheckpointsAsync(new(IntervalSeconds: 1, CaptureWhileRecording: true));
        var captures = backend.Calls.Count(c => c.Operation == "Capture");
        try
        {
            await service.RunAsync(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Until(() => service.Position >= 5);
            Assert.True(service.IsRunning); Assert.Empty(service.StateMarkers);
            Assert.Equal(captures + 1, backend.Calls.Count(c => c.Operation == "Capture"));
            Assert.NotEqual(Assert.Single(backend.Calls.Select(c => c.Thread).Distinct()), writerThread);
            var pause = service.PauseAsync();
            await Until(() => !service.IsRunning);
            Assert.False(pause.IsCompleted); // Pause drains the one outstanding write before returning.
            release.Set(); await pause.WaitAsync(TimeSpan.FromSeconds(5));
            var checkpoint = Assert.Single(service.StateMarkers);
            Assert.True(checkpoint.Valid); Assert.Equal(1UL, checkpoint.Position);
            var state = ProjectArchive.Load(Assert.Single(Directory.GetFiles(files.Options.SaveDirectory, "*.tasstate", SearchOption.AllDirectories)), ProjectArchive.StateKind);
            Assert.Equal(await service.HistoryAtAsync(1), state.Metadata.HistoryHash);
            var inputs = service.Inputs.ToArray();
            await service.LoadMarkerAsync(checkpoint.Id);
            Assert.Equal(1UL, service.Position); Assert.Equal(inputs, service.Inputs);
            await service.SetInputAsync(0, ControllerState.Neutral with { Buttons = PadButtons.A });
            Assert.Empty(service.StateMarkers);
            Assert.Empty(Directory.GetFiles(files.Options.SaveDirectory, "*.tasstate", SearchOption.AllDirectories));
        }
        finally { release.Set(); }
    }

    [Fact]
    public async Task FailedBackgroundCheckpointReportsFailureAndKeepsRecording()
    {
        using var files = new TestWorkspace(); var failure = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var service = new ExecutionService(new RealtimeBackend(), (_, _, _) => throw new IOException("Injected disk failure"));
        service.StatusChanged += message => { if (message.Contains("Injected disk failure")) failure.TrySetResult(); };
        await service.LoadGameAsync(files.GamePath, files.Options); await service.NewProjectAsync();
        await service.ConfigureCheckpointsAsync(new(IntervalSeconds: 1, CaptureWhileRecording: true));
        await service.RunAsync(); await failure.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var position = service.Position;
        await Until(() => service.Position > position);
        Assert.True(service.IsRecordingLive); Assert.Empty(service.StateMarkers);
        await service.PauseAsync(); Assert.NotEmpty(service.Inputs);
    }

    [Fact]
    public async Task DisposalWaitsForCheckpointWriterThenRemovesItsOwnedFile()
    {
        using var files = new TestWorkspace(); using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new ExecutionService(new RealtimeBackend(), (path, metadata, snapshot) =>
        {
            entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Writer was not released");
            ProjectArchive.Save(path, metadata, snapshot);
        });
        try
        {
            await service.LoadGameAsync(files.GamePath, files.Options); await service.NewProjectAsync();
            await service.ConfigureCheckpointsAsync(new(IntervalSeconds: 1, CaptureWhileRecording: true));
            await service.RunAsync(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var dispose = Task.Run(service.Dispose);
            await Until(() => !service.IsRunning);
            Assert.False(dispose.IsCompleted);
            release.Set(); await dispose.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Empty(Directory.GetFiles(files.Options.SaveDirectory, "*.tasstate", SearchOption.AllDirectories));
        }
        finally { release.Set(); service.Dispose(); }
    }

    [Fact]
    public async Task DefaultRecordingDefersNativeCheckpointCaptureUntilPause()
    {
        using var files = new TestWorkspace(); var backend = new RealtimeBackend();
        using var service = new ExecutionService(backend);
        await service.LoadGameAsync(files.GamePath, files.Options); await service.NewProjectAsync();
        await service.ConfigureCheckpointsAsync(new(IntervalSeconds: 1));
        Assert.False(service.Checkpoints.CaptureWhileRecording);
        var captures = backend.Calls.Count(c => c.Operation == "Capture");
        await service.RunAsync(); await Until(() => service.Position >= 5);
        Assert.Equal(captures, backend.Calls.Count(c => c.Operation == "Capture"));
        Assert.Empty(service.StateMarkers);
        await service.PauseAsync();
        Assert.Equal(captures + 1, backend.Calls.Count(c => c.Operation == "Capture"));
        var checkpoint = Assert.Single(service.StateMarkers);
        Assert.True(checkpoint.Valid); Assert.Equal(service.Position, checkpoint.Position);
        await service.PauseAsync();
        Assert.Equal(captures + 1, backend.Calls.Count(c => c.Operation == "Capture"));
    }

    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(5, timeout.Token);
    }

    private sealed class RealtimeBackend : FakeBackend, IRealtimeAudioBackend, IAudioSource
    {
        public RealtimeBackend() { FieldsPerStep = 60; RecordPolls = true; BeforeStep = () => Thread.Sleep(5); }
        public int SampleRate => 48000;
        public IAudioSource StartRealtimePlayback() => this;
        public void StopRealtimePlayback() { }
        public void Read(short[] samples, int count) => Array.Clear(samples, 0, count);
    }
}
