# Native per-call tracing

The pinned Dolphin-libretro core has an opt-in `DOLPHIN_TAS_TRACE` CMake option, default OFF. The ordinary build explicitly disables it. A trace-enabled core still captures nothing until runtime start. The working `artifacts/prod` distribution is not replaced.

## Build

From the repository root, with the existing pinned Dolphin checkout, CMake 4.2 or newer, and Visual Studio 2026 C++ tools:

```powershell
.\scripts\build-trace.ps1
```

This applies the existing TAS contract patch followed by `native/patches/0002-tas-trace.patch`, builds into `native/build-dolphin-trace`, and runs the standalone buffer tests. It does not publish, modify the production DLLs, or launch experiments. Output: `native/build-dolphin-trace/Binaries/dolphin_libretro.dll`. The trace patch uses Dolphin's debugger breakpoint callbacks, without pausing on a hit or patching game instructions. Enabling/disabling capture invalidates the JIT cache and temporarily enables debugging; this has host-side overhead and requires replay-parity testing.

## Native interface

These additive C exports are independent of the existing TAS ABI 3. The Windows x64 layout is defined in `Source/Core/Core/TasTraceBuffer.h` (included in the patch).

| Export | Signature / behavior |
| --- | --- |
| `dolphin_tas_trace_version` | `uint32_t ()`; 1 when supported, 0 when compiled out. Older cores lack the export. |
| `dolphin_tas_trace_start` | `bool (const uint32_t* addresses, size_t count, size_t capacity)` |
| `dolphin_tas_trace_stop` | `bool ()`; retain buffered records. |
| `dolphin_tas_trace_status` | `bool (Status* output)` |
| `dolphin_tas_trace_drain` | `size_t (Record* output, size_t capacity)`; consume records, or query pending count with null output. |

All capture operations require the libretro owner thread, single-core mode, and a paused boundary between runs. Start accepts 1-64 distinct aligned cached MEM1 instruction addresses and 1-1,048,576 records of capacity. Existing breakpoints at requested addresses, concurrent capture, or unread records from the previous capture cause rejection. Stop restores the previous debugging/breaking settings and removes only owned breakpoints.

Each 160-byte record holds `uint64_t sequence, ticks, fields`, `uint32_t pc, lr`, then all 32 `uint32_t` GPRs. Values describe the state **before** the selected instruction executes. Sequence establishes event order; ticks are Dolphin CoreTiming timestamps, not a claim of instruction-cycle-exact hardware timing. The callback performs no file I/O or allocation. Full buffers drop new records and increment persistent overflow counters; reject such captures for call-count analysis.

The 48-byte status holds four `uint32_t` fields (`version, record_size, active, stop_reason`) followed by four `uint64_t` fields (`capacity, pending, observed, dropped`). Stop reasons: 0 none, 1 explicit, 2 reset, 3 restore attempt, 4 unload, 5 start exception. Reset/restore/unload terminate the current capture segment; trace state is deliberately not serialized in save states. Drain before unloading; deinitialization frees the buffer. `observed` counts recorded plus dropped hits and survives drains, resetting at the next successful start.

## SOA RNG probe

For the verified USA DOL only:

- `0x8025ECE0`: RNG return instruction. `r4` = old seed, `r0` = new seed, `r3` = output, LR = caller return address.
- `0x8025ECC0`: seed setter return instruction. `r3` is the supplied seed.
- State location: `0x803469A8`. The smoke test verifies `new = old * 1103515245 + 12345` modulo 2^32 and `output = (new >> 16) & 0x7fff` for each captured RNG return.

ROM SHA256: `C21971AA819790337887E989741C7E4BA606AE7DDDD3FB1F53F74A9C2684D092`. These addresses are not portable to other revisions.

## Live validation

Export input intent without transferring an old core's save states. Replace the sample checkout, project and ROM paths with your own:

```powershell
.\scripts\export-trace-input.ps1 -Project 'D:\TAS\My run\movie.tasproj' -Output 'D:\Source\TASDolphin\artifacts\my-trace-input.bin'
.\native\build-trace-tests\Release\trace-smoke.exe `
  'D:\Source\TASDolphin\artifacts\prod\native\TasStudio.LibretroHost.dll' `
  'D:\Source\TASDolphin\native\build-dolphin-trace\Binaries\dolphin_libretro.dll' `
  'D:\Source\TASDolphin\artifacts\prod\system' `
  'D:\Source\TASDolphin\artifacts\my-trace-smoke' `
  'D:\Games\Skies of Arcadia Legends (USA).iso' `
  5800 'D:\Source\TASDolphin\artifacts\my-trace-input.bin'
```

