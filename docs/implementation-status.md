# Implementation and verification status

Updated: 2026-09-06. Target: Windows x64, GameCube, Skies of Arcadia Legends (USA).

This document records the initial M2 baseline. The subsequent workspace implementation
is described in [iteration2-verification.md](iteration2-verification.md) and
[project-format-v2.md](project-format-v2.md). Those documents supersede the baseline's
ZIP-project, state-detachment, initial-only seeking, and test-count descriptions below.

## Implemented scope

The repository contains the native Dolphin host, managed adapter, single-owner
execution service, Avalonia frontend, exact controller data, state-file and slot
workflows, a basic editable TAS project, ordered memory/reset events, and replay
seeking from the initial state. Automatic checkpoints and the M3+ editor/search
features are not implemented.

`PLAN.md` renumbered the original proposal: its M0 is the backend feasibility
gate, M1 is the viable emulator frontend, and M2 is the minimal editable project.
The implementation includes that revised M2 project workflow. The user's request
to continue through M2 supplies authorization to proceed beyond the intermediate
architectural review while documenting its findings.

## Toolchain and backend

- .NET SDK 10.0.400, target framework net10.0.
- Avalonia 11.3.20 and NAudio.WinMM 2.2.1.
- Visual Studio 2026 / MSVC 19.51.36256; Windows SDK 10.0.26100.0.
- CMake 4.3.1, Visual Studio 18 2026 x64 generator.
- Dolphin Libretro revision `e1e6d25fa1392b7d1bc05bf800c71b807a2bd2e0`.
- Reproducible changes: `native/patches/0001-tas-contract.patch`.
- Rendering: D3D11 RGBA texture readback to Avalonia.
- Execution: single core, JIT64, HLE DSP, native internal resolution.
- Clock: configured epoch 2000-01-01 UTC plus emulated ticks.
- Storage: application-owned profile with raw memory card in slot A, slot B empty.

Verified artifact SHA-256 identities:

| Artifact | SHA-256 |
| --- | --- |
| Test game image (GEAE8P) | `C21971AA819790337887E989741C7E4BA606AE7DDDD3FB1F53F74A9C2684D092` |
| Published Dolphin core DLL | `359B52967495D4117D212EFAB0513E2A4CC6C235FBBCB27D8B49A85F940C7AD4` |
| TAS core patch | `232B28DE15FA2D78E47526CB87934504920A6758C336A5E464473D5401664D05` |

The initial core boot call establishes the application's state 0. It has already
executed boot work; this is not a claim of input recording from physical power-on.
Each subsequent step advances one VI field. The tested game settles to 59.9401 Hz;
the temporary pre-settle refresh rate is refreshed after every step.

