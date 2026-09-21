using System.Buffers.Binary;
using Skies;
using Xunit;

namespace TasStudio.Core.Tests;

public sealed class SkiesEnemyNameTests
{
    private sealed class Memory
    {
        public Dictionary<uint, byte> Bytes { get; } = [];
        public int Reads { get; private set; }
        public SkiesGame Game => new(new GameCubeMemoryReader((address, count) =>
        {
            Reads++;
            return Task.FromResult(Enumerable.Range(0, count).Select(i => Bytes.GetValueOrDefault(address + (uint)i)).ToArray());
        }));
        public void Word(uint address, uint value)
        {
            var bytes = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
            for (var i = 0; i < 4; i++) Bytes[address + (uint)i] = bytes[i];
        }
        public void Enemy(int index, short id, int hp, int maxHp, uint status)
        {
            var slot = 4 + index;
            var idAddress = 0x80309DCCu + (uint)(slot * 2);
            Bytes[idAddress] = (byte)(id >> 8);
            Bytes[idAddress + 1] = (byte)id;
            var pointer = 0x80001000u + (uint)(index * 0x200);
            Word(0x80309DE4u + (uint)(slot * 4), pointer);
            Word(pointer + 0x14, unchecked((uint)hp));
            Word(pointer + 0x18, unchecked((uint)maxHp));
            Word(pointer + 0x1C, status);
        }
    }

    [Fact]
    public async Task FindsBossByIdentityRatherThanHpAndReturnsEnemyIndexNotActorSlotOrSpeciesId()
    {
        var m = new Memory();
        m.Enemy(0, 0, 100, 100, 0);
        m.Enemy(5, 128, 100, 100, 0x400); // same HP; different identity, after empty slots
        Assert.Equal(5, await m.Game.GetEnemyIndexAsync("  aNtOnIo  "));
        var status = Assert.IsType<BattleStatusSnapshot>(await m.Game.ReadEnemyStatusAsync("Antonio"));
        Assert.Equal(9, status.ActorSlot);
        Assert.True(status.HasAll(BattleStatus.Sleep));
        var identities = await m.Game.ReadEnemyIdentitiesAsync();
        Assert.Equal(new[] { 0, 5 }, identities.Select(e => e.EnemyIndex));
        Assert.Equal((short)128, identities[1].EnemyId);
        Assert.Equal("Antonio", identities[1].Name);
        Assert.Null(await m.Game.GetEnemyIndexAsync("Ant"));
        Assert.Null(await m.Game.ReadEnemyStatusAsync("Not present"));
    }

    [Fact]
    public async Task DuplicateNamesRequireSelectionAndAliveFilterPrecedesOccurrence()
    {
        var m = new Memory();
        m.Enemy(1, 0, 0, 58, 0x100);
        m.Enemy(3, 0, 29, 58, 0x80);
        Assert.Equal(new[] { 1, 3 }, await m.Game.GetEnemyIndicesAsync("Soldier"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => m.Game.GetEnemyIndexAsync("Soldier"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => m.Game.ReadEnemyStatusAsync("Soldier"));
        Assert.Equal(3, await m.Game.GetEnemyIndexAsync("Soldier", aliveOnly: true));
        var second = Assert.IsType<BattleStatusSnapshot>(await m.Game.ReadEnemyStatusAsync("Soldier", occurrence: 1));
        Assert.Equal(7, second.ActorSlot);
        Assert.True(second.HasAny(BattleStatus.Poison));
        Assert.Equal(7, (await m.Game.ReadEnemyStatusAsync("Soldier", occurrence: 0, aliveOnly: true))!.ActorSlot);
        Assert.Null(await m.Game.ReadEnemyStatusAsync("Soldier", occurrence: 1, aliveOnly: true));
        Assert.Null(await m.Game.ReadEnemyStatusAsync("Soldier", occurrence: 2));
    }

    [Fact]
    public async Task InvalidSlotsAreSkippedUnknownNamesStayUnknownAndLookupsAreNotCached()
    {
        var m = new Memory();
        m.Enemy(0, -1, 10, 10, 0);
        m.Enemy(1, 255, 10, 10, 0);
        m.Enemy(2, 128, 11, 10, 0); // invalid HP
        m.Enemy(3, 128, 10, 10, 0);
        m.Word(0x80309DF4 + 3 * 4, 0); // stale ID with absent pointer
        m.Enemy(7, 300, 10, 10, 0); // unknown ID, no guessed name
        var unknown = Assert.Single(await m.Game.ReadEnemyIdentitiesAsync());
        Assert.Equal(7, unknown.EnemyIndex);
        Assert.Null(unknown.Name);
        Assert.Empty(await m.Game.GetEnemyIndicesAsync("Antonio"));
        m.Enemy(7, 128, 10, 10, 0);
        Assert.Equal(7, await m.Game.GetEnemyIndexAsync("Antonio"));
        Assert.Null(unknown.Name); // previous snapshot stays unchanged
    }

    [Fact]
    public async Task RejectsInvalidArgumentsWithoutReadingAndPropagatesMemoryErrors()
    {
        var m = new Memory();
        await Assert.ThrowsAsync<ArgumentNullException>(() => m.Game.GetEnemyIndicesAsync(null!));
        await Assert.ThrowsAsync<ArgumentException>(() => m.Game.GetEnemyIndexAsync("  "));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => m.Game.ReadEnemyStatusAsync("Soldier", -1));
        Assert.Equal(0, m.Reads);
        var shortReader = new SkiesGame(new GameCubeMemoryReader((_, count) => Task.FromResult(new byte[count - 1])));
        await Assert.ThrowsAsync<InvalidDataException>(() => shortReader.ReadEnemyStatusAsync("Antonio"));
    }
}
