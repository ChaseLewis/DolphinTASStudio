# Frontend acceptance through M2

Prepared 2026-09-06 from `PLAN.md` and official Dolphin documentation/source. This is a review checklist, not a claim that the checks have passed. Record actual evidence in the implementation verification report.

The local acceptance test uses a user-supplied Skies of Arcadia Legends (USA) image. Keep the image and generated game data out of source control. The application must also accept a different user-selected game; the test path is not a product default.

## Familiar Dolphin conventions

Use File, Emulation, Options, and View menus. Put Open in File; Play/Pause, Stop, Reset, Frame Advance, and state operations in Emulation. Expose common operations in the toolbar as well. Dolphin has ten state slots, separate file save/load actions, a selected slot, and state descriptions. [Dolphin menu source](https://github.com/dolphin-emu/dolphin/blob/a2efdf1197be8132674b90fe9cf4761df39752ed/Source/Core/DolphinQt/MenuBar.cpp)

Match these Windows shortcuts where implemented:

| Action | Dolphin default |
|---|---|
| Open game | Ctrl+O |
| Play/Pause | F10 |
| Stop | Escape |
| Fullscreen | Alt+Enter |
| Load slots 1–8 | F1–F8 |
| Save slots 1–8 | Shift+F1–F8 |
| Undo load | F12 |
| Undo save | Shift+F12 |

Dolphin's F11 means debugger Step Into. Frame Advance has its own action and no default in the inspected defaults. If Studio assigns F11 to frame advance, document it as a Studio shortcut. Hotkey display and dispatch should share one command definition. [Dolphin hotkey defaults](https://github.com/dolphin-emu/dolphin/blob/a2efdf1197be8132674b90fe9cf4761df39752ed/Source/Core/Core/HotkeyManager.cpp)

Use Controllers as a recognizable toolbar/settings entry. Show GameCube Port 1, its Standard Controller device type, and Configure; show unsupported ports explicitly as unavailable or None. In Configure, provide a physical Device selector and group mappings by Buttons, D-Pad, Control Stick, C Stick, and Triggers. Offer a Defaults action. Named profiles and calibration can follow as settings grow. Dolphin exposes separate analog and digital trigger mappings. [Controller guide](https://dolphin-emu.org/docs/guides/configuring-controllers/), [mapping layout](https://github.com/dolphin-emu/dolphin/blob/a2efdf1197be8132674b90fe9cf4761df39752ed/Source/Core/DolphinQt/Config/Mapping/GCPadEmu.cpp)

The inspected Dolphin Windows keyboard defaults are:

| GameCube input | Keyboard mapping |
|---|---|
| A / B / X / Y / Z | X / Z / C / S / D |
| Start | Enter |
| Control Stick up/down/left/right | Arrow keys |
| C Stick up/down/left/right | I / K / J / L |
| D-Pad up/down/left/right | T / G / F / H |
| L / R digital clicks | Q / W |
| Control Stick / C Stick modifier | Shift / Ctrl |

The keyboard defaults set square calibration shapes. Do not claim fully identical Dolphin analog behavior merely by copying the keys: its stick processing includes calibration, gates, and modifiers. Studio's physical mapping must produce documented canonical integer values, while its timeline bypasses physical dead zones and calibration. [Dolphin controller implementation](https://github.com/dolphin-emu/dolphin/blob/a2efdf1197be8132674b90fe9cf4761df39752ed/Source/Core/Core/HW/GCPadEmu.cpp)

The source links pin the official repository HEAD observed during research. The selected backend's own pinned source and verified behavior remain authoritative for compatibility.

## Application startup and game lifecycle

- [ ] The documented build/run command opens the real Avalonia application on Windows x64 without an IDE.
- [ ] A fresh settings directory produces a usable window with a visible Open Game command and clear empty viewport.
- [ ] Open Game presents a native picker for supported image types; cancellation leaves the current session unchanged.
- [ ] Selecting the Skies of Arcadia image boots it and displays moving game output inside the Studio viewport.
- [ ] The UI distinguishes Loading, Running, Paused, Stopped, and Error. The title/status identifies the actual loaded game and position.
- [ ] Missing images, unsupported files, missing core/resources, and startup failures produce actionable messages. A failed load can be followed by a successful load without restarting Studio.
- [ ] Pause reaches a backend safe boundary. Frame Advance performs precisely one backend interval and remains paused. Position advances only on completed steps.
- [ ] Run and Pause work repeatedly. Reset returns to a documented initial execution state and does not silently preserve an incompatible timeline.
- [ ] Stop unloads the current game. Open → Stop → Open works, as does closing Studio while a game is running.
- [ ] Resize and minimize/restore retain aspect ratio and redraw the paused image. Closing a dialog does not leave a blank viewport.
- [ ] Commands unavailable without a game are disabled; opening Controllers and application settings remains possible before loading a game.
- [ ] A small memory viewer or runnable integration harness reads known RAM and rejects unsupported ranges coherently.

## Save/load-state workflow

The minimum user flow must be visible in the launched app: **Open Game → Pause → Save State → Advance/Run → Load State**. File-based states should be available even if temporary slots are also implemented.

- [ ] Save State to File opens a destination picker and captures at a safe boundary. It reports success only after the durable file is written.
- [ ] Load State from File opens a source picker and restores both backend state and displayed position. It redraws immediately while paused.
- [ ] Slot selection is explicit. Save/Load Selected Slot gives a clear empty-slot message or disables empty loads. The slot belongs to the loaded game/session identity.
- [ ] State files contain a versioned compatibility envelope with game hash, backend identity, effective configuration, and position. A filename alone is not identity.
- [ ] Wrong-game, wrong-backend, changed-configuration, truncated, and corrupt states fail before mutating execution whenever validation can detect the issue.
- [ ] Save a state, advance at least 60 intervals, restore, and verify the restored position and game observations; replay the same inputs and compare again.
- [ ] Close Studio, reopen the same game/environment, and load a previously saved compatible state.
- [ ] Save/load failures leave a coherent paused session and do not overwrite a known-good file with partial content.
- [ ] Restoring flushes stale queued audio; resuming does not play sound left over from before restoration.
- [ ] Studio state files are labeled as Studio states. Do not imply arbitrary standalone Dolphin states are compatible unless explicitly implemented and verified.
- [ ] Loading a manual state during an existing TAS project establishes a documented new baseline or rejects an incompatible history; it must not pretend unrelated input history reproduced that state.

## Controller and configuration acceptance

- [ ] Keyboard mappings are visible, editable, persisted, and resettable to the documented Dolphin-like defaults.
- [ ] Gamepad selection identifies connected devices. Refresh/hotplug behavior and disconnect state are visible.
- [ ] Every GameCube button, both sticks, analog triggers, and independent L/R clicks has a mapping or explicit editor control. Show current canonical values for user testing.
- [ ] Input clears on focus loss and key release. Typing in paths, project names, memory addresses, or mapping fields does not also press game buttons. Hotkeys do not fire while capturing a binding.
- [ ] Live input is accepted only in the declared live/record mode. Playback reads recorded snapshots and ignores physical devices.
- [ ] The user can test device axes, dead zones, trigger pressure, and reconnect behavior without editing source files.
- [ ] Keyboard and gamepad settings persist across a fresh process. Invalid settings recover with an understandable message and safe defaults.
- [ ] App data, Dolphin user data, and project storage locations are separate and discoverable. No automatic writes target the user's standalone Dolphin installation/settings.
- [ ] Settings expose only functioning backend options. Use familiar groups such as General, Graphics, Audio, and GameCube; do not show unsupported renderer choices as functional controls.
- [ ] Emulation-affecting options are fixed or recorded in the project. An option needing restart says so and cannot silently change an existing replay environment.
- [ ] Mute and volume act on real audio. Paused and stopped states do not accumulate an unbounded audio backlog.

## M2 end-to-end project acceptance

- [ ] Create a project from a documented boot or captured state, with stable game/core/configuration/storage identity.
- [ ] Record a short known sequence with neutral intervals and one recognizable button press. Rows show the exact snapshots submitted to the core.
- [ ] Pause and edit that button on row N. The change takes effect while executing input N, from state N to N+1.
- [ ] Replay restores the initial state and required storage, uses only timeline input, and reaches the expected observation.
- [ ] Seek to 0, N, and a later row by restore/replay. Position remains truthful while seeking, and long operations keep the UI responsive.
- [ ] Save the project atomically, close the entire process, reopen the project, and reproduce the recorded observations.
- [ ] Reopening validates the game hash, backend, format, input schema, effective settings, and initial state. Missing game paths can be relocated only after verifying identity.
- [ ] Editing saved project metadata to incompatible values is rejected clearly; corrupt/truncated content does not produce partial canonical data.
- [ ] An interrupted or failed save preserves the previous valid project. Temporary files do not replace canonical content until complete.
- [ ] Unsaved edits are clearly indicated and preserved or explicitly resolved before destructive session/project replacement.
- [ ] Reset, state loads, and memory writes either have reproducible ordered-event semantics or establish a new explicit starting state. Unsupported mutation must not silently alter project playback.

## Evidence required for handoff

The handoff should include the exact run command, an application screenshot, the test game hash/revision, selected core/build identity, a workflow verification log, focused test results, and a short list of remaining limits. A mock backend can prove UI/domain behavior but cannot satisfy actual game boot, audio, controller injection, state restore, or replay gates.

Controller feel and physical-device support may be handed to the user for the requested manual test. The developer should still verify that the configuration UI opens, edits persist, canonical keyboard input reaches the emulator, and the relevant backend controls are real.
