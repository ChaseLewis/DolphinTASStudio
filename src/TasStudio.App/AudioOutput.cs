using NAudio.Wave;

namespace TasStudio.App;

public sealed class AudioOutput : IDisposable
{
    private const int ChannelCount = 2;
    private const int SampleBits = 16;
    private const int OutputLatencyMilliseconds = 80;
    private static readonly TimeSpan MaximumBuffer = TimeSpan.FromMilliseconds(250);
    private readonly object _gate = new();
    private WaveOutEvent? _output;
    private BufferedWaveProvider? _buffer;
    private float _volume = 0.7f;
    private bool _disposed;
    public event Action<string>? Failed;
    public void SetVolume(int percent, bool muted)
    {
        lock (_gate)
        {
            _volume = muted ? 0 : Math.Clamp(percent / 100f, 0, 1);
            if (_output != null) _output.Volume = _volume;
        }
    }
    public void Add(short[] samples, int sampleRate)
    {
        if (samples.Length == 0 || sampleRate <= 0) { Flush(); return; }
        lock (_gate)
        {
            if (_disposed) return;
            try
            {
                if (_buffer == null || _buffer.WaveFormat.SampleRate != sampleRate)
                {
                    _output?.Dispose();
                    _buffer = new BufferedWaveProvider(new WaveFormat(sampleRate, SampleBits, ChannelCount)) { BufferDuration = MaximumBuffer, DiscardOnBufferOverflow = true };
                    _output = new WaveOutEvent { DesiredLatency = OutputLatencyMilliseconds, Volume = _volume };
                    _output.Init(_buffer);
                    _output.Play();
                }
                var bytes = new byte[samples.Length * sizeof(short)];
                Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
                _buffer.AddSamples(bytes, 0, bytes.Length);
            }
            catch (Exception ex) when (ex is NAudio.MmException or InvalidOperationException or ArgumentException)
            {
                _disposed = true;
                _output?.Dispose();
                _output = null;
                Failed?.Invoke($"Audio output unavailable: {ex.Message}");
            }
        }
    }
    public void Flush() { lock (_gate) _buffer?.ClearBuffer(); }
    public void Dispose() { lock (_gate) { _disposed = true; _output?.Dispose(); _output = null; } }
}
