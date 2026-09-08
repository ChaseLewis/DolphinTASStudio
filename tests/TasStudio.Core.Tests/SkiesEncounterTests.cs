using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text;
using Skies;
using Xunit;

namespace TasStudio.Core.Tests;

public sealed class SkiesEncounterTests
{
    private sealed class Memory
    {
        public readonly Dictionary<uint, byte> Bytes = new();
        public readonly List<uint> Reads = new();
        public SkiesGame Game => new(new GameCubeMemoryReader((address, count) =>
        {
            Reads.Add(address);
            return Task.FromResult(Enumerable.Range(0, count).Select(i => Bytes.GetValueOrDefault(address + (uint)i)).ToArray());
        }));
        public void Put(uint address, params byte[] bytes)
        {
            for (var i = 0; i < bytes.Length; i++) Bytes[address + (uint)i] = bytes[i];
        }
        public void I32(uint address, int value)
        {
            var bytes = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(bytes, value); Put(address, bytes);
        }
        public void Pointer(uint address, uint value) => I32(address, unchecked((int)value));
        public void I16(uint address, short value)
        {
            var bytes = new byte[2]; BinaryPrimitives.WriteInt16BigEndian(bytes, value); Put(address, bytes);
        }
        public void Enemy(int slot, int hp, int maxHp)
        {
            var pointer = 0x80400000u + (uint)slot * 0x100;
            Pointer(SkiesAddresses.EnemyBattlePointers + (uint)slot * 4, pointer);
            I32(pointer + 0x14, hp); I32(pointer + 0x18, maxHp);
        }
        public void Party(int slot) => Pointer(SkiesAddresses.CharacterBattlePointers + (uint)slot * 4, 0x80500000 + (uint)slot * 0x100);
        public void Identity()
        {
            I32(SkiesAddresses.AreaId, -123456);
            Put(SkiesAddresses.AreaSubId, (byte)'b');
            I16(SkiesAddresses.StageId, -123);
            I16(SkiesAddresses.EncounterId, 456);
        }
        public void Ship(string name = "Little Jack", int maxHp = 10000, int enemyHp = 12000)
        {
            Pointer(SkiesAddresses.ShipBattlePointer, 0x80600000);
            Pointer(0x80600020, 0x80601000);
            Pointer(0x80600024, 0x80602000);
            Put(0x80601000, new byte[16]);
            Put(0x80601000, Encoding.UTF8.GetBytes(name));
            I32(0x80601014, maxHp); I32(0x80602018, enemyHp);
        }
    }

    [Fact]
    public async Task LivingEnemyCountExcludesDefeatedEnemiesAndPreservesUnknown()
    {
        var memory = new Memory();
        Assert.Null(await memory.Game.ReadLivingEnemyCountAsync());
        memory.Party(0); memory.Enemy(0, 100, 100); memory.Enemy(1, 0, 100);
        Assert.Equal(1, await memory.Game.ReadLivingEnemyCountAsync());
        memory.Enemy(0, 0, 100);
        Assert.Equal(0, await memory.Game.ReadLivingEnemyCountAsync());
        memory.Pointer(SkiesAddresses.EnemyBattlePointers, 0);
        Assert.Null(await memory.Game.ReadLivingEnemyCountAsync());
    }

    [Fact]
    public async Task EncounterReadsIdentityAndOrderedHealthAsPackedStats()
    {
        var memory = new Memory(); memory.Identity(); memory.Party(0);
        memory.Enemy(0, 42, 100); memory.Enemy(1, 0, 50000);
        var encounter = Assert.IsType<EnemyEncounter>(await memory.Game.ReadCurrentEncounterAsync());
        Assert.Equal(new EncounterIdentifier(new Area(-123456, 'b'), -123, 456), encounter.EncounterId);
        Assert.Equal(8, Unsafe.SizeOf<EnemyStats>());
        Assert.Equal(2, encounter.Enemies.Count);
        Assert.Equal(42, encounter.Enemies[0].Hp);
        Assert.Equal(100, encounter.Enemies[0].MaxHp);
        Assert.True(encounter.Enemies[1].IsDefeated);
        Assert.False(encounter.IsAtFullHealth);
        Assert.True(await memory.Game.IsInsideEncounterAsync());
        Assert.Throws<NotSupportedException>(() => ((IList<EnemyStats>)encounter.Enemies)[0] = default);
        memory.Enemy(0, 1, 100);
        Assert.Equal(42, encounter.Enemies[0].Hp);
    }

