using Avalonia.Controls;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TasStudio.App;
using TasStudio.Core;
using Xunit;

namespace TasStudio.Core.Tests;

public sealed class WatchEditorTests
{
    [AvaloniaFact]
    public void NewWatchWithoutPointerCanOpen()
    {
        var dialog = new WatchEditorDialog(WatchNode.Entry("New watch", new(0x80000000)), true, _ => Task.FromResult<WatchValue?>(null));
        try { dialog.Show(); dialog.UpdateLayout(); Assert.NotNull(dialog.ResultDefinition().Watch); }
        finally { dialog.Close(); }
    }
    [AvaloniaFact]
    public async Task DoubleClickEditsRowsAndBlankAreaAddsAndPersists()
    {
        using var files = new TestWorkspace();
        var watches = files.FilePath("fixture.watches.json");
        var original = WatchNode.Entry("Key Items", new(0x8030C048, WatchType.Bytes, 12));
        new WatchDocument(1, [original]).Save(watches);
        var main = new MainWindow(["--layout-file", files.FilePath("layout.json")]);
        try
        {
            main.ShowEditor(); main.Show(); main.LoadWatchDocument(watches); main.ShowWorkspacePanel("watcher");
            Dispatcher.UIThread.RunJobs();
            var root = (Dock.Model.Controls.IRootDock)main.GetVisualDescendants().OfType<Dock.Avalonia.Controls.DockControl>().First().Layout!;
            var factory = (WorkspaceFactory)root.Factory!;
            var view = factory.Panels["watcher"].View!;
            var host = (Window)TopLevel.GetTopLevel(view)!;
            host.Width = 620; host.Height = 480; host.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            var row = host.GetVisualDescendants().OfType<ListBoxItem>().First();
            var point = row.TranslatePoint(new Point(60, 12), host)!.Value;
            host.MouseDown(point, MouseButton.Left); host.MouseUp(point, MouseButton.Left);
            host.MouseDown(point, MouseButton.Left); host.MouseUp(point, MouseButton.Left);
            WatchEditorDialog? dialog = null;
            for (var i = 0; i < 30 && dialog is null; i++) { await Task.Delay(10); dialog = host.OwnedWindows.OfType<WatchEditorDialog>().FirstOrDefault(); }
            Assert.NotNull(dialog);
            Assert.Equal("Key Items", dialog.NameEditor.Text);
            dialog.NameEditor.Text = "Edited watch";
            dialog.Close(dialog.ResultDefinition()); await Task.Delay(30); Dispatcher.UIThread.RunJobs();
            Assert.Equal("Edited watch", WatchDocument.Load(watches).Nodes[0].Name);
            var list = host.GetVisualDescendants().OfType<ListBox>().First(l => l.ItemsSource is System.Collections.ObjectModel.ObservableCollection<WatchRow>);
            Assert.True(list.Bounds.Height > 150, $"List height: {list.Bounds.Height}");
            point = list.TranslatePoint(new Point(70, 140), host)!.Value;
            host.MouseDown(point, MouseButton.Left); host.MouseUp(point, MouseButton.Left);
            host.MouseDown(point, MouseButton.Left); host.MouseUp(point, MouseButton.Left);
            dialog = null;
            for (var i = 0; i < 30 && dialog is null; i++) { await Task.Delay(10); dialog = host.OwnedWindows.OfType<WatchEditorDialog>().FirstOrDefault(); }
            Assert.NotNull(dialog); Assert.Equal("Add watch", dialog.Title);
            dialog.NameEditor.Text = "Added watch"; dialog.Close(dialog.ResultDefinition());
            await Task.Delay(30); Dispatcher.UIThread.RunJobs();
            Assert.Equal(2, WatchDocument.Load(watches).Nodes.Length);
            var project = files.FilePath("movie.tasproj");
            main.StoreWatchesForProject(project); main.LoadWatchDocument(project + ".watches.json");
            Assert.Equal(2, WatchDocument.Load(project + ".watches.json").Nodes.Length);
            host.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            if (Environment.GetEnvironmentVariable("TASSTUDIO_TEST_CAPTURE_DIR") is { Length: > 0 } output)
            { Directory.CreateDirectory(output); host.CaptureRenderedFrame()!.Save(Path.Combine(output, "watcher.png")); }
        }
        finally { main.CloseAfterCapture(); }
    }
    [AvaloniaFact]
    public void PointerAndLengthEditorRoundTripWithoutDisplayChoice()
    {
        var watch = WatchNode.Entry("Key Items", new(0x8030C048, WatchType.Bytes, 12, [0x3C, -4]));
        var dialog = new WatchEditorDialog(watch, false, _ => Task.FromResult<WatchValue?>(null));
        try
        {
            dialog.Show(); dialog.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            Assert.Single(dialog.GetVisualDescendants().OfType<ComboBox>());
            Assert.True(dialog.LengthEditor.IsVisible); Assert.True(dialog.PointerEditor.IsChecked);
            Assert.Equal(watch.Watch!.Offsets, dialog.ResultDefinition().Watch!.Offsets);
            if (Environment.GetEnvironmentVariable("TASSTUDIO_TEST_CAPTURE_DIR") is { Length: > 0 } output)
            { Directory.CreateDirectory(output); dialog.CaptureRenderedFrame()!.Save(Path.Combine(output, "watch-editor.png")); }
            dialog.NameEditor.Text = "Edited"; dialog.AddressEditor.Text = "8030C04C";
            Assert.Equal("Edited", dialog.ResultDefinition().Name);
            Assert.Equal(0x8030C04Cu, dialog.ResultDefinition().Watch!.Address);
            dialog.PointerEditor.IsChecked = false; Assert.Empty(dialog.ResultDefinition().Watch!.Offsets!);
            dialog.AddressEditor.Text = "bad address";
            Assert.Throws<FormatException>(() => dialog.ResultDefinition());
        }
        finally { dialog.Close(); }
    }
}
