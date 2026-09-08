# Memory watcher — implementation and verification

September 6, 2026.

## Behavior

- Compact Name / Value / Type list with collapsible nested groups. Addresses and
  full pointer expressions are in tooltips and the edit dialog.
- Double-click a watch to edit or empty list space to add. Double-click a group
  to rename it; use its chevron to expand. Insert adds, F2/Enter edits, Delete removes.
- Editor: label, read-only preview, address, type, optional byte length, pointer
  toggle and ordered signed hexadecimal offsets. Each resolved hop is shown.
  Changing definitions never writes emulated memory.
- Display defaults follow the type. Imported display bases remain intact without
  requiring a second format selector. Text uses bounded UTF-8 with explicit fallback
  for invalid bytes; this is not a promise of exact DME string rendering.
- Import `.dmw` into a new group. Original JSON is retained as provenance;
  unsupported entries remain visible with a diagnostic. The source is unchanged.
- One execution-thread command resolves all visible expanded watches at one boundary.
  Paused step/seek/restore changes trigger refresh while the panel is visible.
  Manual Refresh pauses first. Old samples retain their state label and become stale;
  late batches after execution/definition changes are discarded. Playback sampling
  is not enabled in this iteration. Each batch is capped at 1024 watches.

## Storage

Edits save atomically as JSON. For a saved project the file is
`<project path>.watches.json`; Save As copies definitions to the new project sidecar.
Unsaved/offline watches use `watches.json` beside app settings. Watch definitions
are also copied beside local recovery projects. Recovery detaches the copied
definitions from the original recovery source before subsequent edits.

These files are UI metadata, separate from checkpoint/history hashes. Existing
project manifests are unchanged; keep the sidecar with a project when moving it.
An unreadable watch file is preserved and blocks replacement rather than silently
overwriting definitions with an empty collection.

## Verification

- Release build and self-contained publish completed with zero warnings/errors;
  all 118 tests passed, including the supplied DMW fixture checks.
- The supplied SOAStuff.dmw imports 590 watches, 46 groups and 257 pointer chains
  with no unsupported entries. This verifies definitions, not ROM/address validity.
- Fixed byte fixtures verify big-endian pointer hops, negative offsets, null and
  out-of-range pointers, final-width validation, exact 64-bit integer formatting,
  floats, byte arrays and bounded text.
- A 590-watch batch uses one execution boundary/thread without advancing inputs,
  modifying memory, changing the project revision or invalidating its saved state.
  Advancing produces a new generation, making earlier samples stale.
- Avalonia headless mouse tests double-click a row, edit/save it, double-click blank
  space, add/save a second watch, and round-trip definitions through a project
  sidecar. Editor tests cover pointer toggle, offsets, length and validation.
- Skia captures reviewed: `artifacts/watchers/watcher.png` and
  `artifacts/watchers/watch-editor.png`. No live desktop interaction was needed.

Bounds: 8 MiB files, 10,000 definitions, 16 group/pointer levels, 4096 bytes per
string/array, and GameCube cached MEM1 only. Memory search, writing/freezing values,
DMW export and active-playback refresh remain outside this iteration.
