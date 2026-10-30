// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace UwpMigration.Enforcement.Tests;

internal static class PngTestData
{
    internal static byte[] Create(bool uniform = false, int size = 64, int bitDepth = 8, bool interlaced = false)
    {
        using var output = new MemoryStream();
        output.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0, 4), size);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4, 4), size);
        header[8] = (byte)bitDepth;
        header[9] = 2;
        header[12] = interlaced ? (byte)1 : (byte)0;
        WriteChunk(output, "IHDR"u8, header);
        using var compressed = new MemoryStream();
        using (var encoder = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            (int X, int Y, int Dx, int Dy)[] passes = interlaced
                ? [(0, 0, 8, 8), (4, 0, 8, 8), (0, 4, 4, 8), (2, 0, 4, 4), (0, 2, 2, 4), (1, 0, 2, 2), (0, 1, 1, 2)]
                : [(0, 0, 1, 1)];
            foreach (var pass in passes)
            {
                if (pass.X >= size || pass.Y >= size) continue;
                for (var y = pass.Y; y < size; y += pass.Dy)
                {
                    encoder.WriteByte(0);
                    for (var x = pass.X; x < size; x += pass.Dx)
                    {
                        var value = (byte)(uniform || (x / 8 + y / 8) % 2 == 0 ? 255 : 0);
                        encoder.Write(bitDepth == 16 ? [value, value, value, value, value, value] : [value, value, value]);
                    }
                }
            }
        }
        WriteChunk(output, "IDAT"u8, compressed.ToArray());
        WriteChunk(output, "IEND"u8, []);
        return output.ToArray();
    }

    internal static byte[] ReplaceChunkByte(byte[] png, string chunk, int payloadOffset, byte value, bool repairCrc = true)
    {
        var result = (byte[])png.Clone();
        for (var offset = 8; offset < result.Length;)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(result.AsSpan(offset, 4));
            if (result.AsSpan(offset + 4, 4).SequenceEqual(Encoding.ASCII.GetBytes(chunk)))
            {
                result[offset + 8 + payloadOffset] = value;
                if (repairCrc)
                {
                    using var rewritten = new MemoryStream();
                    WriteChunk(rewritten, result.AsSpan(offset + 4, 4), result.AsSpan(offset + 8, length));
                    rewritten.ToArray().CopyTo(result, offset);
                }
                return result;
            }
            offset += length + 12;
        }
        throw new InvalidOperationException($"PNG test chunk not found: {chunk}.");
    }

    private static void WriteChunk(Stream stream, ReadOnlySpan<byte> type, ReadOnlySpan<byte> bytes)
    {
        Span<byte> word = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(word, bytes.Length);
        stream.Write(word);
        stream.Write(type);
        stream.Write(bytes);
        var crc = uint.MaxValue;
        foreach (var value in type) crc = Update(crc, value);
        foreach (var value in bytes) crc = Update(crc, value);
        BinaryPrimitives.WriteUInt32BigEndian(word, ~crc);
        stream.Write(word);
    }

    private static uint Update(uint crc, byte value)
    {
        crc ^= value;
        for (var bit = 0; bit < 8; bit++)
            crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0xedb88320u);
        return crc;
    }
}
