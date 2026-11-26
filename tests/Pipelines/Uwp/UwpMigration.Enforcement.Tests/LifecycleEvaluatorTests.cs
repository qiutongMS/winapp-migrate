// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using WinAppMigration.Enforcement;

namespace UwpMigration.Enforcement.Tests;

public sealed partial class EnforcementEvaluatorTests
{
    private static readonly DateTimeOffset LifecycleNow = new(2026, 10, 26, 0, 0, 0, TimeSpan.Zero);

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
        input.StatePlan.States[1].Dependencies = ["state-1"];
        var dependent = EnforcementEvaluator.Evaluate(input with
        {
            Progress = replayed.Workflow, TargetFingerprint = "changed", GeneratedAt = LifecycleNow.AddMinutes(4)
        });
        Assert.AreEqual("category", dependent.ActiveSlice);
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
        Assert.AreEqual("state", failed.Frontier?.Kind);
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
