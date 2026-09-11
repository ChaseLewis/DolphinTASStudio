# Memory watcher design

## Implemented iteration — September 11, 2026

The native app now has a compact grouped Name / Value watcher. Double-click a
value to pause and edit in place with type/range validation; Enter applies and
Escape or clicking away cancels. Writes use recorded memory events and resolve
pointers at the paused execution boundary. Types remain in tooltips/settings.
Double-click a name to edit its definition, double-click blank space to add,
use Insert to add, F2 for definitions, or Enter for values, plus a context menu.
Groups expand using their chevron; double-clicking a group edits its label.
The editor is a modal with label, preview, address, value type, byte length where
needed, and optional pointer offsets with resolved addresses. There is no separate
display-format requirement or help paragraph. New integers use decimal; new byte
arrays use hexadecimal. Imported DMW display bases are preserved.

DMW import, bounded big-endian reads, coherent batches, automatic paused refresh,
and persisted definitions are implemented. Project-owned definitions are currently
an adjacent `<project>.tasproj.watches.json` file, also copied with local recovery;
they are independent of execution identity. Definition edits save immediately.
The source `.dmw` is never overwritten. Moving a project should include this sidecar.
See [verification and current limits](../watcher-verification.md).

The rest of this document is the original broader design proposal. Its selected-row
editor and mandatory display control have been superseded by the compact dialog.
Freezes, reordering, multi-select and manifest-referenced
watch assets remain future work. The accompanying `memory-watcher.html` is the
earlier mock with illustrative values; it does not access memory.

## Layout and editing

Keep the watcher in the editor's optional top-right dock. Give it a resizable
grouped table, rather than a stack of formatted output labels. At narrow dock
widths, show Name, Value and Type; make Address an optional column and keep the
complete address/pointer chain in the selected-row editor. In an expanded view,
show all four columns. Long names wrap or expose their full text in the editor.

Toolbar actions are Import .dmw, Add watch, Add group and Refresh. Refresh after
step is enabled by default; sampled state and stale/unavailable state remain
visible. Collapsible nested groups preserve imported order. Group menus offer
rename, move, duplicate and remove; remove changes watch definitions only.
Support multi-selection and keyboard editing without requiring drag-and-drop.

Selecting a row opens an editor below the table: name, value type, numeric
display, length where applicable, base address, and ordered pointer offsets.
Show signedness explicitly as s8/u8, s16/u16 and s32/u32 rather than hiding it in
a checkbox. Float32/Float64, bounded strings and byte arrays have distinct types.
Decimal/hexadecimal/binary/octal are display choices, not memory interpretations.
Preserve exact raw bytes for formatting and writes; JavaScript-style floating
point must not be used to round 64-bit integers in a future implementation.

The editor shows the original pointer expression and a resolved address after a
sample. Let each pointer hop expand to show read address, pointer value, offset
and result. A bad pointer says which hop failed; do not replace failure with zero
or display an old value as current. Persist definition changes with the project;
renaming, regrouping or changing display format does not invalidate TAS states.

The mock lets reviewers collapse groups, select/edit definitions, add rows,
inspect import review, and preview a recorded write. Optional design controls
compare compact row density and address-column visibility. Values stay clearly
labelled illustrative. Full editing shortcuts, drag/reorder and live reads are
outside this mock.

## What the supplied file actually contains

A locally supplied `SOAStuff.dmw` sample (not included) was read without modification. It
is 213,572 bytes of JSON, with `structDefs` and `watchList` at the root.

| Observed content | Count |
| --- | ---: |
| Watches | 590 |
| Nested groups | 46 |
| Direct addresses | 333 |
| One-offset pointers | 134 |
| Two-offset pointers | 123 |
| Byte / halfword / word / float watches | 135 / 259 / 101 / 9 |
| String / byte-array watches | 30 / 56 |
| Decimal / hexadecimal / binary display | 505 / 70 / 15 |

`structDefs.rootNode` is empty. The supplied file uses no double, structure,
instruction or typed-array watches. Group names include Player Character Data,
Inventories, Encounters, Battle State and PAL files stuff. Keep PAL-labelled
watches; labels alone do not establish which game revision an address supports.
The import summary must distinguish parsing definitions from validating their
addresses or values against the loaded ROM.

## .dmw compatibility

Use a versioned importer rather than treating `.dmw` as Dolphin's separate
Locations.txt format. The source JSON has no explicit format version. Retain its
original bytes or a lossless representation with source identity, unknown
fields and import diagnostics. Importing creates project-owned definitions and
does not modify the user's original file.

| DMW field | Studio interpretation |
| --- | --- |
| `watchList` | Ordered collection of groups and watches |
| `groupName`, `groupEntries` | Recursive named group and ordered children |
| `label` | Editable display name; never a unique identifier |
| `address` | Hexadecimal guest base address, not a host process pointer |
| `typeIndex` 0 / 1 / 2 | 8 / 16 / 32-bit integer |
| `typeIndex` 3 / 4 | IEEE Float32 / Float64 |
| `typeIndex` 5 / 6 | String / byte array, using `length` |
| `unsigned` | Integer signedness |
| `baseIndex` 0 / 1 / 2 / 3 | Decimal / hexadecimal / octal / binary display |
| `length` | String/byte-array byte count; source default is 1 when omitted |
| `pointerOffsets` | Ordered signed hexadecimal offsets, one dereference per offset |
| `structDefs` and unknown fields | Preserve; unsupported constructs import disabled with a diagnostic |

