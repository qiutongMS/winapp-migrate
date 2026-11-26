// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Security.Cryptography;
using System.Text;

namespace WinAppMigration.Enforcement;

public static class WorkflowOperations
{

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
        if (!Enum.IsDefined(operation) || operation == WorkflowOperation.Hypothesis)
        {
            reason = $"Unsupported workflow operation: {operation}.";
            return false;
        }
        if (snapshot.Disposition is { Kind: "tool-blocked" or "input-review-required" } disposition &&
            !disposition.AllowedOperations.Contains(operation))
        {
            reason = disposition.Kind == "input-review-required"
                ? "Remaining comparisons require input review, not another unchanged build/replay. Preserve the partial report, collect diagnostic evidence and establish comparable source/target inputs. A capture-review diagnostic is not proof of an application defect or of equivalence."
                : "Unchanged required surfaces are blocked on comparison support. Preserve the partial report and collect diagnostics; a different target or comparison build is required before build/replay retries.";
            return false;
        }
        var binding = intended ?? StateAttribution.Authorize(input);
        StateAttribution.Validate(input.StatePlan, binding);
        if (intended is not null && binding.StateId is not null)
        {
            var authorized = StateAttribution.Authorize(input, binding.StateId);
            if (authorized != binding)
                throw new InvalidDataException("Operation intent does not match the declared surface contract.");
        }
        var progress = snapshot.Workflow;
        var now = input.GeneratedAt;
        string? evidenceFingerprint = null;
        if (evidence.Count > 0)
        {
            if (!TryEvidenceFingerprint(input.TargetRoot, evidence, out evidenceFingerprint, out reason))
                return false;
        }
        progress.StartedAt ??= now;
        progress.Operations.Add(new WorkflowOperationRecord
        {
            RequiredContractHash = input.RequiredContractHash,
            StateId = binding.StateId, IntendedSliceId = binding.SliceId,
            Id = Guid.NewGuid().ToString("N"),
            Kind = operation, Stage = progress.Stage,
            SliceId = progress.Stage == WorkflowStage.SemanticConvergence ? snapshot.ActiveSlice : null,
            TargetFingerprint = input.TargetFingerprint, RecordedAt = now,
            ReplayAttestationId = operation == WorkflowOperation.UiAction
                ? input.Replay?.Id
                : null,
            EvidenceFingerprint = evidenceFingerprint, Signature = signature
        });
        WorkflowOperationAuthentication.Seal(progress.Operations[^1],
            progress.Operations.Count > 1 ? progress.Operations[^2] : null, progress.Operations.Count);
        progress.LastObservedAt = now;
        snapshot.Telemetry = WorkflowTelemetry.Measure(progress, now);
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
