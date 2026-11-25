// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace WinAppMigration.Enforcement;

public sealed record WorkflowCompletion(
    [property: JsonPropertyName("outcome")] string Outcome,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("actionableSurfaceIds")] IReadOnlyList<string> ActionableSurfaceIds,
    [property: JsonPropertyName("toolBlockedSurfaceIds")] IReadOnlyList<string> ToolBlockedSurfaceIds)
{
    [JsonPropertyName("inputReviewSurfaceIds")]
    public IReadOnlyList<string> InputReviewSurfaceIds { get; init; } = [];

    public static WorkflowCompletion Assess(EnforcementSnapshot snapshot)
    {
        List<string> toolBlocked = [], inputReview = [], actionable = [];
        foreach (var state in snapshot.StateVerifications)
        {
            if (state.Result is SurfaceVerificationResult.Verified or "excluded")
                continue;
            if (IsToolBlocked(state)) toolBlocked.Add(state.Id);
            else if (RequiresInputReview(state)) inputReview.Add(state.Id);
            else actionable.Add(state.Id);
        }
        if (snapshot.Status == "passed" && snapshot.Disposition?.Kind == "complete")
            return new("complete", "Product closure passed.", [], []);
        if (snapshot.Disposition is { Terminal: true, Kind: "infrastructure-failure" })
            return new("infrastructure-failure", snapshot.Frontier?.Reason ?? "Product evidence is invalid.", actionable, toolBlocked);
        var otherWork = snapshot.Gates.Any(gate => gate.Status != "passed" &&
            gate.Id is not ("state-closure" or "report-closure"));
        return (actionable.Count == 0 && inputReview.Count == 0 && toolBlocked.Count > 0 && !otherWork
            ? new("tool-blocked", "Remaining required surfaces need tool support; closure has not passed.", [], toolBlocked)
            : new WorkflowCompletion("needs-work", snapshot.Frontier?.Reason ?? "Product closure is incomplete.", actionable, toolBlocked))
            with
        { InputReviewSurfaceIds = inputReview };
    }

    internal static bool IsToolBlocked(StateVerificationSummary state)
    {
        var diagnostics = state.ComparisonDiagnostics?
            .Where(item => !item.IsNormalization).ToArray();
        return state.Level == StateVerificationLevel.SurfaceReachable &&
            diagnostics is { Length: > 0 } && diagnostics.All(item => item.RepairOwner == "tool");
    }

    internal static bool RequiresInputReview(StateVerificationSummary state)
    {
        var diagnostics = state.ComparisonDiagnostics?
            .Where(item => !item.IsNormalization).ToArray();
        return state.Level == StateVerificationLevel.SurfaceReachable &&
            diagnostics is { Length: > 0 } &&
            diagnostics.Any(item => item.RepairOwner == "capture") &&
            diagnostics.All(item => item.RepairOwner is "capture" or "tool");
    }
}
