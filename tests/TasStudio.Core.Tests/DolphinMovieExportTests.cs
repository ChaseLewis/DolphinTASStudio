using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using TasStudio.Emulation;
using Xunit;

namespace TasStudio.Core.Tests;

public sealed class DolphinMovieExportTests
{
    [Fact]
    public void WritesDolphinWireFormatWithPollCountsAndBootRelativeTiming()
    {
        using var files = new TestWorkspace();
        var pad = new ControllerState(PadButtons.Start | PadButtons.A | PadButtons.Up | PadButtons.L, 0, 255, 13, 240, 7, 9);
        InputPollFrame[] frames = [
            new(pad, 2, 1000, [new(100, 0, 0, pad), new(800, 1, 0, ControllerState.Neutral)]),
            new(pad, 3, 1500, []),
            new(pad, 1, 1000, [new(0, 0, 0, pad with { Buttons = ControllerState.KnownButtons })])];
        var result = DolphinMovieExport.Save(files.FilePath("test.dtm"), new("GABC01", 500, 2), frames,
            new() { StartUtcSeconds = 1234567890 }, files.GamePath, "sha256", "test-core",
            bootInputs: new(ControllerState.Neutral, 2, 500, []));
        var bytes = File.ReadAllBytes(result.MoviePath);
        Assert.Equal(256 + 3 * 8, bytes.Length);
        Assert.Equal("DTM\u001aGABC01", Encoding.ASCII.GetString(bytes, 0, 10));
        Assert.Equal(new byte[] { 0, 1, 0 }, bytes[10..13]); // GameCube, port 1, power-on.
        Assert.Equal(8UL, U64(bytes, 0x0D)); // VI fields, not Studio groups.
        Assert.Equal(3UL, U64(bytes, 0x15));
        Assert.Equal(5UL, U64(bytes, 0x1D));
        Assert.Equal(MD5.HashData(File.ReadAllBytes(files.GamePath)), bytes[0x71..0x81]);
        Assert.Equal(1234567890UL, U64(bytes, 0x81));
        Assert.Equal(1, bytes[0x89]); // Avoid preboot GetSettings / uninitialized game region.
        Assert.Equal("D3D", Encoding.ASCII.GetString(bytes, 0x51, 3));
        Assert.Equal(1, bytes[0x8D]); // HLE DSP.
        Assert.Equal(1, bytes[0x8F]); // x64 JIT, not interpreter.
        Assert.Equal(1, bytes[0x97]); // Preserve the supplied Slot A memory card.
        Assert.Equal(0, bytes[0x98]);
        Assert.Equal(3000UL, U64(bytes, 0xED)); // Absolute tick at last poll, not duration or last presentation.
        Assert.Equal(new byte[] { 0x43, 0x44, 7, 9, 0, 255, 13, 240 }, bytes[256..264]);
        Assert.Equal(new byte[] { 0, 0x40, 0, 0, 128, 128, 128, 128 }, bytes[264..272]);
        Assert.Equal(new byte[] { 0xFF, 0x4F, 7, 9, 0, 255, 13, 240 }, bytes[272..280]);
        Assert.True(File.Exists(result.InstructionsPath));
        var profile = Path.Combine(Path.GetDirectoryName(result.InstructionsPath)!, "User", "Config", "Dolphin.ini");
        Assert.Contains("CustomRTCValue = 1234567890", File.ReadAllText(profile));
        Assert.Contains("CPUThread = False", File.ReadAllText(profile));
        Assert.False(File.Exists(result.MoviePath + ".sav"));
    }

