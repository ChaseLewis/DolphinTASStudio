# Trial preview window verification

Verified September 7, 2026.

- `Headless` defaults to true in C# configs, batch definitions, and worker jobs.
  Missing fields in existing configs/jobs retain background behavior. False propagates
  from the coordinator to each worker job.
- Full build and publish: 275 tests passed, zero warnings/errors.
  Log: `artifacts/worker-preview-full-build.log`.
- Preview tests verify latest-frame rendering, automatic closure after completion,
  and cancellation of only the displayed trial while waiting for result cleanup.
  Studio's Headless checkbox is restored with the experiment draft.
- Two real native workers booted the saved Skies project in isolated sessions with
  Headless false. Both had distinct native window handles/titles and logged advances.
  Closing trial 0's window produced a cancelled result; trial 1 continued. Trial 1
  then cancelled through its own cancel file. The source project hash was unchanged.
  Evidence: `artifacts/worker-preview-native-ada7b539126e47538b79a7f37833e9be/windows.json`
  and each run's `result.json`/`output.jsonl`.

Each visible worker starts Avalonia on an STA UI thread. Emulation and script execution
stay on their existing background/owner threads. The view keeps only the latest video
frame, displays at roughly 30 Hz, and accepts no emulator input. Audio remains silent.
Headless workers do not initialize a desktop application. Both modes continue the same
emulation/rendering behavior internally. Windows close automatically when their trial
finishes, so large batches do not retain completed preview windows.
