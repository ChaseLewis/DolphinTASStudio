using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using TasStudio.Sdk;

namespace Skies;

/// <summary>Read-only MEM1 access with typed big-endian reads, raw packed reads, and pointer offsets.</summary>
public sealed class GameCubeMemoryReader
{
    private readonly Func<uint, int, Task<byte[]>> read;
    public GameCubeMemoryReader(IExperimentEmulator emulator)
    {
        ArgumentNullException.ThrowIfNull(emulator);
        read = emulator.ReadMemoryAsync;
    }
    public GameCubeMemoryReader(Func<uint, int, Task<byte[]>> readMemory) =>
        read = readMemory ?? throw new ArgumentNullException(nameof(readMemory));

    /// <summary>No offsets means a direct address. Each offset dereferences the current
    /// address, then adds the offset; the final result is the value's address.</summary>
    public async Task<uint> ResolveAddressAsync(uint address, params uint[] offsets)
    {
        ArgumentNullException.ThrowIfNull(offsets);
        var current = Normalize(address, 1);
        foreach (var offset in offsets)
        {
            var pointer = await ReadUInt32Async(current);
            if (pointer == 0) throw new InvalidDataException($"Null pointer at 0x{current:X8}.");
            var targetOffset = (ulong)(Normalize(pointer, 1) - 0x80000000) + offset;
            if (targetOffset >= 0x01800000)
                throw new InvalidDataException($"Pointer 0x{pointer:X8} + offset 0x{offset:X} is outside GameCube MEM1.");
            current = 0x80000000 | (uint)targetOffset;
        }
        return current;
    }

    public async Task<byte[]> ReadBytesAsync(uint address, int count, params uint[] offsets)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        var resolved = Normalize(await ResolveAddressAsync(address, offsets), count);
        var bytes = await read(resolved, count);
        if (bytes.Length != count)
            throw new InvalidDataException($"Read at 0x{resolved:X8} returned {bytes.Length} bytes; expected {count}.");
        return bytes;
    }

    public async Task<byte> ReadByteAsync(uint address, params uint[] offsets) => (await ReadBytesAsync(address, 1, offsets))[0];
    /// <summary>Copies raw bytes into T's managed memory layout, without endian conversion or
    /// marshalling. Use an unmanaged struct with explicit layout or Sequential, Pack = 1.</summary>
    public async Task<T> ReadPackedAsync<T>(uint address, params uint[] offsets) where T : unmanaged
    {
        var bytes = await ReadBytesAsync(address, Unsafe.SizeOf<T>(), offsets);
        return MemoryMarshal.Read<T>(bytes);
    }

    /// <summary>Copies count contiguous raw structs, with a stride of sizeof(T). No endian
    /// conversion or marshalling; the returned array is independent of emulator memory.</summary>
    public async Task<T[]> ReadPackedArrayAsync<T>(uint address, int count, params uint[] offsets) where T : unmanaged
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        var bytes = await ReadBytesAsync(address, checked(count * Unsafe.SizeOf<T>()), offsets);
        return MemoryMarshal.Cast<byte, T>(bytes).ToArray();
    }

    /// <summary>Alias for ReadPackedAsync; returns a task and must be awaited.</summary>
    public Task<T> ReadPacked<T>(uint address, params uint[] offsets) where T : unmanaged =>
        ReadPackedAsync<T>(address, offsets);

    /// <summary>Alias for ReadPackedArrayAsync; count is an element count, not a byte count.</summary>
    public Task<T[]> ReadPackedArray<T>(uint address, int count, params uint[] offsets) where T : unmanaged =>
        ReadPackedArrayAsync<T>(address, count, offsets);

    public async Task<sbyte> ReadSByteAsync(uint address, params uint[] offsets) => unchecked((sbyte)await ReadByteAsync(address, offsets));
    public async Task<short> ReadInt16Async(uint address, params uint[] offsets) => BinaryPrimitives.ReadInt16BigEndian(await ReadBytesAsync(address, 2, offsets));
    public async Task<ushort> ReadUInt16Async(uint address, params uint[] offsets) => BinaryPrimitives.ReadUInt16BigEndian(await ReadBytesAsync(address, 2, offsets));
    public async Task<int> ReadInt32Async(uint address, params uint[] offsets) => BinaryPrimitives.ReadInt32BigEndian(await ReadBytesAsync(address, 4, offsets));
    public async Task<uint> ReadUInt32Async(uint address, params uint[] offsets) => BinaryPrimitives.ReadUInt32BigEndian(await ReadBytesAsync(address, 4, offsets));
    public async Task<long> ReadInt64Async(uint address, params uint[] offsets) => BinaryPrimitives.ReadInt64BigEndian(await ReadBytesAsync(address, 8, offsets));
    public async Task<ulong> ReadUInt64Async(uint address, params uint[] offsets) => BinaryPrimitives.ReadUInt64BigEndian(await ReadBytesAsync(address, 8, offsets));
    public async Task<float> ReadSingleAsync(uint address, params uint[] offsets) => BinaryPrimitives.ReadSingleBigEndian(await ReadBytesAsync(address, 4, offsets));
    public async Task<double> ReadDoubleAsync(uint address, params uint[] offsets) => BinaryPrimitives.ReadDoubleBigEndian(await ReadBytesAsync(address, 8, offsets));
    public async Task<short[]> ReadInt16ArrayAsync(uint address, int count, params uint[] offsets)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        var bytes = await ReadBytesAsync(address, checked(count * 2), offsets);
        var values = new short[count];
        for (var i = 0; i < count; i++)
            values[i] = BinaryPrimitives.ReadInt16BigEndian(bytes.AsSpan(i * 2));
        return values;
    }
    /// <summary>Decodes up to the first zero byte, with UTF-8 unless an encoding is supplied.</summary>
    public async Task<string> ReadStringAsync(uint address, int byteCount, Encoding? encoding = null, params uint[] offsets)
    {
        var bytes = await ReadBytesAsync(address, byteCount, offsets);
        var end = Array.IndexOf(bytes, (byte)0);
        return (encoding ?? Encoding.UTF8).GetString(bytes, 0, end < 0 ? bytes.Length : end);
    }

    private static uint Normalize(uint address, int count)
    {
        // Accept physical, cached and uncached MEM1 aliases; send cached guest addresses to Studio.
        var region = address & 0xFE000000;
        var offset = address & 0x01FFFFFF;
        if (region is not (0 or 0x80000000 or 0xC0000000) || offset >= 0x01800000 ||
            (ulong)offset + (uint)count > 0x01800000)
            throw new InvalidDataException($"Address range 0x{address:X8} + {count} is outside GameCube MEM1.");
        return 0x80000000 | offset;
    }
}
