using System.Security.Cryptography;
using System.Text;
using TasStudio.Core;

namespace TasStudio.Emulation;

/// <summary>Owner-thread hash chain. Cached entries are before boundary events; At includes them.</summary>
public sealed class ExecutionHistory
{
    private readonly List<byte[]> _prefixes;
    private readonly IReadOnlyList<ControllerState> _inputs;
    private readonly IReadOnlyList<ExecutionEvent> _events;
    private readonly IReadOnlyDictionary<int, RecordedInputFrame>? _pollFrames;
    public ExecutionHistory(string rootIdentity, IReadOnlyList<ControllerState> inputs, IReadOnlyList<ExecutionEvent> events,
        IReadOnlyDictionary<int, RecordedInputFrame>? pollFrames = null)
    {
        _prefixes = [SHA256.HashData(Encoding.UTF8.GetBytes("tas-history/v1/root\0" + rootIdentity))];
        _inputs = inputs; _events = events;
        _pollFrames = pollFrames;
    }
    public void Invalidate(int firstChangedInput)
    {
        var retained = Math.Max(1, firstChangedInput + 1);
        if (_prefixes.Count > retained) _prefixes.RemoveRange(retained, _prefixes.Count - retained);
    }
    public string At(ulong position)
    {
        BeforeEventsAt(position);
        return Convert.ToHexString(AfterEvents((int)position));
    }
    internal string BeforeEventsAt(ulong position)
    {
        if (position > (ulong)_inputs.Count) throw new ArgumentOutOfRangeException(nameof(position));
        while (_prefixes.Count <= (int)position)
        {
            var index = _prefixes.Count - 1;
            _prefixes.Add(Hash(writer =>
            {
                writer.Write("tas-history/v1/input"); writer.Write(AfterEvents(index)); writer.Write(index);
                var p = _inputs[index]; writer.Write((ushort)p.Buttons);
                writer.Write(p.StickX); writer.Write(p.StickY); writer.Write(p.CStickX); writer.Write(p.CStickY);
                writer.Write(p.TriggerL); writer.Write(p.TriggerR);
                if (_pollFrames?.TryGetValue(index, out var recorded) == true)
                {
                    writer.Write("poll-frame/v1"); writer.Write(recorded.Frame.Fields); writer.Write(recorded.Frame.Ticks);
                    writer.Write(recorded.Frame.Polls.Length);
                    foreach (var poll in recorded.Frame.Polls)
                    {
                        writer.Write(poll.TickOffset); writer.Write(poll.FieldOffset); writer.Write(poll.Port);
                        var pad = poll.Input; writer.Write((ushort)pad.Buttons);
                        writer.Write(pad.StickX); writer.Write(pad.StickY); writer.Write(pad.CStickX); writer.Write(pad.CStickY);
                        writer.Write(pad.TriggerL); writer.Write(pad.TriggerR);
                    }
                }
            }));
        }
        return Convert.ToHexString(_prefixes[(int)position]);
    }
    private byte[] AfterEvents(int index) => Hash(writer =>
    {
        writer.Write("tas-history/v1/boundary"); writer.Write(_prefixes[index]); writer.Write(index);
        var events = _events.Where(e => e.Position == (ulong)index).ToArray(); writer.Write(events.Length);
        foreach (var e in events)
        {
            writer.Write((int)e.Kind); writer.Write(e.Address); writer.Write(e.Bytes?.Length ?? 0);
            if (e.Bytes != null) writer.Write(e.Bytes);
        }
    });
    private static byte[] Hash(Action<BinaryWriter> encode)
    {
        using var stream = new MemoryStream(); using (var writer = new BinaryWriter(stream, Encoding.UTF8, true)) encode(writer);
        return SHA256.HashData(stream.GetBuffer().AsSpan(0, (int)stream.Length));
    }
}

public sealed record InputTake(string Id, string Name, int Start, ControllerState[] Inputs, string BaselineHash, string Provenance)
{
    public string? EventsHash { get; init; }
}
public sealed record TimelineSection(int Start, int Length, string Name);
public sealed record SavedStateReference(string Id, string Name, ulong Position, string HistoryHash, string Path)
{
    internal bool OwnsFile { get; init; }
}
public sealed record CheckpointReference(SavedStateReference State, double Seconds, long Created, long LastUse);
public sealed record StateMarker(string Id, string Name, ulong Position, bool Automatic, bool Valid);
