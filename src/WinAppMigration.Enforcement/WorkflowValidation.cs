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
        if (!Enum.IsDefined(progress.Stage) ||
            string.IsNullOrWhiteSpace(progress.LastTargetFingerprint) ||
            progress.LastObservedAt == default || progress.LastObservedAt > now ||
            progress.StartedAt is null || progress.StartedAt == default(DateTimeOffset) || progress.StartedAt > now ||
            progress.OperationsStartedAt > progress.LastObservedAt || progress.OperationsStartedAt < progress.StartedAt ||
            progress.ActiveSliceId is not null && string.IsNullOrWhiteSpace(progress.ActiveSliceId) ||
            progress.Units is null || progress.Events is null || progress.Operations is null ||
            progress.CompletedSliceIds is null ||
            progress.UnresolvedRequiredStates is null || progress.Slices is null ||
            progress.LastObservedAt < progress.StartedAt || now < progress.LastObservedAt)
            return "Workflow progress has an unsupported schema or invalid fields/timestamps.";
        if (progress.Units.Any(unit => unit is null || !Enum.IsDefined(unit.Stage) ||
                (unit.Stage != WorkflowStage.SemanticConvergence && unit.SliceId is not null) ||
                !CountsValid(unit.Operations, int.MaxValue) ||
                unit.StartedAt is null || unit.StartedAt < progress.StartedAt || unit.StartedAt > now ||
                unit.Epochs is null || unit.Epochs.Count == 0 ||
                unit.Epochs.Any(epoch => epoch is null ||
                    string.IsNullOrWhiteSpace(epoch.TargetFingerprint) ||
                    epoch.EnteredAt < progress.StartedAt || epoch.EnteredAt > progress.LastObservedAt ||
                    epoch.ExitedAt < epoch.EnteredAt || epoch.ExitedAt > progress.LastObservedAt ||
                    (epoch.ExitedAt is not null) != !string.IsNullOrWhiteSpace(epoch.ExitReason)) ||
                unit.Epochs[0].EnteredAt != unit.StartedAt ||
                unit.Epochs.Zip(unit.Epochs.Skip(1)).Any(pair =>
                    pair.First.ExitedAt is null || pair.First.ExitedAt > pair.Second.EnteredAt)) ||
            progress.Units.GroupBy(unit => (unit.Stage, unit.SliceId)).Any(group => group.Count() > 1))
            return "Workflow progress contains invalid or duplicate workflow units.";
        var epochs = progress.Units.SelectMany(unit => unit.Epochs).OrderBy(epoch => epoch.EnteredAt)
            .ThenBy(epoch => epoch.ExitedAt ?? DateTimeOffset.MaxValue).ToArray();
        if (epochs.Count(epoch => epoch.ExitedAt is null) > 1 ||
            epochs.Zip(epochs.Skip(1)).Any(pair =>
                pair.First.ExitedAt is null || pair.First.ExitedAt > pair.Second.EnteredAt) ||
            progress.Units.Any(unit => unit.Epochs.Any(epoch => epoch.ExitedAt is null) &&
                (unit.Stage != progress.Stage ||
                 unit.Stage == WorkflowStage.SemanticConvergence && unit.SliceId != progress.ActiveSliceId)))
            return "Workflow activation epochs overlap or do not match the active workflow unit.";
        if (progress.Slices.Any(slice => slice is null || string.IsNullOrWhiteSpace(slice.SliceId) ||
                slice.EvidenceFresh && (!slice.Resolved ||
                    slice.EvidenceTargetFingerprint != progress.LastTargetFingerprint ||
                    string.IsNullOrWhiteSpace(slice.EvidenceGeneration))) ||
            progress.Slices.GroupBy(slice => slice.SliceId).Any(group => group.Count() > 1) ||
            !progress.CompletedSliceIds.ToHashSet(StringComparer.Ordinal).SetEquals(
                progress.Slices.Where(slice => slice.Resolved).Select(slice => slice.SliceId)))
            return "Workflow slice resolution or evidence generation is invalid.";
        if (progress.Disposition is null)
            return $"Workflow progress schema {progress.SchemaVersion} requires a run disposition.";
        var dispositionError = DispositionError(progress.Disposition);
        if (dispositionError is not null)
            return dispositionError;
        if (progress.Disposition is { Terminal: true } && epochs.Any(epoch => epoch.ExitedAt is null))
            return "Terminal workflow progress must not contain an active epoch.";
        if (progress.Events.Any(item => item is null || !Enum.IsDefined(item.Stage) ||
                string.IsNullOrWhiteSpace(item.Key) || item.RecordedAt == default || item.RecordedAt > progress.LastObservedAt ||
                item.Kind is not ("target-changed" or "required-states-reduced" or
                    "diagnosis-completed" or "slice-completed" or "discriminating-evidence" or
                    "source-oracle-completed" or "mechanical-stabilized" or "bootstrap-completed")) ||
            progress.Operations.Any(item => item is null || !Enum.IsDefined(item.Stage) ||
                !Enum.IsDefined(item.Kind) || string.IsNullOrWhiteSpace(item.TargetFingerprint) ||
                item.RecordedAt == default || item.RecordedAt > progress.LastObservedAt))
            return "Workflow progress contains invalid progress events or operations.";
        foreach (var unit in progress.Units)
        {
            foreach (var operation in Enum.GetValues<WorkflowOperation>())
            {
                if (unit.Operations.For(operation) != progress.Operations.Count(item =>
                    item.Stage == unit.Stage && item.SliceId == unit.SliceId && item.Kind == operation))
                    return "Workflow operation records and per-unit counts disagree.";
            }
        }
        if (progress.Operations.Any(operation => !progress.Units.Any(unit =>
                unit.Stage == operation.Stage && unit.SliceId == operation.SliceId &&
                unit.Epochs.Any(epoch => epoch.EnteredAt <= operation.RecordedAt &&
                    (epoch.ExitedAt is null || operation.RecordedAt <= epoch.ExitedAt)))))
            return "Workflow operation records must fall within their workflow unit's activation epochs.";
        if (progress.Operations.Count > 0 &&
            (progress.StartedAt is null || progress.OperationsStartedAt is null) ||
            progress.Operations.Any(operation => !progress.Units.Any(unit =>
                unit.Stage == operation.Stage && unit.SliceId == operation.SliceId)))
            return "Workflow operations require a start time and matching workflow units.";
        return null;
    }

    public static string? DispositionError(RunDisposition? disposition)
    {
        if (disposition is null) return null;
        var expected = disposition.Kind switch
        {
            "continue" => (false, true, "not-ready"),
            "complete" => (true, false, "complete"),
            "infrastructure-failure" => (true, true, "failed"),
            _ => ((bool, bool, string)?)null
        };
        var reasonValid = (disposition.Kind, disposition.ReasonCode) switch
        {
            ("continue", "workflow-active" or "closure-evidence-reopened") => true,
            ("complete", "closure-passed") => true,
            ("infrastructure-failure", "invalid-enforcement-artifacts" or "process-invocation-failed") => true,
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
                attempt.RecordedAt == default || attempt.Evidence is null ||
                attempt.Diagnosis is not null && (attempt.Diagnosis.SourceLocations is null ||
                    attempt.Diagnosis.TargetLocations is null || attempt.Diagnosis.Evidence is null)))
            return "Attempt artifacts must use schema 1.0 with valid failure tuples and diagnosis collections.";
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

    public static string? UiProbeError(UiDevelopmentProbe? probe) =>
        probe is not null && (probe.SchemaVersion != UiDevelopmentProbe.CurrentSchemaVersion ||
            probe.ArtifactClass != MigrationArtifactClass.DevelopmentProbe ||
            probe.Producer != MigrationArtifactClass.Producer ||
            string.IsNullOrWhiteSpace(probe.Id) || string.IsNullOrWhiteSpace(probe.TargetFingerprint) ||
            string.IsNullOrWhiteSpace(probe.ReplayAttestationId) ||
            string.IsNullOrWhiteSpace(probe.CommandSignature) ||
            probe.Command is null || probe.Command.Count == 0 ||
            string.IsNullOrWhiteSpace(probe.ArtifactPath) ||
            string.IsNullOrWhiteSpace(probe.StandardOutputPath) || string.IsNullOrWhiteSpace(probe.StandardErrorPath) ||
            probe.StartedAt == default || probe.CompletedAt < probe.StartedAt)
            ? "UI artifacts must be winapp-migrate development probes using schema 1.0 with valid identities, commands, paths and timestamps." : null;

    private static bool CountsValid(OperationCounts? counts, int maximum) =>
        counts is not null && Enum.GetValues<WorkflowOperation>().All(operation =>
            counts.For(operation) >= 0 && counts.For(operation) <= maximum);
}
