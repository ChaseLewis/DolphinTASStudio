using System.Buffers.Binary;
using TasStudio.Core;
using TasStudio.Emulation;
using Xunit;

namespace TasStudio.Core.Tests;

public sealed class MemoryWatchWriteTests
{
    [Theory]
    [InlineData(WatchType.U8, "255", "FF")]
    [InlineData(WatchType.S8, "-128", "80")]
    [InlineData(WatchType.U16, "65535", "FFFF")]
    [InlineData(WatchType.S16, "-32768", "8000")]
    [InlineData(WatchType.U32, "4294967295", "FFFFFFFF")]
    [InlineData(WatchType.S32, "-2147483648", "80000000")]
    [InlineData(WatchType.U64, "18446744073709551615", "FFFFFFFFFFFFFFFF")]
    [InlineData(WatchType.S64, "-9223372036854775808", "8000000000000000")]
    [InlineData(WatchType.S64, "9223372036854775807", "7FFFFFFFFFFFFFFF")]
    [InlineData(WatchType.Float32, "1.5", "3FC00000")]
    [InlineData(WatchType.Float64, "-1.5e0", "BFF8000000000000")]
    public void ParsesTypedValuesAsExactBigEndianBytes(WatchType type, string input, string hex) =>
        Assert.Equal(hex, Convert.ToHexString(WatchMemory.ParseValue(new(0, type), input)));

    [Theory]
    [InlineData(WatchType.U8, "-1")]
    [InlineData(WatchType.U8, "256")]
    [InlineData(WatchType.S8, "128")]
    [InlineData(WatchType.S8, "-129")]
    [InlineData(WatchType.U16, "65536")]
    [InlineData(WatchType.S16, "-32769")]
    [InlineData(WatchType.U32, "4294967296")]
    [InlineData(WatchType.S32, "2147483648")]
    [InlineData(WatchType.U64, "18446744073709551616")]
    [InlineData(WatchType.S64, "-9223372036854775809")]
    [InlineData(WatchType.U32, "1.5")]
    [InlineData(WatchType.U32, "")]
    [InlineData(WatchType.U32, "abc")]
    [InlineData(WatchType.Float32, "1e39")]
    [InlineData(WatchType.Float64, "1e309")]
    [InlineData(WatchType.Float32, "NaN")]
    [InlineData(WatchType.Float64, "Infinity")]
    public void RejectsInvalidAndOutOfRangeValues(WatchType type, string input) =>
        Assert.Throws<FormatException>(() => WatchMemory.ParseValue(new(0, type), input));

    [Fact]
    public void IntegerAndByteDisplaysRoundTripIncludingSignedBitPatterns()
    {
        foreach (var type in Enum.GetValues<WatchType>().Where(t => t <= WatchType.S64 || t == WatchType.Bytes))
        foreach (var display in Enum.GetValues<WatchDisplay>())
        {
            var watch = new WatchDefinition(0, type, 8, Display: display);
            var bytes = Enumerable.Repeat((byte)0xFE, watch.ByteCount).ToArray();
            Assert.Equal(bytes, WatchMemory.ParseValue(watch, WatchMemory.Format(watch, bytes)));
        }
        Assert.Equal(new byte[] { 0xFF }, WatchMemory.ParseValue(new(0, WatchType.S8, Display: WatchDisplay.Hex), "0xFF"));
        Assert.Throws<FormatException>(() => WatchMemory.ParseValue(new(0, WatchType.S8, Display: WatchDisplay.Hex), "100"));
        Assert.Throws<FormatException>(() => WatchMemory.ParseValue(new(0, WatchType.U8, Display: WatchDisplay.Binary), "2"));
        Assert.Throws<FormatException>(() => WatchMemory.ParseValue(new(0, WatchType.U8, Display: WatchDisplay.Octal), "8"));
    }

    [Fact]
    public void ByteArraysRequireExactLengthAndValidElements()
    {
        var watch = new WatchDefinition(0, WatchType.Bytes, 2);
        Assert.Equal(new byte[] { 1, 0xAF }, WatchMemory.ParseValue(watch, "01 AF"));
        foreach (var text in new[] { "01", "01 02 03", "01 GG", "01 100", "01 -1" })
            Assert.Throws<FormatException>(() => WatchMemory.ParseValue(watch, text));
    }

