// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WinAppMigration.Enforcement;

public static class WorkflowValidation
{
    public static string PolicyFingerprint(EnforcementPolicy policy) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(policy, EnforcementStore.JsonOptions)))).ToLowerInvariant();

    public static string? PolicyError(EnforcementPolicy? policy)
    {
        if (policy is null || policy.SchemaVersion != EnforcementPolicy.CurrentSchemaVersion)
            return "Policy must use enforcement policy schema 1.0.";
        if (policy.TotalHardMinutes is < 1 or > 10080 ||
            policy.ExtensionMinutes is < 1 or > 60 ||
            policy.MaximumProgressExtensionsPerUnit is < 0 or > 1)
            return "Policy total hard minutes must be 1..10080, extension minutes 1..60, and maximumProgressExtensionsPerUnit 0..1.";
        foreach (var stage in Enum.GetValues<WorkflowStage>())
        {
            var budget = policy.For(stage);
            if (budget is null || budget.SoftMinutes < 1 ||
                budget.HardMinutes < budget.SoftMinutes || budget.HardMinutes > 1440 ||
                budget.HardMinutes > policy.TotalHardMinutes || !CountsValid(budget.Operations, 10000))
                return $"Policy {stage} requires 1 <= softMinutes <= hardMinutes <= min(1440, totalHardMinutes) and operation ceilings 0..10000.";
            if (Enum.GetValues<WorkflowOperation>().Any(operation =>
                !WorkflowOperations.IsCompatible(stage, operation) && budget.Operations.For(operation) != 0))
                return $"Policy {stage} must have zero ceilings for incompatible operations.";
        }
        return null;
    }

    public static string? ProgressError(
        WorkflowProgress? progress, EnforcementPolicy policy, DateTimeOffset now)
    {
        if (progress is null) return null;
        if (progress.SchemaVersion != WorkflowProgress.CurrentSchemaVersion)
            return $"Workflow progress schema {progress.SchemaVersion} must be explicitly imported to schema {WorkflowProgress.CurrentSchemaVersion} before evaluation.";
        return ProgressStructureError(progress, policy, now, allowLegacyAuthorization: false);
    }

    public static string? Schema20ImportError(
        WorkflowProgress progress, EnforcementPolicy policy, DateTimeOffset now)
    {
        if (progress.SchemaVersion != "2.0")
            return $"Expected workflow progress schema 2.0, found {progress.SchemaVersion}.";
        return ProgressStructureError(progress, policy, now, allowLegacyAuthorization: true);
    }

    private static string? ProgressStructureError(
        WorkflowProgress progress, EnforcementPolicy policy, DateTimeOffset now,
        bool allowLegacyAuthorization)
    {
        if (!Enum.IsDefined(progress.Stage) ||
            string.IsNullOrWhiteSpace(progress.PolicyFingerprint) ||
            string.IsNullOrWhiteSpace(progress.LastTargetFingerprint) ||
            progress.LastObservedAt == default || progress.LastObservedAt > now ||
            progress.StartedAt is null || progress.StartedAt == default(DateTimeOffset) || progress.StartedAt > now ||
            progress.OperationsStartedAt > progress.LastObservedAt || progress.OperationsStartedAt < progress.StartedAt ||
            progress.ActiveSliceId is not null && string.IsNullOrWhiteSpace(progress.ActiveSliceId) ||
            progress.Units is null || progress.Events is null || progress.Operations is null ||
            progress.Debt is null || progress.CompletedSliceIds is null ||
            progress.UnresolvedRequiredStates is null || progress.Slices is null ||
            progress.LastObservedAt < progress.StartedAt || now < progress.LastObservedAt)
            return "Workflow progress has an unsupported schema or invalid fields/timestamps.";
        if (allowLegacyAuthorization && progress.OperationsStartedAt is not null &&
            progress.PolicyFingerprint != PolicyFingerprint(policy))
            return "Enforcement policy is immutable once workflow operations have started.";
        if (progress.Units.Any(unit => unit is null || !Enum.IsDefined(unit.Stage) ||
                (unit.Stage != WorkflowStage.SemanticConvergence && unit.SliceId is not null) ||
                !CountsValid(unit.Operations, int.MaxValue) ||
                unit.StartedAt is null || unit.StartedAt < progress.StartedAt || unit.StartedAt > now ||
                unit.ExtensionGrantedAt > progress.LastObservedAt || unit.ExtensionGrantedAt < unit.StartedAt ||
                unit.ExtensionGrantedAt is not null && (!allowLegacyAuthorization ||
                    policy.MaximumProgressExtensionsPerUnit == 0) ||
                unit.BlockedAt > progress.LastObservedAt || unit.BlockedAt < unit.StartedAt ||
                (unit.BlockedAt is not null) != !string.IsNullOrWhiteSpace(unit.BlockedReason) ||
                !allowLegacyAuthorization &&
                    (unit.BlockedAt is not null || unit.BlockedReason is not null) ||
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
            return "Workflow progress contains invalid or duplicate workflow units or retired authorization state.";
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
        if (DispositionError(progress.Disposition) is { } dispositionError)
            return dispositionError;
        if (progress.Disposition is { Terminal: true } && epochs.Any(epoch => epoch.ExitedAt is null))
            return "Terminal workflow progress must not contain an active epoch.";
        if (progress.Events.Any(item => item is null || !Enum.IsDefined(item.Stage) ||
                string.IsNullOrWhiteSpace(item.Key) || item.RecordedAt == default || item.RecordedAt > progress.LastObservedAt ||
                item.Kind is not ("target-changed" or "required-states-reduced" or
                    "diagnosis-completed" or "slice-completed" or "discriminating-evidence" or
                    "source-oracle-completed" or "mechanical-stabilized" or "bootstrap-completed")) ||
            progress.Operations.Any(item => item is null || !Enum.IsDefined(item.Stage) ||
                !WorkflowOperations.IsCompatible(item.Stage, item.Kind) || string.IsNullOrWhiteSpace(item.TargetFingerprint) ||
                item.RecordedAt == default || item.RecordedAt > progress.LastObservedAt) ||
            progress.Debt.Any(item => item is null || string.IsNullOrWhiteSpace(item.SliceId) ||
                string.IsNullOrWhiteSpace(item.Reason)))
            return "Workflow progress contains invalid progress events, operations, or debt.";
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
            return "Workflow operations require a start time and matching budget units.";
        return null;
    }

    public static string? DispositionError(RunDisposition? disposition)
    {
        if (disposition is null) return null;
        var expected = disposition.Kind switch
        {
            "continue" => (false, true, "not-ready"),
            "pause-replan" => (false, true, "not-ready"),
            "continue-with-debt" => (false, true, "debt"),
            "complete" => (true, false, "complete"),
            "blocked-required" => (true, false, "blocked"),
            "total-budget-exhausted" => (true, false, "blocked"),
            "infrastructure-failure" => (true, true, "failed"),
            _ => ((bool, bool, string)?)null
        };
        return expected is null || string.IsNullOrWhiteSpace(disposition.ReasonCode) ||
            disposition.Terminal != expected.Value.Item1 || disposition.Retryable != expected.Value.Item2 ||
            disposition.Finalization != expected.Value.Item3 || disposition.AllowedOperations is null ||
            disposition.AllowedOperations.Any(operation => !Enum.IsDefined(operation)) ||
            (disposition.Terminal || disposition.Kind is "pause-replan" or "continue-with-debt") &&
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
            probe.Command is null || probe.Command.Count == 0 ||
            string.IsNullOrWhiteSpace(probe.StandardOutputPath) || string.IsNullOrWhiteSpace(probe.StandardErrorPath) ||
            probe.StartedAt == default || probe.CompletedAt < probe.StartedAt)
            ? "UI artifacts must be winapp-migrate development probes using schema 1.0 with valid identities, commands, paths and timestamps." : null;

    private static bool CountsValid(OperationCounts? counts, int maximum) =>
        counts is not null && Enum.GetValues<WorkflowOperation>().All(operation =>
            counts.For(operation) >= 0 && counts.For(operation) <= maximum);
}
