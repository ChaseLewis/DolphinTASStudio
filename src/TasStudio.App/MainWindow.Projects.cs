using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using TasStudio.Core;
using TasStudio.Emulation;

namespace TasStudio.App;

public sealed partial class MainWindow
{
    private Control _editorToolbar = null!, _editorWorkspace = null!, _homeToolbar = null!, _projectHome = null!;
    private readonly ListBox _recentProjects = new() { Background = Brushes.Transparent };
    private readonly StackPanel _projectDetails = new() { Spacing = 16 };
    private readonly Dictionary<string, string> _verification = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Bitmap> _banners = [];
    private readonly HashSet<string> _bannerAttempts = [];
    private readonly HashSet<Window> _homeHiddenWindows = [];
    private bool _homeVisible = true;
    private string? _lastRecentPath;
    private CancellationTokenSource? _verificationCancellation;
    private readonly Button _cancelVerification = new() { Content = "Cancel verification", IsVisible = false, VerticalAlignment = VerticalAlignment.Center };

    private Control BuildProjectHome()
    {
        var root = new Grid { RowDefinitions = new("Auto,*"), Margin = new Thickness(32, 24) };
        var heading = new Grid { ColumnDefinitions = new("*,Auto"), Margin = new Thickness(0, 0, 0, 28) };
        heading.Children.Add(new TextBlock { Text = "Your projects", FontSize = 26, FontWeight = FontWeight.SemiBold });
        var create = ActionButton("+  New project", NewProject); Grid.SetColumn(create, 1); heading.Children.Add(create); root.Children.Add(heading);
        var body = new Grid { ColumnDefinitions = new("3*,2*"), ColumnSpacing = 26 };
        var left = new Grid { RowDefinitions = new("Auto,*") };
        _recentProjects.Styles.Add(new Style(s => s.OfType<ListBoxItem>()) { Setters =
        {
            new Setter(BackgroundProperty, PanelBrush), new Setter(MarginProperty, new Thickness(0, 0, 0, 8)),
            new Setter(TemplatedControl.BorderBrushProperty, LineBrush), new Setter(TemplatedControl.BorderThicknessProperty, new Thickness(1)),
            new Setter(TemplatedControl.CornerRadiusProperty, new CornerRadius(5))
        } });
        _recentProjects.Styles.Add(new Style(s => s.OfType<ListBoxItem>().Class(":selected")) { Setters =
        {
            new Setter(BackgroundProperty, Brush.Parse("#17394B")), new Setter(TemplatedControl.BorderBrushProperty, Brush.Parse("#5AC8FA"))
        } });
        left.Children.Add(new TextBlock { Text = "RECENTLY OPENED", FontSize = 11, Foreground = Brushes.LightSteelBlue, Margin = new Thickness(0, 0, 0, 12) });
        _recentProjects.ItemTemplate = new FuncDataTemplate<RecentProject>((project, _) =>
        {
            if (project == null) return new Border();
            var row = new Grid { ColumnDefinitions = new("104,*,Auto"), MinHeight = 78, Margin = new Thickness(8) };
            Control art = _banners.TryGetValue(project.GameHash, out var banner) ? new Image { Source = banner, Width = 96, Height = 32 } :
                new Border { Width = 96, Height = 32, Background = LineBrush, Child = new TextBlock { Text = "GAMECUBE", FontSize = 10, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
            row.Children.Add(art);
            var labels = new StackPanel { Spacing = 5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0) };
            labels.Children.Add(new TextBlock { Text = project.Name, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            labels.Children.Add(new TextBlock { Text = Path.GetFileNameWithoutExtension(project.GamePath), FontSize = 12, Foreground = Brushes.LightSteelBlue, TextTrimming = TextTrimming.CharacterEllipsis });
            var checkedStatus = _verification.GetValueOrDefault(project.Path, "Not checked");
            labels.Children.Add(new TextBlock { Text = checkedStatus.StartsWith("Needs attention:") ? "Needs attention" : checkedStatus, FontSize = 11, TextWrapping = TextWrapping.Wrap });
            Grid.SetColumn(labels, 1); row.Children.Add(labels);
            var date = new TextBlock { Text = project.OpenedUtc.LocalDateTime.ToString("MMM d"), FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.LightSteelBlue };
            Grid.SetColumn(date, 2); row.Children.Add(date); return row;
        });
        _recentProjects.SelectionChanged += (_, _) => ShowProjectDetails();
        _recentProjects.DoubleTapped += async (_, _) => { if (_recentProjects.SelectedItem is RecentProject p) await Perform(() => OpenProjectPath(p.Path)); };
        Grid.SetRow(_recentProjects, 1); left.Children.Add(_recentProjects); body.Children.Add(left);
        var detail = new Border { Background = PanelBrush, BorderBrush = LineBrush, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(22), VerticalAlignment = VerticalAlignment.Top, Child = _projectDetails };
        var detailsScroll = new ScrollViewer { Content = detail };
        Grid.SetColumn(detailsScroll, 1); body.Children.Add(detailsScroll); Grid.SetRow(body, 1); root.Children.Add(body);
        RefreshProjectList(); return root;
    }
    private void RefreshProjectList()
    {
        var selected = (_recentProjects.SelectedItem as RecentProject)?.Path ?? _lastRecentPath;
        _recentProjects.ItemsSource = _settings.RecentProjects.ToArray();
        _recentProjects.SelectedItem = _settings.RecentProjects.FirstOrDefault(p => p.Path == selected) ?? _settings.RecentProjects.FirstOrDefault();
        ShowProjectDetails();
    }
    private void ShowProjectDetails()
    {
        _projectDetails.Children.Clear();
        if (_recentProjects.SelectedItem is not RecentProject p)
        {
            _projectDetails.Children.Add(new TextBlock { Text = "Start a new timeline", FontSize = 20 });
            _projectDetails.Children.Add(ProjectButton("New project", NewProject));
            _projectDetails.Children.Add(ProjectButton("Open existing project…", OpenProject));
            _projectDetails.Children.Add(ProjectButton("Recover inputs…", RecoverInputs)); return;
        }
        _lastRecentPath = p.Path;
        _projectDetails.Children.Add(new TextBlock { Text = p.Name, FontSize = 20, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
        _projectDetails.Children.Add(new TextBlock { Text = Path.GetFileNameWithoutExtension(p.GamePath), Foreground = Brushes.LightSteelBlue, TextWrapping = TextWrapping.Wrap });
        if (p.InputCount > 0) _projectDetails.Children.Add(ProjectTimelinePreview(p));
        void Detail(string title, string value)
        {
            var grid = new Grid { ColumnDefinitions = new("110,*") };
            grid.Children.Add(new TextBlock { Text = title, FontSize = 12, Foreground = Brushes.LightSteelBlue });
            var text = new TextBlock { Text = value, FontSize = 12, TextWrapping = TextWrapping.Wrap }; Grid.SetColumn(text, 1); grid.Children.Add(text); _projectDetails.Children.Add(grid);
        }
        Detail("Last saved", $"Frame {p.Position:N0}"); Detail("Timeline", $"{p.InputCount:N0} inputs · {p.TakeCount} candidate takes");
        Detail("Starting point", p.StartLabel); Detail("Project folder", Path.GetDirectoryName(p.Path)!);
        Detail("Verification", _verification.GetValueOrDefault(p.Path, "Not checked"));
        _projectDetails.Children.Add(ProjectButton("Open project", () => OpenProjectPath(p.Path)));
        var actions = new WrapPanel();
        actions.Children.Add(ProjectButton("Verify", () => VerifyProjects([p])));
        actions.Children.Add(ProjectButton("Remove from recent", () => { _settings.RecentProjects.Remove(p); _settings.Save(); RefreshProjectList(); return Task.CompletedTask; }));
        _projectDetails.Children.Add(actions);
    }
    private Button ProjectButton(string label, Func<Task> action)
    {
        var button = new Button { Content = label, Margin = new Thickness(0, 0, 6, 0) };
        button.Click += async (_, _) => await Perform(action);
        return button;
    }
    private static Control ProjectTimelinePreview(RecentProject project)
    {
        var takes = (project.TakeSpans ?? []).Take(4).ToArray();
        var canvas = new Canvas { Width = 360, Height = 32 + takes.Length * 20, Background = Brush.Parse("#15191F") };
        var length = Math.Max(1L, Math.Max(project.InputCount, takes.Select(t => (long)t.Start + t.Length).DefaultIfEmpty().Max()));
        void Row(string name, long start, long count, int row, IBrush color)
        {
            var label = new TextBlock { Text = name, FontSize = 10, Width = 58, TextTrimming = TextTrimming.CharacterEllipsis };
            Canvas.SetLeft(label, 8); Canvas.SetTop(label, 8 + row * 20); canvas.Children.Add(label);
            var track = new Border { Width = 280, Height = 12, Background = LineBrush };
            Canvas.SetLeft(track, 72); Canvas.SetTop(track, 8 + row * 20); canvas.Children.Add(track);
            var span = new Border { Width = Math.Max(1, 280d * count / length), Height = 12, Background = color, CornerRadius = new CornerRadius(2) };
            Canvas.SetLeft(span, 72 + 280d * start / length); Canvas.SetTop(span, 8 + row * 20); canvas.Children.Add(span);
        }
        Row("Active", 0, project.InputCount, 0, Brush.Parse("#5AC8FA"));
        for (var i = 0; i < takes.Length; i++) Row(takes[i].Name, takes[i].Start, takes[i].Length, i + 1, Brush.Parse("#467E98"));
        var playhead = new Border { Width = 1, Height = canvas.Height - 8, Background = Brushes.White };
        Canvas.SetLeft(playhead, 72 + 280d * Math.Min(project.Position, (ulong)length) / length); Canvas.SetTop(playhead, 4); canvas.Children.Add(playhead);
        return new Viewbox { Child = canvas, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Stretch, MaxHeight = 112 };
    }
    internal async Task ShowProjects()
    {
        await _execution.PauseAsync(); _audio.Flush(); await SaveRecoveryNow();
        SetProjectHome(true); RefreshProjectList(); await LoadProjectBanners();
    }
    internal void ShowEditor() => SetProjectHome(false);
    private void SetProjectHome(bool visible)
    {
        _homeVisible = visible;
        _projectHome.IsVisible = _homeToolbar.IsVisible = visible;
        _editorWorkspace.IsVisible = _editorToolbar.IsVisible = !visible;
        _position.IsVisible = !visible;
        if (visible)
        {
            foreach (var window in _workspaceWindows.Where(w => w != this && w.IsVisible).ToArray())
            { _homeHiddenWindows.Add(window); window.Hide(); }
        }
        else
        {
            foreach (var window in _homeHiddenWindows.Where(_workspaceWindows.Contains).ToArray()) window.Show();
            _homeHiddenWindows.Clear();
        }
        UpdateInputAcceptance();
    }
    private async Task RememberProject(string path, FolderProjectContent? content = null)
    {
        content ??= await Task.Run(() => FolderProject.Load(path));
        var recent = RecentProject.From(path, content);
        _settings.RecentProjects.RemoveAll(p => p.Path.Equals(recent.Path, StringComparison.OrdinalIgnoreCase));
        _settings.RecentProjects.Insert(0, recent);
        _settings.RecentProjects = _settings.RecentProjects.Take(50).ToList();
        _verification.Remove(path); _settings.Save(); _lastRecentPath = path; RefreshProjectList();
    }
    private async Task VerifyProjects(IEnumerable<RecentProject> projects)
    {
        // ROMs shared by projects are hashed once in this verification pass, never trusted from a previous pass.
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var cancellation = new CancellationTokenSource(); _verificationCancellation = cancellation; _cancelVerification.IsVisible = true;
        try
        {
        var identity = await _execution.InspectBackendIdentityAsync(BackendOptions());
        foreach (var p in projects.ToArray())
        {
            _verification[p.Path] = "Checking…"; RefreshProjectList();
            try
            {
                var content = await ProjectVerification.VerifyAsync(p.Path, identity, _settings.ResolvedRoms, hashes, cancellation.Token);
                var index = _settings.RecentProjects.FindIndex(r => r.Path == p.Path);
                if (index >= 0) _settings.RecentProjects[index] = RecentProject.From(p.Path, content) with { OpenedUtc = p.OpenedUtc };
                _verification[p.Path] = "Files, ROM & emulator build verified";
            }
            catch (OperationCanceledException) { _verification[p.Path] = "Check canceled"; RefreshProjectList(); break; }
            catch (Exception ex) { _verification[p.Path] = "Needs attention: " + ex.Message; }
            RefreshProjectList();
        }
        _settings.Save(); _status.Text = "Verification finished. Runtime compatibility is checked when opening a project.";
        await LoadProjectBanners();
        }
        finally { _verificationCancellation = null; _cancelVerification.IsVisible = false; }
    }
    private async Task LoadProjectBanners()
    {
        foreach (var p in _settings.RecentProjects.ToArray())
        {
            if (!_bannerAttempts.Add(p.GameHash)) continue;
            try
            {
                var cache = Path.Combine(AppPaths.Data, "Banners", Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(p.GameHash))) + ".png");
                if (File.Exists(cache)) { _banners[p.GameHash] = new Bitmap(cache); continue; }
                var rom = _settings.ResolvedRoms.GetValueOrDefault(p.GameHash) ?? FolderProject.ResolveRomPath(p.Path, p.GamePath);
                var pixels = await Task.Run(() => GameCubeBanner.ReadRgba(rom));
                if (pixels == null) continue;
                using var bitmap = new WriteableBitmap(new PixelSize(96, 32), new Vector(96, 96), PixelFormat.Rgba8888, AlphaFormat.Unpremul);
                using (var buffer = bitmap.Lock())
                    for (var y = 0; y < 32; y++) Marshal.Copy(pixels, y * 96 * 4, buffer.Address + y * buffer.RowBytes, 96 * 4);
                Directory.CreateDirectory(Path.GetDirectoryName(cache)!); bitmap.Save(cache);
                _banners[p.GameHash] = new Bitmap(cache);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException or OverflowException) { /* A missing banner never blocks a project. */ }
        }
        RefreshProjectList();
    }
}
