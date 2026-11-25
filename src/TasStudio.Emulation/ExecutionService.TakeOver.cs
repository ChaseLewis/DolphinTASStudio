namespace TasStudio.Emulation;

public sealed partial class ExecutionService
{
    private volatile bool _takingOver;
    private EmulatorSnapshot? _takeOverOrigin;
    private PlaybackPresentation? _takeOverPresentation;
    private bool _takeOverPlaybackOnly;
    public bool IsTakingOver => _takingOver;
    public ulong? TakeOverPosition => Volatile.Read(ref _takeOverOrigin)?.Position;

    /// <summary>Branch into temporary live play without changing the project's inputs or history.</summary>
    public Task BeginTakeOverAsync(ulong? position = null) => Enqueue(() =>
    {
        RequireProject(); Pause();
        if (position is { } target && (target != _backend.Position || !Current())) Seek(target);
        else EnsureCurrent();
        var origin = _backend.Capture();
        _takeOverPresentation = LatestPresentation;
        _takeOverPlaybackOnly = _playbackOnly;
        Volatile.Write(ref _takeOverOrigin, origin);
        _takingOver = true;
        _playbackOnly = false;
        Notify("Take Over Mode — live input is not recorded");
    });

    /// <summary>Discard temporary play and restore the exact takeover boundary, paused.</summary>
    public Task EndTakeOverAsync() => Enqueue(EndTakeOver);

    private void EndTakeOver()
    {
        if (!_takingOver) return;
        Pause();
        // Keep the snapshot and mode intact if restoring fails, so returning can be retried.
        RestoreBackend(_takeOverOrigin!);
        _executedHistory = _history!.At(_takeOverOrigin!.Position);
        var presentation = _takeOverPresentation!;
        _latestFrame = presentation.Video;
        _lastPresentedInput = presentation.Input;
        Present(presentation.Video, presentation.Input, presentation.Position);
        _playbackOnly = _takeOverPlaybackOnly;
        ClearTakeOver();
        Notify("Returned to TAS at the takeover point");
    }

    private void RequireNoTakeOver()
    {
        if (_takingOver) throw new InvalidOperationException("Return to the TAS before editing, seeking, or saving.");
    }

    private void ClearTakeOver()
    {
        _takingOver = false;
        Volatile.Write(ref _takeOverOrigin, null);
        _takeOverPresentation = null;
    }
}
