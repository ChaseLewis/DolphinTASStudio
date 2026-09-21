# Skies read-only utilities

## Enemies by name

Name-based lookup uses the live enemy ID and the GameCube US exported name table,
not HP, initiative order, or a guessed slot:

```csharp
int? enemyIndex = await skies.GetEnemyIndexAsync("Antonio"); // 0–7, null if absent
var antonio = await skies.ReadEnemyStatusAsync("Antonio");
bool asleep = antonio?.HasAll(BattleStatus.Sleep) == true;

// Repeated names: all matching indices, or an explicit zero-based occurrence.
var soldiers = await skies.GetEnemyIndicesAsync("Soldier");
var secondSoldier = await skies.ReadEnemyStatusAsync("Soldier", occurrence: 1);
var livingSoldiers = await skies.GetEnemyIndicesAsync("Soldier", aliveOnly: true);
```

Matching ignores case and trims surrounding whitespace, but otherwise requires
the exact exported name. A unique lookup (`GetEnemyIndexAsync` or the name-only
status overload) returns null for no match and throws `InvalidOperationException`
for multiple matches. An explicitly requested occurrence returns null if absent;
negative occurrences throw. Matches are ordered by enemy index and `aliveOnly`
filters before selecting an occurrence. Defeated but still-present enemies are
included by default. `aliveOnly` means HP > 0, not that the actor can act.

`ReadEnemyIdentitiesAsync()` exposes each enemy's index, actor slot, species ID,
name, and HP. It handles holes in the slot array; invalid IDs -1/255, absent stats
pointers and implausible HP are skipped. Unknown IDs retain their number and a
null name and cannot accidentally match a known name. The built-in 245-ID
`EnemyNames` table comes from SOARandomizer's US `enemy.csv`; it needs no CSV at
runtime and does not reflect custom names in modified game data.

Lookup is refreshed on each call. Use a known live encounter and keep emulation
paused across lookup and status reads so the slot cannot change between them.
These helpers preserve existing numeric overloads; async operations retain the
library's `Async` naming convention.

Provenance: SkiesDecomp `tools/battle_trace/battle_encounter_init.py` writes the
signed BE16 enemy identity to `0x80309DCC + actorSlot*2` and the stats pointer to
`0x80309DE4 + actorSlot*4`. Enemy actor slots are 4–11. Tests cover identity-vs-HP
matching, duplicate names, dead/missing slots and memory errors; live validation
of name-based lookup is still pending.

## Named items and inventory queries

`UsableItem` and `ShipItem` are enums of **global item IDs** from SOARandomizer's
GameCube US `usableitem.csv` and `shipitem.csv` exports (80 and 30 entries).
All 16 boxes are named, including `ElectriBox`, `ElectrumBox`, `SliparaBox`,
`SylenisBox`, and `PanikaBox`. The three dummy usable entries are named
`Reserved302`, `Reserved303`, and `Reserved304` rather than given invented uses.
Ship items include consumables such as wax and repair kits; ship cannons and
accessories have separate game categories and are not included in this enum.

```csharp
// Items already carried in the persistent inventory:
int berries = await skies.CountOwnedItemAsync(UsableItem.Moonberry);
int valor = await skies.CountOwnedItemAsync(UsableItem.AuraOfValor);
int apa = await skies.CountOwnedItemAsync(ShipItem.ApaWax);
int apo = await skies.CountOwnedItemAsync(ShipItem.ApoWax);

// Copy once when querying several items at the same observation:
var bag = await skies.ReadOwnedUsableItemsAsync();
int electri = bag.CountItem(UsableItem.ElectriBox);
int slipara = bag.CountItem(UsableItem.SliparaBox);
var shipBag = await skies.ReadOwnedShipItemsAsync();
int repairs = shipBag.CountItem(ShipItem.RepairKit);

// Existing search behavior: count rewards recorded in this battle's drop table.
int droppedBerries = await skies.CountItemAsync(UsableItem.Moonberry);
var drops = await skies.ReadInventoryAsync();
int droppedBoxes = drops.CountItem(UsableItem.ElectriBox);
```

`CountItemAsync` / `ReadInventoryAsync` retain the original scanner's **eight-slot
battle-drop table** behavior. They do not report everything the player owns.
`CountOwnedItemAsync`, `ReadOwnedUsableItemsAsync`, and `ReadOwnedShipItemsAsync`
read persistent inventory instead. Rewards may not yet be transferred into owned
inventory on first entering Victory; use the battle-drop table for immediate
drop checks. These are snapshots, not subscriptions. Read while emulation is
paused when several observations must describe the same instant.

