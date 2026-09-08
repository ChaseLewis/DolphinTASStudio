# Workspace and project design — proposal

Status: design discussion, 2026-09-06. This proposes the next iteration; it does not describe features already shipped. The M2 testing build remains unchanged.

## Direction

Use Dolphin's familiar menu and icon-toolbar arrangement, then organize TAS editing like a movie editor. The game viewport remains prominent. A horizontal timeline sits below it, with the input inspector at bottom right. An optional memory watcher occupies the upper right.

The user's supplied Dolphin screenshot is the visual reference. Use one coherent icon family, restrained separators, short captions below icons by default, and tooltips with shortcuts. Allow captions to be hidden later. Avoid oversized text buttons and duplicate controls in multiple panels.

```text
File  Emulation  Movie  Options  Tools  View  Help
Open  Save | Play/Pause  Stop  Advance | State | Controllers  Config
┌─────────────────────────────────────┬─────────────────────┐
│                                     │ Memory watcher      │
│             Game viewport           │ (optional)          │
│                                     │                     │
├─────────────────────────────────────┼─────────────────────┤
│ Timeline controls / frame ruler     │ Input inspector     │
│ Saved-state and checkpoint markers │ Port / selection    │
│ Active playback + alternative takes │ Buttons / sticks    │
│                 │ playhead          │ Triggers / raw data │
└─────────────────────────────────────┴─────────────────────┘
Paused • Frame 2527 • Port 1 • Project status
```

Persist split sizes and visible panels per user. Hiding the watcher expands the viewport across the upper area; it should not leave an empty reserved column. Keep the input inspector alongside the timeline. Prioritize resizable split panes before implementing arbitrary floating/docking windows.

## Timeline interaction

- Horizontal time is labeled in exact step/frame indices. The backend currently steps VI fields; elapsed time is secondary and must not silently assume 60 Hz.
- The ruler and saved-state lane share the same time scale as every input track.
- The solid playhead identifies the emulator's actual state. Selection shading identifies the inputs being edited. Selection and emulator position move independently.
- Confirmed behavior: clicking the ruler selects a frame/input without moving the emulator. Dragging changes the selection only. A separate **Seek to selection** command restores an appropriate compatible state when needed and replays as fast as possible, suppressing unnecessary presentation/audio output while preserving required emulation work. Disable it when already at the selected boundary. Keep the actual playhead at the current state until progress is reported; show the selected target independently. Cancel leaves the actual position visible. The first implementation can replay from the initial state; compatible checkpoints later reduce the distance.
- Dragging in an input track selects a range. It does not seek repeatedly or overwrite input merely by moving the pointer.
- State N is immediately before input N. Selecting input N displays the buttons that will execute next, not the input that produced the current image.
- Zoomed out: held buttons appear as continuous blocks; analog input has compact tracks. Zoomed in: individual frame cells are editable. Adjacent blocks must not imply interpolated analog values unless interpolation was explicitly applied and baked into exact inputs.
- Named saved states appear as recognizable icons above the ruler. Automatic checkpoints use a different, quieter symbol. Hover/focus reveals name, frame, and compatibility. Dense markers group at low zoom.
- Saved states and automatic checkpoints follow the same strict history-validity rule. Changing preceding executed input/events makes either invalid for the changed timeline. Invalid states cannot be loaded or used as seek anchors there. Retaining a named state file does not grant permission to use it against incompatible history.
- Automatic checkpoints are regenerable caches and may be evicted. User-created saved states and the project's initial state are durable assets.
- Frame advance follows the current record/playback mode. Make that mode visible so physical input cannot silently replace recorded inputs.
- Editing existing input should be undoable. A range edit affects only the controls explicitly changed; mixed values are displayed as mixed. Unrelated buttons and axes are preserved.

The initial redesign can show existing project inputs in this layout before advanced track operations and automatic checkpoints ship. Do not depict unavailable commands as functional.

