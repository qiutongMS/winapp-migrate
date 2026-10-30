// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinAppMigration.Enforcement;

namespace UwpMigration.Enforcement.Tests;

public sealed partial class EnforcementEvaluatorTests
{
    [TestMethod]
    public void Workflow_StageTransitionsAreEvidenceDriven()
    {
        using var fixture = new EvaluatorFixture();
        var input = fixture.ClosedInput() with { Build = null, Replay = null, MechanicalReady = false };
        input.StatePlan!.States[0].Source.Status = "unverified";
        var source = EnforcementEvaluator.Evaluate(input);
        Assert.AreEqual(WorkflowStage.SourceOracle, source.Stage);
        Assert.AreEqual("source-oracle", source.Phase);
        Assert.AreEqual("source-oracle", source.Frontier?.Kind);

        input.StatePlan.States[0].Source.Status = "captured";
        var mechanical = EnforcementEvaluator.Evaluate(input with { Progress = source.Workflow });
        Assert.AreEqual(WorkflowStage.MechanicalStabilization, mechanical.Stage);
        var reportReady = EnforcementEvaluator.Evaluate(input with
        {
            MechanicalReady = true, Progress = mechanical.Workflow
        });
        Assert.AreEqual(WorkflowStage.MechanicalStabilization, reportReady.Stage);
        Assert.AreEqual("build", reportReady.Frontier?.Kind);
        var bootstrap = EnforcementEvaluator.Evaluate(input with
        {
            MechanicalReady = true, Build = fixture.ClosedInput().Build, Progress = reportReady.Workflow
        });
        Assert.AreEqual(WorkflowStage.TargetBootstrap, bootstrap.Stage);
        Assert.AreEqual("replay", bootstrap.Frontier?.Kind);
        Assert.IsTrue(bootstrap.Workflow!.Events.Any(item => item.Kind == "mechanical-stabilized"));

        var fresh = fixture.ClosedInput();
        fresh.StatePlan!.States[0].Target.Status = "unverified";
        var semantic = EnforcementEvaluator.Evaluate(fresh with { Progress = bootstrap.Workflow });
        Assert.AreEqual(WorkflowStage.SemanticConvergence, semantic.Stage);
        fresh.StatePlan.States[0].Target.Status = "passed";
        var closure = EnforcementEvaluator.Evaluate(fresh with { Progress = semantic.Workflow });
        Assert.AreEqual(WorkflowStage.Closure, closure.Stage);
        Assert.AreEqual("passed", closure.Status);
    }

    [TestMethod]
    [DataRow("missing")]
    [DataRow("stale")]
    [DataRow("not-analyzer-attested")]
    public void Workflow_MechanicalReportDoesNotReplaceFreshAnalyzerBuild(string build)
    {
        using var fixture = new EvaluatorFixture();
        var input = fixture.ClosedInput() with { Replay = null };
        if (build == "missing") input = input with { Build = null };
        if (build == "stale") input.Build!.TargetFingerprint = "old";
        if (build == "not-analyzer-attested") input.Build!.AnalyzerPrivateAssets = false;
        var snapshot = EnforcementEvaluator.Evaluate(input);
        Assert.AreEqual(WorkflowStage.MechanicalStabilization, snapshot.Stage);
        Assert.AreEqual("build", snapshot.Frontier?.Kind);
    }

    [TestMethod]
    public void Workflow_SemanticCorrectionKeepsStageAndSliceBudget()
    {
        using var fixture = new EvaluatorFixture();
        var input = SemanticInput(fixture);
        var before = EnforcementEvaluator.Evaluate(input);
        var changed = EnforcementEvaluator.Evaluate(input with
        {
            Progress = before.Workflow, TargetFingerprint = "corrected-target"
        });
        Assert.AreEqual(WorkflowStage.SemanticConvergence, changed.Stage);
        Assert.AreEqual("build", changed.Frontier?.Kind);
        Assert.AreEqual("state-1", changed.ActiveSlice);
        Assert.AreEqual(before.Workflow!.Units[0].StartedAt, changed.Workflow!.Units[0].StartedAt);
        Assert.IsTrue(changed.Workflow.Events.Any(item => item.Kind == "target-changed"));
    }

