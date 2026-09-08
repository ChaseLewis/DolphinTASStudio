# Contributing

Start with [README.md](README.md) for build requirements and the
[C# experiment guide](docs/csharp-experiments.md) for automation contracts.
This is a development-stage project; prefer focused changes with explicit validation.

## Development loop

`just dev` reuses a stable local workspace and an existing running instance. Close
that instance before rebuilding. `just dev-unique` uses isolated settings for manual
testing. Build native code with `just build`; after that, managed-only changes can
use `scripts/build.ps1 -SkipNative`. Do not replace DLLs in a running distribution.

Run managed checks without a ROM:

```powershell
dotnet test tests/TasStudio.Core.Tests -c Release
dotnet build examples/csharp/Basic.Experiments -c Release
cd extensions/tas-studio
npm run check
```

Use `scripts/test-integration.ps1` with a locally supplied ROM for native timing,
save-state, replay or shutdown changes. Use `scripts/verify-resume.cjs` for native
worker cancellation/retention/resume checks. Trace changes also require the dedicated
buffer tests and, where applicable, ROM-dependent trace/replay comparison. A successful
managed test does not establish native determinism or trace completeness.

## Invariants to preserve

- The emulator executes on its owner thread. UI refresh and experiment SDK calls
  marshal to that thread; independent trials use separate processes.
- Canonical inputs include controller polls and timing; editable frame groups end
  at presentation boundaries. Do not equate advances, video fields and polls.
- Existing recorded input wins during advance. Script takeover explicitly authors
  current input. Earlier edits require a seek and invalidate dependent states.
- Project input edits persist even before advancing. Saving, recovery, ROM
  relocation, and checkpoint validation must not silently discard user work.
- `context.Movie` and source snapshots stay immutable. Initialization has no emulator.
- The coordinator serializes SQLite transactions. Clean artifacts only after a
  successful commit and worker exit; never delete uncommitted recovery evidence.
- Cancellation stops new scheduling rather than creating results for pending indices.
- Do not reintroduce the removed embedded scripting runtime/editor.

Use small typed game wrappers rather than scattered memory addresses. Document
which game revision a map applies to. Tests should model recorded-input preservation,
state invalidation and failure paths, not just mirror helper implementations.

## Public repository hygiene

Keep ROMs, saves, projects, traces, private configuration, credentials, build output,
and `.runs` out of Git. Use generic paths in examples. Do not add files from another
private checkout merely to make a local build pass. The source revision and patches
for Dolphin live in this repository; its generated checkout does not.

Before a public commit, review `git status` and run:

```powershell
python scripts/check-publication.py
```

The check scans the candidate Git file set, including untracked non-ignored files,
for forbidden artifacts, common credential patterns, private Windows user paths,
and broken local Markdown links. It reports locations, not secret values. It is a
heuristic review aid, not a guarantee that all sensitive content has been detected.

Application-owned contributions follow the existing GPL-2.0-or-later license.
Retain upstream copyright/license notices when changing or importing code.
Binary release packaging must preserve third-party notices and corresponding-source
information; publishing the source repository alone does not create a vetted installer.

For issue reports, include build/commit, native core identity, relevant settings,
the smallest reproduction steps and sanitized logs. Do not upload a ROM or personal
save/project data just to report a UI or build problem.
