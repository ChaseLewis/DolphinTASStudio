using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using TasStudio.Emulation;
using TasStudio.Sdk;
using Xunit;

namespace TasStudio.Core.Tests;

public sealed class TypedResultTests
{
    public readonly struct Detail(int count, long seed)
    {
        public readonly int Count = count;
        public readonly long Seed = seed;
    }
    public sealed record Row(int Score, double Seconds, Detail Detail, int[] Values, string Name, int? Optional);

    [Fact]
    public async Task ScalarColumnsAndNestedJsonbAreQueryableWhileBatchRuns()
    {
        using var files = new TestWorkspace();
        await using var writer = new SerializedExperimentWriter(new SqliteExperimentWriter(typeof(Row)));
        await writer.InitializeAsync(new(files.DirectoryPath, "Test", 33), default);
        using var reader = Open(files);
        var schemaVersion = Scalar(reader, "PRAGMA schema_version");
        await Task.WhenAll(Enumerable.Range(0, 32).Select(i => Task.Run(() => writer.WriteAsync(Result(i)))));
        Assert.Equal(32L, Scalar(reader, "SELECT COUNT(*) FROM results"));
        Assert.Equal(schemaVersion, Scalar(reader, "PRAGMA schema_version"));
        Assert.Equal("blob", Scalar(reader, "SELECT typeof(Detail) FROM results LIMIT 1"));
        Assert.Equal(-123L, Scalar(reader, "SELECT json_extract(Detail, '$.Seed') FROM results LIMIT 1"));
        Assert.Equal(3L, Scalar(reader, "SELECT json_extract(\"Values\", '$[1]') FROM results LIMIT 1"));
        Assert.Equal(32L, Scalar(reader, "SELECT COUNT(*) FROM results WHERE Optional IS NULL AND Name = 'Test'"));
        Assert.Equal(1L, Scalar(reader, "SELECT \"notnull\" FROM pragma_table_info('results') WHERE name = 'Name'"));
        Assert.Equal(1L, Scalar(reader, "SELECT pk FROM pragma_table_info('results') WHERE name = 'trial_index'"));

        await writer.WriteAsync(Result(32) with { Status = "failed", Error = "No victory", Value = null });
        Assert.Equal(33L, Scalar(reader, "SELECT COUNT(*) FROM trials"));
        Assert.Equal(32L, Scalar(reader, "SELECT COUNT(*) FROM results"));
        // Retry/replacement is idempotent and failure never leaves a stale successful result.
        await writer.WriteAsync(Result(0) with { Status = "cancelled", Value = null });
        Assert.Equal(31L, Scalar(reader, "SELECT COUNT(*) FROM results"));
        await writer.WriteAsync(Result(0));
        Assert.Equal(32L, Scalar(reader, "SELECT COUNT(*) FROM results"));
    }

    [Fact]
    public async Task InvalidResultAndFailedInsertCannotLeaveHalfWrittenTrial()
    {
        using var files = new TestWorkspace();
        await using var writer = new SerializedExperimentWriter(new SqliteExperimentWriter(typeof(Row)));
        await writer.InitializeAsync(new(files.DirectoryPath, "Test", 4), default);
        using var reader = Open(files);
        await Assert.ThrowsAsync<InvalidDataException>(() => writer.WriteAsync(Result(0) with { Value = null }));
        var invalid = JsonSerializer.SerializeToElement(new { Score = 1, Seconds = 2, Detail = new { }, Values = new[] { 1 }, Name = (string?)null });
        await Assert.ThrowsAsync<InvalidDataException>(() => writer.WriteAsync(Result(0) with { Value = invalid }));
        Assert.Equal(0L, Scalar(reader, "SELECT COUNT(*) FROM trials"));
        Scalar(reader, "CREATE TRIGGER reject_score BEFORE INSERT ON results WHEN NEW.Score = 1 BEGIN SELECT RAISE(ABORT, 'test failure'); END;");
        await Assert.ThrowsAsync<SqliteException>(() => writer.WriteAsync(Result(1)));
        Assert.Equal(0L, Scalar(reader, "SELECT COUNT(*) FROM trials"));
        await writer.WriteAsync(Result(2));
        Assert.Equal(1L, Scalar(reader, "SELECT COUNT(*) FROM results"));
    }

    [Fact]
    public async Task SchemaSetupRunsOnceAndRejectsReservedColumnsBeforeLaunch()
    {
        using var files = new TestWorkspace();
        await using var writer = new SqliteExperimentWriter(typeof(Row));
        await writer.InitializeAsync(new(files.DirectoryPath, "Test", 1), default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => writer.InitializeAsync(new(files.DirectoryPath, "Test", 1), default));
        Assert.Throws<InvalidDataException>(() => new ExperimentResultSchema(typeof(Reserved)));
        Assert.Throws<InvalidDataException>(() => new ExperimentResultSchema(typeof(int)));
    }

    public sealed record Reserved([property: JsonPropertyName("TRIAL_INDEX")] int Index);

    [Fact]
    public void MemberNamesAreQuotedAndWideNumbersKeepTheirPrecision()
    {
        var schema = new ExperimentResultSchema(typeof(Wide));
        var value = JsonSerializer.SerializeToElement(new Wide(ulong.MaxValue, decimal.MaxValue), ExperimentResultSchema.CreateJsonOptions());
        Assert.Contains("\"A\"\"B\" TEXT NOT NULL", schema.CreateTableSql);
        Assert.Equal(new object[] { ulong.MaxValue.ToString(), decimal.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture) }, schema.ReadValues(value));
    }
    public sealed record Wide([property: JsonPropertyName("A\"B")] ulong Value, decimal Decimal);

    [Fact]
    public async Task GenericExperimentDispatchReturnsTypedObjectAndRejectsNull()
    {
        IExperiment valid = new Typed();
        Assert.IsType<Row>(await valid.RunAsync(null!, default));
        IExperiment invalid = new NullResult();
        await Assert.ThrowsAsync<InvalidDataException>(() => invalid.RunAsync(null!, default));
    }
    private sealed class Typed : IExperiment<Row>
    {
        public Task<Row> RunAsync(ExperimentRunContext context, CancellationToken token) => Task.FromResult(Value(1));
    }
    private sealed class NullResult : IExperiment<Row>
    {
        public Task<Row> RunAsync(ExperimentRunContext context, CancellationToken token) => Task.FromResult<Row>(null!);
    }

    private static Row Value(int index) => new(index, 1.5, new(2, -123), [2, 3], "Test", null);
    private static ExperimentResult Result(int index) => new("Test", "completed", null, DateTimeOffset.UtcNow,
        1, 1, 2, 0.5, JsonSerializer.SerializeToElement(Value(index), ExperimentResultSchema.CreateJsonOptions()), null, index);
    private static SqliteConnection Open(TestWorkspace files)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = files.FilePath("results.sqlite"), Pooling = false }.ToString());
        connection.Open(); return connection;
    }
    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand(); command.CommandText = sql; return command.ExecuteScalar();
    }
}
