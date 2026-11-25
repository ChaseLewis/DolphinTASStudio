# Dolphin TAS Studio

A Windows desktop workspace for creating and testing GameCube tool-assisted input
recordings, built around a pinned, modified Dolphin Libretro core.

Edit inputs, advance one presented frame at a time, keep alternative takes, inspect
memory, and run C# experiments in isolated emulator processes. Experiments can replay
a saved movie, take over at a chosen state, vary UTC or other parameters by trial
index, and write typed results to SQLite across thousands of resumable trials.

**Development software:** project formats, native contracts, and APIs can change.
Keep your source projects backed up. Repeatability targets a fixed emulator build,
game image, configuration, and machine setup; identical output across GPUs or
arbitrary Dolphin versions is not promised. GameCube is the primary target. Wii
and controllers beyond the implemented keyboard/XInput paths are not validated.

This is an independent frontend, not an official Dolphin release. No game images,
BIOS files, save data, or game-derived trace captures are included.

## Start here

- [Build and run](#build-and-run)
- [Editing a TAS](#editing-a-tas)
- [Playing by hand and making test fixtures](#playing-by-hand-and-making-test-fixtures)
- [C# experiments](#c-experiments)
- [Builds with and without tracing](#builds-with-and-without-tracing)
- [Experiment authoring guide and AI contract](docs/csharp-experiments.md)
- [Compilable game-independent example](examples/csharp/Basic.Experiments/PulseExperiment.cs)
- [Skies of Arcadia utility library](examples/csharp/Skies/README.md)
- [Contributing and verification](CONTRIBUTING.md)

## What is implemented

| Area | Capabilities |
| --- | --- |
| Workspace | Draggable, resizable, tabbed and floating game, timeline, input and memory panels; persistent layouts |
| Appearance | Light, Dark and System themes; System by default; editable palette embedded at build time |
| Input editor | Interactive sticks, numeric axes, trigger pressure, controller input, alternating-frame turbo |
| Timeline | Poll-based ruler, frame-group selections, alternative takes, visual tags, seek, undo/redo |
| Projects | ROM identity/relocation, editable settings and start UTC, disk-backed states/checkpoints, saves and recovery |
| Play mode | Direct keyboard/XInput play, persistent Slot A cards, raw card and GCI folder import, start TAS from current Play |
| Memory watcher | Grouped watches, typed display, pointer offsets, double-click editing, Dolphin Memory Engine `.dmw` import |
| Experiments | C# class libraries, pre-boot initialization, immutable original movie, up to four isolated workers |
| Results | Per-experiment typed SQLite columns, JSONB nested values, serialized writes, batch resume, failed-trial diagnostics |
| Diagnostics | Optional native instruction/call tracing in a separate build |

## Build and run

The supported build target is **Windows x64**. Install:

- Git, including submodule support.
- .NET **10 SDK** (needed to compile both Studio and experiments).
- CMake **4.2 or newer** (the Visual Studio 2026 generator requires 4.2).
- Visual Studio **2026** with Desktop development with C++, MSVC x64 tools and a Windows SDK.
  The scripts select the `Visual Studio 18 2026` CMake generator explicitly.
- [`just`](https://github.com/casey/just), optional if you invoke the PowerShell scripts directly.
- Node.js/npm and VS Code if you want to package/use the local experiment extension.

Run commands from the repository root:

```powershell
just dev          # Stable local dev workspace; reuse its open instance
just dev-unique   # Separate workspace and isolated local settings
just build        # Native build + managed Release + tests + self-contained publish
```

No `just` installed? The equivalent commands use built-in Windows PowerShell:

```powershell
powershell.exe -NoProfile -File scripts/dev.ps1
powershell.exe -NoProfile -File scripts/dev.ps1 -Unique
powershell.exe -NoProfile -File scripts/build.ps1 -Publish -PublishDirectory artifacts/prod
```

The first native build downloads the pinned Dolphin checkout and its submodules,
applies the repository patches, and compiles the host/core. Expect it to take
substantially longer than a managed-only rebuild. The upstream commit is pinned in
[build.ps1](scripts/build.ps1); patches are checked before application.

After native dependencies exist, rebuild managed code without recompiling Dolphin:

```powershell
powershell.exe -NoProfile -File scripts/build.ps1 -SkipNative -Publish -PublishDirectory artifacts/prod
```

### Create a GitHub Release

The **Create GitHub release** workflow builds and publishes a permanent, versioned
Windows x64 release on demand. Once the workflow exists on the repository's default
branch, open **Actions**, select that workflow, choose **Run workflow**, and enter a
SemVer version without `v`, such as `0.1.0`. Releases must be run from the default
branch. Select the matching update channel: **stable** (`0.2.0`), **beta**
(`0.2.0-beta.1`), or **alpha** (`0.2.0-alpha.1`). Alpha and beta releases are marked
as GitHub prereleases automatically.

After the native build and tests pass, the workflow creates tag `v<version>`, generates
release notes, and publishes the GitHub Release with these assets:

- A Velopack `Setup.exe` installer and a `Portable.zip` distribution.
- A full `.nupkg` update payload and `releases.win-x64-<channel>.json` feed.
- `SHA256SUMS.txt` covering those assets.

The ZIP contains the self-contained Studio and experiment worker, the pinned Dolphin
core and system files, examples, licenses, and other runtime dependencies. The release
asset remains available until the release is manually deleted. `TasStudio.App.exe`
uses the Dolphin TAS Studio logo as its Windows application icon.

### Automatic updates

Release installations and release portable ZIPs check GitHub on launch, download a
newer release from their own channel, and install it after Studio closes normally.
**Options → Updates** controls automatic updates, lets you check manually or change
channel, and offers **Restart to update** after a download. Restart uses the normal
save prompt. Active Studio instances and experiment workers defer installation;
the download remains ready for a later Studio session. Switching channels clears
the queued update and never downgrades; wait for a newer release on that channel.

Velopack supplies a stable `Dolphin TAS Studio.exe` launcher and `Update.exe` beside a
managed `current` directory. Launch the top-level executable; keep the complete
portable directory together. Studio, the worker, SDK, and native core update as a
single package. Preferences, memory cards, recovery data and states remain under
`%LOCALAPPDATA%/TasStudio`; keep your projects and experiment workspaces outside
the installation directory. Each installation has its own update preferences.

Existing plain ZIP builds need one manual installation/download of the first
updater-enabled release. Development builds do not check for updates. Before 1.0,
replay and save-state compatibility is preserved where practical but is not
guaranteed; known changes should appear in release notes. Updates do not deliberately
invalidate otherwise compatible recordings. See [release maintenance](docs/updates.md)
for local packaging and verification.

### Where the executables and data live

| Command | Executables | User data |
| --- | --- | --- |
| `just dev` | `.local/dev/default/app/` | Existing `%LOCALAPPDATA%/TasStudio` settings/recovery |
| `just dev-unique` | `.local/dev/<timestamp>-<id>/app/` | That workspace's `data/` folder |
| `just build` | `artifacts/prod/` | Normal local settings, unless a data-path marker is supplied |

`just dev` reports the existing app's PID when it is already running. Close that
instance and rerun the command to rebuild it. If experiment workers still use that
build, the command reports their PIDs and stops before rebuilding. Wait for the
experiments to finish or cancel them, or use `just dev-unique` to build and run
alongside them. Neither dev command deletes a workspace.
Projects stay wherever you save them; opening the same project from two workspaces
still opens the same files.

Launch **`artifacts/prod/TasStudio.App.exe`**. Experiments use the adjacent
**`artifacts/prod/TasStudio.Worker.exe`**. Keep the whole published folder together,
including `native/`, `system/`, and managed dependencies. Intermediate
`src/TasStudio.Worker/bin/Release/...` output is not a complete emulator distribution.
Launch the apphost `.exe` directly: it carries the CET compatibility setting needed
by Dolphin's JIT exception handling.

If a build is in use, publish into a different directory instead of replacing its
loaded DLLs. `-SkipNative` reuses existing binaries; it does not apply native changes.

Choose **Config → Interface → UI theme** to switch between Light, Dark and System.
Changes apply immediately and are saved for the application, independently of project
settings. To customize colors, edit the embedded
[palette file](src/TasStudio.App/Themes/palettes.json) and rebuild; see
[UI themes](docs/ui-themes.md). Timeline input and marker colors stay consistent.

## Playing by hand and making test fixtures

Choose **Play a game** on the home screen or **File → Play Game** (`Ctrl+O`).
The game starts with your mapped keyboard or XInput controller in a simple game view.
Use **Controllers** to configure input and **Config** to change emulation settings.
Play does not create a project, record inputs, or accumulate automatic checkpoints.
Creating or opening a TAS project switches back to the editor.

| Control | Play mode behavior |
| --- | --- |
| Ctrl+P / Play / Pause | Run with live input, or pause |
| Escape | Leave fullscreen first, then pause |
| F11 / F10 | Advance with current controller input / neutral input |
| Ctrl+S / Save state | Pause and save a named `.tasstate` file |
| Shift+F1–F8 | Save a quick slot; continue playing if already running |
| F1–F8 | Load a quick slot and pause |
| Alt+Enter | Toggle fullscreen |
| Stop game | Shut down and flush memory card writes |

Save normally **inside the game** to use the persistent raw memory card in **Slot A**.
Play uses its own profile under the app's data folder at `Play/DolphinUser/User/GC/`;
TAS projects and experiment workers use isolated profiles. Slot B is empty.

**Memory cards…** lets you choose USA, EUR or JAP and import/export a `.raw` card
(59, 123, 251, 507, 1019 or 2043 blocks). Import copies the selected file and restarts
the game; any replaced card is retained alongside it as a uniquely named `.bak`.
Export flushes Dolphin's card writer, copies the card, and restores your paused
position. Cards are separate by region and size. Match the region to your game.
The dialog shows the active file path; an unused region may have no card to export.

**Import GCI folder…** accepts stock Dolphin's `GC/USA/Card A` (or EUR/JAP) folder.
It copies the `.gci` saves into a new raw card using Dolphin's save importer and
restarts Play. Source saves are unchanged; future in-game saves go to Studio's
Play card. A replaced card is backed up. Folders with duplicate in-game save
identities, corrupt files, or more than a card can hold are rejected.

For a live card owned by a project, use **Options → Project memory card…**.
Choose **Open GCI folder**, copy saves directly into the project's `gci/` folder,
then **Restart from GCI folder**. Dolphin reads the folder at game start and writes
in-game saves back there. Restart preserves timeline inputs but replaces the starting
state and removes old state markers. New power-on projects use this folder by default;
older/raw-state projects switch when you use that restart button.

States embed all loaded GCI saves. Seeking rewinds the card, and experiment workers
restore private copies. Moving the project or using Save As changes the destination
folder correctly. Adding/replacing GCI files requires the explicit restart, since
ordinary project reopening restores its saved timeline and card. Dolphin movie
export currently supports raw-card projects only.

To reclaim experiment storage, use **Experiments → Clean experiment storage…**.
The scan shows an increasing file/byte count and a breakdown of the entire
experiments folder, including archives, fixtures and detached/deleted experiments.
**Clean experiments** removes old states, worker profiles and BMP screenshots,
then losslessly compresses retained input JSON. It shows progress and space freed,
and can be stopped. Main-project assets, experiment baselines and recipe start
states remain protected; active runs are skipped. Results, top plays, logs and
code remain. Removed state markers disappear; cleaned batches cannot resume.

New input/poll/take assets and experiment result receipts use gzip by default.
Studio and the worker load both plain JSON and `.json.gz` automatically. Use
the updated Studio/worker when opening projects with compressed input assets.

To go straight from a stock Dolphin save to experiments:

1. Open the game in **Play**, choose **Memory cards… → Import GCI folder…**, and
   select the region and `Card A` folder.
2. Play and load the desired save through the game's menu.
3. Click **Start TAS here…**, name the project, and create it with **Current Play
   position** selected. The new project starts at group zero with the exact game
   state, settings, and card contents, in a separate writable profile.
4. Create an experiment from the project start or a named state. The project
   embeds the baseline, so it does not depend on the original Dolphin folder.

A **save state includes the memory card contents**. Loading an older state also
rewinds that card, which will then persist to disk. Export the card first if you
want to keep both versions of your in-game saves. Play quick slots are separate
from TAS quick slots. States retain game/build identity, emulation settings and
card size; they require a compatible Studio build.

To build a reusable automation suite:

1. Play to a useful situation and save a descriptively named `.tasstate`, such as
   `before-battle.tasstate`. Repeat for each scenario.
2. Create a **New Project**, choose the same game, and select that save state as
   the starting point. Each project embeds its baseline and starts at group zero.
3. Save one project per scenario, then use its `.tasproj` as `SourceProject` in a
   C# experiment workspace. The worker restores the baseline and card into its
   own profile. The original `.tasstate` and Play card are not required at runtime.
   The generated config uses `"Start": "SaveState", "StateId": "project-start"`;
   keep those values to test the captured situation. `Boot` starts from power-on.

The card-size support requires rebuilding the native core; `-SkipNative` alone
cannot apply the new core patch. A changed core identity means states made with
older builds must be recreated for that build.

## Editing a TAS

1. Create a project and choose your locally supplied GameCube ISO/GCM/RVZ.
2. Choose **Power on** or a compatible **TAS Studio save state** as the starting point.
   Set the start UTC and project settings as needed.
3. Edit the selected frame's inputs in TAS Input. They become part of the project
   immediately, even if you have not advanced yet. Save with **Ctrl+S**.
4. Advance, inspect memory, and mark useful positions. Use takes to try alternatives.
5. Use **Seek** when you want the preview to replay to your selected input position.

Opening a saved project automatically restores/seeks to its saved position. If the
ROM cannot be found, Studio asks you to locate the matching image. The project
references the ROM; it does not embed it.

### Movie Mode and controller capture

Open a TAS project, then choose **Movie → Watch movie from start** or **Watch movie
from here**. Movie Mode plays recorded inputs without accepting live controller
input or enabling editing. It pauses at the recorded end; Play starts again from
the beginning. Use the slider and **Seek** to replay to a frame group, or **Frame
advance** / **F12** to advance one recorded group. **Space** toggles playback.
**Escape** cancels a seek, leaves game fullscreen, or returns to the editor.

**Render resolution (experimental)** selects **Project default** or **1×–4× native**
for the current Movie Mode session. Changes appear on the next advanced frame,
without rebooting or changing the project's saved configuration. Returning to the
editor restores the project resolution. Existing save states remain available for
seeking; automatic checkpoints are suspended while an override is selected.
Higher resolutions cost performance and may affect playback in some games;
recorded poll/timing desync checks remain active.

**Game** and **Controller Inputs** use the same docking system as the TAS editor.
Drag their tabs to dock, tab together, or float them independently. **View → Float
Game / Float Controller Inputs** opens separate windows with stable titles for
capture and composition in OBS. Closing either panel only hides it; reopen it
from **View** or the Movie Mode toolbar. Movie and editing layouts are saved
separately, including floating window positions. Returning to the editor restores
its layout, timeline selection, and scroll position.

The controller panel is also available from **View → Controller Inputs** while
editing. Its red stick dots, buttons, and analog trigger meters follow the last
controller poll in the displayed frame group, not the timeline selection or a
connected physical pad. Groups with no polls retain the previous polled input;
the project baseline has no known input and displays neutral controls. Pausing
keeps the displayed input. Right-click the controller to show exact values or
choose a theme, black, white, or chroma-key background. Values are hidden by
default for clean capture. Analog L/R pressure and their digital clicks are
displayed separately.

Command-line playback: `TasStudio.App --project "path/to/project.tasproj" --movie`.
The existing `--autoplay` option now uses recorded playback when opening a project.

### Selection, execution, and timing

**Take Over** on the toolbar plays from the current TAS preview using your keyboard
or controller without recording. To start at a different point, right-click the
active timeline and choose **Take over from here**. **Ctrl+P** pauses or resumes;
**Alt+Enter** toggles fullscreen. **Return to TAS** or **Escape** discards that play
and restores the exact takeover position, paused, with the editor layout and
selection intact. You can play beyond the recorded end without extending the TAS.
The temporary return snapshot stays in memory; takeover creates no save-state files.

The **cursor** selects input for inspection/editing. The **preview** is the emulator's
current position. Selecting a timeline position or tag moves the cursor without
running the game. Seek restores the nearest valid state/checkpoint at or before the
target and replays from there; with no suitable checkpoint it uses the initial state.

One advance is a **frame group** ending at Dolphin's next presentation boundary.
A 30 FPS game can require two video fields and multiple controller polls per group.
The project records those polls and their port/timing data. The timeline ruler counts
polls, while editing and cursor movement snap to whole frame groups. SDK positions
and `context.Movie` indices are **frame groups**, not individual polls.

| Control | Behavior |
| --- | --- |
| Record / Stop | Play in real time, preserving existing frames and appending live controller input at the end; enables the controller preview and follows the recording |
| F11 / Next Frame | Advance and move the cursor; absent input uses the displayed input |
| F10 | Advance and move the cursor; absent input uses the live controller when Use controller is enabled, otherwise neutral input |
| F12 | Play one existing recorded group without moving the cursor; stop at the recorded end |
| Shift+F11, held | Repeat advances; release either key or lose focus to stop |
| Left / Right | Move the timeline cursor one group without seeking |
| Shift+Left / Shift+Right | Move the selected inputs one frame group left / right |
| Escape | Pause; leave fullscreen first if applicable |
| Ctrl+S | Save the project |
| Ctrl+Z / Ctrl+Y | Undo / redo |
| F1–F8 / Shift+F1–F8 | Load / save a numbered state slot |

Existing recorded input always wins during advance. To change it, edit that group's
input first. Keyboard shortcuts yield to text controls where appropriate; keyboards
with media-first function keys may require Fn.

**Use controller** reads the device mapped in Controllers. Right-click an input
button to toggle turbo: `+` means pressed on this group, `−` means released.
Middle-click the timeline to add a visual tag. Right-click state/checkpoint markers
to load or clear them. Clear later input preserves the selected group and visual tags.
Use the compact **Find tag…** box to search tag names; results include frame groups.
Click a result or use **Enter / Shift+Enter** to cycle forward/backward through matches.
Search selects and reveals the tag without seeking the game; use **Seek** to update
the preview. **Ctrl+F** focuses search from the timeline; **Escape** clears it.

**+ Take** / **Copy selection as take** asks for a name, with the numbered name as
the default. Right-click a take and choose **Rename take…**, or select it and press
**F2** while the timeline has focus. Renames support undo/redo and are saved with
the project. F2 keeps its save-slot shortcut outside that context.

Experiment menus, run windows, top plays, and newly imported takes use the experiment's
name saved by Create/Rename experiment in `experiment.identity.json`, or its folder
name when no identity is saved. Run config fields, including `Name` and `DisplayName`,
cannot rename the experiment. The config's `Name` describes a batch and can change for each run.
The Top plays batch dropdown shows those batch names, sorted by last updated time
descending (newest first). Refresh updates the order and keeps the selected batch.
Run identifiers and trial names/scores are secondary details. Hover the selected take's label in
the input panel to see its source trial and score.

Applying a take at the cursor replaces input starting there without shifting later
input; it removes the applied take, and Undo restores both.

With the timeline focused, **Ctrl+C** copies the selected input groups from either
Active playback or a take. Select a destination in another lane and **Ctrl+V**
pastes at the cursor, replacing the same number of groups without shifting later
inputs. Paste must fit inside a destination take; Active playback can extend at
its end. Copying preserves the source, and paste supports undo/redo. **+ Take**
also copies from the selected lane.

Select a take and **Mark active** to include it in a trial. Multiple takes can be
active: the highest active take in the timeline supplies inputs wherever it has
them, including neutral inputs. Lower active takes fill uncovered ranges, then
Active playback fills the remaining gaps. Right-click a take to move it up or down
to change priority. **Trial takes** plays this combination from the earliest active
take; **Stop trial** or **Escape** stops it. The original recording, poll timing,
and saved states are preserved. **Apply takes** commits the same combination as
one undoable edit, keeps the takes, and clears their active marks. Active marks
and priority order are saved with the project. Normal Play still uses Active playback.

Editing, pasting, moving, clearing, or applying inputs more than one minute behind
the game preview asks for confirmation before overwriting. The check uses recorded
video fields rather than assuming every input group lasts one frame.

Selecting two or more frame groups shows their duration over the tags section. In
**Configuration → Timeline → Timing display**, choose **Multiple Only** (default),
**Always** to include single-group selections, or **Never** to hide the readout.
This preference is saved for all projects on this computer. Drag either selection edge to
resize the range, and hover the duration for start/end timestamps using the timeline
time offset. Measuring a selection does not move playback or edit inputs. A `≈`
marks estimates for unrecorded or changed input and alternative takes.

Drag across the timeline to select a range, then drag inside that selection to move
it. **Shift+drag** also moves a single selected group. The destination outline previews
the move; release to apply it or press **Escape** to cancel. **Shift+Left/Right** nudges
the selection one frame group at a time; these actions also appear in the timeline's
editing menu. Moves leave neutral inputs behind and replace inputs at the destination,
including overlapping ranges. Moving beyond the active recording extends it with
neutral input in any gap. Candidate inputs move within their existing take boundaries.
Use **Ctrl+Z** to undo a move and **Seek** to refresh a preview affected by earlier edits.
Unexecuted groups use frame-group spacing until replay records their poll timing.

### State validity and persistence

- **Ctrl+S saves the project**, including inputs, takes and tags. Recovery autosaves
  provide crash recovery; they are not a substitute for external backups.
- States/checkpoints are stored on disk. Defaults are every **60 emulated seconds**,
  at most **300 automatic checkpoints**, and a **4096 MiB** automatic-state budget.
  Configuration supports age/LRU retention. Named states and the baseline are separate.
- State validity includes the preceding input/event history and configuration.
  Earlier edits invalidate dependent states. A state immediately before an edited
  group's input can remain valid; states after it cannot.
- Per-project settings, including UTC and resolution, remain editable. Changing
  emulation settings preserves inputs but rebuilds the baseline and invalidates old
  checkpoints. Single-core execution is required. Game compatibility overrides take
  precedence over requested options.
- **Export Replay** produces Studio's flattened `.tasreplay`, not a Dolphin DTM
  movie, a video, or an experiment configuration. To run experiments, save the
  `.tasproj` and point `SourceProject` at it.
- **Export to Dolphin → Quick / Full** creates an experimental `.dtm` for power-on
  GameCube movies without memory-write or reset events. Quick uses complete, current
  cached polls (otherwise it directs you to Full). Full bakes the entire timeline. Each
  preserves your paused position, and creates a companion folder containing an
  isolated Dolphin profile, original Slot A card copies, and playback instructions.
  It automatically measures and prepends startup controller polls, verifying the
  recreated boot against the original timing and RAM. Use the supplied launcher
  for testing; compatibility still depends on the game/build/settings. See [Dolphin export](docs/dolphin-movie-export.md).

See [input/recovery behavior](docs/input-workflow-and-recovery.md),
[project format](docs/project-format-v2.md), and [configuration policy](docs/configuration-policy.md).

## C# experiments

Right-click a saved state in the timeline and choose **Experiment → Create
experiment…** to name and scaffold a C# workspace under the project's `experiments/`
folder, initialized to start at that state. The same menu offers **Rename experiment…**
and **Remove experiment from state**; removal clears only the state attachment.
The experiment stays in the project and in **Attach existing experiment**, with its code and run history.
Edit its code/config, then use the
state menu to run it or open its folder. **Attach existing experiment** reuses a
recipe at a replacement state after redoing a section; previous runs keep their
own snapshots. See [state experiment workflows](docs/csharp-experiments.md#from-a-saved-state-in-studio).

Configure a code editor and default library references under **Options →
Configuration → Interface → Experiments**. Editor detection prefers VS Code,
then Visual Studio. New experiments created in Studio automatically reference
the selected C# projects or compiled .NET DLLs.

C#/.NET class libraries are the supported scripting workflow. Author them in VS Code
or another C# editor. The deprecated embedded Lua editor and runtime have been removed.

An experiment implements **`IExperiment<TResult>`**:

```csharp
public sealed record Result(long Utc, ulong EndGroup);

public sealed class Experiment : TasStudio.Sdk.IExperiment<Result>
{
    public TasStudio.Sdk.ExperimentBootOptions Initialize(
        TasStudio.Sdk.ExperimentInitializationContext context) =>
        context.StartingPoint == TasStudio.Sdk.ExperimentStartingPoint.PowerOn
            ? new(checked(context.DefaultStartUtcSeconds + context.Index))
            : new();

    public async Task<Result> RunAsync(TasStudio.Sdk.ExperimentRunContext context,
        CancellationToken cancellationToken)
    {
        while (await context.Emulator.PlayAsync())
            cancellationToken.ThrowIfCancellationRequested();

        return new(await context.Emulator.GetStartUtcAsync(),
            (await context.Emulator.GetPositionAsync()).Group);
    }
}
```

`Initialize` runs once per trial **before boot**, with metadata and boot options only.
`RunAsync` runs after boot/state restoration and optional movie preroll, on the same
experiment instance. The runner discovers the typed result schema once per batch
without constructing your experiment.

Every worker receives a frozen copy of the source project. `context.Movie` is an
immutable view of its original input intent. Editing the worker does not change the
main Studio session, the source project, or another worker. C# code is trusted local
code with ordinary filesystem/network access; process isolation is not a sandbox.

### Create and run a scripting workspace

1. Build Studio and save a `.tasproj`.
2. Package/install the included VS Code extension:

   ```powershell
   cd extensions/tas-studio
   npm run check
   npx @vscode/vsce package --allow-missing-repository --out ../../artifacts/tas-studio-experiments.vsix
   code --install-extension ../../artifacts/tas-studio-experiments.vsix
   cd ../..
   ```

3. Set VS Code's `tasStudio.workerPath` to the absolute path of the published
   `TasStudio.Worker.exe`. Install Microsoft's C# editor tooling for IntelliSense.
4. Run **TAS Studio: New C# Experiment Workspace**, select the saved project and a new
   folder. Edit `Experiment.cs` and `experiment.tascsharp.json`.
5. Use **Run Experiment** in the TAS Experiments view. Use **Cancel Experiments** to stop.

CLI equivalents:

```powershell
& ./artifacts/prod/TasStudio.Worker.exe new-csharp 'D:/TAS/My run/movie.tasproj' 'D:/TAS/Scripts'
& ./artifacts/prod/TasStudio.Worker.exe run-csharp 'D:/TAS/Scripts/experiment.tascsharp.json' 'D:/TAS/Scripts/.runs/batch-001'
& ./artifacts/prod/TasStudio.Worker.exe resume-csharp 'D:/TAS/Scripts/.runs/batch-001'
& ./artifacts/prod/TasStudio.Worker.exe clean-csharp 'D:/TAS/Scripts/.runs/batch-001'
```

Each new run requires a fresh output directory. The configuration controls count,
parallel workers, boot/save-state start, preroll, timeout, and custom typed parameters.
`Headless` defaults to `true`; set it to `false` for separate, silent worker preview
windows. Closing one cancels that trial. Preview windows do not expose manual input.

### Long searches and results

The runner owns `results.sqlite`. The `trials` table stores status/diagnostics/full
receipts; the `results` table stores your successful typed values. Both use the
zero-based `trial_index` primary key. Nested structs/objects/collections become
JSONB columns; enums are stored numerically, and nullability follows your C# type.
Writes are serialized through the coordinator and committed per trial.

Resume reuses the frozen assembly, movie, parameters, count, and indices. It skips
finished trials even when workers finished out of order, restarting only unfinished
ones from their original start. It does not resume a C# call stack. Cancellation
stops scheduling; unstarted indices remain pending without synthetic results.

Successful/cancelled trial directories are cleaned after commit and worker exit.
Failed/timed-out trials retain diagnostics. A failed database write preserves files
for recovery. Each batch starts fresh and automatically removes its temporary source
snapshots, trial states, profiles and result projects when it exits, including cancellation.
SQLite results, top plays, logs and compiled scripts remain. Resume is available only
for interrupted batches whose snapshots have not been cleaned; otherwise start a new batch.
Use `clean-batch <batch> --dry-run` to preview cleanup of an older stopped batch,
then omit `--dry-run` to reclaim its temporary storage.
Successful trials do not automatically leave a replay project to open in Studio.

For the complete configuration, API, takeover semantics, memory utilities, state
lifecycle, SQL examples and an AI authoring checklist, read
**[the experiment guide](docs/csharp-experiments.md)**. The
[basic example](examples/csharp/Basic.Experiments/) compiles as part of the solution;
the [Skies example](examples/csharp/Skies.Experiments/UtcSweep.cs) shows game-specific
utilities and trial-index parameter mapping.

## Builds with and without tracing

Native trace/traceback support is optional. The two builds use separate output trees:

| Build | Command | Core output | Compile option |
| --- | --- | --- | --- |
| Normal TAS runtime | `just build` | `artifacts/prod/native/dolphin_libretro.dll` | `DOLPHIN_TAS_TRACE=OFF` |
| Trace-enabled diagnostics | `powershell.exe -NoProfile -File scripts/build-trace.ps1` | `native/build-dolphin-trace/Binaries/dolphin_libretro.dll` | `DOLPHIN_TAS_TRACE=ON` |

Run the ordinary build setup first so the pinned Dolphin checkout exists.
`build-trace.ps1 -Jobs 4` adjusts build concurrency. It builds the separate core and
runs the native buffer tests; it **does not publish a full Studio distribution or
replace the normal core**. `just dev` reuses whichever normal native binaries already
exist; it does not switch tracing modes.

Tracing observes selected guest instruction addresses and captures sequence numbers,
emulated ticks/video fields, PC, LR and registers. Capture starts explicitly through
the native API; a trace-enabled binary does not record automatically. This is not an
automatic C# stack trace or a managed SDK tracing method. Overflow invalidates a
capture for complete call-count analysis. Strict intermediate-state parity has known
limitations, so use normal builds for regular TAS work and validate diagnostic traces
against a fixed baseline. See [native tracing](docs/native-tracing.md) for exports,
input export, smoke-test commands, and the observed limits.

## Tests and repository structure

Managed tests need no ROM:

```powershell
dotnet test tests/TasStudio.Core.Tests -c Release
dotnet build examples/csharp/Basic.Experiments -c Release
```

Real-core verification requires your own game image and native dependencies:

```powershell
powershell.exe -NoProfile -File scripts/test-integration.ps1 -RomPath 'D:/Games/game.iso'
powershell.exe -NoProfile -File scripts/verify-workflow.ps1 -Rom 'D:/Games/game.iso'
node scripts/verify-resume.cjs 'D:/TAS/My run/movie.tasproj' 'artifacts/resume-check-001'
```

The last command uses SQLite support in Node.js 22.14+ and creates a fresh test batch.
Historical verification documents under `docs/` describe specific development runs;
ignored artifacts mentioned there are not shipped and are not current test results.

The manual-play integration check uses a locally supplied USA-region GameCube image
and a fresh output folder. It creates three independent states, imports/exports a
59-block card, converts each state to a project baseline, and compares full RAM and
card bytes again in a fresh process:

```powershell
dotnet run --project tests/TasStudio.Integration.Tests -c Release -- 'D:/Games/game.iso' artifacts/play-check manual-play-create
dotnet run --project tests/TasStudio.Integration.Tests -c Release --no-build -- 'D:/Games/game.iso' artifacts/play-check manual-play-restore
```

| Directory | Purpose |
| --- | --- |
| `src/TasStudio.Core` | Canonical controller values |
| `src/TasStudio.Sdk` | Public experiment contracts and basic memory helpers |
| `src/TasStudio.Emulation` | Owner-thread execution, projects, history, checkpoints, experiments, SQLite |
| `src/TasStudio.Dolphin` | Managed adapter for the native emulator host |
| `src/TasStudio.App` | Avalonia editor, docking, controller input and audio |
| `src/TasStudio.Worker` | CLI, workspace generator and isolated trial process |
| `native/` | Native host, pinned-source patches, optional tracing harness |
| `extensions/tas-studio` | Local VS Code experiment extension |
| `examples/csharp` | Compilable experiment and game utility libraries |
| `tests/` | Managed contracts and ROM-dependent native integration checks |

See [CONTRIBUTING.md](CONTRIBUTING.md) before changing input timing, state validity,
or experiment isolation. [PLAN.md](PLAN.md) records design goals, not a guarantee
that every proposed feature is complete.

## License and provenance

Application-owned source is **GPL-2.0-or-later**, as declared in [NOTICE.md](NOTICE.md).
The GPL text is in [LICENSE](LICENSE). Dolphin, its components, NuGet packages, and
other dependencies retain their own notices. The pinned source revision, patches,
and build scripts are included for reproducibility. Read the notices before
redistributing compiled bundles; a source checkout is not a complete binary-release
packaging process.
