using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using TasStudio.Emulation;

namespace TasStudio.App;

public sealed partial class MainWindow
{
    private readonly TextBox _tagSearch = new()
    {
        Name = "TagSearch", Watermark = "Find tag…", Width = 170, MinHeight = 28,
        FontSize = 12, Padding = new Thickness(7, 3), VerticalAlignment = VerticalAlignment.Center
    };
    private readonly Popup _tagSearchPopup = new() { IsLightDismissEnabled = true };
    private readonly StackPanel _tagSearchResults = new() { Spacing = 2 };
    private readonly TextBlock _tagSearchSummary = new() { Name = "TagSearchSummary", FontSize = 11, Margin = new Thickness(7, 5), TextWrapping = TextWrapping.Wrap };
    private IReadOnlyList<TimelineTag> _searchedTags = [];
    private TimelineTag[] _tagMatches = [];
    private string? _searchedQuery;
    private int _tagMatchIndex = -1;

    private Control BuildTagSearch()
    {
        AutomationProperties.SetName(_tagSearch, "Find timeline tag");
        ToolTip.SetTip(_tagSearch, "Find tags by name. Enter: next match; Shift+Enter: previous. Selects the timeline without seeking the game. Ctrl+F from the timeline focuses search.");
        var container = new Grid { Margin = new Thickness(6, 0) };
        container.Children.Add(_tagSearch); container.Children.Add(_tagSearchPopup);
        _tagSearchPopup.PlacementTarget = _tagSearch;
        _tagSearchPopup.Placement = PlacementMode.BottomEdgeAlignedLeft;
        _tagSearchPopup.Child = new Border
        {
            Width = 300, Padding = new Thickness(4), Background = PanelBrush,
            BorderBrush = LineBrush, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4),
            Child = new StackPanel { Children =
            {
                new ScrollViewer { Content = _tagSearchResults, MaxHeight = 240,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }, _tagSearchSummary
            } }
        };
        _tagSearch.TextChanged += (_, _) => RefreshTagSearch();
        _tagSearch.GotFocus += (_, _) => { RefreshTagSearch(); ShowTagSearchResults(); };
        void CloseAfterFocusLeaves() => Dispatcher.UIThread.Post(() =>
        {
            if (!_tagSearch.IsKeyboardFocusWithin && !_tagSearchPopup.Child!.IsKeyboardFocusWithin)
                _tagSearchPopup.IsOpen = false;
        });
        _tagSearch.LostFocus += (_, _) => CloseAfterFocusLeaves();
        _tagSearchPopup.Child.LostFocus += (_, _) => CloseAfterFocusLeaves();
        _tagSearch.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            { e.Handled = true; _tagSearchPopup.IsOpen = false; _tagSearch.Clear(); return; }
            if (e.Key != Key.Enter && e.Key != Key.Down && e.Key != Key.Up) return;
            e.Handled = true;
            RefreshTagSearch();
            if (_tagMatches.Length == 0) return;
            var backwards = e.Key == Key.Up || e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            _tagMatchIndex = _tagMatchIndex < 0 ? (backwards ? _tagMatches.Length - 1 : 0) :
                (_tagMatchIndex + (backwards ? -1 : 1) + _tagMatches.Length) % _tagMatches.Length;
            NavigateToTag(_tagMatches[_tagMatchIndex]);
            ShowTagSearchResults();
        };
        _availability.Add((_tagSearch, () => _execution.HasProject && !_movieMode));
        return container;
    }

    private void RefreshTagSearch()
    {
        var query = _tagSearch.Text?.Trim() ?? "";
        var tags = _execution.Tags;
        if (_searchedQuery == query && (ReferenceEquals(_searchedTags, tags) || _searchedTags.SequenceEqual(tags))) return;
        _searchedQuery = query; _searchedTags = tags;
        _tagMatches = query.Length == 0 ? [] : tags.Where(tag => tag.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(tag => tag.Position).ThenBy(tag => tag.Name, StringComparer.OrdinalIgnoreCase).ThenBy(tag => tag.Id).ToArray();
        _tagMatchIndex = -1;
        _tagSearchResults.Children.Clear();
        // Keep the popup small even for very large projects; keyboard navigation visits every match.
        foreach (var tag in _tagMatches.Take(50))
        {
            var row = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 12 };
            row.Children.Add(new TextBlock { Text = tag.Name, TextTrimming = TextTrimming.CharacterEllipsis });
            var frame = new TextBlock { Text = $"Group {tag.Position:N0}", FontSize = 11, VerticalAlignment = VerticalAlignment.Center,
                Foreground = StudioTheme.Brush(ThemeColor.Muted) };
            Grid.SetColumn(frame, 1); row.Children.Add(frame);
            var result = new Button { Name = "TagSearchResult", Tag = tag.Id, Content = row, Padding = new Thickness(7, 5),
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
            AutomationProperties.SetName(result, $"{tag.Name}, frame group {tag.Position}");
            ToolTip.SetTip(result, tag.Name);
            result.Click += (_, _) => { NavigateToTag(tag); _tagSearchPopup.IsOpen = false; _timeline.Focus(); };
            _tagSearchResults.Children.Add(result);
        }
        _tagSearchSummary.Text = _tagMatches.Length == 0 ? "No matching tags" :
            $"{_tagMatches.Length:N0} {(_tagMatches.Length == 1 ? "match" : "matches")}{(_tagMatches.Length > 50 ? " · first 50 shown" : "")} · Enter / Shift+Enter to cycle";
        ShowTagSearchResults();
    }

    private void ShowTagSearchResults() => _tagSearchPopup.IsOpen = _execution.HasProject && !_movieMode &&
        _tagSearch.IsKeyboardFocusWithin && !string.IsNullOrWhiteSpace(_tagSearch.Text);

    private void NavigateToTag(TimelineTag tag)
    {
        _timeline.Tags = _execution.Tags;
        _timeline.SelectTag(tag.Id);
        _tagMatchIndex = Array.FindIndex(_tagMatches, t => t.Id == tag.Id);
        if (_tagMatchIndex >= 0) _tagSearchSummary.Text = $"{_tagMatchIndex + 1:N0} / {_tagMatches.Length:N0} · {tag.Name}";
    }
}
