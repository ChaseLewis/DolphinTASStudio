using Skies;
using TasStudio.Core;
using TasStudio.Sdk;

namespace Skies.Experiments;

public sealed record SweepParameters(long? BaseUtcSeconds = null, int HoldVariants = 30);
public sealed record SweepResult(ulong Group, int OriginalMovieGroups, uint Rng, int Hold);

public sealed class UtcSweep : IExperiment<SweepResult>
{
    private int _hold;
    public ExperimentBootOptions Initialize(ExperimentInitializationContext context)
    {
        var parameters = context.GetParameters<SweepParameters>();
        if (parameters.HoldVariants is < 1 or > 60) throw new ArgumentException("HoldVariants must be 1–60.");
        _hold = 1 + context.Index % parameters.HoldVariants;
        // CUDA-style flattened parameter grid: fast-varying hold, slow-varying UTC.
        return context.StartingPoint == ExperimentStartingPoint.SaveState ? new() :
            new(checked((parameters.BaseUtcSeconds ?? context.DefaultStartUtcSeconds) + context.Index / parameters.HoldVariants));
    }
    public async Task<SweepResult> RunAsync(ExperimentRunContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var game = new SkiesGame(context.Emulator);
        var position = await context.Emulator.GetPositionAsync();
        context.Log(new { context.Index, Utc = await context.Emulator.GetStartUtcAsync(), StartGroup = position.Group, Hold = _hold });
        for (var i = 0; i < 60; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var input = ControllerState.Neutral with { Buttons = i < _hold ? PadButtons.A : PadButtons.None };
            await context.Emulator.SetCurrentInputAsync(input);
            await context.Emulator.AdvanceAsync();
        }
        var end = await context.Emulator.GetPositionAsync();
        return new(end.Group, context.Movie.Count, await game.ReadRngAsync(), _hold);
    }
}
