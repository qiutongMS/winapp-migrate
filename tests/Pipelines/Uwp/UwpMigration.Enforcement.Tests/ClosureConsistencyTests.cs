// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinAppMigration.Enforcement;

namespace UwpMigration.Enforcement.Tests;

public sealed partial class RunnerAcceptanceTests
{
    [TestMethod]
    [DataRow("source", ".png")]
    [DataRow("target", ".png")]
    [DataRow("source", "-ui.json")]
    [DataRow("target", "-ui.json")]
    public async Task CanonicalConsistency_ReplacedValidCanonicalCopiesInvalidateCompletion(string side, string suffix)
    {
        using var fixture = await CompletedConsistencyFixtureAsync();
        var fingerprint = await TargetFingerprint.ComputeAsync(fixture.Root);
        var path = $".migration-evidence/{side}/state-1{suffix}";
        if (suffix == ".png")
            await File.WriteAllBytesAsync(fixture.Path(path), PngTestData.Create(size: 72));
        else
            await File.WriteAllTextAsync(fixture.Path(path), ClosureTree.Replace("Ready", "Other content", StringComparison.Ordinal));
        Assert.AreEqual(fingerprint, await TargetFingerprint.ComputeAsync(fixture.Root));

        var changed = await UwpEnforcementRunner.AssessAsync(fixture.Root, TestWindowOwnerResolver.Shared);

        Assert.AreNotEqual("passed", changed.Status);
        Assert.AreNotEqual("complete", changed.Disposition?.Kind);
        Assert.AreEqual("passed", changed.Gates.Single(gate => gate.Id == "state-closure").Status);
        Assert.AreEqual("blocked", changed.Gates.Single(gate => gate.Id == "report-closure").Status);
        StringAssert.Contains(changed.Gates.Single(gate => gate.Id == "report-closure").Reason, "evidence hashes");
        Assert.AreEqual("passed", (await UwpEnforcementRunner.SynchronizeReportAsync(fixture.Root, TestWindowOwnerResolver.Shared)).Status,
            "Finalize must restore copies from the original derived evidence, not bless the edited copies.");
    }

    [TestMethod]
    public async Task CanonicalConsistency_PropertyCopiesAreHashBoundDespiteValidPropertyShape()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var observation = PropertyObservation("IsSelected", "True");
        await PlanPropertyAsync(fixture, "isSelected", observation);
        var probe = await CaptureClosureProbeAsync(fixture, new EnforcementProcessResult(0, observation, ""),
            arguments: ["get-property", "main", "-p", "IsSelected", "-w", "42", "--json"]);
        await UwpEnforcementRunner.RecordStateAsync(fixture.Root, TestWindowOwnerResolver.Shared, "state-1", [probe.ArtifactPath]);
        Assert.AreEqual("passed", (await UwpEnforcementRunner.SynchronizeReportAsync(fixture.Root, TestWindowOwnerResolver.Shared)).Status);
        await File.WriteAllTextAsync(fixture.Path(".migration-evidence/target/state-1-ui.json"),
            PropertyObservation("IsSelected", "False"));

        var changed = await UwpEnforcementRunner.AssessAsync(fixture.Root, TestWindowOwnerResolver.Shared);

