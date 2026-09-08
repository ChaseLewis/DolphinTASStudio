using TasStudio.Core;

namespace TasStudio.Emulation;

public sealed partial class ExecutionService
{
    public Task<string> ImportInputTakeAsync(string name, int start, ControllerState[] inputs, string provenance)
    {
        var copy = (ControllerState[])inputs.Clone(); foreach (var input in copy) input.Validate();
        return Enqueue(() =>
        {
            RequireProject();
            if (start < 0 || start > _inputs.Count || copy.Length is < 1 or > 100000 || (long)start + copy.Length > int.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(start));
            var id = AddTake(name, start, copy, provenance); Notify("Experiment inputs imported as a take"); return id;
        });
    }
}
