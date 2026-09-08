using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Skies;

/// <summary>The quick/hit region beginning at character stats + 0x88.</summary>
[StructLayout(LayoutKind.Explicit, Size = 0x14, Pack = 1)]
public readonly struct CharacterBattleStats
{
#pragma warning disable CS0649 // Populated by ReadPacked.
    [FieldOffset(0)] private readonly ushort _rawQuick;
    [FieldOffset(0x12)] private readonly ushort _rawHit;
#pragma warning restore CS0649

    public ushort Quick => BitConverter.IsLittleEndian ? BinaryPrimitives.ReverseEndianness(_rawQuick) : _rawQuick;
    public ushort Hit => BitConverter.IsLittleEndian ? BinaryPrimitives.ReverseEndianness(_rawHit) : _rawHit;
}
