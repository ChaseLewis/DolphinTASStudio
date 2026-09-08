using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace TasStudio.Core;

/// <summary>Keeps application replacement out of active Studio and worker sessions.</summary>
public sealed class RuntimeActivity : IDisposable
{
    private readonly string _directory;
    private readonly Func<bool> _updaterRunning;
    private FileStream? _lease;

    public static string UpdateDirectory(string applicationDirectory) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TasStudio", "Updates",
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            Path.GetFullPath(applicationDirectory).TrimEnd('\\', '/').ToUpperInvariant())))[..24]);

    public static RuntimeActivity Enter(bool launchedByUpdater = false) => new(
        UpdateDirectory(AppContext.BaseDirectory),
        () => IsUpdaterRunning(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Update.exe"))),
        launchedByUpdater);

    // The injectable process probe also allows lock handoff to be tested without installing anything.
    public RuntimeActivity(string directory, Func<bool> updaterRunning, bool launchedByUpdater = false)
    {
        _directory = directory;
        _updaterRunning = updaterRunning;
        Directory.CreateDirectory(directory);
        using var gate = Gate();
        if (!launchedByUpdater && updaterRunning())
            throw new IOException("Studio is being updated. Wait for the update to finish, then launch it again.");
        _lease = OpenLease(FileShare.ReadWrite);
    }

    public bool CanApply()
    {
        using var gate = Gate();
        return WithExclusiveLease(null);
    }

    /// <summary>Call only after the UI and emulator have finished shutting down.</summary>
    public bool TryStartUpdate(Action startUpdater)
    {
        using var gate = Gate();
        return WithExclusiveLease(startUpdater);
    }

    private bool WithExclusiveLease(Action? startUpdater)
    {
        if (_updaterRunning()) return false;
        _lease?.Dispose(); _lease = null;
        try
        {
            FileStream exclusive;
            try { exclusive = OpenLease(FileShare.None); }
            catch (IOException) { return false; }
            using (exclusive) startUpdater?.Invoke();
            return true;
        }
        finally { _lease = OpenLease(FileShare.ReadWrite); }
    }

    private FileStream OpenLease(FileShare share) => new(Path.Combine(_directory, "active.lock"),
        FileMode.OpenOrCreate, FileAccess.ReadWrite, share);

    private FileStream Gate()
    {
        var timer = Stopwatch.StartNew();
        while (true)
        {
            try { return new FileStream(Path.Combine(_directory, "gate.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (timer.Elapsed < TimeSpan.FromSeconds(5)) { Thread.Sleep(25); }
        }
    }

    private static bool IsUpdaterRunning(string updaterPath)
    {
        foreach (var process in Process.GetProcessesByName("Update"))
        {
            using (process)
            {
                try
                {
                    if (string.Equals(process.MainModule?.FileName, updaterPath, StringComparison.OrdinalIgnoreCase)) return true;
                }
                catch (InvalidOperationException) { /* Already exited. */ }
                catch (System.ComponentModel.Win32Exception) { return true; } // Cannot establish that replacement is safe.
            }
        }
        return false;
    }

    public void Dispose() { _lease?.Dispose(); _lease = null; }
}
