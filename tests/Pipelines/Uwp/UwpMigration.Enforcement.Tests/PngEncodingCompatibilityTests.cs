// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinAppMigration.Enforcement;

namespace UwpMigration.Enforcement.Tests;

public sealed partial class RunnerAcceptanceTests
{
    [TestMethod]
    [DataRow(16, false, 64)]
    [DataRow(8, true, 64)]
    [DataRow(16, true, 64)]
    [DataRow(16, false, 1)]
    [DataRow(8, true, 1)]
    public async Task PngEncoding_LegalUnsupportedEncodingsRemainClassified(
        int depth,
        bool interlaced,
        int size)
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var path =
            fixture.Path(".migration-evidence/unsupported.png");
        await File.WriteAllBytesAsync(
            path,
            PngTestData.Create(
                size: size,
                bitDepth: depth,
                interlaced: interlaced));

        Assert.AreEqual(
            PngInformation.ValidUnclassified,
            PngEvidence.Classify(path));
    }

    [TestMethod]
    [DataRow("crc")]
    [DataRow("zlib-with-valid-crc")]
    [DataRow("illegal-interlace")]
    [DataRow("illegal-depth")]
    [DataRow("truncated")]
    public async Task PngEncoding_CorruptionRemainsInvalid(
        string corruption)
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var valid =
            PngTestData.Create(bitDepth: 16, interlaced: true);
        var invalid = corruption switch
        {
            "crc" =>
                PngTestData.ReplaceChunkByte(
                    valid,
                    "IDAT",
                    0,
                    0,
                    repairCrc: false),
            "zlib-with-valid-crc" =>
                PngTestData.ReplaceChunkByte(
                    valid,
                    "IDAT",
                    0,
                    0),
            "illegal-interlace" =>
                PngTestData.ReplaceChunkByte(
                    valid,
                    "IHDR",
                    12,
                    2),
            "illegal-depth" =>
                PngTestData.ReplaceChunkByte(
                    valid,
                    "IHDR",
                    8,
                    3),
            _ => valid[..^6]
        };
        var path =
            fixture.Path(".migration-evidence/corrupt.png");
        await File.WriteAllBytesAsync(path, invalid);

        Assert.AreEqual(
            PngInformation.Invalid,
            PngEvidence.Classify(path));
    }
}
