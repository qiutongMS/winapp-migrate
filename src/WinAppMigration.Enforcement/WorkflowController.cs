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
        bool sourceReady,
        bool buildFresh,
        bool replayFresh,
        IReadOnlyList<SemanticSlice> slices)
    {
        var progress = input.Progress is null
            ? new WorkflowProgress
            {
                LastTargetFingerprint = input.TargetFingerprint,
                LastObservedAt = input.GeneratedAt,
                StartedAt = input.GeneratedAt,
                Stage = WorkflowStage.SourceOracle,
                UnresolvedRequiredStates = Unresolved(slices),
                CompletedSliceIds = slices.Where(slice => slice.Resolved).Select(slice => slice.Id).ToList()
            }
            : Copy(input.Progress);
        var active = SemanticSlicePlanner.Select(slices) ??
            slices.FirstOrDefault(slice => !slice.Complete);
        var previousUnit = progress.Units.FirstOrDefault(unit => unit.Epochs.Any(epoch => epoch.ExitedAt is null));

        if (input.Progress is not null)
        {
            previousUnit ??= CurrentUnit(progress, active?.Id);
            if (previousUnit.Epochs.Count == 0)
                Activate(progress, previousUnit, input.GeneratedAt, input.TargetFingerprint);
            if (progress.LastTargetFingerprint != input.TargetFingerprint)
                AddEvent(progress, previousUnit, "target-changed", input.TargetFingerprint, input.GeneratedAt);
            var unresolved = Unresolved(slices);
            if (unresolved.Count < progress.UnresolvedRequiredStates.Count)
                AddEvent(progress, previousUnit, "required-states-reduced", $"remaining:{string.Join("|", unresolved)}", input.GeneratedAt);
            foreach (var slice in slices.Where(slice => slice.Resolved &&
                !progress.CompletedSliceIds.Contains(slice.Id, StringComparer.Ordinal)))
                AddEvent(progress, previousUnit, "slice-completed", slice.Id, input.GeneratedAt);
            foreach (var attempt in input.Attempts.Attempts.Where(attempt =>
                EnforcementEvaluator.DiagnosisIsComplete(input.TargetRoot, attempt)))
                AddEvent(progress, previousUnit, "diagnosis-completed",
                    $"{attempt.Signature}:{attempt.TargetFingerprint}", input.GeneratedAt);
            if (progress.Stage == WorkflowStage.SourceOracle && sourceReady)
                AddEvent(progress, previousUnit, "source-oracle-completed", "source-oracle", input.GeneratedAt);
            if (progress.Stage == WorkflowStage.MechanicalStabilization && buildFresh)
                AddEvent(progress, previousUnit, "mechanical-stabilized", "mechanical", input.GeneratedAt);
            if (progress.Stage == WorkflowStage.TargetBootstrap && replayFresh)
                AddEvent(progress, previousUnit, "bootstrap-completed", "bootstrap", input.GeneratedAt);
        }
        progress.UnresolvedRequiredStates = Unresolved(slices);
        progress.CompletedSliceIds = slices.Where(slice => slice.Resolved).Select(slice => slice.Id).ToList();
        progress.Slices = slices.Select(slice =>
        {
            var previous = progress.Slices.FirstOrDefault(item => item.SliceId == slice.Id);
            return new WorkflowSliceProgress
            {
                SliceId = slice.Id, Resolved = slice.Resolved, EvidenceFresh = slice.Complete,
                EvidenceTargetFingerprint = slice.Complete ? input.TargetFingerprint : previous?.EvidenceTargetFingerprint,
                EvidenceGeneration = slice.Complete ? input.Replay?.Id : previous?.EvidenceGeneration
            };
        }).ToList();
        progress.LastTargetFingerprint = input.TargetFingerprint;
        progress.LastObservedAt = input.GeneratedAt;
        progress.Stage = NextStage(
            progress.Stage, buildFresh, replayFresh,
            input.Replay is { Success: true } && input.Build is { Success: true } &&
                input.Replay.TargetFingerprint == input.Build.TargetFingerprint,
            slices.Count > 0 && slices.All(slice => slice.Resolved));
        if (progress.Stage == WorkflowStage.Closure)
            active = slices.FirstOrDefault(slice => slice.Resolved && !slice.Complete);
        progress.ActiveSliceId = progress.Stage is WorkflowStage.SemanticConvergence or WorkflowStage.Closure
            ? active?.Id : null;
        var activeStates = active?.States.Where(state => progress.Stage == WorkflowStage.Closure ||
            EnforcementEvaluator.StateRequiresWork(input, state)).ToArray() ?? [];
        progress.ActiveStateId = progress.ActiveSliceId is not null && activeStates.Length == 1 ? activeStates[0].Id : null;
        var unit = CurrentUnit(progress, active?.Id);
        Activate(progress, unit, input.GeneratedAt, input.TargetFingerprint);

        var telemetry = WorkflowTelemetry.Measure(progress, unit, input.GeneratedAt);
        return new WorkflowAssessment(progress, active, telemetry);
    }

    private static WorkflowStage NextStage(
        WorkflowStage current, bool buildFresh,
        bool replayFresh, bool historicalReplay, bool statesComplete)
    {
        if (current >= WorkflowStage.SemanticConvergence || historicalReplay)
            return statesComplete ? WorkflowStage.Closure : WorkflowStage.SemanticConvergence;
        if (!buildFresh) return WorkflowStage.MechanicalStabilization;
        return replayFresh
            ? statesComplete ? WorkflowStage.Closure : WorkflowStage.SemanticConvergence
            : WorkflowStage.TargetBootstrap;
    }

    public static WorkflowUnitProgress CurrentUnit(WorkflowProgress progress, string? activeSlice)
    {
        var slice = progress.Stage == WorkflowStage.SemanticConvergence ? activeSlice : null;
        var unit = progress.Units.FirstOrDefault(item => item.Stage == progress.Stage && item.SliceId == slice);
        if (unit is null)
        {
            unit = new WorkflowUnitProgress { Stage = progress.Stage, SliceId = slice };
            progress.Units.Add(unit);
        }
        return unit;
    }

    public static void Activate(
        WorkflowProgress progress, WorkflowUnitProgress account, DateTimeOffset now, string fingerprint)
    {
        var current = account.Epochs.LastOrDefault();
        if (current is { ExitedAt: null } && current.TargetFingerprint == fingerprint)
            return;
        CloseActiveEpochs(progress, now, "account-switched");
        account.StartedAt ??= now;
        account.Epochs.Add(new WorkflowActivationEpoch { EnteredAt = now, TargetFingerprint = fingerprint });
    }

    public static void CloseActiveEpochs(WorkflowProgress progress, DateTimeOffset now, string reason)
    {
        foreach (var epoch in progress.Units.SelectMany(unit => unit.Epochs).Where(epoch => epoch.ExitedAt is null))
        {
            epoch.ExitedAt = now;
            epoch.ExitReason = reason;
        }
    }

    public static void AddEvent(
        WorkflowProgress progress, WorkflowUnitProgress unit,
        string kind, string key, DateTimeOffset now)
    {
        if (progress.Events.Any(item => item.Kind == kind && item.Key == key))
            return;
        progress.Events.Add(new WorkflowProgressEvent
        {
            Kind = kind, Key = key, RecordedAt = now, Stage = unit.Stage, SliceId = unit.SliceId
        });
    }

    private static List<string> Unresolved(IReadOnlyList<SemanticSlice> slices) =>
        slices.Where(slice => !slice.Resolved && slice.Criticality == StateCriticality.Required)
            .SelectMany(slice => slice.States.Select(state => state.Id)).ToList();

    private static WorkflowProgress Copy(WorkflowProgress source) => source with
    {
        UnresolvedRequiredStates = [.. source.UnresolvedRequiredStates],
        CompletedSliceIds = [.. source.CompletedSliceIds],
        Units = source.Units.Select(unit => unit with
        {
            Operations = unit.Operations with { },
            Epochs = unit.Epochs.Select(epoch => epoch with { }).ToList()
        }).ToList(),
        Slices = source.Slices.Select(slice => slice with { }).ToList(),
        Events = [.. source.Events],
        Operations = [.. source.Operations]
    };
}

public static class WorkflowTelemetry
{
    public static WorkflowTelemetrySnapshot Measure(
        WorkflowProgress progress, WorkflowUnitProgress unit,
        DateTimeOffset now)
    {
        var elapsed = unit.ActiveMinutes(now);
        var total = progress.StartedAt is null ? 0 : (now - progress.StartedAt.Value).TotalMinutes;
        var measured = progress.Events.Any(item => item.Stage == unit.Stage && item.SliceId == unit.SliceId &&
            unit.StartedAt is not null && item.RecordedAt >= unit.StartedAt);
        var totals = new OperationCounts();
        foreach (var item in progress.Operations) totals.Increment(item.Kind);
        return new WorkflowTelemetrySnapshot
        {
            Stage = unit.Stage,
            SliceId = unit.SliceId,
            ElapsedMinutes = Math.Max(0, elapsed),
            TotalElapsedMinutes = Math.Max(0, total),
            MeasuredProgress = measured,
            Operations = unit.Operations with { },
            TotalOperations = totals
        };
    }
}
