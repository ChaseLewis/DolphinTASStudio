using System.Buffers.Binary;

namespace TasStudio.Emulation;

/// <summary>Dolphin raw cards in the persistent Play profile. Slot A, one card per region and size.</summary>
public static class PlayMemoryCards
{
    public static string[] Regions => ["USA", "EUR", "JAP"];

    public static string CardPath(BackendOptions options, string region)
    {
        if (!Regions.Contains(region)) throw new ArgumentException("Select USA, EUR or JAP.", nameof(region));
        var size = options.Configuration?.ValidatedCopy().MemoryCardSizeOverride;
        var suffix = size is { } value ? "." + ((4 << value) * 16 - 5) : "";
        return Path.Combine(options.SaveDirectory, "User", "GC", $"MemoryCardA.{region}{suffix}.raw");
    }

    public static int? ValidateRawCard(byte[] bytes, string region)
    {
        if (!Regions.Contains(region)) throw new ArgumentException("Select USA, EUR or JAP.", nameof(region));
        var sizes = new[] { 4, 8, 16, 32, 64, 128 };
        var index = Array.FindIndex(sizes, size => bytes.Length == size * 131072);
        if (index < 0 || BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(0x22)) != sizes[index])
            throw new InvalidDataException("Select a raw GameCube memory card (59–2043 blocks), not an individual GCI save.");
        var encoding = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(0x24));
        if (encoding != (region == "JAP" ? 1 : 0))
            throw new InvalidDataException("The card's text encoding does not match the selected region.");
        // Check the header before allowing a file to replace the active card.
        ushort sum = 0, inverse = 0;
        for (var offset = 0; offset < 0x1fc; offset += 2)
        {
            var word = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset));
            sum = unchecked((ushort)(sum + word)); inverse = unchecked((ushort)(inverse + (ushort)~word));
        }
        if (sum == ushort.MaxValue) sum = 0;
        if (inverse == ushort.MaxValue) inverse = 0;
        if (sum != BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(0x1fc)) || inverse != BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(0x1fe)))
            throw new InvalidDataException("The memory card header checksum is invalid.");
        return index == 5 ? null : index;
    }

    internal static void WriteAtomic(string path, byte[] bytes)
    {
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var temporary = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { file.Write(bytes); file.Flush(true); }
            File.Move(temporary, full, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
