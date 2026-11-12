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
            throw new InvalidDataException("A bound surface/slice tuple requires a valid surface plan.");
        if (binding.SliceId is null || !plan.States.Any(state => SemanticSlicePlanner.SliceId(state) == binding.SliceId) ||
            binding.StateId is not null && !plan.States.Any(state =>
                state.Id == binding.StateId && SemanticSlicePlanner.SliceId(state) == binding.SliceId))
            throw new InvalidDataException("Unknown or inconsistent surface/slice attribution; a failure cannot be silently rebound.");
    }

    public static StateSliceBinding Authorize(EnforcementEvaluationInput input, string? requestedState = null)
    {
        var plan = input.StatePlan;
        if (plan is null)
        {
            if (requestedState is not null) throw new InvalidDataException("Surface authorization requires a plan.");
            return StateSliceBinding.Unbound;
        }
        if (!EnforcementEvaluator.ValidateStatePlan(plan, input.StatePlanError, out var reason))
            throw new InvalidDataException(reason);
        if (requestedState is not null)
        {
            var state = plan.States.SingleOrDefault(candidate => candidate.Id == requestedState) ??
                throw new InvalidDataException($"Unknown planned surface: {requestedState}.");
            return new StateSliceBinding(state.Id, SemanticSlicePlanner.SliceId(state));
        }
        var slices = EnforcementEvaluator.AssessSlices(input, EnforcementEvaluator.FreshReplay(input));
        var selected = SemanticSlicePlanner.Select(slices);
        var revalidate = selected is null && slices.All(slice => slice.Resolved);
        if (revalidate)
            selected = slices.FirstOrDefault(slice => slice.Resolved && !slice.Complete);
        var candidates = selected?.States.Where(state => revalidate ||
            EnforcementEvaluator.StateRequiresWork(input, state)).ToArray() ?? [];
        return selected is null ? StateSliceBinding.Unbound :
            new StateSliceBinding(candidates.Length == 1 ? candidates[0].Id : null, selected.Id);
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
        if (error is not null || progress is not { SchemaVersion: WorkflowProgress.CurrentSchemaVersion, Operations: not null } ||
            !WorkflowOperationAuthentication.VerifyAnchored(root, progress.Operations))
            throw new InvalidDataException(error ?? "Operation attribution requires a complete authenticated history.");
        return progress.Operations.SingleOrDefault(operation => operation.Id == operationId) ??
            throw new InvalidDataException("Unknown operation attribution ID.");
    }
}
