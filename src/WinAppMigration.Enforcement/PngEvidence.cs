// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Buffers.Binary;
using System.IO.Compression;

namespace WinAppMigration.Enforcement;

public enum PngInformation
{
    Invalid,
    LowInformation,
    Informative,
    ValidUnclassified
}

public static class PngEvidence
{
    private static readonly (int X, int Y, int Dx, int Dy)[] Adam7 =
        [(0, 0, 8, 8), (4, 0, 8, 8), (0, 4, 4, 8), (2, 0, 4, 4), (0, 2, 2, 4), (1, 0, 2, 2), (0, 1, 1, 2)];
    private static readonly (int X, int Y, int Dx, int Dy)[] Noninterlaced = [(0, 0, 1, 1)];

    public static PngInformation Classify(string path, CancellationToken cancellationToken = default)
    {
        try
        {
            using var stream = File.OpenRead(path);
            Span<byte> signature = stackalloc byte[8];
            stream.ReadExactly(signature);
            if (!signature.SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
                return PngInformation.Invalid;
            using var compressed = new MemoryStream();
            var width = 0;
            var height = 0;
            var channels = 0;
            var colorType = 0;
            var bitDepth = 0;
            var interlace = 0;
            var ended = false;
            var imageDataStarted = false;
            var imageDataEnded = false;
            byte[]? palette = null;
            byte[]? transparency = null;
            Span<byte> header = stackalloc byte[8];
            Span<byte> crc = stackalloc byte[4];
            while (stream.Position < stream.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                stream.ReadExactly(header);
                var length = BinaryPrimitives.ReadUInt32BigEndian(header[..4]);
                if (length > 64 * 1024 * 1024 || length + 4 > stream.Length - stream.Position)
                    return PngInformation.Invalid;
                var data = new byte[(int)length];
                stream.ReadExactly(data);
                stream.ReadExactly(crc);
                if (ComputeCrc(header[4..], data) != BinaryPrimitives.ReadUInt32BigEndian(crc))
                    return PngInformation.Invalid;
                if (!ValidChunkType(header[4..])) return PngInformation.Invalid;
                if (width == 0 && !header[4..].SequenceEqual("IHDR"u8))
                    return PngInformation.Invalid;
                if (imageDataStarted && !header[4..].SequenceEqual("IDAT"u8))
                    imageDataEnded = true;
                if (header[4..].SequenceEqual("IHDR"u8))
                {
                    if (width != 0 || data.Length != 13 ||
                        data[10] != 0 || data[11] != 0 || data[12] > 1)
                        return PngInformation.Invalid;
                    width = checked((int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(0, 4)));
                    height = checked((int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(4, 4)));
                    colorType = data[9];
                    bitDepth = data[8];
                    interlace = data[12];
                    channels = colorType switch { 0 or 3 => 1, 2 => 3, 4 => 2, 6 => 4, _ => 0 };
                    if (width <= 0 || height <= 0 || width > 16384 || height > 16384 ||
                        (long)width * height > 16 * 1024 * 1024 || channels == 0 ||
                        !LegalBitDepth(colorType, bitDepth))
                        return PngInformation.Invalid;
                }
                else if (header[4..].SequenceEqual("PLTE"u8))
                {
                    if (palette is not null || imageDataStarted || colorType is 0 or 4 ||
                        data.Length is 0 or > 768 || data.Length % 3 != 0 ||
                        colorType == 3 && data.Length / 3 > 1 << bitDepth)
                        return PngInformation.Invalid;
                    palette = data;
                }
                else if (header[4..].SequenceEqual("tRNS"u8))
                {
                    if (transparency is not null || imageDataStarted ||
                        !(colorType == 0 && data.Length == 2 && BinaryPrimitives.ReadUInt16BigEndian(data) < 1 << bitDepth ||
                          colorType == 2 && data.Length == 6 &&
                            BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(0, 2)) < 1 << bitDepth &&
                            BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(2, 2)) < 1 << bitDepth &&
                            BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(4, 2)) < 1 << bitDepth ||
                          colorType == 3 && palette is not null && data.Length > 0 && data.Length <= palette.Length / 3))
                        return PngInformation.Invalid;
                    transparency = data;
                }
                else if (header[4..].SequenceEqual("IDAT"u8))
                {
                    if (imageDataEnded || colorType == 3 && palette is null ||
                        compressed.Length + data.Length > 64 * 1024 * 1024)
                        return PngInformation.Invalid;
                    imageDataStarted = true;
                    compressed.Write(data);
                }
                else if (header[4..].SequenceEqual("IEND"u8))
                {
                    ended = length == 0 && stream.Position == stream.Length;
                    break;
                }
                else if (header[4] is >= (byte)'A' and <= (byte)'Z')
                    return PngInformation.Invalid;
            }
            if (!ended || compressed.Length == 0 || colorType == 3 && palette is null)
                return PngInformation.Invalid;
            if (!ValidateImageData(compressed, width, height, channels, bitDepth, interlace, cancellationToken))
                return PngInformation.Invalid;
            if (bitDepth != 8 || interlace != 0)
                return PngInformation.ValidUnclassified;
            compressed.Position = 0;
            using var decoder = new ZLibStream(compressed, CompressionMode.Decompress);
            var rowSize = checked(width * channels);
            var previous = new byte[rowSize];
            var current = new byte[rowSize];
            var colors = new Dictionary<int, int>();
            for (var y = 0; y < height; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var filter = decoder.ReadByte();
                if (filter is < 0 or > 4) return PngInformation.Invalid;
                decoder.ReadExactly(current);
                for (var x = 0; x < rowSize; x++)
                {
                    var left = x >= channels ? current[x - channels] : 0;
                    var above = previous[x];
                    var upperLeft = x >= channels ? previous[x - channels] : 0;
                    current[x] = unchecked((byte)(current[x] + (filter switch
                    {
                        1 => left, 2 => above, 3 => (left + above) / 2,
                        4 => Paeth(left, above, upperLeft), _ => 0
                    })));
                }
                for (var x = 0; x < rowSize; x += channels)
                {
                    int r, g, b;
                    var alpha = colorType switch { 4 => current[x + 1], 6 => current[x + 3], _ => 255 };
                    if (colorType == 3)
                    {
                        var index = current[x] * 3;
                        if (index + 2 >= palette!.Length) return PngInformation.Invalid;
                        r = palette[index]; g = palette[index + 1]; b = palette[index + 2];
                        if (transparency is not null && current[x] < transparency.Length) alpha = transparency[current[x]];
                    }
                    else
                    {
                        r = current[x];
                        g = colorType is 0 or 4 ? r : current[x + 1];
                        b = colorType is 0 or 4 ? r : current[x + 2];
                        if (colorType == 0 && transparency is { Length: 2 } &&
                            r == BinaryPrimitives.ReadUInt16BigEndian(transparency))
                            alpha = 0;
                        if (colorType == 2 && transparency is { Length: 6 } &&
                            r == BinaryPrimitives.ReadUInt16BigEndian(transparency.AsSpan(0, 2)) &&
                            g == BinaryPrimitives.ReadUInt16BigEndian(transparency.AsSpan(2, 2)) &&
                            b == BinaryPrimitives.ReadUInt16BigEndian(transparency.AsSpan(4, 2)))
                            alpha = 0;
                    }
                    r = (r * alpha + 255 * (255 - alpha)) / 255;
                    g = (g * alpha + 255 * (255 - alpha)) / 255;
                    b = (b * alpha + 255 * (255 - alpha)) / 255;
                    var key = ((r >> 4) << 8) | ((g >> 4) << 4) | (b >> 4);
                    colors[key] = colors.GetValueOrDefault(key) + 1;
                }
                (previous, current) = (current, previous);
            }
            if (decoder.ReadByte() != -1) return PngInformation.Invalid;
            return width < 32 || height < 32 || colors.Count < 2 ||
                colors.Values.Max() >= (long)width * height * 0.999
                ? PngInformation.LowInformation : PngInformation.Informative;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or OverflowException)
        {
            return PngInformation.Invalid;
        }
    }

    private static bool LegalBitDepth(int colorType, int depth) => colorType switch
    {
        0 => depth is 1 or 2 or 4 or 8 or 16,
        3 => depth is 1 or 2 or 4 or 8,
        2 or 4 or 6 => depth is 8 or 16,
        _ => false
    };

    private static bool ValidChunkType(ReadOnlySpan<byte> type)
    {
        foreach (var value in type)
            if (value is not (>= (byte)'A' and <= (byte)'Z') and not (>= (byte)'a' and <= (byte)'z'))
                return false;
        return type[2] is >= (byte)'A' and <= (byte)'Z';
    }

    private static bool ValidateImageData(MemoryStream compressed, int width, int height,
        int channels, int depth, int interlace, CancellationToken cancellationToken)
    {
        var bytes = compressed.GetBuffer().AsSpan(0, (int)compressed.Length);
        if (bytes.Length < 6 || (bytes[0] & 15) != 8 || bytes[0] >> 4 > 7 ||
            (bytes[0] * 256 + bytes[1]) % 31 != 0 || (bytes[1] & 32) != 0)
            return false;
        var checksum = BinaryPrimitives.ReadUInt32BigEndian(bytes[^4..]);
        compressed.Position = 0;
        using var decoder = new ZLibStream(compressed, CompressionMode.Decompress, leaveOpen: true);
        var row = new byte[(width * channels * depth + 7) / 8];
        uint a = 1, b = 0;
        foreach (var pass in interlace == 0 ? Noninterlaced : Adam7)
        {
            var columns = width <= pass.X ? 0 : (width - pass.X + pass.Dx - 1) / pass.Dx;
            var rows = height <= pass.Y ? 0 : (height - pass.Y + pass.Dy - 1) / pass.Dy;
            if (columns == 0 || rows == 0) continue;
            var length = (columns * channels * depth + 7) / 8;
            for (var y = 0; y < rows; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var filter = decoder.ReadByte();
                if (filter is < 0 or > 4) return false;
                a = (a + (uint)filter) % 65521;
                b = (b + a) % 65521;
                decoder.ReadExactly(row.AsSpan(0, length));
                foreach (var value in row.AsSpan(0, length))
                {
                    a = (a + value) % 65521;
                    b = (b + a) % 65521;
                }
            }
        }
        return decoder.ReadByte() == -1 && (b << 16 | a) == checksum;
    }

    private static int Paeth(int left, int above, int upperLeft)
    {
        var estimate = left + above - upperLeft;
        var dl = Math.Abs(estimate - left);
        var da = Math.Abs(estimate - above);
        var du = Math.Abs(estimate - upperLeft);
        return dl <= da && dl <= du ? left : da <= du ? above : upperLeft;
    }

    private static uint ComputeCrc(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        var crc = uint.MaxValue;
        foreach (var value in type) crc = UpdateCrc(crc, value);
        foreach (var value in data) crc = UpdateCrc(crc, value);
        return ~crc;
    }

    private static uint UpdateCrc(uint crc, byte value)
    {
        crc ^= value;
        for (var bit = 0; bit < 8; bit++)
            crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0);
        return crc;
    }
}
