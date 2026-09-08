# Docking and local development — 2026-09-06

The workspace now uses Dock's Avalonia 11 packages, pinned to 11.3.20, with
Dock.Model.Mvvm 12.1.0.4. Game, Timeline, TAS Input and Memory Watcher can float,
split and form tab groups. The menu, transport and status remain window chrome.
Auto-hide is disabled in this iteration. Closing panels preserves their live views;
View reopens them. Reset Layout restores the initial arrangement.

Default: Game above Timeline on the left, full-height TAS Input on the right,
Memory Watcher closed. The input panel has no internal scrolling or explanatory
paragraph. Stick pads occupy equal-width columns, growing with available space
while preserving room for the other controls. Numeric arrows are 20 px wide and
numeric/trigger rows are 26 px high. The minimum main window is 1060 × 720.

Layout metadata is a versioned, explicitly typed JSON tree in `workspace.json`
beside app settings. It records splits, proportions, active tabs and floating
bounds. Unknown/duplicate panels and malformed trees are rejected; invalid layout
files fall back to defaults. Restored floating bounds are clamped to connected
screens. Layout changes do not affect project hashes, inputs or checkpoints.

## Verified

- Release build and 105 tests passed, including six layout serialization checks,
  three Avalonia headless integration checks using Skia, and two data-path checks.
- Full `just build` completed using built-in Windows PowerShell 5.1, including the
  native targets, managed build, tests and self-contained publish to `artifacts/prod`.
- At minimum window size, all six numeric controls fit inside the input panel;
  there is no editor scroll container. Both stick pads use equal square areas.
- Floating and tabbing transfer the same controls with their edited values intact.
  Closing/reopening and Reset Layout preserve those values. Widening a floating
  editor increases the stick area. Reset closes the old floating hosts.
- Floating layout survives main-window close and reopening a new application
  window. Layout is saved before floating windows are torn down.
- A native Windows drag detached TAS Input successfully. Further physical UI
  interaction was stopped by the user's Escape key. Re-docking was verified
  through the docking API in headless tests, not through a completed mouse drag.
- Both stable and unique development directories build under Windows PowerShell
  5.1 with `-NoLaunch`. The earlier launch recipe was also run successfully with
  built-in PowerShell before being split into the stable/unique recipes.

Headless visual capture: `artifacts/docking/minimum-layout.png`.
No new native-emulation determinism claim is made by these UI tests.

## Local workspace behavior

`just dev` uses `.local/dev/default`, builds under its `app` directory and reuses
that executable's running instance. Close the instance before rebuilding changes.
The existing `%LOCALAPPDATA%/TasStudio` data stays in use so existing recovery
files and settings remain accessible.

`just dev-unique` creates `.local/dev/<timestamp>-<id>` with an independent `data`
directory. A `tasstudio-data.path` marker beside the executable resolves to that
directory even when launched directly later. Projects remain at their selected
save paths; isolation does not copy a project opened from another workspace.
Neither recipe deletes or cleans an existing workspace.
