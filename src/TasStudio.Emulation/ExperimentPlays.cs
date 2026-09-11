using System.IO.Compression;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TasStudio.Core;

namespace TasStudio.Emulation;

public sealed record ExperimentTopPlays(string ScoreField = "Score", bool HigherIsBetter = true, int Keep = 10)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ScoreField) || Keep is < 1 or > 1000)
            throw new InvalidDataException("TopPlays requires a score field and Keep between 1 and 1000.");
    }

    internal double ReadScore(JsonElement? result)
    {
        if (result is not { ValueKind: JsonValueKind.Object } value ||
            !value.TryGetProperty(ScoreField, out var field) || field.ValueKind != JsonValueKind.Number ||
            !field.TryGetDouble(out var score) || !double.IsFinite(score))
            throw new InvalidDataException($"TopPlays score field '{ScoreField}' must contain a finite JSON number, or submit plays explicitly.");
        return score;
    }

    internal IEnumerable<ExperimentPlay> Rank(IEnumerable<ExperimentPlay> plays) =>
        (HigherIsBetter ? plays.OrderByDescending(p => p.Score) : plays.OrderBy(p => p.Score)).ThenBy(p => p.SubmissionIndex);
}

/// <summary>Compressed, immutable-on-submission timeline snapshot; no savestate/profile assets are retained.</summary>
public sealed record ExperimentPlay(int SubmissionIndex, string Name, double Score, int Start, int Length, byte[] Data);
internal sealed record ExperimentPlayAnchor(int Start, string PrefixHash, string BaselineHash);
internal sealed record ExperimentPlayData(string BaselineHash, ControllerState[] Inputs, ExecutionEvent[] Events, RecordedInputFrame[] PollFrames)
{
    internal const int MaximumGroups = 1000000;
    private const int MaximumDecodedBytes = 256 * 1024 * 1024;
    internal byte[] Encode()
    {
        using var data = new MemoryStream();
        using (var zip = new GZipStream(data, CompressionLevel.Fastest, leaveOpen: true)) JsonSerializer.Serialize(zip, this);
        return data.ToArray();
    }

    internal static ExperimentPlayData Decode(ExperimentPlay play)
    {
        if (!double.IsFinite(play.Score) || play.Start < 0 || play.Length is < 1 or > MaximumGroups ||
            (long)play.Start + play.Length > int.MaxValue || play.Data == null)
            throw new InvalidDataException("Invalid retained play.");
        using var compressed = new MemoryStream(play.Data, writable: false);
        using var zip = new GZipStream(compressed, CompressionMode.Decompress);
        using var decoded = new MemoryStream();
        var buffer = new byte[81920];
        int count;
        while ((count = zip.Read(buffer)) > 0)
        {
            if (decoded.Length + count > MaximumDecodedBytes) throw new InvalidDataException("Retained play is too large.");
            decoded.Write(buffer, 0, count);
        }
        decoded.Position = 0;
        var result = JsonSerializer.Deserialize<ExperimentPlayData>(decoded) ?? throw new InvalidDataException("Empty retained play.");
        if (string.IsNullOrWhiteSpace(result.BaselineHash) || result.Inputs?.Length != play.Length || result.Events == null || result.PollFrames == null)
            throw new InvalidDataException("Invalid retained play contents.");
        foreach (var input in result.Inputs) input.Validate();
        foreach (var entry in result.Events)
            if (entry.Position < (ulong)play.Start || entry.Position > (ulong)(play.Start + play.Length) ||
                !Enum.IsDefined(entry.Kind) || (entry.Kind == ExecutionEventKind.MemoryWrite && entry.Bytes is not { Length: > 0 }))
                throw new InvalidDataException("Invalid retained execution event.");
        var indices = new HashSet<int>();
        foreach (var frame in result.PollFrames)
        {
            if (frame.Index < play.Start || frame.Index >= play.Start + play.Length || !indices.Add(frame.Index) ||
                frame.Frame.Input != result.Inputs[frame.Index - play.Start]) throw new InvalidDataException("Invalid retained poll frame.");
            frame.Frame.Validate();
        }
        return result;
    }
}

public sealed record ExperimentPlaySummary(int TrialIndex, int SubmissionIndex, string Name, double Score, int Start, int Length)
{
    public override string ToString() => $"{Score:G8} · {Name} · Trial {TrialIndex} · groups [{Start}, {Start + Length})";
}

public static class ExperimentPlayResults
{
    private static SqliteConnection Open(string batch)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = Path.Combine(batch, "results.sqlite"), Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open(); return connection;
    }

    public static IReadOnlyList<ExperimentPlaySummary> Read(string batch)
    {
        if (!File.Exists(Path.Combine(batch, "results.sqlite"))) return [];
        using var db = Open(batch);
        using var query = db.CreateCommand();
        query.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='plays'";
        if ((long)query.ExecuteScalar()! == 0) return [];
        var options = ExperimentFiles.Read<ExperimentDefinition>(Path.Combine(batch, "experiment.json")).TopPlays;
        query.CommandText = "SELECT trial_index,submission_index,name,score,start_group,input_count FROM plays ORDER BY score " +
            (options?.HigherIsBetter == false ? "ASC" : "DESC") + ",trial_index,submission_index";
        using var reader = query.ExecuteReader();
        var results = new List<ExperimentPlaySummary>();
        while (reader.Read()) results.Add(new(reader.GetInt32(0), reader.GetInt32(1), reader.GetString(2), reader.GetDouble(3), reader.GetInt32(4), reader.GetInt32(5)));
        return results;
    }

    public static ExperimentPlay Load(string batch, int trialIndex, int submissionIndex)
    {
        using var db = Open(batch); using var query = db.CreateCommand();
        query.CommandText = "SELECT name,score,start_group,input_count,data FROM plays WHERE trial_index=$trial AND submission_index=$submission";
        query.Parameters.AddWithValue("$trial", trialIndex); query.Parameters.AddWithValue("$submission", submissionIndex);
        using var reader = query.ExecuteReader();
        if (!reader.Read()) throw new InvalidDataException("This play is no longer in the top K. Refresh the results.");
        return new(submissionIndex, reader.GetString(0), reader.GetDouble(1), reader.GetInt32(2), reader.GetInt32(3), (byte[])reader[4]);
    }
}
