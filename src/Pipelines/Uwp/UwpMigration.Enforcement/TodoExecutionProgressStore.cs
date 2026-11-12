// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using UwpMigration.Contracts;
using UwpMigration.Mechanical;
using WinAppMigration.Enforcement;

namespace UwpMigration.Enforcement;

internal static class TodoExecutionProgressStore
{
    internal static async Task<TodoExecutionContext> LoadAsync(
        DirectoryInfo target,
        CancellationToken cancellationToken)
    {
        var reportPath = Path.Combine(
            target.FullName,
            "migration-report.json");
        if (!File.Exists(reportPath))
        {
            throw new FileNotFoundException(
                "migration-report.json was not found in the target directory.",
                reportPath);
        }
        var report = await MigrationReportStore.LoadAsync(
            reportPath,
            cancellationToken);
        var (plan, planError) =
            await EnforcementStore.TryLoadAsync<StatePlanDocument>(
                target,
                report.Validation.StatePlan,
                cancellationToken);
        if (planError is not null)
        {
            throw new InvalidDataException(planError);
        }
        var (build, buildError) =
            await EnforcementStore.TryLoadAsync<BuildAttestation>(
                target,
                EnforcementStore.BuildAttestationPath,
                cancellationToken);
        if (buildError is not null)
        {
            throw new InvalidDataException(buildError);
        }
        var (replay, replayError) =
            await EnforcementStore.TryLoadAsync<ReplayAttestation>(
                target,
                EnforcementStore.ReplayAttestationPath,
                cancellationToken);
        if (replayError is not null)
        {
            throw new InvalidDataException(replayError);
        }
        var (progress, progressError) =
            await EnforcementStore.TryLoadAsync<TodoExecutionProgressDocument>(
                target,
                EnforcementStore.TodoProgressPath,
                cancellationToken);
        if (progressError is not null)
        {
            throw new InvalidDataException(progressError);
        }
        progress ??= new TodoExecutionProgressDocument();
        Validate(progress);
        ValidateReferences(progress, report);
        return new TodoExecutionContext(
            report,
            plan,
            build,
            replay,
            progress);
    }

    internal static Task SaveAsync(
        DirectoryInfo target,
        TodoExecutionProgressDocument document,
        CancellationToken cancellationToken) =>
        EnforcementStore.WriteAtomicAsync(
            target,
            EnforcementStore.TodoProgressPath,
            document,
            cancellationToken);

    internal static void Validate(
        TodoExecutionProgressDocument document)
    {
        if (document.SchemaVersion !=
            TodoExecutionProgressDocument.CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"todo-progress.json schemaVersion '{document.SchemaVersion}' is not supported.");
        }
        if (document.Todos is null || document.Todos.Any(item => item is null ||
                item.Events is null || item.Events.Any(entry => entry is null || entry.Evidence is null)))
            throw new InvalidDataException("todo-progress.json requires non-null TODOs, events, and evidence collections.");
        var duplicate = document.Todos
            .GroupBy(item => item.TodoId, StringComparer.Ordinal)
            .FirstOrDefault(group =>
                string.IsNullOrWhiteSpace(group.Key) ||
                group.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidDataException(
                "todo-progress.json TODO IDs must be non-empty and unique.");
        }
        foreach (var progress in document.Todos)
        {
            if (progress.Attempt < 0 ||
                progress.Events.Select(item => item.Sequence)
                    .Any(sequence => sequence <= 0) ||
                progress.Events.Select(item => item.Sequence)
                    .Distinct()
                    .Count() != progress.Events.Count ||
                progress.Events.Any(item =>
                    item.Kind is not (
                        TodoExecutionService.ApproachSelected or
                        TodoExecutionService.Implementation or
                        TodoExecutionService.ApproachRejected) ||
                    string.IsNullOrWhiteSpace(item.Outcome) ||
                    string.IsNullOrWhiteSpace(item.Summary) ||
                    item.RecordedAt == default ||
                    item.Kind == TodoExecutionService.Implementation &&
                        item.Outcome is not ("passed" or "failed") ||
                    item.Kind == TodoExecutionService.ApproachRejected &&
                        string.IsNullOrWhiteSpace(item.ApproachKey)))
            {
                throw new InvalidDataException(
                    $"todo-progress.json contains invalid event history for TODO '{progress.TodoId}'.");
            }
        }
    }

    internal static void ValidateReferences(
        TodoExecutionProgressDocument document,
        MigrationReport report)
    {
        var todoIds = report.Todos.Select(todo => todo.Id)
            .ToHashSet(StringComparer.Ordinal);
        var unknown = document.Todos
            .Select(item => item.TodoId)
            .FirstOrDefault(id => !todoIds.Contains(id));
        if (unknown is not null)
        {
            throw new InvalidDataException(
                $"todo-progress.json references unknown TODO '{unknown}'.");
        }
    }
}

internal sealed record TodoExecutionContext(
    MigrationReport Report,
    StatePlanDocument? Plan,
    BuildAttestation? Build,
    ReplayAttestation? Replay,
    TodoExecutionProgressDocument Progress);
