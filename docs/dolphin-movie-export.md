# Experimental standalone Dolphin export

Choose **File → Export to Dolphin → Quick… / Full…** from a power-on GameCube project.
**Quick** uses cached controller polls without replaying the timeline. Every group
must have valid polls matching its current input and history; missing or stale
polls stop the export with a message to use Full. **Full** replays the complete
active timeline to materialize and verify its polls. Both preserve the original
paused state and recover the initial card and startup polls through a verified reboot.
Quick still performs that short boot and reads the game image for its checksum.
A progress bar during Full shows completed input groups, total
groups and percentage during baking, followed by status messages while restoring
the session, recovering startup polls and the card, and writing files. Captured/savestate starts, memory-write events, reset
events, Wii games and recordings with no controller polls are rejected.

The result is a `.dtm` plus a uniquely named `.dtm.dolphin-*` companion directory:

- `Play in Dolphin.cmd`: double-click to select Dolphin.exe and play the movie
  with its exported profile. It restores original card copies before each test
  and refuses to reset a card while that profile's Dolphin session is running.
- `README.txt`: an exact PowerShell launch command and test limitations.
- `User/Config`: standalone settings translated from Studio's requested options.
- `InitialCards`: original Slot A card bytes recovered by restoring the project's
  initial state, stopping Dolphin to flush its card writer, and restoring the
  session. These are raw memory cards, not converted savestates.
- `User/GC`: working copies of those cards for standalone playback.
- `export.json`: export mode, source identity, settings, timing, startup poll count and card hashes.

Use the launcher rather than opening the DTM from your usual Dolphin profile:
the DTM alone does not carry every exported setting or the memory card. For manual
launches, close standalone Dolphin and copy `InitialCards` over `User/GC` before
repeating a test. Keep the DTM and companion directory together. The manual launch command uses
absolute paths; update them if moving the export.

The first comparison target for the pinned libretro revision `e1e6d25` is upstream
commit `430138f468effe6bf396adf3cd4d46df4cbd9050`, 282 commits beyond release 2606.
It was incorporated by [this upstream merge](https://github.com/libretro/dolphin/commit/d02c31a87789870d2242e1e39e1fe177e0a6151f).
Libretro-specific changes and Studio's patches remain differences.

## Format and limits

The file follows `Movie::DTMHeader` and `Movie::ControllerState`: a 256-byte
little-endian header and one 8-byte record for each recorded GameCube poll.
Buttons are remapped explicitly, analog bytes are preserved, and the connected
bit is set. Counts use VI fields and actual polls, not editable presentation
groups. The final tick is the absolute CoreTiming tick of the last poll. The
project's start UTC becomes the movie's RTC epoch; the game's MD5 covers the
actual supplied image file, as Dolphin's movie checksum does.

The DTM embeds its supported settings, resolved from Studio options and system/local
game INI overrides. This also avoids Dolphin 2606 reading region-dependent card
settings before game metadata is initialized. The companion profile supplies
additional settings. Game INI settings outside the DTM subset, BIOS and DSP ROM
files are not copied.
The profile targets Windows x64 JIT and D3D, matching Studio's supported host.

The initial `retro_run` occurs before Studio's editable timeline begins. Export
reboots using the original card and session options/game INIs, passively captures
those startup polls, and prepends their actual pad values. It verifies the recreated
boot's ticks, video fields and all 24 MiB of MEM1 against the project's initial
state before accepting the prefix. A mismatch or unsupported capture fails the
export without replacing an existing DTM. Input/lag totals include the startup
polls. DTM has no per-poll timestamps and ends when its input stream ends,
including when Studio has trailing groups without polls.

A diagnostic of GEAE8P found 137 neutral port-one polls before its initial boundary
(tick 547319917, field 70). Two fresh boots using the same exported card/settings
produced identical traces. Omitting these polls shifts the DTM stream early.
The exporter now measures this prefix automatically; no game-specific count is
hardcoded. The user confirmed that a corrected existing movie played successfully.

Opening a DTM successfully does not prove that it stays synchronized. Compare
recognizable gameplay/RNG outcomes and identify the first divergence. This feature
does not change Studio's recording or savestate format. Patch `0006-boot-polls.patch`
adds an optional ABI 3 capability, requiring an updated core/host for export.
Older projects can be opened using the existing build-compatibility warning flow.

## Verification

Managed tests check the binary layout against explicit DTM offsets and button
bits, measured startup prefixes (including non-neutral values), missing/mismatched
startup capture, multiple/zero-poll groups, initial timing, edited-group baking, unsupported
starts/events, and restoration after success/failure.

Run a real-ROM export smoke check:

```powershell
dotnet run --project tests/TasStudio.Integration.Tests -c Release -- <game.iso> <new-output-directory> dtm-export
```

To export an existing project/replay with the same check, add
`--movie <project.tasproj-or-movie.tasreplay>`. This reads the source, bakes in an
isolated profile, checks the DTM and card copies, and verifies that all MEM1 bytes
and the paused position survive export. It then runs Quick against the baked cache
and requires a byte-identical movie with the same paused RAM/position.
It does not run standalone Dolphin.
