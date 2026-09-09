#include "host.h"
#include <Windows.h>
#include <d3d11.h>
#include <d3dcompiler.h>
#include <wrl/client.h>
#include <libretro.h>
#include <libretro_d3d11.h>
#include <algorithm>
#include <cstdarg>
#include <cstdio>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <mutex>
#include <stdexcept>
#include <string>
#include <string_view>
#include <unordered_map>
#include <vector>

using Microsoft::WRL::ComPtr;
namespace {
constexpr unsigned BytesPerPixel = 4;
constexpr unsigned DefaultSampleRate = 48000;
constexpr size_t MaximumAudioSamples = DefaultSampleRate * 2;
constexpr uint32_t Mem1Base = 0x80000000;
constexpr unsigned TasAbiVersion = 3;
constexpr unsigned MaximumDimension = 4096;
constexpr unsigned MaximumControllerPorts = 4;
constexpr size_t LogMessageCapacity = 8192;
constexpr unsigned StereoChannels = 2;
constexpr unsigned CoreOptionsVersion = 2;
constexpr int AudioVideoEnabled = 3;
constexpr uint8_t StickCenter = 128;
constexpr uint8_t OpaqueAlpha = 255;
constexpr size_t MaximumStateBytes = 512ull * 1024 * 1024;
constexpr int MaximumResolutionScale = 6;
constexpr char OptionEnabled[] = "enabled";
constexpr char OptionDisabled[] = "disabled";
constexpr char CoreThreadOption[] = "dolphin_main_cpu_thread";
constexpr char CoreRendererOption[] = "dolphin_renderer";
constexpr char CoreResolutionOption[] = "dolphin_efb_scale";
constexpr char CoreDspOption[] = "dolphin_dsp_hle";
constexpr char CoreAudioOption[] = "dolphin_call_back_audio_method";
constexpr char CoreOsdOption[] = "dolphin_osd_enabled";
thread_local std::string creation_error;
std::mutex instance_mutex;
}

struct tas_host {
  HMODULE library = nullptr;
  DWORD owner = GetCurrentThreadId();
  bool initialized = false, loaded = false, load_attempted = false, context_active = false;
  bool faulted = false;
  std::string error, system_dir, save_dir;
  std::ofstream log;
  std::mutex log_mutex, audio_mutex;
  std::unordered_map<std::string, std::string> options;
  retro_hw_render_callback hw{};
  retro_hw_render_interface_d3d11 d3d{};
  retro_system_av_info av{};
  ComPtr<ID3D11Device> device;
  ComPtr<ID3D11DeviceContext> context;
  ComPtr<ID3D11Texture2D> staging;
  std::vector<uint8_t> pixels;
  std::vector<int16_t> samples;
  unsigned width = 0, height = 0;
  uint64_t sequence = 0;
#define CORE_FN(name) decltype(&retro_##name) name = nullptr
  CORE_FN(set_environment); CORE_FN(set_video_refresh); CORE_FN(set_audio_sample);
  CORE_FN(set_audio_sample_batch); CORE_FN(set_input_poll); CORE_FN(set_input_state);
  CORE_FN(init); CORE_FN(deinit); CORE_FN(load_game); CORE_FN(unload_game);
  CORE_FN(run); CORE_FN(reset); CORE_FN(serialize_size); CORE_FN(serialize); CORE_FN(unserialize);
  CORE_FN(get_system_av_info); CORE_FN(get_memory_data); CORE_FN(get_memory_size);
  CORE_FN(set_controller_port_device);
#undef CORE_FN
  bool (*set_pad)(unsigned, const tas_pad*) = nullptr;
  uint64_t (*fields)() = nullptr;
  uint64_t (*presentations)() = nullptr;
  uint64_t (*ticks)() = nullptr;
  bool (*begin_polls)(const tas_poll*, size_t, bool) = nullptr;
  bool (*end_polls)() = nullptr;
  size_t (*copy_polls)(tas_poll*, size_t) = nullptr;
  unsigned (*audio_playback)(bool) = nullptr;
  size_t (*mix_audio)(int16_t*, size_t) = nullptr;
  bool pull_audio = false;
  template<class T> void bind(T& function, const char* name) {
    function = reinterpret_cast<T>(GetProcAddress(library, name));
    if (!function) throw std::runtime_error(std::string("Missing core export: ") + name);
  }
  void check_owner() const {
    if (owner != GetCurrentThreadId()) throw std::runtime_error("Backend accessed outside its execution thread.");
  }
  void require_loaded() const {
    check_owner();
    if (!loaded) throw std::runtime_error("No game loaded.");
    if (faulted) throw std::runtime_error("Backend state recovery failed; stop and reopen the game.");
  }
};

