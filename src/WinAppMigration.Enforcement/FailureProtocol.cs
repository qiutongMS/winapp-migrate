// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinAppMigration.Enforcement;

public static class FailureProtocol
{
    public static IEnumerable<EnforcementAttempt> ActiveRuntimeFailures(EnforcementEvaluationInput input) =>
        input.Attempts.Attempts.Where(attempt => attempt.Kind == "runtime")
            .GroupBy(attempt => (attempt.Signature, attempt.TargetFingerprint, attempt.StateId, attempt.SliceId))
            .Select(group => group.OrderByDescending(attempt =>
                    EnforcementEvaluator.DiagnosisIsComplete(input.TargetRoot, attempt))
                .ThenByDescending(attempt => attempt.RecordedAt).First())
            .Where(attempt => !SupersededByReplay(input, attempt));

    public static bool SupersededByReplay(EnforcementEvaluationInput input, EnforcementAttempt attempt) =>
        input.Replay is { Success: true, ProcessId: > 0 } && input.Build is { Success: true } &&
        input.Replay.TargetFingerprint != attempt.TargetFingerprint &&
        input.Replay.TargetFingerprint == input.Build.TargetFingerprint &&
        input.Replay.BuildAttestationId == input.Build.Id &&
        input.Replay.StartedAt > (attempt.Diagnosis?.RecordedAt ?? attempt.RecordedAt) &&
        input.Build.CompletedAt <= input.Replay.StartedAt &&
        EvidenceExists(input.TargetRoot, input.Replay.StandardOutputPath) &&
        EvidenceExists(input.TargetRoot, input.Replay.StandardErrorPath);

    private static bool EvidenceExists(DirectoryInfo root, string relativePath) =>
        EnforcementStore.TryResolveContainedPath(root, relativePath, out var path, out _) && File.Exists(path);

}
