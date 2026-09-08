using System.Buffers.Binary;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TasStudio.App;
using TasStudio.Core;
using TasStudio.Emulation;
using Xunit;

namespace TasStudio.Core.Tests;

public sealed class ManualPlayTests
{
    [Fact]
    public async Task ManualStatesAreIndependentAndBecomePortableProjectBaselines()
    {
        using var files = new TestWorkspace();
        using var service = new ExecutionService(new FakeBackend { RecordPolls = true });
        await service.LoadPlayGameAsync(files.GamePath, files.Options);
        var expected = new List<byte[]>();
        var states = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            await service.AdvanceFrameAsync(ControllerState.Neutral with { Buttons = i == 1 ? PadButtons.A : PadButtons.Start });
            states.Add(files.FilePath($"fixture-{i}.tasstate"));
            await service.SaveStateAsync(states[^1]);
            expected.Add(await service.ReadMemoryAsync(0x80000000, 4));
        }
        Assert.True(service.IsManualPlay); Assert.Empty(service.Inputs); Assert.Empty(service.StateMarkers);
        for (var i = 0; i < states.Count; i++)
        {
            await service.LoadPlayGameAsync(files.GamePath, files.Options, states[i]);
            Assert.Equal(expected[i], await service.ReadMemoryAsync(0x80000000, 4));
            Assert.False(service.HasProject);
            var project = files.FilePath($"project-{i}/test.tasproj");
            await service.CreateProjectAsync(project, files.GamePath, files.Options, states[i]);
            File.Delete(states[i]);
            await service.LoadProjectAsync(project, files.Options);
            Assert.Equal(0UL, service.Position);
            Assert.Equal(expected[i], await service.ReadMemoryAsync(0x80000000, 4));
        }
    }

    [Fact]
    public async Task FailedPlayOpenRestoresExistingProject()
    {
        using var files = new TestWorkspace(); var backend = new FakeBackend(); using var service = new ExecutionService(backend);
        await service.CreateProjectAsync(files.FilePath("movie/movie.tasproj"), files.GamePath, files.Options);
        await service.StepAsync(); var memory = await service.ReadMemoryAsync(0x80000000, 4);
        backend.FailNextLoad = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.LoadPlayGameAsync(files.GamePath, files.Options));
        Assert.True(service.HasProject); Assert.Single(service.Inputs);
        Assert.Equal(memory, await service.ReadMemoryAsync(0x80000000, 4));
    }

    [Fact]
    public async Task CardsImportAsCopiesAndExportWithoutChangingPlayPosition()
    {
        using var files = new TestWorkspace(); var backend = new FakeBackend(); using var service = new ExecutionService(backend);
        var options = files.Options with { Configuration = new EmulationConfiguration() };
        await service.LoadPlayGameAsync(files.GamePath, options);
        var source = files.FilePath("import.raw"); var bytes = Card(4); File.WriteAllBytes(source, bytes);
        await service.ImportPlayMemoryCardAsync("USA", source);
        var active = PlayMemoryCards.CardPath(backend.LastLoadOptions!, "USA");
        Assert.Equal(0, backend.LastLoadOptions!.Configuration!.MemoryCardSizeOverride);
        Assert.Equal(bytes, File.ReadAllBytes(active)); Assert.Equal(bytes, File.ReadAllBytes(source));
        await service.StepAsync(); var memory = await service.ReadMemoryAsync(0x80000000, 4);
        var exported = files.FilePath("export.raw"); await service.ExportPlayMemoryCardAsync("USA", exported);
        Assert.Equal(bytes, File.ReadAllBytes(exported)); Assert.Equal(1UL, service.Position);
        Assert.Equal(memory, await service.ReadMemoryAsync(0x80000000, 4));
        var state = files.FilePath("small-card.tasstate"); await service.SaveStateAsync(state);
        await service.CreateProjectAsync(files.FilePath("small/small.tasproj"), files.GamePath, options, state);
        Assert.Equal(0, backend.LastLoadOptions.Configuration!.MemoryCardSizeOverride);
        Assert.NotEqual(options.SaveDirectory, backend.LastLoadOptions.SaveDirectory);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ImportPlayMemoryCardAsync("USA", source));
    }

    [Fact]
    public async Task FailedImportRestoresCardAndSessionAndInvalidCardsNeverReplaceIt()
    {
        using var files = new TestWorkspace(); var backend = new FakeBackend(); using var service = new ExecutionService(backend);
        var options = files.Options with { Configuration = new EmulationConfiguration { MemoryCardSizeOverride = 0 } };
        var active = PlayMemoryCards.CardPath(options, "USA"); Directory.CreateDirectory(Path.GetDirectoryName(active)!);
        var original = Card(4); File.WriteAllBytes(active, original);
        await service.LoadPlayGameAsync(files.GamePath, options); await service.StepAsync();
        var memory = await service.ReadMemoryAsync(0x80000000, 4);
        var imported = Card(4); imported[0x2000] = 7; var source = files.FilePath("source.raw"); File.WriteAllBytes(source, imported);
        backend.FailNextLoad = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ImportPlayMemoryCardAsync("USA", source));
        Assert.Equal(original, File.ReadAllBytes(active)); Assert.Equal(1UL, service.Position);
        Assert.Equal(memory, await service.ReadMemoryAsync(0x80000000, 4));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(active)!, "*.bak"));
        File.WriteAllBytes(source, [1, 2, 3]);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.ImportPlayMemoryCardAsync("USA", source));
        Assert.Equal(original, File.ReadAllBytes(active));
        await Assert.ThrowsAsync<FileNotFoundException>(() => service.ExportPlayMemoryCardAsync("EUR", files.FilePath("unused.raw")));
        Assert.True(service.IsManualPlay); Assert.Equal(1UL, service.Position);
    }

    [Theory]
    [InlineData(4, 0, "59")]
    [InlineData(8, 1, "123")]
    [InlineData(16, 2, "251")]
    [InlineData(32, 3, "507")]
    [InlineData(64, 4, "1019")]
    [InlineData(128, null, "")]
    public void SupportedCardSizesMatchDolphinAndRejectCorruptHeaders(int mbits, int? size, string suffix)
    {
        var bytes = Card(mbits);
        Assert.Equal(size, PlayMemoryCards.ValidateRawCard(bytes, "USA"));
        using var files = new TestWorkspace();
        var path = PlayMemoryCards.CardPath(files.Options with { Configuration = new() { MemoryCardSizeOverride = size } }, "USA");
        Assert.EndsWith("MemoryCardA.USA" + (suffix.Length > 0 ? "." + suffix : "") + ".raw", path);
        Assert.Throws<InvalidDataException>(() => PlayMemoryCards.ValidateRawCard(bytes, "JAP"));
        bytes[0] ^= 1;
        Assert.Throws<InvalidDataException>(() => PlayMemoryCards.ValidateRawCard(bytes, "USA"));
    }

    [AvaloniaFact]
    public async Task PlayViewUsesLiveKeyboardAndAdvanceWithoutTimelineOrRecording()
    {
        using var files = new TestWorkspace(); var backend = new FakeBackend(); using var service = new ExecutionService(backend);
        await service.LoadPlayGameAsync(files.GamePath, files.Options);
        var main = new MainWindow(["--layout-file", files.FilePath("layout.json")], new AppSettings(), service);
        try
        {
            main.Show(); main.ShowEditor(); main.Activate(); main.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            Assert.Contains(main.GetVisualDescendants().OfType<Control>(), c => c.Name == "PlayWorkspace" && c.IsEffectivelyVisible);
            Assert.DoesNotContain(main.GetVisualDescendants().OfType<TimelineView>(), t => t.IsEffectivelyVisible);
            main.KeyPressQwerty(PhysicalKey.ArrowUp, RawInputModifiers.None);
            main.KeyPressQwerty(PhysicalKey.X, RawInputModifiers.None);
            await Until(() => service.LiveInput().Buttons.HasFlag(PadButtons.A));
            main.KeyPressQwerty(PhysicalKey.F11, RawInputModifiers.None);
            main.KeyReleaseQwerty(PhysicalKey.F11, RawInputModifiers.None);
            await Until(() => service.Position == 1);
            var input = Assert.Single(backend.SubmittedInputs);
            Assert.True(input.Buttons.HasFlag(PadButtons.A)); Assert.True(input.StickY > 128);
            Assert.Empty(service.Inputs); Assert.False(service.IsRecordingLive);
            main.KeyReleaseQwerty(PhysicalKey.X, RawInputModifiers.None);
            main.KeyReleaseQwerty(PhysicalKey.ArrowUp, RawInputModifiers.None);
            await Until(() => service.LiveInput() == ControllerState.Neutral);
            await service.RunAsync(); await Until(() => service.Position >= 3); await service.PauseAsync();
            Assert.True(service.IsManualPlay); Assert.Empty(service.Inputs);
            await main.ShowProjects();
        }
        finally { main.CloseAfterCapture(); }
    }

    private static async Task Until(Func<bool> condition)
    {
        var end = DateTime.UtcNow.AddSeconds(5);
        while (!condition()) { if (DateTime.UtcNow > end) throw new TimeoutException(); Dispatcher.UIThread.RunJobs(); await Task.Delay(10); }
    }

    private static byte[] Card(int mbits)
    {
        var bytes = new byte[mbits * 131072];
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(0x22), (ushort)mbits);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(0x1fc), (ushort)mbits);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(0x1fe), unchecked((ushort)(0xffff * (0x1fc / 2) - mbits)));
        return bytes;
    }
}
