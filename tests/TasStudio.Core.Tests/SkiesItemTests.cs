using System.Buffers.Binary;
using Skies;
using Xunit;

namespace TasStudio.Core.Tests;

public sealed class SkiesItemTests
{
    private sealed class Memory
    {
        public readonly Dictionary<uint, byte> Bytes = [];
        public readonly List<(uint Address, int Count)> Reads = [];
        public SkiesGame Game => new(new GameCubeMemoryReader((address, count) =>
        {
            Reads.Add((address, count));
            return Task.FromResult(Enumerable.Range(0, count).Select(i => Bytes.GetValueOrDefault(address + (uint)i)).ToArray());
        }));
        public void Put(uint address, params byte[] bytes)
        {
            for (var i = 0; i < bytes.Length; i++) Bytes[address + (uint)i] = bytes[i];
        }
        public void Owned(uint address, short id, sbyte quantity, byte extra = 0)
        {
            var bytes = new byte[4];
            BinaryPrimitives.WriteInt16BigEndian(bytes, id);
            bytes[2] = unchecked((byte)quantity);
            bytes[3] = extra;
            Put(address, bytes);
        }
    }

    [Fact]
    public void NamedEnumsUseGlobalIdsAndKeepLegacyConstants()
    {
        Assert.Equal(258, (int)UsableItem.Moonberry);
        Assert.Equal(246, (int)UsableItem.AuraOfValor);
        Assert.Equal(497, (int)ShipItem.ApaWax);
        Assert.Equal(498, (int)ShipItem.ApoWax);
        Assert.Equal(273, (int)UsableItem.ElectriBox);
        Assert.Equal(281, (int)UsableItem.SliparaBox);
        Assert.Equal((ushort)UsableItem.Moonberry, SkiesGame.Moonberry);
        Assert.Equal((ushort)UsableItem.ElectriBox, SkiesGame.Electri);
        Assert.Equal(80, Enum.GetValues<UsableItem>().Distinct().Count());
        Assert.Equal(30, Enum.GetValues<ShipItem>().Distinct().Count());
        Assert.Equal("Apa Wax", ItemNames.GetName((int)ShipItem.ApaWax));
        Assert.Equal("Aura of Valor", ItemNames.GetName((int)UsableItem.AuraOfValor));
        Assert.Equal("Item 12345", ItemNames.GetName(12345));
    }

    [Fact]
    public async Task OwnedUsablesReadBeyondEightSlotsAndNeverCountPendingRewards()
    {
        var m = new Memory();
        m.Owned(0x8030BF08, 258, 3, 0x99);
        m.Owned(0x8030BF08 + 79 * 4, 258, 2);
        m.Owned(0x8030BF08 + 4, 246, 1);
        m.Owned(0x8030BF08 + 8, -1, 99);
        m.Owned(0x8030BF08 + 12, 1234, -2, 0xFE);
        // Separate reward table has seven berries, using quantity-before-ID halfwords.
        m.Put(SkiesAddresses.InventoryPointer, 0x80, 0x00, 0x10, 0x00);
        m.Put(0x8000100E, 0, 7, 1, 2);
        var game = m.Game;
        var bag = await game.ReadOwnedUsableItemsAsync();
        Assert.Equal((0x8030BF08u, 320), Assert.Single(m.Reads));
        Assert.Equal(80, bag.Slots.Count);
        Assert.Equal(5, bag.CountItem(UsableItem.Moonberry));
        Assert.Equal(1, bag.CountItem(UsableItem.AuraOfValor));
        Assert.Equal(0, bag.CountItem(-1));
        Assert.True(bag.Slots[2].IsEmpty);
        Assert.Equal((sbyte)-2, bag.Slots[3].Quantity);
        Assert.Equal((byte)0xFE, bag.Slots[3].RawExtra);
        Assert.Equal((byte)0x99, bag.Slots[0].RawExtra);
        Assert.Equal("Moonberry", bag.Slots[0].ItemName);
        Assert.Equal(7, await game.CountItemAsync(UsableItem.Moonberry));
        Assert.Equal(5, await game.CountOwnedItemAsync(UsableItem.Moonberry));
        m.Owned(0x8030BF08, 258, 9);
        Assert.Equal(5, bag.CountItem(UsableItem.Moonberry));
        Assert.Equal(11, await game.CountOwnedItemAsync(UsableItem.Moonberry));
        Assert.Throws<NotSupportedException>(() => ((IList<OwnedInventorySlot>)bag.Slots)[0] = new(258, 1, 0));
    }

    [Fact]
    public async Task ShipInventoryAndTypedRewardOverloadsUseTheirRespectiveLayouts()
    {
        var m = new Memory();
        m.Owned(0x8030C188, 497, 4);
        m.Owned(0x8030C188 + 29 * 4, 498, 6);
        m.Put(SkiesAddresses.InventoryPointer, 0x80, 0x00, 0x10, 0x00);
        m.Put(0x8000100E, 0, 2, 1, 0xF1); // reward: Apa Wax x2
        var game = m.Game;
        var ship = await game.ReadOwnedShipItemsAsync();
        Assert.Equal((0x8030C188u, 120), Assert.Single(m.Reads));
        Assert.Equal(30, ship.Slots.Count);
        Assert.Equal(4, ship.CountItem(ShipItem.ApaWax));
        Assert.Equal(6, ship.CountItem(ShipItem.ApoWax));
        Assert.Equal(6, await game.CountOwnedItemAsync(ShipItem.ApoWax));
        Assert.Equal(2, await game.CountItemAsync(ShipItem.ApaWax));
        var drops = await game.ReadInventoryAsync();
        Assert.Equal(2, drops.CountItem(ShipItem.ApaWax));
        Assert.Equal("Apa Wax", drops.Slots[0].ItemName);
    }

    [Fact]
    public async Task ShortOwnedInventoryReadsPropagateInsteadOfReportingZeroItems()
    {
        var game = new SkiesGame(new GameCubeMemoryReader((_, count) => Task.FromResult(new byte[count - 1])));
        await Assert.ThrowsAsync<InvalidDataException>(() => game.ReadOwnedUsableItemsAsync());
        await Assert.ThrowsAsync<InvalidDataException>(() => game.ReadOwnedShipItemsAsync());
    }
}
