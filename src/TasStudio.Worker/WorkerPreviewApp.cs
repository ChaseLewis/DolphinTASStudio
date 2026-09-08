using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using TasStudio.Emulation;

namespace TasStudio.Worker;

public sealed class WorkerPreviewApp : Application
{
    private ExperimentJob? _job;

    internal static int Run(ExperimentJob job) =>
        AppBuilder.Configure(() => new WorkerPreviewApp { _job = job })
            .UsePlatformDetect().StartWithClassicDesktopLifetime([]);

    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Dark;
        Styles.Add(new FluentTheme());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var job = _job ?? throw new InvalidOperationException("Missing trial job.");
            var window = new WorkerPreviewWindow(job, (showFrame, token) => TrialExecution.RunAsync(job, token, showFrame));
            window.Completed += code => desktop.Shutdown(code);
            desktop.MainWindow = window;
        }
        base.OnFrameworkInitializationCompleted();
    }
}
