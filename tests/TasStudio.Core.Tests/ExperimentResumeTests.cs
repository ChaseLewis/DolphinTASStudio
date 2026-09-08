using System.Text.Json;
using Microsoft.Data.Sqlite;
using TasStudio.Emulation;
using TasStudio.Experiment.Fixtures;
using Xunit;

namespace TasStudio.Core.Tests;

public sealed class ExperimentResumeTests
{
    [Fact]
    public async Task ResumeKeepsOutOfOrderResultsRecoversUncommittedReceiptAndRetriesGapsWithOriginalIndices()
    {
        using var files = new TestWorkspace();
        var batch = Path.Combine(files.DirectoryPath, "batch");
        await CreateBatch(batch);
        var first = Result(0);
        var later = Result(2);
        var failure = Result(3) with { Status = "failed", Error = "No victory", Value = null };
        await using (var writer = new SerializedExperimentWriter(new SqliteExperimentWriter(typeof(TypedResult), resume: true)))
        {
            await writer.InitializeAsync(new(batch, "Test", 6), default);
            foreach (var result in new[] { first, later, failure })
            {
                ExperimentFiles.Write(Receipt(batch, result.Index), result);
                await writer.WriteAsync(result);
            }
        }
        // The worker finished trial 4, but the coordinator died before its DB commit.
        ExperimentFiles.Write(Path.Combine(batch, "run-00005", "attempts", "old", "result.json"), Result(4));
        File.WriteAllText(Path.Combine(batch, "cancel"), "cancel");
        Directory.CreateDirectory(Path.Combine(batch, "run-00002"));
        File.WriteAllText(Path.Combine(batch, "run-00002", "cancel"), "cancel");
        File.Delete(Receipt(batch, 0)); // SQLite alone must be enough to skip this trial.
        var launches = new List<string>();
        ExperimentJob? launched = null;
        using var cancel = new CancellationTokenSource();
        var results = Resume(batch, message =>
        {
            if (!message.StartsWith("Starting")) return;
            launches.Add(message);
            launched = ExperimentFiles.Read<ExperimentJob>(Path.Combine(batch, "run-00002", "job.json"));
            cancel.Cancel(); // Inspect jobs without launching an emulator.
        }, cancel.Token);
        Assert.Equal(Enumerable.Range(0, 5), results.Select(r => r.Index));
        Assert.Equal(new[] { "completed", "cancelled", "completed", "failed", "completed" }, results.Select(r => r.Status));
        Assert.Equal(JsonSerializer.Serialize(first.Value), JsonSerializer.Serialize(results[0].Value));
        Assert.Equal(first.Position, results[0].Position);
        Assert.Equal(first.Frame, results[0].Frame);
        Assert.False(Directory.Exists(Path.Combine(batch, "run-00001")));
        Assert.True(File.Exists(Receipt(batch, 3))); // Failed trial evidence stays.
        Assert.DoesNotContain(launches, s => s.Contains("Trial 0") || s.Contains("Trial 2") || s.Contains("Trial 3") || s.Contains("Trial 4"));
        Assert.Contains(launches, s => s.Contains("Trial 1"));
        var job = Assert.IsType<ExperimentJob>(launched);
        Assert.Equal(1, job.Index); Assert.Equal(6, job.Count);
        Assert.Equal(123L, job.StartUtcSeconds);
        Assert.Equal(1, job.Parameters.GetProperty("Offset").GetInt32());
        Assert.Equal(Path.Combine(batch, "source", "project.tasproj"), job.SourceProject);
        Assert.StartsWith(Path.Combine(batch, "assembly"), job.AssemblyPath);
        Assert.False(Directory.Exists(Path.Combine(batch, "run-00002")));
        Assert.False(File.Exists(Path.Combine(batch, "cancel")));
        using var db = Open(batch);
        Assert.Equal(3L, Scalar(db, "SELECT COUNT(*) FROM results"));
        Assert.Equal(4L, Scalar(db, "SELECT Score FROM results WHERE trial_index = 4"));
        Assert.Equal(5L, Scalar(db, "SELECT COUNT(*) FROM trials"));
        CollectAssemblies();
    }