Both output paths must be fresh. The exporter verifies timeline/chunk hashes, writes 8-byte packed pad intents plus provenance JSON, and leaves the source project unchanged. This is **not canonical poll replay**: it reuses intended pads under the new build, which establishes a fresh baseline after 120 warmup groups with fixed RTC 946684800. It does not prove equivalence to the old published backend or that every original movie event was replayed.

The live smoke requires nonempty traces, checks RNG arithmetic, compares two captures byte-for-byte across all recorded registers, checks complete terminal MEM1/ticks/fields against an untraced run, forces bounded-buffer overflow, rejects invalid addresses/wrong-thread access, and verifies restore stops capture without discarding unread data. It writes `passed.json` only after all checks succeed. CSVs retain selected registers for inspection. The build script's CTest run covers only the buffer tests, not this ROM-dependent test.

## Initial validation and subsequent completeness correction (2026-09-07)

**The original 19,791-record capture below is incomplete.** A later RNG-chain audit found that Jit64's followed-BLR path (`op.skip`) bypassed the normal breakpoint callback. Guest calls still executed correctly; some return-address observations were absent. Sequence continuity and matching final state did not detect this. Do not use the original CSVs for total call counts.

The trace patch now explicitly emits the observer for selected followed BLRs. The corrected core SHA256 is `C0AFA7DCFEDBACF6D86DFF7AE114933DE29DEE3ADE1C5992356276A2A61CB5A7`. A separate battle-analysis harness (not included in this repository) rejects missing RNG transitions, trace sequence gaps, overflow, or a mismatch between traced and live seed at any boundary. Its regression tests include valid individual RNG results with a missing intermediate call.

Local corrected-core battle evidence was recorded as `battle-first-20260907-c` (not included). The baseline has 629 RNG returns plus 42 kernel entry/exit observations (671 total), versus 245 RNG observations in the older battle capture. Repeated enabled battles match exactly. The enabled/disabled comparison matches terminal MEM1 and timing but **fails strict intermediate-frame parity**: 26 of 836 samples differ in ticks, 11 differ at byte `0x80347603`. Other sampled fields, including actor data, positions, decisions, HP, rewards and RNG, match. These differences are preserved in `parity-diagnostics.json`; they have not been waived. Broad nonintrusiveness and cycle-accurate tracing remain unproven.

The following records the earlier, weaker test for provenance:

The live smoke above completed successfully using 5,800 groups after warmup. Each enabled run captured 19,791 RNG returns from 18 distinct LR values, with identical full records, no sequence gaps, and no backwards timestamps. The untraced run captured zero records and matched all 24 MiB of terminal MEM1, ticks (99,295,863,987), and fields (12,249). The forced-overflow and restore-boundary checks also passed. No seed-setter hits occurred in this window, so that address was not independently exercised by this capture. These results establish this replay's parity, not universal parity for all games or hook selections.

Local evidence (not included in the source repository): `artifacts/trace-smoke-20260907-c/passed.json`, adjacent CSVs, `artifacts/trace-smoke-c.log`, and `artifacts/trace-script-build.log`. Reproducible input export: `artifacts/trace-input-verified.bin` plus its provenance JSON, SHA256 `8AFF250970D638C9970A228C173EDB8120B3F0FF804BB68E2A9602A02BD9A066`.

Trace core SHA256: `96A25CD617A11C7F444506CBBED274E9E3438E91455EC7EAF455A32EF75D1A03`. The existing production core (`97106F3AD00986239A4DD02CB0F287DDD92219411880356D6C44300B75B73D17`) and host (`EAB82B4D80E9CF39700D10F7D8FD3628F27E5EE1D040B737C25599CC787AE369`) remained unchanged.

## Next integration

The experiment SDK/worker does not yet expose these trace exports. Add owner-thread host/managed wrappers, per-trial capture configuration, and durable trace artifacts outside the worker's temporary trial folders (successful trial folders are cleaned up). Record ROM/core/host/input hashes, RTC, capture points, segment identity and overflow status alongside each trace. Preserve backend identity checks: old save states are not interchangeable with a newly built core.

First use identical input and RTC across repeated trials. The existing ElectriSearch experiment varies UTC by trial index, so it is not an identical-repeat baseline. After the baseline passes, vary one choice at a time and correlate caller addresses with movement, attack selection, and animation handlers. Registers alone do not yet identify battle actors or reconstruct their grid state; selected memory snapshots and additional hook addresses are separate work.