## Multiple takes and script results

After an edit, matching frame numbers alone do not make the preview current. Seek remains available until the emulator state matches the active input/event history at the selected boundary. Show when the preview predates the edit.

Confirmed direction: support multiple horizontal input rows, similar to a music sequencer. Each row represents a possible controller-input sequence, not just one button. The top row is the active playback. Lower rows are alternative takes, including script/search results, available for review.

- Active playback is an explicit composition of referenced input sections. Lower candidate rows do not automatically affect it, regardless of their visual order.
- Select a frame/range in any take to inspect its inputs. Selection alone neither applies the take nor moves the emulator.
- **Audition selection** temporarily replays the candidate substitution from a compatible baseline, with the audition source clearly visible. Ordinary **Seek to selection** follows the active playback. Keep these operations distinct.
- **Use section** replaces the selected half-open interval `[start, end)` in active playback with the candidate's exact inputs. Preserve the prior version through undo/history, leave the candidate intact, and show the source take on the resulting active clip.
- Replacement supplies the complete controller state for each affected configured port. Neutral input is an explicit state, not a transparent gap. Uncovered portions retain their existing active input. Per-button blending would require a separate, explicit future operation.
- Same-length section replacement is the initial operation. Variable-duration search results require a later explicit choice between rippling later inputs and retaining absolute positions; do not silently shift the rest of the movie.
- Script results land as candidate takes by default. Record the source timeline revision/history, baseline position/state, substituted interval, inputs/events, script/search identity, parameters, and observations. Applying or auditioning against a changed baseline must detect incompatibility and require re-evaluation.
- Applied changes invalidate downstream checkpoints and incompatible named saved states. Markers belong to a history and position, not merely their horizontal screen coordinate.
- At coarse zoom, show named clips; at fine zoom, expose exact frames and optional expanded button/analog detail. Avoid permanently dedicating a lane to every button.
- Export bakes only the chosen active composition with its exact ordered execution events. Unused candidates and their observations remain editable project data.

The design sketch demonstrates selection separately from seeking and a sample **Use section** action. Auditioning, history validation, and script execution need real backend implementation; the sketch does not simulate them.

## State validity and incremental history hashing

Confirmed requirement: **manual saved states are inherently invalid when the preceding effective input history changes, exactly like automatic checkpoints.** Their retention policy differs; their validity rules do not.

### History identity

Use a versioned cryptographic hash chain over canonical execution data. Proposed definition for ordinary step boundaries:

```text
H[0]   = SHA256(encode("tas-history/v1/root", executionIdentity))
H[N+1] = SHA256(encode("tas-history/v1/step", H[N], N,
                      orderedEvents[N], exactInputs[N]))
```

`executionIdentity` includes game content hash, exact core/extension identity, effective determinism-relevant configuration, controller topology/input schema, and initial state/storage/RTC identity. Hash the actual resolved input sequence for all configured ports, not clip names, row order, script descriptions, file paths, or JSON formatting. Equivalent takes producing identical execution data should have identical history hashes.

Specify canonical encoding with fixed field order, explicit integer widths/byte order, event type tags, and length prefixes. Do not concatenate ambiguous strings or use runtime object hash codes. The same execution sequence must hash identically regardless of input file chunking or clip boundaries.

Each captured state stores its boundary, history hash, execution identity, and a separate checksum of its opaque state bytes. The history hash establishes association with the executed movie; the byte checksum detects a damaged state file. Neither establishes determinism by itself.

State N is before input N and before its ordered boundary events in this definition. If a state is captured after a memory write/reset at the same step index, record an explicit event cursor and hash the events already applied. Never give pre-event and post-event snapshots the same state identity merely because their displayed frame is equal. Implementations may instead restrict captures to normalized boundaries, but cannot omit this distinction.

### Invalidate immediately; recompute incrementally

