using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TasStudio.Core;

public enum WatchType { U8, S8, U16, S16, U32, S32, U64, S64, Float32, Float64, Text, Bytes }
public enum WatchDisplay { Auto, Decimal, Hex, Octal, Binary }
public sealed record WatchDefinition(uint Address, WatchType Type = WatchType.U32, int Length = 1,
    int[]? Offsets = null, WatchDisplay Display = WatchDisplay.Auto)
{
    public int ByteCount => Type switch
    {
        WatchType.U8 or WatchType.S8 => 1,
        WatchType.U16 or WatchType.S16 => 2,
        WatchType.U32 or WatchType.S32 or WatchType.Float32 => 4,
        WatchType.U64 or WatchType.S64 or WatchType.Float64 => 8,
        _ => Length
    };
    public void Validate()
    {
        if (!Enum.IsDefined(Type) || !Enum.IsDefined(Display) || Length is < 1 or > 4096 || Offsets is { Length: > 16 })
            throw new InvalidDataException("Unsupported type, length or pointer depth.");
    }
}
public sealed record WatchNode(Guid Id, string Name, WatchDefinition? Watch = null, WatchNode[]? Children = null, string? Diagnostic = null)
{
    public bool IsGroup => Children is not null;
    public static WatchNode Group(string name) => new(Guid.NewGuid(), name, Children: []);
    public static WatchNode Entry(string name, WatchDefinition watch) => new(Guid.NewGuid(), name, watch);
}
public sealed record WatchImport(string FileName, string OriginalJson);
public sealed record WatchDocument(int Version, WatchNode[] Nodes, WatchImport[]? Imports = null)
{
    public static WatchDocument Empty => new(1, []);
    public const int MaximumFileBytes = 8 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, MaxDepth = 48, Converters = { new JsonStringEnumConverter() } };
    public static IEnumerable<WatchNode> Walk(IEnumerable<WatchNode> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;
            foreach (var child in Walk(node.Children ?? [])) yield return child;
        }
    }
    public void Validate()
    {
        if (Version != 1 || Nodes is null) throw new InvalidDataException("Unsupported watch file.");
        var ids = new HashSet<Guid>(); var count = 0;
        void Check(WatchNode[] nodes, int depth)
        {
            if (depth > 16) throw new InvalidDataException("Too many nested groups.");
            foreach (var node in nodes)
            {
                if (node is null || ++count > 10000 || node.Id == Guid.Empty || !ids.Add(node.Id) || string.IsNullOrWhiteSpace(node.Name) || node.Name.Length > 512)
                    throw new InvalidDataException("Invalid watch entry.");
                if (node.IsGroup) { if (node.Watch != null) throw new InvalidDataException("Group contains a value definition."); Check(node.Children!, depth + 1); }
                else if (node.Diagnostic is null) (node.Watch ?? throw new InvalidDataException("Missing watch definition.")).Validate();
            }
        }
        Check(Nodes, 0);
    }
    public string ToJson() { Validate(); return JsonSerializer.Serialize(this, Json); }
    public static WatchDocument Parse(string json)
    {
        if (Encoding.UTF8.GetByteCount(json) > MaximumFileBytes) throw new InvalidDataException("Watch file exceeds 8 MiB.");
        var document = JsonSerializer.Deserialize<WatchDocument>(json, Json) ?? throw new InvalidDataException("Empty watch file.");
        document.Validate(); return document;
    }
    public static WatchDocument Load(string path)
    {
        if (new FileInfo(path).Length > MaximumFileBytes) throw new InvalidDataException("Watch file exceeds 8 MiB.");
        return Parse(File.ReadAllText(path));
    }
    public void Save(string path)
    {
        var json = ToJson();
        if (Encoding.UTF8.GetByteCount(json) > MaximumFileBytes) throw new InvalidDataException("Watch file exceeds 8 MiB.");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, json); File.Move(temporary, path, true);
    }
    public WatchDocument Replace(Guid id, WatchNode? replacement)
    {
        WatchNode[] Change(WatchNode[] nodes) => nodes.Where(n => n.Id != id || replacement != null)
            .Select(n => n.Id == id ? replacement! : n.Children is { } children ? n with { Children = Change(children) } : n).ToArray();
        return this with { Nodes = Change(Nodes) };
    }
    public WatchDocument Add(WatchNode node, Guid? group)
    {
        if (group is null) return this with { Nodes = [.. Nodes, node] };
        var parent = Walk(Nodes).First(n => n.Id == group && n.IsGroup);
        return Replace(parent.Id, parent with { Children = [.. parent.Children!, node] });
    }
}

