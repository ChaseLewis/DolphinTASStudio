# Trial retention and cancellation verification

Verified on 2026-09-07. Supersedes the per-trial receipt retention requirement in
the earlier resume verification report.

- SQLite now commits the full trial receipt together with status and typed result.
  Resume skips committed trials without reading any per-trial files. Legacy
  databases reconstruct typed results directly from SQL columns, including JSONB,
  booleans and wide numbers. Older databases did not store position/frame counters;
  those legacy counters default to zero, while new receipts preserve them exactly.
- Successful typed trials skip result timeline export. After worker exit and a
  successful SQLite commit, successful/cancelled trial directories are removed.
  Failed/timed-out artifacts and failed database writes retain their evidence.
- Cleanup derives each target from the verified batch directory and trial index,
  checks containment and refuses filesystem links. Locked leftovers are reported
  and can be retried by resume or standalone cleanup.
- Cancellation stops scheduling instead of manufacturing a cancelled result for
  every unstarted index. In-flight callbacks finish their commits; pending indices
  have no rows/files and remain available to resume.
- `clean-csharp <batch-directory>` acquires the coordinator lock, prunes committed
  successful artifacts and removes the old synthetic "Cancelled before launch."
  records/folders. Actual interrupted attempts and failures are retained.

## Evidence

`artifacts/retention-full-build.log`: 284 managed tests passed, zero warnings/errors.
Coverage includes SQLite-only resume, legacy typed values, failed commit retention,
removal of synthetic cancellation rows without deleting real interrupted trials,
and cancellation of a 50,001-trial schedule after at most four callbacks start.
The three extension tests also pass.

`scripts/verify-resume.cjs` ran four native headless trials. Cancellation after
trial 0 left the rest pending instead of creating a batch-sized result array.
After resume, trials 0–2 existed only in SQLite and trial 3 deliberately failed,
retaining its diagnostic folder and result project. Original indices/UTC and
the source project hash were preserved. A repeated resume launched no workers.

Native logs and assertions:
`artifacts/retention-native-94658e3662dd443486181a8e13dac617/verification.json`.
Standalone cleanup was then exercised by creating a simulated leftover folder
for committed trial 0; it removed that folder and retained failed trial 3.

The production build was published to `artifacts/prod`; the updated VS Code
extension was packaged and installed. The user's previously observed ElectriSearch
batch folder had already disappeared before cleanup, so no user batch was deleted
as part of verification.
