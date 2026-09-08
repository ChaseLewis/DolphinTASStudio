# Code quality review through M2

Review scope: application-owned C# in `src` and native code in `native/TasStudio.LibretroHost`; external Dolphin source is excluded. Prepared 2026-09-06 while implementation is in progress. This report does not establish emulator determinism or frontend runtime acceptance.

## Policy for constants

Use `const`, `static readonly`, `constexpr`, or enums for protocol values, file format names/versions, storage paths, resource limits, controller ranges, timing, and repeated policy choices. Prefer domain names over generic `MagicNumber` aliases. Literal zero/one in ordinary indexing and arithmetic, pixel-channel indices, and one-off UI labels do not merit separate constants. Constants cannot compensate for a missing ownership or compatibility contract.

The initial implementation already names controller bit masks and axis bounds, ABI versions, core option names, RTC seed, audio buffering limits, timeline page size, state slot count, and shared application paths. File formats and execution limits must follow this practice as those modules land.

## First-pass findings

| Priority | Finding | Disposition |
|---|---|---|
| P1 | The backend emits an empty audio buffer with sample rate zero to flush on stop/restore. `AudioOutput.Add` originally tried to initialize an audio device at zero Hz, which could permanently disable audio. | Fixed and source verified: sentinel flush returns before `WaveFormat` creation. |
| P1 | Native destroy originally proceeded to free a DLL and host after catching a wrong-thread error. Several getters lacked owner validation. | Fixed and source verified: wrong-owner destroy refuses to unload; getters use the guarded boundary. |
| P1 | Software video pitch and audio sample multiplication originally lacked bounds checks at callback boundaries. | Fixed and source verified: pitch validation, overflow protection, and callback exception containment. |
| P2 | The displayed application settings originally reported OpenGL while the host actually selects D3D11. | Fixed and source verified: Direct3D 11. |
| P2 | Slot filenames use a game basename, so unrelated images with equal basenames share and overwrite slots. Payload compatibility validation alone prevents wrong loads but not unwanted overwrite. | Fixed with canonical path hash subdirectories; archive game hashes additionally reject different image content. Moving a ROM creates a new slot location. |
| P2 | The File menu displayed Ctrl+S without a matching handler in the first-pass snapshot. | Fixed and source verified: Ctrl+S dispatches SaveProject. |
| P2 | Open Game initially did not pause before resolving unsaved projects, allowing inputs to arrive during the decision. | Fixed and source verified: pause precedes save/discard resolution. |

## Metrics and verification approach

First-pass snapshot contained 12 application-owned `.cs`, `.cpp`, and `.h` files (1,477 physical lines). The largest was `host.cpp` at 381 lines; C# maximum was 182 lines. These are a baseline rather than quality thresholds: source is being added during review. MainWindow is split by workflow, input/audio own separate concerns, and native ownership is explicit. `rg` found 90 constant/read-only declaration matches and 10 catch matches; these lexical counts include local C++ `const` declarations and are not a meaningful magic-number score or complexity score.

Use actionable final metrics: zero build errors; compiler warnings reviewed; one backend owner thread across load/step/capture/restore/dispose; bounded file/state/video/audio buffers; rejected incompatible/corrupt archives before backend mutation; atomic replacement of canonical saves; known input sequences reproducing after edit/seek/reopen. Record actual test totals only after the tests run. Do not infer coverage percentages or cyclomatic complexity from line or regex counts.

Execution and archive regression tests will use a stateful fake backend to isolate ordering, frame boundaries, failure recovery, round trips, validation, and project edit semantics. These tests complement the real-ROM integration test; they cannot establish real Dolphin rendering, state compatibility, or replay determinism.

## Execution/archive review results

The second pass added safe-boundary seek interruption through `PauseAsync`, `StopAsync`, `Dispose`, and an optional cancellation token. Playback deadlines now include elapsed emulation time, with bounded catch-up after a stall. Failed creation of a new project leaves the prior initial state and timeline intact. Memory-write arguments are copied when submitted, preserving command ordering even if callers reuse their buffer.

Archive validation now rejects unknown controller bits, unsupported resolution, invalid positions, state files carrying unrelated timelines, invalid events, negative/inconsistent preview dimensions, duplicate required entries, and checksum failures. Save validates the completed temporary archive before replacing canonical content; a regression test verifies a failed save preserves the previous bytes and removes its temporary file.

`dotnet test tests/TasStudio.Core.Tests/TasStudio.Core.Tests.csproj --nologo` passed **25 tests, zero failed/skipped**, with zero compiler warnings/errors on 2026-09-06. Covered behavior includes one dedicated backend thread through disposal; state-before-input recording and playback; past-input recomputation; ordered memory/reset events; full service-instance project reopen; wrong game/build/configuration rejection before restore; failed step recovery; failed new-project rollback; stable pause boundaries; manual-state project detachment; active and pre-canceled seek handling; archive roundtrip, validation, and save preservation.

At the end of this review pass, 16 application-owned C#/C++ source/header files totaled 2,046 physical lines, excluding generated files and tests. Largest files: native host 395, execution service 279, main window 203, command partial 181. The 64 MiB metadata, 256 MiB state, and 4,096-pixel preview limits are named policy constants. Further performance work should target publishing whole input-array copies during long recordings and scanning events during seeks; M3 checkpointing/large-timeline work remains outside these focused tests.

The backend identity coupling is resolved: `IEmulatorBackend.InspectIdentity` performs nonmutating build preflight, Dolphin owns its single identity prefix and core hashing implementation, and the execution service asks the backend before replacing a live project. A regression test verifies a build mismatch leaves the old game, project, inputs, position, and memory intact without stop/load/restore calls. Project roundtrip tests use an independent fake identity rather than emulating Dolphin naming. Real-ROM and frontend evidence is recorded separately by their owners.
