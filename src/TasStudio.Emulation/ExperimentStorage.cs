using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace TasStudio.Emulation;

public sealed record ExperimentStorageCleanup(int Files, long Bytes, bool InUse = false, string? Error = null);
public sealed record ExperimentBatchStorage(string Directory, ExperimentStorageCleanup Cleanup);
public sealed record ProjectExperimentStorage(IReadOnlyList<ExperimentBatchStorage> Batches)
{
    public int Files => Batches.Sum(batch => batch.Cleanup.Files);
    public long Bytes => Batches.Sum(batch => batch.Cleanup.Bytes);
    public int InUse => Batches.Count(batch => batch.Cleanup.InUse);
    public int Blocked => Batches.Count(batch => batch.Cleanup.Error != null);
    public int TotalFiles { get; init; }
    public long TotalBytes { get; init; }
    public IReadOnlyList<ExperimentStorageCategory> Categories { get; init; } = [];
    public int CompressibleFiles { get; init; }
    public long CompressibleBytes { get; init; }
}
public sealed record ExperimentStorageCategory(string Name, int Files, long Bytes);
public sealed record ExperimentStorageProgress(string Stage, int ScannedFiles, long ScannedBytes, int ProcessedFiles, int TotalFiles, long FreedBytes, string? Path = null);

/// <summary>Batch-owned snapshots are disposable after execution; receipts and plays are durable.</summary>
public static partial class ExperimentStorage
{
    private const string CleanedFile = "storage-cleanup.json";

    public static ProjectExperimentStorage CleanProject(string projectPath, bool preview = false,
        IEnumerable<string>? additionalProtectedFiles = null, bool removeScreenshots = true, bool compressInputs = true,
        IProgress<ExperimentStorageProgress>? progress = null, CancellationToken cancellationToken = default) =>
        MaintainProject(projectPath, preview, additionalProtectedFiles, removeScreenshots, compressInputs, progress, cancellationToken);