1. Find the earliest effective execution change K when an edit is committed. Mark dependent state references invalid immediately; do not leave them usable while hashes recompute. Changes to unused candidate takes do not invalidate the active playback.
2. For an input-only edit at K, state K remains valid; states K+1 and later are invalid. Event edits use their precise position/order. A changed initial execution identity invalidates all derived states.
3. Keep verified prefix hashes at fixed input-block boundaries and state boundaries. Start hashing from the nearest unchanged cached prefix at or before K, which may be the last valid checkpoint/saved-state boundary. Hashing inputs/events does not require loading or running the emulator.
4. Recompute only the necessary suffix to the state/seek target. Cache those new prefix hashes for later validation. A chain still requires processing the changed suffix; it cannot reuse an old downstream hash after its preceding history changed.
5. Compare each retained state's recorded history identity to the newly computed identity at that exact boundary. Reuse it only on a complete match, with valid compatibility metadata and state checksum. An exact undo or switching back to the original take can make a retained state valid again.
6. A mismatch requires restoring an earlier valid state and actually replaying/capturing a replacement. **Never stamp a new history hash onto old state bytes to revalidate them.** Identical-looking video or selected RAM values are not sufficient to reuse the state.

This allows seeking from the nearest valid saved state or checkpoint, then extending the verified prefix with the inputs/events between it and the target. It does not require rehashing from the beginning for every seek.

### UI, persistence, and races

- Invalid named-state markers remain visible with an explicit invalid status/reason and disabled load action. Automatic invalid checkpoints may be evicted. A retained named state can still belong to its original unchanged history; it cannot bypass validation in the edited timeline.
- New state data may replace an invalid named state's snapshot only through an explicit regeneration action; preserve the old asset until the new save is committed.
- Capture records the executed history identity at capture time. If editing occurs during background compression/writing, the completed file retains that original identity and must not be published as valid for the new history.
- Key validation caches by immutable history revision and execution identity. On reopening a project, verify against canonical project data; a serialized `valid: true` flag is not authoritative.
- Legacy/manual states with no verifiable history cannot serve as arbitrary seek anchors. They can become an explicitly chosen new project baseline, which establishes a new execution identity.

Acceptance checks: editing input K preserves state K and rejects both manual and automatic states after K; undo restores reuse when identities match; memory/reset events and initial configuration changes invalidate correctly; untouched candidates and cosmetic clip renames do not invalidate playback; file chunking does not change hashes; edits during state persistence cannot publish a stale state as current; damaged state bytes remain rejected even when history matches.

## Input inspector and memory watcher

The inspector follows the selected frame/range and shows its port and span. Use labeled GameCube buttons, two stick pads with numeric X/Y fields, and analog trigger bars with independent L/R click controls. Numeric entry remains available for exact 0–255 values. Keep physical controller mapping in its own configuration dialog.

The watcher is opened from View/Tools. Its table shows name, address/expression, type, and value at the last coherent emulator boundary. Mark values as stale during seeking; unavailable addresses are not zero. A placeholder panel in the layout does not constitute a completed memory-watcher feature.

## Folder-based project

Use a small, versioned JSON `.tasproj` file as the entry point, similar in role to a solution file. Referenced project assets live beside it. A second solution/workspace wrapper is unnecessary until one workspace needs multiple projects.

```text
Skies TAS/
  Skies.tasproj
  timelines/
    main.json
    takes/
      <take-id>.json
  inputs/
    <content-hash>.json
  states/
    initial.tasstate
    <saved-state-id>.tasstate
  watches/
    default.json
  .tasstudio/
    user.json
    cache/
      checkpoints/
      thumbnails/
  exports/
    Skies.tasreplay
```

