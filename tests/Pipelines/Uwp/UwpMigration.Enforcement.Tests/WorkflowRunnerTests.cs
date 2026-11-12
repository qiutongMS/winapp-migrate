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
    public async Task Progress_RecordsStageSpecificOperations()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        await UwpEnforcementRunner.AssessAsync(fixture.Root, TestWindowOwnerResolver.Shared);
        var process = SuccessfulLaunch();
        await new UwpEnforcementRunner(process).RunAsync(fixture.Root, new UwpEnforcementOptions());
        var progress = await fixture.LoadAsync<WorkflowProgress>(EnforcementStore.ProgressPath);
        Assert.IsNotNull(progress.OperationsStartedAt);
        Assert.HasCount(2, progress.Operations);
        Assert.AreEqual(1, progress.Units.Single(unit => unit.Stage == WorkflowStage.MechanicalStabilization).Operations.Builds);
        Assert.AreEqual(1, progress.Units.Single(unit => unit.Stage == WorkflowStage.TargetBootstrap).Operations.Replays);
        Assert.AreEqual(WorkflowStage.MechanicalStabilization, progress.Operations[0].Stage);
        Assert.AreEqual(WorkflowStage.TargetBootstrap, progress.Operations[1].Stage);
    }

    [TestMethod]
    [DataRow("{")]
    [DataRow("{}")]
    [DataRow("null")]
    [DataRow("""{"schemaVersion":"1.0","schemaVersion":"2.0"}""")]
    public async Task Progress_MalformedDocumentBlocksAllOperationsWithoutReplacement(string json)
    {
        using var fixture = await RunnerFixture.CreateAsync();
        await EnforcementStore.WriteTextAtomicAsync(fixture.Root, EnforcementStore.ProgressPath, json);
        var process = new FakeProcessRunner();
        var snapshot = await new UwpEnforcementRunner(process).RunAsync(
            fixture.Root, new UwpEnforcementOptions { ForceReplay = true });
        Assert.AreEqual("evidence", snapshot.Frontier?.Kind);
        Assert.AreEqual(0, process.CallCount);
        Assert.AreEqual(json, await File.ReadAllTextAsync(fixture.Path(EnforcementStore.ProgressPath)));
    }

    [TestMethod]
    public async Task Progress_RetiredPolicyArtifactIsRejectedWithoutReplacement()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        const string policyPath = ".migration-evidence/enforcement/policy.json";
        const string policy = """{"schemaVersion":"1.0","totalHardMinutes":150}""";
        await EnforcementStore.WriteTextAtomicAsync(fixture.Root, policyPath, policy);
        var process = new FakeProcessRunner();
        var snapshot = await new UwpEnforcementRunner(process).RunAsync(fixture.Root, new UwpEnforcementOptions());
        Assert.AreEqual("evidence", snapshot.Frontier?.Kind);
        Assert.IsNull(snapshot.Workflow);
        Assert.AreEqual(0, process.CallCount);
        StringAssert.Contains(snapshot.Gates.Single(gate => gate.Id == "enforcement-artifacts").Reason,
            "Retired enforcement policy");
        Assert.AreEqual(policy, await File.ReadAllTextAsync(fixture.Path(policyPath)));
        Assert.IsFalse(File.Exists(fixture.Path(EnforcementStore.ProgressPath)));
    }

    [TestMethod]
    [DataRow("schema")]
    [DataRow("counts")]
    [DataRow("null-units")]
    [DataRow("invalid-event")]
    [DataRow("null-epoch")]
    [DataRow("overlapping-epochs")]
    [DataRow("missing-disposition")]
    [DataRow("retired-policy")]
    [DataRow("retired-extension")]
    [DataRow("retired-debt")]
    public async Task Progress_InvalidPersistedProgressFailsArtifactGate(string mutation)
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var assessed = await UwpEnforcementRunner.AssessAsync(fixture.Root, TestWindowOwnerResolver.Shared);
        var node = JsonSerializer.SerializeToNode(assessed.Workflow, EnforcementStore.JsonOptions)!.AsObject();
        switch (mutation)
        {
            case "schema": node["schemaVersion"] = "4.0"; break;
            case "counts": node["units"]![0]!["operations"]!["builds"] = 1; break;
            case "null-units": node["units"] = null; break;
            case "null-epoch": node["units"]![0]!["epochs"]![0] = null; break;
            case "overlapping-epochs":
                node["units"]![0]!["epochs"]!.AsArray().Add(node["units"]![0]!["epochs"]![0]!.DeepClone());
                break;
            case "missing-disposition": node.Remove("disposition"); break;
            case "retired-policy": node["policyFingerprint"] = "old"; break;
            case "retired-extension": node["units"]![0]!["extensionGrantedAt"] = DateTimeOffset.UtcNow; break;
            case "retired-debt": node["debt"] = new JsonArray(); break;
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
        var snapshot = await UwpEnforcementRunner.AssessAsync(fixture.Root, TestWindowOwnerResolver.Shared);
        Assert.AreEqual("evidence", snapshot.Frontier?.Kind);
    }

    [TestMethod]
    [DataRow("schema")]
    [DataRow("retired-budget")]
    public async Task Progress_UnsupportedSnapshotIsRejectedWithoutReplacingArtifacts(string mutation)
    {
        using var fixture = await RunnerFixture.CreateAsync();
        await UwpEnforcementRunner.AssessAsync(fixture.Root, TestWindowOwnerResolver.Shared);
        var node = JsonSerializer.SerializeToNode(
            await fixture.LoadAsync<EnforcementSnapshot>(EnforcementStore.StatePath),
            EnforcementStore.JsonOptions)!;
        if (mutation == "schema")
            node["schemaVersion"] = "2.0";
        else
            node["budget"] = new JsonObject { ["decision"] = "within-budget" };
        var original = node.ToJsonString();
        var progressJson = await File.ReadAllTextAsync(fixture.Path(EnforcementStore.ProgressPath));
        await EnforcementStore.WriteTextAtomicAsync(fixture.Root, EnforcementStore.StatePath, original);
        var process = new FakeProcessRunner();

        var snapshot = await new UwpEnforcementRunner(process).RunAsync(fixture.Root, new UwpEnforcementOptions());

        Assert.AreEqual("evidence", snapshot.Frontier?.Kind);
        Assert.AreEqual(0, process.CallCount);
        Assert.AreEqual(original, await File.ReadAllTextAsync(fixture.Path(EnforcementStore.StatePath)));
        Assert.AreEqual(progressJson, await File.ReadAllTextAsync(fixture.Path(EnforcementStore.ProgressPath)));
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
        Assert.AreEqual(4, resumed.Telemetry!.TotalOperations.Builds + resumed.Telemetry.TotalOperations.Replays);
        Assert.AreEqual("unverified", (await fixture.LoadAsync<StatePlanDocument>(
            ".migration-evidence/state-plan.json")).States[0].Target.Status);
    }

    [TestMethod]
    public async Task Run_SourceDebtDoesNotBlockBuildOrReplay()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var plan = await fixture.LoadAsync<StatePlanDocument>(".migration-evidence/state-plan.json");
        plan.States[0].Source.Status = "unverified";
        await EnforcementStore.WriteAtomicAsync(fixture.Root, ".migration-evidence/state-plan.json", plan);
        var process = new FakeProcessRunner(BuildSuccess(), ReplaySuccess());
        var snapshot = await new UwpEnforcementRunner(process).RunAsync(fixture.Root, new UwpEnforcementOptions());
        Assert.AreEqual(WorkflowStage.SemanticConvergence, snapshot.Stage);
        Assert.AreEqual("blocked", snapshot.Gates.Single(gate => gate.Id == "source-oracle").Status);
        Assert.AreEqual(2, process.CallCount);
        Assert.IsTrue(snapshot.Build!.Success);
        Assert.IsTrue(snapshot.Replay!.Success);
        Assert.AreEqual(2, snapshot.Telemetry!.TotalOperations.Builds + snapshot.Telemetry.TotalOperations.Replays);
        CollectionAssert.Contains(snapshot.Disposition!.AllowedOperations, WorkflowOperation.UiAction);
        var strict = new UwpEnforceCommand(
            new FakeProcessRunner(), TestWindowOwnerResolver.Shared).Parse(
                ["assess", fixture.Root.FullName, "--strict"]);
        Assert.AreEqual(2, await strict.InvokeAsync(
            strict.InvocationConfiguration, CancellationToken.None));
    }

    [TestMethod]
    public async Task Operations_SourceOracleRegressionDoesNotBlockTargetProbes()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var process = new FakeProcessRunner(
            BuildSuccess(),
            ReplaySuccess(),
            new EnforcementProcessResult(0, "[]", ""));
        var runner = new UwpEnforcementRunner(process);
        await runner.RunAsync(fixture.Root, new UwpEnforcementOptions());
        var plan = await fixture.LoadAsync<StatePlanDocument>(".migration-evidence/state-plan.json");
        plan.States[0].Source.Status = "unverified";
        plan.States[0].Source.Evidence.Clear();
        plan.States[0].Source.Reason = "Source evidence was invalidated.";
        await EnforcementStore.WriteAtomicAsync(
            fixture.Root, ".migration-evidence/state-plan.json", plan);

        var snapshot = await UwpEnforcementRunner.AssessAsync(fixture.Root, TestWindowOwnerResolver.Shared);
        Assert.AreEqual("blocked", snapshot.Gates.Single(gate => gate.Id == "source-oracle").Status);
        CollectionAssert.Contains(snapshot.Disposition!.AllowedOperations, WorkflowOperation.UiAction);
        var result = await runner.InvokeUiAsync(fixture.Root, "winapp", ["list-windows", "--json"]);

        Assert.AreEqual(0, result.ExitCode);
        Assert.AreEqual(3, process.CallCount);
    }

    [TestMethod]
    public async Task Run_ForceReplayDoesNotRequireAnIntentReceipt()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var process = new FakeProcessRunner(BuildSuccess(), ReplaySuccess(), ReplaySuccess());
        var runner = new UwpEnforcementRunner(process);
        await runner.RunAsync(fixture.Root, new UwpEnforcementOptions());
        var snapshot = await runner.RunAsync(fixture.Root, new UwpEnforcementOptions { ForceReplay = true });
        Assert.AreEqual("state", snapshot.Frontier?.Kind);
        Assert.AreEqual(3, process.CallCount);
    }

    [TestMethod]
    public async Task Run_DiagnosticNoteContentDoesNotAuthorizeReplay()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var process = new FakeProcessRunner(BuildSuccess(), ReplaySuccess(), ReplaySuccess(), ReplaySuccess());
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
        var replayed = await runner.RunAsync(fixture.Root, new UwpEnforcementOptions
        {
            ForceReplay = true, DiscriminatingEvidence = [copy]
        });
        Assert.AreEqual("state", replayed.Frontier?.Kind);
        Assert.AreEqual(4, process.CallCount);
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
            UwpEnforcementRunner.RecordFailureAsync(fixture.Root, TestWindowOwnerResolver.Shared, "runtime", "crash",
                "state-1", [".migration-evidence/failure.txt"]));
        Assert.AreEqual(before, await File.ReadAllTextAsync(fixture.Path(EnforcementStore.AttemptLedgerPath)));
        var attempt = (await fixture.LoadAsync<AttemptLedger>(EnforcementStore.AttemptLedgerPath)).Attempts.Single();
        Assert.AreEqual(attempt.TargetFingerprint, attempt.Diagnosis!.TargetFingerprint);
        Assert.AreEqual(attempt.Signature, attempt.Diagnosis.Signature);
        Assert.AreEqual("state-1", attempt.Diagnosis.StateId);
        Assert.AreEqual("state-1", attempt.Diagnosis.SliceId);
    }

    [TestMethod]
    public async Task Diagnosis_OptionalNotesDoNotBlockReplayOrFreshDiagnostics()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var process = new FakeProcessRunner(
            BuildSuccess(), ReplaySuccess(), ReplaySuccess(), new EnforcementProcessResult(0, "debug", ""));
        var runner = new UwpEnforcementRunner(process);
        await runner.RunAsync(fixture.Root, new UwpEnforcementOptions());
        await RecordDiagnosedFailureAsync(fixture);
        var replayed = await runner.RunAsync(fixture.Root, new UwpEnforcementOptions { ForceReplay = true });
        Assert.AreEqual("state", replayed.Frontier?.Kind);
        await runner.DiagnoseAsync(fixture.Root, new UwpEnforcementOptions());
        Assert.AreEqual(4, process.CallCount);
        await File.AppendAllTextAsync(fixture.ProjectPath, Environment.NewLine);
        var corrected = await UwpEnforcementRunner.AssessAsync(fixture.Root, TestWindowOwnerResolver.Shared);
        Assert.AreEqual("build", corrected.Frontier?.Kind);
        Assert.AreEqual(WorkflowStage.SemanticConvergence, corrected.Stage);
    }

    [TestMethod]
    public async Task Diagnosis_TargetChangesPermitFreshBuildButNotFailureRebinding()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        await RecordRuntimeFailureAsync(fixture);
        await File.AppendAllTextAsync(fixture.ProjectPath, Environment.NewLine);
        var process = SuccessfulLaunch();
        var snapshot = await new UwpEnforcementRunner(process).RunAsync(
            fixture.Root, new UwpEnforcementOptions { ForceReplay = true });
        Assert.AreEqual("state", snapshot.Frontier?.Kind);
        Assert.AreEqual(2, process.CallCount);
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
    public async Task Telemetry_FreshReplayRemainsAuthorizedByDiscriminatingEvidence()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var process = new FakeProcessRunner(
            BuildSuccess(), ReplaySuccess(), ReplaySuccess());
        var runner = new UwpEnforcementRunner(process);
        await runner.RunAsync(fixture.Root, new UwpEnforcementOptions());
        await File.WriteAllTextAsync(fixture.Path(".migration-evidence/discriminator.txt"), "New evidence");
        var snapshot = await runner.RunAsync(fixture.Root, new UwpEnforcementOptions
        {
            ForceReplay = true, DiscriminatingEvidence = [".migration-evidence/discriminator.txt"]
        });
        Assert.AreEqual(2, snapshot.Telemetry!.TotalOperations.Replays);
        Assert.AreEqual(3, process.CallCount);
    }

    [TestMethod]
    public async Task Store_ProgressAndSnapshotAreAtomicAndRoundTrip()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var snapshot = await UwpEnforcementRunner.AssessAsync(fixture.Root, TestWindowOwnerResolver.Shared);
        var saved = await fixture.LoadAsync<EnforcementSnapshot>(EnforcementStore.StatePath);
        var progress = await fixture.LoadAsync<WorkflowProgress>(EnforcementStore.ProgressPath);
        Assert.AreEqual(snapshot.Stage, saved.Stage);
        Assert.AreEqual(snapshot.Workflow!.StartedAt, progress.StartedAt);
        Assert.IsNull(WorkflowValidation.ProgressError(progress, DateTimeOffset.UtcNow));
        Assert.IsEmpty(fixture.Root.EnumerateFiles("*.tmp", SearchOption.AllDirectories));
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
    }

    [TestMethod]
    public async Task Store_DeletingAnchoredHistoryCannotResetFailureGuards()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var process = new FakeProcessRunner(new EnforcementProcessResult(1, """{"Error":"compile failed"}""", ""));
        await new UwpEnforcementRunner(process).RunAsync(fixture.Root, new UwpEnforcementOptions());
        File.Delete(fixture.Path(EnforcementStore.ProgressPath));
        var resumed = await new UwpEnforcementRunner(process).RunAsync(fixture.Root, new UwpEnforcementOptions());
        Assert.AreEqual("infrastructure-failure", resumed.Disposition?.Kind);
        Assert.AreEqual("blocked", resumed.Gates.Single(gate => gate.Id == "enforcement-artifacts").Status);
        Assert.AreEqual(1, process.CallCount);
        Assert.HasCount(1, (await fixture.LoadAsync<AttemptLedger>(EnforcementStore.AttemptLedgerPath)).Attempts);
    }

    [TestMethod]
    [DataRow("missing")]
    [DataRow("corrupt")]
    [DataRow("1.0")]
    [DataRow("2.0")]
    public async Task Progress_StartedHistoryCannotBeResetOrReconstructed(string mutation)
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var process = SuccessfulLaunch();
        var runner = new UwpEnforcementRunner(process);
        await runner.RunAsync(fixture.Root, new UwpEnforcementOptions());
        var stateJson = await File.ReadAllTextAsync(fixture.Path(EnforcementStore.StatePath));
        string? progressJson = null;
        if (mutation == "missing")
            File.Delete(fixture.Path(EnforcementStore.ProgressPath));
        else
        {
            var node = JsonSerializer.SerializeToNode(
                await fixture.LoadAsync<WorkflowProgress>(EnforcementStore.ProgressPath),
                EnforcementStore.JsonOptions)!;
            node["schemaVersion"] = mutation;
            progressJson = mutation == "corrupt" ? "{" : node.ToJsonString();
            await EnforcementStore.WriteTextAtomicAsync(fixture.Root, EnforcementStore.ProgressPath, progressJson);
        }

        var rejected = await runner.RunAsync(fixture.Root, new UwpEnforcementOptions { ForceReplay = true });

        Assert.AreEqual("infrastructure-failure", rejected.Disposition?.Kind);
        Assert.AreEqual("blocked", rejected.Gates.Single(gate => gate.Id == "enforcement-artifacts").Status);
        Assert.IsNull(rejected.Workflow);
        Assert.AreEqual(2, process.CallCount);
        Assert.AreEqual(stateJson, await File.ReadAllTextAsync(fixture.Path(EnforcementStore.StatePath)));
        if (progressJson is null)
            Assert.IsFalse(File.Exists(fixture.Path(EnforcementStore.ProgressPath)));
        else
            Assert.AreEqual(progressJson, await File.ReadAllTextAsync(fixture.Path(EnforcementStore.ProgressPath)));
    }

    [TestMethod]
    public async Task Progress_IndependentHeadAlonePreventsFreshHistory()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var process = SuccessfulLaunch();
        var runner = new UwpEnforcementRunner(process);
        await runner.RunAsync(fixture.Root, new UwpEnforcementOptions());
        foreach (var path in new[]
        {
            EnforcementStore.ProgressPath, EnforcementStore.StatePath,
            EnforcementStore.BuildAttestationPath, EnforcementStore.ReplayAttestationPath,
            EnforcementStore.AttemptLedgerPath
        })
            File.Delete(fixture.Path(path));

        var rejected = await runner.RunAsync(fixture.Root, new UwpEnforcementOptions());

        StringAssert.Contains(rejected.Gates.Single(gate => gate.Id == "enforcement-artifacts").Reason,
            "already-started operation history");
        Assert.AreEqual(2, process.CallCount);
        Assert.IsFalse(File.Exists(fixture.Path(EnforcementStore.ProgressPath)));
        Assert.IsFalse(File.Exists(fixture.Path(EnforcementStore.StatePath)));
    }

    [TestMethod]
    public void Cli_ClosureCommandsExposeOnlyCurrentProtocol()
    {
        var command = new UwpEnforceCommand(new FakeProcessRunner());
        var assess = command.Subcommands.Single(child => child.Name == "assess");
        Assert.AreEqual("closure", command.Name);
        Assert.IsEmpty(command.Aliases);
        StringAssert.Contains(assess.Description!, "evidence freshness");
        StringAssert.Contains(command.Subcommands.Single(child => child.Name == "diagnose").Description!,
            "optional");
        var capture = command.Subcommands.Single(child => child.Name == "capture");
        Assert.IsEmpty(capture.Aliases);
        StringAssert.Contains(capture.Description!,
            "authenticated target probe");
        var attest = command.Subcommands.Single(child => child.Name == "attest");
        Assert.IsEmpty(attest.Aliases);
        var finalize = command.Subcommands.Single(child => child.Name == "finalize");
        Assert.IsEmpty(finalize.Aliases);
        foreach (var retired in new[] { "run", "ui", "sync-report", "configure", "wait" })
            Assert.IsNotEmpty(command.Parse([retired, "target"]).Errors);
        foreach (var verb in new[] { "assess", "attest", "diagnose", "finalize" })
            Assert.IsTrue(command.Parse([verb, "target", "--max-attempts", "2"]).Errors.Any(error =>
                error.Message.Contains("Unrecognized", StringComparison.OrdinalIgnoreCase) &&
                error.Message.Contains("--max-attempts", StringComparison.Ordinal)));
        var parsed = command.Parse(["attest", "target"]);
        Assert.IsFalse(parsed.Errors.Any(error => error.Message.Contains("Unrecognized", StringComparison.OrdinalIgnoreCase)));
        parsed = command.Parse(["capture", "target", "--", "list"]);
        Assert.IsFalse(parsed.Errors.Any(error => error.Message.Contains("Unrecognized", StringComparison.OrdinalIgnoreCase)));
        parsed = command.Parse(["finalize", "target"]);
        Assert.IsFalse(parsed.Errors.Any(error => error.Message.Contains("Unrecognized", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public async Task Operations_BootstrapWithoutFailureRejectsDiagnosisAndUiBeforeInvocation()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var process = new InterruptingReplayProcessRunner();
        var runner = new UwpEnforcementRunner(process);
        await Assert.ThrowsExactlyAsync<IOException>(() => runner.RunAsync(fixture.Root, new UwpEnforcementOptions()));
        var snapshot = await UwpEnforcementRunner.AssessAsync(fixture.Root, TestWindowOwnerResolver.Shared);
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
        await UwpEnforcementRunner.RecordDiagnosisAsync(fixture.Root, TestWindowOwnerResolver.Shared, attempt.Signature, attempt.StateId,
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
    public async Task Operations_ClosureUsesEvidencePrerequisitesRatherThanStageOrIntentGates()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var process = new FakeProcessRunner(BuildSuccess(), ReplaySuccess(),
            new EnforcementProcessResult(0, "debug", ""),
            new EnforcementProcessResult(0, "ui", ""), ReplaySuccess());
        var runner = new UwpEnforcementRunner(process);
        await runner.RunAsync(fixture.Root, new UwpEnforcementOptions());
        await fixture.VerifyStateAsync();
        var closed = await UwpEnforcementRunner.AssessAsync(fixture.Root, TestWindowOwnerResolver.Shared);
        Assert.AreEqual(WorkflowStage.Closure, closed.Stage);
        Assert.AreEqual(3, closed.Workflow!.Operations.Count);
        await runner.DiagnoseAsync(fixture.Root, new UwpEnforcementOptions());
        var command = new UwpEnforceCommand(process);
        var parsed = command.Parse(["capture", fixture.Root.FullName, "--", "list"]);
        Assert.AreEqual(0, await parsed.InvokeAsync(parsed.InvocationConfiguration, CancellationToken.None));
        await runner.RunAsync(fixture.Root, new UwpEnforcementOptions
        {
            ForceReplay = true
        });
        var progress = await fixture.LoadAsync<WorkflowProgress>(EnforcementStore.ProgressPath);
        var unit = progress.Units.Single(item => item.Stage == WorkflowStage.Closure);
        Assert.AreEqual(1, unit.Operations.UiActions);
        Assert.AreEqual(1, unit.Operations.Replays);
        Assert.AreEqual(1, unit.Operations.DiagnosticRuns);
        Assert.IsTrue(progress.Operations.Skip(3).All(operation => operation.Stage == WorkflowStage.Closure &&
            operation.EvidenceFingerprint is null));
        Assert.AreEqual(5, process.CallCount);
        await fixture.VerifyStateAsync();
        var complete = await UwpEnforcementRunner.SynchronizeReportAsync(fixture.Root, TestWindowOwnerResolver.Shared);
        Assert.AreEqual("complete", complete.Disposition?.Kind);
    }

    [TestMethod]
    [DataRow("advisory")]
    [DataRow("optional")]
    public async Task StatePlan_UnknownCriticalityAndMalformedAttemptsFailClosed(string criticality)
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var planPath = ".migration-evidence/state-plan.json";
        var node = JsonSerializer.SerializeToNode(await fixture.LoadAsync<StatePlanDocument>(planPath),
            EnforcementStore.JsonOptions)!.AsObject();
        node["surfaces"]![0]!["criticality"] = criticality;
        await EnforcementStore.WriteTextAtomicAsync(fixture.Root, planPath, node.ToJsonString());
        var snapshot = await UwpEnforcementRunner.AssessAsync(fixture.Root, TestWindowOwnerResolver.Shared);
        Assert.AreEqual("state-plan", snapshot.Frontier?.Kind);
        node["surfaces"]![0]!["criticality"] = "required";
        await EnforcementStore.WriteTextAtomicAsync(fixture.Root, planPath, node.ToJsonString());
        await EnforcementStore.WriteTextAtomicAsync(fixture.Root, EnforcementStore.AttemptLedgerPath,
            """{"schemaVersion":"1.0","attempts":null}""");
        snapshot = await UwpEnforcementRunner.AssessAsync(fixture.Root, TestWindowOwnerResolver.Shared);
        Assert.AreEqual("evidence", snapshot.Frontier?.Kind);
    }

    [TestMethod]
    public async Task Diagnosis_ForegroundFailurePersistsBoundOperationAndSnapshot()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var process = new FakeProcessRunner(BuildSuccess(), ReplaySuccess(), new EnforcementProcessResult(1, "", "crash"));
        var runner = new UwpEnforcementRunner(process);
        await runner.RunAsync(fixture.Root, new UwpEnforcementOptions());
        await runner.DiagnoseAsync(fixture.Root, new UwpEnforcementOptions());
        var snapshot = await fixture.LoadAsync<EnforcementSnapshot>(EnforcementStore.StatePath);
        Assert.AreEqual("state", snapshot.Frontier?.Kind);
        var operation = snapshot.Workflow!.Operations.Single(item => item.Kind == WorkflowOperation.DiagnosticRun);
        var failure = (await fixture.LoadAsync<AttemptLedger>(EnforcementStore.AttemptLedgerPath)).Attempts.Single();
        Assert.AreEqual(failure.Signature, operation.Signature);
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
        var snapshot = await UwpEnforcementRunner.AssessAsync(fixture.Root, TestWindowOwnerResolver.Shared);
        Assert.AreEqual(diagnostic ? "state" : "replay", snapshot.Frontier?.Kind);
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
        await UwpEnforcementRunner.RecordFailureAsync(fixture.Root, TestWindowOwnerResolver.Shared, "runtime", "crash",
            "state-1", [".migration-evidence/failure.txt"]);
    }

    private static async Task RecordDiagnosedFailureAsync(RunnerFixture fixture)
    {
        await RecordRuntimeFailureAsync(fixture);
        await File.WriteAllTextAsync(fixture.Path(".migration-evidence/diagnosis.txt"), "Source and target inspected; guard removal discriminates.");
        await RecordDiagnosisAsync(fixture);
    }

    private static void AgeWorkflow(WorkflowProgress progress, int minutes, DirectoryInfo root)
    {
        progress.StartedAt = progress.StartedAt?.AddMinutes(-minutes);
        progress.OperationsStartedAt = progress.OperationsStartedAt?.AddMinutes(-minutes);
        progress.LastObservedAt = progress.LastObservedAt.AddMinutes(-minutes);
        foreach (var unit in progress.Units)
        {
            unit.StartedAt = unit.StartedAt?.AddMinutes(-minutes);
            unit.Epochs = unit.Epochs.Select(epoch => epoch with
            {
                EnteredAt = epoch.EnteredAt.AddMinutes(-minutes),
                ExitedAt = epoch.ExitedAt?.AddMinutes(-minutes)
            }).ToList();
        }
        progress.Events = progress.Events.Select(item => item with { RecordedAt = item.RecordedAt.AddMinutes(-minutes) }).ToList();
        progress.Operations = progress.Operations.Select(item => item with { RecordedAt = item.RecordedAt.AddMinutes(-minutes) }).ToList();
        ContractTestAuthority.SealOperations(progress, root);
    }

    private static Task<EnforcementSnapshot> RecordDiagnosisAsync(RunnerFixture fixture) =>
        UwpEnforcementRunner.RecordDiagnosisAsync(fixture.Root, TestWindowOwnerResolver.Shared, "crash", "state-1",
            ["SourcePage.xaml.cs:10"], ["TargetPage.xaml.cs:10"],
            "Suppress reentrant navigation",
            "The removed guard causes the crash",
            "Restore only the guard and replay",
            [".migration-evidence/diagnosis.txt"]);
}
