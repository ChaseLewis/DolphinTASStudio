# Folder project format v3 (v2-compatible reader)

Updated 2026-09-06. The `.tasproj` entry point is UTF-8 JSON. The document keeps its original filename for existing links. Folder writes now use version 3; ZIP project/replay and `.tasstate` metadata writes use archive version 2. Readers accept folder v2 and archive v1 as well as the new versions. State containers carry history provenance, required when loading inside a project.

## Files

```text
My movie/
  My movie.tasproj
  inputs/<sha256>.json
  timelines/<sha256>.json
  states/initial-<state-data-sha256>[-<configuration-sha256>].tasstate
  states/<container-sha256>.tasstate
```

The manifest contains `Version: 3`, stable project UUID `Id`, `Environment`, `InitialState`, `Timeline`, `States`, and `AutomaticCheckpoints`. Each automatic entry contains a `State` asset descriptor, its emulated `Seconds`, and `Created`/`LastUse` retention ordering values. Each asset reference contains a project-relative `Path` and SHA-256 checksum. Absolute paths and paths escaping the project directory are rejected for assets. `Environment.GamePath` is a separate ROM path hint; the ROM is never copied into the project. Environment records include game hash, core binary identity, initial state identity and saved preview position, plus `Configuration`, `ConfigurationIdentity`, and `Checkpoints`. `Configuration` stores Start UTC and a validated allowlist of project options; `ConfigurationIdentity` fingerprints requested settings, native host and resource dependencies. `Checkpoints` stores enabled, interval, maximum count, disk budget and retention policy. The legacy resolution/DSP fields remain consistent with the configuration. These fields do not claim effective runtime readback or selected GPU/driver verification.

Input chunks contain up to 4,096 exact controller records, with byte-valued sticks/triggers and independent button bits. They are plain JSON arrays in this implementation, rather than run-length encoded data. Identical chunks deduplicate by content hash. The current supported maximum is 5,000,000 input steps; this is a format bound, not a claim that all editor operations are optimized at that size.

The timeline asset contains canonical active `Inputs` chunk references, ordered `Events`, candidate `Takes`, and provenance `Sections`. Take descriptors contain UUID, name, start, input chunk references, baseline prefix hash, event-span hash, and producer provenance. The top playback is currently materialized as exact input chunks; section labels record where a take was applied. It is not yet a general-purpose live clip graph. Row order never mixes inputs implicitly.

Named saved states and automatic checkpoints are disk-backed containers. Automatic checkpoints are created in isolated `CheckpointCache` storage and copied to immutable project assets on save, with references persisted in `AutomaticCheckpoints`. Defaults are every 60 emulated seconds, maximum 300 checkpoints and 4096 MiB of automatic checkpoint files; the first limit reached controls retention. Projects can choose oldest-created or least-recently-used eviction. The required initial state and named states are outside that automatic cache budget. There is no RAM LRU of state payloads yet; temporary capture/restore buffers and the active baseline still use memory.

## Saving and importing

Writes create immutable content-addressed assets first. A temporary manifest is flushed, all references are validated, and the manifest is atomically replaced. Existing committed assets are not edited in place. The last committed manifest and its assets remain usable until replacement. Invalid active references and session-owned state files are removed immediately. After commit, superseded generated state assets are collected only when no sibling project references them and their paths/checksums remain valid. Modified files, reparse targets, ambiguous ownership and unrelated exports are preserved. Recovery folders additionally collect superseded generated input/timeline JSON assets to avoid accumulating a new tail chunk every five seconds.

New Save As creates a sibling directory named after the chosen project and places the manifest inside it. Existing folder-project manifests save in place. The previous ZIP-based M2 format is detected and imported, but cannot be overwritten in place by a folder save. Save As creates a separate project and retains the original ZIP.

On open, the app checks the machine-local resolved ROM path and the path hint. Missing, inaccessible, or wrong-hash images lead to a Locate ROM workflow with Browse and Cancel. Hashing has an indeterminate progress dialog and cancellation. A matching relocated path is remembered in local application settings, keyed by game hash. The next project save records the resolved path as the shared hint. Failed preflight preserves the session; failure during native restoration attempts rollback to the captured prior session, including its inputs, takes, state references, and undo/redo history.

