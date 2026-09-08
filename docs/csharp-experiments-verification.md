# C# experiment verification — September 7, 2026

## Current-input API follow-up

`SetCurrentInputAsync` resolves the current group and edits it in one owner-thread
operation. `artifacts/set-current-input-build.log` records 225 passing tests and a
successful production publish with zero warnings/errors. New SDK tests verify
last-write-wins replacement, actual poll inputs on advance, unchanged later
groups/original movie/source project, downstream state invalidation, unadvanced
end-input persistence, invalid-button rejection, and explicit seek after earlier
history edits. The native fixture now uses this method; the native results below
predate this convenience-method change.

## Automated tests

`artifacts/csharp-final-verification.log`: full solution build, 223 tests passed,
zero warnings/errors, production app and worker published. The SDK adds no NuGet
runtime dependency. New tests cover narrow initialization capabilities, exactly
one boot with the selected UTC, invalid UTC and state-UTC rejection before boot,
initialization cancellation, immutable original inputs, input preservation,
explicit edits/seeks, owner-thread confinement, memory events, saved-state restore,
candidate output, failed-run persistence, and closed runtime contexts.

`npm run check` in `extensions/tas-studio`: syntax check and two helper tests pass.
The extension is packaged in `artifacts/tas-studio-experiments-0.1.0.vsix`.

`artifacts/csharp-packaged-example.log`: the shipped Skies library and experiment
build successfully against SDK DLL references in the production directory, with
zero warnings/errors. This caught and fixed a missing direct SDK reference that
repository ProjectReference transitivity had masked.

## Native Skies checks

`scripts/verify-csharp.cjs` ran against the existing local Skies smoke project,
whose ROM path comes from the user's local project. It did not alter that project.

`artifacts/csharp-native-20260907-015459/verification.json` records:

- Three power-on C# trials, two concurrent workers; Initialize selected
  946684800, 946684801 and 946684802 before boot.
- Three saved-state trials with two workers and two-group preroll; source UTC
  retained and source group numbering preserved.
- The same initialized experiment object reached RunAsync. A separate Skies
  class-library dependency resolved from the snapshotted publish output and read
  game RNG memory. Original movie length was 30 groups.
- Each runtime explicitly edited the current input, advanced while preserving
  that recording, saved/restored a named state and returned a candidate take.
- Cooperative cancellation retained a result project with completed edits.
- A deliberately infinite Initialize loop timed out and its owned worker was
  terminated; no emulator state/result project was invented for it.
- Source manifest SHA-256 remained unchanged.

## VS Code extension host

Launched the installed VS Code binary using isolated test user-data/extensions
directories, with the extension in development mode. Its normal user profile and
installed extensions were not modified. The generated workspace at
`artifacts/csharp-vscode-20260907-015545` was compiled and run through the real
`tasStudio.run` command. `host-verification.json` records activation, command
registration and successful native execution with a result project. The host
exited with code 0. VS Code emitted unrelated account/agent-host startup warnings
in `artifacts/csharp-vscode-host.log`; the extension's assertions passed.

## Limits

The extension currently runs saved projects, not an unsaved live Studio session.
It does not supply a dedicated debugger launch handshake or MCP server. The Skies
library addresses target the scanner's revision; the full Electri search route
was not validated. Four workers are supported; native tests used two. C# is
trusted local code, not sandboxed. No changes to Dolphin's native source were
needed for this feature. Cross-build project migration remains deferred.
