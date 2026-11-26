// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinAppMigration.Enforcement;

namespace UwpMigration.Enforcement.Tests;

[TestClass]
public sealed class WorkflowCompletionTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Completion_ReachableComparisonDiagnosticsDoNotRequestFurtherWork(bool applicationDifference)
    {
        var diagnostics = new List<SurfaceComparisonDiagnostic>
        {
            new("input-comparability-review", "capture", "Establish comparable inputs.")
        };
        if (applicationDifference)
            diagnostics.Add(new("app-property-mismatch", "application", "Restore the source label."));
        var snapshot = new EnforcementSnapshot
        {
            Phase = "semantic-convergence",
            Status = "passed",
            TargetFingerprint = "target",
            Gates = [new() { Id = "state-closure", Status = "passed", Reason = "reachable" }],
            Workflow = new WorkflowProgress { },
            StateVerifications =
            [
                new("adaptive", StateVerificationLevel.SurfaceReachable, "timestamp mismatch")
                {
                    Result = SurfaceVerificationResult.Mismatch,
                    ComparisonDiagnostics = diagnostics
                }
            ]
        };
        WorkflowDispositions.Update(snapshot, DateTimeOffset.UtcNow);
        var completion = WorkflowCompletion.Assess(snapshot);

        Assert.AreEqual("complete", completion.Outcome);
        Assert.AreEqual("complete", snapshot.Disposition!.Kind);
        Assert.IsFalse(snapshot.Disposition.Retryable);
        Assert.HasCount(0, completion.ActionableSurfaceIds);
        Assert.HasCount(0, completion.InputReviewSurfaceIds);
        Assert.HasCount(0, completion.ToolBlockedSurfaceIds);
        Assert.IsNull(WorkflowValidation.DispositionError(snapshot.Disposition));
        Assert.IsEmpty(snapshot.Disposition.AllowedOperations);
    }

    [TestMethod]
    public void Completion_InputReviewDoesNotStarveAnIndependentUncapturedSurface()
    {
        var snapshot = new EnforcementSnapshot
        {
            Phase = "semantic-convergence",
            Status = "blocked",
            TargetFingerprint = "target",
            Gates = [new() { Id = "state-closure", Status = "blocked", Reason = "incomplete" }],
            StateVerifications =
            [
                new("adaptive", StateVerificationLevel.SurfaceReachable, "timestamp mismatch")
                {
                    Result = SurfaceVerificationResult.Mismatch,
                    ComparisonDiagnostics = [new("input-comparability-review", "capture", "Establish comparable inputs.")]
                },
                new("store", "unverified", "not captured")
            ]
        };
        WorkflowDispositions.Update(snapshot, DateTimeOffset.UtcNow);

        Assert.AreEqual("continue", snapshot.Disposition!.Kind);
        var completion = WorkflowCompletion.Assess(snapshot);
        CollectionAssert.AreEqual(new[] { "store" }, completion.ActionableSurfaceIds.ToArray());
        Assert.IsEmpty(completion.InputReviewSurfaceIds);
        var slices = new[]
        {
            new SemanticSlice("adaptive", StateCriticality.Required, [], [], true, false, "reachable"),
            new SemanticSlice("store", StateCriticality.Required, [], [], false, false, "capture next")
        };
        Assert.AreEqual("store", SemanticSlicePlanner.Select(slices)!.Id);
    }

    [TestMethod]
    public void Completion_NormalizedFrameworkDifferencesDoNotHideApplicationRepairs()
    {
        var diagnostics = new List<SurfaceComparisonDiagnostic>
        {
            new("platform-host-normalized", "tool", "Aligned native hosts."),
            new("platform-control-normalized", "tool", "Aligned NavigationView providers.")
        };
        var state = new StateVerificationSummary("startup", StateVerificationLevel.SurfaceReachable, "normalized")
        {
            Result = SurfaceVerificationResult.Mismatch,
            ComparisonDiagnostics = diagnostics
        };
        Assert.IsFalse(WorkflowCompletion.IsToolBlocked(state));
        diagnostics.Add(new("app-property-mismatch", "application", "Restore the menu label."));
        Assert.IsFalse(WorkflowCompletion.IsToolBlocked(state));
        diagnostics[^1] = new("platform-control-review", "tool", "Unknown nested provider shape.");
        Assert.IsFalse(WorkflowCompletion.IsToolBlocked(state));
    }

    [TestMethod]
    public void Completion_DoesNotTreatAllContinueStatesAsEitherSuccessOrToolBlockage()
    {
        var snapshot = new EnforcementSnapshot
        {
            Phase = "semantic-convergence",
            Status = "blocked",
            TargetFingerprint = "target",
            Gates = [new() { Id = "state-closure", Status = "blocked", Reason = "incomplete" }],
            StateVerifications =
            [
                new("startup", StateVerificationLevel.SurfaceReachable, "provider difference")
                {
                    Result = SurfaceVerificationResult.Mismatch,
                    ComparisonDiagnostics = [new("platform-control-review", "tool", "Review provider semantics.")]
                },
                new("purchase", "unverified", "missing evidence")
            ]
        };
        var needsWork = WorkflowCompletion.Assess(snapshot);
        Assert.AreEqual("needs-work", needsWork.Outcome);
        CollectionAssert.AreEqual(new[] { "purchase" }, needsWork.ActionableSurfaceIds.ToArray());
        Assert.IsEmpty(needsWork.ToolBlockedSurfaceIds);
        snapshot.StateVerifications.RemoveAt(1);
        Assert.AreEqual("needs-work", WorkflowCompletion.Assess(snapshot).Outcome);
        snapshot.Gates.Add(new() { Id = "report-sync", Status = "blocked", Reason = "sync honest partial report" });
        Assert.AreEqual("needs-work", WorkflowCompletion.Assess(snapshot).Outcome);
    }

    [TestMethod]
    public void SliceSelection_DoesNotStarveIndependentActionableWorkBehindToolReview()
    {
        var slices = new[]
        {
            new SemanticSlice("startup", StateCriticality.Required, [], [], false, false, "tool review") { ToolBlocked = true },
            new SemanticSlice("store", StateCriticality.Required, [], [], false, false, "capture next")
        };
        Assert.AreEqual("store", SemanticSlicePlanner.Select(slices)!.Id);
        Assert.AreEqual("startup", SemanticSlicePlanner.Select([slices[0]])!.Id);
        Assert.IsFalse(slices[0].Complete);
        Assert.IsFalse(slices[0].Resolved);
    }
}
