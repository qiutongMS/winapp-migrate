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
    public void Completion_InputReviewDoesNotAuthorizeUnchangedBuildOrReplay(bool applicationDifference)
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
            Status = "blocked",
            TargetFingerprint = "target",
            Gates = [new() { Id = "state-closure", Status = "blocked", Reason = "incomplete" }],
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

        Assert.AreEqual("needs-work", completion.Outcome);
        Assert.AreEqual(applicationDifference ? "continue" : "input-review-required", snapshot.Disposition!.Kind);
        Assert.AreEqual(applicationDifference, snapshot.Disposition.Retryable);
        Assert.AreEqual(applicationDifference ? 1 : 0, completion.ActionableSurfaceIds.Count);
        Assert.AreEqual(applicationDifference ? 0 : 1, completion.InputReviewSurfaceIds.Count);
        Assert.HasCount(0, completion.ToolBlockedSurfaceIds);
        Assert.IsNull(WorkflowValidation.DispositionError(snapshot.Disposition));
        Assert.AreEqual(applicationDifference, snapshot.Disposition.AllowedOperations.Contains(WorkflowOperation.Build));
        Assert.AreEqual(applicationDifference, snapshot.Disposition.AllowedOperations.Contains(WorkflowOperation.Replay));
        Assert.Contains(WorkflowOperation.UiAction, snapshot.Disposition.AllowedOperations);
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
        CollectionAssert.AreEqual(new[] { "adaptive" }, completion.InputReviewSurfaceIds.ToArray());
        var slices = new[]
        {
            new SemanticSlice("adaptive", StateCriticality.Required, [], [], false, false, "input review")
                { InputReviewRequired = true },
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
        Assert.IsTrue(WorkflowCompletion.IsToolBlocked(state));
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
        CollectionAssert.AreEqual(new[] { "startup" }, needsWork.ToolBlockedSurfaceIds.ToArray());
        snapshot.StateVerifications.RemoveAt(1);
        Assert.AreEqual("tool-blocked", WorkflowCompletion.Assess(snapshot).Outcome);
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
