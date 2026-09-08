# Frontend verification — 2026-09-06

This records checks performed against the real Avalonia app and the supplied Skies of Arcadia Legends (USA) image. Native/core replay evidence is recorded separately. Physical controller feel and mappings remain for the user's requested manual test.

## Build and startup

- `dotnet build src/TasStudio.App/TasStudio.App.csproj` completed with zero warnings and zero errors.
- Avalonia 11.3.20 and NAudio.WinMM 2.2.1 are pinned. The earlier Avalonia 11.3.12 evaluation was upgraded after NuGet reported a vulnerable transitive dependency.
- Fresh app launch displayed enabled Open Game and Controllers commands, with game-dependent commands disabled.
- The actual Controller Settings dialog opened and rendered its Port 1 device selector, keyboard/Gamepad mapping columns, grouped mappings, and Save/Cancel controls. Its body scrolls to sticks, triggers, and mapping options.
- Artifact captures: `artifacts/frontend-empty.png` and `artifacts/frontend-controllers.png`. These capture early layout verification; subsequent changes include explicit 4:3 game presentation and a Cancel seek control.

## Actual Windows UI workflow

Using the installed computer-use skill's `@oai/sky` API, with observed Windows screenshots and accessibility trees:

1. Launched the application and clicked Open Game.
2. Entered the user-supplied ISO path in the native Windows file picker and opened it. The app identified the loaded game, paused at state 0, and enabled playback, states, and project creation.
3. Clicked Play / Pause and observed the position advancing. Paused through F10.
4. Saved slot 1 through Shift+F1 at state 2642; advanced through F11 to state 2643; restored through F1 and verified state 2642 plus the game title image.
5. Opened Emulation → Save State to File, saved `artifacts/ui-manual.tasstate` through the native picker, and verified the resulting 16,369,350-byte file.
6. Advanced to state 2643; opened Emulation → Load State from File; selected the saved file in the native picker and verified restoration to state 2642.
7. Closed the app with its title-bar close button; the process exited normally.

The test discovered that unqualified F1–F8 handling intercepted Alt+F4. Hotkeys now require exact modifiers, and the rebuilt app subsequently closed normally through Alt+F4.

## Live rendering and aspect correction

The first running UI initially retained a black bitmap while its position advanced. Bitmap lock lifetime now ends before requesting Avalonia redraw. A rebuilt, fresh process was then observed running the actual game: the image at state 1204 showed the opening prologue line, and the image at state 1911 showed the later complete paragraph. This verifies changing live frames rather than merely a restored preview.

Dolphin's raw frame dimensions do not necessarily imply square pixels. The game image now fills a fixed 640×480 logical surface inside an aspect-preserving Viewbox. The observed displayed image measured approximately 879×660, matching 4:3 within rounding.

## Remaining manual checks

- Test the intended physical controller, button feel, analog dead zone, trigger pressure/click threshold, and disconnect/reconnect behavior. The configuration dialog exposes XInput connectivity and raw values; native GameCube adapters and rumble are explicitly unavailable in this prototype.
- Listen for output quality, latency, mute, and volume behavior. The app uses bounded stereo buffering and flushes pause/restore audio; hardware listening was not performed by this automated review.
- Native core and service tests cover longer replay, compatibility rejection, and fresh-process reproduction; the packaged project UI check below separately verifies the frontend commands.

## Packaged M2 project UI acceptance

The final `artifacts/app/TasStudio.App.exe` was launched and exercised through real Windows pointer/keyboard interaction without rebuilding:

1. Opened the supplied ROM through the native game picker, created New project, and pressed F11 three times. The UI showed three neutral input rows and state 3.
2. Selected row 1, checked A, and clicked Apply input. Row 1 changed to A; rows 0 and 2 remained neutral.
3. Saved through the native project picker to `artifacts/ui-project.tasproj` (2,901,950 bytes). The unsaved marker cleared.
4. Clicked Stop, then Open in the project panel and selected that archive in the native picker. The reopened project showed state 3, three input rows, and A preserved on row 1.
5. Selected state 0 and clicked Seek; position became 0. Set target 3 with the numeric control and clicked Seek; position returned to 3 with the same input rows and no unsaved marker.

This packaged UI sequence passed without a source change. The test project remains available as a local artifact.

## Capture helpers

The app supports optional `--rom <path>`, `--autoplay`, `--screenshot <absolute.png>`, and `--exit-after-capture` arguments. The screenshot helper pauses after five seconds and captures the current real window. `--controller-screenshot <absolute.png>` opens, captures, and closes the actual controller dialog. These helpers supplement the Windows UI workflow above; they do not stand in for file-picker or state-command verification.