public static class DmwImporter
{
    public static WatchDocument Import(string json, string fileName)
    {
        if (Encoding.UTF8.GetByteCount(json) > WatchDocument.MaximumFileBytes) throw new InvalidDataException("DMW file exceeds 8 MiB.");
        using var parsed = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 48 });
        if (!parsed.RootElement.TryGetProperty("watchList", out var list) || list.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("This file has no DMW watch list.");
        var count = 0;
        WatchNode Read(JsonElement item, int depth)
        {
            if (++count > 10000 || depth > 16) throw new InvalidDataException("DMW entry/depth limit exceeded.");
            var name = "Unsupported entry";
            try
            {
                if (item.TryGetProperty("groupName", out var group))
                {
                    name = group.GetString() ?? "Group";
                    var children = item.GetProperty("groupEntries").EnumerateArray().Select(n => Read(n, depth + 1)).ToArray();
                    return new(Guid.NewGuid(), Clean(name), Children: children);
                }
                name = item.GetProperty("label").GetString() ?? "Watch";
                var address = WatchMemory.ParseAddress(item.GetProperty("address").GetString()!);
                var unsigned = item.TryGetProperty("unsigned", out var u) && u.GetBoolean();
                var type = item.GetProperty("typeIndex").GetInt32() switch
                {
                    0 => unsigned ? WatchType.U8 : WatchType.S8, 1 => unsigned ? WatchType.U16 : WatchType.S16,
                    2 => unsigned ? WatchType.U32 : WatchType.S32, 3 => WatchType.Float32, 4 => WatchType.Float64,
                    5 => WatchType.Text, 6 => WatchType.Bytes,
                    _ => throw new FormatException("Unsupported DMW type; source definition preserved.")
                };
                var display = item.TryGetProperty("baseIndex", out var b) ? b.GetInt32() switch
                { 0 => WatchDisplay.Decimal, 1 => WatchDisplay.Hex, 2 => WatchDisplay.Octal, 3 => WatchDisplay.Binary, _ => throw new FormatException("Unsupported display base.") } : WatchDisplay.Auto;
                var offsets = item.TryGetProperty("pointerOffsets", out var p) ? p.EnumerateArray().Select(o => WatchMemory.ParseOffset(o.GetString()!)).ToArray() : [];
                var length = item.TryGetProperty("length", out var l) ? l.GetInt32() : 1;
                var watch = new WatchDefinition(address, type, length, offsets, display); watch.Validate();
                return new(Guid.NewGuid(), Clean(name), watch);
            }
            catch (Exception ex) when (ex is FormatException or InvalidOperationException or KeyNotFoundException or OverflowException or ArgumentException)
            { return new(Guid.NewGuid(), Clean(name), Diagnostic: ex.Message); }
            catch (InvalidDataException ex) when (count <= 10000 && depth <= 16)
            { return new(Guid.NewGuid(), Clean(name), Diagnostic: ex.Message); }
        }
        var result = new WatchDocument(1, list.EnumerateArray().Select(n => Read(n, 0)).ToArray(), [new(Path.GetFileName(fileName), json)]);
        result.Validate(); return result;
    }
    private static string Clean(string name) => string.IsNullOrWhiteSpace(name) ? "Watch" : name[..Math.Min(name.Length, 512)];
}

