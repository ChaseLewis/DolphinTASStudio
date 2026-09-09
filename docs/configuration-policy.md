# Editable project configuration and rendering repeatability

Decision date: September 6, 2026. This revision supersedes the initial read-only configuration inspector: settings must be editable after opening a project.

## Target and precedence

The target is repeatable gameplay and captures on a **fixed setup**: the same ROM, initial storage, input/event history, emulator and native host build, configuration, system resources, and graphics environment. Identical pixels across different PCs or GPUs are outside this target. A configuration hash is a compatibility check, not proof of repeatable rendering.

**Game compatibility overrides always take precedence over Studio defaults and the project's requested options.** Studio passes its choices at Dolphin's base layer and preserves the game's higher-priority compatibility layers. Disabling all graphics hacks is not a substitute for game-specific compatibility.

Single-core emulator execution is required and is not editable. If an override requires an unsupported execution mode, report the conflict and reject that boot; do not silently replace the override or run an unsupported mode. Effective runtime conflict detection remains a verification gate: a fixed frontend request alone does not prove enforcement after every compatibility layer. The UI and input workers remain separate threads, independent of Dolphin's CPU execution mode.

## Editing an open project

Configuration belongs to the project and is saved with it. The settings window offers editable General, Graphics, Compatibility, Audio, and Checkpoints pages. It displays requested choices, which compatibility overrides can supersede.

Applying an emulation change pauses execution, boots with the new configuration and isolated writable storage, and captures a new baseline. It retains the original input sequence and ordered execution events. Existing candidate takes remain available for inspection with their original provenance; a changed root must not make them falsely compatible. Existing state/checkpoint references are removed. State bytes are never relabeled to match the new configuration.

The user can optionally replay retained inputs to the selected input after Apply. This supports trying several start UTC values against the same movie or replaying at higher internal resolution for capture. A checkpoint-only change applies without rebooting or changing execution history. Cancel leaves the applied configuration unchanged; a failed configuration transition attempts to restore the previous session.

Start UTC is an initial clock seed, not an instruction to overwrite the clock after each restore. State zero is the supported core's post-boot boundary, not a claim of exact hardware power-on emulation. Retained execution events still execute at their recorded boundaries, including any explicit later RTC change.

## Public game compatibility settings

