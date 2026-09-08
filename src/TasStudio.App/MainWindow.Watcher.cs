using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.VisualTree;
using TasStudio.Core;
using TasStudio.Emulation;

namespace TasStudio.App;

internal sealed class WatchRow(WatchNode node, int depth) : INotifyPropertyChanged
{
    public WatchNode Node { get; } = node;
    public int Depth { get; } = depth;
    public string ValueText { get; private set; } = node.Diagnostic is null ? "???" : "Unsupported";
    public string? ValueDetail { get; private set; } = node.Diagnostic;
    public event PropertyChangedEventHandler? PropertyChanged;
    public void SetValue(string text, string? detail)
    {
        ValueText = text; ValueDetail = detail;
        PropertyChanged?.Invoke(this, new(nameof(ValueText))); PropertyChanged?.Invoke(this, new(nameof(ValueDetail)));
    }
}

public sealed partial class MainWindow
{
    private WatchDocument _watchDocument = WatchDocument.Empty;
    private string _watchPath = Path.Combine(AppPaths.Data, "watches.json");
    private bool _watchSaveBlocked;
    private readonly HashSet<Guid> _expandedWatches = [];
    private readonly ObservableCollection<WatchRow> _watchRows = [];
    private readonly ListBox _watchList = new() { Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
    private readonly TextBlock _watchSampleLabel = new() { FontSize = 11, Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap };
    private Control? _watchView;
    private long _watchVersion, _watchRequestedGeneration = -1;
    private bool _watchSampling;
    private WatchSample? _watchSample;

    private Control BuildWatcher()
    {
        var root = new Grid { RowDefinitions = new("Auto,Auto,*,Auto"), Background = PanelBrush };
        var toolbar = new WrapPanel { Margin = new Thickness(6) };
        Button Action(string label, Func<Task> action)
        { var b = ActionButton(label, action); b.FontSize = 11; b.Padding = new Thickness(7, 3); return b; }
        toolbar.Children.Add(Action("+ Watch", () => EditWatch(null, false)));
        toolbar.Children.Add(Action("+ Group", () => EditWatch(null, true)));
        toolbar.Children.Add(Action("Import…", ImportWatches));
        toolbar.Children.Add(Action("Refresh", RefreshWatches));
        root.Children.Add(toolbar);
        var headings = new Grid { ColumnDefinitions = new("*,110,75"), Margin = new Thickness(8, 0, 8, 3), ColumnSpacing = 6 };
        headings.Children.Add(new TextBlock { Text = "Name", FontSize = 11 });
        var value = new TextBlock { Text = "Value", FontSize = 11 }; Grid.SetColumn(value, 1); headings.Children.Add(value);
        var type = new TextBlock { Text = "Type", FontSize = 11 }; Grid.SetColumn(type, 2); headings.Children.Add(type);
        Grid.SetRow(headings, 1); root.Children.Add(headings);
        var rowStyle = new Style(s => s.OfType<ListBoxItem>());
        rowStyle.Setters.Add(new Setter(TemplatedControl.PaddingProperty, new Thickness(5, 1)));
        rowStyle.Setters.Add(new Setter(MinHeightProperty, 26d));
        rowStyle.Setters.Add(new Setter(ContentControl.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        _watchList.Styles.Add(rowStyle); _watchList.ItemsSource = _watchRows;
        _watchList.ItemTemplate = new FuncDataTemplate<WatchRow>((row, _) => row is null ? new Border() : BuildWatchRow(row));
        _watchList.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(_watchList).Properties.IsRightButtonPressed &&
                (e.Source as Visual)?.GetSelfAndVisualAncestors().OfType<ListBoxItem>().FirstOrDefault()?.DataContext is WatchRow row)
                _watchList.SelectedItem = row;
        };
        _watchList.AddHandler(PointerPressedEvent, async (_, e) =>
        {
            if (e.ClickCount != 2 || !e.GetCurrentPoint(_watchList).Properties.IsLeftButtonPressed ||
                (e.Source as Visual)?.GetSelfAndVisualAncestors().Any(v => v is Button or ScrollBar) == true) return;
            var row = (e.Source as Visual)?.GetSelfAndVisualAncestors().OfType<ListBoxItem>().FirstOrDefault()?.DataContext as WatchRow;
            e.Handled = true; await Perform(() => EditWatch(row?.Node, false));
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        _watchList.KeyDown += async (_, e) =>
        {
            if (e.Key is Key.F2 or Key.Enter) { e.Handled = true; await Perform(() => EditWatch((_watchList.SelectedItem as WatchRow)?.Node, false)); }
            else if (e.Key == Key.Insert) { e.Handled = true; await Perform(() => EditWatch(null, false)); }
            else if (e.Key == Key.Delete) { e.Handled = true; await Perform(RemoveWatch); }
        };
        var menu = new ContextMenu();
        menu.ItemsSource = new Control[]
        {
            ActionMenu("Edit…", () => EditWatch((_watchList.SelectedItem as WatchRow)?.Node, false)),
            ActionMenu("Add watch…", () => EditWatch(null, false)), ActionMenu("Add group…", () => EditWatch(null, true)),
            ActionMenu("Remove", RemoveWatch)
        };
        _watchList.ContextMenu = menu;
        Grid.SetRow(_watchList, 2); root.Children.Add(_watchList);
        _watchSampleLabel.Margin = new Thickness(8, 4); Grid.SetRow(_watchSampleLabel, 3); root.Children.Add(_watchSampleLabel);
        _watchView = root;
        LoadWatchDocument(_watchPath);
        return root;
    }
    private Control BuildWatchRow(WatchRow row)
    {
        var node = row.Node;
        var grid = new Grid { ColumnDefinitions = new("*,110,75"), ColumnSpacing = 6 };
        var name = new Grid { ColumnDefinitions = new("Auto,*"), Margin = new Thickness(row.Depth * 12, 0, 0, 0) };
        if (node.IsGroup)
        {
            var expand = new Button { Content = _expandedWatches.Contains(node.Id) ? "▾" : "▸", Padding = new Thickness(2, 0), Background = Brushes.Transparent, BorderThickness = new Thickness(0), Width = 20 };
            expand.Click += (_, e) => { e.Handled = true; if (!_expandedWatches.Add(node.Id)) _expandedWatches.Remove(node.Id); RebuildWatches(node.Id); };
            name.Children.Add(expand);
        }
        var label = new TextBlock { Text = node.Name, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, FontWeight = node.IsGroup ? FontWeight.SemiBold : FontWeight.Normal, FontSize = 12 };
        Grid.SetColumn(label, 1); name.Children.Add(label); grid.Children.Add(name);
        if (!node.IsGroup)
        {
            var value = new TextBlock { DataContext = row, FontFamily = FontFamily.Parse("Consolas"), FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            value.Bind(TextBlock.TextProperty, new Binding(nameof(WatchRow.ValueText)));
            value.Bind(ToolTip.TipProperty, new Binding(nameof(WatchRow.ValueDetail)));
            Grid.SetColumn(value, 1); grid.Children.Add(value);
            var type = new TextBlock { Text = node.Watch is { } watch ? WatchTypeLabel(watch) : "—", FontSize = 11, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            Grid.SetColumn(type, 2); grid.Children.Add(type);
        }
        else Grid.SetColumnSpan(name, 3);
        ToolTip.SetTip(grid, node.Name + (node.Watch is { } w ? $"\n0x{w.Address:X8}" + string.Concat((w.Offsets ?? []).Select(o => " → " + WatchMemory.OffsetText(o))) : "") + (node.Diagnostic is { } d ? "\n" + d : ""));
        return grid;
    }
    private static string WatchTypeLabel(WatchDefinition watch) => watch.Type switch
    { WatchType.Bytes => $"Bytes[{watch.Length}]", WatchType.Text => $"Text[{watch.Length}]", _ => watch.Type.ToString() };

    internal void LoadWatchDocument(string path)
    {
        _watchPath = path; _watchSaveBlocked = false;
        try { _watchDocument = File.Exists(path) ? WatchDocument.Load(path) : WatchDocument.Empty; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        { _watchDocument = WatchDocument.Empty; _watchSaveBlocked = true; _watchSampleLabel.Text = "Watch file could not be loaded: " + ex.Message; }
        _expandedWatches.Clear();
        foreach (var group in _watchDocument.Nodes.Where(n => n.IsGroup)) _expandedWatches.Add(group.Id);
        ++_watchVersion; _watchSample = null; RebuildWatches();
    }
    internal void StoreWatchesForProject(string path)
    {
        if (_watchSaveBlocked) return;
        var destination = path + ".watches.json";
        _watchDocument.Save(destination); _watchPath = destination;
    }
    private void DetachWatchDocument()
    {
        if (_watchSaveBlocked) return;
        _watchPath = Path.Combine(AppPaths.Data, "watches.json");
        _watchDocument.Save(_watchPath);
        ++_watchVersion; _watchSample = null; _watchRequestedGeneration = -1;
    }
    private void CommitWatches(WatchDocument document, Guid? selected = null)
    {
        if (_watchSaveBlocked) throw new InvalidOperationException("The existing watch file could not be read. Preserve or repair it before replacing its definitions.");
        document.Save(_watchPath); _watchDocument = document;
        ++_watchVersion; _watchSample = null; RebuildWatches(selected);
        if (_execution.HasProject && File.Exists(_recoveryPath))
        {
            try { document.Save(_recoveryPath + ".watches.json"); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { _watchSampleLabel.Text = "Watch definitions saved; recovery copy failed: " + ex.Message; }
        }
    }
    private void RebuildWatches(Guid? selected = null)
    {
        selected ??= (_watchList.SelectedItem as WatchRow)?.Node.Id;
        _watchRows.Clear();
        void Add(WatchNode[] nodes, int depth)
        {
            foreach (var node in nodes)
            {
                _watchRows.Add(new(node, depth));
                if (node.IsGroup && _expandedWatches.Contains(node.Id)) Add(node.Children!, depth + 1);
            }
        }
        Add(_watchDocument.Nodes, 0);
        _watchList.SelectedItem = _watchRows.FirstOrDefault(r => r.Node.Id == selected);
        _watchRequestedGeneration = -1;
    }
    private async Task EditWatch(WatchNode? node, bool group)
    {
        var adding = node is null;
        node ??= group ? WatchNode.Group("New group") : WatchNode.Entry("New watch", new(0x80000000));
        var parent = (_watchList.SelectedItem as WatchRow)?.Node;
        if (parent is { IsGroup: false }) parent = WatchDocument.Walk(_watchDocument.Nodes).FirstOrDefault(n => n.Children?.Any(c => c.Id == parent.Id) == true);
        await _execution.PauseAsync();
        var dialog = new WatchEditorDialog(node, adding, async watch =>
        {
            if (!_execution.IsLoaded) return null;
            var sample = await _execution.SampleWatchesAsync([(node.Id, watch)]);
            return sample.Generation == _execution.SampleGeneration ? sample.Values[0] : null;
        });
        _dialogOpen = true;
        try
        {
            var owner = TopLevel.GetTopLevel(_watchView!) as Window ?? this;
            if (await dialog.ShowDialog<WatchNode?>(owner) is not { } edited) return;
            var document = adding ? _watchDocument.Add(edited, parent?.IsGroup == true ? parent.Id : null) : _watchDocument.Replace(edited.Id, edited);
            if (parent?.IsGroup == true) _expandedWatches.Add(parent.Id);
            CommitWatches(document, edited.Id);
        }
        finally { _dialogOpen = false; }
    }
    private Task RemoveWatch()
    {
        if (_watchList.SelectedItem is WatchRow row) CommitWatches(_watchDocument.Replace(row.Node.Id, null));
        return Task.CompletedTask;
    }
    private async Task ImportWatches()
    {
        var path = await PickOpen("Import memory watches", new FilePickerFileType("Memory watches") { Patterns = ["*.dmw", "*.watches.json"] });
        if (path is null) return;
        var imported = await Task.Run(() =>
        {
            if (new FileInfo(path).Length > WatchDocument.MaximumFileBytes) throw new InvalidDataException("Watch file exceeds 8 MiB.");
            return Path.GetExtension(path).Equals(".dmw", StringComparison.OrdinalIgnoreCase) ? DmwImporter.Import(File.ReadAllText(path), path) : WatchDocument.Load(path);
        });
        // Fresh IDs allow importing the same collection as a separate group for comparison.
        WatchNode Copy(WatchNode node) => node with { Id = Guid.NewGuid(), Children = node.Children?.Select(Copy).ToArray() };
        var group = new WatchNode(Guid.NewGuid(), Path.GetFileNameWithoutExtension(path), Children: imported.Nodes.Select(Copy).ToArray());
        _expandedWatches.Add(group.Id);
        CommitWatches(_watchDocument.Add(group, null) with { Imports = [.. _watchDocument.Imports ?? [], .. imported.Imports ?? []] }, group.Id);
        var nodes = WatchDocument.Walk(imported.Nodes).ToArray();
        _watchSampleLabel.Text = $"Imported {nodes.Count(n => !n.IsGroup)} watches · {nodes.Count(n => n.Diagnostic != null)} unsupported";
    }
    private async Task RefreshWatches()
    {
        if (!_execution.IsLoaded) { _watchSampleLabel.Text = "No game loaded"; return; }
        await _execution.PauseAsync(); await SampleVisibleWatches();
    }
    private async Task SampleVisibleWatches()
    {
        if (_watchSampling || !_execution.IsLoaded) return;
        var request = _watchRows.Where(r => !r.Node.IsGroup && r.Node.Diagnostic is null && r.Node.Watch != null).Take(1024).Select(r => (r.Node.Id, r.Node.Watch!)).ToArray();
        if (request.Length == 0) return;
        _watchSampling = true; var version = _watchVersion;
        _watchRequestedGeneration = _execution.SampleGeneration;
        try
        {
            var sample = await _execution.SampleWatchesAsync(request);
            if (version != _watchVersion || sample.Generation != _execution.SampleGeneration || _closingApproved) return;
            _watchSample = sample;
            var values = sample.Values.ToDictionary(v => v.Id);
            foreach (var row in _watchRows)
                if (row.Node.Watch is { } watch && values.TryGetValue(row.Node.Id, out var value))
                {
                    row.SetValue(value.Error is null ? WatchMemory.Format(watch, value.Bytes) : "???",
                        value.Error ?? $"0x{value.Address:X8} · State {sample.Position}\n" + Convert.ToHexString(value.Bytes));
                }
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException) { _watchSampleLabel.Text = ex.Message; }
        finally { _watchSampling = false; }
    }
    private void RefreshWatcherStatus()
    {
        if (_watchSaveBlocked) return;
        if (_watchSample is { } sample)
            _watchSampleLabel.Text = $"State {sample.Position}" + (sample.Generation != _execution.SampleGeneration || !sample.Current ? " · stale" : "") + (_watchRows.Count(r => !r.Node.IsGroup) > 1024 ? " · first 1024 watches" : "");
        if (!_busy && !_dialogOpen && !_execution.IsRunning && _execution.IsLoaded && _watchView?.IsEffectivelyVisible == true &&
            TopLevel.GetTopLevel(_watchView) != null && _watchRequestedGeneration != _execution.SampleGeneration)
            _ = SampleVisibleWatches();
    }
    private void ToggleWatcher() => ShowWorkspacePanel("watcher");
}
