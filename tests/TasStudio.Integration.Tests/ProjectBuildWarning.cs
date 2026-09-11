using System.Security.Cryptography;
using TasStudio.Dolphin;
using TasStudio.Emulation;

internal static class ProjectBuildWarning
{
    public static async Task Run(string rom, string output, string source)
    {
        var original = FolderProject.Load(source);
        var sourceBytes = File.ReadAllBytes(source);
        using var backend = new DolphinBackend();
        using var execution = new ExecutionService(backend);
        var options = new BackendOptions(Path.Combine(AppContext.BaseDirectory, "native/dolphin_libretro.dll"),
            Path.Combine(AppContext.BaseDirectory, "system"), Path.Combine(output, "profiles"));
        if (backend.InspectIdentity(options) == original.Archive.Metadata.BackendIdentity)
            throw new Exception("This test requires a project created with a different core binary.");
        var rejected = false;
        try { await execution.LoadProjectAsync(source, options, rom, requireExactRuntime: true); }
        catch (InvalidDataException) { rejected = true; }
        if (!rejected) throw new Exception("Strict worker mode accepted a different build.");
        await execution.LoadProjectAsync(source, options, rom);
        if (execution.CompatibilityWarning == null || execution.Position != original.Archive.Metadata.Position ||
            !execution.Inputs.SequenceEqual(original.Archive.Metadata.Inputs))
            throw new Exception("Project position, inputs or warning were not preserved.");
        var memory = Convert.ToHexString(SHA256.HashData(await execution.ReadMemoryAsync(0x80000000, 24 * 1024 * 1024)));
        Console.WriteLine($"Opened original project at group {execution.Position} with warning: {execution.CompatibilityWarning}");
        var saved = Path.Combine(output, "copy", "project.tasproj");
        await execution.SaveProjectAsync(saved);
        var copy = FolderProject.Load(saved);
        if (copy.Archive.Metadata.BackendIdentity != original.Archive.Metadata.BackendIdentity ||
            copy.Archive.Metadata.ConfigurationIdentity != original.Archive.Metadata.ConfigurationIdentity ||
            !copy.Archive.InitialState.Data.SequenceEqual(original.Archive.InitialState.Data))
            throw new Exception("Saving relabeled or replaced the original baseline.");
        await execution.LoadProjectAsync(saved, options, rom);
        var actual = Convert.ToHexString(SHA256.HashData(await execution.ReadMemoryAsync(0x80000000, 24 * 1024 * 1024)));
        if (actual != memory || execution.CompatibilityWarning == null)
            throw new Exception("Reopened copy diverged or lost its warning.");
        if (!File.ReadAllBytes(source).SequenceEqual(sourceBytes)) throw new Exception("Original project was modified.");
        Console.WriteLine($"PASS: {execution.Inputs.Count} inputs, position {execution.Position}, RAM {actual}; original baseline/build retained, copy reopened, source unchanged, strict worker mode rejected.");
    }
}
