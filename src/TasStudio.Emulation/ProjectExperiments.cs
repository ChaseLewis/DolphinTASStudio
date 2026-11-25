using System.Text.Json;
using System.Text.Json.Nodes;

namespace TasStudio.Emulation;

public sealed record ProjectExperiment(string ConfigPath, string Name, string? SourceProject, string? StateId, string? Error = null)
{
    public bool IsHidden { get; init; }
    public bool IsDeleted { get; init; }
    public bool WasRemoved { get; init; }
    public bool IsVisible => !IsHidden && !IsDeleted;
}
public sealed record PreparedExperimentRun(string Directory, string ConfigPath, string BatchDirectory)
{
    public string Name { get; init; } = "Experiment";
}

/// <summary>Project-local recipes. Their config is the durable state association; batches are independent snapshots.</summary>
public static class ProjectExperiments
{
    public const string ConfigFileName = "experiment.tascsharp.json";
    public const string IdentityFileName = "experiment.identity.json";
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
            var readable = ExistingConfig(path);
            if (readable == null) continue;
            var wasRemoved = !string.Equals(path, readable, StringComparison.OrdinalIgnoreCase);
            var identity = ReadIdentity(folder);
            var hidden = identity["Hidden"] is JsonValue h && h.TryGetValue<bool>(out var hide) && hide;
            var deleted = identity["Deleted"] is JsonValue d && d.TryGetValue<bool>(out var delete) && delete;
            ProjectExperiment experiment;
            try
            {
                var config = ExperimentFiles.Read<CSharpExperimentFile>(readable);
                var source = Path.GetFullPath(config.SourceProject, folder);
                experiment = new(path, DisplayName(path), source, !wasRemoved && config.Start == ExperimentStart.SaveState ? config.StateId : null);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or ArgumentException or UnauthorizedAccessException or NotSupportedException)
            { experiment = new(path, DirectoryDisplayName(folder), null, null, ex.Message); }
            result.Add(experiment with { IsHidden = hidden, IsDeleted = deleted, WasRemoved = wasRemoved });
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

