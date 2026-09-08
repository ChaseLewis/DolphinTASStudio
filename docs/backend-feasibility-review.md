# Dolphin Libretro feasibility review

Reviewed upstream revision `e1e6d25fa1392b7d1bc05bf800c71b807a2bd2e0` against `PLAN.md` on 2026-09-06. This document records source evidence and focused changes; it is **not** a claim that the runtime M0/M1/M2 acceptance tests passed. Runtime evidence belongs in the integration test results.

## Conclusion

Libretro is a viable candidate when restricted to a single emulation owner thread, single-core execution, a pinned rendering/configuration path, and the TAS patches below. Upstream alone does not satisfy exact controller input, storage rewind, deterministic RTC, or reliable state error reporting. Core global singletons and callback globals prohibit independently usable instances in one process. Future parallel search should use processes.

## Frame and thread semantics

`DolphinLibretro/Main.cpp:retro_run` updates input before executing `CPUManager::RunSingleFrame` in single-core mode. `Core/HW/CPU.cpp:RunSingleFrame` sets frame-step mode, enters the PowerPC loop, and drains asynchronous GPU requests before returning. Upstream Libretro unconditionally breaks at a VI boundary; Studio ABI 2 instead uses standalone Dolphin's `Callback_FramePresented`/`Callback_NewField` condition, excluding `VideoInterfaceDuplicate` presentations. The presentation event hook must survive Libretro's initialization function returning, and is retained with its shutdown guards.

An editable **frame group** is a presentation advance, potentially spanning multiple VI fields. Recording holds the selected pad snapshot across that call and captures every actual SI poll. Playback consumes the stored per-poll values. This is Dolphin's presentation event, not a pixel-difference test: a newly rendered static image still counts. The first `retro_run` initializes emulation; the host treats initialization separately and numbers groups from that starting boundary. Steps must advance both the field and presentation counters. Playback timing uses elapsed CoreTiming time, not a fixed 60 advances per second. ABI 1/2 recordings are incompatible with the current poll-based dev format.

Use a persistent owner thread for initialization, graphics context use, running, state/memory calls, and unloading. Serialize UI commands onto that thread. Never call Libretro on audio/UI callback threads. The upstream serializer temporarily declares its calling thread as the CPU thread; this is unsafe as a general cross-thread synchronization mechanism. Its single-core owner-thread use is the supported path here. Disable `dolphin_main_cpu_thread` and graphics backend multithreading. Dual-core is outside the TAS extension contract.

The current source alone establishes the boundary location, but actual SI poll counts and ordering still need a runtime probe. Matching RAM or frame hashes does not prove all poll cases.

## Exact controller injection

`DolphinLibretro/Input.cpp:retro_set_controller_port_device` maps Libretro analog values through Dolphin `GCPad` expressions. `Core/HW/GCPadEmu.cpp:GetInput` then applies the controller groups and maps floats into bytes. `InputCommon/ControllerEmu/ControlGroup/MixedTriggers.cpp` forces the analog trigger to full when its digital threshold is reached. Thus even careful Libretro callback conversion cannot promise independent raw analog trigger values and clicks through the normal path.

The patch adds a direct override in `Pad::GetStatus`, after live controller mapping but before delivery to the emulated SI device. It returns the exact supplied bytes. Main and C sticks are unsigned 0–255 with neutral 128; positive Y points up. Analog triggers are unsigned 0–255 and independent of digital L/R bits. A/B analog pressures follow Dolphin's conventional 0/255 derivation from their buttons. Port topology still uses normal Dolphin device configuration; the override does not connect additional SI ports.

Physical gamepad dead zones, calibration and bindings should remain host settings. Store the resulting exact submitted pad in the timeline. Playback must bypass live mapping. Standalone `GCPadNew.ini` expression syntax is a richer language than simple key bindings, so do not silently claim arbitrary import compatibility.

## TAS extension ABI, version 3

All functions are exported with C linkage and the default C calling convention. Windows exports use `__declspec(dllexport)`. Call only at emulation boundaries on the owner thread. Native `bool` is one byte for managed interop.

```c
struct DolphinTasPad {
    uint16_t buttons;
    uint8_t stick_x, stick_y, cstick_x, cstick_y, trigger_l, trigger_r;
}; /* sizeof = 8; natural alignment = 2 */

uint32_t dolphin_tas_get_abi_version(void); /* returns 3 */
bool dolphin_tas_set_pad(unsigned port, const struct DolphinTasPad* input);
uint64_t dolphin_tas_get_ticks(void);
uint64_t dolphin_tas_get_field_count(void);
uint64_t dolphin_tas_get_presentation_count(void);
uint64_t dolphin_tas_get_pad_poll_count(unsigned port);
bool dolphin_tas_get_last_polled_pad(unsigned port, struct DolphinTasPad* output);
```

