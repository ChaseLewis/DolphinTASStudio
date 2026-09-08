using TasStudio.Core;
using TasStudio.Sdk;

namespace Basic.Experiments;

public sealed record Parameters(int PulseGroups = 60, double MaximumSeconds = 300);
public enum Outcome { Completed, TimeLimit }
public sealed record Result(Outcome Outcome, long Utc, int OriginalMovieGroups, ulong EndGroup, double Seconds);

/// <summary>Replay the original movie, then alternate A/neutral in this worker only.</summary>
public sealed class PulseExperiment : IExperiment<Result>
{
    public ExperimentBootOptions Initialize(ExperimentInitializationContext context)
    {
        var options = context.GetParameters<Parameters>();
        if (options.PulseGroups is < 1 or > 10000 || !double.IsFinite(options.MaximumSeconds) || options.MaximumSeconds <= 0)
            throw new ArgumentException("Use 1–10000 pulse groups and a positive, finite time limit.");
        return context.StartingPoint == ExperimentStartingPoint.PowerOn
            ? new(checked(context.DefaultStartUtcSeconds + context.Index))
            : new(); // A saved state keeps its saved clock.
    }

    public async Task<Result> RunAsync(ExperimentRunContext context, CancellationToken cancellationToken)
    {
        var emulator = context.Emulator;
        var options = context.GetParameters<Parameters>();
        var initial = await emulator.GetPositionAsync();
        var utc = await emulator.GetStartUtcAsync();
        var pulses = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var position = await emulator.GetPositionAsync();
            var elapsed = position.Seconds - initial.Seconds;
            if (pulses == options.PulseGroups)
                return new(Outcome.Completed, utc, context.Movie.Count, position.Group, elapsed);
            if (elapsed >= options.MaximumSeconds)
                return new(Outcome.TimeLimit, utc, context.Movie.Count, position.Group, elapsed);

            if (position.Group < (ulong)context.Movie.Count)
            {
                if (!await emulator.PlayAsync()) throw new InvalidOperationException("Movie ended before its recorded boundary.");
                continue;
            }

            var input = ControllerState.Neutral with { Buttons = pulses % 2 == 0 ? PadButtons.A : PadButtons.None };
            await emulator.SetCurrentInputAsync(input);
            await emulator.AdvanceAsync();
            pulses++;
        }
    }
}