The .NET apphosts use `CETCompat=false`, scoped to these executable builds.
Dolphin's JIT/fastmem exception continuations caused a reproducible Windows
`0xc0000409` termination with the .NET default. The executable compatibility
setting resolved it without changing Windows security settings. Background:
[Microsoft's .NET CET compatibility documentation](https://learn.microsoft.com/en-us/dotnet/core/compatibility/interop/9.0/cet-support).

## Verified evidence

| Area | Evidence |
| --- | --- |
| Native/managed builds | Native core and host compile; managed solution and published app build. Host and managed code build with zero compiler warnings/errors in the reviewed runs. |
| Exact steps and inputs | Native smoke observed each step advance one field; four distinct raw pad probes survived the actual SI polling path, with two polls per tested field. Probes include opposite stick extrema, neutral values, separate digital trigger clicks and analog values, and both clicks with zero analog values. |
| Real core replay | After 1,800 warmup steps, replayed 600 inputs from one savestate twice and compared all 24 MiB of MEM1. Hashes matched; a new process restored the persisted state and matched the same expected hash. |
| State boundary recovery | Native smoke compares restored field/tick counters and rejects an incompatible state while recovering the preceding state. |
| Memory | Actual RAM byte write/read roundtrip, big-endian u32 interpretation, invalid address rejection, and restoration of original bytes. |
| Video/audio production | D3D11 produced 640×528 RGBA frames; inspected the resulting game image. Core delivered non-silent stereo audio, explicitly checked by the integration harness. Speaker quality and hardware-controller feel remain user testing tasks. |
| Lifecycle/failures | Stop/reload succeeds. Malformed ISO is rejected, followed by a successful valid-ROM load in the same process. |
| Real M2 project | Record 60 inputs; edit an earlier button; seek to start/end; save project; save/load manual state; reopen project in the same process. Full MEM1 result matches. Another process reopens and seeks the project and matches the expected full MEM1 hash. |
| Domain/contracts | 25 tests pass, covering ownership, step ordering, replay, edits, events, state/project validation, cancellation, preservation of prior state on failure, and atomic save replacement. |
| Actual UI | Native ROM picker, state save/load pickers, slot save/load, frame advance, controller dialog, and live image updates passed. The packaged app also created a three-input project, edited row 1 to A, saved/reopened it through native pickers, and sought to states 0 and 3 with the edit preserved. See `frontend-verification.md`. |
| Packaged executable | Self-contained `artifacts/app/TasStudio.App.exe` booted the game, played, captured its actual rendered window, and exited with code 0. Capture: `artifacts/frontend-packaged.png`. |

Local evidence files, ignored by Git:

- `artifacts/native-smoke/process.log`
- `artifacts/final-integration.log`
- `artifacts/invalid-rom-check.log`
- `artifacts/audio-signal-check.log`
- `artifacts/publish.log`
- `artifacts/integration/` — state, project, expected hashes, and raw frame outputs
- `artifacts/frontend-*.png` — frontend captures

The native and managed tests are complementary. Fake-backend tests do not prove
Dolphin determinism; full RAM hash matches are concrete repeatability evidence
for these sequences, not a proof for every game or configuration.

## Review outcomes

- Backend review: `backend-feasibility-review.md` documents the source evidence,
  TAS extension, lifecycle, RTC, storage, and actual polling tests.
- Code quality: `code-quality-review.md` documents fixes, meaningful constants,
  threading, state validation, and 25 regression tests. Backend identity probing
  stays behind the backend interface.
- Frontend review: `frontend-acceptance.md` records Dolphin defaults and the
  acceptance checklist; `frontend-verification.md` records actual app tests.

## Practical limits for this testing build

- GameCube port 1; keyboard and common XInput gamepads. Physical controller
  testing is assigned to the user. No claim of GameCube USB adapter or arbitrary
  DirectInput/SDL device support.
- Fixed D3D11/1×/single-core/HLE profile; controller and volume settings are
  editable. The application does not duplicate all standalone Dolphin options.
- State files are TAS Studio containers tied to the exact core binary hash, game
  hash, and relevant exposed settings. Arbitrary standalone Dolphin savestates
  are not accepted as compatible. Rebuilding the core can invalidate old states.
- Projects require the recorded ROM path to exist. ROM relocation, broad
  migration, and cross-machine portability need additional UI and tests.
- Project baselines include a core savestate with raw memory-card contents.
  Reopen uses a fresh writable profile. Wii NAND/GCI folder portability is outside
  the initial supported configuration.
- Memory operations currently cover MEM1 RAM. Treat writes as data changes;
  arbitrary code patches requiring JIT invalidation and MMIO are not exposed.
- Seeking replays from the beginning. It is cancelable at step boundaries;
  checkpoints and large-timeline performance are M3 work.
- Raw savestates are around 107–109 MB in the tested game. Compressed project and
  state files are smaller; capture/compression can briefly pause emulation.
- The GUI and emulator share a process. Native fatal failures can terminate the
  application; worker-process crash isolation is future work.

## User test checklist

1. Start `artifacts/app/TasStudio.App.exe` and open the Skies ISO.
2. Play, pause, frame advance, resize, reset, and stop/reopen.
3. Save/load a numbered slot and a `.tasstate` file from the menus.
4. Test your controller under Controllers: choose an XInput device, map buttons,
   inspect both sticks, and check analog trigger travel versus digital clicks.
5. Create a project, record a short sequence, change an earlier input, seek,
   save, close, and reopen it.
6. Report any black/stale frames, input mismatch, audio problems, or replay
   divergence with the project/state and `DolphinUser/host.log` from the app data
   directory. Keep game images local.
