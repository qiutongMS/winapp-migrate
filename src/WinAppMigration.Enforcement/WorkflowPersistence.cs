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
        var (state, stateError) = await EnforcementStore.TryLoadAsync<EnforcementSnapshot>(
            target, EnforcementStore.StatePath, cancellationToken);
        if (stateError is not null) throw new InvalidDataException(stateError);
        if (state?.Disposition is { Terminal: true })
            throw new InvalidDataException("Workflow is terminal; policy configuration is not permitted.");
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
            (progress, error) = ImportProgress(progress, existing!, DateTimeOffset.UtcNow);
            if (error is not null) throw new InvalidDataException(error);
            if (progress?.Disposition is { Terminal: true })
                throw new InvalidDataException("Workflow is terminal; policy configuration is not permitted.");
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
        string fingerprint, CancellationToken cancellationToken, DateTimeOffset? generatedAt = null)
    {
        var now = generatedAt ?? DateTimeOffset.UtcNow;
        var (policy, policyError) = await EnforcementStore.TryLoadAsync<EnforcementPolicy>(
            target, EnforcementStore.PolicyPath, cancellationToken);
        var (progress, progressError) = await EnforcementStore.TryLoadAsync<WorkflowProgress>(
            target, EnforcementStore.ProgressPath, cancellationToken);
        if (policy is null && policyError is null && progress is not null)
            policyError = "Persisted workflow progress requires its persisted policy.";
        policy ??= EnforcementPolicy.Balanced();
        var error = policyError ?? progressError ?? WorkflowValidation.PolicyError(policy) ??
            ImportError();
        if (error is not null) return (policy, progress, error);
        string? ImportError()
        {
            (progress, var importError) = ImportProgress(progress, policy, now);
            return importError;
        }
        var (diagnostic, diagnosticError) = await EnforcementStore.TryLoadAsync<DiagnosticRunAttestation>(
            target, EnforcementStore.DiagnosticAttestationPath, cancellationToken);
        diagnosticError ??= WorkflowValidation.DiagnosticError(diagnostic);
        if (diagnosticError is not null)
            return (policy, progress, diagnosticError);
        if (progress is null)
        {
            var legacy = ImportLegacy(policy, fingerprint, build, replay, diagnostic, attempts, now);
            (progress, error) = ImportProgress(legacy, policy, now);
            if (error is not null) return (policy, progress, error);
        }
        await EnforcementStore.WriteAtomicAsync(target, EnforcementStore.PolicyPath, policy, cancellationToken);
        return (policy, progress, null);
    }

    public static (WorkflowProgress? Progress, string? Error) ImportProgress(
        WorkflowProgress? progress, EnforcementPolicy policy, DateTimeOffset now)
    {
        if (progress is null) return (null, null);
        if (progress.SchemaVersion == WorkflowProgress.CurrentSchemaVersion)
        {
            WorkflowDispositions.ResumeRetiredAuthorization(progress);
            return (progress, WorkflowValidation.ProgressError(progress, policy, now));
        }
        if (progress.SchemaVersion == "2.0")
        {
            var legacyError = WorkflowValidation.Schema20ImportError(progress, policy, now);
            if (legacyError is not null)
                return (progress, $"Cannot safely import workflow progress schema 2.0: {legacyError}");
            var imported20 = ImportToCurrent(progress);
            var currentError = WorkflowValidation.ProgressError(imported20, policy, now);
            return currentError is null
                ? (imported20, null)
                : (progress, $"Cannot safely import workflow progress schema 2.0: {currentError}");
        }
        if (progress.SchemaVersion != "1.0")
            return (progress, $"Unsupported workflow progress schema {progress.SchemaVersion}; supported schemas are 1.0 and 2.0 (explicit import) and {WorkflowProgress.CurrentSchemaVersion}.");
        if (progress.Units is null || progress.Units.Count != 1 ||
            progress.Units.Any(unit => unit is null || unit.Epochs is null || unit.Epochs.Count > 0 ||
                unit.Stage != progress.Stage || unit.SliceId !=
                    (progress.Stage == WorkflowStage.SemanticConvergence ? progress.ActiveSliceId : null)) ||
            progress.Slices is null || progress.Slices.Count > 0 || progress.Disposition is not null)
            return (progress, "Cannot safely import workflow progress schema 1.0: exactly one reconstructable budget unit is required; missing/multiple/switched units or lifecycle fields lack reconstructable activation epochs. Preserve the original artifacts; do not reset budgets.");
        var imported = progress with
        {
            SchemaVersion = WorkflowProgress.CurrentSchemaVersion,
            Units = progress.Units.Select(unit => unit with
            {
                ExtensionGrantedAt = null,
                BlockedAt = null,
                BlockedReason = null,
                Operations = unit.Operations is null ? null! : unit.Operations with { },
                Epochs = [new WorkflowActivationEpoch
                {
                    EnteredAt = unit.StartedAt ?? default,
                    TargetFingerprint = progress.LastTargetFingerprint
                }]
            }).ToList(),
            Slices = (progress.CompletedSliceIds ?? []).Select(id => new WorkflowSliceProgress
            {
                SliceId = id, Resolved = true, EvidenceFresh = false
            }).ToList(),
            Disposition = new RunDisposition
            {
                Kind = "continue", ReasonCode = "schema-1-imported", Terminal = false,
                Retryable = true, Finalization = "not-ready"
            }
        };
        var error = WorkflowValidation.ProgressError(imported, policy, now);
        return error is null ? (imported, null) : (progress, $"Cannot safely import workflow progress schema 1.0: {error}");
    }

    private static WorkflowProgress ImportToCurrent(WorkflowProgress progress)
    {
        var imported = progress with
        {
            SchemaVersion = WorkflowProgress.CurrentSchemaVersion,
            UnresolvedRequiredStates = [.. progress.UnresolvedRequiredStates],
            CompletedSliceIds = [.. progress.CompletedSliceIds],
            Units = progress.Units.Select(unit => unit with
            {
                ExtensionGrantedAt = null,
                BlockedAt = null,
                BlockedReason = null,
                Operations = unit.Operations with { },
                Epochs = unit.Epochs.Select(epoch => epoch with { }).ToList()
            }).ToList(),
            Slices = progress.Slices.Select(slice => slice with { }).ToList(),
            Events = [.. progress.Events],
            Operations = [.. progress.Operations],
            Debt = progress.Debt.Where(debt => !IsRetiredAuthorizationDebt(debt))
                .Select(debt => debt with { }).ToList()
        };
        WorkflowDispositions.ResumeRetiredAuthorization(imported);
        if (imported.Disposition is { Kind: "continue-with-debt", ReasonCode: "optional-debt" } &&
            imported.Debt.Count == 0)
        {
            imported.Disposition = new RunDisposition
            {
                Kind = "continue",
                ReasonCode = "retired-authorization-resumed",
                Terminal = false,
                Retryable = true,
                Finalization = "not-ready"
            };
        }
        return imported;
    }

    private static bool IsRetiredAuthorizationDebt(WorkflowDebt debt) =>
        debt.Reason.StartsWith(
            "Stage hard limit expired.",
            StringComparison.Ordinal) ||
        debt.Reason.StartsWith(
            "Stage operation ceiling reached",
            StringComparison.Ordinal);

    private static WorkflowProgress? ImportLegacy(
        EnforcementPolicy policy, string fingerprint, BuildAttestation? build,
        ReplayAttestation? replay, DiagnosticRunAttestation? diagnostic, AttemptLedger attempts, DateTimeOffset now)
    {
        if (build is null && replay is null && diagnostic is null && attempts.Attempts.Count == 0)
            return null;
        var progress = new WorkflowProgress
        {
            SchemaVersion = "1.0",
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