    [TestMethod]
    public void Workflow_StaleReplayDoesNotMoveCorrectionToEarlierCompletedSlice()
    {
        using var fixture = new EvaluatorFixture();
        var input = fixture.ClosedInput();
        input.StatePlan!.States.Add(State("active"));
        var before = EnforcementEvaluator.Evaluate(input);
        Assert.AreEqual("active", before.ActiveSlice);
        var changed = EnforcementEvaluator.Evaluate(input with
        {
            Progress = before.Workflow, TargetFingerprint = "corrected"
        });
        Assert.AreEqual(WorkflowStage.SemanticConvergence, changed.Stage);
        Assert.AreEqual("active", changed.ActiveSlice);
        Assert.AreEqual("build", changed.Frontier?.Kind);
        Assert.IsTrue(WorkflowOperations.TryBegin(input with { TargetFingerprint = "corrected" },
            changed, WorkflowOperation.Build, [], null, out var reason), reason);
        Assert.AreEqual("active", changed.Workflow!.Operations.Single().SliceId);
    }

    [TestMethod]
    public void Workflow_ResumesHistoricalReplayWithoutRegressingToMechanical()
    {
        using var fixture = new EvaluatorFixture();
        var input = fixture.ClosedInput() with { TargetFingerprint = "new", MechanicalReady = false };
        var snapshot = EnforcementEvaluator.Evaluate(input);
        Assert.AreEqual(WorkflowStage.SemanticConvergence, snapshot.Stage);
        Assert.AreEqual("mechanical", snapshot.Frontier?.Kind);
    }

    [TestMethod]
    public void Workflow_ClosureTargetChangeRevalidatesWithoutSemanticReopen()
    {
        using var fixture = new EvaluatorFixture();
        var input = fixture.ClosedInput() with
        {
            ReportValidation = new PipelineValidationSummary("captured", ["state-1"], "unverified", [], "unverified", "pending")
        };
        var closed = EnforcementEvaluator.Evaluate(input);
        var reopened = EnforcementEvaluator.Evaluate(input with
        {
            Progress = closed.Workflow, TargetFingerprint = "corrected"
        });
        Assert.AreEqual(WorkflowStage.Closure, reopened.Stage);
        Assert.AreEqual("build", reopened.Frontier?.Kind);
    }

    [TestMethod]
    [DataRow(16)]
    [DataRow(26)]
    [DataRow(151)]
    public void Telemetry_ElapsedTimeDoesNotChangeAuthorization(int minutes)
    {
        using var fixture = new EvaluatorFixture();
        var snapshot = EnforcementEvaluator.Evaluate(ExpiredInput(fixture, minutes));
        Assert.AreEqual("state", snapshot.Frontier?.Kind);
        Assert.AreEqual("within-budget", snapshot.Budget?.Decision);
        Assert.IsFalse(snapshot.Budget!.MeasuredProgress);
        Assert.AreEqual(minutes, snapshot.Budget.ElapsedMinutes);
        Assert.AreEqual(minutes, snapshot.Budget.TotalElapsedMinutes);
        Assert.IsNull(snapshot.Budget.SoftDeadline);
        Assert.IsNull(snapshot.Budget.HardDeadline);
        Assert.IsNull(snapshot.Budget.TotalHardDeadline);
        Assert.IsFalse(snapshot.Disposition!.Terminal);
    }

    [TestMethod]
    public void Telemetry_TargetChangeIsMeasuredWithoutAuthorizingAnExtension()
    {
        using var fixture = new EvaluatorFixture();
        var input = ExpiredInput(fixture, 16);
        input.Policy.SemanticConvergence.HardMinutes = 40;
        var observed = EnforcementEvaluator.Evaluate(input with { TargetFingerprint = "correction" });
        Assert.AreEqual("within-budget", observed.Budget?.Decision);
        Assert.AreEqual(0, observed.Budget!.ExtensionsUsed);
        Assert.IsTrue(observed.Budget.MeasuredProgress);
        Assert.AreEqual("build", observed.Frontier?.Kind);
    }

