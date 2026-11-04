// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Nodes;
using WinAppMigration.Cli.Pipelines.Uwp;
using WinAppMigration.Enforcement;
using UwpMigration.Contracts;

namespace UwpMigration.Enforcement.Tests;

public sealed partial class RunnerAcceptanceTests
{
    [TestMethod]
    [DataRow("action")]
    [DataRow("assertion")]
    [DataRow("command")]
    [DataRow("order")]
    [DataRow("outcome")]
    public async Task RequiredContract_SourceCaptureFreezesRequirementsAgainstMutation(string mutation)
    {
        using var fixture = await RunnerFixture.CreateAsync(unstarted: true);
        var plan = await NewRequiredPlanAsync(fixture);
        var contract = await UwpEnforcementRunner.SealContractAsync(fixture.Root);
        await ObserveSourceAsync(fixture, plan);
        switch (mutation)
        {
            case "action": plan.States[0].Verification!.RequiredActions.Clear(); break;
            case "assertion": plan.States[0].Assertions.Clear(); break;
            case "command": plan.States[0].Verification!.RequiredActions[0].Command.Add("other"); break;
            case "order": plan.States[0].Verification!.Steps.Reverse(); break;
            case "outcome": plan.States[0].ExpectedOutcome = "A convenient weaker outcome."; break;
        }
        await EnforcementStore.WriteAtomicAsync(fixture.Root, ClosurePlan, plan);

        Assert.ThrowsExactly<InvalidDataException>(() => RequiredStateContracts.Require(fixture.Root, plan.States[0]));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            UwpEnforcementRunner.SealContractAsync(fixture.Root, extend: true));
        Assert.AreEqual(contract.RequiredContractHash,
            (await fixture.LoadAsync<RequiredStateContractDocument>(RequiredStateContracts.Path)).RequiredContractHash);
    }

    [TestMethod]
    public async Task RequiredContract_AppendOnlyExtensionIsExplicitAndRollbackResistant()
    {
        using var fixture = await RunnerFixture.CreateAsync(unstarted: true);
        var plan = await NewRequiredPlanAsync(fixture);
        var initial = await UwpEnforcementRunner.SealContractAsync(fixture.Root);
        var state = plan.States[0];
        state.Actions.Add("Observe the state again.");
        state.Verification!.RequiredActions.Add(new RequiredStateAction("observe-again", ["inspect"]));
        state.Assertions.Add(new StateAssertion
        {
            Id = "repeat-name", Kind = "ui-property-equals-source", ProbeIndex = 1,
            AutomationId = "main", Property = "name", SourceEvidence = state.Assertions[0].SourceEvidence
        });
        state.Verification.Steps.AddRange([new("action", "observe-again"), new("assertion", "repeat-name")]);
        await EnforcementStore.WriteAtomicAsync(fixture.Root, ClosurePlan, plan);
        var extended = await UwpEnforcementRunner.SealContractAsync(fixture.Root, extend: true);
        Assert.AreEqual(initial.BaselineHash, extended.BaselineHash);
        Assert.AreNotEqual(initial.RequiredContractHash, extended.RequiredContractHash);
        await EnforcementStore.WriteAtomicAsync(fixture.Root, RequiredStateContracts.Path, initial);

        Assert.ThrowsExactly<InvalidDataException>(() => RequiredStateContracts.Require(fixture.Root, state));
    }

    [TestMethod]
    [DataRow("action")]
    [DataRow("assertion")]
    [DataRow("order")]
    public async Task RequiredContract_EvenPreSourceExtensionCannotRemoveOrRewriteRequirements(string weakening)
    {
        using var fixture = await RunnerFixture.CreateAsync(unstarted: true);
        var plan = await NewRequiredPlanAsync(fixture);
        await UwpEnforcementRunner.SealContractAsync(fixture.Root);
        if (weakening == "action")
            plan.States[0].Verification!.RequiredActions[0] = plan.States[0].Verification!.RequiredActions[0] with
                { Command = ["invoke", "A different action"] };
        if (weakening == "assertion") plan.States[0].Assertions[0].Property = "value";
        if (weakening == "order") plan.States[0].Verification!.Steps.Reverse();
        await EnforcementStore.WriteAtomicAsync(fixture.Root, ClosurePlan, plan);

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => UwpEnforcementRunner.SealContractAsync(fixture.Root, extend: true));
    }

    [TestMethod]
    public async Task RequiredContract_DeletingJsonDoesNotResetTheIndependentAnchor()
    {
        using var fixture = await RunnerFixture.CreateAsync(unstarted: true);
        await NewRequiredPlanAsync(fixture);
        await UwpEnforcementRunner.SealContractAsync(fixture.Root);
        File.Delete(fixture.Path(RequiredStateContracts.Path));

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => UwpEnforcementRunner.SealContractAsync(fixture.Root));
    }

    [TestMethod]
    public async Task RequiredContract_CopyingAnotherWorkspacesSignedContractCannotBlessCapturedLegacyEvidence()
    {
        using var clean = await RunnerFixture.CreateAsync(unstarted: true);
        await NewRequiredPlanAsync(clean);
        var foreign = await UwpEnforcementRunner.SealContractAsync(clean.Root);
        using var legacy = await RunnerFixture.CreateAsync(unstarted: true);
        var plan = await NewRequiredPlanAsync(legacy);
        await ObserveSourceAsync(legacy, plan);
        await EnforcementStore.WriteAtomicAsync(legacy.Root, RequiredStateContracts.Path, foreign);

        Assert.ThrowsExactly<InvalidDataException>(() => RequiredStateContracts.Require(legacy.Root, plan.States[0]));
        var assessed = await UwpEnforcementRunner.AssessAsync(legacy.Root, TestWindowOwnerResolver.Shared);
        Assert.AreEqual("blocked", assessed.Gates.Single(gate => gate.Id == "required-contract").Status);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            UwpEnforcementRunner.SealContractAsync(legacy.Root, extend: true));
    }

    [TestMethod]
    public async Task RequiredContract_ValidNonvisualDifferencesAreContractSatisfiedButNotParity()
    {
        using var fixture = await RunnerFixture.CreateAsync(unstarted: true);
        var plan = await NewRequiredPlanAsync(fixture);
        await UwpEnforcementRunner.SealContractAsync(fixture.Root);
        await ObserveSourceAsync(fixture, plan);
        await AddRequiredTodoAndFindingAsync(fixture, ["main-name"]);
        await new UwpEnforcementRunner(SuccessfulLaunch()).RunAsync(fixture.Root, new UwpEnforcementOptions());
        var probe = await ContractProbeAsync(fixture, ["inspect", "-w", "42", "--json"],
            ClosureTree.Replace("Ready", "Different", StringComparison.Ordinal));

        var recorded = await UwpEnforcementRunner.RecordStateAsync(fixture.Root, TestWindowOwnerResolver.Shared, "state-1", [probe.ArtifactPath]);

        Assert.AreEqual(StateVerificationLevel.ContractSatisfied, recorded.StateVerifications.Single().Level);
        Assert.AreNotEqual("passed", recorded.Status);
        Assert.AreEqual("pending", (await fixture.LoadReportAsync()).Todos.Single().Status);
        Assert.AreEqual("open", (await fixture.LoadAsync<SemanticFindingLedger>(EnforcementStore.FindingLedgerPath)).Findings.Single().Status);
    }

    [TestMethod]
    public async Task RequiredContract_EstablishmentTimeCannotFollowTheReplayItAuthorizes()
    {
        using var fixture = await RunnerFixture.CreateAsync(unstarted: true);
        var plan = await NewRequiredPlanAsync(fixture);
        await UwpEnforcementRunner.SealContractAsync(fixture.Root);
        await ObserveSourceAsync(fixture, plan);
        var replayed = await new UwpEnforcementRunner(SuccessfulLaunch()).RunAsync(fixture.Root, new UwpEnforcementOptions());
        var probe = await ContractProbeAsync(fixture, ["inspect", "-w", "42", "--json"], ClosureTree);
        var contract = await fixture.LoadAsync<RequiredStateContractDocument>(RequiredStateContracts.Path);
        contract.EstablishedAt = replayed.Replay!.StartedAt.AddTicks(1);
        StateClosureSeal.Sign(contract);
        await EnforcementStore.WriteAtomicAsync(fixture.Root, RequiredStateContracts.Path, contract);

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            UwpEnforcementRunner.RecordStateAsync(fixture.Root, TestWindowOwnerResolver.Shared, "state-1", [probe.ArtifactPath]));
    }

    [TestMethod]
    public async Task RequiredContract_WorkflowStartLocksEvenSourceUnattemptedPlans()
    {
        using var fixture = await RunnerFixture.CreateAsync(unstarted: true);
        var plan = await NewRequiredPlanAsync(fixture);
        await UwpEnforcementRunner.SealContractAsync(fixture.Root);
        await UwpEnforcementRunner.AssessAsync(fixture.Root, TestWindowOwnerResolver.Shared);
        plan.States[0].Assertions[0].Property = "value";
        await EnforcementStore.WriteAtomicAsync(fixture.Root, ClosurePlan, plan);

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => UwpEnforcementRunner.SealContractAsync(fixture.Root, extend: true));
        Assert.ThrowsExactly<InvalidDataException>(() => RequiredStateContracts.Require(fixture.Root, plan.States[0]));
    }

    [TestMethod]
    public async Task RequiredContract_CliSafelyUpgradesOnlyUnstartedStructuredLegacyPlans()
    {
        using var fixture = await RunnerFixture.CreateAsync(unstarted: true);
        var plan = await NewRequiredPlanAsync(fixture);
        plan.SchemaVersion = "1.0";
        await EnforcementStore.WriteAtomicAsync(fixture.Root, ClosurePlan, plan);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => UwpEnforcementRunner.SealContractAsync(fixture.Root));
        var command = new UwpEnforceCommand(new FakeProcessRunner(), TestWindowOwnerResolver.Shared);
        var parsed = command.Parse(["seal-contract", fixture.Root.FullName, "--upgrade-plan"]);
        Assert.AreEqual(0, await parsed.InvokeAsync(parsed.InvocationConfiguration, CancellationToken.None));
        Assert.AreEqual("2.0", (await fixture.LoadAsync<StatePlanDocument>(ClosurePlan)).SchemaVersion);
        await ObserveSourceAsync(fixture, plan);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            UwpEnforcementRunner.SealContractAsync(fixture.Root, extend: true, upgradePlan: true));
    }

    [TestMethod]
    public async Task RequiredContract_CapturedMutableLegacyPlansCannotBecomeVerified()
    {
        using var fixture = await RunnerFixture.CreateAsync(unstarted: true);
        var plan = await NewRequiredPlanAsync(fixture);
        plan.SchemaVersion = "1.0";
        await ObserveSourceAsync(fixture, plan);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            UwpEnforcementRunner.SealContractAsync(fixture.Root, upgradePlan: true));
        await new UwpEnforcementRunner(SuccessfulLaunch()).RunAsync(fixture.Root, new UwpEnforcementOptions());
        var runner = new UwpEnforcementRunner(new FakeProcessRunner(new EnforcementProcessResult(0, ClosureTree, "")),
            windowOwnerResolver: TestWindowOwnerResolver.Shared);

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => runner.InvokeUiAsync(fixture.Root,
            "winapp", ["inspect", "-w", "42", "--json"], [], "state-1"));
    }

    [TestMethod]
    [DataRow("blank", true, StateVerificationLevel.EvidenceValid)]
    [DataRow("blank", false, StateVerificationLevel.EvidenceValid)]
    [DataRow("different", true, StateVerificationLevel.ContractSatisfied)]
    [DataRow("same", true, StateVerificationLevel.ParityVerified)]
    [DataRow("same", false, StateVerificationLevel.ParityVerified)]
    [DataRow("source-unclassified", false, StateVerificationLevel.ContractSatisfied)]
    [DataRow("target-unclassified", false, StateVerificationLevel.EvidenceValid)]
    [DataRow("mae-tolerated", false, StateVerificationLevel.ParityVerified)]
    [DataRow("mae-different", false, StateVerificationLevel.ContractSatisfied)]
    public async Task VisualContract_ShopProductsInTreeCannotHideBlankProductImageRegion(
        string image, bool bounds, string expectedLevel)
    {
        using var fixture = await RunnerFixture.CreateAsync(unstarted: true);
        var plan = await NewRequiredPlanAsync(fixture);
        const string tree = """
            {"windows":[{"hwnd":42,"elements":[{"type":"Window","name":"Catalogue","x":100,"y":200,"width":128,"height":128,"children":[
              {"type":"List","automationId":"main","name":"Ready","children":[
                {"type":"Image","automationId":"semantic-image","name":"Product photograph","x":132,"y":248,"width":64,"height":64}
              ]}
            ]}]}]}
            """;
        var state = plan.States[0];
        state.Verification!.Kind = "visual";
        state.Assertions.Add(new StateAssertion
        {
            Id = "image-visible", Kind = "visual-region-equals-source", ProbeIndex = 1,
            SourceEvidence = ".migration-evidence/states/state-1/source.png",
            AutomationId = "semantic-image",
            Region = bounds ? null : new VisualRegion(0.25, 0.375, 0.5, 0.5),
            BoundsEvidence = bounds ? ".migration-evidence/states/state-1/source-ui.json" : null,
            BoundsProbeIndex = bounds ? 0 : null,
            BoundsAfterProbeIndex = bounds ? 2 : null,
            SourceCaptureEvidence = bounds ? ".migration-evidence/states/state-1/source-capture.json" : null
        });
        if (image.StartsWith("mae-", StringComparison.Ordinal))
        {
            state.Assertions[^1].Comparison = "sampled-mae";
            state.Assertions[^1].MaximumMeanError = 0.01;
        }
        state.Verification.Steps.Add(new RequiredStateStep("assertion", "image-visible"));
        await EnforcementStore.WriteAtomicAsync(fixture.Root, ClosurePlan, plan);
        await UwpEnforcementRunner.SealContractAsync(fixture.Root);
        var source = image == "source-unclassified" ? PngTestData.Create(size: 128, bitDepth: 16) : SemanticScene("same");
        await ObserveSourceAsync(fixture, plan, tree, source);
        var metadata = CoordinateCapture(128, 128, 42, 4242, 100, 200, 128, 128);
        if (bounds)
        {
            await File.WriteAllTextAsync(fixture.Path(".migration-evidence/states/state-1/source-capture.json"), metadata.ToJsonString());
            state.Source.Evidence.Add(".migration-evidence/states/state-1/source-capture.json");
            await EnforcementStore.WriteAtomicAsync(fixture.Root, ClosurePlan, plan);
        }
        await AddRequiredTodoAndFindingAsync(fixture, ["main-name", "image-visible"]);
        await new UwpEnforcementRunner(SuccessfulLaunch()).RunAsync(fixture.Root, new UwpEnforcementOptions());
        var inspect = await ContractProbeAsync(fixture, ["inspect", "-w", "42", "--json"], tree);
        var target = image == "target-unclassified" ? PngTestData.Create(size: 128, bitDepth: 16) :
            SemanticScene(image switch { "source-unclassified" => "same", "mae-different" => "different", _ => image });
        Assert.AreEqual(image == "target-unclassified" ? PngInformation.ValidUnclassified : PngInformation.Informative,
            PngEvidence.Classify(await WriteSceneAsync(fixture, target)));
        var screenshot = await new UwpEnforcementRunner(new ScreenshotCliProcessRunner(png: target, capture: metadata),
            windowOwnerResolver: TestWindowOwnerResolver.Shared)
            .InvokeUiAsync(fixture.Root, "winapp", ["screenshot", "-w", "42", "--json"], [], "state-1");

        var evidence = new List<string> { inspect.ArtifactPath, screenshot.ArtifactPath };
        if (bounds)
            evidence.Add((await ContractProbeAsync(fixture, ["inspect", "-w", "42", "--json"], tree)).ArtifactPath);
        var recorded = await UwpEnforcementRunner.RecordStateAsync(fixture.Root, TestWindowOwnerResolver.Shared, "state-1", evidence);

        var updated = await fixture.LoadAsync<StatePlanDocument>(ClosurePlan);
        Assert.AreEqual(expectedLevel, updated.States[0].Target.VerificationLevel);
        Assert.AreEqual(expectedLevel, recorded.StateVerifications.Single().Level);
        var finding = (await fixture.LoadAsync<SemanticFindingLedger>(EnforcementStore.FindingLedgerPath)).Findings.Single();
        if (expectedLevel != StateVerificationLevel.ParityVerified)
        {
            Assert.AreEqual("unverified", updated.States[0].Comparison.Status);
            Assert.AreEqual("pending", (await fixture.LoadReportAsync()).Todos.Single().Status);
            Assert.AreEqual("open", finding.Status);
            Assert.AreNotEqual("passed", recorded.Status);
            if (image == "blank") StringAssert.Contains(updated.States[0].Target.Reason!, "ROI");
        }
        else
        {
            Assert.AreEqual("resolved", finding.Status);
            var finalized = await UwpEnforcementRunner.SynchronizeReportAsync(fixture.Root, TestWindowOwnerResolver.Shared);
            Assert.AreEqual("passed", finalized.Status);
            Assert.AreEqual("verified", (await fixture.LoadReportAsync()).Todos.Single().Status);
            var proof = await fixture.LoadAsync<StateClosureAttestation>(updated.States[0].Target.Attestation!);
            Assert.IsFalse(string.IsNullOrEmpty(proof.RequiredContractHash));
            CollectionAssert.AreEqual(new[] { "observe-main" }, proof.RequiredActionIds);
        }
    }

    [TestMethod]
    [DataRow("missing-return", StateVerificationLevel.EvidenceValid)]
    [DataRow("missing-comparison", StateVerificationLevel.EvidenceValid)]
    [DataRow("comparison-without-return", StateVerificationLevel.EvidenceValid)]
    [DataRow("valid", StateVerificationLevel.ParityVerified)]
    public async Task RestorationContract_DayNightMustProveReturnToDayAndComparison(string scenario, string expected)
    {
        using var fixture = await RunnerFixture.CreateAsync(unstarted: true);
        var plan = await NewRequiredPlanAsync(fixture);
        var state = plan.States[0];
        state.Actions = ["Switch to alternate state.", "Return to initial state."];
        state.Assertions =
        [
            new() { Id = "initial", Kind = "ui-property-equals-source", ProbeIndex = 0, AutomationId = "main", Property = "name", SourceEvidence = ".migration-evidence/states/state-1/source-ui.json" },
            new() { Id = "alternate", Kind = "ui-property-equals-source", ProbeIndex = 2, AutomationId = "main", Property = "name", SourceEvidence = ".migration-evidence/states/state-1/alternate.json" },
            new() { Id = "restored", Kind = "ui-property-equals-source", ProbeIndex = 4, AutomationId = "main", Property = "name", SourceEvidence = ".migration-evidence/states/state-1/source-ui.json" }
        ];
        state.Verification = new StateVerificationContract
        {
            RequiredActions = [new("to-alternate", ["invoke", "Night"]), new("return-initial", ["invoke", "Day"])],
            Steps = [new("assertion", "initial"), new("action", "to-alternate"), new("assertion", "alternate"),
                new("action", "return-initial"), new("assertion", "restored")],
            Restorations = [new("roundtrip", "initial", "return-initial", "restored")]
        };
        await EnforcementStore.WriteAtomicAsync(fixture.Root, ClosurePlan, plan);
        await UwpEnforcementRunner.SealContractAsync(fixture.Root);
        var day = ClosureTree.Replace("Ready", "Day", StringComparison.Ordinal);
        var night = ClosureTree.Replace("Ready", "Night", StringComparison.Ordinal);
        await ObserveSourceAsync(fixture, plan, day);
        await File.WriteAllTextAsync(fixture.Path(".migration-evidence/states/state-1/alternate.json"), night);
        state.Source.Evidence.Add(".migration-evidence/states/state-1/alternate.json");
        await EnforcementStore.WriteAtomicAsync(fixture.Root, ClosurePlan, plan);
        await AddRequiredTodoAndFindingAsync(fixture, ["initial", "alternate", "restored"]);
        await new UwpEnforcementRunner(SuccessfulLaunch()).RunAsync(fixture.Root, new UwpEnforcementOptions());
        var probes = new List<string>
        {
            (await ContractProbeAsync(fixture, ["inspect", "-w", "42", "--json"], day)).ArtifactPath,
            (await ContractProbeAsync(fixture, ["invoke", "Night", "-w", "42", "--json"], """{"elementId":"Night","pattern":"Invoke","hwnd":42}""")).ArtifactPath,
            (await ContractProbeAsync(fixture, ["inspect", "-w", "42", "--json"], night)).ArtifactPath
        };
        if (scenario == "comparison-without-return")
            probes.Add((await ContractProbeAsync(fixture, ["inspect", "-w", "42", "--json"], day)).ArtifactPath);
        else if (scenario != "missing-return")
            probes.Add((await ContractProbeAsync(fixture, ["invoke", "Day", "-w", "42", "--json"], """{"elementId":"Day","pattern":"Invoke","hwnd":42}""")).ArtifactPath);
        if (scenario is "valid" or "comparison-without-return")
            probes.Add((await ContractProbeAsync(fixture, ["inspect", "-w", "42", "--json"], day)).ArtifactPath);

        await UwpEnforcementRunner.RecordStateAsync(fixture.Root, TestWindowOwnerResolver.Shared, "state-1", probes);

        var updated = await fixture.LoadAsync<StatePlanDocument>(ClosurePlan);
        Assert.AreEqual(expected, updated.States[0].Target.VerificationLevel);
        if (expected != StateVerificationLevel.ParityVerified)
        {
            Assert.AreEqual("pending", (await fixture.LoadReportAsync()).Todos.Single().Status);
            Assert.AreEqual("open", (await fixture.LoadAsync<SemanticFindingLedger>(EnforcementStore.FindingLedgerPath)).Findings.Single().Status);
        }
        else
        {
            Assert.AreEqual("passed", (await UwpEnforcementRunner.SynchronizeReportAsync(fixture.Root, TestWindowOwnerResolver.Shared)).Status);
            var proof = await fixture.LoadAsync<StateClosureAttestation>(updated.States[0].Target.Attestation!);
            CollectionAssert.AreEqual(new[] { "to-alternate", "return-initial" }, proof.RequiredActionIds);
            CollectionAssert.AreEqual(new[] { "initial", "to-alternate", "alternate", "return-initial", "restored" },
                proof.VerifiedSteps.Select(step => step.Id).ToArray());
        }
    }

    [TestMethod]
    public async Task RequiredContract_ManuallySignedStepAndLevelClaimsAreStillRederived()
    {
        using var fixture = await RunnerFixture.CreateAsync(unstarted: true);
        var plan = await NewRequiredPlanAsync(fixture);
        await UwpEnforcementRunner.SealContractAsync(fixture.Root);
        await ObserveSourceAsync(fixture, plan);
        await new UwpEnforcementRunner(SuccessfulLaunch()).RunAsync(fixture.Root, new UwpEnforcementOptions());
        var probe = await ContractProbeAsync(fixture, ["inspect", "-w", "42", "--json"], ClosureTree);
        await UwpEnforcementRunner.RecordStateAsync(fixture.Root, TestWindowOwnerResolver.Shared, "state-1", [probe.ArtifactPath]);
        var proofPath = StateClosureProtocol.AttestationPath("state-1");
        var proof = await fixture.LoadAsync<StateClosureAttestation>(proofPath);
        proof.VerifiedSteps.Clear();
        StateClosureSeal.Sign(proof);
        await EnforcementStore.WriteAtomicAsync(fixture.Root, proofPath, proof);

        var snapshot = await UwpEnforcementRunner.AssessAsync(fixture.Root, TestWindowOwnerResolver.Shared);

        Assert.AreEqual("unverified", snapshot.StateVerifications.Single().Level);
        Assert.AreNotEqual("passed", snapshot.Status);
    }

    private static async Task<StatePlanDocument> NewRequiredPlanAsync(RunnerFixture fixture)
    {
        var plan = await fixture.LoadAsync<StatePlanDocument>(ClosurePlan);
        var state = plan.States[0];
        state.Actions = ["Observe the current state via inspection."];
        state.Verification = new StateVerificationContract
        {
            RequiredActions = [new("observe-main", ["inspect"])],
            Steps = [new("action", "observe-main"), new("assertion", "main-name")]
        };
        await EnforcementStore.WriteAtomicAsync(fixture.Root, ClosurePlan, plan);
        return plan;
    }

    private static async Task ObserveSourceAsync(RunnerFixture fixture, StatePlanDocument plan,
        string? tree = null, byte[]? png = null)
    {
        Directory.CreateDirectory(fixture.Path(".migration-evidence/states/state-1"));
        await File.WriteAllTextAsync(fixture.Path(".migration-evidence/states/state-1/source-ui.json"), tree ?? ClosureTree);
        await File.WriteAllBytesAsync(fixture.Path(".migration-evidence/states/state-1/source.png"), png ?? PngTestData.Create());
        plan.States[0].Source.Status = "captured";
        plan.States[0].Source.Evidence = [".migration-evidence/states/state-1/source-ui.json", ".migration-evidence/states/state-1/source.png"];
        await EnforcementStore.WriteAtomicAsync(fixture.Root, ClosurePlan, plan);
    }

    private static Task<UiDevelopmentProbe> ContractProbeAsync(RunnerFixture fixture, string[] arguments, string output) =>
        new UwpEnforcementRunner(new FakeProcessRunner(new EnforcementProcessResult(0, output, "")),
            windowOwnerResolver: TestWindowOwnerResolver.Shared).InvokeUiAsync(fixture.Root, "winapp", arguments, [], "state-1");

    private static async Task AddRequiredTodoAndFindingAsync(RunnerFixture fixture, string[] assertionIds)
    {
        var report = await fixture.LoadReportAsync();
        report.Todos.Add(new MigrationTodo { Id = "SEMANTIC-REQUIRED", Category = "behavior", Priority = "required",
            Summary = "Complete the required behavior.", Reason = "Requires parity, not merely valid evidence." });
        await fixture.SaveReportAsync(report);
        var plan = await fixture.LoadAsync<StatePlanDocument>(ClosurePlan);
        plan.States[0].TodoIds = ["SEMANTIC-REQUIRED"];
        await EnforcementStore.WriteAtomicAsync(fixture.Root, ClosurePlan, plan);
        await EnforcementStore.WriteAtomicAsync(fixture.Root, EnforcementStore.FindingLedgerPath,
            new SemanticFindingLedger { Findings = [new SemanticFinding
            {
                Id = "semantic-contract", Category = "behavior", StateIds = ["state-1"], Status = "open",
                SourceInvariant = "Preserve every required outcome.", CompletionCondition = "All required comparisons pass.",
                NextActions = ["Verify complete contract parity."],
                CompletionAssertions = assertionIds.Select(id => new FindingCompletionAssertion { StateId = "state-1", AssertionId = id }).ToList()
            }] });
    }

    private static byte[] SemanticScene(string mode) => PngTestData.Image(128, 128, (x, y) =>
    {
        if (y < 32) return ((byte)(x % 2 == 0 ? 20 : 200), (byte)80, (byte)160);
        if (x >= 32 && x < 96 && y >= 48 && y < 112 && mode != "blank")
        {
            var color = (byte)((((x / 8 + y / 8) % 2 == 0) ^ (mode == "different") ? 40 : 200) +
                (mode == "mae-tolerated" ? 1 : 0));
            return (color, (byte)100, (byte)50);
        }
        return ((byte)255, (byte)255, (byte)255);
    });

    private static async Task<string> WriteSceneAsync(RunnerFixture fixture, byte[] png)
    {
        var path = fixture.Path(".migration-evidence/scene.png");
        await File.WriteAllBytesAsync(path, png);
        return path;
    }
}
