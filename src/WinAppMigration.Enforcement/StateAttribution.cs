// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinAppMigration.Enforcement;

public sealed record StateSliceBinding(string? StateId, string? SliceId)
{
    public static StateSliceBinding Unbound { get; } = new(null, null);
}

public static class StateAttribution
{
    public static void Validate(StatePlanDocument? plan, StateSliceBinding binding)
    {
        if (binding.StateId is null && binding.SliceId is null) return;
        if (plan?.States is null || plan.States.Any(state => state is null))
            throw new InvalidDataException("A bound state/slice tuple requires a valid state plan.");
        if (binding.SliceId is null || !plan.States.Any(state => SemanticSlicePlanner.SliceId(state) == binding.SliceId) ||
            binding.StateId is not null && !plan.States.Any(state =>
                state.Id == binding.StateId && SemanticSlicePlanner.SliceId(state) == binding.SliceId))
            throw new InvalidDataException("Unknown or inconsistent state/slice attribution; a failure cannot be silently rebound.");
    }

    public static StateSliceBinding Authorize(EnforcementEvaluationInput input, string? requestedState = null)
    {
        var plan = input.StatePlan;
        if (plan is null)
        {
            if (requestedState is not null) throw new InvalidDataException("State authorization requires a plan.");
            return StateSliceBinding.Unbound;
        }
        if (!EnforcementEvaluator.ValidateStatePlan(plan, input.StatePlanError, out var reason))
            throw new InvalidDataException(reason);
        if (requestedState is not null)
        {
            var state = plan.States.SingleOrDefault(candidate => candidate.Id == requestedState) ??
                throw new InvalidDataException($"Unknown planned state: {requestedState}.");
            return new StateSliceBinding(state.Id, SemanticSlicePlanner.SliceId(state));
        }
        var slices = EnforcementEvaluator.AssessSlices(input, EnforcementEvaluator.FreshReplay(input));
        var debt = input.Progress?.Debt ?? [];
        var selected = SemanticSlicePlanner.Select(slices, debt);
        var revalidate = selected is null && slices.All(slice => slice.Resolved ||
            slice.Criticality == StateCriticality.Optional && debt.Any(item => item.SliceId == slice.Id));
        if (revalidate)
            selected = slices.FirstOrDefault(slice => slice.Resolved && !slice.Complete &&
                !debt.Any(item => item.SliceId == slice.Id));
        var candidates = selected?.States.Where(state => revalidate ||
            EnforcementEvaluator.StateRequiresWork(input, state)).ToArray() ?? [];
        return selected is null ? StateSliceBinding.Unbound :
            new StateSliceBinding(candidates.Length == 1 ? candidates[0].Id : null, selected.Id);
    }

    public static StateSliceBinding PersistedFallback(EnforcementEvaluationInput input)
    {
        if (input.Progress is null) return StateSliceBinding.Unbound;
        var persisted = new StateSliceBinding(input.Progress.ActiveStateId, input.Progress.ActiveSliceId);
        Validate(input.StatePlan, persisted);
        if (persisted.SliceId is null) return StateSliceBinding.Unbound;
        if (!EnforcementEvaluator.ValidateStatePlan(input.StatePlan, input.StatePlanError, out var reason))
            throw new InvalidDataException(reason);
        if (input.StatePlan!.States.Where(state => SemanticSlicePlanner.SliceId(state) == persisted.SliceId)
            .All(state => !EnforcementEvaluator.StateRequiresWork(input, state)))
            return StateSliceBinding.Unbound;
        var authorized = Authorize(input);
        if (authorized.SliceId != persisted.SliceId) return StateSliceBinding.Unbound;
        if (persisted.StateId is { } id && input.StatePlan!.States.Any(state =>
            state.Id == id && !EnforcementEvaluator.StateRequiresWork(input, state)))
            return StateSliceBinding.Unbound;
        return persisted;
    }

    internal static void ValidateFailures(EnforcementEvaluationInput input)
    {
        if (input.Replay is { } replay) Validate(input.StatePlan, new StateSliceBinding(replay.StateId, replay.SliceId));
        if (input.Diagnostic is { } diagnostic) Validate(input.StatePlan, new StateSliceBinding(diagnostic.StateId, diagnostic.SliceId));
        if (input.Build is { } build) Validate(input.StatePlan, new StateSliceBinding(build.StateId, build.SliceId));
        if (input.Progress is { } progress)
            Validate(input.StatePlan, new StateSliceBinding(progress.ActiveStateId, progress.ActiveSliceId));
        foreach (var attempt in input.Attempts.Attempts)
        {
            Validate(input.StatePlan, new StateSliceBinding(attempt.StateId, attempt.SliceId));
            if (attempt.Diagnosis is { } diagnosis &&
                (diagnosis.StateId != attempt.StateId || diagnosis.SliceId != attempt.SliceId))
                throw new InvalidDataException("Diagnosis attribution must exactly preserve its recorded failure tuple.");
            if (attempt.OperationId is not null)
            {
                var operation = AuthenticatedOperation(input.TargetRoot, attempt.OperationId);
                if (operation.StateId != attempt.StateId || operation.IntendedSliceId != attempt.SliceId)
                    throw new InvalidDataException("Failure tuple differs from its authenticated operation intent.");
            }
        }
    }

    internal static WorkflowOperationRecord AuthenticatedOperation(DirectoryInfo root, string operationId)
    {
        var (progress, error) = EnforcementStore.TryLoadAsync<WorkflowProgress>(root, EnforcementStore.ProgressPath).GetAwaiter().GetResult();
        if (error is not null || progress?.Operations is null ||
            !WorkflowOperationAuthentication.VerifyAnchored(root, progress.Operations))
            throw new InvalidDataException(error ?? "Operation attribution requires a complete authenticated history.");
        return progress.Operations.SingleOrDefault(operation => operation.Id == operationId) ??
            throw new InvalidDataException("Unknown operation attribution ID.");
    }
}
