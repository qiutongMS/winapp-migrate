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
                CompletedSliceIds = slices.Where(slice => slice.Complete).Select(slice => slice.Id).ToList()
            }
            : Copy(input.Progress);
        var active = SemanticSlicePlanner.Select(slices, progress.Debt);
        if (progress.Stage == WorkflowStage.SemanticConvergence && !replayFresh &&
            progress.ActiveSliceId is not null &&
            !progress.Debt.Any(debt => debt.SliceId == progress.ActiveSliceId))
            active = slices.FirstOrDefault(slice => slice.Id == progress.ActiveSliceId) ?? active;
        var previousUnit = progress.Units.FirstOrDefault(unit => unit.BlockedAt is not null) ??
            progress.Units.FirstOrDefault(unit => unit.Stage == progress.Stage &&
                unit.SliceId == progress.ActiveSliceId);

        if (input.Progress is not null)
        {
            previousUnit ??= CurrentUnit(progress, active?.Id);
            previousUnit.StartedAt ??= input.GeneratedAt;
            if (progress.LastTargetFingerprint != input.TargetFingerprint)
                AddEvent(progress, previousUnit, "target-changed", input.TargetFingerprint, input.GeneratedAt);
            var unresolved = Unresolved(slices);
            if (unresolved.Count < progress.UnresolvedRequiredStates.Count)
                AddEvent(progress, previousUnit, "required-states-reduced", $"remaining:{string.Join("|", unresolved)}", input.GeneratedAt);
            foreach (var slice in slices.Where(slice => slice.Complete &&
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
        progress.CompletedSliceIds = slices.Where(slice => slice.Complete).Select(slice => slice.Id).ToList();
        progress.LastTargetFingerprint = input.TargetFingerprint;
        progress.LastObservedAt = input.GeneratedAt;
        if (progress.OperationsStartedAt is null)
            progress.PolicyFingerprint = WorkflowValidation.PolicyFingerprint(input.Policy);

        if (previousUnit is not null &&
            !(previousUnit.SliceId is not null &&
                slices.Any(slice => slice.Id == previousUnit.SliceId && slice.Criticality == StateCriticality.Optional) &&
                progress.Debt.Any(debt => debt.SliceId == previousUnit.SliceId)))
        {
            var previousSlice = slices.FirstOrDefault(slice => slice.Id == previousUnit.SliceId);
            var priorBudget = WorkflowBudgets.Evaluate(input.Policy, progress, previousUnit, input.GeneratedAt,
                previousUnit.Stage != WorkflowStage.SemanticConvergence ||
                    previousSlice?.Criticality != StateCriticality.Optional);
            if (priorBudget.Decision is "stage-budget" or "blocked-required" or "continue-with-debt")
            {
                progress.Stage = previousUnit.Stage;
                progress.ActiveSliceId = previousUnit.SliceId;
                if (priorBudget.Decision == "continue-with-debt" && previousSlice is not null)
                    Defer(progress, previousSlice, priorBudget.Reason);
                return new WorkflowAssessment(progress, previousSlice, priorBudget, BudgetFrontier(priorBudget, previousSlice));
            }
        }
        progress.Stage = NextStage(
            progress.Stage, sourceReady, buildFresh, replayFresh,
            input.Replay is { Success: true } && input.Build is { Success: true } &&
                input.Replay.TargetFingerprint == input.Build.TargetFingerprint,
            slices.Count > 0 && slices.All(slice => slice.Complete ||
                slice.Criticality == StateCriticality.Optional && progress.Debt.Any(debt => debt.SliceId == slice.Id)));
        progress.ActiveSliceId = progress.Stage == WorkflowStage.SemanticConvergence ? active?.Id : null;
        var unit = CurrentUnit(progress, active?.Id);
        unit.StartedAt ??= input.GeneratedAt;

        // A semantic correction reuses its slice budget, including when closure is reopened.
        var operation = next?.Kind switch
        {
            "build" => (WorkflowOperation?)WorkflowOperation.Build,
            "replay" => WorkflowOperation.Replay,
            _ => null
        };
        var required = progress.Stage != WorkflowStage.SemanticConvergence ||
            active is null || active.Criticality == StateCriticality.Required;
        var budget = WorkflowBudgets.Evaluate(input.Policy, progress, unit, input.GeneratedAt, required, operation);
        EnforcementFrontier? frontier = null;
        if (budget.Decision is "stage-budget" or "blocked-required" or "continue-with-debt")
        {
            frontier = BudgetFrontier(budget, active);
            if (budget.Decision == "continue-with-debt" && active is not null &&
                active.Criticality == StateCriticality.Optional)
                Defer(progress, active, budget.Reason);
        }
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
            else if (active is null && slices.Any(slice => !slice.Complete))
            {
                var requiredBlocked = slices.Any(slice => !slice.Complete &&
                    slice.Criticality == StateCriticality.Required);
                frontier = new EnforcementFrontier
                {
                    Kind = requiredBlocked ? "required-blocked" : "continue-with-debt",
                    Reason = "Remaining slices depend on incomplete or deferred work; only independent dependency-ready work may proceed."
                };
            }
        }
        if (frontier is null && progress.Stage == WorkflowStage.Closure &&
            slices.Any(slice => !slice.Complete) && progress.Debt.Count > 0)
            frontier = new EnforcementFrontier
            {
                Kind = "continue-with-debt",
                Reason = "Required slices are complete; optional deferred slices remain explicit debt and are not verified."
            };
        return new WorkflowAssessment(progress, active, budget, frontier);
    }

    private static EnforcementFrontier BudgetFrontier(WorkflowBudgetSnapshot budget, SemanticSlice? slice) => new()
    {
        Kind = budget.Decision == "continue-with-debt" ? "continue-with-debt" : "stage-budget",
        Reason = budget.Reason, SliceId = slice?.Id,
        StateIds = slice?.States.Select(state => state.Id).ToList() ?? []
    };

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
        slices.Where(slice => !slice.Complete && slice.Criticality == StateCriticality.Required)
            .SelectMany(slice => slice.States.Select(state => state.Id)).ToList();

    private static WorkflowStage NextStage(
        WorkflowStage current, bool sourceReady, bool buildFresh,
        bool replayFresh, bool historicalReplay, bool statesComplete)
    {
        if (current >= WorkflowStage.SemanticConvergence || historicalReplay)
            return statesComplete && replayFresh ? WorkflowStage.Closure : WorkflowStage.SemanticConvergence;
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
        Units = source.Units.Select(unit => unit with { Operations = unit.Operations with { } }).ToList(),
        Events = [.. source.Events],
        Operations = [.. source.Operations],
        Debt = [.. source.Debt]
    };
}