    [Theory]
    [InlineData(PadButtons.Start, 0x4001)]
    [InlineData(PadButtons.A, 0x4002)]
    [InlineData(PadButtons.B, 0x4004)]
    [InlineData(PadButtons.X, 0x4008)]
    [InlineData(PadButtons.Y, 0x4010)]
    [InlineData(PadButtons.Z, 0x4020)]
    [InlineData(PadButtons.Up, 0x4040)]
    [InlineData(PadButtons.Down, 0x4080)]
    [InlineData(PadButtons.Left, 0x4100)]
    [InlineData(PadButtons.Right, 0x4200)]
    [InlineData(PadButtons.L, 0x4400)]
    [InlineData(PadButtons.R, 0x4800)]
    public void MapsEachGameCubeButtonToDolphinsDistinctBitLayout(PadButtons button, ushort expected)
    {
        using var files = new TestWorkspace();
        var pad = ControllerState.Neutral with { Buttons = button };
        var result = DolphinMovieExport.Save(files.FilePath("test.dtm"), new("GABC01", 0, 0),
            [new(pad, 1, 1000, [new(50, 0, 0, pad)])], new(), files.GamePath, "hash", "core");
        Assert.Equal(expected, BinaryPrimitives.ReadUInt16LittleEndian(File.ReadAllBytes(result.MoviePath).AsSpan(256)));
    }