    [TestMethod]
    public void Telemetry_CompletedDiagnosisIsMeasuredProgress()
    {
        using var fixture = new EvaluatorFixture();
        var input = ExpiredInput(fixture, 16);
        var recorded = input.GeneratedAt.AddSeconds(-2);
        File.SetLastWriteTimeUtc(fixture.Path("evidence.txt"), input.GeneratedAt.UtcDateTime);
        input.Attempts.Attempts.Add(new EnforcementAttempt
        {
            Kind = "runtime", Signature = "failure", TargetFingerprint = input.TargetFingerprint,
            RecordedAt = recorded,
            Diagnosis = Diagnosis(recorded.AddSeconds(1))
        });
        var snapshot = EnforcementEvaluator.Evaluate(input);
        Assert.AreEqual("within-budget", snapshot.Budget?.Decision);
        Assert.AreEqual(0, snapshot.Budget!.ExtensionsUsed);
        Assert.AreEqual("change-required", snapshot.Frontier?.Kind);
        Assert.IsTrue(snapshot.Workflow!.Events.Any(item => item.Kind == "diagnosis-completed"));
    }

    [TestMethod]
    public void Telemetry_OperationTotalsDoNotAuthorizeSemanticUnits()
    {
        using var fixture = new EvaluatorFixture();
        var input = SemanticInput(fixture) with { TargetFingerprint = "corrected" };
        input.Policy.SemanticConvergence.Operations.Builds = 0;
        var snapshot = EnforcementEvaluator.Evaluate(input);
        Assert.AreEqual("within-budget", snapshot.Budget?.Decision);
        Assert.AreEqual("build", snapshot.Frontier?.Kind);
        Assert.IsFalse(snapshot.Disposition!.Terminal);
        Assert.AreEqual(0, snapshot.Budget!.Operations.Builds);
        Assert.AreEqual(0, snapshot.Budget.OperationCeilings.Builds);
        Assert.AreEqual(2, snapshot.Budget.OperationCeilings.Replays);
    }

    [TestMethod]
    public void Slices_DefaultMetadataPreservesSchema10()
    {
        using var fixture = new EvaluatorFixture();
        var snapshot = EnforcementEvaluator.Evaluate(SemanticInput(fixture));
        Assert.AreEqual("1.0", snapshot.SchemaVersion);
        Assert.AreEqual("state-1", snapshot.ActiveSlice);
        CollectionAssert.AreEqual(new[] { "state-1" }, snapshot.ActiveStateIds);
        Assert.AreEqual("state-1", snapshot.Frontier?.SliceId);
    }

