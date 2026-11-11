// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using UwpMigration.Contracts;
using WinAppMigration.Enforcement;

namespace UwpMigration.Enforcement;

internal static class TodoStatusService
{
    internal static TodoStatusSummary Build(
        MigrationReport report,
        StatePlanDocument? plan,
        IReadOnlyList<TodoExecutionAssessment> assessments,
        TodoExecutionProgressDocument progress)
    {
        var required = assessments.Where(assessment =>
                assessment.Todo.Priority.Equals(
                    "required",
                    StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var states = BuildStateProgress(report, plan, required);
        var pendingImplementation = required.Where(assessment =>
                assessment.ImplementationStatus == "pending")
            .ToArray();
        var pendingDevelopment = required.Where(assessment =>
                assessment.ImplementationStatus == "implemented" &&
                !assessment.ReadyForCanonical)
            .ToArray();
        var humanValidationItems = required
            .SelectMany(assessment => assessment.HumanValidationItems)
            .ToList();
        var allTerminal = required.All(assessment =>
            assessment.ImplementationStatus == "verified");
        var readyForCanonical = required.All(assessment =>
            assessment.ImplementationStatus == "verified" ||
            assessment.ReadyForCanonical);
        var canonicalStarted = plan?.States.Any(state =>
            state.Target.Status == "passed" ||
            state.Comparison.Status == "verified") == true;
        var phase = allTerminal
            ? "complete"
            : pendingImplementation.Length > 0
                ? "implementation"
                : pendingDevelopment.Length > 0
                    ? "development-validation"
                    : canonicalStarted
                        ? "canonical-verification"
                        : "ready-for-canonical";
        var frontier = states.Where(state =>
                state.Status is not ("passed" or "unowned"))
            .Select(state => state.StateId)
            .ToList();
        var currentAssessment = pendingImplementation
            .Concat(pendingDevelopment)
            .FirstOrDefault();
        var currentAction = currentAssessment?.NextCommand ??
            (readyForCanonical
                ? "Run the single ordered canonical state pass."
                : "No executable migration action is available.");
        var nextCheckpoint = frontier.FirstOrDefault() is { } frontierState
            ? humanValidationItems.FirstOrDefault(item =>
                item.StateId.Equals(
                    frontierState,
                    StringComparison.Ordinal)) is { } humanItem
                ? $"Complete human validation item '{humanItem.CheckId}' for state '{frontierState}'."
                : $"Record a fresh development outcome for state '{frontierState}'."
            : readyForCanonical
                ? "Complete canonical record-state for every required state, then finalize once."
                : "Complete the current TODO operation.";
        var latestFailureSignature = progress.Todos
            .SelectMany(item => item.Events)
            .Where(item =>
                !string.IsNullOrWhiteSpace(item.FailureSignature))
            .OrderBy(item => item.RecordedAt)
            .Select(item => item.FailureSignature)
            .LastOrDefault();
        return new TodoStatusSummary
        {
            Phase = phase,
            Todos = new MigrationTodoProgressSummary
            {
                Required = required.Length,
                PendingImplementation = pendingImplementation.Length,
                Implemented = required.Count(assessment =>
                    assessment.ImplementationStatus == "implemented"),
                DevelopmentValidated = required.Count(assessment =>
                    assessment.DevelopmentStatus ==
                    "development-validated"),
                Blocked = required.Count(assessment =>
                    assessment.DevelopmentStatus == "blocked"),
                Stale = required.Count(assessment =>
                    assessment.DevelopmentStatus == "stale"),
                HumanValidationRequired = required.Count(assessment =>
                    assessment.DevelopmentStatus ==
                    "human-validation-required"),
                Verified = required.Count(assessment =>
                    assessment.ImplementationStatus == "verified")
            },
            States = states,
            FrontierStateIds = frontier,
            TotalAttempts = progress.Todos.Sum(item => item.Attempt),
            ReadyForCanonical = readyForCanonical,
            CurrentAction = currentAction,
            NextCheckpoint = nextCheckpoint,
            LatestFailureSignature = latestFailureSignature,
            HumanValidationItems = humanValidationItems
        };
    }

    private static List<MigrationStateProgress> BuildStateProgress(
        MigrationReport report,
        StatePlanDocument? plan,
        IReadOnlyList<TodoExecutionAssessment> assessments)
    {
        if (plan is null)
        {
            return [];
        }

        var byTodoId = assessments.ToDictionary(
            assessment => assessment.Todo.Id,
            StringComparer.Ordinal);
        var result = new List<MigrationStateProgress>();
        foreach (var state in plan.States)
        {
            var owners = TodoCompletionContract.TodosForState(
                    report,
                    state.Id)
                .Where(todo => byTodoId.ContainsKey(todo.Id))
                .ToArray();
            var ownerStates = owners
                .Select(todo => byTodoId[todo.Id].DevelopmentStates
                    .SingleOrDefault(item =>
                        item.StateId.Equals(
                            state.Id,
                            StringComparison.Ordinal)))
                .Where(item => item is not null)
                .Cast<TodoDevelopmentStateAssessment>()
                .ToArray();
            var status =
                state.Target.Status == "passed" &&
                state.Comparison.Status == "verified"
                    ? "passed"
                    : owners.Length == 0
                        ? "unowned"
                        : ownerStates.Any(item =>
                            item.Status == "blocked")
                            ? "blocked"
                            : ownerStates.Any(item =>
                                item.Status == "stale")
                                ? "stale"
                                : ownerStates.Any(item =>
                                    item.Status == "unmapped")
                                    ? "unmapped"
                                    : ownerStates.Any(item =>
                                        item.Status ==
                                        "human-validation-required")
                                        ? "human-validation-required"
                                    : ownerStates.Length > 0 &&
                                      ownerStates.All(item =>
                                          item.Status == "passed")
                                        ? "passed"
                                        : "not-run";
            result.Add(new MigrationStateProgress
            {
                StateId = state.Id,
                Status = status,
                TodoIds = owners.Select(todo => todo.Id)
                    .Order(StringComparer.Ordinal)
                    .ToList(),
                DependencyStateIds =
                    TodoCompletionContract.DependencyStateIds(
                        plan,
                        state.Id),
                FailureSignature = ownerStates
                    .Where(item =>
                        !string.IsNullOrWhiteSpace(
                            item.FailureSignature))
                    .Select(item => item.FailureSignature)
                    .LastOrDefault()
            });
        }
        return result;
    }
}
