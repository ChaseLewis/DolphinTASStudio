using System.Buffers.Binary;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TasStudio.App;
using TasStudio.Emulation;
using Xunit;

namespace TasStudio.Core.Tests;

public sealed class ProjectHomeTests
{
    [AvaloniaFact]
    public void RecentProjectCardsShowRealSummaryAndSelectedDetails()
    {
        using var files = new TestWorkspace();
        var recent = new RecentProject(files.FilePath("Opening route.tasproj"), DateTimeOffset.UtcNow,
            files.GamePath, "fixture", 360, 600, 2, new(ProjectStartKind.SaveState, "start.tasstate", 120));
        var main = new MainWindow(["--layout-file", files.FilePath("layout.json")], new AppSettings { RecentProjects = [recent] }) { Width = 1060, Height = 720 };
        try
        {
            main.Show(); main.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            var labels = main.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).ToArray();
            Assert.Contains(labels, t => t.Text == "Opening route");
            Assert.Contains(labels, t => t.Text == "600 inputs · 2 candidate takes");
            Assert.Contains(labels, t => t.Text == recent.StartLabel);
            Capture(main, "project-home-populated.png");
        }
        finally { main.CloseAfterCapture(); }
    }
    [AvaloniaFact]
    public void StartsAtProjectHomeAndWizardOffersBothStartingPoints()
    {
        using var files = new TestWorkspace();
        var main = new MainWindow(["--layout-file", files.FilePath("layout.json")]);
        NewProjectDialog? wizard = null;
        try
        {
            main.Show(); main.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            Assert.Contains(main.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Your projects" && t.IsEffectivelyVisible);
            Capture(main, "project-home.png");
            wizard = new NewProjectDialog(files.GamePath); wizard.Show(main); wizard.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            wizard.Location.Text = files.DirectoryPath; wizard.ProjectName.Text = "Test project";
            var power = wizard.ReadRequest(); Assert.Null(power.StatePath); Assert.Equal(946684800, power.StartUtc);
            Assert.EndsWith(Path.Combine("Test project", "Test project.tasproj"), power.Path);
            Capture(wizard, "new-project.png");
            wizard.StartingPoint.SelectedIndex = 1;
            Assert.False(wizard.Utc.IsEnabled);
            Assert.Throws<InvalidDataException>(() => wizard.ReadRequest());
            wizard.StatePath.Text = files.GamePath; // File existence only here; backend validates the archive before replacing the session.
            Assert.Equal(files.GamePath, wizard.ReadRequest().StatePath);
            Capture(wizard, "new-project-state.png");
        }
        finally { wizard?.Close(); main.CloseAfterCapture(); }
    }
    [AvaloniaFact]
    public void WizardRejectsInvalidUtcAndExistingFolders()
    {
        using var files = new TestWorkspace(); var wizard = new NewProjectDialog(files.GamePath);
        wizard.Location.Text = files.DirectoryPath; wizard.ProjectName.Text = "new"; wizard.Utc.Text = "invalid";
        Assert.Throws<InvalidDataException>(() => wizard.ReadRequest());
        wizard.Utc.Text = "2000-01-01 00:00:00";
        Directory.CreateDirectory(files.FilePath("new"));
        Assert.Throws<InvalidDataException>(() => wizard.ReadRequest());
    }
    [Fact]
    public void BannerReadsTilesAndRejectsOutOfBoundsFst()
    {
        using var files = new TestWorkspace(); var image = new byte[0x2200];
        void Word(int offset, uint value) => BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(offset), value);
        Word(0x1c, 0xc2339f3d); Word(0x424, 0x500); Word(0x428, 40); Word(0x508, 2);
        Word(0x510, 0x600); Word(0x514, 0x1820); "opening.bnr\0"u8.CopyTo(image.AsSpan(0x518));
        "BNR1"u8.CopyTo(image.AsSpan(0x600));
        for (var i = 0x620; i < 0x1e20; i += 2) BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(i), 0xfc00);
        File.WriteAllBytes(files.GamePath, image);
        var rgba = GameCubeBanner.ReadRgba(files.GamePath)!;
        Assert.Equal(96 * 32 * 4, rgba.Length); Assert.Equal(new byte[] { 255, 0, 0, 255 }, rgba[..4]);
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, rgba[^4..]);
        Word(0x424, uint.MaxValue); File.WriteAllBytes(files.GamePath, image);
        Assert.Throws<InvalidDataException>(() => GameCubeBanner.ReadRgba(files.GamePath));
    }
    [Fact]
    public void RealDiscBannerCanBeReadWithoutEmulation()
    {
        if (Environment.GetEnvironmentVariable("TASSTUDIO_ROM_FIXTURE") is not { Length: > 0 } path) return;
        var rgba = GameCubeBanner.ReadRgba(path); Assert.NotNull(rgba); Assert.Equal(96 * 32 * 4, rgba.Length);
    }
    private static void Capture(Window window, string name)
    {
        if (Environment.GetEnvironmentVariable("TASSTUDIO_TEST_CAPTURE_DIR") is not { Length: > 0 } path) return;
        Directory.CreateDirectory(path); window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); window.CaptureRenderedFrame()!.Save(Path.Combine(path, name));
    }
}
