using System.Text.Json;

namespace TasStudio.Emulation;

public sealed record CSharpExperimentFile(string Name, string SourceProject, string Project, string AssemblyName,
    string TypeName, int Count = 4, int Parallelism = 2, int TimeoutSeconds = 300,
    ExperimentStart Start = ExperimentStart.Boot, string? StateId = null, long? StartUtcSeconds = null,
    double PrerollSeconds = 0, int PrerollGroups = 0, JsonElement Parameters = default, bool Headless = true);
