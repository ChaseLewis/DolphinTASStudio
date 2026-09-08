# Skies read-only utilities

Reference `Skies.csproj` from an experiment, then create the reader in `RunAsync`
after the worker has booted. The bundled `Skies.Experiments` project demonstrates the reference; game-specific search experiments can build on the same readers.

```csharp
using Skies;

var skies = new SkiesGame(context.Emulator);
int seed = await skies.ReadRngSeedAsync();
BattleState battle = await skies.ReadBattleStateAsync();
ulong gameFrames = await skies.ReadFrameCounterAsync();
int electri = await skies.ReadElectriCountAsync();
int moonberries = await skies.ReadMoonberryCountAsync();

var inventory = await skies.ReadInventoryAsync();
for (var index = 0; index < inventory.Slots.Count; index++)
{
    var slot = inventory.Slots[index];
    context.Log(new { Index = index, slot.ItemName, slot.ItemId, slot.Quantity });
}
```

These port every game read in `scan_electri_seeds/src/skies/mod.rs`: signed
32-bit RNG seed, all eleven battle states (including TryAgain = 13), the game's
64-bit frame counter, and Electri/Moonberry counts. Unknown battle-state bytes
return `BattleState.Unknown`. The game counter is separate from Studio's
frame-group, video-field and input-poll counters.

`InventorySnapshot` copies the original scanner's eight-slot table, preserving
signed 16-bit item IDs and quantities and summing repeated items. It is not a
catalog of every inventory category in the game. Only Electri and Moonberry have
known names here; other IDs are displayed numerically. A snapshot never updates
itself; read again after advancing. Separate reads across a running emulator are
not an atomic snapshot of all game data.

## SoAManips encounter and battle readers

Read-only components ported from the original SoAManips battle reader are
also available directly on `SkiesGame`. That separate project is not required to build this library:

```csharp
var encounter = await skies.ReadCurrentEncounterAsync();
if (encounter is not null)
{
    context.Log(encounter.EncounterId); // Area.FileId, Area.SubId, StageId, EncounterId
    for (var slot = 0; slot < encounter.Enemies.Count; slot++)
    {
        var enemy = encounter.Enemies[slot];
        context.Log(new { Slot = slot, enemy.Hp, enemy.MaxHp, enemy.IsDefeated });
    }
}

var area = await skies.ReadAreaAsync();
var partySize = await skies.ReadBattlePartySizeAsync();
var character = await skies.ReadCharacterBattleStatsAsync(0);
if (character is { } stats)
    context.Log(new { stats.Quick, stats.Hit });

var ship = await skies.ReadCurrentShipBattleAsync();
if (ship is not null)
    context.Log(new { ship.ShipName, ship.MaxHp, ship.EnemyHp });
```

- Enemies: up to eight consecutive slots; `ReadEnemyStatsAsync(slot)` reads one.
  `EnemyStats` is a packed eight-byte HP/max-HP block with private raw fields.
  `ReadCurrentEncounterAsync()` requires a plausible first party pointer and
  stops at the first invalid enemy pointer or HP pair, matching SoAManips.
  HP must be 0..MaxHp, and MaxHp must be 1..50000. Zero HP represents a defeated
  enemy. No candidate returns null. Read failures still propagate.
- Identity: `ReadEncounterIdentifierAsync`, `ReadAreaIdAsync`, and
  `ReadAreaSubIdAsync`. `IsSameEncounter(other)` compares area, stage/encounter
  IDs, enemy count and ordered maximum HP; damage does not change that identity.
  The source has no enemy species/name table, so enemies are identified by their
  slot and stats within the encounter.
- Party: four slots, with null for absent/implausible pointers. `HasBattleCharacterAsync`,
  `ReadCharacterBattleStatsPointerAsync`, `ReadBattleQuickAsync`, and
  `ReadBattleHitAsync` are also available. A plausible pointer is necessary,
  but does not by itself prove a live battle. Only known quick/hit fields are
  modeled; party HP offsets were not provided by this source.
- Ships: `ReadShipNameAsync`, `ReadShipMaxHpAsync`, `ReadEnemyShipHpAsync`, and
  `ReadShipCannonHitAsync(slot)` follow the original nested pointer chains.
  `IsInShipBattleAsync`/`ReadCurrentShipBattleAsync` require Little Jack or
  Delphinus, player max HP >= 10000, and positive enemy HP. Invalid memory chains
  return false/null for these detection methods; direct reads report errors.
  Cancellation is never swallowed.
- Signed chance/modifier reads: `ReadFirstStrikeChanceAsync`,
  `ReadBackAttackChanceAsync`, `ReadEscapeChanceAsync`, `ReadEncounterModifierAsync`.