| Important item | Global ID |
|---|---:|
| `UsableItem.AuraOfValor` | 246 |
| `UsableItem.Moonberry` | 258 |
| `UsableItem.ElectriBox` | 273 |
| `UsableItem.SliparaBox` | 281 |
| `ShipItem.ApaWax` | 497 |
| `ShipItem.ApoWax` | 498 |

`ItemNames.GetName(id)` and each slot's `ItemName` use exported display names,
including the full name **Electri Box** (previously displayed as Electri).
Unknown IDs retain `Item <number>`. Existing numeric APIs and the
`SkiesGame.Electri` / `SkiesGame.Moonberry` constants are retained.

Owned inventory provenance: SkiesDecomp `tools/battle_trace/battle_inventory.py`
recovers `801f4c24`'s categories and `801efdc0`'s insertion layout. Usable items
are at `0x8030BF08` (80 slots), ship items at `0x8030C188` (30 slots). Each
four-byte slot has a signed big-endian 16-bit ID, signed byte quantity, and a
trailing byte preserved as `RawExtra`. ID `-1` means empty. This layout differs
from the drop table's two 16-bit fields. Counts sum matching nonempty slots;
raw signed quantities are preserved, and failed memory reads propagate.
Tests use synthetic memory; the new owned-inventory readers still need live
validation. Enums/names contain only IDs and names, not item descriptions or
copied exporter implementation. They do not require the CSV at runtime.

## Active status effects

Read applied ailments for either side using the shared battle-actor stats layout:

```csharp
var aika = await skies.ReadCharacterStatusAsync(1); // party slot 1
var enemy = await skies.ReadEnemyStatusAsync(0);   // first enemy, actor slot 4
bool enemyAsleep = enemy?.HasAll(BattleStatus.Sleep) == true;
bool aikaDisabled = aika?.HasAny(BattleStatus.Sleep | BattleStatus.Stone) == true;
if (enemy is not null)
    context.Log(new { enemy.ActorSlot, enemy.Effects,
        enemy.RawActiveFlags, enemy.UnmappedActiveFlags, enemy.RawPersistentFlags });
```

`ReadBattleStatusAsync(actorSlot)` accepts the full actor range 0–11.
`ReadCharacterStatusAsync` accepts party slots 0–3; `ReadEnemyStatusAsync` accepts
enemy indices 0–7. Slots identify battle positions, not permanent character IDs.
An absent/implausible stats pointer returns **null**, which is different from a
present actor with `Effects == BattleStatus.None`. For search conditions requiring
a readable actor, handle null explicitly rather than treating it as healthy.
Invalid indices throw; memory failures propagate.

| Active ailment | Mask |
|---|---|
| Poison | `0x00000080` |
| Silence | `0x00000200` |
| Sleep | `0x00000400` |
| Confusion | `0x00000800` |
| Fatigue | `0x00001000` |
| Stone | `0x00004000` |
| Weak | `0x00080000` |

