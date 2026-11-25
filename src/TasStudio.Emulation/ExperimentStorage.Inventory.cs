using System.Diagnostics;
using System.Text.Json;

namespace TasStudio.Emulation;

public static partial class ExperimentStorage
{
    private sealed record DiskFile(string Path, long Bytes);
    private sealed class StorageLeases(IDisposable? scope, IDisposable batch) : IDisposable
    { public void Dispose() { batch.Dispose(); scope?.Dispose(); } }

    private static string? FindExperimentDirectory(string path)
    {
        for (var directory = new DirectoryInfo(Path.GetFullPath(path)); directory?.Parent != null; directory = directory.Parent)
            if (directory.Parent.Name.Equals("experiments", StringComparison.OrdinalIgnoreCase)) return directory.FullName;
        return null;
    }

    private static ProjectExperimentStorage MaintainProject(string projectPath, bool preview, IEnumerable<string>? additionalProtectedFiles,
        bool removeScreenshots, bool compressInputs, IProgress<ExperimentStorageProgress>? progress, CancellationToken token)
    {
        var project = Path.GetFullPath(projectPath); var root = Path.GetDirectoryName(project)!;
        RejectLinksTo(root);
        var protectedFiles = new HashSet<string>((additionalProtectedFiles ?? []).Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase) { project };
        var results = new List<ExperimentBatchStorage>();
        var files = new List<DiskFile>(); var batches = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var manifests = new Dictionary<string, ProjectManifest>(StringComparer.OrdinalIgnoreCase);
        var unsafeRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var scannedFiles = 0; long scannedBytes = 0, freed = 0; var processed = 0; var total = 0;
        var watch = Stopwatch.StartNew(); long reported = -100;
        void Report(string stage, string? path = null, bool force = false)
        {
            if (!force && watch.ElapsedMilliseconds - reported < 100) return;
            reported = watch.ElapsedMilliseconds;
            progress?.Report(new(stage, scannedFiles, scannedBytes, processed, total, freed, path));
        }
        void AddReferences(string path, bool all)
        {
            var directory = Path.GetDirectoryName(path)!;
            var manifest = ReadManifest(path); manifests[path] = manifest;
            string Resolve(ProjectAsset asset) => Path.GetFullPath(asset.Path, directory);
            protectedFiles.Add(path); protectedFiles.Add(Resolve(manifest.InitialState));
            if (!all) return;
            protectedFiles.Add(Resolve(manifest.Timeline));
            foreach (var state in manifest.States.Concat(manifest.AutomaticCheckpoints.Select(c => c.State))) protectedFiles.Add(Resolve(state.Data));
            var movie = JsonSerializer.Deserialize<MovieData>(CompressedJson.ReadBytes(Resolve(manifest.Timeline))) ?? throw new InvalidDataException("Invalid timeline.");
            foreach (var asset in movie.Inputs.Concat(movie.PollFrames).Concat(movie.Takes.SelectMany(t => t.Inputs))
                .Concat(movie.Takes.Where(t => t.Experiment != null).Select(t => t.Experiment!))) protectedFiles.Add(Resolve(asset));
        }
        foreach (var path in Directory.GetFiles(root, "*.tasproj"))
        { RejectLinksTo(path); if (!FolderProject.IsLegacy(path)) AddReferences(path, true); }
        var mainProtected = protectedFiles.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var batchProtected = mainProtected.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var experiments = ProjectExperiments.Root(project);
        if (!Directory.Exists(experiments)) return new([]);
        var pending = new Stack<string>(); pending.Push(experiments);
        Report("Scanning", force: true);
        while (pending.TryPop(out var directory))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                RejectLinksTo(directory);
                foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
                {
                    token.ThrowIfCancellationRequested();
                    if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                    { unsafeRoots.Add(entry.FullName); results.Add(new(entry.FullName, new(0, 0, Error: "Filesystem link retained."))); continue; }
                    if (entry is DirectoryInfo child) { pending.Push(child.FullName); continue; }
                    var file = (FileInfo)entry; files.Add(new(file.FullName, file.Length)); scannedFiles++; scannedBytes += file.Length;
                    if (file.Name is "experiment.json" or "request.json") batches.Add(directory);
                    if (file.Name == "request.tascsharp.json") batches.Add(Path.Combine(directory, "batch"));
                    Report("Scanning", directory);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { unsafeRoots.Add(directory); results.Add(new(directory, new(0, 0, Error: ex.Message))); }
        }
        Report("Inspecting projects", force: true);
        foreach (var file in files.Where(file => file.Path.EndsWith(".tasproj", StringComparison.OrdinalIgnoreCase)))
        {
            token.ThrowIfCancellationRequested();
            try { AddReferences(file.Path, false); }
            catch (Exception ex) when (CleanupFailure(ex))
            { unsafeRoots.Add(Path.GetDirectoryName(file.Path)!); }
            Report("Inspecting projects", file.Path);
        }
        // Preserve current recipe anchors even when their source is a fixture inside experiments.
        foreach (var file in files.Where(f => f.Path.EndsWith(".tascsharp.json", StringComparison.OrdinalIgnoreCase) && Path.GetFileName(f.Path) != "request.tascsharp.json"))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var config = ExperimentFiles.Read<CSharpExperimentFile>(file.Path);
                var source = Path.GetFullPath(config.SourceProject, Path.GetDirectoryName(file.Path)!);
                if (manifests.TryGetValue(source, out var manifest))
                {
                    batchProtected.Add(source);
                    batchProtected.Add(Path.GetFullPath(manifest.InitialState.Path, Path.GetDirectoryName(source)!));
                    foreach (var state in manifest.States.Concat(manifest.AutomaticCheckpoints.Select(c => c.State)).Where(s => s.Id == config.StateId))
                    {
                        protectedFiles.Add(Path.GetFullPath(state.Data.Path, Path.GetDirectoryName(source)!));
                        batchProtected.Add(Path.GetFullPath(state.Data.Path, Path.GetDirectoryName(source)!));
                    }
                }
            }
            catch (Exception ex) when (CleanupFailure(ex))
            { unsafeRoots.Add(FindExperimentDirectory(file.Path) ?? Path.GetDirectoryName(file.Path)!); results.Add(new(file.Path, new(0, 0, Error: "Recipe retained: " + ex.Message))); }
        }
        var inputFiles = 0; long inputBytes = 0;
        foreach (var experiment in Directory.GetDirectories(experiments).Order(StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            if (unsafeRoots.Contains(experiment)) continue;
            RejectLinksTo(experiment);
            using var scope = TryLock(experiment, ".experiment-storage.lock");
            if (scope == null) { results.Add(new(experiment, new(0, 0, InUse: true))); continue; }
            bool Within(string path, string parent) => path.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            bool Safe(string path) => !unsafeRoots.Any(unsafeRoot => path.Equals(unsafeRoot, StringComparison.OrdinalIgnoreCase) || Within(path, unsafeRoot));
            var experimentBatches = batches.Where(batch => Within(batch, experiment) || batch == experiment).OrderBy(p => p.Length).ToArray();
            var candidates = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            var plans = new List<ExperimentBatchStorage>();
            foreach (var batch in experimentBatches)
            {
                if (candidates.ContainsKey(Path.Combine(batch, "experiment.json")) || candidates.ContainsKey(Path.Combine(batch, "request.json"))) continue;
                var added = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                var plan = CleanBatch(batch, preview: true, protectedFiles: batchProtected,
                    inspect: (path, bytes) => { if (!candidates.ContainsKey(path)) added[path] = bytes; Report("Inspecting runs", batch); }, cancellationToken: token);
                if (plan.Error != null || plan.InUse)
                {
                    added.Clear();
                    unsafeRoots.Add(batch);
                    var run = Path.GetDirectoryName(batch)!;
                    if (Path.GetFileName(batch) == "batch" && File.Exists(Path.Combine(run, "request.tascsharp.json"))) unsafeRoots.Add(Path.Combine(run, "source"));
                }
                foreach (var pair in added) candidates[pair.Key] = pair.Value;
                plans.Add(new(batch, plan with { Files = added.Count, Bytes = added.Values.Sum() }));
            }
            if (plans.Any(p => p.Cleanup.InUse))
            { results.AddRange(plans.Select(p => p.Cleanup.InUse ? p : new(p.Directory, new(0, 0, InUse: true)))); continue; }
            var localFiles = files.Where(file => Within(file.Path, experiment) && Safe(file.Path) && !candidates.ContainsKey(file.Path)).ToArray();
            var screenshots = removeScreenshots ? localFiles.Where(file => file.Path.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase) && !protectedFiles.Contains(file.Path)).ToArray() : [];
            var states = localFiles.Where(file => GeneratedState(file.Path) && !protectedFiles.Contains(file.Path)).ToArray();
            var projects = manifests.Keys.Where(path => Within(path, experiment) && Safe(path) && !candidates.ContainsKey(path))
                .Select(Path.GetDirectoryName).Distinct(StringComparer.OrdinalIgnoreCase).Cast<string>().ToArray();
            var compressibleProjects = projects.Where(directory => !mainProtected.Any(path => Within(path, directory))).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var inputs = compressInputs ? localFiles.Where(file => Path.GetFileName(Path.GetDirectoryName(file.Path)) == "inputs" &&
                file.Path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) && ProjectInputCompression.IsGeneratedJson(file.Path) &&
                compressibleProjects.Contains(Path.GetDirectoryName(Path.GetDirectoryName(file.Path))!)).ToArray() : [];
            inputFiles += inputs.Length; inputBytes += inputs.Sum(file => file.Bytes);
            total = candidates.Count + screenshots.Length + states.Length + 2 * inputs.Length; processed = 0;
            if (preview)
            {
                results.AddRange(plans);
                if (screenshots.Length > 0) results.Add(new(Path.Combine(experiment, "BMP screenshots"), new(screenshots.Length, screenshots.Sum(f => f.Bytes))));
                if (states.Length > 0) results.Add(new(Path.Combine(experiment, "obsolete save states"), new(states.Length, states.Sum(f => f.Bytes))));
                continue;
            }
            void Removed(string path, long bytes) { processed++; freed += bytes; Report("Freeing space", path); }
            Report("Freeing space", experiment, true);
            foreach (var plan in plans)
            {
                token.ThrowIfCancellationRequested();
                if (plan.Cleanup.Error != null) { results.Add(plan); continue; }
                results.Add(new(plan.Directory, CleanBatch(plan.Directory, protectedFiles: batchProtected,
                    removed: Removed, cancellationToken: token)));
            }
            var stateSet = states.Select(file => file.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var extra = 0; long extraBytes = 0;
            var projectLeases = new List<IDisposable>();
            try
            {
                foreach (var directory in projects.Concat(states.Select(file => Path.GetDirectoryName(Path.GetDirectoryName(file.Path))!))
                    .Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
                    projectLeases.Add(ProjectAssetLease.Acquire(directory, exclusive: true));
                var current = projects.Where(Directory.Exists).SelectMany(directory => Directory.GetFiles(directory, "*.tasproj"))
                    .ToDictionary(path => path, ReadManifest, StringComparer.OrdinalIgnoreCase);
                foreach (var (path, manifest) in current) stateSet.Remove(Path.GetFullPath(manifest.InitialState.Path, Path.GetDirectoryName(path)!));
                foreach (var (path, manifest) in current)
                {
                    token.ThrowIfCancellationRequested(); RejectLinksTo(path);
                    var directory = Path.GetDirectoryName(path)!;
                    var updated = manifest with {
                        States = manifest.States.Where(s => !stateSet.Contains(Path.GetFullPath(s.Data.Path, directory))).ToArray(),
                        AutomaticCheckpoints = manifest.AutomaticCheckpoints.Where(c => !stateSet.Contains(Path.GetFullPath(c.State.Data.Path, directory))).ToArray() };
                    if (updated.States.Length != manifest.States.Length || updated.AutomaticCheckpoints.Length != manifest.AutomaticCheckpoints.Length)
                        PlayMemoryCards.WriteAtomic(path, JsonSerializer.SerializeToUtf8Bytes(updated, new JsonSerializerOptions { WriteIndented = true }));
                }
                foreach (var state in states.Where(file => stateSet.Contains(file.Path) && File.Exists(file.Path)))
                { token.ThrowIfCancellationRequested(); RejectLinksTo(state.Path); File.Delete(state.Path); extra++; extraBytes += state.Bytes; Removed(state.Path, state.Bytes); }
            }
            catch (Exception ex) when (CleanupFailure(ex))
            { results.Add(new(experiment, new(0, 0, Error: "Save states retained: " + ex.Message))); }
            finally { foreach (var lease in projectLeases) lease.Dispose(); }
            foreach (var file in screenshots.Where(file => File.Exists(file.Path)))
            {
                token.ThrowIfCancellationRequested();
                try { RejectLinksTo(file.Path); File.Delete(file.Path); extra++; extraBytes += file.Bytes; Removed(file.Path, file.Bytes); }
                catch (Exception ex) when (CleanupFailure(ex)) { results.Add(new(file.Path, new(0, 0, Error: ex.Message))); }
            }
            if (extra > 0) results.Add(new(experiment, new(extra, extraBytes)));
            if (compressInputs)
                foreach (var directory in projects.Where(Directory.Exists))
                {
                    token.ThrowIfCancellationRequested();
                    if (!inputs.Any(file => Path.GetDirectoryName(Path.GetDirectoryName(file.Path))!.Equals(directory, StringComparison.OrdinalIgnoreCase))) continue;
                    var before = freed; var start = processed;
                    try
                    {
                        var compressed = ProjectInputCompression.Compress(directory, (done, count, saved) =>
                        { processed = start + done; freed = before + saved; Report("Compressing inputs", directory); }, token);
                        freed = before + compressed.BytesSaved;
                        results.Add(new(Path.Combine(directory, "compressed inputs"), new(compressed.Files, compressed.BytesSaved)));
                    }
                    catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
                    { results.Add(new(directory, new(0, 0, Error: "Compression skipped: " + ex.Message))); }
                }
        }
        Report(preview ? "Scan complete" : "Cleanup complete", force: true);
        return new(results) { TotalFiles = scannedFiles, TotalBytes = scannedBytes, CompressibleFiles = inputFiles, CompressibleBytes = inputBytes,
            Categories = files.GroupBy(file => Category(file.Path)).Select(group => new ExperimentStorageCategory(group.Key, group.Count(), group.Sum(f => f.Bytes))).OrderByDescending(c => c.Bytes).ToArray() };
    }
    private static bool CleanupFailure(Exception ex) => ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException;
    private static ProjectManifest ReadManifest(string path)
    {
        if (FolderProject.IsLegacy(path)) throw new InvalidDataException("Legacy project assets retained.");
        var value = JsonSerializer.Deserialize<ProjectManifest>(CompressedJson.ReadBytes(path)) ?? throw new InvalidDataException("Invalid project manifest.");
        if (value.Version is not (2 or FolderProject.Version) || value.InitialState == null || value.Timeline == null || value.States == null || value.AutomaticCheckpoints == null ||
            value.States.Any(s => s?.Data == null) || value.AutomaticCheckpoints.Any(c => c?.State?.Data == null))
            throw new InvalidDataException("Unsupported project manifest.");
        return value;
    }
    private static bool GeneratedState(string path)
    {
        if (Path.GetFileName(Path.GetDirectoryName(path)) != "states" || Path.GetExtension(path) != ".tasstate") return false;
        var name = Path.GetFileNameWithoutExtension(path);
        if (name.StartsWith("initial-", StringComparison.Ordinal)) name = name[8..];
        var parts = name.Split('-');
        return parts.Length is 1 or 2 && parts.All(part => part.Length == 64 && part.All(Uri.IsHexDigit));
    }
    private static string Category(string path) => Path.GetFileName(Path.GetDirectoryName(path)) == "inputs" ?
        path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) ? "Compressed inputs" : "Input JSON" :
        Path.GetExtension(path).ToLowerInvariant() switch { ".bmp" => "BMP screenshots", ".tasstate" => "Save states", ".sqlite" => "Result databases", ".json" => "Other JSON", _ => "Other files" };
}