These are snapshots, not the SoAManips manipulation state machine. That state
machine additionally requires four consecutive matching full-health encounter
observations before entry and prioritizes ship-battle detection. Use
`IsAtFullHealth` and `IsSameEncounter` when implementing that confirmation in an
experiment. Do not treat a single plausible snapshot as proof of a new battle.
Memory is reused outside combat, and separate reads are not a global atomic
snapshot. The utility library does not advance emulation or write RNG/stats.

## Reusable memory wrapper

Use `skies.Memory`, or construct `new GameCubeMemoryReader(context.Emulator)`
independently. Typed helpers handle big-endian byte/sbyte, signed and unsigned
16/32/64-bit integers, float/double, strings, byte arrays and Int16 arrays.
`SkiesAddresses` holds named addresses so components need no scattered literals.

```csharp
// Direct address: no dereference.
int seed = await skies.Memory.ReadInt32Async(SkiesAddresses.RngSeed);

// Rust read_i16(INVENTORY_POINTER, Some(&[0x10])) equivalent:
short firstItem = await skies.Memory.ReadInt16Async(
    SkiesAddresses.InventoryPointer, 0x10);
short firstQuantity = await skies.Memory.ReadInt16Async(
    SkiesAddresses.InventoryPointer, 0x0e);

// General pointer-chain API (root/offsets supplied by your component):
uint valueAddress = await skies.Memory.ResolveAddressAsync(root, firstOffset, secondOffset);
short value = await skies.Memory.ReadInt16Async(root, firstOffset, secondOffset);
```

Each offset means dereference the current address, then add that offset.
The last result is the value address, not another pointer. Use an offset of zero
to dereference without adding anything; no offsets means a direct read.
Physical, cached and uncached GameCube MEM1 aliases are accepted. Null pointers,
out-of-MEM1 ranges and incomplete reads fail explicitly rather than masquerading
as a zero item count. MEM2 and process launching are outside this wrapper's scope.
`ReadStringAsync` takes a maximum byte count and optional encoding (UTF-8 by
default), stopping at the first zero byte.

## Packed structs

`ReadInventoryAsync` uses `ReadPackedArray<InventorySlot>` to read all eight
entries directly. `InventorySlot` is a four-byte packed struct with `ItemId` and
`Quantity` properties that decode big-endian fields, plus a computed `ItemName`.
The slot index is its array position; computed properties add no stored bytes.

`ReadPacked<T>` and `ReadPackedArray<T>` return tasks; await them like the other
readers. `ReadPackedAsync<T>` and `ReadPackedArrayAsync<T>` are equivalent names.
Both accept the same optional pointer-chain offsets. The array count is a number
of elements, and each element occupies `sizeof(T)` bytes in one contiguous read.

```csharp
using System.Buffers.Binary;
using System.Runtime.InteropServices;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct PackedItem
{
    private short RawQuantity;
    private short RawItemId;

    public readonly short Quantity => BitConverter.IsLittleEndian
        ? BinaryPrimitives.ReverseEndianness(RawQuantity) : RawQuantity;
    public readonly short ItemId => BitConverter.IsLittleEndian
        ? BinaryPrimitives.ReverseEndianness(RawItemId) : RawItemId;
}

// Inside RunAsync: dereference the inventory pointer, then add 0x0e.
var first = await skies.Memory.ReadPacked<PackedItem>(SkiesAddresses.InventoryPointer, 0x0e);
var items = await skies.Memory.ReadPackedArray<PackedItem>(SkiesAddresses.InventoryPointer, 8, 0x0e);
context.Log(new { first.ItemId, first.Quantity });
```

These are **raw copies without endian conversion**. The GameCube stores numbers
big-endian; multi-byte fields need conversion as shown above. The regular typed
readers still convert big-endian values automatically.

`T` must be unmanaged (no strings, managed arrays, or object references). Use
`Sequential, Pack = 1` for packed fields, or explicit `FieldOffset`/`Size` for a
known layout. Nested structs must also have the intended layout. These methods
preserve the actual managed memory layout, including declared padding; they do
not apply marshalling attributes or automatically remove padding. Prefer bytes
for game flags and uint for GameCube pointers, rather than bool or host pointers.
Returned structs and arrays are independent copies, so editing them does not
write to the game. Invalid lengths, incomplete reads, and invalid pointers fail
the same way as other memory reads.

The library exposes no memory writes, playback, boot, or process control. Scripts
continue to control their own worker with `context.Emulator`. `ReadRngAsync`
remains as an unsigned seed-bits convenience; `ReadRngSeedAsync` matches Rust's
signed return type. The former `SetRngAsync` and `PlayUntilAsync` helpers have been
removed from this read-only library.

Addresses target the ROM revision used by SkiesScanner; they are not automatically
translated for other game revisions. The reusable wrapper takes a read delegate
as an alternative constructor for testing components against memory fixtures.
