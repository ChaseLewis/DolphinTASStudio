using System.Buffers.Binary;
using TasStudio.Core;
using TasStudio.Emulation;
using Xunit;

namespace TasStudio.Core.Tests;

public sealed class MemoryWatchTests
{
    [Fact]
    public void PointerChainReadsEachBigEndianHopAndSignedOffset()
    {
        var calls = new List<uint>();
        byte[] Read(uint address, int count)
        {
            calls.Add(address);
            var bytes = new byte[count];
            BinaryPrimitives.WriteUInt32BigEndian(bytes, address switch { 0x80000000 => 0x80000020, 0x80000024 => 0x80000050, 0x8000004C => 0xFFFFFFFE, _ => throw new InvalidOperationException() });
            return bytes;
        }
        var watch = new WatchDefinition(0x80000000, WatchType.S32, Offsets: [4, -4]);
        var result = WatchMemory.Read(Guid.NewGuid(), watch, Read);
        Assert.Null(result.Error); Assert.Equal(0x8000004Cu, result.Address);
        Assert.Equal(new uint[] { 0x80000000, 0x80000024, 0x8000004C }, calls);
        Assert.Equal("-2", WatchMemory.Format(watch, result.Bytes)); Assert.Equal(2, result.Hops.Length);
    }
    [Theory]
    [InlineData(0u)]
    [InlineData(0xFFFFFFFFu)]
    [InlineData(0x81800000u)]
    public void BadPointerStopsAtFailedLevelWithoutReadingValue(uint pointer)
    {
        var calls = 0;
        var result = WatchMemory.Read(Guid.NewGuid(), new(0x80000000, Offsets: [4]), (_, _) =>
        { calls++; var b = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, pointer); return b; });
        Assert.Contains("Pointer level 1", result.Error); Assert.Empty(result.Bytes); Assert.Null(result.Address); Assert.Equal(1, calls);
    }
    [Fact]
    public void InvalidFinalWidthIsRejectedBeforeBackendRead()
    {
        var result = WatchMemory.Read(Guid.NewGuid(), new(0x817FFFFF, WatchType.U32), (_, _) => throw new Exception("Must not read"));
        Assert.Contains("outside", result.Error);
    }
    [Fact]
    public void FormatsWithoutLosingIntegerPrecisionAndUsesTypeDefaults()
    {
        Assert.Equal("18446744073709551615", WatchMemory.Format(new(0, WatchType.U64), [255,255,255,255,255,255,255,255]));
        Assert.Equal("-1", WatchMemory.Format(new(0, WatchType.S64), [255,255,255,255,255,255,255,255]));
        Assert.Equal("1", WatchMemory.Format(new(0, WatchType.Float32), [0x3F,0x80,0,0]));
        Assert.Equal("01 AF", WatchMemory.Format(new(0, WatchType.Bytes, 2), [1,0xAF]));
        Assert.Equal("hi", WatchMemory.Format(new(0, WatchType.Text, 4), [(byte)'h',(byte)'i',0,255]));
        Assert.Contains("invalid UTF-8", WatchMemory.Format(new(0, WatchType.Text, 1), [255]));
    }
    [Fact]
    public void ImportPreservesGroupsUnsupportedDefinitionsAndOriginalSource()
    {
        const string json = """
        {"extra":"retained","watchList":[{"groupName":"Inventory","groupEntries":[
          {"label":"Key Items","address":"8030C048","typeIndex":6,"length":12,"baseIndex":1},
          {"label":"Pointer","address":"80309710","typeIndex":2,"pointerOffsets":["88","-14"]},
          {"label":"Future type","address":"80000000","typeIndex":999,"unknown":"kept"},
          {"label":"Good sibling","address":"80000004","typeIndex":0,"unsigned":true}]}]}
        """;
        var imported = DmwImporter.Import(json, "example.dmw");
        Assert.Single(imported.Nodes); Assert.Equal(4, imported.Nodes[0].Children!.Length);
        Assert.Equal(new[] { 0x88, -0x14 }, imported.Nodes[0].Children![1].Watch!.Offsets);
        Assert.NotNull(imported.Nodes[0].Children![2].Diagnostic);
        Assert.Equal(WatchType.U8, imported.Nodes[0].Children![3].Watch!.Type);
        Assert.Equal(json, Assert.Single(imported.Imports!).OriginalJson);
        Assert.Equal(imported.ToJson(), WatchDocument.Parse(imported.ToJson()).ToJson());
    }
    [Fact]
    public void LocalDmwFixtureWhenProvidedHasExpectedCounts()
    {
        var fixture = Environment.GetEnvironmentVariable("TASSTUDIO_DMW_FIXTURE");
        if (string.IsNullOrWhiteSpace(fixture)) return;
        var imported = DmwImporter.Import(File.ReadAllText(fixture), fixture);
        var nodes = WatchDocument.Walk(imported.Nodes).ToArray();
        Assert.Equal(590, nodes.Count(n => !n.IsGroup)); Assert.Equal(46, nodes.Count(n => n.IsGroup));
        Assert.Equal(257, nodes.Count(n => n.Watch?.Offsets is { Length: > 0 }));
        Assert.DoesNotContain(nodes, n => n.Diagnostic != null);
    }
    [Fact]
    public void DefinitionEditsPreserveStableIdentityAcrossSaveAndReload()
    {
        using var files = new TestWorkspace();
        var path = files.FilePath("movie.tasproj.watches.json");
        var entry = WatchNode.Entry("Value", new(0x80000000));
        var doc = WatchDocument.Empty.Add(entry, null); doc.Save(path);
        doc.Replace(entry.Id, entry with { Name = "Renamed" }).Save(path);
        Assert.Equal("Renamed", WatchDocument.Load(path).Nodes[0].Name);
        Assert.Equal(entry.Id, WatchDocument.Load(path).Nodes[0].Id);
        Assert.Equal(entry.Watch, WatchDocument.Load(path).Nodes[0].Watch);
    }
    [Fact]
    public async Task BatchUsesOneExecutionBoundaryAndOlderGenerationsBecomeStale()
    {
        using var files = new TestWorkspace(); var backend = new FakeBackend(); using var service = new ExecutionService(backend);
        await service.LoadGameAsync(files.GamePath, files.Options); await service.NewProjectAsync();
        await service.SaveNamedStateAsync("Watch baseline");
        var revision = service.Revision;
        var sample = await service.SampleWatchesAsync(Enumerable.Range(0, 590).Select(i => (Guid.NewGuid(), new WatchDefinition(0x80000000 + (uint)i * 4))));
        Assert.Equal(590, sample.Values.Length); Assert.Equal(0uL, sample.Position); Assert.Equal(sample.Generation, service.SampleGeneration);
        Assert.All(sample.Values, value => Assert.Null(value.Error));
        Assert.Equal(revision, service.Revision); Assert.All(service.StateMarkers, marker => Assert.True(marker.Valid));
        Assert.DoesNotContain(backend.Calls, c => c.Operation == nameof(FakeBackend.WriteMemory));
        Assert.Single(backend.Calls.Where(c => c.Operation == nameof(FakeBackend.ReadMemory)).Select(c => c.Thread).Distinct());
        await service.StepAsync(); Assert.NotEqual(sample.Generation, service.SampleGeneration);
        Assert.Equal(1uL, service.Position);
    }
}
