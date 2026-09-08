using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace TasStudio.Emulation;

/// <summary>Built once per batch. Per-trial writes bind JSON values without reflecting over CLR types.</summary>
internal sealed class ExperimentResultSchema
{
    // Metadata must not live in a global cache: it would keep experiment load contexts alive.
    internal static JsonSerializerOptions CreateJsonOptions() => new()
    {
        IncludeFields = true,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    public IReadOnlyList<ResultColumn> Columns { get; }
    public string CreateTableSql { get; }
    public string InsertSql { get; }

    public ExperimentResultSchema(Type resultType)
    {
        if (!new ResultColumn("", resultType, false).IsJson || Nullable.GetUnderlyingType(resultType) != null ||
            typeof(System.Collections.IEnumerable).IsAssignableFrom(resultType) ||
            resultType.GetCustomAttribute<JsonConverterAttribute>() != null)
            throw new InvalidDataException("Experiment results must be a record, struct or class with named members.");
        var nullability = new NullabilityInfoContext();
        var properties = resultType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetMethod?.IsPublic == true && p.GetIndexParameters().Length == 0);
        var members = properties.Cast<MemberInfo>().Concat(resultType.GetFields(BindingFlags.Public | BindingFlags.Instance));
        Columns = members.Where(m => m.GetCustomAttribute<JsonIgnoreAttribute>()?.Condition != JsonIgnoreCondition.Always).Select(member =>
        {
            if (member.GetCustomAttribute<JsonExtensionDataAttribute>() != null)
                throw new InvalidDataException("Result extension data has no fixed columns; use a named dictionary member instead.");
            var info = member is PropertyInfo property ? nullability.Create(property) : nullability.Create((FieldInfo)member);
            var nullable = Nullable.GetUnderlyingType(info.Type) != null || (!info.Type.IsValueType && info.ReadState != NullabilityState.NotNull);
            return new ResultColumn(member.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? member.Name, info.Type, nullable);
        }).ToArray();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "trial_index" };
        foreach (var column in Columns)
            if (!names.Add(column.Name) || column.Name.Contains('\0'))
                throw new InvalidDataException($"Duplicate or reserved result column: {column.Name}");

        var fields = Columns.Select(c => $", {Quote(c.Name)} {c.SqlType}{(c.Nullable ? "" : " NOT NULL")}");
        CreateTableSql = "CREATE TABLE results (trial_index INTEGER PRIMARY KEY REFERENCES trials(trial_index)" + string.Concat(fields) + ");";
        InsertSql = "INSERT OR REPLACE INTO results (trial_index" + string.Concat(Columns.Select(c => ", " + Quote(c.Name))) +
            ") VALUES ($index" + string.Concat(Columns.Select((c, i) => c.IsJson ? $", jsonb($p{i})" : $", $p{i}")) + ");";
    }

    public object[] ReadValues(JsonElement? result)
    {
        if (result is not { ValueKind: JsonValueKind.Object } value)
            throw new InvalidDataException("A completed trial must provide its typed result object.");
        return Columns.Select(c => c.Read(value)).ToArray();
    }

    private static string Quote(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";
}

internal sealed class ResultColumn
{
    private readonly Type _type;
    public string Name { get; }
    public bool Nullable { get; }
    public string SqlType { get; }
    public bool IsJson => SqlType == "BLOB";

    public ResultColumn(string name, Type type, bool nullable)
    {
        Name = name; Nullable = nullable;
        _type = System.Nullable.GetUnderlyingType(type) ?? type;
        if (_type.IsEnum) _type = Enum.GetUnderlyingType(_type);
        SqlType = _type == typeof(bool) || _type == typeof(byte) || _type == typeof(sbyte) ||
            _type == typeof(short) || _type == typeof(ushort) || _type == typeof(int) ||
            _type == typeof(uint) || _type == typeof(long) ? "INTEGER" :
            _type == typeof(float) || _type == typeof(double) ? "REAL" :
            _type == typeof(string) || _type == typeof(char) || _type == typeof(Guid) ||
            _type == typeof(DateTime) || _type == typeof(DateTimeOffset) || _type == typeof(DateOnly) ||
            _type == typeof(TimeOnly) || _type == typeof(TimeSpan) || _type == typeof(decimal) ||
            _type == typeof(ulong) ? "TEXT" : "BLOB";
    }

    public object Read(JsonElement result)
    {
        if (!result.TryGetProperty(Name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            if (!Nullable) throw new InvalidDataException($"Result member {Name} cannot be null or missing.");
            return DBNull.Value;
        }
        if (IsJson) return value.GetRawText();
        if (_type == typeof(bool)) return value.GetBoolean() ? 1L : 0L;
        if (SqlType == "INTEGER") return value.GetInt64();
        if (SqlType == "REAL") return value.GetDouble();
        // Preserve the entire range of unsigned 64-bit integers and decimal precision.
        if (_type == typeof(ulong)) return value.GetUInt64().ToString(CultureInfo.InvariantCulture);
        if (_type == typeof(decimal)) return value.GetDecimal().ToString(CultureInfo.InvariantCulture);
        return value.GetString()!;
    }

    internal object? FromStorage(object? value)
    {
        if (value == null) return null;
        if (IsJson) return JsonSerializer.Deserialize<JsonElement>((string)value);
        if (_type == typeof(bool)) return Convert.ToInt64(value, CultureInfo.InvariantCulture) != 0;
        if (_type == typeof(ulong)) return ulong.Parse((string)value, CultureInfo.InvariantCulture);
        if (_type == typeof(decimal)) return decimal.Parse((string)value, CultureInfo.InvariantCulture);
        return value;
    }
}
