using System.Text.Json;
using Microsoft.Data.Sqlite;
using TasStudio.Emulation;
using TasStudio.Experiment.Fixtures;
using Xunit;

namespace TasStudio.Core.Tests;

public sealed class ExperimentRetentionTests
{
    public sealed record LegacyRow(bool Flag, ulong Wide, decimal Precise, ResultDetails Nested, int? Optional);

    [Fact]
    public async Task LegacyDatabaseRestoresTypedResultsWithoutAnyTrialFiles()
    {
        using var files = new TestWorkspace();
        var row = new LegacyRow(true, ulong.MaxValue, decimal.MaxValue, new([7, 9], "nested"), null);
        await using (var writer = new SerializedExperimentWriter(new SqliteExperimentWriter(typeof(LegacyRow))))
        {
            await writer.InitializeAsync(new(files.DirectoryPath, "Test", 1), default);
            await writer.WriteAsync(Result(0, JsonSerializer.SerializeToElement(row)));
        }
        using (var db = Open(files.DirectoryPath)) Execute(db, "ALTER TABLE trials DROP COLUMN result_json");
        await using var reopened = new SqliteExperimentWriter(typeof(LegacyRow), resume: true);
        await reopened.InitializeAsync(new(files.DirectoryPath, "Test", 1), default);
        var stored = reopened.ReadTrials()[0];
        var restored = stored.Value!.Value.Deserialize<LegacyRow>()!;
        Assert.True(restored.Flag);
        Assert.Equal(row.Wide, restored.Wide);
        Assert.Equal(row.Precise, restored.Precise);
        Assert.Equal(row.Nested.Enemies, restored.Nested.Enemies);
        Assert.Equal(row.Nested.Name, restored.Nested.Name);
        Assert.Null(restored.Optional);
        Assert.Single(ExperimentResume.ReadFinished(files.DirectoryPath, 1, reopened.ReadTrials()));
    }

    [Fact]
    public async Task FullSqliteReceiptPreservesPositionAndResultsWhenFilesAreGone()
    {
        using var files = new TestWorkspace();
        var value = JsonSerializer.SerializeToElement(new TypedResult(17, new([1], "test")));
        var expected = Result(0, value) with { CompatibilityWarning = "Warning: mixed runtime history." };
        await using (var writer = new SerializedExperimentWriter(new SqliteExperimentWriter(typeof(TypedResult))))
        {
            await writer.InitializeAsync(new(files.DirectoryPath, "Test", 1), default);
            await writer.WriteAsync(expected);
        }
        await using var reopened = new SqliteExperimentWriter(typeof(TypedResult), true);
        await reopened.InitializeAsync(new(files.DirectoryPath, "Test", 1), default);
        var actual = reopened.ReadTrials()[0];
        Assert.Equal(expected.Position, actual.Position);
        Assert.Equal(expected.Frame, actual.Frame);
        Assert.Equal(expected.EmulatedSeconds, actual.EmulatedSeconds);
        Assert.Equal(expected.CompatibilityWarning, actual.CompatibilityWarning);
        Assert.Equal(JsonSerializer.Serialize(expected.Value), JsonSerializer.Serialize(actual.Value));
    }

    [Fact]
    public void FailedCommitRetainsArtifactsAndReportsFailure()
    {
        using var files = new TestWorkspace();
        var batch = Path.Combine(files.DirectoryPath, "batch");
        try { RunWithFailedCommit(batch); }
        finally { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
        Assert.True(File.Exists(Path.Combine(batch, "run-00001", "profile", "keep.txt")));
        Assert.True(File.Exists(Path.Combine(batch, "run-00001", "writer-error.json")));
        using var db = Open(batch);
        using var query = db.CreateCommand(); query.CommandText = "SELECT COUNT(*) FROM trials WHERE trial_index=0";
        Assert.Equal(0L, query.ExecuteScalar());
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void RunWithFailedCommit(string batch)
    {
        using var cancellation = new CancellationTokenSource();
        var definition = new ExperimentDefinition(1, "Test", ExperimentStart.Boot, null, null, 1, 30,
            [new("Trial 0", JsonSerializer.SerializeToElement(new { }))],
            AssemblyPath: typeof(TypedExperiment).Assembly.Location, TypeName: typeof(TypedExperiment).FullName);
        Assert.Throws<IOException>(() => Task.Run(() => ExperimentRunner.RunAsync(definition, "unused", batch,
            Environment.ProcessPath!, "unused", "unused", message =>
            {
                if (!message.StartsWith("Starting")) return;
                var profile = Path.Combine(batch, "run-00001", "profile"); Directory.CreateDirectory(profile);
                File.WriteAllText(Path.Combine(profile, "keep.txt"), "uncommitted evidence");
                using var db = Open(batch);
                Execute(db, "CREATE TRIGGER reject_write BEFORE INSERT ON trials BEGIN SELECT RAISE(ABORT, 'write failure'); END;");
                cancellation.Cancel();
            }, cancellation.Token)).GetAwaiter().GetResult());
    }

    private static ExperimentResult Result(int index, JsonElement value) => new("Test", "completed", null,
        DateTimeOffset.UtcNow, 2.5, 100, 200, 3.25, value, null, index);
    private static SqliteConnection Open(string batch)
    {
        var db = new SqliteConnection($"Data Source={Path.Combine(batch, "results.sqlite")};Pooling=False"); db.Open(); return db;
    }
    private static void Execute(SqliteConnection db, string sql)
    {
        using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
    }

    [Fact]
    public async Task RemovesOldCancellationFloodWithoutRemovingActualInterruptedTrials()
    {
        using var files = new TestWorkspace();
        await using (var writer = new SerializedExperimentWriter(new SqliteExperimentWriter(typeof(TypedResult))))
        {
            await writer.InitializeAsync(new(files.DirectoryPath, "Test", 3), default);
            await writer.WriteAsync(Result(0, JsonSerializer.SerializeToElement(new TypedResult(1, new([], "test")))));
            await writer.WriteAsync(Result(1, default) with { Status = "cancelled", Error = "Cancelled by user.", Value = null });
            await writer.WriteAsync(Result(2, default) with { Status = "cancelled", Error = "Cancelled before launch.", WallSeconds = 0, Value = null });
        }
        await using var reopened = new SqliteExperimentWriter(typeof(TypedResult), true);
        await reopened.InitializeAsync(new(files.DirectoryPath, "Test", 3), default);
        Assert.Equal(new[] { 2 }, reopened.RemoveNeverStarted());
        Assert.Equal(new[] { 0, 1 }, reopened.ReadTrials().Keys.Order());
    }
}
