# Startup rendering and loading indicator

Verified September 6, 2026 with the local Skies of Arcadia Legends (USA) image.

With the project default `dolphin_wait_for_shaders=enabled`, native captures remained entirely black after 2,400 inputs. Disabling that option restored visible frames. The libretro shader wait path was presenting its internal loading UI before the first game frame. The native patch now waits for compilation without those frontend-inappropriate UI presents; actual shader compilation and the editable setting remain enabled.

The Avalonia frontend displays a modal indeterminate loading indicator while initializing the emulator and compiling shaders. It covers loose game loading, opening projects, creating projects, and configuration changes that reboot the game. It closes on success or failure. It does not report a fabricated percentage or offer cancellation that the native boot operation cannot safely honor.

Validation:

- 148 managed tests pass, including loading indicator lifetime and failure cleanup.
- Native `boot-preview restore-baseline` check boots with default project settings and shader precompilation enabled, restores the initial project state, requires visible video within 600 inputs, runs to 2,400 inputs, then restarts and verifies identical RGBA pixels at input 600.
- Actual app title screen captured at preview frame 2,618: `artifacts/skies-fixed-title/title-screen.png`.
- Production output refreshed in `artifacts/prod`. Existing running dev instances were left alone.
- The native patch passes reverse-application validation against the vendored source. Native core changes retain the existing core identity checks: save states from another core build are not silently treated as compatible.

Run the native regression after building:

```powershell
& tests/TasStudio.Integration.Tests/bin/Release/net10.0/TasStudio.Integration.Tests.exe '<ROM path>' artifacts/boot-preview-check boot-preview restore-baseline
```

Use a fresh output directory to exercise a cold shader cache.
