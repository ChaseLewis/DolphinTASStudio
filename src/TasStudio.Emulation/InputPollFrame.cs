using System.Runtime.InteropServices;
using TasStudio.Core;

namespace TasStudio.Emulation;

[StructLayout(LayoutKind.Sequential)]
public readonly record struct InputPoll(ulong TickOffset, ulong FieldOffset, uint Port, ControllerState Input);

/// <summary>One editable presentation group, containing the actual ordered controller polls.</summary>
public sealed record InputPollFrame(ControllerState Input, ulong Fields, ulong Ticks, InputPoll[] Polls)
{
    public InputPollFrame Copy() => this with { Polls = (InputPoll[])Polls.Clone() };
    public void Validate()
    {
        Input.Validate();
        if (Fields == 0 || Ticks == 0 || Polls == null || Polls.Length > 100000)
            throw new InvalidDataException("Invalid input poll group.");
        ulong ticks = 0, fields = 0;
        foreach (var poll in Polls)
        {
            poll.Input.Validate();
            if (poll.Port != 0 || poll.TickOffset < ticks || poll.TickOffset > Ticks ||
                poll.FieldOffset < fields || poll.FieldOffset > Fields)
                throw new InvalidDataException("Invalid controller poll timing or port.");
            ticks = poll.TickOffset; fields = poll.FieldOffset;
        }
    }
}

public sealed record RecordedInputFrame(int Index, string PrefixHash, InputPollFrame Frame);
