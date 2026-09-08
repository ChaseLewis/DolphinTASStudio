using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using TasStudio.Core;
using Velopack;

namespace TasStudio.App;

internal static class Program
{
    internal static AppUpdates? Updates { get; private set; }
    internal static RuntimeActivity? Activity { get; private set; }
    internal static bool RestartForUpdate { get; set; }
    [STAThread]
    public static void Main(string[] args)
    {
        var launchedByUpdater = false;
        // Installer hooks must run before Avalonia, native code, or runtime activity locks.
        VelopackApp.Build().SetAutoApplyOnStartup(false)
            .OnFirstRun(_ => launchedByUpdater = true).OnRestarted(_ => launchedByUpdater = true).Run();
        try { Activity = RuntimeActivity.Enter(launchedByUpdater); }
        catch (IOException error)
        {
            if (OperatingSystem.IsWindows()) MessageBox(0, error.Message, "Dolphin TAS Studio", 0x40);
            else Console.Error.WriteLine(error.Message);
            return;
        }
        using (Activity)
        using (Updates = AppUpdates.Create())
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            // Saving, memory-card flushing, emulator disposal and window teardown are complete.
            Updates.ApplyAfterShutdown(Activity, RestartForUpdate);
        }
    }
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, EntryPoint = "MessageBoxW")]
    private static extern int MessageBox(nint owner, string text, string caption, uint type);
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<StudioApp>().UsePlatformDetect().LogToTrace();
}

public sealed class StudioApp : Application
{
    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Default;
        Styles.Add(StudioTheme.CreateFluentTheme());
        Styles.Add(new Dock.Avalonia.Themes.Fluent.DockFluentTheme());
        StudioTheme.Initialize(this);
        DataTemplates.Add(new Avalonia.Controls.Templates.FuncDataTemplate<StudioPanel>((panel, _) => panel == null ? null : new StudioPanelPresenter(panel)));
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new MainWindow(desktop.Args ?? []);
        base.OnFrameworkInitializationCompleted();
    }
}