    [TestMethod]
    public void Slices_SelectsFirstDependencyReadyIncompleteSlice()
    {
        using var fixture = new EvaluatorFixture();
        var input = SemanticInput(fixture);
        var dependent = input.StatePlan!.States[0];
        dependent.Slice = "dependent";
        dependent.Dependencies = ["prerequisite"];
        var prerequisite = State("prerequisite");
        input.StatePlan.States.Add(prerequisite);
        var first = EnforcementEvaluator.Evaluate(input);
        Assert.AreEqual("prerequisite", first.ActiveSlice);
        CollectionAssert.AreEqual(new[] { "prerequisite" }, first.ActiveStateIds);
        AttestState(input, prerequisite);
        var second = EnforcementEvaluator.Evaluate(input with { Progress = first.Workflow });
        Assert.AreEqual("dependent", second.ActiveSlice);
        Assert.IsTrue(second.Workflow!.Events.Any(item =>
            item.Kind == "slice-completed" && item.Key == "prerequisite"));
        Assert.AreEqual(2, second.Workflow.Units.Count);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Slices_RejectUnknownDependencyAndCycles(bool cycle)
    {
        using var fixture = new EvaluatorFixture();
        var input = SemanticInput(fixture);
        input.StatePlan!.States[0].Dependencies = [cycle ? "state-1" : "missing"];
        var snapshot = EnforcementEvaluator.Evaluate(input);
        Assert.AreEqual("state-plan", snapshot.Frontier?.Kind);
        StringAssert.Contains(snapshot.Gates.Single(gate => gate.Id == "state-plan").Reason,
            cycle ? "cycle" : "unknown dependency");
    }

    [TestMethod]
    public void Slices_RequiredBlockedWorkStopsWithoutVerification()
    {
        using var fixture = new EvaluatorFixture();
        var input = SemanticInput(fixture);
        input.StatePlan!.States[0].Target.Status = "blocked";
        var snapshot = EnforcementEvaluator.Evaluate(input);
        Assert.AreEqual("required-blocked", snapshot.Frontier?.Kind);
        Assert.AreEqual("blocked", snapshot.Status);
        Assert.AreEqual("verified", input.StatePlan.States[0].Comparison.Status);
        Assert.AreEqual("blocked", input.StatePlan.States[0].Target.Status);
    }

    [TestMethod]
    public void Slices_OptionalDebtDoesNotBlockIndependentSliceOrSatisfyDependencies()
    {
        using var fixture = new EvaluatorFixture();
        var input = SemanticInput(fixture);
        var optional = input.StatePlan!.States[0];
        optional.Criticality = StateCriticality.Optional;
        optional.Target.Status = "blocked";
        input.StatePlan.States.Add(State("independent"));
        var dependent = State("dependent");
        dependent.Dependencies = ["state-1"];
        input.StatePlan.States.Add(dependent);
        var deferred = EnforcementEvaluator.Evaluate(input);
        Assert.AreEqual("continue-with-debt", deferred.Status);
        var next = EnforcementEvaluator.Evaluate(input with { Progress = deferred.Workflow });
        Assert.AreEqual("independent", next.ActiveSlice);
        AttestState(input, input.StatePlan.States[1]);
        var blocked = EnforcementEvaluator.Evaluate(input with { Progress = next.Workflow });
        Assert.IsNull(blocked.ActiveSlice);
        Assert.AreEqual("required-blocked", blocked.Frontier?.Kind);
    }

    [TestMethod]
    public void Diagnosis_EarlierUnresolvedRuntimeFailureCannotBeHiddenByLaterDiagnosis()
    {
        using var fixture = new EvaluatorFixture();
        var input = fixture.ClosedInput();
        var recorded = input.GeneratedAt.AddSeconds(-2);
        input.Attempts.Attempts.Add(new EnforcementAttempt
        {
            Kind = "runtime", Signature = "older-unresolved", TargetFingerprint = input.TargetFingerprint,
            RecordedAt = recorded
        });
        input.Attempts.Attempts.Add(new EnforcementAttempt
        {
            Kind = "runtime", Signature = "newer-diagnosed", TargetFingerprint = input.TargetFingerprint,
            RecordedAt = recorded.AddSeconds(1), Diagnosis = Diagnosis(input.GeneratedAt)
        });
        var snapshot = EnforcementEvaluator.Evaluate(input);
        Assert.AreEqual("diagnosis-required", snapshot.Frontier?.Kind);
        Assert.AreEqual("older-unresolved", snapshot.Frontier?.Signature);
    }

    [TestMethod]
    public void Diagnosis_CompleteDiagnosisRequiresChangeThenBuild()
    {
        using var fixture = new EvaluatorFixture();
        var input = SemanticInput(fixture);
        input.Attempts.Attempts.Add(new EnforcementAttempt
        {
            Kind = "runtime", Signature = "crash", TargetFingerprint = input.TargetFingerprint,
            RecordedAt = input.GeneratedAt.AddSeconds(-1), Diagnosis = Diagnosis(input.GeneratedAt)
        });
        var diagnosed = EnforcementEvaluator.Evaluate(input);
        Assert.AreEqual("change-required", diagnosed.Frontier?.Kind);
        var corrected = EnforcementEvaluator.Evaluate(input with
        {
            TargetFingerprint = "corrected", Progress = diagnosed.Workflow
        });
        Assert.AreEqual("build", corrected.Frontier?.Kind);
        Assert.AreEqual(WorkflowStage.SemanticConvergence, corrected.Stage);
    }

    [TestMethod]
    public void Policy_InvalidRangesFailArtifactGate()
    {
        using var fixture = new EvaluatorFixture();
        var input = fixture.ClosedInput();
        input.Policy.SemanticConvergence.HardMinutes = 2;
        var snapshot = EnforcementEvaluator.Evaluate(input);
        Assert.AreEqual("evidence", snapshot.Frontier?.Kind);
        Assert.IsNull(snapshot.Workflow);
    }

    [TestMethod]
    public void Telemetry_ElapsedTimeCannotFreezeCompletedSlice()
    {
        using var fixture = new EvaluatorFixture();
        var input = ExpiredInput(fixture, 26);
        var observed = EnforcementEvaluator.Evaluate(input);
        input.StatePlan!.States[0].Target.Status = "passed";
        var completed = EnforcementEvaluator.Evaluate(input with { Progress = observed.Workflow });
        Assert.AreEqual(WorkflowStage.Closure, completed.Stage);
        Assert.AreNotEqual("blocked-required", completed.Disposition?.Kind);
        Assert.AreNotEqual("total-budget-exhausted", completed.Disposition?.Kind);
        Assert.IsTrue(completed.Workflow!.Units.All(unit => unit.BlockedAt is null));
    }

    [TestMethod]
    public void Telemetry_EachSliceTracksElapsedTimeWithoutExtensions()
    {
        using var fixture = new EvaluatorFixture();
        var input = ExpiredInput(fixture, 16);
        input.StatePlan!.States.Add(State("second"));
        var observed = EnforcementEvaluator.Evaluate(input with { TargetFingerprint = "correction" });
        Assert.AreEqual(0, observed.Budget!.ExtensionsUsed);
        input.StatePlan.States[0].Target.Status = "passed";
        var next = EnforcementEvaluator.Evaluate(input with { Progress = observed.Workflow });
        Assert.AreEqual("second", next.ActiveSlice);
        var observedAgain = EnforcementEvaluator.Evaluate(input with
        {
            Progress = next.Workflow, TargetFingerprint = "another-correction",
            GeneratedAt = input.GeneratedAt.AddMinutes(16)
        });
        Assert.AreEqual("within-budget", observedAgain.Budget?.Decision);
        Assert.AreEqual(0, observedAgain.Budget!.ExtensionsUsed);
        Assert.IsTrue(observedAgain.Budget.MeasuredProgress);
        Assert.IsTrue(observedAgain.Workflow!.Units.All(unit => unit.ExtensionGrantedAt is null));
        Assert.IsNull(WorkflowValidation.ProgressError(observedAgain.Workflow, input.Policy, input.GeneratedAt.AddMinutes(16)));
    }

    [TestMethod]
    public void Slices_OpenFindingReopensOnlyItsSliceBeforeClosure()
    {
        using var fixture = new EvaluatorFixture();
        var input = fixture.ClosedInput();
        var second = State("second");
        second.Target.Status = "passed";
        second.Comparison.Status = "verified";
        input.StatePlan!.States.Add(second);
        input = input with
        {
            FindingLedger = new SemanticFindingLedger
            {
                Findings =
                [
                    new()
                    {
                        Id = "open-finding", Category = "behavior", StateIds = ["second"],
                        SourceInvariant = "Preserve state", Status = "open", CompletionCondition = "Observe the fix"
                    }
                ]
            }
        };
        var snapshot = EnforcementEvaluator.Evaluate(input);
        Assert.AreEqual(WorkflowStage.SemanticConvergence, snapshot.Stage);
        Assert.AreEqual("second", snapshot.ActiveSlice);
        Assert.AreEqual("finding", snapshot.Frontier?.Kind);
    }

    [TestMethod]
    public void Diagnosis_HistoricalFailureSupersededByDifferentSuccessfulReplayIsNotActive()
    {
        using var fixture = new EvaluatorFixture();
        var input = fixture.ClosedInput();
        input.Attempts.Attempts.Add(new EnforcementAttempt
        {
            Kind = "runtime", Signature = "historical", TargetFingerprint = "old",
            RecordedAt = input.Replay!.StartedAt.AddMinutes(-1)
        });
        var snapshot = EnforcementEvaluator.Evaluate(input);
        Assert.AreEqual("passed", snapshot.Status);
    }

    [TestMethod]
    public void Diagnosis_ClosureRuntimeFailureReopensSemanticSlice()
    {
        using var fixture = new EvaluatorFixture();
        var input = fixture.ClosedInput() with
        {
            ReportValidation = new PipelineValidationSummary("captured", ["state-1"], "unverified", [], "unverified", "pending")
        };
        var closure = EnforcementEvaluator.Evaluate(input);
        input.Attempts.Attempts.Add(new EnforcementAttempt
        {
            Kind = "runtime", Signature = "new-failure", TargetFingerprint = input.TargetFingerprint,
            StateId = "state-1", SliceId = "state-1", RecordedAt = input.GeneratedAt
        });
        var snapshot = EnforcementEvaluator.Evaluate(input with { Progress = closure.Workflow });
        Assert.AreEqual(WorkflowStage.SemanticConvergence, snapshot.Stage);
        Assert.AreEqual("state-1", snapshot.ActiveSlice);
        Assert.AreEqual("diagnosis-required", snapshot.Frontier?.Kind);
    }

    [TestMethod]
    [DataRow("total-low")]
    [DataRow("total-high")]
    [DataRow("extension-low")]
    [DataRow("extension-high")]
    [DataRow("extension-count-low")]
    [DataRow("extension-count-high")]
    [DataRow("soft-low")]
    [DataRow("hard-high")]
    [DataRow("hard-above-total")]
    [DataRow("operation-high")]
    public void Policy_AllNumericRangesAreValidated(string mutation)
    {
        var policy = EnforcementPolicy.Balanced();
        switch (mutation)
        {
            case "total-low": policy.TotalHardMinutes = 0; break;
            case "total-high": policy.TotalHardMinutes = 10081; break;
            case "extension-low": policy.ExtensionMinutes = 0; break;
            case "extension-high": policy.ExtensionMinutes = 61; break;
            case "extension-count-low": policy.MaximumProgressExtensionsPerUnit = -1; break;
            case "extension-count-high": policy.MaximumProgressExtensionsPerUnit = 2; break;
            case "soft-low": policy.SourceOracle.SoftMinutes = 0; break;
            case "hard-high": policy.SourceOracle.HardMinutes = 1441; break;
            case "hard-above-total": policy.TotalHardMinutes = 20; break;
            case "operation-high": policy.SourceOracle.Operations.UiActions = 10001; break;
            default: Assert.Fail("Unknown test mutation."); break;
        }
        Assert.IsNotNull(WorkflowValidation.PolicyError(policy));
    }

    [TestMethod]
    [DataRow(WorkflowStage.SourceOracle, 0, 0, 0, 0, 0)]
    [DataRow(WorkflowStage.MechanicalStabilization, 3, 0, 0, 0, 0)]
    [DataRow(WorkflowStage.TargetBootstrap, 0, 2, 1, 1, 0)]
    [DataRow(WorkflowStage.SemanticConvergence, 3, 2, 1, 1, 40)]
    [DataRow(WorkflowStage.Closure, 1, 1, 0, 0, 10)]
    public void Policy_BalancedCeilingsAreStageSpecific(
        WorkflowStage stage, int builds, int replays, int diagnostics, int hypotheses, int ui)
    {
        var policy = EnforcementPolicy.Balanced();
        var counts = policy.For(stage).Operations;
        Assert.AreEqual(builds, counts.Builds);
        Assert.AreEqual(replays, counts.Replays);
        Assert.AreEqual(diagnostics, counts.DiagnosticRuns);
        Assert.AreEqual(hypotheses, counts.Hypotheses);
        Assert.AreEqual(ui, counts.UiActions);
        foreach (var operation in Enum.GetValues<WorkflowOperation>())
            Assert.AreEqual(counts.For(operation) > 0, WorkflowOperations.IsCompatible(stage, operation));
        Assert.IsNull(WorkflowValidation.PolicyError(policy));
    }

    [TestMethod]
    [DataRow(WorkflowStage.SourceOracle)]
    [DataRow(WorkflowStage.MechanicalStabilization)]
    [DataRow(WorkflowStage.TargetBootstrap)]
    [DataRow(WorkflowStage.Closure)]
    public void Operations_IncompatibleKindsCannotBeEnabledByCustomCeilings(WorkflowStage stage)
    {
        using var fixture = new EvaluatorFixture();
        var input = SemanticInput(fixture);
        var snapshot = EnforcementEvaluator.Evaluate(input);
        snapshot.Workflow!.Stage = stage;
        foreach (var operation in Enum.GetValues<WorkflowOperation>().Where(kind =>
            !WorkflowOperations.IsCompatible(stage, kind)))
        {
            input.Policy.For(stage).Operations.Increment(operation);
            Assert.IsFalse(WorkflowOperations.TryBegin(input, snapshot, operation, [], null, out var reason));
            StringAssert.Contains(reason, "not permitted");
        }
        Assert.IsEmpty(snapshot.Workflow.Operations);
    }

    [TestMethod]
    [DataRow(WorkflowOperation.DiagnosticRun)]
    [DataRow(WorkflowOperation.Hypothesis)]
    public void Operations_BootstrapDiagnosisRequiresRuntimeFailure(WorkflowOperation operation)
    {
        using var fixture = new EvaluatorFixture();
        var input = fixture.ClosedInput() with { Replay = null };
        var snapshot = EnforcementEvaluator.Evaluate(input);
        Assert.AreEqual(WorkflowStage.TargetBootstrap, snapshot.Stage);
        Assert.IsFalse(WorkflowOperations.TryBegin(input, snapshot, operation, [], null, out var reason));
        StringAssert.Contains(reason, "require a bootstrap runtime failure");
        Assert.IsEmpty(snapshot.Workflow!.Operations);
    }

    [TestMethod]
    public void Budget_DisabledExtensionsAreValidatedPerUnit()
    {
        using var fixture = new EvaluatorFixture();
        var input = ExpiredInput(fixture, 16);
        input.Policy.MaximumProgressExtensionsPerUnit = 0;
        var snapshot = EnforcementEvaluator.Evaluate(input with { TargetFingerprint = "correction" });
        Assert.AreEqual("within-budget", snapshot.Budget?.Decision);
        var unit = snapshot.Workflow!.Units.Single();
        unit.ExtensionGrantedAt = input.GeneratedAt;
        Assert.IsNotNull(WorkflowValidation.ProgressError(snapshot.Workflow, input.Policy, input.GeneratedAt));
    }

    [TestMethod]
    public void Diagnosis_FailedReplayWithoutLedgerStillRequiresSourceFirstDiagnosis()
    {
        using var fixture = new EvaluatorFixture();
        var input = fixture.ClosedInput();
        input.Replay!.Success = false;
        input.Replay.Signature = "runtime-failure";
        var snapshot = EnforcementEvaluator.Evaluate(input);
        Assert.AreEqual("diagnosis-required", snapshot.Frontier?.Kind);
        Assert.AreEqual("runtime-failure", snapshot.Frontier?.Signature);
        Assert.IsEmpty(input.Attempts.Attempts);
    }

    private static EnforcementEvaluationInput SemanticInput(EvaluatorFixture fixture)
    {
        var input = fixture.ClosedInput();
        input.StatePlan!.States[0].Target.Status = "unverified";
        return input;
    }

    private static EnforcementEvaluationInput ExpiredInput(EvaluatorFixture fixture, int minutes)
    {
        var input = SemanticInput(fixture);
        var snapshot = EnforcementEvaluator.Evaluate(input);
        snapshot.Workflow!.StartedAt = input.GeneratedAt.AddMinutes(-minutes);
        snapshot.Workflow.Units[0].StartedAt = snapshot.Workflow.StartedAt;
        snapshot.Workflow.Units[0].Epochs[0] = snapshot.Workflow.Units[0].Epochs[0] with
        {
            EnteredAt = snapshot.Workflow.StartedAt.Value
        };
        return input with { Progress = snapshot.Workflow };
    }

    private static MigrationState State(string id) => new()
    {
        Id = id, Actions = ["Observe"], ExpectedOutcome = "Expected state",
        Source = new StateObservation { Status = "captured", Evidence = ["evidence.txt"] },
        Target = new StateObservation { Status = "unverified", Evidence = ["evidence.txt"] },
        Comparison = new StateComparison { Status = "unverified" }
    };

    private static FailureDiagnosis Diagnosis(DateTimeOffset at) => new()
    {
        RecordedAt = at, SourceLocations = ["source.cs:1"], TargetLocations = ["target.cs:1"],
        SourceInvariant = "Preserve source invariant",
        Hypothesis = "Removing the guard causes the crash",
        DiscriminatingTest = "Restore only the guard and observe",
        Evidence = ["evidence.txt"]
    };
}