    [Fact]
    public void PrependsMeasuredStartupInputsAndCountsTheirPolledFields()
    {
        using var files = new TestWorkspace();
        var bootPad = ControllerState.Neutral with { Buttons = PadButtons.A, StickX = 27 };
        var start = ControllerState.Neutral with { Buttons = PadButtons.Start };
        var boot = new InputPollFrame(ControllerState.Neutral, 3, 500,
            [new(20, 0, 0, bootPad), new(30, 0, 0, ControllerState.Neutral), new(450, 2, 0, bootPad)]);
        var result = DolphinMovieExport.Save(files.FilePath("movie.dtm"), new("GABC01", 500, 3),
            [new(start, 2, 1000, [new(100, 0, 0, start)])], new(), files.GamePath, "hash", "core", bootInputs: boot);
        var bytes = File.ReadAllBytes(result.MoviePath);
        Assert.Equal(4UL, result.InputCount);
        Assert.Equal(5UL, U64(bytes, 0x0D));
        Assert.Equal(2UL, U64(bytes, 0x1D));
        Assert.Equal(600UL, U64(bytes, 0xED));
        Assert.Equal(new byte[] { 2, 64, 0, 0, 27, 128, 128, 128 }, bytes[256..264]);
        Assert.Equal(0x4001, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(256 + 3 * 8)));
        using var report = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(result.InstructionsPath)!, "export.json")));
        Assert.True(report.RootElement.GetProperty("BootPollsIncluded").GetBoolean());
        Assert.Equal(3, report.RootElement.GetProperty("BootPollCount").GetInt32());
    }

    [Fact]
    public void MissingOrMismatchedStartupCaptureDoesNotOverwriteTheDestination()
    {
        using var files = new TestWorkspace();
        var path = files.FilePath("movie.dtm"); File.WriteAllText(path, "existing");
        InputPollFrame[] frames = [new(ControllerState.Neutral, 1, 100, [new(1, 0, 0, ControllerState.Neutral)])];
        Assert.Throws<NotSupportedException>(() => DolphinMovieExport.Save(path, new("GABC01", 500, 3), frames, new(), files.GamePath, "hash", "core"));
        Assert.Throws<InvalidDataException>(() => DolphinMovieExport.Save(path, new("GABC01", 500, 3), frames, new(), files.GamePath, "hash", "core",
            bootInputs: new(ControllerState.Neutral, 3, 499, [])));
        Assert.Equal("existing", File.ReadAllText(path));
    }

    [Fact]
    public void EmbeddedSettingsPreserveGameOverridesAndDoNotTouchInputsOrTiming()
    {
        using var files = new TestWorkspace();
        var system = files.FilePath("system-settings"); var local = files.FilePath("local-settings");
        Directory.CreateDirectory(system); Directory.CreateDirectory(local);
        File.WriteAllText(Path.Combine(system, "GAB.ini"), "[Video_Hacks]\nEFBToTextureEnable = False\n[Core]\nCPUCore = 5\n");
        File.WriteAllText(Path.Combine(local, "GABC01r2.ini"), "[Core]\nCPUCore = 1\nGameCubeLanguage = 3\n");
        var settings = DolphinMovieSettings.Create(new(), "GABC01", 2, system, local);
        var bytes = Enumerable.Repeat((byte)0xAB, 272).ToArray(); "DTM\u001a"u8.CopyTo(bytes);
        var before = bytes.ToArray();
        settings.WriteHeader(bytes);
        Assert.Equal(1, bytes[0x89]); Assert.Equal(0, bytes[0x92]);
        Assert.Equal(1, bytes[0x8F]); Assert.Equal(3, bytes[0x9D]);
        Assert.Equal(before[256..], bytes[256..]);
        Assert.Equal(before[0xED..0xF5], bytes[0xED..0xF5]);
        Assert.Equal(before[0x71..0x89], bytes[0x71..0x89]);
    }

    [Fact]
    public async Task ServiceBakesEditedGroupsAndRestoresPreviewAndInputHistory()
    {
        using var files = new TestWorkspace();
        using var service = new ExecutionService(new MovieBackend { RecordPolls = true });
        await service.CreateProjectAsync(files.FilePath("project.tasproj"), files.GamePath, files.Options);
        for (var i = 0; i < 3; i++) await service.StepAsync();
        await service.SeekAsync(1);
        var memory = await service.ReadMemoryAsync(0x80000040, 4);
        await service.SetInputAsync(2, ControllerState.Neutral with { Buttons = PadButtons.B });
        var progress = new ExportProgress();
        var result = await service.ExportDolphinMovieAsync(files.FilePath("movie.dtm"), progress);
        var baking = progress.Updates.Where(update => update.Stage == DolphinMovieExportStage.Baking).ToArray();
        Assert.Equal(0, baking[0].CompletedGroups);
        Assert.Equal(3, baking[^1].CompletedGroups);
        Assert.All(baking, update => Assert.Equal(3, update.TotalGroups));
        Assert.Equal(DolphinMovieExportStage.Writing, progress.Updates[^1].Stage);
        Assert.Equal(12UL, result.InputCount);
        Assert.Equal(1UL, service.Position);
        Assert.Equal(memory, await service.ReadMemoryAsync(0x80000040, 4));
        Assert.Equal(PadButtons.B, service.Inputs[2].Buttons);
        var bytes = File.ReadAllBytes(result.MoviePath);
        Assert.Equal(0x4004, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(256 + 8 * 8)));
        Assert.True(service.IsPreviewCurrent);
    }

    [Fact]
    public async Task FailureRestoresOriginalStateAndDoesNotReplaceExistingExport()
    {
        using var files = new TestWorkspace();
        var backend = new MovieBackend { RecordPolls = true };
        using var service = new ExecutionService(backend);
        await service.CreateProjectAsync(files.FilePath("project.tasproj"), files.GamePath, files.Options);
        await service.StepAsync();
        var memory = await service.ReadMemoryAsync(0x80000040, 4);
        var path = files.FilePath("movie.dtm"); File.WriteAllText(path, "existing movie");
        backend.FailNextStep = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ExportDolphinMovieAsync(path));
        Assert.Equal(1UL, service.Position);
        Assert.Equal(memory, await service.ReadMemoryAsync(0x80000040, 4));
        Assert.Equal("existing movie", File.ReadAllText(path));
    }

    [Fact]
    public async Task QuickUsesCachedPollsWithoutPlaybackAndMatchesFullExport()
    {
        using var files = new TestWorkspace();
        var backend = new MovieBackend { RecordPolls = true };
        using var service = new ExecutionService(backend);
        await service.CreateProjectAsync(files.FilePath("project.tasproj"), files.GamePath, files.Options);
        for (var i = 0; i < 3; i++) await service.StepAsync();
        await service.SeekAsync(1);
        var full = await service.ExportDolphinMovieAsync(files.FilePath("full.dtm"));
        var before = await service.ReadMemoryAsync(0x80000040, 4);
        var steps = backend.Calls.Count(call => call.Operation == "Step");
        backend.FailNextStep = true; // Any attempted timeline playback must fail this test.
        var progress = new ExportProgress();
        var quick = await service.ExportDolphinMovieAsync(files.FilePath("quick.dtm"), progress, DolphinMovieExportMode.Quick);
        Assert.Equal(File.ReadAllBytes(full.MoviePath), File.ReadAllBytes(quick.MoviePath));
        Assert.Equal(steps, backend.Calls.Count(call => call.Operation == "Step"));
        Assert.True(backend.FailNextStep);
        Assert.DoesNotContain(progress.Updates, update => update.Stage == DolphinMovieExportStage.Baking);
        Assert.Contains(progress.Updates, update => update.Stage == DolphinMovieExportStage.CheckingCache);
        Assert.Equal(1UL, service.Position);
        Assert.Equal(before, await service.ReadMemoryAsync(0x80000040, 4));
        Assert.True(service.IsPreviewCurrent);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QuickRejectsMissingOrEditedCacheWithoutPlaybackOrOverwriting(bool edited)
    {
        using var files = new TestWorkspace();
        var backend = new MovieBackend { RecordPolls = edited };
        using var service = new ExecutionService(backend);
        await service.CreateProjectAsync(files.FilePath("project.tasproj"), files.GamePath, files.Options);
        for (var i = 0; i < 3; i++) await service.StepAsync();
        if (edited) await service.SetInputAsync(1, ControllerState.Neutral with { Buttons = PadButtons.B });
        var steps = backend.Calls.Count(call => call.Operation == "Step");
        var captures = backend.Calls.Count(call => call.Operation == "Capture");
        var path = files.FilePath("movie.dtm"); File.WriteAllText(path, "existing movie");
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => service.ExportDolphinMovieAsync(path, mode: DolphinMovieExportMode.Quick));
        Assert.Contains("Use Export to Dolphin → Full", error.Message);
        Assert.Equal(steps, backend.Calls.Count(call => call.Operation == "Step"));
        Assert.Equal(captures, backend.Calls.Count(call => call.Operation == "Capture"));
        Assert.Equal("existing movie", File.ReadAllText(path));
        backend.RecordPolls = true;
        await service.ExportDolphinMovieAsync(files.FilePath("full.dtm"));
        await service.ExportDolphinMovieAsync(files.FilePath("quick.dtm"), mode: DolphinMovieExportMode.Quick);
        Assert.Equal(File.ReadAllBytes(files.FilePath("full.dtm")), File.ReadAllBytes(files.FilePath("quick.dtm")));
    }

    [Fact]
    public async Task RejectsCapturedStartsAndExecutionEvents()
    {
        using var files = new TestWorkspace();
        using var service = new ExecutionService(new MovieBackend { RecordPolls = true });
        await service.LoadGameAsync(files.GamePath, files.Options);
        await service.NewProjectAsync(); await service.StepAsync();
        var path = files.FilePath("movie.dtm");
        Assert.Contains("power-on", (await Assert.ThrowsAsync<InvalidOperationException>(() => service.ExportDolphinMovieAsync(path))).Message);
        await service.CreateProjectAsync(files.FilePath("power.tasproj"), files.GamePath, files.Options);
        await service.StepAsync(); await service.ResetAsync();
        Assert.Contains("reset events", (await Assert.ThrowsAsync<InvalidOperationException>(() => service.ExportDolphinMovieAsync(path))).Message);
        Assert.False(File.Exists(path));
    }

    private static ulong U64(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(offset));
    private sealed class ExportProgress : IProgress<DolphinMovieExportProgress>
    {
        public List<DolphinMovieExportProgress> Updates { get; } = [];
        public void Report(DolphinMovieExportProgress value) => Updates.Add(value);
    }
    private sealed class MovieBackend : FakeBackend
    {
        public override byte[] ReadMemory(uint address, int count)
        {
            if (address != 0x80000000 || count != 32) return base.ReadMemory(address, count);
            var header = new byte[32];
            "GABC01"u8.CopyTo(header);
            new byte[] { 0xC2, 0x33, 0x9F, 0x3D }.CopyTo(header, 28);
            return header;
        }
    }
}
