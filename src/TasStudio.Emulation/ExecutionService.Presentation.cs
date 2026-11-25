using TasStudio.Core;

namespace TasStudio.Emulation;

/// <summary>The image and last controller poll from the same completed presentation group.</summary>
public sealed record PlaybackPresentation(VideoFrame? Video, ControllerState? Input, ulong Position);

public sealed partial class ExecutionService
{
    private bool _collectingPresentation;
    private VideoFrame? _groupVideo;
    private ControllerState? _lastPresentedInput;
    private PlaybackPresentation _presentation = new(null, null, 0);
    public PlaybackPresentation LatestPresentation => Volatile.Read(ref _presentation);
    public event Action<PlaybackPresentation>? PresentationReady;

    private void ReceiveVideo(VideoFrame frame)
    {
        _latestFrame = frame;
        if (!_suppressMedia) VideoReady?.Invoke(frame);
        if (_collectingPresentation) _groupVideo = frame;
        else Present(frame, _lastPresentedInput, _backend.Position);
    }

    private void Present(VideoFrame? video, ControllerState? input, ulong position)
    {
        var presentation = new PlaybackPresentation(video, input, position);
        Volatile.Write(ref _presentation, presentation);
        if (!_suppressMedia) PresentationReady?.Invoke(presentation);
    }

    // At a restored boundary N, the visible image follows input N-1, never input N.
    // Empty poll groups retain the most recent actual poll; the baseline has no known input.
    private ControllerState? InputBeforePosition(ulong position)
    {
        if (!_hasProject || position > (ulong)_inputs.Count) return null;
        for (var index = (int)position - 1; index >= 0; index--)
        {
            if (!_pollFrames.TryGetValue(index, out var record)) return _inputs[index];
            if (record.Frame.Input != _inputs[index] || record.PrefixHash != _history!.At((ulong)index)) return null;
            if (record.Frame.Polls.Length > 0) return record.Frame.Polls[^1].Input;
        }
        return null;
    }

    private void RestoreBackend(EmulatorSnapshot snapshot)
    {
        _collectingPresentation = true;
        _groupVideo = null;
        try { _backend.Restore(snapshot); }
        finally { _collectingPresentation = false; }
        _latestFrame = _groupVideo ?? snapshot.Preview;
        _lastPresentedInput = InputBeforePosition(snapshot.Position);
        Present(_latestFrame, _lastPresentedInput, snapshot.Position);
    }
}
