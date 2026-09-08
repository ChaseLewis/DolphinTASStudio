using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace TasStudio.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<StudioApp>().UsePlatformDetect().LogToTrace();
}

public sealed class StudioApp : Application
{
    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Dark;
        Styles.Add(new FluentTheme());
        Styles.Add(new Dock.Avalonia.Themes.Fluent.DockFluentTheme());
        DataTemplates.Add(new Avalonia.Controls.Templates.FuncDataTemplate<StudioPanel>((panel, _) => panel == null ? null : new StudioPanelPresenter(panel)));
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new MainWindow(desktop.Args ?? []);
        base.OnFrameworkInitializationCompleted();
    }
}