namespace {
tas_host* active = nullptr;
std::filesystem::path utf8_path(const char* value) {
  return std::filesystem::path(std::u8string_view(reinterpret_cast<const char8_t*>(value)));
}
void check_hr(HRESULT result, const char* operation) {
  if (FAILED(result)) throw std::runtime_error(std::string(operation) + " failed (HRESULT " + std::to_string(result) + ").");
}
void RETRO_CALLCONV log_message(retro_log_level, const char* format, ...) {
  if (!active) return;
  char message[LogMessageCapacity];
  va_list args; va_start(args, format); vsnprintf(message, sizeof(message), format, args); va_end(args);
  try {
    std::lock_guard lock(active->log_mutex);
    active->log << message << std::flush;
  } catch (...) { /* Diagnostics must not unwind through the emulator callback. */ }
}
bool environment_impl(unsigned command, void* data) {
  if (!active) return false;
  auto& h = *active;
  switch (command) {
  case RETRO_ENVIRONMENT_GET_SYSTEM_DIRECTORY:
  case RETRO_ENVIRONMENT_GET_CORE_ASSETS_DIRECTORY: *static_cast<const char**>(data) = h.system_dir.c_str(); return true;
  case RETRO_ENVIRONMENT_GET_SAVE_DIRECTORY: *static_cast<const char**>(data) = h.save_dir.c_str(); return true;
  case RETRO_ENVIRONMENT_GET_LOG_INTERFACE: static_cast<retro_log_callback*>(data)->log = log_message; return true;
  case RETRO_ENVIRONMENT_GET_CORE_OPTIONS_VERSION: *static_cast<unsigned*>(data) = CoreOptionsVersion; return true;
  case RETRO_ENVIRONMENT_SET_CORE_OPTIONS_V2: {
    auto* defs = static_cast<retro_core_options_v2*>(data)->definitions;
    for (auto* d = defs; d && d->key; ++d)
      h.options.try_emplace(d->key, d->default_value ? d->default_value : d->values[0].value);
    return true;
  }
  case RETRO_ENVIRONMENT_GET_VARIABLE: {
    auto* variable = static_cast<retro_variable*>(data);
    auto found = h.options.find(variable->key);
    variable->value = found == h.options.end() ? nullptr : found->second.c_str();
    return variable->value != nullptr;
  }
  case RETRO_ENVIRONMENT_GET_VARIABLE_UPDATE: *static_cast<bool*>(data) = false; return true;
  case RETRO_ENVIRONMENT_SET_PIXEL_FORMAT: return *static_cast<retro_pixel_format*>(data) == RETRO_PIXEL_FORMAT_XRGB8888;
  case RETRO_ENVIRONMENT_GET_PREFERRED_HW_RENDER: *static_cast<retro_hw_context_type*>(data) = RETRO_HW_CONTEXT_D3D11; return true;
  case RETRO_ENVIRONMENT_SET_HW_RENDER: {
    auto* hw = static_cast<retro_hw_render_callback*>(data);
    if (hw->context_type != RETRO_HW_CONTEXT_D3D11) return false;
    h.hw = *hw; return true;
  }
  case RETRO_ENVIRONMENT_GET_HW_RENDER_INTERFACE: *static_cast<const retro_hw_render_interface**>(data) = reinterpret_cast<retro_hw_render_interface*>(&h.d3d); return true;
  case RETRO_ENVIRONMENT_SET_SYSTEM_AV_INFO: h.av = *static_cast<retro_system_av_info*>(data); return true;
  case RETRO_ENVIRONMENT_SET_GEOMETRY: h.av.geometry = *static_cast<retro_game_geometry*>(data); return true;
  case RETRO_ENVIRONMENT_GET_TARGET_SAMPLE_RATE: *static_cast<unsigned*>(data) = DefaultSampleRate; return true;
  case RETRO_ENVIRONMENT_GET_LANGUAGE: *static_cast<unsigned*>(data) = RETRO_LANGUAGE_ENGLISH; return true;
  case RETRO_ENVIRONMENT_GET_INPUT_BITMASKS: return true;
  case RETRO_ENVIRONMENT_GET_CAN_DUPE: *static_cast<bool*>(data) = true; return true;
  case RETRO_ENVIRONMENT_GET_FASTFORWARDING: *static_cast<bool*>(data) = false; return true;
  case RETRO_ENVIRONMENT_GET_AUDIO_VIDEO_ENABLE: *static_cast<int*>(data) = AudioVideoEnabled; return true;
  case RETRO_ENVIRONMENT_SET_INPUT_DESCRIPTORS:
  case RETRO_ENVIRONMENT_SET_CONTROLLER_INFO:
  case RETRO_ENVIRONMENT_SET_MEMORY_MAPS:
  case RETRO_ENVIRONMENT_SET_SUPPORT_ACHIEVEMENTS:
  case RETRO_ENVIRONMENT_SET_CORE_OPTIONS_DISPLAY:
  case RETRO_ENVIRONMENT_SET_CORE_OPTIONS_UPDATE_DISPLAY_CALLBACK:
  case RETRO_ENVIRONMENT_SET_SUBSYSTEM_INFO:
  case RETRO_ENVIRONMENT_SET_SERIALIZATION_QUIRKS: return true;
  default: return false;
  }
}
bool RETRO_CALLCONV environment(unsigned command, void* data) {
  try { return environment_impl(command, data); }
  catch (const std::exception& e) { if (active) active->error = e.what(); return false; }
  catch (...) { if (active) active->error = "Native environment callback failed."; return false; }
}
void RETRO_CALLCONV video(const void* data, unsigned width, unsigned height, size_t pitch) {
  if (!active || !data) return;
  auto& h = *active;
  try {
    if (width == 0 || height == 0 || width > MaximumDimension || height > MaximumDimension)
      throw std::runtime_error("Core returned invalid video dimensions.");
    const auto row_size = static_cast<size_t>(width) * BytesPerPixel;
    std::vector<uint8_t> frame(row_size * height);
    if (data == RETRO_HW_FRAME_BUFFER_VALID) {
      ComPtr<ID3D11ShaderResourceView> view;
      h.context->PSGetShaderResources(0, 1, view.GetAddressOf());
      if (!view) throw std::runtime_error("Core did not provide the D3D11 frame texture.");
      ComPtr<ID3D11Resource> resource; view->GetResource(&resource);
      ComPtr<ID3D11Texture2D> texture; check_hr(resource.As(&texture), "Query frame texture");
      D3D11_TEXTURE2D_DESC desc{}; texture->GetDesc(&desc);
      if (desc.Width != width || desc.Height != height ||
          (desc.Format != DXGI_FORMAT_R8G8B8A8_UNORM && desc.Format != DXGI_FORMAT_R8G8B8A8_TYPELESS))
        throw std::runtime_error("Unexpected D3D11 frame texture: " + std::to_string(desc.Width) + "x" +
          std::to_string(desc.Height) + " format " + std::to_string(desc.Format));
      if (!h.staging || h.width != width || h.height != height) {
        h.staging.Reset(); desc.Usage = D3D11_USAGE_STAGING; desc.BindFlags = 0;
        desc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
        desc.CPUAccessFlags = D3D11_CPU_ACCESS_READ; desc.MiscFlags = 0;
        check_hr(h.device->CreateTexture2D(&desc, nullptr, &h.staging), "Create frame readback");
      }
      h.context->CopyResource(h.staging.Get(), texture.Get());
      D3D11_MAPPED_SUBRESOURCE mapped{};
      check_hr(h.context->Map(h.staging.Get(), 0, D3D11_MAP_READ, 0, &mapped), "Read frame");
      for (unsigned y = 0; y < height; ++y)
        memcpy(frame.data() + y * row_size, static_cast<const uint8_t*>(mapped.pData) + y * mapped.RowPitch, row_size);
      h.context->Unmap(h.staging.Get(), 0);
    } else {
      if (pitch < row_size) throw std::runtime_error("Core returned an invalid video row pitch.");
      // Libretro XRGB8888 is BGRA in little-endian host memory.
      for (unsigned y = 0; y < height; ++y) {
        const auto* source = static_cast<const uint8_t*>(data) + y * pitch;
        auto* destination = frame.data() + y * row_size;
        for (unsigned x = 0; x < width; ++x) {
          destination[x * 4] = source[x * 4 + 2]; destination[x * 4 + 1] = source[x * 4 + 1];
          destination[x * 4 + 2] = source[x * 4]; destination[x * 4 + 3] = OpaqueAlpha;
        }
      }
    }
    h.pixels.swap(frame); h.width = width; h.height = height; ++h.sequence;
  } catch (const std::exception& e) { h.error = e.what(); }
  catch (...) { h.error = "Native video callback failed."; }
}
size_t RETRO_CALLCONV audio_batch(const int16_t* samples, size_t frames) {
  if (!active || !samples || !frames) return frames;
  try {
  std::lock_guard lock(active->audio_mutex);
  auto& buffer = active->samples;
  const size_t count = std::min(frames, MaximumAudioSamples / StereoChannels) * StereoChannels;
  if (buffer.size() + count > MaximumAudioSamples) buffer.clear();
  buffer.insert(buffer.end(), samples, samples + count);
  return frames;
  } catch (const std::exception& e) { active->error = e.what(); return 0; }
  catch (...) { active->error = "Native audio callback failed."; return 0; }
}
void RETRO_CALLCONV audio_sample(int16_t left, int16_t right) { int16_t pair[]{left, right}; audio_batch(pair, 1); }
void RETRO_CALLCONV input_poll() {}
int16_t RETRO_CALLCONV input_state(unsigned, unsigned, unsigned, unsigned) { return 0; }
template<class F> int guarded(tas_host* h, F action) {
  if (!h) return 0;
  try { h->check_owner(); h->error.clear(); action(); return h->error.empty() ? 1 : 0; }
  catch (const std::exception& e) { h->error = e.what(); return 0; }
  catch (...) { h->error = "Unknown native backend error."; return 0; }
}
}

