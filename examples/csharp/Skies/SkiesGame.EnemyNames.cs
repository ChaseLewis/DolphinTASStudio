using System.Buffers.Binary;

namespace Skies;

public sealed partial class SkiesGame
{
    /// <summary>Reads present enemies in slot order, including defeated enemies and holes.
    /// Uses the shared actor-ID table, stats pointers and the existing HP plausibility check.
    /// Keep emulation paused in a known live encounter; these checks alone do not prove one.</summary>
    public async Task<IReadOnlyList<EnemyIdentity>> ReadEnemyIdentitiesAsync()
    {
        // Twelve signed IDs immediately precede twelve stats pointers.
        var table = await Memory.ReadBytesAsync(SkiesAddresses.BattleActorIds, MaximumBattleActors * 6);
        var enemies = new List<EnemyIdentity>();
        for (var index = 0; index < MaximumEnemies; index++)
        {
            var slot = MaximumPartyMembers + index;
            var id = BinaryPrimitives.ReadInt16BigEndian(table.AsSpan(slot * 2));
            var pointer = BinaryPrimitives.ReadUInt32BigEndian(table.AsSpan(MaximumBattleActors * 2 + slot * 4));
            if (id < 0 || id == 255 || !IsPlausibleBattlePointer(pointer)) continue;
            var stats = await Memory.ReadPacked<EnemyStats>(pointer + SkiesAddresses.EnemyHealthOffset);
            if (!stats.IsPlausible) continue;
            enemies.Add(new(index, id, EnemyNames.GetName(id), stats.Hp, stats.MaxHp));
        }
        return enemies.AsReadOnly();
    }

    /// <summary>Exact exported-name matching, case-insensitive, with outer whitespace trimmed.
    /// Returns all matching enemy indices (0–7), sorted by slot; no fuzzy or substring matching.</summary>
    public async Task<IReadOnlyList<int>> GetEnemyIndicesAsync(string name, bool aliveOnly = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var wanted = name.Trim();
        var enemies = await ReadEnemyIdentitiesAsync();
        return Array.AsReadOnly(enemies.Where(e => (!aliveOnly || !e.IsDefeated) &&
            string.Equals(e.Name, wanted, StringComparison.OrdinalIgnoreCase)).Select(e => e.EnemyIndex).ToArray());
    }

    /// <summary>Returns null when absent. Duplicate names throw rather than choosing an enemy silently.</summary>
    public async Task<int?> GetEnemyIndexAsync(string name, bool aliveOnly = false)
    {
        var indices = await GetEnemyIndicesAsync(name, aliveOnly);
        return indices.Count switch
        {
            0 => null,
            1 => indices[0],
            _ => throw new InvalidOperationException($"Multiple enemies named '{name}' at indices {string.Join(", ", indices)}. Use GetEnemyIndicesAsync or specify an occurrence.")
        };
    }

    /// <summary>Read a uniquely named enemy's status. Missing name returns null; ambiguous names throw.
    /// Resolves afresh on each call; pause emulation across lookup and status reading.</summary>
    public async Task<BattleStatusSnapshot?> ReadEnemyStatusAsync(string name, bool aliveOnly = false)
    {
        var index = await GetEnemyIndexAsync(name, aliveOnly);
        return index is int found ? await ReadEnemyStatusAsync(found) : null;
    }

    /// <summary>Read a specific same-name occurrence, zero-based in ascending enemy-slot order.
    /// Missing occurrence returns null. aliveOnly filters before occurrence selection.</summary>
    public async Task<BattleStatusSnapshot?> ReadEnemyStatusAsync(string name, int occurrence, bool aliveOnly = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(occurrence);
        var indices = await GetEnemyIndicesAsync(name, aliveOnly);
        return occurrence < indices.Count ? await ReadEnemyStatusAsync(indices[occurrence]) : null;
    }
}
