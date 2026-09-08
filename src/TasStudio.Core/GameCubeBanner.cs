using System.Buffers.Binary;
using System.Text;

namespace TasStudio.Core;

/// <summary>Reads opening.bnr from an uncompressed GameCube disc without booting the emulator.</summary>
public static class GameCubeBanner
{
    public static byte[]? ReadRgba(string path)
    {
        using var file = File.OpenRead(path);
        byte[] Read(long offset, int count)
        {
            if (offset < 0 || count < 0 || offset > file.Length - count) throw new InvalidDataException("Disc banner is outside the image.");
            var bytes = new byte[count]; file.Position = offset; file.ReadExactly(bytes); return bytes;
        }
        if (file.Length < 0x430) return null;
        var header = Read(0, 0x430);
        uint Word(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset));
        if (Word(header, 0x1c) != 0xc2339f3d) return null; // Other/compressed formats use a placeholder.
        var fstOffset = Word(header, 0x424); var fstSize = Word(header, 0x428);
        if (fstSize is < 12 or > 16 * 1024 * 1024) return null;
        var fst = Read(fstOffset, (int)fstSize);
        var count = Word(fst, 8);
        if (count == 0 || count > fst.Length / 12) return null;
        for (int i = 1; i < count; i++)
        {
            var entry = i * 12; if (fst[entry] != 0) continue;
            var nameAt = checked((int)(count * 12 + (Word(fst, entry) & 0xffffff)));
            if (nameAt >= fst.Length) throw new InvalidDataException("Invalid disc filename.");
            var end = Array.IndexOf(fst, (byte)0, nameAt); if (end < 0) continue;
            if (!Encoding.ASCII.GetString(fst, nameAt, end - nameAt).Equals("opening.bnr", StringComparison.OrdinalIgnoreCase)) continue;
            if (Word(fst, entry + 8) < 0x1820) return null;
            var banner = Read(Word(fst, entry + 4), 0x1820);
            if (!banner.AsSpan(0, 4).SequenceEqual("BNR1"u8) && !banner.AsSpan(0, 4).SequenceEqual("BNR2"u8)) return null;
            var rgba = new byte[96 * 32 * 4]; var at = 0x20;
            for (int by = 0; by < 32; by += 4)
            for (int bx = 0; bx < 96; bx += 4)
            for (int y = 0; y < 4; y++)
            for (int x = 0; x < 4; x++)
            {
                var pixel = BinaryPrimitives.ReadUInt16BigEndian(banner.AsSpan(at)); at += 2;
                int r, g, b, a;
                if ((pixel & 0x8000) != 0)
                {
                    int Expand(int value) => (value << 3) | (value >> 2);
                    r = Expand((pixel >> 10) & 31); g = Expand((pixel >> 5) & 31); b = Expand(pixel & 31); a = 255;
                }
                else
                {
                    r = ((pixel >> 8) & 15) * 17; g = ((pixel >> 4) & 15) * 17; b = (pixel & 15) * 17;
                    var alpha = (pixel >> 12) & 7; a = (alpha << 5) | (alpha << 2) | (alpha >> 1);
                }
                var target = ((by + y) * 96 + bx + x) * 4;
                rgba[target] = (byte)r; rgba[target + 1] = (byte)g; rgba[target + 2] = (byte)b; rgba[target + 3] = (byte)a;
            }
            return rgba;
        }
        return null;
    }
}
