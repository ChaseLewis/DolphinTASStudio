using System.Buffers.Binary;

namespace Skies;

public sealed partial class SkiesGame
{
    public const int MaximumBattleActors = MaximumPartyMembers + MaximumEnemies;

    /// <summary>Raw slot read, including unused/stale slots. Actor slot is 0–11, not enemy index.</summary>
    public async Task<BattleDecision> ReadBattleDecisionAsync(int actorSlot)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(actorSlot);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(actorSlot, MaximumBattleActors);
        return new(actorSlot, await Memory.ReadBytesAsync(
            SkiesAddresses.BattleDecisions + (uint)(actorSlot * BattleDecision.RecordSize), BattleDecision.RecordSize));
    }

    public async Task<BattleActionOrder> ReadBattleActionOrderAsync() =>
        new(await Memory.ReadBytesAsync(SkiesAddresses.BattleActionOrder, MaximumBattleActors));

    /// <summary>Copies all 12 decisions and the adjacent order array in one memory read.
    /// Pointer presence and phase are separate reads: keep emulation paused throughout.
    /// Presence is not eligibility; dead/guarding actors may have decisions but no queue entry.</summary>
    public async Task<BattlePlanSnapshot> ReadBattlePlanAsync()
    {
        var before = await ReadBattleStateAsync();
        var pointers = await Memory.ReadBytesAsync(SkiesAddresses.CharacterBattlePointers, MaximumBattleActors * 4);
        var bytes = await Memory.ReadBytesAsync(SkiesAddresses.BattleDecisions,
            MaximumBattleActors * BattleDecision.RecordSize + MaximumBattleActors);
        var after = await ReadBattleStateAsync();
        var present = new List<int>();
        var decisions = new BattleDecision[MaximumBattleActors];
        for (var slot = 0; slot < MaximumBattleActors; slot++)
        {
            if (IsPlausibleBattlePointer(BinaryPrimitives.ReadUInt32BigEndian(pointers.AsSpan(slot * 4))))
                present.Add(slot);
            decisions[slot] = new(slot, bytes.AsSpan(slot * BattleDecision.RecordSize, BattleDecision.RecordSize));
        }
        return new(before, after, present.AsReadOnly(), Array.AsReadOnly(decisions),
            new(bytes.AsSpan(MaximumBattleActors * BattleDecision.RecordSize)));
    }
}
