// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinAppMigration.Enforcement;

public sealed record WorkflowAssessment(
    WorkflowProgress Progress,
    SemanticSlice? ActiveSlice,
    WorkflowBudgetSnapshot Budget,
    EnforcementFrontier? Frontier);

public static class WorkflowController
{
    public static WorkflowAssessment Observe(
        EnforcementEvaluationInput input,
        bool sourceReady,
        bool buildFresh,
        bool replayFresh,
        IReadOnlyList<SemanticSlice> slices,
        EnforcementFrontier? next)
    {
        var progress = input.Progress is null
            ? new WorkflowProgress
            {
                PolicyFingerprint = WorkflowValidation.PolicyFingerprint(input.Policy),
                LastTargetFingerprint = input.TargetFingerprint,
                LastObservedAt = input.GeneratedAt,
                StartedAt = input.GeneratedAt,
                Stage = WorkflowStage.SourceOracle,
                UnresolvedRequiredStates = Unresolved(slices),
                CompletedSliceIds = slices.Where(slice => slice.Resolved).Select(slice => slice.Id).ToList()
            }
            : Copy(input.Progress);
        WorkflowDispositions.ResumeRetiredAuthorization(progress);
        var active = SemanticSlicePlanner.Select(slices, progress.Debt);
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
        if (progress.OperationsStartedAt is null)
            progress.PolicyFingerprint = WorkflowValidation.PolicyFingerprint(input.Policy);

        progress.Stage = NextStage(
            progress.Stage, sourceReady, buildFresh, replayFresh,
            input.Replay is { Success: true } && input.Build is { Success: true } &&
                input.Replay.TargetFingerprint == input.Build.TargetFingerprint,
            slices.Count > 0 && slices.All(slice => slice.Resolved ||
                slice.Criticality == StateCriticality.Optional && progress.Debt.Any(debt => debt.SliceId == slice.Id)));
        if (progress.Stage == WorkflowStage.Closure)
            active = slices.FirstOrDefault(slice => slice.Resolved && !slice.Complete &&
                !progress.Debt.Any(debt => debt.SliceId == slice.Id));
        progress.ActiveSliceId = progress.Stage is WorkflowStage.SemanticConvergence or WorkflowStage.Closure
            ? active?.Id : null;
        var unit = CurrentUnit(progress, active?.Id);
        Activate(progress, unit, input.GeneratedAt, input.TargetFingerprint);

        var budget = WorkflowBudgets.Evaluate(
            input.Policy, progress, unit, input.GeneratedAt);
        EnforcementFrontier? frontier = null;
        if (frontier is null && progress.Stage == WorkflowStage.SemanticConvergence &&
            next?.Kind is not ("diagnosis-required" or "change-required"))
        {
            if (active is { Blocked: true })
            {
                var optional = active.Criticality == StateCriticality.Optional;
                frontier = new EnforcementFrontier
                {
                    Kind = optional ? "continue-with-debt" : "required-blocked",
                    Reason = active.Reason,
                    SliceId = active.Id,
                    StateIds = active.States.Select(state => state.Id).ToList()
                };
                if (optional) Defer(progress, active, active.Reason);
            }
            else if (active is null && slices.Any(slice => !slice.Resolved))
            {
                var requiredBlocked = slices.Any(slice => !slice.Resolved &&
                    slice.Criticality == StateCriticality.Required);
                frontier = new EnforcementFrontier
                {
                    Kind = requiredBlocked ? "required-blocked" : "continue-with-debt",
                    Reason = "Remaining slices depend on incomplete or deferred work; only independent dependency-ready work may proceed."
                };
            }
        }
        if (frontier is null && progress.Stage == WorkflowStage.Closure &&
            active is null && next?.Kind is not ("build" or "replay") &&
            slices.Any(slice => !slice.Resolved) && progress.Debt.Count > 0)
            frontier = new EnforcementFrontier
            {
                Kind = "continue-with-debt",
                Reason = "Required slices are complete; optional deferred slices remain explicit debt and are not verified."
            };
        if (frontier is null && progress.Stage == WorkflowStage.Closure && active is not null &&
            next?.Kind is not ("build" or "replay" or "diagnosis-required" or "change-required"))
            frontier = new EnforcementFrontier
            {
                Kind = "revalidate", SliceId = active.Id,
                StateIds = active.States.Select(state => state.Id).ToList(),
                Reason = "Semantic resolution is retained; refresh this resolved slice's evidence for closure."
            };
        return new WorkflowAssessment(progress, active, budget, frontier);
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

    private static void Defer(WorkflowProgress progress, SemanticSlice slice, string reason)
    {
        if (!progress.Debt.Any(debt => debt.SliceId == slice.Id))
            progress.Debt.Add(new WorkflowDebt { SliceId = slice.Id, Reason = reason });
    }

    private static List<string> Unresolved(IReadOnlyList<SemanticSlice> slices) =>
        slices.Where(slice => !slice.Resolved && slice.Criticality == StateCriticality.Required)
            .SelectMany(slice => slice.States.Select(state => state.Id)).ToList();

    private static WorkflowStage NextStage(
        WorkflowStage current, bool sourceReady, bool buildFresh,
        bool replayFresh, bool historicalReplay, bool statesComplete)
    {
        if (current >= WorkflowStage.SemanticConvergence || historicalReplay)
            return statesComplete ? WorkflowStage.Closure : WorkflowStage.SemanticConvergence;
        if (!sourceReady) return current;
        if (!buildFresh) return WorkflowStage.MechanicalStabilization;
        current = WorkflowStage.TargetBootstrap;
        return replayFresh
            ? statesComplete ? WorkflowStage.Closure : WorkflowStage.SemanticConvergence
            : current;
    }

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
        Operations = [.. source.Operations],
        Debt = [.. source.Debt]
    };
}

public static class WorkflowBudgets
{
    public static WorkflowBudgetSnapshot Evaluate(
        EnforcementPolicy policy, WorkflowProgress progress, WorkflowUnitProgress unit,
        DateTimeOffset now)
    {
        var stage = policy.For(unit.Stage);
        var elapsed = unit.ActiveMinutes(now);
        var total = progress.StartedAt is null ? 0 : (now - progress.StartedAt.Value).TotalMinutes;
        var measured = progress.Events.Any(item => item.Stage == unit.Stage && item.SliceId == unit.SliceId &&
            unit.StartedAt is not null && item.RecordedAt >= unit.StartedAt);
        var totals = new OperationCounts();
        foreach (var item in progress.Operations) totals.Increment(item.Kind);
        return new WorkflowBudgetSnapshot
        {
            Stage = unit.Stage,
            SliceId = unit.SliceId,
            Decision = "within-budget",
            Reason = "Elapsed time and operation totals are telemetry only; operation compatibility and evidence freshness govern authorization.",
            ReasonCode = "within-budget",
            ElapsedMinutes = Math.Max(0, elapsed),
            TotalElapsedMinutes = Math.Max(0, total),
            SoftDeadline = null,
            HardDeadline = null,
            TotalHardDeadline = null,
            ExtensionsUsed = unit.ExtensionGrantedAt is null ? 0 : 1,
            MeasuredProgress = measured,
            Operations = unit.Operations with { },
            OperationCeilings = stage.Operations with { },
            TotalOperations = totals
        };
    }
}
