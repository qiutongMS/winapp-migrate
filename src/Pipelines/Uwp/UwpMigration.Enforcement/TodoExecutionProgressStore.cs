// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using UwpMigration.Contracts;
using WinAppMigration.Enforcement;

namespace UwpMigration.Enforcement;

internal static class TodoExecutionProgressStore
{
    internal static async Task<TodoExecutionContext> LoadAsync(
        DirectoryInfo target,
        CancellationToken cancellationToken)
    {
        var files = await UwpWorkspaceStore.LoadAsync(target, cancellationToken);
        if (files.StatePlanError is not null)
            throw new InvalidDataException(files.StatePlanError);
        var runtime = await UwpWorkspaceStore.LoadRuntimeEvidenceAsync(files.Target, cancellationToken);
        if (runtime.BuildError is not null || runtime.ReplayError is not null)
            throw new InvalidDataException(runtime.BuildError ?? runtime.ReplayError);
        return new TodoExecutionContext(
            files.Report, files.StatePlan, runtime.Build, runtime.Replay,
            await LoadProgressAsync(files.Target, cancellationToken));
    }

    internal static async Task<TodoExecutionProgressDocument> LoadProgressAsync(
        DirectoryInfo target, CancellationToken cancellationToken)
    {
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
        return progress;
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
                        item.Outcome is not ("passed" or "failed")))
            {
                throw new InvalidDataException(
                    $"todo-progress.json contains invalid event history for TODO '{progress.TodoId}'.");
            }
        }
    }

}

internal sealed record TodoExecutionContext(
    MigrationReport Report,
    StatePlanDocument? Plan,
    BuildAttestation? Build,
    ReplayAttestation? Replay,
    TodoExecutionProgressDocument Progress);
