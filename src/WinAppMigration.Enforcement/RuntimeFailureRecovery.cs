// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinAppMigration.Enforcement;

public static class RuntimeFailureRecovery
{
    // An interruption between attestation and ledger writes must not erase diagnosis-required.
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
            var existing = attempts.Attempts.FirstOrDefault(attempt => attempt.Kind == "runtime" &&
                attempt.TargetFingerprint == fingerprint &&
                (operationId is not null ? attempt.OperationId == operationId :
                    attempt.Evidence.Contains(stdout, StringComparer.Ordinal)));
            if (existing is not null && operationId is null)
            {
                StateAttribution.Validate(input.StatePlan, new StateSliceBinding(existing.StateId, existing.SliceId));
                return;
            }
            StateSliceBinding binding;
            if (operationId is not null)
            {
                var operation = StateAttribution.AuthenticatedOperation(input.TargetRoot, operationId);
                binding = new StateSliceBinding(operation.StateId, operation.IntendedSliceId);
                StateAttribution.Validate(input.StatePlan, binding);
                if (binding != supplied || operation.TargetFingerprint != fingerprint)
                    throw new InvalidDataException("Failure attestation differs from its authenticated operation-start tuple.");
            }
            else
            {
                if (supplied != StateSliceBinding.Unbound)
                    throw new InvalidDataException("Legacy non-operation attribution cannot be repaired/rebound silently.");
                binding = StateAttribution.PersistedFallback(input);
            }
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
            if (replay.OperationId is not null && !CanonicalAttestationTrust.VerifyFailure(input.TargetRoot, replay))
                throw new InvalidDataException("Runtime recovery requires the trusted replay operation/attestation chain.");
            Add(replay.TargetFingerprint, replay.Signature, replay.StateId, replay.SliceId,
                replay.OperationId,
                replay.CompletedAt, replay.StandardOutputPath, replay.StandardErrorPath, replay.Error ?? replay.Id);
        }
        if (diagnostic is { ExitCode: not 0 })
        {
            if (diagnostic.OperationId is not null && !CanonicalAttestationTrust.Verify(input.TargetRoot, diagnostic))
                throw new InvalidDataException("Runtime recovery requires the trusted diagnostic operation/attestation chain.");
            Add(diagnostic.TargetFingerprint, diagnostic.Signature, diagnostic.StateId, diagnostic.SliceId,
                diagnostic.OperationId,
                diagnostic.CompletedAt, diagnostic.StandardOutputPath, diagnostic.StandardErrorPath, diagnostic.Id);
        }
        return changed;
    }
}
