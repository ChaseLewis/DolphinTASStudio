# Input editing, transport and recovery

Updated September 6, 2026.

- Opening a game creates an unsaved project immediately. **Play/Pause** in the timeline plays recorded active inputs and pauses at the end. **Movie → Record live controller input** explicitly enables recording from the configured device at the timeline end; the transport displays REC.
- **Restart** returns to the project baseline without closing the ROM/project. **Previous state** restores the nearest valid named state or automatic checkpoint strictly before the preview frame; repeated clicks walk backward, falling back to the project baseline. Invalid history is skipped, and checkpoint access updates its LRU age. Escape pauses without rewinding.
- The top toolbar contains Projects, Save, Controllers, Config, Watches and audio. **Save** and Ctrl+S save the project. Save As chooses the folder for an untitled or recovered project. Recovery status is available by hovering the project status; failures remain visible in the status bar.
- TAS controls update the selected range automatically. Only changed controls replace mixed values. Main/C-stick pads show red position dots and retain exact byte entry; trigger pressure stays separate from digital trigger clicks.
- Each stick has an independent, initially disabled **Normalize** control and radius from 0 to 1. `1.00` is full circular radius; `0.10` is 10%. Enabling it or changing its radius rescales the shown direction. Dragging or editing X/Y while enabled updates both axes as one edit. A blue ring shows the target radius. Byte values use neutral 128 and signed endpoints 0/255, so fractional results round to the nearest representable input. Center and radius zero produce neutral. Mixed axes remain unchanged until both coordinates or a drag supply a direction. Selection changes do not silently normalize existing timeline data. The constraint is an editor aid, not recorded project metadata; its resulting input bytes are saved normally.
- **Next Frame** lives under the TAS Input controls; F11 also works while an input numeric field has focus. It plays existing recorded inputs unchanged and uses displayed controls only when creating a new frame group. F12 plays one existing recorded group with a parked cursor and stops at the end; F10 uses neutral input for new groups and moves the cursor to the new preview frame. A single-frame selection at the preview follows to the next frame, ready for the next edit; separate selections and ranges stay selected. Selecting a different frame does not seek. Candidate edits stay separate; Next Frame plays active inputs and Audition previews the candidate.
- Timeline editing, Undo/Redo, Audition and checkpoint settings are available from the timeline context menu or its overflow button. Right-click targets the clicked row/marker while preserving a range when clicked inside it. Markers support Load and Clear. Seek sits immediately before Save state in the transport and moves the preview to the selection's start. Select frames/ranges directly on the timeline; numeric selection boxes have been removed.
- Controller setup groups buttons, D-pad, both sticks and triggers. Select Keyboard or an XInput slot, click a binding, then press/move the desired control. Escape cancels capture, Backspace/right-click clears, and Save applies the draft. Existing bindings remain compatible.

## Recovery

The app writes a separate recovery project every five seconds while a project has changes. Creating/opening a project prepares the initial recovery copy; replacing an unsaved session or explicitly saving also attempts a backup. The timeline shows the last successful backup time or its error. Background serialization does not pause playback or call Dolphin off its owner thread.

Recovery preserves the baseline, requested configuration, exact inputs, ordered events, candidate takes and section provenance. It deliberately excludes temporary checkpoints/named states: those may disappear while the background writer is active, and inputs can be replayed from the baseline. The saved project is not overwritten by autosave.

Backups live in `%LOCALAPPDATA%/TasStudio/Recovery/<session>/recovery.tasproj`. Writes commit assets before atomically replacing the manifest. A failed or interrupted replacement leaves the previous committed backup usable. Old generated recovery JSON/state assets are collected conservatively after commit; there is no blanket sweep of unrelated files.

An exclusive session marker distinguishes an active instance from a previous crash. A normal close clears its marker; a later normal startup offers recovery for unlocked markers left by an unclean exit. File → Recover inputs or the timeline's Recover button also lists previous backups. Recovery opens as an unsaved project so Save As cannot accidentally overwrite the original project.

Recovery is periodic, not a per-input durability journal. A crash can lose the interval since the last completed backup (normally up to five seconds plus write time). Backups from previous sessions remain available; there is no global recovery-folder size limit yet.

## Verification

Service tests cover Stop retaining the project, owner-thread capture/background writing, candidate/event recovery after session disposal, playback continuing during backup, and a failed write retaining the previous backup. Native `recovery-create` / `recovery-restore` tests reopen a 120-input Skies of Arcadia recording in a fresh process and compare inputs, candidate and terminal 24 MiB MEM1. This does not close the separate known first-three-VI rendering mismatch.

The controller capture tests use injected XInput samples. Physical XInput hardware has not been verified in this session.
