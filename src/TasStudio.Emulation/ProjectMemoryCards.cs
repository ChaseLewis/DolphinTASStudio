namespace TasStudio.Emulation;

public static class ProjectMemoryCards
{
    public static string DirectoryFor(string projectPath) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(projectPath))!, "gci");

    internal static void Copy(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        if (Path.GetFullPath(source).Equals(Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase) || !Directory.Exists(source)) return;
        foreach (var file in Directory.EnumerateFiles(source).Where(file =>
            Path.GetExtension(file).Equals(".gci", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(file) == "MC_SYSTEM_AREA"))
            PlayMemoryCards.WriteAtomic(Path.Combine(destination, Path.GetFileName(file)), File.ReadAllBytes(file));
    }
}