    [Fact]
    public async Task RequiresFirstPartySlotAndStopsAtFirstInvalidEnemy()
    {
        var memory = new Memory(); memory.Enemy(0, 100, 100); memory.Party(1);
        Assert.Null(await memory.Game.ReadCurrentEncounterAsync());
        Assert.DoesNotContain(SkiesAddresses.EnemyBattlePointers, memory.Reads);
        memory.Party(0); memory.Enemy(2, 100, 100);
        var encounter = (await memory.Game.ReadCurrentEncounterAsync())!;
        Assert.Single(encounter.Enemies);
        Assert.DoesNotContain(SkiesAddresses.EnemyBattlePointers + 8, memory.Reads);
        for (var i = 0; i < 8; i++) memory.Enemy(i, 100, 100);
        Assert.Equal(8, (await memory.Game.ReadCurrentEncounterAsync())!.Enemies.Count);
    }

    [Theory]
    [InlineData(-1, 100)]
    [InlineData(101, 100)]
    [InlineData(0, 0)]
    [InlineData(0, -1)]
    [InlineData(1, 50001)]
    public async Task RejectsImplausibleEnemyHealth(int hp, int maxHp)
    {
        var memory = new Memory(); memory.Party(0); memory.Enemy(0, hp, maxHp);
        Assert.Null(await memory.Game.ReadEnemyStatsAsync(0));
        Assert.Null(await memory.Game.ReadCurrentEncounterAsync());
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(0x7FFFFFFCu)]
    [InlineData(0x81800000u)]
    [InlineData(0x80400001u)]
    [InlineData(0xC0400000u)]
    public async Task RejectsStalePointerShapesWithoutDereferencing(uint pointer)
    {
        var memory = new Memory();
        memory.Pointer(SkiesAddresses.EnemyBattlePointers, pointer);
        memory.Pointer(SkiesAddresses.CharacterBattlePointers, pointer);
        Assert.False(SkiesGame.IsPlausibleBattlePointer(pointer));
        Assert.Null(await memory.Game.ReadEnemyStatsAsync(0));
        Assert.Null(await memory.Game.ReadCharacterBattleStatsAsync(0));
        Assert.Equal(new[] { SkiesAddresses.EnemyBattlePointers, SkiesAddresses.CharacterBattlePointers }, memory.Reads);
    }

    [Fact]
    public async Task EncounterMatchingUsesIdentityAndMaxHpButAllowsDamage()
    {
        var memory = new Memory(); memory.Identity(); memory.Party(0); memory.Enemy(0, 100, 100);
        var original = (await memory.Game.ReadCurrentEncounterAsync())!;
        Assert.True(original.IsAtFullHealth);
        memory.Enemy(0, 1, 100);
        Assert.True(original.IsSameEncounter(await memory.Game.ReadCurrentEncounterAsync()));
        memory.Enemy(0, 1, 101);
        Assert.False(original.IsSameEncounter(await memory.Game.ReadCurrentEncounterAsync()));
        memory.Enemy(0, 100, 100); memory.I16(SkiesAddresses.StageId, 1);
        Assert.False(original.IsSameEncounter(await memory.Game.ReadCurrentEncounterAsync()));
        memory.Identity(); memory.Enemy(1, 100, 100);
        Assert.False(original.IsSameEncounter(await memory.Game.ReadCurrentEncounterAsync()));
        Assert.False(original.IsSameEncounter(null));
    }

