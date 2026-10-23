// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinAppMigration.Enforcement;

public static class RuntimeFailureRecovery
{
    // An interruption between attestation and ledger writes must not erase diagnosis-required.
    public static bool Recover(
        AttemptLedger attempts, ReplayAttestation? replay, DiagnosticRunAttestation? diagnostic,
        MigrationState? fallbackState)
    {
        var changed = false;
        void Add(
            string fingerprint, string? signature, string? stateId, string? sliceId,
            DateTimeOffset at, string stdout, string stderr, string fallbackSignature)
        {
            signature ??= DiagnosticGrouper.CreateSignature("runtime", [], fallbackSignature);
            if (attempts.Attempts.Any(attempt => attempt.Kind == "runtime" &&
                attempt.TargetFingerprint == fingerprint &&
                (attempt.Signature == signature || attempt.Evidence.Contains(stdout, StringComparer.Ordinal))))
                return;
            attempts.Attempts.Add(new EnforcementAttempt
            {
                Kind = "runtime", TargetFingerprint = fingerprint, Signature = signature,
                StateId = stateId ?? fallbackState?.Id,
                SliceId = sliceId ?? (fallbackState is null ? null : SemanticSlicePlanner.SliceId(fallbackState)),
                RecordedAt = at, Evidence = [stdout, stderr]
            });
            changed = true;
        }
        if (replay is { Success: false })
            Add(replay.TargetFingerprint, replay.Signature, replay.StateId, replay.SliceId,
                replay.CompletedAt, replay.StandardOutputPath, replay.StandardErrorPath, replay.Error ?? replay.Id);
        if (diagnostic is { ExitCode: not 0 })
            Add(diagnostic.TargetFingerprint, diagnostic.Signature, diagnostic.StateId, diagnostic.SliceId,
                diagnostic.CompletedAt, diagnostic.StandardOutputPath, diagnostic.StandardErrorPath, diagnostic.Id);
        return changed;
    }
}
