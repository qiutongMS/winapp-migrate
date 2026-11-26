// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinAppMigration.Enforcement;

namespace UwpMigration.Enforcement;

internal sealed record PreparedEnforcementWorkspace(
    LoadedEnforcementWorkspace Loaded,
    string? RequiredContractHash,
    string? RequiredContractError,
    string? FindingLedgerError,
    string? ArtifactError);

internal static class UwpEnforcementWorkspacePreparation
{
    internal static string FreezeContract(UwpWorkspaceFiles files) =>
        RequiredStateContracts.Freeze(files.Target, files.StatePlan ??
            throw new InvalidDataException(files.StatePlanError ?? "A surface plan is required."));

    // Called by command orchestration while its enforcement lock is held.
    internal static async Task<PreparedEnforcementWorkspace> PrepareAsync(
        LoadedEnforcementWorkspace loaded, CancellationToken cancellationToken)
    {
        using var timing = EnforcementTrace.Measure("workspace.prepare");
        var files = loaded.Files;
        string? requiredHash = null;
        string? requiredError = null;
        if (files.StatePlan is not null)
        {
            try { requiredHash = FreezeContract(files); }
            catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
            { requiredError = exception.Message; }
        }
        var (report, importError) = await LegacyFindingImporter.ImportAsync(
            files.ReportPath, files.Report, loaded.Findings, loaded.FindingsError, cancellationToken);
        var findingsError = loaded.FindingsError;
        if (findingsError is null && loaded.StateError is null &&
            loaded.PersistedState?.Findings is { Count: > 0 } legacySnapshot)
        {
            var snapshotFindings = new SemanticFindingLedger
            {
                Findings = legacySnapshot.Where(finding => finding is null ||
                    loaded.Findings?.Findings?.Any(existing => existing?.Id == finding.Id) != true).ToList()
            };
            var snapshotError = EnforcementStore.LegacyFindingError(snapshotFindings, null);
            if (snapshotError is null)
            {
                (report, snapshotError) = await LegacyFindingImporter.ImportAsync(
                    files.ReportPath, report, snapshotFindings, null, cancellationToken, EnforcementStore.StatePath);
                importError ??= snapshotError;
            }
            else
                findingsError = $"{EnforcementStore.StatePath}: {snapshotError}";
        }
        loaded = loaded with { Files = files with { Report = report } };
        var artifactError = loaded.ArtifactError;
        if (loaded.PersistedState?.Disposition is not { Terminal: true } && artifactError is null)
        {
            try
            {
                StateAttribution.ValidateFailures(files.Target, files.StatePlan, loaded.Runtime.Build,
                    loaded.Runtime.Replay, loaded.Diagnostic, loaded.Attempts);
                if (RuntimeFailureRecovery.Recover(loaded.Attempts, loaded.Runtime.Replay,
                    loaded.Diagnostic, files.Target, files.StatePlan))
                    await EnforcementStore.WriteAtomicAsync(
                        files.Target, EnforcementStore.AttemptLedgerPath, loaded.Attempts, cancellationToken);
            }
            catch (InvalidDataException exception) { artifactError = exception.Message; }
        }
        return new(loaded, requiredHash, requiredError,
            importError ?? EnforcementStore.LegacyFindingError(loaded.Findings, findingsError), artifactError);
    }
}
