#include "../dolphin-libretro/Source/Core/Core/TasTraceBuffer.h"
#include <iostream>
#include <stdexcept>

void check(bool value)
{
  if (!value)
    throw std::runtime_error("Trace buffer invariant failed");
}

int main()
{
  Core::TasTrace::Buffer buffer;
  Core::TasTrace::Record input{}, output[3]{};
  check(buffer.Drain(output, 3) == 0);
  buffer.Push(input);
  check(buffer.GetStatus(false, 0).dropped == 1);
  buffer.Reset(2);
  for (uint32_t index = 0; index < 3; ++index)
  {
    input.pc = index;
    buffer.Push(input);
  }
  auto status = buffer.GetStatus(true, 0);
  check(status.pending == 2 && status.observed == 3 && status.dropped == 1);
  check(buffer.Drain(nullptr, 0) == 2);
  check(buffer.Drain(output, 0) == 0);
  check(buffer.Drain(output, 1) == 1 && output[0].pc == 0 && output[0].sequence == 0);
  input.pc = 3;
  buffer.Push(input);
  check(buffer.Drain(output, 3) == 2 && output[0].pc == 1 && output[1].pc == 3);
  check(output[1].sequence == 3);
  status = buffer.GetStatus(false, 3);
  check(status.pending == 0 && status.observed == 4 && status.dropped == 1 && status.stop_reason == 3);
  buffer.Reset(1);
  status = buffer.GetStatus(false, 0);
  check(status.pending == 0 && status.observed == 0 && status.dropped == 0 && status.capacity == 1);
  std::cout << "Trace buffer checks passed: bounds, wraparound, sequence gaps, reset, overflow, drain\n";
}
