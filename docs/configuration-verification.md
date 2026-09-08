# Configuration verification — September 6, 2026

**Update:** the historical first-three-frame fresh-process mismatch recorded below is now resolved for the tested Skies sequence. See [workflow verification](workflow-verification.md) for the fix and strict 600-observation results. Historical failures below remain as the original evidence.

## Editable configuration and recovery verification

The integrated revision passes 94 unit tests covering configuration, checkpoint retention/invalidation, rollback, repeated reopening, disk cleanup, recovery and controller/TAS input semantics. Native tests on the pinned Dolphin core and Skies of Arcadia Legends separately passed:

- UTC +1 day changes the native boot clock by exactly 3,499,200,000,000 ticks.
- 1×→2× changes output dimensions from 640×528 to 1280×1056 while preserving all 360 recorded inputs.
- Old-setting states are rejected; checkpoint policy and settings/history identity survive same-process and fresh-process reopening.
- A contradictory game compatibility UTC override wins over the requested profile and changes dependency identity.
- Residual `User/Config` files are archived before explicit-profile boot. Repeated boot clock, identity and tested pixels match; local compatibility overrides and save storage survive.
- A separate 120-input recovery opens in a fresh process with matching inputs, candidate and terminal 24 MiB MEM1. Stop retains its ROM/project and inputs.

Evidence: `artifacts/final-config-create.log`, `final-config-restore.log`, `final-clean.log`, `final-recovery-create.log`, and `final-recovery-restore.log`. Settings pages were inspected in `artifacts/editable-config*.png`. These tests do **not** resolve the historical first-three-VI restore rendering mismatch below, prove every option combination, or test physical XInput hardware.

Packaged app UI smoke also passed: dragging both stick pads immediately changed selected input 0 to main stick 211/190 and C-stick 50/56; Next Frame appended those exact values at input 360 and advanced the preview to 361 without moving selection. The on-disk recovery file contained both edits and its timestamp advanced. Stop returned to state 0 while retaining the ROM, project name and 361 inputs. The test closed with Discard and left its source project unchanged. Controller UI checks verified keyboard capture, Escape preserving the previous binding, and Cancel discarding the draft; no mappings were saved during that check.

## Historical inspector verification

This report describes the app-v4 read-only inspector and its native rendering tests. That UI is superseded by editable per-project configuration and disk-backed checkpoint policy. The build hashes and results below belong to that earlier implementation; they are not verification of the newer configuration profiles. The known first-three-VI fresh-process rendering mismatch remains an open gate until a separate native test records its resolution.

The current design allows changing Start UTC, graphics and DSP choices while preserving inputs/events, then rebooting with a new baseline and optionally replaying to selection. It includes settings/host/resources in execution identity and persists checkpoints on disk. Requested values and hashed dependencies still are not effective runtime readback. See [current configuration policy](configuration-policy.md) and [folder format v3](project-format-v2.md).

The historical inspector had 41 passing unit tests. The separate results above cover the later implementation.

## Historical changes delivered

- Replaced the hardcoded Config message with a resizable settings window:
  Profile, Graphics, Audio & input, Paths, and Verification. Long content scrolls.
- Obtain requested session options on the emulation owner thread. A reopened
  project's resolution/DSP and isolated storage path are shown instead of app
  defaults. No effective-settings readback or verification badge is fabricated.
- Configuration pauses playback and flushes listening audio. Controller bindings
  open through the existing controller dialog. No new emulation switches.
- Set `dolphin_osd_enabled=disabled` as a base option in the native host. This
  prevents time-dependent memory-card notifications from entering game pixels
  in the tested case; game compatibility layers retain precedence.
- Added per-VI raw RGBA comparisons to the existing native integration harness,
  fresh-process comparisons, callback diagnostics, saved baseline previews and
  optional diagnostic frame dumps. No initial steps are excluded from comparison.
- Recorded [the policy](configuration-policy.md), including the user's decision
  that compatibility overrides always win over Studio defaults.

No project schema, checkpoint identities or Dolphin core patch changed. The
existing core binary is retained. The native host binary changed for the OSD
fix; current project identity still lacks a host hash, as documented in the audit.

## Results

