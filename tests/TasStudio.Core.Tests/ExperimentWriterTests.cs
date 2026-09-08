using TasStudio.Emulation;
using TasStudio.Sdk;
using Xunit;

namespace TasStudio.Core.Tests;

public sealed class ExperimentWriterTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RunnerDiscoversSchemaWithoutConstructingExperimentAndPersistsCancelledTrials(bool headless)
    {
        using var files = new TestWorkspace();
        var batch = Path.Combine(files.DirectoryPath, "batch");
        RunCancelledBatch(batch, headless);
        Assert.Empty(Directory.GetDirectories(batch, "run-*"));
        using (var reader = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(batch, "results.sqlite")};Pooling=False"))
        {
            reader.Open(); using var query = reader.CreateCommand();
            query.CommandText = "SELECT COUNT(*) FROM trials WHERE status = 'cancelled'";
            Assert.InRange((long)query.ExecuteScalar()!, 1L, 4L);
            query.CommandText = "SELECT COUNT(*) FROM results";
            Assert.Equal(0L, query.ExecuteScalar());
        }
        Assert.InRange(ExperimentFiles.Read<ExperimentResult[]>(Path.Combine(batch, "results.json")).Length, 1, 4);
        Assert.False(File.Exists(Path.Combine(batch, "writer-errors.json")));
        // Collectible load contexts release Windows DLL handles when collected, not at Unload().
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void RunCancelledBatch(string batch, bool headless)
    {
        using var cancellation = new CancellationTokenSource();
        // Cancel after setup but before the first worker starts; no emulator is launched.
        var results = Task.Run(() => ExperimentRunner.RunAsync(Definition() with { Headless = headless }, "unused", batch,
            Environment.ProcessPath!, "unused", "unused", message =>
            {
                if (message.StartsWith("Starting"))
                {
                    var ordinal = int.Parse(message["Starting ".Length..].Split('/')[0]);
                    Assert.Equal(headless, ExperimentFiles.Read<ExperimentJob>(Path.Combine(batch, $"run-{ordinal:00000}", "job.json")).Headless);
                }
                cancellation.Cancel();
            }, cancellation.Token)).GetAwaiter().GetResult();
        Assert.All(results, result => Assert.Equal("cancelled", result.Status));
    }

    [Fact]
    public async Task RunnerRejectsAnAlreadyOwnedBatchDirectory()
    {
        using var files = new TestWorkspace();
        using var owner = new FileStream(Path.Combine(files.DirectoryPath, ".runner.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        await Assert.ThrowsAsync<IOException>(() => ExperimentRunner.RunAsync(Definition(), "unused", files.DirectoryPath,
            Environment.ProcessPath!, "unused", "unused", null, CancellationToken.None));
    }

    private static ExperimentDefinition Definition() => new(1, "Test", ExperimentStart.Boot,
        null, null, 4, 30, Enumerable.Range(0, 16).Select(i => new ExperimentTrial("Trial " + i,
            System.Text.Json.JsonSerializer.SerializeToElement(new { }))).ToArray(),
        AssemblyPath: typeof(TasStudio.Experiment.Fixtures.TypedExperiment).Assembly.Location,
        TypeName: "TasStudio.Experiment.Fixtures.TypedExperiment");

    [Fact]
    public async Task ConcurrentSubmissionsNeverOverlapAndDisposeWaitsForWrites()
    {
        var target = new ObservedWriter();
        var writer = new SerializedExperimentWriter(target);
        await writer.InitializeAsync(new("unused", "Test", 32), CancellationToken.None);
        var writes = Enumerable.Range(0, 32).Select(i => writer.WriteAsync(Result(i))).ToArray();
        var disposal = writer.DisposeAsync().AsTask();
        await Task.WhenAll(writes.Append(disposal));
        Assert.Equal(1, target.MaximumActive);
        Assert.Equal(32, target.Indices.Distinct().Count());
        Assert.True(target.Disposed);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => writer.WriteAsync(Result(33)));
    }

    [Fact]
    public async Task FailedWriteReleasesGateForRemainingResults()
    {
        var target = new ObservedWriter { FailIndex = 0 };
        await using var writer = new SerializedExperimentWriter(target);
        await writer.InitializeAsync(new("unused", "Test", 2), CancellationToken.None);
        var failed = writer.WriteAsync(Result(0));
        var next = writer.WriteAsync(Result(1));
        await Assert.ThrowsAsync<IOException>(() => failed);
        await next;
        Assert.Equal(new[] { 1 }, target.Indices);
        Assert.Equal(1, target.MaximumActive);
    }

    private static ExperimentResult Result(int index) => new("Test", "completed", null,
        DateTimeOffset.UtcNow, 0, 0, 0, 0, null, null, index);

    private sealed class ObservedWriter : IExperimentResultWriter
    {
        private int _active;
        public int MaximumActive;
        public int FailIndex = -1;
        public bool Disposed;
        public List<int> Indices { get; } = [];
        public Task InitializeAsync(ExperimentOutputContext context, CancellationToken token) => Task.CompletedTask;
        public async Task WriteAsync(ExperimentOutputResult result, CancellationToken token)
        {
            var active = Interlocked.Increment(ref _active);
            MaximumActive = Math.Max(MaximumActive, active);
            try
            {
                await Task.Delay(2, token);
                Assert.False(Disposed);
                if (result.Index == FailIndex) throw new IOException("Expected failure");
                Indices.Add(result.Index);
            }
            finally { Interlocked.Decrement(ref _active); }
        }
        public ValueTask DisposeAsync()
        {
            Assert.Equal(0, _active);
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
