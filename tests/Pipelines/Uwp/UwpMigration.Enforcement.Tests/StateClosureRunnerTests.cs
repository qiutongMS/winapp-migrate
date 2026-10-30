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
    private const string ClosureTree =
        """{"windows":[{"elements":[{"type":"Window","automationId":"main","name":"Ready"}]}]}""";
    private const string ClosurePlan = ".migration-evidence/state-plan.json";

    [TestMethod]
    public async Task RecordState_CliDerivesClosureAndFinalizeSynchronizesTodoAndCanonicalEvidence()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var report = await fixture.LoadReportAsync();
        report.Todos.Add(new MigrationTodo
        {
            Id = "UWMIG-STATE", Category = "behavior", Priority = "required",
            Summary = "Verify the main state.", Reason = "Requires observable equivalence."
        });
        await fixture.SaveReportAsync(report);
        var plan = await fixture.LoadAsync<StatePlanDocument>(ClosurePlan);
        plan.States[0].TodoIds = ["UWMIG-STATE"];
        await EnforcementStore.WriteAtomicAsync(fixture.Root, ClosurePlan, plan);
        var process = new FakeProcessRunner(BuildSuccess(), ReplaySuccess(),
            new EnforcementProcessResult(0, ClosureTree, ""));
        var runner = new UwpEnforcementRunner(process);
        await runner.RunAsync(fixture.Root, new UwpEnforcementOptions());
        var command = new UwpEnforceCommand(process, new TestWindowOwnerResolver(4242));
        var captured = await InvokeWithCapturedOutputAsync(command.Parse(
            ["capture", fixture.Root.FullName, "--state", "state-1", "--", "inspect", "--json", "-w", "42"]));
        var probe = JsonSerializer.Deserialize<UiDevelopmentProbe>(captured, EnforcementStore.JsonOptions)!;
        var recorded = await InvokeWithCapturedOutputAsync(command.Parse(
            ["record-state", fixture.Root.FullName, "--state", "state-1", "--probe", probe.ArtifactPath]));

        Assert.IsNotNull(JsonNode.Parse(recorded)!["Gates"], recorded);
        Assert.AreEqual("passed", JsonNode.Parse(recorded)!["Gates"]!.AsArray()
            .Single(gate => gate!["id"]!.GetValue<string>() == "state-closure")!["status"]!.GetValue<string>());
        plan = await fixture.LoadAsync<StatePlanDocument>(ClosurePlan);
        var state = plan.States.Single();
        Assert.AreEqual("passed", state.Target.Status);
        Assert.AreEqual("verified", state.Comparison.Status);
        var attestation = await fixture.LoadAsync<StateClosureAttestation>(state.Target.Attestation!);
        CollectionAssert.AreEqual(new[] { "main-name" }, attestation.AssertionIds);
        CollectionAssert.AreEqual(new[] { probe.ArtifactPath }, attestation.Probes);
        Assert.AreEqual("pending", (await fixture.LoadReportAsync()).Todos.Single().Status);

        var completed = await UwpEnforcementRunner.SynchronizeReportAsync(fixture.Root, TestWindowOwnerResolver.Shared);

        Assert.AreEqual("complete", completed.Disposition?.Kind);
        Assert.AreEqual("verified", (await fixture.LoadReportAsync()).Todos.Single().Status);
        Assert.AreEqual(ClosureTree,
            await File.ReadAllTextAsync(fixture.Path(".migration-evidence/target/state-1-ui.json")));
        Assert.IsFalse(File.Exists(fixture.Path(".migration-evidence/target/state-1.png")),
            "A structured nonvisual assertion must not manufacture a screenshot.");
    }

    [TestMethod]
    [DataRow("fingerprint")]
    [DataRow("replay")]
    [DataRow("state")]
    [DataRow("producer")]
    [DataRow("artifact-class")]
    [DataRow("stale-receipt")]
    [DataRow("command")]
    [DataRow("untracked")]
    [DataRow("fabricated")]
    [DataRow("modified-raw")]
    [DataRow("missing-raw")]
    [DataRow("stale-raw")]
    [DataRow("outside-storage")]
    [DataRow("latest-alias")]
    [DataRow("duplicate")]
    public async Task RecordState_RejectsUntrustedOrStaleProbes(string invalid)
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var probe = await CaptureClosureProbeAsync(fixture);
        var replay = await fixture.LoadAsync<ReplayAttestation>(EnforcementStore.ReplayAttestationPath);
        var path = probe.ArtifactPath;
        switch (invalid)
        {
            case "fingerprint": probe.TargetFingerprint = "wrong"; break;
            case "replay": probe.ReplayAttestationId = "wrong"; break;
            case "state": probe.StateId = "another-state"; break;
            case "producer": probe.Producer = "agent"; break;
            case "artifact-class": probe.ArtifactClass = MigrationArtifactClass.CanonicalAttestation; break;
            case "stale-receipt": probe.StartedAt = replay.StartedAt.AddSeconds(-1); break;
            case "command": probe.Command[2] = "invoke"; break;
            case "modified-raw":
                await File.WriteAllTextAsync(fixture.Path(probe.StandardOutputPath), ClosureTree + " ");
                break;
            case "missing-raw": File.Delete(fixture.Path(probe.StandardOutputPath)); break;
            case "stale-raw":
                File.SetLastWriteTimeUtc(fixture.Path(probe.StandardOutputPath), replay.StartedAt.AddSeconds(-1).UtcDateTime);
                break;
            case "untracked":
            case "fabricated":
                var progress = await fixture.LoadAsync<WorkflowProgress>(EnforcementStore.ProgressPath);
                var operation = progress.Operations.Single(item => item.ProbeId == probe.Id);
                operation.ProbeSha256 = invalid == "fabricated" ? "agent-authored-hash" : null;
                await EnforcementStore.WriteAtomicAsync(fixture.Root, EnforcementStore.ProgressPath, progress);
                break;
            case "outside-storage":
                path = ".migration-evidence/copied-probe.json";
                probe.ArtifactPath = path;
                break;
            case "latest-alias": path = EnforcementStore.UiProbePath; break;
        }
        if (invalid is "fingerprint" or "replay" or "state" or "producer" or "artifact-class" or
            "stale-receipt" or "command" or "outside-storage")
            await EnforcementStore.WriteAtomicAsync(fixture.Root, path, probe);
        var before = await File.ReadAllTextAsync(fixture.Path(ClosurePlan));

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            UwpEnforcementRunner.RecordStateAsync(fixture.Root, TestWindowOwnerResolver.Shared, "state-1",
                invalid == "duplicate" ? [path, path] : [path]));

        Assert.AreEqual(before, await File.ReadAllTextAsync(fixture.Path(ClosurePlan)));
        Assert.IsFalse(File.Exists(fixture.Path(StateClosureProtocol.AttestationPath("state-1"))));
    }

    [TestMethod]
    [DataRow("changed-target")]
    [DataRow("new-replay")]
    [DataRow("changed-state-contract")]
    [DataRow("changed-source-observation")]
    [DataRow("unbound")]
    public async Task RecordState_RejectsChangedGenerationOrPlan(string invalid)
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var probe = await CaptureClosureProbeAsync(fixture, stateId: invalid == "unbound" ? null : "state-1");
        if (invalid == "changed-target")
            await File.AppendAllTextAsync(fixture.ProjectPath, Environment.NewLine);
        if (invalid == "new-replay")
        {
            const string evidence = ".migration-evidence/new-replay.txt";
            await File.WriteAllTextAsync(fixture.Path(evidence), "A new canonical generation is needed.");
            await new UwpEnforcementRunner(new FakeProcessRunner(ReplaySuccess())).RunAsync(
                fixture.Root, new UwpEnforcementOptions { ForceReplay = true, DiscriminatingEvidence = [evidence] });
        }
        if (invalid == "changed-state-contract")
        {
            var plan = await fixture.LoadAsync<StatePlanDocument>(ClosurePlan);
            plan.States[0].ExpectedOutcome = "A different planned behavior.";
            await EnforcementStore.WriteAtomicAsync(fixture.Root, ClosurePlan, plan);
        }
        if (invalid == "changed-source-observation")
            await File.WriteAllTextAsync(fixture.Path(".migration-evidence/states/state-1/source-ui.json"), ClosureTree + " ");

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            UwpEnforcementRunner.RecordStateAsync(fixture.Root, TestWindowOwnerResolver.Shared, "state-1", [probe.ArtifactPath]));
    }

    [TestMethod]
    [DataRow("exit-code")]
    [DataRow("structured-failure")]
    [DataRow("infrastructure")]
    [DataRow("text")]
    [DataRow("booleans")]
    [DataRow("mismatch")]
    [DataRow("ambiguous-element")]
    [DataRow("duplicate-json-keys")]
    public async Task RecordState_RequiresSuccessfulStructuredObservations(string invalid)
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var output = invalid switch
        {
            "structured-failure" => """{"success":false,"windows":[]}""",
            "text" => "passed verified true",
            "booleans" => """{"success":true,"passed":true,"verified":true}""",
            "mismatch" => ClosureTree.Replace("Ready", "Wrong", StringComparison.Ordinal),
            "ambiguous-element" => """{"elements":[{"type":"Window","automationId":"main","name":"Ready"},{"type":"Window","automationId":"main","name":"Ready"}]}""",
            "duplicate-json-keys" => """{"elements":[],"elements":[{"type":"Window","automationId":"main","name":"Ready"}]}""",
            _ => ClosureTree
        };
        var result = new EnforcementProcessResult(invalid == "exit-code" ? 7 : 0, output, "")
        {
            InfrastructureFailure = invalid == "infrastructure"
        };
        if (invalid == "infrastructure")
        {
            await Assert.ThrowsExactlyAsync<IOException>(() => CaptureClosureProbeAsync(fixture, result));
            Assert.IsFalse(File.Exists(fixture.Path(EnforcementStore.UiProbePath)));
            return;
        }
        var probe = await CaptureClosureProbeAsync(fixture, result);

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            UwpEnforcementRunner.RecordStateAsync(fixture.Root, TestWindowOwnerResolver.Shared, "state-1", [probe.ArtifactPath]));
    }

    [TestMethod]
    [DataRow("missing")]
    [DataRow("unknown-kind")]
    [DataRow("agent-boolean-property")]
    [DataRow("status-property")]
    [DataRow("unmapped-source")]
    [DataRow("missing-index")]
    public async Task RecordState_UnsupportedOrUnmappedAssertionsRemainUnverified(string invalid)
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var plan = await fixture.LoadAsync<StatePlanDocument>(ClosurePlan);
        var assertion = plan.States[0].Assertions[0];
        switch (invalid)
        {
            case "missing": plan.States[0].Assertions.Clear(); break;
            case "unknown-kind": assertion.Kind = "agent-verified"; break;
            case "agent-boolean-property": assertion.Property = "agentPassed"; break;
            case "status-property": assertion.Property = "status"; break;
            case "unmapped-source": assertion.SourceEvidence = ".migration-evidence/other.json"; break;
            case "missing-index": assertion.ProbeIndex = 99; break;
        }
        await EnforcementStore.WriteAtomicAsync(fixture.Root, ClosurePlan, plan);
        var probe = await CaptureClosureProbeAsync(fixture);

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            UwpEnforcementRunner.RecordStateAsync(fixture.Root, TestWindowOwnerResolver.Shared, "state-1", [probe.ArtifactPath]));

        Assert.AreEqual("unverified", (await fixture.LoadAsync<StatePlanDocument>(ClosurePlan)).States[0].Target.Status);
    }

    [TestMethod]
    public async Task RecordState_ActionsCannotSupplyAssertionsEvenWithPlausibleJson()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        await new UwpEnforcementRunner(SuccessfulLaunch()).RunAsync(fixture.Root, new UwpEnforcementOptions());
        var probe = await new UwpEnforcementRunner(new FakeProcessRunner(
            new EnforcementProcessResult(0, ClosureTree, "")),
            windowOwnerResolver: new TestWindowOwnerResolver(4242)).InvokeUiAsync(
                fixture.Root, "winapp", ["invoke", "Ready", "--json", "-w", "42"], [], "state-1");

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            UwpEnforcementRunner.RecordStateAsync(fixture.Root, TestWindowOwnerResolver.Shared, "state-1", [probe.ArtifactPath]));
    }

    [TestMethod]
    [DataRow("missing-attestation")]
    [DataRow("fabricated-attestation")]
    [DataRow("modified-attestation")]
    [DataRow("changed-assertions")]
    [DataRow("modified-probe")]
    [DataRow("modified-output")]
    [DataRow("removed-operation-receipt")]
    public async Task Closure_ManualJsonEditsCannotCloseOrPreserveClosure(string edit)
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var probe = await CaptureClosureProbeAsync(fixture);
        await UwpEnforcementRunner.RecordStateAsync(fixture.Root, TestWindowOwnerResolver.Shared, "state-1", [probe.ArtifactPath]);
        var complete = await UwpEnforcementRunner.SynchronizeReportAsync(fixture.Root, TestWindowOwnerResolver.Shared);
        Assert.AreEqual("complete", complete.Disposition?.Kind);
        var plan = await fixture.LoadAsync<StatePlanDocument>(ClosurePlan);
        switch (edit)
        {
            case "missing-attestation":
                File.Delete(fixture.Path(plan.States[0].Target.Attestation!)); break;
            case "fabricated-attestation":
            case "modified-attestation":
                var attestation = await fixture.LoadAsync<StateClosureAttestation>(plan.States[0].Target.Attestation!);
                if (edit == "fabricated-attestation") attestation.Producer = "agent";
                else attestation.RecordedAt = DateTimeOffset.UtcNow;
                await EnforcementStore.WriteAtomicAsync(fixture.Root, plan.States[0].Target.Attestation!, attestation);
                break;
            case "changed-assertions":
                plan.States[0].Assertions[0].Property = "value";
                await EnforcementStore.WriteAtomicAsync(fixture.Root, ClosurePlan, plan);
                break;
            case "modified-probe":
                probe.ExitCode = 1;
                await EnforcementStore.WriteAtomicAsync(fixture.Root, probe.ArtifactPath, probe);
                break;
            case "modified-output":
                await File.WriteAllTextAsync(fixture.Path(probe.StandardOutputPath), ClosureTree + " ");
                break;
            case "removed-operation-receipt":
                var progress = await fixture.LoadAsync<WorkflowProgress>(EnforcementStore.ProgressPath);
                progress.Operations.Single(operation => operation.ProbeId == probe.Id).ProbeSha256 = null;
                await EnforcementStore.WriteAtomicAsync(fixture.Root, EnforcementStore.ProgressPath, progress);
                break;
        }

        var reassessed = await UwpEnforcementRunner.AssessAsync(fixture.Root, TestWindowOwnerResolver.Shared);

        Assert.AreNotEqual("passed", reassessed.Status);
        Assert.AreNotEqual("complete", reassessed.Disposition?.Kind);
        Assert.AreEqual("blocked", reassessed.Gates.Single(gate => gate.Id == "state-closure").Status);
    }

    [TestMethod]
    public async Task RecordState_ReceiptSealRejectsCoordinatedJsonAndWorkflowHashForgery()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var probe = await CaptureClosureProbeAsync(fixture);
        await File.WriteAllTextAsync(fixture.Path(probe.StandardOutputPath), ClosureTree + " ");
        probe.StandardOutputSha256 = StateClosureProtocol.FileHash(fixture.Root, probe.StandardOutputPath);
        await EnforcementStore.WriteAtomicAsync(fixture.Root, probe.ArtifactPath, probe);
        var progress = await fixture.LoadAsync<WorkflowProgress>(EnforcementStore.ProgressPath);
        progress.Operations.Single(operation => operation.ProbeId == probe.Id).ProbeSha256 =
            StateClosureProtocol.FileHash(fixture.Root, probe.ArtifactPath);
        await EnforcementStore.WriteAtomicAsync(fixture.Root, EnforcementStore.ProgressPath, progress);

        var error = await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            UwpEnforcementRunner.RecordStateAsync(fixture.Root, TestWindowOwnerResolver.Shared, "state-1", [probe.ArtifactPath]));

        StringAssert.Contains(error.Message, "integrity seal");
    }

    [TestMethod]
    public async Task RecordState_RealReceiptForAnotherPlannedStateCannotBeRelabeled()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var plan = await fixture.LoadAsync<StatePlanDocument>(ClosurePlan);
        var second = JsonSerializer.Deserialize<MigrationState>(
            JsonSerializer.Serialize(plan.States[0], EnforcementStore.JsonOptions), EnforcementStore.JsonOptions)!;
        second.Id = "state-2";
        plan.States.Add(second);
        await EnforcementStore.WriteAtomicAsync(fixture.Root, ClosurePlan, plan);
        var probe = await CaptureClosureProbeAsync(fixture, stateId: "state-2");

        var error = await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            UwpEnforcementRunner.RecordStateAsync(fixture.Root, TestWindowOwnerResolver.Shared, "state-1", [probe.ArtifactPath]));

        StringAssert.Contains(error.Message, "planned state identity");
    }

    [TestMethod]
    [DataRow("\"True\"", true)]
    [DataRow("\"False\"", false)]
    [DataRow("true", false)]
    public async Task RecordState_SelectionUsesObservedTypedPropertiesNotAgentAuthoredVerdicts(string targetValue, bool matches)
    {
        using var fixture = await RunnerFixture.CreateAsync();
        const string selectionTree = """{"elementId":"main","properties":{"IsSelected":"True"}}""";
        await File.WriteAllTextAsync(fixture.Path(".migration-evidence/states/state-1/source-ui.json"), selectionTree);
        var plan = await fixture.LoadAsync<StatePlanDocument>(ClosurePlan);
        plan.States[0].Assertions[0].Property = "isSelected";
        await EnforcementStore.WriteAtomicAsync(fixture.Root, ClosurePlan, plan);
        var probe = await CaptureClosureProbeAsync(fixture, new EnforcementProcessResult(0,
            selectionTree.Replace("\"True\"", targetValue, StringComparison.Ordinal), ""),
            arguments: ["get-property", "main", "-p", "IsSelected", "-w", "42", "--json"]);

        if (matches)
        {
            var snapshot = await UwpEnforcementRunner.RecordStateAsync(fixture.Root, TestWindowOwnerResolver.Shared, "state-1", [probe.ArtifactPath]);
            Assert.AreEqual("passed", snapshot.Gates.Single(gate => gate.Id == "state-closure").Status);
            Assert.AreEqual("passed", (await UwpEnforcementRunner.SynchronizeReportAsync(fixture.Root, TestWindowOwnerResolver.Shared)).Status);
        }
        else
        {
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
                UwpEnforcementRunner.RecordStateAsync(fixture.Root, TestWindowOwnerResolver.Shared, "state-1", [probe.ArtifactPath]));
        }
    }

    [TestMethod]
    public async Task StatePlan_AgentAuthoredVerdictFieldsAreNotAnAssertionLanguage()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var plan = JsonNode.Parse(await File.ReadAllTextAsync(fixture.Path(ClosurePlan)))!;
        plan["states"]![0]!["assertions"]![0]!["passed"] = true;
        await File.WriteAllTextAsync(fixture.Path(ClosurePlan), plan.ToJsonString());

        var snapshot = await UwpEnforcementRunner.AssessAsync(fixture.Root, TestWindowOwnerResolver.Shared);

        Assert.AreEqual("state-plan", snapshot.Frontier?.Kind);
        Assert.AreEqual("blocked", snapshot.Gates.Single(gate => gate.Id == "state-plan").Status);
    }

    [TestMethod]
    public async Task Closure_ManuallyPassingStatusesDoNotVerifyMappedTodo()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var report = await fixture.LoadReportAsync();
        report.Todos.Add(new MigrationTodo
        {
            Id = "UWMIG-MANUAL", Category = "behavior", Priority = "required",
            Summary = "Must not trust text.", Reason = "Requires derived closure."
        });
        await fixture.SaveReportAsync(report);
        await new UwpEnforcementRunner(SuccessfulLaunch()).RunAsync(fixture.Root, new UwpEnforcementOptions());
        await fixture.WriteTargetEvidenceAsync();
        var plan = await fixture.LoadAsync<StatePlanDocument>(ClosurePlan);
        var state = plan.States[0];
        state.Target.Status = "passed";
        state.Comparison.Status = "verified";
        state.TodoIds = ["UWMIG-MANUAL"];
        state.Target.Evidence = [".migration-evidence/states/state-1/target.png", ".migration-evidence/states/state-1/stdout.log"];
        await EnforcementStore.WriteAtomicAsync(fixture.Root, ClosurePlan, plan);

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            UwpEnforcementRunner.SynchronizeReportAsync(fixture.Root, TestWindowOwnerResolver.Shared));
        var snapshot = await UwpEnforcementRunner.AssessAsync(fixture.Root, TestWindowOwnerResolver.Shared);

        Assert.AreEqual("blocked", snapshot.Gates.Single(gate => gate.Id == "state-closure").Status);
        Assert.AreEqual("pending", (await fixture.LoadReportAsync()).Todos.Single().Status);
    }

    [TestMethod]
    [DataRow("captured", "target")]
    [DataRow("passed", "source")]
    [DataRow("passed", "comparison")]
    [DataRow("unknown", "target")]
    public async Task StatePlan_ObservationEnumsAreValidatedBeforeSummary(string status, string side)
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var plan = await fixture.LoadAsync<StatePlanDocument>(ClosurePlan);
        if (side == "target") plan.States[0].Target.Status = status;
        if (side == "source") plan.States[0].Source.Status = status;
        if (side == "comparison") plan.States[0].Comparison.Status = status;
        await EnforcementStore.WriteAtomicAsync(fixture.Root, ClosurePlan, plan);

        var snapshot = await UwpEnforcementRunner.AssessAsync(fixture.Root, TestWindowOwnerResolver.Shared);

        Assert.AreEqual("state-plan", snapshot.Frontier?.Kind);
        Assert.ThrowsExactly<InvalidDataException>(() => EnforcementEvaluator.SummarizeStatePlan(plan));
    }

    [TestMethod]
    public async Task RecordState_OnlySatisfiedMappedFindingContractsAreResolved()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var findings = new SemanticFindingLedger
        {
            Findings =
            [
                ClosureFinding("machine", "main-name"),
                ClosureFinding("missing-assertion", "unknown"),
                ClosureFinding("prose", null)
            ]
        };
        await EnforcementStore.WriteAtomicAsync(fixture.Root, EnforcementStore.FindingLedgerPath, findings);
        var probe = await CaptureClosureProbeAsync(fixture);

        var snapshot = await UwpEnforcementRunner.RecordStateAsync(fixture.Root, TestWindowOwnerResolver.Shared, "state-1", [probe.ArtifactPath]);

        findings = await fixture.LoadAsync<SemanticFindingLedger>(EnforcementStore.FindingLedgerPath);
        Assert.AreEqual("resolved", findings.Findings[0].Status);
        Assert.IsEmpty(findings.Findings[0].NextActions);
        Assert.AreEqual("open", findings.Findings[1].Status);
        Assert.AreEqual("open", findings.Findings[2].Status);
        Assert.AreNotEqual("passed", snapshot.Status);
        findings.Findings[2].Status = "resolved";
        findings.Findings[2].NextActions = [];
        findings.Findings[2].ValidationEvidence = [probe.ArtifactPath];
        await EnforcementStore.WriteAtomicAsync(fixture.Root, EnforcementStore.FindingLedgerPath, findings);
        var forged = await UwpEnforcementRunner.AssessAsync(fixture.Root, TestWindowOwnerResolver.Shared);
        Assert.AreEqual("blocked", forged.Gates.Single(gate => gate.Id == "findings").Status);
    }

    [TestMethod]
    [DataRow(false, PngInformation.Informative)]
    [DataRow(true, PngInformation.LowInformation)]
    public async Task PngEvidence_ClassifiesDecodedInformation(bool uniform, PngInformation expected)
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var path = fixture.Path(".migration-evidence/image.png");
        await File.WriteAllBytesAsync(path, PngTestData.Create(uniform));
        Assert.AreEqual(expected, PngEvidence.Classify(path));
        await File.WriteAllBytesAsync(path, PngTestData.Create(size: 1));
        Assert.AreEqual(PngInformation.LowInformation, PngEvidence.Classify(path));
        var corrupted = PngTestData.Create();
        corrupted[corrupted.Length / 2] ^= 0xff;
        await File.WriteAllBytesAsync(path, corrupted);
        Assert.AreEqual(PngInformation.Invalid, PngEvidence.Classify(path));
    }

    [TestMethod]
    public async Task PngEvidence_LowInformationCannotSubstituteForMachineClosure()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        await new UwpEnforcementRunner(SuccessfulLaunch()).RunAsync(fixture.Root, new UwpEnforcementOptions());
        await fixture.WriteTargetEvidenceAsync();
        await File.WriteAllBytesAsync(fixture.Path(".migration-evidence/states/state-1/target.png"), PngTestData.Create(uniform: true));
        var plan = await fixture.LoadAsync<StatePlanDocument>(ClosurePlan);
        plan.States[0].Target.Evidence =
            [".migration-evidence/states/state-1/target.png", ".migration-evidence/states/state-1/stdout.log"];
        await EnforcementStore.WriteAtomicAsync(fixture.Root, ClosurePlan, plan);

        var error = await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            UwpEnforcementRunner.SynchronizeReportAsync(fixture.Root, TestWindowOwnerResolver.Shared));

        StringAssert.Contains(error.Message, "low-information");
    }

    [TestMethod]
    public async Task PngEvidence_LowInformationAndIntentionalRestorationAllowStructuredClosure()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        await File.WriteAllBytesAsync(fixture.Path(".migration-evidence/states/state-1/source.png"), PngTestData.Create(uniform: true));
        var plan = await fixture.LoadAsync<StatePlanDocument>(ClosurePlan);
        plan.States[0].Assertions.Add(new StateAssertion
        {
            Id = "restored-main-name", Kind = "ui-property-equals-source", ProbeIndex = 2,
            SourceEvidence = ".migration-evidence/states/state-1/source-ui.json", AutomationId = "main", Property = "name"
        });
        await EnforcementStore.WriteAtomicAsync(fixture.Root, ClosurePlan, plan);
        var first = await CaptureClosureProbeAsync(fixture);
        var runner = new UwpEnforcementRunner(new RestorationProbeRunner(),
            windowOwnerResolver: new TestWindowOwnerResolver(4242));
        var firstImage = await runner.InvokeUiAsync(fixture.Root, "winapp",
            ["screenshot", "-o", ".migration-evidence/restoration-1.png", "-w", "42"], [], "state-1");
        var second = await runner.InvokeUiAsync(fixture.Root, "winapp", ["inspect", "--json", "-w", "42"], [], "state-1");
        var secondImage = await runner.InvokeUiAsync(fixture.Root, "winapp",
            ["screenshot", "-o", ".migration-evidence/restoration-2.png", "-w", "42"], [], "state-1");

        await UwpEnforcementRunner.RecordStateAsync(fixture.Root, TestWindowOwnerResolver.Shared, "state-1",
            [first.ArtifactPath, firstImage.ArtifactPath, second.ArtifactPath, secondImage.ArtifactPath]);
        var complete = await UwpEnforcementRunner.SynchronizeReportAsync(fixture.Root, TestWindowOwnerResolver.Shared);

        Assert.AreEqual("passed", complete.Status,
            "Matching restoration observations and low-information screenshots are not themselves a failure of nonvisual assertions.");
    }

    private sealed class RestorationProbeRunner : IEnforcementProcessRunner
    {
        public async Task<EnforcementProcessResult> RunAsync(string executable, DirectoryInfo workingDirectory,
            IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
        {
            if (arguments[1] != "screenshot")
                return new EnforcementProcessResult(0, ClosureTree, "");
            await File.WriteAllBytesAsync(System.IO.Path.Combine(workingDirectory.FullName,
                arguments[3].Replace('/', System.IO.Path.DirectorySeparatorChar)), PngTestData.Create(uniform: true), cancellationToken);
            return new EnforcementProcessResult(0, """{"success":true}""", "");
        }
    }

    private static SemanticFinding ClosureFinding(string id, string? assertionId) => new()
    {
        Id = id, Category = "behavior", StateIds = ["state-1"], Status = "open",
        SourceInvariant = "Main state retains its source observable name.",
        CompletionCondition = "Verify the named observation.", NextActions = ["Capture and compare."],
        CompletionAssertions = assertionId is null ? [] :
            [new FindingCompletionAssertion { StateId = "state-1", AssertionId = assertionId }]
    };

    private static async Task<UiDevelopmentProbe> CaptureClosureProbeAsync(
        RunnerFixture fixture, EnforcementProcessResult? result = null, string? stateId = "state-1",
        IReadOnlyList<string>? arguments = null)
    {
        await new UwpEnforcementRunner(SuccessfulLaunch()).RunAsync(fixture.Root, new UwpEnforcementOptions());
        return await new UwpEnforcementRunner(new FakeProcessRunner(
            result ?? new EnforcementProcessResult(0, ClosureTree, "")),
            windowOwnerResolver: new TestWindowOwnerResolver(4242)).InvokeUiAsync(
                fixture.Root, "winapp", arguments ?? ["inspect", "--json", "-w", "42"], [], stateId);
    }
}