These mappings were checked against the upstream enums and JSON reader at
revision `b776fab4f9261b346493f3d629488e14c101addd`, rather than guessed from the
display names. Newer upstream types include structures, PowerPC instructions,
64-bit integers and typed arrays. Initial import support must not silently
reinterpret those types as 32-bit numbers. String decoding/termination must be
tested against DME before promising exact round-trip formatting; preserve raw
bytes and length even when textual decoding fails.

For a base B and offsets O0…On, each hop is `A = ReadU32BigEndian(A) + Oi`,
starting from A = B. The value is read at the final A. The supplied Enemy 1
Current HP is base `80309710`, offsets `88`, `14`, a signed 32-bit value:
`ReadS32BE(ReadU32BE(ReadU32BE(0x80309710) + 0x88) + 0x14)`.
Validate every dereference, addition and final width against supported guest
memory. Do not dereference on the UI thread or use the desktop process address
space. Bound recursion, file size, entry count, string lengths and pointer depth
before allocating. Malformed entries get per-entry diagnostics without silently
dropping otherwise valid siblings; cancel leaves the current collection intact.

Initial support targets the existing GameCube MEM1 backend. MEM2, ARAM and MMIO
entries are preserved as unavailable until backend capability exists. Do not
translate them into MEM1 or call an unsupported read “zero.” Import does not
activate freezes or writes, even if a later source file contains such metadata.

Upstream sources:

- [Memory type and base enums](https://github.com/aldelaro5/dolphin-memory-engine/blob/b776fab4f9261b346493f3d629488e14c101addd/Source/Common/MemoryCommon.h)
- [Watch JSON and pointer handling](https://github.com/aldelaro5/dolphin-memory-engine/blob/b776fab4f9261b346493f3d629488e14c101addd/Source/MemoryWatch/MemWatchEntry.cpp)
- [Grouped watch JSON](https://github.com/aldelaro5/dolphin-memory-engine/blob/b776fab4f9261b346493f3d629488e14c101addd/Source/MemoryWatch/MemWatchTreeNode.cpp)

## Coherent sampling

The current watcher is `src/TasStudio.App/MainWindow.Watcher.cs`: a transient
list of direct addresses, u8/u16/u32/float formatting, and explicit paused
refresh. It lacks persistent names, groups, editing, pointer resolution and
DMW import. Its ordered service reads should become one batch request that
resolves all pointers and values at one safe boundary.

Return an immutable sample with project execution identity, state index,
sample generation, each resolved address, bytes and status. Discard late results
after a seek, project switch, profile change or definition edit. No two samples
may claim the same current-state label while mixing different boundaries.

When paused, refresh after a completed step/seek/restore. During playback, a
bounded optional display cadence may sample the latest coherent boundary;
coalesce pending requests so 590 watches cannot build an unbounded queue.
Read-only sampling must not choose inputs or advance the core. Refresh visible
expanded rows first, with any pinned watches included. Hidden/offline values
retain their sampled-state label rather than pretending they are live.

Highlight changed values briefly and show an explicit stale marker if the
preview is an audition or no longer matches active history. Clicking a timeline
input still only selects it: watcher values describe the actual game preview,
not the selected input. Seeking is the explicit action that aligns them.

## Deterministic writes

Watch-definition editing and emulated-memory writing are separate actions.
“Write value…” pauses at a current valid boundary, validates type/range and
resolves the pointer there. Show the resulting address, exact bytes, state index
and affected history before applying. Submit through the execution service as an
ordered memory-write event before input N, not a background UI write.

Record the resolved guest address and bytes for deterministic replay, plus the
original watch ID/expression as provenance. Replaying the event uses its recorded
destination rather than resolving a potentially changed pointer again. Memory
events at N invalidate dependent states at N and later under existing boundary
rules. If the preview is stale, require a seek before enabling the write.

Do not copy DME's continuously rewritten Lock column into this first design. A
future freeze must be an explicit start/end event with defined execution-boundary
semantics, not a timer whose behavior depends on UI speed. RAM search, a hex
editor and structure editing remain later tools; this design focuses on watches.

## Persistence and acceptance

Store a versioned `watches` asset referenced by the project manifest, with stable
IDs, group order, definitions and import provenance. Keep live samples and local
dock sizing outside canonical execution identity. Preserve unsupported imported
definitions so future support can activate them. A later DMW export must label
unsupported Studio-only features rather than silently discard them.

Before implementation is called complete:

1. Import this fixture with exactly 590 watches and 46 groups, retaining the
   original order, names, types, bases, lengths, signedness and all 257 pointers.
2. Verify integer/float endianness and each pointer hop against fixed byte
   fixtures, including null, out-of-range, signed offset and overflow cases.
3. Sample all displayed rows at one boundary; prove seeks and profile changes
   cannot publish stale samples as current. Exercise a 590-row list without
   blocking the UI or accumulating unbounded requests.
4. Round-trip watch definitions through project save/reopen and ensure ordinary
   definition edits leave checkpoints valid.
5. Replay a recorded write event to reproduce the destination bytes; confirm
   invalidation includes the event's boundary, with no implicit writes on import.
6. Review compact and expanded watcher layouts and keyboard editing. The current
   mock was generated and source-checked; browser rendering could not be checked
   in this agent because no browser surface was available.