    [Fact]
    public async Task ReadsPartyStatsWithGapsAndPreservesUnsignedValues()
    {
        var memory = new Memory(); memory.Party(0); memory.Party(2);
        memory.I16(0x80500288, unchecked((short)60000));
        memory.I16(0x8050029A, 1234);
        Assert.Equal(2, await memory.Game.ReadBattlePartySizeAsync());
        Assert.Null(await memory.Game.ReadCharacterBattleStatsAsync(1));
        Assert.Null(await memory.Game.ReadBattleQuickAsync(3));
        Assert.Equal((ushort)60000, await memory.Game.ReadBattleQuickAsync(2));
        Assert.Equal((ushort)1234, await memory.Game.ReadBattleHitAsync(2));
        Assert.Equal(0x14, Unsafe.SizeOf<CharacterBattleStats>());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => memory.Game.ReadCharacterBattleStatsAsync(-1));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => memory.Game.ReadCharacterBattleStatsAsync(4));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => memory.Game.ReadEnemyStatsAsync(8));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => memory.Game.ReadEnemyStatsAsync(-1));
    }

    [Fact]
    public async Task ReadsSignedEncounterModifiersAndEscapePointer()
    {
        var memory = new Memory();
        memory.Put(SkiesAddresses.FirstStrikeChance, 0xFF);
        memory.Put(SkiesAddresses.BackAttackChance, 0x80);
        memory.Put(SkiesAddresses.EncounterModifier, 5);
        memory.Pointer(SkiesAddresses.InventoryPointer, 0x80400000);
        memory.Put(0x80400002, 100);
        Assert.Equal((sbyte)-1, await memory.Game.ReadFirstStrikeChanceAsync());
        Assert.Equal((sbyte)-128, await memory.Game.ReadBackAttackChanceAsync());
        Assert.Equal((sbyte)5, await memory.Game.ReadEncounterModifierAsync());
        Assert.Equal((sbyte)100, await memory.Game.ReadEscapeChanceAsync());
    }

    [Fact]
    public async Task ShipReadsFollowSiblingChainsAndCannonOffsets()
    {
        var memory = new Memory(); memory.Ship(" Delphinus ", 20000, 12345);
        memory.Put(0x8060100C, 0xFF); // Garbage after the NUL must not affect the name.
        Assert.Equal("Delphinus", await memory.Game.ReadShipNameAsync());
        Assert.Equal(new ShipBattle("Delphinus", 20000, 12345), await memory.Game.ReadCurrentShipBattleAsync());
        uint[] offsets = [0x7A, 0x9C, 0xBE, 0xE0];
        for (var i = 0; i < offsets.Length; i++)
        {
            memory.I16(0x80601000 + offsets[i], (short)(150 + i));
            Assert.Equal((ushort)(150 + i), await memory.Game.ReadShipCannonHitAsync(i));
        }
        Assert.True(await memory.Game.IsInShipBattleAsync());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => memory.Game.ReadShipCannonHitAsync(4));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => memory.Game.ReadShipCannonHitAsync(-1));
    }

    [Theory]
    [InlineData("Not a ship", 10000, 1)]
    [InlineData("Little Jack", 9999, 1)]
    [InlineData("Delphinus", 10000, 0)]
    [InlineData("Delphinus", 10000, -1)]
    public async Task ShipDetectionRequiresAllSignals(string name, int maxHp, int enemyHp)
    {
        var memory = new Memory(); memory.Ship(name, maxHp, enemyHp);
        Assert.Null(await memory.Game.ReadCurrentShipBattleAsync());
    }

    [Fact]
    public async Task ShipDetectionToleratesInvalidMemoryButNotCancellation()
    {
        Assert.Null(await new Memory().Game.ReadCurrentShipBattleAsync());
        var failed = new SkiesGame(new GameCubeMemoryReader((_, _) => throw new IOException("Read failed")));
        Assert.Null(await failed.ReadCurrentShipBattleAsync());
        var cancelled = new SkiesGame(new GameCubeMemoryReader((_, _) => throw new OperationCanceledException()));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.ReadCurrentShipBattleAsync());
    }
}
