namespace Skies;

/// <summary>Named active ailments in GEAE8P-r0 battle stats +0x1C.
/// This is a partial mapping: other bits include internal battle state and buffs.</summary>
[Flags]
public enum BattleStatus : uint
{
    None = 0,
    Poison = 0x00000080,
    Silence = 0x00000200,
    Sleep = 0x00000400,
    Confusion = 0x00000800,
    Fatigue = 0x00001000,
    Stone = 0x00004000,
    Weak = 0x00080000
}

/// <summary>A frozen status read. Active effects are separate from persistent flags
/// and from queued effect IDs. None means no mapped ailment, not an all-clear for every buff/debuff.</summary>
public sealed class BattleStatusSnapshot
{
    public const BattleStatus KnownMask = BattleStatus.Poison | BattleStatus.Silence | BattleStatus.Sleep |
        BattleStatus.Confusion | BattleStatus.Fatigue | BattleStatus.Stone | BattleStatus.Weak;

    public int ActorSlot { get; }
    public bool IsEnemy => ActorSlot >= SkiesGame.MaximumPartyMembers;
    public uint StatsAddress { get; }
    public uint RawActiveFlags { get; }
    public uint RawPersistentFlags { get; }
    public BattleStatus Effects => (BattleStatus)(RawActiveFlags & (uint)KnownMask);
    public uint UnmappedActiveFlags => RawActiveFlags & ~(uint)KnownMask;

    /// <summary>True if every requested bit is active; None returns true.</summary>
    public bool HasAll(BattleStatus effects) => (RawActiveFlags & (uint)effects) == (uint)effects;
    /// <summary>True if any requested bit is active; None returns false.</summary>
    public bool HasAny(BattleStatus effects) => (RawActiveFlags & (uint)effects) != 0;

    internal BattleStatusSnapshot(int actorSlot, uint statsAddress, uint active, uint persistent)
    {
        ActorSlot = actorSlot;
        StatsAddress = statsAddress;
        RawActiveFlags = active;
        RawPersistentFlags = persistent;
    }
}
