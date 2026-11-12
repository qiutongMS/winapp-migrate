// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using WinAppMigration.Cli.Pipelines.Uwp;
using WinAppMigration.Enforcement;
using UwpMigration.Contracts;

namespace UwpMigration.Enforcement.Tests;

public sealed partial class RunnerAcceptanceTests
{
    private const string ClosureTree =
        """{"windows":[{"elements":[{"type":"Window","automationId":"main","name":"Ready"}]}]}""";
    private const string ClosurePlan =
        ".migration-evidence/state-plan.json";

    [TestMethod]
    public async Task SurfaceClosure_CliDerivesAndFinalizesCanonicalTrees()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var process = new FakeProcessRunner(
            BuildSuccess(),
            ReplaySuccess(),
            new EnforcementProcessResult(0, ClosureTree, ""));
        var runner = new UwpEnforcementRunner(process);
        await runner.RunAsync(
            fixture.Root,
            new UwpEnforcementOptions());
        var command =
            new UwpEnforceCommand(
                process,
                new TestWindowOwnerResolver(4242));
        var probe = await runner.InvokeUiAsync(
            fixture.Root,
            "winapp",
            ["inspect", "--json", "-w", "42"],
            [],
            "state-1");
        var recorded = command.Parse(
            [
                "record-surface",
                fixture.Root.FullName,
                "--surface",
                "state-1",
                "--probe",
                probe.ArtifactPath
            ]);
        Assert.AreEqual(
            0,
            await recorded.InvokeAsync(
                recorded.InvocationConfiguration,
                CancellationToken.None));
        var recordedSnapshot =
            await UwpEnforcementRunner.AssessAsync(
                fixture.Root,
                TestWindowOwnerResolver.Shared);
        Assert.AreEqual(
            "passed",
            recordedSnapshot.Gates.Single(gate =>
                gate.Id == "state-closure").Status);
        var completed =
            await UwpEnforcementRunner.SynchronizeReportAsync(
                fixture.Root,
                TestWindowOwnerResolver.Shared);
        Assert.AreEqual("complete", completed.Disposition?.Kind);
        Assert.AreEqual(
            ClosureTree,
            await File.ReadAllTextAsync(
                fixture.Path(
                    ".migration-evidence/target/state-1-ui.json")));
        Assert.IsFalse(
            File.Exists(
                fixture.Path(
                    ".migration-evidence/target/state-1.png")));
    }

    [TestMethod]
    [DataRow("fingerprint")]
    [DataRow("replay")]
    [DataRow("state")]
    [DataRow("modified-output")]
    [DataRow("duplicate")]
    public async Task SurfaceClosure_RejectsUntrustedOrStaleProbes(
        string invalid)
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var probe = await CaptureClosureProbeAsync(fixture);
        var path = probe.ArtifactPath;
        if (invalid == "fingerprint")
            probe.TargetFingerprint = "wrong";
        if (invalid == "replay")
            probe.ReplayAttestationId = "wrong";
        if (invalid == "state")
            probe.StateId = "another-surface";
        if (invalid == "modified-output")
        {
            await File.WriteAllTextAsync(
                fixture.Path(probe.StandardOutputPath),
                ClosureTree + " ");
        }
        if (invalid is "fingerprint" or "replay" or "state")
        {
            await EnforcementStore.WriteAtomicAsync(
                fixture.Root,
                path,
                probe);
        }

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            UwpEnforcementRunner.RecordSurfaceAsync(
                fixture.Root,
                TestWindowOwnerResolver.Shared,
                "state-1",
                invalid == "duplicate"
                    ? [path, path]
                    : [path]));
    }

    [TestMethod]
    [DataRow("structured-failure")]
    [DataRow("text")]
    [DataRow("empty-tree")]
    [DataRow("duplicate-json-keys")]
    public async Task SurfaceClosure_UnsupportedTreeEvidenceIsInconclusive(
        string invalid)
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var output = invalid switch
        {
            "structured-failure" =>
                """{"success":false,"windows":[]}""",
            "text" => "passed verified true",
            "empty-tree" => """{"windows":[]}""",
            "duplicate-json-keys" =>
                """{"elements":[],"elements":[{"type":"Window"}]}""",
            _ => ClosureTree
        };
        var probe = await CaptureClosureProbeAsync(
            fixture,
            new EnforcementProcessResult(0, output, ""));

        if (invalid == "structured-failure")
        {
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
                UwpEnforcementRunner.RecordSurfaceAsync(
                    fixture.Root,
                    TestWindowOwnerResolver.Shared,
                    "state-1",
                    [probe.ArtifactPath]));
            return;
        }
        var snapshot =
            await UwpEnforcementRunner.RecordSurfaceAsync(
                fixture.Root,
                TestWindowOwnerResolver.Shared,
                "state-1",
                [probe.ArtifactPath]);
        Assert.AreEqual(
            StateVerificationLevel.EvidenceValid,
            snapshot.StateVerifications.Single().Level);
        Assert.AreEqual(
            SurfaceVerificationResult.HumanValidationRequired,
            snapshot.StateVerifications.Single().Result);
        Assert.AreEqual("state-1", snapshot.HumanValidationItems.Single().StateId);
    }

    [TestMethod]
    public async Task SurfaceClosure_NotAttemptedAndConfirmedMismatchRemainDistinct()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var pending = await UwpEnforcementRunner.AssessAsync(
            fixture.Root, TestWindowOwnerResolver.Shared);
        Assert.AreEqual(SurfaceVerificationResult.Pending, pending.StateVerifications.Single().Result);
        var probe = await CaptureClosureProbeAsync(fixture,
            new EnforcementProcessResult(0,
                """{"windows":[{"elements":[{"type":"Window","automationId":"main","name":"Changed"}]}]}""", ""));
        var failed = await UwpEnforcementRunner.RecordSurfaceAsync(
            fixture.Root, TestWindowOwnerResolver.Shared, "state-1", [probe.ArtifactPath]);
        Assert.AreEqual(SurfaceVerificationResult.Mismatch, failed.StateVerifications.Single().Result);
        Assert.AreEqual("blocked", failed.Gates.Single(gate => gate.Id == "state-closure").Status);
    }

    [TestMethod]
    public async Task SurfacePlan_ObservationEnumsAreValidatedBeforeSummary()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var plan =
            await fixture.LoadAsync<StatePlanDocument>(ClosurePlan);
        plan.States[0].Comparison.Status = "agent-passed";
        await EnforcementStore.WriteAtomicAsync(
            fixture.Root,
            ClosurePlan,
            plan);

        var snapshot =
            await UwpEnforcementRunner.AssessAsync(
                fixture.Root,
                TestWindowOwnerResolver.Shared);

        Assert.AreEqual(
            "blocked",
            snapshot.Gates.Single(
                gate => gate.Id == "state-plan").Status);
    }

    private static async Task<UiDevelopmentProbe>
        CaptureClosureProbeAsync(
            RunnerFixture fixture,
            EnforcementProcessResult? result = null,
            string? stateId = "state-1",
            IReadOnlyList<string>? arguments = null)
    {
        var plan =
            await fixture.LoadAsync<StatePlanDocument>(ClosurePlan);
        ContractTestAuthority.Prepare(fixture.Root, plan);
        await EnforcementStore.WriteAtomicAsync(
            fixture.Root,
            ClosurePlan,
            plan);
        await new UwpEnforcementRunner(SuccessfulLaunch()).RunAsync(
            fixture.Root,
            new UwpEnforcementOptions
            {
                ForceReplay = true
            });
        return await new UwpEnforcementRunner(
            new FakeProcessRunner(
                result ??
                new EnforcementProcessResult(0, ClosureTree, "")),
            windowOwnerResolver:
                new TestWindowOwnerResolver(4242))
            .InvokeUiAsync(
                fixture.Root,
                "winapp",
                arguments ??
                    ["inspect", "--json", "-w", "42"],
                [],
                stateId);
    }
}
