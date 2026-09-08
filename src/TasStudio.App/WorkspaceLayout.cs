using System.Text.Json;
using Avalonia.Controls;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm;
using Dock.Model.Mvvm.Controls;

namespace TasStudio.App;

// Only layout metadata is serialized. Views, emulator state and arbitrary CLR types never are.
public sealed record WorkspaceNode(string Kind, string? Panel = null, double Proportion = 1,
    int Active = 0, WorkspaceNode[]? Children = null);
public sealed record WorkspaceFloat(WorkspaceNode Layout, double X, double Y, double Width, double Height);
public sealed record WorkspaceLayout(int Version, WorkspaceNode Main, WorkspaceFloat[] Windows)
{
    public const int CurrentVersion = 1;
    public static readonly string[] PanelIds = ["game", "timeline", "input", "watcher"];
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, MaxDepth = 32 };

    public string ToJson() => JsonSerializer.Serialize(this, Json);
    public static WorkspaceLayout Parse(string json)
    {
        var result = JsonSerializer.Deserialize<WorkspaceLayout>(json, Json) ?? throw new InvalidDataException("Empty layout.");
        if (result.Version != CurrentVersion || result.Windows is null || result.Windows.Length > PanelIds.Length)
            throw new InvalidDataException("Unsupported workspace layout.");
        var seen = new HashSet<string>();
        var count = 0;
        void Validate(WorkspaceNode? node, int depth)
        {
            if (node is null || depth > 12 || ++count > 64 || !double.IsFinite(node.Proportion) || node.Proportion <= 0)
                throw new InvalidDataException("Invalid layout structure.");
            if (node.Kind == "panel")
            {
                if (node.Panel is null || !PanelIds.Contains(node.Panel) || !seen.Add(node.Panel) || node.Children is { Length: > 0 })
                    throw new InvalidDataException("Unknown or duplicate panel.");
                return;
            }
            if (node.Kind is not ("root" or "horizontal" or "vertical" or "tabs") || node.Children is null || node.Children.Length > 16)
                throw new InvalidDataException("Unknown container.");
            if (node.Kind == "tabs" && node.Children.Any(n => n?.Kind != "panel"))
                throw new InvalidDataException("Invalid tab group.");
            foreach (var child in node.Children) Validate(child, depth + 1);
        }
        Validate(result.Main, 0);
        if (result.Main.Kind != "root") throw new InvalidDataException("Missing workspace root.");
        foreach (var window in result.Windows)
        {
            if (window is null || window.Layout?.Kind != "root" ||
                !double.IsFinite(window.X) || !double.IsFinite(window.Y) ||
                !double.IsFinite(window.Width) || !double.IsFinite(window.Height) || window.Width <= 0 || window.Height <= 0)
                throw new InvalidDataException("Invalid floating window.");
            Validate(window.Layout, 0);
        }
        return result;
    }
}

public sealed class StudioPanel : Tool
{
    public Control? View { get; init; }
}

public sealed class WorkspaceFactory : Factory
{
    public IReadOnlyDictionary<string, StudioPanel> Panels { get; }
    public WorkspaceFactory(IReadOnlyDictionary<string, StudioPanel> panels) => Panels = panels;

    public override IRootDock CreateLayout()
    {
        // Full-height input editor; the watcher starts closed so neither tool needs to be squeezed.
        var left = new ProportionalDock
        {
            Orientation = Orientation.Vertical, Proportion = .70,
            VisibleDockables = CreateList<IDockable>(Tabs(Panels["game"], .64), new ProportionalDockSplitter(), Tabs(Panels["timeline"], .36))
        };
        var main = new ProportionalDock
        {
            Orientation = Orientation.Horizontal,
            VisibleDockables = CreateList<IDockable>(left, new ProportionalDockSplitter(), Tabs(Panels["input"], .30))
        };
        var root = (RootDock)CreateRootDock();
        root.Id = "workspace"; root.IsCollapsable = false;
        root.VisibleDockables = CreateList<IDockable>(main);
        root.ActiveDockable = root.DefaultDockable = main;
        return root;
    }

    public ToolDock Tabs(StudioPanel panel, double proportion = 1) => new()
    {
        Id = Guid.NewGuid().ToString("N"), Title = panel.Title, Proportion = proportion,
        MinWidth = panel.MinWidth, MinHeight = panel.MinHeight,
        VisibleDockables = CreateList<IDockable>(panel), ActiveDockable = panel,
        CanCloseLastDockable = true
    };