| Check | Result |
| --- | --- |
| Release solution build | Passed, no managed warnings or errors |
| Core/service/input unit tests | 41 passed |
| Session config on project reopen | Existing roundtrip test now saves 2x/LLE and asserts those saved settings and core identity after reopen |
| Native host build | Passed |
| Initial 600-step RGBA replay comparison | Failed: a memory-card OSD notification appeared in one replay only; terminal MEM1 matched |
| 600-step replay with OSD disabled, same process | Passed: all raw per-VI image hashes/dimensions and terminal 24 MiB MEM1 hash matched |
| Fresh-process restore with OSD disabled | **Failed rendering gate:** first 3 VI observations differ; remaining 597 match. Terminal MEM1 matches the reference |
| Existing project opened in packaged settings UI | Checked by screenshot capture using the existing iteration-2 project fixture |

The fresh process produces a flat magenta image at the beginning of the replay;
the reference shows the game image. A second restore in that process matches the
reference. The precise core-side restoration cause remains unresolved. This is
not a pass, and the test does not skip the divergent boundaries. No speculative
restore change is included in the delivered code.

The initial callback-count comparison also found a re-presentation after restore.
Callbacks are now saved separately as diagnostic data. The asserted visual
contract is the actual displayed image at every VI: duplicate/no-output fields
hold the previous image, including the serialized baseline preview. Image
dimensions and all RGBA bytes are compared before UI scaling.

The integration harness checks complete MEM1 only at the end of each replay.
It does not yet prove per-step gameplay equivalence, deterministic audio output,
every scene, other games, alternative profiles, or different graphics setups.
Audio checks remain nonzero/non-silent production checks. Full profile validation
and an in-app Verify action are not implemented by this settings window.

## Reproduction and artifacts

Core revision: `e1e6d25fa1392b7d1bc05bf800c71b807a2bd2e0` with the existing TAS patch.

Core SHA-256:
`359B52967495D4117D212EFAB0513E2A4CC6C235FBBCB27D8B49A85F940C7AD4`

Native host SHA-256:
`A285236379A16DA8FDC10ACDFFA50E07958F08FD16DD9638B87A23D78C99D705`

ROM: Skies of Arcadia Legends (USA), SHA-256
`C21971AA819790337887E989741C7E4BA606AE7DDDD3FB1F53F74A9C2684D092`.

Windows reports NVIDIA GeForce RTX 4070 SUPER, driver `32.0.15.9621`.
This is a system inventory observation; the host still needs an actual selected-
adapter readback. D3D11, requested native resolution/HLE, single core, 1800-step
warmup and a 600-step replay. The test uses its own writable storage.

```powershell
dotnet build TasStudio.slnx -c Release --nologo
dotnet test tests/TasStudio.Core.Tests -c Release --no-build --nologo

# Set $testRom to the local game image. The restore command currently fails
# intentionally on the first divergent rendered boundary; this remains a gate.
& tests/TasStudio.Integration.Tests/bin/Release/net10.0/TasStudio.Integration.Tests.exe $testRom artifacts/config-osd-off
& tests/TasStudio.Integration.Tests/bin/Release/net10.0/TasStudio.Integration.Tests.exe $testRom artifacts/config-osd-off restore --diagnostic-input 0
```

Local evidence (generated artifacts are ignored by Git):

- `artifacts/config-publish.log`: build, 41 tests and packaging.
- `artifacts/config-render-diagnostic.log`: pre-fix image mismatch.
- `artifacts/config-render-diagnostic/{first,second}-diagnostic.png`: game image
  without/with the memory-card notification.
- `artifacts/config-osd-off.log`: passing same-process replay and lifecycle checks.
- `artifacts/config-osd-off-fresh.log`: failing fresh-process rendering check.
- `artifacts/config-osd-off/*-video.txt`: displayed image hashes at every VI.
- `artifacts/config-osd-off/*-callbacks.txt`: new-image callback hashes by VI.
- `artifacts/config-osd-off/fresh-*-diagnostic.rgba` and `.json`: failing boundary.
- `artifacts/configuration-profile.png` and `-tab1` through `-tab4`: settings pages.
- `artifacts/app-v4/TasStudio.App.exe`: packaged app. Earlier app builds remain.

## Next implementation gate

The later workflow pass resolves the documented fresh-process GPU-state restoration failure for its tested sequence. Effective-profile readback and broader scene/profile coverage remain outstanding. Preserve game overrides and keep the strict first-boundary comparison; the passing regression does not justify skipping early frames or claiming every configuration is verified.
