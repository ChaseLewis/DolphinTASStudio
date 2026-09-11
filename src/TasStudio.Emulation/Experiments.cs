using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using TasStudio.Sdk;

namespace TasStudio.Emulation;

public enum ExperimentStart { Boot, SaveState }
public static class ExperimentStates
{
    /// <summary>Reserved StateId for the embedded project baseline, without requiring a named marker.</summary>
    public const string ProjectStart = "project-start";
}
public sealed record ExperimentTrial(string Name, JsonElement Parameters, long? StartUtcSeconds = null);
public sealed record ExperimentDefinition(int Version, string Name,
    ExperimentStart Start, string? StateId, long? StartUtcSeconds, int Parallelism, int TimeoutSeconds, ExperimentTrial[] Trials,
    double PrerollSeconds = 0, int PrerollGroups = 0, string? AssemblyPath = null, string? TypeName = null, bool Headless = true)
{
    public ExperimentTopPlays? TopPlays { get; init; }
    public void Validate()
    {
        TopPlays?.Validate();
        if (Version != 1 || string.IsNullOrWhiteSpace(Name) || Parallelism is < 1 or > 4 || TimeoutSeconds is < 1 or > 86400 || Trials is not { Length: > 0 })
            throw new InvalidDataException("Experiment needs a name, at least one trial, 1–4 workers and a 1–86400 second timeout.");
        if (!Enum.IsDefined(Start) || (Start == ExperimentStart.SaveState && string.IsNullOrWhiteSpace(StateId))) throw new InvalidDataException("Select a starting save state.");
        if (!double.IsFinite(PrerollSeconds) || PrerollSeconds is < 0 or > 86400 || PrerollGroups is < 0 or > 10000000 || (PrerollSeconds > 0 && PrerollGroups > 0))
            throw new InvalidDataException("Choose movie preroll in seconds or frame groups.");
        if (Start == ExperimentStart.SaveState && (StartUtcSeconds != null || Trials.Any(t => t.StartUtcSeconds != null))) throw new InvalidDataException("UTC overrides apply only to boot experiments.");
        foreach (var trial in Trials)
            if (string.IsNullOrWhiteSpace(trial.Name) || trial.Parameters.ValueKind != JsonValueKind.Object || (trial.StartUtcSeconds ?? StartUtcSeconds) is < 0 or > uint.MaxValue)
                throw new InvalidDataException("Each trial needs a name, a parameters object and a supported UTC timestamp.");
        if (AssemblyPath != null && !File.Exists(AssemblyPath)) throw new FileNotFoundException("Experiment assembly is missing.", AssemblyPath);
    }
}
public sealed record ExperimentJob(string Name, string SourceProject, string CorePath, string SystemDirectory,
    string OutputDirectory, ExperimentStart Start, string? StateId, long? StartUtcSeconds,
    JsonElement Parameters, int TimeoutSeconds, int Index, int Count,
    double PrerollSeconds = 0, int PrerollGroups = 0, string? AssemblyPath = null, string? TypeName = null, bool Headless = true,
    bool KeepSuccessfulArtifact = true)
{
    public ExperimentTopPlays? TopPlays { get; init; }
}
public sealed record ExperimentResult(string Name, string Status, string? Error, DateTimeOffset Started, double WallSeconds,
    ulong Position, ulong Frame, double EmulatedSeconds, JsonElement? Value, string? ProjectPath, int Index)
{
    public string? CompatibilityWarning { get; init; }
    public string? PlayWarning { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ExperimentPlay[]? Plays { get; init; }
}

public static class ExperimentFiles
{
    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true, Converters = { new JsonStringEnumConverter() } };
    public static void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(value, Json)); File.Move(path + ".tmp", path, true);
    }
    public static T Read<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json) ?? throw new InvalidDataException("Empty " + path);

}

