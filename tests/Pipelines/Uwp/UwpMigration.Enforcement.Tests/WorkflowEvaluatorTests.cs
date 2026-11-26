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
        input.StatePlan.States[0].Target.Status = "unverified";
        var source = EnforcementEvaluator.Evaluate(input);
        Assert.AreEqual(WorkflowStage.MechanicalStabilization, source.Stage);
        Assert.AreEqual("mechanical-stabilization", source.Phase);
        Assert.AreEqual("mechanical", source.Frontier?.Kind);
        Assert.IsFalse(source.Gates.Any(gate => gate.Id == "source-oracle"));

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
    public void Workflow_SemanticCorrectionKeepsWorkAndElapsedHistory()
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
        Assert.AreEqual(before.Workflow!.StartedAt, changed.Workflow!.StartedAt);
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
        Assert.AreEqual(WorkflowStage.Closure, snapshot.Stage);
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
    public void Slices_SelectsFirstDependencyReadyIncompleteSlice()
    {
        using var fixture = new EvaluatorFixture();
        var input = SemanticInput(fixture);
        var dependent = input.StatePlan!.States[0];
        dependent.Slice = "dependent";
        dependent.Dependencies = ["prerequisite"];
        var prerequisite = State("prerequisite");
        input.StatePlan.States.Add(prerequisite);
        AnchorPlan(input);
        var first = EnforcementEvaluator.Evaluate(input);
        Assert.AreEqual("prerequisite", first.ActiveSlice);
        CollectionAssert.AreEqual(new[] { "prerequisite" }, first.ActiveStateIds);
        AttestState(input, prerequisite);
        var second = EnforcementEvaluator.Evaluate(input with { Progress = first.Workflow });
        Assert.AreEqual("dependent", second.ActiveSlice);
        CollectionAssert.AreEqual(new[] { dependent.Id }, second.ActiveStateIds);
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
        Assert.AreEqual("state", snapshot.Frontier?.Kind);
        Assert.AreEqual("blocked", snapshot.Status);
        Assert.AreEqual("verified", input.StatePlan.States[0].Comparison.Status);
        Assert.AreEqual("blocked", input.StatePlan.States[0].Target.Status);
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
    public void Diagnosis_UnscopedFailureHistoryDoesNotOverrideVerifiedSurfaceEvidence()
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
        Assert.AreEqual(WorkflowStage.Closure, snapshot.Stage);
        Assert.IsNull(snapshot.ActiveSlice);
        Assert.AreEqual("report-sync", snapshot.Frontier?.Kind);
    }

    [TestMethod]
    public void Operations_StagesDoNotAuthorizeOperations()
    {
        using var fixture = new EvaluatorFixture();
        var input = SemanticInput(fixture);
        var snapshot = EnforcementEvaluator.Evaluate(input);
        snapshot.Workflow!.Stage = WorkflowStage.SourceOracle;
        foreach (var operation in new[] { WorkflowOperation.Build, WorkflowOperation.Replay,
            WorkflowOperation.DiagnosticRun, WorkflowOperation.UiAction })
        {
            Assert.IsTrue(WorkflowOperations.TryBegin(input, snapshot, operation, [], null, out var reason), reason);
        }
    }

    [TestMethod]
    public void Operations_DiagnosticsDoNotRequireARecordedRuntimeFailure()
    {
        using var fixture = new EvaluatorFixture();
        var input = fixture.ClosedInput() with { Replay = null };
        var snapshot = EnforcementEvaluator.Evaluate(input);
        Assert.AreEqual(WorkflowStage.TargetBootstrap, snapshot.Stage);
        Assert.IsTrue(WorkflowOperations.TryBegin(input, snapshot, WorkflowOperation.DiagnosticRun, [], null, out var reason), reason);
        Assert.AreEqual(WorkflowOperation.DiagnosticRun, snapshot.Workflow!.Operations.Single().Kind);
    }

    [TestMethod]
    public void Diagnosis_FailedReplayRequiresASuccessfulReplayNotAMandatoryDiagnosis()
    {
        using var fixture = new EvaluatorFixture();
        var input = fixture.ClosedInput();
        input.Replay!.Success = false;
        input.Replay.Signature = "runtime-failure";
        SealCanonicalFixture(input);
        var snapshot = EnforcementEvaluator.Evaluate(input);
        Assert.AreEqual("replay", snapshot.Frontier?.Kind);
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
        return input with { Progress = snapshot.Workflow };
    }

    private static MigrationState State(string id) => new()
    {
        Id = id,
        Kind = "page",
        SourceDefinitions = [$"{id}.xaml"],
        TargetIdentity = [new() { Type = "Window", AutomationId = "main" }],
        Verification = new StateVerificationContract { TreeProbeIndex = 0 },
        Source = new StateObservation
        {
            Status = "captured",
            Evidence = ["evidence.txt"],
            TreeEvidence = "evidence.txt"
        },
        Target = new StateObservation { Status = "unverified", Evidence = ["evidence.txt"] },
        Comparison = new StateComparison { Status = "unverified" }
    };
}
