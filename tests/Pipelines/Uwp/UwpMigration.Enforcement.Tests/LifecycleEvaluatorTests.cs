// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using WinAppMigration.Enforcement;

namespace UwpMigration.Enforcement.Tests;

public sealed partial class EnforcementEvaluatorTests
{
    private static readonly DateTimeOffset LifecycleNow = new(2026, 10, 26, 0, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void Accounts_ReentryTracksActiveEpochsAndOperationCounts()
    {
        var progress = new WorkflowProgress
        {
            LastTargetFingerprint = "target", LastObservedAt = LifecycleNow,
            StartedAt = LifecycleNow, Stage = WorkflowStage.SemanticConvergence, ActiveSliceId = "A",
            Disposition = new RunDisposition
            {
                Kind = "continue", ReasonCode = "workflow-active", Terminal = false,
                Retryable = true, Finalization = "not-ready"
            }
        };
        var a = WorkflowController.CurrentUnit(progress, "A");
        WorkflowController.Activate(progress, a, LifecycleNow, "target");
        a.Operations.Builds = 1;
        progress.OperationsStartedAt = LifecycleNow;
        progress.Operations.Add(new WorkflowOperationRecord
        {
            Kind = WorkflowOperation.Build, Stage = progress.Stage, SliceId = "A",
            TargetFingerprint = "target", RecordedAt = LifecycleNow
        });
        progress.ActiveSliceId = "B";
        var b = WorkflowController.CurrentUnit(progress, "B");
        WorkflowController.Activate(progress, b, LifecycleNow.AddMinutes(5), "target");
        Assert.AreEqual(5, a.ActiveMinutes(LifecycleNow.AddMinutes(25)));
        Assert.AreEqual(20, b.ActiveMinutes(LifecycleNow.AddMinutes(25)));
        progress.ActiveSliceId = "A";
        WorkflowController.Activate(progress, a, LifecycleNow.AddMinutes(25), "target");
        Assert.AreEqual(5, a.ActiveMinutes(LifecycleNow.AddMinutes(25)));
        var budget = WorkflowTelemetry.Measure(
            progress, a, LifecycleNow.AddMinutes(28));
        Assert.AreEqual(8, budget.ElapsedMinutes);
        Assert.AreEqual(28, budget.TotalElapsedMinutes);
        Assert.AreEqual(1, budget.Operations.Builds);
        Assert.HasCount(2, a.Epochs);
        Assert.AreEqual(LifecycleNow.AddMinutes(5), a.Epochs[0].ExitedAt);
        Assert.AreEqual("account-switched", a.Epochs[0].ExitReason);
        progress.LastObservedAt = LifecycleNow.AddMinutes(28);
        Assert.IsNull(WorkflowValidation.ProgressError(progress, progress.LastObservedAt));

        WorkflowController.CloseActiveEpochs(progress, progress.LastObservedAt, "paused");
        WorkflowController.Activate(progress, a, progress.LastObservedAt.AddMinutes(20), "corrected");
        var reopened = WorkflowTelemetry.Measure(
            progress, a, progress.LastObservedAt.AddMinutes(20));
        Assert.AreEqual(8, reopened.ElapsedMinutes);
        Assert.AreEqual(1, reopened.Operations.Builds);
    }

    [TestMethod]
    public void Resolution_TargetChangeAndFreshReplayKeepSchedulerOnUnresolvedCategory()
    {
        using var fixture = new EvaluatorFixture();
        var input = LifecycleInput(fixture);
        input.StatePlan!.States.Add(State("category"));
        var before = EnforcementEvaluator.Evaluate(input);
        Assert.AreEqual("category", before.ActiveSlice);
        var changed = EnforcementEvaluator.Evaluate(input with
        {
            Progress = before.Workflow, TargetFingerprint = "changed", GeneratedAt = LifecycleNow.AddMinutes(1)
        });
        Assert.AreEqual("category", changed.ActiveSlice);
        Assert.IsTrue(changed.Workflow!.Slices.Single(slice => slice.SliceId == "state-1").Resolved);
        Assert.IsFalse(changed.Workflow.Slices.Single(slice => slice.SliceId == "state-1").EvidenceFresh);
        input.Build!.TargetFingerprint = "changed";
        input.Replay!.TargetFingerprint = "changed";
        input.Replay.StartedAt = LifecycleNow.AddMinutes(2);
        input.Replay.CompletedAt = input.Replay.StartedAt.AddSeconds(1);
        input.Replay.Id = "replay-2";
        SealCanonicalFixture(input with { TargetFingerprint = "changed" });
        var replayed = EnforcementEvaluator.Evaluate(input with
        {
            Progress = changed.Workflow, TargetFingerprint = "changed", GeneratedAt = LifecycleNow.AddMinutes(3)
        });
        Assert.AreEqual("category", replayed.ActiveSlice);
        Assert.AreEqual(WorkflowStage.SemanticConvergence, replayed.Stage);
        Assert.IsTrue(replayed.Workflow!.Slices.Single(slice => slice.SliceId == "state-1").Resolved);
        Assert.IsFalse(replayed.Workflow.Slices.Single(slice => slice.SliceId == "state-1").EvidenceFresh);
        Assert.AreEqual("replay-1", replayed.Workflow.Slices[0].EvidenceGeneration);
        input.StatePlan.States[1].Dependencies = ["state-1"];
        var dependent = EnforcementEvaluator.Evaluate(input with
        {
            Progress = replayed.Workflow, TargetFingerprint = "changed", GeneratedAt = LifecycleNow.AddMinutes(4)
        });
        Assert.AreEqual("category", dependent.ActiveSlice);
    }

    [TestMethod]
    public void Accounts_SemanticReducerReopensFiveMinuteSliceAfterTwentyMinutesElsewhere()
    {
        using var fixture = new EvaluatorFixture();
        var input = LifecycleInput(fixture);
        input.StatePlan!.States[0].Target.Status = "unverified";
        input.StatePlan.States.Add(State("B"));
        var a = EnforcementEvaluator.Evaluate(input);
        Assert.IsTrue(WorkflowOperations.TryBegin(input, a, WorkflowOperation.Build, [], null, out var reason), reason);
        var accountId = a.Workflow!.Units.Single().AccountId;
        input.StatePlan.States[0].Target.Status = "passed";
        var b = EnforcementEvaluator.Evaluate(input with
        {
            Progress = a.Workflow, GeneratedAt = LifecycleNow.AddMinutes(5)
        });
        Assert.AreEqual("B", b.ActiveSlice);
        input.StatePlan.States[0].Comparison.Status = "failed";
        var reopened = EnforcementEvaluator.Evaluate(input with
        {
            Progress = b.Workflow, GeneratedAt = LifecycleNow.AddMinutes(25)
        });
        Assert.AreEqual("state-1", reopened.ActiveSlice);
        Assert.AreEqual(5, reopened.Telemetry!.ElapsedMinutes);
        Assert.AreEqual(1, reopened.Telemetry.Operations.Builds);
        Assert.AreEqual(20, reopened.Workflow!.Units.Single(unit => unit.SliceId == "B")
            .ActiveMinutes(LifecycleNow.AddMinutes(25)));
        Assert.AreEqual(accountId, reopened.Workflow.Units.Single(unit => unit.SliceId == "state-1").AccountId);
        var running = EnforcementEvaluator.Evaluate(input with
        {
            Progress = reopened.Workflow, GeneratedAt = LifecycleNow.AddMinutes(28)
        });
        Assert.AreEqual(8, running.Telemetry!.ElapsedMinutes);
        Assert.AreEqual(28, running.Telemetry.TotalElapsedMinutes);
        Assert.AreEqual(1, running.Telemetry.Operations.Builds);
    }

    [TestMethod]
    public void Closure_RevalidatesStaleResolutionAndFailedComparisonReopensSemanticWork()
    {
        using var fixture = new EvaluatorFixture();
        var input = LifecycleInput(fixture) with
        {
            ReportValidation = new PipelineValidationSummary("captured", ["state-1"], "unverified", [], "unverified", "pending")
        };
        var closure = EnforcementEvaluator.Evaluate(input);
        Assert.AreEqual(WorkflowStage.Closure, closure.Stage);
        Assert.IsFalse(closure.Disposition!.Terminal);
        var changed = EnforcementEvaluator.Evaluate(input with
        {
            Progress = closure.Workflow, TargetFingerprint = "changed", GeneratedAt = LifecycleNow.AddMinutes(1)
        });
        Assert.AreEqual(WorkflowStage.Closure, changed.Stage);
        Assert.AreEqual("state-1", changed.ActiveSlice);
        Assert.AreEqual("build", changed.Frontier?.Kind);
        input.Build!.TargetFingerprint = "changed";
        input.Replay!.TargetFingerprint = "changed";
        input.Replay.StartedAt = LifecycleNow.AddMinutes(2);
        input.Replay.CompletedAt = input.Replay.StartedAt.AddSeconds(1);
        SealCanonicalFixture(input with { TargetFingerprint = "changed" });
        var stale = EnforcementEvaluator.Evaluate(input with
        {
            Progress = changed.Workflow, TargetFingerprint = "changed", GeneratedAt = LifecycleNow.AddMinutes(3)
        });
        Assert.AreEqual(WorkflowStage.Closure, stale.Stage);
        Assert.AreEqual("state", stale.Frontier?.Kind);
        input.StatePlan!.States[0].Comparison.Status = "failed";
        var failed = EnforcementEvaluator.Evaluate(input with
        {
            Progress = stale.Workflow, TargetFingerprint = "changed", GeneratedAt = LifecycleNow.AddMinutes(4)
        });
        Assert.AreEqual(WorkflowStage.SemanticConvergence, failed.Stage);
        Assert.AreEqual("state-1", failed.ActiveSlice);
        Assert.IsFalse(failed.Workflow!.Slices.Single().Resolved);
        Assert.IsTrue(failed.Workflow.Units.Single(unit => unit.Stage == WorkflowStage.Closure)
            .Epochs.All(epoch => epoch.ExitedAt is not null));
    }

    [TestMethod]
    public void Accounts_GlobalElapsedTimeIncludesInactiveWallClockAsTelemetry()
    {
        using var fixture = new EvaluatorFixture();
        var input = LifecycleInput(fixture);
        input.StatePlan!.States[0].Target.Status = "unverified";
        var observed = EnforcementEvaluator.Evaluate(input);
        WorkflowController.CloseActiveEpochs(observed.Workflow!, LifecycleNow.AddMinutes(5), "paused");
        observed.Workflow!.LastObservedAt = LifecycleNow.AddMinutes(5);
        var observedLater = EnforcementEvaluator.Evaluate(input with
        {
            Progress = observed.Workflow, GeneratedAt = LifecycleNow.AddMinutes(150)
        });
        Assert.AreEqual("continue", observedLater.Disposition?.Kind);
        Assert.IsFalse(observedLater.Disposition!.Terminal);
        Assert.AreEqual(5, observedLater.Telemetry!.ElapsedMinutes);
        Assert.AreEqual(150, observedLater.Telemetry.TotalElapsedMinutes);
    }

    [TestMethod]
    [DataRow("complete")]
    [DataRow("infrastructure-failure")]
    public void Disposition_TerminalIsImmutableAndRejectsEveryOperation(string kind)
    {
        using var fixture = new EvaluatorFixture();
        var input = LifecycleInput(fixture);
        if (kind == "infrastructure-failure") input = input with { ArtifactError = "broken artifact" };
        var terminal = EnforcementEvaluator.Evaluate(input);
        Assert.AreEqual(kind, terminal.Disposition?.Kind);
        Assert.IsTrue(terminal.Disposition!.Terminal);
        Assert.IsEmpty(terminal.Disposition.AllowedOperations);
        var before = JsonSerializer.Serialize(terminal.Workflow, EnforcementStore.JsonOptions);
        var observation = input with
        {
            Progress = terminal.Workflow, PersistedDisposition = terminal.Disposition,
            TargetFingerprint = kind == "complete" ? input.TargetFingerprint : "another-change",
            GeneratedAt = LifecycleNow.AddDays(1), ArtifactError = null
        };
        var observed = EnforcementEvaluator.Evaluate(observation);
        Assert.AreEqual(terminal.Disposition, observed.Disposition);
        Assert.AreEqual(before, JsonSerializer.Serialize(observed.Workflow, EnforcementStore.JsonOptions));
        foreach (var operation in Enum.GetValues<WorkflowOperation>())
        {
            Assert.IsFalse(WorkflowOperations.TryBegin(observation, observed, operation, [], null, out var reason));
            StringAssert.Contains(reason, "terminal");
        }
        Assert.IsFalse(WorkflowOperations.TryObserveEvidence(observation, observed, [], out _));
    }

    [TestMethod]
    public void Disposition_BlockedRequiredRemainsRetryableForDeclaredStateEvidence()
    {
        using var fixture = new EvaluatorFixture();
        var input = LifecycleInput(fixture);
        input.StatePlan!.States[0].Target.Status = "blocked";

        var blocked = EnforcementEvaluator.Evaluate(input);

        Assert.AreEqual("continue", blocked.Disposition?.Kind);
        Assert.IsFalse(blocked.Disposition!.Terminal);
        Assert.IsTrue(blocked.Disposition.Retryable);
        CollectionAssert.Contains(blocked.Disposition.AllowedOperations, WorkflowOperation.UiAction);
        Assert.IsTrue(WorkflowOperations.TryBegin(
            input,
            blocked,
            WorkflowOperation.UiAction,
            ["evidence.txt"],
            "declared-state",
            out _,
            StateAttribution.Authorize(input, "state-1")));
    }

    [TestMethod]
    public void Disposition_RetiredBlockedRequiredTerminalFailsWithoutMutation()
    {
        using var fixture = new EvaluatorFixture();
        var input = LifecycleInput(fixture);
        input.StatePlan!.States[0].Target.Status = "blocked";
        var legacy = new RunDisposition
        {
            Kind = "blocked-required",
            ReasonCode = "required-work-blocked",
            Terminal = true,
            Retryable = false,
            AllowedOperations = [],
            Finalization = "blocked"
        };
        var progress = EnforcementEvaluator.Evaluate(input).Workflow!;
        progress.Disposition = legacy;
        var before = JsonSerializer.Serialize(progress, EnforcementStore.JsonOptions);

        var resumed = EnforcementEvaluator.Evaluate(input with
        {
            Progress = progress,
            PersistedDisposition = legacy,
            GeneratedAt = LifecycleNow.AddMinutes(1)
        });

        Assert.AreEqual("infrastructure-failure", resumed.Disposition?.Kind);
        Assert.IsTrue(resumed.Disposition!.Terminal);
        Assert.AreEqual(before, JsonSerializer.Serialize(progress, EnforcementStore.JsonOptions));
    }

    [TestMethod]
    public void Disposition_ElapsedTimeRemainsActiveWithoutPausing()
    {
        using var fixture = new EvaluatorFixture();
        var input = LifecycleInput(fixture);
        input.StatePlan!.States[0].Target.Status = "unverified";
        var active = EnforcementEvaluator.Evaluate(input);
        var observed = EnforcementEvaluator.Evaluate(input with
        {
            Progress = active.Workflow, GeneratedAt = LifecycleNow.AddMinutes(16)
        });
        Assert.AreEqual("continue", observed.Disposition?.Kind);
        Assert.IsFalse(observed.Disposition!.Terminal);
        Assert.IsTrue(observed.Disposition.Retryable);
        Assert.IsNotEmpty(observed.Disposition.AllowedOperations);
        Assert.AreEqual(16, observed.Telemetry!.ElapsedMinutes);
    }

    [TestMethod]
    public void Disposition_InvalidTerminalContractCannotClaimCompletion()
    {
        using var fixture = new EvaluatorFixture();
        var snapshot = EnforcementEvaluator.Evaluate(LifecycleInput(fixture) with
        {
            PersistedDisposition = new RunDisposition
            {
                Kind = "complete", ReasonCode = "closure-passed", Terminal = true,
                Retryable = true, Finalization = "complete"
            }
        });
        Assert.AreEqual("infrastructure-failure", snapshot.Disposition?.Kind);
        Assert.AreEqual("blocked", snapshot.Status);
        StringAssert.Contains(snapshot.Gates.Single(gate => gate.Id == "enforcement-artifacts").Reason, "disposition");
    }

    [TestMethod]
    [DataRow("1.0")]
    [DataRow("2.0")]
    [DataRow("4.0")]
    public void Progress_UnsupportedSchemasFailWithoutMutation(string schema)
    {
        using var fixture = new EvaluatorFixture();
        var input = LifecycleInput(fixture);
        input.StatePlan!.States[0].Target.Status = "unverified";
        var progress = EnforcementEvaluator.Evaluate(input).Workflow!;
        progress.SchemaVersion = schema;
        var before = JsonSerializer.Serialize(progress, EnforcementStore.JsonOptions);
        var error = WorkflowValidation.ProgressError(progress, LifecycleNow);
        Assert.IsNotNull(error);
        StringAssert.Contains(error, "Unsupported workflow progress schema");
        Assert.AreEqual(before, JsonSerializer.Serialize(progress, EnforcementStore.JsonOptions));
    }

    private static EnforcementEvaluationInput LifecycleInput(EvaluatorFixture fixture)
    {
        var input = fixture.ClosedInput() with { GeneratedAt = LifecycleNow };
        input.Build!.StartedAt = LifecycleNow.AddMinutes(-2);
        input.Build.CompletedAt = LifecycleNow.AddMinutes(-1).AddSeconds(-1);
        input.Replay!.StartedAt = LifecycleNow.AddMinutes(-1);
        input.Replay.CompletedAt = LifecycleNow.AddMinutes(-1).AddSeconds(1);
        File.SetLastWriteTimeUtc(fixture.Path("evidence.txt"), LifecycleNow.UtcDateTime);
        return input;
    }
}
