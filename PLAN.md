# Dolphin TAS Studio — Project Plan

Current interaction and recovery contract: [input workflow and recovery](docs/input-workflow-and-recovery.md). Watcher proposal: [memory watcher design](docs/design/memory-watcher.md). The TAS logo concept lives in `docs/design/assets/dolphin-tas-logo-v1.png`.

Status: implementation baseline, subject to the feasibility gates below.

Priority update (September 7, 2026): C# class-library experiments and VS Code are
the primary scripting workflow. Initialize receives only trial/boot metadata;
RunAsync receives emulator capabilities after boot/state restoration and preroll.
The deprecated Lua runner and embedded editor have been removed. See [C# experiments](docs/csharp-experiments.md). Safe cross-build project
migration remains deferred until the approach to 1.0.

Next iteration: [workspace and folder-project design proposal](docs/workspace-design.md), including the user's input-selection/explicit-seek workflow and multiple candidate takes. This is a design draft, not a claim of implemented features.

## Objective

Build a modern desktop tool-assisted speedrun (TAS) environment for GameCube and, later, Wii games using Dolphin as the emulation backend.

The application will combine deterministic input playback, frame stepping, editable timelines, savestates and checkpoints, branches, memory inspection, scripting, and automated input search.

Use .NET and Avalonia for the desktop application. Start by evaluating a pinned fork of `dolphin-libretro`, hosted through a small native C ABI. Keep all emulator-specific behavior behind application-owned interfaces so the backend can be replaced without rewriting TAS services or the UI.

The first deliverable is proof that the backend can support accurate TAS operations. A working game window alone is not sufficient.

## Initial scope

The initial implementation targets:

- Windows x64.
- GameCube games and one connected GameCube controller on port 1.
- Keyboard input, followed by basic common-gamepad support.
- One graphics integration path, selected during the feasibility milestone.
- A small set of named test games, with exact revisions and hashes recorded locally.
- Reproducibility within a pinned, tested backend build and configuration.

Represent controller ports and device configuration explicitly so additional ports can be enabled later. Do not promise cross-platform, cross-version, or cross-machine deterministic replay until separately tested.

Wii support remains a goal. Its milestone must define supported emulated controllers, motion/IR inputs, extensions, and storage behavior. Real-device passthrough is not part of the initial deterministic playback path.

## Architecture

```text
Avalonia UI
    |
Application services
    +-- Execution and playback
    +-- Controller mapping
    +-- Projects and timeline
    +-- Checkpoints and seeking
    +-- Memory and watches
    +-- Scripting
    +-- Search
    |
Application-owned backend interfaces
    |
Managed Dolphin adapter
    |
Native host C ABI
    |
Pinned dolphin-libretro core + isolated TAS extensions
```

Suggested repository layout:

```text
src/
    TasStudio.App/
    TasStudio.Core/
    TasStudio.Emulation/
    TasStudio.Dolphin/
native/
    TasStudio.LibretroHost/
    dolphin-libretro/
tests/
    TasStudio.Core.Tests/
    TasStudio.Integration.Tests/
docs/
```

- **App:** Avalonia views, view models, and presentation.
- **Core:** timeline inputs, positions, events, branches, markers, project metadata, and search definitions; no Avalonia or Dolphin dependencies.
- **Emulation:** backend contracts and execution coordination.
- **Dolphin:** managed adapter and native interop.
- **LibretroHost:** core loading, callbacks, environment negotiation, graphics integration, audio delivery, input delivery, configuration, serialization, and shutdown.

Keep the initial interfaces small. Add capability interfaces where appropriate instead of building a universal emulator framework. Do not expose Dolphin C++ objects or emulated-memory host pointers to application services.

## Foundational contracts

### Execution and frame numbering

One execution service owns and orders all backend operations. The UI, scripting, seeking, and search use this service. Backend calls must follow the threading and synchronization requirements established by the prototype.

- Run and pause are execution-service operations; the backend exposes a precise stepping primitive.
- A step supplies an immutable snapshot of all configured controller inputs.
- A frame group follows Dolphin's next nonduplicate presentation, holding that snapshot across all intervening VI fields. ABI 3 stores the ordered controller polls within each group, including exact pad bytes, port and tick/field offsets. Timeline coordinates count polls and selection/editing snap to whole groups. Playback consumes recorded polls and validates count/order/timing; it is paced by elapsed emulated time. This development change requires new recordings.
- State capture, state restoration, memory access, and configuration mutations execute at defined safe boundaries.
- Pause completes only once the backend is at a safe boundary.
- Frame advance while paused performs one step and remains paused.
- Cancellation of replay/search is observed at safe boundaries.
- UI refresh, audio consumption, and wall-clock timing must not select or alter TAS inputs.

The project convention is:

> State N is the boundary immediately before input N. Executing input N produces state N+1.

Initial state is state 0. A checkpoint at N is positioned before input N. Editing input N invalidates dependent states after N; state N remains reusable if its preceding history is unchanged.

Before implementation freezes the contract, identify the actual Dolphin boundary used by a step and verify its relationship to controller polls, video interrupts, and presented images. A video callback is not the definition of a timeline frame. Record the behavior of intervals with no controller polls or multiple polls. Initially hold the selected controller snapshot throughout a step; any later subframe input support must be explicit.

### Controller data

Store exact integer values in canonical GameCube input data, with documented ranges, neutral values, and conversion rules. Include buttons, both sticks, analog triggers, and independent digital L/R clicks. Normalized floating-point values may be used for UI interaction and physical-device mapping.

Physical input is translated into the same canonical input representation used by playback. TAS playback cannot silently merge live input. Recording captures the exact input submitted to the backend.

Verify that the backend preserves the required controller values without unintended dead zones, rounding, remapping, or trigger coupling. Add a focused Dolphin input extension if the standard Libretro path cannot do so accurately.

### Native boundary

Use an opaque host handle and a versioned C ABI with fixed-width fields, explicit buffer ownership, structured status codes, and diagnostic messages. Define string encoding, structure layout, calling convention, and state-buffer size negotiation. Native exceptions must not cross the ABI.

Do not assume multiple core instances can coexist in one process. Establish instance and graphics-context limitations during feasibility work; parallel search can use isolated worker processes later if necessary.

### Rendering and audio

The native host must support the selected Libretro graphics interface, including resource/context lifetime and presentation into Avalonia. Hardware video callbacks may identify a completed GPU frame rather than provide a CPU pixel buffer.

Validate one rendering path before offering renderer selection. Keep rendering isolated from editor layout. CPU readback is acceptable for an initial prototype if measured and usable; avoid committing to an unproven zero-copy design.

Audio uses bounded buffering. Pause, seek, and restore must discard stale queued audio. Fast replay may suppress presentation and audio output, but must preserve necessary emulated GPU/DSP work. Verify accelerated replay against ordinary playback.

### Reproducibility and project state

A project records:

- Project format version and identity.
- Game identity, region/revision, and content hash.
- Exact backend/core build identity and TAS extension version.
- Effective determinism-relevant configuration, including applicable game overrides.
- Controller topology and input schema version.
- Initial-state mode: defined boot or embedded starting savestate.
- Initial RTC configuration and subsequent recorded clock mutations.
- Required initial persistent storage, such as memory-card data and later Wii NAND/save data.
- Canonical inputs, ordered execution events, markers, and branch metadata.

Use project-owned writable storage. Establish which external files are covered by core serialization and how the rest are restored or isolated. Search candidates must start with equivalent storage as well as equivalent emulator state.

Wrap opaque savestate bytes in metadata containing compatibility identity, timeline position, and relevant project/history identity. Reject incompatible states with a clear error. Checkpoints are disposable caches; canonical project data and any required initial savestate are durable.

Manual saved states and automatic checkpoints have identical history-validity requirements: an earlier effective input/event change makes dependent states invalid and unusable for that timeline. Preserve verified prefix hashes and extend a canonical, versioned hash chain incrementally; never relabel old state bytes with a new history hash. Remove invalid states and checkpoints from the active project immediately; delete owned automatic cache files. Undo restores inputs, not removed state references. Preserve files needed by the last committed manifest until its atomic replacement, then collect superseded owned assets safely. See [state validity and incremental history hashing](docs/workspace-design.md#state-validity-and-incremental-history-hashing) for boundary/event rules, capture races, and acceptance checks.

Selected RAM hashes are useful divergence diagnostics, not proof of complete determinism. Test repeated restoration, long replay, fresh-process replay, and accelerated replay. Use multiple meaningful observations and report the earliest observed mismatch. Do not require raw savestate byte equality unless the serialization format is verified to be canonical.

### Memory and execution events

Specify supported address spaces, mappings, guest byte order, invalid-range behavior, and safe access rules. Initial typed GameCube reads use explicit big-endian interpretation. Initial memory access may be limited to supported RAM regions; MMIO and translated-address behavior require separate definitions.

Memory writes, RTC changes, reset actions, and other execution-affecting mutations cannot be invisible side effects of a replayable project. Record supported mutations as ordered events at defined boundaries, including their order relative to input application. Alternatively, an experimental mutation requires capturing a new starting state before treating its continuation as a reproducible project.

Scripts use the same execution service and event recording rules as the UI. Read-only watches observe coherent state at a defined boundary.

## Milestones and acceptance gates

### M0 — Backend feasibility and contract validation

Select an exact `dolphin-libretro` revision, document the native toolchain, identify the test games, and build a minimal host and test harness. Create only enough Avalonia UI to prove presentation.

Prove:

1. A reproducible native build and successful game boot.
2. One functioning graphics path into Avalonia, including resize and paused redraw.
3. Exact controller injection, including analog extremes, neutral values, and independent trigger clicks.
4. Defined single-step boundaries and correct input timing.
5. In-memory state capture/restore and repeated replay with matching observations.
6. Fresh-process restoration/replay under the pinned environment.
7. Safe typed memory reads/writes and clear failures for unsupported addresses.
8. Clean load, unload, reload, and shutdown with useful diagnostics.

Inspect available Libretro memory interfaces before adding custom exports. Keep required Dolphin changes focused, but allow changes necessary for correct stepping, input, or synchronization.

**Gate:** retain Libretro only if the evidence supports accurate TAS behavior and manageable integration. Record findings and unresolved limits. If it fails, evaluate a direct Dolphin integration behind the same application boundary before building further UI.

### M1 — Minimal usable emulator frontend

Build an editor-style shell with game viewport, menus, status, and room for future panels.

Include:

- Open/stop game and actionable startup errors.
- Run, pause, reset, and frame advance.
- Basic audio output, mute, and volume.
- Keyboard mapping and basic gamepad mapping for port 1.
- Temporary manual savestate slots.
- Aspect-ratio-preserving resize and reliable paused redraw.
- Persisted application settings and isolated Dolphin user/resource directories.
- Only the configuration needed by the supported test set.
- A small memory debug panel or integration harness.

**Gate:** review rendering, audio, stepping, controller injection, state restoration, memory access, lifecycle behavior, effective configuration, and the managed/native boundary. Confirm feasibility tests still pass. Pause for architectural review before the TAS project milestone.

### M2 — Minimal editable TAS project

Implement the canonical project format, atomic saves, loading validation, and a minimal input editor.

The first complete workflow is:

> Record inputs → edit a button → restore the starting state → replay → save the project → reopen in a fresh process → reproduce the result.

Include basic input inspection/editing, position display, playback, and seeking by restoring the initial state and replaying. Add ordered execution events and storage handling needed by this workflow. A sophisticated cache is not required yet.

**Gate:** reopen and reproduce a saved project, verify that input edits affect the intended step, reject incompatible state/configuration combinations, and preserve canonical data across failed or interrupted saves.

### M3 — Checkpoints and responsive seeking

Capture checkpoints at safe emulation boundaries. Move compression and disk writing to background work after capture; do not call the core concurrently to save a state.

- Seek by restoring the nearest compatible checkpoint and replaying forward.
- Associate checkpoints with their input/event history and execution configuration.
- Invalidate dependent checkpoints and manual saved states immediately when effective execution history changes; apply the same history validation to both before loading.
- Keep named states and automatic checkpoints on disk. Persist per-project enablement, spacing, maximum count, disk budget and oldest-created/LRU retention. Default to every 60 emulated seconds, at most 300 checkpoints within 4096 MiB; support five-minute spacing by choosing 300 seconds. The required baseline and named states are outside automatic retention.
- A RAM LRU of roughly ten state payloads is optional future work; disk storage is authoritative. Reduce limits and remove invalid active references immediately, while respecting atomic-save asset ownership.
- Support cancellation and progress for long seeks.
- Suppress unnecessary presentation during replay while retaining required emulation work.

**Gate:** seeking produces the same observations as uninterrupted replay; edits never reuse incompatible downstream states; cancellation leaves a coherent position; cache use stays bounded.

### M4 — Timeline editing and branches

Expand the editor with buttons and analog values, selections, copy/paste, insert/delete, shifts, hold ranges, markers, and undo/redo. Implement durable named branches and branch switching.

Branch history and canonical inputs belong to the domain model, not visual row state. Checkpoint sharing is allowed only when histories and execution environments are compatible.

**Gate:** editing, undo/redo, branch switching, save/reopen, and seeking preserve the expected input histories and replay results.

### M5 — Memory watcher

Implement Dolphin `Locations.txt` expression import and pointer-chain compatibility, verified against documented behavior and fixtures. Preserve original expressions; store application labels and formatting separately.

Support signed/unsigned integers from 8 to 64 bits, float, double, byte arrays, explicit endian settings, and decimal/hex/boolean/enum formatting. Handle invalid pointers without crashing or silently inventing values.

**Gate:** imported expressions resolve correctly against known memory, typed values use the specified byte order, and watch refreshes observe coherent boundaries.

### M6 — C# experiments

C# class libraries implement typed experiments in isolated worker processes. The SDK exposes execution, input, state and memory operations; the coordinator owns scheduling, cancellation and serialized SQLite results. See the [current experiment contract](docs/csharp-experiments.md).

Script-driven advances must not race playback or UI commands. Start with explicitly user-run local scripts; define their trust and filesystem-access model before supporting shared scripts.

**Gate:** scripts can automate reproducible edits/replays, stop cleanly, and report errors without corrupting the project or backend state.

### M7 — Automated search

Implement serial candidate search first: restore baseline, apply a candidate, replay to a bounded horizon, evaluate a predicate/objective, and retain the result.

Support delay scans, input variations, memory predicates, cancellation, progress, and applying a successful candidate as a reviewable timeline change or branch. Record the baseline, candidate inputs/events, configuration, search definition, and evaluation outcome.

**Gate:** every candidate starts from equivalent state and storage, the baseline remains intact, and an applied result reproduces through ordinary project playback. Report candidate counts, replay cost, and state-restore cost before optimizing.

### M8 — Wii, C# scripting, and advanced tooling

Schedule these based on demonstrated need:

- Wii controller/input models and persistent-storage support.
- Additional controller ports and devices.
- Roslyn-based C# scripting and user plugins.
- Parallel search using verified instance or process isolation.
- Branch comparison, input optimization, and timeline transformations.
- RAM graphs, script debugging, and Dolphin debugger integration.
- Broader determinism diagnostics and automated TAS verification.
- Additional platforms and rendering paths.
- Movie import/export and compatibility with external TAS workflows.

Each feature needs its own scoped acceptance criteria. Existing project replay must remain stable or require an explicit compatibility transition.

## Engineering rules

- Pin backend dependencies and retain a reproducible build recipe.
- Keep a documented patch inventory for the Dolphin fork.
- Keep emulation execution independent of UI rendering and live-device timing during playback.
- Route all state-changing work through ordered application services.
- Prefer exact, documented domain data over implicit conversion or UI-owned state.
- Use a freely redistributable test program where practical; keep user game images out of the repository.
- Add focused domain tests and meaningful native integration tests at the milestones that introduce the behavior.
- Measure checkpoint sizes, restore time, replay speed, and presentation cost before optimizing them.
- Preserve required license notices and document distribution requirements before packaging releases.
- Complete correctness gates before sophisticated UI, scripting, or search optimization.

## Decisions to resolve during M0

- Exact backend revision and Windows build toolchain.
- Initial test-game set and test-program availability.
- First graphics API and Avalonia presentation mechanism.
- Precise stepping boundary and controller-poll behavior.
- Required input/step/memory extensions beyond standard Libretro.
- Deterministic core configuration and persistent-storage restoration strategy.
- State compatibility identity and observation strategy for repeatability tests.
- Core instance, threading, and graphics-context limitations.

These are feasibility tasks, not assumptions to bury in implementation. Record each result in `docs/` as evidence becomes available.

## Reference material

- [Dolphin Libretro repository](https://github.com/libretro/dolphin)
- [Dolphin core documentation](https://docs.libretro.com/library/dolphin/)
- [Libretro API header](https://github.com/libretro/libretro-common/blob/master/include/libretro.h)

The selected source revision and observed behavior take precedence when documentation and implementation differ.


## Game window and input threading (implemented September 6, 2026)

- Keep one emulation core on its existing dedicated owner thread.
- Allow the same game view to move between the editor and a separate window,
  with fullscreen and close-to-dock behavior. Preserve execution position,
  project history, and checkpoint validity during every presentation transition.
- Expand the timeline when the game is detached; retain the optional watcher.
- Share playback shortcuts and keyboard bindings between both windows.
- Poll XInput on a separate input worker. Publish immutable controller samples
  to emulation and diagnostic UI; copy settings before crossing thread boundaries.
- Clear keyboard state and publish neutral input immediately on focus loss or
  modal entry. Keyboard events and image presentation still use Avalonia's UI
  thread; emulator execution and gamepad sampling do not wait for UI refresh.
- Join the input worker and dispose the core when the editor closes.

See [verification](docs/popout-verification.md) for observed results and limits.

## Configuration decision (September 6, 2026)

Target repeatable gameplay and captures on a fixed setup. Game compatibility
overrides always take precedence over Studio defaults. Record and validate the
resolved effective profile, including override provenance; conflicting profiles
remain unverified rather than silently replacing a compatibility setting.

Keep application preferences separate from **editable per-project settings**. After opening a project, users can change Start UTC, internal resolution and the supported general/graphics/compatibility/audio allowlist. Apply restarts from boot with a new baseline and isolated storage, preserves original inputs/events, removes old state/checkpoint references, and can replay to the selected input. Checkpoint-policy changes need no reboot. Single CPU execution is required and not editable; unsupported compatibility conflicts must be reported rather than overriding the game.

Start the history hash at the validated settings and environment dependencies, including native host/resources and the existing ROM/core/baseline identities. Current fingerprints cover requested defaults and compatibility resources, not effective runtime readback or actual GPU/driver identity. Never rewrite an old state identity to make it reusable under changed settings. Folder v3 and archive v2 persist this metadata while retaining readers for folder v2/archive v1. See the [configuration policy](docs/configuration-policy.md), [project format](docs/project-format-v2.md), and [historical verification evidence](docs/configuration-verification.md). The earlier fresh-process first-three-frame rendering mismatch is resolved for the tested Skies sequence; all 600 observed boundaries match after the restoration-order fix. See [workflow verification](docs/workflow-verification.md). Editable controls and one passing sequence do not imply every profile is rendering-verified.
