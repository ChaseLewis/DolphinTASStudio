namespace Skies;

public sealed partial class SkiesGame
{
    public const int MaximumEnemies = 8;
    public const int MaximumPartyMembers = 4;

    /// <summary>A necessary pointer check, not proof that the structure belongs to a live battle.</summary>
    public static bool IsPlausibleBattlePointer(uint pointer) =>
        pointer is >= 0x80000000 and < 0x81800000 && pointer % 4 == 0;

    public Task<int> ReadAreaIdAsync() => Memory.ReadInt32Async(SkiesAddresses.AreaId);
    public async Task<char> ReadAreaSubIdAsync() => (char)await Memory.ReadByteAsync(SkiesAddresses.AreaSubId);
    public async Task<Area> ReadAreaAsync() => new(await ReadAreaIdAsync(), await ReadAreaSubIdAsync());
    public async Task<EncounterIdentifier> ReadEncounterIdentifierAsync() => new(
        await ReadAreaAsync(), await Memory.ReadInt16Async(SkiesAddresses.StageId),
        await Memory.ReadInt16Async(SkiesAddresses.EncounterId));

    public Task<uint> ReadCharacterBattleStatsPointerAsync(int slot) =>
        Memory.ReadUInt32Async(SlotAddress(SkiesAddresses.CharacterBattlePointers, slot, MaximumPartyMembers));

    public async Task<bool> HasBattleCharacterAsync(int slot) =>
        IsPlausibleBattlePointer(await ReadCharacterBattleStatsPointerAsync(slot));

    /// <summary>Count plausible party pointers. Empty slots are allowed anywhere in the four-slot table.</summary>
    public async Task<int> ReadBattlePartySizeAsync()
    {
        var size = 0;
        for (var slot = 0; slot < MaximumPartyMembers; slot++)
            if (await HasBattleCharacterAsync(slot)) size++;
        return size;
    }

    public async Task<CharacterBattleStats?> ReadCharacterBattleStatsAsync(int slot)
    {
        var pointer = await ReadCharacterBattleStatsPointerAsync(slot);
        if (!IsPlausibleBattlePointer(pointer)) return null;
        return await Memory.ReadPacked<CharacterBattleStats>(pointer + SkiesAddresses.CharacterQuickOffset);
    }

    public async Task<ushort?> ReadBattleQuickAsync(int slot) => (await ReadCharacterBattleStatsAsync(slot))?.Quick;
    public async Task<ushort?> ReadBattleHitAsync(int slot) => (await ReadCharacterBattleStatsAsync(slot))?.Hit;

    /// <summary>Read a candidate enemy. Null indicates an invalid pointer or implausible HP.</summary>
    public async Task<EnemyStats?> ReadEnemyStatsAsync(int slot)
    {
        var address = SlotAddress(SkiesAddresses.EnemyBattlePointers, slot, MaximumEnemies);
        var pointer = await Memory.ReadUInt32Async(address);
        if (!IsPlausibleBattlePointer(pointer)) return null;
        var enemy = await Memory.ReadPacked<EnemyStats>(pointer + SkiesAddresses.EnemyHealthOffset);
        return enemy.IsPlausible ? enemy : null;
    }

    public async Task<EnemyEncounter?> ReadCurrentEncounterAsync()
    {
        // Enemy memory can look valid outside battle; SoAManips also requires party slot zero.
        if (!await HasBattleCharacterAsync(0)) return null;
        var enemies = new List<EnemyStats>();
        for (var slot = 0; slot < MaximumEnemies; slot++)
        {
            var enemy = await ReadEnemyStatsAsync(slot);
            if (enemy is null) break;
            enemies.Add(enemy.Value);
        }
        return enemies.Count == 0 ? null : new EnemyEncounter(await ReadEncounterIdentifierAsync(), enemies);
    }

    public async Task<bool> IsInsideEncounterAsync() => await ReadCurrentEncounterAsync() is not null;
    /// <summary>Living enemies in a validated encounter snapshot; null means no readable encounter.</summary>
    public async Task<int?> ReadLivingEnemyCountAsync() =>
        (await ReadCurrentEncounterAsync())?.Enemies.Count(enemy => !enemy.IsDefeated);

    public Task<sbyte> ReadFirstStrikeChanceAsync() => Memory.ReadSByteAsync(SkiesAddresses.FirstStrikeChance);
    public Task<sbyte> ReadBackAttackChanceAsync() => Memory.ReadSByteAsync(SkiesAddresses.BackAttackChance);
    public Task<sbyte> ReadEncounterModifierAsync() => Memory.ReadSByteAsync(SkiesAddresses.EncounterModifier);
    public Task<sbyte> ReadEscapeChanceAsync() => Memory.ReadSByteAsync(SkiesAddresses.InventoryPointer, SkiesAddresses.EscapeChanceOffset);

    private static uint SlotAddress(uint table, int slot, int count)
    {
        if (slot < 0 || slot >= count) throw new ArgumentOutOfRangeException(nameof(slot));
        return table + (uint)slot * 4;
    }
}
