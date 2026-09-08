# Project home and creation

Implemented September 6, 2026.

The app opens on **Your projects**. New project, Open project, Verify projects and Open game are available without loading an emulator session. Opening or saving a project adds it to the recent list (up to 50, kept in the current app data directory). Existing projects enter the list when opened. Removing a recent entry does not delete any files.

Selecting a card shows the last saved frame, input count, candidate takes, starting point, project folder and verification result. The miniature timeline uses saved input/take spans. Double-click opens the project. Projects in the editor toolbar returns home after pausing and saving a recovery backup; Resume editor restores the same session and floating panels. Home does not accept gameplay shortcuts.

## New project

Choose a name, parent location, game image and starting point. Creation writes `<location>/<name>/<name>.tasproj` immediately, together with its immutable baseline and timeline assets. An existing destination folder is rejected by the wizard.

- **Power on:** uses a fresh writable Dolphin profile and the selected UTC (default `2000-01-01 00:00:00`). Single-core execution and existing compatibility policy remain enforced.
- **Save state:** accepts a TAS Studio `.tasstate`, validates its checksum, ROM, pinned emulator build and effective configuration, then copies the captured state into the new project. It inherits the source settings/UTC. Input 0 starts at that captured state; source inputs are not imported. The original file is not modified or needed to reopen the project. Raw standalone Dolphin save states are not supported.

The starting point is recorded in optional `Environment.Start` metadata: `Kind` (`CapturedState = 0`, `PowerOn = 1`, `SaveState = 2`), source filename and source position. Older projects without this field are labeled “Captured state”, never assumed to be a power-on run. This additive field does not change the folder format version.

Creation validates before replacing the live session and rolls it back if native loading, state restore or project saving fails. As with other folder saves, an interrupted/failed save can leave unreferenced assets; it does not replace an existing manifest. A retry after a partially created directory may require a different destination.

Project configuration stays editable. Changing emulation settings/UTC preserves inputs and restarts from **Power on**, discarding the old state's role as a baseline and invalidating downstream states. A state captured with different settings cannot be reused under the new configuration.

## Verification and banners

Verify checks project assets and their hashes, referenced ROM identity, emulator build identity and an existing watcher sidecar. It runs without booting emulation, can be canceled, and hashes shared ROM paths once per pass. Results are session-local and are cleared after saving/opening a changed project. Runtime compatibility resources are additionally checked when opening. “Verified” here is file/build verification, not replay execution or a pixel-determinism claim.

The home page reads `opening.bnr` (BNR1/BNR2 tiled RGB5A3, 96×32) directly from uncompressed GameCube ISO/GCM files and caches PNGs under the app's `Banners` directory. Unsupported/compressed image formats and games without a readable banner use a placeholder. ROM files are read-only. Banner extraction does not run the emulator.

## Validation

- Creation tests cover power-on UTC, state rebasing and inherited settings, reopen after deleting the source state, settings changes preserving inputs, ROM mismatch, existing manifest protection, native load failure and disk-write failure rollback.
- Verification tests cover asset corruption, ROM relocation/mismatch/missing files, build mismatch and no backend calls.
- Headless Avalonia/Skia checks cover home cards, both wizard modes, invalid UTC/destination validation, compact editor regression checks, and hiding/restoring floating panels through home.
- Synthetic disc tests cover banner decoding and out-of-bounds file tables. The banner reader also succeeded against the local Skies of Arcadia Legends ISO.
- UI captures are under `artifacts/project-home/`. Creation transaction tests use the deterministic fake backend; this change has not added a new native Dolphin replay/pixel verification claim.
