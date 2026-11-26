// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinAppMigration.Enforcement;

namespace UwpMigration.Enforcement;

internal sealed record LoadedEnforcementWorkspace(
    UwpWorkspaceFiles Files,
    string TargetProject,
    AnalyzerReferenceStatus Analyzer,
    string Fingerprint,
    UwpRuntimeEvidence Runtime,
    AttemptLedger Attempts,
    DiagnosticRunAttestation? Diagnostic,
    EnforcementSnapshot? PersistedState,
    string? StateError,
    SemanticFindingLedger? Findings,
    string? FindingsError,
    WorkflowProgress? Progress,
    string? ArtifactError,
    DateTimeOffset GeneratedAt);

internal static class UwpEnforcementWorkspaceLoader
{
    internal static async Task<LoadedEnforcementWorkspace> LoadAsync(
        DirectoryInfo target, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        using var timing = EnforcementTrace.Measure("workspace.load");
        var files = await UwpWorkspaceStore.LoadAsync(target, cancellationToken);
        target = files.Target;
        var project = UwpWorkspaceStore.ResolveTargetProject(files);
        var analyzer = AnalyzerReferenceInspector.Inspect(project);
        string fingerprint;
        using (EnforcementTrace.Measure("workspace.fingerprint"))
            fingerprint = await TargetFingerprint.ComputeAsync(target, cancellationToken);
        var runtime = await UwpWorkspaceStore.LoadRuntimeEvidenceAsync(target, cancellationToken);
        var (attempts, attemptsError) = await EnforcementStore.TryLoadAsync<AttemptLedger>(
            target, EnforcementStore.AttemptLedgerPath, cancellationToken);
        attempts ??= new AttemptLedger();
        var (diagnostic, diagnosticError) = await EnforcementStore.TryLoadAsync<DiagnosticRunAttestation>(
            target, EnforcementStore.DiagnosticAttestationPath, cancellationToken);
        var (persisted, stateError) = await EnforcementStore.TryLoadAsync<EnforcementSnapshot>(
            target, EnforcementStore.StatePath, cancellationToken);
        if (stateError is null && persisted is not null &&
            persisted.SchemaVersion != EnforcementSnapshot.CurrentSchemaVersion)
            stateError = $"Unsupported enforcement snapshot schema {persisted.SchemaVersion}; only {EnforcementSnapshot.CurrentSchemaVersion} is supported.";
        stateError ??= WorkflowValidation.DispositionError(persisted?.Disposition);
        var (findings, findingsError) = await EnforcementStore.TryLoadAsync<SemanticFindingLedger>(
            target, EnforcementStore.FindingLedgerPath, cancellationToken);
        var artifactError = runtime.BuildError ?? runtime.ReplayError ?? attemptsError ??
            diagnosticError ?? stateError ?? WorkflowValidation.EvidenceArtifactError(
                attempts, runtime.Build, runtime.Replay) ?? WorkflowValidation.DiagnosticError(diagnostic);
        var now = timeProvider.GetUtcNow();
        WorkflowProgress? progress = null;
        string? workflowError = null;
        if (artifactError is null)
            (progress, workflowError) = await WorkflowPersistence.LoadAsync(
                target, runtime.Build is not null || runtime.Replay is not null || diagnostic is not null ||
                    attempts.Attempts.Count > 0 || persisted?.Workflow?.Operations.Count > 0,
                now, cancellationToken);
        if (workflowError is null && progress is not null &&
            !WorkflowOperationAuthentication.VerifyAnchored(target, progress.Operations))
            workflowError = "Operation history does not match the independently committed local head (rollback, omission, or legacy unordered history).";
        return new(files, project, analyzer, fingerprint, runtime, attempts, diagnostic, persisted,
            stateError, findings, findingsError, progress, artifactError ?? workflowError, now);
    }
}
