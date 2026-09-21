namespace Skies;

/// <summary>A persistent inventory slot: signed BE16 ID, signed byte quantity, trailing raw byte.
/// This differs from the battle-drop table's two signed halfwords.</summary>
public sealed record OwnedInventorySlot(short ItemId, sbyte Quantity, byte RawExtra)
{
    public bool IsEmpty => ItemId == -1;
    public string ItemName => ItemNames.GetName(ItemId);
}

/// <summary>A frozen copy of one persistent inventory category. Battle rewards may not yet
/// have been transferred to this inventory when the game first enters Victory.</summary>
public sealed class OwnedInventorySnapshot
{
    public IReadOnlyList<OwnedInventorySlot> Slots { get; }
    internal OwnedInventorySnapshot(IEnumerable<OwnedInventorySlot> slots) => Slots = Array.AsReadOnly(slots.ToArray());
    public int CountItem(int itemId) => Slots.Where(s => !s.IsEmpty && s.ItemId == itemId).Sum(s => (int)s.Quantity);
    public int CountItem(UsableItem item) => CountItem((int)item);
    public int CountItem(ShipItem item) => CountItem((int)item);
}