Dolphin publishes its per-game overrides in the
[GameSettings directory](https://github.com/dolphin-emu/dolphin/tree/master/Data/Sys/GameSettings).
Studio ships the [pinned Libretro copy](https://github.com/libretro/dolphin/tree/e1e6d25fa1392b7d1bc05bf800c71b807a2bd2e0/Data/Sys/GameSettings)
under `system/dolphin-emu/Sys/GameSettings`. Dolphin resolves general, game-prefix,
full game ID, and revision-specific INIs, with local `User/GameSettings` overrides
above bundled settings. The saved base `GFX.ini` alone does not show the effective
game settings.

For [Skies of Arcadia Legends (`GEA.ini`)](https://github.com/dolphin-emu/dolphin/blob/master/Data/Sys/GameSettings/GEA.ini),
the bundled overrides are `CPUThread = False`,
`SafeTextureCacheColorSamples = 512`, and `EFBToTextureEnable = False`.
The latter allows EFB copies to RAM instead of keeping them only as GPU textures.
These match the standalone Dolphin settings checked on September 9, 2026.

Game overrides cannot repair missing renderer capabilities. Patch
`0004-d3d-logic-ops.patch` restores the desktop Libretro D3D11 capability query
used by standalone Dolphin. Previously, a preprocessor branch left logic-operation
support unset even on a capable frontend device, triggering inaccurate blending
approximations. UWP retains its existing disabled setting.

Validation on September 9, 2026: the existing `boot-preview` integration mode ran
2,400 presentation advances of Skies with both the previous development DLL and
the patched DLL in separate profiles. The previous log contained three
logic-operation approximation warnings and four blend-state failure messages
(including repeated popup text); the patched log contained neither. Native
`configuration-create` verified that bundled compatibility overrides still win.
The ordinary integration run and fresh-process `restore` matched terminal RAM
and all 600 replayed video observations. These checks cover startup and replay;
the reported Alfonso ship discoloration still needs a visual recheck in that scene.

## Current editable allowlist

`EmulationConfiguration` validates explicit enum choices and booleans. Unknown keys and unsupported values are rejected. Unspecified keys receive the defaults in [ProjectConfiguration.cs](../src/TasStudio.Emulation/ProjectConfiguration.cs).

| Page | Editable project settings |
| --- | --- |
| General | Start UTC (Unix seconds 0–4,294,967,295); Japanese/English/German/French/Spanish/Italian system language; accurate CPU cache; skip GameCube BIOS |
| Graphics | Internal resolution 1×–4×; Auto/16:9/4:3/Stretch/Raw pixels aspect behavior; no AA or 2×/4×/8× MSAA/SSAA; synchronous shaders or synchronous ubershaders; compile shaders before starting; game-default/nearest/linear filtering; 1×/2×/4×/8×/16× anisotropy; disable copy filter; force 24-bit color; scaled EFB copies |
| Compatibility | Safe/Medium/Fast texture cache accuracy; CPU EFB access; EFB format changes; texture-only EFB copies; deferred EFB-to-RAM copies; texture-only XFB copies; bounding box; fast depth calculation; GPU texture decoding |
| Audio | DSP HLE/LLE and DSP JIT |

The supported renderer remains Direct3D 11. Single CPU execution, no VBI skip, no implicit cheats/import, no mutable custom textures/mods, and no Dolphin OSD are fixed base requests. Async Skip Drawing is excluded from the allowlist. The pinned core supplies other option defaults; its identity and the compatibility resources enter identity checking. Adding an editable option requires validation, persistence, boot mapping and compatibility coverage, rather than merely adding a widget.

Listening volume/mute, controller bindings, window layout, docking/fullscreen, recent projects and machine-local paths remain application preferences. They do not rewrite project history. Host pacing may accelerate seek while executing every emulated step; emulated CPU clocks are a different setting. Preview resizing is distinct from original capture pixels.

## Automatic checkpoints and disk storage

Named states and automatic checkpoints live on disk. Automatic capture defaults to every **60 emulated seconds**, a maximum of **300 checkpoints**, and a **4096 MiB disk budget**. The first reached limit constrains retention, so the actual retained count may be lower than 300. Five-minute spacing is available by setting the interval to 300 seconds.

Each project stores enabled/disabled, interval, maximum count, disk budget and retention policy. Retention defaults to oldest-created eviction; least-recently-used eviction is optional. Reducing a limit trims the active cache immediately. These budgets apply to automatic checkpoints, not named states or the required baseline. Interval uses emulated time, so accelerated seeking does not create checkpoints according to the host's wall clock.

Editing input K removes dependent state references after K; a boundary event change also invalidates the state at that boundary. Invalid named states and checkpoints disappear from the active timeline immediately. Session-owned automatic and named files are deleted when invalidated. Immutable assets referenced by the last committed manifest remain until its atomic replacement, then dropped generated state assets are collected if their checksums and ownership are clear. Sibling project references, modified files, exports and reparse targets are preserved. Ambiguous ownership can leave an orphan file. Undo restores inputs, not removed states.

Saving a folder project persists retained checkpoint references and state files. Reopening checks compatibility before any can become a seek anchor. There is no RAM LRU of state payloads yet; an optional cache of roughly ten states may be added if measurements justify it. Capture/restore can still need temporary RAM, and the active baseline and emulator have their own memory use.

## Configuration identity and history

Play mode uses a persistent, separate profile with a raw Slot A card per region
and size. Importing a card sets `MemoryCardSizeOverride` (0–4 for 59–1019 blocks;
omitted for 2043 blocks). That value travels with states and project baselines and
enters the configuration fingerprint when present. The Libretro-specific
`0003-play-memory-card-size.patch` allows it to be loaded from Studio's isolated
INI; normal Dolphin builds retain their original policy. Experiment profiles create
the matching size before state restore. Loading a state rewinds card contents too.

The validated configuration expands defaults and hashes a canonical sorted option map plus Start UTC. A separate configuration identity includes that fingerprint, native host binary hash, system resources and app-owned local GameSettings contents. The history root combines settings/environment identity with the existing ROM/core identity, input contract and baseline identity. Ordered inputs and execution events extend the existing prefix hash chain. Checkpoint retention settings and UI preferences do not affect that root.

This is **requested configuration plus dependency identity**, not effective runtime readback. The pinned core hash covers compiled defaults, and resource hashes cover bundled compatibility data. The actual selected GPU/driver and resolved setting provenance are not currently read back. Do not label the frontend option dictionary as a verified effective profile.

The D3D11 host selects the default hardware adapter. Explicit selected-adapter identity, driver/environment reporting, effective setting readback and conflict detection remain needed before a complete profile verification claim. Changes to that environment also invalidate previous rendering-verification evidence.

Folder format v3 and archive format v2 carry configuration/checkpoint metadata. Older folder v2 and archive v1 remain readable. Legacy metadata retains its old identity semantics until an explicit configuration transition boots a new baseline; loading an old project must not manufacture a verified modern profile or rewrite old state hashes. See [project format](project-format-v2.md).

## Verification gates

1. **Integrity:** check versions, ROM/content hashes, baseline, referenced states, inputs and resources. Distinguish missing files from incompatible history.
2. **Configuration:** preserve compatibility precedence and validate saved requested configuration/dependency fingerprints. Add effective readback and selected graphics-environment checks; report unsupported conflicts.
3. **Replay:** replay a fixed sequence twice and then in a fresh process with isolated writable storage. Compare every VI's displayed raw RGBA dimensions and pixels, including held images when there is no new callback, and gameplay observations. Keep the first divergent boundary visible. Exercise altered UTC/resolution profiles and seek/checkpoint restoration separately.
4. **Evidence:** record the exact case, build, environment, range and diagnostics. File validity does not imply rendering verification, and one passing sequence does not prove every game, scene or editable option combination.

The harness compares per-VI displayed RGBA hashes and terminal MEM1 hashes. The historical first-three-VI fresh-process mismatch is resolved for the tested Skies sequence: apply pending compatibility settings before restoring GPU caches, then redraw after hardware/RAM restoration. All 600 observations now match in both same-process and fresh-process tests. This is case-specific evidence, not a guarantee for every profile or game. Native tests separately cover UTC/resolution changes, retained inputs, compatibility precedence, clean configuration boots and project/recovery reopening. See [workflow verification](workflow-verification.md) and [historical evidence](configuration-verification.md).