    public static string DisplayName(string configPath)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(configPath))!;
        return DirectoryDisplayName(ExperimentDirectory(folder) ?? folder);
    }

    private static string DirectoryDisplayName(string folder) =>
        ReadName(Path.Combine(folder, IdentityFileName), "Name") ?? Path.GetFileName(folder);

    private static string? ExperimentDirectory(string batch)
    {
        for (var directory = new DirectoryInfo(Path.GetFullPath(batch)); directory != null; directory = directory.Parent)
            if (directory.Name.Equals(".runs", StringComparison.OrdinalIgnoreCase)) return directory.Parent?.FullName;
        return null;
    }

    private static JsonObject ReadIdentity(string folder)
    {
        try { return JsonNode.Parse(File.ReadAllText(Path.Combine(folder, IdentityFileName)), new JsonNodeOptions { PropertyNameCaseInsensitive = true }) as JsonObject ?? new(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }

    internal static void WriteDisplayName(string folder, string name)
    {
        var identity = ReadIdentity(folder); identity["Name"] = ValidateName(name);
        WriteConfig(Path.Combine(folder, IdentityFileName), identity);
    }

    public static void SetHidden(string sourceProject, string configPath, bool hidden)
    {
        configPath = RequireProjectConfig(sourceProject, configPath);
        var folder = Path.GetDirectoryName(configPath)!;
        var identity = ReadIdentity(folder); identity["Hidden"] = hidden;
        WriteConfig(Path.Combine(folder, IdentityFileName), identity);
    }

    public static void DeleteFromProject(string sourceProject, string configPath)
    {
        configPath = RequireProjectConfig(sourceProject, configPath);
        var folder = Path.GetDirectoryName(configPath)!;
        var identity = ReadIdentity(folder); identity["Deleted"] = true;
        WriteConfig(Path.Combine(folder, IdentityFileName), identity);
    }

    public static void Restore(string sourceProject, string configPath)
    {
        configPath = RequireProjectConfig(sourceProject, configPath);
        if (!File.Exists(configPath))
        {
            var config = ReadConfig(ExistingConfig(configPath)!);
            config["StateId"] = null;
            // Old Remove meant detach, so recover the recipe without its old binding.
            WriteConfig(configPath, config);
        }
        var folder = Path.GetDirectoryName(configPath)!;
        var identity = ReadIdentity(folder); identity["Hidden"] = false; identity["Deleted"] = false;
        WriteConfig(Path.Combine(folder, IdentityFileName), identity);
    }

    private static string? ExistingConfig(string configPath)
    {
        if (File.Exists(configPath)) return configPath;
        var folder = Path.GetDirectoryName(configPath)!;
        return !Directory.Exists(folder) ? null : Directory.EnumerateFiles(folder, ConfigFileName + ".removed-*")
            .OrderByDescending(File.GetLastWriteTimeUtc).ThenBy(path => path, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
    }

    public static void Rename(string sourceProject, string configPath, string name)
    {
        configPath = RequireProjectConfig(sourceProject, configPath);
        // Run scripts can replace their entire config. Only explicit Create/Rename
        // actions write identity, so a new batch cannot rename the experiment.
        WriteDisplayName(Path.GetDirectoryName(configPath)!, name);
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

    public static void SetBatchScheduling(string sourceProject, string configPath, int count, int workers)
    {
        if (count < 1 || workers is < 1 or > 8)
            throw new InvalidDataException("Choose at least one trial and between 1 and 8 workers.");
        configPath = RequireProjectConfig(sourceProject, configPath);
        var config = ReadConfig(configPath);
        config["Count"] = count;
        config["Parallelism"] = workers;
        WriteConfig(configPath, config);
    }

    public static IReadOnlyList<string> PreviousBatches(string configPath)
    {
        var runs = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(configPath))!, ".runs");
        return !Directory.Exists(runs) ? [] : Directory.EnumerateDirectories(runs)
            // Studio wraps batches in a run folder; CLI output may be the run folder itself.
            .SelectMany(run => new[] { Path.Combine(run, "batch"), run })
            .Where(batch => File.Exists(Path.Combine(batch, "results.sqlite")))
            .OrderByDescending(BatchLastUpdatedUtc).ThenByDescending(batch => batch, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static DateTime BatchLastUpdatedUtc(string batch)
    {
        var updated = DateTime.MinValue;
        // Directory timestamps miss writes to existing files. Active SQLite results may
        // still be in the WAL; reading its timestamp does not open or checkpoint the DB.
        foreach (var name in new[] { "results.sqlite", "results.sqlite-wal", "results.json", "results.json.gz", "experiment.json", "request.json" })
        {
            try
            {
                var file = new FileInfo(Path.Combine(batch, name));
                if (file.Exists && file.LastWriteTimeUtc > updated) updated = file.LastWriteTimeUtc;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        if (updated != DateTime.MinValue) return updated;
        try { return Directory.GetLastWriteTimeUtc(batch); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return DateTime.MinValue; }
    }

    public static string? ReadBatchName(string batch) => BatchMetadataPaths(batch)
        .Select(path => ReadName(path, "Name")).FirstOrDefault(name => name != null);

    public static string? ReadBatchExperimentName(string batch)
    {
        if (ExperimentDirectory(batch) is { } folder) return DirectoryDisplayName(folder);
        // Detached exports need a separate identity snapshot too. Neither Name nor
        // DisplayName in a run configuration is authoritative for experiment identity.
        return ReadName(Path.Combine(batch, IdentityFileName), "Name") ??
            (Path.GetFileName(Path.GetFullPath(batch)).Equals("batch", StringComparison.OrdinalIgnoreCase)
                ? ReadName(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(batch))!, IdentityFileName), "Name") : null);
    }

    private static string[] BatchMetadataPaths(string batch) =>
        [Path.Combine(batch, "experiment.json"), Path.Combine(batch, "request.json"),
            Path.Combine(Path.GetDirectoryName(Path.GetFullPath(batch))!, "request.tascsharp.json")];

    private static string? ReadName(string path, string field)
    {
        try
        {
            if (JsonNode.Parse(File.ReadAllText(path), new JsonNodeOptions { PropertyNameCaseInsensitive = true }) is JsonObject config &&
                config[field] is JsonValue value && value.TryGetValue<string>(out var name) && !string.IsNullOrWhiteSpace(name))
                return name.Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        return null;
    }

    public static void Detach(string sourceProject, string configPath, string stateId)
    {
        configPath = RequireProjectConfig(sourceProject, configPath);
        var config = ReadConfig(configPath);
        var folder = Path.GetDirectoryName(configPath)!;
        var typed = config.Deserialize<CSharpExperimentFile>(ExperimentFiles.Json)!;
        if (string.IsNullOrWhiteSpace(stateId) || typed.Start != ExperimentStart.SaveState || typed.StateId != stateId ||
            !string.Equals(Path.GetFullPath(typed.SourceProject, folder), Path.GetFullPath(sourceProject), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("This experiment is no longer attached to the selected state. Reopen the experiment menu.");
        // Keep SaveState mode: a detached recipe must be reattached before it can run,
        // rather than silently starting from boot. The recipe stays in project discovery.
        config["StateId"] = null;
        WriteConfig(configPath, config);
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
        var readable = ExistingConfig(full) ?? throw new FileNotFoundException("Experiment configuration is missing.", full);
        foreach (var path in new[] { root, folder, readable })
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Experiment management does not follow linked folders or files.");
        return full;
    }

    public static void Attach(string configPath, string sourceProject, string stateId)
    {
        configPath = RequireProjectConfig(sourceProject, configPath);
        RequireState(FolderProject.Load(sourceProject), stateId);
        var root = Path.GetDirectoryName(Path.GetFullPath(configPath))!;
        var config = ReadConfig(ExistingConfig(configPath)!);
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
        var displayName = DisplayName(configPath);
        if (!string.Equals(Path.GetFullPath(typed.SourceProject, folder), Path.GetFullPath(sourceProject), StringComparison.OrdinalIgnoreCase) ||
            typed.Start != ExperimentStart.SaveState || typed.StateId != stateId)
            throw new InvalidDataException("This experiment is no longer attached to the selected state. Reattach it before running.");
        if (!File.Exists(Path.GetFullPath(typed.Project, folder)))
            throw new FileNotFoundException("The experiment's C# project is missing.", typed.Project);
        using var timing = PerfLog.Time("experiment.prepare-run");
        var source = FolderProject.LoadForState(sourceProject, stateId);
        RequireState(source, stateId);
        var directory = Path.Combine(folder, ".runs", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8]);
        var snapshot = Path.Combine(directory, "source", "project.tasproj");
        SaveRunSnapshot(snapshot, sourceProject, source);
        // Snapshot before handing control back to Studio, including when compilation is slow.
        config["SourceProject"] = Path.GetRelativePath(directory, snapshot);
        config["Project"] = Path.GetFullPath(config["Project"]!.GetValue<string>(), folder);
        config["DisplayName"] = displayName;
        var request = Path.Combine(directory, "request.tascsharp.json");
        WriteDisplayName(directory, displayName);
        WriteConfig(request, config);
        return new(directory, request, Path.Combine(directory, "batch")) { Name = displayName };
    }

    /// <summary>
    /// A run's private copy: the movie plus whichever states <paramref name="source"/> was loaded with
    /// (see <see cref="FolderProject.LoadForState"/>). Workers read only this copy, never the author's project.
    /// </summary>
    public static void SaveRunSnapshot(string snapshot, string sourceProject, FolderProjectContent source)
    {
        var metadata = source.Archive.Metadata with { GamePath = FolderProject.ResolveRomPath(sourceProject, source.Archive.Metadata.GamePath) };
        FolderProject.Save(snapshot, metadata, source.Archive.InitialState, source.Takes, source.Sections, source.States, source.AutomaticCheckpoints);
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
