namespace TasStudio.Emulation;

/// <summary>Optional device-driven audio and internal wall-clock pacing. Mode changes use the execution thread.</summary>
public interface IRealtimeAudioBackend
{
    IAudioSource StartRealtimePlayback();
    void StopRealtimePlayback();
}

/// <summary>One audio-device consumer may read concurrently with emulation. Retired sources return silence.</summary>
public interface IAudioSource
{
    int SampleRate { get; }
    /// <summary>Fill interleaved signed 16-bit stereo samples, including silence on shortages.</summary>
    void Read(short[] samples, int count);
}