## Execution history

The versioned SHA-256 chain starts with the requested configuration fingerprint and configuration dependency identity, together with the existing ROM/core/input-contract/baseline identity, then hashes canonical raw controller states and ordered execution events. Legacy projects without the new configuration fields retain their previous root rules; the reader does not relabel old state bytes. Fixed-width numeric encoding uses `BinaryWriter` little-endian fields; text tags use its UTF-8 length-prefixed string encoding. Project layout, ROM filename, take name, and physical input-file chunk boundaries do not affect history.

Prefix cache entries are before boundary events. The externally compared history at state N includes every recorded event already applied at N, with its count and order, before input N executes. A snapshot is only captured at this normalized boundary. Adding an event at N changes the hash at N even though its displayed step index is unchanged.

An input edit at K retains state K and invalidates later states. An event edit also invalidates the affected boundary snapshot. Invalid named-state and checkpoint references are removed immediately. Undo restores inputs but does not resurrect removed states. Invalid manual states are not loadable inside the project and cannot be used as seek anchors. History hash matching does not replace state-byte checksums or backend/game compatibility checks.

Seek selects the nearest valid current state, named state, or automatic checkpoint. It restores the initial state if none is usable, then replays without wall-clock pacing or intermediate UI/audio delivery. Required native GPU/DSP emulation still executes. Pause/Cancel interrupts replay at step boundaries. Invalid state bytes are never relabeled with a new history hash.

Candidate producers call `HistoryAtAsync`, `EventsHashAsync`, then `AddCandidateAsync` with their result inputs and recorded baseline identities. Submission never alters active playback. Applying a candidate checks those identities again. Editing, section application, and candidate edits support undo/redo. Audition temporarily substitutes candidate inputs and leaves the preview explicitly stale relative to the active movie when the histories differ.

## Applying project settings

Settings remain editable after project open. Applying an emulation change boots with the new Start UTC/options and isolated storage, captures a new baseline, and preserves the recorded inputs and ordered execution events. Existing state/checkpoint references are removed because their settings-rooted history no longer matches. Candidate inputs and section provenance remain inspectable with their original hashes. Optional replay advances to the selected input using the retained movie. A checkpoint-policy-only change does not reboot or change execution identity.

Game compatibility overrides always take precedence over project requests. Single CPU execution remains required and is not a user option; a conflicting compatibility requirement is unsupported rather than silently overridden. See [configuration policy](configuration-policy.md) for the current allowlist and remaining effective-configuration verification gates.

## Replay export

`.tasreplay` currently uses version-2 ZIP archive metadata (the reader also accepts version 1) (`Kind: tas-project`) to package the initial state, canonical active inputs, ordered events, and configuration, checkpoint policy and compatibility metadata. Automatic checkpoint payloads are a folder-project concern and are not part of the flattened replay archive. Candidate rows, editor layout, undo history, and unused state files are excluded. It opens through Open Project / Replay and imports as a new editable session. It contains no ROM or emulator executable and is not a Dolphin DTM movie or video.

## Current limits

- One GameCube port, same-length section replacement. Per-port layering, per-button blending, ripple edits, general clip transforms, and persistent undo are not implemented.
- C# experiments run in isolated workers with a frozen source snapshot. Worker takes do not automatically replace the main timeline; see [C# experiments](csharp-experiments.md) for current result and retention behavior.
- Watcher supports typed MEM1 values, pointer chains, groups, persisted project sidecars and DMW import. It refreshes at paused execution boundaries; playback sampling, memory search/writing/freezing and DMW export remain future work.
- Checkpoint capture and disk serialization currently run synchronously on the execution owner. Timeline markers support load and clear. Background compression/writes, a RAM LRU, richer named-state management and grouped dense markers remain future work.
- The inspector supports graphical stick pads, optional normalized radii, exact numeric entry and direct range editing. Next Frame operates on the preview boundary; timeline selection remains independent of seeking.
- Undo retains at most 32 entries and aims to limit active-input snapshots to 64 MiB, retaining at least the most recent entry. Large candidate sets add memory beyond this bound. The automatic checkpoint disk budget does not bound baseline, previews, undo history, or native emulator memory.
