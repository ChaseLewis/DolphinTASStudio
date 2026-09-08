using TasStudio.Sdk;

namespace TasStudio.Experiment.Fixtures;

public sealed record ResumeResult(int Index, int Count, long Utc, int MovieLength, ulong Group);

/// <summary>Small native trial for exercising coordinator cancellation and resume.</summary>
public sealed class ResumeTrial : IExperiment<ResumeResult>
{
    public ExperimentBootOptions Initialize(ExperimentInitializationContext context) =>
        new(context.DefaultStartUtcSeconds + context.Index);

    public async Task<ResumeResult> RunAsync(ExperimentRunContext context, CancellationToken token)
    {
        context.Log(new { Ready = true, context.Index });
        await Task.Delay(context.Index == 0 ? 0 : 1500, token);
        await context.Emulator.AdvanceAsync();
        if (context.Index == 3) throw new InvalidOperationException("Deliberate failure to verify artifact retention.");
        return new(context.Index, context.Count, await context.Emulator.GetStartUtcAsync(),
            context.Movie.Count, (await context.Emulator.GetPositionAsync()).Group);
    }
}
