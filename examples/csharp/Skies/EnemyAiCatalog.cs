using System.Globalization;
using Microsoft.VisualBasic.FileIO;

namespace Skies;

/// <summary>Exported instruction labels, not an AI interpreter or exact probability model.</summary>
public sealed record EnemyAiInstruction(int EntryId, int EnemyId, string Filter, string EnemyName,
    int TypeId, int TaskId, string TaskName, int ParameterId, string ParameterName);

/// <summary>Reads ALX/SOARandomizer enemytask.csv supplied by the caller; no bundled game data.
/// Unknown/conflicting names fall back to numeric IDs. Does not predict a boss's actions.</summary>
public sealed class EnemyAiCatalog
{
    public IReadOnlyList<EnemyAiInstruction> Instructions { get; }
    private EnemyAiCatalog(List<EnemyAiInstruction> rows) => Instructions = rows.AsReadOnly();

    public static EnemyAiCatalog Load(string path)
    {
        using var reader = File.OpenText(path);
        return Read(reader);
    }

    /// <summary>Consumes and disposes the supplied reader.</summary>
    public static EnemyAiCatalog Read(TextReader reader)
    {
        using var parser = new TextFieldParser(reader) { HasFieldsEnclosedInQuotes = true, TrimWhiteSpace = false };
        parser.SetDelimiters(",");
        var headers = parser.ReadFields() ?? throw new InvalidDataException("Missing AI CSV header.");
        string[] required = ["Entry ID", "[EC ID]", "[Filter]", "[EC US Name]", "Type ID", "Task ID", "[Task Name]", "Param ID", "[Param Name]"];
        var columns = required.Select(name => Array.IndexOf(headers, name)).ToArray();
        if (columns.Any(i => i < 0)) throw new InvalidDataException("Missing required enemytask.csv columns.");
        var rows = new List<EnemyAiInstruction>();
        while (!parser.EndOfData)
        {
            var fields = parser.ReadFields()!;
            if (fields.Length != headers.Length) throw new InvalidDataException("AI CSV row has the wrong field count.");
            string Value(int column) => fields[columns[column]];
            int Number(int column) => int.TryParse(Value(column), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
                ? n : throw new InvalidDataException($"Invalid integer in {required[column]}.");
            rows.Add(new(Number(0), Number(1), Value(2), Value(3), Number(4), Number(5), Value(6), Number(7), Value(8)));
        }
        return new(rows);
    }

    /// <summary>Exact filter match. Encounter-specific variants are never silently merged with '*'.</summary>
    public IEnumerable<EnemyAiInstruction> GetScript(int enemyId, string filter = "*") =>
        Instructions.Where(i => i.EnemyId == enemyId && i.Filter == filter).OrderBy(i => i.EntryId);

    public string Describe(BattleDecision decision)
    {
        var command = decision.Command;
        if (command is not (BattleCommand.Magic or BattleCommand.SuperMove))
            return command?.ToString() ?? $"Command {decision.RawCommand}";
        var id = decision.AbilityOrVariant;
        // AI task 500..549 is magic; 0..499 is an enemy super move.
        if (id >= 0 && id < (command == BattleCommand.Magic ? 50 : 500))
        {
            var task = command == BattleCommand.Magic ? 500 + id : id;
            var names = Instructions.Where(i => i.TypeId == 1 && i.TaskId == task)
                .Select(i => i.TaskName).Where(n => !string.IsNullOrWhiteSpace(n) && n != "???")
                .Distinct(StringComparer.Ordinal).Take(2).ToArray();
            if (names.Length == 1) return names[0];
        }
        return $"{command} {id}";
    }
}
