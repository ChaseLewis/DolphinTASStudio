namespace TasStudio.App;

public sealed partial class MainWindow
{
    private string PlaybackPositionText
    {
        get
        {
            var seconds = (long)Math.Round(_execution.ElapsedSeconds, MidpointRounding.AwayFromZero);
            if (_execution.IsManualPlay) return $"Time: {seconds / 3600:00}:{seconds / 60 % 60:00}:{seconds % 60:00} | Frame: {_execution.VideoFieldCount:N0}";
            return $"Time: {seconds / 3600:00}:{seconds / 60 % 60:00}:{seconds % 60:00} | Frame: {_execution.VideoFieldCount:N0} | Poll: {CurrentPollPosition:N0}";
        }
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
