# TAS Studio Experiments

Manage C# experiments from VS Code. Requires the .NET 10 SDK and a matching TAS
Studio build. Set `tasStudio.workerPath` to its `TasStudio.Worker.exe`.

Run **TAS Studio: New C# Experiment Workspace**, select a saved `.tasproj`, then
edit `Experiment.cs` and `experiment.tascsharp.json`. The **TAS Experiments** view
in Explorer lists configurations, batches and results. Run builds once and starts
isolated trials; Cancel stops the current batches. Query successful typed results
in SQLite; retained failed-trial projects can be opened in Studio for debugging.
Install Microsoft's C# extension for IntelliSense and debugging.

Each run snapshots the saved source project and compiled dependencies. Save input
edits in Studio before running: this version does not connect to its unsaved
in-memory session. Failed-trial logs are in `.runs/<batch>/run-00001/` (or its latest attempt folder).
Structured `context.Log(...)` entries are in `output.jsonl`; console output and
errors are in `console.log` / `error.log`.

Implement `IExperiment<YourResult>` to store typed results automatically in the batch's
`results.sqlite`. The runner creates the schema once and serializes all writes. `trials`
contains statuses/errors; `results` contains successful typed values. Both use the
runner's `trial_index` primary key. Nested objects and collections are JSONB columns.

Use **Resume Experiment Batch** on a batch in the TAS Experiments view, or from
the Command Palette. Resume reuses the saved movie, compiled script and parameters;
it does not rebuild your current source. Completed trials are skipped by index,
including trials that finished out of order. Interrupted/cancelled and unstarted
trials restart from their original boot/save-state start. Finished failures and
timeouts remain recorded. Resume does not continue inside an individual RunAsync.

The existing SQLite database is retained and includes full result receipts; resume
does not require per-trial files for committed results. Trial JSON is recovered if a
crash occurred before its database commit. Successful and cancelled trial folders
are removed after commit and worker exit; only failed/timed-out diagnostics remain.
Successful trials also skip exporting a result timeline. Database write failures
preserve artifacts for recovery. New attempts use `run-00001/attempts/`. The original count and
indices stay fixed, preserving index-based UTC. A batch can have only one coordinator.
Keep the same emulator build: new batches check runtime hashes on resume; older
batches without hashes display a warning. The compiled snapshot and result schema
are checked before scheduling. Batches appear even if a crash prevented results.json.

CLI equivalent: `TasStudio.Worker.exe resume-csharp "path/to/.runs/batch"`.

Cancellation stops scheduling immediately and waits for in-flight workers. Unstarted
indices have no result rows or folders and remain pending for resume. To clean an
older stopped batch, run `TasStudio.Worker.exe clean-csharp "path/to/.runs/batch"`.
This removes committed successful artifacts and old synthetic "cancelled before
launch" placeholders, preserving actual failures and interrupted work. It refuses
to clean a batch while its coordinator is running.

Set `"Headless": false` in the experiment config to watch each active trial in a
separate preview window. The default is true. Closing a preview cancels only that
trial; finished trials close automatically. Preview windows are silent and read-only.

This extension invokes trusted local C# code. Its worker processes isolate
emulator sessions, not filesystem permissions. The extension honors VS Code
workspace trust. No AI account or MCP is required; AI editor tools can work with
the normal C# source files.

For development, open this directory with `--extensionDevelopmentPath` or package
with `npx @vscode/vsce package --allow-missing-repository` and install the VSIX.