    [Fact]
    public void TextUsesUtf8ByteLimitZeroPaddingAndReversibleEscapes()
    {
        var watch = new WatchDefinition(0, WatchType.Text, 4);
        Assert.Equal(new byte[] { 0xC3, 0xA9, 0, 0 }, WatchMemory.ParseValue(watch, "é"));
        Assert.Throws<FormatException>(() => WatchMemory.ParseValue(watch, "ééé"));
        Assert.Throws<FormatException>(() => WatchMemory.ParseValue(watch, "bad\\q"));
        Assert.Throws<FormatException>(() => WatchMemory.ParseValue(watch, "a\0"));
        var bytes = new byte[] { (byte)'\\', (byte)'n', 10, 9 };
        Assert.Equal(bytes, WatchMemory.ParseValue(watch, WatchMemory.Format(watch, bytes)));
    }

    [Fact]
    public async Task WritesAreRecordedInvalidateStatesAndReplayAtTheirResolvedAddress()
    {
        using var files = new TestWorkspace(); var backend = new PointerBackend(); using var service = new ExecutionService(backend);
        await service.LoadGameAsync(files.GamePath, files.Options); await service.NewProjectAsync();
        await service.SaveNamedStateAsync("Before write");
        var watch = new WatchDefinition(0x80000000, Offsets: [4]);
        var sample = await service.SampleWatchesAsync([(Guid.NewGuid(), watch)]);
        backend.Pointer = 0x80000040; // The service must resolve afresh, not use the display's address.
        await service.WriteWatchAsync(watch, "42", sample.Generation);
        Assert.Equal(0x80000044u, backend.Writes.Single().Address);
        Assert.Equal(new byte[] { 0, 0, 0, 42 }, backend.Writes.Single().Bytes);
        Assert.All(service.StateMarkers, m => Assert.False(m.Valid));
        Assert.NotEqual(sample.Generation, service.SampleGeneration);
        backend.Pointer = 0x80000080;
        await service.StepAsync(); await service.SeekAsync(0);
        Assert.Equal(2, backend.Writes.Count);
        Assert.All(backend.Writes, w => Assert.Equal(0x80000044u, w.Address));
        await service.UndoAsync(); await service.SeekAsync(0);
        Assert.Equal(new byte[4], await service.ReadMemoryAsync(0x80000044, 4));
    }

    [Fact]
    public async Task InvalidInputStaleEditsAndBadPointersNeverWrite()
    {
        using var files = new TestWorkspace(); var backend = new PointerBackend(); using var service = new ExecutionService(backend);
        await service.LoadGameAsync(files.GamePath, files.Options); await service.NewProjectAsync();
        var watch = new WatchDefinition(0x80000000, Offsets: [4]);
        var generation = service.SampleGeneration; var revision = service.Revision;
        await Assert.ThrowsAsync<FormatException>(() => service.WriteWatchAsync(watch, "4294967296", generation));
        Assert.Equal(revision, service.Revision);
        await service.StepAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.WriteWatchAsync(watch, "42", generation));
        backend.Pointer = 0;
        await Assert.ThrowsAsync<InvalidDataException>(() => service.WriteWatchAsync(watch, "42", service.SampleGeneration));
        Assert.Empty(backend.Writes);
    }

    private sealed class PointerBackend : FakeBackend, IEmulatorBackend
    {
        public uint Pointer { get; set; } = 0x80000020;
        public List<(uint Address, byte[] Bytes)> Writes { get; } = [];
        byte[] IEmulatorBackend.ReadMemory(uint address, int count)
        {
            if (address != 0x80000000) return ReadMemory(address, count);
            var bytes = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(bytes, Pointer); return bytes;
        }
        void IEmulatorBackend.WriteMemory(uint address, byte[] bytes)
        { Writes.Add((address, bytes.ToArray())); WriteMemory(address, bytes); }
    }
}
