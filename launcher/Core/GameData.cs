using System.Buffers.Binary;
using System.Text;

namespace Quasar;

public static class GameData
{
    private const uint TitleId = 0x43430002;
    private const int Sector = 2048;

    public static string Validate(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new InvalidDataException("Select a game image or extracted game directory.");
        if (Directory.Exists(path))
        {
            var executable = Path.Combine(path, "default.xbe");
            Storage.RequireOrdinaryPath(executable);
            using var xbe = File.OpenRead(executable);
            CheckXbe(xbe, 0, xbe.Length);
            return "extracted";
        }

        if (!new[] { ".iso", ".xiso" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException("Select an ISO, XISO, or extracted game directory.");

        using var image = File.OpenRead(path);
        foreach (long partition in new long[] { 0, 0x18300000 })
        {
            var descriptor = partition + 0x10000;
            if (descriptor + Sector > image.Length)
                continue;
            var header = Read(image, descriptor, Sector);
            var magic = "MICROSOFT*XBOX*MEDIA"u8;
            if (!header.AsSpan(0, 20).SequenceEqual(magic) ||
                !header.AsSpan(Sector - 20).SequenceEqual(magic))
                continue;
            var root = partition + U32(header, 20) * (long)Sector;
            var length = U32(header, 24);
            if (length is < 14 or > 4 * 1024 * 1024 || root + length > image.Length)
                throw new InvalidDataException("The XDVDFS root directory is invalid.");
            CheckRoot(image, partition, Read(image, root, (int)length));
            return "xdvdfs";
        }
        throw new InvalidDataException("No supported Xbox game partition was found.");
    }

    private static void CheckRoot(Stream image, long partition, byte[] root)
    {
        for (var index = 0; index + 14 <= root.Length;)
        {
            var sectorEnd = Math.Min(root.Length, (index / Sector + 1) * Sector);
            var nameLength = root[index + 13];
            if (nameLength is 0 or 255 || index + 14 + nameLength > sectorEnd)
            {
                index = sectorEnd;
                continue;
            }
            if ((root[index + 12] & 0x10) == 0 &&
                Encoding.ASCII.GetString(root, index + 14, nameLength)
                    .Equals("default.xbe", StringComparison.OrdinalIgnoreCase))
            {
                CheckXbe(image, partition + U32(root, index + 4) * (long)Sector, U32(root, index + 8));
                return;
            }
            index += (14 + nameLength + 3) & ~3;
            if (sectorEnd - index < 14)
                index = sectorEnd;
        }
        throw new InvalidDataException("The image has no root default.xbe.");
    }

    private static void CheckXbe(Stream stream, long offset, long length)
    {
        const int headerLength = 0x11C;
        if (length < headerLength || offset + length > stream.Length)
            throw new InvalidDataException("The XBE header is truncated.");
        var header = Read(stream, offset, headerLength);
        var certificate = (long)U32(header, 0x118) - U32(header, 0x104);
        if (!header.AsSpan(0, 4).SequenceEqual("XBEH"u8) ||
            certificate < headerLength || certificate + 12 > Math.Min(length, U32(header, 0x108)) ||
            U32(Read(stream, offset + certificate, 12), 8) != TitleId)
            throw new InvalidDataException("Expected the supported 2002 Steel Battalion XBE (title 43430002).");
    }

    private static uint U32(byte[] bytes, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));

    private static byte[] Read(Stream stream, long offset, int length)
    {
        stream.Position = offset;
        var buffer = new byte[length];
        stream.ReadExactly(buffer);
        return buffer;
    }
}
