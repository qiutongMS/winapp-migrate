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
        var policy = EnforcementPolicy.Balanced();
        policy.SemanticConvergence.SoftMinutes = 60;
        policy.SemanticConvergence.HardMinutes = 90;
        var progress = new WorkflowProgress
        {
            PolicyFingerprint = WorkflowValidation.PolicyFingerprint(policy),
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
        var budget = WorkflowBudgets.Evaluate(
            policy, progress, a, LifecycleNow.AddMinutes(28));
        Assert.AreEqual(8, budget.ElapsedMinutes);
        Assert.AreEqual(28, budget.TotalElapsedMinutes);
        Assert.AreEqual(1, budget.Operations.Builds);
        Assert.AreEqual(0, budget.ExtensionsUsed);
        Assert.HasCount(2, a.Epochs);
        Assert.AreEqual(LifecycleNow.AddMinutes(5), a.Epochs[0].ExitedAt);
        Assert.AreEqual("account-switched", a.Epochs[0].ExitReason);
        progress.LastObservedAt = LifecycleNow.AddMinutes(28);
        Assert.IsNull(WorkflowValidation.ProgressError(progress, policy, progress.LastObservedAt));

        a.Operations.Builds = policy.SemanticConvergence.Operations.Builds;
        var ceiling = WorkflowBudgets.Evaluate(
            policy, progress, a, progress.LastObservedAt);
        Assert.AreEqual("within-budget", ceiling.Decision);
        Assert.AreEqual("within-budget", ceiling.ReasonCode);
        Assert.AreEqual(0, ceiling.ExtensionsUsed);
        WorkflowController.CloseActiveEpochs(progress, progress.LastObservedAt, "paused");
        WorkflowController.Activate(progress, a, progress.LastObservedAt.AddMinutes(20), "corrected");
        var reopened = WorkflowBudgets.Evaluate(
            policy, progress, a, progress.LastObservedAt.AddMinutes(20));
        Assert.AreEqual(8, reopened.ElapsedMinutes);
        Assert.AreEqual("within-budget", reopened.Decision);
        Assert.IsNull(a.BlockedAt);
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
        input.Policy.SemanticConvergence.SoftMinutes = 30;
        input.Policy.SemanticConvergence.HardMinutes = 60;
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
        Assert.AreEqual(5, reopened.Budget!.ElapsedMinutes);
        Assert.AreEqual(1, reopened.Budget.Operations.Builds);
        Assert.AreEqual(20, reopened.Workflow!.Units.Single(unit => unit.SliceId == "B")
            .ActiveMinutes(LifecycleNow.AddMinutes(25)));
        Assert.AreEqual(accountId, reopened.Workflow.Units.Single(unit => unit.SliceId == "state-1").AccountId);
        var running = EnforcementEvaluator.Evaluate(input with
        {
            Progress = reopened.Workflow, GeneratedAt = LifecycleNow.AddMinutes(28)
        });
        Assert.AreEqual(8, running.Budget!.ElapsedMinutes);
        Assert.AreEqual(28, running.Budget.TotalElapsedMinutes);
        Assert.AreEqual(1, running.Budget.Operations.Builds);
        Assert.AreEqual(3, running.Budget.OperationCeilings.Builds);
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
        Assert.AreEqual("revalidate", stale.Frontier?.Kind);
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
        Assert.AreEqual(5, observedLater.Budget!.ElapsedMinutes);
        Assert.AreEqual(150, observedLater.Budget.TotalElapsedMinutes);
        Assert.AreEqual("within-budget", observedLater.Budget.Decision);
    }

    [TestMethod]
    public void Debt_RemainsNonterminalAndDoesNotHideRequiredClosureRevalidation()
    {
        using var fixture = new EvaluatorFixture();
        var input = LifecycleInput(fixture);
        var optional = State("optional");
        optional.Criticality = StateCriticality.Optional;
        optional.Target.Status = "blocked";
        input.StatePlan!.States.Add(optional);
        var deferred = EnforcementEvaluator.Evaluate(input);
        Assert.AreEqual("continue-with-debt", deferred.Disposition?.Kind);
        Assert.IsFalse(deferred.Disposition!.Terminal);
        Assert.IsEmpty(deferred.Disposition.AllowedOperations);
        Assert.IsTrue(deferred.Workflow!.Units.SelectMany(unit => unit.Epochs).All(epoch => epoch.ExitedAt is not null));
        var closure = EnforcementEvaluator.Evaluate(input with
        {
            Progress = deferred.Workflow, GeneratedAt = LifecycleNow.AddMinutes(1)
        });
        Assert.AreEqual(WorkflowStage.Closure, closure.Stage);
        Assert.AreEqual("continue-with-debt", closure.Disposition?.Kind);
        var changed = EnforcementEvaluator.Evaluate(input with
        {
            Progress = closure.Workflow, TargetFingerprint = "changed", GeneratedAt = LifecycleNow.AddMinutes(2)
        });
        Assert.AreEqual(WorkflowStage.Closure, changed.Stage);
        Assert.AreEqual("state-1", changed.ActiveSlice);
        Assert.AreEqual("build", changed.Frontier?.Kind);
        Assert.AreEqual("continue", changed.Disposition?.Kind);
        Assert.IsTrue(changed.Workflow!.Slices.Single(slice => slice.SliceId == "state-1").Resolved);
    }

    [TestMethod]
    [DataRow("complete")]
    [DataRow("blocked-required")]
    [DataRow("infrastructure-failure")]
    public void Disposition_TerminalIsImmutableAndRejectsEveryOperation(string kind)
    {
        using var fixture = new EvaluatorFixture();
        var input = LifecycleInput(fixture);
        if (kind == "blocked-required")
        {
            input.StatePlan!.States[0].Target.Status = "blocked";
        }
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
        Assert.AreEqual("within-budget", observed.Budget?.Decision);
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
    public void ProgressSchema20_PreservesEvidenceHistoryAndRetiresBudgetState()
    {
        using var fixture = new EvaluatorFixture();
        var input = LifecycleInput(fixture);
        input.StatePlan!.States[0].Target.Status = "unverified";
        var initial = EnforcementEvaluator.Evaluate(input);
        var changedInput = input with
        {
            Progress = initial.Workflow,
            TargetFingerprint = "corrected"
        };
        var changed = EnforcementEvaluator.Evaluate(changedInput);
        Assert.IsTrue(WorkflowOperations.TryBegin(
            changedInput, changed, WorkflowOperation.Build, [], null, out var reason), reason);
        var original = changed.Workflow!;
        original.SchemaVersion = "2.0";
        var unit = original.Units.Single(unit => unit.SliceId == "state-1");
        unit.ExtensionGrantedAt = LifecycleNow;
        original.Disposition = new RunDisposition
        {
            Kind = "pause-replan",
            ReasonCode = "stage-soft-limit",
            Terminal = false,
            Retryable = true,
            Finalization = "not-ready"
        };
        var before = JsonSerializer.Serialize(original, EnforcementStore.JsonOptions);

        var (imported, error) = WorkflowPersistence.ImportProgress(
            original, input.Policy, LifecycleNow);

        Assert.IsNull(error);
        Assert.AreEqual("3.0", imported!.SchemaVersion);
        Assert.AreEqual("continue", imported.Disposition?.Kind);
        Assert.AreEqual("retired-authorization-resumed", imported.Disposition?.ReasonCode);
        Assert.IsNull(imported.Units.Single(unit => unit.SliceId == "state-1").ExtensionGrantedAt);
        Assert.AreEqual(1, imported.Operations.Count);
        Assert.AreEqual(WorkflowOperation.Build, imported.Operations.Single().Kind);
        Assert.AreEqual(1, imported.Units.Single(unit => unit.SliceId == "state-1").Operations.Builds);
        Assert.AreEqual(before, JsonSerializer.Serialize(original, EnforcementStore.JsonOptions));
        Assert.IsNull(WorkflowValidation.ProgressError(imported, input.Policy, LifecycleNow));
        var json = JsonSerializer.Serialize(imported, EnforcementStore.JsonOptions);
        Assert.DoesNotContain("\"extensionGrantedAt\"", json);
        Assert.DoesNotContain("\"blockedAt\"", json);
        Assert.DoesNotContain("\"blockedReason\"", json);
        Assert.DoesNotContain("\"softMinutes\"", json);
        Assert.DoesNotContain("\"hardMinutes\"", json);
        Assert.DoesNotContain("\"operationCeilings\"", json);
    }

    [TestMethod]
    [DataRow(false, "continue")]
    [DataRow(true, "continue-with-debt")]
    public void ProgressSchema20_RemovesOnlyRetiredAuthorizationDebt(
        bool retainSemanticDebt, string expectedDisposition)
    {
        using var fixture = new EvaluatorFixture();
        var input = LifecycleInput(fixture);
        input.StatePlan!.States[0].Target.Status = "unverified";
        input.StatePlan.States[0].Criticality = StateCriticality.Optional;
        var original = EnforcementEvaluator.Evaluate(input).Workflow!;
        original.SchemaVersion = "2.0";
        original.Debt.Add(new WorkflowDebt
        {
            SliceId = "state-1",
            Reason = "Stage hard limit expired. Optional work must be deferred with debt."
        });
        if (retainSemanticDebt)
        {
            original.Debt.Add(new WorkflowDebt
            {
                SliceId = "external-state",
                Reason = "Required hardware is unavailable."
            });
        }
        original.Disposition = new RunDisposition
        {
            Kind = "continue-with-debt",
            ReasonCode = "optional-debt",
            Terminal = false,
            Retryable = true,
            Finalization = "debt"
        };
        WorkflowController.CloseActiveEpochs(
            original, LifecycleNow, "optional-deferred");

        var (imported, error) = WorkflowPersistence.ImportProgress(
            original, input.Policy, LifecycleNow);

        Assert.IsNull(error);
        Assert.AreEqual(expectedDisposition, imported!.Disposition?.Kind);
        Assert.IsFalse(imported.Debt.Any(debt => debt.SliceId == "state-1"));
        Assert.AreEqual(retainSemanticDebt ? 1 : 0, imported.Debt.Count);
        Assert.IsNull(WorkflowValidation.ProgressError(
            imported, input.Policy, LifecycleNow));
    }

    [TestMethod]
    public void ProgressSchema10_ImportsSingleContinuousAccountAndRetiresAuthorizationState()
    {
        using var fixture = new EvaluatorFixture();
        var input = LifecycleInput(fixture);
        input.StatePlan!.States[0].Target.Status = "unverified";
        var original = EnforcementEvaluator.Evaluate(input).Workflow!;
        original.SchemaVersion = "1.0";
        original.Disposition = null;
        original.Slices.Clear();
        original.CompletedSliceIds = ["previously-resolved"];
        var unit = original.Units.Single();
        unit.Epochs.Clear();
        unit.ExtensionGrantedAt = LifecycleNow.AddMinutes(4);
        unit.Operations.Builds = 2;
        original.OperationsStartedAt = LifecycleNow;
        original.Operations =
        [
            new WorkflowOperationRecord
            {
                Kind = WorkflowOperation.Build, Stage = unit.Stage, SliceId = unit.SliceId,
                TargetFingerprint = input.TargetFingerprint, RecordedAt = LifecycleNow
            },
            new WorkflowOperationRecord
            {
                Kind = WorkflowOperation.Build, Stage = unit.Stage, SliceId = unit.SliceId,
                TargetFingerprint = input.TargetFingerprint, RecordedAt = LifecycleNow.AddMinutes(1)
            }
        ];
        original.LastObservedAt = LifecycleNow.AddMinutes(5);
        var (imported, error) = WorkflowPersistence.ImportProgress(original, input.Policy, original.LastObservedAt);
        Assert.IsNull(error);
        Assert.AreEqual("3.0", imported!.SchemaVersion);
        Assert.AreEqual("1.0", original.SchemaVersion);
        Assert.IsEmpty(original.Units.Single().Epochs);
        Assert.AreEqual(5, imported.Units.Single().ActiveMinutes(original.LastObservedAt));
        Assert.AreEqual(2, imported.Units.Single().Operations.Builds);
        Assert.IsNull(imported.Units.Single().ExtensionGrantedAt);
        Assert.IsTrue(imported.Slices.Single().Resolved);
        Assert.IsFalse(imported.Slices.Single().EvidenceFresh);
        Assert.IsNull(WorkflowValidation.ProgressError(imported, input.Policy, original.LastObservedAt));
        Assert.IsNotNull(WorkflowValidation.ProgressError(original, input.Policy, original.LastObservedAt));
    }

    [TestMethod]
    [DataRow("multiple-units")]
    [DataRow("zero-units")]
    [DataRow("switched-unit")]
    [DataRow("bad-counts")]
    [DataRow("future-start")]
    [DataRow("unsupported-schema")]
    public void ProgressSchema10_AmbiguousOrInvalidHistoryFailsClosed(string mutation)
    {
        using var fixture = new EvaluatorFixture();
        var input = LifecycleInput(fixture);
        input.StatePlan!.States[0].Target.Status = "unverified";
        var progress = EnforcementEvaluator.Evaluate(input).Workflow!;
        progress.SchemaVersion = "1.0";
        progress.Disposition = null;
        progress.Slices.Clear();
        var unit = progress.Units.Single();
        unit.Epochs.Clear();
        switch (mutation)
        {
            case "zero-units": progress.Units.Clear(); break;
            case "multiple-units":
                progress.Units.Add(new WorkflowUnitProgress { Stage = WorkflowStage.MechanicalStabilization, StartedAt = LifecycleNow });
                break;
            case "switched-unit": progress.ActiveSliceId = "other"; break;
            case "bad-counts": unit.Operations.Builds = 1; break;
            case "future-start": unit.StartedAt = LifecycleNow.AddMinutes(1); break;
            case "unsupported-schema": progress.SchemaVersion = "4.0"; break;
        }
        var before = JsonSerializer.Serialize(progress, EnforcementStore.JsonOptions);
        var (unchanged, error) = WorkflowPersistence.ImportProgress(progress, input.Policy, LifecycleNow);
        Assert.IsNotNull(error);
        StringAssert.Contains(error, mutation == "unsupported-schema" ? "Unsupported workflow progress schema" : "Cannot safely import");
        Assert.AreSame(progress, unchanged);
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
