#include "host.h"
#include <Windows.h>
#include <filesystem>
#include <array>
#include <cstring>
#include <iostream>
#include <stdexcept>
#include <string>
#include <vector>

namespace {
constexpr int RequiredArguments = 5;
constexpr unsigned SmokeFrames = 5;
constexpr uint8_t StickCenter = 128;
constexpr size_t MaximumStateBytes = 512ull * 1024 * 1024;
constexpr unsigned MaximumFieldsToPoll = 600;
constexpr uint16_t LeftTriggerClick = 0x0040;
constexpr uint16_t RightTriggerClick = 0x0020;
constexpr uint16_t AButton = 0x0100;
// TAS state layout prepends the uint64 field counter to Dolphin's console-mode flag.
constexpr size_t StateConsoleModeOffset = sizeof(uint64_t);
template<typename Function> Function bind(HMODULE module, const char* name) {
  auto function = reinterpret_cast<Function>(GetProcAddress(module, name));
  if (!function) throw std::runtime_error(std::string("Missing host export: ") + name);
  return function;
}
std::string utf8(const wchar_t* input) {
  const auto length = WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, input, -1, nullptr, 0, nullptr, nullptr);
  if (!length) throw std::runtime_error("Invalid Unicode argument.");
  std::string value(static_cast<size_t>(length), '\0');
  WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, input, -1, value.data(), length, nullptr, nullptr);
  value.pop_back();
  return value;
}
}

int wmain(int argc, wchar_t** argv) {
  std::cout << std::unitbuf;
  std::cerr << std::unitbuf;
  if (argc != RequiredArguments) {
    std::cerr << "Usage: tas-native-smoke <core-dll> <system-dir> <save-dir> <rom>\n";
    return 2;
  }
  HMODULE module = nullptr;
  tas_host* host = nullptr;
  decltype(&tas_destroy) cleanup = nullptr;
  try {
    const auto host_path = std::filesystem::absolute(argv[0]).parent_path() / L"TasStudio.LibretroHost.dll";
    module = LoadLibraryW(host_path.c_str());
    if (!module) throw std::runtime_error("Cannot load native host DLL: " + std::to_string(GetLastError()));
#define LOAD(name) const auto name = bind<decltype(&tas_##name)>(module, "tas_" #name)
    LOAD(create); LOAD(error); LOAD(load); LOAD(step); LOAD(fields); LOAD(presentations); LOAD(ticks);
    LOAD(state_size); LOAD(save); LOAD(restore); LOAD(destroy); LOAD(fps);
#undef LOAD
    cleanup = destroy;
    const auto core = utf8(argv[1]), system = utf8(argv[2]), saves = utf8(argv[3]), rom = utf8(argv[4]);
    std::cout << "Creating host on native thread " << GetCurrentThreadId() << "\n";
    host = create(core.c_str(), system.c_str(), saves.c_str(), 1, 1);
    if (!host) throw std::runtime_error(error(nullptr));
    std::cout << "Loading game\n";
    if (!load(host, rom.c_str())) throw std::runtime_error(error(host));
    std::cout << "Loaded at field " << fields(host) << ", ticks " << ticks(host) << "\n";
    auto core_module = GetModuleHandleW(std::filesystem::path(argv[1]).filename().c_str());
    if (!core_module) throw std::runtime_error("Loaded core module was not found.");
    const auto poll_count = bind<uint64_t (*)(unsigned)>(core_module, "dolphin_tas_get_pad_poll_count");
    const auto last_polled = bind<bool (*)(unsigned, tas_pad*)>(core_module, "dolphin_tas_get_last_polled_pad");
    const tas_pad neutral{0, StickCenter, StickCenter, StickCenter, StickCenter, 0, 0};
    for (unsigned frame = 0; frame < SmokeFrames; ++frame) {
      const auto before = fields(host);
      const auto presented_before = presentations(host);
      if (!step(host, &neutral)) throw std::runtime_error(error(host));
      if (fields(host) <= before || presentations(host) <= presented_before)
        throw std::runtime_error("Frame advance did not reach a new presentation boundary.");
      std::cout << "Stepped field " << fields(host) << ", ticks " << ticks(host) << "\n";
    }
    const auto saved_fields = fields(host), saved_ticks = ticks(host);
    const auto size = state_size(host);
    if (!size || size > MaximumStateBytes) throw std::runtime_error("Unexpected serialized state size.");
    std::vector<uint8_t> state(size);
    if (!save(host, state.data(), state.size())) throw std::runtime_error(error(host));
    std::cout << "Saved " << state.size() << " bytes\n";
    const std::array<tas_pad, 4> probes{{
      neutral,
      {LeftTriggerClick | AButton, 0, 255, 255, 0, 17, 255},
      {RightTriggerClick, 255, 0, 0, 255, 255, 23},
      {LeftTriggerClick | RightTriggerClick, StickCenter, StickCenter, StickCenter, StickCenter, 0, 0}
    }};
    unsigned probe_index = 0;
    for (const auto& probe : probes) {
      bool observed = false;
      unsigned zero_poll_fields = 0;
      for (unsigned wait = 0; wait < MaximumFieldsToPoll; ++wait) {
        const auto before = fields(host), before_polls = poll_count(0);
        if (!step(host, &probe)) throw std::runtime_error(error(host));
        if (fields(host) <= before) throw std::runtime_error("Input probe did not advance emulation.");
        const auto polls = poll_count(0) - before_polls;
        if (!polls) { ++zero_poll_fields; continue; }
        tas_pad actual{};
        if (!last_polled(0, &actual) || std::memcmp(&actual, &probe, sizeof(probe)) != 0)
          throw std::runtime_error("An actual controller poll did not observe the exact submitted bytes.");
        std::cout << "Exact pad probe " << probe_index << " passed at field " << fields(host)
                  << ": " << polls << " polls in interval, " << zero_poll_fields << " preceding zero-poll fields\n";
        observed = true;
        break;
      }
      if (!observed) throw std::runtime_error("Game did not poll the controller within the smoke limit.");
      ++probe_index;
    }
    std::cout << "Settled refresh rate: " << fps(host) << " Hz\n";
    const auto before_failed_restore_fields = fields(host), before_failed_restore_ticks = ticks(host);
    auto incompatible_state = state;
    incompatible_state.at(StateConsoleModeOffset) = 1; // Wii state marker in the GameCube-only probe.
    if (restore(host, incompatible_state.data(), incompatible_state.size()))
      throw std::runtime_error("Console-incompatible native state was accepted.");
    if (fields(host) != before_failed_restore_fields || ticks(host) != before_failed_restore_ticks)
      throw std::runtime_error("Failed native restoration did not recover its previous boundary.");
    std::cout << "Console-incompatible state rejected; previous field/ticks recovered\n";
    if (!restore(host, state.data(), state.size())) throw std::runtime_error(error(host));
    if (fields(host) != saved_fields || ticks(host) != saved_ticks)
      throw std::runtime_error("Restoration did not recover the captured field/ticks.");
    std::cout << "Restored state; destroying host\n";
    destroy(host);
    host = nullptr;
    std::cout << "Destroyed host successfully\n";
    FreeLibrary(module);
    return 0;
  } catch (const std::exception& exception) {
    std::cerr << "Native smoke failed: " << exception.what() << "\n";
    if (host && cleanup) {
      std::cout << "Destroying failed native session\n";
      cleanup(host);
      std::cout << "Failed session cleanup returned\n";
    }
    if (module) FreeLibrary(module);
    return 1;
  }
}
