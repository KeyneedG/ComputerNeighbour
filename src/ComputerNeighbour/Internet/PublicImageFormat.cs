using System.Buffers.Binary;

namespace ComputerNeighbour.Internet;

internal static class PublicImageFormat
{
    internal static bool IsSupported(ReadOnlySpan<byte> bytes, string contentType) => contentType switch
    {
        "image/png" => bytes.Length >= 33 && bytes[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) &&
            BinaryPrimitives.ReadUInt32BigEndian(bytes[8..]) == 13 && bytes[12..16].SequenceEqual("IHDR"u8) &&
            ValidSize(BinaryPrimitives.ReadUInt32BigEndian(bytes[16..]), BinaryPrimitives.ReadUInt32BigEndian(bytes[20..])),
        "image/jpeg" => IsJpeg(bytes),
        _ => false
    };

    private static bool IsJpeg(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 12 || bytes[0] != 0xff || bytes[1] != 0xd8) return false;
        var offset = 2;
        while (offset + 4 <= bytes.Length && bytes[offset++] == 0xff)
        {
            while (offset < bytes.Length && bytes[offset] == 0xff) offset++;
            if (offset + 3 > bytes.Length) break;
            var marker = bytes[offset++];
            if (marker is 0xda or 0xd9 or 0) break;
            var length = BinaryPrimitives.ReadUInt16BigEndian(bytes[offset..]);
            if (length < 2 || length > bytes.Length - offset) break;
            if (marker is 0xc0 or 0xc1 or 0xc2)
                return length >= 8 && ValidSize(BinaryPrimitives.ReadUInt16BigEndian(bytes[(offset + 5)..]),
                    BinaryPrimitives.ReadUInt16BigEndian(bytes[(offset + 3)..]));
            offset += length;
        }
        return false;
    }

    private static bool ValidSize(uint width, uint height) =>
        width is >= 96 and <= 4096 && height is >= 96 and <= 4096 && width * height <= 12_000_000;
}