public static class ExperimentWorker
{
    public static async Task<ExperimentResult> RunAsync(ExperimentJob job, ExecutionService execution, CancellationToken token, IExperiment? experiment = null)
    {
        var started = DateTimeOffset.UtcNow; var elapsed = Stopwatch.StartNew();
        var status = "completed"; string? error = null; JsonElement? value = null; string? artifact = null;
        string? compatibilityWarning = null;
        string? playWarning = null;
        var plays = new List<ExperimentPlay>();
        Directory.CreateDirectory(job.OutputDirectory);
        using var output = new StreamWriter(new FileStream(Path.Combine(job.OutputDirectory, "output.jsonl"), FileMode.Create, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
        var logGate = new object(); var logCount = 0; var loggingClosed = false;
        void Log(string kind, JsonElement data)
        {
            lock (logGate)
            {
                if (loggingClosed) throw new ObjectDisposedException("Experiment log");
                token.ThrowIfCancellationRequested();
                if (++logCount > 100000 || data.GetRawText().Length > 65536) throw new InvalidOperationException("Experiment log limit exceeded.");
                output.WriteLine(JsonSerializer.Serialize(new { kind, index = job.Index, position = execution.Position, frame = execution.VideoFieldCount, time = execution.ElapsedSeconds, data }));
            }
        }
        void ReportCompatibilityWarning()
        {
            if (execution.CompatibilityWarning is not { } warning || warning == compatibilityWarning) return;
            compatibilityWarning = warning;
            Log("warning", JsonSerializer.SerializeToElement(new { code = "runtime-compatibility", message = warning }));
        }
        ExperimentAssembly? assembly = null;
        try
        {
            token.ThrowIfCancellationRequested();
            job.TopPlays?.Validate();
            var source = FolderProject.Load(job.SourceProject);
            var metadata = source.Archive.Metadata;
            var config = metadata.Configuration ?? EmulationConfiguration.FromLegacy(metadata.InternalResolution, metadata.DspHle);
            var initialization = new ExperimentInitializationContext(job.Index, job.Count, job.Parameters,
                job.Start == ExperimentStart.Boot ? ExperimentStartingPoint.PowerOn : ExperimentStartingPoint.SaveState,
                job.StartUtcSeconds ?? config.StartUtcSeconds, token);
            if (job.AssemblyPath != null) { assembly = new(job.AssemblyPath); experiment = assembly.Create(job.TypeName); }
            if (experiment == null) throw new InvalidDataException("Select a compiled C# experiment assembly and type.");
            var boot = experiment.Initialize(initialization);
            token.ThrowIfCancellationRequested();
            var utc = boot.StartUtcSeconds ?? job.StartUtcSeconds;
            if (utc is < 0 or > uint.MaxValue) throw new InvalidDataException("Start UTC must be between 1970 and February 2106.");
            if (job.Start == ExperimentStart.SaveState && utc != null) throw new InvalidDataException("Cannot change UTC when starting from a saved state.");
            if (utc != null) config = config with { StartUtcSeconds = utc.Value };
            ExperimentFiles.Write(Path.Combine(job.OutputDirectory, "initialization.json"), new { job.Index, job.Count, job.Start, StartUtcSeconds = config.StartUtcSeconds });
            var options = new BackendOptions(job.CorePath, job.SystemDirectory, Path.Combine(job.OutputDirectory, "profile"));
            // Runtime provenance is advisory, just as when opening the project in Studio.
            // ROM/settings/integrity, native restore and recorded-poll validation still apply.
            await execution.LoadProjectAsync(job.SourceProject, options, positionOverride: 0, powerOnConfiguration: job.Start == ExperimentStart.Boot ? config : null);
            ReportCompatibilityWarning();
            var originalMovie = execution.Inputs.ToArray();
            token.ThrowIfCancellationRequested();
            if (job.Start == ExperimentStart.SaveState)
            {
                if (job.StartUtcSeconds != null) throw new InvalidDataException("Cannot change UTC when starting from a saved state.");
                var stateId = job.StateId ?? throw new InvalidDataException("Missing start state.");
                // LoadProjectAsync above has already restored the embedded baseline at group zero.
                if (stateId != ExperimentStates.ProjectStart) await execution.LoadMarkerAsync(stateId);
                await execution.ConfigureCheckpointsAsync(new(Enabled: false));
                ReportCompatibilityWarning();
            }
            token.ThrowIfCancellationRequested();
            var prerollStart = execution.ElapsedSeconds;
            var groups = 0;
            while (groups < job.PrerollGroups || execution.ElapsedSeconds - prerollStart + 0.0000001 < job.PrerollSeconds)
            {
                token.ThrowIfCancellationRequested();
                if (!await execution.StepRecordedFrameAsync()) throw new InvalidOperationException("Recorded movie ended before the requested preroll completed.");
                groups++;
            }
            Log("script-start", JsonSerializer.SerializeToElement(new { prerollGroups = groups, originalMovieGroups = originalMovie.Length }));
            if (experiment != null)
            {
                using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
                await using var emulator = new ExperimentEmulator(execution, lifetime.Token);
                var anchor = job.TopPlays == null ? null : await execution.CapturePlayAnchorAsync();
                var submitted = 0;
                async Task Submit(double score, string? name, CancellationToken cancellation)
                {
                    if (job.TopPlays == null) return;
                    await emulator.SubmitPlayAsync(async () =>
                    {
                        cancellation.ThrowIfCancellationRequested();
                        if (name is { Length: > 200 }) throw new ArgumentException("Play names must be at most 200 characters.");
                        var play = await execution.CapturePlayAsync(anchor!, submitted, name ?? job.Name, score);
                        cancellation.ThrowIfCancellationRequested();
                        submitted = checked(submitted + 1);
                        plays.Add(play);
                        var best = job.TopPlays.Rank(plays).Take(job.TopPlays.Keep).ToArray();
                        plays.Clear(); plays.AddRange(best);
                    });
                }
                var context = new ExperimentRunContext(initialization, emulator, originalMovie,
                    data => Log("value", JsonSerializer.SerializeToElement(data)), Submit);
                try
                {
                    var returned = await experiment.RunAsync(context, token);
                    token.ThrowIfCancellationRequested(); value = JsonSerializer.SerializeToElement(returned, ExperimentResultSchema.CreateJsonOptions());
                    if (job.TopPlays != null && submitted == 0)
                    {
                        try { await Submit(job.TopPlays.ReadScore(value), job.Name, token); }
                        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException)
                        {
                            // A valid score result is still useful when no replayable path was produced.
                            playWarning = ex.Message;
                            Log("play-warning", JsonSerializer.SerializeToElement(new { message = playWarning }));
                        }
                    }
                    ReportCompatibilityWarning();
                }
                finally { lifetime.Cancel(); }
            }

        }
        catch (OperationCanceledException) { status = "cancelled"; error = "Run cancelled or timed out."; }
        catch (Exception ex) { status = "failed"; error = ex.Message; }
        finally
        {
            lock (logGate) loggingClosed = true;
            assembly?.Dispose();
            if (execution.HasProject && (job.KeepSuccessfulArtifact || status != "completed"))
            {
                try { artifact = Path.Combine(job.OutputDirectory, "result.tasproj"); await execution.SaveProjectAsync(artifact); }
                catch (Exception ex) { artifact = null; status = "failed"; error = (error == null ? "" : error + "\n") + "Saving result: " + ex.Message; }
            }
        }
        var summary = new ExperimentResult(job.Name, status, error, started, elapsed.Elapsed.TotalSeconds,
            execution.Position, execution.VideoFieldCount, execution.ElapsedSeconds, value, artifact, job.Index)
        { CompatibilityWarning = compatibilityWarning, PlayWarning = playWarning, Plays = plays.Count == 0 ? null : plays.ToArray() };
        ExperimentFiles.Write(Path.Combine(job.OutputDirectory, "result.json"), summary);
        return summary;
    }
}

public static class ExperimentRunner
{
    public static async Task<int> CleanAsync(string batchDirectory, Action<string>? progress, CancellationToken token)
    {
        batchDirectory = Path.GetFullPath(batchDirectory);
        using var batchLock = new FileStream(Path.Combine(batchDirectory, ".runner.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var definition = ExperimentFiles.Read<ExperimentDefinition>(Path.Combine(batchDirectory, "experiment.json"));
        if (definition.AssemblyPath == null) throw new InvalidDataException("Cleanup requires typed SQLite results.");
        using var assembly = new ExperimentAssembly(Path.Combine(batchDirectory, "assembly", Path.GetFileName(definition.AssemblyPath)), loadInMemory: true);
        var type = assembly.GetResultType(definition.TypeName) ?? throw new InvalidDataException("Cleanup requires typed SQLite results.");
        await using var sqlite = new SqliteExperimentWriter(type, resume: true);
        await sqlite.InitializeAsync(new(batchDirectory, definition.Name, definition.Trials.Length), token);
        await using var writer = new SerializedExperimentWriter(sqlite);
        var results = sqlite.ReadTrials();
        if (results.Keys.Any(index => index < 0 || index >= definition.Trials.Length)) throw new InvalidDataException("Invalid stored trial index.");
        var count = 0;
        foreach (var index in sqlite.RemoveNeverStarted())
        {
            ExperimentArtifactCleanup.RemoveTrial(batchDirectory, index, progress);
            results.Remove(index); count++;
        }
        foreach (var (index, result) in results.ToArray())
        {
            token.ThrowIfCancellationRequested();
            if (index < 0 || index >= definition.Trials.Length) throw new InvalidDataException("Invalid stored trial index.");
            // Interrupted trials can have a newer uncommitted attempt on disk.
            // The standalone command only prunes definitively completed trials.
            if (result.Status != "completed") continue;
            var saved = result with { ProjectPath = null };
            // Backfill complete receipts in old databases before deleting anything.
            await writer.WriteAsync(saved);
            ExperimentArtifactCleanup.RemoveTrial(batchDirectory, index, progress);
            results[index] = saved; count++;
        }
        // This is only a view for the existing results browser; resume uses SQLite.
        ExperimentFiles.Write(Path.Combine(batchDirectory, "results.json"), results.OrderBy(p => p.Key).Select(p => p.Value).ToArray());
        return count;
    }

    public static async Task<ExperimentResult[]> RunAsync(ExperimentDefinition definition, string sourceProject, string batchDirectory,
        string workerExecutable, string corePath, string systemDirectory, Action<string>? progress, CancellationToken token)
        => await RunBatchAsync(definition, sourceProject, batchDirectory, workerExecutable, corePath, systemDirectory, progress, token, false);

    public static Task<ExperimentResult[]> ResumeAsync(string batchDirectory, string workerExecutable,
        string corePath, string systemDirectory, Action<string>? progress, CancellationToken token)
    {
        batchDirectory = Path.GetFullPath(batchDirectory);
        var definition = ExperimentFiles.Read<ExperimentDefinition>(Path.Combine(batchDirectory, "experiment.json"));
        if (definition.AssemblyPath == null) throw new InvalidDataException("Resume requires a typed C# experiment batch.");
        definition = definition with { AssemblyPath = Path.Combine(batchDirectory, "assembly", Path.GetFileName(definition.AssemblyPath)) };
        return RunBatchAsync(definition, Path.Combine(batchDirectory, "source", "project.tasproj"),
            batchDirectory, workerExecutable, corePath, systemDirectory, progress, token, true);
    }

    private static async Task<ExperimentResult[]> RunBatchAsync(ExperimentDefinition definition, string sourceProject, string batchDirectory,
        string workerExecutable, string corePath, string systemDirectory, Action<string>? progress, CancellationToken token, bool resume)
    {
        definition.Validate();
        Directory.CreateDirectory(batchDirectory);
        // An OS file lock also prevents two coordinators targeting this same batch directory.
        using var batchLock = new FileStream(Path.Combine(batchDirectory, ".runner.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (!File.Exists(workerExecutable)) throw new FileNotFoundException("Experiment worker is missing. Rebuild TAS Studio.", workerExecutable);
        ExperimentResume.CheckRuntime(batchDirectory, workerExecutable, corePath, resume, progress);
        if (resume)
        {
            ExperimentResume.ValidateSnapshot(batchDirectory, sourceProject);
            // A cancelled batch's old signal must not cancel the resumed coordinator.
            File.Delete(Path.Combine(batchDirectory, "cancel"));
        }
        var experimentAssembly = definition.AssemblyPath == null ? null : resume ? definition.AssemblyPath : SnapshotAssembly(definition.AssemblyPath, Path.Combine(batchDirectory, "assembly"));
        using var writerAssembly = experimentAssembly == null ? null : new ExperimentAssembly(experimentAssembly, loadInMemory: true);
        var resultType = writerAssembly?.GetResultType(definition.TypeName);
        if (resume && resultType == null) throw new InvalidDataException("Resume requires typed SQLite results.");
        if (definition.TopPlays != null && resultType == null) throw new InvalidDataException("TopPlays requires a typed IExperiment<TResult> result.");
        var sqlite = resultType == null ? null : new SqliteExperimentWriter(resultType, resume, definition.TopPlays);
        await using var writer = sqlite == null ? null : new SerializedExperimentWriter(sqlite);
        if (writer != null) await writer.InitializeAsync(new(Path.GetFullPath(batchDirectory), definition.Name, definition.Trials.Length), token);
        var writerErrors = new System.Collections.Concurrent.ConcurrentQueue<string>();
        async Task<ExperimentResult> PersistResultAsync(ExperimentResult result)
        {
            if (writer == null) return result;
            var retain = ExperimentArtifactCleanup.RetainArtifacts(result.Status);
            var saved = retain ? result : result with { ProjectPath = null };
            try
            {
                await writer.WriteAsync(saved);
                if (!retain) ExperimentArtifactCleanup.RemoveTrial(batchDirectory, result.Index, progress);
                return saved with { Plays = null };
            }
            catch (Exception ex)
            {
                var error = $"Trial {result.Index}: {ex.Message}";
                writerErrors.Enqueue(error);
                ExperimentFiles.Write(Path.Combine(batchDirectory, $"run-{result.Index + 1:00000}", "writer-error.json"), error);
                progress?.Invoke("Saving result failed: " + error);
                return result;
            }
        }
        if (!resume)
        {
            ExperimentFiles.Write(Path.Combine(batchDirectory, "experiment.json"), definition);
        }
        var results = new ExperimentResult[definition.Trials.Length];
        if (resume)
        {
            var stored = sqlite!.ReadTrials();
            var recovered = ExperimentResume.ReadFinished(batchDirectory, definition.Trials.Length, stored);
            foreach (var (index, result) in recovered)
            {
                results[index] = result;
                if (stored.GetValueOrDefault(index)?.Status != result.Status ||
                    (!ExperimentArtifactCleanup.RetainArtifacts(result.Status) && result.ProjectPath != null))
                    results[index] = await PersistResultAsync(result);
                else if (!ExperimentArtifactCleanup.RetainArtifacts(result.Status))
                    ExperimentArtifactCleanup.RemoveTrial(batchDirectory, index, progress);
            }
            progress?.Invoke($"Resuming batch: {recovered.Count}/{results.Length} trials already finished; {results.Length - recovered.Count} remaining.");
        }
        await ExperimentScheduler.RunAsync(definition.Trials.Length, definition.Parallelism, async index =>
        {
            if (results[index] != null || token.IsCancellationRequested) return;
            var trial = definition.Trials[index];
            var trialDirectory = Path.Combine(batchDirectory, $"run-{index + 1:00000}");
            // New attempts cannot consume stale result/cancel files or overwrite prior evidence.
            var directory = resume ? Path.Combine(trialDirectory, "attempts", Guid.NewGuid().ToString("N")) : trialDirectory;
            var job = new ExperimentJob(trial.Name, sourceProject, corePath, systemDirectory, directory, definition.Start,
                definition.StateId, trial.StartUtcSeconds ?? definition.StartUtcSeconds, trial.Parameters, definition.TimeoutSeconds, index, definition.Trials.Length,
                definition.PrerollSeconds, definition.PrerollGroups, experimentAssembly, definition.TypeName, definition.Headless, KeepSuccessfulArtifact: writer == null)
                { TopPlays = definition.TopPlays };
            ExperimentFiles.Write(Path.Combine(directory, "job.json"), job);
            if (resume) ExperimentFiles.Write(Path.Combine(trialDirectory, "job.json"), job);
            progress?.Invoke($"Starting {index + 1}/{definition.Trials.Length}: {trial.Name}");
            results[index] = await RunProcess(job, workerExecutable, token);
            if (resume) ExperimentFiles.Write(Path.Combine(trialDirectory, "result.json"), results[index]);
            results[index] = await PersistResultAsync(results[index]);
            if (results[index].CompatibilityWarning is { } warning) progress?.Invoke($"{trial.Name}: {warning}");
            if (results[index].PlayWarning is { } playWarning) progress?.Invoke($"{trial.Name}: play not retained — {playWarning}");
            progress?.Invoke($"{trial.Name}: {results[index].Status}" + (results[index].Error == null ? "" : " — " + results[index].Error));
        }, token);
        var recorded = results.Where(result => result != null).ToArray();
        ExperimentFiles.Write(Path.Combine(batchDirectory, "results.json"), recorded);
        if (!writerErrors.IsEmpty)
        {
            ExperimentFiles.Write(Path.Combine(batchDirectory, "writer-errors.json"), writerErrors.ToArray());
            throw new IOException("Some results could not be written to SQLite. Original results are preserved in results.json; see writer-errors.json.");
        }
        return recorded;
    }

    private static string SnapshotAssembly(string assemblyPath, string destination)
    {
        var source = Path.GetDirectoryName(Path.GetFullPath(assemblyPath))!;
        destination = Path.GetFullPath(destination);
        if (destination.Equals(source, StringComparison.OrdinalIgnoreCase) || destination.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Batch output must be outside the experiment build folder.");
        var pending = new Stack<string>(); pending.Push(source); var hashes = new Dictionary<string, string>();
        while (pending.TryPop(out var folder))
        {
            if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0) throw new IOException("Experiment output cannot contain directory links.");
            foreach (var child in Directory.EnumerateDirectories(folder)) pending.Push(child);
            foreach (var file in Directory.EnumerateFiles(folder))
            {
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new IOException("Experiment output cannot contain file links.");
                var relative = Path.GetRelativePath(source, file); var target = Path.Combine(destination, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(file, target, overwrite: false);
                hashes.Add(relative, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(target))));
            }
        }
        ExperimentFiles.Write(Path.Combine(destination, "assembly-manifest.json"), hashes);
        return Path.Combine(destination, Path.GetFileName(assemblyPath));
    }

    private static async Task<ExperimentResult> RunProcess(ExperimentJob job, string executable, CancellationToken token)
    {
        var started = DateTimeOffset.UtcNow; var timer = Stopwatch.StartNew(); var resultFile = Path.Combine(job.OutputDirectory, "result.json");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(job.TimeoutSeconds));
        using var process = new Process { StartInfo = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = job.OutputDirectory,
            RedirectStandardOutput = true, RedirectStandardError = true } };
        using var stdout = new FileStream(Path.Combine(job.OutputDirectory, "console.log"), FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
        using var stderr = new FileStream(Path.Combine(job.OutputDirectory, "error.log"), FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
        Task outputCopy = Task.CompletedTask, errorCopy = Task.CompletedTask;
        process.StartInfo.ArgumentList.Add(Path.Combine(job.OutputDirectory, "job.json"));
        string? failure = null; var status = "failed";
        try
        {
            token.ThrowIfCancellationRequested();
            if (!process.Start()) throw new IOException("Could not start experiment worker.");
            outputCopy = process.StandardOutput.BaseStream.CopyToAsync(stdout);
            errorCopy = process.StandardError.BaseStream.CopyToAsync(stderr);
            await process.WaitForExitAsync(deadline.Token);
            if (File.Exists(resultFile)) return ExperimentFiles.Read<ExperimentResult>(resultFile);
            failure = $"Worker exited with code {process.ExitCode} without a result.";
        }
        catch (OperationCanceledException)
        {
            status = token.IsCancellationRequested ? "cancelled" : "timed out";
            failure = status == "timed out" ? "Trial exceeded its time limit." : "Cancelled by user.";
            File.WriteAllText(Path.Combine(job.OutputDirectory, "cancel"), "cancel");
        }
        catch (Exception ex) { failure = ex.Message; }
        finally
        {
            // Only this invocation's owned child process is eligible for termination.
            if (process.IdOrNull() != null && !process.HasExited)
            {
                using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try { await process.WaitForExitAsync(grace.Token); }
                catch (OperationCanceledException) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
            }
            await Task.WhenAll(outputCopy, errorCopy);
        }
        var prior = File.Exists(resultFile) ? ExperimentFiles.Read<ExperimentResult>(resultFile) : null;
        var result = prior is null ? new ExperimentResult(job.Name, status, failure, started, timer.Elapsed.TotalSeconds, 0, 0, 0, null, null, job.Index) : prior with { Status = status, Error = failure };
        ExperimentFiles.Write(resultFile, result); return result;
    }
    private static int? IdOrNull(this Process process) { try { return process.Id; } catch (InvalidOperationException) { return null; } }
}
