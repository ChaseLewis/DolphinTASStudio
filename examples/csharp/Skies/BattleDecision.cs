using System.Buffers.Binary;

namespace Skies;

/// <summary>Recovered command IDs. Other values remain available as RawCommand.</summary>
public enum BattleCommand
{
    None = -1, Magic = 1, Attack = 3, Guard = 4, Item = 5, Run = 6, SuperMove = 12
}

/// <summary>A copy of one actor's intention, not a guarantee of its eventual action.
/// Target and attack variant may change during preparation. Slots 0–3 are party, 4–11 enemies.</summary>
public sealed class BattleDecision
{
    public const int RecordSize = 32;
    public int ActorSlot { get; }
    public bool IsEnemy => ActorSlot >= 4;
    public int RawCommand { get; }
    public BattleCommand? Command => Enum.IsDefined(typeof(BattleCommand), RawCommand) ? (BattleCommand)RawCommand : null;
    public byte RawTargetSlot { get; }
    public int? TargetSlot => RawTargetSlot < 12 ? RawTargetSlot : null;
    public sbyte TargetRule { get; }
    public short AbilityOrVariant { get; }
    public int? AbilityId => Command is BattleCommand.Magic or BattleCommand.SuperMove ? AbilityOrVariant : null;
    public int? AttackVariant => Command == BattleCommand.Attack ? AbilityOrVariant : null;
    public IReadOnlyList<byte> RawBytes { get; }

    internal BattleDecision(int slot, ReadOnlySpan<byte> bytes)
    {
        ActorSlot = slot;
        RawCommand = BinaryPrimitives.ReadInt32BigEndian(bytes);
        RawTargetSlot = bytes[4];
        TargetRule = unchecked((sbyte)bytes[5]);
        AbilityOrVariant = BinaryPrimitives.ReadInt16BigEndian(bytes[6..]);
        RawBytes = Array.AsReadOnly(bytes.ToArray());
    }
}

/// <summary>Raw queue plus its decoded prefix. 0xFF terminates it; malformed entries
/// are preserved and flagged, never silently converted to actor slots.</summary>
public sealed class BattleActionOrder
{
    public IReadOnlyList<byte> RawBytes { get; }
    public IReadOnlyList<int> ActorSlots { get; }
    public bool IsWellFormed { get; }

    internal BattleActionOrder(ReadOnlySpan<byte> bytes)
    {
        RawBytes = Array.AsReadOnly(bytes.ToArray());
        var slots = new List<int>();
        var valid = true;
        foreach (var value in bytes)
        {
            if (value == 0xff) break;
            if (value >= 12 || slots.Contains(value)) { valid = false; break; }
            slots.Add(value);
        }
        ActorSlots = slots.AsReadOnly();
        IsWellFormed = valid;
    }
}

/// <summary>Read while emulation is paused. Phase checks detect some races, not all.
/// Read on the first transition into CameraTransition (state 3) for the initial round plan.
/// Later reads may contain revised decisions; unused slots can contain stale data.</summary>
public sealed record BattlePlanSnapshot(
    BattleState PhaseBefore, BattleState PhaseAfter,
    IReadOnlyList<int> PresentActorSlots,
    IReadOnlyList<BattleDecision> Decisions, BattleActionOrder Order)
{
    public bool PhaseChangedDuringRead => PhaseBefore != PhaseAfter;
    public IEnumerable<BattleDecision> EnemyDecisions =>
        Decisions.Where(d => d.IsEnemy && PresentActorSlots.Contains(d.ActorSlot));
}