        Assert.AreEqual("blocked", changed.Gates.Single(gate => gate.Id == "report-closure").Status);
        Assert.AreNotEqual("complete", changed.Disposition?.Kind);
    }

    [TestMethod]
    public async Task CanonicalConsistency_UnboundOptionalScreenshotCannotBeInjected()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var probe = await CaptureClosureProbeAsync(fixture);
        await UwpEnforcementRunner.RecordStateAsync(fixture.Root, TestWindowOwnerResolver.Shared, "state-1", [probe.ArtifactPath]);
        await UwpEnforcementRunner.SynchronizeReportAsync(fixture.Root, TestWindowOwnerResolver.Shared);
        const string screenshot = ".migration-evidence/target/state-1.png";
        Assert.IsFalse(File.Exists(fixture.Path(screenshot)));
        await File.WriteAllBytesAsync(fixture.Path(screenshot), PngTestData.Create());

        var changed = await UwpEnforcementRunner.AssessAsync(fixture.Root, TestWindowOwnerResolver.Shared);

        Assert.AreEqual("blocked", changed.Gates.Single(gate => gate.Id == "report-closure").Status);
        Assert.AreEqual("passed", (await UwpEnforcementRunner.SynchronizeReportAsync(fixture.Root, TestWindowOwnerResolver.Shared)).Status);
        Assert.IsFalse(File.Exists(fixture.Path(screenshot)));
    }

    [TestMethod]
    [DataRow("source")]
    [DataRow("target")]
    public async Task CanonicalConsistency_ManuallyReplacedManifestHashesCannotAuthorizeCopies(string side)
    {
        using var fixture = await CompletedConsistencyFixtureAsync();
        var plan = await fixture.LoadAsync<StatePlanDocument>(ClosurePlan);
        var proofPath = plan.States[0].Target.Attestation!;
        var proof = await fixture.LoadAsync<StateClosureAttestation>(proofPath);
        Assert.HasCount(plan.States[0].Source.Evidence.Count, proof.SourceEvidenceHashes);
        Assert.HasCount(plan.States[0].Target.Evidence.Count, proof.TargetEvidenceHashes);
        var canonical = $".migration-evidence/{side}/state-1-ui.json";
        await File.WriteAllTextAsync(fixture.Path(canonical), ClosureTree.Replace("Ready", "Injected", StringComparison.Ordinal));
        var manifest = side == "source" ? proof.SourceEvidenceHashes : proof.TargetEvidenceHashes;
        var index = manifest.FindIndex(item => File.ReadAllText(fixture.Path(item.Path)).Contains("windows", StringComparison.Ordinal));
        Assert.IsGreaterThanOrEqualTo(0, index);
        manifest[index] = manifest[index] with { Sha256 = StateClosureProtocol.FileHash(fixture.Root, canonical) };
        StateClosureSeal.Sign(proof);
        await EnforcementStore.WriteAtomicAsync(fixture.Root, proofPath, proof);

        var changed = await UwpEnforcementRunner.AssessAsync(fixture.Root, TestWindowOwnerResolver.Shared);

        Assert.AreNotEqual("passed", changed.Status);
        Assert.AreEqual("blocked", changed.Gates.Single(gate => gate.Id == "state-closure").Status,
            "A valid seal cannot replace product re-derivation of evidence hashes.");
    }

    [TestMethod]
    [DataRow("source", "failed")]
    [DataRow("source", "blocked")]
    [DataRow("source", "reason-only")]
    [DataRow("source", "missing-status")]
    [DataRow("target", "failed")]
    [DataRow("target", "blocked")]
    [DataRow("target", "reason-only")]
    [DataRow("target", "missing-status")]
    public async Task ReportEntries_PerStateContradictionsInvalidateCompletedAggregates(string side, string contradiction)
    {
        using var fixture = await CompletedConsistencyFixtureAsync();
        var report = await fixture.LoadReportAsync();
        var entry = side == "source" ? report.Validation.SourceBaseline.States.Single() :
            report.Validation.TargetReplay.States.Single();
        if (contradiction != "reason-only")
            entry.Status = contradiction == "missing-status" ? null : contradiction;
        entry.Reason = "Contradicts the derived completed observation.";
        await fixture.SaveReportAsync(report);
        var fingerprint = await TargetFingerprint.ComputeAsync(fixture.Root);

        var changed = await UwpEnforcementRunner.AssessAsync(fixture.Root, TestWindowOwnerResolver.Shared);

        Assert.AreEqual(fingerprint, changed.TargetFingerprint);
        Assert.AreNotEqual("passed", changed.Status);
        Assert.AreNotEqual("complete", changed.Disposition?.Kind);
        Assert.AreEqual("blocked", changed.Gates.Single(gate => gate.Id == "report-sync").Status);
        StringAssert.Contains(changed.Gates.Single(gate => gate.Id == "report-sync").Reason, "per-state status/reason");
        Assert.AreEqual("passed", (await UwpEnforcementRunner.SynchronizeReportAsync(fixture.Root, TestWindowOwnerResolver.Shared)).Status);
    }

    [TestMethod]
    public async Task ReportEntries_CapturedSourceReasonMustRoundtripExactly()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var plan = await fixture.LoadAsync<StatePlanDocument>(ClosurePlan);
        const string reason = "Baseline captured on the source desktop.";
        plan.States[0].Source.Reason = reason;
        await EnforcementStore.WriteAtomicAsync(fixture.Root, ClosurePlan, plan);
        await new UwpEnforcementRunner(SuccessfulLaunch()).RunAsync(fixture.Root, new UwpEnforcementOptions());
        await fixture.VerifyStateAsync();
        await UwpEnforcementRunner.SynchronizeReportAsync(fixture.Root, TestWindowOwnerResolver.Shared);
        var report = await fixture.LoadReportAsync();
        Assert.AreEqual(reason, report.Validation.SourceBaseline.States.Single().Reason);
        report.Validation.SourceBaseline.States.Single().Reason = null;
        await fixture.SaveReportAsync(report);

        var changed = await UwpEnforcementRunner.AssessAsync(fixture.Root, TestWindowOwnerResolver.Shared);

        Assert.AreEqual("blocked", changed.Gates.Single(gate => gate.Id == "report-sync").Status);
    }

    private static async Task<RunnerFixture> CompletedConsistencyFixtureAsync()
    {
        var fixture = await RunnerFixture.CreateAsync();
        try
        {
            await new UwpEnforcementRunner(SuccessfulLaunch()).RunAsync(fixture.Root, new UwpEnforcementOptions());
            await fixture.VerifyStateAsync();
            Assert.AreEqual("passed", (await UwpEnforcementRunner.SynchronizeReportAsync(fixture.Root, TestWindowOwnerResolver.Shared)).Status);
            return fixture;
        }
        catch { fixture.Dispose(); throw; }
    }
}

public sealed partial class EnforcementEvaluatorTests
{
    [TestMethod]
    [DataRow("status")]
    [DataRow("reason")]
    [DataRow("missing")]
    public void ReportEntries_EvaluatorRequiresCompleteObservationsNotJustIds(string mismatch)
    {
        using var fixture = new EvaluatorFixture();
        var input = fixture.ClosedInput();
        var summary = input.ReportValidation;
        var target = summary.TargetObservations.Single();
        input = input with
        {
            ReportValidation = summary with
            {
                TargetObservations = mismatch == "missing" ? [] :
                    [target with { Status = mismatch == "status" ? "failed" : target.Status, Reason = "Contradiction" }]
            }
        };

        var changed = EnforcementEvaluator.Evaluate(input);

        Assert.AreEqual("blocked", changed.Gates.Single(gate => gate.Id == "report-sync").Status);
    }
}
