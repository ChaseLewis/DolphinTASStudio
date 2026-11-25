using System.IO.Compression;
using System.Text.Json;

namespace TasStudio.Emulation;

internal static class CompressedJson
{
    internal static byte[] Compress(byte[] bytes)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true)) gzip.Write(bytes);
        return output.ToArray();
    }

    internal static Stream OpenRead(string path)
    {
        var file = File.OpenRead(path);
        return path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) ? new GZipStream(file, CompressionMode.Decompress) : file;
    }

    internal static byte[] ReadBytes(string path, int maximum = 64 * 1024 * 1024)
    {
        using var stream = OpenRead(path); using var output = new MemoryStream();
        var buffer = new byte[81920]; int count;
        while ((count = stream.Read(buffer)) > 0)
        {
            if (output.Length + count > maximum) throw new InvalidDataException("Project metadata is too large.");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }

    internal static void Write<T>(string path, T value, JsonSerializerOptions options)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                if (path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
                { using var gzip = new GZipStream(file, CompressionLevel.Optimal, leaveOpen: true); JsonSerializer.Serialize(gzip, value, options); }
                else JsonSerializer.Serialize(file, value, options);
                file.Flush(true);
            }
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
