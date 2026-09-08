using System.Diagnostics;
using System.Text.Json;
using TasStudio.Emulation;

internal sealed record CSharpExperimentFile(string Name, string SourceProject, string Project, string AssemblyName,
    string TypeName, int Count = 4, int Parallelism = 2, int TimeoutSeconds = 300,
    ExperimentStart Start = ExperimentStart.Boot, string? StateId = null, long? StartUtcSeconds = null,
    double PrerollSeconds = 0, int PrerollGroups = 0, JsonElement Parameters = default, bool Headless = true);

internal static class CSharpCommand
{
    public static async Task<int> ResumeAsync(string batchDirectory, CancellationToken token)
    {
        ValidateRuntimeFiles();
        var results = await ExperimentRunner.ResumeAsync(batchDirectory,
            Path.Combine(AppContext.BaseDirectory, "TasStudio.Worker.exe"),
            Path.Combine(AppContext.BaseDirectory, "native", "dolphin_libretro.dll"),
            Path.Combine(AppContext.BaseDirectory, "system"), Console.WriteLine, token);
        Console.WriteLine("Results: " + Path.Combine(batchDirectory, "results.json"));
        return !token.IsCancellationRequested && results.All(r => r.Status == "completed") ? 0 : 1;
    }

    public static async Task<int> RunAsync(string configPath, string outputDirectory, CancellationToken token)
    {
        configPath = Path.GetFullPath(configPath); outputDirectory = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(outputDirectory)) throw new IOException("Choose a new batch directory; existing runs are preserved.");
        ValidateRuntimeFiles();
        var config = ExperimentFiles.Read<CSharpExperimentFile>(configPath);
        var root = Path.GetDirectoryName(configPath)!;
        var sourcePath = Path.GetFullPath(config.SourceProject, root);
        var projectPath = Path.GetFullPath(config.Project, root);
        var parameters = config.Parameters.ValueKind == JsonValueKind.Undefined ? JsonSerializer.SerializeToElement(new { }) : config.Parameters;
        if (config.Count < 1) throw new InvalidDataException("Count must be at least 1.");
        if (string.IsNullOrWhiteSpace(config.AssemblyName) || config.AssemblyName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new InvalidDataException("AssemblyName must be the experiment assembly's simple name.");
        var definition = new ExperimentDefinition(1, config.Name, config.Start, config.StateId, config.StartUtcSeconds,
            config.Parallelism, config.TimeoutSeconds, Enumerable.Range(0, config.Count).Select(i => new ExperimentTrial("Trial " + i, parameters)).ToArray(),
            config.PrerollSeconds, config.PrerollGroups, TypeName: config.TypeName, Headless: config.Headless);
        definition.Validate();
        Directory.CreateDirectory(outputDirectory);
        ExperimentFiles.Write(Path.Combine(outputDirectory, "request.json"), config);
        var buildDirectory = Path.Combine(outputDirectory, "build");
        Console.WriteLine("Building C# experiment: " + projectPath);
        using (var build = new Process { StartInfo = new("dotnet") { UseShellExecute = false, CreateNoWindow = true } })
        {
            foreach (var arg in new[] { "publish", projectPath, "-c", "Debug", "--nologo", "-o", buildDirectory }) build.StartInfo.ArgumentList.Add(arg);
            build.Start();
            try { await build.WaitForExitAsync(token); }
            finally { if (!build.HasExited) { build.Kill(entireProcessTree: true); await build.WaitForExitAsync(); } }
            if (build.ExitCode != 0) throw new InvalidOperationException("Experiment build failed. See compiler output.");
        }
        token.ThrowIfCancellationRequested();
        var source = FolderProject.Load(sourcePath);
        var snapshot = Path.Combine(outputDirectory, "source", "project.tasproj");
        var metadata = source.Archive.Metadata with { GamePath = FolderProject.ResolveRomPath(sourcePath, source.Archive.Metadata.GamePath) };
        FolderProject.Save(snapshot, metadata, source.Archive.InitialState, source.Takes, source.Sections, source.States, source.AutomaticCheckpoints);
        var worker = Path.Combine(AppContext.BaseDirectory, "TasStudio.Worker.exe");
        var results = await ExperimentRunner.RunAsync(definition with { AssemblyPath = Path.Combine(buildDirectory, config.AssemblyName + ".dll") },
            snapshot, outputDirectory, worker, Path.Combine(AppContext.BaseDirectory, "native", "dolphin_libretro.dll"),
            Path.Combine(AppContext.BaseDirectory, "system"), Console.WriteLine, token);
        Console.WriteLine("Results: " + Path.Combine(outputDirectory, "results.json"));
        return !token.IsCancellationRequested && results.All(r => r.Status == "completed") ? 0 : 1;
    }

    private static void ValidateRuntimeFiles()
    {
        foreach (var name in new[] { "dolphin_libretro.dll", "TasStudio.LibretroHost.dll" })
        {
            var path = Path.Combine(AppContext.BaseDirectory, "native", name);
            if (!File.Exists(path)) throw new FileNotFoundException(
                $"Incomplete TAS Studio build: missing {path}. Set tasStudio.workerPath to TasStudio.Worker.exe in a complete published build (artifacts/prod for repository development).", path);
        }
        var system = Path.Combine(AppContext.BaseDirectory, "system", "dolphin-emu", "Sys");
        if (!Directory.Exists(system)) throw new DirectoryNotFoundException(
            $"Incomplete TAS Studio build: missing {system}. Use the worker from a complete published build.");
    }
}
