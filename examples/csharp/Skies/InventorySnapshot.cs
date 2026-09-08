using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Skies;

/// <summary>A four-byte game inventory entry. Properties decode big-endian fields;
/// the slot index is its position in the inventory array.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly struct InventorySlot
{
    // Populated by ReadPacked/MemoryMarshal rather than by a C# constructor.
#pragma warning disable CS0649
    private readonly short _rawQuantity;
    private readonly short _rawItemId;
#pragma warning restore CS0649

    public short Quantity => BitConverter.IsLittleEndian
        ? BinaryPrimitives.ReverseEndianness(_rawQuantity) : _rawQuantity;
    public short ItemId => BitConverter.IsLittleEndian
        ? BinaryPrimitives.ReverseEndianness(_rawItemId) : _rawItemId;

    public string ItemName => ItemId switch
    {
        (short)SkiesGame.Electri => "Electri",
        (short)SkiesGame.Moonberry => "Moonberry",
        _ => $"Item {ItemId}"
    };
}

/// <summary>An immutable copy of the scanner's eight-slot item table at read time.</summary>
public sealed class InventorySnapshot
{
    internal InventorySnapshot(IEnumerable<InventorySlot> slots) => Slots = Array.AsReadOnly(slots.ToArray());
    public IReadOnlyList<InventorySlot> Slots { get; }
    public int ElectriCount => CountItem(SkiesGame.Electri);
    public int MoonberryCount => CountItem(SkiesGame.Moonberry);
    public int CountItem(int itemId) => Slots.Where(slot => slot.ItemId == itemId).Sum(slot => (int)slot.Quantity);
}
