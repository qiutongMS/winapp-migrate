// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinAppMigration.Enforcement;

public static class WorkflowPersistence
{
    public static async Task<(WorkflowProgress? Progress, string? Error)> LoadAsync(
        DirectoryInfo target, bool operationsStarted, DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var (progress, error) = await EnforcementStore.TryLoadAsync<WorkflowProgress>(
            target, EnforcementStore.ProgressPath, cancellationToken);
        error ??= WorkflowValidation.ProgressError(progress, now);
        if (error is not null) return (progress, error);
        if (progress is null &&
            (operationsStarted || WorkflowOperationAuthentication.ReadHead(target) is not null))
            return (null, "Workflow progress is missing for an already-started operation history. Preserve the artifacts; provenance cannot be reconstructed or reset.");
        if (File.Exists(Path.Combine(target.FullName, EnforcementStore.EnforcementDirectory, "policy.json")))
            return (progress, "Retired enforcement policy artifacts are unsupported by the current protocol. Preserve the workspace artifacts.");
        return (progress, null);
    }
}