public static class WorkflowBudgets
{
    public static WorkflowBudgetSnapshot Evaluate(
        EnforcementPolicy policy, WorkflowProgress progress, WorkflowUnitProgress unit,
        DateTimeOffset now, bool required, WorkflowOperation? operation = null)
    {
        var stage = policy.For(unit.Stage);
        var elapsed = unit.StartedAt is null ? 0 : (now - unit.StartedAt.Value).TotalMinutes;
        var total = progress.StartedAt is null ? 0 : (now - progress.StartedAt.Value).TotalMinutes;
        var measured = progress.Events.Any(item => item.Stage == unit.Stage && item.SliceId == unit.SliceId &&
            unit.StartedAt is not null && item.RecordedAt >= unit.StartedAt);
        var decision = "within-budget";
        var reason = "Stage and total budgets permit the next operation.";
        var ceiling = operation is not null &&
            unit.Operations.For(operation.Value) >= stage.Operations.For(operation.Value);
        var overCeiling = Enum.GetValues<WorkflowOperation>().Any(kind =>
            unit.Operations.For(kind) > stage.Operations.For(kind));
        if (unit.BlockedAt is not null || total >= policy.TotalHardMinutes || elapsed >= stage.HardMinutes || ceiling || overCeiling)
        {
            decision = unit.BlockedAt is not null || required ? "blocked-required" : "continue-with-debt";
            reason = unit.BlockedReason ?? (total >= policy.TotalHardMinutes
                ? "Total workflow hard limit expired; no more controlled operations are permitted."
                : ceiling || overCeiling
                    ? $"Stage operation ceiling reached for {operation?.ToString() ?? "recorded operations"}."
                    : "Stage hard limit expired.");
            if (unit.BlockedAt is null)
            {
                reason += required ? " Required work is blocked." : " Optional work must be deferred with debt.";
                if (required)
                {
                    unit.BlockedAt = now;
                    unit.BlockedReason = reason;
                }
            }
        }
        else
        {
            if (elapsed >= stage.SoftMinutes && unit.ExtensionGrantedAt is null &&
                measured && policy.MaximumProgressExtensionsPerUnit > 0)
                unit.ExtensionGrantedAt = now;
            var soft = stage.SoftMinutes + (unit.ExtensionGrantedAt is null ? 0 : policy.ExtensionMinutes);
            if (elapsed >= soft)
            {
                decision = "stage-budget";
                reason = "Stage soft limit expired without an available measured-progress extension. Replan or stop; assertions of near-completion do not authorize operations.";
            }
            else if (unit.ExtensionGrantedAt is not null)
            {
                decision = "extension-granted";
                reason = "Measured progress authorized this budget unit's one configured extension; stage and total hard limits remain unchanged.";
            }
        }
        var totals = new OperationCounts();
        foreach (var item in progress.Operations) totals.Increment(item.Kind);
        return new WorkflowBudgetSnapshot
        {
            Stage = unit.Stage,
            SliceId = unit.SliceId,
            Decision = decision,
            Reason = reason,
            ElapsedMinutes = Math.Max(0, elapsed),
            TotalElapsedMinutes = Math.Max(0, total),
            SoftDeadline = unit.StartedAt?.AddMinutes(Math.Min(stage.HardMinutes,
                stage.SoftMinutes + (unit.ExtensionGrantedAt is null ? 0 : policy.ExtensionMinutes))),
            HardDeadline = unit.StartedAt?.AddMinutes(stage.HardMinutes),
            TotalHardDeadline = progress.StartedAt?.AddMinutes(policy.TotalHardMinutes),
            ExtensionsUsed = unit.ExtensionGrantedAt is null ? 0 : 1,
            MeasuredProgress = measured,
            Operations = unit.Operations with { },
            OperationCeilings = stage.Operations with { },
            TotalOperations = totals
        };
    }
}
