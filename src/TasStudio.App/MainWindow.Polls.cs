namespace TasStudio.App;

public sealed partial class MainWindow
{
    private string PlaybackPositionText
    {
        get
        {
            var time = FormatTimelineTime(_execution.ElapsedSeconds, _execution.HasProject ? _execution.TimelineOffsetMilliseconds : 0);
            if (_execution.IsManualPlay) return $"Time: {time} | Frame: {_execution.VideoFieldCount:N0}";
            return $"Time: {time} | Frame: {_execution.VideoFieldCount:N0} | Poll: {CurrentPollPosition:N0}";
        }
    }

    internal static string FormatTimelineTime(double seconds, long offsetMilliseconds = 0)
    {
        // Round before splitting components so boundaries carry into the next second.
        var milliseconds = (long)Math.Round(seconds * 1000, MidpointRounding.AwayFromZero) - offsetMilliseconds;
        var sign = milliseconds < 0 ? "-" : "";
        milliseconds = Math.Abs(milliseconds);
        return FormattableString.Invariant($"{sign}{milliseconds / 3600000:00}:{milliseconds / 60000 % 60:00}:{milliseconds / 1000 % 60:00}.{milliseconds % 1000:000}");
    }

    private ulong CurrentPollPosition
    {
        get
        {
            var boundaries = _execution.PollBoundaries;
            return _execution.Position < (ulong)boundaries.Count ? boundaries[(int)_execution.Position] : boundaries[^1];
        }
    }
}
