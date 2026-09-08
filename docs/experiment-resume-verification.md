# Experiment batch resume verification

Verified on 2026-09-07. The published worker in `artifacts/prod` supports
`resume-csharp <batch-directory>`. The packaged and installed VS Code extension
offers **Resume Experiment Batch** on batch rows and in the Command Palette.

## Automated coverage

- Full managed build: 279 tests passed, zero warnings/errors.
- Resume tests cover completed indices on both sides of a gap, failed trials
  retained, cancelled trials retried, recovery of a finished receipt missing its
  SQLite commit, original index/count/UTC parameters and source/assembly paths,
  separate attempt folders, removal of the old batch cancellation signal,
  finished batches launching no workers or rewriting their SQLite rows,
  coordinator locking, modified compiled snapshots and changed result schemas.
- Three extension checks pass, including interrupted batches without results.json,
  invoking resume from the batch row and Command Palette without saving/building
  current source, and resolving failed-trial logs from the latest attempt.

Build log: `artifacts/resume-full-build.log`.

## Native worker verification

`scripts/verify-resume.cjs` ran three headless Skies of Arcadia trials against a
snapshot of the user's saved project. The batch was cancelled after trial 0
completed, then resumed with the original config deliberately replaced by invalid
test config and the initial build directory moved aside. Resume used the compiled
assembly snapshot, preserved trial 0's result.json bytes, and completed trials 1
and 2 with the original index/count and UTC values. SQLite contained exactly three
trial rows and three result rows. The source project hash was unchanged.

A second resume of the completed batch launched no workers and returned success.
Artifacts, logs and assertions are in
`artifacts/resume-native-0276b2d9833748dc85df4720a7202946/verification.json`.

## Scope

Resume restarts unfinished trials from their saved starting point; it does not
serialize a C# async stack. Completed failures and timeouts remain recorded.
New batches store hashes of the native core/host and managed emulation contracts;
older batches lack that fingerprint and emit a warning rather than claiming a
verified runtime match. Preserve the batch folder, including its frozen source,
assembly, result receipts and SQLite database. Corrupt/missing finalized receipts
are reported instead of silently rerunning or replacing a completed result.
