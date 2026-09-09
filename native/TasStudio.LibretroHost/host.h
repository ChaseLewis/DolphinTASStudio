#pragma once
#include <cstdint>
#include <cstddef>
#define TAS_API extern "C" __declspec(dllexport)
struct tas_host;
struct tas_pad { uint16_t buttons; uint8_t stick_x, stick_y, cstick_x, cstick_y, trigger_l, trigger_r; };
static_assert(sizeof(tas_pad) == 8);
struct tas_poll { uint64_t ticks, fields; uint32_t port; tas_pad pad; };
static_assert(sizeof(tas_poll) == 32);
// UTF-8 strings; all host operations run on the creating thread. One live host per process.
TAS_API tas_host* tas_create(const char* core, const char* system, const char* saves, int resolution, int dsp_hle);
TAS_API const char* tas_error(tas_host* host);
TAS_API int tas_set_option(tas_host* host, const char* key, const char* value);
TAS_API void tas_destroy(tas_host* host);
TAS_API int tas_load(tas_host* host, const char* game);
// ABI 2: hold pad through the next nonduplicate presentation and stop at a VI boundary.
TAS_API int tas_step(tas_host* host, const tas_pad* pad);
TAS_API int tas_replay_step(tas_host* host, const tas_pad* fallback, const tas_poll* polls, size_t count);
TAS_API size_t tas_polls(tas_host* host, tas_poll* polls, size_t capacity);
TAS_API int tas_reset(tas_host* host);
TAS_API uint64_t tas_fields(tas_host* host);
TAS_API uint64_t tas_presentations(tas_host* host);
TAS_API uint64_t tas_ticks(tas_host* host);
TAS_API double tas_fps(tas_host* host);
TAS_API size_t tas_state_size(tas_host* host);
TAS_API int tas_save(tas_host* host, void* bytes, size_t size);
TAS_API int tas_restore(tas_host* host, const void* bytes, size_t size);
TAS_API int tas_memory(tas_host* host, uint32_t address, void* bytes, size_t size, int write);
// Copy the latest RGBA image. Returns required bytes; width/height/sequence are always populated.
TAS_API size_t tas_video(tas_host* host, void* bytes, size_t capacity, unsigned* width, unsigned* height, uint64_t* sequence);
// Read and consume interleaved signed 16-bit stereo samples. Return sample count, not frame count.
TAS_API size_t tas_audio(tas_host* host, int16_t* samples, size_t capacity, unsigned* rate);
// Owner thread, between advances. Returns sample rate, or zero on failure.
TAS_API unsigned tas_audio_playback(tas_host* host, int enabled);
// Exception to owner-thread rule: one audio consumer may mix concurrently with
// tas_step/replay. Caller must stop/join that consumer before destroying the host.
TAS_API size_t tas_mix_audio(tas_host* host, int16_t* samples, size_t frames);
