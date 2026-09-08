# Per-poll playback and grouped editing

Verified September 6, 2026, on the local Windows/D3D11 setup with Skies of Arcadia Legends (USA).

The native ABI is now 3. A presentation advance remains one editable frame group, but its persisted data contains every ordered controller poll, exact pad bytes, port, tick offset and field offset. Group duration records total ticks and video fields. Frame means the native video-field counter; Poll means the recorded stream position. Timeline selection snaps to a whole group, including when clicking in the middle of its polls.

Edits rewrite every poll in the selected group. Poll bytes and timing participate in the history hash, invalidating later checkpoints/states. Affected timing is regenerated when executing the edited history. Unchanged groups replay recorded polls and validate count, port, offsets and ending presentation time. Poll mismatch pauses with an invalid preview rather than silently continuing. Exports materialize edited groups before writing the replay. These remain Studio replay files; DTM export is not implemented by this change.

Evidence:

- `artifacts/poll-playback-final/polls.tasproj`: 120 groups containing 644 polls, ending at native VI 392. This includes startup/transitions, so the poll/frame ratio varies.
- `artifacts/poll-final-create.log`: same-process save/reopen, a whole-group input edit, checkpoint invalidation and regenerated poll recording passed.
- `artifacts/poll-final-restore.log`: a fresh process replayed the poll stream with matching terminal MEM1 and RGBA preview. A one-tick change to a stored poll was rejected as a desync, with the previous session restored. Distinct controller bytes within one group were delivered individually by the native replay path.
- Managed tests cover project/recovery persistence, editing every poll in a group, undo/redo, stale-state rejection, poll-count divergence, zero-poll groups, exported replay boundaries, poll-axis selection and the UI's +2-field/+4-poll advance. All 168 tests passed.
- `artifacts/poll-review/poll-timeline.png`: headless UI check of the poll ruler, frame/poll counters and selection snapping. The preview uses a fake backend, not a game capture.

Reproduce after building native and integration targets:

```powershell
tests/TasStudio.Integration.Tests/bin/Release/net10.0/TasStudio.Integration.Tests.exe 'C:\path\to\Skies.iso' artifacts/new-poll-run poll-create
tests/TasStudio.Integration.Tests/bin/Release/net10.0/TasStudio.Integration.Tests.exe 'C:\path\to\Skies.iso' artifacts/new-poll-run poll-restore
```

Current topology is GameCube port 1. Earlier ABI recordings/states are intentionally incompatible; their files are not deleted.
