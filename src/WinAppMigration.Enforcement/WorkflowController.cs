// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinAppMigration.Enforcement;

public sealed record WorkflowAssessment(
    WorkflowProgress Progress,
    SemanticSlice? ActiveSlice,
    WorkflowTelemetrySnapshot Telemetry);

public static class WorkflowController
{
    public static WorkflowAssessment Observe(
        EnforcementEvaluationInput input,
        bool buildFresh,
        bool replayFresh,
        IReadOnlyList<SemanticSlice> slices,
        IReadOnlyList<StateVerificationSummary> verifications)
    {
        var progress = input.Progress is null
            ? new WorkflowProgress
            {
                LastObservedAt = input.GeneratedAt,
                StartedAt = input.GeneratedAt,
                Stage = WorkflowStage.SourceOracle
            }
            : input.Progress with { Operations = [.. input.Progress.Operations] };
        var active = SemanticSlicePlanner.Select(slices) ??
            slices.FirstOrDefault(slice => !slice.Complete);
        progress.LastObservedAt = input.GeneratedAt;
        progress.Stage = NextStage(
            progress.Operations.Any(operation => operation.Stage >= WorkflowStage.SemanticConvergence),
            buildFresh, replayFresh,
            input.Replay is { Success: true } && input.Build is { Success: true } &&
                input.Replay.TargetFingerprint == input.Build.TargetFingerprint,
            slices.Count > 0 && slices.All(slice => slice.Resolved));
        if (progress.Stage == WorkflowStage.Closure)
            active = slices.FirstOrDefault(slice => slice.Resolved && !slice.Complete);
        progress.ActiveSliceId = progress.Stage is WorkflowStage.SemanticConvergence or WorkflowStage.Closure
            ? active?.Id : null;
        var activeStates = active?.States.Where(state => progress.Stage == WorkflowStage.Closure ||
            !verifications.First(verification => verification.Id == state.Id).IsReachable).ToArray() ?? [];
        progress.ActiveStateId = progress.ActiveSliceId is not null && activeStates.Length == 1 ? activeStates[0].Id : null;
        var telemetry = WorkflowTelemetry.Measure(progress, input.GeneratedAt);
        return new WorkflowAssessment(progress, active, telemetry);
    }

    private static WorkflowStage NextStage(
        bool semanticStarted, bool buildFresh,
        bool replayFresh, bool historicalReplay, bool statesComplete)
    {
        if (semanticStarted || historicalReplay)
            return statesComplete ? WorkflowStage.Closure : WorkflowStage.SemanticConvergence;
        if (!buildFresh) return WorkflowStage.MechanicalStabilization;
        return replayFresh
            ? statesComplete ? WorkflowStage.Closure : WorkflowStage.SemanticConvergence
            : WorkflowStage.TargetBootstrap;
    }

}

public static class WorkflowTelemetry
{
    public static WorkflowTelemetrySnapshot Measure(
        WorkflowProgress progress, DateTimeOffset now)
    {
        var startedAt = progress.StartedAt ?? progress.Operations.FirstOrDefault()?.RecordedAt;
        var total = startedAt is null ? 0 : (now - startedAt.Value).TotalMinutes;
        return new WorkflowTelemetrySnapshot
        {
            ElapsedMinutes = Math.Max(0, total),
            OperationCount = progress.Operations.Count
        };
    }
}