public sealed record WatchHop(uint ReadAddress, uint Pointer, int Offset, uint ResolvedAddress);
public sealed record WatchValue(Guid Id, uint? Address, byte[] Bytes, WatchHop[] Hops, string? Error);
public static class WatchMemory
{
    public static uint ParseAddress(string text) => uint.Parse(text.Trim().Replace("0x", "", StringComparison.OrdinalIgnoreCase), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    public static int ParseOffset(string text)
    {
        text = text.Trim();
        if (text.StartsWith('-')) return checked((int)-(long)ParseAddress(text[1..]));
        return unchecked((int)ParseAddress(text.TrimStart('+')));
    }
    public static string OffsetText(int value) => value < 0 ? "-" + (-(long)value).ToString("X") : value.ToString("X");
    public static void ValidateRange(uint address, int count)
    {
        if (address < 0x80000000 || (ulong)address + (uint)count > 0x81800000 || count < 1)
            throw new InvalidDataException($"0x{address:X8} is outside supported GameCube MEM1.");
    }
    public static WatchValue Read(Guid id, WatchDefinition watch, Func<uint, int, byte[]> read)
    {
        var hops = new List<WatchHop>(); var address = watch.Address;
        try
        {
            watch.Validate();
            byte[] Bytes(uint at, int count)
            { ValidateRange(at, count); var bytes = read(at, count); return bytes.Length == count ? bytes : throw new InvalidDataException("Short memory read."); }
            foreach (var offset in watch.Offsets ?? [])
            {
                try
                {
                    var pointer = BinaryPrimitives.ReadUInt32BigEndian(Bytes(address, 4));
                    ValidateRange(pointer, 1);
                    var next = checked((uint)((long)pointer + offset)); ValidateRange(next, 1);
                    hops.Add(new(address, pointer, offset, next)); address = next;
                }
                catch (Exception ex) when (ex is InvalidDataException or OverflowException or InvalidOperationException)
                { throw new InvalidDataException($"Pointer level {hops.Count + 1}: {ex.Message}"); }
            }
            return new(id, address, Bytes(address, watch.ByteCount), hops.ToArray(), null);
        }
        catch (Exception ex) when (ex is InvalidDataException or OverflowException or InvalidOperationException or ArgumentException)
        { return new(id, null, [], hops.ToArray(), ex.Message); }
    }
    public static string Format(WatchDefinition watch, ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != watch.ByteCount) return "???";
        if (watch.Type == WatchType.Text)
        {
            var end = bytes.IndexOf((byte)0); if (end >= 0) bytes = bytes[..end];
            try { return new UTF8Encoding(false, true).GetString(bytes).Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t"); }
            catch (DecoderFallbackException) { return Convert.ToHexString(bytes) + " (invalid UTF-8)"; }
        }
        if (watch.Type == WatchType.Bytes)
            return string.Join(" ", bytes.ToArray().Select(b => watch.Display switch
            { WatchDisplay.Decimal => b.ToString(), WatchDisplay.Binary => Convert.ToString(b, 2).PadLeft(8, '0'), WatchDisplay.Octal => Convert.ToString(b, 8), _ => b.ToString("X2") }));
        if (watch.Type == WatchType.Float32) return BinaryPrimitives.ReadSingleBigEndian(bytes).ToString("G9", CultureInfo.InvariantCulture);
        if (watch.Type == WatchType.Float64) return BinaryPrimitives.ReadDoubleBigEndian(bytes).ToString("G17", CultureInfo.InvariantCulture);
        ulong raw = bytes.Length switch { 1 => bytes[0], 2 => BinaryPrimitives.ReadUInt16BigEndian(bytes), 4 => BinaryPrimitives.ReadUInt32BigEndian(bytes), _ => BinaryPrimitives.ReadUInt64BigEndian(bytes) };
        if (watch.Display == WatchDisplay.Hex) return raw.ToString("X" + bytes.Length * 2);
        if (watch.Display == WatchDisplay.Binary) return Convert.ToString(unchecked((long)raw), 2).PadLeft(bytes.Length * 8, '0');
        if (watch.Display == WatchDisplay.Octal) return Convert.ToString(unchecked((long)raw), 8);
        return watch.Type switch
        {
            WatchType.S8 => unchecked((sbyte)raw).ToString(CultureInfo.InvariantCulture),
            WatchType.S16 => unchecked((short)raw).ToString(CultureInfo.InvariantCulture),
            WatchType.S32 => unchecked((int)raw).ToString(CultureInfo.InvariantCulture),
            WatchType.S64 => unchecked((long)raw).ToString(CultureInfo.InvariantCulture),
            _ => raw.ToString(CultureInfo.InvariantCulture)
        };
    }
}
