# Pop-out window and controller input verification

Verified September 6, 2026, on Windows x64 using the packaged
`artifacts/app-v3/TasStudio.App.exe` and the pinned Dolphin runtime.

## Implemented behavior

- The toolbar and View menu offer Pop out / dock; Ctrl+Shift+D works from
  either window. The existing image surface moves between hosts, retaining the
  same core, frame buffer, project, and execution position.
- Alt+Enter toggles game fullscreen. Escape leaves fullscreen before its normal
  Stop behavior applies. Closing the game window redocks it; closing the editor
  closes both windows and disposes the emulation and input workers.
- The detached editor expands the timeline, with its ruler aligned at the top.
  The optional memory watcher remains available. Docking restores the previous
  timeline height; exiting while detached saves that docked height.
- F10/F11, slot shortcuts, and keyboard bindings share handlers across windows.
  Focus and modal events update input acceptance immediately. Text entry in one
  window does not incorrectly suppress controls in the other window.
- A dedicated background thread samples XInput devices with an 8 ms wait
  between polling passes. It publishes immutable input and diagnostic samples.
  The controller dialog reads those samples without calling XInput on its UI timer.
- Settings dictionaries are copied before publication. Keyboard changes, focus
  changes, configuration swaps, and sample composition are synchronized; focus
  loss cannot be overwritten by an in-flight stale pressed-input sample.

## Evidence

`./scripts/build.ps1 -SkipNative -Publish -PublishDirectory artifacts/app-v3`
completed with zero build warnings/errors and **41 passing tests**. The local
build log is `artifacts/popout-build.log`.

Four input tests cover sampling on a different thread without UI ticks,
connection loss, shutdown, configuration copies, keyboard release and focus
blocking, an in-flight device read during focus loss, and controller diagnostics
while modal input is neutral. The existing 37 domain tests also pass.

Windows UI checks used Skies of Arcadia Legends and the previously verified
360-input folder project. Observed:

- Detach and fullscreen transitions preserved the paused state at 360.
- Escape restored the normal game window without stopping the core.
- F11 in the detached window advanced exactly once to 361; closing the game
  window returned that same preview to the editor. The extra test input was
  discarded, preserving the saved fixture.
- Ctrl+Shift+D detached from the editor and redocked from the game window.
- The watcher remained visible in the detached editor; docking restored layout.
- Closing the editor with a detached game exited the process. After the layout
  regression fix, the persisted timeline height remained 320 instead of the
  expanded 770 pixels.
- The final package reopened the saved project at 360, displayed corrected text,
  and passed another detach/redock and watcher-layout check.

## Limits

No physical XInput controller was exercised during this pass; the worker tests
use simulated devices. Multi-monitor DPI transitions were not tested.
Keyboard event delivery and image presentation still run on Avalonia's UI
thread. A blocked UI can delay those; emulation execution and XInput sampling
run independently. The existing image presentation refresh is approximately
30 Hz. No native core or project-format changes were made in this iteration;
the prior fresh-process replay/MEM1 comparison is documented separately.
