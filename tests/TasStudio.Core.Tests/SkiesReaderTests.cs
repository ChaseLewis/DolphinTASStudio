using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Skies;
using Xunit;

namespace TasStudio.Core.Tests;

public sealed class SkiesReaderTests
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct PackedEntry
    {
        public byte Kind;
        public uint RawValue;
        public ushort RawCount;
    }

    [StructLayout(LayoutKind.Explicit, Size = 8)]
    private struct ExplicitEntry
    {
        [FieldOffset(0)] public byte Kind;
        [FieldOffset(3)] public uint RawValue;
    }

    [Fact]
    public async Task PackedReadsPreserveRawBytesWithoutEndianConversionOrAlignmentPadding()
    {
        var memory = new Memory();
        byte[] bytes = [7, 0x12, 0x34, 0x56, 0x78, 0xFE, 0xDC];
        memory.Put(0x80001001, bytes);
        var entry = await memory.Reader.ReadPacked<PackedEntry>(0x1001);
        Assert.Equal((byte)7, entry.Kind);
        Assert.Equal(BitConverter.ToUInt32(bytes, 1), entry.RawValue);
        Assert.Equal(BitConverter.ToUInt16(bytes, 5), entry.RawCount);
        Assert.Equal(new[] { (0x80001001u, 7) }, memory.Reads);
        memory.Put(0x80001001, 0);
        Assert.Equal((byte)7, entry.Kind);
        Assert.Equal((byte)0, (await memory.Reader.ReadPackedAsync<PackedEntry>(0x1001)).Kind);
    }

    [Fact]
    public async Task PackedArraysUseStructStrideAndFollowPointerOffsetsOnce()
    {
        var memory = new Memory();
        memory.U32(0x80001000, 0x80002000);
        memory.U32(0x80002010, 0x80003000);
        byte[] bytes = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14];
        memory.Put(0x80003003, bytes);
        var entries = await memory.Reader.ReadPackedArray<PackedEntry>(0x1000, 2, 0x10, 3);
        Assert.Equal(2, entries.Length);
        Assert.Equal((byte)1, entries[0].Kind);
        Assert.Equal((byte)8, entries[1].Kind);
        Assert.Equal(BitConverter.ToUInt32(bytes, 8), entries[1].RawValue);
        Assert.Equal(BitConverter.ToUInt16(bytes, 12), entries[1].RawCount);
        Assert.Equal(new[] { (0x80001000u, 4), (0x80002010u, 4), (0x80003003u, 14) }, memory.Reads);
        entries[0].Kind = 99;
        Assert.Equal((byte)1, memory.Bytes[0x80003003]);
    }

    [Fact]
    public async Task PackedReadsRespectExplicitOffsetsAndSize()
    {
        var memory = new Memory();
        byte[] bytes = [1, 0, 0, 0x12, 0x34, 0x56, 0x78, 0];
        memory.Put(0x80001000, bytes);
        var entry = await memory.Reader.ReadPackedAsync<ExplicitEntry>(0x1000);
        Assert.Equal((byte)1, entry.Kind);
        Assert.Equal(BitConverter.ToUInt32(bytes, 3), entry.RawValue);
        Assert.Equal(new[] { (0x80001000u, 8) }, memory.Reads);
    }

    [Fact]
    public async Task PackedArraysHandleEmptyCountsAndRejectInvalidReads()
    {
        var memory = new Memory();
        Assert.Empty(await memory.Reader.ReadPackedArrayAsync<PackedEntry>(0x1000, 0));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => memory.Reader.ReadPackedArrayAsync<PackedEntry>(0x1000, -1));
        await Assert.ThrowsAsync<OverflowException>(() => memory.Reader.ReadPackedArrayAsync<PackedEntry>(0x1000, int.MaxValue));
        await Assert.ThrowsAsync<InvalidDataException>(() => memory.Reader.ReadPackedArrayAsync<PackedEntry>(0x817FFFF9, 2));
        await Assert.ThrowsAsync<InvalidDataException>(() => memory.Reader.ReadPackedAsync<PackedEntry>(0x1000, 0));
        var shortReader = new GameCubeMemoryReader((_, count) => Task.FromResult(new byte[count - 1]));
        await Assert.ThrowsAsync<InvalidDataException>(() => shortReader.ReadPackedAsync<PackedEntry>(0x1000));
        await Assert.ThrowsAsync<InvalidDataException>(() => shortReader.ReadPackedArrayAsync<PackedEntry>(0x1000, 2));
    }

    private sealed class Memory
    {
        public readonly Dictionary<uint, byte> Bytes = new();
        public readonly List<(uint Address, int Count)> Reads = new();
        public GameCubeMemoryReader Reader => new((address, count) =>
        {
            Reads.Add((address, count));
            return Task.FromResult(Enumerable.Range(0, count).Select(i => Bytes.GetValueOrDefault(address + (uint)i)).ToArray());
        });
        public void Put(uint address, params byte[] bytes)
        {
            for (var i = 0; i < bytes.Length; i++) Bytes[address + (uint)i] = bytes[i];
        }
        public void U32(uint address, uint value)
        {
            var bytes = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
            Put(address, bytes);
        }
    }

    [Fact]
    public async Task ReadsOriginalSignedSeedAndGameCounter()
    {
        var memory = new Memory();
        memory.U32(SkiesAddresses.RngSeed, 0xFEDCBA98);
        memory.Put(SkiesAddresses.FrameCounter, 1, 2, 3, 4, 5, 6, 7, 8);
        var game = new SkiesGame(memory.Reader);
        Assert.Equal(unchecked((int)0xFEDCBA98), await game.ReadRngSeedAsync());
        Assert.Equal(0xFEDCBA98u, await game.ReadRngAsync());
        Assert.Equal(0x0102030405060708ul, await game.ReadFrameCounterAsync());
    }

    [Fact]
    public async Task DecodesEveryBattleStateAndReturnsUnknownForUnrecognizedValues()
    {
        var memory = new Memory();
        var game = new SkiesGame(memory.Reader);
        foreach (var state in Enum.GetValues<BattleState>())
        {
            memory.Put(SkiesAddresses.BattleState, (byte)state);
            Assert.Equal(state, await game.ReadBattleStateAsync());
        }
        foreach (byte invalid in new byte[] { 10, 11, 12, 14, 255 })
        {
            memory.Put(SkiesAddresses.BattleState, invalid);
            Assert.Equal(BattleState.Unknown, await game.ReadBattleStateAsync());
        }
    }

    [Fact]
    public async Task InventoryPreservesSlotsSignedValuesAndSnapshot()
    {
        var memory = new Memory();
        memory.U32(SkiesAddresses.InventoryPointer, 0x80300000);
        short[] entries = [2, 273, 3, 258, 4, 273, -1, 273, 9, 42, 7, -1, 6, 258, 8, 273];
        var bytes = new byte[32];
        for (var i = 0; i < entries.Length; i++) BinaryPrimitives.WriteInt16BigEndian(bytes.AsSpan(i * 2), entries[i]);
        memory.Put(0x8030000e, bytes);
        var game = new SkiesGame(memory.Reader);
        var inventory = await game.ReadInventoryAsync();
        Assert.Equal(4, System.Runtime.CompilerServices.Unsafe.SizeOf<InventorySlot>());
        var packedSlots = await memory.Reader.ReadPackedArray<InventorySlot>(SkiesAddresses.InventoryPointer, 8, 0x0e);
        Assert.Equal((short)273, packedSlots[0].ItemId);
        Assert.Equal((short)2, packedSlots[0].Quantity);
        Assert.Equal((short)-1, packedSlots[3].Quantity);
        Assert.Equal((short)-1, packedSlots[5].ItemId);
        Assert.Equal(8, inventory.Slots.Count);
        Assert.Equal(13, inventory.ElectriCount);
        Assert.Equal(9, inventory.MoonberryCount);
        Assert.Equal("Electri", inventory.Slots[0].ItemName);
        Assert.Equal("Moonberry", inventory.Slots[1].ItemName);
        Assert.Equal("Item 42", inventory.Slots[4].ItemName);
        Assert.Equal(7, inventory.CountItem(-1));
        Assert.Equal(13, await game.ReadElectriCountAsync());
        Assert.Equal(9, await game.ReadMoonberryCountAsync());
        Assert.Equal(0, await game.CountItemAsync(999));
        memory.Put(0x8030000e, 0, 10);
        Assert.Equal(13, inventory.ElectriCount);
        Assert.Equal(21, await game.ReadElectriCountAsync());
        Assert.Throws<NotSupportedException>(() => ((IList<InventorySlot>)inventory.Slots)[0] = default);
    }

    [Fact]
    public async Task PointerChainsDereferenceBeforeEachOffsetAndAcceptMem1Aliases()
    {
        var memory = new Memory();
        memory.U32(0x80001000, 0xC0002000);
        memory.U32(0x80002010, 0x00003000);
        memory.Put(0x80003024, 0xFE, 0xDC);
        Assert.Equal((short)-292, await memory.Reader.ReadInt16Async(0x1000, 0x10, 0x24));
        Assert.Equal(new[] { (0x80001000u, 4), (0x80002010u, 4), (0x80003024u, 2) }, memory.Reads);
        Assert.Equal(0x80003024u, await memory.Reader.ResolveAddressAsync(0x80001000, 0x10, 0x24));
    }

    [Fact]
    public async Task PrimitiveReadsDecodeBigEndianAndStrings()
    {
        var memory = new Memory();
        memory.Put(0x80001000, 0xFF, 0xFE, 0xDC, 0xBA, 0x98, 0x76, 0x54, 0x32);
        var reader = memory.Reader;
        Assert.Equal((byte)255, await reader.ReadByteAsync(0x1000));
        Assert.Equal((sbyte)-1, await reader.ReadSByteAsync(0x1000));
        Assert.Equal((ushort)65534, await reader.ReadUInt16Async(0x1000));
        Assert.Equal(unchecked((long)0xFFFEDCBA98765432ul), await reader.ReadInt64Async(0x1000));
        memory.Put(0x80001000, 0x3F, 0xC0, 0, 0, 0, 0, 0, 0);
        Assert.Equal(1.5f, await reader.ReadSingleAsync(0x1000));
        memory.Put(0x80001000, 0x3F, 0xF8);
        Assert.Equal(1.5d, await reader.ReadDoubleAsync(0x1000));
        memory.Put(0x80001000, 65, 66, 0, 67);
        Assert.Equal("AB", await reader.ReadStringAsync(0x1000, 4));
    }

    [Fact]
    public async Task InvalidPointersRangesAndShortReadsFailInsteadOfReturningInventedValues()
    {
        var memory = new Memory();
        var reader = memory.Reader;
        await Assert.ThrowsAsync<InvalidDataException>(() => reader.ReadInt16Async(0x1000, 0));
        memory.U32(0x80001000, 0x90000000);
        await Assert.ThrowsAsync<InvalidDataException>(() => reader.ReadInt16Async(0x1000, 0));
        memory.U32(0x80001000, 0x817FFFFF);
        await Assert.ThrowsAsync<InvalidDataException>(() => reader.ReadInt16Async(0x1000, 0));
        memory.U32(0x80001000, 0x80001000);
        await Assert.ThrowsAsync<InvalidDataException>(() => reader.ReadInt16Async(0x1000, 0x40000000));
        await Assert.ThrowsAsync<InvalidDataException>(() => reader.ReadUInt32Async(0x817FFFFE));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => reader.ReadBytesAsync(0x1000, -1));
        var shortReader = new GameCubeMemoryReader((_, _) => Task.FromResult(new byte[1]));
        await Assert.ThrowsAsync<InvalidDataException>(() => shortReader.ReadUInt32Async(0x1000));
        await Assert.ThrowsAsync<InvalidDataException>(() => new SkiesGame(memory.Reader).ReadElectriCountAsync());
    }
}