Effects can be combined. `HasAll` requires every requested bit; `HasAny` requires
at least one. As with conventional bit masks, `HasAll(None)` is true and
`HasAny(None)` is false. `Effects` contains only the named ailments above;
`UnmappedActiveFlags` preserves everything else, including internal state and
unmapped buffs/debuffs. No mapped ailments does not mean every possible effect
is absent. `RawPersistentFlags` is a separate word and is never merged into active
effects. Queued effect IDs (such as the watch file's sleep ID 9) are not these masks.

These immutable snapshots read active/persistent big-endian words together at
stats `+0x1C/+0x20`, after reading the actor pointer from `0x80309DE4 + slot*4`.
Keep emulation paused across the reads and use a known live encounter: pointer
plausibility alone does not prove the data belongs to a current battle. Read after
an effect resolves to determine whether it landed, and re-read after later actions
or round cleanup to detect removal.

Provenance (GameCube US GEAE8P-r0): SkiesDecomp
`docs/battle-resource-formats.md` documents the shared active/persistent layout.
The original executable's target-rule table at `0x802DFC28`, paired with
SOARandomizer `libs/alx/src/lookups.rs` target labels 13–19, tests these bits in
stats `+0x1C`: Stone at `0x800898CC`, Confusion `0x800897A4`, Silence
`0x8008967C`, Sleep `0x80089554`, Weak `0x8008942C`, Fatigue `0x80089304`,
Poison `0x800891DC`. These are `rlwinm.` single-bit tests with PPC bit indices
17, 20, 22, 21, 12, 19, 24 respectively (mask = `1u << (31 - index)`).
The poison mapping also agrees with `battle_coordinator.py`'s round-end check.
Reader tests cover synthetic memory, slot bounds, combined flags, missing actors,
and persistent/active separation; live status-effect validation remains pending.

## Queued enemy actions and turn order

`ReadBattlePlanAsync()` reads all 12 decision records and the adjacent prepared
action-order array. `ReadBattleDecisionAsync(actorSlot)` and
`ReadBattleActionOrderAsync()` provide individual reads. These use **actor slots**:
party 0–3, enemies 4–11 (first enemy = 4, not 0).

For a new round, observe the transition from command selection/initialization
into `BattleState.CameraTransition` (state 3), then read the plan **without
advancing emulation during the reads**. The recovered normal-round initializer
writes enemy decisions and initiative before entering state 3. State 3 also
occurs between later actions: the enum name alone does not identify a new round.
`PhaseChangedDuringRead` detects a phase change, not every possible race.

```csharp
// Load once, using an export from the same game data/revision as your movie.
var catalog = EnemyAiCatalog.Load(@"E:\Dev\SOARandomizer\roms\data_fresh\enemytask.csv");

// At the first state-3 observation for this round, with emulation paused:
var plan = await skies.ReadBattlePlanAsync();
if (plan.PhaseChangedDuringRead || plan.PhaseAfter != BattleState.CameraTransition
    || !plan.Order.IsWellFormed)
    throw new InvalidDataException("Expected a stable prepared round.");

foreach (var decision in plan.EnemyDecisions)
    context.Log(new { decision.ActorSlot, Action = catalog.Describe(decision),
        decision.RawCommand, decision.TargetSlot, decision.TargetRule,
        decision.AbilityId, decision.AttackVariant });
context.Log(new { Order = plan.Order.ActorSlots });

// Example policy for a validated Antonio encounter. The caller performs restoration
// and chooses the next cancel count; these readers never advance or write memory.
bool reject = plan.EnemyDecisions.Any(d =>
    d.Command == BattleCommand.SuperMove && d.AbilityId == 5); // Thunder of Fury
```

`BattleDecision` preserves the complete 32-byte record, unknown command IDs,
invalid target bytes, and signed targeting-rule/ability fields. `AbilityId` is
only populated for Magic/SuperMove; `AttackVariant` only for Attack. Raw slot
reads may contain stale decisions. `EnemyDecisions` filters by plausible actor
pointer presence, **not** eligibility or alive status. Guarding actors normally
have a decision but no action-order entry. The queue stops at `0xFF`; invalid or
duplicate slots set `IsWellFormed = false` and remain available in `RawBytes`.

These records describe **intentions**, not guaranteed execution. Preparation can
change targets/attack variants, and an actor may die or become unable to act.
Validate each boss and the sampling point before using a decision to prune a
search. There is no general boss AI predictor or automatic rejection loop here.

`EnemyAiCatalog` reads the labeled ALX/SOARandomizer `enemytask.csv` format,
including quoted CSV fields. `GetScript(enemyId, filter)` returns instructions
in entry order with their exported branch/action/target labels. Filter matching
is exact (`"*"` by default); encounter-specific variants are not merged.
`Describe(decision)` resolves enemy Magic/SuperMove names by command and ability
ID, falling back to numeric labels for missing or conflicting names. Labels such
as `Rating 10%` are exporter descriptions, not verified exact probabilities.
The CSV is caller-supplied; the library bundles no extracted game data.

Layout provenance: SkiesDecomp `tools/battle_trace/ai.c`,
`predict_enemy_round.py`, `battle_round.py`, and `order.c`; corroborated by
`SOAStuff.dmw`'s Battle Decisions watches. The recovered layout is for GameCube
US GEAE8P-r0: decisions at `0x80309174` (32 bytes/actor), order at `0x803092F4`
(12 bytes). The new readers have synthetic memory/CSV tests; they have not yet
been validated on a live boss battle.

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
catalog of every inventory category in the game. Usable and ship item IDs have
exported names; other IDs are displayed numerically. A snapshot never updates
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
  That encounter comparison uses slot and stats. For species IDs and exported
  names, use the separate `ReadEnemyIdentitiesAsync` and name-lookup helpers above.
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
