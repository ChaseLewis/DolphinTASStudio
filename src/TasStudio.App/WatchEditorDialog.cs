using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using TasStudio.Core;

namespace TasStudio.App;

internal sealed class WatchEditorDialog : Window
{
    private readonly WatchNode _original;
    internal readonly TextBox NameEditor = new();
    internal readonly TextBox AddressEditor = new();
    internal readonly ComboBox TypeEditor = new() { ItemsSource = Enum.GetValues<WatchType>() };
    internal readonly NumericUpDown LengthEditor = new() { Minimum = 1, Maximum = 4096, Value = 1, Width = 115, FormatString = "0" };
    internal readonly CheckBox PointerEditor = new() { Content = "This is a pointer" };
    private readonly List<TextBox> _offsets = [];
    private readonly List<TextBlock> _resolved = [];
    private readonly StackPanel _offsetRows = new() { Spacing = 5 };
    private readonly TextBlock _preview = new() { Text = "???", TextWrapping = TextWrapping.Wrap, MaxHeight = 65 };
    private readonly TextBlock _error = new() { Foreground = StudioTheme.Brush(ThemeColor.Error), TextWrapping = TextWrapping.Wrap };
    private readonly DispatcherTimer _previewTimer = new() { Interval = TimeSpan.FromMilliseconds(220) };
    private bool _closed;
    private long _editVersion;
    private bool _previewBusy;
    internal WatchNode ResultDefinition()
    {
        if (string.IsNullOrWhiteSpace(NameEditor.Text)) throw new FormatException("Enter a label.");
        var name = NameEditor.Text.Trim();
        if (name.Length > 512) throw new FormatException("Label must be at most 512 characters.");
        if (_original.IsGroup) return _original with { Name = name };
        var definition = new WatchDefinition(WatchMemory.ParseAddress(AddressEditor.Text ?? ""),
            (WatchType)(TypeEditor.SelectedItem ?? WatchType.U32), (int)(LengthEditor.Value ?? 1),
            PointerEditor.IsChecked == true ? _offsets.Select(t => WatchMemory.ParseOffset(t.Text ?? "")).ToArray() : [],
            _original.Watch?.Display ?? WatchDisplay.Auto);
        definition.Validate();
        return _original with { Name = name, Watch = definition, Diagnostic = null };
    }
    internal WatchEditorDialog(WatchNode original, bool adding, Func<WatchDefinition, Task<WatchValue?>> preview)
    {
        _original = original;
        Title = (adding ? "Add " : "Edit ") + (original.IsGroup ? "group" : "watch");
        Background = StudioTheme.Brush(ThemeColor.Panel);
        Width = 440; MinWidth = 360; SizeToContent = SizeToContent.Height; CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var body = new StackPanel { Margin = new Thickness(14), Spacing = 8 };
        NameEditor.Text = original.Name;
        body.Children.Add(Field("Label", NameEditor));
        if (!original.IsGroup)
        {
            var watch = original.Watch ?? new WatchDefinition(0x80000000);
            AddressEditor.Text = watch.Address.ToString("X8"); TypeEditor.SelectedItem = watch.Type; LengthEditor.Value = watch.Length;
            TypeEditor.ItemTemplate = new FuncDataTemplate<WatchType>((t, _) => new TextBlock { Text = t switch
            {
                WatchType.U8 => "Byte (unsigned)", WatchType.S8 => "Byte (signed)",
                WatchType.U16 => "2 bytes (unsigned)", WatchType.S16 => "2 bytes (signed)",
                WatchType.U32 => "4 bytes (unsigned)", WatchType.S32 => "4 bytes (signed)",
                WatchType.U64 => "8 bytes (unsigned)", WatchType.S64 => "8 bytes (signed)",
                WatchType.Float32 => "Float", WatchType.Float64 => "Double", WatchType.Bytes => "Array of bytes", _ => "Text (UTF-8)"
            } });
            body.Children.Add(Field("Preview", _preview));
            body.Children.Add(Field("Address", AddressEditor));
            var types = new Grid { ColumnDefinitions = new("*,Auto,Auto"), ColumnSpacing = 6 };
            var lengthLabel = new TextBlock { Text = "Length", FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
            LengthEditor.Width = 105;
            types.Children.Add(TypeEditor); Grid.SetColumn(lengthLabel, 1); types.Children.Add(lengthLabel);
            Grid.SetColumn(LengthEditor, 2); types.Children.Add(LengthEditor);
            ToolTip.SetTip(LengthEditor, "Length in bytes");
            body.Children.Add(Field("Type", types)); body.Children.Add(PointerEditor);
            var offsets = new StackPanel { Spacing = 6 };
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            var add = new Button { Content = "Add offset", Padding = new Thickness(8, 3) };
            var remove = new Button { Content = "Remove offset", Padding = new Thickness(8, 3) };
            actions.Children.Add(add); actions.Children.Add(remove); offsets.Children.Add(actions);
            offsets.Children.Add(new ScrollViewer { MaxHeight = 260, Content = _offsetRows }); body.Children.Add(offsets);
            void AddOffset(int value)
            {
                var text = new TextBox { Text = WatchMemory.OffsetText(value), MinHeight = 28 };
                var resolved = new TextBlock { Text = "→ ???", VerticalAlignment = VerticalAlignment.Center, FontSize = 11 };
                var row = new Grid { ColumnDefinitions = new("Auto,*,Auto"), ColumnSpacing = 7 };
                row.Children.Add(new TextBlock { Text = $"Level {_offsets.Count + 1}", VerticalAlignment = VerticalAlignment.Center });
                Grid.SetColumn(text, 1); row.Children.Add(text); Grid.SetColumn(resolved, 2); row.Children.Add(resolved);
                _offsets.Add(text); _resolved.Add(resolved); _offsetRows.Children.Add(row);
                text.TextChanged += (_, _) => Changed();
            }
            foreach (var offset in watch.Offsets is { Length: > 0 } values ? values : [0]) AddOffset(offset);
            PointerEditor.IsChecked = watch.Offsets is { Length: > 0 };
            void UpdateFields()
            {
                LengthEditor.IsVisible = TypeEditor.SelectedItem is WatchType.Bytes or WatchType.Text;
                lengthLabel.IsVisible = LengthEditor.IsVisible;
                offsets.IsVisible = PointerEditor.IsChecked == true;
                add.IsEnabled = _offsets.Count < 16; remove.IsEnabled = _offsets.Count > 1;
            }
            add.Click += (_, _) => { if (_offsets.Count < 16) AddOffset(0); UpdateFields(); Changed(); };
            remove.Click += (_, _) =>
            {
                if (_offsets.Count <= 1) return;
                _offsets.RemoveAt(_offsets.Count - 1); _resolved.RemoveAt(_resolved.Count - 1); _offsetRows.Children.RemoveAt(_offsetRows.Children.Count - 1);
                UpdateFields(); Changed();
            };
            TypeEditor.SelectionChanged += (_, _) => { UpdateFields(); Changed(); };
            PointerEditor.IsCheckedChanged += (_, _) => { UpdateFields(); Changed(); };
            AddressEditor.TextChanged += (_, _) => Changed(); LengthEditor.ValueChanged += (_, _) => Changed();
            UpdateFields();
        }
        body.Children.Add(_error);
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        var ok = new Button { Content = "OK", MinWidth = 75, IsDefault = true };
        var cancel = new Button { Content = "Cancel", MinWidth = 75, IsCancel = true };
        footer.Children.Add(ok); footer.Children.Add(cancel); body.Children.Add(footer);
        ok.Click += (_, _) => { try { Close(ResultDefinition()); } catch (Exception ex) when (ex is FormatException or OverflowException or InvalidDataException) { _error.Text = ex.Message; } };
        cancel.Click += (_, _) => Close(null);
        Content = body;
        NameEditor.TextChanged += (_, _) => Changed();
        _previewTimer.Tick += async (_, _) =>
        {
            if (_previewBusy) return;
            _previewTimer.Stop(); var version = _editVersion; _previewBusy = true;
            try
            {
                var definition = ResultDefinition().Watch;
                if (definition is null) return;
                var sample = await preview(definition);
                if (_closed || version != _editVersion) return;
                _preview.Text = sample is { Error: null } ? WatchMemory.Format(definition, sample.Bytes) : "???";
                ToolTip.SetTip(_preview, sample?.Error);
                for (var i = 0; i < _resolved.Count; i++) _resolved[i].Text = sample?.Hops.ElementAtOrDefault(i) is { } hop ? $"→ {hop.ResolvedAddress:X8}" : "→ ???";
            }
            catch (Exception ex) when (ex is FormatException or OverflowException or InvalidDataException or InvalidOperationException)
            { _preview.Text = "???"; ToolTip.SetTip(_preview, ex.Message); }
            finally { _previewBusy = false; }
        };
        Opened += (_, _) => { NameEditor.Focus(); Changed(); };
        Closed += (_, _) => { _closed = true; _previewTimer.Stop(); };
    }
    private void Changed()
    {
        ++_editVersion; _error.Text = ""; _preview.Text = "???";
        foreach (var label in _resolved) label.Text = "→ ???";
        _previewTimer.Stop(); _previewTimer.Start();
    }
    private static Grid Field(string label, Control control)
    {
        var row = new Grid { ColumnDefinitions = new("60,*"), ColumnSpacing = 8 };
        row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(control, 1); row.Children.Add(control); return row;
    }
}