tas_host* tas_create(const char* core, const char* system, const char* saves, int resolution, int dsp_hle) {
  try {
  std::lock_guard lock(instance_mutex);
  if (active) { creation_error = "Only one Dolphin host may be active per process."; return nullptr; }
  auto* h = new tas_host(); active = h;
  if (!guarded(h, [&] {
    if (!core || !system || !saves || !*core || !*system || !*saves)
      throw std::runtime_error("Core, system and save paths are required.");
    if (resolution < 1 || resolution > MaximumResolutionScale)
      throw std::runtime_error("Internal resolution scale must be between 1 and 6.");
    h->system_dir = system; h->save_dir = saves;
    std::filesystem::create_directories(utf8_path(saves));
    h->log.open(utf8_path(saves) / "host.log", std::ios::app);
    h->library = LoadLibraryW(utf8_path(core).c_str());
    if (!h->library) throw std::runtime_error("Unable to load Dolphin core; Windows error " + std::to_string(GetLastError()));
#define BIND(name) h->bind(h->name, "retro_" #name)
    BIND(set_environment); BIND(set_video_refresh); BIND(set_audio_sample); BIND(set_audio_sample_batch);
    BIND(set_input_poll); BIND(set_input_state); BIND(init); BIND(deinit); BIND(load_game); BIND(unload_game);
    BIND(run); BIND(reset); BIND(serialize_size); BIND(serialize); BIND(unserialize); BIND(get_system_av_info);
    BIND(get_memory_data); BIND(get_memory_size); BIND(set_controller_port_device);
#undef BIND
    unsigned (*abi)() = nullptr; h->bind(abi, "dolphin_tas_get_abi_version");
    if (abi() != TasAbiVersion) throw std::runtime_error("Dolphin TAS extension ABI mismatch.");
    h->bind(h->set_pad, "dolphin_tas_set_pad"); h->bind(h->fields, "dolphin_tas_get_field_count");
    h->bind(h->ticks, "dolphin_tas_get_ticks");
    h->bind(h->presentations, "dolphin_tas_get_presentation_count");
    h->bind(h->begin_polls, "dolphin_tas_begin_polls");
    h->bind(h->end_polls, "dolphin_tas_end_polls");
    h->bind(h->copy_polls, "dolphin_tas_copy_polls");
    // Optional for existing experiment runtimes; required when playback is enabled.
    h->audio_playback = reinterpret_cast<decltype(h->audio_playback)>(GetProcAddress(h->library, "dolphin_tas_audio_playback"));
    h->mix_audio = reinterpret_cast<decltype(h->mix_audio)>(GetProcAddress(h->library, "dolphin_tas_mix_audio"));
    check_hr(D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, D3D11_CREATE_DEVICE_BGRA_SUPPORT,
      nullptr, 0, D3D11_SDK_VERSION, &h->device, &h->d3d.featureLevel, &h->context), "Create D3D11 device");
    if (h->d3d.featureLevel < D3D_FEATURE_LEVEL_11_0)
      throw std::runtime_error("Dolphin requires Direct3D feature level 11.0 or newer.");
    h->d3d.interface_type = RETRO_HW_RENDER_INTERFACE_D3D11;
    h->d3d.interface_version = RETRO_HW_RENDER_INTERFACE_D3D11_VERSION;
    h->d3d.handle = h; h->d3d.device = h->device.Get(); h->d3d.context = h->context.Get(); h->d3d.D3DCompile = D3DCompile;
    h->options = {{CoreThreadOption, OptionDisabled}, {CoreRendererOption, "Hardware"},
      {CoreResolutionOption, std::to_string(resolution)}, {CoreDspOption, dsp_hle ? OptionEnabled : OptionDisabled},
      // Wall-clock-driven notifications (e.g. memory-card writes) must not enter capture pixels.
      // This is a base option; Dolphin's game compatibility layers still take precedence.
      {CoreAudioOption, "0"}, {CoreOsdOption, OptionDisabled}};
    h->set_environment(environment); h->set_video_refresh(video); h->set_audio_sample(audio_sample);
    h->set_audio_sample_batch(audio_batch); h->set_input_poll(input_poll); h->set_input_state(input_state);
    h->init(); h->initialized = true;
  })) {
    creation_error = h->error;
    if (h->library) FreeLibrary(h->library);
    active = nullptr; delete h; return nullptr;
  }
  return h;
  } catch (const std::exception& e) { creation_error = e.what(); return nullptr; }
  catch (...) { creation_error = "Unknown native host creation error."; return nullptr; }
}
const char* tas_error(tas_host* h) { return h ? h->error.c_str() : creation_error.c_str(); }
int tas_set_option(tas_host* h, const char* key, const char* value) { return guarded(h, [&] {
  if (h->load_attempted) throw std::runtime_error("Options must be set before boot.");
  if (!key || !value || std::string_view(key).substr(0, 8) != "dolphin_") throw std::runtime_error("Invalid option.");
  if (std::string_view(key) == CoreThreadOption && std::string_view(value) != OptionDisabled)
    throw std::runtime_error("Single-core execution is required.");
  h->options[key] = value;
}); }
void tas_destroy(tas_host* h) {
  if (!h) return;
  // Refuse to unload a live DLL from another thread even if a caller violates the ABI.
  if (h->owner != GetCurrentThreadId()) {
    h->error = "Destroy the backend on its execution thread.";
    return;
  }
  std::lock_guard lock(instance_mutex);
  if (!guarded(h, [&] {
    // This core's unload checks IsDestroyed() before shutting down the graphics backend.
    // Destroy the context first to avoid duplicate graphics teardown and leaked D3D globals.
    if (h->context_active && h->hw.context_destroy) {
      h->hw.context_destroy(); h->context_active = false;
    }
    if (h->load_attempted) { h->unload_game(); h->load_attempted = false; h->loaded = false; }
    if (h->initialized) { h->deinit(); h->initialized = false; }
  })) return; // Preserve the live module if shutdown failed; do not free executable code in use.
  if (h->library) FreeLibrary(h->library);
  active = nullptr; delete h;
}
int tas_load(tas_host* h, const char* game) {
  return guarded(h, [&] {
    if (h->loaded) throw std::runtime_error("Stop the current game before loading another.");
    if (h->load_attempted) throw std::runtime_error("Recreate the backend after a failed game load.");
    if (!game || !*game || !std::filesystem::is_regular_file(utf8_path(game)))
      throw std::runtime_error("The selected game file does not exist.");
    retro_game_info info{game, nullptr, 0, nullptr};
    h->load_attempted = true;
    if (!h->load_game(&info)) throw std::runtime_error("Dolphin could not boot the game. See host.log.");
    h->loaded = true;
    if (h->hw.context_type != RETRO_HW_CONTEXT_D3D11 || !h->hw.context_reset)
      throw std::runtime_error("Dolphin did not negotiate D3D11 rendering.");
    h->context_active = true; h->hw.context_reset();
    for (unsigned port = 0; port < MaximumControllerPorts; ++port)
      h->set_controller_port_device(port, port == 0 ? RETRO_DEVICE_JOYPAD : RETRO_DEVICE_NONE);
    tas_pad neutral{0, StickCenter, StickCenter, StickCenter, StickCenter, 0, 0};
    // Boot requires the first retro_run. This becomes the application's initial boundary.
    h->run();
    if (!h->set_pad(0, &neutral)) throw std::runtime_error("Exact input injection failed. Single-core execution is required.");
    h->get_system_av_info(&h->av);
  });
}
int tas_step(tas_host* h, const tas_pad* pad) { return guarded(h, [&] {
  h->require_loaded(); if (!pad || !h->set_pad(0, pad)) throw std::runtime_error("Invalid controller state.");
  if (!h->begin_polls(nullptr, 0, false)) throw std::runtime_error("Cannot begin poll recording.");
  h->run();
  if (!h->end_polls()) throw std::runtime_error("Controller poll recording overflow.");
  // The core can settle its VI rate after boot while announcing geometry only.
  h->get_system_av_info(&h->av);
}); }
int tas_replay_step(tas_host* h, const tas_pad* fallback, const tas_poll* polls, size_t count) { return guarded(h, [&] {
  h->require_loaded();
  if (!fallback || !h->set_pad(0, fallback) || !h->begin_polls(polls, count, true))
    throw std::runtime_error("Invalid poll playback input.");
  h->run();
  if (!h->end_polls()) throw std::runtime_error("Playback desync: controller poll count, port, or timing changed.");
  h->get_system_av_info(&h->av);
}); }
size_t tas_polls(tas_host* h, tas_poll* polls, size_t capacity) {
  size_t result = 0; guarded(h, [&] { h->require_loaded(); result = h->copy_polls(polls, capacity); }); return result;
}
int tas_reset(tas_host* h) { return guarded(h, [&] { h->require_loaded(); std::lock_guard lock(h->audio_mutex); h->reset(); }); }
uint64_t tas_fields(tas_host* h) { uint64_t result = 0; guarded(h, [&] { h->require_loaded(); result = h->fields(); }); return result; }
uint64_t tas_presentations(tas_host* h) { uint64_t result = 0; guarded(h, [&] { h->require_loaded(); result = h->presentations(); }); return result; }
uint64_t tas_ticks(tas_host* h) { uint64_t result = 0; guarded(h, [&] { h->require_loaded(); result = h->ticks(); }); return result; }
double tas_fps(tas_host* h) { double result = 0; guarded(h, [&] { result = h->av.timing.fps; }); return result; }
size_t tas_state_size(tas_host* h) { size_t size = 0; guarded(h, [&] { h->require_loaded(); std::lock_guard lock(h->audio_mutex); size = h->serialize_size(); }); return size; }
int tas_save(tas_host* h, void* bytes, size_t size) { return guarded(h, [&] {
  std::lock_guard lock(h->audio_mutex);
  h->require_loaded(); if (!bytes || size < h->serialize_size() || !h->serialize(bytes, size)) throw std::runtime_error("Savestate serialization failed.");
}); }
int tas_restore(tas_host* h, const void* bytes, size_t size) { return guarded(h, [&] {
  std::lock_guard lock(h->audio_mutex);
  h->require_loaded();
  if (!bytes || !size || size > MaximumStateBytes) throw std::runtime_error("Invalid savestate buffer size.");
  const auto rollback_size = h->serialize_size();
  if (!rollback_size || rollback_size > MaximumStateBytes) throw std::runtime_error("Invalid rollback state size.");
  std::vector<uint8_t> rollback(rollback_size);
  if (!h->serialize(rollback.data(), rollback.size())) throw std::runtime_error("Unable to capture rollback state.");
  const auto recover = [&] {
    h->faulted = true;
    try { h->faulted = !h->unserialize(rollback.data(), rollback.size()); }
    catch (...) { /* Keep the backend faulted if native recovery also throws. */ }
    h->samples.clear();
  };
  bool restored = false;
  try { restored = h->unserialize(bytes, size); }
  catch (...) {
    recover();
    throw;
  }
  if (!restored) {
    recover();
    throw std::runtime_error(h->faulted ? "Savestate restoration and recovery failed; reopen the game." :
      "Savestate restoration failed; the previous state was recovered.");
  }
  h->samples.clear();
}); }
int tas_memory(tas_host* h, uint32_t address, void* bytes, size_t size, int write) { return guarded(h, [&] {
  h->require_loaded();
  const size_t ram_size = h->get_memory_size(RETRO_MEMORY_SYSTEM_RAM);
  const uint64_t offset = address >= Mem1Base ? uint64_t(address) - Mem1Base : UINT64_MAX;
  if ((!bytes && size) || offset > ram_size || size > ram_size - offset)
    throw std::runtime_error("Address outside supported MEM1 RAM range.");
  if (!size) return;
  auto* ram = static_cast<uint8_t*>(h->get_memory_data(RETRO_MEMORY_SYSTEM_RAM));
  if (!ram) throw std::runtime_error("RAM unavailable.");
  if (write) memcpy(ram + (address - Mem1Base), bytes, size); else memcpy(bytes, ram + (address - Mem1Base), size);
}); }
size_t tas_video(tas_host* h, void* bytes, size_t capacity, unsigned* width, unsigned* height, uint64_t* sequence) {
  size_t result = 0;
  guarded(h, [&] {
    if (!width || !height || !sequence) throw std::runtime_error("Video metadata output pointers are required.");
    *width = h->width; *height = h->height; *sequence = h->sequence;
    if (bytes && !h->pixels.empty() && capacity >= h->pixels.size()) memcpy(bytes, h->pixels.data(), h->pixels.size());
    result = h->pixels.size();
  });
  return result;
}
size_t tas_audio(tas_host* h, int16_t* samples, size_t capacity, unsigned* rate) {
  size_t count = 0;
  guarded(h, [&] {
    if (!rate) throw std::runtime_error("Audio sample rate output pointer is required.");
    std::lock_guard lock(h->audio_mutex);
    *rate = static_cast<unsigned>(h->av.timing.sample_rate);
    count = std::min(capacity, h->samples.size());
    count -= count % StereoChannels;
    if (samples && count) { memcpy(samples, h->samples.data(), count * sizeof(int16_t)); h->samples.erase(h->samples.begin(), h->samples.begin() + count); }
  });
  return count;
}

unsigned tas_audio_playback(tas_host* h, int enabled) {
  unsigned rate = 0;
  guarded(h, [&] {
    h->require_loaded();
    std::lock_guard lock(h->audio_mutex);
    if (!h->audio_playback || !h->mix_audio)
      throw std::runtime_error("Rebuild Dolphin to enable device-driven audio playback.");
    rate = h->audio_playback(enabled != 0);
    if (!rate) throw std::runtime_error("Dolphin audio mixer is unavailable.");
    h->pull_audio = enabled != 0;
    h->samples.clear();
  });
  return rate;
}

size_t tas_mix_audio(tas_host* h, int16_t* samples, size_t frames) {
  if (!h || !samples || frames > MaximumAudioSamples / StereoChannels) return 0;
  // No guarded(): audio consumption is intentionally independent of the owner.
  // Do not write the owner's error string from the audio thread.
  try {
    std::lock_guard lock(h->audio_mutex);
    return h->pull_audio ? h->mix_audio(samples, frames) : 0;
  } catch (...) { return 0; }
}
