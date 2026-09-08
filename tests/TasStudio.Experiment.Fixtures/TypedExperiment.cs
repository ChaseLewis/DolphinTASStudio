using TasStudio.Sdk;

namespace TasStudio.Experiment.Fixtures;

public sealed record TypedResult(int Score, ResultDetails Details);
public readonly record struct ResultDetails(int[] Enemies, string Name);

public sealed class TypedExperiment : IExperiment<TypedResult>
{
    public TypedExperiment() => throw new InvalidOperationException("Schema discovery must not construct an experiment.");
    public Task<TypedResult> RunAsync(ExperimentRunContext context, CancellationToken token) => throw new NotSupportedException();
}
