// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;

namespace WinAppMigration.Enforcement;

public static class WorkflowPersistence
{
    public static async Task<EnforcementPolicy> ReadPolicyAsync(
        FileInfo file, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var stream = file.OpenRead();
            var policy = await JsonSerializer.DeserializeAsync<EnforcementPolicy>(
                stream, EnforcementStore.JsonOptions, cancellationToken);
            var error = WorkflowValidation.PolicyError(policy);
            if (error is not null) throw new InvalidDataException(error);
            return policy!;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"Malformed enforcement policy: {exception.Message}", exception);
        }
    }

    public static async Task ConfigureAsync(
        DirectoryInfo target, EnforcementPolicy policy, CancellationToken cancellationToken = default)
    {
        var error = WorkflowValidation.PolicyError(policy);
        if (error is not null) throw new InvalidDataException(error);
        var (progress, loadError) = await EnforcementStore.TryLoadAsync<WorkflowProgress>(
            target, EnforcementStore.ProgressPath, cancellationToken);
        if (loadError is not null) throw new InvalidDataException(loadError);
        if (progress is not null)
        {
            // Validate against the persisted policy before considering a replacement.
            var (existing, policyError) = await EnforcementStore.TryLoadAsync<EnforcementPolicy>(
                target, EnforcementStore.PolicyPath, cancellationToken);
            error = policyError ?? WorkflowValidation.PolicyError(existing);
            if (error is not null) throw new InvalidDataException(error);
            error = WorkflowValidation.ProgressError(progress, existing!, DateTimeOffset.UtcNow);
            if (error is not null) throw new InvalidDataException(error);
        }
        var legacyPaths = new[]
        {
            EnforcementStore.BuildAttestationPath, EnforcementStore.ReplayAttestationPath,
            EnforcementStore.DiagnosticAttestationPath
        };
        var (attempts, attemptsError) = await EnforcementStore.TryLoadAsync<AttemptLedger>(
            target, EnforcementStore.AttemptLedgerPath, cancellationToken);
        error = attemptsError ?? (attempts is null ? null : WorkflowValidation.EvidenceArtifactError(attempts, null, null));
        if (error is not null) throw new InvalidDataException(error);
        if (progress?.OperationsStartedAt is not null ||
            attempts?.Attempts.Count > 0 ||
            legacyPaths.Any(path => File.Exists(Path.Combine(target.FullName, path))))
            throw new InvalidDataException("Enforcement policy is immutable once workflow operations have started.");
        await EnforcementStore.WriteAtomicAsync(target, EnforcementStore.PolicyPath, policy, cancellationToken);
        if (progress is not null)
        {
            progress.PolicyFingerprint = WorkflowValidation.PolicyFingerprint(policy);
            await EnforcementStore.WriteAtomicAsync(target, EnforcementStore.ProgressPath, progress, cancellationToken);
        }
    }

    public static async Task<(EnforcementPolicy Policy, WorkflowProgress? Progress, string? Error)> LoadAsync(
        DirectoryInfo target, BuildAttestation? build, ReplayAttestation? replay, AttemptLedger attempts,
        string fingerprint, CancellationToken cancellationToken)
    {
        var (policy, policyError) = await EnforcementStore.TryLoadAsync<EnforcementPolicy>(
            target, EnforcementStore.PolicyPath, cancellationToken);
        var (progress, progressError) = await EnforcementStore.TryLoadAsync<WorkflowProgress>(
            target, EnforcementStore.ProgressPath, cancellationToken);
        if (policy is null && policyError is null && progress is not null)
            policyError = "Persisted workflow progress requires its persisted policy.";
        policy ??= EnforcementPolicy.Balanced();
        var error = policyError ?? progressError ?? WorkflowValidation.PolicyError(policy) ??
            WorkflowValidation.ProgressError(progress, policy, DateTimeOffset.UtcNow);
        if (error is not null) return (policy, progress, error);
        var (diagnostic, diagnosticError) = await EnforcementStore.TryLoadAsync<DiagnosticRunAttestation>(
            target, EnforcementStore.DiagnosticAttestationPath, cancellationToken);
        diagnosticError ??= WorkflowValidation.DiagnosticError(diagnostic);
        if (diagnosticError is not null)
            return (policy, progress, diagnosticError);
        if (progress is null)
            progress = ImportLegacy(policy, fingerprint, build, replay, diagnostic, attempts);
        await EnforcementStore.WriteAtomicAsync(target, EnforcementStore.PolicyPath, policy, cancellationToken);
        return (policy, progress, null);
    }

    private static WorkflowProgress? ImportLegacy(
        EnforcementPolicy policy, string fingerprint, BuildAttestation? build,
        ReplayAttestation? replay, DiagnosticRunAttestation? diagnostic, AttemptLedger attempts)
    {
        if (build is null && replay is null && diagnostic is null && attempts.Attempts.Count == 0)
            return null;
        var now = DateTimeOffset.UtcNow;
        var progress = new WorkflowProgress
        {
            PolicyFingerprint = WorkflowValidation.PolicyFingerprint(policy),
            LastTargetFingerprint = fingerprint,
            LastObservedAt = now,
            Stage = replay is { Success: true } ? WorkflowStage.SemanticConvergence : WorkflowStage.MechanicalStabilization
        };
        void Add(WorkflowOperation kind, string targetFingerprint, DateTimeOffset at, string? signature = null)
        {
            var stage = kind == WorkflowOperation.Build
                ? WorkflowStage.MechanicalStabilization : WorkflowStage.TargetBootstrap;
            var unit = progress.Units.FirstOrDefault(item => item.Stage == stage);
            if (unit is null)
            {
                unit = new WorkflowUnitProgress { Stage = stage };
                progress.Units.Add(unit);
            }
            at = at == default || at > now ? now : at;
            unit.StartedAt = unit.StartedAt is null || at < unit.StartedAt ? at : unit.StartedAt;
            progress.StartedAt = progress.StartedAt is null || at < progress.StartedAt ? at : progress.StartedAt;
            unit.Operations.Increment(kind);
            progress.Operations.Add(new WorkflowOperationRecord
            {
                Kind = kind, Stage = stage, TargetFingerprint = targetFingerprint, RecordedAt = at, Signature = signature
            });
        }
        foreach (var attempt in attempts.Attempts)
        {
            if (attempt.Kind == "build")
                Add(WorkflowOperation.Build, attempt.TargetFingerprint, attempt.RecordedAt, attempt.Signature);
            if (attempt.Diagnosis is not null)
                Add(WorkflowOperation.Hypothesis, attempt.TargetFingerprint, attempt.Diagnosis.RecordedAt, attempt.Signature);
        }
        if (build is not null && (build.Success || !attempts.Attempts.Any(attempt =>
            attempt.Kind == "build" && attempt.TargetFingerprint == build.TargetFingerprint && attempt.Signature == build.Signature)))
            Add(WorkflowOperation.Build, build.TargetFingerprint, build.StartedAt);
        if (replay is not null) Add(WorkflowOperation.Replay, replay.TargetFingerprint, replay.StartedAt);
        if (diagnostic is not null) Add(WorkflowOperation.DiagnosticRun, diagnostic.TargetFingerprint, diagnostic.StartedAt);
        // Observed failures are workflow work even when no controlled process has run yet.
        progress.StartedAt ??= attempts.Attempts.Min(attempt => (DateTimeOffset?)attempt.RecordedAt) ?? now;
        progress.OperationsStartedAt = progress.StartedAt;
        return progress;
    }
}
