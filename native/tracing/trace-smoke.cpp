#include "../TasStudio.LibretroHost/host.h"
#include "../dolphin-libretro/Source/Core/Core/TasTraceBuffer.h"
#include <Windows.h>
#include <array>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <stdexcept>
#include <string>
#include <thread>
#include <vector>

using Core::TasTrace::Record;
using Core::TasTrace::Status;

template<typename Function> Function bind(HMODULE module, const char* name)
{
  auto result = reinterpret_cast<Function>(GetProcAddress(module, name));
  if (!result)
    throw std::runtime_error(std::string("Missing export: ") + name);
  return result;
}

void check(bool value, const char* message)
{
  if (!value)
    throw std::runtime_error(message);
}

std::string utf8(const wchar_t* value)
{
  auto bytes = std::filesystem::path(value).u8string();
  return std::string(bytes.begin(), bytes.end());
}

int wmain(int argc, wchar_t** argv)
{
  std::cout << std::unitbuf;
  if (argc != 7 && argc != 8)
  {
    std::cerr << "trace-smoke <host-dll> <core-dll> <system> <fresh-output> <rom> <groups> [input-intent.bin]\n";
    return 2;
  }
  tas_host* host = nullptr;
  decltype(&tas_destroy) destroy = nullptr;
  HMODULE host_module = nullptr;
  try
  {
    const std::filesystem::path output(argv[4]);
    check(!std::filesystem::exists(output), "Output must be a fresh directory");
    std::filesystem::create_directories(output / "profile/User/Config");
    std::ofstream(output / "profile/User/Config/Dolphin.ini")
        << "[Core]\nCPUThread = False\nEnableCustomRTC = True\nCustomRTCValue = 946684800\nSlotA = 8\nSlotB = 255\n";
    const auto groups = std::stoul(argv[6]);
    check(groups > 0 && groups <= 10000, "Invalid group count");
    std::vector<tas_pad> inputs;
    if (argc == 8)
    {
      static_assert(sizeof(tas_pad) == 8);
      std::ifstream input_file(std::filesystem::path(argv[7]), std::ios::binary | std::ios::ate);
      check(input_file.good(), "Cannot open input intent");
      const auto size = input_file.tellg();
      check(size > 0 && size % sizeof(tas_pad) == 0, "Invalid input intent size");
      inputs.resize(static_cast<size_t>(size) / sizeof(tas_pad));
      input_file.seekg(0);
      input_file.read(reinterpret_cast<char*>(inputs.data()), size);
      check(input_file.good() && inputs.size() >= groups + 120, "Insufficient input intent");
    }
    host_module = LoadLibraryW(argv[1]);
    check(host_module != nullptr, "Cannot load host DLL");
#define LOAD(name) const auto name = bind<decltype(&tas_##name)>(host_module, "tas_" #name)
    LOAD(create); LOAD(error); LOAD(load); LOAD(step); LOAD(fields); LOAD(ticks);
    LOAD(state_size); LOAD(save); LOAD(restore); LOAD(memory);
#undef LOAD
    destroy = bind<decltype(&tas_destroy)>(host_module, "tas_destroy");
    const auto core_path = utf8(argv[2]), system = utf8(argv[3]), rom = utf8(argv[5]);
    const auto profile_bytes = (output / "profile").u8string();
    const std::string profile(profile_bytes.begin(), profile_bytes.end());
    host = create(core_path.c_str(), system.c_str(), profile.c_str(), 1, 1);
    check(host != nullptr, "Cannot create host");
    check(load(host, rom.c_str()) != 0, error(host));
    const auto core = GetModuleHandleW(std::filesystem::path(argv[2]).filename().c_str());
    check(core != nullptr, "Cannot locate loaded core");
    const auto version = bind<uint32_t (*)()>(core, "dolphin_tas_trace_version");
    const auto start = bind<bool (*)(const uint32_t*, size_t, size_t)>(core, "dolphin_tas_trace_start");
    const auto stop = bind<bool (*)()>(core, "dolphin_tas_trace_stop");
    const auto status = bind<bool (*)(Status*)>(core, "dolphin_tas_trace_status");
    const auto drain = bind<size_t (*)(Record*, size_t)>(core, "dolphin_tas_trace_drain");
    check(version() == 1, "Wrong trace ABI");
    Status info{};
    check(status(&info) && !info.active && info.record_size == sizeof(Record), "Capture must default off");
    const uint32_t addresses[] = {0x8025ece0, 0x8025ecc0};
    const uint32_t invalid[] = {0, 0x8025ece1};
    check(!start(invalid, 1, 10) && !start(invalid + 1, 1, 10), "Invalid trace addresses accepted");
    const uint32_t duplicate[] = {addresses[0], addresses[0]};
    check(!start(duplicate, 2, 10), "Duplicate trace points accepted");
    bool wrong_thread = true;
    std::thread([&] { wrong_thread = start(addresses, 2, 10); }).join();
    check(!wrong_thread, "Wrong-thread capture accepted");
    auto advance = [&](unsigned long group) {
      tas_pad input{static_cast<uint16_t>(group % 2 == 0 ? 0x0100 : 0), 128, 128, 128, 128, 0, 0};
      if (!inputs.empty())
        input = inputs.at(group);
      check(step(host, &input) != 0, error(host));
    };
    for (unsigned long group = 0; group < 120; ++group)
      advance(group);
    std::vector<uint8_t> state(state_size(host));
    check(!state.empty() && save(host, state.data(), state.size()), "Cannot save trace-build baseline");
    struct RunResult { std::vector<Record> records; std::vector<uint8_t> ram; uint64_t ticks, fields; };
    auto run = [&](bool enabled, const char* name) {
      check(restore(host, state.data(), state.size()) != 0, error(host));
      if (enabled)
        check(start(addresses, 2, 65536), "Cannot enable tracing");
      RunResult result;
      std::ofstream csv(output / (std::string(name) + ".csv"));
      csv << "group,sequence,ticks,fields,pc,lr,r0,r3,r4\n";
      std::vector<Record> chunk(65536);
      for (unsigned long group = 0; group < groups; ++group)
      {
        advance(group + 120);
        const size_t count = drain(chunk.data(), chunk.size());
        for (size_t index = 0; index < count; ++index)
        {
          const auto& record = chunk[index];
          if (record.pc == addresses[0])
          {
            const uint32_t expected = record.gpr[4] * 1103515245u + 12345u;
            check(record.gpr[0] == expected && record.gpr[3] == ((expected >> 16) & 0x7fff),
                  "Captured RNG registers do not match original arithmetic");
          }
          csv << group << ',' << record.sequence << ',' << record.ticks << ',' << record.fields << ','
              << record.pc << ',' << record.lr << ',' << record.gpr[0] << ',' << record.gpr[3] << ',' << record.gpr[4] << '\n';
          result.records.push_back(record);
        }
      }
      check(status(&info) && info.dropped == 0, "Trace overflow");
      check(stop(), "Cannot disable tracing");
      result.ram.resize(0x1800000);
      check(memory(host, 0x80000000, result.ram.data(), result.ram.size(), 0), "Cannot read MEM1");
      result.ticks = ticks(host);
      result.fields = fields(host);
      std::cout << name << ": " << result.records.size() << " records, ticks=" << result.ticks << " fields=" << result.fields << '\n';
      return result;
    };
    const auto first = run(true, "enabled-first");
    check(!first.records.empty(), "No trace hits; extend the capture window");
    const auto second = run(true, "enabled-repeat");
    check(first.records.size() == second.records.size() &&
          std::memcmp(first.records.data(), second.records.data(), first.records.size() * sizeof(Record)) == 0,
          "Repeated traces differ");
    check(first.ram == second.ram && first.ticks == second.ticks && first.fields == second.fields,
          "Repeated trace terminal state differs");
    const auto disabled = run(false, "disabled");
    check(disabled.records.empty(), "Disabled tracer captured records");
    check(first.ram == disabled.ram && first.ticks == disabled.ticks && first.fields == disabled.fields,
          "Tracing enabled/disabled terminal MEM1 or timing differs");
    check(restore(host, state.data(), state.size()), "Cannot restore before overflow probe");
    check(start(addresses, 2, 1), "Cannot start overflow probe");
    for (unsigned long group = 0; group < groups; ++group)
      advance(group + 120);
    check(status(&info) && info.pending == 1 && info.dropped > 0 && info.observed == info.pending + info.dropped,
          "Overflow accounting failed or probe had fewer than two hits");
    check(restore(host, state.data(), state.size()), "Cannot restore during capture");
    check(status(&info) && !info.active && info.stop_reason == 3, "Restore did not end capture segment");
    check(!start(addresses, 2, 10), "Undrained trace was overwritten");
    Record last{};
    check(drain(&last, 1) == 1, "Cannot drain stopped capture");
    std::ofstream(output / "passed.json") << "{\"status\":\"passed\",\"checks\":\"RNG arithmetic, repeat trace, full MEM1 and timing parity, overflow, owner thread, restore boundary\",\"records\":" << first.records.size() << "}\n";
    destroy(host);
    host = nullptr;
    FreeLibrary(host_module);
    std::cout << "Native trace smoke passed\n";
    return 0;
  }
  catch (const std::exception& exception)
  {
    std::cerr << exception.what() << '\n';
    if (host && destroy)
      destroy(host);
    if (host_module)
      FreeLibrary(host_module);
    return 1;
  }
}
