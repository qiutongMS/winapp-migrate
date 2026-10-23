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
        if (progress.SchemaVersion != WorkflowProgress.CurrentSchemaVersion ||
            !Enum.IsDefined(progress.Stage) ||
            string.IsNullOrWhiteSpace(progress.PolicyFingerprint) ||
            string.IsNullOrWhiteSpace(progress.LastTargetFingerprint) ||
            progress.LastObservedAt == default || progress.LastObservedAt > now ||
            progress.StartedAt is null || progress.StartedAt == default(DateTimeOffset) || progress.StartedAt > now ||
            progress.OperationsStartedAt > now || progress.OperationsStartedAt < progress.StartedAt ||
            progress.ActiveSliceId is not null && string.IsNullOrWhiteSpace(progress.ActiveSliceId) ||
            progress.Units is null || progress.Events is null || progress.Operations is null ||
            progress.Debt is null || progress.CompletedSliceIds is null ||
            progress.UnresolvedRequiredStates is null)
            return "Workflow progress has an unsupported schema or invalid fields/timestamps.";
        if (progress.OperationsStartedAt is not null &&
            progress.PolicyFingerprint != PolicyFingerprint(policy))
            return "Enforcement policy is immutable once workflow operations have started.";
        if (progress.Units.Any(unit => unit is null || !Enum.IsDefined(unit.Stage) ||
                (unit.Stage != WorkflowStage.SemanticConvergence && unit.SliceId is not null) ||
                !CountsValid(unit.Operations, int.MaxValue) ||
                unit.StartedAt is null || unit.StartedAt < progress.StartedAt || unit.StartedAt > now ||
                unit.ExtensionGrantedAt > now || unit.ExtensionGrantedAt < unit.StartedAt ||
                unit.ExtensionGrantedAt is not null && policy.MaximumProgressExtensionsPerUnit == 0 ||
                unit.BlockedAt > now || unit.BlockedAt < unit.StartedAt ||
                (unit.BlockedAt is not null) != !string.IsNullOrWhiteSpace(unit.BlockedReason)) ||
            progress.Units.GroupBy(unit => (unit.Stage, unit.SliceId)).Any(group => group.Count() > 1))
            return "Workflow progress contains invalid or duplicate budget units/extensions.";
        if (progress.Events.Any(item => item is null || !Enum.IsDefined(item.Stage) ||
                string.IsNullOrWhiteSpace(item.Key) || item.RecordedAt == default || item.RecordedAt > now ||
                item.Kind is not ("target-changed" or "required-states-reduced" or
                    "diagnosis-completed" or "slice-completed" or "discriminating-evidence" or
                    "source-oracle-completed" or "mechanical-stabilized" or "bootstrap-completed")) ||
            progress.Operations.Any(item => item is null || !Enum.IsDefined(item.Stage) ||
                !WorkflowOperations.IsCompatible(item.Stage, item.Kind) || string.IsNullOrWhiteSpace(item.TargetFingerprint) ||
                item.RecordedAt == default || item.RecordedAt > now) ||
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
        if (progress.Operations.Count > 0 &&
            (progress.StartedAt is null || progress.OperationsStartedAt is null) ||
            progress.Operations.Any(operation => !progress.Units.Any(unit =>
                unit.Stage == operation.Stage && unit.SliceId == operation.SliceId)))
            return "Workflow operations require a start time and matching budget units.";
        return null;
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
                string.IsNullOrWhiteSpace(build.Id) || string.IsNullOrWhiteSpace(build.TargetFingerprint) ||
                string.IsNullOrWhiteSpace(build.StandardOutputPath) || string.IsNullOrWhiteSpace(build.StandardErrorPath) ||
                build.StartedAt == default || build.CompletedAt < build.StartedAt) ||
            replay is not null && (replay.SchemaVersion != ReplayAttestation.CurrentSchemaVersion ||
                string.IsNullOrWhiteSpace(replay.Id) || string.IsNullOrWhiteSpace(replay.RunId) ||
                string.IsNullOrWhiteSpace(replay.TargetFingerprint) || string.IsNullOrWhiteSpace(replay.BuildAttestationId) ||
                string.IsNullOrWhiteSpace(replay.StandardOutputPath) || string.IsNullOrWhiteSpace(replay.StandardErrorPath) ||
                replay.StartedAt == default || replay.CompletedAt < replay.StartedAt))
            return "Build and replay artifacts must use schema 1.0 with valid identities, paths and timestamps.";
        return null;
    }

    public static string? DiagnosticError(DiagnosticRunAttestation? diagnostic) =>
        diagnostic is not null && (diagnostic.SchemaVersion != DiagnosticRunAttestation.CurrentSchemaVersion ||
            string.IsNullOrWhiteSpace(diagnostic.Id) || string.IsNullOrWhiteSpace(diagnostic.TargetFingerprint) ||
            string.IsNullOrWhiteSpace(diagnostic.StandardOutputPath) || string.IsNullOrWhiteSpace(diagnostic.StandardErrorPath) ||
            diagnostic.StartedAt == default || diagnostic.CompletedAt < diagnostic.StartedAt)
            ? "Diagnostic artifacts must use schema 1.0 with valid identities, paths and timestamps." : null;

    private static bool CountsValid(OperationCounts? counts, int maximum) =>
        counts is not null && Enum.GetValues<WorkflowOperation>().All(operation =>
            counts.For(operation) >= 0 && counts.For(operation) <= maximum);
}
