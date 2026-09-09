# Playback audio and transition pacing

Continuous Play uses Dolphin's producer/consumer mixer and internal throttler.
The execution thread still advances deterministic presentation groups and owns
all emulation work. The NAudio device thread reads PCM directly from Dolphin's
mixer through `IAudioSource` / `tas_mix_audio`, even while `retro_run` is inside a
long presentation group. There is no presentation-sized PCM handoff or extra
managed queue in this path. WaveOut remains the Windows device backend; we do
not bundle Cubeb.

`0005-realtime-audio.patch` adds an opt-in playback mode. It prevents Libretro's
synchronous `Stream::Update` from consuming the mixer, enables Dolphin's default
gap filling, sets normal speed, and clears Libretro's temporary throttle-disable
flag. Dolphin's existing VI throttle therefore controls timing during transitions
with no new XFB presentation. The managed presentation pacer is bypassed while
this mode is active, avoiding two independent clocks. Starting/resuming resets
the host timing reference so time spent paused does not become catch-up debt.

The device requests signed 16-bit stereo at the mixer's actual sample rate
(32029 Hz for the tested GameCube path). Dolphin supplies resampling, queue
correction, gap repetition and fades. WaveOut requests 80 ms device latency;
Dolphin also retains its own configured mixer buffer. This is not a claim of
80 ms end-to-end latency.

Pause retires the source, waits out any active read, disables realtime mode and
stops the device. Retired sources return silence even if a device retains an old
provider across a resume or game replacement. State serialization, restoration
and reset exclude mixing with the native audio mutex; `retro_run` does not hold
that mutex. Teardown retires the source before destroying the native host.
Mode switches clear only the mixer's output history, preserving its emulated
sample-rate and volume registers. Emulation and audio consumers are stopped
during that clear.

Frame advance, seek and experiment execution do not enable realtime mode. They
retain unthrottled deterministic execution, existing input polls and presentation
boundaries, and the packet audio path. Its fallback output buffer retains two
seconds of capacity and an 80 ms startup threshold. A September 9 Skies replay
found six packets over the old 250 ms limit, including a 899 ms loading transition
and a 1235 ms startup packet; that earlier buffer correction remains useful for
packet output.

This patch changes the core binary identity. Existing projects/states and batches
must retain their original runtime; compatibility checks are not bypassed.
Validation of a newer build imports inputs/polls into a separate fresh power-on
baseline. Speaker timing is not an experiment parameter.

Tests cover independent device reads, retired providers, execution mode lifecycle
and failure cleanup. The `realtime-playback` native integration test compares
unpaced and paced RAM, video and poll timing, verifies audio consumption inside
a long advance, and exercises save/pause/restore/unload with a live consumer.
These checks cannot establish that every audible hitch is gone: a sufficiently
long shader compilation or host stall can still exceed the mixer buffer.
## Upstream design references

- [Dolphin device callback](https://github.com/dolphin-emu/dolphin/blob/master/Source/Core/AudioCommon/CubebStream.cpp)
- [Mixer queues and gap handling](https://github.com/dolphin-emu/dolphin/blob/master/Source/Core/AudioCommon/Mixer.cpp)
- [VI throttle and loading-screen safeguard](https://github.com/dolphin-emu/dolphin/blob/master/Source/Core/Core/CoreTiming.cpp)
