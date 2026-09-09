using TasStudio.Emulation;
using Xunit;

namespace TasStudio.Core.Tests;

public sealed class RealtimePlaybackTests
{
    [Fact]
    public async Task PlaybackEnablesInternalPacingAndPauseRetiresItBeforeManualAdvance()
    {
        using var workspace = new TestWorkspace();
        var backend = new RealtimeBackend();
        using var service = new ExecutionService(backend);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.AudioSourceChanged += source => { if (source != null) started.TrySetResult(); else stopped.TrySetResult(); };
        await service.LoadGameAsync(workspace.GamePath, workspace.Options);
        await service.StepAsync();
        Assert.Equal(0, backend.Starts);
        await service.RunAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await service.PauseAsync();
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(backend.Active);
        Assert.Equal(1, backend.Starts);
        Assert.Equal(1, backend.Stops);
        await service.StepAsync();
        Assert.False(backend.Active);
        Assert.Equal(1, backend.Starts);
        Assert.Equal(backend.ModeThread, Assert.Single(backend.Calls.Select(c => c.Thread).Distinct()));
    }

    [Fact]
    public async Task FailedAdvanceDisablesRealtimeAudio()
    {
        using var workspace = new TestWorkspace();
        var backend = new RealtimeBackend { FailNextStep = true };
        using var service = new ExecutionService(backend);
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.StatusChanged += status => { if (status.StartsWith("Emulation stopped:")) failed.TrySetResult(); };
        await service.LoadGameAsync(workspace.GamePath, workspace.Options);
        await service.RunAsync();
        await failed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(service.IsRunning);
        Assert.False(backend.Active);
        Assert.Equal(1, backend.Stops);
    }

    private sealed class RealtimeBackend : FakeBackend, IRealtimeAudioBackend, IAudioSource
    {
        public bool Active { get; private set; }
        public int Starts { get; private set; }
        public int Stops { get; private set; }
        public int ModeThread { get; private set; }
        public int SampleRate => 48000;
        public IAudioSource StartRealtimePlayback() { ModeThread = Environment.CurrentManagedThreadId; Active = true; Starts++; return this; }
        public void StopRealtimePlayback() { Assert.Equal(ModeThread, Environment.CurrentManagedThreadId); Active = false; Stops++; }
        public void Read(short[] samples, int count) => Array.Clear(samples, 0, count);
    }
}
