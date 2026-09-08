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
| Play mode | Direct keyboard/XInput play, persistent Slot A memory cards, raw card import/export, independent save-state files |
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
branch. You can optionally mark the release as a prerelease.

After the native build and tests pass, the workflow creates tag `v<version>`, generates
release notes, and publishes the GitHub Release with these assets:

- `Dolphin-TAS-Studio-<version>-win-x64.zip`
- `Dolphin-TAS-Studio-<version>-win-x64.zip.sha256`

The ZIP contains the self-contained Studio and experiment worker, the pinned Dolphin
core and system files, examples, licenses, and other runtime dependencies. The release
asset remains available until the release is manually deleted. `TasStudio.App.exe`
uses the Dolphin TAS Studio logo as its Windows application icon.

### Where the executables and data live

| Command | Executables | User data |
| --- | --- | --- |
| `just dev` | `.local/dev/default/app/` | Existing `%LOCALAPPDATA%/TasStudio` settings/recovery |
| `just dev-unique` | `.local/dev/<timestamp>-<id>/app/` | That workspace's `data/` folder |
| `just build` | `artifacts/prod/` | Normal local settings, unless a data-path marker is supplied |

`just dev` reports the existing app's PID when it is already running. Close that
instance and rerun the command to rebuild it. Neither dev command deletes a workspace.
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
TAS projects and experiment workers use isolated profiles. Slot B and individual
`.gci` save import are not exposed in this workflow.

**Memory cards…** lets you choose USA, EUR or JAP and import/export a `.raw` card
(59, 123, 251, 507, 1019 or 2043 blocks). Import copies the selected file and restarts
the game; any replaced card is retained alongside it as a uniquely named `.bak`.
Export flushes Dolphin's card writer, copies the card, and restores your paused
position. Cards are separate by region and size. Match the region to your game.
The dialog shows the active file path; an unused region may have no card to export.

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

### Selection, execution, and timing

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
| F11 / Next Frame | Advance and move the cursor; absent input uses the displayed input |
| F10 | Advance and move the cursor; absent input uses neutral input |
| F12 | Play one existing recorded group without moving the cursor; stop at the recorded end |
| Shift+F11, held | Repeat advances; release either key or lose focus to stop |
| Left / Right | Move the timeline cursor one group without seeking |
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
Applying a take at the cursor replaces input starting there without shifting later
input; it removes the applied take, and Undo restores both.

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

See [input/recovery behavior](docs/input-workflow-and-recovery.md),
[project format](docs/project-format-v2.md), and [configuration policy](docs/configuration-policy.md).

## C# experiments

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
for recovery. Keep the batch's SQLite database and shared source/assembly snapshots.
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
