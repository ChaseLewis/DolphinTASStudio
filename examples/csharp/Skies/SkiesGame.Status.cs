using System.Buffers.Binary;

namespace Skies;

public sealed partial class SkiesGame
{
    /// <summary>Reads a battle actor (party 0–3, enemies 4–11).
    /// Null means an absent/implausible stats pointer, not an actor with no ailments.
    /// Use during a known live battle with emulation paused; pointer plausibility alone
    /// cannot establish a live encounter. Memory read errors propagate.</summary>
    public async Task<BattleStatusSnapshot?> ReadBattleStatusAsync(int actorSlot)
    {
        var pointer = await Memory.ReadUInt32Async(
            SlotAddress(SkiesAddresses.CharacterBattlePointers, actorSlot, MaximumBattleActors));
        if (!IsPlausibleBattlePointer(pointer)) return null;
        // Both flag words are copied together; the preceding pointer read is separate.
        var bytes = await Memory.ReadBytesAsync(pointer + SkiesAddresses.ActiveStatusOffset, 8);
        return new(actorSlot, pointer, BinaryPrimitives.ReadUInt32BigEndian(bytes),
            BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(4)));
    }

    /// <summary>Party-slot convenience reader, 0–3. Slots are battle positions, not character IDs.</summary>
    public Task<BattleStatusSnapshot?> ReadCharacterStatusAsync(int partySlot)
    {
        _ = SlotAddress(SkiesAddresses.CharacterBattlePointers, partySlot, MaximumPartyMembers);
        return ReadBattleStatusAsync(partySlot);
    }

    /// <summary>Enemy-index convenience reader, 0–7. Enemy 0 corresponds to actor slot 4.</summary>
    public Task<BattleStatusSnapshot?> ReadEnemyStatusAsync(int enemyIndex)
    {
        _ = SlotAddress(SkiesAddresses.EnemyBattlePointers, enemyIndex, MaximumEnemies);
        return ReadBattleStatusAsync(MaximumPartyMembers + enemyIndex);
    }
}
