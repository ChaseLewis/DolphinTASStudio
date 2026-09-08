namespace Skies;

public sealed record ShipBattle(string ShipName, int MaxHp, int EnemyHp);

public sealed partial class SkiesGame
{
    private static readonly uint[] CannonHitOffsets = [0x7A, 0x9C, 0xBE, 0xE0];

    public async Task<string> ReadShipNameAsync() => (await Memory.ReadStringAsync(
        SkiesAddresses.ShipBattlePointer, 16, null, SkiesAddresses.PlayerShipChainOffset, 0)).Trim();
    public Task<int> ReadShipMaxHpAsync() => Memory.ReadInt32Async(SkiesAddresses.ShipBattlePointer,
        SkiesAddresses.PlayerShipChainOffset, SkiesAddresses.ShipMaxHpOffset);
    public Task<int> ReadEnemyShipHpAsync() => Memory.ReadInt32Async(SkiesAddresses.ShipBattlePointer,
        SkiesAddresses.EnemyShipChainOffset, SkiesAddresses.ShipHpOffset);

    public Task<ushort> ReadShipCannonHitAsync(int slot)
    {
        if (slot < 0 || slot >= CannonHitOffsets.Length) throw new ArgumentOutOfRangeException(nameof(slot));
        return Memory.ReadUInt16Async(SkiesAddresses.ShipBattlePointer,
            SkiesAddresses.PlayerShipChainOffset, CannonHitOffsets[slot]);
    }

    /// <summary>SoAManips' live ship-battle check: recognized name, max HP >= 10000,
    /// and positive enemy HP. Invalid memory chains yield null; cancellation still propagates.</summary>
    public async Task<ShipBattle?> ReadCurrentShipBattleAsync()
    {
        try
        {
            var name = await ReadShipNameAsync();
            if (name is not ("Little Jack" or "Delphinus")) return null;
            var maxHp = await ReadShipMaxHpAsync();
            if (maxHp < 10000) return null;
            var enemyHp = await ReadEnemyShipHpAsync();
            return enemyHp > 0 ? new ShipBattle(name, maxHp, enemyHp) : null;
        }
        catch (Exception error) when (error is IOException or InvalidDataException)
        {
            return null;
        }
    }

    public async Task<bool> IsInShipBattleAsync() => await ReadCurrentShipBattleAsync() is not null;
}
