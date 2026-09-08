using System.Buffers.Binary;

namespace TasStudio.Sdk;

/// <summary>Typed big-endian reads and event-recorded writes for guest memory.</summary>
public static class MemoryExtensions
{
    public static async Task<byte> ReadU8Async(this IExperimentEmulator emulator, uint address) => (await emulator.ReadMemoryAsync(address, 1))[0];
    public static async Task<sbyte> ReadS8Async(this IExperimentEmulator emulator, uint address) => unchecked((sbyte)await emulator.ReadU8Async(address));
    public static async Task<ushort> ReadU16Async(this IExperimentEmulator emulator, uint address) => BinaryPrimitives.ReadUInt16BigEndian(await emulator.ReadMemoryAsync(address, 2));
    public static async Task<short> ReadS16Async(this IExperimentEmulator emulator, uint address) => BinaryPrimitives.ReadInt16BigEndian(await emulator.ReadMemoryAsync(address, 2));
    public static async Task<uint> ReadU32Async(this IExperimentEmulator emulator, uint address) => BinaryPrimitives.ReadUInt32BigEndian(await emulator.ReadMemoryAsync(address, 4));
    public static async Task<int> ReadS32Async(this IExperimentEmulator emulator, uint address) => BinaryPrimitives.ReadInt32BigEndian(await emulator.ReadMemoryAsync(address, 4));
    public static async Task<float> ReadF32Async(this IExperimentEmulator emulator, uint address) => BinaryPrimitives.ReadSingleBigEndian(await emulator.ReadMemoryAsync(address, 4));
    public static async Task<double> ReadF64Async(this IExperimentEmulator emulator, uint address) => BinaryPrimitives.ReadDoubleBigEndian(await emulator.ReadMemoryAsync(address, 8));
    public static Task WriteU8Async(this IExperimentEmulator emulator, uint address, byte value) => emulator.WriteMemoryAsync(address, [value]);
    public static Task WriteU16Async(this IExperimentEmulator emulator, uint address, ushort value)
    { var bytes = new byte[2]; BinaryPrimitives.WriteUInt16BigEndian(bytes, value); return emulator.WriteMemoryAsync(address, bytes); }
    public static Task WriteU32Async(this IExperimentEmulator emulator, uint address, uint value)
    { var bytes = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(bytes, value); return emulator.WriteMemoryAsync(address, bytes); }
}
