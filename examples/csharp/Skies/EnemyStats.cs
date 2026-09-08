using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Skies;

/// <summary>The eight-byte health block at enemy stats + 0x14, in guest byte order.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly struct EnemyStats
{
#pragma warning disable CS0649 // Populated by ReadPacked.
    private readonly int _rawHp;
    private readonly int _rawMaxHp;
#pragma warning restore CS0649

    public int Hp => BitConverter.IsLittleEndian ? BinaryPrimitives.ReverseEndianness(_rawHp) : _rawHp;
    public int MaxHp => BitConverter.IsLittleEndian ? BinaryPrimitives.ReverseEndianness(_rawMaxHp) : _rawMaxHp;
    public bool IsAtFullHealth => Hp == MaxHp;
    public bool IsDefeated => Hp == 0;
    public bool IsPlausible => MaxHp is > 0 and <= 50000 && Hp >= 0 && Hp <= MaxHp;
}
