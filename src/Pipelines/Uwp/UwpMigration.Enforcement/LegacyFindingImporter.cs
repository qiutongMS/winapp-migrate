// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Security.Cryptography;
using System.Text;
using UwpMigration.Contracts;
using UwpMigration.Mechanical;
using WinAppMigration.Enforcement;

namespace UwpMigration.Enforcement;

internal static class LegacyFindingImporter
{
    internal static async Task<(MigrationReport Report, string? Error)> ImportAsync(
        string reportPath, MigrationReport report, SemanticFindingLedger? ledger, string? loadError,
        CancellationToken cancellationToken, string legacyPath = EnforcementStore.FindingLedgerPath)
    {
        var root = new DirectoryInfo(Path.GetDirectoryName(reportPath)!);
        if (!EnforcementStore.TryResolveContainedPath(root, $"{legacyPath}.imported", out var archive, out var pathError))
            return (report, $"Legacy finding import archive could not be inspected: {pathError}");
        if (File.Exists(archive) || EnforcementStore.LegacyFindingError(ledger, loadError) is not null || ledger is null)
            return (report, null);
        var pending = ledger.Findings.Where(finding => finding.Status != "resolved")
            .Select(finding => (Finding: finding, Marker:
                $"legacy-finding-{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(finding.Id)))}")).ToList();
        if (pending.All(item => report.Todos.Any(todo => todo.Guidance?.Facts.Any(fact => fact.Id == item.Marker) == true)))
            return (report, ArchiveLegacyFindings(root, legacyPath, archive));

        await using var reportLock = await MigrationReportStore.AcquireTransactionLockAsync(reportPath, cancellationToken);
        report = await MigrationReportStore.LoadAsync(reportPath, cancellationToken);
        var changed = false;
        foreach (var (finding, marker) in pending)
        {
            var signature = $"legacy-semantic-finding:{finding.Id}";
            if (report.Todos.Any(todo => todo.Guidance?.Facts.Any(fact => fact.Id == marker) == true))
                continue;

            var linked = report.Todos.Where(todo => finding.TodoIds.Contains(todo.Id, StringComparer.Ordinal)).ToList();
            if (linked.Count == 0)
            {
                var id = marker;
                for (var suffix = 1; report.Todos.Any(todo => todo.Id == id); suffix++)
                    id = $"{marker}-{suffix}";
                var todo = new MigrationTodo
                {
                    Id = id,
                    Category = string.IsNullOrWhiteSpace(finding.Category) ? "behavior" : finding.Category,
                    Priority = "required",
                    Origin = "discovered",
                    Summary = finding.SourceInvariant,
                    Reason = finding.CompletionCondition,
                    Discovery = new MigrationTodoDiscovery
                    {
                        Phase = "behavior",
                        Signature = signature,
                        Evidence = [legacyPath, .. finding.Evidence, .. finding.ValidationEvidence]
                    },
                    Guidance = new MigrationRepairGuidance { ReasonCode = "legacy-semantic-finding" }
                };
                report.Todos.Add(todo);
                linked.Add(todo);
            }

            foreach (var todo in linked)
            {
                var guidance = todo.Guidance ??= new MigrationRepairGuidance { ReasonCode = "legacy-semantic-finding" };
                guidance.Facts.Add(new MigrationGuidanceFact
                {
                    Id = marker,
                    Summary = $"{finding.SourceInvariant} Completion: {finding.CompletionCondition} " +
                        "Historical evidence is retained for context, not current acceptance.",
                    Authority = "legacy-input"
                });
                guidance.CompletionConditions = guidance.CompletionConditions.Concat([finding.CompletionCondition])
                    .Distinct(StringComparer.Ordinal).ToList();
                todo.AffectedStates = todo.AffectedStates.Concat(finding.StateIds)
                    .Distinct(StringComparer.Ordinal).ToList();
                todo.AffectedFeatures = todo.AffectedFeatures.Concat(finding.FeaturePaths)
                    .Distinct(StringComparer.Ordinal).ToList();

                foreach (var mapping in finding.CompletionAssertions.DistinctBy(check => (check.StateId, check.AssertionId)))
                {
                    var machine = SurfaceCheckIds.All.Contains(mapping.AssertionId, StringComparer.Ordinal);
                    if (machine && guidance.CompletionChecks.Any(check => check.VerificationKind == "surface-check" &&
                        check.Target == mapping.StateId && check.AssertionIds.Contains(mapping.AssertionId, StringComparer.Ordinal)))
                        continue;
                    guidance.CompletionChecks.Add(new MigrationCompletionCheck
                    {
                        Id = $"{marker}-check-{guidance.CompletionChecks.Count + 1}",
                        Summary = $"{finding.SourceInvariant} ({mapping.StateId}/{mapping.AssertionId})",
                        VerificationKind = machine ? "surface-check" : "todo-resolution-evidence",
                        Target = mapping.StateId,
                        Expected = machine ? "passed" : finding.CompletionCondition,
                        AssertionIds = [mapping.AssertionId]
                    });
                }
                if (finding.CompletionAssertions.Count == 0 || finding.StateIds.Any(id =>
                    finding.CompletionAssertions.All(check => check.StateId != id)))
                    guidance.CompletionChecks.Add(new MigrationCompletionCheck
                    {
                        Id = $"{marker}-scope-review",
                        Summary = finding.SourceInvariant,
                        VerificationKind = "todo-resolution-evidence",
                        Target = todo.Id,
                        Expected = finding.CompletionCondition
                    });
            }
            changed = true;
        }
        if (changed)
            await MigrationReportStore.WriteAtomicAsync(reportPath, report, cancellationToken);
        return (report, ArchiveLegacyFindings(root, legacyPath, archive));
    }

    private static string? ArchiveLegacyFindings(DirectoryInfo root, string legacyPath, string archive)
    {
        if (!EnforcementStore.TryResolveContainedPath(root, legacyPath, out var source, out var error))
            return $"Legacy finding import archive could not be created: {error}";
        var staged = $"{archive}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.Copy(source, staged);
            File.Move(staged, archive);
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return $"Legacy obligations were transferred, but their one-time archive could not be created: {exception.Message} Preserve {legacyPath} and retry the import before removing transferred TODOs.";
        }
        finally
        {
            if (File.Exists(staged))
                File.Delete(staged);
        }
    }
}