`set_pad` accepts ports 0–3, rejects dual-core and unknown button bits, and copies the snapshot. Null disables the override. It does not retain the input pointer. `field_count` resets on boot and is serialized/restored; ticks are Dolphin CoreTiming ticks. Neither getter performs synchronization. Input snapshots are not part of the savestate: the host supplies them again before every step.

The two poll observation exports count calls to `Pad::GetStatus` while the exact override is enabled and copy the last returned pad. These cumulative observations reset on deinitialization and do not rewind with a savestate. The presentation counter is also observational and does not rewind; compare differences around each step. Read all diagnostics on the owner thread.

ABI 3 adds `dolphin_tas_begin_polls`, `dolphin_tas_end_polls`, and `dolphin_tas_copy_polls`. Their 32-byte poll record contains 64-bit tick/field offsets from the start of the advance, a 32-bit port, and the 8-byte exact pad snapshot (with native alignment padding). The current Studio project topology supports GameCube port 1. Recording captures each actual overridden controller poll; replay consumes a separate recorded value on every poll and verifies port, offset, order and total count. The backend also validates the ending presentation's tick/field duration. A failed replay marks the project preview stale, pauses execution, and requires restoration before continuing.

The project keeps presentation groups for editing, but persists their complete poll streams in content-addressed JSON chunks alongside the authoring inputs. The timeline ruler uses cumulative poll counts; scrubbing snaps to the enclosing group and edits rewrite all its polls. Hash prefixes incorporate poll bytes and timing. Groups invalidated by an edit retain their editable layout, then regenerate timing against the new prefix on execution. Unchanged groups replay their stored poll records. Project/recovery saves, replay exports, undo/redo, and session rollback preserve the group mapping. Earlier native ABIs are deliberately incompatible.

Button bits are the native Dolphin `PadButton` values:

| Input | Value |
|---|---:|
| Left / Right / Down / Up | 0x0001 / 0x0002 / 0x0004 / 0x0008 |
| Z / R click / L click | 0x0010 / 0x0020 / 0x0040 |
| A / B / X / Y | 0x0100 / 0x0200 / 0x0400 / 0x0800 |
| Start | 0x1000 |

## State correctness and storage

Upstream `retro_serialize` and `retro_unserialize` always returned true. `PointerWrap` switches to measure mode on buffer overrun or a format marker/console/memory-size failure. The patch now returns whether the requested read/write mode remains active and rejects empty/null buffers. This improves diagnostics; restoration is still not transactional. Validate wrapper metadata and payload hash before passing data to Dolphin, retain a rollback state, and restore it after a failed native restoration. Raw state input is an opaque trusted backend format, not a hardened parser for arbitrary bytes.

`Core/HW/EXI/EXI_DeviceMemoryCard.cpp:DoState` originally captured card contents and even card command state only while a Dolphin movie was active. The patch includes these for Libretro states. `GCMemcardRaw.cpp:DoState` now locks the raw card flush mutex, rejects allocation-size mismatches, and marks restored data dirty for subsequent persistence.

Use **raw memory cards** (`SlotA=1`) in project-owned directories, or explicitly disable both slots (`255`) for an intentionally card-free test. The upstream default is GCI folders (`SlotA=8`). `GCMemcardDirectory::DoState` serializes its absolute save-directory path, which would redirect writes after project relocation. Raw card states retain the current instance's configured filename, so they are a better M2 choice.

External writes are not undone on disk atomically with an in-memory state load. Raw cards have a background flush thread; restored contents are subsequently flushed. Preserve an immutable initial persistent-storage snapshot, materialize a project runtime copy before boot, and never point a TAS session at the user's standalone Dolphin cards. New raw card formatting uses wall time, so boot-only reproducibility requires recorded initial card bytes. Embedded starting-state replay includes the card contents after the patch, provided the new instance uses the same card geometry. Wii NAND, network devices, GBA saves and non-card external state remain outside initial scope.

The patched state layout contains the additional field counter and card contents. It is incompatible with upstream raw Libretro states and must be identified by core build hash plus TAS ABI version in the application's wrapper.

## RTC and configuration

Standalone `EnableCustomRTC` does not by itself make elapsed time deterministic. `SystemTimersManager::Init` computes a boot wall-clock offset, and `EXI_DeviceIPL::GetEmulatedTime` normally subtracts it from the current wall clock. Pausing or replaying faster therefore changes RTC observations.

For Libretro with `EnableCustomRTC=True`, the patch returns `CustomRTCValue + CoreTimingTicks / TicksPerSecond`. The host must persist that explicit epoch with the project and prohibit unrecorded runtime changes. Leave overclock disabled, fast disc speed disabled, cheats disabled, and emulation-affecting configuration fixed. Pin effective game INI overrides too. DSP HLE is an acceptable candidate, but confirm it through repeated replay; do not infer determinism merely from single-core mode. Controller mapping and UI preferences may mirror standalone labels while TAS mode locks incompatible settings.

## Memory and graphics

