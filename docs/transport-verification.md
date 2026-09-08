# Transport layout verification

September 6, 2026.

The approved mockup is implemented as native Avalonia controls: project/tool actions at the top, playback transport above the timeline, and a full-width Next Frame button below the TAS inputs. Next Frame advances a single-frame selection that follows the preview, so subsequent edits target the next input. Separate range/candidate selections retain their existing behavior.

Previous state walks strictly backward through named states and automatic checkpoints with valid input/configuration history, then falls back to the project baseline. It restores disk-backed states directly and updates checkpoint LRU use. It never clears the ROM, timeline or takes. The selected frame follows that restored preview.

Seek is a compact icon button beside Save state. Select frames or drag ranges directly on the timeline; numeric selection boxes have been removed. Seek moves the preview to the selection's start without changing the range or its inputs. Cancel seek appears in the same toolbar during a seek.

Right-click a marker above the timeline to load or clear the selected state. Clear works for named states and automatic checkpoints without moving the preview or editing inputs. The removal marks the project dirty for saving/recovery. Owned cache files are retired; exported states are preserved, and assets belonging to a saved manifest are cleaned up when the updated manifest commits. Automatic checkpointing remains enabled and can generate a new checkpoint when replaying through that part of the timeline later.

Play uses recorded inputs and stops at EOF without reading the live controller or appending inputs. Physical recording is explicitly available through the Movie menu and shows REC in the transport. Escape is pause; restarting is a separately named action.

Validation:

- Fake-backend tests cover EOF playback without input mutation, explicit live recording, repeated previous-state jumps through named and automatic states, strict earlier-than semantics, baseline fallback, invalidated history, and no replay steps during jumps.
- Headless Avalonia input events exercise the actual Previous state button and F11 while a numeric input field has focus, then check execution, input preservation and preview-following selection. The Seek button is checked against a separate range selection, including preservation of its bounds and inputs.
- Default minimum-size layout checks verify the input controls, Next Frame, and timeline buttons remain inside their panels. Existing docking, normalization, project and recovery tests remain in the suite.
- Full managed suite after marker clearing and loading UI: 152 passing tests. Clear coverage includes right-click selection/menu execution, unchanged inputs/preview, saved manifest cleanup, preserved exports, and stale marker handling. Captures: `artifacts/transport/minimum-layout.png`, `artifacts/transport/transport-loaded.png`.

These are service and headless UI checks; no new native Dolphin replay/pixel-determinism claim is made by this change.