- `Skies.tasproj`: format version, project UUID, name, game identity/hash and ROM path hint, exact backend identity, effective emulation configuration, controller schema/topology, initial-state reference, and active timeline reference.
- `timelines/main.json`: active playback composition, candidate take references, ordered execution events, markers and named-state references. Each clip names its source take/chunk, source offset, destination start, and exact length. Candidate definitions in `timelines/takes/` include baseline identity and script/search provenance where applicable. Visual row order does not determine the canonical active composition. Persistent storage requirements remain part of the reproducibility contract.
- `inputs/`: canonical exact input data. Start with a documented JSON encoding of frame runs and integer controller values; a repeated-state run has an explicit positive length. No need to build a binary codec first. Split immutable chunks at a documented size limit so saving a long movie does not rewrite the whole recording.
- `states/`: durable initial and user-created states, retaining the existing compatibility envelope. Each timeline reference includes the position and history identity. Project opening must not require copying the ROM here.
- `watches/`: shared watch definitions, separate from panel positions and widths.
- `.tasstudio/user.json`: machine-specific resolved ROM path, selection, panel layout, and recent UI state. This is not canonical movie data.
- `.tasstudio/cache/`: disposable state/thumbnail caches. Deleting this directory must not lose the authored movie or named states.
- `exports/`: optional generated replay files, separate from the editable source project.

Paths to project assets are relative to the project directory. The ROM reference may be absolute or relative; game identity is determined by content, not filename. Stable IDs identify saved states and timelines; display names are freely renameable.

### Save and recovery

A directory tree cannot be atomically replaced one file at a time. Write changed assets as new immutable files, flush and validate them, then atomically replace the project manifest as the commit point. Keep the previous manifest/assets recoverable until the new save succeeds. Never overwrite an asset still referenced by the committed project. Garbage collection is a separate later operation.

Reject unknown format versions clearly. Import the current ZIP-based M2 `.tasproj` into a new folder project, preserving the source file. Detect the old container explicitly; do not rewrite it in place or treat it as the new manifest.

### ROM relocation

1. Read and validate the project manifest without disturbing the current session.
2. Try the local resolved path, then the project's ROM path hint.
3. If the file is missing or cannot be opened, show a Locate ROM dialog with the expected game and prior path, an actionable reason, Browse, and Cancel.
4. Hash the chosen image with visible progress and cancellation. An existing path with a wrong hash also requires resolution.
5. Accept an exact match; remember the resolved path locally. Offer to update the shared project path hint when appropriate.
6. If it is a different revision/image, explain the mismatch and keep the project unopened. A separate explicit conversion/new-project workflow can be designed later.
7. Cancel or a failed replacement keeps the current project and emulator session intact. Only replace the active session after preflight validation succeeds.

### Baked replay export

Use a separate versioned `.tasreplay` container. Export resolves the selected timeline/branch into an exact input sequence and ordered events, accompanied by game hash, backend/configuration identity, controller topology, and the required initial state/storage. Editor layout, undo history, unused branches, and caches are excluded.

The replay still needs the matching game image and compatible emulator build. It is not a video and does not contain a ROM. Dolphin movie/DTM export is a separate compatibility feature, not implied by this format. Check exported playback against project playback before claiming a successful export.

## Proposed implementation order

1. Agree on the shell, timeline interactions, and project structure using the design sketch.
2. Replace the shell with icon toolbar and resizable panes; keep existing emulation commands working.
3. Add folder-project persistence, legacy import, and the ROM relocation dialog with cancel/failure tests.
4. Add horizontal timeline selection/zoom, input inspector, named-state markers, multiple takes, and undoable section replacement. Script producers can then target the same candidate-take service.
5. Add checkpoint caching and responsive seeking, then the optional watcher and baked replay export as separately verified increments.

## Decisions still open

- Confirmed: timeline clicks select inputs only; accelerated Seek to selection moves the emulator explicitly.
- Confirmed: the top row is active playback; lower rows are alternative takes, including script results. Button/analog detail is expandable within a take.
- Confirm captions under toolbar icons as the default.
- Exact version-2 field names, chunk encoding, file limits, and migration fixtures will be specified before persistence implementation.
