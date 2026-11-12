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
        TodoExecutionProgressDocument progress,
        IReadOnlyList<TodoSurfaceAssessment> surfaces)
    {
        var required = assessments.Where(assessment =>
            assessment.Todo.Priority.Equals("required", StringComparison.OrdinalIgnoreCase)).ToArray();
        var pendingImplementation = required.Where(assessment =>
            assessment.ImplementationStatus == "pending").ToArray();
        var handoffs = required.SelectMany(assessment => assessment.HumanValidationItems).ToList();
        var phase = pendingImplementation.Length > 0 ? "implementation" :
            required.Any(assessment => assessment.VerificationStatus == "failed") ? "repair" :
            required.Any(assessment => assessment.VerificationStatus == "pending") ? "verification" :
            handoffs.Count > 0 ? "human-handoff" : "complete";
        var current = pendingImplementation.FirstOrDefault() ??
            required.FirstOrDefault(assessment => assessment.VerificationStatus != "verified");
        var states = surfaces.Select(surface => new MigrationStateProgress
        {
            StateId = surface.StateId, Status = surface.Status,
            TodoIds = TodoCompletionContract.TodosForState(report, surface.StateId)
                .Select(todo => todo.Id).ToList(),
            DependencyStateIds = plan is null ? [] :
                TodoCompletionContract.DependencyStateIds(plan, surface.StateId),
            FailureSignature = surface.FailureSignature
        }).ToList();
        var frontier = states.Where(state => state.Status is not ("verified" or "excluded"))
            .Select(state => state.StateId).ToList();
        return new TodoStatusSummary
        {
            Phase = phase,
            Todos = new MigrationTodoProgressSummary
            {
                Required = required.Length,
                PendingImplementation = pendingImplementation.Length,
                Implemented = required.Count(assessment => assessment.ImplementationStatus == "implemented"),
                PendingVerification = required.Count(assessment => assessment.VerificationStatus == "pending"),
                Failed = required.Count(assessment => assessment.VerificationStatus == "failed"),
                HumanValidationRequired = required.Count(assessment => assessment.VerificationStatus == "human-validation-required"),
                Verified = required.Count(assessment => assessment.VerificationStatus == "verified")
            },
            States = states, FrontierStateIds = frontier,
            TotalAttempts = progress.Todos.Sum(todo => todo.Attempt),
            CurrentAction = current?.NextCommand ??
                "Reuse current project/surface evidence and finalize; machine verification does not prove business behavior.",
            NextCheckpoint = frontier.FirstOrDefault() is { } surfaceId
                ? $"Record current product evidence for required surface '{surfaceId}'."
                : handoffs.Count > 0
                    ? "Hand off validation outside machine-verifiable project and UI surface scope."
                    : "Finalize the current project and surface results once.",
            LatestFailureSignature = progress.Todos.SelectMany(todo => todo.Events)
                .Where(item => !string.IsNullOrWhiteSpace(item.FailureSignature))
                .MaxBy(item => item.RecordedAt)?.FailureSignature,
            HumanValidationItems = handoffs
        };
    }
}