    // Coordinators hold this from compilation through exit, and children hold it independently.
    // An orphaned worker therefore still protects its states if its coordinator crashes.
    public static IDisposable HoldBatch(string batch)
    {
        Directory.CreateDirectory(batch);
        var experiment = FindExperimentDirectory(batch);
        var scope = experiment == null ? null : new FileStream(Path.Combine(experiment, ".experiment-storage.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
        try { return new StorageLeases(scope, new FileStream(Path.Combine(batch, ".storage.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite)); }
        catch { scope?.Dispose(); throw; }
    }

    internal static void RequireSnapshots(string batch)
    {
        if (File.Exists(Path.Combine(batch, CleanedFile)))
            throw new InvalidDataException("This batch's temporary states and source snapshot were cleaned up. Its results and top plays are still available. Start a new batch to run the experiment again.");
    }

    public static ExperimentStorageCleanup CleanBatch(string batchDirectory, Action<string>? progress = null, bool preview = false,
        IReadOnlySet<string>? protectedFiles = null, Action<string, long>? inspect = null, Action<string, long>? removed = null,
        CancellationToken cancellationToken = default)
    {
        var count = 0; long bytes = 0;
        try
        {
            var batch = Path.GetFullPath(batchDirectory);
            // Only known coordinator output is eligible. Never recursively search user projects.
            var preparedSource = PreparedSource(batch);
            if (!File.Exists(Path.Combine(batch, "experiment.json")) && !File.Exists(Path.Combine(batch, "request.json")) && preparedSource == null)
                return new(0, 0);
            Directory.CreateDirectory(batch);
            RejectLinksTo(batch);
            using var storage = TryLock(batch, ".storage.lock");
            if (storage == null) return new(0, 0, InUse: true);
            using var runner = TryLock(batch, ".runner.lock");
            if (runner == null) return new(0, 0, InUse: true);
            if (File.Exists(Path.Combine(batch, "writer-errors.json")))
                throw new InvalidDataException("Results were not safely committed. Resolve writer-errors.json before cleaning this batch.");
            var directories = new List<string> { Path.Combine(batch, "source") };
            if (preparedSource != null) directories.Add(preparedSource);
            var manifests = new List<string>();
            var receipts = new List<string> { Path.Combine(batch, "results.json") };
            var pending = new Stack<string>(Directory.EnumerateDirectories(batch).Where(IsTrial));
            while (pending.TryPop(out var trial))
            {
                RejectLinksTo(trial);
                if (File.Exists(Path.Combine(trial, "writer-error.json")))
                    throw new InvalidDataException("A trial has uncommitted results. Resolve writer-error.json before cleaning this batch.");
                directories.AddRange(new[] { "profile", "states", "inputs", "timelines", "gci" }.Select(name => Path.Combine(trial, name)));
                manifests.Add(Path.Combine(trial, "result.tasproj"));
                receipts.Add(Path.Combine(trial, "result.json"));
                var attempts = Path.Combine(trial, "attempts");
                if (Directory.Exists(attempts))
                {
                    RejectLinksTo(attempts);
                    foreach (var attempt in Directory.EnumerateDirectories(attempts))
                        if (Guid.TryParseExact(Path.GetFileName(attempt), "N", out _)) pending.Push(attempt);
                }
            }
            // Validate the entire deletion set before changing receipts or removing anything.
            var files = new List<string>();
            foreach (var directory in directories.Where(Directory.Exists))
            {
                RejectLinksTo(directory);
                var folders = new Stack<string>(); folders.Push(directory);
                while (folders.TryPop(out var folder))
                    foreach (var entry in Directory.EnumerateFileSystemEntries(folder))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        RejectLink(entry);
                        if (Directory.Exists(entry)) folders.Push(entry); else files.Add(entry);
                    }
            }
            foreach (var file in manifests.Where(File.Exists)) { RejectLinksTo(file); files.Add(file); }
            if (protectedFiles != null && files.Any(protectedFiles.Contains))
                return new(0, 0, Error: "This run contains protected project or recipe references; retained.");
            if (files.Count == 0) return new(0, 0);
            if (preview)
            {
                long total = 0;
                foreach (var file in files) { cancellationToken.ThrowIfCancellationRequested(); var length = new FileInfo(file).Length; total += length; inspect?.Invoke(file, length); }
                return new(files.Count, total);
            }
            var removedProjects = manifests.ToHashSet(StringComparer.OrdinalIgnoreCase);
            ClearProjectLinks(batch, receipts, removedProjects);
            // Write before deletion: interrupted cleanup must never offer a damaged source for resume.
            ExperimentFiles.Write(Path.Combine(batch, CleanedFile), new { CleanedUtc = DateTimeOffset.UtcNow });
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                RejectLinksTo(file);
                var size = new FileInfo(file).Length;
                File.Delete(file); count++; bytes += size; removed?.Invoke(file, size);
            }
            // Remove empty generated folders only; concurrent or unexpected files are left intact.
            foreach (var directory in directories.Where(Directory.Exists)) RemoveEmptyDirectories(directory);
            progress?.Invoke($"Cleaned batch temporary storage: {count} files, {bytes / (1024.0 * 1024):F1} MiB. Results, top plays and logs retained.");
            return new(count, bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or SqliteException or ArgumentException or NotSupportedException)
        {
            progress?.Invoke("Batch storage cleanup could not finish: " + ex.Message);
            return new(count, bytes, Error: ex.Message);
        }
    }

    private static string? PreparedSource(string batch)
    {
        var run = Path.GetDirectoryName(batch);
        if (Path.GetFileName(batch) != "batch" || run == null) return null;
        var request = Path.Combine(run, "request.tascsharp.json");
        if (!File.Exists(request)) return null;
        var config = ExperimentFiles.Read<CSharpExperimentFile>(request);
        var source = Path.Combine(run, "source");
        return Path.GetFullPath(config.SourceProject, run).Equals(Path.Combine(source, "project.tasproj"), StringComparison.OrdinalIgnoreCase) ? source : null;
    }

    private static bool IsTrial(string path)
    {
        var name = Path.GetFileName(path);
        return name.StartsWith("run-", StringComparison.Ordinal) && name.Length >= 9 && name[4..].All(char.IsAsciiDigit);
    }

    private static FileStream? TryLock(string batch, string name)
    {
        var path = Path.Combine(batch, name);
        if (File.Exists(path)) RejectLink(path);
        try { return new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { return null; }
    }

    private static void RejectLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Batch cleanup refuses filesystem links: " + path);
    }
    private static void RejectLinksTo(string path)
    {
        for (var cursor = path; cursor != null; cursor = Path.GetDirectoryName(cursor)) RejectLink(cursor);
    }
    private static void RemoveEmptyDirectories(string directory)
    {
        RejectLinksTo(directory);
        foreach (var child in Directory.EnumerateDirectories(directory)) RemoveEmptyDirectories(child);
        if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
    }

    private static bool ClearLink(JsonObject receipt, HashSet<string> removedProjects)
    {
        if (receipt["ProjectPath"] is not JsonValue value || !value.TryGetValue<string>(out var path) || path == null ||
            !removedProjects.Contains(Path.GetFullPath(path))) return false;
        receipt["ProjectPath"] = null; return true;
    }

    private static void ClearProjectLinks(string batch, IEnumerable<string> receipts, HashSet<string> removedProjects)
    {
        var options = new JsonNodeOptions { PropertyNameCaseInsensitive = true };
        var rewrites = new List<(string Path, JsonNode Node)>();
        foreach (var receiptPath in receipts.Where(ExperimentFiles.Exists))
        {
            var file = ExperimentFiles.ExistingPath(receiptPath);
            RejectLinksTo(file);
            using var stream = CompressedJson.OpenRead(file);
            var node = JsonNode.Parse(stream, options);
            var changed = false;
            foreach (var receipt in node is JsonArray array ? array.OfType<JsonObject>() : node is JsonObject obj ? new[] { obj } : [])
                changed |= ClearLink(receipt, removedProjects);
            if (changed) rewrites.Add((file, node!));
        }
        var database = Path.Combine(batch, "results.sqlite");
        if (File.Exists(database))
        {
            RejectLinksTo(database);
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
            db.Open(); using var transaction = db.BeginTransaction();
            using var query = db.CreateCommand(); query.Transaction = transaction;
            query.CommandText = "SELECT COUNT(*) FROM pragma_table_info('trials') WHERE name='result_json'";
            var hasReceipt = (long)query.ExecuteScalar()! != 0;
            query.CommandText = "SELECT trial_index,project_path" + (hasReceipt ? ",result_json" : "") + " FROM trials WHERE project_path IS NOT NULL";
            var updates = new List<(int Index, string? Receipt)>();
            using (var reader = query.ExecuteReader())
                while (reader.Read())
                {
                    if (!removedProjects.Contains(Path.GetFullPath(reader.GetString(1)))) continue;
                    var receipt = hasReceipt && !reader.IsDBNull(2) ? JsonNode.Parse(reader.GetString(2), options) as JsonObject : null;
                    if (receipt != null) ClearLink(receipt, removedProjects);
                    updates.Add((reader.GetInt32(0), receipt?.ToJsonString(ExperimentFiles.Json)));
                }
            foreach (var update in updates)
            {
                query.Parameters.Clear();
                query.CommandText = "UPDATE trials SET project_path=NULL" + (hasReceipt ? ",result_json=$receipt" : "") + " WHERE trial_index=$index";
                query.Parameters.AddWithValue("$index", update.Index);
                if (hasReceipt) query.Parameters.AddWithValue("$receipt", (object?)update.Receipt ?? DBNull.Value);
                query.ExecuteNonQuery();
            }
            transaction.Commit();
        }
        foreach (var rewrite in rewrites) ExperimentFiles.Write(rewrite.Path, rewrite.Node);
    }
}
