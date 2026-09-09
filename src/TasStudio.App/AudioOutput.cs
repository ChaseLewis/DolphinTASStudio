using NAudio.Wave;
using TasStudio.Emulation;

namespace TasStudio.App;

public sealed class AudioOutput : IDisposable
{
    private const int ChannelCount = 2;
    private const int SampleBits = 16;
    private const int OutputLatencyMilliseconds = 80;
    // Fallback packet output (manual advance/other backends) can contain a long
    // loading screen. Realtime Dolphin playback reads the mixer directly.
    private static readonly TimeSpan MaximumBuffer = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan StartupBuffer = TimeSpan.FromMilliseconds(OutputLatencyMilliseconds);
    private readonly object _gate = new();
    private readonly Func<IWavePlayer> _createOutput;
    private IWavePlayer? _output;
    private BufferedWaveProvider? _buffer;
    private PullWaveProvider? _pull;
    private float _volume = 0.7f;
    private bool _disposed;
    public event Action<string>? Failed;
    public AudioOutput() : this(() => new WaveOutEvent { DesiredLatency = OutputLatencyMilliseconds }) { }
    internal AudioOutput(Func<IWavePlayer> createOutput) => _createOutput = createOutput;
    public void SetSource(IAudioSource? source)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _pull?.Retire();
            _output?.Dispose();
            _output = null; _buffer = null; _pull = null;
            if (source == null) return;
            try
            {
                _pull = new PullWaveProvider(source);
                _output = _createOutput();
                _output.Volume = _volume;
                _output.Init(_pull);
                _output.Play();
            }
            catch (Exception ex) when (ex is NAudio.MmException or InvalidOperationException or ArgumentException)
            {
                _pull?.Retire();
                _output?.Dispose(); _output = null;
                _disposed = true;
                Failed?.Invoke($"Audio output unavailable: {ex.Message}");
            }
        }
    }
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
            if (_pull != null) return;
            try
            {
                if (_buffer == null || _buffer.WaveFormat.SampleRate != sampleRate)
                {
                    _output?.Dispose();
                    _buffer = new BufferedWaveProvider(new WaveFormat(sampleRate, SampleBits, ChannelCount)) { BufferDuration = MaximumBuffer, DiscardOnBufferOverflow = true };
                    _output = _createOutput();
                    _output.Volume = _volume;
                    _output.Init(_buffer);
                }
                var bytes = new byte[samples.Length * sizeof(short)];
                Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
                _buffer.AddSamples(bytes, 0, bytes.Length);
                // WaveOut fills its device buffers immediately on Play. Starting
                // with one short packet inserts silence before emulation catches up.
                if (_output!.PlaybackState != PlaybackState.Playing && _buffer.BufferedDuration >= StartupBuffer)
                    _output.Play();
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
    public void Flush()
    {
        lock (_gate)
        {
            // Clearing only the provider leaves already queued device audio playing.
            _pull?.Retire();
            _output?.Stop();
            _buffer?.ClearBuffer();
        }
    }
    public void Dispose() { lock (_gate) { _disposed = true; _pull?.Retire(); _output?.Dispose(); _output = null; } }

    private sealed class PullWaveProvider(IAudioSource source) : IWaveProvider
    {
        private readonly object _gate = new();
        private IAudioSource? _source = source;
        private short[] _samples = [];
        public WaveFormat WaveFormat { get; } = new(source.SampleRate, SampleBits, ChannelCount);
        public void Retire() { lock (_gate) _source = null; }
        public int Read(byte[] buffer, int offset, int count)
        {
            lock (_gate)
            {
                if (_source == null) { Array.Clear(buffer, offset, count); return count; }
                var sampleCount = count / WaveFormat.BlockAlign * ChannelCount;
                if (_samples.Length < sampleCount) _samples = new short[sampleCount];
                _source.Read(_samples, sampleCount);
                Buffer.BlockCopy(_samples, 0, buffer, offset, sampleCount * sizeof(short));
                Array.Clear(buffer, offset + sampleCount * sizeof(short), count - sampleCount * sizeof(short));
                return count;
            }
        }
    }
}
