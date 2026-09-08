using System.Security.Cryptography;

namespace TasStudio.Emulation;

internal static class ExperimentResume
{
    internal static void CheckRuntime(string batch, string worker, string core, bool resume, Action<string>? progress)
    {
        var root = Path.GetDirectoryName(Path.GetFullPath(worker))!;
        var paths = new Dictionary<string, string>
        {
            ["dolphin_libretro.dll"] = core,
            ["TasStudio.LibretroHost.dll"] = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(core))!, "TasStudio.LibretroHost.dll")
        };
        foreach (var name in new[] { "TasStudio.Core.dll", "TasStudio.Sdk.dll", "TasStudio.Emulation.dll", "TasStudio.Dolphin.dll" })
            paths[name] = Path.Combine(root, name);
        var manifest = Path.Combine(batch, "runtime-hashes.json");
        if (!resume)
        {
            ExperimentFiles.Write(manifest, paths.Where(p => File.Exists(p.Value)).ToDictionary(p => p.Key,
                p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p.Value)))));
            return;
        }
        if (!File.Exists(manifest))
        {
            progress?.Invoke("This older batch has no runtime fingerprint. Resume using the same emulator build that started it.");
            return;
        }
        foreach (var (name, expected) in ExperimentFiles.Read<Dictionary<string, string>>(manifest))
            if (!paths.TryGetValue(name, out var file) || !File.Exists(file) ||
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))) != expected)
                throw new InvalidDataException("The emulator runtime differs from this batch: " + name + ". Use its original build or start a new batch.");
    }

    internal static void ValidateSnapshot(string batchDirectory, string sourceProject)
    {
        if (!File.Exists(sourceProject)) throw new FileNotFoundException("The batch's saved source project is missing.", sourceProject);
        if (!File.Exists(Path.Combine(batchDirectory, "results.sqlite")))
            throw new InvalidDataException("This batch has no SQLite results database to resume.");
        var root = Path.GetFullPath(Path.Combine(batchDirectory, "assembly"));
        var hashes = ExperimentFiles.Read<Dictionary<string, string>>(Path.Combine(root, "assembly-manifest.json"));
        foreach (var (relative, expected) in hashes)
        {
            var file = Path.GetFullPath(Path.Combine(root, relative));
            if (!file.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(file) || Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))) != expected)
                throw new InvalidDataException("The batch's compiled experiment has changed or is missing: " + relative);
        }
    }

    internal static Dictionary<int, ExperimentResult> ReadFinished(string batchDirectory, int count, Dictionary<int, ExperimentResult> stored)
    {
        if (stored.Keys.Any(index => index < 0 || index >= count))
            throw new InvalidDataException("SQLite contains trial indices outside this batch's saved definition.");
        var finished = new Dictionary<int, ExperimentResult>();
        for (var index = 0; index < count; index++)
        {
            var directory = Path.Combine(batchDirectory, $"run-{index + 1:00000}");
            var canonical = Path.Combine(directory, "result.json");
            if (stored.TryGetValue(index, out var committed) && IsFinished(committed.Status))
            {
                finished.Add(index, committed);
                continue;
            }

            // A worker can finish before the coordinator commits its SQLite transaction.
            // Recover that receipt instead of executing the trial twice.
            var candidates = new List<string> { canonical };
            var attempts = Path.Combine(directory, "attempts");
            if (Directory.Exists(attempts))
                candidates.AddRange(Directory.EnumerateDirectories(attempts).Select(p => Path.Combine(p, "result.json")));
            var recovered = candidates.Select(file => ReadResult(file, index))
                .Where(result => result != null && IsFinished(result.Status))
                .OrderByDescending(result => result!.Started).FirstOrDefault();
            if (recovered != null)
            {
                ExperimentFiles.Write(canonical, recovered);
                finished.Add(index, recovered);
            }
        }
        return finished;
    }

    private static bool IsFinished(string? status) => status is "completed" or "failed" or "timed out";

    private static ExperimentResult? ReadResult(string path, int index)
    {
        if (!File.Exists(path)) return null;
        var result = ExperimentFiles.Read<ExperimentResult>(path);
        if (result.Index != index) throw new InvalidDataException("Trial result index mismatch: " + path);
        return result;
    }
}