    public static IEnumerable<IDockable> Walk(IDockable node)
    {
        yield return node;
        if (node is IDock dock)
            foreach (var child in dock.VisibleDockables ?? [])
                foreach (var descendant in Walk(child)) yield return descendant;
        if (node is IRootDock root)
            foreach (var window in root.Windows ?? [])
                if (window.Layout is { } layout)
                    foreach (var descendant in Walk(layout)) yield return descendant;
    }

    private static WorkspaceNode CaptureNode(IDockable node)
    {
        var proportion = double.IsFinite(node.Proportion) && node.Proportion > 0 ? node.Proportion : 1;
        if (node is StudioPanel panel) return new("panel", panel.Id, proportion);
        var dock = (IDock)node;
        var children = (dock.VisibleDockables ?? []).Where(d => d is not IProportionalDockSplitter).ToArray();
        var kind = node switch
        {
            IRootDock => "root",
            IProportionalDock split => split.Orientation == Orientation.Horizontal ? "horizontal" : "vertical",
            _ => "tabs"
        };
        return new(kind, Proportion: proportion, Active: Math.Max(0, Array.IndexOf(children, dock.ActiveDockable)),
            Children: children.Select(CaptureNode).ToArray());
    }

    public static WorkspaceLayout Capture(IRootDock root)
    {
        var windows = Walk(root).OfType<IRootDock>().SelectMany(r => r.Windows ?? []).Distinct()
            .Where(w => w.Layout is not null).Select(w =>
            {
                var x = w.X; var y = w.Y; var width = w.Width; var height = w.Height;
                w.Host?.GetPosition(out x, out y); w.Host?.GetSize(out width, out height);
                return new WorkspaceFloat(CaptureNode(w.Layout!), Finite(x, 80), Finite(y, 80), Finite(width, 640), Finite(height, 560));
            }).ToArray();
        return new(WorkspaceLayout.CurrentVersion, CaptureNode(root), windows);
    }
    private static double Finite(double value, double fallback) => double.IsFinite(value) ? value : fallback;

    public IRootDock Restore(WorkspaceLayout saved)
    {
        // Validate even when the caller built the DTO itself.
        saved = WorkspaceLayout.Parse(saved.ToJson());
        IDockable Build(WorkspaceNode node)
        {
            if (node.Kind == "panel")
            {
                var panel = Panels[node.Panel!]; panel.Proportion = node.Proportion; return panel;
            }
            IDock dock = node.Kind switch
            {
                "root" => CreateRootDock(),
                "tabs" => new ToolDock { CanCloseLastDockable = true },
                _ => new ProportionalDock { Orientation = node.Kind == "horizontal" ? Orientation.Horizontal : Orientation.Vertical }
            };
            dock.Id = Guid.NewGuid().ToString("N"); dock.Proportion = node.Proportion;
            var children = node.Children!.Select(Build).ToArray();
            if (dock is IToolDock)
            {
                dock.MinWidth = children.Select(c => double.IsFinite(c.MinWidth) ? c.MinWidth : 0).DefaultIfEmpty().Max();
                dock.MinHeight = children.Select(c => double.IsFinite(c.MinHeight) ? c.MinHeight : 0).DefaultIfEmpty().Max();
            }
            dock.VisibleDockables = CreateList<IDockable>();
            foreach (var child in children)
            {
                if (dock is IProportionalDock && dock.VisibleDockables.Count > 0) dock.VisibleDockables.Add(new ProportionalDockSplitter());
                dock.VisibleDockables.Add(child);
            }
            dock.ActiveDockable = dock.DefaultDockable = children.ElementAtOrDefault(Math.Clamp(node.Active, 0, Math.Max(0, children.Length - 1)));
            if (dock is RootDock root) root.IsCollapsable = false;
            return dock;
        }
        var result = (IRootDock)Build(saved.Main);
        result.Windows = CreateList<IDockWindow>();
        foreach (var floating in saved.Windows)
        {
            var window = CreateDockWindow();
            window.Id = nameof(IDockWindow); window.Title = "Dolphin TAS Studio";
            window.X = floating.X; window.Y = floating.Y; window.Width = floating.Width; window.Height = floating.Height;
            window.Layout = (IRootDock)Build(floating.Layout);
            result.Windows.Add(window);
        }
        return result;
    }
}
