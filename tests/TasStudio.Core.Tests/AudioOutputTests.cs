using NAudio.Wave;
using TasStudio.App;
using TasStudio.Emulation;
using Xunit;

namespace TasStudio.Core.Tests;

public sealed class AudioOutputTests
{
    [Fact]
    public void DevicePullsFreshAudioWithoutAnyPresentationPacket()
    {
        var device = new TestPlayer();
        using var output = new AudioOutput(() => device);
        var source = new TestSource();
        output.SetSource(source);
        Assert.Equal(PlaybackState.Playing, device.PlaybackState);
        var bytes = new byte[32];
        device.Provider!.Read(bytes, 4, 24);
        Assert.Equal(1, source.Reads);
        Assert.Equal((short)1200, BitConverter.ToInt16(bytes, 4));
        source.Value = 2300;
        device.Provider.Read(bytes, 0, bytes.Length);
        Assert.Equal((short)2300, BitConverter.ToInt16(bytes));
        Assert.Equal(2, source.Reads);
    }

    [Fact]
    public void RetiredDeviceProviderCannotReadOldOrReplacementSource()
    {
        var device = new TestPlayer();
        using var output = new AudioOutput(() => device);
        var oldSource = new TestSource();
        output.SetSource(oldSource);
        var oldProvider = device.Provider!;
        output.SetSource(null);
        var newSource = new TestSource();
        output.SetSource(newSource);
        var bytes = Enumerable.Repeat((byte)255, 32).ToArray();
        oldProvider.Read(bytes, 0, bytes.Length);
        Assert.All(bytes, value => Assert.Equal(0, value));
        Assert.Equal(0, oldSource.Reads);
        Assert.Equal(0, newSource.Reads);
        device.Provider!.Read(bytes, 0, bytes.Length);
        Assert.Equal(1, newSource.Reads);
    }

    private sealed class TestSource : IAudioSource
    {
        public int SampleRate => 32029;
        public int Reads { get; private set; }
        public short Value { get; set; } = 1200;
        public void Read(short[] samples, int count) { Reads++; Array.Fill(samples, Value, 0, count); }
    }

    [Theory]
    [InlineData(32029, 0.8991851134908989)]
    [InlineData(32029, 1.2348808891941678)]
    [InlineData(48000, 1.25)]
    public void LoadingScreenBurstReachesDeviceWithoutDroppingSamples(int rate, double seconds)
    {
        var device = new TestPlayer();
        using var output = new AudioOutput(() => device);
        var samples = Enumerable.Range(0, (int)(rate * seconds) * 2).Select(i => (short)(i % 30000 + 1)).ToArray();
        output.Add(samples, rate);
        Assert.Equal(PlaybackState.Playing, device.PlaybackState);
        var bytes = new byte[samples.Length * sizeof(short)];
        Assert.Equal(bytes.Length, device.Provider!.Read(bytes, 0, bytes.Length));
        var actual = new short[samples.Length]; Buffer.BlockCopy(bytes, 0, actual, 0, bytes.Length);
        Assert.Equal(samples, actual);
    }

    [Fact]
    public void DeviceStartsOnlyAfterItsInitialBuffersCanBeFilled()
    {
        var device = new TestPlayer();
        using var output = new AudioOutput(() => device);
        var packet = Enumerable.Repeat((short)1200, 48000 * 2 / 50).ToArray(); // 20 ms stereo
        for (var i = 0; i < 3; i++) output.Add(packet, 48000);
        Assert.Equal(PlaybackState.Stopped, device.PlaybackState);
        output.Add(packet, 48000);
        Assert.Equal(PlaybackState.Playing, device.PlaybackState);
        var bytes = new byte[packet.Length * sizeof(short) * 4];
        device.Provider!.Read(bytes, 0, bytes.Length);
        var actual = new short[packet.Length * 4]; Buffer.BlockCopy(bytes, 0, actual, 0, bytes.Length);
        Assert.All(actual, sample => Assert.Equal((short)1200, sample));
    }

    [Fact]
    public void FlushStopsQueuedDeviceAudioAndRequiresFreshPrebuffer()
    {
        var device = new TestPlayer();
        using var output = new AudioOutput(() => device);
        output.Add(Enumerable.Repeat((short)9000, 9600).ToArray(), 48000);
        Assert.Equal(PlaybackState.Playing, device.PlaybackState);
        output.Flush();
        Assert.Equal(PlaybackState.Stopped, device.PlaybackState);
        var empty = new byte[160]; device.Provider!.Read(empty, 0, empty.Length);
        Assert.All(empty, value => Assert.Equal(0, value));
        output.Add(new short[1920], 48000);
        Assert.Equal(PlaybackState.Stopped, device.PlaybackState);
        output.Add(new short[5760], 48000);
        Assert.Equal(PlaybackState.Playing, device.PlaybackState);
    }

    private sealed class TestPlayer : IWavePlayer
    {
        public IWaveProvider? Provider { get; private set; }
        public float Volume { get; set; }
        public PlaybackState PlaybackState { get; private set; }
        public WaveFormat OutputWaveFormat => Provider!.WaveFormat;
        public event EventHandler<StoppedEventArgs>? PlaybackStopped { add { } remove { } }
        public void Init(IWaveProvider waveProvider) => Provider = waveProvider;
        public void Play() => PlaybackState = PlaybackState.Playing;
        public void Pause() => PlaybackState = PlaybackState.Paused;
        public void Stop() => PlaybackState = PlaybackState.Stopped;
        public void Dispose() => Stop();
    }
}
