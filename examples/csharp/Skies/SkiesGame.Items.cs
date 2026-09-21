using System.Buffers.Binary;

namespace Skies;

public sealed partial class SkiesGame
{
    /// <summary>Copies all 80 persistent usable-item slots, not the battle-drop table.</summary>
    public Task<OwnedInventorySnapshot> ReadOwnedUsableItemsAsync() => ReadOwnedItemsAsync(SkiesAddresses.OwnedUsableItems, 80);
    /// <summary>Copies all 30 persistent ship-item slots. Does not include ship equipment.</summary>
    public Task<OwnedInventorySnapshot> ReadOwnedShipItemsAsync() => ReadOwnedItemsAsync(SkiesAddresses.OwnedShipItems, 30);

    public async Task<int> CountOwnedItemAsync(UsableItem item) => (await ReadOwnedUsableItemsAsync()).CountItem(item);
    public async Task<int> CountOwnedItemAsync(ShipItem item) => (await ReadOwnedShipItemsAsync()).CountItem(item);

    private async Task<OwnedInventorySnapshot> ReadOwnedItemsAsync(uint address, int count)
    {
        var bytes = await Memory.ReadBytesAsync(address, count * 4);
        var slots = new OwnedInventorySlot[count];
        for (var i = 0; i < count; i++)
            slots[i] = new(BinaryPrimitives.ReadInt16BigEndian(bytes.AsSpan(i * 4)),
                unchecked((sbyte)bytes[i * 4 + 2]), bytes[i * 4 + 3]);
        return new(slots);
    }
}
