using Skies;
using TasStudio.Core;
using TasStudio.Sdk;

namespace TasStudio.Experiment.Fixtures;

public sealed class NativeTrial : IExperiment
{
    private int _initializedIndex = -1;
    public ExperimentBootOptions Initialize(ExperimentInitializationContext context)
    {
        _initializedIndex = context.Index;
        return context.StartingPoint == ExperimentStartingPoint.PowerOn ? new(context.DefaultStartUtcSeconds + context.Index) : new();
    }
    public async Task<object?> RunAsync(ExperimentRunContext context, CancellationToken token)
    {
        if (_initializedIndex != context.Index) throw new Exception("Initialization instance was not retained.");
        var game = new SkiesGame(context.Emulator);
        var before = await context.Emulator.GetPositionAsync();
        var original = context.Movie[checked((int)before.Group)];
        var edited = original with { Buttons = PadButtons.A };
        await context.Emulator.SetCurrentInputAsync(edited);
        await context.Emulator.AdvanceAsync(ControllerState.Neutral with { Buttons = PadButtons.B });
        if ((await context.Emulator.GetInputAsync(checked((int)before.Group))).Buttons != PadButtons.A || context.Movie[checked((int)before.Group)] != original)
            throw new Exception("Recorded-input preservation or immutable movie failed.");
        var state = await context.Emulator.SaveStateAsync("C# state");
        await context.Emulator.AdvanceAsync(); await context.Emulator.LoadStateAsync(state);
        await context.Emulator.AddTakeAsync("Candidate " + context.Index, checked((int)before.Group), [edited]);
        var value = new { context.Index, context.Count, StartGroup = before.Group, FinalGroup = (await context.Emulator.GetPositionAsync()).Group,
            Utc = await context.Emulator.GetStartUtcAsync(), MovieLength = context.Movie.Count, Rng = await game.ReadRngAsync() };
        context.Log(value); token.ThrowIfCancellationRequested(); return value;
    }
}

public sealed class InitializationLoop : IExperiment
{
    public ExperimentBootOptions Initialize(ExperimentInitializationContext context)
    {
        Console.WriteLine("initialization-loop");
        while (true) Thread.SpinWait(1000);
    }
    public Task<object?> RunAsync(ExperimentRunContext context, CancellationToken token) => throw new Exception("Must not run.");
}

public sealed class CancelRuntime : IExperiment
{
    public async Task<object?> RunAsync(ExperimentRunContext context, CancellationToken token)
    {
        await context.Emulator.SetInputAsync(0, ControllerState.Neutral with { Buttons = PadButtons.X });
        context.Log(new { Waiting = true });
        await Task.Delay(Timeout.Infinite, token); return null;
    }
}
