# Typed experiment result verification

Verified September 7, 2026.

- Full build and publish: 269 Studio tests passed, zero build warnings/errors.
  Log: `artifacts/typed-results-full-build.log`.
- Nine focused database/coordinator tests verify serialized overlapping submissions, prepared schema
  stability across 32 writes, JSONB BLOB storage and nested struct fields, non-null constraints,
  trial-index ownership, cancellation, idempotent replacement, reserved identifiers, full precision
  for decimal/ulong values, and transaction rollback without a half-written trial.
- Coordinator assembly discovery does not construct the experiment. A fixture constructor deliberately
  throws; the runner still discovers its generic result type, creates the database, and persists a
  cancelled batch. The collectible assembly can be released and the batch files removed afterward.
- A worker test verifies generic interface dispatch and nested struct field serialization.
- ElectriSearch: 13 workflow tests passed, covering UTC, unchanged movie playback, neutral waiting,
  encounter/reward detection, unsuccessful outcomes, cancellation, and complete non-null results.
  Log: `artifacts/electri-typed-results-tests.log`.
- Published the actual ElectriSearch project, then used the real runner to discover its ElectriResult
  type and create five NOT NULL result columns. Four parallel dummy workers deliberately failed;
  all four statuses committed to trials and no fabricated rows appeared in results.
  Log: `artifacts/typed-loader-check.log`. No game/emulator ran in this loader check.

Schema setup and command preparation happen once before workers launch. The coordinator owns SQLite
and serializes all writes. Each trial/status and result commit together. trials and results both use
trial_index as their primary key, with a foreign key from results to trials. Failed/cancelled trials
have no typed result row. JSON output remains available if SQLite persistence fails.

The former custom writer/config API and ElectriSearch SQL files have been removed. The authoring
contract is IExperiment<TResult>; non-generic legacy experiments continue to produce JSON.
