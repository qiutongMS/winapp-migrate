// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinAppMigration.Enforcement;

public static class RuntimeFailureRecovery
{
    // Preserve failure history if attestation and ledger writes were interrupted.
    public static bool Recover(
        AttemptLedger attempts, ReplayAttestation? replay, DiagnosticRunAttestation? diagnostic,
        EnforcementEvaluationInput input)
    {
        var changed = false;
        void Add(
            string fingerprint, string? signature, string? stateId, string? sliceId, string? operationId,
            DateTimeOffset at, string stdout, string stderr, string fallbackSignature)
        {
            var supplied = new StateSliceBinding(stateId, sliceId);
            StateAttribution.Validate(input.StatePlan, supplied);
            signature ??= DiagnosticGrouper.CreateSignature("runtime", [], fallbackSignature);
            if (string.IsNullOrWhiteSpace(operationId))
                throw new InvalidDataException("Runtime recovery requires an authenticated operation ID; attribution cannot be inferred.");
            var existing = attempts.Attempts.FirstOrDefault(attempt => attempt.Kind == "runtime" &&
                attempt.TargetFingerprint == fingerprint && attempt.OperationId == operationId);
            var operation = StateAttribution.AuthenticatedOperation(input.TargetRoot, operationId);
            var binding = new StateSliceBinding(operation.StateId, operation.IntendedSliceId);
            StateAttribution.Validate(input.StatePlan, binding);
            if (binding != supplied || operation.TargetFingerprint != fingerprint)
                throw new InvalidDataException("Failure attestation differs from its authenticated operation-start tuple.");
            if (existing is not null)
            {
                if (existing.StateId != binding.StateId || existing.SliceId != binding.SliceId)
                    throw new InvalidDataException("Recovered failure conflicts with an existing tuple; rebinding is forbidden.");
                return;
            }
            attempts.Attempts.Add(new EnforcementAttempt
            {
                Kind = "runtime", TargetFingerprint = fingerprint, Signature = signature,
                StateId = binding.StateId, SliceId = binding.SliceId, OperationId = operationId,
                RecordedAt = at, Evidence = [stdout, stderr]
            });
            changed = true;
        }
        if (replay is { Success: false })
        {
            if (!CanonicalAttestationTrust.VerifyFailure(input.TargetRoot, replay))
                throw new InvalidDataException("Runtime recovery requires the trusted replay operation/attestation chain.");
            Add(replay.TargetFingerprint, replay.Signature, replay.StateId, replay.SliceId,
                replay.OperationId,
                replay.CompletedAt, replay.StandardOutputPath, replay.StandardErrorPath, replay.Error ?? replay.Id);
        }
        if (diagnostic is { ExitCode: not 0 })
        {
            if (!CanonicalAttestationTrust.Verify(input.TargetRoot, diagnostic))
                throw new InvalidDataException("Runtime recovery requires the trusted diagnostic operation/attestation chain.");
            Add(diagnostic.TargetFingerprint, diagnostic.Signature, diagnostic.StateId, diagnostic.SliceId,
                diagnostic.OperationId,
                diagnostic.CompletedAt, diagnostic.StandardOutputPath, diagnostic.StandardErrorPath, diagnostic.Id);
        }
        return changed;
    }
}
