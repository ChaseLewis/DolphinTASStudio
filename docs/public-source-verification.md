# Public source preparation — September 7, 2026

The public snapshot removes the deprecated Lua runtime, editor, examples and their
package references. C# class-library experiments and the local VS Code extension
are the supported authoring path. The workspace generator embeds the same example
source compiled by `Basic.Experiments`, avoiding a separate stale template.

The README documents build/setup, input timing, project persistence, experiments,
results and native tracing. The experiment guide describes the actual SDK, typed
schema, worker isolation, explicit input takeover, state cleanup, and resume.
Machine-specific paths were replaced with generic examples. The full existing GPL
license text and extension license are included, and Lua-only dependency notices
were removed. The publication checker scans the candidate source tree, not history.

## Checks performed

- Release solution build: zero warnings/errors, including both example experiments.
- Managed suite: **264 passed**, no failures or skipped tests. Removed Lua-only
  tests account for the smaller suite; the docking test now expects four panels.
- VS Code extension: **3 tests passed**, JavaScript syntax check passed.
- Native trace buffer: **1 CTest passed**. Both native patches also applied to the
  pinned unmodified upstream tree using a separate index, leaving its working
  checkout unchanged.
- A fresh self-contained app/worker distribution published successfully with
  existing normal native binaries. It contains no MoonSharp/AvaloniaEdit DLLs.
- The published basic example and a newly generated standalone workspace both
  compiled with zero warnings/errors against that distribution's SDK.
- A real-core four-trial retention/resume fixture passed using the fresh worker:
  cancellation left unscheduled indices pending; committed successes resumed from
  SQLite without rerunning; original indices/UTC and the source project hash were
  preserved; three successful result rows remained and only the deliberately failed
  trial retained its directory. A second resume launched no workers.
- Candidate-file publication scan passed, including local Markdown links and common
  credential/personal-path patterns. The only image was inspected: the TAS logo concept.

Local evidence is under ignored `artifacts/public-*` paths. These runtime outputs
are not included in the public source. The emulator check used a locally supplied
game and a copied test project; neither is distributed.

## Scope

The managed publish reused the normal native build; this preparation did not perform
a full native rebuild or repeat the ROM-dependent trace-parity study. Its previously
observed limitations remain documented in [native tracing](native-tracing.md).
Source publication does not constitute a vetted external binary installer release.
Heuristic secret checks are not an exhaustive security audit.

The public branch starts with one root commit. Existing local branch history is
preserved separately and is intentionally excluded from the public ancestry.
