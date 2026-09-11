using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace TasStudio.Emulation;

internal sealed class SqliteExperimentWriter(Type resultType, bool resume = false) : IExperimentResultWriter
{
    private readonly ExperimentResultSchema _schema = new(resultType);
    private SqliteConnection? _connection;
    private SqliteCommand? _trialCommand;
    private SqliteCommand? _resultCommand;
    private SqliteCommand? _deleteResultCommand;

    public Task InitializeAsync(ExperimentOutputContext context, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (_connection != null) throw new InvalidOperationException("Result database is already initialized.");
        Directory.CreateDirectory(context.BatchDirectory);
        _connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = Path.Combine(context.BatchDirectory, "results.sqlite"), Pooling = false, ForeignKeys = true }.ToString());
        _connection.Open();
        if (resume) ValidateSchema(); else CreateSchema();
        EnsureReceiptColumn();
        PrepareCommands();
        return Task.CompletedTask;
    }

    private void ValidateSchema()
    {
        using var query = _connection!.CreateCommand();
        query.CommandText = "PRAGMA table_info(results);";
        using var reader = query.ExecuteReader();
        var columns = new List<(string Name, string Type, bool Required)>();
        while (reader.Read()) columns.Add((reader.GetString(1), reader.GetString(2), reader.GetBoolean(3)));
        var expected = new[] { ("trial_index", "INTEGER", false) }
            .Concat(_schema.Columns.Select(c => (c.Name, c.SqlType, !c.Nullable)));
        if (!columns.SequenceEqual(expected))
            throw new InvalidDataException("Saved result schema differs from the batch's compiled experiment.");
    }

    private void EnsureReceiptColumn()
    {
        using var query = _connection!.CreateCommand();
        query.CommandText = "SELECT COUNT(*) FROM pragma_table_info('trials') WHERE name = 'result_json'";
        if ((long)query.ExecuteScalar()! != 0) return;
        query.CommandText = "ALTER TABLE trials ADD COLUMN result_json TEXT";
        query.ExecuteNonQuery();
    }

    internal Dictionary<int, ExperimentResult> ReadTrials()
    {
        using var query = _connection!.CreateCommand();
        // json() converts SQLite JSONB columns back to text for legacy databases
        // which predate the full result receipt stored in trials.result_json.
        query.CommandText = "SELECT t.trial_index,t.name,t.status,t.error,t.started_utc,t.wall_seconds,t.emulated_seconds,t.project_path,t.result_json,r.trial_index" +
            string.Concat(_schema.Columns.Select(c => c.IsJson ? $",json(r.{Quote(c.Name)})" : $",r.{Quote(c.Name)}")) +
            " FROM trials t LEFT JOIN results r ON r.trial_index=t.trial_index";
        using var reader = query.ExecuteReader();
        var results = new Dictionary<int, ExperimentResult>();
        while (reader.Read())
        {
            var index = reader.GetInt32(0);
            var status = reader.GetString(2);
            if (status == "completed" && reader.IsDBNull(9))
                throw new InvalidDataException($"Completed trial {index} is missing its typed SQLite result.");
            ExperimentResult result;
            if (!reader.IsDBNull(8))
            {
                result = JsonSerializer.Deserialize<ExperimentResult>(reader.GetString(8), ExperimentFiles.Json)
                    ?? throw new InvalidDataException($"Empty SQLite result receipt for trial {index}.");
                if (result.Index != index || result.Status != status)
                    throw new InvalidDataException($"Inconsistent SQLite result receipt for trial {index}.");
            }
            else
            {
                JsonElement? value = status == "completed" ? JsonSerializer.SerializeToElement(_schema.Columns.Select((c, i) =>
                    (c.Name, Value: c.FromStorage(reader.IsDBNull(i + 10) ? null : reader.GetValue(i + 10)))).ToDictionary(p => p.Name, p => p.Value)) : null;
                result = new(reader.GetString(1), status, reader.IsDBNull(3) ? null : reader.GetString(3),
                    DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture), reader.GetDouble(5),
                    0, 0, reader.GetDouble(6), value, reader.IsDBNull(7) ? null : reader.GetString(7), index);
            }
            results.Add(index, result);
        }
        return results;
    }

    private static string Quote(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";

    internal int[] RemoveNeverStarted()
    {
        const string predicate = "status='cancelled' AND error='Cancelled before launch.' AND wall_seconds=0";
        using var transaction = _connection!.BeginTransaction();
        using var command = _connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT trial_index FROM trials WHERE " + predicate;
        var indices = new List<int>();
        using (var reader = command.ExecuteReader()) while (reader.Read()) indices.Add(reader.GetInt32(0));
        command.CommandText = "DELETE FROM trials WHERE " + predicate;
        command.ExecuteNonQuery(); transaction.Commit();
        return indices.ToArray();
    }

    private void CreateSchema()
    {
        using var command = _connection!.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = FULL;
            CREATE TABLE trials (
                trial_index INTEGER PRIMARY KEY,
                name TEXT NOT NULL,
                status TEXT NOT NULL,
                error TEXT,
                started_utc TEXT NOT NULL,
                wall_seconds REAL NOT NULL,
                emulated_seconds REAL NOT NULL,
                project_path TEXT
            );
            """ + _schema.CreateTableSql;
        command.ExecuteNonQuery();
    }

    private void PrepareCommands()
    {
        _trialCommand = Prepare("""
            INSERT INTO trials (trial_index, name, status, error, started_utc, wall_seconds, emulated_seconds, project_path, result_json)
            VALUES ($index, $name, $status, $error, $started, $wall, $emulated, $project, $receipt)
            ON CONFLICT(trial_index) DO UPDATE SET name = excluded.name, status = excluded.status,
                error = excluded.error, started_utc = excluded.started_utc, wall_seconds = excluded.wall_seconds,
                emulated_seconds = excluded.emulated_seconds, project_path = excluded.project_path, result_json = excluded.result_json;
            """, ["$index", "$name", "$status", "$error", "$started", "$wall", "$emulated", "$project", "$receipt"]);
        _resultCommand = Prepare(_schema.InsertSql, ["$index", .. _schema.Columns.Select((_, i) => "$p" + i)]);
        _deleteResultCommand = Prepare("DELETE FROM results WHERE trial_index = $index;", ["$index"]);
    }

    private SqliteCommand Prepare(string sql, string[] parameters)
    {
        var command = _connection!.CreateCommand();
        command.CommandText = sql;
        foreach (var name in parameters) command.Parameters.AddWithValue(name, DBNull.Value);
        command.Prepare();
        return command;
    }

    public Task WriteAsync(ExperimentOutputResult result, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var connection = _connection ?? throw new InvalidOperationException("Initialize the result database first.");
        var values = result.Status == "completed" ? _schema.ReadValues(result.Value) : null;
        using var transaction = connection.BeginTransaction();
        Execute(_trialCommand!, transaction, [result.Index, result.Name, result.Status, result.Error,
            result.Started.ToString("O", CultureInfo.InvariantCulture), result.WallSeconds, result.EmulatedSeconds, result.ProjectPath,
            JsonSerializer.Serialize(new ExperimentResult(result.Name, result.Status, result.Error, result.Started, result.WallSeconds,
                result.Position, result.Frame, result.EmulatedSeconds, result.Value, result.ProjectPath, result.Index)
                { CompatibilityWarning = result.CompatibilityWarning }, ExperimentFiles.Json)]);
        if (values == null) Execute(_deleteResultCommand!, transaction, [result.Index]);
        else Execute(_resultCommand!, transaction, [result.Index, .. values]);
        transaction.Commit();
        return Task.CompletedTask;
    }

    private static void Execute(SqliteCommand command, SqliteTransaction transaction, object?[] values)
    {
        command.Transaction = transaction;
        try
        {
            for (var i = 0; i < values.Length; i++) command.Parameters[i].Value = values[i] ?? DBNull.Value;
            command.ExecuteNonQuery();
        }
        finally { command.Transaction = null; }
    }

    public ValueTask DisposeAsync()
    {
        _trialCommand?.Dispose(); _resultCommand?.Dispose(); _deleteResultCommand?.Dispose();
        _connection?.Dispose(); _connection = null;
        return ValueTask.CompletedTask;
    }
}