The standard `retro_get_memory_data(RETRO_MEMORY_SYSTEM_RAM)` and matching size already expose MEM1. On first run, `RETRO_ENVIRONMENT_SET_MEMORY_MAPS` additionally announces MEM1 at `0x80000000` and Wii MEM2 at `0x90000000`, with big-endian descriptors. Copy any descriptor metadata immediately because the announced descriptor array is stack-local. Expose copied bounded reads/writes, never the host pointer. Validate zero-length/overflow/end-of-region accesses. Typed reads should explicitly decode big-endian. Raw pointer writes into executable memory may bypass JIT invalidation and should be limited to data RAM unless the host adds an appropriate CPU/MMU write extension.

The OpenGL path asks the frontend to create a context and calls a hardware-frame sentinel; it does not deliver pixels. The frontend must provide `get_proc_address`, a current framebuffer, context lifetime, and readback/presentation. `VideoContexts/GLContextLR.cpp` reports bottom-left origin and uses null for duplicate presentation. Retain the last frame for duplicate callbacks and paused redraw. Software renderer delivers CPU data but is a slow fallback, not evidence that hardware integration works. A failed hardware negotiation falls back to Null unless Software was explicitly selected; do not present a successful boot with a permanently blank viewport as a supported graphics path.

## Patch inventory and validation still required

`native/patches/0001-tas-contract.patch` records the changes outside the cloned dependency. Apply it to the pinned clean checkout before building. Files: `Core/Tas.h`, `Core/Core.cpp`, `Core/State.cpp`, `Core/HW/GCPad.cpp`, `Core/HW/EXI/EXI_DeviceIPL.cpp`, `Core/HW/EXI/EXI_DeviceMemoryCard.cpp`, `Core/HW/GCMemcard/GCMemcardRaw.cpp`, `DolphinLibretro/Main.cpp`, and `DolphinLibretro/Boot.cpp`. The Boot change guards optional Game Boy Player configuration with `HAS_LIBMGBA`, allowing the intended build with mGBA disabled.

Source/diff inspection verifies named constants, struct size assertion, unknown-button/port rejection, independent analog values, state error propagation, and field-count capture points. Runtime acceptance must still prove real game boot/presentation/audio; exact observed SI pad values and field/poll timing; state restore after different lengths of replay; fresh-process replay; corruption rejection and rollback; raw card rewind; and repeated load/unload/reload on the persistent owner thread. Compile/test results should be recorded separately rather than inferred from this review.

## Native host review, first implementation

`native/TasStudio.LibretroHost/host.cpp` selects D3D11. The pinned core's `DX11SwapChain::Present` explicitly binds its RGBA8 texture as pixel-shader resource slot 0 before calling the video callback, so retrieving that SRV and copying into a staging texture is supported by the inspected implementation. Context reset occurs after successful game load and before first run. Context destruction precedes unload; the core's unload recognizes the destroyed context and skips duplicate backend shutdown. Runtime lifecycle proof is still required.

The native host now enforces owner-thread access across getters and mutations, refuses wrong-thread DLL destruction, validates bounded memory operations without additive overflow, catches callback allocation exceptions, checks software row pitches and metadata output pointers, retains whole stereo audio pairs, and rolls back failed savestate restores. Failed rollback poisons execution until the instance is recreated. A caller must still validate project compatibility and checksums before invoking native state parsing.

Verification: `cmake -S native/TasStudio.LibretroHost -B native/build-host -G "Visual Studio 18 2026" -A x64` and `cmake --build native/build-host --config Release` succeeded with zero warnings on 2026-09-06. This is a compilation result, not runtime acceptance.

## Native executable smoke test

The `tas-native-smoke` CMake target loads the same host/core ABI without .NET. It accepts four paths: core DLL, system resources root, isolated save directory, and ROM. On 2026-09-06 the initial native run booted Skies of Arcadia Legends (USA), advanced fields 3–7, captured and restored 106,925,752 bytes, and destroyed the host cleanly (exit 0). The initial application boundary was field 2, reflecting boot work before the first ordinary step. The test profile is `artifacts/native-smoke/profile`; logs stay local.

The enhanced smoke test passed against the rebuilt core on 2026-09-06. It checked every field increment and verified neutral inputs, opposing 0/255 stick extremes, digital L/R with partial analog triggers, and digital L/R with zero analog triggers against actual pad-poll observations. Fields 8–11 each contained **two controller polls**, preserving the submitted snapshot. These tested intervals contained no zero-poll interval; zero-poll behavior remains supported by the hold contract but was not empirically covered by this run. Refresh rate settled to 59.9401 Hz after the host refreshed AV info after each step.

The smoke also changed the serialized console marker to Wii and confirmed native restoration rejected it and recovered the previous field/tick boundary. Restoring the valid capture then recovered its original field/tick values. It completed context/game teardown with exit 0. Full output is in `artifacts/native-smoke/process.log`.
