using TasStudio.Emulation;

namespace TasStudio.App;

public sealed record RecentTake(string Name, int Start, int Length);
public sealed record RecentProject(string Path, DateTimeOffset OpenedUtc, string GamePath, string GameHash,
    ulong Position, int InputCount, int TakeCount, ProjectStart? Start)
{
    public RecentTake[] TakeSpans { get; init; } = [];
    public string Name => System.IO.Path.GetFileNameWithoutExtension(Path);
    public static RecentProject From(string path, FolderProjectContent project) => new(System.IO.Path.GetFullPath(path), DateTimeOffset.UtcNow,
        project.Archive.Metadata.GamePath, project.Archive.Metadata.GameHash, project.Archive.Metadata.Position,
        project.Archive.Metadata.Inputs.Length, project.Takes.Length, project.Archive.Metadata.Start)
        { TakeSpans = project.Takes.Take(4).Select(t => new RecentTake(t.Name, t.Start, t.Inputs.Length)).ToArray() };
    public string StartLabel => Start?.Kind switch
    {
        ProjectStartKind.PowerOn => "Power on",
        ProjectStartKind.SaveState => $"Save state · exploration · source frame {Start.SourcePosition:N0}",
        _ => "Captured state · existing project"
    };
}
