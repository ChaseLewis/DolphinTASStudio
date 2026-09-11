using System.Diagnostics;

namespace TasStudio.App;

internal static class CodeEditors
{
    private static readonly object Gate = new();
    private static Task<string?>? _automatic;

    public static async Task<string?> ResolveAsync(string? configured, bool refresh = false)
    {
        if (!string.IsNullOrWhiteSpace(configured)) return ExistingExecutable(configured);
        Task<string?> automatic;
        lock (Gate)
        {
            if (refresh || _automatic == null) _automatic = Task.Run(Detect);
            automatic = _automatic;
        }
        var path = await automatic;
        return path == null ? null : ExistingExecutable(path);
    }

    internal static string? Select(string? configured, IEnumerable<string> codeCandidates, Func<string?> findVisualStudio)
    {
        if (!string.IsNullOrWhiteSpace(configured)) return ExistingExecutable(configured);
        return codeCandidates.Select(ExistingExecutable).FirstOrDefault(p => p != null) ?? findVisualStudio();
    }

    public static string Validate(string path) => ExistingExecutable(path)
        ?? throw new InvalidDataException("Select an existing editor executable (.exe).");

    private static string? ExistingExecutable(string path)
    {
        try
        {
            path = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim().Trim('"')));
            return Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(path) ? path : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) { return null; }
    }

    private static string? Detect() => Select(null, CodeCandidates(), FindVisualStudio);

    private static IEnumerable<string> CodeCandidates()
    {
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Microsoft VS Code", "Code.exe");
        foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
            yield return Path.Combine(root, "Microsoft VS Code", "Code.exe");
        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var root = folder.Trim().Trim('"');
            yield return Path.Combine(root, "Code.exe");
            // VS Code usually adds its bin/ launcher directory to PATH.
            yield return Path.Combine(root, "..", "Code.exe");
        }
    }

    private static string? FindVisualStudio()
    {
        var vswhere = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft Visual Studio", "Installer", "vswhere.exe");
        if (File.Exists(vswhere))
        {
            try
            {
                using var process = new Process { StartInfo = new(vswhere)
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true } };
                foreach (var arg in new[] { "-latest", "-products", "*", "-requires", "Microsoft.VisualStudio.Component.CoreEditor", "-find", "Common7\\IDE\\devenv.exe" })
                    process.StartInfo.ArgumentList.Add(arg);
                process.Start();
                var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(3000)) { process.Kill(); process.WaitForExit(); }
                else if (process.ExitCode == 0)
                {
                    var found = output.GetAwaiter().GetResult().Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                        .Select(ExistingExecutable).FirstOrDefault(p => p != null);
                    if (found != null) return found;
                }
            }
            catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException) { }
        }
        var install = Environment.GetEnvironmentVariable("VSINSTALLDIR");
        if (!string.IsNullOrWhiteSpace(install) && ExistingExecutable(Path.Combine(install, "Common7", "IDE", "devenv.exe")) is { } installed) return installed;
        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            if (ExistingExecutable(Path.Combine(folder.Trim().Trim('"'), "devenv.exe")) is { } found) return found;
        return null;
    }

    internal static ProcessStartInfo StartInfo(string executable, string folder)
    {
        executable = Validate(executable); folder = Path.GetFullPath(folder);
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException("The experiment folder no longer exists.");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = folder };
        // Both Code.exe and devenv.exe accept a folder. ArgumentList preserves spaces and shell characters.
        start.ArgumentList.Add(folder);
        return start;
    }

    public static void Open(string executable, string folder) => Process.Start(StartInfo(executable, folder));
}
