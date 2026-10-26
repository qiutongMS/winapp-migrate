// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Nodes;
using WinAppMigration.Cli.Pipelines.Uwp;
using WinAppMigration.Enforcement;

namespace UwpMigration.Enforcement.Tests;

public sealed partial class RunnerAcceptanceTests
{
    [TestMethod]
    public async Task Policy_CanConfigureBeforeOperationsButNotAfter()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        await UwpEnforcementRunner.AssessAsync(fixture.Root);
        var policy = EnforcementPolicy.Balanced();
        policy.SemanticConvergence.SoftMinutes = 14;
        await UwpEnforcementRunner.ConfigurePolicyAsync(fixture.Root, policy);
        var persisted = await fixture.LoadAsync<EnforcementPolicy>(EnforcementStore.PolicyPath);
        Assert.AreEqual(14, persisted.SemanticConvergence.SoftMinutes);
        var process = SuccessfulLaunch();
        await new UwpEnforcementRunner(process).RunAsync(fixture.Root, new UwpEnforcementOptions());
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            UwpEnforcementRunner.ConfigurePolicyAsync(fixture.Root, EnforcementPolicy.Balanced()));
        var progress = await fixture.LoadAsync<WorkflowProgress>(EnforcementStore.ProgressPath);
        Assert.IsNotNull(progress.OperationsStartedAt);
        Assert.HasCount(2, progress.Operations);
        Assert.AreEqual(1, progress.Units.Single(unit => unit.Stage == WorkflowStage.MechanicalStabilization).Operations.Builds);
        Assert.AreEqual(1, progress.Units.Single(unit => unit.Stage == WorkflowStage.TargetBootstrap).Operations.Replays);
        Assert.AreEqual(WorkflowStage.MechanicalStabilization, progress.Operations[0].Stage);
        Assert.AreEqual(WorkflowStage.TargetBootstrap, progress.Operations[1].Stage);
    }

    [TestMethod]
    public async Task Policy_DirectReplacementAfterOperationsFailsClosed()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var process = SuccessfulLaunch();
        var runner = new UwpEnforcementRunner(process);
        await runner.RunAsync(fixture.Root, new UwpEnforcementOptions());
        var changed = EnforcementPolicy.Balanced();
        changed.TotalHardMinutes++;
        await EnforcementStore.WriteAtomicAsync(fixture.Root, EnforcementStore.PolicyPath, changed);
        var snapshot = await runner.RunAsync(fixture.Root, new UwpEnforcementOptions { ForceReplay = true });
        Assert.AreEqual("evidence", snapshot.Frontier?.Kind);
        StringAssert.Contains(snapshot.Gates.Single(gate => gate.Id == "enforcement-artifacts").Reason, "immutable");
        Assert.AreEqual(2, process.CallCount);
    }

    [TestMethod]
    [DataRow("{")]
    [DataRow("{}")]
    [DataRow("null")]
    [DataRow("""{"schemaVersion":"1.0","schemaVersion":"2.0"}""")]
    public async Task Policy_MalformedDocumentBlocksAllOperations(string json)
    {
        using var fixture = await RunnerFixture.CreateAsync();
        await EnforcementStore.WriteTextAtomicAsync(fixture.Root, EnforcementStore.PolicyPath, json);
        var process = new FakeProcessRunner();
        var snapshot = await new UwpEnforcementRunner(process).RunAsync(
            fixture.Root, new UwpEnforcementOptions { ForceReplay = true });
        Assert.AreEqual("evidence", snapshot.Frontier?.Kind);
        Assert.AreEqual(0, process.CallCount);
        Assert.AreEqual(json, await File.ReadAllTextAsync(fixture.Path(EnforcementStore.PolicyPath)));
    }

    [TestMethod]
    [DataRow("unknown")]
    [DataRow("null-stage")]
    [DataRow("missing-limit")]
    [DataRow("unsupported")]
    [DataRow("negative")]
    [DataRow("extensions")]
    [DataRow("missing-extensions-per-unit")]
    [DataRow("legacy-extensions")]
    public async Task Policy_UnsupportedOrInvalidFieldsFailClosed(string mutation)
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var node = JsonSerializer.SerializeToNode(EnforcementPolicy.Balanced(), EnforcementStore.JsonOptions)!.AsObject();
        switch (mutation)
        {
            case "unknown": node["unsupportedFeature"] = true; break;
            case "null-stage": node["semanticConvergence"] = null; break;
            case "missing-limit": node.Remove("totalHardMinutes"); break;
            case "unsupported": node["schemaVersion"] = "2.0"; break;
            case "negative": node["sourceOracle"]!["operations"]!["builds"] = -1; break;
            case "extensions": node["maximumProgressExtensionsPerUnit"] = 2; break;
            case "missing-extensions-per-unit": node.Remove("maximumProgressExtensionsPerUnit"); break;
            case "legacy-extensions":
                node.Remove("maximumProgressExtensionsPerUnit");
                node["maximumProgressExtensions"] = 1;
                break;
            default: Assert.Fail("Unknown test mutation."); break;
        }
        await EnforcementStore.WriteTextAtomicAsync(fixture.Root, EnforcementStore.PolicyPath, node.ToJsonString());
        var process = new FakeProcessRunner();
        var snapshot = await new UwpEnforcementRunner(process).RunAsync(fixture.Root, new UwpEnforcementOptions());
        Assert.AreEqual("evidence", snapshot.Frontier?.Kind);
        Assert.IsNull(snapshot.Workflow);
        Assert.AreEqual(0, process.CallCount);
    }

    [TestMethod]
    [DataRow(WorkflowStage.SourceOracle, WorkflowOperation.Build)]
    [DataRow(WorkflowStage.SourceOracle, WorkflowOperation.Replay)]
    [DataRow(WorkflowStage.SourceOracle, WorkflowOperation.DiagnosticRun)]
    [DataRow(WorkflowStage.SourceOracle, WorkflowOperation.Hypothesis)]
    [DataRow(WorkflowStage.SourceOracle, WorkflowOperation.UiAction)]
    [DataRow(WorkflowStage.MechanicalStabilization, WorkflowOperation.Replay)]
    [DataRow(WorkflowStage.MechanicalStabilization, WorkflowOperation.DiagnosticRun)]
    [DataRow(WorkflowStage.MechanicalStabilization, WorkflowOperation.Hypothesis)]
    [DataRow(WorkflowStage.MechanicalStabilization, WorkflowOperation.UiAction)]
    [DataRow(WorkflowStage.TargetBootstrap, WorkflowOperation.Build)]
    [DataRow(WorkflowStage.TargetBootstrap, WorkflowOperation.UiAction)]
    [DataRow(WorkflowStage.Closure, WorkflowOperation.DiagnosticRun)]
    [DataRow(WorkflowStage.Closure, WorkflowOperation.Hypothesis)]
    public async Task Policy_IncompatibleStageCeilingsFailBeforeInvocation(
        WorkflowStage stage, WorkflowOperation operation)
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var policy = EnforcementPolicy.Balanced();
        policy.For(stage).Operations.Increment(operation);
        StringAssert.Contains(WorkflowValidation.PolicyError(policy)!, "zero ceilings");
        await EnforcementStore.WriteAtomicAsync(fixture.Root, EnforcementStore.PolicyPath, policy);
        var process = new FakeProcessRunner();
        var snapshot = await new UwpEnforcementRunner(process).RunAsync(fixture.Root,
            new UwpEnforcementOptions { ForceReplay = true });
        Assert.AreEqual("evidence", snapshot.Frontier?.Kind);
        Assert.IsNull(snapshot.Workflow);
        Assert.AreEqual(0, process.CallCount);
    }

    [TestMethod]
    [DataRow("schema")]
    [DataRow("counts")]
    [DataRow("null-units")]
    [DataRow("invalid-event")]
    [DataRow("null-epoch")]
    [DataRow("overlapping-epochs")]
    [DataRow("missing-disposition")]
    public async Task Progress_InvalidPersistedProgressFailsArtifactGate(string mutation)
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var assessed = await UwpEnforcementRunner.AssessAsync(fixture.Root);
        var node = JsonSerializer.SerializeToNode(assessed.Workflow, EnforcementStore.JsonOptions)!.AsObject();
        switch (mutation)
        {
            case "schema": node["schemaVersion"] = "3.0"; break;
            case "counts": node["units"]![0]!["operations"]!["builds"] = 1; break;
            case "null-units": node["units"] = null; break;
            case "null-epoch": node["units"]![0]!["epochs"]![0] = null; break;
            case "overlapping-epochs":
                node["units"]![0]!["epochs"]!.AsArray().Add(node["units"]![0]!["epochs"]![0]!.DeepClone());
                break;
            case "missing-disposition": node.Remove("disposition"); break;
            case "invalid-event":
                node["events"] = new JsonArray(new JsonObject
                {
                    ["kind"] = "nearly-done", ["key"] = "agent-assertion",
                    ["stage"] = "target-bootstrap", ["recordedAt"] = DateTimeOffset.UtcNow
                });
                break;
            default: Assert.Fail("Unknown test mutation."); break;
        }
        await EnforcementStore.WriteTextAtomicAsync(fixture.Root, EnforcementStore.ProgressPath, node.ToJsonString());
        var snapshot = await UwpEnforcementRunner.AssessAsync(fixture.Root);
        Assert.AreEqual("evidence", snapshot.Frontier?.Kind);
    }

    [TestMethod]
    public async Task Progress_ResumesSemanticStageAndPreservesCountersAfterCorrection()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var process = new FakeProcessRunner(
            BuildSuccess(), ReplaySuccess(), BuildSuccess(), ReplaySuccess());
        var runner = new UwpEnforcementRunner(process);
        var first = await runner.RunAsync(fixture.Root, new UwpEnforcementOptions());
        Assert.AreEqual(WorkflowStage.SemanticConvergence, first.Stage);
        var started = first.Workflow!.Units.Single(unit => unit.Stage == WorkflowStage.SemanticConvergence).StartedAt;
        await File.AppendAllTextAsync(fixture.ProjectPath, Environment.NewLine);
        var resumed = await new UwpEnforcementRunner(process).RunAsync(fixture.Root, new UwpEnforcementOptions());
        Assert.AreEqual(WorkflowStage.SemanticConvergence, resumed.Stage);
        var unit = resumed.Workflow!.Units.Single(item => item.Stage == WorkflowStage.SemanticConvergence);
        Assert.AreEqual(started, unit.StartedAt);
        Assert.AreEqual(1, unit.Operations.Builds);
        Assert.AreEqual(1, unit.Operations.Replays);
        CollectionAssert.AreEqual(
            new[] { WorkflowStage.MechanicalStabilization, WorkflowStage.TargetBootstrap,
                WorkflowStage.SemanticConvergence, WorkflowStage.SemanticConvergence },
            resumed.Workflow.Operations.Select(operation => operation.Stage).ToArray());
        Assert.IsTrue(resumed.Workflow.Operations.Skip(2).All(operation => operation.SliceId == "state-1"));
        Assert.AreEqual(4, resumed.Budget!.TotalOperations.Builds + resumed.Budget.TotalOperations.Replays);
        Assert.AreEqual("unverified", (await fixture.LoadAsync<StatePlanDocument>(
            ".migration-evidence/state-plan.json")).States[0].Target.Status);
    }

    [TestMethod]
    public async Task Run_SourceOracleMustPrecedeBuild()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var plan = await fixture.LoadAsync<StatePlanDocument>(".migration-evidence/state-plan.json");
        plan.States[0].Source.Status = "unverified";
        await EnforcementStore.WriteAtomicAsync(fixture.Root, ".migration-evidence/state-plan.json", plan);
        var process = new FakeProcessRunner();
        var snapshot = await new UwpEnforcementRunner(process).RunAsync(fixture.Root, new UwpEnforcementOptions());
        Assert.AreEqual(WorkflowStage.SourceOracle, snapshot.Stage);
        Assert.AreEqual("source-oracle", snapshot.Frontier?.Kind);
        Assert.AreEqual(0, process.CallCount);
        Assert.IsNotNull(snapshot.Budget!.SoftDeadline);
    }

    [TestMethod]
    public async Task Operations_SourceOracleRegressionBlocksHistoricalTargetReplay()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var process = SuccessfulLaunch();
        var runner = new UwpEnforcementRunner(process);
        await runner.RunAsync(fixture.Root, new UwpEnforcementOptions());
        var plan = await fixture.LoadAsync<StatePlanDocument>(".migration-evidence/state-plan.json");
        plan.States[0].Source.Status = "unverified";
        plan.States[0].Source.Evidence.Clear();
        plan.States[0].Source.Reason = "Source evidence was invalidated.";
        await EnforcementStore.WriteAtomicAsync(
            fixture.Root, ".migration-evidence/state-plan.json", plan);

        var exception = await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            runner.InvokeUiAsync(fixture.Root, "winapp", ["list"]));

        StringAssert.Contains(exception.Message, "source-oracle evidence");
        Assert.AreEqual(2, process.CallCount);
    }

    [TestMethod]
    public async Task Run_UnchangedBuildFailureCannotBeBypassedByNewInvocationOrMaxAttempts()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var process = new FakeProcessRunner(new EnforcementProcessResult(1, """{"Error":"compile failure"}""", ""));
        await new UwpEnforcementRunner(process).RunAsync(fixture.Root, new UwpEnforcementOptions());
        var snapshot = await new UwpEnforcementRunner(process).RunAsync(
            fixture.Root, new UwpEnforcementOptions { MaximumSignatureAttempts = 10, ForceReplay = true });
        Assert.AreEqual("change-required", snapshot.Frontier?.Kind);
        Assert.AreEqual(1, process.CallCount);
        Assert.AreEqual(1, snapshot.Budget!.TotalOperations.Builds);
    }

    [TestMethod]
    public async Task Run_ForceReplayRequiresNewDiscriminatingEvidence()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var process = SuccessfulLaunch();
        var runner = new UwpEnforcementRunner(process);
        await runner.RunAsync(fixture.Root, new UwpEnforcementOptions());
        var snapshot = await runner.RunAsync(fixture.Root, new UwpEnforcementOptions { ForceReplay = true });
        Assert.AreEqual("evidence-required", snapshot.Frontier?.Kind);
        Assert.AreEqual(2, process.CallCount);
        StringAssert.Contains(snapshot.Frontier!.Reason, "discriminating evidence");
    }

    [TestMethod]
    public async Task Run_ReplayCannotReuseOrRenameEvidenceContent()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var process = new FakeProcessRunner(BuildSuccess(), ReplaySuccess(), ReplaySuccess());
        var runner = new UwpEnforcementRunner(process);
        await runner.RunAsync(fixture.Root, new UwpEnforcementOptions());
        var path = ".migration-evidence/discriminator.txt";
        await File.WriteAllTextAsync(fixture.Path(path), "Observed navigation discriminates a new hypothesis.");
        await runner.RunAsync(fixture.Root, new UwpEnforcementOptions
        {
            ForceReplay = true, DiscriminatingEvidence = [path]
        });
        var copy = ".migration-evidence/renamed-discriminator.txt";
        await File.WriteAllTextAsync(fixture.Path(copy), await File.ReadAllTextAsync(fixture.Path(path)));
        var blocked = await runner.RunAsync(fixture.Root, new UwpEnforcementOptions
        {
            ForceReplay = true, DiscriminatingEvidence = [copy]
        });
        Assert.AreEqual("evidence-required", blocked.Frontier?.Kind);
        Assert.AreEqual(3, process.CallCount);
    }

    [TestMethod]
    public async Task Diagnosis_RejectsOverwriteAndFailureRerecording()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        await new UwpEnforcementRunner(SuccessfulLaunch()).RunAsync(fixture.Root, new UwpEnforcementOptions());
        await RecordDiagnosedFailureAsync(fixture);
        var before = await File.ReadAllTextAsync(fixture.Path(EnforcementStore.AttemptLedgerPath));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => RecordDiagnosisAsync(fixture));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            UwpEnforcementRunner.RecordFailureAsync(fixture.Root, "runtime", "crash",
                "state-1", [".migration-evidence/failure.txt"]));
        Assert.AreEqual(before, await File.ReadAllTextAsync(fixture.Path(EnforcementStore.AttemptLedgerPath)));
        var attempt = (await fixture.LoadAsync<AttemptLedger>(EnforcementStore.AttemptLedgerPath)).Attempts.Single();
        Assert.AreEqual(attempt.TargetFingerprint, attempt.Diagnosis!.TargetFingerprint);
        Assert.AreEqual(attempt.Signature, attempt.Diagnosis.Signature);
        Assert.AreEqual("state-1", attempt.Diagnosis.StateId);
        Assert.AreEqual("state-1", attempt.Diagnosis.SliceId);
    }

    [TestMethod]
    public async Task Diagnosis_RequiresTargetChangeAndForceCannotBypass()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var process = SuccessfulLaunch();
        var runner = new UwpEnforcementRunner(process);
        await runner.RunAsync(fixture.Root, new UwpEnforcementOptions());
        await RecordDiagnosedFailureAsync(fixture);
        var blocked = await runner.RunAsync(fixture.Root, new UwpEnforcementOptions { ForceReplay = true });
        Assert.AreEqual("change-required", blocked.Frontier?.Kind);
        Assert.AreEqual(2, process.CallCount);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            runner.DiagnoseAsync(fixture.Root, new UwpEnforcementOptions()));
        await File.AppendAllTextAsync(fixture.ProjectPath, Environment.NewLine);
        var corrected = await UwpEnforcementRunner.AssessAsync(fixture.Root);
        Assert.AreEqual("build", corrected.Frontier?.Kind);
        Assert.AreEqual(WorkflowStage.SemanticConvergence, corrected.Stage);
    }

    [TestMethod]
    public async Task Diagnosis_TargetChangeBeforeDiagnosisDoesNotAuthorizeBuildOrRebinding()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        await RecordRuntimeFailureAsync(fixture);
        await File.AppendAllTextAsync(fixture.ProjectPath, Environment.NewLine);
        var process = new FakeProcessRunner();
        var snapshot = await new UwpEnforcementRunner(process).RunAsync(
            fixture.Root, new UwpEnforcementOptions { ForceReplay = true });
        Assert.AreEqual("diagnosis-required", snapshot.Frontier?.Kind);
        Assert.AreEqual(0, process.CallCount);
        await File.WriteAllTextAsync(fixture.Path(".migration-evidence/diagnosis.txt"), "Fresh diff");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => RecordDiagnosisAsync(fixture));
    }

    [TestMethod]
    public async Task Diagnosis_DebugWorkCannotRepeatForSameFingerprint()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var process = new FakeProcessRunner(BuildSuccess(), ReplaySuccess(), new EnforcementProcessResult(0, "debug", ""));
        var runner = new UwpEnforcementRunner(process);
        await runner.RunAsync(fixture.Root, new UwpEnforcementOptions());
        await runner.DiagnoseAsync(fixture.Root, new UwpEnforcementOptions());
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            runner.DiagnoseAsync(fixture.Root, new UwpEnforcementOptions()));
        Assert.AreEqual(3, process.CallCount);
    }

    [TestMethod]
    public async Task Budget_ExpiredPersistedStageStopsRunAndUi()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var process = SuccessfulLaunch();
        var runner = new UwpEnforcementRunner(process);
        await runner.RunAsync(fixture.Root, new UwpEnforcementOptions());
        var progress = await fixture.LoadAsync<WorkflowProgress>(EnforcementStore.ProgressPath);
        AgeWorkflow(progress, 26);
        await EnforcementStore.WriteAtomicAsync(fixture.Root, EnforcementStore.ProgressPath, progress);
        var snapshot = await runner.RunAsync(fixture.Root, new UwpEnforcementOptions { ForceReplay = true });
        Assert.AreEqual("blocked-required", snapshot.Budget?.Decision);
        Assert.AreEqual("stage-budget", snapshot.Frontier?.Kind);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            runner.InvokeUiAsync(fixture.Root, "winapp", ["list"]));
        Assert.AreEqual(2, process.CallCount);
    }

    [TestMethod]
    public async Task Budget_OperationCeilingCannotBeBypassedByForceReplay()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var policy = EnforcementPolicy.Balanced();
        policy.SemanticConvergence.Operations.Replays = 0;
        await UwpEnforcementRunner.ConfigurePolicyAsync(fixture.Root, policy);
        var process = SuccessfulLaunch();
        var runner = new UwpEnforcementRunner(process);
        await runner.RunAsync(fixture.Root, new UwpEnforcementOptions());
        await File.WriteAllTextAsync(fixture.Path(".migration-evidence/discriminator.txt"), "New evidence");
        var snapshot = await runner.RunAsync(fixture.Root, new UwpEnforcementOptions
        {
            ForceReplay = true, DiscriminatingEvidence = [".migration-evidence/discriminator.txt"]
        });
        Assert.AreEqual("stage-budget", snapshot.Frontier?.Kind);
        Assert.AreEqual(2, process.CallCount);
    }

    [TestMethod]
    public async Task Cli_ConfigureWritesDefaultAndAcceptsJsonPolicy()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var command = new UwpEnforceCommand(new FakeProcessRunner());
        var defaults = command.Parse(["configure", fixture.Root.FullName, "--write-default"]);
        Assert.AreEqual(0, await defaults.InvokeAsync(defaults.InvocationConfiguration, CancellationToken.None));
        var policy = await fixture.LoadAsync<EnforcementPolicy>(EnforcementStore.PolicyPath);
        Assert.AreEqual(20, policy.SourceOracle.SoftMinutes);
        Assert.AreEqual(30, policy.SourceOracle.HardMinutes);
        Assert.AreEqual(1, policy.MaximumProgressExtensionsPerUnit);
        Assert.AreEqual(0, policy.SourceOracle.Operations.Builds);
        Assert.AreEqual(0, policy.TargetBootstrap.Operations.Builds);
        var json = await File.ReadAllTextAsync(fixture.Path(EnforcementStore.PolicyPath));
        StringAssert.Contains(json, "\"maximumProgressExtensionsPerUnit\"");
        Assert.DoesNotContain("\"maximumProgressExtensions\"", json);
        policy.SemanticConvergence.SoftMinutes = 12;
        await EnforcementStore.WriteAtomicAsync(fixture.Root, ".migration-evidence/custom-policy.json", policy);
        var custom = command.Parse(["configure", fixture.Root.FullName, "--policy",
            fixture.Path(".migration-evidence/custom-policy.json")]);
        Assert.AreEqual(0, await custom.InvokeAsync(custom.InvocationConfiguration, CancellationToken.None));
        Assert.AreEqual(12, (await fixture.LoadAsync<EnforcementPolicy>(EnforcementStore.PolicyPath))
            .SemanticConvergence.SoftMinutes);
    }

    [TestMethod]
    public async Task Cli_ConfigureRejectsMalformedJsonAndAmbiguousFlags()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var command = new UwpEnforceCommand(new FakeProcessRunner());
        await EnforcementStore.WriteTextAtomicAsync(fixture.Root, ".migration-evidence/invalid-policy.json", "{}");
        foreach (var arguments in new[]
        {
            new[] { "configure", fixture.Root.FullName },
            new[] { "configure", fixture.Root.FullName, "--write-default", "--policy", fixture.Path(".migration-evidence/invalid-policy.json") },
            new[] { "configure", fixture.Root.FullName, "--policy", fixture.Path(".migration-evidence/invalid-policy.json") }
        })
        {
            var parsed = command.Parse(arguments);
            Assert.AreEqual(1, await parsed.InvokeAsync(parsed.InvocationConfiguration, CancellationToken.None));
        }
    }

    [TestMethod]
    public async Task Store_PolicyProgressAndSnapshotAreAtomicAndRoundTrip()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var snapshot = await UwpEnforcementRunner.AssessAsync(fixture.Root);
        var saved = await fixture.LoadAsync<EnforcementSnapshot>(EnforcementStore.StatePath);
        var progress = await fixture.LoadAsync<WorkflowProgress>(EnforcementStore.ProgressPath);
        Assert.AreEqual(snapshot.Stage, saved.Stage);
        Assert.AreEqual(snapshot.Workflow!.StartedAt, progress.StartedAt);
        Assert.IsNull(WorkflowValidation.ProgressError(progress,
            await fixture.LoadAsync<EnforcementPolicy>(EnforcementStore.PolicyPath), DateTimeOffset.UtcNow));
        Assert.IsEmpty(fixture.Root.EnumerateFiles("*.tmp", SearchOption.AllDirectories));
    }

    [TestMethod]
    public async Task Budget_FreshDiscriminatingEvidenceCanAuthorizeSoftExtension()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var process = new FakeProcessRunner(BuildSuccess(), ReplaySuccess(), ReplaySuccess());
        var runner = new UwpEnforcementRunner(process);
        await runner.RunAsync(fixture.Root, new UwpEnforcementOptions());
        var progress = await fixture.LoadAsync<WorkflowProgress>(EnforcementStore.ProgressPath);
        AgeWorkflow(progress, 16);
        await EnforcementStore.WriteAtomicAsync(fixture.Root, EnforcementStore.ProgressPath, progress);
        var expired = await runner.RunAsync(fixture.Root, new UwpEnforcementOptions { ForceReplay = true });
        Assert.AreEqual("stage-budget", expired.Budget?.Decision);
        var evidence = ".migration-evidence/new-evidence.txt";
        await File.WriteAllTextAsync(fixture.Path(evidence), "A fresh discriminating navigation observation.");
        var extended = await runner.RunAsync(fixture.Root, new UwpEnforcementOptions
        {
            ForceReplay = true, DiscriminatingEvidence = [evidence]
        });
        Assert.AreEqual("extension-granted", extended.Budget?.Decision);
        Assert.AreEqual(1, extended.Budget!.ExtensionsUsed);
        Assert.AreEqual(3, process.CallCount);
    }

    [TestMethod]
    public async Task Budget_InfrastructureInterruptionIsChargedBeforeInvocation()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var process = new InterruptingProcessRunner();
        var runner = new UwpEnforcementRunner(process);
        await Assert.ThrowsExactlyAsync<IOException>(() =>
            runner.RunAsync(fixture.Root, new UwpEnforcementOptions()));
        var snapshot = await runner.RunAsync(fixture.Root, new UwpEnforcementOptions());
        Assert.AreEqual("infrastructure-failure", snapshot.Disposition?.Kind);
        Assert.IsTrue(snapshot.Disposition!.Terminal);
        Assert.AreEqual(1, process.CallCount);
        Assert.AreEqual(1, snapshot.Workflow!.Operations.Count);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            UwpEnforcementRunner.ConfigurePolicyAsync(fixture.Root, EnforcementPolicy.Balanced()));
    }

    [TestMethod]
    public async Task Store_LegacyAttestationsResumeWithoutResettingFailureGuards()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var process = new FakeProcessRunner(new EnforcementProcessResult(1, """{"Error":"compile failed"}""", ""));
        await new UwpEnforcementRunner(process).RunAsync(fixture.Root, new UwpEnforcementOptions());
        File.Delete(fixture.Path(EnforcementStore.ProgressPath));
        File.Delete(fixture.Path(EnforcementStore.PolicyPath));
        var resumed = await new UwpEnforcementRunner(process).RunAsync(fixture.Root, new UwpEnforcementOptions());
        Assert.AreEqual("change-required", resumed.Frontier?.Kind);
        Assert.AreEqual(1, process.CallCount);
        Assert.AreEqual(1, resumed.Budget!.TotalOperations.Builds);
        Assert.IsNotNull(resumed.Workflow!.OperationsStartedAt);
    }

    [TestMethod]
    public void Cli_AssessHelpExplainsStageBudgetAndConfigureHelpExplainsImmutability()
    {
        var command = new UwpEnforceCommand(new FakeProcessRunner());
        var assess = command.Subcommands.Single(child => child.Name == "assess");
        var configure = command.Subcommands.Single(child => child.Name == "configure");
        StringAssert.Contains(assess.Description!, "stage-budget");
        StringAssert.Contains(configure.Description!, "immutable");
        StringAssert.Contains(configure.Description!, "maximumProgressExtensionsPerUnit");
        StringAssert.Contains(configure.Description!, "stage-specific");
        StringAssert.Contains(command.Subcommands.Single(child => child.Name == "diagnose").Description!,
            "runtime failure");
        StringAssert.Contains(command.Subcommands.Single(child => child.Name == "ui").Description!,
            "--discriminating-evidence");
        var parsed = command.Parse(["run", "target", "--force-replay", "--discriminating-evidence", "new.txt"]);
        Assert.IsFalse(parsed.Errors.Any(error => error.Message.Contains("Unrecognized", StringComparison.OrdinalIgnoreCase)));
        parsed = command.Parse(["ui", "target", "--discriminating-evidence", "final.txt", "--", "list"]);
        Assert.IsFalse(parsed.Errors.Any(error => error.Message.Contains("Unrecognized", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public async Task Operations_BootstrapWithoutFailureRejectsDiagnosisAndUiBeforeInvocation()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var process = new InterruptingReplayProcessRunner();
        var runner = new UwpEnforcementRunner(process);
        await Assert.ThrowsExactlyAsync<IOException>(() => runner.RunAsync(fixture.Root, new UwpEnforcementOptions()));
        var snapshot = await UwpEnforcementRunner.AssessAsync(fixture.Root);
        Assert.AreEqual(WorkflowStage.TargetBootstrap, snapshot.Stage);
        var exception = await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            runner.DiagnoseAsync(fixture.Root, new UwpEnforcementOptions()));
        StringAssert.Contains(exception.Message, "terminal");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            runner.InvokeUiAsync(fixture.Root, "winapp", ["list"]));
        Assert.AreEqual(2, process.CallCount);
        var progress = await fixture.LoadAsync<WorkflowProgress>(EnforcementStore.ProgressPath);
        Assert.AreEqual(0, progress.Units.Single(unit => unit.Stage == WorkflowStage.TargetBootstrap).Operations.DiagnosticRuns);
        Assert.AreEqual(2, progress.Operations.Count);
    }

    [TestMethod]
    public async Task Operations_BootstrapFailureAllowsBoundedDiagnosisAndHypothesisThenMechanicalCorrection()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var process = new FakeProcessRunner(BuildSuccess(), new EnforcementProcessResult(1, "", "launch failed"),
            new EnforcementProcessResult(0, "debug output", ""), BuildSuccess(), ReplaySuccess());
        var runner = new UwpEnforcementRunner(process);
        var failed = await runner.RunAsync(fixture.Root, new UwpEnforcementOptions());
        Assert.AreEqual(WorkflowStage.TargetBootstrap, failed.Stage);
        await runner.DiagnoseAsync(fixture.Root, new UwpEnforcementOptions());
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            runner.DiagnoseAsync(fixture.Root, new UwpEnforcementOptions()));
        var attempt = (await fixture.LoadAsync<AttemptLedger>(EnforcementStore.AttemptLedgerPath)).Attempts.Single();
        await File.WriteAllTextAsync(fixture.Path(".migration-evidence/diagnosis.txt"), "Source/target discriminator");
        await UwpEnforcementRunner.RecordDiagnosisAsync(fixture.Root, attempt.Signature, attempt.StateId,
            ["Source.cs:1"], ["Target.cs:1"], "Preserve activation", "Activation guard was removed",
            "Restore the guard and replay", [".migration-evidence/diagnosis.txt"]);
        await File.AppendAllTextAsync(fixture.ProjectPath, Environment.NewLine);
        var corrected = await runner.RunAsync(fixture.Root, new UwpEnforcementOptions());
        Assert.AreEqual(WorkflowStage.SemanticConvergence, corrected.Stage);
        var progress = corrected.Workflow!;
        Assert.AreEqual(2, progress.Units.Single(unit => unit.Stage == WorkflowStage.MechanicalStabilization).Operations.Builds);
        var bootstrap = progress.Units.Single(unit => unit.Stage == WorkflowStage.TargetBootstrap);
        Assert.AreEqual(2, bootstrap.Operations.Replays);
        Assert.AreEqual(1, bootstrap.Operations.DiagnosticRuns);
        Assert.AreEqual(1, bootstrap.Operations.Hypotheses);
        Assert.AreEqual(5, process.CallCount);
    }

    [TestMethod]
    public async Task Operations_ClosureRequiresFinalEvidenceAndRejectsDiagnosisBeforeInvocation()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var process = new FakeProcessRunner(BuildSuccess(), ReplaySuccess(), new EnforcementProcessResult(0, "ui", ""),
            ReplaySuccess());
        var runner = new UwpEnforcementRunner(process);
        await runner.RunAsync(fixture.Root, new UwpEnforcementOptions());
        await fixture.VerifyStateAsync();
        var closed = await UwpEnforcementRunner.AssessAsync(fixture.Root);
        Assert.AreEqual(WorkflowStage.Closure, closed.Stage);
        Assert.AreEqual(2, closed.Workflow!.Operations.Count);
        var exception = await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            runner.DiagnoseAsync(fixture.Root, new UwpEnforcementOptions()));
        StringAssert.Contains(exception.Message, "not permitted");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            runner.InvokeUiAsync(fixture.Root, "winapp", ["list"]));
        var blocked = await runner.RunAsync(fixture.Root, new UwpEnforcementOptions { ForceReplay = true });
        Assert.AreEqual("evidence-required", blocked.Frontier?.Kind);
        Assert.AreEqual(2, process.CallCount);

        var evidence = ".migration-evidence/final-justification.txt";
        await File.WriteAllTextAsync(fixture.Path(evidence), "Final navigation check is needed to confirm closure.");
        var command = new UwpEnforceCommand(process);
        var parsed = command.Parse(["ui", fixture.Root.FullName, "--discriminating-evidence", evidence, "--", "list"]);
        Assert.AreEqual(0, await parsed.InvokeAsync(parsed.InvocationConfiguration, CancellationToken.None));
        var replayEvidence = ".migration-evidence/final-replay-justification.txt";
        await File.WriteAllTextAsync(fixture.Path(replayEvidence), "A final launch must confirm the navigation observation.");
        await runner.RunAsync(fixture.Root, new UwpEnforcementOptions
        {
            ForceReplay = true, DiscriminatingEvidence = [replayEvidence]
        });
        var progress = await fixture.LoadAsync<WorkflowProgress>(EnforcementStore.ProgressPath);
        var unit = progress.Units.Single(item => item.Stage == WorkflowStage.Closure);
        Assert.AreEqual(1, unit.Operations.UiActions);
        Assert.AreEqual(1, unit.Operations.Replays);
        Assert.IsTrue(progress.Operations.Skip(2).All(operation => operation.Stage == WorkflowStage.Closure &&
            operation.EvidenceFingerprint is not null));
        Assert.AreEqual(4, process.CallCount);
        await fixture.VerifyStateAsync();
        var complete = await UwpEnforcementRunner.SynchronizeReportAsync(fixture.Root);
        Assert.AreEqual("complete", complete.Disposition?.Kind);
    }

    [TestMethod]
    public async Task StatePlan_UnknownCriticalityAndMalformedAttemptsFailClosed()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var planPath = ".migration-evidence/state-plan.json";
        var node = JsonSerializer.SerializeToNode(await fixture.LoadAsync<StatePlanDocument>(planPath),
            EnforcementStore.JsonOptions)!.AsObject();
        node["states"]![0]!["criticality"] = "advisory";
        await EnforcementStore.WriteTextAtomicAsync(fixture.Root, planPath, node.ToJsonString());
        var snapshot = await UwpEnforcementRunner.AssessAsync(fixture.Root);
        Assert.AreEqual("state-plan", snapshot.Frontier?.Kind);
        node["states"]![0]!["criticality"] = "required";
        await EnforcementStore.WriteTextAtomicAsync(fixture.Root, planPath, node.ToJsonString());
        await EnforcementStore.WriteTextAtomicAsync(fixture.Root, EnforcementStore.AttemptLedgerPath,
            """{"schemaVersion":"1.0","attempts":null}""");
        snapshot = await UwpEnforcementRunner.AssessAsync(fixture.Root);
        Assert.AreEqual("evidence", snapshot.Frontier?.Kind);
    }

    [TestMethod]
    public async Task Policy_EmptyLegacyAttemptLedgerDoesNotFreezeConfiguration()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        await EnforcementStore.WriteAtomicAsync(fixture.Root, EnforcementStore.AttemptLedgerPath, new AttemptLedger());
        await UwpEnforcementRunner.ConfigurePolicyAsync(fixture.Root, EnforcementPolicy.Balanced());
        Assert.IsNotNull(await fixture.LoadAsync<EnforcementPolicy>(EnforcementStore.PolicyPath));
    }

    [TestMethod]
    public async Task Diagnosis_ForegroundFailurePersistsBoundOperationAndSnapshot()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var policy = EnforcementPolicy.Balanced();
        policy.SemanticConvergence.Operations.DiagnosticRuns = 2;
        await UwpEnforcementRunner.ConfigurePolicyAsync(fixture.Root, policy);
        var process = new FakeProcessRunner(BuildSuccess(), ReplaySuccess(), new EnforcementProcessResult(1, "", "crash"));
        var runner = new UwpEnforcementRunner(process);
        await runner.RunAsync(fixture.Root, new UwpEnforcementOptions());
        await runner.DiagnoseAsync(fixture.Root, new UwpEnforcementOptions());
        var snapshot = await fixture.LoadAsync<EnforcementSnapshot>(EnforcementStore.StatePath);
        Assert.AreEqual("diagnosis-required", snapshot.Frontier?.Kind);
        var operation = snapshot.Workflow!.Operations.Single(item => item.Kind == WorkflowOperation.DiagnosticRun);
        Assert.AreEqual(snapshot.Frontier?.Signature, operation.Signature);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            runner.DiagnoseAsync(fixture.Root, new UwpEnforcementOptions()));
        Assert.AreEqual(3, process.CallCount);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Diagnosis_InterruptedLedgerWriteRecoversFailureFromAttestation(bool diagnostic)
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var process = diagnostic
            ? new FakeProcessRunner(BuildSuccess(), ReplaySuccess(), new EnforcementProcessResult(1, "", "crash"))
            : new FakeProcessRunner(BuildSuccess(), new EnforcementProcessResult(1, """{"Error":"launch crash"}""", ""));
        var runner = new UwpEnforcementRunner(process);
        await runner.RunAsync(fixture.Root, new UwpEnforcementOptions());
        if (diagnostic) await runner.DiagnoseAsync(fixture.Root, new UwpEnforcementOptions());
        var count = process.CallCount;
        File.Delete(fixture.Path(EnforcementStore.AttemptLedgerPath));
        var snapshot = await runner.RunAsync(fixture.Root, new UwpEnforcementOptions { ForceReplay = true });
        Assert.AreEqual("diagnosis-required", snapshot.Frontier?.Kind);
        Assert.AreEqual(count, process.CallCount);
        var attempt = (await fixture.LoadAsync<AttemptLedger>(EnforcementStore.AttemptLedgerPath)).Attempts.Single();
        Assert.AreEqual("state-1", attempt.StateId);
        Assert.AreEqual("state-1", attempt.SliceId);
    }

    private sealed class InterruptingProcessRunner : IEnforcementProcessRunner
    {
        internal int CallCount { get; private set; }
        public Task<EnforcementProcessResult> RunAsync(string fileName, DirectoryInfo workingDirectory,
            IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
        {
            CallCount++;
            throw new IOException("Simulated infrastructure interruption.");
        }
    }

    private sealed class InterruptingReplayProcessRunner : IEnforcementProcessRunner
    {
        internal int CallCount { get; private set; }
        public Task<EnforcementProcessResult> RunAsync(string fileName, DirectoryInfo workingDirectory,
            IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return CallCount == 1 ? Task.FromResult(BuildSuccess()) :
                throw new IOException("Simulated replay interruption.");
        }
    }

    private static FakeProcessRunner SuccessfulLaunch() => new(BuildSuccess(), ReplaySuccess());
    private static EnforcementProcessResult BuildSuccess() => new(0, """{"AUMID":"Contoso.App"}""", "");
    private static EnforcementProcessResult ReplaySuccess() => new(0, """{"ProcessId":4242}""", "");

    private static async Task RecordRuntimeFailureAsync(RunnerFixture fixture)
    {
        await File.WriteAllTextAsync(fixture.Path(".migration-evidence/failure.txt"), "Observed crash");
        await UwpEnforcementRunner.RecordFailureAsync(fixture.Root, "runtime", "crash",
            "state-1", [".migration-evidence/failure.txt"]);
    }

    private static async Task RecordDiagnosedFailureAsync(RunnerFixture fixture)
    {
        await RecordRuntimeFailureAsync(fixture);
        await File.WriteAllTextAsync(fixture.Path(".migration-evidence/diagnosis.txt"), "Source and target inspected; guard removal discriminates.");
        await RecordDiagnosisAsync(fixture);
    }

    private static void AgeWorkflow(WorkflowProgress progress, int minutes)
    {
        progress.StartedAt = progress.StartedAt?.AddMinutes(-minutes);
        progress.OperationsStartedAt = progress.OperationsStartedAt?.AddMinutes(-minutes);
        progress.LastObservedAt = progress.LastObservedAt.AddMinutes(-minutes);
        foreach (var unit in progress.Units)
        {
            unit.StartedAt = unit.StartedAt?.AddMinutes(-minutes);
            unit.ExtensionGrantedAt = unit.ExtensionGrantedAt?.AddMinutes(-minutes);
            unit.BlockedAt = unit.BlockedAt?.AddMinutes(-minutes);
            unit.Epochs = unit.Epochs.Select(epoch => epoch with
            {
                EnteredAt = epoch.EnteredAt.AddMinutes(-minutes),
                ExitedAt = epoch.ExitedAt?.AddMinutes(-minutes)
            }).ToList();
        }
        progress.Events = progress.Events.Select(item => item with { RecordedAt = item.RecordedAt.AddMinutes(-minutes) }).ToList();
        progress.Operations = progress.Operations.Select(item => item with { RecordedAt = item.RecordedAt.AddMinutes(-minutes) }).ToList();
    }

    private static Task<EnforcementSnapshot> RecordDiagnosisAsync(RunnerFixture fixture) =>
        UwpEnforcementRunner.RecordDiagnosisAsync(fixture.Root, "crash", "state-1",
            ["SourcePage.xaml.cs:10"], ["TargetPage.xaml.cs:10"],
            "Suppress reentrant navigation",
            "The removed guard causes the crash",
            "Restore only the guard and replay",
            [".migration-evidence/diagnosis.txt"]);
}
