// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinAppMigration.Enforcement;

public static class WorkflowValidation
{
    public static string? ProgressError(
        WorkflowProgress? progress, DateTimeOffset now)
    {
        if (progress is null) return null;
        if (progress.SchemaVersion != WorkflowProgress.CurrentSchemaVersion)
            return $"Unsupported workflow progress schema {progress.SchemaVersion}; only {WorkflowProgress.CurrentSchemaVersion} is supported. Preserve the original artifacts.";
        if (progress.Operations is null ||
            progress.Operations.Any(item => item is null ||
                !Enum.IsDefined(item.Kind) || string.IsNullOrWhiteSpace(item.TargetFingerprint) ||
                item.RecordedAt == default || item.RecordedAt > now))
            return "Workflow progress contains invalid operation records.";
        if (progress.Disposition is null)
            return $"Workflow progress schema {progress.SchemaVersion} requires a run disposition.";
        var dispositionError = DispositionError(progress.Disposition);
        if (dispositionError is not null)
            return dispositionError;
        return null;
    }

    public static string? DispositionError(RunDisposition? disposition)
    {
        if (disposition is null) return null;
        var expected = disposition.Kind switch
        {
            "continue" => (false, true, "not-ready"),
            "tool-blocked" => (false, false, "not-ready"),
            "input-review-required" => (false, false, "not-ready"),
            "complete" => (true, false, "complete"),
            "infrastructure-failure" => (true, true, "failed"),
            "stopped" => (true, false, "incomplete"),
            _ => ((bool, bool, string)?)null
        };
        var reasonValid = (disposition.Kind, disposition.ReasonCode) switch
        {
            ("continue", "workflow-active" or "closure-evidence-reopened") => true,
            ("tool-blocked", "comparison-support-required") => true,
            ("input-review-required", "comparable-inputs-required") => true,
            ("complete", "closure-passed") => true,
            ("infrastructure-failure", "invalid-enforcement-artifacts" or "process-invocation-failed") => true,
            ("stopped", "budget-exhausted" or "no-progress" or "external-blocker") => true,
            _ => false
        };
        return expected is null || !reasonValid ||
            disposition.Terminal != expected.Value.Item1 || disposition.Retryable != expected.Value.Item2 ||
            disposition.Finalization != expected.Value.Item3 || disposition.AllowedOperations is null ||
            disposition.AllowedOperations.Any(operation => !Enum.IsDefined(operation)) ||
            disposition.Terminal &&
                disposition.AllowedOperations.Count > 0 ||
            disposition.AllowedOperations.Distinct().Count() != disposition.AllowedOperations.Count
            ? "Workflow run disposition has invalid kind, reason code, terminal, retryable, operations, or finalization semantics."
            : null;
    }

    public static string? EvidenceArtifactError(
        AttemptLedger attempts, BuildAttestation? build, ReplayAttestation? replay)
    {
        if (attempts.SchemaVersion != AttemptLedger.CurrentSchemaVersion ||
            attempts.Attempts is null || attempts.Attempts.Any(attempt => attempt is null ||
                attempt.Kind is not ("build" or "runtime") ||
                string.IsNullOrWhiteSpace(attempt.TargetFingerprint) ||
                string.IsNullOrWhiteSpace(attempt.Signature) ||
                attempt.RecordedAt == default || attempt.Evidence is null))
            return "Attempt artifacts must use schema 1.0 with valid failure tuples and evidence.";
        if (build is not null && (build.SchemaVersion != BuildAttestation.CurrentSchemaVersion ||
                build.ArtifactClass != MigrationArtifactClass.CanonicalAttestation ||
                build.Producer != MigrationArtifactClass.Producer ||
                string.IsNullOrWhiteSpace(build.Id) || string.IsNullOrWhiteSpace(build.TargetFingerprint) ||
                build.Command is null || build.Command.Count == 0 ||
                string.IsNullOrWhiteSpace(build.StandardOutputPath) || string.IsNullOrWhiteSpace(build.StandardErrorPath) ||
                build.StartedAt == default || build.CompletedAt < build.StartedAt) ||
            replay is not null && (replay.SchemaVersion != ReplayAttestation.CurrentSchemaVersion ||
                replay.ArtifactClass != MigrationArtifactClass.CanonicalAttestation ||
                replay.Producer != MigrationArtifactClass.Producer ||
                string.IsNullOrWhiteSpace(replay.Id) || string.IsNullOrWhiteSpace(replay.RunId) ||
                string.IsNullOrWhiteSpace(replay.TargetFingerprint) || string.IsNullOrWhiteSpace(replay.BuildAttestationId) ||
                replay.Command is null || replay.Command.Count == 0 ||
                string.IsNullOrWhiteSpace(replay.StandardOutputPath) || string.IsNullOrWhiteSpace(replay.StandardErrorPath) ||
                replay.StartedAt == default || replay.CompletedAt < replay.StartedAt))
            return "Build and replay artifacts must be canonical winapp-migrate attestations using schema 1.0 with valid identities, commands, paths and timestamps.";
        return null;
    }

    public static string? DiagnosticError(DiagnosticRunAttestation? diagnostic) =>
        diagnostic is not null && (diagnostic.SchemaVersion != DiagnosticRunAttestation.CurrentSchemaVersion ||
            diagnostic.ArtifactClass != MigrationArtifactClass.DevelopmentProbe ||
            diagnostic.Producer != MigrationArtifactClass.Producer ||
            string.IsNullOrWhiteSpace(diagnostic.Id) || string.IsNullOrWhiteSpace(diagnostic.TargetFingerprint) ||
            diagnostic.Command is null || diagnostic.Command.Count == 0 ||
            string.IsNullOrWhiteSpace(diagnostic.StandardOutputPath) || string.IsNullOrWhiteSpace(diagnostic.StandardErrorPath) ||
            diagnostic.StartedAt == default || diagnostic.CompletedAt < diagnostic.StartedAt)
            ? "Diagnostic artifacts must be winapp-migrate development probes using schema 1.0 with valid identities, commands, paths and timestamps." : null;
}
