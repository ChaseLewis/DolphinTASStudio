using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Dock.Avalonia.Controls;
using Dock.Model.Controls;
using Dock.Model.Core;

namespace TasStudio.App;

// Retain control instances, subscriptions, selection and pending editor values across hosts.
internal sealed class StudioPanelPresenter(StudioPanel panel) : ContentControl
{
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (panel.View?.Parent is ContentControl old && old != this) old.Content = null;
        Content = panel.View;
        base.OnAttachedToVisualTree(e);
    }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Content = null;
        base.OnDetachedFromVisualTree(e);
    }
}

public sealed partial class MainWindow
{
    private WorkspaceFactory _dockFactory = null!;
    private IRootDock _dockRoot = null!;
    private DockControl _dockControl = null!;
    private string _layoutPath = null!;
    private bool _closingWorkspace;

    private static string LayoutPathFromArgs(string[] args)
    {
        var index = Array.IndexOf(args, "--layout-file");
        return index >= 0 && index + 1 < args.Length ? Path.GetFullPath(args[index + 1]) :
            Path.Combine(AppPaths.Data, "workspace.json");
    }

    private Control[] BuildViewMenu() =>
    [
        ActionMenu("Game", () => { ShowWorkspacePanel("game"); return Task.CompletedTask; }),
        ActionMenu("Timeline", () => { ShowWorkspacePanel("timeline"); return Task.CompletedTask; }),
        ActionMenu("TAS Input · Port 1", () => { ShowWorkspacePanel("input"); return Task.CompletedTask; }),
        ActionMenu("Memory Watcher", () => { ShowWorkspacePanel("watcher"); return Task.CompletedTask; }),
        new Separator(),
        ActionMenu("Game fullscreen    Alt+Enter", () => { ToggleGameFullscreen(); return Task.CompletedTask; }),
        ActionMenu("Reset Layout", () => { ResetDockWorkspace(); return Task.CompletedTask; })
    ];

    private Control BuildDockWorkspace()
    {
        var display = new Viewbox { Stretch = Stretch.Uniform, Child = new Border { Width = GameCubeDisplayWidth, Height = GameCubeDisplayHeight, Child = _viewport } };
        var game = new Grid { Background = Brushes.Black, ClipToBounds = true, Children = { display, _empty } };
        StudioPanel Panel(string id, string title, Control view, double minWidth = 200, double minHeight = 120) =>
            new() { Id = id, Title = title, View = view, CanPin = false, CanDockAsDocument = false, MinWidth = minWidth, MinHeight = minHeight };
        _dockFactory = new WorkspaceFactory(new Dictionary<string, StudioPanel>
        {
            ["game"] = Panel("game", "Game", game),
            ["timeline"] = Panel("timeline", "Timeline", BuildTimeline(), 520, 260),
            ["input"] = Panel("input", "TAS Input · Port 1", BuildInspector(), 350, 490),
            ["watcher"] = Panel("watcher", "Memory Watcher", BuildWatcher(), 300, 220)
        });
        _dockFactory.HostWindowLocator = new Dictionary<string, Func<IHostWindow?>> { [nameof(IDockWindow)] = CreateWorkspaceHost };
        _dockFactory.DefaultHostWindowLocator = CreateWorkspaceHost;
        _dockRoot = _dockFactory.CreateLayout();
        try
        {
            if (File.Exists(_layoutPath)) _dockRoot = _dockFactory.Restore(WorkspaceLayout.Parse(File.ReadAllText(_layoutPath)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException)
        { _status.Text = "Saved layout could not be restored; using the default layout."; }
        _dockControl = new DockControl { Layout = _dockRoot, Factory = _dockFactory, InitializeFactory = true, InitializeLayout = false, IsDockingEnabled = true };
        Opened += (_, _) =>
        {
            foreach (var window in _dockRoot.Windows ?? []) ClampFloatingWindow(window);
            _dockFactory.InitLayout(_dockRoot);
        };
        return _dockControl;
    }

    private IHostWindow CreateWorkspaceHost()
    {
        var host = new HostWindow { MinWidth = 260, MinHeight = 180 };
        AttachInputEvents(host);
        return host;
    }

    private void ClampFloatingWindow(IDockWindow window)
    {
        var screen = Screens.ScreenFromPoint(new PixelPoint((int)Math.Clamp(window.X, int.MinValue, int.MaxValue), (int)Math.Clamp(window.Y, int.MinValue, int.MaxValue))) ?? Screens.Primary;
        if (screen is null) return;
        var area = screen.WorkingArea;
        window.Width = Math.Clamp(window.Width, 260, Math.Max(260, area.Width / screen.Scaling));
        window.Height = Math.Clamp(window.Height, 180, Math.Max(180, area.Height / screen.Scaling));
        window.X = Math.Clamp(window.X, area.X, Math.Max(area.X, area.Right - window.Width * screen.Scaling));
        window.Y = Math.Clamp(window.Y, area.Y, Math.Max(area.Y, area.Bottom - window.Height * screen.Scaling));
    }

    internal void ShowWorkspacePanel(string id)
    {
        if (_closingWorkspace) return;
        ShowEditor();
        var panel = _dockFactory.Panels[id];
        if (!WorkspaceFactory.Walk(_dockRoot).Contains(panel))
        {
            // Closing a panel only hides its view. Reopen floating and let the user place it.
            var group = _dockFactory.Tabs(panel);
            _dockRoot.VisibleDockables ??= _dockFactory.CreateList<IDockable>();
            _dockRoot.VisibleDockables.Add(group);
            _dockFactory.InitDockable(group, _dockRoot);
            _dockFactory.FloatDockable(panel);
        }
        _dockFactory.SetActiveDockable(panel);
        if (TopLevel.GetTopLevel(panel.View!) is Window host) host.Activate();
    }

    internal void ResetDockWorkspace()
    {
        _input.SetAcceptInput(false);
        foreach (var window in _workspaceWindows.Where(w => w != this).ToArray()) window.Close();
        _dockControl.Layout = null;
        foreach (var panel in _dockFactory.Panels.Values) { panel.Owner = null; panel.OriginalOwner = null; }
        _dockRoot = _dockFactory.CreateLayout();
        _dockFactory.InitLayout(_dockRoot);
        _dockControl.Layout = _dockRoot;
        SaveDockWorkspace();
        UpdateInputAcceptance();
    }

    private void SaveDockWorkspace()
    {
        try
        {
            var json = WorkspaceFactory.Capture(_dockRoot).ToJson();
            WorkspaceLayout.Parse(json);
            Directory.CreateDirectory(Path.GetDirectoryName(_layoutPath)!);
            File.WriteAllText(_layoutPath + ".tmp", json);
            File.Move(_layoutPath + ".tmp", _layoutPath, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException)
        { _status.Text = "Could not save workspace layout: " + ex.Message; }
    }

    private void CloseDockWorkspace()
    {
        _closingWorkspace = true;
        foreach (var window in _workspaceWindows.Where(w => w != this).ToArray()) window.Close();
    }

    internal void CloseAfterCapture() { _closingApproved = true; Close(); }
}
