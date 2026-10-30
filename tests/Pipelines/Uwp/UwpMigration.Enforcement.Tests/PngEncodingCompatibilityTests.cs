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
    public async Task PngEncoding_LegalUnsupportedEncodingsAreValidButNotInformative(int depth, bool interlaced, int size)
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var path = fixture.Path(".migration-evidence/unsupported.png");
        await File.WriteAllBytesAsync(path, PngTestData.Create(size: size, bitDepth: depth, interlaced: interlaced));

        Assert.AreEqual(PngInformation.ValidUnclassified, PngEvidence.Classify(path));
        Assert.AreNotEqual(PngInformation.Informative, PngEvidence.Classify(path));
    }

    [TestMethod]
    [DataRow("crc")]
    [DataRow("zlib-with-valid-crc")]
    [DataRow("illegal-interlace")]
    [DataRow("illegal-depth")]
    [DataRow("truncated")]
    public async Task PngEncoding_CorruptionIsNeverAnUnsupportedEncoding(string corruption)
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var valid = PngTestData.Create(bitDepth: 16, interlaced: true);
        var invalid = corruption switch
        {
            "crc" => PngTestData.ReplaceChunkByte(valid, "IDAT", 0, 0, repairCrc: false),
            "zlib-with-valid-crc" => PngTestData.ReplaceChunkByte(valid, "IDAT", 0, 0),
            "illegal-interlace" => PngTestData.ReplaceChunkByte(valid, "IHDR", 12, 2),
            "illegal-depth" => PngTestData.ReplaceChunkByte(valid, "IHDR", 8, 3),
            _ => valid[..^6]
        };
        var path = fixture.Path(".migration-evidence/corrupt.png");
        await File.WriteAllBytesAsync(path, invalid);

        Assert.AreEqual(PngInformation.Invalid, PngEvidence.Classify(path));
    }

    [TestMethod]
    [DataRow(16, false)]
    [DataRow(8, true)]
    [DataRow(16, true)]
    public async Task PngEncoding_UnclassifiedImagesAreSupplementalToVerifiedNonvisualAssertions(int depth, bool interlaced)
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var png = PngTestData.Create(bitDepth: depth, interlaced: interlaced);
        await File.WriteAllBytesAsync(fixture.Path(".migration-evidence/states/state-1/source.png"), png);
        var inspect = await CaptureClosureProbeAsync(fixture);
        var runner = new UwpEnforcementRunner(new ScreenshotCliProcessRunner(png: png),
            windowOwnerResolver: new TestWindowOwnerResolver(4242));
        var screenshot = await runner.InvokeUiAsync(fixture.Root, "winapp", ["screenshot", "-w", "42", "--json"], [], "state-1");

        var recorded = await UwpEnforcementRunner.RecordStateAsync(fixture.Root, TestWindowOwnerResolver.Shared, "state-1", [inspect.ArtifactPath, screenshot.ArtifactPath]);
        var finalized = await UwpEnforcementRunner.SynchronizeReportAsync(fixture.Root, TestWindowOwnerResolver.Shared);

        Assert.AreEqual("passed", recorded.Gates.Single(gate => gate.Id == "state-closure").Status);
        Assert.AreEqual("passed", finalized.Status);
        CollectionAssert.AreEqual(png, await File.ReadAllBytesAsync(fixture.Path(".migration-evidence/target/state-1.png")));
    }

    [TestMethod]
    [DataRow(16, false)]
    [DataRow(8, true)]
    public async Task PngEncoding_UnclassifiedImagesWithoutNonvisualAttestationCannotPassMaterialization(int depth, bool interlaced)
    {
        using var fixture = await RunnerFixture.CreateAsync();
        await new UwpEnforcementRunner(SuccessfulLaunch()).RunAsync(fixture.Root, new UwpEnforcementOptions());
        await fixture.WriteTargetEvidenceAsync();
        await File.WriteAllBytesAsync(fixture.Path(".migration-evidence/states/state-1/target.png"),
            PngTestData.Create(bitDepth: depth, interlaced: interlaced));
        var plan = await fixture.LoadAsync<StatePlanDocument>(ClosurePlan);
        plan.States[0].Target.Evidence =
            [".migration-evidence/states/state-1/target.png", ".migration-evidence/states/state-1/stdout.log"];
        await EnforcementStore.WriteAtomicAsync(fixture.Root, ClosurePlan, plan);

        var error = await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            UwpEnforcementRunner.SynchronizeReportAsync(fixture.Root, TestWindowOwnerResolver.Shared));

        StringAssert.Contains(error.Message, "valid-unclassified");
    }

    [TestMethod]
    [DataRow("visual-information")]
    [DataRow("visual-similarity")]
    public async Task PngEncoding_UnclassifiedImagesCannotAuthorizeVisualAssertions(string kind)
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var plan = await fixture.LoadAsync<StatePlanDocument>(ClosurePlan);
        plan.States[0].Assertions[0].Kind = kind;
        await EnforcementStore.WriteAtomicAsync(fixture.Root, ClosurePlan, plan);
        var inspect = await CaptureClosureProbeAsync(fixture);
        var runner = new UwpEnforcementRunner(new ScreenshotCliProcessRunner(png: PngTestData.Create(bitDepth: 16)),
            windowOwnerResolver: new TestWindowOwnerResolver(4242));
        var screenshot = await runner.InvokeUiAsync(fixture.Root, "winapp", ["screenshot", "-w", "42", "--json"], [], "state-1");

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            UwpEnforcementRunner.RecordStateAsync(fixture.Root, TestWindowOwnerResolver.Shared, "state-1", [inspect.ArtifactPath, screenshot.ArtifactPath]));
    }

    [TestMethod]
    public async Task PngEncoding_CorruptImagesRemainInvalidEvenWithVerifiedNonvisualAssertions()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var corrupt = PngTestData.ReplaceChunkByte(PngTestData.Create(bitDepth: 16), "IDAT", 0, 0);
        await File.WriteAllBytesAsync(fixture.Path(".migration-evidence/states/state-1/source.png"), corrupt);
        var inspect = await CaptureClosureProbeAsync(fixture);
        await UwpEnforcementRunner.RecordStateAsync(fixture.Root, TestWindowOwnerResolver.Shared, "state-1", [inspect.ArtifactPath]);

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            UwpEnforcementRunner.SynchronizeReportAsync(fixture.Root, TestWindowOwnerResolver.Shared));
    }
}
