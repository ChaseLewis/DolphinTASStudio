# Workspace iteration verification

2026-09-06. Testing build: `artifacts/app-v2/TasStudio.App.exe`.

## Implemented

- Dolphin-style icon toolbar, horizontal movie lanes, resizable game/timeline/input panes, optional watcher.
- Separate selection and actual playhead. Input edits do not silently seek; stale previews are labeled. Explicit accelerated seek and audition support cancellation.
- Multiple candidate takes, exact input editing, masked range edits, same-length section replacement, audition, and undo/redo.
- Version-2 folder projects with immutable assets, ROM relocation and identity checking, legacy import, and baked replay export.
- Incremental history hashes shared by named states, state files/slots inside a project, and automatic checkpoints. Invalid states are excluded from seeking and loading.

## Automated evidence

- Solution builds with zero warnings/errors.
- 37 domain/contract tests pass. Added coverage includes exact invalidation boundaries, event ordering, undo/revalidation, nearest valid seek anchors, candidate isolation and audition, range preservation, folder persistence, ROM relocation, failed manifest commits, legacy preservation, replay export, and rollback after failed native project restoration.
- Actual Skies ROM: warmup of 1,200 steps followed by a 360-input project. Created a take, changed an earlier input, applied the section, and verified old named states/checkpoints become invalid. Replayed, saved, reopened, and compared all 24 MiB of MEM1.
- A fresh process reopened the folder project and matched the expected complete MEM1 hash, including another seek-to-start/end cycle.
- Logs: `artifacts/iteration2-create.log`, `artifacts/iteration2-restore.log`. Tests establish repeatability for these sequences, not universal determinism.

## Actual app checks

- Launched the redesigned app against the supplied ROM.
- Opened a real folder project through the Windows native file picker, including its ROM hash check. It displayed the game, active playback, and alternative take.
- Clicking the alternative lane selected input 13 while the actual emulator remained paused at state 360. Clicking Seek to selection then moved it to state 13.
- Actual window inspection identified narrow analog fields and a scrolling Apply action. Numeric fields were widened and Apply moved into a fixed inspector footer.
- Self-contained packaged executable opened the real project through `--project`, rendered the game and editor, captured the actual window, and exited with code 0. Capture: `artifacts/iteration2-packaged.png`.
- Final packaged build was reopened for handoff at state 360 with no unsaved movie edits. Its Watches toolbar action opened the upper-right panel; adding a u32 watch at MEM1 base and clicking Refresh returned a value labeled with sampled state 360.

See `project-format-v2.md` for the exact persisted structure and current limits. Physical controller feel remains a user test; the underlying mapping path was retained.