    [Fact]
    public async Task ResumeFinishedBatchDoesNotLaunchWorkersOrRewriteResults()
    {
        using var files = new TestWorkspace();
        var batch = Path.Combine(files.DirectoryPath, "batch");
        await CreateBatch(batch);
        await using (var writer = new SerializedExperimentWriter(new SqliteExperimentWriter(typeof(TypedResult), true)))
        {
            await writer.InitializeAsync(new(batch, "Test", 6), default);
            for (var i = 0; i < 6; i++)
            {
                ExperimentFiles.Write(Receipt(batch, i), Result(i));
                await writer.WriteAsync(Result(i));
            }
        }
        using (var db = Open(batch))
            Scalar(db, "CREATE TRIGGER prevent_rewrite BEFORE INSERT ON trials BEGIN SELECT RAISE(ABORT, 'Do not rewrite finished trials'); END;");
        var messages = new List<string>();
        var results = Resume(batch, messages.Add, default);
        Assert.All(results, r => Assert.Equal("completed", r.Status));
        Assert.DoesNotContain(messages, m => m.StartsWith("Starting"));
        CollectAssemblies();
    }

    [Fact]
    public async Task ResumeRejectsConcurrentOwnerAndModifiedCompiledSnapshot()
    {
        using var files = new TestWorkspace();
        var batch = Path.Combine(files.DirectoryPath, "batch");
        await CreateBatch(batch);
        using (var owner = new FileStream(Path.Combine(batch, ".runner.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            await Assert.ThrowsAsync<IOException>(() => ExperimentRunner.ResumeAsync(batch, Environment.ProcessPath!, "unused", "unused", null, default));
        var manifest = Path.Combine(batch, "assembly", "assembly-manifest.json");
        var hashes = ExperimentFiles.Read<Dictionary<string, string>>(manifest);
        hashes[hashes.Keys.First()] = "changed";
        ExperimentFiles.Write(manifest, hashes);
        await Assert.ThrowsAsync<InvalidDataException>(() => ExperimentRunner.ResumeAsync(batch, Environment.ProcessPath!, "unused", "unused", null, default));
        CollectAssemblies();
    }

    [Fact]
    public async Task ResumeRejectsChangedResultSchemaWithoutOverwritingDatabase()
    {
        using var files = new TestWorkspace();
        await using (var writer = new SqliteExperimentWriter(typeof(TypedResult)))
            await writer.InitializeAsync(new(files.DirectoryPath, "Test", 1), default);
        await using var changed = new SqliteExperimentWriter(typeof(ChangedResult), true);
        await Assert.ThrowsAsync<InvalidDataException>(() => changed.InitializeAsync(new(files.DirectoryPath, "Test", 1), default));
        using var db = Open(files.DirectoryPath);
        Assert.Equal(1L, Scalar(db, "SELECT COUNT(*) FROM pragma_table_info('results') WHERE name = 'Score'"));
    }

    private sealed record ChangedResult(string Other);
    private static string Receipt(string batch, int index) => Path.Combine(batch, $"run-{index + 1:00000}", "result.json");
    private static ExperimentResult Result(int index) => new("Trial " + index, "completed", null, DateTimeOffset.UtcNow,
        1, 7, 14, .25, JsonSerializer.SerializeToElement(new TypedResult(index, new([1, 2], "test"))), null, index);
    private static SqliteConnection Open(string batch)
    {
        var db = new SqliteConnection($"Data Source={Path.Combine(batch, "results.sqlite")};Pooling=False");
        db.Open(); return db;
    }
    private static object? Scalar(SqliteConnection db, string sql)
    {
        using var command = db.CreateCommand(); command.CommandText = sql; return command.ExecuteScalar();
    }
    private static async Task CreateBatch(string batch)
    {
        var definition = new ExperimentDefinition(1, "Test", ExperimentStart.Boot, null, 123, 1, 30,
            Enumerable.Range(0, 6).Select(i => new ExperimentTrial("Trial " + i, JsonSerializer.SerializeToElement(new { Offset = i }))).ToArray(),
            AssemblyPath: typeof(TypedExperiment).Assembly.Location, TypeName: typeof(TypedExperiment).FullName);
        using var cancel = new CancellationTokenSource();
        await ExperimentRunner.RunAsync(definition, "unused", batch, Environment.ProcessPath!, "unused", "unused",
            _ => cancel.Cancel(), cancel.Token);
        Directory.CreateDirectory(Path.Combine(batch, "source"));
        File.WriteAllText(Path.Combine(batch, "source", "project.tasproj"), "test snapshot");
        CollectAssemblies();
    }
    private static void CollectAssemblies() { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }

    // Keep completed async state machines out of the test frame so collectible
    // experiment assemblies can release their Windows file handles before cleanup.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static ExperimentResult[] Resume(string batch, Action<string>? progress, CancellationToken token) =>
        Task.Run(() => ExperimentRunner.ResumeAsync(batch, Environment.ProcessPath!, "unused", "unused", progress, token)).GetAwaiter().GetResult();
}
