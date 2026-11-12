// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Security.Cryptography;
using System.Text;

namespace WinAppMigration.Enforcement;

public static class WorkflowOperations
{
    public static bool TryObserveEvidence(
        EnforcementEvaluationInput input, EnforcementSnapshot snapshot,
        IReadOnlyList<string> evidence, out string reason)
    {
        if (WorkflowDispositions.RejectTerminal(snapshot, out reason)) return false;
        reason = "Valid workflow artifacts are required before recording discriminating progress.";
        if (snapshot.Workflow is null) return false;
        if (!TryEvidenceFingerprint(input.TargetRoot, evidence, out var fingerprint, out reason))
            return false;
        WorkflowController.AddEvent(snapshot.Workflow,
            WorkflowController.CurrentUnit(snapshot.Workflow, snapshot.ActiveSlice),
            "discriminating-evidence", fingerprint!, input.GeneratedAt);
        return true;
    }

    public static bool TryBegin(
        EnforcementEvaluationInput input, EnforcementSnapshot snapshot, WorkflowOperation operation,
        IReadOnlyList<string> evidence, string? signature, out string reason, StateSliceBinding? intended = null)
    {
        if (WorkflowDispositions.RejectTerminal(snapshot, out reason)) return false;
        reason = "Valid enforcement artifacts are required.";
        if (snapshot.Workflow is null ||
            snapshot.Gates.First(gate => gate.Id == "enforcement-artifacts").Status != "passed")
            return false;
        var prerequisite = snapshot.Gates.FirstOrDefault(gate =>
            gate.Id == "mechanical" && gate.Status != "passed");
        if (prerequisite is not null)
        {
            reason = prerequisite.Reason;
            return false;
        }
        if (!Enum.IsDefined(operation))
        {
            reason = $"Unsupported workflow operation: {operation}.";
            return false;
        }
        var binding = intended ?? StateAttribution.Authorize(input);
        StateAttribution.Validate(input.StatePlan, binding);
        var failureIntent = operation is WorkflowOperation.DiagnosticRun or WorkflowOperation.Hypothesis &&
            FailureProtocol.ActiveRuntimeFailures(input).Any(attempt =>
                attempt.StateId == binding.StateId && attempt.SliceId == binding.SliceId);
        if (intended is not null && !failureIntent)
        {
            var authorized = StateAttribution.Authorize(input, binding.StateId);
            if (authorized != binding)
                throw new InvalidDataException("Operation intent does not match the declared surface contract.");
        }
        var progress = snapshot.Workflow;
        var now = input.GeneratedAt;
        var unit = WorkflowController.CurrentUnit(progress, snapshot.ActiveSlice);
        if (operation is WorkflowOperation.DiagnosticRun or WorkflowOperation.Hypothesis &&
            progress.Operations.Any(item => item.Kind == operation &&
                item.TargetFingerprint == input.TargetFingerprint && item.Signature == signature &&
                item.StateId == binding.StateId && item.IntendedSliceId == binding.SliceId))
        {
            reason = "This failure tuple already has this diagnostic/hypothesis receipt.";
            return false;
        }
        string? evidenceFingerprint = null;
        if (evidence.Count > 0)
        {
            if (!TryEvidenceFingerprint(input.TargetRoot, evidence, out evidenceFingerprint, out reason))
                return false;
        }
        if (evidenceFingerprint is not null)
            WorkflowController.AddEvent(progress, unit, "discriminating-evidence", evidenceFingerprint, now);
        progress.StartedAt ??= now;
        progress.OperationsStartedAt ??= now;
        WorkflowController.Activate(progress, unit, now, input.TargetFingerprint);
        unit.Operations.Increment(operation);
        progress.Operations.Add(new WorkflowOperationRecord
        {
            RequiredContractHash = input.RequiredContractHash,
            StateId = binding.StateId, IntendedSliceId = binding.SliceId,
            Id = Guid.NewGuid().ToString("N"),
            Kind = operation, Stage = unit.Stage, SliceId = unit.SliceId,
            TargetFingerprint = input.TargetFingerprint, RecordedAt = now,
            ReplayAttestationId = operation == WorkflowOperation.UiAction
                ? input.Replay?.Id
                : null,
            EvidenceFingerprint = evidenceFingerprint, Signature = signature
        });
        WorkflowOperationAuthentication.Seal(progress.Operations[^1],
            progress.Operations.Count > 1 ? progress.Operations[^2] : null, progress.Operations.Count);
        progress.LastObservedAt = now;
        snapshot.Telemetry = WorkflowTelemetry.Measure(progress, unit, now);
        WorkflowDispositions.Update(snapshot, now);
        return true;
    }

    private static bool TryEvidenceFingerprint(
        DirectoryInfo root, IReadOnlyList<string> evidence,
        out string? fingerprint, out string reason)
    {
        fingerprint = null;
        if (evidence.Count == 0)
        {
            reason = "At least one diagnostic evidence path is required.";
            return false;
        }
        var hashes = new List<string>();
        foreach (var relativePath in evidence)
        {
            if (!EnforcementStore.TryResolveContainedPath(root, relativePath, out var path, out reason))
                return false;
            if (!File.Exists(path))
            {
                reason = $"Diagnostic evidence must exist: {relativePath}";
                return false;
            }
            using var stream = File.OpenRead(path);
            hashes.Add(Convert.ToHexString(SHA256.HashData(stream)));
        }
        fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Join("|", hashes.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))))).ToLowerInvariant();
        reason = string.Empty;
        return true;
    }
}
