using Microsoft.Data.Sqlite;

namespace TasStudio.Emulation;

/// <summary>Commits each explicit play before SubmitPlayAsync returns, independently of trial completion.</summary>
internal sealed class ExperimentLivePlays : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ExperimentJob _job;
    private readonly DateTimeOffset _started;
    internal int NextSubmissionIndex { get; }

    internal ExperimentLivePlays(ExperimentJob job, DateTimeOffset started)
    {
        _job = job; _started = started;
        _connection = new(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(job.BatchDirectory!, "results.sqlite"), Mode = SqliteOpenMode.ReadWrite,
            Pooling = false, ForeignKeys = true, DefaultTimeout = 60
        }.ToString());
        try
        {
            _connection.Open();
            using var command = _connection.CreateCommand();
            command.CommandText = """
                PRAGMA synchronous=FULL;
                CREATE TABLE IF NOT EXISTS submitted_plays (
                    trial_index INTEGER NOT NULL REFERENCES trials(trial_index), submission_index INTEGER NOT NULL,
                    name TEXT NOT NULL, score REAL NOT NULL, start_group INTEGER NOT NULL, input_count INTEGER NOT NULL,
                    data BLOB NOT NULL, submitted_utc TEXT NOT NULL,
                    PRIMARY KEY(trial_index,submission_index)
                );
                """;
            command.ExecuteNonQuery();
            command.CommandText = """
                SELECT COALESCE(MAX(submission_index),-1)+1 FROM (
                    SELECT submission_index FROM submitted_plays WHERE trial_index=$trial
                    UNION ALL SELECT submission_index FROM plays WHERE trial_index=$trial)
                """;
            command.Parameters.AddWithValue("$trial", job.Index);
            NextSubmissionIndex = checked(Convert.ToInt32(command.ExecuteScalar()));
        }
        catch { _connection.Dispose(); throw; }
    }

    internal void Save(ExperimentPlay play)
    {
        if (!double.IsFinite(play.Score) || play.SubmissionIndex < 0)
            throw new InvalidDataException("Invalid submitted play score or index.");
        using var transaction = _connection.BeginTransaction();
        using var command = _connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO trials(trial_index,name,status,started_utc,wall_seconds,emulated_seconds)
            VALUES($trial,$trialname,'running',$started,0,0) ON CONFLICT(trial_index) DO NOTHING;
            INSERT INTO submitted_plays(trial_index,submission_index,name,score,start_group,input_count,data,submitted_utc)
            VALUES($trial,$submission,$name,$score,$start,$length,$data,$now);
            INSERT INTO plays(trial_index,submission_index,name,score,start_group,input_count,data)
            VALUES($trial,$submission,$name,$score,$start,$length,$data);
            """;
        command.Parameters.AddWithValue("$trial", _job.Index);
        command.Parameters.AddWithValue("$trialname", _job.Name);
        command.Parameters.AddWithValue("$started", _started.ToString("O"));
        command.Parameters.AddWithValue("$submission", play.SubmissionIndex);
        command.Parameters.AddWithValue("$name", play.Name);
        command.Parameters.AddWithValue("$score", play.Score);
        command.Parameters.AddWithValue("$start", play.Start);
        command.Parameters.AddWithValue("$length", play.Length);
        command.Parameters.AddWithValue("$data", play.Data);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
        command.Parameters.Clear();
        command.CommandText = "DELETE FROM plays WHERE (trial_index,submission_index) NOT IN " +
            "(SELECT trial_index,submission_index FROM plays ORDER BY score " +
            (_job.TopPlays!.HigherIsBetter ? "DESC" : "ASC") + ",trial_index,submission_index LIMIT $keep)";
        command.Parameters.AddWithValue("$keep", _job.TopPlays.Keep);
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    public void Dispose() => _connection.Dispose();
}
