# Workflow verification

September 6, 2026. Local Windows/D3D11, Skies of Arcadia Legends (USA), pinned Dolphin source plus Studio's tracked native patch.

## Presentation stepping (ABI 2)

This section records the earlier ABI 2 run. Current ABI 3 per-poll verification is in [poll playback verification](poll-playback-verification.md).

Next Frame now uses Dolphin's nonduplicate presentation callback and stops at the following field boundary. Libretro's callback registration is retained until shutdown. One stored input and one turbo phase cover the whole advance; the timeline position increments once. Normal playback uses elapsed emulated time. Old ABI 1 dev projects/states are intentionally incompatible.

Verified with Skies on the fixed local setup:

- Of 900 advances, 886 spanned two fields. The others spanned 3–74 fields during startup/transitions. No fixed two-field multiplier is used. This run did not exercise a 60 FPS game.
- A 60-advance sequence reproduced field counts, emulated timestamps, RGBA pixels and all 24 MiB of MEM1 after restoring its baseline.
- Native input probes observed the exact supplied button/axis/trigger bytes. Each tested two-field interval contained four controller polls. Console-incompatible state rollback still passed.
- A separate 600-advance replay reproduced every displayed image and terminal MEM1 across same-process and fresh-process restoration, and passed shutdown/reload and malformed-ROM checks.
- Release build and all 161 managed tests passed.

Evidence: `artifacts/presentation-advance-verified-v2/results.json`, `artifacts/presentation-pad-smoke.log`, `artifacts/presentation-replay-create.log`, `artifacts/presentation-replay-restore.log`, and `artifacts/presentation-final-build.log`. The replay logs used the previous harness's “VI-boundary” label, but ran the ABI 2 presentation-based backend; that label is now corrected in source.

To repeat the focused cadence/replay test after building native and integration targets:

```powershell
tests/TasStudio.Integration.Tests/bin/Release/net10.0/TasStudio.Integration.Tests.exe 'C:\path\to\game.iso' artifacts/new-presentation-check presentation-advance
```

The field-based results below describe the earlier ABI 1 verification run.

## Rendering defect found and fixed

Fresh-process state restoration previously matched terminal MEM1 but displayed a purple placeholder for its first three VI observations. The strict regression reproduced it at states 1801–1803.

The game compatibility layer changes texture-cache sampling from 128 to 512. Its pending graphics update was applied after texture-cache deserialization, invalidating the saved XFB copies. In addition, redraw during video deserialization could look up XFB content before the saved hardware/RAM had been restored. The libretro restore path now applies pending graphics settings before state deserialization and redraws only after a successful complete restore. Compatibility overrides retain precedence. The state byte layout is unchanged; Studio still enforces exact backend identity for project/state loading.

No frame is skipped or hidden by the test. All 600 VI-boundary displayed RGBA observations and terminal 24 MiB MEM1 hashes now match across uninterrupted replay, same-process restoration and fresh-process restoration.

## Passed workflow checks

- Record a native session, capture a candidate, edit it, apply a section, and invalidate dependent states.
- Seek, save the project, reopen in the same process and a fresh process, retaining inputs, candidate edits and memory.
- Persist automatic checkpoints, load an earlier checkpoint, replay to the endpoint, and clear it without changing inputs or preview position.
- Boot with shader precompilation enabled, require visible video within 600 inputs, and reproduce a frame after restarting from the initial state.
- Write a recovery snapshot with 120 inputs and a candidate, then make an unsaved edit. The parent terminates that specific writer process without normal disposal. A new process restores exactly the committed inputs/candidate/position and matching MEM1, excluding the unsaved edit.

The forced-termination case tests the shared recovery writer and reader; it does not claim that every possible interruption during every disk-write phase has been exercised. Existing managed tests cover atomic asset/manifest cleanup and UI recovery behavior separately.

Reproduce with a locally built native core:

```powershell
powershell.exe -NoProfile -File scripts/verify-workflow.ps1 -Rom 'C:\path\to\game.iso'
```

The script creates isolated writable profiles, retains logs and JSON results, rejects reused output directories, and never terminates an existing app instance. Evidence from this run is in `artifacts/workflow-verified/results.json` and its adjacent logs/frame hashes. UTC +1 day and 1×→2× transitions have separate evidence in `artifacts/workflow-settings-create.log` and `artifacts/workflow-settings-restore.log`.

## Deferred until approaching 1.0: safe project updates

September 7 priority change: implement [C# experiments](csharp-experiments.md)
first. The compatibility work below remains planned, but is no longer next.

This pass validates a fixed build; automatic cross-build migration is not implemented. Before encouraging long-lived TAS work across updates:

1. Retain a project-compatible runtime alongside updates and identify the exact core/host/resources used. Opening an older project should offer its retained compatible runtime rather than silently upgrading its states.
2. Migrate into a new project folder, leaving the source project and assets intact. For a power-on project, create a new baseline under the selected runtime/settings, retain the input movie and candidate input data, invalidate old states and candidate validity, then replay and compare checkpoints. Record migration provenance and divergence evidence.
3. A project beginning from an imported/captured state needs its compatible runtime or a separately supplied compatible baseline. ROM plus inputs cannot reconstruct an arbitrary captured starting state. Never rewrite old identity hashes to make the state load.
4. Verify the migrated copy, reopen it in a fresh process, and test recovery before presenting it as ready. Integrity checks, gameplay agreement and pixel agreement must remain separate results.

Broader profile/scene coverage, effective-setting readback, graphics-driver identity and physical controller testing remain independent verification work. One passing Skies sequence does not establish universal determinism.
