using System.Buffers.Binary;
using Skies;
using Xunit;

namespace TasStudio.Core.Tests;

public sealed class SkiesStatusTests
{
    private sealed class Memory
    {
        public Dictionary<uint, byte> Bytes { get; } = [];
        public List<(uint Address, int Count)> Reads { get; } = [];
        public SkiesGame Game => new(new GameCubeMemoryReader((address, count) =>
        {
            Reads.Add((address, count));
            return Task.FromResult(Enumerable.Range(0, count).Select(i => Bytes.GetValueOrDefault(address + (uint)i)).ToArray());
        }));
        public void Word(uint address, uint value)
        {
            var bytes = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
            for (var i = 0; i < 4; i++) Bytes[address + (uint)i] = bytes[i];
        }
    }

    [Theory]
    [InlineData(0x80u, BattleStatus.Poison)]
    [InlineData(0x200u, BattleStatus.Silence)]
    [InlineData(0x400u, BattleStatus.Sleep)]
    [InlineData(0x800u, BattleStatus.Confusion)]
    [InlineData(0x1000u, BattleStatus.Fatigue)]
    [InlineData(0x4000u, BattleStatus.Stone)]
    [InlineData(0x80000u, BattleStatus.Weak)]
    public async Task ReadsRecoveredActiveBitsInGuestByteOrder(uint flags, BattleStatus expected)
    {
        var m = new Memory();
        m.Word(0x80309DE4, 0x80001000);
        m.Word(0x8000101C, flags);
        var status = Assert.IsType<BattleStatusSnapshot>(await m.Game.ReadCharacterStatusAsync(0));
        Assert.Equal(expected, status.Effects);
        Assert.Equal(flags, status.RawActiveFlags);
        Assert.True(status.HasAll(expected));
        Assert.Equal(0u, status.UnmappedActiveFlags);
        Assert.Equal(new[] { (0x80309DE4u, 4), (0x8000101Cu, 8) }, m.Reads);
    }

    [Fact]
    public async Task CombinesAilmentsPreservesOtherBitsAndDoesNotTreatPersistentEffectsAsActive()
    {
        var m = new Memory();
        m.Word(0x80309DF4, 0x80002000);
        m.Word(0x8000201C, 0x80000481); // Sleep + Poison plus high/internal bits
        m.Word(0x80002020, 0x200); // persistent Silence only
        var status = Assert.IsType<BattleStatusSnapshot>(await m.Game.ReadEnemyStatusAsync(0));
        Assert.Equal(4, status.ActorSlot);
        Assert.True(status.IsEnemy);
        Assert.Equal(0x80002000u, status.StatsAddress);
        Assert.Equal(BattleStatus.Sleep | BattleStatus.Poison, status.Effects);
        Assert.Equal(0x80000001u, status.UnmappedActiveFlags);
        Assert.Equal(0x200u, status.RawPersistentFlags);
        Assert.False(status.HasAny(BattleStatus.Silence));
        Assert.True(status.HasAny(BattleStatus.Poison | BattleStatus.Silence));
        Assert.False(status.HasAll(BattleStatus.Poison | BattleStatus.Silence));
        Assert.True(status.HasAll(BattleStatus.Sleep | BattleStatus.Poison));
        Assert.False(status.HasAny(BattleStatus.None));
        Assert.True(status.HasAll(BattleStatus.None));
        m.Word(0x8000201C, 0);
        Assert.Equal(BattleStatus.Sleep | BattleStatus.Poison, status.Effects);
        Assert.Equal(BattleStatus.None, (await m.Game.ReadEnemyStatusAsync(0))!.Effects);
    }

    [Fact]
    public async Task ConvenienceReadersReachLastPartyAndEnemySlots()
    {
        var m = new Memory();
        m.Word(0x80309DF0, 0x80003000); // party 3
        m.Word(0x80309E10, 0x80004000); // enemy 7 / actor 11
        m.Word(0x8000301C, 0x200);
        m.Word(0x8000401C, 0x80000);
        var party = Assert.IsType<BattleStatusSnapshot>(await m.Game.ReadCharacterStatusAsync(3));
        var enemy = Assert.IsType<BattleStatusSnapshot>(await m.Game.ReadEnemyStatusAsync(7));
        Assert.Equal(3, party.ActorSlot);
        Assert.False(party.IsEnemy);
        Assert.Equal(BattleStatus.Silence, party.Effects);
        Assert.Equal(11, enemy.ActorSlot);
        Assert.Equal(BattleStatus.Weak, enemy.Effects);
        Assert.Equal(enemy.RawActiveFlags, (await m.Game.ReadBattleStatusAsync(11))!.RawActiveFlags);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(0x12345678u)]
    [InlineData(0x80001001u)]
    [InlineData(0x81800000u)]
    public async Task InvalidPointersReturnNullWithoutReadingFlags(uint pointer)
    {
        var m = new Memory();
        m.Word(0x80309DE4, pointer);
        Assert.Null(await m.Game.ReadBattleStatusAsync(0));
        Assert.Single(m.Reads);
    }

    [Fact]
    public async Task RejectsBadIndicesBeforeAnyReadAndPropagatesMemoryFailures()
    {
        var m = new Memory();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => m.Game.ReadBattleStatusAsync(-1));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => m.Game.ReadBattleStatusAsync(12));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => m.Game.ReadCharacterStatusAsync(-1));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => m.Game.ReadCharacterStatusAsync(4));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => m.Game.ReadEnemyStatusAsync(-1));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => m.Game.ReadEnemyStatusAsync(8));
        Assert.Empty(m.Reads);
        m.Word(0x80309DE4, 0x817FFFFC);
        await Assert.ThrowsAsync<InvalidDataException>(() => m.Game.ReadBattleStatusAsync(0));
        var broken = new SkiesGame(new GameCubeMemoryReader((_, _) => throw new IOException("read failed")));
        await Assert.ThrowsAsync<IOException>(() => broken.ReadBattleStatusAsync(0));
    }
}
