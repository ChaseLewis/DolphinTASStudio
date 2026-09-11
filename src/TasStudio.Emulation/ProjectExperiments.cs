using System.Text.Json;
using System.Text.Json.Nodes;

namespace TasStudio.Emulation;

public sealed record ProjectExperiment(string ConfigPath, string Name, string? SourceProject, string? StateId, string? Error = null);
public sealed record PreparedExperimentRun(string Directory, string ConfigPath, string BatchDirectory);

/// <summary>Project-local recipes. Their config is the durable state association; batches are independent snapshots.</summary>
public static class ProjectExperiments
{
    public const string ConfigFileName = "experiment.tascsharp.json";
    public const int MaximumNameLength = 100;
    public static string Root(string sourceProject) => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(sourceProject))!, "experiments");

    public static string Create(string sourceProject, string stateId, string name, IEnumerable<string>? libraries = null)
    {
        name = ValidateName(name);
        var root = Root(sourceProject);
        var stem = new string(name.Select(c => char.IsLetterOrDigit(c) || c == '-' ? char.ToLowerInvariant(c) : '-').ToArray()).Trim('-');
        if (stem.Length == 0) stem = "search";
        stem = "experiment-" + stem[..Math.Min(stem.Length, 60)];
        var folder = Path.Combine(root, stem);
        for (var suffix = 2; Directory.Exists(folder) || File.Exists(folder); suffix++) folder = Path.Combine(root, stem + "-" + suffix);
        CSharpWorkspace.Create(sourceProject, folder, stateId, name, libraries);
        return Path.Combine(folder, ConfigFileName);
    }

    public static IReadOnlyList<ProjectExperiment> Discover(string sourceProject)
    {
        var root = Root(sourceProject);
        if (!Directory.Exists(root)) return [];
        var result = new List<ProjectExperiment>();
        foreach (var folder in Directory.EnumerateDirectories(root).Order(StringComparer.OrdinalIgnoreCase))
        {
            var path = Path.Combine(folder, ConfigFileName);
            if (!File.Exists(path)) continue;
            try
            {
                var config = ExperimentFiles.Read<CSharpExperimentFile>(path);
                var source = Path.GetFullPath(config.SourceProject, folder);
                result.Add(new(path, config.Name, source, config.Start == ExperimentStart.SaveState ? config.StateId : null));
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or ArgumentException or UnauthorizedAccessException or NotSupportedException)
            { result.Add(new(path, Path.GetFileName(folder), null, null, ex.Message)); }
        }
        return result.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.ConfigPath, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static string ValidateName(string? name)
    {
        name = name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > MaximumNameLength || name.Any(char.IsControl))
            throw new InvalidDataException($"Enter an experiment name of 1–{MaximumNameLength} characters on one line.");
        return name;
    }

    public static void Rename(string sourceProject, string configPath, string name)
    {
        configPath = RequireProjectConfig(sourceProject, configPath);
        name = ValidateName(name);
        var config = ReadConfig(configPath);
        config["Name"] = name;
        // Keep paths and previously frozen requests stable, including for an active batch.
        WriteConfig(configPath, config);
    }

    public static void SetTopPlays(string sourceProject, string configPath, ExperimentTopPlays? options)
    {
        configPath = RequireProjectConfig(sourceProject, configPath);
        options?.Validate();
        var config = ReadConfig(configPath);
        if (options == null) config.Remove("TopPlays");
        else config["TopPlays"] = JsonSerializer.SerializeToNode(options, ExperimentFiles.Json);
        WriteConfig(configPath, config);
    }

    public static IReadOnlyList<string> PreviousBatches(string configPath)
    {
        var runs = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(configPath))!, ".runs");
        return !Directory.Exists(runs) ? [] : Directory.EnumerateDirectories(runs).OrderDescending(StringComparer.OrdinalIgnoreCase)
            // Studio wraps batches in a run folder; CLI output may be the run folder itself.
            .SelectMany(run => new[] { Path.Combine(run, "batch"), run })
            .Where(batch => File.Exists(Path.Combine(batch, "results.sqlite"))).ToArray();
    }

    public static string Remove(string sourceProject, string configPath)
    {
        configPath = RequireProjectConfig(sourceProject, configPath);
        // Retire only the recipe. Its folder, source, dependencies and running/past batches stay in place.
        // Restoring this file's original name makes the experiment discoverable again.
        var retired = configPath + ".removed-" + Guid.NewGuid().ToString("N");
        File.Move(configPath, retired);
        return retired;
    }

    private static string RequireProjectConfig(string sourceProject, string configPath)
    {
        var root = Path.GetFullPath(Root(sourceProject));
        var full = Path.GetFullPath(configPath);
        var folder = Path.GetDirectoryName(full)!;
        if (!string.Equals(Path.GetFileName(full), ConfigFileName, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetDirectoryName(folder), root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Select an experiment from this project's experiments folder.");
        // Do not rename a recipe through a linked directory outside the project.
        foreach (var path in new[] { root, folder, full })
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Experiment management does not follow linked folders or files.");
        return full;
    }

    public static void Attach(string configPath, string sourceProject, string stateId)
    {
        RequireState(FolderProject.Load(sourceProject), stateId);
        var root = Path.GetDirectoryName(Path.GetFullPath(configPath))!;
        var config = ReadConfig(configPath);
        // Update only the start binding. Preserve custom fields, parameters, code and all prior runs.
        config["SourceProject"] = Path.GetRelativePath(root, Path.GetFullPath(sourceProject));
        config["Start"] = "SaveState";
        config["StateId"] = stateId;
        config["StartUtcSeconds"] = null;
        config["PrerollSeconds"] = 0;
        config["PrerollGroups"] = 0;
        WriteConfig(configPath, config);
    }

    public static PreparedExperimentRun PrepareRun(string configPath, string sourceProject, string stateId)
    {
        var config = ReadConfig(configPath);
        var folder = Path.GetDirectoryName(Path.GetFullPath(configPath))!;
        var typed = config.Deserialize<CSharpExperimentFile>(ExperimentFiles.Json)!;
        if (!string.Equals(Path.GetFullPath(typed.SourceProject, folder), Path.GetFullPath(sourceProject), StringComparison.OrdinalIgnoreCase) ||
            typed.Start != ExperimentStart.SaveState || typed.StateId != stateId)
            throw new InvalidDataException("This experiment is no longer attached to the selected state. Reattach it before running.");
        if (!File.Exists(Path.GetFullPath(typed.Project, folder)))
            throw new FileNotFoundException("The experiment's C# project is missing.", typed.Project);
        var source = FolderProject.Load(sourceProject);
        RequireState(source, stateId);
        var directory = Path.Combine(folder, ".runs", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8]);
        var snapshot = Path.Combine(directory, "source", "project.tasproj");
        var metadata = source.Archive.Metadata with { GamePath = FolderProject.ResolveRomPath(sourceProject, source.Archive.Metadata.GamePath) };
        FolderProject.Save(snapshot, metadata, source.Archive.InitialState, source.Takes, source.Sections, source.States, source.AutomaticCheckpoints);
        // Snapshot before handing control back to Studio, including when compilation is slow.
        config["SourceProject"] = Path.GetRelativePath(directory, snapshot);
        config["Project"] = Path.GetFullPath(config["Project"]!.GetValue<string>(), folder);
        var request = Path.Combine(directory, "request.tascsharp.json");
        WriteConfig(request, config);
        return new(directory, request, Path.Combine(directory, "batch"));
    }

    internal static void RequireState(FolderProjectContent source, string stateId)
    {
        if (stateId == ExperimentStates.ProjectStart) return;
        if (!source.States.Concat(source.AutomaticCheckpoints.Select(c => c.State)).Any(s => s.Id == stateId))
            throw new InvalidDataException("The selected state is no longer in the saved project. Save or select a replacement state and reattach the experiment.");
    }

    private static JsonObject ReadConfig(string path)
    {
        var config = JsonNode.Parse(File.ReadAllText(path), new JsonNodeOptions { PropertyNameCaseInsensitive = true }) as JsonObject
            ?? throw new InvalidDataException("Expected an experiment configuration object.");
        foreach (var key in new[] { "SourceProject", "Project", "AssemblyName", "TypeName" })
            if (config[key] is not JsonValue value || !value.TryGetValue<string>(out var text) || string.IsNullOrWhiteSpace(text))
                throw new InvalidDataException($"Experiment configuration requires {key}.");
        return config;
    }

    private static void WriteConfig(string path, JsonObject config)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temp, config.ToJsonString(ExperimentFiles.Json)); File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
