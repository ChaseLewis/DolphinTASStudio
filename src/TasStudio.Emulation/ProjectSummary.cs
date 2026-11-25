namespace TasStudio.Emulation;

public sealed record ProjectTakeSummary(string Name, int Start, int Length);

/// <summary>Small description of a committed project, without rereading its assets.</summary>
public sealed record ProjectSummary(string GamePath, string GameHash, ulong Position, int InputCount,
    int TakeCount, ProjectStart? Start, ProjectTakeSummary[] TakeSpans)
{
    internal static ProjectSummary From(ArchiveMetadata metadata, IReadOnlyList<InputTake> takes) =>
        new(metadata.GamePath, metadata.GameHash, metadata.Position, metadata.Inputs.Length, takes.Count,
            metadata.Start, takes.Take(4).Select(t => new ProjectTakeSummary(t.Name, t.Start, t.Inputs.Length)).ToArray());
}
